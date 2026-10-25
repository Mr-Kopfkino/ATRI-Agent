using System.Text.RegularExpressions;

namespace Atri.Core;

public sealed partial class MemoryStore
{
    /// <summary>Extracts only narrow, literal user declarations. No model inference
    /// is promoted to a user fact. Complex events remain editable manual memories.</summary>
    public IReadOnlyList<MemoryRecord> ObserveUserMessage(string sessionId, string text)
    {
        ValidateSession(sessionId);
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (text.Length > MaximumTextLength) throw new ArgumentException("用户陈述过长。", nameof(text));
        // Avoid interpreting quoted dialogue, hypothetical roleplay, questions or
        // reported speech as current facts about the real user.
        if (IsNonFactualStatement(text)) return [];
        Match correctedName = NameCorrectionPattern().Match(text.Trim());
        Match correctedPreference = PreferenceCorrectionPattern().Match(text.Trim());
        if (correctedName.Success || correctedPreference.Success)
            return ObserveCorrection(sessionId, correctedName.Success ? correctedName : correctedPreference, correctedName.Success);
        Match futureName = FutureNamePattern().Match(text.Trim());
        if (futureName.Success)
        {
            string newName = futureName.Groups["new"].Value.Trim();
            string literal = futureName.Groups["replacement"].Value;
            lock (_gate)
            {
                ThrowIfDisposed();
                MemoryRecord? previous = ReadRecords("SELECT * FROM memories WHERE kind=? AND normalized_key=?", (int)MemoryKind.UserFact, MemorySearch.Normalize("user.preferred-name")).SingleOrDefault();
                if (previous is null)
                    return [Remember(MemoryKind.UserFact, "user.preferred-name", newName, "user-explicit", 1, .9, sessionId, [literal])];
                MemoryRecord? updated = null;
                _db.Transaction(() => updated = CorrectInternal(previous, "user.preferred-name", newName, literal,
                    "user-correction", sessionId));
                PurgeBackups(); CheckpointPrivacyChanges(); Signal();
                return [updated!];
            }
        }
        var records = new List<MemoryRecord>();
        Match name = UserNamePattern().Match(text.Trim());
        if (name.Success)
            records.Add(Remember(MemoryKind.UserFact, "user.preferred-name", name.Groups[1].Value.Trim(), "user-explicit", 1, .9, sessionId, [text]));
        Match preference = PreferencePattern().Match(text.Trim());
        Match japanesePreference = JapanesePreferencePattern().Match(text.Trim());
        if (japanesePreference.Success)
        {
            string subject = japanesePreference.Groups[1].Value.Trim();
            bool negative = japanesePreference.Groups[2].Value == "嫌い";
            records.Add(Remember(MemoryKind.Preference, "preference:" + MemorySearch.Normalize(subject),
                $"用户{(negative ? "不喜欢" : "喜欢")}：{subject}", "user-explicit", 1, .7, sessionId, [text]));
        }
        if (preference.Success)
        {
            string subject = preference.Groups[2].Value.Trim();
            bool negative = preference.Groups[1].Value is "不喜欢" or "讨厌" or "嫌い";
            records.Add(Remember(MemoryKind.Preference, "preference:" + MemorySearch.Normalize(subject),
                $"用户{(negative ? "不喜欢" : "喜欢")}：{subject}", "user-explicit", 1, .7, sessionId, [text]));
        }
        return records;
    }

    private IReadOnlyList<MemoryRecord> ObserveCorrection(string sessionId, Match correction, bool name)
    {
        string oldSubject = correction.Groups["old"].Value.Trim();
        string newSubject = correction.Groups["new"].Value.Trim();
        if (oldSubject.Length == 0 || newSubject.Length == 0 || oldSubject == newSubject) return [];
        string newLiteral = correction.Groups["replacement"].Value;
        lock (_gate)
        {
            ThrowIfDisposed();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<MemoryRecord> candidates = ReadRecords("SELECT * FROM memories WHERE kind=?", (int)(name ? MemoryKind.UserFact : MemoryKind.Preference))
                .Where(record => IsExplicitSource(record.Source) && (!record.ExpiresAt.HasValue || record.ExpiresAt > now))
                .Where(record => name
                    ? record.Key == "user.preferred-name" && record.Text == oldSubject
                    : record.Text == "我喜欢" + oldSubject || record.Text == "用户喜欢" + oldSubject || record.Text == "用户喜欢：" + oldSubject)
                .ToList();
            // Select only an exact old declaration. The controlled canonical key
            // identifies our own extractor's record; otherwise require uniqueness.
            string canonicalKey = "preference:" + MemorySearch.Normalize(oldSubject);
            MemoryRecord[] canonical = candidates.Where(record => name || record.Key == canonicalKey).ToArray();
            MemoryRecord? previous = canonical.Length == 1 ? canonical[0] : canonical.Length == 0 && candidates.Count == 1 ? candidates[0] : null;
            if (previous is null) return [];
            MemoryRecord? updated = null;
            string newKey = name ? "user.preferred-name" : "preference:only-drink:" + MemorySearch.Normalize(newSubject);
            _db.Transaction(() => updated = CorrectInternal(previous, newKey, name ? newSubject : newLiteral,
                newLiteral, "user-correction", sessionId, correction.Groups["oldClaim"].Value));
            PurgeBackups(); CheckpointPrivacyChanges(); Signal();
            return [updated!];
        }
    }

