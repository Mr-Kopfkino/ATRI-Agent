using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Atri.Core;

/// <summary>An original, attributable delivered message excerpt; never a user fact inferred from an assistant reply.</summary>
public sealed record HistoryMemoryMatch(long MessageId, string SessionId, string Role, string Text,
    DateTimeOffset CreatedAt, int ChunkIndex, int Start, int Length, double Score, double SemanticScore, double LexicalScore);

public sealed record HistoryIndexStatus(long TotalMessages, long LexicallyIndexedMessages, long VectorIndexedMessages,
    long Chunks, long VectorChunks, long PendingVectorChunks, bool SemanticAvailable, bool SemanticReady)
{
    public long PendingMessages => Math.Max(0, TotalMessages - VectorIndexedMessages);
}

public sealed partial class MemoryStore
{
    private const int HistoryDimensions = 384;
    private const int HistoryHashTables = 8;
    private const int HistoryHashBits = 12;
    private const int HistoryLexicalBatch = 12;
    private const int HistoryVectorBatch = 2;
    private const int HistoryCandidates = 768;
    private static readonly sbyte[] HistoryProjection = CreateHistoryProjection();
    private int _historyIndexing;

    // Called within the v5 migration transaction. The triggers cover direct SQL
    // imports, privacy scrubs and session deletion, not only AddMessage callers.
    private void InitializeHistorySchema()
    {
        _db.Execute("CREATE TABLE IF NOT EXISTS history_state(message_id INTEGER PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,revision INTEGER NOT NULL DEFAULT 1,lexical_cursor INTEGER NOT NULL DEFAULT 0,vector_cursor INTEGER NOT NULL DEFAULT 0,chunk_count INTEGER NOT NULL DEFAULT 0,lexical_ready INTEGER NOT NULL DEFAULT 0,indexed_model TEXT)");
        _db.Execute("CREATE INDEX IF NOT EXISTS history_pending_lexical ON history_state(lexical_ready,message_id)");
        _db.Execute("CREATE INDEX IF NOT EXISTS history_pending_vector ON history_state(indexed_model,message_id)");
        _db.Execute("CREATE TABLE IF NOT EXISTS history_chunks(id INTEGER PRIMARY KEY AUTOINCREMENT,message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,ordinal INTEGER NOT NULL,start INTEGER NOT NULL,length INTEGER NOT NULL,text TEXT NOT NULL,UNIQUE(message_id,ordinal))");
        _db.Execute("CREATE TABLE IF NOT EXISTS history_terms(term TEXT NOT NULL,chunk_id INTEGER NOT NULL REFERENCES history_chunks(id) ON DELETE CASCADE,weight REAL NOT NULL,sample INTEGER NOT NULL,PRIMARY KEY(term,chunk_id)) WITHOUT ROWID");
        _db.Execute("CREATE INDEX IF NOT EXISTS history_terms_chunk ON history_terms(chunk_id)");
        _db.Execute("CREATE INDEX IF NOT EXISTS history_terms_weight ON history_terms(term,weight DESC,chunk_id)");
        _db.Execute("CREATE INDEX IF NOT EXISTS history_terms_sample ON history_terms(term,sample,chunk_id)");
        _db.Execute("CREATE TABLE IF NOT EXISTS history_vectors(chunk_id INTEGER PRIMARY KEY REFERENCES history_chunks(id) ON DELETE CASCADE,model TEXT NOT NULL,vector TEXT NOT NULL)");
        _db.Execute("CREATE TABLE IF NOT EXISTS history_lsh(model TEXT NOT NULL,code INTEGER NOT NULL,chunk_id INTEGER NOT NULL REFERENCES history_chunks(id) ON DELETE CASCADE,sample INTEGER NOT NULL,PRIMARY KEY(model,code,chunk_id)) WITHOUT ROWID");
        _db.Execute("CREATE INDEX IF NOT EXISTS history_lsh_chunk ON history_lsh(chunk_id)");
        _db.Execute("CREATE INDEX IF NOT EXISTS history_lsh_sample ON history_lsh(model,code,sample,chunk_id)");
        _db.Execute("CREATE TABLE IF NOT EXISTS history_metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL)");
        _db.Execute("CREATE TRIGGER IF NOT EXISTS history_message_insert AFTER INSERT ON messages BEGIN INSERT INTO history_state(message_id) VALUES(NEW.id); END");
        _db.Execute("CREATE TRIGGER IF NOT EXISTS history_message_update AFTER UPDATE OF text,role,session_id,created_at ON messages WHEN OLD.text<>NEW.text OR OLD.role<>NEW.role OR OLD.session_id<>NEW.session_id OR OLD.created_at<>NEW.created_at BEGIN DELETE FROM history_chunks WHERE message_id=OLD.id; UPDATE history_state SET revision=revision+1,lexical_cursor=0,vector_cursor=0,chunk_count=0,lexical_ready=0,indexed_model=NULL WHERE message_id=NEW.id; INSERT OR IGNORE INTO history_state(message_id) VALUES(NEW.id); END");
        _db.Execute("CREATE TRIGGER IF NOT EXISTS history_message_delete BEFORE DELETE ON messages BEGIN DELETE FROM history_chunks WHERE message_id=OLD.id; DELETE FROM history_state WHERE message_id=OLD.id; END");
        _db.Execute("INSERT OR IGNORE INTO history_state(message_id) SELECT id FROM messages");
        EnsureHistoryModelCore();
    }

