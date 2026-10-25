using System.Security.Cryptography;
using System.Text;

namespace Atri.Core;

public sealed partial class MemoryStore
{
    private readonly object _queryCacheGate = new();
    private readonly Dictionary<string, (double[] Vector, DateTimeOffset At)> _queryVectors = new(StringComparer.Ordinal);

    // Knowledge, structured facts and history use the same query in one turn.
    // Cache only the digest and vector, never another copy of the user's text.
    private double[]? QueryVector(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_embedding is null) return null;
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query)));
        lock (_queryCacheGate)
            if (_queryVectors.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(2))
                return cached.Vector;
        double[]? vector = _embedding.Encode([Clip(query, 4000)], "query", cancellationToken)?[0];
        if (vector is null) return null;
        lock (_queryCacheGate)
        {
            if (_queryVectors.Count >= 16)
                _queryVectors.Remove(_queryVectors.MinBy(pair => pair.Value.At).Key);
            _queryVectors[key] = (vector, DateTimeOffset.UtcNow);
        }
        return vector;
    }

    private void ClearQueryCache()
    {
        lock (_queryCacheGate) _queryVectors.Clear();
    }
}
