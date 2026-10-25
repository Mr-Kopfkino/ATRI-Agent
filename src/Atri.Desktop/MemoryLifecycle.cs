using System.Text.Json;
using System.Windows.Threading;
using Atri.Core;

namespace Atri.Desktop;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer modelMemoryTimer=new(){Interval=TimeSpan.FromSeconds(30)};
    private CancellationTokenSource modelMemoryCancellation=new();
    private Task modelMemoryTask=Task.CompletedTask;
    private bool modelMemoryPaused;
    private readonly SemaphoreSlim modelMemoryPauseGate=new(1,1);

    private void StartModelMemoryMaintenance()
    {
        modelMemoryTimer.Tick+=(_,_)=>StartModelMemoryWork();
        modelMemoryTimer.Start();
        // Also recover a process that was killed before it could queue its last
        // session. Imported/other historical sessions are not silently uploaded.
        if(Settings.ModelMemoryEnabled)Memory.QueueModelMemory(Settings.LastSessionId);
        StartModelMemoryWork();
    }

    private void StartModelMemoryWork()
    {
        if(closing||memoryTransitioning||modelMemoryPaused||capture||uiSmoke||audioSmoke||!Settings.ModelMemoryEnabled||!modelMemoryTask.IsCompleted||Settings.ApiBaseUrl.Length==0||Settings.Model.Length==0)return;
        var memory=Memory;var settings=JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(Settings))!;
        string key;
        try{key=Store.ReadSecret("llm");}catch(Exception error){Paths.Log("model-memory",error.GetType().Name);return;}
        var cancellation=modelMemoryCancellation.Token;
        modelMemoryTask=Task.Run(async()=>
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);
            try{await memory.ProcessModelMemoryAsync(new(api),settings,key,linked.Token);}
            catch(OperationCanceledException) when(linked.IsCancellationRequested){}
            catch(Exception error){Paths.Log("model-memory",error.GetType().Name);}
            finally{key="";}
        });
    }

    private async Task QueueFinishedSessionAsync()
    {
        if(modelMemoryPaused||!Settings.ModelMemoryEnabled||capture||uiSmoke||audioSmoke)return;
        var memory=Memory;var session=Settings.LastSessionId;
        await Task.Run(()=>memory.QueueModelMemory(session));
        StartModelMemoryWork();
    }

    internal async Task ConsolidateCurrentMemoryAsync()
    {
        if(memoryTransitioning||closing)throw new InvalidOperationException("正在恢复记忆或退出，请稍后操作。");
        await StopTurnAsync();
        await QueueFinishedSessionAsync();
        await modelMemoryTask;
    }

    internal async Task ApplyModelMemorySettingsAsync()
    {
        if(memoryTransitioning)return;
        await PauseModelMemoryAsync();
        modelMemoryPaused=false;
        StartModelMemoryWork();
    }

    private async Task PauseModelMemoryAsync()
    {
        await modelMemoryPauseGate.WaitAsync();
        try
        {
            modelMemoryPaused=true;
            modelMemoryCancellation.Cancel();
            await modelMemoryTask;
            modelMemoryCancellation.Dispose();
            modelMemoryCancellation=new();
        }
        finally{modelMemoryPauseGate.Release();}
    }

    private async Task FinishModelMemoryOnExitAsync()
    {
        modelMemoryTimer.Stop();
        await PauseModelMemoryAsync();
        try
        {
            if(FactoryReset.HasPending(Paths)||!Settings.ModelMemoryEnabled||capture||uiSmoke||audioSmoke||Settings.ApiBaseUrl.Length==0||Settings.Model.Length==0)return;
            Memory.QueueModelMemory(Settings.LastSessionId);
            var settings=JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(Settings))!;
            var key=Store.ReadSecret("llm");
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
            SetState("正在整理本次记忆…未完成的任务会在下次启动继续。");
            try{await Task.Run(()=>Memory.ProcessModelMemoryAsync(new(api),settings,key,deadline.Token,maximumBatches:int.MaxValue));}
            catch(OperationCanceledException) when(deadline.IsCancellationRequested){}
            finally{key="";}
        }
        finally{modelMemoryCancellation.Dispose();}
    }
}
