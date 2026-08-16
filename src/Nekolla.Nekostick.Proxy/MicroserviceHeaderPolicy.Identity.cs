using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;

namespace Nekolla.Nekostick.Proxy;

public sealed partial class MicroserviceHttpTransformer
{
    private static void StripIdentityHeaders(HttpHeaders headers, HttpContent? content)
    {
        foreach (var name in headers.Select(pair => pair.Key).ToArray())
        {
            if (IsIdentityHeader(name))
            {
                headers.Remove(name);
            }
        }

        if (content is not null)
        {
            foreach (var name in content.Headers.Select(pair => pair.Key).ToArray())
            {
                if (IsIdentityHeader(name))
                {
                    content.Headers.Remove(name);
                }
            }
        }
    }

    private static void StripIdentityHeaders(IHeaderDictionary headers)
    {
        foreach (var name in headers.Select(pair => pair.Key).ToArray())
        {
            if (IsIdentityHeader(name))
            {
                headers.Remove(name);
            }
        }
    }

    private static bool IsIdentityHeader(string name) =>
        name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
        || name.Equals("X-Real-IP", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeScheme(string? scheme) =>
        scheme is not null
        && (scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("https", StringComparison.OrdinalIgnoreCase));

    private static string FormatForwardedAddress(IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? "\"[" + address + "]\""
            : address.ToString();

    private static void AddHeaderValues(
        HttpHeaders headers,
        string name,
        IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            headers.TryAddWithoutValidation(name, [value]);
        }
    }

    private sealed class IPAddressComparer : IEqualityComparer<IPAddress>
    {
        public static IPAddressComparer Instance { get; } = new();

        public bool Equals(IPAddress? left, IPAddress? right) =>
            left is not null && right is not null && left.Equals(right);

        public int GetHashCode(IPAddress obj) => obj.GetHashCode();
    }
}
