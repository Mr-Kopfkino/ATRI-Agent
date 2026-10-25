using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Atri.Core;

/// <summary>The owning capability host received and explicitly rejected an operation.</summary>
public sealed class CapabilityOperationException(string? message) : InvalidOperationException(message);

/// <summary>Private managed stdio transport to the reused Amadeus Work core.</summary>
public sealed class CapabilityBridge(AppPaths paths, Settings settings) : IAsyncDisposable
{
    private readonly SemaphoreSlim startGate=new(1,1),writeGate=new(1,1);
    private readonly object disposalGate=new();
    private readonly List<Task> retirements=[];
    private Owner? owner;
    private int closing;
    private Task? disposal;
    private sealed class Owner(Process process,KillJob job)
    {
        public readonly Process Process=process;
        public readonly KillJob Job=job;
        public readonly CancellationTokenSource Stopped=new();
        public readonly ConcurrentDictionary<string,TaskCompletionSource<JsonElement>> Pending=new();
        public Task Reader=Task.CompletedTask,Errors=Task.CompletedTask;
        public volatile bool Closed;
        public bool Running
        {
            get{try{return !Closed&&!Process.HasExited;}catch(InvalidOperationException){return false;}}
        }
    }
    public event Action<JsonElement>? EventReceived;
    public bool Running=>Volatile.Read(ref closing)==0&&Volatile.Read(ref owner) is {Running:true};

