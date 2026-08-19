using System.Collections.Immutable;

namespace Nekolla.Nekostick.Contracts;

/// <summary>Specifies the stable path matcher semantics of a route.</summary>
public enum RouteMatcherType
{
    /// <summary>Case-sensitive complete path match.</summary>
    Exact,

    /// <summary>Ordinal case-insensitive complete path match.</summary>
    ExactCaseInsensitive,

    /// <summary>Case-sensitive segment or raw prefix match.</summary>
    Prefix,

    /// <summary>Ordinal case-insensitive segment or raw prefix match.</summary>
    PrefixCaseInsensitive,

    /// <summary>Culture-invariant regular-expression match.</summary>
    Regex
}

/// <summary>Specifies the stable kind of route target.</summary>
public enum RouteTargetType
{
    /// <summary>A locally supervised microservice.</summary>
    Microservice,

    /// <summary>A static-file root owned by the route.</summary>
    StaticFile,

    /// <summary>A trusted extension handler identified by stable ID.</summary>
    ExtensionHandler
}

/// <summary>Specifies how a matched path is forwarded.</summary>
public enum ForwardingMode
{
    /// <summary>Preserve the normalized request path.</summary>
    Preserve,

    /// <summary>Remove the matched route prefix.</summary>
    Strip,

    /// <summary>Generate a path from a validated replacement template.</summary>
    Replace
}

/// <summary>Specifies one safe header rewrite operation.</summary>
public enum HeaderRewriteOperation
{
    /// <summary>Removes all values for the header.</summary>
    Remove,

    /// <summary>Replaces the header values.</summary>
    Set,

    /// <summary>Appends a header value.</summary>
    Add
}

/// <summary>Describes the path, host, and method conditions of a route.</summary>
public sealed record RouteMatcherConfiguration
{
    /// <summary>Creates an immutable route matcher DTO.</summary>
    /// <param name="type">The matcher type.</param>
    /// <param name="pattern">The matcher pattern.</param>
    /// <param name="hostPatterns">The optional host constraints.</param>
    /// <param name="methods">The optional method constraints.</param>
    public RouteMatcherConfiguration(
        RouteMatcherType type,
        string pattern,
        ImmutableArray<string> hostPatterns,
        ImmutableArray<string> methods)
    {
        Type = type;
        Pattern = string.IsNullOrWhiteSpace(pattern)
            ? throw new ArgumentException("A route pattern is required.", nameof(pattern))
            : pattern;
        HostPatterns = hostPatterns.IsDefault ? ImmutableArray<string>.Empty : hostPatterns;
        Methods = methods.IsDefault ? ImmutableArray<string>.Empty : methods;
    }

    /// <summary>Gets the matcher type.</summary>
    public RouteMatcherType Type { get; }

    /// <summary>Gets the path or regular-expression pattern.</summary>
    public string Pattern { get; }

    /// <summary>Gets the immutable host constraints.</summary>
    public ImmutableArray<string> HostPatterns { get; }

    /// <summary>Gets the immutable HTTP method constraints.</summary>
    public ImmutableArray<string> Methods { get; }
}

/// <summary>Describes a declarative header rewrite.</summary>
public sealed record HeaderRewriteConfiguration
{
    /// <summary>Creates a header rewrite DTO.</summary>
    /// <param name="operation">The rewrite operation.</param>
    /// <param name="name">The header name.</param>
    /// <param name="value">The optional value used by set or add.</param>
    public HeaderRewriteConfiguration(HeaderRewriteOperation operation, string name, string? value)
    {
        Operation = operation;
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A header name is required.", nameof(name))
            : name;
        Value = value;
    }

    /// <summary>Gets the rewrite operation.</summary>
    public HeaderRewriteOperation Operation { get; }

    /// <summary>Gets the header name.</summary>
    public string Name { get; }

    /// <summary>Gets the optional rewrite value.</summary>
    public string? Value { get; }
}

/// <summary>Defines the target-independent route forwarding settings.</summary>
public sealed record ForwardingConfiguration
{
    /// <summary>Creates forwarding settings.</summary>
    /// <param name="mode">The forwarding mode.</param>
    /// <param name="replaceTemplate">The replacement template for replace mode.</param>
    public ForwardingConfiguration(ForwardingMode mode, string? replaceTemplate)
    {
        if (mode == ForwardingMode.Replace && string.IsNullOrWhiteSpace(replaceTemplate))
        {
            throw new ArgumentException("Replace mode requires a template.", nameof(replaceTemplate));
        }

        if (mode != ForwardingMode.Replace && replaceTemplate is not null)
        {
            throw new ArgumentException("Only replace mode accepts a template.", nameof(replaceTemplate));
        }

        Mode = mode;
        ReplaceTemplate = replaceTemplate;
    }

