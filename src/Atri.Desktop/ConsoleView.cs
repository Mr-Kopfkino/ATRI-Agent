using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Atri.Core;
using Microsoft.Win32;

namespace Atri.Desktop;
internal sealed class ConsoleView:Grid
{
    private readonly MainWindow owner;private readonly TextBlock status=new(){Foreground=MainWindow.Blue,TextWrapping=TextWrapping.Wrap,FontSize=16};private CapabilityView? workView;
    private readonly Dictionary<string,TextBox> fields=[];private readonly PasswordBox llmKey=new(),asrKey=new();
    private readonly ComboBox subtitle=new(){ItemsSource=new[]{"中文字幕","日文字幕"}},language=new(){ItemsSource=new[]{"中文","日本語"}},devices=new(),models=new(){DisplayMemberPath="Name"},asrProvider=new(){ItemsSource=new[]{"本地 SenseVoice · 离线","外部兼容 API"}};
    private readonly CheckBox structured=new(){Content="请求 JSON 输出（不支持时自动兼容）"},enabled=new(){Content="启用日语语音"},bgmEnabled=new(){Content="播放背景音乐"};
    private readonly CheckBox modelMemoryEnabled=new(){Content="会话结束时用对话模型整理记忆",FontSize=16,VerticalAlignment=VerticalAlignment.Center};
    private readonly ComboBox displayMode=new(){ItemsSource=new[]{"1280 × 720 窗口","全屏"}};private bool refreshing;
    private readonly CheckBox vadEnabled=new(){Content="自动检测语音结束（本地 Silero）"},wakeEnabled=new(){Content="录音按钮开启唤醒待机"},wakeAutoSend=new(){Content="唤醒后自动发送（会调用对话 API）"};
    private readonly Slider voiceVolume=new(){Minimum=0,Maximum=1},effectVolume=new(){Minimum=0,Maximum=1},bgmVolume=new(){Minimum=0,Maximum=1};
    private readonly DataGrid memories=new(){AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,SelectionMode=DataGridSelectionMode.Single,FontSize=15,RowHeight=30,HeadersVisibility=DataGridHeadersVisibility.Column};
    private readonly TextBox search=new(){FontSize=17},edit=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly TextBlock diagnostics=new(){TextWrapping=TextWrapping.Wrap,FontSize=17,Foreground=MainWindow.Blue};private readonly ComboBox sessions=new(){DisplayMemberPath="Title",MinWidth=300,FontSize=15};private readonly TextBox sessionTitle=new(){Width=220,FontSize=15};
    private readonly TabControl tabs=new(){FontSize=17,Background=Brushes.Transparent,BorderBrush=Brushes.Transparent,BorderThickness=new Thickness(0)};private int searchVersion;
    private readonly HashSet<Task> operations=[];
    private async void RunOperation(Func<Task> work)
    {
        if(owner.IsMemoryTransitioning||owner.IsClosing)return;
        Task? task=null;
        try{task=work();operations.Add(task);await task;}
        catch(Exception error){status.Text=error.Message;}
        finally{if(task!=null)operations.Remove(task);}
    }
    internal async Task DrainOperationsAsync()
    {
        while(operations.Count>0)
        {
            try{await Task.WhenAll(operations.ToArray());}catch{/* Each operation reports its own error. */}
            // A completed operation's async-void wrapper may still be queued on
            // this dispatcher. Do not spin here and starve its final removal.
            operations.RemoveWhere(task=>task.IsCompleted);
        }
    }
    internal void ReportResetStatus(string message)=>status.Text=message;
    public ConsoleView(MainWindow parent)
    {
        owner=parent;Width=1280;Height=720;tabs.SelectionChanged+=AnnounceTab;displayMode.SelectionChanged+=(_,_)=>{if(!refreshing)owner.GameSounds.Announce("display-settings");};
        Background=Brushes.Transparent;
        MainWindow.Place(this,new TextBlock{Text="SYSTEM",FontSize=56,Foreground=Brushes.White,FontWeight=FontWeights.Light},24,8,450,80);
        MainWindow.Place(this,new Border{Background=new SolidColorBrush(Color.FromArgb(185,246,253,255)),Padding=new Thickness(12),Child=status},550,15,690,55);
        MainWindow.Place(this,tabs,36,93,1208,545);
        tabs.Items.Add(Tab("MODEL / 对话",Api()));tabs.Items.Add(Tab("SOUND / 语音",Sound()));tabs.Items.Add(Tab("MEMORY / 记忆",Memory()));tabs.Items.Add(Tab("SYSTEM / 诊断",Diagnostics()));tabs.Items.Add(Tab("WORK / 工作",workView=new CapabilityView(parent,parent.Capabilities)));
        MainWindow.Place(this,MainWindow.Button("保存设置",()=>RunOperation(Save)),854,657,170,48);MainWindow.Place(this,MainWindow.Button("返回",parent.ReturnFromSystem),1039,657,170,48);
        bgmEnabled.Checked+=(_,_)=>ToggleBgm();bgmEnabled.Unchecked+=(_,_)=>ToggleBgm();displayMode.SelectionChanged+=(_,_)=>{if(refreshing)return;try{owner.SetDisplayMode(displayMode.SelectedIndex==1?"fullscreen":"window");status.Text="显示模式已切换；F11 可切换，Esc 返回窗口。";}catch(Exception e){status.Text=e.Message;refreshing=true;displayMode.SelectedIndex=owner.Display.Mode=="fullscreen"?1:0;refreshing=false;}};tabs.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.OriginalSource,tabs))owner.GameSounds.Select();};Refresh();
    }
    private static TabItem Tab(string title,UIElement body)=>new(){Header=title,Content=new Border{Background=new SolidColorBrush(Color.FromRgb(245,252,255)),BorderBrush=MainWindow.Aqua,BorderThickness=new Thickness(1),Padding=new Thickness(22),Child=body}};
    private void AnnounceTab(object sender,SelectionChangedEventArgs e){if(!ReferenceEquals(e.OriginalSource,tabs)||refreshing)return;if(tabs.SelectedIndex==1)owner.GameSounds.Announce("sound-settings");else if(tabs.SelectedIndex==2)owner.GameSounds.Announce("memory");}
    private StackPanel Section(string title)
    {var p=new StackPanel{Margin=new Thickness(0,0,24,0)};p.Children.Add(new TextBlock{Text="▪  "+title,Foreground=MainWindow.Blue,FontSize=22,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,16)});return p;}
    private TextBox Field(StackPanel panel,string key,string label)
    {panel.Children.Add(new TextBlock{Text=label,Foreground=MainWindow.Blue,FontSize=16,Margin=new Thickness(0,7,0,4)});var t=new TextBox{FontSize=17,Padding=new Thickness(7,5,7,5),MaxLength=1024};fields[key]=t;panel.Children.Add(t);return t;}
    private static void Label(StackPanel panel,string text,UIElement input)
    {panel.Children.Add(new TextBlock{Text=text,Foreground=MainWindow.Blue,FontSize=16,Margin=new Thickness(0,10,0,6)});if(input is Control c){c.FontSize=17;c.Padding=new Thickness(6);}panel.Children.Add(input);}
    private UIElement Api()
    {
        var grid=new Grid();grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new());var chat=Section("人格模型 · Chat Completions");var asr=Section("语音输入 · Audio Transcriptions");Grid.SetColumn(asr,1);grid.Children.Add(chat);grid.Children.Add(asr);
        Field(chat,"api","API 基址（含 /v1，或完整 /chat/completions）");Field(chat,"model","模型名称");Label(chat,"密钥（留空保留已保存密钥）",llmKey);Field(chat,"user","希望亚托莉使用的称呼（可留空）");
        var numeric=new Grid();for(int i=0;i<3;i++)numeric.ColumnDefinitions.Add(new());int n=0;foreach(var tuple in new[]{("temperature","温度 0–2"),("timeout","超时 10–300 秒"),("tokens","回复 token 上限（正整数）")}){var p=new StackPanel{Margin=new Thickness(0,0,8,0)};var field=Field(p,tuple.Item1,tuple.Item2);if(tuple.Item1=="tokens")field.ToolTip="不设应用侧 token 允许范围；实际可用上限由 API 模型决定。";Grid.SetColumn(p,n++);numeric.Children.Add(p);}chat.Children.Add(numeric);structured.Margin=new Thickness(0,16,0,0);chat.Children.Add(structured);
        Label(asr,"识别来源（本地中日识别已部署）",asrProvider);Field(asr,"asr","识别 API 基址（使用外部 API 时填写）");Field(asr,"asrModel","语音识别模型（外部 API）");Label(asr,"识别密钥（留空保留）",asrKey);Label(asr,"输入语言",language);Label(asr,"麦克风设备",devices);
        foreach(var check in new[]{vadEnabled,wakeEnabled,wakeAutoSend}){check.Margin=new Thickness(0,8,0,0);asr.Children.Add(check);}
        asr.Children.Add(new TextBlock{Text="点击录音开始，停止后识别，确认文字再发送。待机始终显示麦克风状态；切换页面或播放时关闭收音。原始音频仅在内存。",TextWrapping=TextWrapping.Wrap,FontSize=16,Foreground=MainWindow.Blue,Margin=new Thickness(0,16,0,0)});
        return new ScrollViewer{Content=grid,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    }
    private UIElement Sound()
    {
        var grid=new Grid();grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new());var left=Section("VOICE · GPT-SoVITS v2ProPlus");var right=Section("TEXT & SOUND");Grid.SetColumn(right,1);grid.Children.Add(left);grid.Children.Add(right);
        Label(left,"已登记模型（只支持 v2ProPlus）",models);var buttons=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,12,0,12)};buttons.Children.Add(MainWindow.Button("刷新模型",()=>RunOperation(RefreshModels),17));buttons.Children.Add(MainWindow.Button("加载所选",()=>RunOperation(LoadModel),17));left.Children.Add(buttons);
        left.Children.Add(new TextBlock{Text="将一对 .ckpt / .pth 放入 models/voice/<名称>/ 后刷新。缺失、重复或版本不兼容会明确拒绝；原始权重不修改。",TextWrapping=TextWrapping.Wrap,Foreground=MainWindow.Blue,FontSize=17,Margin=new Thickness(0,7,20,20)});
        left.Children.Add(MainWindow.Button("真实日语语音自检",()=>RunOperation(TestVoice),17));Field(left,"python","Python 相对路径（留空使用内置运行时）");
        Label(right,"字幕语言（只改变显示，语音始终为日语）",subtitle);Label(right,"角色语音 / 系统提示音量",voiceVolume);Label(right,"按键音效音量",effectVolume);enabled.Margin=new Thickness(0,18,0,12);right.Children.Add(enabled);bgmEnabled.Margin=new Thickness(0,8,0,0);right.Children.Add(bgmEnabled);Label(right,"背景音乐音量",bgmVolume);
        right.Children.Add(new TextBlock{Text="标题与聊天使用原作 BGM；BGM 开关立即生效，音量保存后生效。录音时背景音乐静音，角色说话时自动压低。系统语音仅用于台词匹配的操作。",TextWrapping=TextWrapping.Wrap,Foreground=MainWindow.Blue,FontSize=16,Margin=new Thickness(0,14,0,0)});
        return new ScrollViewer{Content=grid,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    }
    private UIElement Memory()
    {
        var host=new DockPanel();var options=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,10)};
        options.Children.Add(modelMemoryEnabled);options.Children.Add(new TextBlock{Text="  记忆整理 token 上限：",FontSize=16,VerticalAlignment=VerticalAlignment.Center});
        var memoryTokens=new TextBox{Width=110,FontSize=16,VerticalContentAlignment=VerticalAlignment.Center,ToolTip="只要求正整数，实际上限由所选模型决定。与普通回复分别设置。"};fields["memoryTokens"]=memoryTokens;options.Children.Add(memoryTokens);
        options.Children.Add(MainWindow.Button("整理当前会话",()=>RunOperation(async()=>{try{status.Text="正在整理；使用已保存的 API 与记忆设置。";await owner.ConsolidateCurrentMemoryAsync();RefreshMemory();UpdateDiagnostics();status.Text=owner.Memory.ModelMemoryStatus;}catch(Exception error){status.Text=error.Message;}}),15));
        DockPanel.SetDock(options,Dock.Top);host.Children.Add(options);
        var grid=new Grid();foreach(var h in new[]{44d,45d,180d,80d,52d})grid.RowDefinitions.Add(new(){Height=new GridLength(h)});
        var sessionRow=new StackPanel{Orientation=Orientation.Horizontal};sessionRow.Children.Add(sessions);sessionRow.Children.Add(sessionTitle);
        sessionRow.Children.Add(MainWindow.Button("切换",()=>RunOperation(async()=>{try{if(sessions.SelectedItem is MemorySession s){await owner.SwitchSession(s.SessionId);status.Text="已恢复会话："+s.Title;}}catch(Exception e){status.Text=e.Message;}}),15));
        sessionRow.Children.Add(MainWindow.Button("改名",()=>RunOperation(async()=>{try{if(sessions.SelectedItem is MemorySession s){var text=sessionTitle.Text;await Task.Run(()=>owner.Memory.RenameSession(s.SessionId,text));RefreshSessions();}}catch(Exception e){status.Text=e.Message;}}),15));
        sessionRow.Children.Add(MainWindow.Button("删除会话",()=>RunOperation(async()=>{try{if(sessions.SelectedItem is not MemorySession s)return;await owner.StopTurnAsync();await Task.Run(()=>owner.Memory.DeleteSession(s.SessionId));owner.ClearVisibleContext();if(owner.Settings.LastSessionId==s.SessionId)await owner.NewSession();RefreshSessions();status.Text="会话记录已删除；独立长期事实保留。";}catch(Exception e){status.Text=e.Message;}}),15));grid.Children.Add(sessionRow);sessions.SelectionChanged+=(_,_)=>sessionTitle.Text=(sessions.SelectedItem as MemorySession)?.Title??"";
        var top=new Grid();top.ColumnDefinitions.Add(new());top.ColumnDefinitions.Add(new(){Width=new GridLength(130)});top.Children.Add(search);var find=MainWindow.Button("搜索",RefreshMemory,17);Grid.SetColumn(find,1);top.Children.Add(find);Grid.SetRow(top,1);grid.Children.Add(top);
        foreach(var column in new[]{("类型","Kind",125d),("键","Key",170d),("内容","Text",430d),("来源","Source",140d),("更新","UpdatedAt",180d)})memories.Columns.Add(new DataGridTextColumn{Header=column.Item1,Binding=new System.Windows.Data.Binding(column.Item2),Width=column.Item3});
        Grid.SetRow(memories,2);grid.Children.Add(memories);memories.SelectionChanged+=(_,_)=>edit.Text=(memories.SelectedItem as MemoryRecord)?.Text??"";Grid.SetRow(edit,3);edit.FontSize=17;edit.Margin=new Thickness(0,8,0,8);grid.Children.Add(edit);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal};buttons.Children.Add(MainWindow.Button("纠正所选",()=>RunOperation(Correct),16));buttons.Children.Add(MainWindow.Button("遗忘所选",()=>RunOperation(Forget),16));buttons.Children.Add(MainWindow.Button("导出",()=>RunOperation(Export),16));buttons.Children.Add(MainWindow.Button("导入",()=>RunOperation(Import),16));buttons.Children.Add(MainWindow.Button("备份",()=>RunOperation(Backup),16));buttons.Children.Add(MainWindow.Button("恢复备份",()=>RunOperation(Restore),16));buttons.Children.Add(MainWindow.Button("新会话",()=>RunOperation(async()=>{await owner.NewSession();RefreshSessions();status.Text="已开始新会话，长期记忆保留。";}),16));
        buttons.Children.Add(MainWindow.Button("恢复出厂设置",async()=>{if(owner.IsMemoryTransitioning)return;try{if(!await owner.ConfirmFactoryResetAsync())return;await owner.FactoryResetAsync();}catch(Exception error){status.Text="未清除数据："+error.Message;}},16));
        Grid.SetRow(buttons,4);grid.Children.Add(buttons);host.Children.Add(grid);return host;
    }
    private UIElement Diagnostics()
    {
        var p=Section("运行状态与受控数据");Label(p,"显示模式（固定窗口 / 全屏，F11 切换）",displayMode);displayMode.Margin=new Thickness(0,0,0,16);p.Children.Add(diagnostics);var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,22,0,18)};
        row.Children.Add(MainWindow.Button("刷新诊断",UpdateDiagnostics,17));row.Children.Add(MainWindow.Button("重启语音组件",()=>RunOperation(async()=>{try{await owner.StopTurnAsync();await owner.Voice.StopAsync();await owner.Voice.EnsureStartedAsync(owner.LifetimeToken);if(owner.IsClosing)return;UpdateDiagnostics();status.Text="语音组件已重启。";}catch(Exception e){status.Text=e.Message;}}),17));
        row.Children.Add(MainWindow.Button("清理失效临时文件",()=>{try{status.Text=$"已清理 {owner.Paths.CleanTemporary(TimeSpan.FromHours(1))/1024} KiB；不涉及记忆、模型和原作素材。";}catch(Exception e){status.Text=e.Message;}},17));row.Children.Add(MainWindow.Button("重试记忆整理",()=>{owner.Memory.RetryFailedTasks();owner.Memory.RetryModelMemory();UpdateDiagnostics();},17));p.Children.Add(row);
        var desktop=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,16)};desktop.Children.Add(MainWindow.Button("嵌入桌面",()=>{owner.Desktop.Attach(owner);status.Text=owner.Desktop.State;UpdateDiagnostics();},17));desktop.Children.Add(MainWindow.Button("恢复普通窗口",()=>{owner.Desktop.Detach(owner);status.Text=owner.Desktop.State;UpdateDiagnostics();},17));p.Children.Add(desktop);
        p.Children.Add(new TextBlock{Text="桌面模式默认关闭，按 Esc 可恢复普通窗口；不改变系统壁纸。Explorer 不支持时明确保留普通窗口。\n数据：data/memory（SQLite、有限备份、导入导出）\n配置：data/config（密钥由 Windows DPAPI 当前账户加密）\n可清理：data/temp（受控且已失效的临时音频）\n缓存：data/cache · 日志：data/logs（1MB × 4）\n原作素材、训练参考、原始权重永不纳入自动清理。\n外部 API 的人格、翻译与识别效果需真实接入后评价。",TextWrapping=TextWrapping.Wrap,FontSize=17,Foreground=MainWindow.Blue});
        return new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    }
    public void Refresh()
    {
        refreshing=true;try{var s=owner.Settings;fields["api"].Text=s.ApiBaseUrl;fields["model"].Text=s.Model;fields["user"].Text=s.UserName;fields["temperature"].Text=s.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture);fields["timeout"].Text=s.TimeoutSeconds.ToString();fields["tokens"].Text=s.MaxReplyTokens.ToString();fields["asr"].Text=s.AsrBaseUrl;fields["asrModel"].Text=s.AsrModel;fields["python"].Text=s.PythonPath;structured.IsChecked=s.StructuredOutput;modelMemoryEnabled.IsChecked=s.ModelMemoryEnabled;fields["memoryTokens"].Text=s.MemorySummaryTokens.ToString();subtitle.SelectedIndex=s.SubtitleLanguage=="zh"?0:1;language.SelectedIndex=s.InputLanguage=="zh"?0:1;voiceVolume.Value=s.VoiceVolume;effectVolume.Value=s.EffectVolume;enabled.IsChecked=s.VoiceEnabled;bgmEnabled.IsChecked=s.BgmEnabled;bgmVolume.Value=s.BgmVolume;displayMode.SelectedIndex=s.DisplayMode=="fullscreen"?1:0;
        asrProvider.SelectedIndex=s.AsrProvider=="local"?0:1;vadEnabled.IsChecked=s.VadEnabled;wakeEnabled.IsChecked=s.WakeEnabled;wakeAutoSend.IsChecked=s.WakeAutoSend;try{devices.ItemsSource=Microphone.Devices();devices.SelectedIndex=s.MicrophoneDevice;}catch{devices.ItemsSource=new[]{"麦克风不可用"};}
        status.Text=(s.ApiBaseUrl.Length>0&&s.Model.Length>0?"对话模型已配置":"外部模型待配置")+" · 所有语音均为日语 · 设置保存在项目目录";RefreshMemory();RefreshSessions();UpdateDiagnostics();}finally{refreshing=false;}
    }
    private void ToggleBgm(){if(refreshing)return;try{owner.SetBgmEnabled(bgmEnabled.IsChecked==true);status.Text=owner.Settings.BgmEnabled?"背景音乐已开启。":"背景音乐已关闭。";}catch(Exception e){status.Text=e.Message;}}
    internal void SelectWork()=>tabs.SelectedIndex=4;
    internal void RefreshProfile()=>fields["user"].Text=owner.Settings.UserName;
    internal void RefreshDisplayMode(){refreshing=true;try{displayMode.SelectedIndex=owner.Display.Mode=="fullscreen"?1:0;}finally{refreshing=false;}}
    internal void SelectWorkPage(int index)=>workView?.SelectPage(index);
    internal int WorkPageCount=>workView?.PageCount??0;
    internal void SelectTab(int index)=>tabs.SelectedIndex=index;
    private async void RefreshSessions(){try{sessions.ItemsSource=await Task.Run(()=>owner.Memory.Sessions());sessions.SelectedItem=(sessions.ItemsSource as IReadOnlyList<MemorySession>)?.FirstOrDefault(s=>s.SessionId==owner.Settings.LastSessionId);}catch(Exception e){status.Text=e.Message;}}
    private async Task Save()
    {
        try
        {
            if(!int.TryParse(fields["tokens"].Text,System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out var replyTokens)||replyTokens<=0)throw new InvalidDataException("回复 token 上限必须为正整数。");
            if(!int.TryParse(fields["memoryTokens"].Text,System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out var memoryTokens)||memoryTokens<=0)throw new InvalidDataException("记忆整理 token 上限必须为正整数。");
            var old=JsonSerializer.Serialize(owner.Settings);var s=owner.Settings;s.BgmVolume=bgmVolume.Value;
            try{s.ApiBaseUrl=fields["api"].Text.Trim();s.Model=fields["model"].Text.Trim();s.UserName=fields["user"].Text.Trim();s.Temperature=double.Parse(fields["temperature"].Text,System.Globalization.CultureInfo.InvariantCulture);s.TimeoutSeconds=int.Parse(fields["timeout"].Text);s.MaxReplyTokens=replyTokens;s.MemorySummaryTokens=memoryTokens;s.ModelMemoryEnabled=modelMemoryEnabled.IsChecked==true;s.AsrBaseUrl=fields["asr"].Text.Trim();s.AsrModel=fields["asrModel"].Text.Trim();s.PythonPath=fields["python"].Text.Trim();s.StructuredOutput=structured.IsChecked==true;s.SubtitleLanguage=subtitle.SelectedIndex==0?"zh":"ja";s.InputLanguage=language.SelectedIndex==0?"zh":"ja";s.MicrophoneDevice=Math.Max(0,devices.SelectedIndex);s.VoiceVolume=voiceVolume.Value;s.EffectVolume=effectVolume.Value;s.VoiceEnabled=enabled.IsChecked==true;s.Validate();}
            catch{var restore=JsonSerializer.Deserialize<Settings>(old)!;foreach(var property in typeof(Settings).GetProperties())property.SetValue(s,property.GetValue(restore));throw;}
            s.AsrProvider=asrProvider.SelectedIndex==0?"local":"api";s.VadEnabled=vadEnabled.IsChecked==true;s.WakeEnabled=wakeEnabled.IsChecked==true;s.WakeAutoSend=wakeAutoSend.IsChecked==true;if(llmKey.Password.Length>0)owner.Store.SaveSecret("llm",llmKey.Password);if(asrKey.Password.Length>0)owner.Store.SaveSecret("asr",asrKey.Password);llmKey.Clear();asrKey.Clear();owner.ApplySettings();await owner.ApplyModelMemorySettingsAsync();if(owner.IsClosing)return;try{await owner.Capabilities.ConfigureProviderAsync(s.ApiBaseUrl,s.Model,owner.Store.ReadSecret("llm"));if(!owner.IsClosing)status.Text="设置已保存；密钥已加密。";}catch(Exception e){if(!owner.IsClosing)status.Text="设置已保存；Work 配置失败："+e.Message;}
        }catch(Exception e){status.Text="未保存："+e.Message;}
    }
    private async void RefreshMemory(){var version=++searchVersion;var memory=owner.Memory;var query=search.Text.Trim();try{var results=await Task.Run(()=>query.Length==0?memory.List():memory.Search(query,100));if(version==searchVersion&&ReferenceEquals(memory,owner.Memory))memories.ItemsSource=results;}catch(Exception e){if(version==searchVersion)status.Text=e.Message;}}
    private async Task Correct(){try{if(memories.SelectedItem is not MemoryRecord r)return;var text=edit.Text;await owner.StopTurnAsync();await Task.Run(()=>owner.Memory.Correct(r.Id,text));owner.ClearVisibleContext(r.Text,r.Key=="user.preferred-name");edit.Clear();search.Clear();RefreshMemory();status.Text="已纠正并同步净化历史、摘要与索引。";}catch(Exception e){status.Text=e.Message;}}
    private async Task Forget(){try{if(memories.SelectedItem is not MemoryRecord r)return;await owner.StopTurnAsync();var n=await Task.Run(()=>owner.Memory.Forget(r.Id));owner.ClearVisibleContext(r.Text,r.Key=="user.preferred-name");edit.Clear();search.Clear();RefreshMemory();status.Text=$"已遗忘 {n} 项及相关上下文，旧备份已失效。";}catch(Exception e){status.Text=e.Message;}}
    private async Task Export(){var memory=owner.Memory;try{var dir=Path.Combine(owner.Paths.Memory,"exports");Directory.CreateDirectory(dir);var file=Path.Combine(dir,$"memories-{DateTime.Now:yyyyMMdd-HHmmss}.json");await Task.Run(()=>memory.Export(file));status.Text="已导出："+Path.GetRelativePath(owner.Paths.Root,file);}catch(Exception e){status.Text=e.Message;}}
    private async Task Import(){try{var dialog=new OpenFileDialog{InitialDirectory=Path.Combine(owner.Paths.Memory,"exports"),Filter="ATRI memory JSON|*.json"};if(dialog.ShowDialog(owner)!=true)return;await owner.StopTurnAsync();var memory=owner.Memory;var n=await Task.Run(()=>memory.Import(dialog.FileName));RefreshMemory();status.Text=$"已校验导入 {n} 项；导入前已创建有限备份。";}catch(Exception e){status.Text=e.Message;}}
    private async Task Backup(){var memory=owner.Memory;try{var file=await Task.Run(memory.Backup);status.Text="已创建在线备份："+Path.GetRelativePath(owner.Paths.Root,file);}catch(Exception e){status.Text=e.Message;}}
    private async Task Restore(){try{var dialog=new OpenFileDialog{InitialDirectory=Path.Combine(owner.Paths.Memory,"backups"),Filter="SQLite backups|*.sqlite;*.db"};if(dialog.ShowDialog(owner)!=true)return;
        // Snapshot before our first await: RunOperation has not registered this
        // Restore task yet, so draining predecessors cannot wait on itself.
        var priorOperations=operations.ToArray();await owner.RestoreMemory(dialog.FileName,priorOperations);RefreshMemory();status.Text="备份已恢复；原数据库已保护。";}catch(Exception e){status.Text=e.Message;}}
    private async Task RefreshModels(){try{status.Text="正在扫描并校验权重…";var items=await owner.Voice.ModelsAsync(owner.LifetimeToken);if(owner.IsClosing)return;models.ItemsSource=items;models.SelectedItem=items.FirstOrDefault(x=>x.Id==owner.Settings.VoiceModel)??items.FirstOrDefault();status.Text=string.Join("；",items.Where(x=>!x.Valid).Select(x=>x.Name+": "+x.Error));if(status.Text.Length==0)status.Text=$"找到 {items.Count} 套合法 v2ProPlus 模型。";}catch(Exception e){status.Text=e.Message;}}
    private async Task LoadModel(){try{if(models.SelectedItem is not VoiceModel m)return;if(!m.Valid){status.Text=m.Error;return;}await owner.StopTurnAsync();status.Text="加载模型中…";await owner.Voice.LoadAsync(m.Id,owner.LifetimeToken);if(owner.IsClosing)return;owner.ApplySettings();status.Text="已加载 "+m.Name+"；人格与记忆不变。";}catch(Exception e){status.Text=e.Message;}}
    private async Task TestVoice(){try{status.Text="真实日语合成与播放自检…";await owner.TestLocalVoice();status.Text="实际合成、顺序播放与嘴型自检完成；未调用对话 API。";}catch(Exception e){status.Text=e.Message;}}
    private async void UpdateDiagnostics()
    {
        var memory=owner.Memory;
        try
        {
            var details=await Task.Run(()=>{var history=memory.HistoryStatus;return $"记忆：{memory.List(limit:10000).Count} 项 · 整理任务：{memory.Tasks().Count} 项\n语义检索：{memory.SemanticStatus}\n历史索引：字词 {history.LexicallyIndexedMessages}/{history.TotalMessages} 条 · 向量 {history.VectorIndexedMessages}/{history.TotalMessages} 条\n跨会话总结：{memory.ConsolidationStatus}\n{memory.ModelMemoryStatus}";});
            if(!ReferenceEquals(memory,owner.Memory))return;
            diagnostics.Text=$"语音进程：{owner.Voice.State}\n游戏音频：{owner.GameSounds.Status} · BGM：{(owner.Settings.BgmEnabled?"开":"关")}\n显示：{owner.Display.State}\nAPI：{(owner.Settings.ApiBaseUrl.Length>0?"已配置地址":"待配置")} · 对话密钥：{(owner.Store.HasSecret("llm")?"已加密保存":"未填写")}\n当前会话：{owner.Settings.LastSessionId}\n{details}\n项目路径：{owner.Paths.Root}";
        }
        catch(Exception e){status.Text=e.Message;}
    }
}
