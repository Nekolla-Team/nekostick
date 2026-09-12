using System.Collections.Concurrent;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Suppresses repetitive per-request log entries while keeping a cumulative occurrence count.</summary>
internal sealed class ExtensionLogThrottle
{
    private const int ReportEvery = 100;
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

    /// <summary>Returns whether the keyed entry should be logged now, with its cumulative occurrence count.</summary>
    /// <param name="key">The stable throttle key, usually an extension and failure category pair.</param>
    /// <param name="occurrences">The cumulative number of observed occurrences for the key.</param>
    /// <returns><see langword="true" /> for the first occurrence and then every hundredth occurrence.</returns>
    internal bool TryAcquire(string key, out long occurrences)
    {
        var count = _counts.AddOrUpdate(key, 1, static (_, current) => current + 1);
        occurrences = count;
        return count == 1 || count % ReportEvery == 0;
    }
}