    /// <summary>Gets the forwarding mode.</summary>
    public ForwardingMode Mode { get; }

    /// <summary>Gets the replacement template when replace mode is selected.</summary>
    public string? ReplaceTemplate { get; }
}

/// <summary>Provides the target-specific immutable route boundary.</summary>
public abstract record RouteTargetConfiguration
{
    /// <summary>Initializes a route target DTO.</summary>
    /// <param name="type">The target type.</param>
    protected RouteTargetConfiguration(RouteTargetType type) => Type = type;

    /// <summary>Gets the target type.</summary>
    public RouteTargetType Type { get; }
}

/// <summary>References a supervised microservice route target.</summary>
public sealed record MicroserviceRouteTargetConfiguration : RouteTargetConfiguration
{
    /// <summary>Creates a microservice target reference.</summary>
    /// <param name="serviceId">The stable service identifier.</param>
    public MicroserviceRouteTargetConfiguration(Guid serviceId) : base(RouteTargetType.Microservice)
    {
        ServiceId = IdentityValidation.RequireUuidV7(serviceId, nameof(serviceId));
    }

    /// <summary>Gets the referenced service identifier.</summary>
    public Guid ServiceId { get; }
}

/// <summary>Defines an absolute static-file route target.</summary>
public sealed record StaticFileRouteTargetConfiguration : RouteTargetConfiguration
{
    /// <summary>Creates a static-file target reference.</summary>
    /// <param name="rootPath">The absolute static-file root.</param>
    public StaticFileRouteTargetConfiguration(string rootPath) : base(RouteTargetType.StaticFile)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Path.IsPathRooted(rootPath))
        {
            throw new ArgumentException("An absolute static root path is required.", nameof(rootPath));
        }

        RootPath = rootPath;
    }

    /// <summary>Gets the configured static-file root path.</summary>
    public string RootPath { get; }
}

/// <summary>References a trusted extension handler by stable ID.</summary>
public sealed record ExtensionHandlerRouteTargetConfiguration : RouteTargetConfiguration
{
    /// <summary>Creates an extension handler target reference.</summary>
    /// <param name="handlerId">The stable handler identifier.</param>
    public ExtensionHandlerRouteTargetConfiguration(string handlerId) : base(RouteTargetType.ExtensionHandler)
    {
        HandlerId = string.IsNullOrWhiteSpace(handlerId)
            ? throw new ArgumentException("A handler identifier is required.", nameof(handlerId))
            : handlerId;
    }

    /// <summary>Gets the stable handler identifier.</summary>
    public string HandlerId { get; }
}