    // Constructor calls this on every open, including an already-v5 database.
    // A deployed model replacement must not strand old non-null indexed_model
    // cursors or silently treat incompatible vectors as current.
    private void EnsureHistoryModel() => _db.Transaction(EnsureHistoryModelCore);

    private void EnsureHistoryModelCore()
    {
        string? previousModel = _db.Query("SELECT value FROM history_metadata WHERE key='model'").FirstOrDefault()?[0];
        if (previousModel != MemoryEmbedding.ModelId)
        {
            _db.Execute("DELETE FROM history_lsh");
            _db.Execute("DELETE FROM history_vectors");
            _db.Execute("UPDATE history_state SET vector_cursor=0,indexed_model=CASE WHEN lexical_ready=1 AND chunk_count=0 THEN ? ELSE NULL END", MemoryEmbedding.ModelId);
            _db.Execute("INSERT INTO history_metadata(key,value) VALUES('model',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", MemoryEmbedding.ModelId);
        }
    }

    public HistoryIndexStatus HistoryStatus
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                long total = CountHistory("SELECT count(*) FROM messages");
                long lexical = CountHistory("SELECT count(*) FROM history_state WHERE lexical_ready=1");
                long complete = CountHistory("SELECT count(*) FROM history_state WHERE indexed_model=?", MemoryEmbedding.ModelId);
                long chunks = CountHistory("SELECT count(*) FROM history_chunks");
                long vectors = CountHistory("SELECT count(*) FROM history_vectors WHERE model=?", MemoryEmbedding.ModelId);
                return new(total, lexical, complete, chunks, vectors, Math.Max(0, chunks - vectors), _embedding is not null, _embedding?.Ready == true);
            }
        }
    }

    private long CountHistory(string sql, params object?[] args) => long.Parse(_db.Query(sql, args)[0][0]!, CultureInfo.InvariantCulture);

    /// <summary>Bounded, resumable maintenance: at most 12 lexical chunks and 2 neural chunks.
    /// Returns chunk work units, not a claim that all pending messages are indexed.
    /// A list/backup-only store never cold-starts the model; a foreground search warms it.</summary>
    public int IndexHistoryBatch(int maxMessages = 8, CancellationToken cancellationToken = default)
    {
        // Manual catch-up and the maintenance worker can run concurrently. Skip
        // a duplicate indexer instead of blocking a caller behind CPU inference.
        if (Interlocked.CompareExchange(ref _historyIndexing, 1, 0) != 0) return 0;
        try { return IndexHistoryBatchCore(maxMessages, cancellationToken); }
        finally { Volatile.Write(ref _historyIndexing, 0); }
    }

    private int IndexHistoryBatchCore(int maxMessages, CancellationToken cancellationToken)
    {
        CancellationToken stopping;
        lock (_gate) { ThrowIfDisposed(); stopping = _stopping.Token; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        int progress = 0;
        HistorySource? source;
        lock (_gate)
        {
            ThrowIfDisposed();
            var row = _db.Query("SELECT m.id,m.session_id,m.role,m.text,m.created_at,s.revision,s.lexical_cursor FROM history_state s JOIN messages m ON m.id=s.message_id WHERE s.lexical_ready=0 ORDER BY s.message_id LIMIT 1").FirstOrDefault();
            source = row is null ? null : ReadHistorySource(row);
        }
        // Chunking and text feature extraction also happen outside the DB gate.
        if (source is not null)
        {
            string readable = HistoryMessageText(source.Role, source.Text);
            var chunks = SplitHistory(readable);
            int take = Math.Min(HistoryLexicalBatch, Math.Clamp(maxMessages, 1, 32) * 2);
            var prepared = chunks.Skip(source.Cursor).Take(take).Select(chunk => (chunk, terms: HistoryTerms(chunk.Text))).ToArray();
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfDisposed();
                bool committed = false;
                _db.Transaction(() =>
                {
                        // Revalidate after BEGIN IMMEDIATE as another process
                        // may own a store for this same database too.
                        if (!HistorySourceMatches(source)) return;
                        foreach (var (chunk, terms) in prepared)
                        {
                            _db.Execute("INSERT OR IGNORE INTO history_chunks(message_id,ordinal,start,length,text) VALUES(?,?,?,?,?)", source.Id, chunk.Ordinal, chunk.Start, chunk.Text.Length, chunk.Text);
                            long chunkId = CountHistory("SELECT id FROM history_chunks WHERE message_id=? AND ordinal=?", source.Id, chunk.Ordinal);
                            foreach (var term in terms)
                                _db.Execute("INSERT OR REPLACE INTO history_terms(term,chunk_id,weight,sample) VALUES(?,?,?,?)", term.Key, chunkId, term.Value, HistorySample(chunkId));
                        }
                        int cursor = source.Cursor + prepared.Length;
                        bool done = cursor >= chunks.Count;
                        _db.Execute("UPDATE history_state SET lexical_cursor=?,chunk_count=?,lexical_ready=?,indexed_model=CASE WHEN ?=0 THEN ? ELSE indexed_model END WHERE message_id=? AND revision=?", cursor, chunks.Count, done ? 1 : 0, chunks.Count, MemoryEmbedding.ModelId, source.Id, source.Revision);
                        committed = true;
                });
                if (committed) progress += prepared.Length == 0 ? 1 : prepared.Length;
            }
        }

        // Neural inference must never hold _gate. The durable cursor permits
        // interruption halfway through a very long message without losing its tail.
        if (_embedding?.Ready != true) return progress;
        HistorySource? vectorSource;
        string?[][] pending;
        lock (_gate)
        {
            ThrowIfDisposed();
            var row = _db.Query("SELECT m.id,m.session_id,m.role,m.text,m.created_at,s.revision,s.vector_cursor FROM history_state s JOIN messages m ON m.id=s.message_id WHERE s.indexed_model IS NULL AND s.vector_cursor<s.lexical_cursor ORDER BY s.message_id LIMIT 1").FirstOrDefault();
            vectorSource = row is null ? null : ReadHistorySource(row);
            pending = vectorSource is null ? [] : _db.Query("SELECT id,ordinal,text FROM history_chunks WHERE message_id=? AND ordinal>=? ORDER BY ordinal LIMIT ?", vectorSource.Id, vectorSource.Cursor, HistoryVectorBatch).ToArray();
        }
        if (vectorSource is null || pending.Length == 0) return progress;
        double[][]? vectors = _embedding.Encode(pending.Select(row => row[2]!).ToArray(), "passage", token, background: true);
        if (vectors is null) return progress; // Busy/unavailable is durable pending, never fake completion.
        var encoded = vectors.Select(vector => (packed: PackHistoryVector(vector), codes: HistoryCodes(vector))).ToArray();
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            bool committed = false;
            _db.Transaction(() =>
            {
                if (!HistorySourceMatches(vectorSource, vector: true)) return;
                for (int i = 0; i < pending.Length; i++)
                {
                    long id = long.Parse(pending[i][0]!, CultureInfo.InvariantCulture);
                    _db.Execute("INSERT INTO history_vectors(chunk_id,model,vector) VALUES(?,?,?) ON CONFLICT(chunk_id) DO UPDATE SET model=excluded.model,vector=excluded.vector", id, MemoryEmbedding.ModelId, encoded[i].packed);
                    _db.Execute("DELETE FROM history_lsh WHERE chunk_id=?", id);
                    foreach (int code in encoded[i].codes)
                        _db.Execute("INSERT INTO history_lsh(model,code,chunk_id,sample) VALUES(?,?,?,?)", MemoryEmbedding.ModelId, code, id, HistorySample(id));
                }
                int cursor = int.Parse(pending[^1][1]!, CultureInfo.InvariantCulture) + 1;
                _db.Execute("UPDATE history_state SET vector_cursor=?,indexed_model=CASE WHEN lexical_ready=1 AND chunk_count=? THEN ? ELSE NULL END WHERE message_id=? AND revision=?", cursor, cursor, MemoryEmbedding.ModelId, vectorSource.Id, vectorSource.Revision);
                committed = true;
            });
            return progress + (committed ? vectors.Length : 0);
        }
    }

    /// <summary>Indexed lexical + multi-probe LSH candidates, followed by exact
    /// cosine re-ranking. SQL returns at most 768 vectors, independent of archive size.
    /// Approximate semantic candidates trade exhaustive recall for bounded foreground work.</summary>
    public IReadOnlyList<HistoryMemoryMatch> SearchHistory(string query, int limit = 4,
        IReadOnlyCollection<long>? excludedMessageIds = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        if (query.Length > MaximumTextLength) throw new ArgumentException("检索文本过长。", nameof(query));
        CancellationToken stopping;
        lock (_gate) { ThrowIfDisposed(); stopping = _stopping.Token; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        // QueryVector is shared with structured-memory searches in the same turn.
        double[]? queryVector = QueryVector(query, token);
        var terms = HistoryTerms(query).OrderByDescending(term => term.Value).Take(24).ToArray();
        int pivot = HistoryQuerySample(query);
        var candidates = new Dictionary<long, double>();
        string?[][] rows;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (terms.Length > 0)
            {
                foreach (var candidate in HistoryLexicalCandidates(terms, pivot)) candidates[candidate.Key] = candidate.Value;
            }
            if (queryVector is not null)
            {
                // Exact retrieval for small archives avoids approximate misses
                // (and E5's clustered buckets) without an unbounded vector read.
                var small = _db.Query("SELECT chunk_id FROM history_vectors WHERE model=? ORDER BY chunk_id LIMIT 641", MemoryEmbedding.ModelId);
                var semanticCandidates = small.Count <= 640 ? small : HistoryApproximateCandidates(queryVector, pivot);
                foreach (var row in semanticCandidates)
                    candidates.TryAdd(long.Parse(row[0]!, CultureInfo.InvariantCulture), 0);
            }
            // A small recent supplement gives useful retrieval while a large
            // archive is still backfilling. It is not an unbounded archive scan.
            foreach (var row in _db.Query("SELECT id FROM history_chunks ORDER BY id DESC LIMIT 32"))
                candidates.TryAdd(long.Parse(row[0]!, CultureInfo.InvariantCulture), 0);
            long[] ids = candidates.Keys.Take(HistoryCandidates).ToArray();
            if (ids.Length == 0) return [];
            object?[] parameters = new object?[] { MemoryEmbedding.ModelId }.Concat(ids.Cast<object?>()).ToArray();
            rows = _db.Query($"SELECT c.id,c.message_id,m.session_id,m.role,c.text,m.created_at,c.ordinal,c.start,c.length,v.vector FROM history_chunks c JOIN messages m ON m.id=c.message_id LEFT JOIN history_vectors v ON v.chunk_id=c.id AND v.model=? WHERE c.id IN ({string.Join(",", ids.Select(_ => "?"))})", parameters).ToArray();
        }
        var excluded = excludedMessageIds?.ToHashSet() ?? [];
        string normalized = MemorySearch.Normalize(query);
        var ranked = new List<HistoryMemoryMatch>();
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            long messageId = long.Parse(row[1]!, CultureInfo.InvariantCulture);
            if (excluded.Contains(messageId)) continue;
            double lexical = candidates[long.Parse(row[0]!, CultureInfo.InvariantCulture)];
            double semantic = queryVector is not null && row[9] is not null ? HistoryCosine(queryVector, row[9]!) : 0;
            bool literal = MemorySearch.Normalize(row[4]!).Contains(normalized, StringComparison.Ordinal);
            if (!literal && lexical < .08 && semantic < .80) continue;
            var created = ParseDate(row[5]!);
            double freshness = Math.Exp(-Math.Max(0, (DateTimeOffset.UtcNow - created).TotalDays) / 180);
            double score = (literal ? 1.2 : 0) + lexical * .7 + Math.Max(0, (semantic - .65) / .35) * 1.5 + freshness * .03;
            ranked.Add(new(messageId, row[2]!, row[3]!, row[4]!, created,
                int.Parse(row[6]!, CultureInfo.InvariantCulture), int.Parse(row[7]!, CultureInfo.InvariantCulture), int.Parse(row[8]!, CultureInfo.InvariantCulture), score, semantic, lexical));
        }
        // Diversity prevents a single long message from consuming the context.
        var result = ranked.OrderByDescending(match => match.Score).ThenByDescending(match => match.CreatedAt)
            .DistinctBy(match => match.MessageId).Take(Math.Clamp(limit, 1, 12)).ToArray();
        // A concurrent forget/correction may commit after candidate reading. Do
        // not return snapshots whose message/chunk was invalidated meanwhile.
        lock (_gate)
        {
            ThrowIfDisposed();
            return result.Where(match => _db.Query("SELECT 1 FROM history_chunks c JOIN messages m ON m.id=c.message_id WHERE c.message_id=? AND c.ordinal=? AND c.text=? AND m.session_id=? AND m.role=? AND m.created_at=? LIMIT 1",
                match.MessageId, match.ChunkIndex, match.Text, match.SessionId, match.Role, Date(match.CreatedAt)).Count == 1).ToArray();
        }
    }

    private Dictionary<long, double> HistoryLexicalCandidates(KeyValuePair<string, double>[] terms, int pivot)
    {
        var statements = new List<string>();
        var args = new List<object?>();
        for (int index = 0; index < terms.Length; index++)
        {
            void Add(string body, bool sample = false)
            {
                statements.Add("SELECT ?,chunk_id,weight FROM (" + body + ")");
                args.Add(index); args.Add(terms[index].Key);
                if (sample) args.Add(pivot);
            }
            // Every posting read has an index-compatible LIMIT. Common words
            // cannot cause GROUP BY to walk millions of archive postings.
            Add("SELECT chunk_id,weight FROM history_terms INDEXED BY history_terms_weight WHERE term=? ORDER BY weight DESC,chunk_id LIMIT 16");
            Add("SELECT chunk_id,weight FROM history_terms WHERE term=? ORDER BY chunk_id LIMIT 8");
            Add("SELECT chunk_id,weight FROM history_terms WHERE term=? ORDER BY chunk_id DESC LIMIT 8");
            Add("SELECT chunk_id,weight FROM history_terms INDEXED BY history_terms_sample WHERE term=? AND sample>=? ORDER BY sample,chunk_id LIMIT 8", true);
            Add("SELECT chunk_id,weight FROM history_terms INDEXED BY history_terms_sample WHERE term=? AND sample<? ORDER BY sample DESC,chunk_id DESC LIMIT 8", true);
        }
        var scores = new Dictionary<long, double>();
        var seen = new HashSet<(int term, long chunk)>();
        foreach (var row in _db.Query(string.Join(" UNION ALL ", statements), args.ToArray()))
        {
            int term = int.Parse(row[0]!, CultureInfo.InvariantCulture);
            long chunk = long.Parse(row[1]!, CultureInfo.InvariantCulture);
            if (!seen.Add((term, chunk))) continue;
            scores[chunk] = scores.GetValueOrDefault(chunk) + terms[term].Value * double.Parse(row[2]!, CultureInfo.InvariantCulture);
        }
        return scores.OrderByDescending(item => item.Value).ThenBy(item => item.Key).Take(96).ToDictionary(item => item.Key, item => item.Value);
    }

    private List<string?[]> HistoryApproximateCandidates(double[] queryVector, int pivot)
    {
        var votes = new Dictionary<long, int>();
        // 632 probes, at most 16 indexed postings each. Batching avoids SQLite
        // variable/compound-select limits and bounds work even for dense buckets.
        foreach (int[] batch in HistoryProbes(queryVector).Chunk(128))
        {
            var statements = new List<string>();
            var args = new List<object?>();
            foreach (int code in batch)
            {
                statements.Add("SELECT chunk_id FROM (SELECT chunk_id FROM history_lsh INDEXED BY history_lsh_sample WHERE model=? AND code=? AND sample>=? ORDER BY sample,chunk_id LIMIT 8)");
                args.Add(MemoryEmbedding.ModelId); args.Add(code); args.Add(pivot);
                statements.Add("SELECT chunk_id FROM (SELECT chunk_id FROM history_lsh INDEXED BY history_lsh_sample WHERE model=? AND code=? AND sample<? ORDER BY sample DESC,chunk_id DESC LIMIT 8)");
                args.Add(MemoryEmbedding.ModelId); args.Add(code); args.Add(pivot);
            }
            foreach (var row in _db.Query(string.Join(" UNION ALL ", statements), args.ToArray()))
            {
                long id = long.Parse(row[0]!, CultureInfo.InvariantCulture);
                votes[id] = votes.GetValueOrDefault(id) + 1;
            }
        }
        return votes.OrderByDescending(item => item.Value).ThenBy(item => item.Key).Take(640)
            .Select(item => new string?[] { item.Key.ToString(CultureInfo.InvariantCulture) }).ToList();
    }

    private static int HistorySample(long id)
    {
        uint value = (uint)(id ^ (id >> 32));
        value ^= value >> 16; value *= 0x7feb352d; value ^= value >> 15; value *= 0x846ca68b; value ^= value >> 16;
        return (int)(value & int.MaxValue);
    }

    private static int HistoryQuerySample(string text)
    {
        uint hash = 2166136261;
        foreach (char value in text) { hash ^= value; hash *= 16777619; }
        return (int)(hash & int.MaxValue);
    }

    // Explicit hook for a caller changing derived history outside message SQL.
    // Normal message mutations are covered by the database triggers above.
    private void InvalidateHistoryMessage(long id)
    {
        _db.Execute("DELETE FROM history_chunks WHERE message_id=?", id);
        _db.Execute("UPDATE history_state SET revision=revision+1,lexical_cursor=0,vector_cursor=0,chunk_count=0,lexical_ready=0,indexed_model=NULL WHERE message_id=?", id);
    }

    private sealed record HistorySource(long Id, string Session, string Role, string Text, string Created, long Revision, int Cursor);
    private sealed record HistoryChunk(int Ordinal, int Start, string Text);
    private static HistorySource ReadHistorySource(string?[] row) => new(long.Parse(row[0]!, CultureInfo.InvariantCulture), row[1]!, row[2]!, row[3]!, row[4]!, long.Parse(row[5]!, CultureInfo.InvariantCulture), int.Parse(row[6]!, CultureInfo.InvariantCulture));

    private bool HistorySourceMatches(HistorySource source, bool vector = false) => _db.Query("SELECT 1 FROM messages m JOIN history_state s ON s.message_id=m.id WHERE m.id=? AND s.revision=? AND m.session_id=? AND m.role=? AND m.text=? AND m.created_at=? AND s." + (vector ? "vector_cursor" : "lexical_cursor") + "=? LIMIT 1",
        source.Id, source.Revision, source.Session, source.Role, source.Text, source.Created, source.Cursor).Count == 1;

    internal static string HistoryMessageText(string role, string text)
    {
        if (role != "assistant") return text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            JsonElement segments;
            if (root.ValueKind == JsonValueKind.Array) segments = root;
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("segments", out var found)) segments = found;
            else return text;
            if (segments.ValueKind != JsonValueKind.Array) return text;
            var parsed = segments.Deserialize<DialogueSegment[]>() ?? [];
            // ReplyDelivery only puts fully delivered segments here. Preserve
            // both languages, never delivery bookkeeping or unused model fields.
            return string.Join("\n", parsed.SelectMany(segment => new[] { segment.Ja, segment.Zh }).Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch (JsonException) { return text; }
    }

    private static List<HistoryChunk> SplitHistory(string text)
    {
        var chunks = new List<HistoryChunk>();
        int start = 0;
        while (start < text.Length)
        {
            int end = start, bytes = 0;
            // The byte bound plus the E5 prefix/special tokens remains below
            // 512 even for byte-fallback tokenization; no silent truncated tail.
            while (end < text.Length)
            {
                int width = char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
                int nextBytes = Encoding.UTF8.GetByteCount(text.AsSpan(end, width));
                if (end + width - start > 240 || bytes + nextBytes > 480) break;
                bytes += nextBytes;
                end += width;
            }
            chunks.Add(new(chunks.Count, start, text[start..end]));
            if (end == text.Length) break;
            start = Math.Max(start + 1, end - 24);
            if (char.IsLowSurrogate(text[start]) && start > 0 && char.IsHighSurrogate(text[start - 1])) start++;
        }
        return chunks;
    }

    private static Dictionary<string, double> HistoryTerms(string text)
    {
        var features = MemorySearch.Features(text).OrderByDescending(term => term.Value).Take(384).ToDictionary(term => term.Key, term => term.Value, StringComparer.Ordinal);
        double norm = Math.Sqrt(features.Values.Sum(value => value * value));
        if (norm > 0) foreach (string key in features.Keys.ToArray()) features[key] /= norm;
        return features;
    }

    private static string PackHistoryVector(double[] vector)
    {
        byte[] bytes = new byte[HistoryDimensions * sizeof(float)];
        for (int i = 0; i < HistoryDimensions; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), (float)vector[i]);
        return Convert.ToBase64String(bytes);
    }

    private static double HistoryCosine(double[] query, string encoded)
    {
        Span<byte> bytes = stackalloc byte[HistoryDimensions * sizeof(float)];
        if (query.Length != HistoryDimensions || !Convert.TryFromBase64String(encoded, bytes, out int written) || written != bytes.Length) return 0;
        double dot = 0;
        for (int i = 0; i < HistoryDimensions; i++) dot += query[i] * BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * sizeof(float))..]);
        return double.IsFinite(dot) ? dot : 0;
    }

    private static sbyte[] CreateHistoryProjection()
    {
        var result = new sbyte[HistoryHashTables * HistoryHashBits * HistoryDimensions];
        uint seed = 0x41545249;
        for (int i = 0; i < result.Length; i++)
        {
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
            result[i] = (seed & 1) == 0 ? (sbyte)-1 : (sbyte)1;
        }
        return result;
    }

    private static int[] HistoryCodes(double[] vector)
    {
        int[] codes = new int[HistoryHashTables];
        for (int table = 0; table < HistoryHashTables; table++)
        {
            int code = 0;
            for (int bit = 0; bit < HistoryHashBits; bit++)
            {
                double dot = 0;
                int offset = (table * HistoryHashBits + bit) * HistoryDimensions;
                for (int dimension = 0; dimension < HistoryDimensions; dimension++) dot += vector[dimension] * HistoryProjection[offset + dimension];
                if (dot >= 0) code |= 1 << bit;
            }
            codes[table] = (table << HistoryHashBits) | code;
        }
        return codes;
    }

    private static int[] HistoryProbes(double[] vector)
    {
        var probes = new List<int>(HistoryHashTables * (1 + HistoryHashBits + HistoryHashBits * (HistoryHashBits - 1) / 2));
        foreach (int code in HistoryCodes(vector))
        {
            probes.Add(code);
            for (int a = 0; a < HistoryHashBits; a++)
            {
                probes.Add(code ^ (1 << a));
                for (int b = a + 1; b < HistoryHashBits; b++) probes.Add(code ^ (1 << a) ^ (1 << b));
            }
        }
        return probes.ToArray();
    }
}
