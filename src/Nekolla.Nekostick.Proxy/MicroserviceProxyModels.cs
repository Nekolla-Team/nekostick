using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Proxy;

/// <summary>Specifies one immutable proxy header rewrite operation.</summary>
public enum HeaderRewriteOperation
{
    /// <summary>Removes every value for the header.</summary>
    Remove,

    /// <summary>Replaces the header with one value.</summary>
    Set,

    /// <summary>Adds one value without joining existing values.</summary>
    Add
}

/// <summary>Contains one request-local context for expanding validated rewrite templates.</summary>
public sealed class RequestHeaderExpansionContext
{
    /// <summary>Creates an immutable request expansion context.</summary>
    public RequestHeaderExpansionContext(
        string clientIp,
        string path,
        string method,
        string host)
        : this(clientIp, path, method, host, null)
    {
    }

    internal RequestHeaderExpansionContext(
        string clientIp,
        string path,
        string method,
        string host,
        EffectiveClientIdentity? effectiveClientIdentity)
    {
        ClientIp = clientIp ?? throw new ArgumentNullException(nameof(clientIp));
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Host = host ?? throw new ArgumentNullException(nameof(host));
        EffectiveClientIdentity = effectiveClientIdentity;
    }

    /// <summary>Gets the trusted client address selected for the request.</summary>
    public string ClientIp { get; }

    /// <summary>Gets the match-time forwarded path.</summary>
    public string Path { get; }

    /// <summary>Gets the HTTP request method.</summary>
    public string Method { get; }

    /// <summary>Gets the safe current request host.</summary>
    public string Host { get; }

    internal EffectiveClientIdentity? EffectiveClientIdentity { get; }

    internal RequestHeaderExpansionContext WithEffectiveClientIdentity(
        EffectiveClientIdentity effectiveClientIdentity) =>
        new(
            effectiveClientIdentity.ClientIp,
            Path,
            Method,
            Host,
            effectiveClientIdentity);
}

/// <summary>Defines one validated immutable proxy header rewrite.</summary>
public sealed record HeaderRewriteConfiguration
{
    private readonly CompiledHeaderRewriteTemplate? _template;

    /// <summary>Creates a safe header rewrite configuration.</summary>
    /// <param name="operation">The rewrite operation.</param>
    /// <param name="name">The HTTP token header name.</param>
    /// <param name="value">The visible ASCII value for set or add.</param>
    public HeaderRewriteConfiguration(HeaderRewriteOperation operation, string name, string? value = null)
    {
        if (!MicroserviceHttpTransformer.IsHeaderNameSafe(name))
        {
            throw new ArgumentException("The rewrite header name is invalid.", nameof(name));
        }

        if (operation is HeaderRewriteOperation.Set or HeaderRewriteOperation.Add
            && !MicroserviceHttpTransformer.IsHeaderValueSafe(value))
        {
            throw new ArgumentException("The rewrite header value is invalid.", nameof(value));
        }

        if (operation is HeaderRewriteOperation.Set or HeaderRewriteOperation.Add)
        {
            if (!CompiledHeaderRewriteTemplate.TryCompile(value, out var compiledTemplate)
                || compiledTemplate is null)
            {
                throw new ArgumentException("The rewrite header template is invalid.", nameof(value));
            }

            _template = compiledTemplate;
        }

        if (operation is not HeaderRewriteOperation.Remove
            and not HeaderRewriteOperation.Set
            and not HeaderRewriteOperation.Add)
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        if (operation == HeaderRewriteOperation.Remove && value is not null)
        {
            throw new ArgumentException("A remove rewrite cannot carry a value.", nameof(value));
        }

        Operation = operation;
        Name = name;
        Value = value;
    }

    /// <summary>Gets the operation.</summary>
    public HeaderRewriteOperation Operation { get; }

    /// <summary>Gets the case-preserving header name.</summary>
    public string Name { get; }

    /// <summary>Gets the safe value for set or add.</summary>
    public string? Value { get; }

    internal CompiledHeaderRewriteTemplate? Template => _template;
}

/// <summary>Contains an immutable, precompiled trusted-proxy CIDR policy.</summary>
public sealed class TrustedProxyPolicy
{
    private readonly ImmutableArray<TrustedProxyNetwork> _networks;

    /// <summary>Creates a policy from validated CIDR strings.</summary>
    /// <param name="cidrs">The trusted proxy CIDRs.</param>
    public TrustedProxyPolicy(IEnumerable<string>? cidrs = null)
    {
        var source = cidrs?.ToArray() ?? Array.Empty<string>();
        var networks = ImmutableArray.CreateBuilder<TrustedProxyNetwork>(source.Length);
        var normalized = ImmutableArray.CreateBuilder<string>(source.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var cidr in source)
        {
            if (!TrustedProxyNetwork.TryParse(cidr, out var network, out var canonical))
            {
                throw new ArgumentException("A trusted proxy CIDR is invalid.", nameof(cidrs));
            }

            if (!seen.Add(canonical))
            {
                throw new ArgumentException("Trusted proxy CIDRs must be unique.", nameof(cidrs));
            }

            networks.Add(network);
            normalized.Add(canonical);
        }

        _networks = networks.MoveToImmutable();
        Cidrs = normalized.MoveToImmutable();
    }

