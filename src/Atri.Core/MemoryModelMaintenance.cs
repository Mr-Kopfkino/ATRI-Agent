using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atri.Core;

public sealed partial class MemoryStore
{
    public const string ModelMemorySource = "derived-model-reviewed";
    private int _modelProcessing;
    private (MemoryRecord Record, Dictionary<string,double> Features, double Norm)[]? _modelNotesCache;
    private long _modelNotesEpoch=-1, _modelNotesDataVersion=-1;
    private sealed record DigestWork(string Session, string Ticket, long Cursor, long Epoch, long Generation, MemoryMessage[] Messages, string Previous);

    private void InitializeModelMemorySchema()
    {
        _db.Execute("CREATE TABLE model_memory_state(id INTEGER PRIMARY KEY CHECK(id=1),epoch INTEGER NOT NULL DEFAULT 0)");
        _db.Execute("INSERT INTO model_memory_state(id) VALUES(1)");
        _db.Execute("CREATE TABLE model_memory_jobs(session_id TEXT PRIMARY KEY,cursor INTEGER NOT NULL DEFAULT 0,generation INTEGER NOT NULL DEFAULT 0,state TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,ticket TEXT,error TEXT,next_at TEXT NOT NULL,updated_at TEXT NOT NULL)");
        _db.Execute("CREATE INDEX model_memory_pending ON model_memory_jobs(state,next_at)");
        const string invalidation = "UPDATE model_memory_state SET epoch=epoch+1 WHERE id=1; DELETE FROM memories WHERE source='derived-model-reviewed' AND session_id=OLD.session_id; UPDATE model_memory_jobs SET cursor=0,generation=generation+1,state='pending',attempts=0,ticket=NULL,error=NULL,next_at=strftime('%Y-%m-%dT%H:%M:%f+00:00','now') WHERE session_id=OLD.session_id; ";
        _db.Execute("CREATE TRIGGER model_memory_update AFTER UPDATE ON messages BEGIN "+invalidation+" END");
        _db.Execute("CREATE TRIGGER model_memory_delete AFTER DELETE ON messages BEGIN "+invalidation+" END");
    }

    private void UpdateModelMemoryEndTrigger()
    {
        // New turns invalidate in-flight snapshots, but do not request another
        // remote summary until the session is explicitly finished again.
        _db.Execute("DROP TRIGGER IF EXISTS model_memory_insert");
        _db.Execute("CREATE TRIGGER model_memory_insert AFTER INSERT ON messages BEGIN UPDATE model_memory_jobs SET generation=generation+1,state='dirty',attempts=0,ticket=NULL,error=NULL,next_at=NEW.created_at WHERE session_id=NEW.session_id; END");
    }

    // Called in the SAME transaction as every explicit privacy operation.
    private void InvalidateModelWork() => _db.Execute("UPDATE model_memory_state SET epoch=epoch+1 WHERE id=1");

    public void QueueModelMemory(string session)
    {
        ValidateSession(session);
        lock(_gate)
        {
            ThrowIfDisposed();
            if(_db.Query("SELECT id FROM messages WHERE session_id=? LIMIT 1",session).Count==0)return;
            string now=Date(DateTimeOffset.UtcNow);
            // A completed unchanged session is already processed. Repeated close
            // requests must not create duplicate notes or reset failed attempts.
            _db.Execute("INSERT OR IGNORE INTO model_memory_jobs(session_id,state,next_at,updated_at) VALUES(?,'pending',?,?)",session,now,now);
            _db.Execute("UPDATE model_memory_jobs SET state='pending',next_at=?,updated_at=? WHERE session_id=? AND state='dirty'",now,now,session);
        }
    }

    public string ModelMemoryStatus
    {
        get
        {
            lock(_gate)
            {
                ThrowIfDisposed();
                var counts=_db.Query("SELECT state,COUNT(*) FROM model_memory_jobs GROUP BY state").ToDictionary(r=>r[0]!,r=>r[1]!);
                return $"模型记忆：待结束 {counts.GetValueOrDefault("dirty","0")} · 待处理 {counts.GetValueOrDefault("pending","0")} · 处理中 {counts.GetValueOrDefault("running","0")} · 失败 {counts.GetValueOrDefault("failed","0")} · 已完成 {counts.GetValueOrDefault("complete","0")}";
            }
        }
    }

    public void RetryModelMemory()
    {
        lock(_gate){ThrowIfDisposed();_db.Execute("UPDATE model_memory_jobs SET state='pending',attempts=0,error=NULL,next_at=? WHERE state='failed'",Date(DateTimeOffset.UtcNow));}
    }

