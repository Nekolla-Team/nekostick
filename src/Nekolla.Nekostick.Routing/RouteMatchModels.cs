using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Routing;

/// <summary>Describes the safe outcome of a route lookup.</summary>
public sealed class RouteMatchResult
{
    private RouteMatchResult(
        RouteMatchStatus status,
        RouteMatch? match,
        RouteNoMatchReason? noMatchReason,
        PathNormalizationErrorCode? invalidRequestCode,
        ImmutableArray<Guid> regexTimeoutRouteIds)
    {
        Status = status;
        Match = match;
        NoMatchReason = noMatchReason;
        InvalidRequestCode = invalidRequestCode;
        RegexTimeoutRouteIds = regexTimeoutRouteIds.IsDefault
            ? ImmutableArray<Guid>.Empty
            : regexTimeoutRouteIds;
    }

    /// <summary>Gets the lookup status.</summary>
    public RouteMatchStatus Status { get; }

    /// <summary>Gets the selected route, or null when no route was selected.</summary>
    public RouteMatch? Match { get; }

    /// <summary>Gets the no-match reason when <see cref="Status"/> is <see cref="RouteMatchStatus.NoMatch"/>.</summary>
    public RouteNoMatchReason? NoMatchReason { get; }

    /// <summary>Gets the safe invalid-request code when applicable.</summary>
    public PathNormalizationErrorCode? InvalidRequestCode { get; }

    /// <summary>
    /// Gets route IDs whose safe regex evaluation timed out and was skipped. The caller may log
    /// these IDs without exposing request content.
    /// </summary>
    public ImmutableArray<Guid> RegexTimeoutRouteIds { get; }

    internal static RouteMatchResult Matched(RouteMatch match, ImmutableArray<Guid> timeoutRouteIds) =>
        new(RouteMatchStatus.Matched, match, null, null, timeoutRouteIds);

    internal static RouteMatchResult NoMatch(RouteNoMatchReason reason, ImmutableArray<Guid> timeoutRouteIds) =>
        new(RouteMatchStatus.NoMatch, null, reason, null, timeoutRouteIds);

    internal static RouteMatchResult InvalidRequest(
        PathNormalizationErrorCode code,
        ImmutableArray<Guid> timeoutRouteIds) =>
        new(RouteMatchStatus.InvalidRequest, null, null, code, timeoutRouteIds);

    /// <summary>Returns a representation that does not contain request values.</summary>
    public override string ToString() => $"RouteMatchResult({Status})";
}

/// <summary>Identifies the immutable target reference selected by a route.</summary>
public sealed class RouteTargetReference
{
    internal RouteTargetReference(RouteTargetType type, Guid? serviceId, string? rootPath, string? handlerId)
    {
        Type = type;
        ServiceId = serviceId;
        RootPath = rootPath;
        HandlerId = handlerId;
    }

    /// <summary>Gets the target category.</summary>
    public RouteTargetType Type { get; }

    /// <summary>Gets the referenced service ID for a microservice target.</summary>
    public Guid? ServiceId { get; }

    /// <summary>Gets the absolute root path for a static-file target.</summary>
    public string? RootPath { get; }

    /// <summary>Gets the stable handler ID for an extension target.</summary>
    public string? HandlerId { get; }

    /// <summary>Returns a representation that excludes target details.</summary>
    public override string ToString() => $"RouteTargetReference({Type})";
}

/// <summary>Contains the immutable result of selecting one route.</summary>
public sealed class RouteMatch
{
    internal RouteMatch(
        Guid routeId,
        RouteTargetReference target,
        ForwardingMode forwardingMode,
        string? replaceTemplate,
        string normalizedPath,
        string matchedText,
        string forwardedPath)
    {
        RouteId = routeId;
        Target = target;
        ForwardingMode = forwardingMode;
        ReplaceTemplate = replaceTemplate;
        NormalizedPath = normalizedPath;
        MatchedText = matchedText;
        ForwardedPath = forwardedPath;
    }

    /// <summary>Gets the stable route ID.</summary>
    public Guid RouteId { get; }

    /// <summary>Gets the selected target reference.</summary>
    public RouteTargetReference Target { get; }

    /// <summary>Gets the configured forwarding mode.</summary>
    public ForwardingMode ForwardingMode { get; }

    /// <summary>Gets the configured replacement template, when present.</summary>
    public string? ReplaceTemplate { get; }

    /// <summary>Gets the normalized path used by matching.</summary>
    public string NormalizedPath { get; }

    /// <summary>Gets the complete text matched by the selected matcher.</summary>
    public string MatchedText { get; }

    /// <summary>Gets the absolute path produced for forwarding, without a query string.</summary>
    public string ForwardedPath { get; }

    /// <summary>Returns a representation that does not contain path or template values.</summary>
    public override string ToString() => $"RouteMatch({RouteId:D})";
}

/// <summary>Contains a configuration error without echoing route configuration content.</summary>
public sealed class RouteConfigurationError
{
    internal RouteConfigurationError(Guid? routeId, RouteConfigurationErrorCode code)
    {
        RouteId = routeId;
        Code = code;
    }

    /// <summary>Gets the route ID associated with the error, when available.</summary>
    public Guid? RouteId { get; }

    /// <summary>Gets the safe configuration error code.</summary>
    public RouteConfigurationErrorCode Code { get; }

    /// <summary>Returns a representation that does not contain configuration values.</summary>
    public override string ToString() => RouteId is Guid id
        ? $"RouteConfigurationError({id:D},{Code})"
        : $"RouteConfigurationError({Code})";
}

/// <summary>Contains the immutable result of building a route snapshot.</summary>
public sealed class RouteSnapshotBuildResult
{
    private RouteSnapshotBuildResult(
        RouteMatchSnapshot? snapshot,
        ImmutableArray<RouteConfigurationError> errors)
    {
        Snapshot = snapshot;
        Errors = errors.IsDefault ? ImmutableArray<RouteConfigurationError>.Empty : errors;
    }

    /// <summary>Gets whether snapshot construction succeeded.</summary>
    public bool IsSuccess => Snapshot is not null;

    /// <summary>Gets the immutable snapshot when construction succeeded.</summary>
    public RouteMatchSnapshot? Snapshot { get; }

    /// <summary>Gets all deterministic safe errors when construction failed.</summary>
    public ImmutableArray<RouteConfigurationError> Errors { get; }

    internal static RouteSnapshotBuildResult Success(RouteMatchSnapshot snapshot) =>
        new(snapshot, ImmutableArray<RouteConfigurationError>.Empty);

    internal static RouteSnapshotBuildResult Failure(ImmutableArray<RouteConfigurationError> errors) =>
        new(null, errors);

    /// <summary>Returns a representation that does not contain route configuration.</summary>
    public override string ToString() => IsSuccess
        ? "RouteSnapshotBuildResult(Success)"
        : $"RouteSnapshotBuildResult(Failure:{Errors.Length})";
}