    private async Task<Owner> StartAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref closing)!=0,this);
        await startGate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref closing)!=0,this);
            if(owner is {Running:true} current)return current;
            retirements.RemoveAll(task=>task.IsCompletedSuccessfully);
            if(owner is {} retired){owner=null;retirements.Add(CleanupAsync(retired));}
            var bundled=paths.WithinRoot("runtime/voice/python/python.exe");
            if(!File.Exists(bundled))bundled=paths.WithinRoot("GPT-SoVITS-v2pro-20261006/runtime/python.exe");
            var python=!string.IsNullOrWhiteSpace(settings.PythonPath)?paths.WithinRoot(settings.PythonPath):bundled;
            if(!File.Exists(python))throw new FileNotFoundException("能力组件需要可用的 Python（可在设置中指定）。",python);
            var script=paths.WithinRoot("runtime/capabilities/bridge.py");
            if(!File.Exists(script))throw new FileNotFoundException("能力桥文件缺失。",script);
            var info=new ProcessStartInfo(python){WorkingDirectory=paths.Root,UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            info.ArgumentList.Add("-u");info.ArgumentList.Add(script);paths.Apply(info);
            var process=Process.Start(info)??throw new IOException("能力桥启动失败。");
            KillJob job;
            try{job=new KillJob(process);}catch{process.Kill(entireProcessTree:true);process.Dispose();throw;}
            var started=new Owner(process,job);owner=started;
            started.Reader=ReadAsync(started);started.Errors=DrainErrorsAsync(started);
            return started;
        }
        finally{startGate.Release();}
    }

    public async Task<JsonElement> InvokeAsync(string operation,object? arguments=null,CancellationToken token=default)
    {
        var target=await StartAsync(token);
        return await SendAsync(target,operation,arguments,token);
    }

    private async Task<JsonElement> SendAsync(Owner target,string operation,object? arguments,CancellationToken token,bool shutdown=false)
    {
        var id=Guid.NewGuid().ToString("N");
        var completion=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request=JsonSerializer.Serialize(new{id,op=operation,args=arguments??new{}});
        if(Encoding.UTF8.GetByteCount(request)>256*1024)throw new InvalidDataException("能力请求超过256 KiB。");
        target.Pending[id]=completion;
        try
        {
            await writeGate.WaitAsync(token);
            try
            {
                if(!shutdown)ObjectDisposedException.ThrowIf(Volatile.Read(ref closing)!=0,this);
                if(!target.Running)throw new IOException("当前能力进程已退出；请求未转交给其他进程。");
                await target.Process.StandardInput.WriteLineAsync(request.AsMemory(),token);
                await target.Process.StandardInput.FlushAsync(token);
            }
            finally{writeGate.Release();}
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(shutdown?3:45),token);
        }
        finally{target.Pending.TryRemove(id,out _);}
    }

    /// <summary>Private stdin only. Credentials remain in this provider's memory.</summary>
    public Task<JsonElement> ConfigureProviderAsync(string baseUrl,string model,string key,CancellationToken token=default)
        =>InvokeAsync("configure_provider",new{baseUrl,model,key},token);

    private async Task ReadAsync(Owner target)
    {
        Exception failure=new IOException("能力组件已退出；进行中的任务下次启动会标记为需恢复。");
        try
        {
            while(!target.Stopped.IsCancellationRequested)
            {
                var line=await target.Process.StandardOutput.ReadLineAsync(target.Stopped.Token);
                if(line is null)break;
                if(line.Length>2*1024*1024)throw new InvalidDataException("能力响应超过限制。");
                using var doc=JsonDocument.Parse(line);var message=doc.RootElement;
                if(message.TryGetProperty("id",out var id)&&target.Pending.TryGetValue(id.GetString()??"",out var completion))
                {
                    if(message.GetProperty("ok").GetBoolean())completion.TrySetResult(message.GetProperty("result").Clone());
                    else completion.TrySetException(new CapabilityOperationException(message.GetProperty("error").GetString()));
                }
                else if(message.TryGetProperty("event",out _)&&ReferenceEquals(Volatile.Read(ref owner),target)&&Volatile.Read(ref closing)==0)
                {
                    try{EventReceived?.Invoke(message.Clone());}catch{ /* UI subscribers own their failures. */ }
                }
            }
        }
        catch(Exception ex) when(ex is IOException or JsonException or OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        {failure=new IOException("能力桥连接中断。",ex);}
        finally
        {
            target.Closed=true;
            // An old reader has authority only over requests sent to its process.
            foreach(var completion in target.Pending.Values)completion.TrySetException(failure);
        }
    }

    private static async Task DrainErrorsAsync(Owner target)
    {
        try{var buffer=new char[2048];while(await target.Process.StandardError.ReadAsync(buffer.AsMemory(),target.Stopped.Token)>0){} }
        catch(Exception ex) when(ex is IOException or OperationCanceledException or InvalidOperationException){}
        // Native diagnostics can contain credentials. Never forward them to logs.
    }

    private static async Task CleanupAsync(Owner target)
    {
        target.Closed=true;
        try
        {
            try{if(!target.Process.HasExited)target.Process.Kill(entireProcessTree:true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
            target.Job.Dispose(); // Kill descendants before awaiting pipe readers.
            target.Stopped.Cancel();
            try{await target.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch(InvalidOperationException){}catch(TimeoutException){}
            try{await Task.WhenAll(target.Reader,target.Errors).WaitAsync(TimeSpan.FromSeconds(5));}catch(TimeoutException){}
        }
        finally
        {
            foreach(var completion in target.Pending.Values)completion.TrySetException(new IOException("该能力进程已停止。"));
            // Process.Dispose does not close streams that were already obtained.
            // This target owns only its retired generation and drained readers.
            try{target.Process.StandardInput.Dispose();}catch(IOException){}catch(InvalidOperationException){}
            try{target.Process.StandardOutput.Dispose();}catch(IOException){}catch(InvalidOperationException){}
            try{target.Process.StandardError.Dispose();}catch(IOException){}catch(InvalidOperationException){}
            target.Process.Dispose();target.Stopped.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock(disposalGate)
        {
            if(disposal is null){Interlocked.Exchange(ref closing,1);disposal=DisposeCoreAsync();}
            return new ValueTask(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Owner? target;Task[] old;
        await startGate.WaitAsync();
        try{target=owner;owner=null;old=retirements.ToArray();}
        finally{startGate.Release();}
        if(target is not null)
        {
            // Send to the existing owner: disposal cannot launch a replacement.
            if(target.Running)try{using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));await SendAsync(target,"shutdown",null,timeout.Token,shutdown:true);target.Process.StandardInput.Close();await target.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch{}
            await CleanupAsync(target);
        }
        await Task.WhenAll(old);
        // Gates stay valid for callers already waiting; they fail the closing
        // check after acquiring them. Their WaitHandles are never materialized.
    }
}
