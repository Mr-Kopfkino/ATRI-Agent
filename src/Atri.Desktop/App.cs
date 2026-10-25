using System.IO;
using System.Windows;
using Atri.Core;
namespace Atri.Desktop;
public sealed class App:Application
{
    [STAThread]public static int Main(string[] args)
    {
        AppPaths? paths=null;
        try
        {
            paths=new AppPaths(AppPaths.Discover());using var mutex=new Mutex(true,"Local\\ATRI_Agent_"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(paths.Root.ToUpperInvariant())))[..16],out var first);
            if(!first){MessageBox.Show("ATRI 已在运行。请使用现有窗口。");return 0;}
            var app=new App();app.DispatcherUnhandledException+=(_,e)=>{paths.Log("ui",e.Exception.GetType().Name);MessageBox.Show("操作失败："+e.Exception.Message,"ATRI");e.Handled=true;};
            var window=new MainWindow(paths);if(args.Contains("--capture"))window.EnableCapture();if(args.Contains("--audio-smoke"))window.EnableAudioSmoke();if(args.Contains("--ui-smoke"))window.EnableUiSmoke();app.Run(window);return 0;
        }
        catch(Exception e){paths?.Log("startup",e.GetType().Name);MessageBox.Show("启动失败："+e.Message,"ATRI");return 1;}
    }
}
