using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atri.Core;

public sealed record FactoryResetFile(string Path,string Sha256,long Size);

/// <summary>Exact-file, restartable reset. The caller must first stop every state
/// owner and dispose MemoryStore; this class never terminates other processes.</summary>
public static class FactoryReset
{
    private const string Area="data/factory-reset";
    private const string Marker=Area+"/transaction.json";
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    private static readonly string[] DatabaseSuffixes=["","-wal","-shm","-journal"];
    private static readonly string[] ManagedTemporary=["asr","python","gradio","embedding","capability-browser"];
    // These are the current verified source assets, not a used database or a
    // count inferred from whatever happens to be on disk. A deliberate source
    // release must update these fingerprints together with its verification.
    private const string PersonaSha256="BCAC3849E8F0357AEAFA4D10CBD1D0C3DAB97ED7C353F5786889675E08C9B4AA";
    private const string KnowledgeSha256="4533A66337B5EF7321ECCBA02503EE49519B3D553FAFD2E2F60076C0E50EF2DE";
    private sealed record Baseline(FactoryResetFile[] Sources,Dictionary<string,string> Entries);
    private sealed record Journal(int Version,string State,FactoryResetFile[] Files,FactoryResetFile[] Replacements);

    public static void ValidateBaseline(AppPaths paths)=>ReadBaseline(paths);
    public static bool HasPending(AppPaths paths)=>File.Exists(Safe(paths,Marker));

    public static async Task<bool> AwaitStartupForResetAsync(AppPaths paths,MemoryStore memory,Task startup,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        Exception? previousFailure=null;
        try{await startup.WaitAsync(token);}
        catch(Exception error) when(!token.IsCancellationRequested&&RecoverableStartupFailure(startup.Exception??error)){previousFailure=error;}
        if(previousFailure is null)return false;
        token.ThrowIfCancellationRequested();
        // A failed Task.WhenAll has still drained both original startup owners.
        // Retry only the actual context dependency against the trusted sources;
        // current voice health/model validation remains the caller's next step.
        var baseline=ReadBaseline(paths);
        await Task.Run(()=>
        {
            new ContextBuilder(paths,memory).RegisterKnowledge(token);token.ThrowIfCancellationRequested();
            var current=memory.List(MemoryKind.CharacterKnowledge,100000).ToDictionary(record=>record.Key,StringComparer.Ordinal);
            foreach(var expected in baseline.Entries)
            {
                if(!current.TryGetValue(expected.Key,out var record)||record.Source!="original-script")
                    throw new InvalidDataException("启动失败后的初始知识未能恢复；未清除原数据。");
                using var actual=JsonDocument.Parse(record.Text);using var source=JsonDocument.Parse(expected.Value);
                if(!JsonElement.DeepEquals(actual.RootElement,source.RootElement))
                    throw new InvalidDataException("启动失败后的初始知识与可信基线不一致；未清除原数据。");
            }
            VerifySources(paths,baseline);
        },token);
        paths.Log("startup-recovery","恢复出厂前已重新核验初始知识；历史启动错误类型="+previousFailure.GetType().Name);
        return true;
    }
    private static bool RecoverableStartupFailure(Exception error)=>error switch
    {
        AggregateException aggregate=>aggregate.Flatten().InnerExceptions.Count>0&&aggregate.Flatten().InnerExceptions.All(RecoverableStartupFailure),
        IOException or UnauthorizedAccessException or JsonException or TimeoutException=>true,
        _=>false
    };

