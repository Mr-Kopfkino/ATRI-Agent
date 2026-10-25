using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Atri.Core;

namespace Atri.Desktop;

public sealed partial class MainWindow
{
    private readonly TextBox workProgress=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,
        VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontSize=15,Foreground=Brushes.White,
        Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(10)};
    private readonly Border workProgressPanel=new(){Visibility=Visibility.Collapsed,CornerRadius=new CornerRadius(6),
        Background=new SolidColorBrush(Color.FromArgb(220,8,43,67)),BorderBrush=Aqua,BorderThickness=new Thickness(1)};
    private ChatWorkSession? chatWorkSession;
    private bool chatWorkNeedsRecovery;

    private void BuildChatWorkProgress()
    {workProgressPanel.Child=workProgress;Place(chat,workProgressPanel,72,337,1050,137);}
    private void ClearChatWorkProgress(){workProgress.Clear();workProgressPanel.Visibility=Visibility.Collapsed;}

    private void RefreshChatWorkInput()
    {
        var working=chatWorkSession is not null||chatWorkNeedsRecovery;
        input.IsReadOnly=working;
        if(working)input.Text=chatWorkNeedsRecovery?"任务停止状态待确认，请检查 WORK 或重启应用":"亚托莉正在工作";
        else if(input.Text is "亚托莉正在工作" or "任务停止状态待确认，请检查 WORK 或重启应用")input.Clear();
        send.IsEnabled=record.IsEnabled=!working&&!busy&&!closing&&!memoryTransitioning;
        imageButton.IsEnabled=!working&&!closing&&!memoryTransitioning;
    }

    private async Task<JsonElement> ChatWorkSnapshotAsync(CapabilityBridge bridge,Settings settings,string key,CancellationToken token)
    {
        try
        {
            // Reuse the current conversation configuration; never provision or
            // copy credentials from another application or provider profile.
            try{await bridge.ConfigureProviderAsync(settings.ApiBaseUrl,settings.Model,key,token);}
            catch(CapabilityOperationException error)
            {
                // A confirmed provider rejection has already cleared Pi's old
                // configuration. Other providers and existing tasks still have
                // a real host snapshot; connection failures remain unavailable.
                Paths.Log("chat-work-configuration",error.GetType().Name);
            }
            return await bridge.InvokeAsync("chat_work_snapshot",token:token);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(Exception error)
        {
            Paths.Log("chat-work-snapshot",error.GetType().Name);
            return JsonSerializer.SerializeToElement(new{tasks=Array.Empty<object>(),providers=Array.Empty<object>(),
                unavailable="当前工作组件配置或连接不可用。可以正常聊天，本轮不能建立或调度任务。"});
        }
    }

    internal bool OwnsChatWorkPermission(JsonElement item)
    {
        var work=chatWorkSession;
        if(work is null)return false;
        var requestId=ChatWorkSession.Text(item,"chatRequestId");
        if(requestId.Length>0)return requestId==work.RequestId;
        var attemptId=ChatWorkSession.Text(item,"attemptId");
        return work.WorkId.Length>0&&ChatWorkSession.Text(item,"workId")==work.WorkId&&
            (attemptId.Length==0||work.AttemptId.Length==0||attemptId==work.AttemptId);
    }

    private async Task RunChatWorkTurnAsync(ChatWorkRequest request,DialogueReply acknowledgement,JsonElement snapshot,string originalInput,
        CapabilityBridge bridge,MemoryStore memory,ContextBuilder builder,string session,Settings settings,string key,long epoch,CancellationToken token)
    {
        try{ChatWorkSession.ValidateProposal(request,snapshot,originalInput);}
        catch(Exception error)
        {
            var rejected=JsonSerializer.SerializeToElement(new{status="not_started",result="",error=error.Message,artifacts=Array.Empty<object>(),stopConfirmed=true});
            AppendChatWorkProgress("这次任务尚未开始："+error.Message,true);
            await ReportChatWorkAsync(rejected,request.Summary,builder,session,settings,key,epoch,token);
            ChatWorkMemory.Record(memory,session,request.Summary,request.WorkId,"not_started",0,request.Intent);
            return;
        }
        var work=new ChatWorkSession((operation,args,cancellation)=>bridge.InvokeAsync(operation,args,cancellation));
        chatWorkSession=work;RefreshChatWorkInput();
        workProgress.Clear();workProgressPanel.Visibility=Visibility.Visible;
        var permissions=new HashSet<string>(StringComparer.Ordinal);
        var progressGate=new object();string bufferedProgress="";bool progressScheduled=false;
        bool Current()=>epoch==generation&&!closing&&!memoryTransitioning&&Settings.LastSessionId==session&&ReferenceEquals(Capabilities,bridge)&&ReferenceEquals(chatWorkSession,work);
        void Changed(JsonElement result)
        {
            if(!Current())return;
            var status=ChatWorkSession.Text(result,"status");
            SetState(status is "queued" or "running"?"亚托莉正在工作…":"正在准备任务汇报…");
            AppendChatWorkProgress(status switch
            {
                "queued"=>"任务已建立，正在准备执行…",
                "running"=>"亚托莉正在处理任务…",
                "succeeded"=>"任务处理结束，正在核对结果并准备汇报。",
                "failed"=>"任务未能完成："+ChatWorkSession.Text(result,"error"),
                "cancelled"=>"任务已取消。",
                "orphaned"=>"任务曾被中断，未自动重新执行。",
                "not_started"=>"这个任务尚未开始执行。",
                _=>"正在确认任务状态…"
            });
        }
        void Received(JsonElement item)
        {
            if(!OwnsChatWorkPermission(item))return;
            if(ChatWorkSession.Text(item,"event")=="provider.progress")
            {
                lock(progressGate)
                {
                    bufferedProgress+=ChatWorkSession.Text(item,"text");
                    if(bufferedProgress.Length>8000)bufferedProgress=bufferedProgress[^8000..];
                    if(progressScheduled)return;
                    progressScheduled=true;
                }
                Dispatcher.BeginInvoke(async()=>
                {
                    await Task.Delay(120);
                    string text;lock(progressGate){text=bufferedProgress;bufferedProgress="";progressScheduled=false;}
                    if(Current())AppendChatWorkProgress(text);
                });
                return;
            }
            Dispatcher.BeginInvoke(async()=>
            {
                if(!Current())return;
                var attempt=ChatWorkSession.Text(item,"attemptId");
                if(work.AttemptId.Length>0&&attempt.Length>0&&attempt!=work.AttemptId)return;
                var kind=ChatWorkSession.Text(item,"event");
                if(kind=="permission.requested")
                {
                    var id=ChatWorkSession.Text(item,"requestId");
                    if(!permissions.Add(id))return;
                    try
                    {
                        AppendChatWorkProgress("等待确认本次外部访问…");
                        var allowed=MessageBox.Show(this,ChatWorkSession.Text(item,"reason"),"亚托莉 · 确认本次外部访问",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
                        if(Current())await bridge.InvokeAsync("permission_response",new{requestId=id,allow=allowed},token);
                    }
                    catch(OperationCanceledException){}
                    catch(Exception error){if(Current())AppendChatWorkProgress("此次确认未完成："+error.Message);}
                }
                else if(kind=="provider.tool")
                {
                    var tool=ChatWorkSession.Text(item,"tool");
                    AppendChatWorkProgress(tool switch{"read"=>"正在读取任务文件…","ls"=>"正在检查工作目录…","write"=>"正在生成任务文件…","edit"=>"正在修改任务文件…","browser_read"=>"正在读取已确认的网页…",_=>"正在处理任务步骤…"});
                }
                else if(kind=="provider.ready")AppendChatWorkProgress("工作组件已就绪，开始处理。 ");
                else if(kind=="work.started")AppendChatWorkProgress("任务开始执行。 ");
            });
        }
        bridge.EventReceived+=Received;
        var recorded=false;
        try
        {
            AppendChatWorkProgress("正在准备工作…");
            await DeliverChatWorkReplyAsync(acknowledgement,settings,epoch,token);
            token.ThrowIfCancellationRequested();
            SetState(request.Intent=="report"?"正在核对任务记录…":"亚托莉正在工作…");
            var result=await work.RunAsync(request,Changed,token);
            token.ThrowIfCancellationRequested();
            if(!Current())return;
            chatWorkNeedsRecovery=work.RequiresRecovery;
            ShowChatWorkArtifacts(result,request.Intent!="report");
            ChatWorkMemory.Record(memory,session,request.Summary,work.WorkId,work.RequiresRecovery?"unconfirmed":ChatWorkSession.Text(result,"status"),ArtifactCount(result),request.Intent);recorded=true;
            await ReportChatWorkAsync(result,request.Summary,builder,session,settings,key,epoch,token);
        }
        finally
        {
            bridge.EventReceived-=Received;
            try
            {
                if(!recorded)
                {
                    // The turn/session owner waits for RunAsync's stop-and-drain
                    // before replacing memory or closing it. No raw work text is saved.
                    var outcome=work.Outcome;
                    var status=work.RequiresRecovery?"unconfirmed":outcome.ValueKind==JsonValueKind.Object?ChatWorkSession.Text(outcome,"status"):"cancelled";
                    ChatWorkMemory.Record(memory,session,request.Summary,work.WorkId,status,ArtifactCount(outcome),request.Intent);
                }
            }
            finally
            {
                chatWorkNeedsRecovery|=work.RequiresRecovery;
                if(ReferenceEquals(chatWorkSession,work)){chatWorkSession=null;RefreshChatWorkInput();}
            }
        }
    }

    private async Task ReportChatWorkAsync(JsonElement result,string summary,ContextBuilder builder,string session,Settings settings,string key,long epoch,CancellationToken token)
    {
        DialogueReply report;
        try
        {
            var messages=(await Task.Run(()=>builder.Build(session,"请用平常的语气向我说明这次任务的真实处理情况。",settings,token),token)).ToList();
            messages.Insert(messages.Count-1,new("system",ChatWork.ReportPrompt(result)+"\n本次用户任务简述（概括，非原话）："+summary));
            report=await api.ReplyAsync(settings,key,messages,token,allowWork:false);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(Exception error){Paths.Log("chat-work-report",error.GetType().Name);report=ChatWorkFallback(result);}
        await DeliverChatWorkReplyAsync(report,settings,epoch,token);
    }

    private async Task DeliverChatWorkReplyAsync(DialogueReply reply,Settings settings,long epoch,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(epoch!=generation)return;
        lastReply=reply;currentSegment=-1;
        if(settings.VoiceEnabled)
        {
            try{await Speak(reply,epoch,token);return;}
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception error){Paths.Log("chat-work-voice",error.GetType().Name);SetState("本次语音未能播放，已显示完整回复。 ");}
        }
        RenderSubtitle();SetEmotion(reply.Segments[0].Emotion);SetState(settings.VoiceEnabled?"本次语音未能播放，已显示完整回复。":"任务回复已显示 · 语音已关闭");
    }

    private void AppendChatWorkProgress(string text,bool reset=false)
    {
        if(text.Length==0)return;
        workProgressPanel.Visibility=Visibility.Visible;
        if(reset)workProgress.Clear();
        // Polls repeat the same durable status; append only a changed line.
        if(workProgress.Text.EndsWith(text,StringComparison.Ordinal))return;
        workProgress.AppendText((workProgress.Text.Length>0?"\n":"")+text);
        if(workProgress.Text.Length>8000)workProgress.Text=workProgress.Text[^8000..];
        workProgress.ScrollToEnd();
    }

    private void ShowChatWorkArtifacts(JsonElement result,bool owned=true)
    {
        var error=ChatWorkSession.Text(result,"error");if(error.Length>0)AppendChatWorkProgress(error);
        var text=ChatWorkSession.Text(result,"result");if(text.Length>0)AppendChatWorkProgress(text);
        if(result.TryGetProperty("artifacts",out var artifacts)&&artifacts.ValueKind==JsonValueKind.Array)
            foreach(var artifact in artifacts.EnumerateArray().Take(20))AppendChatWorkProgress("产物："+ChatWorkSession.Text(artifact,"title")+"  "+ChatWorkSession.Text(artifact,"path"));
        if(owned&&result.TryGetProperty("stopConfirmed",out var stopped)&&stopped.ValueKind==JsonValueKind.False)
            AppendChatWorkProgress("任务进程停止状态尚未确认。请在 WORK 检查并重启应用后再开始聊天。 ");
    }
    private static int ArtifactCount(JsonElement result)=>result.ValueKind==JsonValueKind.Object&&result.TryGetProperty("artifacts",out var artifacts)&&artifacts.ValueKind==JsonValueKind.Array?artifacts.GetArrayLength():0;
    private static DialogueReply ChatWorkFallback(JsonElement result)
    {
        var status=ChatWorkSession.Text(result,"status");
        var stopped=status is "running" or "queued"||!(result.TryGetProperty("stopConfirmed",out var confirmed)&&confirmed.ValueKind==JsonValueKind.False);
        var segment=!stopped?new DialogueSegment(0,"むぅ、作業が安全に止まったか、まだ確認できません。画面の記録を確認してください。","唔……我还没确认任务是否已经安全停止。请查看画面上的记录。","serious"):
            status switch
            {
                "succeeded"=>new(0,"作業は終わりました！ただ、今は詳しい報告をうまくまとめられません。結果は画面に残してありますから、確認してくださいね。","任务处理结束了！不过我现在没能整理好详细汇报。结果已经留在画面上了，请查看一下哦。","happy"),
                "cancelled"=>new(0,"作業は止めました。途中の成果があれば、そのまま残してあります。","任务已经停止了。中途若有成果，会原样保留。","serious"),
                "queued" or "running"=>new(0,"確認しました。まだ作業中です。終わったふりはしませんから、もう少し待っていてください。","我确认过了，任务还在处理中。我可不会假装已经做好，请再等一下。","serious"),
                "orphaned"=>new(0,"作業が途中で中断されていました。勝手にやり直さず、記録を残してあります。","任务之前中断了。我没有擅自重做，记录已经保留。","serious"),
                _=>new(0,"むぅ、今回はうまくいきませんでした。できたふりはしません。分かっている理由は画面に残してあります。","唔……这次没能办成。我不会假装已经做好，已知的原因留在画面上了。","sad")
            };
        return new([segment],[]);
    }
}
