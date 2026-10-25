using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Atri.Desktop;

public sealed partial class MainWindow
{
    private Grid? confirmationOverlay;
    private Button? confirmationNo,confirmationYes;
    private TaskCompletionSource<bool>? confirmationCompletion;
    private bool confirmationMouseMoved;
    private const string DialogArt=Art+"UI_菜单图层/data/uipsd/dialog/";

    internal Task<bool> ConfirmFactoryResetAsync()=>ShowGameConfirmationAsync("factory-reset",
        "是否将亚托莉恢复到初始状态？",
        "将清除：全部聊天、用户称呼、事实与偏好、关系进展、摘要与索引、旧会话、内部备份与受管导出、交互音频与日志，以及 Work 历史、场景进度和未修改的受管报告／截图。\n\n"+
        "将保留：完整初始人格、自我认知、初始记忆和原作知识、声音模型与素材、API 地址与密钥、设备及界面偏好、用户创作、外部另存和独立验证实例。恢复后开始新会话，仍可对话、学习和保存新记忆。\n\n"+
        "成功清除的数据不能恢复；执行阶段不能中途取消。故障将回退，或继续已提交的清理。",
        "当前实例："+Paths.Root,"factory-reset-confirm");

    private async Task<bool> ShowGameConfirmationAsync(string kind,string heading,string description,string target,string cue)
    {
        if(confirmationOverlay!=null||closing||memoryTransitioning)return false;
        // Decide before focusing NO: focus must not authorize a background warp.
        bool mayMoveMouse=IsVisible&&(IsActive||IsMouseOver);
        var enabled=(title.IsEnabled,chat.IsEnabled,system.IsEnabled);
        var previousFocus=Keyboard.FocusedElement;
        var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        confirmationCompletion=completion;confirmationMouseMoved=false;
        var overlay=new Grid{Name="GameConfirmationOverlay",Tag=kind,Background=Brushes.Transparent};
        confirmationOverlay=overlay;
        overlay.Children.Add(new Border{Name="ConfirmationShade",Background=new SolidColorBrush(Color.FromArgb(100,0,0,0))});
        var canvas=new Grid{Width=1280,Height=720};
        KeyboardNavigation.SetTabNavigation(canvas,KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(canvas,KeyboardNavigationMode.Cycle);
        bool reset=kind=="factory-reset";
        Place(canvas,Image(DialogArt+"414.png",Stretch.Fill),65,reset?145:255,1149,reset?430:210);
        TextBlock Label(string text,double font)=>new(){Text=text,FontSize=font,Foreground=Brushes.White,
            TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,
            Effect=new DropShadowEffect{Color=Color.FromRgb(14,103,158),BlurRadius=6,ShadowDepth=1.5,Opacity=.95}};
        Place(canvas,Label(heading,27),240,reset?172:284,800,42);
        var details=Label(description,reset?16.5:18);details.TextAlignment=reset?TextAlignment.Left:TextAlignment.Center;details.LineHeight=22;
        Place(canvas,new ScrollViewer{Content=details,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Focusable=false},240,reset?226:328,800,reset?218:28);
        if(reset)Place(canvas,Label(target,14),240,450,800,43);
        confirmationYes=ConfirmationButton("YES","437","441","445",()=>CompleteGameConfirmation(true));
        confirmationNo=ConfirmationButton("NO","438","442","446",()=>CompleteGameConfirmation(false));
        confirmationNo.IsDefault=true;confirmationNo.IsCancel=true;
        AutomationProperties.SetName(confirmationYes,reset?"YES，确认恢复出厂设置":"YES，退出 ATRI Agent");
        AutomationProperties.SetName(confirmationNo,"NO，取消并继续使用");
        confirmationNo.ToolTip="取消并继续使用（Esc）";
        confirmationYes.ToolTip=reset?"确认清除当前实例的交互数据":"确认退出 ATRI Agent";
        Place(canvas,confirmationYes,514,reset?509:371,92,42);
        Place(canvas,confirmationNo,694,reset?509:371,75,42);
        overlay.Children.Add(new Viewbox{Stretch=Stretch.Uniform,Child=canvas});
        title.IsEnabled=chat.IsEnabled=system.IsEnabled=false;
        stage.Children.Add(overlay);
        try
        {
            await Dispatcher.InvokeAsync(()=>
            {
                if(!ReferenceEquals(confirmationOverlay,overlay)||closing)return;
                overlay.UpdateLayout();
                if(mayMoveMouse){confirmationNo.Focus();MoveCursorToConfirmationNo();}
                GameSounds.Announce(cue);
            },DispatcherPriority.Loaded);
            var accepted=await completion.Task;
            // Let the original click SE finish before shutdown/reset closes audio.
            if(accepted&&!closing)await Task.Delay(280);
            return accepted&&!closing;
        }
        finally
        {
            if(ReferenceEquals(confirmationOverlay,overlay))
            {
                GameSounds.DismissPrompt();stage.Children.Remove(overlay);
                confirmationOverlay=null;confirmationNo=confirmationYes=null;confirmationCompletion=null;
                if(!closing)
                {
                    (title.IsEnabled,chat.IsEnabled,system.IsEnabled)=enabled;
                    if(previousFocus is UIElement element&&element.IsVisible&&element.IsEnabled)element.Focus();
                }
            }
        }
    }

    private Button ConfirmationButton(string text,string normal,string hover,string pressed,Action action)
    {
        var button=new Button{Content=text,Tag="ui-confirm-owned",Background=Brushes.Transparent,BorderThickness=new Thickness(0),
            Padding=new Thickness(0),Cursor=Cursors.Hand,FocusVisualStyle=null};
        var template=new ControlTemplate(typeof(Button));
        var image=new FrameworkElementFactory(typeof(System.Windows.Controls.Image)){Name="Artwork"};
        image.SetValue(System.Windows.Controls.Image.SourceProperty,Bitmap(DialogArt+normal+".png"));
        image.SetValue(System.Windows.Controls.Image.StretchProperty,Stretch.Uniform);
        template.VisualTree=image;
        void State(DependencyProperty property,string asset)
        {
            var trigger=new Trigger{Property=property,Value=true};
            trigger.Setters.Add(new Setter(System.Windows.Controls.Image.SourceProperty,Bitmap(DialogArt+asset+".png"),"Artwork"));template.Triggers.Add(trigger);
        }
        State(IsKeyboardFocusedProperty,hover);State(IsMouseOverProperty,hover);State(System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty,pressed);
        button.Template=template;button.Click+=(_,_)=>action();return button;
    }

    private void CompleteGameConfirmation(bool accepted)
    {
        if(confirmationCompletion is not {} completion||completion.Task.IsCompleted)return;
        GameSounds.DismissPrompt();
        if(accepted)GameSounds.Select();else GameSounds.Back();
        confirmationYes!.IsEnabled=confirmationNo!.IsEnabled=false;completion.TrySetResult(accepted);
    }

    private void MoveCursorToConfirmationNo()
    {
        if(confirmationNo is not {IsVisible:true} no||!IsVisible||(!IsActive&&!IsMouseOver))return;
        var point=no.PointToScreen(new Point(no.ActualWidth/2,no.ActualHeight/2));
        confirmationMouseMoved=ConfirmationNative.SetCursorPos((int)Math.Round(point.X),(int)Math.Round(point.Y));
    }
    private static class ConfirmationNative
    {
        [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetCursorPos(int x,int y);
    }
}
