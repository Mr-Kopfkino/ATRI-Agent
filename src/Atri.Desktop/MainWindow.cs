using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Atri.Core;

namespace Atri.Desktop;
public sealed partial class MainWindow:Window
{
    internal readonly AppPaths Paths;internal readonly Settings Settings;internal readonly SettingsStore Store;internal MemoryStore Memory {get;private set;} internal readonly VoiceProcess Voice;internal CapabilityBridge Capabilities {get;private set;} internal readonly LocalAsrProcess LocalAsr;
    private readonly ProtocolClient api=new();private ContextBuilder context;private readonly AudioPlayer audio=new();private readonly Microphone microphone=new();internal readonly DesktopHost Desktop=new();internal readonly GameAudio GameSounds;internal readonly WindowDisplayController Display;
    private readonly Grid stage=new(){ClipToBounds=true};private readonly Grid title=new(){Width=1280,Height=720},chat=new(){Width=1280,Height=720},system=new(){Width=1280,Height=720};
    private readonly Border transitionBlack=new(){Background=Brushes.Black,Opacity=0,Visibility=Visibility.Collapsed,IsHitTestVisible=false};private readonly PageTransitions navigation;private Task navigationTask=Task.CompletedTask;private bool speakingAudio,exiting;private DependencyObject? hoveredControl;private ButtonBase? previewSoundButton;
    private readonly TextBox input=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,FontSize=19,MaxLength=4000,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly TextBlock subtitle=new(){TextWrapping=TextWrapping.Wrap,FontSize=26,Foreground=Brushes.White,LineHeight=39};
    private readonly TextBlock stateText=new(){Foreground=Brushes.White,FontSize=16};private readonly Image character=new(){Stretch=Stretch.Uniform};
    private readonly Button send=new(),record=new();private readonly DispatcherTimer recordTimer=new(){Interval=TimeSpan.FromSeconds(30)};
    private readonly Button imageButton=new();private VisualAttachment? attachment;
    private readonly string background;private readonly CancellationTokenSource lifetime=new();private CancellationTokenSource? active;private long generation;private bool busy,composing,closing,capture,audioSmoke,uiSmoke,sendStarting;
    private string previousPage="title";private DialogueReply? lastReply;private int currentSegment=-1;private ConsoleView? console;private Task turnTask=Task.CompletedTask,recordingTask=Task.CompletedTask;
    private CancellationTokenSource? vadCancellation;private Channel<byte[]>? vadFrames;private bool wakeCapture,wakeArmed,recordingStarting;private int droppedFrames;private DateTimeOffset wakeUntil;private long recordingGeneration;private Task vadTask=Task.CompletedTask;
    private readonly Dictionary<string,BitmapImage> bitmaps=new();private string currentEmotion="neutral";
    private bool shutdownComplete,memoryTransitioning;
    private Task startupTask=Task.CompletedTask;
    private bool factoryResetProbe;
    internal bool IsMemoryTransitioning=>memoryTransitioning;
    internal bool IsClosing=>closing;
    internal CancellationToken LifetimeToken=>lifetime.Token;
    private readonly SemaphoreSlim memoryTransitionGate=new(1,1);
    public static readonly Brush Blue=new SolidColorBrush(Color.FromRgb(21,106,183));public static readonly Brush Aqua=new SolidColorBrush(Color.FromRgb(53,183,245));
    private const string Art="assets/ATRI-UI-BG/转换后PNG/";
    public MainWindow(AppPaths paths)
    {
        Paths=paths;FactoryReset.RecoverPending(paths);Store=new(paths);Settings=Store.Load();Store.Save(Settings);Memory=new(paths.Memory);context=new(paths,Memory);Voice=new(paths,Settings);Capabilities=new(paths,Settings);LocalAsr=new(paths);GameSounds=new(paths);Display=new(this,Desktop,Settings.DisplayMode);Display.ModeChanged+=mode=>{Settings.DisplayMode=mode;if(!memoryTransitioning&&!capture&&!uiSmoke&&!audioSmoke)Store.Save(Settings);console?.RefreshDisplayMode();};
        Resources.MergedDictionaries.Add(GameStyles.Create());Title="ATRI · My Dear Agent";Width=1280;Height=720;WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=Blue;FontFamily=new FontFamily("Microsoft YaHei UI, Yu Gothic UI");
        var allowed=new[]{"bg002","bg003","bg005","bg006","bg007","bg009","bg010"};background=allowed[Random.Shared.Next(allowed.Length)];
        Content=stage;var pages=new Dictionary<string,Grid>{{"title",PageHost(title,new ImageBrush(Bitmap(Art+"UI_系统图像/data/image/title_base2.png")){Stretch=Stretch.UniformToFill})},{"chat",PageHost(chat,new ImageBrush(Bitmap(Art+$"背景_场景/bgimage/{background}.png")){Stretch=Stretch.UniformToFill})},{"system",PageHost(system,SystemBackdrop())}};foreach(var page in pages.Values)stage.Children.Add(page);stage.Children.Add(transitionBlack);navigation=new(pages,transitionBlack);
        BuildTitle();BuildChat();SourceInitialized+=(_,_)=>Display.Apply(Settings.DisplayMode);
        AddHandler(ButtonBase.ClickEvent,new RoutedEventHandler((_,e)=>{if(closing)return;if(e.Source is ButtonBase b&&b is not RepeatButton){if(ReferenceEquals(previewSoundButton,b)){previewSoundButton=null;return;}PlayClickSound(b);}}),true);
        PreviewMouseMove+=(_,e)=>{DependencyObject? target=e.OriginalSource as DependencyObject;while(target!=null&&target is not ButtonBase&&target is not TabItem)target=target is Visual?VisualTreeHelper.GetParent(target):null;if(ReferenceEquals(target,hoveredControl))return;hoveredControl=target;if(!closing&&target is UIElement element&&element.IsEnabled&&element.IsVisible&&target is not RepeatButton)GameSounds.Hover();};
        PreviewMouseLeftButtonDown+=(_,e)=>{if(FindButtonAncestor(e.OriginalSource as DependencyObject) is {} button&&button.IsEnabled){previewSoundButton=button;PlayClickSound(button);}else if(Display.Mode=="window"&&!Desktop.IsAttached&&e.GetPosition(this).Y<14){try{DragMove();}catch(InvalidOperationException){}}};
        PreviewKeyDown+=(_,e)=>{if(e.Key is Key.Space or Key.Enter&&Keyboard.FocusedElement is ButtonBase button&&button.IsEnabled){previewSoundButton=button;PlayClickSound(button);}};
        Closing+=OnClosing;recordTimer.Tick+=async(_,_)=>{if(wakeCapture&&microphone.Recording){microphone.DiscardBuffer();return;}recordTimer.Stop();if(microphone.Recording)await StopRecording();};
        microphone.FrameReady+=(frame,cookie)=>{if(cookie==microphone.Generation&&vadFrames is {} queue){if(queue.Writer.TryWrite(frame))return;Interlocked.Increment(ref droppedFrames);}Array.Clear(frame);};
        microphone.Failed+=(message,cookie)=>Dispatcher.BeginInvoke(()=>{if(cookie==microphone.Generation){StopMicrophone();SetState(message);}});
        audio.MouthChanged+=SetMouth;
        Loaded+=async(_,_)=>
        {
            if(factoryResetProbe){GameSounds.Configure(false,0,0,0);return;}
            Paths.CleanTemporary(TimeSpan.FromDays(1));ConfigureGameAudio();if(capture||uiSmoke||audioSmoke)GameSounds.Configure(false,0,0,0);else GameSounds.Navigate("title",true);
            if(capture){await CaptureLayouts();return;}
            if(audioSmoke){await RunAudioSmoke();return;}
            if(uiSmoke){await RunUiSmoke();return;}
            StartModelMemoryMaintenance();
            try{startupTask=Task.WhenAll(Task.Run(()=>context.RegisterKnowledge(lifetime.Token),lifetime.Token),Voice.EnsureStartedAsync(lifetime.Token));await startupTask;if(!memoryTransitioning)SetState("本地语音组件就绪");}catch(OperationCanceledException){}catch(Exception e){if(!memoryTransitioning)SetState(e.Message);}
        };
    }
    private static Grid PageHost(Grid page,Brush background){var host=new Grid{Background=background,ClipToBounds=true};host.Children.Add(new Viewbox{Stretch=Stretch.Uniform,Child=page});return host;}
    internal Brush SystemBackdrop()=>new ImageBrush(Bitmap("assets/ATRI-UI-BG/转换后PNG/UI_菜单图层/data/uipsd/config/13397.png")){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(1500,0,494,600),Stretch=Stretch.UniformToFill,AlignmentY=AlignmentY.Top};
    private void ConfigureGameAudio()=>GameSounds.Configure(Settings.BgmEnabled,Settings.BgmVolume,Settings.EffectVolume,Settings.VoiceEnabled?Settings.VoiceVolume:0);
    private void UpdateGameAudioActivity()=>GameSounds.SetVoiceActivity(microphone.Recording,speakingAudio);
    private static ButtonBase? FindButtonAncestor(DependencyObject? target){while(target!=null){if(target is ButtonBase button)return button;target=target is Visual?VisualTreeHelper.GetParent(target):LogicalTreeHelper.GetParent(target);}return null;}
    private void PlayClickSound(ButtonBase button){if(closing||button is RepeatButton||(Equals(button.Tag,"ui-cancel-owned")||Equals(button.Tag,"ui-confirm-owned")))return;if(button.Content is string label&&label is "返回" or "TITLE" or "中止")GameSounds.Back();else GameSounds.Select();}
    private async void ExitFromTitle(){if(exiting||closing)return;exiting=true;try{if(await ShowGameConfirmationAsync("exit","是否退出 ATRI Agent？","本地记忆会保留，下次还可以继续聊天。","","exit-confirm"))Close();}finally{exiting=false;}}
    internal void SetDisplayMode(string mode){if(!Display.Apply(mode))throw new InvalidOperationException(Display.State);Settings.DisplayMode=Display.Mode;if(!capture&&!uiSmoke&&!audioSmoke)Store.Save(Settings);}
    internal void SetBgmEnabled(bool enabled){Settings.BgmEnabled=enabled;if(!capture&&!uiSmoke&&!audioSmoke)Store.Save(Settings);ConfigureGameAudio();}
    internal Image Image(string relative,Stretch stretch=Stretch.Uniform)
    {return new Image{Source=Bitmap(relative),Stretch=stretch,IsHitTestVisible=false};}
    private BitmapImage? Bitmap(string relative)
    {if(bitmaps.TryGetValue(relative,out var cached))return cached;var file=Paths.WithinRoot(relative);if(!File.Exists(file)){Paths.Log("asset","缺少已登记资源 "+Path.GetFileName(relative));return null;}var b=new BitmapImage();b.BeginInit();b.CacheOption=BitmapCacheOption.OnLoad;b.UriSource=new Uri(file);b.EndInit();b.Freeze();bitmaps[relative]=b;return b;}
    internal static void Place(Panel panel,UIElement element,double x,double y,double width,double height)
    {if(element is FrameworkElement f){f.Width=width;f.Height=height;f.HorizontalAlignment=HorizontalAlignment.Left;f.VerticalAlignment=VerticalAlignment.Top;f.Margin=new Thickness(x,y,0,0);}panel.Children.Add(element);}
    internal static Button Button(string text,Action action,double font=20)
    {var b=new Button{Content=text,FontSize=font,Foreground=Blue,Background=new SolidColorBrush(Color.FromArgb(220,242,252,255)),BorderBrush=Aqua,BorderThickness=new Thickness(1),Padding=new Thickness(12,7,12,7),Cursor=Cursors.Hand};b.Click+=(_,_)=>action();return b;}
    private void BuildTitle()
    {
        title.Background=Brushes.Transparent;Place(title,Image(Art+"UI_菜单图层/data/uipsd/title/949.png"),106,61,423,186);
        AddTitleButton("start","1124","1144",172,389,()=>ShowPage("chat"));AddTitleButton("system","1126","1146",172,467,()=>OpenSystem("title"));AddTitleButton("exit","1129","1149",174,549,ExitFromTitle);
        Place(title,new TextBlock{Text="My Dear Agent   /   LOCAL DESKTOP",Foreground=Blue,FontSize=14},110,674,470,25);
    }
    private void AddTitleButton(string name,string normal,string over,double x,double y,Action action)
    {
        var image=Image(Art+$"UI_菜单图层/data/uipsd/title/{normal}.png");var button=new Button{Content=image,Background=Brushes.Transparent,BorderThickness=new Thickness(0),HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(10),Cursor=Cursors.Hand,ToolTip=name};
        System.Windows.Automation.AutomationProperties.SetName(button,name);if(name=="exit")button.Tag="ui-cancel-owned";button.MouseEnter+=(_,_)=>image.Source=Bitmap(Art+$"UI_菜单图层/data/uipsd/title/{over}.png");button.MouseLeave+=(_,_)=>image.Source=Bitmap(Art+$"UI_菜单图层/data/uipsd/title/{normal}.png");button.Click+=(_,_)=>action();Place(title,button,x-10,y-10,215,52);
    }
    private void BuildChat()
    {
        Place(chat,character,380,22,530,1010);SetEmotion("neutral");
        Place(chat,new Border{Background=new LinearGradientBrush(Color.FromArgb(0,5,37,64),Color.FromArgb(230,8,43,67),90)},0,465,1280,255);
        Place(chat,new TextBlock{Text="アトリ  /  亚托莉",Foreground=Brushes.White,FontWeight=FontWeights.Bold,FontSize=25},72,487,500,42);
        Place(chat,new ScrollViewer{Content=subtitle,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},72,539,1050,100);
        subtitle.Text="在下方输入，或点击「录音」开始说话。";
        Place(chat,stateText,72,643,590,24);SetState("准备就绪 · 日语语音 / "+(Settings.SubtitleLanguage=="zh"?"中文字幕":"日文字幕"));
        Place(chat,input,70,675,710,36);input.Padding=new Thickness(8,3,8,3);input.Background=new SolidColorBrush(Color.FromArgb(225,244,252,255));input.Foreground=Blue;input.BorderBrush=Aqua;
        TextCompositionManager.AddPreviewTextInputStartHandler(input,(_,_)=>composing=true);TextCompositionManager.AddPreviewTextInputUpdateHandler(input,(_,_)=>composing=true);input.PreviewTextInput+=(_,_)=>Dispatcher.BeginInvoke(()=>composing=false,DispatcherPriority.Background);
        input.PreviewKeyDown+=async(_,e)=>{if(e.Key==Key.Enter&&!composing&&e.Key!=Key.ImeProcessed&&(Keyboard.Modifiers&ModifierKeys.Shift)==0){e.Handled=true;await Send();}};
        ConfigureButton(send,"发送",async()=>await Send());Place(chat,send,792,674,84,37);ConfigureButton(record,"录音",async()=>await ToggleRecording());Place(chat,record,886,674,84,37);
        Place(chat,Button("中止",CancelResponse,17),980,674,84,37);Place(chat,Button("LOG",ShowLog,17),1074,674,80,37);Place(chat,Button("⚙",()=>OpenSystem("chat"),26),1183,647,60,64);
        Place(chat,Button("TITLE",async()=>{await StopTurnAsync();await QueueFinishedSessionAsync();ShowPage("title");},14),1149,18,107,35);
        Place(chat,Button("WORK",()=>{OpenSystem("chat");console?.SelectWork();},14),1030,18,107,35);
        BuildChatWorkProgress();
        ConfigureButton(imageButton,"IMAGE",()=>{var accepted=VisualInputView.Choose(this,out var chosen);ApplyVisualSelection(accepted,chosen);});Place(chat,imageButton,911,18,107,35);
    }
    internal void ApplyVisualSelection(bool accepted,VisualAttachment? chosen)
    {if(!accepted)return;attachment=chosen;imageButton.Content=chosen==null?"IMAGE":"IMAGE ✓";SetState(chosen==null?"已清除待发送的图片附件。":"已选择图片，发送时传给所配置的视觉模型；不会将图像推测保存为用户事实。");}
    private static void ConfigureButton(Button button,string text,Action action)
    {button.Content=text;button.FontSize=17;button.Foreground=Blue;button.Background=Brushes.White;button.BorderBrush=Aqua;button.Cursor=Cursors.Hand;button.Click+=(_,_)=>action();}
    internal void ShowPage(string page)
    {if(closing)return;if(page!="chat"){var hadRecording=microphone.Recording||recordingStarting;StopMicrophone();if(hadRecording)SetState("录音已取消");}navigationTask=navigation.NavigateAsync(page,destination=>GameSounds.Navigate(destination,!capture&&!uiSmoke&&!audioSmoke),lifetime.Token);}
    internal void OpenSystem(string from)
    {previousPage=from;if(console==null){console=new ConsoleView(this);system.Children.Add(console);}else console.Refresh();ShowPage("system");}
    internal void ReturnFromSystem()=>ShowPage(previousPage);
    internal void ApplySettings(){Store.Save(Settings);ConfigureGameAudio();if(Display.Mode!=Settings.DisplayMode)Display.Apply(Settings.DisplayMode);RenderSubtitle();}
    internal void SetState(string value)=>stateText.Text=value;
    internal void RenderSubtitle()
    {if(lastReply==null)return;subtitle.Text=currentSegment<0?string.Join("\n",lastReply.Segments.Select(s=>Settings.SubtitleLanguage=="zh"?s.Zh:s.Ja)):Settings.SubtitleLanguage=="zh"?lastReply.Segments[currentSegment].Zh:lastReply.Segments[currentSegment].Ja;}
    private void SetEmotion(string emotion)
    {currentEmotion=emotion;SetMouth(false);}
    private void SetMouth(bool open)
    {var relative=$"assets/derived/sprites/{currentEmotion}_{(open?"talk":"closed")}.png";if(!File.Exists(Paths.WithinRoot(relative)))relative=$"assets/derived/sprites/{currentEmotion}.png";character.Source=Bitmap(relative)??Bitmap("assets/derived/sprites/neutral.png");}
    private async Task Send()
    {if(memoryTransitioning||closing||busy||sendStarting||chatWorkSession is not null||chatWorkNeedsRecovery)return;sendStarting=true;var previousTurn=turnTask;CancelResponse();var launchEpoch=generation;try{await previousTurn;if(memoryTransitioning||closing||launchEpoch!=generation)return;turnTask=SendCore();await turnTask;}finally{sendStarting=false;}}
    private async Task SendCore()
    {
        if(busy||microphone.Recording)return;var text=input.Text.Trim();if(text.Length==0&&attachment==null)return;if(text.Length==0)text="この画像についてお話ししてくれますか。";if(text.Length>4000){SetState("输入最多 4000 字。");return;}
        var localCommand=text.StartsWith("/remember ")||text.StartsWith("/forget ")||System.Text.RegularExpressions.Regex.IsMatch(text,@"^(?:请)?(?:忘记|忘掉|遗忘|不要记住)");
        if(!localCommand&&(Settings.ApiBaseUrl.Length==0||Settings.Model.Length==0)){SetState("请先在 SYSTEM 填写对话 API 地址与模型名称。");return;}
        var visualForTurn=localCommand?null:attachment;if(!localCommand){attachment=null;imageButton.Content="IMAGE";}input.Clear();CancelResponse();active=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var token=active.Token;var epoch=generation;var memoryForTurn=Memory;var contextForTurn=context;var sessionForTurn=Settings.LastSessionId;var settingsForTurn=JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(Settings))!;busy=true;send.IsEnabled=false;record.IsEnabled=false;SetState("亚托莉正在思考…");
        try
        {
            if(text.StartsWith("/forget ",StringComparison.Ordinal)){var phrase=text[8..];var matched=await Task.Run(()=>memoryForTurn.List().FirstOrDefault(r=>r.Id==phrase),token);var n=await Task.Run(()=>{token.ThrowIfCancellationRequested();return memoryForTurn.Forget(phrase);},token);ClearVisibleContext(matched?.Text??phrase,matched?.Key=="user.preferred-name");SetState($"已遗忘并清理相关上下文：{n} 项。");return;}
            if(text.StartsWith("/remember ",StringComparison.Ordinal)){await Task.Run(()=>{token.ThrowIfCancellationRequested();memoryForTurn.Remember(MemoryKind.UserFact,"explicit:"+text[10..],text[10..],"user",1,.8,sessionForTurn,[text[10..]]);},token);SetState("已保存明确记忆。");return;}
            var forgetMatch=System.Text.RegularExpressions.Regex.Match(text,@"^(?:请)?(?:忘记|忘掉|遗忘|不要记住)[：:，,\s]*(.{2,100})[。！!]*$");
            if(forgetMatch.Success){var phrase=forgetMatch.Groups[1].Value.TrimEnd('。','！','!');var n=await Task.Run(()=>{token.ThrowIfCancellationRequested();return memoryForTurn.Forget(phrase);},token);ClearVisibleContext(phrase);SetState(n>0?$"已遗忘 {n} 项及相关上下文。":"已清理匹配的原始上下文；若要删除独立记忆，请在 SYSTEM / MEMORY 搜索并选择条目。");return;}
            var messages=await Task.Run(()=>contextForTurn.Build(sessionForTurn,text,settingsForTurn,token,visualForTurn),token);
            workProgressPanel.Visibility=Visibility.Collapsed;
            var bridgeForTurn=Capabilities;
            var keyForTurn=Store.ReadSecret("llm");
            var snapshot=await ChatWorkSnapshotAsync(bridgeForTurn,settingsForTurn,keyForTurn,token);
            var routed=messages.ToList();routed.Insert(routed.Count-1,new("system",ChatWork.Prompt(snapshot)));messages=routed;
            var reply=await api.ReplyAsync(settingsForTurn,keyForTurn,messages,token);token.ThrowIfCancellationRequested();if(epoch!=generation)return;
            if(reply.Work is {} workRequest)
            {
                await RunChatWorkTurnAsync(workRequest,reply,snapshot,text,bridgeForTurn,memoryForTurn,contextForTurn,sessionForTurn,settingsForTurn,keyForTurn,epoch,token);
                return;
            }
            // Classification has completed. Work instructions never enter the
            // general user-observation/history pipeline, even temporarily.
            var observed=await Task.Run(()=>{token.ThrowIfCancellationRequested();return memoryForTurn.ObserveUserMessage(sessionForTurn,text);},token);token.ThrowIfCancellationRequested();if(epoch!=generation)return;
            var nameObserved=observed.Any(r=>r.Key=="user.preferred-name");var historyText=observed.FirstOrDefault(r=>r.Source=="user-correction")?.Evidence?.FirstOrDefault()??text;if(nameObserved||observed.Any(r=>r.Source=="user-correction")){ClearVisibleContext(clearName:nameObserved);if(nameObserved)settingsForTurn.UserName="";}
            await Task.Run(()=>{token.ThrowIfCancellationRequested();memoryForTurn.AddMessage(sessionForTurn,"user",historyText);},token);
            lastReply=reply;currentSegment=-1;
            await Task.Run(()=>{token.ThrowIfCancellationRequested();contextForTurn.AcceptMemories(sessionForTurn,text,reply.Memories,token);token.ThrowIfCancellationRequested();memoryForTurn.Remember(MemoryKind.Event,"turn:"+Guid.NewGuid().ToString("N"),"实际聊天："+historyText[..Math.Min(historyText.Length,160)],"user-chat",1,.35,sessionForTurn,[historyText]);},token);
            var delivery=new ReplyDelivery(settingsForTurn.VoiceEnabled);var finished=false;
            try{if(settingsForTurn.VoiceEnabled){subtitle.Text="正在准备日语回复…";await Speak(reply,epoch,token,delivery);}else{RenderSubtitle();delivery.DisplayAll(reply.Segments);SetEmotion(reply.Segments[0].Emotion);SetState("回复完成 · 语音已关闭");}finished=true;}
            finally{await Task.Run(()=>{memoryForTurn.AddMessage(sessionForTurn,"assistant",delivery.Serialize(finished));if(finished)memoryForTurn.RecordInteraction(sessionForTurn,historyText);});if(!finished&&epoch==generation){lastReply=null;currentSegment=-1;subtitle.Text="回复未完整播放；已保留完成片段。";}}
        }
        catch(OperationCanceledException){if(epoch==generation)SetState("已中止");}
        catch(Exception e){if(epoch==generation){SetState(e.Message);Paths.Log("chat",e.GetType().Name);}}
        finally{if(epoch==generation){busy=false;RefreshChatWorkInput();}}
    }
    private async Task Speak(DialogueReply reply,long epoch,CancellationToken token,ReplyDelivery? delivery=null)
    {
        var responseId=Guid.NewGuid().ToString("N");var ids=new List<string>();using var pipeline=CancellationTokenSource.CreateLinkedTokenSource(token);var queue=Channel.CreateBounded<(DialogueSegment Segment,string File,string Id)>(new BoundedChannelOptions(2){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
        var producer=Task.Run(async()=>
        {
            try
            {
                foreach(var segment in reply.Segments)
                {var id=responseId+"_"+segment.Order;ids.Add(id);var segments=await Voice.SynthesizeAsync(segment.Ja,segment.Emotion,id,responseId,pipeline.Token);pipeline.Token.ThrowIfCancellationRequested();var joined=Path.Combine(Path.GetDirectoryName(segments[0].Path)!,"joined.wav");AudioPlayer.Join(segments.Select(x=>x.Path).ToArray(),joined);await queue.Writer.WriteAsync((segment,joined,id),pipeline.Token);}
                queue.Writer.TryComplete();
            }
            catch(Exception e){queue.Writer.TryComplete(e);}
        },pipeline.Token);
        SetState("正在合成日语语音…");
        try
        {
            await foreach(var item in queue.Reader.ReadAllAsync(token))
            {token.ThrowIfCancellationRequested();if(epoch!=generation)throw new OperationCanceledException(token);currentSegment=item.Segment.Order;RenderSubtitle();SetEmotion(item.Segment.Emotion);SetState($"播放中 · {item.Segment.Order+1}/{reply.Segments.Count}");delivery?.Start(item.Segment);speakingAudio=true;UpdateGameAudioActivity();try{await audio.PlayAsync(item.File,Settings.VoiceVolume,token);}finally{speakingAudio=false;UpdateGameAudioActivity();}delivery?.Complete(item.Segment);await Voice.ReleaseAsync(item.Id);}
            await producer;currentSegment=-1;RenderSubtitle();SetState("回复完成");
        }
        finally{pipeline.Cancel();try{await producer;}catch(OperationCanceledException){}foreach(var id in ids)await Voice.ReleaseAsync(id);}
    }
    internal void CancelResponse()
    {if(chatWorkSession is {DispatchStarted:true,IsExecutionPending:true} work){work.RequestStop();SetState("正在停止任务，请稍等…");return;}CancelResponseCore();}
    private void CancelResponseCore()
    {generation++;active?.Cancel();active?.Dispose();active=null;audio.Stop();StopMicrophone();lastReply=null;currentSegment=-1;busy=false;RefreshChatWorkInput();SetState("已中止");}
    private async Task ToggleRecording()
    {
        if(memoryTransitioning||closing||busy||sendStarting||recordingStarting||chatWorkSession is not null||chatWorkNeedsRecovery)return;if(microphone.Recording){if(wakeCapture){StopMicrophone();SetState("唤醒待机已关闭");}else await StopRecording();return;}
        recordingStarting=true;record.IsEnabled=false;var epoch=Interlocked.Increment(ref recordingGeneration);
        try
        {await vadTask;if(epoch!=recordingGeneration)return;audio.Stop();wakeCapture=Settings.WakeEnabled;wakeArmed=false;droppedFrames=0;
        if(Settings.VadEnabled||wakeCapture){await LocalAsr.EnsureStartedAsync(lifetime.Token);await LocalAsr.ResetVadAsync(lifetime.Token);if(epoch!=recordingGeneration)return;vadCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);vadFrames=Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});vadTask=ConsumeVad(vadFrames.Reader,wakeCapture,epoch,vadCancellation.Token);}
        if(epoch!=recordingGeneration)return;GameSounds.SetVoiceActivity(true,false);microphone.Start(Settings.MicrophoneDevice);UpdateGameAudioActivity();record.Content=wakeCapture?"关闭待机":"停止录音";SetState(wakeCapture?"● 麦克风开启 · 唤醒待机 · 说「亚托莉」或「アトリ」":"● 正在录音 · 最长 30 秒 · 原始音频不落盘");recordTimer.Start();}catch(Exception e){if(epoch==recordingGeneration){StopMicrophone();SetState(e.Message);}}finally{recordingStarting=false;RefreshChatWorkInput();}
    }
    private void StopMicrophone(){Interlocked.Increment(ref recordingGeneration);recordTimer.Stop();var queue=vadFrames;queue?.Writer.TryComplete();vadFrames=null;if(queue!=null)while(queue.Reader.TryRead(out var remaining))Array.Clear(remaining);vadCancellation?.Cancel();vadCancellation?.Dispose();vadCancellation=null;wakeCapture=false;try{microphone.Cancel();}catch(IOException e){SetState(e.Message);Paths.Log("microphone",e.GetType().Name);}UpdateGameAudioActivity();record.Content="录音";}
    private async Task ConsumeVad(ChannelReader<byte[]> frames,bool wake,long epoch,CancellationToken token)
    {
        try
        {
            await foreach(var frame in frames.ReadAllAsync(token))
            {
                if(epoch!=recordingGeneration){Array.Clear(frame);return;}
                if(Volatile.Read(ref droppedFrames)>0){Array.Clear(frame);await Dispatcher.InvokeAsync(()=>{if(epoch==recordingGeneration)SetState("自动端点队列繁忙，已暂停自动识别；请点击停止录音。");});return;}
                LocalVadFrame detection;try{detection=await LocalAsr.PushVadFrameAsync(frame,token);}finally{Array.Clear(frame);}
                try{foreach(var utterance in detection.Utterances)
                {
                    if(epoch!=recordingGeneration||token.IsCancellationRequested)return;
                    if(!wake){_=Dispatcher.BeginInvoke(async()=>{if(epoch==recordingGeneration)await StopRecording();});return;}
                    string text;try{text=await LocalAsr.TranscribeAsync(utterance.Wav,Settings.InputLanguage,token);}finally{Array.Clear(utterance.Wav);}if(epoch!=recordingGeneration||token.IsCancellationRequested)return;var match=LocalAsrWakeMatcher.Match(text);
                    if(wakeArmed&&DateTimeOffset.UtcNow>wakeUntil)wakeArmed=false;
                    if(!wakeArmed&&!match.Matched)continue;
                    var command=wakeArmed?text:match.Command;
                    if(command.Length==0){wakeArmed=true;wakeUntil=DateTimeOffset.UtcNow.AddSeconds(30);await Dispatcher.InvokeAsync(()=>{if(epoch==recordingGeneration)SetState("● 已唤醒 · 30 秒内继续说话 · 麦克风开启");});continue;}
                    await Dispatcher.InvokeAsync(()=>{if(epoch!=recordingGeneration||token.IsCancellationRequested)return;StopMicrophone();input.Text=command;SetState("唤醒识别完成 · 确认后发送");if(Settings.WakeAutoSend)_=Send();});return;
                }}finally{foreach(var utterance in detection.Utterances)Array.Clear(utterance.Wav);}
            }
        }
        catch(OperationCanceledException){}catch(Exception e){await Dispatcher.InvokeAsync(()=>{if(epoch==recordingGeneration&&!token.IsCancellationRequested){StopMicrophone();SetState("自动语音输入失败："+e.Message);}});}
    }
    private Task StopRecording()=>!microphone.Recording||busy?Task.CompletedTask:recordingTask=StopRecordingCore();
    private async Task StopRecordingCore()
    {
        var epoch=generation;Interlocked.Increment(ref recordingGeneration);recordTimer.Stop();vadFrames?.Writer.TryComplete();if(vadFrames!=null)while(vadFrames.Reader.TryRead(out var remaining))Array.Clear(remaining);vadFrames=null;vadCancellation?.Cancel();vadCancellation?.Dispose();vadCancellation=null;wakeCapture=false;byte[]? wav=null;using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);active?.Dispose();active=cancellation;busy=true;send.IsEnabled=false;record.IsEnabled=false;record.Content="录音";
        try{wav=microphone.Stop();UpdateGameAudioActivity();SetState(Settings.AsrProvider=="local"?"正在本地识别…":"正在通过 API 识别…");var text=Settings.AsrProvider=="local"?await LocalAsr.TranscribeAsync(wav,Settings.InputLanguage,cancellation.Token):await api.TranscribeAsync(Settings,Store.ReadSecret("asr"),wav,cancellation.Token);if(epoch!=generation||cancellation.IsCancellationRequested||!ReferenceEquals(active,cancellation))return;input.Text=text;SetState("识别完成 · 确认文本后发送");}
        catch(OperationCanceledException){if(epoch==generation)SetState("识别已取消");}catch(Exception e){if(epoch==generation)SetState(e.Message);}
        finally{if(wav!=null)Array.Clear(wav);if(epoch==generation&&ReferenceEquals(active,cancellation)){busy=false;active=null;RefreshChatWorkInput();}}
    }
    internal async Task NewSession()
    {if(memoryTransitioning||closing)return;await StopTurnAsync();await QueueFinishedSessionAsync();Settings.LastSessionId=Guid.NewGuid().ToString("N");Store.Save(Settings);lastReply=null;ClearChatWorkProgress();subtitle.Text="新的会话已开始。长期记忆仍可检索。";}
    internal async Task StopTurnAsync(){var pendingTurn=turnTask;var pendingRecording=recordingTask;var pendingVad=vadTask;chatWorkSession?.RequestStop();CancelResponseCore();try{await Task.WhenAll(pendingTurn,pendingRecording,pendingVad);}catch(OperationCanceledException) when(pendingTurn.IsCanceled||pendingRecording.IsCanceled||pendingVad.IsCanceled){}}
    internal async Task SwitchSession(string session)
    {if(memoryTransitioning||closing)return;await StopTurnAsync();if(session!=Settings.LastSessionId)await QueueFinishedSessionAsync();Settings.LastSessionId=session;Store.Save(Settings);lastReply=null;currentSegment=-1;ClearChatWorkProgress();var recent=await Task.Run(()=>Memory.Recent(session,1));if(Settings.LastSessionId==session){subtitle.Text=recent.LastOrDefault() is {} m?Readable(m.Text):"此会话尚无消息。";if(recent.Count>0)GameSounds.Announce("resume");}}
    internal void ClearVisibleContext(string? affected=null,bool clearName=false)
    {lastReply=null;currentSegment=-1;ClearChatWorkProgress();subtitle.Text="记忆已更新。";input.Clear();attachment=null;imageButton.Content="IMAGE";if(clearName||(affected?.Length>0&&Settings.UserName.Length>0&&(affected.Contains(Settings.UserName,StringComparison.OrdinalIgnoreCase)||Settings.UserName.Contains(affected,StringComparison.OrdinalIgnoreCase)))){Settings.UserName="";Store.Save(Settings);console?.RefreshProfile();}}
    internal async Task RestoreMemory(string backup,IReadOnlyList<Task>? priorOperations=null)
    {
        if(memoryTransitioning||closing)return;memoryTransitioning=true;
        await memoryTransitionGate.WaitAsync();
        var memoryReady=true;
        try
        {
            await StopTurnAsync();await PauseModelMemoryAsync();send.IsEnabled=false;record.IsEnabled=false;
            if(priorOperations is {Count:>0})try{await Task.WhenAll(priorOperations);}catch(Exception error){Paths.Log("restore-drain",error.GetType().Name);}
            await Task.Run(()=>{memoryReady=false;Memory.Dispose();try{MemoryStore.RestoreBackup(Paths.Memory,backup);}finally{var restored=new MemoryStore(Paths.Memory);var restoredContext=new ContextBuilder(Paths,restored);Memory=restored;context=restoredContext;memoryReady=true;}});
            Settings.LastSessionId=Guid.NewGuid().ToString("N");Store.Save(Settings);lastReply=null;subtitle.Text="备份已恢复；新的会话已开始。";
        }
        finally
        {
            memoryTransitionGate.Release();
            if(memoryReady){memoryTransitioning=false;modelMemoryPaused=false;if(!closing){RefreshChatWorkInput();StartModelMemoryWork();}}
            else
            {
                SetState("数据库尚未安全重新打开；已暂停操作，请退出后重启以恢复。");
                if(!closing)
                {
                    title.IsEnabled=chat.IsEnabled=system.IsEnabled=false;
                    var recovery=new StackPanel{Margin=new Thickness(28),MaxWidth=740};
                    recovery.Children.Add(new TextBlock{Text="备份恢复尚未完成，数据库未能安全重新打开。已暂停操作以保护现有数据；请解除文件占用或修复磁盘权限后重新启动。",TextWrapping=TextWrapping.Wrap,FontSize=20,Foreground=Blue});
                    recovery.Children.Add(Button("退出，重新启动时恢复",Close,18));
                    stage.Children.Add(new Border{Background=Brushes.AliceBlue,BorderBrush=Aqua,BorderThickness=new Thickness(2),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Child=recovery});
                }
            }
        }
    }
    private void ShowLog()
    {
        var log=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontSize=18,Padding=new Thickness(24),Background=new SolidColorBrush(Color.FromRgb(237,248,255)),Foreground=Blue};
        var window=new Window{Title="ATRI · 对话记录",Owner=this,Width=880,Height=580,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=log};
        var rows=Memory.Recent(Settings.LastSessionId,200);log.Text=string.Join("\n\n",rows.Select(m=>m.Role=="assistant"?"亚托莉："+Readable(m.Text):"你："+m.Text));window.ShowDialog();
    }
    internal string Readable(string text)
    {return ReplyDelivery.Readable(text,Settings.SubtitleLanguage);}
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(shutdownComplete)return;e.Cancel=true;if(closing)return;closing=true;title.IsEnabled=chat.IsEnabled=system.IsEnabled=false;confirmationCompletion?.TrySetResult(false);await Task.Yield();
        async Task Release(string name,Func<Task> work){try{await work();}catch(Exception error){Paths.Log("shutdown",name+" "+error.GetType().Name);}}
        await Release("memory-transition",async()=>{await memoryTransitionGate.WaitAsync();memoryTransitionGate.Release();});
        lifetime.Cancel();navigation.Dispose();GameSounds.StopAll();Display.Dispose();
        await Release("navigation",()=>navigationTask);await Release("desktop",()=>{Desktop.Detach(this);Desktop.Dispose();return Task.CompletedTask;});await Release("turn",StopTurnAsync);await Release("console",()=>console?.DrainOperationsAsync()??Task.CompletedTask);await Release("startup",()=>startupTask);await Release("microphone",()=>{microphone.Dispose();return Task.CompletedTask;});await Release("audio",()=>{audio.Dispose();GameSounds.Dispose();return Task.CompletedTask;});await Release("voice",()=>Voice.DisposeAsync().AsTask());await Release("asr",()=>LocalAsr.DisposeAsync().AsTask());await Release("capabilities",()=>Capabilities.DisposeAsync().AsTask());await Release("model-memory",FinishModelMemoryOnExitAsync);await Release("memory",()=>Task.Run(Memory.Dispose));await Release("api",()=>{api.Dispose();return Task.CompletedTask;});await Release("settings",()=>{if(!FactoryReset.HasPending(Paths))Store.Save(Settings);return Task.CompletedTask;});shutdownComplete=true;Close();
    }
    public void EnableCapture()=>capture=true;
    public void EnableAudioSmoke()=>audioSmoke=true;
    public void EnableUiSmoke()=>uiSmoke=true;
    public void EnableFactoryResetProbe()=>factoryResetProbe=true;
    private async Task RunUiSmoke()
    {
        var report=new List<object>();var original=JsonSerializer.Serialize(Settings);var dir=Path.Combine(Paths.Data,"verification","ui-navigation");Directory.CreateDirectory(dir);var blackFrames=new List<bool>();
        using var audioCatalog=JsonDocument.Parse(File.ReadAllText(Paths.WithinRoot("assets/derived/game-audio.json")));
        var chatTracks=audioCatalog.RootElement.GetProperty("chatBgm").EnumerateArray().Select(track=>track.GetString()!).ToHashSet(StringComparer.Ordinal);
        void Check(bool condition,string name,string evidence="A: actual WPF controls"){report.Add(new{name,passed=condition,evidence});if(!condition)throw new InvalidOperationException(name);}
        async Task WaitForMusic(string track){var clock=System.Diagnostics.Stopwatch.StartNew();while((track=="chat"?!chatTracks.Contains(GameSounds.Inspect().Bgm??""):GameSounds.Inspect().Bgm!=track)||GameSounds.Inspect().Position<.12){if(clock.Elapsed>TimeSpan.FromSeconds(5))throw new TimeoutException("BGM实际播放未开始："+track);await Task.Delay(25);}}
        Button TitleButton(string name)=>title.Children.OfType<Button>().Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)==name);
        Button FindButton(DependencyObject parent,string text){for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is Button b&&b.Content is string s&&s==text)return b;try{return FindButton(child,text);}catch(InvalidOperationException){}}throw new InvalidOperationException("Missing button "+text);}
        void BlackFrame(){var bitmap=CaptureViewport(Path.Combine(dir,$"black-frame-{blackFrames.Count+1}.png"));var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);blackFrames.Add(Enumerable.Range(0,pixels.Length/4).All(i=>pixels[i*4]==0&&pixels[i*4+1]==0&&pixels[i*4+2]==0&&pixels[i*4+3]==255));}
        navigation.BlackFrameReached+=BlackFrame;
        try
        {
            Settings.ApiBaseUrl="";Settings.Model="";Settings.BgmEnabled=true;GameSounds.Configure(true,.12,.1,.3);GameSounds.Navigate("title",false);await WaitForMusic("title");Check(GameSounds.Inspect().BgmOpened>0&&GameSounds.Inspect().Duration>100,"product title BGM actually opens and advances");Display.Apply("window");await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var dpi=VisualTreeHelper.GetDpi(stage);
            Check(Math.Abs(stage.ActualWidth*dpi.DpiScaleX-1280)<1&&Math.Abs(stage.ActualHeight*dpi.DpiScaleY-720)<1,"fixed physical 1280x720 viewport");
            Check(WindowStyle==WindowStyle.None&&ResizeMode==ResizeMode.NoResize,"only fixed borderless display modes");
            Check(navigation.CurrentPage=="title","startup title without API");
            TitleButton("system").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await navigationTask;Check(navigation.CurrentPage=="system","title SYSTEM click after black transition");
            FindButton(system,"返回").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await navigationTask;Check(navigation.CurrentPage=="title","return to title with crossfade");
            TitleButton("start").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await navigationTask;Check(navigation.CurrentPage=="chat","START click after black transition");await WaitForMusic("chat");Check(chatTracks.Contains(GameSounds.Inspect().Bgm??"")&&GameSounds.Inspect().Duration>100,"product chat BGM switches and advances");
            Check(navigation.BlackTransitionCount==2&&blackFrames.Count==2&&blackFrames.All(v=>v),"title routes present two actual opaque black frames");
            var selectedBackground=background;FindButton(chat,"⚙").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await navigationTask;
            FindButton(system,"返回").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await navigationTask;Check(navigation.CurrentPage=="chat","gear and return to chat");Check(background==selectedBackground,"background preserved across navigation");
            input.Text="中文输入与日本語入力\n第二行";Check(input.Text.Contains("日本語")&&input.Text.Contains('\n'),"bilingual multiline WPF input");send.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Check(input.Text.Length>0&&stateText.Text.Contains("API"),"missing API preserves input and gives error");
            composing=true;var key=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(input),0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent};input.RaiseEvent(key);Check(!key.Handled,"composition Enter does not send","B: synthetic composition flag and routed event; real IME still manual");composing=false;
            lastReply=new DialogueReply([new(0,"了解。","明白。","neutral")],[]);currentSegment=-1;Settings.SubtitleLanguage="zh";RenderSubtitle();Check(subtitle.Text=="明白。","Chinese subtitle display","B: explicit UI sample");Settings.SubtitleLanguage="ja";RenderSubtitle();Check(subtitle.Text=="了解。","Japanese subtitle display","B: same explicit UI sample");
            ClearVisibleContext(clearName:true);RenderSubtitle();Check(lastReply==null&&currentSegment==-1&&subtitle.Text=="记忆已更新。"&&input.Text.Length==0,"privacy edit clears visible cached response");
            OpenSystem("chat");await navigationTask;console!.SelectTab(1);await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var bgmCheck=Descendants<CheckBox>(system).Single(c=>Equals(c.Content,"播放背景音乐"));bgmCheck.IsChecked=false;Check(!Settings.BgmEnabled&&!GameSounds.Inspect().Enabled,"BGM checkbox immediately disables manager");bgmCheck.IsChecked=true;Check(Settings.BgmEnabled&&GameSounds.Inspect().Enabled,"BGM checkbox immediately enables manager");GameSounds.Configure(false,0,0,0);
            console.SelectTab(4);await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Check(console.WorkPageCount==3,"WORK has three separate tool pages");for(int page=0;page<3;page++){console.SelectWorkPage(page);await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Check(Descendants<CapabilityView>(system).Single().ActualHeight>350,$"WORK page {page+1} has usable content height");}
            console.SelectTab(3);await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);var desktopBefore=Desktop.Inspect(this);FindButton(system,"嵌入桌面").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Check(Desktop.IsAttached,"SYSTEM desktop attach button",Desktop.State);FindButton(system,"恢复普通窗口").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));var desktopAfter=Desktop.Inspect(this);Check(!Desktop.IsAttached&&desktopAfter.Style==desktopBefore.Style&&desktopAfter.ExtendedStyle==desktopBefore.ExtendedStyle,"SYSTEM desktop restore button");
            Check(Display.Apply("fullscreen"),"fullscreen applies");await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);dpi=VisualTreeHelper.GetDpi(stage);var monitor=Display.CurrentMonitor();Check(Math.Abs(stage.ActualWidth*dpi.DpiScaleX-monitor.Bounds.Width)<1&&Math.Abs(stage.ActualHeight*dpi.DpiScaleY-monitor.Bounds.Height)<1,"fullscreen fills physical monitor viewport");
            var fullscreen=CaptureViewport(Path.Combine(dir,"fullscreen.png"));var edge=new byte[fullscreen.PixelWidth*4];fullscreen.CopyPixels(new Int32Rect(0,0,fullscreen.PixelWidth,1),edge,fullscreen.PixelWidth*4,0);Check(edge.Where((_,i)=>i%4!=3).Average(v=>(double)v)>5,"fullscreen top edge contains artwork, no black letterbox");
            Check(Display.Apply("window"),"returns to fixed window");ShowPage("title");await navigationTask;ShowPage("chat");await Task.Delay(60);ShowPage("system");await navigationTask;Check(navigation.CurrentPage=="system"&&!navigation.IsTransitioning&&transitionBlack.Visibility==Visibility.Collapsed,"rapid navigation settles newest destination and clears overlay");
            File.WriteAllText(Path.Combine(dir,"report.json"),JsonSerializer.Serialize(new{passed=true,tests=report,blackFrames=blackFrames.Count,transitions=navigation.CompletedCount,apiCalled=false,physicalMicrophoneOpened=false},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception e){File.WriteAllText(Path.Combine(dir,"report.json"),JsonSerializer.Serialize(new{passed=false,error=e.Message,tests=report,apiCalled=false},new JsonSerializerOptions{WriteIndented=true}));}
        finally{navigation.BlackFrameReached-=BlackFrame;var restored=JsonSerializer.Deserialize<Settings>(original)!;foreach(var property in typeof(Settings).GetProperties())property.SetValue(Settings,property.GetValue(restored));Close();}
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T match)yield return match;foreach(var nested in Descendants<T>(child))yield return nested;}}
    internal async Task TestLocalVoice()
    {
        if(memoryTransitioning||closing||sendStarting)throw new InvalidOperationException("正在处理其他对话或语音操作，请稍后重试。");
        sendStarting=true;
        try{await StopTurnAsync();if(memoryTransitioning||closing)return;turnTask=TestLocalVoiceCore();await turnTask;}
        finally{sendStarting=false;}
    }
    private async Task TestLocalVoiceCore()
    {
        active=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var token=active.Token;var epoch=generation;busy=true;send.IsEnabled=false;record.IsEnabled=false;
        var diagnostic=new DialogueReply([new(0,"こんにちは。私はアトリです。今日も一緒にお話ししましょう。","你好。我是亚托莉。今天也一起聊聊天吧。","happy"),new(1,"声の確認はこれで終わりです。","声音检查到这里结束。","serious")],[]);lastReply=diagnostic;
        try{await Speak(diagnostic,epoch,token);SetState("本地语音自检完成（未调用对话 API）");}finally{if(epoch==generation){busy=false;RefreshChatWorkInput();}}
    }
    private async Task RunAudioSmoke()
    {
        ShowPage("chat");await navigationTask;var dir=Path.Combine(Paths.Data,"verification","audio-playback");Directory.CreateDirectory(dir);var began=DateTimeOffset.Now;
        try{await TestLocalVoice();var initialCompleted=audio.CompletedCount;await StopTurnAsync();using var interrupt=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);interrupt.CancelAfter(TimeSpan.FromSeconds(60));var interrupted=false;var scheduled=false;var proof=new ReplyDelivery(true);lastReply=new DialogueReply([new(0,"前半の声です。","这是前半段声音。","neutral"),new(1,"後半の声です。最後まで続けます。","这是后半段声音。会继续到最后。","happy")],[]);void CancelOnSecond(bool open){if(open&&currentSegment==1&&!scheduled){scheduled=true;interrupt.CancelAfter(TimeSpan.FromMilliseconds(200));}}audio.MouthChanged+=CancelOnSecond;try{await Speak(lastReply,generation,interrupt.Token,proof);}catch(OperationCanceledException){interrupted=true;}finally{audio.MouthChanged-=CancelOnSecond;}var transcript=proof.Serialize(false);File.WriteAllText(Path.Combine(dir,"interrupted-delivery.json"),transcript);using(var delivered=JsonDocument.Parse(transcript)){if(!interrupted||!scheduled||delivered.RootElement.GetProperty("segments").GetArrayLength()!=1||delivered.RootElement.GetProperty("delivery").GetProperty("partialSegmentOrder").GetInt32()!=1||audio.CompletedCount!=initialCompleted+1)throw new InvalidDataException("实际中断播放/历史片段验证失败。");}File.WriteAllText(Path.Combine(dir,"report.json"),JsonSerializer.Serialize(new{passed=true,source="actual AtriTurbo and WPF MediaPlayer MediaEnded",normalSegments=2,interruptedRunCompletedSegments=1,partialSecondSegmentExcluded=true,completedAudio=audio.CompletedCount,mouthTransitions=audio.MouthTransitions,started=began,finished=DateTimeOffset.Now,apiCalled=false},new JsonSerializerOptions{WriteIndented=true}));}
        catch(Exception e){File.WriteAllText(Path.Combine(dir,"report.json"),JsonSerializer.Serialize(new{passed=false,error=e.Message,apiCalled=false}));}
        Close();
    }
    private async Task CaptureLayouts()
    {
        var output=Path.Combine(Paths.Data,"verification","ui-current");Directory.CreateDirectory(output);var captures=new List<object>();
        foreach(var mode in new[]{"window","fullscreen"})
        {
            if(!Display.Apply(mode))throw new InvalidOperationException(Display.State);
            foreach(var page in new[]{"title","chat","system"})
            {
                if(page=="system")OpenSystem("chat");else ShowPage(page);
                await navigationTask;
                if(page=="system")console?.SelectTab(0);
                await SaveCapture($"{page}-{mode}.png");
                if(page=="system")for(int tab=1;tab<5;tab++)
                {
                    console?.SelectTab(tab);if(tab==4)console?.SelectWorkPage(0);await SaveCapture($"system-tab{tab}-{mode}.png");
                    if(tab==4)for(int sub=1;sub<(console?.WorkPageCount??0);sub++){console?.SelectWorkPage(sub);await SaveCapture($"work-page{sub+1}-{mode}.png");}
                }
            }
        }
        File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new{passed=true,modes=new[]{"physical 1280x720 window","current physical monitor fullscreen"},background,captures,apiCalled=false},new JsonSerializerOptions{WriteIndented=true}));Close();
        async Task SaveCapture(string file)
        {
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(100);UpdateLayout();var bitmap=CaptureViewport(Path.Combine(output,file));
            captures.Add(new{file,width=bitmap.PixelWidth,height=bitmap.PixelHeight,mode=Display.Mode});
        }
    }
    private RenderTargetBitmap CaptureViewport(string file)
    {
        UpdateLayout();var dpi=VisualTreeHelper.GetDpi(stage);var bounds=new Rect(0,0,stage.ActualWidth,stage.ActualHeight);var drawing=new DrawingVisual();
        using(var dc=drawing.RenderOpen())dc.DrawRectangle(new VisualBrush(stage){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=bounds,Stretch=Stretch.Fill},null,bounds);
        var bitmap=new RenderTargetBitmap((int)Math.Round(bounds.Width*dpi.DpiScaleX),(int)Math.Round(bounds.Height*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);bitmap.Render(drawing);bitmap.Freeze();var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(file);png.Save(output);return bitmap;
    }
}
