namespace Atri.Core;

/// <summary>Persists only a paraphrased task and host-owned outcome, never tool or provider output.</summary>
public static class ChatWorkMemory
{
    public static void Record(MemoryStore memory,string session,string summary,string workId,string status,int artifactCount,string intent="")
    {
        ArgumentNullException.ThrowIfNull(memory);
        if(string.IsNullOrWhiteSpace(summary))throw new ArgumentException("任务简述不能为空。",nameof(summary));
        summary=new string(summary.Where(character=>!char.IsControl(character)).ToArray()).Trim();
        if(summary.Length==0)throw new ArgumentException("任务简述不能为空。",nameof(summary));
        summary=summary[..Math.Min(summary.Length,240)];
        var outcome=status switch
        {
            "succeeded"=>"执行成功",
            "failed"=>"执行失败",
            "cancelled"=>"已取消",
            "orphaned"=>"执行中断，需恢复",
            "not_started"=>"尚未执行",
            "queued"=>"已排队",
            "running"=>"正在执行",
            _=>"尚无可确认结果"
        };
        var count=Math.Clamp(artifactCount,0,100000);
        if(intent=="report")outcome="查询时任务状态为："+outcome;
        else if(intent=="cancel"&&status=="succeeded")outcome="请求停止时，原任务已经执行结束，未取消";
        var instruction="任务简记（概括，非用户原话）：用户委托“"+summary+"”。";
        var result="任务结果简记（宿主状态）："+summary+"；"+outcome+"；登记产物 "+count+" 项。";
        // Neither the executable instruction, synthesized report, provider text,
        // paths nor progress are accepted by this boundary.
        memory.AddMessage(session,"user",instruction);
        memory.AddMessage(session,"assistant",result);
        var identity=new string((workId??"").Where(character=>char.IsAsciiLetterOrDigit(character)||character is '_' or '-' or ':' or '.').Take(150).ToArray());
        memory.Remember(MemoryKind.Event,"work-outcome:"+identity+":"+Guid.NewGuid().ToString("N"),
            instruction+" "+result,"work-outcome",1,.4,session,[instruction,result]);
    }
}
