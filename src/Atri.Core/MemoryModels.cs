namespace Atri.Core;

public enum MemoryKind { UserFact, Preference, Event, SessionSummary, Relationship, CharacterKnowledge }

public sealed record MemoryRecord(
    string Id, MemoryKind Kind, string Key, string Text, string Source,
    double Confidence, double Importance, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? SessionId = null, string[]? Evidence = null, DateTimeOffset? ExpiresAt = null);

public sealed record MemoryMessage(long Id, string SessionId, string Role, string Text, DateTimeOffset CreatedAt);
public sealed record MemoryTask(string Id, string SessionId, string State, int Attempts, string? Error, DateTimeOffset UpdatedAt);
public sealed record MemoryRevision(string MemoryId, string OldText, string NewText, string Source, DateTimeOffset CreatedAt);
public sealed record MemorySession(string SessionId, string Title, DateTimeOffset LastAt, int MessageCount);
