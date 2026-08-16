using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using System.Net.Http.Headers;

namespace Nekolla.Nekostick.Proxy;

public sealed partial class MicroserviceHttpTransformer
{
    internal static EffectiveClientIdentity ResolveEffectiveClientIdentity(
        HttpContext httpContext,
        TrustedProxyPolicy trustedProxyPolicy)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(trustedProxyPolicy);

        var remoteAddress = NormalizeAddress(httpContext.Connection.RemoteIpAddress);
        if (remoteAddress is null)
        {
            return EffectiveClientIdentity.Empty;
        }

        var chain = new List<IPAddress>();
        if (trustedProxyPolicy.IsTrusted(remoteAddress)
            && TryReadTrustedChain(httpContext.Request.Headers, out var suppliedChain))
        {
            chain.AddRange(TrimToTrustedBoundary(trustedProxyPolicy, suppliedChain));
        }

        chain.Add(remoteAddress);
        return new(chain.ToImmutableArray(), chain[0]);
    }

    private static void ApplyTrustedIdentityHeaders(
        HttpContext httpContext,
        HttpHeaders headers,
        HttpContent? content,
        EffectiveClientIdentity identity)
    {
        StripIdentityHeaders(headers, content);

        if (identity.OutboundChain.IsDefaultOrEmpty)
        {
            return;
        }

        var xForwardedFor = identity.OutboundChain
            .Select(address => address.ToString())
            .ToArray();
        var forwarded = identity.OutboundChain.Select((address, index) =>
        {
            var entry = "for=" + FormatForwardedAddress(address);
            if (index == identity.OutboundChain.Length - 1
                && IsSafeScheme(httpContext.Request.Scheme))
            {
                entry += ";proto=" + httpContext.Request.Scheme;
            }

            return entry;
        }).ToArray();

        AddHeaderValues(headers, "X-Forwarded-For", xForwardedFor);
        AddHeaderValues(headers, "Forwarded", forwarded);
        if (IsSafeScheme(httpContext.Request.Scheme))
        {
            AddHeaderValues(headers, "X-Forwarded-Proto", [httpContext.Request.Scheme]);
        }
    }

    private static bool TryReadTrustedChain(
        IHeaderDictionary headers,
        out List<IPAddress> chain)
    {
        chain = [];
        var hasXForwardedFor = headers.ContainsKey("X-Forwarded-For");
        var hasForwarded = headers.ContainsKey("Forwarded");
        if (!hasXForwardedFor && !hasForwarded)
        {
            return true;
        }

        var xForwardedChain = new List<IPAddress>();
        var forwardedChain = new List<IPAddress>();
        var xForwardedValid = !hasXForwardedFor
            || TryParseXForwardedFor(headers, xForwardedChain);
        var forwardedValid = !hasForwarded
            || TryParseForwarded(headers, forwardedChain);
        if (!xForwardedValid || !forwardedValid)
        {
            chain = [];
            return false;
        }

        if (hasXForwardedFor && hasForwarded
            && !xForwardedChain.SequenceEqual(forwardedChain, IPAddressComparer.Instance))
        {
            chain = [];
            return false;
        }

        chain = hasXForwardedFor ? xForwardedChain : forwardedChain;
        return true;
    }

    private static bool TryParseXForwardedFor(
        IHeaderDictionary headers,
        List<IPAddress> chain)
    {
        if (!headers.TryGetValue("X-Forwarded-For", out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (value is null || !IsHeaderValueSafe(value))
            {
                return false;
            }

            foreach (var item in value.Split(','))
            {
                if (!TryParseAddress(item.Trim(), out var address))
                {
                    return false;
                }

                chain.Add(address);
            }
        }

        return chain.Count > 0;
    }

    private static IPAddress[] TrimToTrustedBoundary(
        TrustedProxyPolicy trustedProxyPolicy,
        List<IPAddress> suppliedChain)
    {
        for (var index = suppliedChain.Count - 1; index >= 0; index--)
        {
            if (!trustedProxyPolicy.IsTrusted(suppliedChain[index]))
            {
                return suppliedChain.Skip(index).ToArray();
            }
        }

        return [];
    }

    private static bool TryParseForwarded(
        IHeaderDictionary headers,
        List<IPAddress> chain)
    {
        if (!headers.TryGetValue("Forwarded", out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (value is null || !IsHeaderValueSafe(value))
            {
                return false;
            }

            foreach (var entry in value.Split(','))
            {
                var foundFor = false;
                foreach (var parameter in entry.Split(';'))
                {
                    var separator = parameter.IndexOf('=');
                    if (separator <= 0)
                    {
                        return false;
                    }

                    var name = parameter[..separator].Trim();
                    var parameterValue = parameter[(separator + 1)..].Trim();
                    if (!IsHeaderNameSafe(name) || !IsHeaderValueSafe(parameterValue))
                    {
                        return false;
                    }

                    if (!name.Equals("for", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (foundFor || !TryParseForwardedAddress(parameterValue, out var address))
                    {
                        return false;
                    }

                    chain.Add(address);
                    foundFor = true;
                }

                if (!foundFor)
                {
                    return false;
                }
            }
        }

        return chain.Count > 0;
    }

    private static bool TryParseForwardedAddress(string value, out IPAddress address)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        if (value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal))
        {
            address = IPAddress.None;
            return false;
        }

        return TryParseAddress(value, out address);
    }

    private static bool TryParseAddress(string value, out IPAddress address)
    {
        if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
        {
            value = value[1..^1];
        }

        if (value.Contains('%', StringComparison.Ordinal)
            || !IPAddress.TryParse(value, out var parsedAddress)
            || parsedAddress is null
            || parsedAddress.AddressFamily is not AddressFamily.InterNetwork and not AddressFamily.InterNetworkV6)
        {
            address = IPAddress.None;
            return false;
        }

        address = parsedAddress;
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return true;
    }

    private static IPAddress? NormalizeAddress(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}

internal readonly record struct EffectiveClientIdentity(
    ImmutableArray<IPAddress> OutboundChain,
    IPAddress? EffectiveClient)
{
    internal static EffectiveClientIdentity Empty { get; } = new([], null);

    internal string ClientIp => EffectiveClient?.ToString() ?? string.Empty;
}
