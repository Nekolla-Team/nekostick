using System.Collections.Concurrent;

namespace Nekolla.Nekostick.Proxy;

internal static class ProxyLogThrottle
{
    private static readonly ConcurrentDictionary<string, long> Counts = new(StringComparer.Ordinal);

    internal static bool TryAcquire(string key, out long occurrences)
    {
        occurrences = Counts.AddOrUpdate(
            key,
            1L,
            static (_, current) => current == long.MaxValue ? current : current + 1);
        return occurrences == 1 || occurrences % 100 == 0;
    }
}
