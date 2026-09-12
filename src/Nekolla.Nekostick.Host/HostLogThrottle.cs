using System.Collections.Concurrent;

namespace Nekolla.Nekostick.Host;

/// <summary>Provides bounded first-and-periodic acquisition for high-frequency Host logs.</summary>
internal sealed class HostLogThrottle
{
    private readonly ConcurrentDictionary<string, long> _occurrences = new(StringComparer.Ordinal);

    internal bool TryAcquire(string key, out long occurrences)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            occurrences = 0;
            return false;
        }

        occurrences = _occurrences.AddOrUpdate(
            key,
            static _ => 1,
            static (_, count) => checked(count + 1));
        return occurrences == 1 || occurrences % 100 == 0;
    }
}