    /// <summary>Records only observable conversation counts and timestamps, with
    /// provenance to persisted user-message rows. No affinity or relationship
    /// level is inferred. Call after a real successfully delivered chat turn.</summary>
    public MemoryRecord RecordInteraction(string sessionId, string userLiteral)
    {
        ValidateSession(sessionId);
        if (string.IsNullOrWhiteSpace(userLiteral) || userLiteral.Length > MaximumTextLength)
            throw new ArgumentException("实际用户消息不能为空或过长。", nameof(userLiteral));
        lock (_gate)
        {
            ThrowIfDisposed();
            var rows = _db.Query("SELECT id,text,created_at FROM messages WHERE session_id=? AND role='user' ORDER BY id DESC LIMIT 24", sessionId);
            if (rows.Count == 0 || rows[0][1] != userLiteral)
                throw new InvalidOperationException("共同聊天记录只能关联已保存的当前真实用户消息。");
            var stats = _db.Query("SELECT COUNT(*),MIN(created_at),MAX(created_at) FROM messages WHERE session_id=? AND role='user'", sessionId)[0];
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string text = $"实际共同聊天记录：本会话用户消息 {stats[0]} 条；首条时间 {stats[1]}；最近时间 {stats[2]}。此记录仅表示消息数量与时间。";
            string[] evidence = rows.Select(row => $"user-message:{row[0]};session:{sessionId};at:{row[2]}").ToArray();
            MemoryRecord? saved = null;
            _db.Transaction(() =>
            {
                saved = UpsertInternal(new MemoryRecord(Guid.NewGuid().ToString("N"), MemoryKind.Relationship,
                    "relationship:interaction:" + sessionId, text, "user-chat", 1, .3, now, now, sessionId, evidence));
                _db.Execute("DELETE FROM memory_revisions WHERE memory_id=?", saved.Id);
                _db.Execute("UPDATE memories SET evidence=? WHERE id=?", System.Text.Json.JsonSerializer.Serialize(evidence), saved.Id);
            });
            return saved! with { Evidence = evidence };
        }
    }

    [GeneratedRegex(@"^(?:我叫|我的名字是|请叫我|請叫我|私の名前は)\s*([^，。！？、\r\n]{1,32}?)(?:です|だよ)?[。.!！]?$", RegexOptions.CultureInvariant)]
    private static partial Regex UserNamePattern();
    [GeneratedRegex(@"^(?:我)(喜欢|喜歡|不喜欢|讨厌)\s*([^，。！？、\r\n]{1,100})[。.!！]?$", RegexOptions.CultureInvariant)]
    private static partial Regex PreferencePattern();
    [GeneratedRegex(@"^私は\s*([^，。！？、\r\n]{1,100}?)が(好き|嫌い)(?:です|だよ|だ)?[。.!！]?$", RegexOptions.CultureInvariant)]
    private static partial Regex JapanesePreferencePattern();
    [GeneratedRegex(@"^之前说(?<oldClaim>我叫(?<old>[^，。！？、\r\n.!]{1,32}))是错的，(?<replacement>请叫我(?<new>[^，。！？、\r\n.!]{1,32}))[。.!！]?$", RegexOptions.CultureInvariant)]
    private static partial Regex NameCorrectionPattern();
    [GeneratedRegex(@"^之前说(?<oldClaim>我喜欢(?<old>[^，。！？、\r\n.!]{1,100}))是错的，(?<replacement>我只喝(?<new>[^，。！？、\r\n.!]{1,100}))[。.!！]?$", RegexOptions.CultureInvariant)]
    private static partial Regex PreferenceCorrectionPattern();
    [GeneratedRegex(@"^(?<replacement>以后叫我(?<new>[^，。！？、\r\n.!]{1,32}))，不要叫(?<old>[^，。！？、\r\n.!]{1,32})[。.!！]?$", RegexOptions.CultureInvariant)]
    private static partial Regex FutureNamePattern();
    internal static bool IsNonFactualStatement(string text) =>
        text.IndexOfAny(['?', '？', '"', '\'', '「', '」', '『', '』', '“', '”', '‘', '’']) >= 0 ||
        HypotheticalPattern().IsMatch(text) || UncertainPattern().IsMatch(text) || JapaneseUncertainPattern().IsMatch(text);

    [GeneratedRegex(@"可能|也许|大概|或许|听说|据说|(?:吗|么|呢|吧)[。.!！]?$", RegexOptions.CultureInvariant)]
    private static partial Regex UncertainPattern();
    // Doubtful preference endings require a preference predicate. Ordinary
    // names/nouns such as かな, かもめ and かな文字, and と思います, stay valid.
    [GeneratedRegex(@"たぶん|多分|おそらく|恐らく|(?:好き|嫌い)(?:なの)?(?:かも(?:です|ね|な)?|かな)[。.!！?？\s]*$", RegexOptions.CultureInvariant)]
    private static partial Regex JapaneseUncertainPattern();
    [GeneratedRegex(@"如果|假如|假设|假設|比如|例如|角色扮演|设定|設定|他说|她说|他說|她說|もし|仮に|例えば|と言った|だとしたら|(?:仮の話|仮定の話|ロールプレイ)(?:では|での|の設定|として)", RegexOptions.CultureInvariant)]
    private static partial Regex HypotheticalPattern();
}
