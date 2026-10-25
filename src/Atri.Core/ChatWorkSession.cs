using System.Text.Json;

namespace Atri.Core;

/// <summary>Owns exactly one chat operation and drains its attempt before a turn can retire.</summary>
public sealed class ChatWorkSession(Func<string,object?,CancellationToken,Task<JsonElement>> invoke)
{
    public string RequestId {get;}=Guid.NewGuid().ToString("N");
    public string WorkId {get;private set;}="";
    public string AttemptId {get;private set;}="";
    public JsonElement Outcome {get;private set;}
    public bool DispatchStarted {get;private set;}
    public bool IsExecutionPending {get;private set;}=true;
    private int stopRequested;
    private bool ownsExecution;
    public void RequestStop()=>Interlocked.Exchange(ref stopRequested,1);
    public bool StopRequested=>Volatile.Read(ref stopRequested)!=0;
    public bool RequiresRecovery=>ownsExecution&&Outcome.ValueKind==JsonValueKind.Object&&
        (!Outcome.TryGetProperty("stopConfirmed",out var stopped)||stopped.ValueKind!=JsonValueKind.True);

    public static void ValidateProposal(ChatWorkRequest request,JsonElement snapshot,string input)
    {
        if(request.BrowserUrl.Length>0&&!input.Contains(request.BrowserUrl,StringComparison.Ordinal))
            throw new InvalidDataException("浏览器范围必须来自本次用户明确给出的完整网址。");
        if(request.Intent=="create")
        {
            var provider=request.Provider.Length==0?"pi":request.Provider;
            if(!snapshot.TryGetProperty("providers",out var providers)||!providers.EnumerateArray().Any(p=>Text(p,"id")==provider&&p.TryGetProperty("ready",out var ready)&&ready.ValueKind==JsonValueKind.True))
                throw new InvalidOperationException("当前任务执行组件尚未就绪；此次没有启动任务。");
        }
        else if(!snapshot.TryGetProperty("tasks",out var tasks)||!tasks.EnumerateArray().Any(t=>Text(t,"workId")==request.WorkId))
            throw new InvalidDataException("无法唯一确认要操作的现有任务；此次没有启动任务。");
    }

    public async Task<JsonElement> RunAsync(ChatWorkRequest request,Action<JsonElement> progress,CancellationToken token)
    {
        bool dispatched=false;
        ownsExecution=request.Intent!="report";
        try
        {
            token.ThrowIfCancellationRequested();
            if(StopRequested)return Outcome=JsonSerializer.SerializeToElement(new{workId="",attemptId="",status="cancelled",result="",error="启动前已取消。",artifacts=Array.Empty<object>(),stopConfirmed=true});
            // Do not cancel receipt of a mutating RPC: even a cancelled turn must
            // learn the created identity so it can stop and drain that exact attempt.
            dispatched=true;DispatchStarted=true;
            var receipt=await invoke("chat_work_dispatch",new{intent=request.Intent,workId=request.WorkId,instruction=request.Instruction,
                provider=request.Provider,summary=request.Summary,browserUrl=request.BrowserUrl,requestId=RequestId},CancellationToken.None);
            if(request.WorkId.Length>0&&Text(receipt,"workId")!=request.WorkId)
                throw new InvalidDataException("任务回执与用户选定的任务不匹配。");
            WorkId=Text(receipt,"workId");AttemptId=Text(receipt,"attemptId");Outcome=receipt;
            progress(Outcome);
            if(request.Intent=="report")return Outcome;
            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(18));
            using var waiting=CancellationTokenSource.CreateLinkedTokenSource(token,deadline.Token);
            bool cancellationSent=request.Intent=="cancel";
            while(!Settled(Outcome))
            {
                waiting.Token.ThrowIfCancellationRequested();
                if(Volatile.Read(ref stopRequested)!=0&&!cancellationSent)
                {
                    await invoke("cancel",new{workId=WorkId,attemptId=AttemptId},waiting.Token);cancellationSent=true;
                }
                await Task.Delay(400,waiting.Token);
                var status=await invoke("chat_work_status",new{workId=WorkId,attemptId=AttemptId},waiting.Token);
                CheckIdentity(status);Outcome=status;progress(Outcome);
            }
            return Outcome;
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested)
        {
            if(dispatched&&request.Intent!="report")await StopAndDrainAsync();
            throw;
        }
        catch(Exception error)
        {
            if(dispatched&&request.Intent!="report"&&!Settled(Outcome))await StopAndDrainAsync();
            // A failed control connection is never presented as completed work.
            var confirmed=!ownsExecution||!dispatched||Settled(Outcome)&&!RequiresRecovery;
            Outcome=JsonSerializer.SerializeToElement(new{workId=WorkId,attemptId=AttemptId,status="failed",
                error="任务未能正常完成："+SafeError(error),result="",artifacts=Array.Empty<object>(),stopConfirmed=confirmed});
            return Outcome;
        }
        finally{IsExecutionPending=false;}
    }

    private async Task StopAndDrainAsync()
    {
        if(Settled(Outcome))return;
        using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            if(WorkId.Length==0)
            {
                Outcome=await invoke("chat_work_status",new{requestId=RequestId},cleanup.Token);
                WorkId=Text(Outcome,"workId");AttemptId=Text(Outcome,"attemptId");
                if(Text(Outcome,"status")=="not_found"||Settled(Outcome))return;
            }
            if(WorkId.Length==0||AttemptId.Length==0)throw new InvalidDataException("任务回执无法确认。");
            await invoke("cancel",new{workId=WorkId,attemptId=AttemptId},cleanup.Token);
            do
            {
                var status=await invoke("chat_work_status",new{workId=WorkId,attemptId=AttemptId},cleanup.Token);
                CheckIdentity(status);Outcome=status;
                if(Settled(Outcome))return;
                await Task.Delay(200,cleanup.Token);
            }while(true);
        }
        catch(Exception)
        {
            Outcome=JsonSerializer.SerializeToElement(new{workId=WorkId,attemptId=AttemptId,status="failed",result="",
                error="任务停止状态暂时无法确认，请在 WORK 检查并重启应用后继续。",artifacts=Array.Empty<object>(),stopConfirmed=false});
        }
    }

    private void CheckIdentity(JsonElement result)
    {
        if(Text(result,"workId")!=WorkId||Text(result,"attemptId")!=AttemptId)
            throw new InvalidDataException("任务状态与本次执行不匹配，不能当作本次结果。");
    }
    public static bool Terminal(JsonElement result)=>Text(result,"status") is "succeeded" or "failed" or "cancelled" or "orphaned" or "not_started" or "not_found";
    private static bool Settled(JsonElement result)=>Terminal(result)&&
        (result.TryGetProperty("stopConfirmed",out var confirmed)&&confirmed.ValueKind==JsonValueKind.True||Text(result,"status")=="failed");
    public static string Text(JsonElement value,string name)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out var field)&&field.ValueKind==JsonValueKind.String?field.GetString()??"":"";
    private static string SafeError(Exception error)=>error switch
    {
        OperationCanceledException=>"任务等待超时，已请求停止。",
        TimeoutException=>"任务组件响应超时，未确认完成。",
        IOException=>"与任务组件的连接中断，未确认完成。",
        _=>error.Message.Length>800?error.Message[..800]:error.Message
    };
}