    public async Task<int> ProcessModelMemoryAsync(MemoryDigestGenerator generator, Settings settings, string key,
        CancellationToken cancellation=default, int maximumBatches=8)
    {
        if(Interlocked.CompareExchange(ref _modelProcessing,1,0)!=0)return 0;
        int completed=0;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,_stopping.Token);
        try
        {
            for(int batch=0;batch<maximumBatches;batch++)
            {
                linked.Token.ThrowIfCancellationRequested();
                DigestWork? work=TakeDigestWork();if(work is null)break;
                try
                {
                    var digest=await generator.GenerateAsync(settings,key,work.Messages,work.Previous,linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    if(CommitDigest(work,digest))completed++;
                }
                catch(OperationCanceledException) when(linked.IsCancellationRequested){ReleaseDigest(work,null);throw;}
                catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException or ProviderException or TimeoutException or HttpRequestException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OverflowException)
                {ReleaseDigest(work,ex.GetType().Name);break;}
            }
        }
        finally{Interlocked.Exchange(ref _modelProcessing,0);}
        return completed;
    }

    private DigestWork? TakeDigestWork()
    {
        lock(_gate)
        {
            ThrowIfDisposed();
            var jobs=_db.Query("SELECT session_id,cursor,generation FROM model_memory_jobs WHERE state IN ('pending','failed') AND attempts<3 AND next_at<=? ORDER BY updated_at LIMIT 1",Date(DateTimeOffset.UtcNow));
            if(jobs.Count==0)return null;
            var job=jobs[0];long cursor=long.Parse(job[1]!,CultureInfo.InvariantCulture);
            var overlap=_db.Query("SELECT id,session_id,role,text,created_at FROM messages WHERE session_id=? AND id<=? ORDER BY id DESC LIMIT 6",job[0],cursor).AsEnumerable().Reverse().ToList();
            // Small overlap carries antecedents across batch boundaries. It is
            // included in source validation and never advances the new cursor.
            while(overlap.Sum(row=>row[3]!.Length)>8000)overlap.RemoveAt(0);
            var rows=overlap.Concat(_db.Query("SELECT id,session_id,role,text,created_at FROM messages WHERE session_id=? AND id>? ORDER BY id LIMIT 32",job[0],cursor)).ToList();
            var messages=new List<MemoryMessage>();int size=0;
            foreach(var row in rows)
            {
                if(size+row[3]!.Length>64000&&messages.Count>0)break;
                messages.Add(new(long.Parse(row[0]!,CultureInfo.InvariantCulture),row[1]!,row[2]!,row[3]!,ParseDate(row[4]!)));size+=row[3]!.Length;
            }
            if(messages.Count==0||messages[^1].Id<=cursor){_db.Execute("UPDATE model_memory_jobs SET state='complete',error=NULL,updated_at=? WHERE session_id=?",Date(DateTimeOffset.UtcNow),job[0]);return null;}
            string ticket=Guid.NewGuid().ToString("N");
            long epoch=long.Parse(_db.Query("SELECT epoch FROM model_memory_state WHERE id=1")[0][0]!,CultureInfo.InvariantCulture);
            _db.Execute("UPDATE model_memory_jobs SET state='running',attempts=attempts+1,ticket=?,updated_at=? WHERE session_id=?",ticket,Date(DateTimeOffset.UtcNow),job[0]);
            string previous=string.Join("\n",ReadRecords("SELECT * FROM memories WHERE source=? AND session_id=? ORDER BY updated_at DESC LIMIT 12",ModelMemorySource,job[0]).Select(r=>Clip(r.Text,500)));
            return new(job[0]!,ticket,cursor,epoch,long.Parse(job[2]!,CultureInfo.InvariantCulture),messages.ToArray(),previous);
        }
    }

    private bool DigestIsCurrent(DigestWork work)
    {
        return _db.Query("SELECT session_id FROM model_memory_jobs WHERE session_id=? AND ticket=? AND generation=? AND cursor=?",work.Session,work.Ticket,work.Generation,work.Cursor).Count>0&&
            _db.Query("SELECT epoch FROM model_memory_state WHERE id=1")[0][0]==work.Epoch.ToString(CultureInfo.InvariantCulture)&&
            work.Messages.All(m=>_db.Query("SELECT id FROM messages WHERE id=? AND session_id=? AND role=? AND text=? AND created_at=?",m.Id,m.SessionId,m.Role,m.Text,Date(m.CreatedAt)).Count>0);
    }

    private bool CommitDigest(DigestWork work,ReviewedMemoryDigest digest)
    {
        lock(_gate)
        {
            ThrowIfDisposed();
            if(digest.Items.Any(item=>!MemoryDigestGenerator.ValidEvidence(item,work.Messages)))throw new InvalidDataException("模型记忆的原文证据已失效。");
            bool committed=false;
            _db.Transaction(()=>
            {
                if(!DigestIsCurrent(work)){ReleaseDigest(work,null);return;}
                foreach(var item in digest.Items)
                {
                    string fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{item.Category,item.Status,Text=item.Text.Trim(),Evidence=item.Evidence.OrderBy(e=>e.MessageId).ToArray()}))));
                    string key="digest:"+fingerprint;
                    if(_db.Query("SELECT id FROM memories WHERE key=? AND source=?",key,ModelMemorySource).Count>0)continue;
                    string text=$"[模型审查的历史摘录/{item.Category}/{item.Status}；不替代当前事实] {item.Text}\n"+
                        string.Join("\n",item.Evidence.Select(e=>$"[{work.Messages.First(m=>m.Id==e.MessageId).CreatedAt:yyyy-MM-dd} message:{e.MessageId}] {e.Quote}"));
                    DateTimeOffset now=DateTimeOffset.UtcNow;
                    UpsertInternal(new MemoryRecord(Guid.NewGuid().ToString("N"),MemoryKind.Event,key,text,ModelMemorySource,.8,.65,now,now,work.Session,item.Evidence.Select(e=>e.Quote).Distinct().ToArray()));
                }
                long cursor=work.Messages[^1].Id;
                bool more=_db.Query("SELECT id FROM messages WHERE session_id=? AND id>? LIMIT 1",work.Session,cursor).Count>0;
                _db.Execute("UPDATE model_memory_jobs SET cursor=?,state=?,attempts=0,ticket=NULL,error=NULL,next_at=?,updated_at=? WHERE session_id=?",cursor,more?"pending":"complete",Date(DateTimeOffset.UtcNow),Date(DateTimeOffset.UtcNow),work.Session);
                committed=true;
            });
            return committed;
        }
    }

    private void ReleaseDigest(DigestWork work,string? error)
    {
        lock(_gate)
        {
            if(_disposed||_closing)return;
            // A privacy change clears its ticket; an obsolete response has no
            // authority to put that newer job back into an older state.
            _db.Execute("UPDATE model_memory_jobs SET state=?,ticket=NULL,error=?,attempts=CASE WHEN ? IS NULL THEN MAX(0,attempts-1) ELSE attempts END,next_at=?,updated_at=? WHERE session_id=? AND ticket=?",
                error is null?"pending":"failed",error,error,Date(DateTimeOffset.UtcNow.AddSeconds(error is null?0:30)),Date(DateTimeOffset.UtcNow),work.Session,work.Ticket);
        }
    }

    public string ModelMemoryNotes(string query="",string? session=null,int budget=1800)
    {
        lock(_gate)
        {
            ThrowIfDisposed();
            long epoch=long.Parse(_db.Query("SELECT epoch FROM model_memory_state WHERE id=1")[0][0]!,CultureInfo.InvariantCulture);
            long dataVersion=long.Parse(_db.Query("PRAGMA data_version")[0][0]!,CultureInfo.InvariantCulture);
            if(_modelNotesCache is null||_modelNotesEpoch!=epoch||_modelNotesDataVersion!=dataVersion)
            {
                _modelNotesCache=_db.Query("SELECT m.*,i.features FROM memories m JOIN memory_index i ON i.memory_id=m.id WHERE m.source=?",ModelMemorySource).Select(row=>
                {
                    var stored=JsonSerializer.Deserialize<Dictionary<string,double>>(row[13]!)??[];
                    return (ParseRecord(row),stored,Math.Sqrt(stored.Values.Sum(value=>value*value)));
                }).ToArray();
                _modelNotesEpoch=epoch;_modelNotesDataVersion=dataVersion;
            }
            var features=MemorySearch.Features(query);double queryNorm=Math.Sqrt(features.Values.Sum(value=>value*value));
            double Score((MemoryRecord Record,Dictionary<string,double> Features,double Norm) entry)=>queryNorm==0||entry.Norm==0?0:features.Sum(pair=>pair.Value*entry.Features.GetValueOrDefault(pair.Key))/(queryNorm*entry.Norm);
            var output=new StringBuilder();
            foreach(var record in _modelNotesCache.Where(entry=>session is null||entry.Record.SessionId==session).OrderByDescending(Score).ThenByDescending(entry=>entry.Record.UpdatedAt).Take(12).Select(entry=>entry.Record))
            {
                // Keep the entire reviewed claim (including its negation/status),
                // but select short, explicitly labelled evidence excerpts. A
                // long supporting quote must not hide the complete memory item.
                string claim=record.Text.Split('\n')[0];
                string sources=string.Join(",",Regex.Matches(record.Text,@"message:\d+").Select(match=>match.Value).Distinct());
                var quotes=(record.Evidence??[]).OrderByDescending(quote=>string.IsNullOrWhiteSpace(query)?0:MemorySearch.Similarity(features,MemorySearch.Features(quote))).Take(2);
                string text=$"[会话 {record.SessionId}; memory:{record.Id}; {sources}] {claim}\n原句节选："+
                    string.Join(" / ",quotes.Select(quote=>ConsolidationQueryExcerpt(quote,query)))+"\n";
                if(output.Length+text.Length>budget)continue;
                output.Append(text);
            }
            return output.ToString();
        }
    }
}