    /// <summary>Gets a policy with no trusted proxies.</summary>
    public static TrustedProxyPolicy Empty { get; } = new();

    /// <summary>Gets the immutable canonical CIDR input.</summary>
    public ImmutableArray<string> Cidrs { get; }

    /// <summary>Returns whether the supplied peer belongs to a trusted CIDR.</summary>
    /// <param name="address">The direct connection peer address.</param>
    /// <returns>True only for an address in a compiled network.</returns>
    public bool IsTrusted(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        return _networks.Any(network => network.Contains(address));
    }

    private readonly struct TrustedProxyNetwork
    {
        private readonly ImmutableArray<byte> _networkBytes;

        private TrustedProxyNetwork(AddressFamily family, int prefixLength, ImmutableArray<byte> networkBytes)
        {
            Family = family;
            PrefixLength = prefixLength;
            _networkBytes = networkBytes;
        }

        private AddressFamily Family { get; }

        private int PrefixLength { get; }

        public bool Contains(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6
                && address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (address.AddressFamily != Family)
            {
                return false;
            }

            var candidate = address.GetAddressBytes();
            var fullBytes = PrefixLength / 8;
            var remainingBits = PrefixLength % 8;
            for (var index = 0; index < fullBytes; index++)
            {
                if (candidate[index] != _networkBytes[index])
                {
                    return false;
                }
            }

            return remainingBits == 0
                || (candidate[fullBytes] & (byte)(0xff << (8 - remainingBits)))
                    == (_networkBytes[fullBytes] & (byte)(0xff << (8 - remainingBits)));
        }

        public static bool TryParse(string? value, out TrustedProxyNetwork network, out string canonical)
        {
            network = default;
            canonical = string.Empty;
            if (string.IsNullOrWhiteSpace(value) || value.Contains('%', StringComparison.Ordinal))
            {
                return false;
            }

            var separator = value.LastIndexOf('/');
            if (separator <= 0 || separator == value.Length - 1 || value.IndexOf('/') != separator)
            {
                return false;
            }

            var addressText = value[..separator];
            var prefixText = value[(separator + 1)..];
            if (!IPAddress.TryParse(addressText, out var address)
                || !int.TryParse(
                    prefixText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var prefixLength))
            {
                return false;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            var maximum = address.AddressFamily switch
            {
                AddressFamily.InterNetwork => 32,
                AddressFamily.InterNetworkV6 => 128,
                _ => -1
            };
            if (maximum < 0 || prefixLength < 0 || prefixLength > maximum)
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            var fullBytes = prefixLength / 8;
            var remainingBits = prefixLength % 8;
            if (remainingBits != 0)
            {
                bytes[fullBytes] &= (byte)(0xff << (8 - remainingBits));
                fullBytes++;
            }

            for (var index = fullBytes; index < bytes.Length; index++)
            {
                bytes[index] = 0;
            }

            address = new IPAddress(bytes);
            network = new(
                address.AddressFamily,
                prefixLength,
                ImmutableArray.Create(bytes));
            canonical = address + "/" + prefixLength.ToString(CultureInfo.InvariantCulture);
            return true;
        }
    }
}

/// <summary>Describes one safe microservice forwarding request.</summary>
public sealed class MicroserviceProxyRequest
{
    /// <summary>Creates a request using a path that contains no query string.</summary>
    /// <param name="serviceId">The stable microservice identifier.</param>
    /// <param name="forwardedPath">The already-computed path to forward.</param>
    /// <param name="trustedProxyPolicy">The immutable precompiled peer policy.</param>
    /// <param name="timeoutPolicy">The immutable timeout policy.</param>
    /// <param name="requestHeaderRewrites">The immutable request rewrites.</param>
    /// <param name="responseHeaderRewrites">The immutable response rewrites.</param>
    /// <param name="headerExpansionContext">The immutable request-local expansion context.</param>
    /// <param name="retryPolicy">The immutable retry policy.</param>
    /// <param name="routeId">The stable route identifier used for safe telemetry.</param>
    public MicroserviceProxyRequest(
        Guid serviceId,
        string forwardedPath,
        MicroserviceTimeoutPolicy timeoutPolicy,
        ImmutableArray<HeaderRewriteConfiguration> requestHeaderRewrites = default,
        ImmutableArray<HeaderRewriteConfiguration> responseHeaderRewrites = default,
        TrustedProxyPolicy? trustedProxyPolicy = null,
        RequestHeaderExpansionContext? headerExpansionContext = null,
        ProxyRetryConfiguration? retryPolicy = null,
        Guid? routeId = null)
    {
        if (serviceId == Guid.Empty)
        {
            throw new ArgumentException("A service identifier is required.", nameof(serviceId));
        }

        if (!IsSafeForwardedPath(forwardedPath))
        {
            throw new ArgumentException("ForwardedPath must be an absolute path without a query string.", nameof(forwardedPath));
        }

        if (routeId == Guid.Empty)
        {
            throw new ArgumentException("A route identifier cannot be empty.", nameof(routeId));
        }

        ServiceId = serviceId;
        RouteId = routeId;
        ForwardedPath = forwardedPath;
        RequestHeaderRewrites = CopyRewrites(requestHeaderRewrites, nameof(requestHeaderRewrites));
        ResponseHeaderRewrites = CopyRewrites(responseHeaderRewrites, nameof(responseHeaderRewrites));
        TrustedProxyPolicy = trustedProxyPolicy ?? TrustedProxyPolicy.Empty;
        TimeoutPolicy = timeoutPolicy ?? throw new ArgumentNullException(nameof(timeoutPolicy));
        RetryPolicy = retryPolicy ?? ProxyRetryConfiguration.Default;
        HeaderExpansionContext = headerExpansionContext
            ?? new RequestHeaderExpansionContext(string.Empty, string.Empty, string.Empty, string.Empty);
    }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the stable route identifier used for safe telemetry.</summary>
    public Guid? RouteId { get; }

