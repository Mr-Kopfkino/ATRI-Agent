using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Atri.Core;

namespace Atri.Desktop;

public sealed partial class MainWindow
{
    private Border? resetOverlay;
    private TextBlock? resetProgress;
    private Button? resetRetry,resetExit;
    private bool factoryResetRunning;
    private bool resetStateReady=true,resetWasCommitted;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if(confirmationOverlay!=null)
        {
            if(e.Key==Key.Escape){e.Handled=true;CompleteGameConfirmation(false);return;}
            if(e.Key==Key.Enter){e.Handled=true;CompleteGameConfirmation(ReferenceEquals(Keyboard.FocusedElement,confirmationYes));return;}
            if(e.Key==Key.F11){e.Handled=true;return;}
        }
        if(memoryTransitioning&&e.Key is Key.F11 or Key.Escape){e.Handled=true;return;}
        base.OnPreviewKeyDown(e);
    }

    internal async Task<bool> FactoryResetAsync()
    {
        if(memoryTransitioning||closing||factoryResetRunning)return false;
        // This check is read-only and runs before any lifecycle or data change.
        FactoryReset.ValidateBaseline(Paths);
        memoryTransitioning=true;factoryResetRunning=true;
        await memoryTransitionGate.WaitAsync();
        var memoryClosed=false;var capabilitiesClosed=false;var succeeded=false;resetWasCommitted=false;
        BeginResetOverlay();
        try
        {
            SetResetProgress("正在停止对话、录音与后台整理…");
            await StopTurnAsync();
            await PauseModelMemoryAsync();
            // A console operation can itself be awaiting modelMemoryTask.
            // Cancel that owner before draining its awaiting UI operation.
            if(console is {} view)await view.DrainOperationsAsync();
            if(await FactoryReset.AwaitStartupForResetAsync(Paths,Memory,startupTask,lifetime.Token))startupTask=Task.CompletedTask;
            Voice.VerifyKnownRuntimeFiles();
            if(Settings.VoiceEnabled)
            {
                SetResetProgress("正在核实已选声音模型…");
                var models=await Voice.ModelsAsync(lifetime.Token);
                if(!models.Any(model=>model.Id==Settings.VoiceModel&&model.Valid))
                    throw new InvalidDataException("当前声音模型不可用；未清除数据。请在 SOUND 修复模型后重试。");
            }
            GameSounds.StopPlayback();
            await Task.WhenAll(Voice.StopAsync(),LocalAsr.StopAsync());
            capabilitiesClosed=true;await Capabilities.DisposeAsync();
            var workFiles=new List<FactoryResetFile>();
            if(File.Exists(Paths.WithinRoot("data/capabilities/work.sqlite")))
            {
                SetResetProgress("正在核对 Work 受管文件归属…");
                // A new, temporary owner sees the final ledger after all old
                // writers have exited. It never executes or resumes Work.
                await using var planner=new CapabilityBridge(Paths,Settings);
                var plan=await planner.InvokeAsync("factory_reset_plan",token:lifetime.Token);
                foreach(var file in plan.GetProperty("files").EnumerateArray())
                    workFiles.Add(new(file.GetProperty("path").GetString()!,file.GetProperty("sha256").GetString()!,file.GetProperty("size").GetInt64()));
            }
            memoryClosed=true;resetStateReady=false;
            await Task.Run(Memory.Dispose);
            var clean=await Task.Run(()=>FactoryReset.Execute(Paths,Settings,workFiles,
                phase=>Dispatcher.Invoke(()=>SetResetProgress(PhaseText(phase)))));
            resetWasCommitted=true;
            CopyResetSettings(clean);
            await ReloadResetStateAsync(true);
            succeeded=true;
            EndResetOverlay("恢复出厂设置完成。初始人格与原作记忆已装载，可以开始新的对话。");
        }
        catch(Exception error)
        {
            if(FactoryReset.HasPending(Paths))
                ShowResetFailure(error);
            else
            {
                try
                {
                    if(memoryClosed)await ReloadResetStateAsync(resetWasCommitted);
                    else if(capabilitiesClosed){Capabilities=new(Paths,Settings);RebuildResetConsole();}
                    EndResetOverlay(resetWasCommitted?"清理已提交，但重新加载发生错误；已恢复可用的新状态。"+error.Message:"未完成恢复出厂设置；原数据已保留。"+error.Message);
                }
                catch(Exception reloadError){resetStateReady=false;ShowResetFailure(reloadError);}
            }
        }
        finally
        {
            factoryResetRunning=false;memoryTransitionGate.Release();
            ResumeAfterResetIfConsistent();
        }
        return succeeded;
    }

    private async Task RetryFactoryResetAsync()
    {
        if(factoryResetRunning||closing)return;
        factoryResetRunning=true;await memoryTransitionGate.WaitAsync();
        if(resetRetry!=null)resetRetry.IsEnabled=false;
        try
        {
            SetResetProgress("正在恢复一致状态并完成受管清理…");
            var committed=await Task.Run(()=>FactoryReset.RecoverPending(Paths))||resetWasCommitted;
            resetWasCommitted=committed;
            await ReloadResetStateAsync(committed);
            EndResetOverlay(committed?"恢复出厂设置完成。初始人格与原作记忆已装载。":"已恢复重置前的数据；此次未重置，可重新确认后再试。");
        }
        catch(Exception error){ShowResetFailure(error);}
        finally{factoryResetRunning=false;memoryTransitionGate.Release();ResumeAfterResetIfConsistent();}
    }

    private async Task ReloadResetStateAsync(bool committed)
    {
        CopyResetSettings(Store.Load());
        var fresh=new MemoryStore(Paths.Memory);var freshContext=new ContextBuilder(Paths,fresh);
        var freshCapabilities=new CapabilityBridge(Paths,Settings);
        try
        {
            if(committed)await Task.Run(()=>freshContext.RegisterKnowledge());
            if(Settings.VoiceEnabled)
                SetResetProgress("正在恢复已选声音模型并核验语音组件…");
            await Voice.RestoreAfterResetAsync(lifetime.Token);
            Memory=fresh;context=freshContext;Capabilities=freshCapabilities;
            chatWorkNeedsRecovery=false;workProgress.Clear();workProgressPanel.Visibility=Visibility.Collapsed;RefreshChatWorkInput();
            ClearVisibleContext();wakeArmed=false;wakeUntil=default;composing=false;SetEmotion("neutral");
            RebuildResetConsole();resetStateReady=true;
        }
        catch{resetStateReady=false;await Voice.StopAsync();await freshCapabilities.DisposeAsync();fresh.Dispose();throw;}
    }

    private void RebuildResetConsole()
    {
        if(console!=null){system.Children.Remove(console);console=null;}
        if(closing)return;
        previousPage="chat";OpenSystem("chat");console?.SelectTab(2);
    }

    private void CopyResetSettings(Settings clean)
    {foreach(var property in typeof(Settings).GetProperties())property.SetValue(Settings,property.GetValue(clean));}

    private void ResumeAfterResetIfConsistent()
    {
        if(FactoryReset.HasPending(Paths)||!resetStateReady)return;
        memoryTransitioning=false;modelMemoryPaused=false;
        if(!closing){title.IsEnabled=chat.IsEnabled=system.IsEnabled=true;RefreshChatWorkInput();ConfigureGameAudio();StartModelMemoryWork();}
    }

    private void BeginResetOverlay()
    {
        title.IsEnabled=chat.IsEnabled=system.IsEnabled=false;
        var panel=new StackPanel{MaxWidth=760,Margin=new Thickness(32)};
        panel.Children.Add(new TextBlock{Text="恢复出厂设置",FontSize=30,Foreground=Blue,Margin=new Thickness(0,0,0,18)});
        resetProgress=new TextBlock{FontSize=18,Foreground=Blue,TextWrapping=TextWrapping.Wrap,LineHeight=28};panel.Children.Add(resetProgress);
        resetRetry=Button("继续恢复",async()=>await RetryFactoryResetAsync(),18);resetRetry.Visibility=Visibility.Collapsed;resetRetry.Margin=new Thickness(0,20,0,0);panel.Children.Add(resetRetry);
        resetExit=Button("退出，重启后继续恢复",Close,18);resetExit.Visibility=Visibility.Collapsed;resetExit.Margin=new Thickness(0,12,0,0);panel.Children.Add(resetExit);
        resetOverlay=new Border{Background=new SolidColorBrush(Color.FromArgb(245,239,249,255)),Child=panel,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,BorderBrush=Aqua,BorderThickness=new Thickness(2)};
        stage.Children.Add(resetOverlay);
    }

    private void SetResetProgress(string text){if(resetProgress!=null)resetProgress.Text=text;SetState(text);}
    private void ShowResetFailure(Exception error)
    {
        SetResetProgress("恢复尚未完成，已阻止旧状态继续使用。\n"+error.Message+"\n请解除文件占用或检查磁盘权限后选择继续恢复，也可退出后重启；不会重新装载已提交清除的旧记忆。");
        if(resetRetry!=null){resetRetry.Visibility=Visibility.Visible;resetRetry.IsEnabled=true;}
        if(resetExit!=null)resetExit.Visibility=Visibility.Visible;
    }
    private void EndResetOverlay(string message)
    {if(resetOverlay!=null)stage.Children.Remove(resetOverlay);resetOverlay=null;SetState(message);console?.ReportResetStatus(message);}
    private static string PhaseText(string phase)=>phase switch{
        "prepared"=>"已验证完整初始状态，正在检查切换条件…",
        "switching"=>"正在切换初始记忆与会话配置…",
        "installed"=>"正在核实新状态与原作初始记忆…",
        "committed"=>"新状态已提交，正在清除旧数据副本…",
        "cleanup"=>"正在完成受管残留清理…",
        _=>"正在准备并验证初始状态…"};
}