    public static Settings Execute(AppPaths paths,Settings settings,IReadOnlyList<FactoryResetFile> workFiles,Action<string>? progress=null)
    {
        ArgumentNullException.ThrowIfNull(workFiles);
        using var resetLock=Acquire(paths);
        if(HasPending(paths))throw new InvalidOperationException("存在未完成的恢复出厂事务；请先恢复该事务再重试。");
        CleanOrphanPreparation(paths);
        var baseline=ReadBaseline(paths);settings.Validate();
        var clean=JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))??throw new InvalidDataException("无法准备运行配置；原数据未修改。");
        clean.UserName="";clean.LastSessionId=Guid.NewGuid().ToString("N");clean.Validate();
        var files=CollectFiles(paths,workFiles);
        foreach(var file in files)ProbeExclusive(paths,file.Path);
        Journal? journal=null;
        try
        {
            var pending=Safe(paths,Area+"/pending");Directory.CreateDirectory(pending);
            using(var memory=new MemoryStore(pending))
            {
                new ContextBuilder(paths,memory).RegisterKnowledge();
                var canon=memory.List(MemoryKind.CharacterKnowledge,100000);
                if(canon.Count!=baseline.Entries.Count||memory.List(limit:100000).Count!=canon.Count||memory.Sessions().Count!=0)
                    throw new InvalidDataException("初始记忆准备不完整；原数据未修改。");
            }
            // Install a standalone database, without live WAL/SHM ownership.
            // MemoryStore re-enables its normal WAL mode on the next open.
            using(var standalone=new MemorySqlite(Safe(paths,Area+"/pending/memory.sqlite")))
            {standalone.Execute("PRAGMA wal_checkpoint(TRUNCATE)");standalone.Execute("PRAGMA journal_mode=DELETE");}
            ValidatePreparedDatabase(paths,Area+"/pending/memory.sqlite",baseline);
            FlushFile(paths,Area+"/pending/memory.sqlite");
            DurableWrite(paths,Area+"/pending/settings.json",JsonSerializer.SerializeToUtf8Bytes(clean,Json));
            var replacements=new[]{Describe(paths,Area+"/pending/memory.sqlite") with{Path="data/memory/memory.sqlite"},Describe(paths,Area+"/pending/settings.json") with{Path="data/config/settings.json"}};
            journal=new(1,"prepared",files,replacements);WriteJournal(paths,journal);progress?.Invoke("prepared");
            VerifySources(paths,baseline);
            foreach(var file in files){VerifyFile(paths,file);ProbeExclusive(paths,file.Path);}
            journal=journal with{State="switching"};WriteJournal(paths,journal);progress?.Invoke("switching");
            Directory.CreateDirectory(Safe(paths,Area+"/old"));
            for(int index=0;index<files.Length;index++)
            {
                VerifyFile(paths,files[index]);
                File.Move(Safe(paths,files[index].Path),Safe(paths,Old(index)));
            }
            foreach(var replacement in replacements)
            {
                var source=Pending(replacement.Path);VerifyFile(paths,replacement with{Path=source});
                var destination=Safe(paths,replacement.Path);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(Safe(paths,source),destination);
            }
            progress?.Invoke("installed");
            foreach(var replacement in replacements)VerifyFile(paths,replacement);
            ValidatePreparedDatabase(paths,"data/memory/memory.sqlite",baseline);
            VerifySources(paths,baseline);
            journal=journal with{State="committed"};WriteJournal(paths,journal);
            progress?.Invoke("committed");progress?.Invoke("cleanup");
            FinishCommitted(paths,journal);
            return clean;
        }
        catch(Exception error)
        {
            // Read the durable marker, rather than an in-memory state that may
            // have advanced just before its atomic write failed.
            try
            {
                if(HasPending(paths))
                {
                    var persisted=ReadJournal(paths);
                    if(persisted.State=="committed")throw new IOException("新状态已提交，但旧数据清理尚未完成。请保持应用停用并重试恢复清理；不会重新装载旧记忆。",error);
                    RollBack(paths,persisted);
                }
                else CleanOrphanPreparation(paths);
            }
            catch(Exception recoveryError)
            {
                if(recoveryError.InnerException==error)throw;
                throw new IOException("恢复出厂未完成，事务标记已保留。请解除文件占用或权限问题后重新启动恢复；请勿手动删除事务目录。",new AggregateException(error,recoveryError));
            }
            throw new IOException("恢复出厂未提交，已回退原状态。请检查初始资源、文件占用与目录权限后重试。",error);
        }
    }

    // true means a previously committed reset has finished cleanup; false means
    // no transaction existed, or an uncommitted transaction was rolled back.
    public static bool RecoverPending(AppPaths paths)
    {
        using var resetLock=Acquire(paths);
        if(!HasPending(paths)){CleanOrphanPreparation(paths);return false;}
        var journal=ReadJournal(paths);
        if(journal.State=="committed"){FinishCommitted(paths,journal);return true;}
        RollBack(paths,journal);return false;
    }

    private static Baseline ReadBaseline(AppPaths paths)
    {
        try{return ReadBaselineCore(paths);}
        catch(Exception error) when(error is JsonException or KeyNotFoundException or InvalidOperationException)
        {throw new InvalidDataException("初始人格或知识资料缺少必要字段、格式错误或版本不兼容；请修复可信初始资源后重试，原交互数据未修改。",error);}
    }
    private static Baseline ReadBaselineCore(AppPaths paths)
    {
        var personaFile=Describe(paths,"assets/derived/persona.json");var knowledgeFile=Describe(paths,"assets/derived/knowledge.json");
        if(!personaFile.Sha256.Equals(PersonaSha256,StringComparison.OrdinalIgnoreCase)||!knowledgeFile.Sha256.Equals(KnowledgeSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("初始人格或知识资料与当前版本经过验证的完整基线不一致；请修复可信初始资源后重试，原交互数据未修改。");
        if(personaFile.Size>256*1024||knowledgeFile.Size>4*1024*1024)throw new InvalidDataException("初始人格或知识资料超出安全读取上限。");
        var personaText=File.ReadAllText(Safe(paths,personaFile.Path));
        if(personaText.Length>16000)throw new InvalidDataException("初始人格超出当前上下文加载上限。");
        using var persona=JsonDocument.Parse(personaText);using var knowledge=JsonDocument.Parse(File.ReadAllBytes(Safe(paths,knowledgeFile.Path)));
        var p=persona.RootElement;var k=knowledge.RootElement;
        if(p.GetProperty("schemaVersion").GetInt32()!=1||Text(p,"id")!="atri"||Text(p.GetProperty("timeline"),"id")!="game_early_common"||Text(p,"systemPrompt").Length==0||k.GetProperty("schemaVersion").GetInt32()!=1||Text(k,"timelineId")!="game_early_common")
            throw new InvalidDataException("初始人格或知识版本、角色身份、剧情时期不兼容；原数据保留。");
        var entries=new Dictionary<string,string>(StringComparer.Ordinal);var identifiers=new HashSet<string>(StringComparer.Ordinal);
        foreach(var entry in k.GetProperty("entries").EnumerateArray())
        {
            var id=Text(entry,"id");var timeline=Text(entry,"timelineId");
            if(!Match(id,@"[A-Za-z0-9._-]{1,128}")||!identifiers.Add(id)||Text(entry,"text").Length==0||Text(entry,"source").Length==0||Text(entry,"kind").Length==0||!entry.GetProperty("canonical").GetBoolean())
                throw new InvalidDataException("初始知识存在重复标识或缺失来源、正文、类型。");
            var examples=entry.GetProperty("examples");if(examples.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("初始知识例句格式不正确。");
            foreach(var example in examples.EnumerateArray())
            {
                if(Text(example,"speaker")!="アトリ"||Text(example,"ja").Length==0||Text(example,"zh").Length==0||Text(example,"source").Length==0||Text(example,"timelineId")!=timeline)
                    throw new InvalidDataException("初始知识例句缺少亚托莉台词、对应翻译或时期来源。");
                if(example.TryGetProperty("voiceIds",out var voiceIds)&&(voiceIds.ValueKind!=JsonValueKind.Array||voiceIds.EnumerateArray().Any(voice=>voice.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(voice.GetString()))))
                    throw new InvalidDataException("初始知识的参考语音标识不正确。");
            }
            if(timeline=="game_early_common")entries.Add("canon:"+id,entry.GetRawText());
        }
        if(entries.Count==0)throw new InvalidDataException("没有可初始化的当前时期原作知识；原数据保留。");
        var baseline=new Baseline([personaFile,knowledgeFile],entries);VerifySources(paths,baseline);return baseline;
    }
    private static string Text(JsonElement element,string property)
    {
        if(element.ValueKind!=JsonValueKind.Object||!element.TryGetProperty(property,out var value)||value.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("初始资源的必要字符串字段缺失或不可读。");
        return value.GetString()!;
    }
    private static void VerifySources(AppPaths paths,Baseline baseline)
    {foreach(var source in baseline.Sources)VerifyFile(paths,source);}
    private static void ValidatePreparedDatabase(AppPaths paths,string relative,Baseline baseline)
    {
        using var database=new MemorySqlite(Safe(paths,relative),readOnly:true);
        if(database.Query("PRAGMA quick_check").Any(row=>row[0]!="ok")||database.Query("PRAGMA user_version")[0][0]!="6")
            throw new InvalidDataException("准备的初始记忆数据库完整性或版本校验失败。");
        var records=database.Query("SELECT kind,key,text,source FROM memories");
        if(records.Count!=baseline.Entries.Count)throw new InvalidDataException("准备的初始记忆数量与可信知识源不一致。");
        foreach(var row in records)
        {
            if(row[0]!=((int)MemoryKind.CharacterKnowledge).ToString(CultureInfo.InvariantCulture)||row[3]!="original-script"||!baseline.Entries.TryGetValue(row[1]!,out var expected))throw new InvalidDataException("准备的初始记忆混入了交互状态。");
            using var actual=JsonDocument.Parse(row[2]!);using var source=JsonDocument.Parse(expected);
            if(!JsonElement.DeepEquals(actual.RootElement,source.RootElement))throw new InvalidDataException("准备的初始知识与可信源内容不一致。");
        }
        foreach(var table in new[]{"messages","session_metadata","maintenance_tasks","model_memory_jobs","memory_revisions","history_state","history_chunks","history_terms","history_vectors","history_lsh","memory_consolidation_nodes","memory_consolidation_dirty","memory_consolidation_terms"})
            if(database.Query("SELECT COUNT(*) FROM "+table)[0][0]!="0")throw new InvalidDataException("准备的初始数据库仍含会话、任务或历史状态。");
        if(int.Parse(database.Query("SELECT COUNT(*) FROM memory_index")[0][0]!,CultureInfo.InvariantCulture)!=baseline.Entries.Count)throw new InvalidDataException("准备的初始知识索引不完整。");
    }

    private static FactoryResetFile[] CollectFiles(AppPaths paths,IReadOnlyList<FactoryResetFile> workFiles)
    {
        try{return CollectFilesCore(paths,workFiles);}
        catch(Exception error) when(error is JsonException or KeyNotFoundException or InvalidOperationException)
        {throw new InvalidDataException("受管使用状态的所有者登记或文件清单格式无效；请检查登记文件后重试，原数据未修改。",error);}
    }
    private static FactoryResetFile[] CollectFilesCore(AppPaths paths,IReadOnlyList<FactoryResetFile> workFiles)
    {
        var collected=new Dictionary<string,FactoryResetFile>(StringComparer.OrdinalIgnoreCase);
        void Add(string relative)
        {
            var file=Safe(paths,relative);
            if(Directory.Exists(file))throw new InvalidDataException("待清理文件位置被目录占用："+relative);
            if(File.Exists(file))collected.TryAdd(relative,Describe(paths,relative));
        }
        void StrictFlat(string directory,Func<string,bool> allowed)
        {
            var full=Safe(paths,directory);if(!Directory.Exists(full))return;
            foreach(var entry in Directory.EnumerateFileSystemEntries(full))
            {
                var relative=Relative(paths,entry);var attributes=Attributes(paths,relative);
                if((attributes&FileAttributes.Directory)!=0||!allowed(System.IO.Path.GetFileName(entry)))throw new InvalidDataException("受管目录存在来源不明的文件或目录，已拒绝清理："+relative);
                Add(relative);
            }
        }
        var memory=Safe(paths,"data/memory");
        foreach(var entry in Directory.EnumerateFileSystemEntries(memory))
        {
            var name=System.IO.Path.GetFileName(entry);var relative=Relative(paths,entry);var attributes=Attributes(paths,relative);
            if((attributes&FileAttributes.Directory)!=0)
            {
                if(name is not ("backups" or "exports" or "recovery"))throw new InvalidDataException("记忆目录存在未登记子目录，已拒绝清理："+relative);
            }
            else if(DatabaseSuffixes.Any(suffix=>name=="memory.sqlite"+suffix))Add(relative);
            else throw new InvalidDataException("记忆目录存在未登记文件，已拒绝清理："+relative);
        }
        StrictFlat("data/memory/backups",name=>Match(name,@"memory-\d{8}-\d{6}-\d{7}\.sqlite(?:-wal|-shm|-journal)?"));
        StrictFlat("data/memory/exports",name=>Match(name,@"memories-\d{8}-\d{6}\.json(?:\.writing)?"));
        var recovery=Safe(paths,"data/memory/recovery");
        if(Directory.Exists(recovery))foreach(var entry in Directory.EnumerateFileSystemEntries(recovery))
        {
            var relative=Relative(paths,entry);var attributes=Attributes(paths,relative);
            if((attributes&FileAttributes.Directory)==0||!Match(System.IO.Path.GetFileName(entry),@"\d{8}-\d{6}-\d{7}"))throw new InvalidDataException("记忆恢复目录存在未登记项目，已拒绝清理："+relative);
            StrictFlat(relative,name=>DatabaseSuffixes.Any(suffix=>name=="memory.sqlite"+suffix));
        }
        foreach(var name in new[]{"settings.json","settings.json.tmp","settings.json.invalid"})Add("data/config/"+name);
        StrictFlat("data/logs",name=>Match(name,@"atri\.log(?:\.[1-3])?"));
        foreach(var suffix in DatabaseSuffixes)Add("data/capabilities/work.sqlite"+suffix);
        StrictFlat("data/capabilities/vn",name=>Match(name,@"[A-Za-z0-9_-]{1,80}\.json(?:\.tmp)?"));
        var voice=Safe(paths,"data/temp/voice");
        if(Directory.Exists(voice))foreach(var entry in Directory.EnumerateFileSystemEntries(voice))
        {
            var relative=Relative(paths,entry);var attributes=Attributes(paths,relative);
            if((attributes&FileAttributes.Directory)==0)throw new InvalidDataException("临时语音目录存在未登记文件，已拒绝清理："+relative);
            var owner=Safe(paths,relative+"/.owner.json");
            if(!File.Exists(owner)||new FileInfo(owner).Length>65536)throw new InvalidDataException("临时语音缺少有效所有者登记，已拒绝清理："+relative);
            using var marker=JsonDocument.Parse(File.ReadAllBytes(owner));var value=marker.RootElement;
            if(Text(value,"kind")!="voice"||!value.TryGetProperty("pid",out var pid)||!pid.TryGetInt32(out var number)||number<=0)throw new InvalidDataException("临时语音所有者格式无效，已拒绝清理。");
            try{using var process=Process.GetProcessById(number);if(!process.HasExited)throw new IOException("语音所有者仍在运行；请停止语音组件后重试。");}catch(ArgumentException){}
            StrictFlat(relative,name=>name==".owner.json"||name.EndsWith(".wav",StringComparison.OrdinalIgnoreCase));
        }
        foreach(var name in ManagedTemporary)
            foreach(var relative in TreeFiles(paths,"data/temp/"+name))Add(relative);
        foreach(var input in workFiles)
        {
            if(input is null)throw new InvalidDataException("受管工作产物清单包含空项目。");
            var relative=Normalize(paths,input.Path,allowAbsolute:true);
            if(!IsWorkFile(relative))throw new InvalidDataException("工作产物不在登记的工作区文件范围内；已拒绝清理。");
            var expected=input with{Path=relative};ValidateDescription(expected);VerifyFile(paths,expected);
            if(!collected.TryAdd(relative,expected))throw new InvalidDataException("受管工作产物清单包含重复路径。");
        }
        if(collected.Count>100000)throw new InvalidDataException("受管文件数量超过安全上限，未开始清理。");
        return collected.Values.OrderBy(file=>file.Path,StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool AllowedOriginal(string path)=>
        Match(path,@"data/memory/memory\.sqlite(?:-wal|-shm|-journal)?")||
        Match(path,@"data/memory/backups/memory-\d{8}-\d{6}-\d{7}\.sqlite(?:-wal|-shm|-journal)?")||
        Match(path,@"data/memory/exports/memories-\d{8}-\d{6}\.json(?:\.writing)?")||
        Match(path,@"data/memory/recovery/\d{8}-\d{6}-\d{7}/memory\.sqlite(?:-wal|-shm|-journal)?")||
        Match(path,@"data/config/settings\.json(?:\.tmp|\.invalid)?")||
        Match(path,@"data/logs/atri\.log(?:\.[1-3])?")||
        Match(path,@"data/capabilities/work\.sqlite(?:-wal|-shm|-journal)?")||
        Match(path,@"data/capabilities/vn/[A-Za-z0-9_-]{1,80}\.json(?:\.tmp)?")||
        Match(path,@"data/temp/voice/[^/]+/(?:\.owner\.json|[^/]+\.wav)")||
        Match(path,@"data/temp/(?:asr|python|gradio|embedding|capability-browser)/.+")||IsWorkFile(path);
    private static bool IsWorkFile(string path)=>Match(path,@"data/workspaces/(?:[^/]+/)*(?:atri-work-attempt_[A-Za-z0-9_-]{1,128}\.json|browser-[a-fA-F0-9]{32}\.png)");
    private static bool Match(string value,string pattern)=>Regex.IsMatch(value,"\\A(?:"+pattern+")\\z",RegexOptions.CultureInvariant);
    private static string Old(int index)=>Area+"/old/"+index.ToString("D6",CultureInfo.InvariantCulture);
    private static string Pending(string destination)=>destination switch{"data/memory/memory.sqlite"=>Area+"/pending/memory.sqlite","data/config/settings.json"=>Area+"/pending/settings.json",_=>throw new InvalidDataException("事务替换目标不在固定范围内。")};

    private static FileStream Acquire(AppPaths paths)
    {
        var directory=Safe(paths,Area);Directory.CreateDirectory(directory);
        try{return new FileStream(Safe(paths,Area+"/.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException error){throw new IOException("已有恢复出厂操作正在执行，或事务锁被占用；请等待后重试。",error);}
    }
    private static void WriteJournal(AppPaths paths,Journal journal)
    {DurableWrite(paths,Marker+".tmp",JsonSerializer.SerializeToUtf8Bytes(journal,Json));File.Move(Safe(paths,Marker+".tmp"),Safe(paths,Marker),true);}
    private static Journal ReadJournal(AppPaths paths)
    {
        var marker=Safe(paths,Marker);if(new FileInfo(marker).Length>64L*1024*1024)throw new InvalidDataException("恢复事务标记过大；未修改任何目标文件。");
        Journal journal;
        try{journal=JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(marker))??throw new InvalidDataException("恢复事务标记不可读；请保留现场。");}
        catch(JsonException error){throw new InvalidDataException("恢复事务标记格式损坏；请保留事务目录，不要继续使用混合状态。",error);}
        if(journal.Version!=1||journal.State is not ("prepared" or "switching" or "committed")||journal.Files is null||journal.Replacements is null||journal.Files.Length>100000||journal.Replacements.Length!=2)
            throw new InvalidDataException("恢复事务版本或状态无效；请保留事务目录。");
        var unique=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in journal.Files)
        {
            ValidateDescription(file);if(Normalize(paths,file.Path)!=file.Path||!AllowedOriginal(file.Path)||!unique.Add(file.Path))throw new InvalidDataException("恢复事务含越界、重复或未登记路径；已拒绝操作。");
            Safe(paths,file.Path);
        }
        unique.Clear();
        foreach(var replacement in journal.Replacements)
        {
            ValidateDescription(replacement);Pending(replacement.Path);
            if(!unique.Add(replacement.Path))throw new InvalidDataException("恢复事务替换路径重复。");
            Safe(paths,replacement.Path);
        }
        CheckTransactionContents(paths,journal);return journal;
    }
    private static void CheckTransactionContents(AppPaths paths,Journal? journal)
    {
        foreach(var entry in Directory.EnumerateFileSystemEntries(Safe(paths,Area)))
        {
            var relative=Relative(paths,entry);var attributes=Attributes(paths,relative);var name=System.IO.Path.GetFileName(entry);
            if(name is "pending" or "old")
            {if((attributes&FileAttributes.Directory)==0)throw new InvalidDataException("恢复事务目录被文件占用。");}
            else if(name is not (".lock" or "transaction.json" or "transaction.json.tmp")||(attributes&FileAttributes.Directory)!=0)throw new InvalidDataException("恢复事务目录存在未登记项目；已停止操作。");
        }
        foreach(var relative in TreeFiles(paths,Area+"/old"))
        {
            var name=relative[(Area+"/old/").Length..];
            if(journal is null||name.Contains('/')||!int.TryParse(name,NumberStyles.None,CultureInfo.InvariantCulture,out var index)||index<0||index>=journal.Files.Length||name!=index.ToString("D6",CultureInfo.InvariantCulture))throw new InvalidDataException("旧状态暂存区存在未登记文件；请保留事务目录。");
        }
        foreach(var relative in TreeFiles(paths,Area+"/pending"))
        {
            var name=relative[(Area+"/pending/").Length..];
            if(name is not ("memory.sqlite" or "memory.sqlite-wal" or "memory.sqlite-shm" or "memory.sqlite-journal" or "settings.json")&&!Match(name,@"backups/memory-\d{8}-\d{6}-\d{7}\.sqlite"))throw new InvalidDataException("新状态暂存区存在未登记文件；请保留事务目录。");
        }
    }
    private static void RollBack(AppPaths paths,Journal journal)
    {
        CheckTransactionContents(paths,journal);
        if(journal.State=="switching")
        {
            foreach(var replacement in journal.Replacements)
            {
                if(File.Exists(Safe(paths,Pending(replacement.Path))))continue;
                var destination=Safe(paths,replacement.Path);if(!File.Exists(destination))continue;
                int originalIndex=Array.FindIndex(journal.Files,file=>file.Path==replacement.Path);
                // A previous recovery may already have restored this original.
                if(originalIndex>=0&&!File.Exists(Safe(paths,Old(originalIndex)))){VerifyFile(paths,journal.Files[originalIndex]);continue;}
                VerifyFile(paths,replacement);ProbeExclusive(paths,replacement.Path);File.Delete(destination);
            }
            for(int index=0;index<journal.Files.Length;index++)
            {
                var original=journal.Files[index];var old=Safe(paths,Old(index));var destination=Safe(paths,original.Path);
                if(File.Exists(old))
                {
                    VerifyFile(paths,original with{Path=Old(index)});
                    if(File.Exists(destination))throw new IOException("回退目标出现新文件，已停止覆盖。请保持应用停用后重试恢复。");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Move(old,destination);
                }
                else VerifyFile(paths,original);
            }
        }
        else if(TreeFiles(paths,Area+"/old").Any())throw new InvalidDataException("尚未切换的事务意外包含旧状态；未删除任何原文件。");
        CleanTransaction(paths,journal);
    }
    private static void FinishCommitted(AppPaths paths,Journal journal)
    {
        CheckTransactionContents(paths,journal);
        foreach(var replacement in journal.Replacements)VerifyFile(paths,replacement);
        foreach(var original in journal.Files)
            if(!journal.Replacements.Any(file=>file.Path==original.Path)&&File.Exists(Safe(paths,original.Path)))throw new IOException("提交后检测到旧所有者重新写入；请停止相关组件后重试恢复清理。");
        for(int index=0;index<journal.Files.Length;index++)
        {
            var old=Old(index);if(!File.Exists(Safe(paths,old)))continue;
            VerifyFile(paths,journal.Files[index] with{Path=old});ProbeExclusive(paths,old);
        }
        for(int index=0;index<journal.Files.Length;index++)
        {var old=Old(index);if(File.Exists(Safe(paths,old)))File.Delete(Safe(paths,old));}
        // Remove only now-empty, application-owned parents of listed files.
        // Workspace directories and all unlisted content remain untouched.
        foreach(var original in journal.Files)
        {
            string? directory=System.IO.Path.GetDirectoryName(original.Path)?.Replace('\\','/');
            while(directory is not null&&(directory.StartsWith("data/temp/",StringComparison.Ordinal)||Match(directory,@"data/memory/recovery/\d{8}-\d{6}-\d{7}")))
            {
                var full=Safe(paths,directory);
                if(Directory.Exists(full))
                {if(Directory.EnumerateFileSystemEntries(full).Any())break;Directory.Delete(full);}
                directory=System.IO.Path.GetDirectoryName(directory)?.Replace('\\','/');
            }
        }
        CleanTransaction(paths,journal);
    }
    private static void CleanOrphanPreparation(AppPaths paths)
    {
        CheckTransactionContents(paths,null);
        if(TreeFiles(paths,Area+"/old").Any())throw new IOException("存在无法确认归属的旧状态暂存，已停止操作。");
        CleanTransaction(paths,null);
    }
    private static void CleanTransaction(AppPaths paths,Journal? journal)
    {
        CheckTransactionContents(paths,journal);
        if(TreeFiles(paths,Area+"/old").Any())throw new IOException("旧状态清理尚未完成；事务标记保留。");
        var prepared=TreeFiles(paths,Area+"/pending").ToArray();foreach(var relative in prepared)ProbeExclusive(paths,relative);
        foreach(var relative in prepared)File.Delete(Safe(paths,relative));
        RemoveEmptyTree(paths,Area+"/pending");RemoveEmptyTree(paths,Area+"/old");
        var temporary=Safe(paths,Marker+".tmp");if(File.Exists(temporary))File.Delete(temporary);
        // The marker is the final removal: cleanup failures remain recoverable.
        var marker=Safe(paths,Marker);if(File.Exists(marker))File.Delete(marker);
    }

    private static IEnumerable<string> TreeFiles(AppPaths paths,string directory)
    {
        var full=Safe(paths,directory);if(!Directory.Exists(full))yield break;
        foreach(var entry in Directory.EnumerateFileSystemEntries(full))
        {
            var relative=Relative(paths,entry);var attributes=Attributes(paths,relative);
            if((attributes&FileAttributes.Directory)!=0){foreach(var file in TreeFiles(paths,relative))yield return file;}
            else yield return relative;
        }
    }
    private static void RemoveEmptyTree(AppPaths paths,string directory)
    {
        var full=Safe(paths,directory);if(!Directory.Exists(full))return;
        foreach(var child in Directory.EnumerateDirectories(full))RemoveEmptyTree(paths,Relative(paths,child));
        if(Directory.EnumerateFileSystemEntries(full).Any())throw new IOException("事务暂存目录仍有文件，已保留恢复标记。");
        Directory.Delete(full);
    }
    private static void ValidateDescription(FactoryResetFile file)
    {
        if(file is null||string.IsNullOrWhiteSpace(file.Path)||file.Size<0||file.Sha256 is null||!Match(file.Sha256,@"[a-fA-F0-9]{64}"))throw new InvalidDataException("恢复文件清单的路径、大小或校验值无效。");
    }
    private static FactoryResetFile Describe(AppPaths paths,string relative)
    {
        var full=Safe(paths,relative);
        try{using var stream=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Read);return new(relative,Convert.ToHexString(SHA256.HashData(stream)),stream.Length);}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException)
        {throw new IOException("恢复所需文件不存在、不可读或被占用；请检查文件与权限后重试："+relative,error);}
    }
    private static void VerifyFile(AppPaths paths,FactoryResetFile expected)
    {
        ValidateDescription(expected);var actual=Describe(paths,expected.Path);
        if(actual.Size!=expected.Size||!actual.Sha256.Equals(expected.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("文件在预检后发生变化，已停止恢复操作："+expected.Path);
    }
    private static void ProbeExclusive(AppPaths paths,string relative)
    {
        var full=Safe(paths,relative);
        try{using var stream=new FileStream(full,FileMode.Open,FileAccess.ReadWrite,FileShare.None);}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException)
        {throw new IOException("无法独占访问恢复文件；请停止占用组件并检查写入权限后重试："+relative,error);}
    }
    private static void FlushFile(AppPaths paths,string relative)
    {using var stream=new FileStream(Safe(paths,relative),FileMode.Open,FileAccess.ReadWrite,FileShare.None);stream.Flush(true);}
    private static void DurableWrite(AppPaths paths,string relative,byte[] bytes)
    {
        var full=Safe(paths,relative);Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream=new FileStream(full,FileMode.Create,FileAccess.Write,FileShare.None);stream.Write(bytes);stream.Flush(true);
    }
    private static FileAttributes Attributes(AppPaths paths,string relative)=>File.GetAttributes(Safe(paths,relative));
    private static string Relative(AppPaths paths,string absolute)=>Normalize(paths,System.IO.Path.GetRelativePath(paths.Root,absolute));
    private static string Normalize(AppPaths paths,string path,bool allowAbsolute=false)
    {
        if(string.IsNullOrWhiteSpace(path)||(!allowAbsolute&&System.IO.Path.IsPathRooted(path)))throw new InvalidDataException("恢复路径必须是受控的根目录相对路径。");
        var full=System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path)?path:System.IO.Path.Combine(paths.Root,path));
        var relative=System.IO.Path.GetRelativePath(paths.Root,full).Replace('\\','/');
        if(relative=="."||relative.Split('/').Any(part=>part.Length==0||part is "." or ".."||part.EndsWith('.')||part.EndsWith(' ')||part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars())>=0))throw new InvalidDataException("恢复路径包含越界或不安全的名称。");
        if(!full.StartsWith(paths.Root.TrimEnd(System.IO.Path.DirectorySeparatorChar)+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("恢复路径超出当前实例。");
        return relative;
    }
    private static string Safe(AppPaths paths,string relative)
    {
        var normalized=Normalize(paths,relative);var full=System.IO.Path.GetFullPath(System.IO.Path.Combine(paths.Root,normalized));
        for(string? current=full;current is not null;current=System.IO.Path.GetDirectoryName(current))
        {
            try{if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("恢复路径不允许链接或目录联接。");}
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
        }
        return full;
    }
}
