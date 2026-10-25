using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Atri.Core;
public sealed record VoiceModel(string Id,string Name,string Version,bool Valid,string? Error,bool Loaded);
public sealed record VoiceAudio(string Path,string Text,string Emotion,double Duration);
public sealed class VoiceProcess(AppPaths paths,Settings settings):IAsyncDisposable
{
    private Process? process;private KillJob? job;private Task? reader,errors;private readonly SemaphoreSlim lifecycle=new(1,1),writer=new(1,1),synthesis=new(1,1);
    private sealed record PendingRequest(Process Owner,TaskCompletionSource<JsonElement> Completion);
    private readonly ConcurrentDictionary<string,PendingRequest> pending=new();private long sequence;private bool disposed;
    // Completed audio remains playable across worker restarts until its caller
    // explicitly releases it. Record only requests submitted by this instance.
    private sealed record AudioOwner(string RequestId,Process Process,int Pid){public int Releasing;public int ReleaseRequested;}
    private readonly ConcurrentDictionary<string,AudioOwner> audioOwners=new(StringComparer.OrdinalIgnoreCase);
    private readonly object identityGate=new();private VoiceRuntimeIdentity? runtimeIdentity;
    public string State {get;private set;}="未启动";
    private string Python()=>!string.IsNullOrWhiteSpace(settings.PythonPath)?paths.WithinRoot(settings.PythonPath):paths.WithinRoot("runtime/voice/python/python.exe");
    public async Task EnsureStartedAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed,this);await lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            VoiceRuntimeIdentity expected;lock(identityGate){runtimeIdentity??=VoiceRuntimeIdentity.Capture(paths);expected=runtimeIdentity;expected.VerifyFiles(paths);}
            if(process is not {HasExited:false}||reader is not {IsCompleted:false})
            {
                var retired=process;process=null;var oldReader=reader;reader=null;var oldErrors=errors;errors=null;var oldJob=job;job=null;
                if(retired is not null)await RetireAsync(retired,oldReader,oldErrors,oldJob);else oldJob?.Dispose();
                if(!File.Exists(Python()))throw new FileNotFoundException("未找到 GPT-SoVITS 的 Python 运行环境。");
                State="启动中";var psi=new ProcessStartInfo(Python()){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=paths.Root,StandardInputEncoding=new System.Text.UTF8Encoding(false),StandardOutputEncoding=System.Text.Encoding.UTF8,StandardErrorEncoding=System.Text.Encoding.UTF8};
                psi.ArgumentList.Add(paths.WithinRoot("runtime/voice/worker.py"));psi.ArgumentList.Add("--root");psi.ArgumentList.Add(paths.Root);paths.Apply(psi);
                process=Process.Start(psi)??throw new IOException("语音子进程启动失败。");try{job=new KillJob(process);}catch{try{process.Kill(true);}finally{CloseRedirectedStreams(process);process.Dispose();process=null;}throw;}
                var active=process;
                reader=ReadLoop(active);
                errors=Task.Run(async()=>{try{while(await active.StandardError.ReadLineAsync() is { } line){ // Do not persist arbitrary traceback lines/paths or user speech.
                    if(line.Contains("Traceback",StringComparison.Ordinal))paths.Log("voice","Python 推理发生异常；详见运行诊断。");}}catch(IOException){}catch(InvalidOperationException){}});
            }
            // Keep the lifecycle gate until the identity handshake finishes. A
            // concurrent caller must never use a newly started, unverified worker.
            var health=await RequestAsync("health",new{},token,180,ensure:false,owner:process);
            expected.VerifyFiles(paths);expected.VerifyHealth(health,process.Id);State="就绪";
        }
        catch
        {
            var rejected=process;process=null;var rejectedReader=reader;reader=null;var rejectedErrors=errors;errors=null;var rejectedJob=job;job=null;
            try{if(rejected is not null)await RetireAsync(rejected,rejectedReader,rejectedErrors,rejectedJob);else rejectedJob?.Dispose();}
            finally{State="语音组件启动或身份校验失败";}
            throw;
        }
        finally{lifecycle.Release();}
    }
    public void VerifyKnownRuntimeFiles(){lock(identityGate){runtimeIdentity?.VerifyFiles(paths);}}
    public async Task RestoreAfterResetAsync(CancellationToken token)
    {
        VerifyKnownRuntimeFiles();
        if(!settings.VoiceEnabled)return;
        try{await LoadAsync(settings.VoiceModel,token);await EnsureStartedAsync(token);}
        catch{await StopAsync();throw;}
    }
    private async Task ReadLoop(Process active)
    {
        try
        {
            while(await active.StandardOutput.ReadLineAsync() is {} line)
            {
                if(line.Length>1_048_576)throw new InvalidDataException("推理进程返回超长信息。");
                JsonDocument doc;try{doc=JsonDocument.Parse(line);}catch(JsonException){continue;}
                using(doc)
                {
                    var root=doc.RootElement;if(!root.TryGetProperty("id",out var id)||id.ValueKind!=JsonValueKind.String)continue;
                    if(root.TryGetProperty("event",out _))continue;
                    if(pending.TryGetValue(id.GetString()!,out var request)&&ReferenceEquals(request.Owner,active)&&pending.TryRemove(id.GetString()!,out _))
                    {
                        if(root.TryGetProperty("ok",out var ok)&&ok.GetBoolean())request.Completion.TrySetResult(root.TryGetProperty("result",out var value)?value.Clone():default);
                        else { LogQualityRejection(root); request.Completion.TrySetException(new IOException("本地语音推理失败："+SafeWorkerError(root))); }
                    }
                }
            }
        }
        catch(Exception e){paths.Log("voice",e.GetType().Name);}
        finally{if(ReferenceEquals(process,active))State="已停止";foreach(var item in pending){if(ReferenceEquals(item.Value.Owner,active)&&pending.TryRemove(item.Key,out var request))request.Completion.TrySetException(new IOException("语音进程已退出；下一次请求将重新启动。"));}}
    }
    private static string SafeWorkerError(JsonElement root)
    {if(!root.TryGetProperty("error",out var e))return "未知错误";var text=e.ValueKind==JsonValueKind.String?e.GetString():e.ValueKind==JsonValueKind.Object&&e.TryGetProperty("message",out var m)?m.GetString():"运行错误";return text?.Length<=400?text??"错误":"请检查模型兼容性与运行环境。";}
    private void LogQualityRejection(JsonElement root)
    {
        // Persist reason counts only. Never log arbitrary worker bodies,
        // target/readback text, reference paths, or provider/user content.
        if(!root.TryGetProperty("error",out var error)||error.ValueKind!=JsonValueKind.Object
            ||!error.TryGetProperty("code",out var code)||code.ValueKind!=JsonValueKind.String||code.GetString()!="incomplete_speech"
            ||!error.TryGetProperty("diagnostics",out var diagnostic)||diagnostic.ValueKind!=JsonValueKind.Object
            ||!diagnostic.TryGetProperty("attempts",out var attempts)||attempts.ValueKind!=JsonValueKind.Array||attempts.GetArrayLength()>8)return;
        string[] allowed=["semantic_length_bound","invalid_pcm","empty_readback","empty_expected_reading","reading_length_bound","short_readback","reading_mismatch","missing_start","missing_end","missing_content","audio_length_bound","silent_audio","short_utterance_duration","expressive_utterance_duration"];
        var counts=new Dictionary<string,int>();
        foreach(var attempt in attempts.EnumerateArray())
            if(attempt.ValueKind==JsonValueKind.Object&&attempt.TryGetProperty("reason",out var reason)&&reason.ValueKind==JsonValueKind.String
                &&reason.GetString() is {} value&&allowed.Contains(value))counts[value]=counts.GetValueOrDefault(value)+1;
        paths.Log("voice-quality","完整性检查未通过；尝试="+attempts.GetArrayLength()+"；原因="+string.Join(",",counts.OrderBy(pair=>pair.Key).Select(pair=>pair.Key+":"+pair.Value)));
    }
    public async Task<JsonElement> RequestAsync(string method,object parameters,CancellationToken token,int timeout=180,bool ensure=true)
        =>await RequestAsync(method,parameters,token,timeout,ensure,null);
    private async Task<JsonElement> RequestAsync(string method,object parameters,CancellationToken token,int timeout,bool ensure,Process? owner)
    {
        if(ensure)await EnsureStartedAsync(token);owner??=process;if(owner is null)throw new IOException("推理进程不可用。");var id=Interlocked.Increment(ref sequence).ToString();var tcs=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);pending[id]=new(owner,tcs);
        try
        {
            await writer.WaitAsync(token);try {if(!ReferenceEquals(process,owner)||owner.HasExited)throw new IOException("推理进程不可用。");await owner.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{id,method,@params=parameters}).AsMemory(),token);await owner.StandardInput.FlushAsync(token);}finally{writer.Release();}
            var result=await tcs.Task.WaitAsync(TimeSpan.FromSeconds(timeout),token);if(!ReferenceEquals(process,owner))throw new OperationCanceledException("语音进程已切换，旧结果已丢弃。",token);return result;
        }
        finally{pending.TryRemove(id,out _);}
    }
    public async Task<IReadOnlyList<VoiceModel>> ModelsAsync(CancellationToken token)
    {
        var r=await RequestAsync("models",new{},token);return r.GetProperty("models").EnumerateArray().Select(m=>new VoiceModel(m.GetProperty("id").GetString()!,m.GetProperty("name").GetString()!,m.TryGetProperty("version",out var v)?v.GetString()??"":"",m.GetProperty("valid").GetBoolean(),m.TryGetProperty("error",out var e)?e.ToString():null,m.TryGetProperty("loaded",out var l)&&l.GetBoolean())).ToArray();
    }
    public async Task LoadAsync(string model,CancellationToken token)
    {
        await synthesis.WaitAsync(token);Process? owner=null;
        try{await EnsureStartedAsync(token);owner=process;var loaded=await RequestAsync("load",new{modelId=model},token,240,false,owner);if(loaded.GetProperty("modelId").GetString()!=model)throw new InvalidDataException("声模加载确认不一致。");settings.VoiceModel=model;State="就绪";}
        catch(OperationCanceledException){await StopOwnedAsync(owner);throw;}
        catch(TimeoutException){await StopOwnedAsync(owner);throw;}
        finally{synthesis.Release();}
    }
    public async Task<IReadOnlyList<VoiceAudio>> SynthesizeAsync(string text,string emotion,string requestId,string session,CancellationToken token)
    {
        if(!ProtocolClient.Emotions.Contains(emotion)||text.Length is <1 or >1200||!AppPaths.IsVoiceRequestId(requestId))throw new InvalidDataException("非法语音输入。");
        await synthesis.WaitAsync(token);Process? owner=null;
        try
        {
            await EnsureStartedAsync(token);owner=process;
            var selected=string.IsNullOrWhiteSpace(settings.VoiceModel)?"AtriTurbo":settings.VoiceModel;
            var health=await RequestAsync("health",new{},token,180,false,owner);
            if(health.GetProperty("modelId").GetString()!=selected)
            {
                State="恢复已选声模中";
                try{var loaded=await RequestAsync("load",new{modelId=selected},token,240,false,owner);if(loaded.GetProperty("modelId").GetString()!=selected)throw new InvalidDataException("声模加载确认不一致。");}
                catch(IOException e){throw new IOException("无法恢复已选声模，请在 SYSTEM 重新选择合法模型。"+e.Message,e);}
            }
            var requestDirectory=paths.WithinRoot(Path.Combine("data","temp","voice",requestId));
            if(Directory.Exists(requestDirectory)||File.Exists(requestDirectory))throw new InvalidDataException("语音请求目录已存在，拒绝覆盖旧音频。");
            if(!audioOwners.TryAdd(requestId,new(requestId,owner!,owner!.Id)))throw new InvalidDataException("语音请求标识已在使用中。");
            State="合成中";var result=await RequestAsync("synthesize",new{text,emotion,requestId,sessionId=session},token,240,false,owner);
            if(result.GetProperty("modelId").GetString()!=selected)throw new InvalidDataException("合成声模与已选声模不一致。");
            var output=new List<VoiceAudio>();foreach(var s in result.GetProperty("segments").EnumerateArray())
            {
                var full=Path.GetFullPath(s.GetProperty("path").GetString()!);var relative=Path.GetRelativePath(paths.Root,full);paths.WithinRoot(relative);
                if(!full.StartsWith(Path.Combine(paths.Temp,"voice")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("语音文件未位于受控临时目录。");
                output.Add(new(full,s.GetProperty("text").GetString()!,emotion,s.TryGetProperty("duration",out var d)?d.GetDouble():0));
            }
            if(string.Concat(output.Select(x=>x.Text))!=text)throw new InvalidDataException("语音分段与台词内容不一致。");State="就绪";return output;
        }
        catch(OperationCanceledException){await StopOwnedAsync(owner);throw;}
        catch(TimeoutException){await StopOwnedAsync(owner);throw;}
        finally{synthesis.Release();}
    }
    public async Task ReleaseAsync(string requestId)
    {
        if(!audioOwners.TryGetValue(requestId,out var registered))return;
        if(Interlocked.CompareExchange(ref registered.Releasing,1,0)!=0)return;
        Volatile.Write(ref registered.ReleaseRequested,1);var owner=process;bool retryAfterRetirement=false;
        try
        {
            if(owner is not null)
            {
                try{if(ReferenceEquals(owner,registered.Process)){await RequestAsync("release",new{requestId=registered.RequestId},CancellationToken.None,5,false,owner);return;}}
                catch(Exception e){paths.Log("voice-release",e.GetType().Name);}
            }
            // Cancellation/timeout can retire the worker before the caller's
            // finally releases audio. Never start a new model just to clean it.
            // Wait out an in-progress retirement before checking its files.
            await lifecycle.WaitAsync();
            try
            {
                if(!paths.TryCleanVoiceRequest(registered.RequestId,registered.Pid))
                {
                    try{retryAfterRetirement=!registered.Process.HasExited;}catch(InvalidOperationException){}
                    paths.Log("voice-release","已释放请求的临时音频暂不能安全回收；保留供再次释放或失效任务清理。");
                }
            }
            finally{lifecycle.Release();}
        }
        finally{if(retryAfterRetirement)Volatile.Write(ref registered.Releasing,0);else audioOwners.TryRemove(new KeyValuePair<string,AudioOwner>(requestId,registered));}
    }
    private Task StopOwnedAsync(Process? owner)=>owner is null?Task.CompletedTask:StopCoreAsync(owner);
    public Task StopAsync()=>StopCoreAsync(null);
    private async Task StopCoreAsync(Process? expected)
    {
        await lifecycle.WaitAsync();try
        {
            if(expected is not null&&!ReferenceEquals(process,expected))return;
            var p=process;process=null;var activeReader=reader;reader=null;var activeErrors=errors;errors=null;var ownedJob=job;job=null;
            try{if(p is not null)await RetireAsync(p,activeReader,activeErrors,ownedJob);else ownedJob?.Dispose();}
            finally{State="已停止";}
        }
        finally{lifecycle.Release();}
    }
    private static async Task RetireAsync(Process active,Task? outputReader,Task? errorReader,KillJob? ownedJob)
    {
        try
        {
            try{if(!active.HasExited)active.Kill(entireProcessTree:true);await active.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));}
            catch(InvalidOperationException){}catch(TimeoutException){}
            finally{ownedJob?.Dispose();}
            try{await Task.WhenAll(outputReader??Task.CompletedTask,errorReader??Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(3));}catch(TimeoutException){}
        }
        finally{CloseRedirectedStreams(active);active.Dispose();ownedJob?.Dispose();}
    }
    private static void CloseRedirectedStreams(Process active)
    {
        try{active.StandardInput.Dispose();}catch(IOException){}catch(InvalidOperationException){}
        try{active.StandardOutput.Dispose();}catch(IOException){}catch(InvalidOperationException){}
        try{active.StandardError.Dispose();}catch(IOException){}catch(InvalidOperationException){}
    }
    public async ValueTask DisposeAsync()
    {
        disposed=true;await StopAsync();
        // Retry only audio whose caller already requested release, never PCM
        // deliberately kept for playback after a worker restart.
        foreach(var item in audioOwners)if(Volatile.Read(ref item.Value.ReleaseRequested)!=0)await ReleaseAsync(item.Key);
    }
}
internal sealed class KillJob:IDisposable
{
    private IntPtr handle;
    [StructLayout(LayoutKind.Sequential)]struct Basic {public long PerProcess,PerJob;public uint Flags;public UIntPtr Min,Max;public uint Active;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]struct Io {public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]struct Extended {public Basic Basic;public Io Io;public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr CreateJobObject(IntPtr attrs,string? name);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(IntPtr h,int cls,ref Extended value,uint length);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool AssignProcessToJobObject(IntPtr h,IntPtr process);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr h);
    public KillJob(Process process)
    {handle=CreateJobObject(IntPtr.Zero,null);var info=new Extended{Basic=new Basic{Flags=0x2000}};if(handle==IntPtr.Zero||!SetInformationJobObject(handle,9,ref info,(uint)Marshal.SizeOf<Extended>())||!AssignProcessToJobObject(handle,process.Handle)){Dispose();throw new IOException("无法将语音进程加入受管理的 Windows Job。");}}
    public void Dispose(){if(handle!=IntPtr.Zero){CloseHandle(handle);handle=IntPtr.Zero;}}
}