    /// <summary>Gets the path to use while YARP builds the destination URI.</summary>
    public string ForwardedPath { get; }

    /// <summary>Gets the immutable request rewrites in declaration order.</summary>
    public ImmutableArray<HeaderRewriteConfiguration> RequestHeaderRewrites { get; }

    /// <summary>Gets the immutable response rewrites in declaration order.</summary>
    public ImmutableArray<HeaderRewriteConfiguration> ResponseHeaderRewrites { get; }

    /// <summary>Gets the immutable precompiled trusted-proxy policy.</summary>
    public TrustedProxyPolicy TrustedProxyPolicy { get; }

    /// <summary>Gets the required immutable timeout policy.</summary>
    public MicroserviceTimeoutPolicy TimeoutPolicy { get; }

    /// <summary>Gets the immutable retry policy.</summary>
    public ProxyRetryConfiguration RetryPolicy { get; }

    /// <summary>Gets the immutable request-local template expansion context.</summary>
    public RequestHeaderExpansionContext HeaderExpansionContext { get; }

    internal ImmutableArray<CompiledHeaderRewrite> CompiledRequestHeaderRewrites =>
        Compile(RequestHeaderRewrites);

    internal ImmutableArray<CompiledHeaderRewrite> CompiledResponseHeaderRewrites =>
        Compile(ResponseHeaderRewrites);

    private static ImmutableArray<HeaderRewriteConfiguration> CopyRewrites(
        ImmutableArray<HeaderRewriteConfiguration> rewrites,
        string parameterName)
    {
        if (rewrites.IsDefaultOrEmpty)
        {
            return ImmutableArray<HeaderRewriteConfiguration>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<HeaderRewriteConfiguration>(rewrites.Length);
        foreach (var rewrite in rewrites)
        {
            builder.Add(rewrite ?? throw new ArgumentException("A rewrite cannot be null.", parameterName));
        }

        return builder.MoveToImmutable();
    }

    private static ImmutableArray<CompiledHeaderRewrite> Compile(
        ImmutableArray<HeaderRewriteConfiguration> rewrites)
    {
        if (rewrites.IsDefaultOrEmpty)
        {
            return ImmutableArray<CompiledHeaderRewrite>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<CompiledHeaderRewrite>(rewrites.Length);
        foreach (var rewrite in rewrites)
        {
            builder.Add(new CompiledHeaderRewrite(
                rewrite.Operation,
                rewrite.Name,
                rewrite.Value,
                rewrite.Template));
        }

        return builder.MoveToImmutable();
    }

    private static bool IsSafeForwardedPath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '/')
        {
            return false;
        }

        return value.IndexOfAny(['?', '#', '\\']) < 0
            && value.All(character => character >= 0x20 && character != 0x7f && character != '\0');
    }
}

internal readonly record struct CompiledHeaderRewrite(
    HeaderRewriteOperation Operation,
    string Name,
    string? Value,
    CompiledHeaderRewriteTemplate? Template)
{
    internal string Expand(RequestHeaderExpansionContext context) =>
        Template?.Expand(context)
        ?? string.Empty;
}
