using System.Text.Json;

namespace Atri.Core;

/// <summary>Records delivered segments, never guesses which words were heard in a partial segment.</summary>
public sealed class ReplyDelivery(bool voiceEnabled)
{
    private readonly List<DialogueSegment> completed=[];
    private int? started;
    public void Start(DialogueSegment segment)=>started=segment.Order;
    public void Complete(DialogueSegment segment){if(completed.Any(s=>s.Order==segment.Order))throw new InvalidOperationException("片段重复完成。");completed.Add(segment);started=null;}
    public void DisplayAll(IEnumerable<DialogueSegment> segments){if(voiceEnabled)throw new InvalidOperationException("语音模式不能把显示内容当已听完。");completed.AddRange(segments);}
    public string Serialize(bool finished)=>JsonSerializer.Serialize(new{segments=completed,delivery=new{mode=voiceEnabled?"spoken":"displayed",finished,partialSegmentOrder=started,note=started is null?"":"该片段未完整播放，不推测已听到的字词；后续上下文只含完整完成片段。"}});
    public static string Readable(string text,string language)
    {
        try{using var document=JsonDocument.Parse(text);var root=document.RootElement;var segments=root.ValueKind==JsonValueKind.Array?root:root.GetProperty("segments");var entries=JsonSerializer.Deserialize<List<DialogueSegment>>(segments.GetRawText())??[];var result=string.Join("\n",entries.Select(s=>language=="zh"?s.Zh:s.Ja));if(root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("delivery",out var status)&&status.TryGetProperty("finished",out var complete)&&!complete.GetBoolean())result+="\n[回复已中断；未完整播放的片段不计入对话内容]";return result;}catch(JsonException){return text;}catch(KeyNotFoundException){return text;}catch(InvalidOperationException){return text;}
    }
}
