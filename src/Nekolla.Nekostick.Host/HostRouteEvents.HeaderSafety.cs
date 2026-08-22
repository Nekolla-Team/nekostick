using System.Globalization;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Host;

internal static partial class HostRouteEvents
{
    private static readonly HashSet<string> ProtectedRouteHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Upgrade",
        "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer"
    };

    private static readonly HashSet<string> HopByHopResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding", "Connection", "Upgrade", "Keep-Alive",
        "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer"
    };

    private static bool IsProtectedRouteHeader(string name) => ProtectedRouteHeaders.Contains(name);

    private static bool TryValidateRequestReplacement(
        HttpContext context,
        ExtensionRouteRequestSnapshot replacement)
    {
        var currentHeaders = context.Request.Headers;
        if (replacement.Headers.TryGetValue("Host", out var replacementHost))
        {
            if (!currentHeaders.TryGetValue("Host", out var currentHost) ||
                !HeaderValuesEqual(currentHost, replacementHost))
            {
                return false;
            }
        }

        if (replacement.Headers.TryGetValue("Content-Length", out var replacementLength))
        {
            if (!currentHeaders.TryGetValue("Content-Length", out var currentLength) ||
                !HeaderValuesEqual(currentLength, replacementLength))
            {
                return false;
            }
        }

        foreach (var name in ProtectedRouteHeaders)
        {
            if (name is "Host" or "Content-Length")
            {
                continue;
            }

            var currentPresent = currentHeaders.TryGetValue(name, out var currentValues);
            var replacementPresent = replacement.Headers.TryGetValue(name, out var replacementValues);
            if (currentPresent != replacementPresent)
            {
                return false;
            }

            if (currentPresent && !HeaderValuesEqual(currentValues, replacementValues))
            {
                return false;
            }
        }

        foreach (var header in replacement.Headers)
        {
            if (!IsProtectedRouteHeader(header.Key) &&
                header.Value.Any(static value => value.Any(char.IsControl)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidResponseReplacement(ExtensionRouteResponseSnapshot response)
    {
        var bodyProhibited = response.StatusCode is >= 100 and < 200 or 204 or 304;
        if (bodyProhibited && !response.Body.IsDefaultOrEmpty)
        {
            return false;
        }

        var contentLengthPresent = false;
        foreach (var header in response.Headers)
        {
            if (HopByHopResponseHeaders.Contains(header.Key) ||
                header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (contentLengthPresent || header.Value.Length != 1 ||
                !long.TryParse(
                    header.Value[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var contentLength) ||
                contentLength < 0 || contentLength != response.Body.Length)
            {
                return false;
            }

            contentLengthPresent = true;
        }

        return true;
    }

    private static bool HeaderValuesEqual(
        IEnumerable<string> left,
        IEnumerable<string> right) =>
        left.SequenceEqual(right, StringComparer.Ordinal);
}
