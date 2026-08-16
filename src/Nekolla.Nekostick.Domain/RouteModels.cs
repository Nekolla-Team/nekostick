using System.Collections.Immutable;

namespace Nekolla.Nekostick.Domain;

/// <summary>Defines the route matcher order and semantics.</summary>
public enum RouteMatcherKind
{
    /// <summary>Case-sensitive complete path.</summary>
    Exact,

    /// <summary>Ordinal case-insensitive complete path.</summary>
    ExactCaseInsensitive,

    /// <summary>Case-sensitive segment or raw prefix.</summary>
    Prefix,

    /// <summary>Ordinal case-insensitive segment or raw prefix.</summary>
    PrefixCaseInsensitive,

    /// <summary>Culture-invariant regular expression.</summary>
    Regex
}

/// <summary>Defines the route target category.</summary>
public enum RouteTargetKind
{
    /// <summary>A local microservice.</summary>
    Microservice,

    /// <summary>An absolute static-file root.</summary>
    StaticFile,

    /// <summary>A trusted extension handler.</summary>
    ExtensionHandler
}

/// <summary>Defines how a selected route forwards a path.</summary>
public enum ForwardingKind
{
    /// <summary>Preserve the normalized path.</summary>
    Preserve,

    /// <summary>Strip the matched route prefix.</summary>
    Strip,

    /// <summary>Use a validated replacement template.</summary>
    Replace
}

/// <summary>Contains immutable route matcher conditions.</summary>
public sealed record RouteMatcher
{
    /// <summary>Creates route matcher conditions.</summary>
    /// <param name="kind">The matcher kind.</param>
    /// <param name="pattern">The non-empty pattern.</param>
    /// <param name="hostPatterns">The optional host conditions.</param>
    /// <param name="methods">The optional method conditions.</param>
    public RouteMatcher(
        RouteMatcherKind kind,
        string pattern,
        ImmutableArray<string> hostPatterns = default,
        ImmutableArray<string> methods = default)
    {
        if (string.IsNullOrWhiteSpace(pattern) || ContainsControlCharacter(pattern))
        {
            throw new ArgumentException("A safe route pattern is required.", nameof(pattern));
        }

        Kind = kind;
        Pattern = pattern;
        HostPatterns = Normalize(hostPatterns, "host pattern");
        Methods = Normalize(methods, "method");
    }

    /// <summary>Gets the matcher kind.</summary>
    public RouteMatcherKind Kind { get; }

    /// <summary>Gets the matcher pattern.</summary>
    public string Pattern { get; }

    /// <summary>Gets immutable host conditions.</summary>
    public ImmutableArray<string> HostPatterns { get; }

    /// <summary>Gets immutable method conditions.</summary>
    public ImmutableArray<string> Methods { get; }

    private static ImmutableArray<string> Normalize(ImmutableArray<string> values, string label)
    {
        if (values.IsDefaultOrEmpty)
        {
            return ImmutableArray<string>.Empty;
        }

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || ContainsControlCharacter(value))
            {
                throw new ArgumentException($"A safe {label} is required.", nameof(values));
            }
        }

        return values;
    }

    private static bool ContainsControlCharacter(string value) => value.Any(char.IsControl);
}

/// <summary>Contains immutable forwarding settings.</summary>
public sealed record ForwardingOptions
{
    /// <summary>Creates forwarding settings.</summary>
    /// <param name="kind">The forwarding kind.</param>
    /// <param name="replaceTemplate">The replace template when applicable.</param>
    public ForwardingOptions(ForwardingKind kind, string? replaceTemplate = null)
    {
        if (kind == ForwardingKind.Replace && string.IsNullOrWhiteSpace(replaceTemplate))
        {
            throw new ArgumentException("Replace forwarding requires a template.", nameof(replaceTemplate));
        }

        if (kind != ForwardingKind.Replace && replaceTemplate is not null)
        {
            throw new ArgumentException("Only replace forwarding accepts a template.", nameof(replaceTemplate));
        }

        Kind = kind;
        ReplaceTemplate = replaceTemplate;
    }

