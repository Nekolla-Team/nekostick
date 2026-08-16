namespace Nekolla.Nekostick.Routing;

/// <summary>Provides path, host, and method input to the pure route matcher.</summary>
public sealed class RouteMatchInput
{
    /// <summary>Creates matcher input without binding it to an HTTP framework.</summary>
    /// <param name="path">The request path without query or fragment.</param>
    /// <param name="host">The host value without a framework host binding, or null when absent.</param>
    /// <param name="method">The request method token.</param>
    public RouteMatchInput(string? path, string? host, string? method)
    {
        Path = path;
        Host = host;
        Method = method;
    }

    /// <summary>Gets the request path.</summary>
    public string? Path { get; }

    /// <summary>Gets the request host, or null when the request has no host value.</summary>
    public string? Host { get; }

    /// <summary>Gets the request method token.</summary>
    public string? Method { get; }

    /// <summary>Returns a representation that does not contain request values.</summary>
    public override string ToString() => "RouteMatchInput";
}

/// <summary>Describes why a valid request did not select a route.</summary>
public enum RouteNoMatchReason
{
    /// <summary>No enabled route had a matching path.</summary>
    NoRoute,

    /// <summary>Path candidates were rejected by host conditions.</summary>
    HostMismatch,

    /// <summary>Path candidates were rejected by method conditions.</summary>
    MethodMismatch,

    /// <summary>Path candidates were rejected by more than one condition.</summary>
    ConditionMismatch
}

/// <summary>Describes the safe outcome of a route lookup.</summary>
public enum RouteMatchStatus
{
    /// <summary>A route was selected.</summary>
    Matched,

    /// <summary>No route was selected for a valid request.</summary>
    NoMatch,

    /// <summary>The request was invalid and is a 400 candidate.</summary>
    InvalidRequest
}

/// <summary>Describes a deterministic route snapshot build error.</summary>
public enum RouteConfigurationErrorCode
{
    /// <summary>The route ID is missing or is not UUID version 7.</summary>
    InvalidRouteIdentifier,

    /// <summary>The route ID appears more than once.</summary>
    DuplicateRouteIdentifier,

    /// <summary>The route matcher type is unsupported.</summary>
    InvalidMatcherType,

    /// <summary>The path pattern is invalid.</summary>
    InvalidPathPattern,

    /// <summary>The prefix wildcard syntax is invalid.</summary>
    InvalidPrefixWildcard,

    /// <summary>A host pattern is invalid.</summary>
    InvalidHostPattern,

    /// <summary>A method condition is invalid.</summary>
    InvalidMethod,

    /// <summary>The route target is invalid.</summary>
    InvalidTarget,

    /// <summary>The forwarding settings are invalid for the matcher.</summary>
    InvalidForwarding,

    /// <summary>The regex exceeds the configured maximum length.</summary>
    RegexTooLong,

    /// <summary>The regex cannot be compiled with the safe options.</summary>
    InvalidRegex,

    /// <summary>The replacement template contains an unsafe or unsupported token.</summary>
    InvalidReplacementTemplate
}
