using System.Text.Json;
using System.Text.Encodings.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using Atri.Core;

namespace Atri.Desktop;

/// <summary>Native, connected Work controls. The app owns the bridge lifetime.</summary>
internal sealed class CapabilityView : Grid
{
    private readonly CapabilityBridge bridge;
    private readonly TextBox workspace=new(){Text="data/workspaces/default",FontSize=15,Padding=new Thickness(5)};
    private readonly TextBox goal=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,FontSize=15,MaxLength=12000,Padding=new Thickness(6),VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly TextBox output=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FontSize=13,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    private readonly ListBox work=new(){FontSize=14,MinHeight=80,HorizontalContentAlignment=HorizontalAlignment.Stretch};
    private readonly TextBlock status=new(){Foreground=MainWindow.Blue,TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis,FontSize=13};
    private readonly ComboBox action=new(){ItemsSource=new[]{"inventory","git-status"},SelectedIndex=0,FontSize=15};
    private readonly TextBox vnText=new(){FontSize=14,MaxLength=2000};
    private readonly TextBox manifestText=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,FontSize=13,MaxLength=2000,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(6)};
    private readonly TextBox sceneOutput=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FontSize=20,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(16)};
    private readonly TextBox toolOutput=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FontSize=13,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(8)};
    private readonly TabControl pages=new(){BorderThickness=new Thickness(0),Background=Brushes.Transparent,Padding=new Thickness(0),HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch};
    private readonly ComboBox artifacts=new(){DisplayMemberPath="Title",FontSize=14};
    private readonly ComboBox scenes=new(){DisplayMemberPath="Title",FontSize=14};
    private readonly ComboBox provider=new(){ItemsSource=new[]{"Pi · 当前 Chat API","Codex · 可选登录"},SelectedIndex=0,FontSize=14};
    private readonly CheckBox allowWrite=new(){Content="允许写入",Foreground=MainWindow.Blue,VerticalAlignment=VerticalAlignment.Center,ToolTip="仅所选Work工作区；每次工具修改文件仍需单独确认"};
    private readonly TextBox browserUrl=new(){FontSize=14,MaxLength=1000,ToolTip="完整 HTTP(S) 网址；自动任务仅能访问此 origin，逐次确认 URL"};
    private readonly Image browserPreview=new(){Height=170,Stretch=Stretch.Uniform,Visibility=Visibility.Collapsed,Margin=new Thickness(0,0,0,8)};
    private readonly MainWindow owner;
    private string? projectId;
    private bool changing;
    private sealed record WorkRow(string Id,string Title);
    private sealed record ArtifactRow(string Id,string Title);
    private sealed record SceneRow(string Id,string Title);

    public CapabilityView(MainWindow owner,CapabilityBridge capabilityBridge)
    {
        bridge=capabilityBridge;this.owner=owner;
        RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});RowDefinitions.Add(new(){Height=new GridLength(25)});
        var tabStyle=(Style)XamlReader.Parse("""
<Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TabItem">
 <Setter Property="Width" Value="184"/><Setter Property="Height" Value="34"/><Setter Property="FontSize" Value="15"/><Setter Property="Foreground" Value="#156AB7"/><Setter Property="Background" Value="#E3F4FC"/>
 <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem"><Border x:Name="HeaderBorder" Background="{TemplateBinding Background}" BorderBrush="#95CDE7" BorderThickness="0,0,1,1" Padding="12,5"><ContentPresenter ContentSource="Header" HorizontalAlignment="Center" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="#156AB7"/><Setter Property="Foreground" Value="White"/></Trigger><Trigger SourceName="HeaderBorder" Property="IsMouseOver" Value="True"><Setter TargetName="HeaderBorder" Property="Opacity" Value="0.85"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
</Style>
""");
        pages.Items.Add(Page("任务与产物",TasksPage(),tabStyle));pages.Items.Add(Page("原作场景",ScenesPage(),tabStyle));pages.Items.Add(Page("网页与工具",ToolsPage(),tabStyle));Children.Add(pages);
        var footer=new Border{Background=new SolidColorBrush(Color.FromRgb(231,246,254)),Padding=new Thickness(7,3,7,2),Child=status};SetRow(footer,1);Children.Add(footer);
        ScrollViewer.SetHorizontalScrollBarVisibility(work,ScrollBarVisibility.Disabled);
        work.ItemTemplate=(DataTemplate)XamlReader.Parse("""<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><TextBlock Text="{Binding Title}" ToolTip="{Binding Title}" TextTrimming="CharacterEllipsis" Margin="3,5"/></DataTemplate>""");
        work.SelectionChanged+=async(_,_)=>{if(!changing&&work.SelectedItem is not null)await Guard(Detail);};
        bridge.EventReceived+=OnEvent;
        Unloaded+=(_,_)=>bridge.EventReceived-=OnEvent;
        Loaded+=async(_,_)=>{bridge.EventReceived-=OnEvent;bridge.EventReceived+=OnEvent;await Guard(async()=>{await EnsureProject();await Refresh();});};
    }

    internal int PageCount=>pages.Items.Count;
    internal void SelectPage(int index)=>pages.SelectedIndex=index;
    private static TabItem Page(string title,UIElement body,Style style)=>new(){Header=title,Style=style,Content=new Border{Margin=new Thickness(0,8,0,0),Child=body}};
    private static TextBlock Label(string text,double size=14)=>new(){Text=text,FontSize=size,Foreground=MainWindow.Blue,VerticalAlignment=VerticalAlignment.Center};
    private static Grid Columns(params double[] widths)
    {var grid=new Grid();foreach(var width in widths)grid.ColumnDefinitions.Add(new(){Width=width==0?new GridLength(1,GridUnitType.Star):new GridLength(width)});return grid;}
    private static void At(Grid grid,UIElement element,int row,int column=0)
    {SetRow(element,row);SetColumn(element,column);grid.Children.Add(element);}
    private UIElement TasksPage()
    {
        var root=new Grid();root.RowDefinitions.Add(new(){Height=new GridLength(38)});root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var directory=Columns(74,0,130);At(directory,Label("工作目录"),0);workspace.ToolTip="仅data/workspaces内的专用目录，原素材、模型、记忆与配置受保护";workspace.Margin=new Thickness(0,2,10,4);At(directory,workspace,0,1);
        At(directory,Btn("登记 / 刷新项目",async()=>{var p=await bridge.InvokeAsync("register_project",new{path=workspace.Text});projectId=p.GetProperty("project_id").GetString();await Refresh();}),0,2);At(root,directory,0);
        var main=Columns(368,18,0);At(root,main,1);
        var left=new Grid();foreach(var height in new[]{36d,68d,24d,0d,36d,36d,36d})left.RowDefinitions.Add(new(){Height=height==0?new GridLength(1,GridUnitType.Star):new GridLength(height)});At(main,left,0);
        var goalHeader=Columns(0,104);At(goalHeader,Label("任务目标 / 继续指令",16),0);At(goalHeader,Btn("建立任务",async()=>{await EnsureProject();await bridge.InvokeAsync("create_work",new{projectId,goal=goal.Text});await Refresh();}),0,1);At(left,goalHeader,0);goal.Margin=new Thickness(0,2,6,2);At(left,goal,1);At(left,Label("任务列表"),2);work.Margin=new Thickness(0,0,6,4);At(left,work,3);
        var providerRow=Columns(0,108);provider.Margin=new Thickness(0,3,12,3);At(providerRow,provider,0);At(providerRow,allowWrite,0,1);At(left,providerRow,4);
        var execute=Columns(0,0,0);At(execute,Btn("执行 Work",()=>Run(true,false)),0);At(execute,Btn("继续 / 重试",()=>Run(true,true)),0,1);At(execute,Btn("中止",async()=>{await bridge.InvokeAsync("cancel",new{workId=Selected()});await Detail();}),0,2);At(left,execute,5);
        var local=Columns(0,120);action.Margin=new Thickness(0,3,12,3);At(local,action,0);At(local,Btn("本地执行",()=>Run(false,false)),0,1);At(left,local,6);
        var right=new Grid();foreach(var height in new[]{28d,36d,36d,0d})right.RowDefinitions.Add(new(){Height=height==0?new GridLength(1,GridUnitType.Star):new GridLength(height)});At(main,right,0,2);
        At(right,Label("任务详情 · 执行进度 · 权限记录",16),0);var review=new WrapPanel();review.Children.Add(Btn("刷新进度",Detail));review.Children.Add(Btn("Git Diff",async()=>Display(await bridge.InvokeAsync("git_diff",new{workId=Selected()}))));review.Children.Add(Btn("验收任务",async()=>{await bridge.InvokeAsync("set_state",new{workId=Selected(),state="accepted"});await Refresh();}));At(right,review,1);
        var artifactRow=Columns(0,128);artifacts.Margin=new Thickness(0,3,10,3);At(artifactRow,artifacts,0);At(artifactRow,Btn("预览所选产物",PreviewArtifact),0,1);At(right,artifactRow,2);output.Padding=new Thickness(8);output.Margin=new Thickness(0,4,6,0);At(right,output,3);
        return root;
    }
    private UIElement ScenesPage()
    {
        var root=new Grid();foreach(var height in new[]{38d,0d,24d,36d})root.RowDefinitions.Add(new(){Height=height==0?new GridLength(1,GridUnitType.Star):new GridLength(height)});
        var navigation=Columns(112,0,76,76,76);At(navigation,Btn("加载早期场景",LoadScenes),0);scenes.Margin=new Thickness(4,3,14,3);At(navigation,scenes,0,1);At(navigation,Btn("上一句",()=>Scene(-1)),0,2);At(navigation,Btn("当前句",()=>Scene(0)),0,3);At(navigation,Btn("下一句",()=>Scene(1)),0,4);At(root,navigation,0);
        sceneOutput.Text="加载早期场景，按原作顺序逐句阅读。字幕使用当前语言设置。";At(root,sceneOutput,1);At(root,Label("也可输入完整原作日语台词，定位来源"),2);
        var match=Columns(0,164);vnText.Margin=new Thickness(0,3,10,3);At(match,vnText,0);At(match,Btn("匹配原作日语台词",async()=>Display(await bridge.InvokeAsync("vn_match",new{text=vnText.Text}),sceneOutput)),0,1);At(root,match,3);return root;
    }
    private async Task LoadScenes()
    {var rows=await bridge.InvokeAsync("vn_scenes");scenes.ItemsSource=rows.EnumerateArray().Select(x=>new SceneRow(x.GetProperty("id").GetString()!,x.GetProperty("chapter").GetString()+" · "+x.GetProperty("label").GetString()+" · "+x.GetProperty(owner.Settings.SubtitleLanguage=="ja"?"titleJa":"titleZh").GetString())).ToArray();scenes.SelectedIndex=0;await Scene(0);}
    private UIElement ToolsPage()
    {
        var root=new Grid();foreach(var height in new[]{24d,38d,0d})root.RowDefinitions.Add(new(){Height=height==0?new GridLength(1,GridUnitType.Star):new GridLength(height)});
        At(root,Label("网页截图归入任务页当前所选任务；此网址也可限定 Work 的浏览器访问范围",13),0);
        var address=Columns(0,156);browserUrl.Margin=new Thickness(0,3,10,3);At(address,browserUrl,0);At(address,Btn("读取网页与截图",ReadBrowser),0,1);At(root,address,1);
        var body=Columns(350,18,0);At(root,body,2);var left=new Grid();foreach(var height in new[]{26d,40d,28d,0d,36d})left.RowDefinitions.Add(new(){Height=height==0?new GridLength(1,GridUnitType.Star):new GridLength(height)});At(body,left,0);
        At(left,Label("Provider 与能力诊断",16),0);var diagnostics=new WrapPanel();diagnostics.Children.Add(Btn("能力诊断",async()=>Display(await bridge.InvokeAsync("catalog"),toolOutput)));diagnostics.Children.Add(Btn("Codex 登录状态",async()=>Display(await bridge.InvokeAsync("provider_probe"),toolOutput)));diagnostics.Children.Add(Btn("Pi 本地就绪检查",async()=>{await Configure();Display(await bridge.InvokeAsync("pi_probe"),toolOutput);}));At(left,diagnostics,1);
        At(left,Label("AUIP 声明 JSON",16),2);manifestText.ToolTip="仅校验声明，不启动或附加应用";manifestText.Margin=new Thickness(0,3,6,3);At(left,manifestText,3);At(left,Btn("校验 AUIP JSON 声明",async()=>{using var doc=JsonDocument.Parse(manifestText.Text);Display(await bridge.InvokeAsync("auip_validate",new{manifest=doc.RootElement.Clone()}),toolOutput);}),4);
        var results=new DockPanel();At(body,results,0,2);DockPanel.SetDock(browserPreview,Dock.Top);results.Children.Add(browserPreview);results.Children.Add(toolOutput);return root;
    }

    private Button Btn(string text,Func<Task> call)
    {var button=new Button{Content=text,FontSize=14,Padding=new Thickness(8,4,8,4),Margin=new Thickness(0,3,6,3),Foreground=MainWindow.Blue};button.Click+=async(_,_)=>await Guard(call);return button;}
    private async Task Guard(Func<Task> call)
    {if(owner.IsMemoryTransitioning||owner.IsClosing||!ReferenceEquals(bridge,owner.Capabilities))return;try{status.Text="处理中…";await call();status.Text="就绪 · 本地任务/权限/产物已持久化";}catch(Exception ex){status.Text=ex.Message;}}
    private string Selected()=>work.SelectedItem is WorkRow row?row.Id:throw new InvalidOperationException("先选择一个任务。");
    private async Task EnsureProject()
    {if(projectId is null){var p=await bridge.InvokeAsync("register_project",new{path=workspace.Text});projectId=p.GetProperty("project_id").GetString();}}
    private async Task Refresh()
    {var selected=(work.SelectedItem as WorkRow)?.Id;var rows=await bridge.InvokeAsync("list_work",new{projectId});changing=true;try{var list=rows.EnumerateArray().Select(x=>new WorkRow(x.GetProperty("work_item_id").GetString()!,x.GetProperty("title").GetString()+" · "+x.GetProperty("state").GetString())).ToArray();work.ItemsSource=list;work.SelectedItem=list.FirstOrDefault(x=>x.Id==selected)??list.FirstOrDefault();}finally{changing=false;}if(work.SelectedItem is not null)await Detail();}
    private async Task Detail()
    {var detail=await bridge.InvokeAsync("work_detail",new{workId=Selected()});var selected=(artifacts.SelectedItem as ArtifactRow)?.Id;var rows=detail.GetProperty("artifacts").EnumerateArray().Select(x=>new ArtifactRow(x.GetProperty("artifact_id").GetString()!,x.GetProperty("title").GetString()??"产物")).ToArray();artifacts.ItemsSource=rows;artifacts.SelectedItem=rows.FirstOrDefault(x=>x.Id==selected)??rows.FirstOrDefault();Display(detail);}
    private void Display(JsonElement data,TextBox? target=null)=>(target??output).Text=JsonSerializer.Serialize(data,new JsonSerializerOptions{WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
    private Task Configure()=>bridge.ConfigureProviderAsync(owner.Settings.ApiBaseUrl,owner.Settings.Model,owner.Store.ReadSecret("llm"));
    private async Task Run(bool agent,bool continued)
    {
        var id=Selected();
        var codex=provider.SelectedIndex==1;var writable=allowWrite.IsChecked==true;string browserOrigin="";
        if(browserUrl.Text.Trim().Length>0){if(!Uri.TryCreate(browserUrl.Text.Trim(),UriKind.Absolute,out var uri)||uri.Scheme is not ("https" or "http")||uri.UserInfo.Length>0)throw new InvalidOperationException("浏览器范围需要完整 HTTP(S) 地址。");browserOrigin=uri.GetLeftPart(UriPartial.Authority);}
        if(agent&&MessageBox.Show((codex?"Codex 使用已有登录，可写入所选专用 Work 目录。":"Pi 使用 SYSTEM 当前 Chat API，"+(writable?"可在确认工具写入后修改所选 Work 目录。":"仅能读取所选 Work 目录。"))+"任务内容和允许的文件会发送给所选模型，可能产生费用。"+(browserOrigin.Length>0?"浏览器范围："+browserOrigin+"，每次外部导航须确认完整 URL。":"")+"是否执行？","执行 Work",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        if(agent&&!codex)await Configure();
        await bridge.InvokeAsync(agent?(codex?"run_codex":"run_pi"):"run_local",new{workId=id,action=action.SelectedItem?.ToString(),instruction=goal.Text,approved=agent,allowWrite=writable,browserOrigin,@continue=continued});
        await Detail();
    }
    private async Task ReadBrowser()
    {
        var url=browserUrl.Text.Trim();if(url.Length==0)throw new InvalidOperationException("先填写完整网址。");
        if(MessageBox.Show("将在独立无登录状态的浏览器中读取并截图：\n"+url+"\n仅允许该 origin 的 GET/HEAD，不提交表单。是否读取？","受管浏览器",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        var result=await bridge.InvokeAsync("browser_read",new{workId=Selected(),url,approved=true});Display(result,toolOutput);
        if(result.TryGetProperty("screenshotPath",out var file)){
            var path=owner.Paths.WithinRoot(file.GetString()!);var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.UriSource=new Uri(path);image.EndInit();image.Freeze();browserPreview.Source=image;browserPreview.Visibility=Visibility.Visible;
        }
        await Detail();Display(result,toolOutput);
    }
    private async Task PreviewArtifact()
    {if(artifacts.SelectedItem is not ArtifactRow row)throw new InvalidOperationException("此任务还没有选定的真实产物。");Display(await bridge.InvokeAsync("artifact_preview",new{artifactId=row.Id}));}
    private async Task Scene(int delta)
    {if(scenes.SelectedItem is not SceneRow scene)throw new InvalidOperationException("先加载早期场景。");var line=await bridge.InvokeAsync("vn_scene",new{sceneId=scene.Id,delta,sessionId="work-view"});var language=owner.Settings.SubtitleLanguage=="ja"?"ja":"zh";sceneOutput.Text=$"{line.GetProperty("speaker").GetString()}  ·  {line.GetProperty("position").GetInt32()+1}/{line.GetProperty("count").GetInt32()}\n\n{line.GetProperty(language).GetString()}\n\n{line.GetProperty("source").GetString()}";}
    private void OnEvent(JsonElement item)
    {Dispatcher.BeginInvoke(async()=>{if(owner.IsMemoryTransitioning||owner.IsClosing||!ReferenceEquals(bridge,owner.Capabilities))return;status.Text=item.GetProperty("event").GetString();if(item.GetProperty("event").GetString()=="permission.requested"){
        if(owner.OwnsChatWorkPermission(item))return;
        var allowed=MessageBox.Show(item.GetProperty("reason").GetString(),"Work 工具单次权限",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
        await Guard(async()=>{await bridge.InvokeAsync("permission_response",new{requestId=item.GetProperty("requestId").GetString(),allow=allowed});});
    }if(item.TryGetProperty("text",out var text)&&text.GetString() is {Length:>0} value){output.AppendText("\n"+value);if(output.Text.Length>64000)output.Text=output.Text[^64000..];output.ScrollToEnd();}});}
}
