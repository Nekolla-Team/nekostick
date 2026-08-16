using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;

namespace Nekolla.Nekostick.Proxy;

public sealed partial class MicroserviceHttpTransformer
{
    private static readonly string[] HopByHopHeaders =
    [
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    ];

    private static void StripHopByHopHeaders(
        HttpRequestHeaders headers,
        HttpHeaders? contentHeaders,
        IHeaderDictionary? source,
        bool preserveWebSocketUpgrade,
        bool preserveChunkedFraming)
    {
        var dynamicTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddConnectionTokens(headers, dynamicTokens);

        if (source is not null)
        {
            if (!source.TryGetValue("Connection", out var values))
            {
                values = default;
            }

            foreach (var value in values)
            {
                if (value is null)
                {
                    continue;
                }

                foreach (var token in value.Split(','))
                {
                    var name = token.Trim();
                    if (IsHeaderNameSafe(name))
                    {
                        dynamicTokens.Add(name);
                    }
                }
            }
        }

        foreach (var name in HopByHopHeaders)
        {
            if (preserveWebSocketUpgrade
                && name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            RemoveRequestHeader(headers, contentHeaders, name);
        }

        foreach (var name in dynamicTokens)
        {
            if (preserveWebSocketUpgrade
                && name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            RemoveRequestHeader(headers, contentHeaders, name);
        }

        if (preserveChunkedFraming)
        {
            headers.Remove("Transfer-Encoding");
            headers.TransferEncodingChunked = true;
        }
    }

    private static void StripResponseHopByHopHeaders(
        IHeaderDictionary headers,
        IEnumerable<string> upstreamConnectionTokens,
        bool preserveWebSocketUpgrade)
    {
        var dynamicTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        dynamicTokens.UnionWith(upstreamConnectionTokens);
        AddConnectionTokens(headers, dynamicTokens);

        foreach (var name in HopByHopHeaders)
        {
            if (preserveWebSocketUpgrade && IsWebSocketUpgradeHeader(name))
            {
                continue;
            }

            headers.Remove(name);
        }

        foreach (var name in dynamicTokens)
        {
            if (preserveWebSocketUpgrade && IsWebSocketUpgradeHeader(name))
            {
                continue;
            }

            headers.Remove(name);
        }
    }

    private static bool HasWebSocketUpgrade(HttpHeaders headers) =>
        headers.TryGetValues("Upgrade", out var values)
        && values.Any(value => value.Equals("websocket", StringComparison.OrdinalIgnoreCase));

    private static bool HasWebSocketUpgrade(IHeaderDictionary headers) =>
        headers.TryGetValue("Upgrade", out var values)
        && values.Any(value => value is not null
            && value.Equals("websocket", StringComparison.OrdinalIgnoreCase));

    private static bool IsWebSocketUpgradeResponse(HttpContext httpContext) =>
        httpContext.Response.StatusCode == StatusCodes.Status101SwitchingProtocols
        && HasWebSocketUpgrade(httpContext.Response.Headers);

    private static bool IsWebSocketUpgradeHeader(string name) =>
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    private static void RestoreWebSocketUpgrade(HttpHeaders headers)
    {
        headers.Remove("Connection");
        headers.Remove("Upgrade");
        headers.TryAddWithoutValidation("Connection", ["Upgrade"]);
        headers.TryAddWithoutValidation("Upgrade", ["websocket"]);
    }

    private static void RestoreWebSocketUpgrade(IHeaderDictionary headers)
    {
        headers.Remove("Connection");
        headers.Remove("Upgrade");
        headers["Connection"] = "Upgrade";
        headers["Upgrade"] = "websocket";
    }

    private static void AddConnectionTokens(
        HttpHeaders headers,
        HashSet<string> tokens)
    {
        if (!headers.TryGetValues("Connection", out var values))
        {
            return;
        }

        foreach (var value in values)
        {
            foreach (var token in value.Split(','))
            {
                var name = token.Trim();
                if (IsHeaderNameSafe(name))
                {
                    tokens.Add(name);
                }
            }
        }
    }

    private static void AddConnectionTokens(
        IHeaderDictionary headers,
        HashSet<string> tokens)
    {
        if (!headers.TryGetValue("Connection", out var values))
        {
            return;
        }

        foreach (var value in values)
        {
            if (value is null)
            {
                continue;
            }

            foreach (var token in value!.Split(','))
            {
                var name = token.Trim();
                if (IsHeaderNameSafe(name))
                {
                    tokens.Add(name);
                }
            }
        }
    }
}