/// <summary>Defines one immutable route configuration record.</summary>
public sealed record RouteConfiguration
{
    /// <summary>Creates a route configuration DTO.</summary>
    /// <param name="id">The public route identifier.</param>
    /// <param name="enabled">Whether the route participates in matching.</param>
    /// <param name="matcher">The route matcher.</param>
    /// <param name="target">The route target.</param>
    /// <param name="priority">The numeric route priority.</param>
    /// <param name="forwarding">The forwarding settings.</param>
    /// <param name="requestHeaderRewrites">The request rewrites.</param>
    /// <param name="responseHeaderRewrites">The response rewrites.</param>
    /// <param name="metadataJson">Extension-owned JSON metadata.</param>
    /// <param name="createdAt">The UTC creation timestamp.</param>
    /// <param name="updatedAt">The UTC update timestamp.</param>
    /// <param name="version">The optimistic-concurrency version.</param>
    /// <param name="clientIpRatePolicy">The optional route client-IP policy; <see langword="null"/> inherits the global policy.</param>
    /// <param name="maxRequestBodyBytes">The optional route request body limit; <see langword="null"/> inherits the global limit.</param>
    /// <param name="maxRequestHeaderBytes">The optional route request header limit; <see langword="null"/> inherits the global limit.</param>
    /// <param name="maxConcurrentRequests">The optional route concurrency limit; <see langword="null"/> inherits the global limit.</param>
    /// <param name="requestReadTimeout">The optional route request read timeout; <see langword="null"/> inherits the global limit.</param>
    /// <param name="proxyRetries">The optional route proxy retry settings; <see langword="null"/> inherits the global settings.</param>
    public RouteConfiguration(
        Guid id,
        bool enabled,
        RouteMatcherConfiguration matcher,
        RouteTargetConfiguration target,
        int priority,
        ForwardingConfiguration forwarding,
        ImmutableArray<HeaderRewriteConfiguration> requestHeaderRewrites,
        ImmutableArray<HeaderRewriteConfiguration> responseHeaderRewrites,
        string metadataJson,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version,
        ClientIpRatePolicyConfiguration? clientIpRatePolicy = null,
        long? maxRequestBodyBytes = null,
        long? maxRequestHeaderBytes = null,
        int? maxConcurrentRequests = null,
        TimeSpan? requestReadTimeout = null,
        ProxyRetryConfiguration? proxyRetries = null)
    {
        Id = IdentityValidation.RequireUuidV7(id, nameof(id));
        Enabled = enabled;
        Matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Priority = priority;
        Forwarding = forwarding ?? throw new ArgumentNullException(nameof(forwarding));
        RequestHeaderRewrites = requestHeaderRewrites.IsDefault
            ? ImmutableArray<HeaderRewriteConfiguration>.Empty
            : requestHeaderRewrites;
        ResponseHeaderRewrites = responseHeaderRewrites.IsDefault
            ? ImmutableArray<HeaderRewriteConfiguration>.Empty
            : responseHeaderRewrites;
        MetadataJson = metadataJson ?? throw new ArgumentNullException(nameof(metadataJson));
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
        Version = version < 0 ? throw new ArgumentOutOfRangeException(nameof(version)) : version;
        if (maxRequestBodyBytes is <= 0 or > GlobalSettingsConfiguration.HardMaximumRequestBodyBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRequestBodyBytes));
        }

        if (maxRequestHeaderBytes is <= 0 or > GlobalSettingsConfiguration.HardMaximumRequestHeaderBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRequestHeaderBytes));
        }

        if (maxConcurrentRequests is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentRequests));
        }

        if (requestReadTimeout is { } readTimeout &&
            (readTimeout <= TimeSpan.Zero ||
             readTimeout.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
             readTimeout > TimeSpan.FromDays(1)))
        {
            throw new ArgumentOutOfRangeException(nameof(requestReadTimeout));
        }
        ClientIpRatePolicy = clientIpRatePolicy;
        MaxRequestBodyBytes = maxRequestBodyBytes;
        MaxRequestHeaderBytes = maxRequestHeaderBytes;
        MaxConcurrentRequests = maxConcurrentRequests;
        RequestReadTimeout = requestReadTimeout;
        ProxyRetries = proxyRetries;
    }

    /// <summary>Gets the route identifier.</summary>
    public Guid Id { get; }

    /// <summary>Gets whether the route is enabled.</summary>
    public bool Enabled { get; }

    /// <summary>Gets the route matcher.</summary>
    public RouteMatcherConfiguration Matcher { get; }

    /// <summary>Gets the route target.</summary>
    public RouteTargetConfiguration Target { get; }

    /// <summary>Gets the numeric route priority.</summary>
    public int Priority { get; }

    /// <summary>Gets the forwarding settings.</summary>
    public ForwardingConfiguration Forwarding { get; }

    /// <summary>Gets the request rewrites.</summary>
    public ImmutableArray<HeaderRewriteConfiguration> RequestHeaderRewrites { get; }

    /// <summary>Gets the response rewrites.</summary>
    public ImmutableArray<HeaderRewriteConfiguration> ResponseHeaderRewrites { get; }

    /// <summary>Gets extension-owned JSON metadata.</summary>
    public string MetadataJson { get; }

    /// <summary>Gets the UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets the UTC update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>Gets the optimistic-concurrency version.</summary>
    public long Version { get; }

    /// <summary>Gets the optional route client-IP rate policy; null inherits the global policy.</summary>
    public ClientIpRatePolicyConfiguration? ClientIpRatePolicy { get; }

    /// <summary>Gets the optional route request body limit; null inherits the global limit.</summary>
    public long? MaxRequestBodyBytes { get; }

    /// <summary>Gets the optional route request header limit; null inherits the global limit.</summary>
    public long? MaxRequestHeaderBytes { get; }

    /// <summary>Gets the optional route concurrency limit; null inherits the global limit.</summary>
    public int? MaxConcurrentRequests { get; }

    /// <summary>Gets the optional route request read timeout; null inherits the global limit.</summary>
    public TimeSpan? RequestReadTimeout { get; }
    /// <summary>Gets the optional route proxy retry settings; null inherits the global settings.</summary>
    public ProxyRetryConfiguration? ProxyRetries { get; }
}
