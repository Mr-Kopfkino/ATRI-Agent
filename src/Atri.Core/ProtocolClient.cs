using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atri.Core;
public sealed record DialogueSegment(int Order,string Ja,string Zh,string Emotion);
public sealed record FactCandidate(string Key,string Text,string Evidence,string Kind="UserFact");
public sealed record DialogueReply(IReadOnlyList<DialogueSegment> Segments,IReadOnlyList<FactCandidate> Memories,ChatWorkRequest? Work=null);
public sealed record ChatMessage(string Role,string Content,VisualAttachment? Attachment=null);

public sealed class ProtocolClient : IDisposable
{
    private readonly HttpClient client;
    public ProtocolClient(HttpMessageHandler? handler=null) {client=handler==null?new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}):new HttpClient(handler);client.Timeout=Timeout.InfiniteTimeSpan;}
    public static readonly HashSet<string> Emotions=["neutral","happy","shy","angry","proud","sad","serious","comfort","curious","surprised"];
    public static Uri Endpoint(string value,string suffix)
    {
        var uri=new Uri(value);
        if(uri.AbsolutePath.TrimEnd('/').EndsWith("/"+suffix,StringComparison.Ordinal))return uri;
        var builder=new UriBuilder(uri){Path=uri.AbsolutePath.TrimEnd('/')+"/"+suffix};
        return builder.Uri;
    }
    public async Task<string> CompleteJsonAsync(Settings settings,string key,IReadOnlyList<ChatMessage> messages,CancellationToken cancellation)
    {
        settings.Validate();
        if(string.IsNullOrWhiteSpace(settings.ApiBaseUrl)||string.IsNullOrWhiteSpace(settings.Model))throw new InvalidOperationException("请先配置对话 API 与模型。");
        var payload=new Dictionary<string,object>{["model"]=settings.Model,["messages"]=messages.Select(m=>new{role=m.Role,content=m.Content}),["temperature"]=0,["max_tokens"]=settings.MaxReplyTokens,["stream"]=false};
        if(settings.StructuredOutput)payload["response_format"]=new{type="json_object"};
        string raw;
        try{raw=await PostJsonAsync(Endpoint(settings.ApiBaseUrl,"chat/completions"),key,payload,settings.TimeoutSeconds,cancellation);}
        catch(ProviderException e) when(e.StatusCode==400&&payload.Remove("response_format")){raw=await PostJsonAsync(Endpoint(settings.ApiBaseUrl,"chat/completions"),key,payload,settings.TimeoutSeconds,cancellation);}
        using var doc=JsonDocument.Parse(raw);
        if(!doc.RootElement.TryGetProperty("choices",out var choices)||choices.ValueKind!=JsonValueKind.Array||choices.GetArrayLength()==0)throw new InvalidDataException("记忆 API 没有返回 choices。");
        var choice=choices[0];
        if(choice.TryGetProperty("finish_reason",out var finish)&&finish.GetString()=="length")throw new InvalidDataException("记忆 JSON 被截断；请增加回复 token 上限后重试。");
        return choice.GetProperty("message").GetProperty("content").GetString()??throw new InvalidDataException("记忆 API 返回空内容。");
    }
    public async Task<DialogueReply> ReplyAsync(Settings settings,string key,IReadOnlyList<ChatMessage> messages,CancellationToken cancellation,bool allowWork=true)
    {
        if(string.IsNullOrWhiteSpace(settings.ApiBaseUrl)||string.IsNullOrWhiteSpace(settings.Model)) throw new InvalidOperationException("请先在 SYSTEM 填写模型 API 地址和模型名称。");
        settings.Validate(); var working=messages.ToList();bool structured=settings.StructuredOutput;
        foreach(var message in working)message.Attachment?.Validate();
        // A second bounded format repair handles providers that intermittently
        // emit empty or malformed content after the first repair. A valid first
        // reply adds no repair round; transport/provider errors stay separate.
        for(int repair=0;repair<3;repair++)
        {
            var payload=new Dictionary<string,object>{["model"]=settings.Model,["messages"]=working.Select(m=>new{role=m.Role,content=m.Attachment is null?(object)m.Content:new object[]{new{type="text",text=m.Content},new{type="image_url",image_url=new{url=$"data:{m.Attachment.MimeType};base64,{m.Attachment.Base64}",detail="auto"}}}}),["temperature"]=settings.Temperature,["max_tokens"]=settings.MaxReplyTokens,["stream"]=false};
            if(structured)payload["response_format"]=new{type="json_object"};
            string raw;
            try {raw=await PostJsonAsync(Endpoint(settings.ApiBaseUrl,"chat/completions"),key,payload,settings.TimeoutSeconds,cancellation);}
            catch(ProviderException e) when(e.StatusCode==400 && structured) {structured=false;payload.Remove("response_format");raw=await PostJsonAsync(Endpoint(settings.ApiBaseUrl,"chat/completions"),key,payload,settings.TimeoutSeconds,cancellation);}
            using var doc=JsonDocument.Parse(raw);
            if(!doc.RootElement.TryGetProperty("choices",out var choices)||choices.GetArrayLength()==0)throw new InvalidDataException("API 没有返回 choices。");
            var choice=choices[0];
            if(choice.TryGetProperty("finish_reason",out var finish)&&finish.GetString()=="length")throw new InvalidDataException("回复被供应商截断；请增加回复 token 上限。");
            var content=choice.GetProperty("message").GetProperty("content").GetString()??"";
            try
            {
                var reply=ParseReply(content);
                if(!allowWork&&reply.Work is not null)throw new InvalidDataException("本轮只允许报告已有事实，不允许请求 Work 操作。");
                return reply;
            }
            catch(Exception e) when(e is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            {
                if(repair==2)break;
                working.Add(new("assistant",content.Length<16000?content:content[..16000]));
                working.Add(new("user","上次回复格式不合法。仅修复为约定的 JSON，保留语义；segments 的 ja 必须是可直接朗读的日语台词、zh 为逐段对应简体中文、emotion 为受控标签。不要附带舞台说明或把工具控制信息写入台词。work 如有请求须严格遵循本轮系统的字段、能力和任务范围约束；普通聊天为 null。"+(!allowWork?"本轮只报告宿主提供的已有事实，work 必须为 null，不得发起、继续、重试或取消任务。":"")));
            }
        }
        throw new InvalidDataException("供应商连续三次返回无法校验的双语回复。");
    }
    public static DialogueReply ParseReply(string content)
    {
        content=content.Trim();if(content.StartsWith("```")){var end=content.LastIndexOf("```",StringComparison.Ordinal);var start=content.IndexOf('\n');if(start<0||end<=start)throw new InvalidDataException("JSON 代码块不完整。");content=content[(start+1)..end].Trim();}
        using var d=JsonDocument.Parse(content,new JsonDocumentOptions{MaxDepth=12});var root=d.RootElement;
        if(root.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("回复须为 JSON 对象。");
        var workFields=root.EnumerateObject().Where(p=>p.Name=="work").ToArray();
        if(workFields.Length>1)throw new InvalidDataException("回复只能包含一个 work 字段。");
        var work=workFields.Length==0||workFields[0].Value.ValueKind==JsonValueKind.Null?null:ChatWork.Parse(workFields[0].Value);
        if(!root.TryGetProperty("segments",out var array)||array.ValueKind!=JsonValueKind.Array||array.GetArrayLength() is <1 or >32)throw new InvalidDataException("片段数须为 1–32。");
        var result=new List<DialogueSegment>();int total=0;
        foreach(var seg in array.EnumerateArray())
        {
            var ja=seg.GetProperty("ja").GetString()?.Trim()??"";var zh=seg.GetProperty("zh").GetString()?.Trim()??"";var emotion=seg.GetProperty("emotion").GetString()??"neutral";
            if(ja.Length is <1 or >1200||zh.Length is <1 or >1600||!Emotions.Contains(emotion))throw new InvalidDataException("台词、翻译或情绪字段不合法。");
            // Kanji-only Japanese is valid. Reject control/meta text, not Han characters.
            if(ja.IndexOfAny(['{','}','[',']','*','`','<','>'])>=0||Regex.IsMatch(ja,@"(?:emotion|segments|字幕|舞台说明)\s*[:：]",RegexOptions.IgnoreCase)||Regex.IsMatch(ja,@"[A-Za-z]{4,}|[（(](笑|泣|怒|微笑|悲|照|得意|小声)[^）)]*[）)]"))throw new InvalidDataException("日语台词包含控制信息或未转换的外语词。");
            total+=ja.Length;if(total>8000)throw new InvalidDataException("回复超出长度预算。");
            result.Add(new(result.Count,ja,zh,emotion));
        }
        var facts=new List<FactCandidate>();
        if(root.TryGetProperty("memories",out var memories)&&memories.ValueKind==JsonValueKind.Array)
            foreach(var f in memories.EnumerateArray().Take(8))
                if(f.TryGetProperty("key",out var k)&&f.TryGetProperty("text",out var t)&&f.TryGetProperty("evidence",out var e))facts.Add(new(k.GetString()??"",t.GetString()??"",e.GetString()??"",f.TryGetProperty("kind",out var kind)?kind.GetString()??"UserFact":"UserFact"));
        return new(result,facts,work);
    }
    public async Task<string> TranscribeAsync(Settings settings,string key,byte[] wav,CancellationToken cancellation)
    {
        if(wav.Length<3200)throw new InvalidDataException("录音过短或没有有效音频。");
        if(string.IsNullOrWhiteSpace(settings.AsrBaseUrl)||string.IsNullOrWhiteSpace(settings.AsrModel))throw new InvalidOperationException("请先配置语音识别 API 地址与模型。");
        settings.Validate();using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using var form=new MultipartFormDataContent();var audio=new ByteArrayContent(wav);audio.Headers.ContentType=new("audio/wav");form.Add(audio,"file","recording.wav");form.Add(new StringContent(settings.AsrModel),"model");form.Add(new StringContent(settings.InputLanguage),"language");form.Add(new StringContent("json"),"response_format");
        using var request=new HttpRequestMessage(HttpMethod.Post,Endpoint(settings.AsrBaseUrl,"audio/transcriptions")){Content=form};if(key.Length>0)request.Headers.Authorization=new("Bearer",key);
        try{using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);if(!response.IsSuccessStatusCode)throw new ProviderException((int)response.StatusCode);
        var raw=await ReadBounded(response,deadline.Token);using var doc=JsonDocument.Parse(raw);var text=doc.RootElement.GetProperty("text").GetString()?.Trim();return !string.IsNullOrWhiteSpace(text)?text:throw new InvalidDataException("没有识别到语言内容。");}catch(OperationCanceledException) when(!cancellation.IsCancellationRequested){throw new TimeoutException("识别 API 请求超时。");}
    }
    private async Task<string> PostJsonAsync(Uri url,string key,object payload,int timeout,CancellationToken token)
    {
        for(int attempt=0;attempt<3;attempt++)
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
            using var request=new HttpRequestMessage(HttpMethod.Post,url){Content=new StringContent(JsonSerializer.Serialize(payload),Encoding.UTF8,"application/json")};if(key.Length>0)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
            HttpResponseMessage response;
            try {response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);}
            catch(OperationCanceledException) when(!token.IsCancellationRequested){throw new TimeoutException("API 请求超时。可调整控制台超时设置后重试。");}
            catch(HttpRequestException){throw new IOException("无法连接 API，请检查地址、网络和代理。");}
            using(response)
            {
                if(response.IsSuccessStatusCode)try{return await ReadBounded(response,deadline.Token);}catch(OperationCanceledException) when(!token.IsCancellationRequested){throw new TimeoutException("API 返回响应正文超时。");}
                if(attempt<2&&response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable)
                {var retry=response.Headers.RetryAfter?.Delta?.TotalSeconds??(attempt+1);await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(retry,.2,8)),token);continue;}
                throw new ProviderException((int)response.StatusCode);
            }
        }
        throw new IOException("API 重试次数已用尽。");
    }
    private static async Task<string> ReadBounded(HttpResponseMessage response,CancellationToken token)
    {
        await using var stream=await response.Content.ReadAsStreamAsync(token);using var buffer=new MemoryStream();var bytes=new byte[8192];int read;
        while((read=await stream.ReadAsync(bytes,token))>0){if(buffer.Length+read>1_048_576)throw new InvalidDataException("API 响应超过 1MB 安全上限。");buffer.Write(bytes,0,read);}return Encoding.UTF8.GetString(buffer.ToArray());
    }
    public void Dispose()=>client.Dispose();
}
public sealed class ProviderException(int status):Exception(status switch{401 or 403=>"API 身份验证失败，请检查密钥和权限。",429=>"API 限流，有限重试后仍不可用。",400=>"API 拒绝了请求参数或模型名称。",_=>$"API 返回 HTTP {status}；请检查供应商状态。"}) { public int StatusCode=>status; }
