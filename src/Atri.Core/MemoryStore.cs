using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atri.Core;

/// <summary>Transactional local memory with explicit provenance, durable maintenance,
/// independent character knowledge and bounded, verified online backups.</summary>
public sealed partial class MemoryStore : IDisposable
{
    private const int SchemaVersion = 6;
    private const int MaximumTextLength = 32000;
    private const long MaximumImportBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly object _disposeGate = new();
    private readonly MemorySqlite _db;
    private readonly MemoryEmbedding? _embedding;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Task _worker;
    private bool _disposed;
    private volatile bool _closing;
    private static int _diagnosticSequence;
    private readonly int _diagnosticId = Interlocked.Increment(ref _diagnosticSequence);
    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public int MaximumBackupFiles { get; init; } = 5;
    public long MaximumBackupBytes { get; init; } = 256L * 1024 * 1024;
    public string? MaintenanceError { get; private set; }
    public string SemanticStatus => _embedding is null ? "词义投影回退（本地 E5 模型未部署）" : _embedding.Error ?? (_embedding.Ready ? "multilingual-e5-small CPU 已就绪" : "multilingual-e5-small CPU 将按需加载");

    public MemoryStore(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        RejectLinks(DataDirectory);
        Directory.CreateDirectory(DataDirectory);
        RecoverRestore(DataDirectory);
        DatabasePath = OwnedPath("memory.sqlite");
        _db = new MemorySqlite(DatabasePath);
        try
        {
            if (_db.Query("PRAGMA quick_check").Any(row => row[0] != "ok"))
                throw new IOException("记忆数据库完整性检查失败。原数据库未覆盖；可使用 MemoryStore.RestoreBackup 恢复已验证备份。");
            Migrate();
            _db.Execute("PRAGMA journal_mode=WAL");
            _db.Execute("PRAGMA synchronous=FULL");
            _db.Execute("PRAGMA foreign_keys=ON");
            _db.Execute("PRAGMA secure_delete=ON");
            EnsureHistoryModel();
            _db.Execute("UPDATE maintenance_tasks SET state='pending' WHERE state='running'");
            _db.Execute("UPDATE model_memory_jobs SET state='pending',ticket=NULL,attempts=MAX(0,attempts-1) WHERE state='running'");
        }
        catch { _db.Dispose(); _stopping.Dispose(); _signal.Dispose(); throw; }
        _embedding = MemoryEmbedding.Find(DataDirectory);
        // Maintenance owns one dedicated thread. Keep Monitor ownership entirely
        // synchronous; a cancelled embedding operation must unwind its gate on
        // that same thread before Dispose joins the worker.
        _worker = Task.Factory.StartNew(MaintenanceLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Signal();
    }

    private void Migrate()
    {
        int version = int.Parse(_db.Query("PRAGMA user_version")[0][0]!, CultureInfo.InvariantCulture);
        if (version > SchemaVersion) throw new IOException($"记忆数据库版本 {version} 比程序支持的 {SchemaVersion} 新；请勿降级覆盖数据。");
        _db.Transaction(() =>
        {
            if (version < 1)
            {
                _db.Execute("CREATE TABLE memories (id TEXT PRIMARY KEY,kind INTEGER NOT NULL,key TEXT NOT NULL,normalized_key TEXT NOT NULL,text TEXT NOT NULL,source TEXT NOT NULL,confidence REAL NOT NULL,importance REAL NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,session_id TEXT,evidence TEXT NOT NULL,expires_at TEXT,UNIQUE(kind,normalized_key))");
                _db.Execute("CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT,session_id TEXT NOT NULL,role TEXT NOT NULL,text TEXT NOT NULL,created_at TEXT NOT NULL)");
                _db.Execute("CREATE INDEX messages_session ON messages(session_id,id)");
                _db.Execute("CREATE TABLE memory_index (memory_id TEXT PRIMARY KEY REFERENCES memories(id) ON DELETE CASCADE,features TEXT NOT NULL)");
                _db.Execute("CREATE TABLE maintenance_tasks (id TEXT PRIMARY KEY,session_id TEXT NOT NULL UNIQUE,state TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,error TEXT,updated_at TEXT NOT NULL,next_at TEXT NOT NULL)");
                _db.Execute("PRAGMA user_version=1");
            }
            if (version < 2)
            {
                _db.Execute("CREATE TABLE memory_revisions (id INTEGER PRIMARY KEY AUTOINCREMENT,memory_id TEXT NOT NULL REFERENCES memories(id) ON DELETE CASCADE,old_text TEXT NOT NULL,new_text TEXT NOT NULL,source TEXT NOT NULL,created_at TEXT NOT NULL)");
                _db.Execute("CREATE INDEX memories_kind ON memories(kind,updated_at)");
                _db.Execute("PRAGMA user_version=2");
            }
            if (version < 3)
            {
                _db.Execute("CREATE TABLE memory_vectors (memory_id TEXT PRIMARY KEY REFERENCES memories(id) ON DELETE CASCADE,model TEXT NOT NULL,vector TEXT NOT NULL)");
                _db.Execute("PRAGMA user_version=3");
            }
            if (version < 4)
            {
                _db.Execute("CREATE TABLE session_metadata(session_id TEXT PRIMARY KEY,title TEXT NOT NULL,updated_at TEXT NOT NULL)");
                _db.Execute("PRAGMA user_version=4");
            }
            if (version < 5)
            {
                InitializeHistorySchema();
                InitializeConsolidationSchema();
                _db.Execute("PRAGMA user_version=5");
            }
            if(version<6)
            {
                InitializeModelMemorySchema();
                _db.Execute("PRAGMA user_version=6");
            }
            UpdateModelMemoryEndTrigger();
        });
    }

    public IReadOnlyList<MemoryRecord> List(MemoryKind? kind = null, int limit = 1000)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            limit = Math.Clamp(limit, 1, 100000);
            return kind.HasValue
                ? ReadRecords("SELECT * FROM memories WHERE kind=? ORDER BY updated_at DESC LIMIT ?", (int)kind.Value, limit)
                : ReadRecords("SELECT * FROM memories ORDER BY updated_at DESC LIMIT ?", limit);
        }
    }

    public IReadOnlyList<MemoryRecord> Search(string query, int limit = 8, MemoryKind? kind = null, CancellationToken cancellationToken = default, bool includeModelNotes = true)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        if (query.Length > MaximumTextLength) throw new ArgumentException("检索文本过长。", nameof(query));
        CancellationToken stopping;
        lock (_gate) { ThrowIfDisposed(); stopping = _stopping.Token; }
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
            linkedCancellation.Token.ThrowIfCancellationRequested();
            var features = MemorySearch.Features(query);
            IndexPendingVectors(32, linkedCancellation.Token,includeModelNotes:includeModelNotes);
            double[]? queryVector = QueryVector(query, linkedCancellation.Token);
            string normalized = MemorySearch.Normalize(query);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<string?[]> rows;
            lock (_gate)
            {
                ThrowIfDisposed();
                rows = kind.HasValue
                ? _db.Query("SELECT m.*,i.features,v.vector FROM memories m JOIN memory_index i ON i.memory_id=m.id LEFT JOIN memory_vectors v ON v.memory_id=m.id AND v.model=? WHERE kind=? AND (? OR m.source<>?)", MemoryEmbedding.ModelId, (int)kind.Value,includeModelNotes?1:0,ModelMemorySource)
                : _db.Query("SELECT m.*,i.features,v.vector FROM memories m JOIN memory_index i ON i.memory_id=m.id LEFT JOIN memory_vectors v ON v.memory_id=m.id AND v.model=? WHERE (? OR m.source<>?)", MemoryEmbedding.ModelId,includeModelNotes?1:0,ModelMemorySource);
            }
            var selected = rows.Select(row =>
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                MemoryRecord record = ParseRecord(row);
                var stored = JsonSerializer.Deserialize<Dictionary<string, double>>(row[13]!) ?? [];
                double similarity = MemorySearch.Similarity(features, stored);
                double semantic = queryVector is not null && row[14] is not null ? VectorSimilarity(queryVector, JsonSerializer.Deserialize<double[]>(row[14]!) ?? []) : 0;
                bool literal = MemorySearch.Normalize(record.Text + " " + record.Key).Contains(normalized, StringComparison.Ordinal);
                double freshness = Math.Exp(-Math.Max(0, (now - record.UpdatedAt).TotalDays) / 90);
                double score = (literal ? 1.2 : 0) + similarity * .6 + Math.Max(0, (semantic - .65) / .35) * 1.4 + .12 * record.Importance + .06 * freshness + .04 * record.Confidence;
                return (record, score, relevant: literal || similarity >= .10 || semantic >= .80);
            }).Where(item => item.relevant && (includeModelNotes || item.record.Source!=ModelMemorySource) && (item.record.ExpiresAt is null || item.record.ExpiresAt > now))
              .OrderByDescending(item => item.score).ThenByDescending(item => item.record.UpdatedAt)
              .Take(Math.Clamp(limit, 1, 100)).Select(item => item.record).ToArray();
        // A concurrent correction must not return a stale snapshot as evidence.
        lock (_gate)
        {
            ThrowIfDisposed();
            linkedCancellation.Token.ThrowIfCancellationRequested();
            return selected.Where(record => _db.Query("SELECT id FROM memories WHERE id=? AND text=? AND updated_at=?", record.Id, record.Text, Date(record.UpdatedAt)).Count > 0).ToArray();
        }
    }

    public Task<IReadOnlyList<MemoryRecord>> SearchAsync(string query, int limit = 8, MemoryKind? kind = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Search(query, limit, kind, cancellationToken), cancellationToken);

    private int IndexPendingVectors(int maximum, CancellationToken cancellationToken = default, bool background = false, bool includeModelNotes = true)
    {
        if (_embedding is null) return 0;
        List<string?[]> records;
        lock (_gate)
        {
            if (_disposed || _closing) return 0;
            records = _db.Query("SELECT m.id,m.key,m.text FROM memories m LEFT JOIN memory_vectors v ON v.memory_id=m.id AND v.model=? WHERE v.memory_id IS NULL AND (? OR m.source<>?) ORDER BY m.updated_at DESC LIMIT ?", MemoryEmbedding.ModelId,includeModelNotes?1:0,ModelMemorySource,maximum);
        }
        int indexed = 0;
        foreach (string?[][] batch in records.Chunk(background ? 2 : 16))
        {
            // E5 accepts 512 tokens. Cap transport before JSON escaping too, so
            // 16 manually edited long CJK records cannot exceed the IPC bound.
            double[][]? vectors = _embedding.Encode(batch.Select(row => Clip(row[1] + " " + row[2], 4000)).ToArray(), "passage", cancellationToken.CanBeCanceled ? cancellationToken : _stopping.Token, background);
            if (vectors is null) break;
            lock (_gate)
            {
                if (_disposed || _closing) return indexed;
                _db.Transaction(() =>
                {
                    for (int i = 0; i < batch.Length; i++)
                        if (_db.Query("SELECT id FROM memories WHERE id=? AND key=? AND text=?", batch[i][0], batch[i][1], batch[i][2]).Count > 0)
                            _db.Execute("INSERT INTO memory_vectors(memory_id,model,vector) VALUES(?,?,?) ON CONFLICT(memory_id) DO UPDATE SET model=excluded.model,vector=excluded.vector", batch[i][0], MemoryEmbedding.ModelId, JsonSerializer.Serialize(vectors[i]));
                });
            }
            indexed += vectors.Length;
        }
        return indexed;
    }

    private static double VectorSimilarity(double[] a, double[] b)
        => a.Length != 384 || b.Length != 384 ? 0 : a.Zip(b, (left, right) => left * right).Sum();

    public MemoryRecord Remember(MemoryKind kind, string key, string text, string source = "user", double confidence = 1,
        double importance = .5, string? sessionId = null, IEnumerable<string>? evidence = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return Upsert(new MemoryRecord(Guid.NewGuid().ToString("N"), kind, key, text, source, confidence, importance, now, now, sessionId, evidence?.ToArray()));
    }

    public MemoryRecord Upsert(MemoryRecord record)
    {
        Validate(record);
        lock (_gate)
        {
            ThrowIfDisposed();
            MemoryRecord? saved = null;
            _db.Transaction(() => saved = UpsertInternal(record));
            return saved!;
        }
    }

    private MemoryRecord UpsertInternal(MemoryRecord incoming)
    {
        string normalizedKey = MemorySearch.Normalize(incoming.Key);
        MemoryRecord? previous = ReadRecords("SELECT * FROM memories WHERE id=? OR (kind=? AND normalized_key=?) LIMIT 1", incoming.Id, (int)incoming.Kind, normalizedKey).FirstOrDefault();
        if(incoming.Source==ModelMemorySource||previous?.Source==ModelMemorySource)_modelNotesCache=null;
        if (previous is not null && previous.Text != incoming.Text && incoming.Confidence < previous.Confidence &&
            !IsExplicitSource(incoming.Source))
            throw new InvalidOperationException("低可信度推测不能覆盖现有事实；请由用户明确纠正。");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MemoryRecord record = incoming with
        {
            Id = previous?.Id ?? incoming.Id,
            Key = incoming.Key.Trim(), Text = incoming.Text.Trim(),
            CreatedAt = previous?.CreatedAt ?? incoming.CreatedAt,
            UpdatedAt = now,
            // A mechanical summary is a snapshot of its visible source window.
            // Accumulating prior IDs would grant a later privacy operation access
            // to messages no longer represented by the selected summary.
            Evidence = incoming.Kind == MemoryKind.SessionSummary
                ? (incoming.Evidence ?? []).Distinct(StringComparer.Ordinal).TakeLast(24).ToArray()
                : (previous?.Evidence ?? []).Concat(incoming.Evidence ?? []).Distinct(StringComparer.Ordinal).TakeLast(24).ToArray()
        };
        if (previous is not null && previous.Text != record.Text && record.Kind != MemoryKind.SessionSummary)
            _db.Execute("INSERT INTO memory_revisions(memory_id,old_text,new_text,source,created_at) VALUES(?,?,?,?,?)", previous.Id, previous.Text, record.Text, record.Source, Date(now));
        if (previous is not null)
            _db.Execute("DELETE FROM memory_revisions WHERE memory_id=? AND id NOT IN (SELECT id FROM memory_revisions WHERE memory_id=? ORDER BY id DESC LIMIT 50)", previous.Id, previous.Id);
        _db.Execute("INSERT INTO memories(id,kind,key,normalized_key,text,source,confidence,importance,created_at,updated_at,session_id,evidence,expires_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(id) DO UPDATE SET kind=excluded.kind,key=excluded.key,normalized_key=excluded.normalized_key,text=excluded.text,source=excluded.source,confidence=excluded.confidence,importance=excluded.importance,updated_at=excluded.updated_at,session_id=excluded.session_id,evidence=excluded.evidence,expires_at=excluded.expires_at",
            record.Id, (int)record.Kind, record.Key, normalizedKey, record.Text, record.Source, record.Confidence, record.Importance,
            Date(record.CreatedAt), Date(record.UpdatedAt), record.SessionId, JsonSerializer.Serialize(record.Evidence), record.ExpiresAt.HasValue ? Date(record.ExpiresAt.Value) : null);
        string projection = JsonSerializer.Serialize(MemorySearch.Features(record.Key + " " + record.Text));
        _db.Execute("INSERT INTO memory_index(memory_id,features) VALUES(?,?) ON CONFLICT(memory_id) DO UPDATE SET features=excluded.features", record.Id, projection);
        if (previous is null || previous.Text != record.Text || previous.Key != record.Key)
            _db.Execute("DELETE FROM memory_vectors WHERE memory_id=?", record.Id);
        return record;
    }

    public int Correct(string id, string replacement, string source = "user-correction")
    {
        if (string.IsNullOrWhiteSpace(replacement) || replacement.Length > MaximumTextLength) throw new ArgumentException("纠正后的记忆不能为空或过长。", nameof(replacement));
        lock (_gate)
        {
            ThrowIfDisposed();
            MemoryRecord? record = ReadRecords("SELECT * FROM memories WHERE id=?", id).FirstOrDefault();
            if (record is null) return 0;
            _db.Transaction(() => CorrectInternal(record, record.Key, replacement, replacement, source, record.SessionId));
            PurgeBackups();
            CheckpointPrivacyChanges();
            Signal();
            return 1;
        }
    }

    private MemoryRecord CorrectInternal(MemoryRecord previous, string key, string replacement, string evidence,
        string source, string? sessionId, string? oldLiteral = null)
    {
        MemoryRecord incoming = previous with { Key = key, Text = replacement, Source = source, Confidence = 1, SessionId = sessionId, Evidence = [evidence], ExpiresAt = null };
        Validate(incoming);
        RemoveReferences([previous], previous.Id, oldLiteral);
        // A changed semantic key must not leave the old topic in retrieval. If
        // this exact new key already exists, Upsert merges into that record.
        if (MemorySearch.Normalize(previous.Key) != MemorySearch.Normalize(key))
            _db.Execute("DELETE FROM memories WHERE id=?", previous.Id);
        MemoryRecord updated = UpsertInternal(incoming);
        // User correction must not keep superseded text or merged old evidence.
        _db.Execute("DELETE FROM memory_revisions WHERE memory_id=?", updated.Id);
        _db.Execute("UPDATE memories SET evidence=? WHERE id=?", JsonSerializer.Serialize(new[] { evidence }), updated.Id);
        return updated with { Evidence = [evidence] };
    }

    public int Forget(string idOrPhrase)
    {
        if (string.IsNullOrWhiteSpace(idOrPhrase)) return 0;
        if (idOrPhrase.Length > MaximumTextLength) throw new ArgumentException("遗忘请求过长。", nameof(idOrPhrase));
        lock (_gate)
        {
            ThrowIfDisposed();
            List<MemoryRecord> records = ReadRecords("SELECT * FROM memories WHERE id=?", idOrPhrase);
            bool byId = records.Count > 0;
            if (!byId)
            {
                string phrase = Like(idOrPhrase.Trim());
                records = ReadRecords("SELECT * FROM memories WHERE kind<>? AND (text LIKE ? ESCAPE '\\' OR key LIKE ? ESCAPE '\\' OR evidence LIKE ? ESCAPE '\\')", (int)MemoryKind.CharacterKnowledge, phrase, phrase, phrase);
            }
            _db.Transaction(() =>
            {
                RemoveReferences(records, null, byId ? null : idOrPhrase.Trim());
                foreach (MemoryRecord record in records) _db.Execute("DELETE FROM memories WHERE id=?", record.Id);
            });
            PurgeBackups();
            CheckpointPrivacyChanges();
            Signal();
            return records.Count;
        }
    }

    private void RemoveReferences(IEnumerable<MemoryRecord> records, string? preserveId, string? extraPhrase = null)
    {
        InvalidateModelWork();
        ClearQueryCache();
        // Sanitise source messages and invalidate derived summaries. Never leave stale
        // evidence available to retrieval after a user correction or forget operation.
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (MemoryRecord record in records)
        {
            // Only an explicitly selected, program-generated summary interprets
            // message:<id> as provenance. Ordinary evidence remains literal text,
            // and a phrase match must never expand into an entire source window.
            if (IsExtractiveSummary(record))
            {
                if (extraPhrase is null) ScrubSummarySources(record, preserveId);
                sessions.Add(record.SessionId!);
                _db.Execute("DELETE FROM memory_revisions WHERE memory_id=?", record.Id);
                continue;
            }
            terms.Add(record.Text);
            if (record.SessionId is not null) sessions.Add(record.SessionId);
            foreach (string item in record.Evidence ?? []) if (!string.IsNullOrWhiteSpace(item)) terms.Add(item);
            foreach (string?[] revision in _db.Query("SELECT old_text FROM memory_revisions WHERE memory_id=?", record.Id)) terms.Add(revision[0]!);
            _db.Execute("DELETE FROM memory_revisions WHERE memory_id=?", record.Id);
        }
        if (!string.IsNullOrWhiteSpace(extraPhrase)) terms.Add(extraPhrase);
        foreach (string term in terms.Where(t => t.Length > 0).OrderByDescending(t => t.Length))
        {
            string pattern = Like(term);
            // Delivered replies are stored as JSON, including Unicode escapes in
            // older history. Match both representations and remove the complete
            // assistant reply so its other language cannot retain the old fact.
            string encodedPattern = Like(JsonSerializer.Serialize(term)[1..^1]);
            foreach (string?[] message in _db.Query("SELECT id,session_id,text,role FROM messages WHERE text LIKE ? ESCAPE '\\' OR (role='assistant' AND text LIKE ? ESCAPE '\\')", pattern, encodedPattern))
            {
                string sanitized = message[3] == "assistant"
                    ? "[对已移除用户陈述的回复已同步移除]"
                    : Regex.Replace(message[2]!, Regex.Escape(term), "[已按用户要求移除]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                _db.Execute("UPDATE messages SET text=? WHERE id=?", sanitized, message[0]);
                sessions.Add(message[1]!);
                if (message[3] == "user")
                    foreach (string?[] reply in _db.Query("SELECT id,role FROM messages WHERE session_id=? AND id>? ORDER BY id", message[1], message[0]))
                    {
                        if (reply[1] == "user") break;
                        _db.Execute("UPDATE messages SET text='[对已移除用户陈述的回复已同步移除]' WHERE id=?", reply[0]);
                    }
            }
            var derived = ReadRecords("SELECT * FROM memories WHERE kind<>? AND (text LIKE ? ESCAPE '\\' OR evidence LIKE ? ESCAPE '\\') AND (? IS NULL OR id<>?)", (int)MemoryKind.CharacterKnowledge, pattern, pattern, preserveId, preserveId);
            foreach (MemoryRecord item in derived)
            {
                if (item.SessionId is not null) sessions.Add(item.SessionId);
                _db.Execute("DELETE FROM memories WHERE id=?", item.Id);
            }
            _db.Execute("DELETE FROM memory_revisions WHERE old_text LIKE ? ESCAPE '\\' OR new_text LIKE ? ESCAPE '\\'", pattern, pattern);
        }
        foreach (string session in sessions)
        {
            _db.Execute("DELETE FROM memories WHERE kind=? AND session_id=? AND (? IS NULL OR id<>?)", (int)MemoryKind.SessionSummary, session, preserveId, preserveId);
            EnqueueSummary(session);
        }
    }

    private static bool IsExtractiveSummary(MemoryRecord record) => record.Kind == MemoryKind.SessionSummary &&
        record.Source == "derived-extractive" && record.SessionId is not null && record.Key == "session:" + record.SessionId;

    private static string SessionSummaryText(IEnumerable<MemoryMessage> messages) => string.Join("\n", messages.Select(message =>
        $"[{(message.Role == "user" ? "用户明确说" : "亚托莉曾回应；不作为用户事实")}] {Clip(message.Text, 210)}"));

    private void ScrubSummarySources(MemoryRecord summary, string? preserveId)
    {
        var sources = new List<MemoryMessage>();
        foreach (string reference in summary.Evidence ?? [])
        {
            if (!reference.StartsWith("message:", StringComparison.Ordinal) ||
                !long.TryParse(reference.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0) continue;
            foreach (string?[] row in _db.Query("SELECT id,session_id,role,text,created_at FROM messages WHERE id=? AND session_id=?", id, summary.SessionId))
                sources.Add(new(id, row[1]!, row[2]!, row[3]!, ParseDate(row[4]!)));
        }
        var ordered = sources.DistinctBy(message => message.Id).OrderBy(message => message.Id).ToArray();
        MemoryMessage[]? visible = null;
        // Older databases accumulated up to 24 evidence IDs for a 16-message
        // summary. Verify the exact rendered snapshot before touching any source;
        // stale extra IDs and IDs belonging to another session confer no scope.
        for (int count = 1; count <= Math.Min(16, ordered.Length); count++)
        {
            var candidate = ordered.TakeLast(count).ToArray();
            if (SessionSummaryText(candidate) == summary.Text) { visible = candidate; break; }
        }
        if (visible is null) throw new InvalidOperationException("会话摘要的原文来源已变化，无法安全净化；请先重新整理会话后重试。");
        var literalFacts = ReadRecords("SELECT * FROM memories WHERE session_id=? AND source='user-explicit' AND kind<>? AND (? IS NULL OR id<>?)",
            summary.SessionId, (int)MemoryKind.CharacterKnowledge, preserveId, preserveId);
        foreach (MemoryMessage message in visible)
        {
            _db.Execute("UPDATE messages SET text=? WHERE id=? AND session_id=?",
                message.Role == "assistant" ? "[对已移除用户陈述的回复已同步移除]" : "[已按用户要求移除]", message.Id, summary.SessionId);
            // A response added after the selected snapshot can still repeat its
            // last user statement. Stop at the next user turn in the same session.
            if (message.Role == "user")
                foreach (string?[] reply in _db.Query("SELECT id,role FROM messages WHERE session_id=? AND id>? ORDER BY id", summary.SessionId, message.Id))
                {
                    if (reply[1] == "user") break;
                    _db.Execute("UPDATE messages SET text='[对已移除用户陈述的回复已同步移除]' WHERE id=?", reply[0]);
                }
            // The literal user extractor can retain a declaration shorter than
            // its complete source message. Remove those derived copies within
            // this session, while independent manual facts remain authoritative.
            if (message.Role == "user")
                foreach (MemoryRecord fact in literalFacts.Where(fact => (fact.Evidence ?? []).Any(evidence =>
                    evidence.Length >= 2 && message.Text.Contains(evidence, StringComparison.Ordinal))))
                    _db.Execute("DELETE FROM memories WHERE id=?", fact.Id);
            string pattern = Like(message.Text);
            foreach (MemoryRecord copy in ReadRecords("SELECT * FROM memories WHERE session_id=? AND kind<>? AND (? IS NULL OR id<>?) AND (text LIKE ? ESCAPE '\\' OR evidence LIKE ? ESCAPE '\\')",
                summary.SessionId, (int)MemoryKind.CharacterKnowledge, preserveId, preserveId, pattern, pattern))
                _db.Execute("DELETE FROM memories WHERE id=?", copy.Id);
            _db.Execute("DELETE FROM memory_revisions WHERE memory_id IN (SELECT id FROM memories WHERE session_id=?) AND (old_text LIKE ? ESCAPE '\\' OR new_text LIKE ? ESCAPE '\\')", summary.SessionId, pattern, pattern);
        }
        _db.Execute("DELETE FROM memory_revisions WHERE memory_id=?", summary.Id);
    }

    public MemoryMessage AddMessage(string sessionId, string role, string text)
    {
        ValidateSession(sessionId);
        if (role is not ("user" or "assistant")) throw new ArgumentException("只保存 user 和 assistant 对话。", nameof(role));
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength) throw new ArgumentException("对话不能为空或过长。", nameof(text));
        lock (_gate)
        {
            ThrowIfDisposed();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            long id = 0;
            _db.Transaction(() =>
            {
                _db.Execute("INSERT INTO messages(session_id,role,text,created_at) VALUES(?,?,?,?)", sessionId, role, text, Date(now));
                id = long.Parse(_db.Query("SELECT last_insert_rowid()")[0][0]!, CultureInfo.InvariantCulture);
                EnqueueSummary(sessionId);
            });
            Signal();
            return new MemoryMessage(id, sessionId, role, text, now);
        }
    }

    public IReadOnlyList<MemoryMessage> Recent(string sessionId, int limit = 20)
    {
        ValidateSession(sessionId);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _db.Query("SELECT id,session_id,role,text,created_at FROM messages WHERE session_id=? ORDER BY id DESC LIMIT ?", sessionId, Math.Clamp(limit, 1, 1000))
                .Select(row => new MemoryMessage(long.Parse(row[0]!, CultureInfo.InvariantCulture), row[1]!, row[2]!, row[3]!, ParseDate(row[4]!)))
                .Reverse().ToArray();
        }
    }

    public string Summary(string sessionId)
    {
        ValidateSession(sessionId);
        lock (_gate)
        {
            ThrowIfDisposed();
            return ReadRecords("SELECT * FROM memories WHERE kind=? AND session_id=? LIMIT 1", (int)MemoryKind.SessionSummary, sessionId).FirstOrDefault()?.Text ?? "";
        }
    }

    public IReadOnlyList<MemorySession> Sessions(int limit = 1000)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var sessions = _db.Query("SELECT m.session_id,MAX(m.created_at),COUNT(m.id),s.title,(SELECT text FROM messages first WHERE first.session_id=m.session_id AND first.role='user' ORDER BY first.id LIMIT 1) FROM messages m LEFT JOIN session_metadata s ON s.session_id=m.session_id GROUP BY m.session_id");
            var result = sessions.Select(row => new MemorySession(row[0]!, row[3] ?? Clip(new string((row[4] ?? "未命名会话").Select(character => char.IsControl(character) ? ' ' : character).ToArray()), 40), ParseDate(row[1]!), int.Parse(row[2]!, CultureInfo.InvariantCulture))).ToList();
            foreach (string?[] empty in _db.Query("SELECT s.session_id,s.title,s.updated_at FROM session_metadata s WHERE NOT EXISTS(SELECT 1 FROM messages m WHERE m.session_id=s.session_id)"))
                result.Add(new MemorySession(empty[0]!, empty[1]!, ParseDate(empty[2]!), 0));
            return result.OrderByDescending(session => session.LastAt).Take(Math.Clamp(limit, 1, 100000)).ToArray();
        }
    }

    public void RenameSession(string sessionId, string title)
    {
        ValidateSession(sessionId);
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120 || title.Any(char.IsControl)) throw new ArgumentException("会话名称须为 1–120 个字符且不含控制符。", nameof(title));
        lock (_gate)
        {
            ThrowIfDisposed();
            _db.Execute("INSERT INTO session_metadata(session_id,title,updated_at) VALUES(?,?,?) ON CONFLICT(session_id) DO UPDATE SET title=excluded.title,updated_at=excluded.updated_at", sessionId, title.Trim(), Date(DateTimeOffset.UtcNow));
        }
    }

    /// <summary>Deletes a session's conversation, summary and maintenance job.
    /// Stable long-term facts survive by default. The explicit optional flag also
    /// forgets non-canon records sourced from this session, including references.</summary>
    public int DeleteSession(string sessionId, bool forgetLinkedMemories = false)
    {
        ValidateSession(sessionId);
        lock (_gate)
        {
            ThrowIfDisposed();
            ClearQueryCache();
            int count = int.Parse(_db.Query("SELECT COUNT(*) FROM messages WHERE session_id=?", sessionId)[0][0]!, CultureInfo.InvariantCulture);
            _db.Transaction(() =>
            {
                InvalidateModelWork();
                if (forgetLinkedMemories)
                {
                    List<MemoryRecord> linked = ReadRecords("SELECT * FROM memories WHERE session_id=? AND kind<>?", sessionId, (int)MemoryKind.CharacterKnowledge);
                    RemoveReferences(linked, null);
                    foreach (MemoryRecord record in linked) _db.Execute("DELETE FROM memories WHERE id=?", record.Id);
                }
                _db.Execute("DELETE FROM memories WHERE session_id=? AND kind=?", sessionId, (int)MemoryKind.SessionSummary);
                _db.Execute("DELETE FROM memories WHERE session_id=? AND source=?",sessionId,ModelMemorySource);
                _db.Execute("DELETE FROM memories WHERE session_id=? AND kind=? AND key=?", sessionId, (int)MemoryKind.Relationship, "relationship:interaction:" + sessionId);
                _db.Execute("DELETE FROM messages WHERE session_id=?", sessionId);
                _db.Execute("DELETE FROM maintenance_tasks WHERE session_id=?", sessionId);
                _db.Execute("DELETE FROM model_memory_jobs WHERE session_id=?",sessionId);
                _db.Execute("DELETE FROM session_metadata WHERE session_id=?", sessionId);
            });
            PurgeBackups();
            CheckpointPrivacyChanges();
            Signal();
            return count;
        }
    }

    public IReadOnlyList<MemoryTask> Tasks()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _db.Query("SELECT id,session_id,state,attempts,error,updated_at FROM maintenance_tasks ORDER BY updated_at DESC LIMIT 256")
                .Select(row => new MemoryTask(row[0]!, row[1]!, row[2]!, int.Parse(row[3]!, CultureInfo.InvariantCulture), row[4], ParseDate(row[5]!))).ToArray();
        }
    }

    public IReadOnlyList<MemoryRevision> Revisions(string memoryId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _db.Query("SELECT memory_id,old_text,new_text,source,created_at FROM memory_revisions WHERE memory_id=? ORDER BY id DESC LIMIT 100", memoryId)
                .Select(row => new MemoryRevision(row[0]!, row[1]!, row[2]!, row[3]!, ParseDate(row[4]!))).ToArray();
        }
    }

    private void EnqueueSummary(string sessionId)
    {
        string now = Date(DateTimeOffset.UtcNow);
        // A session has one durable task. Repeated messages coalesce rather than
        // growing the queue, and settled tasks are bounded diagnostic history.
        _db.Execute("DELETE FROM maintenance_tasks WHERE state='complete' AND id NOT IN (SELECT id FROM maintenance_tasks WHERE state='complete' ORDER BY updated_at DESC LIMIT 127)");
        int outstanding = int.Parse(_db.Query("SELECT COUNT(*) FROM maintenance_tasks WHERE state IN ('pending','running','failed')")[0][0]!, CultureInfo.InvariantCulture);
        bool exists = _db.Query("SELECT id FROM maintenance_tasks WHERE session_id=?", sessionId).Count > 0;
        if (!exists && outstanding >= 256) throw new InvalidOperationException("记忆整理队列已达 256 个会话，请在诊断中处理失败任务后重试。");
        _db.Execute("INSERT INTO maintenance_tasks(id,session_id,state,attempts,error,updated_at,next_at) VALUES(?,?,'pending',0,NULL,?,?) ON CONFLICT(session_id) DO UPDATE SET state='pending',attempts=0,error=NULL,updated_at=excluded.updated_at,next_at=excluded.next_at", Guid.NewGuid().ToString("N"), sessionId, now, now);
    }

    private void RebuildSessionSummary(string sessionId)
    {
        MemoryRecord? previous = ReadRecords("SELECT * FROM memories WHERE kind=? AND session_id=? LIMIT 1", (int)MemoryKind.SessionSummary, sessionId).FirstOrDefault();
        // A user correction is authoritative under the same source/confidence
        // contract as other memories. Mechanical maintenance must not replace it
        // with lower-confidence excerpts or leave a permanently failing job.
        if (previous is not null && previous.Confidence == 1 && IsExplicitSource(previous.Source)) return;
        var messages = Recent(sessionId, 16);
        string summary = SessionSummaryText(messages);
        if (summary.Length == 0) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        UpsertInternal(new MemoryRecord(Guid.NewGuid().ToString("N"), MemoryKind.SessionSummary,
            "session:" + sessionId, summary, "derived-extractive", .8, .35, now, now, sessionId,
            messages.Select(message => "message:" + message.Id.ToString(CultureInfo.InvariantCulture)).ToArray()));
    }

    public int ProcessPendingTasks(int maximum = 8)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var jobs = _db.Query("SELECT id,session_id,attempts FROM maintenance_tasks WHERE state IN ('pending','failed') AND attempts<3 AND next_at<=? ORDER BY updated_at LIMIT ?", Date(DateTimeOffset.UtcNow), Math.Clamp(maximum, 1, 32));
            foreach (string?[] job in jobs)
            {
                int attempts = int.Parse(job[2]!, CultureInfo.InvariantCulture) + 1;
                _db.Execute("UPDATE maintenance_tasks SET state='running',attempts=?,updated_at=? WHERE id=?", attempts, Date(DateTimeOffset.UtcNow), job[0]);
                try
                {
                    _db.Transaction(() =>
                    {
                        RebuildSessionSummary(job[1]!);
                        _db.Execute("UPDATE maintenance_tasks SET state='complete',error=NULL,updated_at=? WHERE id=?", Date(DateTimeOffset.UtcNow), job[0]);
                    });
                    MaintenanceError = null;
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
                {
                    MaintenanceError = ex.Message;
                    _db.Execute("UPDATE maintenance_tasks SET state='failed',error=?,updated_at=?,next_at=? WHERE id=?", Clip(ex.Message, 500), Date(DateTimeOffset.UtcNow), Date(DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, attempts))), job[0]);
                }
            }
            return jobs.Count;
        }
    }

    public void RetryFailedTasks()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _db.Execute("UPDATE maintenance_tasks SET state='pending',attempts=0,error=NULL,next_at=? WHERE state='failed'", Date(DateTimeOffset.UtcNow));
            Signal();
        }
    }

    private void MaintenanceLoop()
    {
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        try
        {
            int delayMilliseconds = 2000;
            while (!_stopping.IsCancellationRequested)
            {
                _signal.Wait(TimeSpan.FromMilliseconds(delayMilliseconds), _stopping.Token);
                if (_stopping.IsCancellationRequested) break;
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    int progress = ProcessPendingTasks(2) + MaintenanceTick();
                    // Catch up an archive without a two-second pause per message.
                    // Rest for at least 3x the work duration (and at least 100ms)
                    // so CPU maintenance yields most of its time to interaction.
                    delayMilliseconds = progress > 0 ? (int)Math.Clamp(watch.Elapsed.TotalMilliseconds * 3, 100, 2000) : 2000;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { MaintenanceError = Clip(ex.Message, 500); delayMilliseconds = 2000; }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    private int MaintenanceTick()
    {
        int progress = ConsolidateBatch(64) + IndexHistoryBatch(8, _stopping.Token);
        if (_embedding?.Ready == true) progress += IndexPendingVectors(2, _stopping.Token, background: true);
        lock (_gate)
        {
            if (_disposed || _closing || _stopping.IsCancellationRequested) return progress;
            // Do not cold-start a large CPU model just because a store is opened
            // for list/backup/restore. Search starts it on demand.
            string backupDirectory = OwnedPath("backups");
            DateTime last = Directory.Exists(backupDirectory) ? Directory.EnumerateFiles(backupDirectory, "memory-*.sqlite").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max() : DateTime.MinValue;
            if (DateTime.UtcNow - last > TimeSpan.FromDays(1)) Backup();
        }
        return progress;
    }

    public void Export(string destination)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            string path = OwnedPath(destination);
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("记忆导出必须使用 .json 文件。");
            if (path.Equals(DatabasePath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("不能覆盖数据库。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var envelope = new MemoryExport(1, DateTimeOffset.UtcNow, ReadRecords("SELECT * FROM memories WHERE source<>?",ModelMemorySource).ToArray(),
                _db.Query("SELECT id,session_id,role,text,created_at FROM messages ORDER BY id").Select(row => new MemoryMessage(long.Parse(row[0]!, CultureInfo.InvariantCulture), row[1]!, row[2]!, row[3]!, ParseDate(row[4]!))).ToArray(), Sessions(100000).ToArray());
            string temp = path + ".writing";
            RejectLinks(temp);
            File.WriteAllText(temp, JsonSerializer.Serialize(envelope, Json));
            File.Move(temp, path, true);
        }
    }

    public int Import(string source)
    {
        string path = Path.GetFullPath(source);
        RejectLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumImportBytes) throw new IOException("导入文件不存在或超过 64 MiB。大型历史请使用数据库备份恢复。");
        MemoryExport envelope = JsonSerializer.Deserialize<MemoryExport>(File.ReadAllText(path)) ?? throw new InvalidDataException("记忆导入内容为空。");
        if (envelope.Schema != 1 || envelope.Records is null || envelope.Messages is null || envelope.Records.Length > 100000 || envelope.Messages.Length > 200000)
            throw new InvalidDataException("不支持的导出格式或记录数超过导入上限。");
        foreach (MemoryRecord record in envelope.Records) Validate(record);
        foreach (MemoryMessage message in envelope.Messages)
        {
            ValidateSession(message.SessionId);
            if (message.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(message.Text) || message.Text.Length > MaximumTextLength)
                throw new InvalidDataException("导入对话角色或长度不合法。");
        }
        if ((envelope.Sessions?.Length ?? 0) > 100000) throw new InvalidDataException("导入会话数超过上限。");
        foreach (MemorySession session in envelope.Sessions ?? [])
        {
            ValidateSession(session.SessionId);
            if (string.IsNullOrWhiteSpace(session.Title) || session.Title.Length > 120 || session.Title.Any(char.IsControl))
                throw new InvalidDataException("导入会话名称不合法。");
        }
        lock (_gate)
        {
            ThrowIfDisposed();
            Backup(); // A failed import always leaves both transaction and pre-import backup intact.
            _db.Transaction(() =>
            {
                InvalidateModelWork();
                foreach (MemoryRecord record in envelope.Records.Where(record=>record.Source!=ModelMemorySource &&
                    !(record.Kind == MemoryKind.SessionSummary && record.Source == "derived-extractive"))) UpsertInternal(record);
                foreach (MemoryMessage message in envelope.Messages)
                    if (_db.Query("SELECT id FROM messages WHERE session_id=? AND role=? AND text=? AND created_at=? LIMIT 1", message.SessionId, message.Role, message.Text, Date(message.CreatedAt)).Count == 0)
                        _db.Execute("INSERT INTO messages(session_id,role,text,created_at) VALUES(?,?,?,?)", message.SessionId, message.Role, message.Text, Date(message.CreatedAt));
                // Exported message IDs belong to the source database. Rebuild
                // mechanical summaries using locally assigned IDs so imported
                // provenance can never authorize scrubbing an unrelated message.
                foreach (string session in envelope.Records.Where(record => record.Kind == MemoryKind.SessionSummary &&
                    record.Source == "derived-extractive" && record.SessionId is not null).Select(record => record.SessionId!).Distinct(StringComparer.Ordinal))
                    RebuildSessionSummary(session);
                foreach (MemorySession session in envelope.Sessions ?? [])
                    _db.Execute("INSERT INTO session_metadata(session_id,title,updated_at) VALUES(?,?,?) ON CONFLICT(session_id) DO UPDATE SET title=excluded.title,updated_at=excluded.updated_at", session.SessionId, session.Title, Date(session.LastAt));
            });
            Signal(); // Imported history participates in incremental backfill too.
            return envelope.Records.Length;
        }
    }

    public string Backup()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            string directory = OwnedPath("backups");
            Directory.CreateDirectory(directory);
            string path = OwnedPath(Path.Combine("backups", "memory-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + ".sqlite"));
            _db.BackupTo(path);
            var info = new FileInfo(path);
            if (info.Length > MaximumBackupBytes)
            {
                DeleteBackupFiles(path);
                throw new IOException($"数据库超过备份容量上限 {MaximumBackupBytes / 1024 / 1024} MiB；旧备份已保留，请显式调整预算。");
            }
            var backups = OwnedBackups().OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            long bytes = 0;
            for (int i = 0; i < backups.Length; i++)
            {
                bytes += backups[i].Length;
                if (i >= Math.Max(1, MaximumBackupFiles) || bytes > MaximumBackupBytes) DeleteBackupFiles(backups[i].FullName);
            }
            return path;
        }
    }

    public static string RestoreBackup(string dataDirectory, string backupPath) => RestoreBackup(dataDirectory, backupPath, null);

    public static string RestoreBackup(string dataDirectory, string backupPath, Action<string>? progress)
    {
        // Call only after the live MemoryStore has been disposed. Open backups read
        // and preserve the original database plus WAL/SHM before replacing anything.
        string directory = Path.GetFullPath(dataDirectory);
        RejectLinks(directory);
        RecoverRestore(directory);
        string backup = Path.GetFullPath(backupPath);
        string allowed = Path.Combine(directory, "backups") + Path.DirectorySeparatorChar;
        if (!backup.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("只允许恢复当前记忆目录的备份。");
        RejectLinks(backup);
        if (!File.Exists(backup)) throw new FileNotFoundException("备份不存在；原数据库未修改。", backup);
        using (var check = new MemorySqlite(backup, readOnly: true))
        {
            ValidateRestoreDatabase(check);
        }
        string identifier = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
        string quarantine = Path.Combine(directory, "recovery", identifier);
        RejectLinks(quarantine);
        if (Directory.Exists(quarantine)) throw new IOException("恢复保护目录已存在；原数据库未修改。");
        string area = Path.Combine(directory, ".restore");
        RejectLinks(area);
        Directory.CreateDirectory(area);
        string prepared = Path.Combine(area, "pending.sqlite");
        string live = Path.Combine(directory, "memory.sqlite");
        try
        {
            // SQLite's online backup copies a coherent snapshot, including any
            // committed WAL pages. Finish all allocation before touching live.
            using (var source = new MemorySqlite(backup, readOnly: true)) source.BackupTo(prepared);
            using (var ready = new MemorySqlite(prepared))
            { ready.Execute("PRAGMA journal_mode=DELETE"); ValidateRestoreDatabase(ready); }
            using (var flush = new FileStream(prepared, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) flush.Flush(true);
            var originals = new List<RestoreFile>();
            foreach (string suffix in RestoreSuffixes)
            {
                RejectLinks(live + suffix);
                if (Directory.Exists(live + suffix)) throw new IOException("记忆文件位置被目录占用；原数据库未修改。");
                if (!File.Exists(live + suffix)) continue;
                using var probe = new FileStream(live + suffix, FileMode.Open, FileAccess.Read, FileShare.None);
                originals.Add(new(suffix, Convert.ToHexString(SHA256.HashData(probe)), probe.Length));
            }
            var journal = new RestoreJournal(1, false, identifier, originals.ToArray(), DescribeRestoreFile(prepared, ""));
            progress?.Invoke("prepared");
            WriteRestoreJournal(area, journal);
            Directory.CreateDirectory(quarantine);
            foreach (var original in journal.Originals)
            {
                VerifyRestoreFile(live + original.Suffix, original);
                File.Move(live + original.Suffix, Path.Combine(quarantine, "memory.sqlite" + original.Suffix));
                progress?.Invoke("moved" + original.Suffix);
            }
            File.Move(prepared, live);
            progress?.Invoke("installed");
            using (var installed = new MemorySqlite(live, readOnly: true)) ValidateRestoreDatabase(installed);
            WriteRestoreJournal(area, journal with { Committed = true });
            progress?.Invoke("committed");
            RecoverRestore(directory);
            return quarantine;
        }
        catch (Exception error)
        {
            try { RecoverRestore(directory); }
            catch (Exception recoveryError)
            { throw new IOException("记忆恢复尚未完成，已阻止打开空库。请解除占用或检查权限后重启；勿删除 .restore 与 recovery 保护目录。", new AggregateException(error, recoveryError)); }
            throw;
        }
    }

    private static readonly string[] RestoreSuffixes = ["", "-wal", "-shm", "-journal"];
    private sealed record RestoreFile(string Suffix, string Sha256, long Size);
    private sealed record RestoreJournal(int Version, bool Committed, string Recovery, RestoreFile[] Originals, RestoreFile Replacement);

    private static void ValidateRestoreDatabase(MemorySqlite database)
    {
        if (database.Query("PRAGMA quick_check").Any(row => row[0] != "ok")) throw new IOException("备份完整性检查失败；原数据库未修改。");
        int version = int.Parse(database.Query("PRAGMA user_version")[0][0]!, CultureInfo.InvariantCulture);
        if (version is < 1 or > SchemaVersion) throw new InvalidDataException("文件不是兼容的 ATRI 记忆备份；原数据库未修改。");
        (int Version, string Table, string Columns)[] required =
        [
            (1,"memories","id,kind,key,normalized_key,text,source,confidence,importance,created_at,updated_at,session_id,evidence,expires_at"),
            (1,"messages","id,session_id,role,text,created_at"), (1,"memory_index","memory_id,features"),
            (1,"maintenance_tasks","id,session_id,state,attempts,error,updated_at,next_at"),
            (2,"memory_revisions","id,memory_id,old_text,new_text,source,created_at"), (3,"memory_vectors","memory_id,model,vector"),
            (4,"session_metadata","session_id,title,updated_at"),
            (5,"history_state","message_id,revision,lexical_cursor,vector_cursor,chunk_count,lexical_ready,indexed_model"),
            (5,"history_chunks","id,message_id,ordinal,start,length,text"), (5,"history_terms","term,chunk_id,weight,sample"),
            (5,"history_vectors","chunk_id,model,vector"), (5,"history_lsh","model,code,chunk_id,sample"), (5,"history_metadata","key,value"),
            (5,"memory_consolidation_state","id,cursor,privacy_dirty,updated_at"), (5,"memory_consolidation_nodes","level,bucket,capsule"),
            (5,"memory_consolidation_dirty","bucket"), (5,"memory_consolidation_terms","term,bucket,weight"),
            (6,"model_memory_state","id,epoch"), (6,"model_memory_jobs","session_id,cursor,generation,state,attempts,ticket,error,next_at,updated_at")
        ];
        foreach (var table in required.Where(table => table.Version <= version))
            database.Query("SELECT " + table.Columns + " FROM " + table.Table + " LIMIT 0");
        if (database.Query("PRAGMA foreign_key_check").Count != 0 ||
            (version >= 6 && database.Query("SELECT id FROM model_memory_state WHERE id=1").Count != 1))
            throw new InvalidDataException("备份的关系或记忆任务状态不完整；原数据库未修改。");
    }

    // Called before sqlite3_open can create a missing database. The marker holds
    // only fixed suffixes, fingerprints and a strictly validated recovery name.
    private static void RecoverRestore(string directory)
    {
        string area = Path.Combine(directory, ".restore"); RejectLinks(area);
        if (!Directory.Exists(area)) return;
        string marker = Path.Combine(area, "transaction.json"); RejectLinks(marker);
        foreach (string file in Directory.EnumerateFileSystemEntries(area))
        {
            RejectLinks(file);
            if (Directory.Exists(file) || Path.GetFileName(file) is not ("transaction.json" or "transaction.json.tmp" or "pending.sqlite" or "pending.sqlite-wal" or "pending.sqlite-shm" or "pending.sqlite-journal"))
                throw new IOException("恢复准备目录含未知内容，已拒绝打开记忆数据库。");
        }
        if (File.Exists(marker))
        {
            if (new FileInfo(marker).Length > 65536) throw new InvalidDataException("记忆恢复标记过大。");
            var journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllText(marker)) ?? throw new InvalidDataException("记忆恢复标记为空。");
            if (journal.Version != 1 || journal.Recovery is null || !Regex.IsMatch(journal.Recovery, @"\A\d{8}-\d{6}-\d{7}\z", RegexOptions.CultureInvariant) ||
                journal.Originals is null || journal.Originals.Length > 4 || journal.Originals.Any(file => !ValidRestoreFile(file)) ||
                journal.Originals.Select(file => file.Suffix).Distinct().Count() != journal.Originals.Length || !ValidRestoreFile(journal.Replacement) || journal.Replacement.Suffix != "")
                throw new InvalidDataException("记忆恢复标记无效，已拒绝打开数据库。");
            string live = Path.Combine(directory, "memory.sqlite");
            string recovery = Path.Combine(directory, "recovery", journal.Recovery); RejectLinks(recovery);
            if (journal.Committed) VerifyRestoreFile(live, journal.Replacement);
            else
            {
                foreach (string suffix in RestoreSuffixes)
                {
                    string destination = live + suffix; string old = Path.Combine(recovery, "memory.sqlite" + suffix);
                    RejectLinks(destination); RejectLinks(old);
                    var original = journal.Originals.SingleOrDefault(file => file.Suffix == suffix);
                    if (original is not null && File.Exists(old))
                    {
                        VerifyRestoreFile(old, original);
                        if (File.Exists(destination))
                        {
                            if (suffix != "") throw new IOException("旧记忆恢复位置含未登记文件，已保留保护副本。");
                            VerifyRestoreFile(destination, journal.Replacement); File.Delete(destination);
                        }
                        File.Move(old, destination);
                    }
                    else if (original is not null) VerifyRestoreFile(destination, original);
                    else if (File.Exists(destination))
                    {
                        if (suffix != "") throw new IOException("旧记忆恢复位置含未登记附属文件。");
                        VerifyRestoreFile(destination, journal.Replacement); File.Delete(destination);
                    }
                }
                if (Directory.Exists(recovery) && !Directory.EnumerateFileSystemEntries(recovery).Any()) Directory.Delete(recovery);
            }
        }
        // Without a durable marker no live file has been moved. Only the exact
        // preparation files above can be removed; unrelated recovery is retained.
        foreach (string name in new[] { "pending.sqlite-wal", "pending.sqlite-shm", "pending.sqlite-journal", "pending.sqlite", "transaction.json.tmp", "transaction.json" })
        { string file = Path.Combine(area, name); RejectLinks(file); if (File.Exists(file)) File.Delete(file); }
        Directory.Delete(area);
    }

    private static bool ValidRestoreFile(RestoreFile? file) => file is not null && RestoreSuffixes.Contains(file.Suffix) && file.Size >= 0 && file.Sha256 is not null && Regex.IsMatch(file.Sha256, @"\A[A-Fa-f0-9]{64}\z", RegexOptions.CultureInvariant);
    private static RestoreFile DescribeRestoreFile(string path, string suffix)
    { RejectLinks(path); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return new(suffix, Convert.ToHexString(SHA256.HashData(stream)), stream.Length); }
    private static void VerifyRestoreFile(string path, RestoreFile expected)
    { var actual = DescribeRestoreFile(path, expected.Suffix); if (actual.Size != expected.Size || !actual.Sha256.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("记忆恢复文件与登记指纹不一致；已保留保护副本并停止恢复。"); }
    private static void WriteRestoreJournal(string area, RestoreJournal journal)
    {
        string temp = Path.Combine(area, "transaction.json.tmp"); RejectLinks(temp);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, journal); stream.Flush(true); }
        string marker = Path.Combine(area, "transaction.json"); RejectLinks(marker); File.Move(temp, marker, true);
    }

    private IEnumerable<FileInfo> OwnedBackups()
    {
        string directory = OwnedPath("backups");
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "memory-*.sqlite", SearchOption.TopDirectoryOnly)
            .Where(file => BackupNamePattern().IsMatch(Path.GetFileName(file)))
            .Select(file => { RejectLinks(file); return new FileInfo(file); }).ToArray();
    }

    private void DeleteBackupFiles(string mainPath)
    {
        string path = OwnedPath(mainPath);
        if (!BackupNamePattern().IsMatch(Path.GetFileName(path))) throw new InvalidDataException("备份清理文件名不属于受管范围。");
        foreach (string suffix in RestoreSuffixes)
        { string file = OwnedPath(path + suffix); if (File.Exists(file)) File.Delete(file); }
    }

    private void PurgeBackups()
    {
        // Privacy edits invalidate old snapshots too. Explicit user exports cannot
        // be recalled; their location and responsibility are documented separately.
        string backups = OwnedPath("backups");
        if (Directory.Exists(backups))
            foreach (string file in Directory.EnumerateFiles(backups, "memory-*.sqlite*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                // Include exact managed sidecars even if an earlier purge or
                // interrupted retention pass already removed their main file.
                if (RestoreSuffixes.Any(suffix => suffix.Length == 0
                    ? BackupNamePattern().IsMatch(name)
                    : name.EndsWith(suffix, StringComparison.Ordinal) && BackupNamePattern().IsMatch(name[..^suffix.Length])))
                { RejectLinks(file); File.Delete(file); }
            }
        string recovery = OwnedPath("recovery");
        if (Directory.Exists(recovery))
            foreach (string file in Directory.EnumerateFiles(recovery, "memory.sqlite*", SearchOption.AllDirectories))
                if (RestoreSuffixes.Any(suffix => Path.GetFileName(file) == "memory.sqlite" + suffix))
                { RejectLinks(file); File.Delete(file); }
    }

    private void CheckpointPrivacyChanges()
    {
        _db.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        _db.Execute("VACUUM");
    }

    private static void Validate(MemoryRecord record)
    {
        if (record is null || !Enum.IsDefined(record.Kind) || string.IsNullOrWhiteSpace(record.Id) || record.Id.Length > 128 ||
            string.IsNullOrWhiteSpace(record.Key) || record.Key.Length > 200 || string.IsNullOrWhiteSpace(record.Text) || record.Text.Length > MaximumTextLength ||
            string.IsNullOrWhiteSpace(record.Source) || record.Source.Length > 256 || !double.IsFinite(record.Confidence) || record.Confidence is < 0 or > 1 ||
            !double.IsFinite(record.Importance) || record.Importance is < 0 or > 1 || (record.Evidence?.Length ?? 0) > 100 ||
            (record.Evidence?.Any(evidence => evidence is null || evidence.Length > MaximumTextLength) ?? false))
            throw new ArgumentException("记忆类型、内容、来源、证据或可信度不合法。");
        if (record.SessionId is not null) ValidateSession(record.SessionId);
        if (record.Kind is MemoryKind.UserFact or MemoryKind.Preference or MemoryKind.Relationship && !IsExplicitSource(record.Source))
            throw new ArgumentException("用户事实、偏好与关系状态须来自用户明确陈述、用户编辑或经确认的导入；模型推测不能直接写入。");
    }

    private static bool IsExplicitSource(string source) => source.Equals("user", StringComparison.OrdinalIgnoreCase) || source.StartsWith("user-", StringComparison.OrdinalIgnoreCase) || source.StartsWith("import-confirmed", StringComparison.OrdinalIgnoreCase);
    private static void ValidateSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 128 || sessionId.Any(char.IsControl)) throw new ArgumentException("会话 ID 不合法。", nameof(sessionId));
    }

    private List<MemoryRecord> ReadRecords(string sql, params object?[] args) => _db.Query(sql, args).Select(ParseRecord).ToList();
    private static MemoryRecord ParseRecord(string?[] row) => new(row[0]!, (MemoryKind)int.Parse(row[1]!, CultureInfo.InvariantCulture), row[2]!, row[4]!, row[5]!,
        double.Parse(row[6]!, CultureInfo.InvariantCulture), double.Parse(row[7]!, CultureInfo.InvariantCulture), ParseDate(row[8]!), ParseDate(row[9]!), row[10],
        JsonSerializer.Deserialize<string[]>(row[11]!) ?? [], row[12] is null ? null : ParseDate(row[12]!));
    private static string Date(DateTimeOffset date) => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string date) => DateTimeOffset.Parse(date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Like(string value) => "%" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    private string OwnedPath(string path)
    {
        string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(DataDirectory, path));
        if (!full.StartsWith(DataDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("记忆写入与清理只允许在受管记忆目录内进行。");
        RejectLinks(full);
        return full;
    }

    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("记忆路径不允许符号链接、联接点或其他重解析点。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private void Signal() { if (_signal.CurrentCount == 0) try { _signal.Release(); } catch (SemaphoreFullException) { } }
    private void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(_disposed || _closing, this); }
    public void Dispose()
    {
        TraceDispose("enter");
        lock (_disposeGate)
        {
            if (_disposed) return;
            _closing = true;
            TraceDispose("cancel begin");
            _stopping.Cancel();
            TraceDispose("cancel done; join worker");
            try { _worker.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            TraceDispose("worker joined; acquire database gate");
            lock (_gate)
            {
                _disposed = true;
                TraceDispose("database checkpoint");
                try { _db.Execute("PRAGMA wal_checkpoint(TRUNCATE)"); } finally { _db.Dispose(); }
                TraceDispose("database closed; release E5 lease");
                _embedding?.Dispose();
                TraceDispose("E5 lease released");
                _signal.Dispose();
                _stopping.Dispose();
                TraceDispose("complete");
            }
        }
    }

    private void TraceDispose(string stage)
    {
        if (Environment.GetEnvironmentVariable("ATRI_MEMORY_TRACE") == "1")
            Console.WriteLine($"MemoryStore {_diagnosticId} thread {Environment.CurrentManagedThreadId}: {stage}");
    }

    private sealed record MemoryExport(int Schema, DateTimeOffset ExportedAt, MemoryRecord[] Records, MemoryMessage[] Messages, MemorySession[]? Sessions = null);
    [GeneratedRegex(@"^memory-\d{8}-\d{6}-\d{7}\.sqlite$", RegexOptions.CultureInvariant)] private static partial Regex BackupNamePattern();
}