    /// <summary>Gets the forwarding kind.</summary>
    public ForwardingKind Kind { get; }

    /// <summary>Gets the replacement template.</summary>
    public string? ReplaceTemplate { get; }
}

/// <summary>Provides a typed route target without framework dependencies.</summary>
public abstract record RouteTarget
{
    /// <summary>Initializes a route target.</summary>
    /// <param name="kind">The target kind.</param>
    protected RouteTarget(RouteTargetKind kind) => Kind = kind;

    /// <summary>Gets the target kind.</summary>
    public RouteTargetKind Kind { get; }
}

/// <summary>References a microservice by UUID.</summary>
public sealed record MicroserviceRouteTarget : RouteTarget
{
    /// <summary>Creates a microservice target.</summary>
    /// <param name="serviceId">The referenced service UUID.</param>
    public MicroserviceRouteTarget(Guid serviceId) : base(RouteTargetKind.Microservice)
    {
        ServiceId = UuidV7.RequireVersion7(serviceId, nameof(serviceId));
    }

    /// <summary>Gets the service UUID.</summary>
    public Guid ServiceId { get; }
}

/// <summary>Defines an absolute static-file root target.</summary>
public sealed record StaticFileRouteTarget : RouteTarget
{
    /// <summary>Creates a static-file target.</summary>
    /// <param name="rootPath">The absolute root path.</param>
    public StaticFileRouteTarget(string rootPath) : base(RouteTargetKind.StaticFile)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Path.IsPathRooted(rootPath))
        {
            throw new ArgumentException("An absolute static root path is required.", nameof(rootPath));
        }

        RootPath = rootPath;
    }

    /// <summary>Gets the absolute static root path.</summary>
    public string RootPath { get; }
}

/// <summary>References an extension handler by stable identifier.</summary>
public sealed record ExtensionHandlerRouteTarget : RouteTarget
{
    /// <summary>Creates an extension handler target.</summary>
    /// <param name="handlerId">The stable handler ID.</param>
    public ExtensionHandlerRouteTarget(string handlerId) : base(RouteTargetKind.ExtensionHandler)
    {
        HandlerId = string.IsNullOrWhiteSpace(handlerId)
            ? throw new ArgumentException("A handler identifier is required.", nameof(handlerId))
            : handlerId;
    }

    /// <summary>Gets the stable handler ID.</summary>
    public string HandlerId { get; }
}

/// <summary>Represents a domain route entity with common persistence state.</summary>
public sealed class RouteDefinition : EntityBase
{
    /// <summary>Creates a new route definition.</summary>
    /// <param name="uuidGenerator">The UUID v7 generator.</param>
    /// <param name="matcher">The immutable matcher.</param>
    /// <param name="target">The immutable target.</param>
    /// <param name="forwarding">The immutable forwarding settings.</param>
    /// <param name="priority">The numeric route priority.</param>
    /// <param name="enabled">Whether the route participates in matching.</param>
    /// <param name="timeProvider">The UTC time provider.</param>
    public RouteDefinition(
        IUuidV7Generator uuidGenerator,
        RouteMatcher matcher,
        RouteTarget target,
        ForwardingOptions forwarding,
        int priority = 0,
        bool enabled = true,
        TimeProvider? timeProvider = null)
        : base(uuidGenerator, timeProvider)
    {
        Matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Forwarding = forwarding ?? throw new ArgumentNullException(nameof(forwarding));
        Priority = priority;
        Enabled = enabled;
    }

    /// <summary>Gets the route matcher.</summary>
    public RouteMatcher Matcher { get; }

    /// <summary>Gets the route target.</summary>
    public RouteTarget Target { get; }

    /// <summary>Gets the forwarding settings.</summary>
    public ForwardingOptions Forwarding { get; }

    /// <summary>Gets the numeric priority.</summary>
    public int Priority { get; }

    /// <summary>Gets whether the route participates in matching.</summary>
    public bool Enabled { get; }
}
