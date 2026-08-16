using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;

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
        string matchedText)
    {
        RouteId = routeId;
        Target = target;
        ForwardingMode = forwardingMode;
        ReplaceTemplate = replaceTemplate;
        NormalizedPath = normalizedPath;
        MatchedText = matchedText;
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

    /// <summary>Returns a representation that does not contain path or template values.</summary>
    public override string ToString() => $"RouteMatch({RouteId:D})";
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

/// <summary>Builds immutable route matching snapshots from Contract or Domain models.</summary>
public static class RouteMatchSnapshotBuilder
{
    /// <summary>Builds a snapshot from immutable Contract route values.</summary>
    /// <param name="routes">The route configurations to validate and compile.</param>
    /// <returns>A snapshot or safe configuration errors. Invalid routes are never included.</returns>
    public static RouteSnapshotBuildResult Build(IEnumerable<RouteConfiguration> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var inputs = new List<RouteCandidateInput>();

        foreach (var route in routes)
        {
            inputs.Add(route is null
                ? RouteCandidateInput.Missing()
                : RouteCandidateInput.FromContract(route));
        }

        return BuildCore(inputs);
    }

    /// <summary>Builds a snapshot from immutable Domain route values.</summary>
    /// <param name="routes">The route definitions to validate and compile.</param>
    /// <returns>A snapshot or safe configuration errors. Invalid routes are never included.</returns>
    public static RouteSnapshotBuildResult Build(IEnumerable<RouteDefinition> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var inputs = new List<RouteCandidateInput>();

        foreach (var route in routes)
        {
            inputs.Add(route is null
                ? RouteCandidateInput.Missing()
                : RouteCandidateInput.FromDomain(route));
        }

        return BuildCore(inputs);
    }

    private static RouteSnapshotBuildResult BuildCore(List<RouteCandidateInput> inputs)
    {
        inputs.Sort(static (left, right) => CompareGuidLexically(left.Id, right.Id));
        var errors = ImmutableArray.CreateBuilder<RouteConfigurationError>();
        var seenIds = new HashSet<Guid>();
        var candidates = new List<CompiledRoute>(inputs.Count);

        foreach (var input in inputs)
        {
            if (input.Id == Guid.Empty)
            {
                errors.Add(new RouteConfigurationError(null, RouteConfigurationErrorCode.InvalidRouteIdentifier));
                continue;
            }

            var duplicate = !seenIds.Add(input.Id);
            if (!UuidV7.IsVersion7(input.Id))
            {
                errors.Add(new RouteConfigurationError(input.Id, RouteConfigurationErrorCode.InvalidRouteIdentifier));
                continue;
            }

            if (duplicate)
            {
                errors.Add(new RouteConfigurationError(input.Id, RouteConfigurationErrorCode.DuplicateRouteIdentifier));
                continue;
            }

            if (TryCompile(input, errors, out var candidate) && candidate is not null && input.Enabled)
            {
                candidates.Add(candidate);
            }
        }

        if (errors.Count > 0)
        {
            return RouteSnapshotBuildResult.Failure(errors.ToImmutable());
        }

        return RouteSnapshotBuildResult.Success(RouteMatchSnapshot.Create(candidates));
    }

    private static bool TryCompile(
        RouteCandidateInput input,
        ImmutableArray<RouteConfigurationError>.Builder errors,
        out CompiledRoute? candidate)
    {
        candidate = null;
        var valid = true;
        var normalizedPattern = string.Empty;
        var isRawPrefix = false;
        var regex = default(Regex);

        if (!Enum.IsDefined(input.MatcherType))
        {
            errors.Add(new RouteConfigurationError(input.Id, RouteConfigurationErrorCode.InvalidMatcherType));
            valid = false;
        }
        else if (input.MatcherType == RouteMatcherType.Regex)
        {
            if (input.Pattern is null || input.Pattern.Length > 4096 || input.Pattern.Length == 0)
            {
                errors.Add(new RouteConfigurationError(input.Id, input.Pattern is not null && input.Pattern.Length > 4096
                    ? RouteConfigurationErrorCode.RegexTooLong
                    : RouteConfigurationErrorCode.InvalidRegex));
                valid = false;
            }
            else if (ContainsUnsafePatternCharacter(input.Pattern))
            {
                errors.Add(new RouteConfigurationError(input.Id, RouteConfigurationErrorCode.InvalidRegex));
                valid = false;
            }
            else
            {
                try
                {
                    regex = new Regex(
                        $"\\A(?:{input.Pattern})\\z",
                        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                        TimeSpan.FromMilliseconds(50));
                }
                catch (ArgumentException)
                {
                    errors.Add(new RouteConfigurationError(input.Id, RouteConfigurationErrorCode.InvalidRegex));
                    valid = false;
                }
                catch (NotSupportedException)
                {
                    errors.Add(new RouteConfigurationError(input.Id, RouteConfigurationErrorCode.InvalidRegex));
                    valid = false;
                }
            }
        }
        else
        {
            if (input.Pattern is null || !RoutePathNormalizer.Normalize(input.Pattern).IsSuccess)
            {
                errors.Add(new RouteConfigurationError(input.Id, RouteConfigurationErrorCode.InvalidPathPattern));
                valid = false;
            }
            else if (!TryCompilePathPattern(input.MatcherType, input.Pattern, out normalizedPattern, out isRawPrefix))
            {
                errors.Add(new RouteConfigurationError(input.Id, RouteConfigurationErrorCode.InvalidPrefixWildcard));
                valid = false;
            }
        }

        var hosts = CompileHosts(input, errors);
        var methods = CompileMethods(input, errors);
        var target = CompileTarget(input, errors);

        if (input.Forwarding is null || !Enum.IsDefined(input.Forwarding.Mode))
        {
            errors.Add(new RouteConfigurationError(input.Id, RouteConfigurationErrorCode.InvalidForwarding));
            valid = false;
        }
        else
        {
            if (input.Forwarding.Mode == ForwardingMode.Strip &&
                (input.MatcherType == RouteMatcherType.Regex ||
                 (input.MatcherType is RouteMatcherType.Prefix or RouteMatcherType.PrefixCaseInsensitive && isRawPrefix)))
            {
                errors.Add(new RouteConfigurationError(input.Id, RouteConfigurationErrorCode.InvalidForwarding));
                valid = false;
            }

            if (input.Forwarding.Mode == ForwardingMode.Replace &&
                !TryValidateReplacementTemplate(input.Forwarding.ReplaceTemplate, regex, input.MatcherType, input.Id, errors))
            {
                valid = false;
            }
        }

        if (!valid || hosts is null || methods is null || target is null || input.Pattern is null || input.Forwarding is null)
        {
            return false;
        }

        candidate = new CompiledRoute(
            input.Id,
            input.CreatedAt,
            input.Priority,
            input.MatcherType,
            normalizedPattern,
            isRawPrefix,
            regex,
            hosts.Value,
            methods.Value,
            target,
            input.Forwarding.Mode,
            input.Forwarding.ReplaceTemplate);
        return true;
    }

    private static ImmutableArray<HostPattern>? CompileHosts(
        RouteCandidateInput input,
        ImmutableArray<RouteConfigurationError>.Builder errors)
    {
        var values = ImmutableArray.CreateBuilder<HostPattern>();
        foreach (var value in input.HostPatterns)
        {
            if (!HostPattern.TryCreate(value, out var pattern))
            {
                errors.Add(new RouteConfigurationError(input.Id, RouteConfigurationErrorCode.InvalidHostPattern));
                return null;
            }

            values.Add(pattern!);
        }

        return values.ToImmutable();
    }

    private static ImmutableArray<string>? CompileMethods(
        RouteCandidateInput input,
        ImmutableArray<RouteConfigurationError>.Builder errors)
    {
        var values = ImmutableArray.CreateBuilder<string>();
        foreach (var value in input.Methods)
        {
            if (!IsMethodToken(value))
            {
                errors.Add(new RouteConfigurationError(input.Id, RouteConfigurationErrorCode.InvalidMethod));
                return null;
            }

            values.Add(value.ToUpperInvariant());
        }

        return values.ToImmutable();
    }

    private static RouteTargetData? CompileTarget(
        RouteCandidateInput input,
        ImmutableArray<RouteConfigurationError>.Builder errors)
    {
        if (input.Target is null)
        {
            errors.Add(new RouteConfigurationError(input.Id, RouteConfigurationErrorCode.InvalidTarget));
            return null;
        }

        switch (input.Target)
        {
            case MicroserviceTargetData microservice when microservice.ServiceId != Guid.Empty:
                return new MicroserviceTargetData(microservice.ServiceId);
            case StaticFileTargetData staticFile when IsSafeAbsolutePath(staticFile.RootPath):
                return new StaticFileTargetData(staticFile.RootPath);
            case ExtensionTargetData extension when IsSafeIdentifier(extension.HandlerId):
                return new ExtensionTargetData(extension.HandlerId);
            default:
                errors.Add(new RouteConfigurationError(input.Id, RouteConfigurationErrorCode.InvalidTarget));
                return null;
        }
    }

    private static bool TryCompilePathPattern(
        RouteMatcherType matcherType,
        string pattern,
        out string normalizedPattern,
        out bool isRawPrefix)
    {
        normalizedPattern = string.Empty;
        isRawPrefix = false;
        var normalized = RoutePathNormalizer.Normalize(pattern);
        if (!normalized.IsSuccess || normalized.NormalizedPath is null)
        {
            return false;
        }

        if (matcherType is not (RouteMatcherType.Prefix or RouteMatcherType.PrefixCaseInsensitive))
        {
            normalizedPattern = normalized.NormalizedPath;
            return true;
        }

        var starIndex = normalized.NormalizedPath.IndexOf('*');
        if (starIndex < 0)
        {
            normalizedPattern = normalized.NormalizedPath;
            return true;
        }

        if (starIndex != normalized.NormalizedPath.Length - 1 ||
            normalized.NormalizedPath.LastIndexOf('*') != starIndex ||
            (starIndex > 0 && normalized.NormalizedPath[starIndex - 1] == '\\'))
        {
            return false;
        }

        normalizedPattern = normalized.NormalizedPath[..^1];
        isRawPrefix = !normalizedPattern.EndsWith('/');
        return true;
    }

    private static bool TryValidateReplacementTemplate(
        string? template,
        Regex? regex,
        RouteMatcherType matcherType,
        Guid routeId,
        ImmutableArray<RouteConfigurationError>.Builder errors)
    {
        if (string.IsNullOrWhiteSpace(template) ||
            ContainsUnsafeTemplateCharacter(template) ||
            !StartsWithAbsoluteReplacement(template, regex, matcherType))
        {
            errors.Add(new RouteConfigurationError(routeId, RouteConfigurationErrorCode.InvalidReplacementTemplate));
            return false;
        }

        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] == '{')
            {
                var end = template.IndexOf('}', index + 1);
                var token = end < 0 ? string.Empty : template[index..(end + 1)];
                if (end < 0 || (token != "{path}" && token != "{match}"))
                {
                    errors.Add(new RouteConfigurationError(routeId, RouteConfigurationErrorCode.InvalidReplacementTemplate));
                    return false;
                }

                index = end;
            }
            else if (template[index] == '$')
            {
                if (index + 1 >= template.Length || !char.IsDigit(template[index + 1]))
                {
                    errors.Add(new RouteConfigurationError(routeId, RouteConfigurationErrorCode.InvalidReplacementTemplate));
                    return false;
                }

                var end = index + 1;
                while (end < template.Length && char.IsDigit(template[end]))
                {
                    end++;
                }

                if (matcherType != RouteMatcherType.Regex ||
                    regex is null ||
                    !int.TryParse(template[(index + 1)..end], NumberStyles.None, CultureInfo.InvariantCulture, out var group) ||
                    group > regex.GetGroupNumbers().Max())
                {
                    errors.Add(new RouteConfigurationError(routeId, RouteConfigurationErrorCode.InvalidReplacementTemplate));
                    return false;
                }

                index = end - 1;
            }
            else if (template[index] == '}')
            {
                errors.Add(new RouteConfigurationError(routeId, RouteConfigurationErrorCode.InvalidReplacementTemplate));
                return false;
            }
        }

        return true;
    }

    private static bool ContainsUnsafePatternCharacter(string value) => value.Any(char.IsControl);

    private static bool ContainsUnsafeTemplateCharacter(string value) =>
        value.Any(character => char.IsControl(character) || character is '?' or '#');

    private static bool StartsWithAbsoluteReplacement(string value, Regex? regex, RouteMatcherType matcherType)
    {
        if (value.StartsWith('/') ||
            value.StartsWith("{path}", StringComparison.Ordinal) ||
            value.StartsWith("{match}", StringComparison.Ordinal))
        {
            return true;
        }

        if (matcherType != RouteMatcherType.Regex || regex is null || value.Length < 2 || value[0] != '$')
        {
            return false;
        }

        var end = 1;
        while (end < value.Length && char.IsDigit(value[end]))
        {
            end++;
        }

        return end > 1 &&
            int.TryParse(value[1..end], NumberStyles.None, CultureInfo.InvariantCulture, out var group) &&
            group == 0;
    }

    private static bool IsMethodToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character > 127 ||
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsWhiteSpace) &&
        !ContainsUnsafePatternCharacter(value);

    private static bool IsSafeAbsolutePath(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value) && !ContainsUnsafePatternCharacter(value);

    internal static int CompareGuidLexically(Guid left, Guid right) =>
        string.CompareOrdinal(left.ToString("D"), right.ToString("D"));
}

/// <summary>Contains immutable route indexes and performs pure route selection.</summary>
public sealed class RouteMatchSnapshot
{
    private readonly ImmutableDictionary<string, ImmutableArray<CompiledRoute>> _exact;
    private readonly ImmutableDictionary<string, ImmutableArray<CompiledRoute>> _exactCaseInsensitive;
    private readonly PrefixTrie _prefix;
    private readonly PrefixTrie _prefixCaseInsensitive;
    private readonly ImmutableArray<CompiledRoute> _regex;

    internal RouteMatchSnapshot(
        ImmutableDictionary<string, ImmutableArray<CompiledRoute>> exact,
        ImmutableDictionary<string, ImmutableArray<CompiledRoute>> exactCaseInsensitive,
        PrefixTrie prefix,
        PrefixTrie prefixCaseInsensitive,
        ImmutableArray<CompiledRoute> regex,
        int routeCount)
    {
        _exact = exact;
        _exactCaseInsensitive = exactCaseInsensitive;
        _prefix = prefix;
        _prefixCaseInsensitive = prefixCaseInsensitive;
        _regex = regex;
        RouteCount = routeCount;
    }

    /// <summary>Gets the number of enabled routes in this snapshot.</summary>
    public int RouteCount { get; }

    /// <summary>Matches a request using only this immutable snapshot.</summary>
    /// <param name="input">The framework-independent matcher input.</param>
    /// <returns>A matched, no-match, or invalid-request result.</returns>
    public RouteMatchResult Match(RouteMatchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = RoutePathNormalizer.Normalize(input.Path);
        if (!normalized.IsSuccess || normalized.NormalizedPath is null)
        {
            return RouteMatchResult.InvalidRequest(
                normalized.Error?.Code ?? PathNormalizationErrorCode.MissingPath,
                ImmutableArray<Guid>.Empty);
        }

        if (!TryNormalizeMethod(input.Method, out var method))
        {
            return RouteMatchResult.InvalidRequest(PathNormalizationErrorCode.InvalidMethod, ImmutableArray<Guid>.Empty);
        }

        if (!HostValue.TryCreate(input.Host, out var host, out _))
        {
            return RouteMatchResult.InvalidRequest(PathNormalizationErrorCode.InvalidHost, ImmutableArray<Guid>.Empty);
        }

        var timeoutIds = ImmutableArray.CreateBuilder<Guid>();
        var flags = new MatchConditionFlags();
        var first = Find(normalized.NormalizedPath, host, method, timeoutIds, flags);
        if (first is not null)
        {
            return RouteMatchResult.Matched(first, timeoutIds.ToImmutable());
        }

        if (!normalized.NormalizedPath.Equals("/", StringComparison.Ordinal) &&
            !normalized.NormalizedPath.EndsWith('/'))
        {
            var alternatePath = normalized.NormalizedPath + "/";
            var alternate = Find(alternatePath, host, method, timeoutIds, flags);
            if (alternate is not null)
            {
                return RouteMatchResult.Matched(alternate, timeoutIds.ToImmutable());
            }
        }

        return RouteMatchResult.NoMatch(flags.GetReason(), timeoutIds.ToImmutable());
    }

    internal static RouteMatchSnapshot Create(List<CompiledRoute> routes)
    {
        var exact = new Dictionary<string, List<CompiledRoute>>(StringComparer.Ordinal);
        var exactCaseInsensitive = new Dictionary<string, List<CompiledRoute>>(StringComparer.Ordinal);
        var prefix = new PrefixTrieBuilder();
        var prefixCaseInsensitive = new PrefixTrieBuilder();
        var regex = new List<CompiledRoute>();

        foreach (var route in routes)
        {
            switch (route.MatcherType)
            {
                case RouteMatcherType.Exact:
                    Add(exact, route.NormalizedPattern, route);
                    break;
                case RouteMatcherType.ExactCaseInsensitive:
                    Add(exactCaseInsensitive, FoldOrdinal(route.NormalizedPattern), route);
                    break;
                case RouteMatcherType.Prefix:
                    prefix.Add(route.NormalizedPattern, route);
                    break;
                case RouteMatcherType.PrefixCaseInsensitive:
                    prefixCaseInsensitive.Add(FoldOrdinal(route.NormalizedPattern), route);
                    break;
                case RouteMatcherType.Regex:
                    regex.Add(route);
                    break;
            }
        }

        return new RouteMatchSnapshot(
            Freeze(exact),
            Freeze(exactCaseInsensitive),
            prefix.Freeze(),
            prefixCaseInsensitive.Freeze(),
            SortRoutes(regex).ToImmutableArray(),
            routes.Count);
    }

    private RouteMatch? Find(
        string path,
        HostValue? host,
        string method,
        ImmutableArray<Guid>.Builder timeoutIds,
        MatchConditionFlags flags)
    {
        var exactResult = FindExact(_exact, path, path, host, method, flags);
        if (exactResult is not null)
        {
            return exactResult;
        }

        var exactCiResult = FindExact(_exactCaseInsensitive, FoldOrdinal(path), path, host, method, flags);
        if (exactCiResult is not null)
        {
            return exactCiResult;
        }

        var prefixResult = FindPrefix(_prefix, path, path, host, method, flags);
        if (prefixResult is not null)
        {
            return prefixResult;
        }

        var prefixCiResult = FindPrefix(_prefixCaseInsensitive, FoldOrdinal(path), path, host, method, flags);
        if (prefixCiResult is not null)
        {
            return prefixCiResult;
        }

        foreach (var route in _regex)
        {
            if (route.Regex is null)
            {
                continue;
            }

            bool matched;
            try
            {
                matched = route.Regex.IsMatch(path);
            }
            catch (RegexMatchTimeoutException)
            {
                timeoutIds.Add(route.Id);
                continue;
            }

            if (!matched)
            {
                continue;
            }

            flags.PathCandidateSeen = true;
            if (!route.MatchesConditions(host, method, flags))
            {
                continue;
            }

            return route.CreateMatch(path, path);
        }

        return null;
    }

    private static RouteMatch? FindExact(
        ImmutableDictionary<string, ImmutableArray<CompiledRoute>> index,
        string key,
        string matchPath,
        HostValue? host,
        string method,
        MatchConditionFlags flags)
    {
        if (!index.TryGetValue(key, out var routes))
        {
            return null;
        }

        foreach (var route in routes)
        {
            flags.PathCandidateSeen = true;
            if (route.MatchesConditions(host, method, flags))
            {
                return route.CreateMatch(matchPath, matchPath);
            }
        }

        return null;
    }

    private static RouteMatch? FindPrefix(
        PrefixTrie trie,
        string lookupPath,
        string matchPath,
        HostValue? host,
        string method,
        MatchConditionFlags flags)
    {
        foreach (var route in trie.GetCandidates(lookupPath))
        {
            if (!route.TryMatchPrefix(matchPath, out var matchedText))
            {
                continue;
            }

            flags.PathCandidateSeen = true;
            if (route.MatchesConditions(host, method, flags))
            {
                return route.CreateMatch(matchPath, matchedText);
            }
        }

        return null;
    }

    private static bool TryNormalizeMethod(string? value, out string method)
    {
        method = string.Empty;
        if (!RouteMatchSnapshot.IsRequestMethodToken(value))
        {
            return false;
        }

        method = value!.ToUpperInvariant();
        return true;
    }

    private static void Add(Dictionary<string, List<CompiledRoute>> dictionary, string key, CompiledRoute route)
    {
        if (!dictionary.TryGetValue(key, out var routes))
        {
            routes = new List<CompiledRoute>();
            dictionary.Add(key, routes);
        }

        routes.Add(route);
    }

    private static ImmutableDictionary<string, ImmutableArray<CompiledRoute>> Freeze(
        Dictionary<string, List<CompiledRoute>> source) =>
        source.ToImmutableDictionary(
            pair => pair.Key,
            pair => SortRoutes(pair.Value).ToImmutableArray(),
            StringComparer.Ordinal);

    private static IEnumerable<CompiledRoute> SortRoutes(IEnumerable<CompiledRoute> routes) =>
        routes.OrderBy(route => route, CompiledRouteComparer.Instance);

    private static string FoldOrdinal(string value)
    {
        var chars = value.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            chars[index] = char.ToUpperInvariant(chars[index]);
        }

        return new string(chars);
    }

    internal static bool IsRequestMethodToken(string? value) =>
        value is not null && IsMethodToken(value);

    private static bool IsMethodToken(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character > 127 ||
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~'))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed class MatchConditionFlags
{
    internal bool PathCandidateSeen { get; set; }
    internal bool HostMismatch { get; set; }
    internal bool MethodMismatch { get; set; }
    internal bool HostSatisfiedMethodMismatch { get; set; }
    internal bool MethodSatisfiedHostMismatch { get; set; }

    internal RouteNoMatchReason GetReason()
    {
        if (!PathCandidateSeen)
        {
            return RouteNoMatchReason.NoRoute;
        }

        if (HostMismatch && MethodMismatch && !HostSatisfiedMethodMismatch && !MethodSatisfiedHostMismatch)
        {
            return RouteNoMatchReason.ConditionMismatch;
        }

        if (MethodMismatch && !HostMismatch)
        {
            return RouteNoMatchReason.MethodMismatch;
        }

        if (HostMismatch && !MethodMismatch)
        {
            return RouteNoMatchReason.HostMismatch;
        }

        if (HostSatisfiedMethodMismatch)
        {
            return RouteNoMatchReason.MethodMismatch;
        }

        if (MethodSatisfiedHostMismatch)
        {
            return RouteNoMatchReason.HostMismatch;
        }

        return RouteNoMatchReason.ConditionMismatch;
    }
}

internal sealed class RouteCandidateInput
{
    private RouteCandidateInput(
        Guid id,
        bool enabled,
        RouteMatcherType matcherType,
        string? pattern,
        ImmutableArray<string> hostPatterns,
        ImmutableArray<string> methods,
        RouteTargetData? target,
        ForwardingData? forwarding,
        int priority,
        DateTimeOffset createdAt)
    {
        Id = id;
        Enabled = enabled;
        MatcherType = matcherType;
        Pattern = pattern;
        HostPatterns = hostPatterns;
        Methods = methods;
        Target = target;
        Forwarding = forwarding;
        Priority = priority;
        CreatedAt = createdAt;
    }

    internal Guid Id { get; }
    internal bool Enabled { get; }
    internal RouteMatcherType MatcherType { get; }
    internal string? Pattern { get; }
    internal ImmutableArray<string> HostPatterns { get; }
    internal ImmutableArray<string> Methods { get; }
    internal RouteTargetData? Target { get; }
    internal ForwardingData? Forwarding { get; }
    internal int Priority { get; }
    internal DateTimeOffset CreatedAt { get; }

    internal static RouteCandidateInput Missing() => new(
        Guid.Empty,
        false,
        (RouteMatcherType)(-1),
        null,
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        null,
        null,
        0,
        DateTimeOffset.MinValue);

    internal static RouteCandidateInput FromContract(RouteConfiguration route) => new(
        route.Id,
        route.Enabled,
        route.Matcher.Type,
        route.Matcher.Pattern,
        Copy(route.Matcher.HostPatterns),
        Copy(route.Matcher.Methods),
        RouteTargetData.FromContract(route.Target),
        ForwardingData.FromContract(route.Forwarding),
        route.Priority,
        route.CreatedAt);

    internal static RouteCandidateInput FromDomain(RouteDefinition route) => new(
        route.Id,
        route.Enabled,
        (RouteMatcherType)route.Matcher.Kind,
        route.Matcher.Pattern,
        Copy(route.Matcher.HostPatterns),
        Copy(route.Matcher.Methods),
        RouteTargetData.FromDomain(route.Target),
        ForwardingData.FromDomain(route.Forwarding),
        route.Priority,
        route.CreatedAt);

    private static ImmutableArray<string> Copy(ImmutableArray<string> values) =>
        values.IsDefaultOrEmpty ? ImmutableArray<string>.Empty : ImmutableArray.CreateRange(values);
}

internal sealed class ForwardingData
{
    internal ForwardingMode Mode { get; }
    internal string? ReplaceTemplate { get; }

    private ForwardingData(ForwardingMode mode, string? replaceTemplate)
    {
        Mode = mode;
        ReplaceTemplate = replaceTemplate;
    }

    internal static ForwardingData? FromContract(ForwardingConfiguration? forwarding) => forwarding is null
        ? null
        : new ForwardingData(forwarding.Mode, forwarding.ReplaceTemplate);

    internal static ForwardingData? FromDomain(ForwardingOptions? forwarding) => forwarding is null
        ? null
        : new ForwardingData((ForwardingMode)forwarding.Kind, forwarding.ReplaceTemplate);
}

internal class RouteTargetData
{
    internal RouteTargetType Type { get; }

    internal RouteTargetData(RouteTargetType type) => Type = type;

    internal static RouteTargetData? FromContract(RouteTargetConfiguration? target) => target switch
    {
        MicroserviceRouteTargetConfiguration microservice => new MicroserviceTargetData(microservice.ServiceId),
        StaticFileRouteTargetConfiguration staticFile => new StaticFileTargetData(staticFile.RootPath),
        ExtensionHandlerRouteTargetConfiguration extension => new ExtensionTargetData(extension.HandlerId),
        _ => null
    };

    internal static RouteTargetData? FromDomain(RouteTarget? target) => target switch
    {
        MicroserviceRouteTarget microservice => new MicroserviceTargetData(microservice.ServiceId),
        StaticFileRouteTarget staticFile => new StaticFileTargetData(staticFile.RootPath),
        ExtensionHandlerRouteTarget extension => new ExtensionTargetData(extension.HandlerId),
        _ => null
    };
}

internal sealed class MicroserviceTargetData : RouteTargetData
{
    internal MicroserviceTargetData(Guid serviceId) : base(RouteTargetType.Microservice) => ServiceId = serviceId;
    internal Guid ServiceId { get; }
}

internal sealed class StaticFileTargetData : RouteTargetData
{
    internal StaticFileTargetData(string rootPath) : base(RouteTargetType.StaticFile) => RootPath = rootPath;
    internal string RootPath { get; }
}

internal sealed class ExtensionTargetData : RouteTargetData
{
    internal ExtensionTargetData(string handlerId) : base(RouteTargetType.ExtensionHandler) => HandlerId = handlerId;
    internal string HandlerId { get; }
}

internal sealed class CompiledRoute
{
    internal CompiledRoute(
        Guid id,
        DateTimeOffset createdAt,
        int priority,
        RouteMatcherType matcherType,
        string normalizedPattern,
        bool isRawPrefix,
        Regex? regex,
        ImmutableArray<HostPattern> hosts,
        ImmutableArray<string> methods,
        RouteTargetData target,
        ForwardingMode forwardingMode,
        string? replaceTemplate)
    {
        Id = id;
        CreatedAt = createdAt;
        Priority = priority;
        MatcherType = matcherType;
        NormalizedPattern = normalizedPattern;
        IsRawPrefix = isRawPrefix;
        Regex = regex;
        Hosts = hosts;
        Methods = methods;
        Target = target;
        ForwardingMode = forwardingMode;
        ReplaceTemplate = replaceTemplate;
    }

    internal Guid Id { get; }
    internal DateTimeOffset CreatedAt { get; }
    internal int Priority { get; }
    internal RouteMatcherType MatcherType { get; }
    internal string NormalizedPattern { get; }
    internal bool IsRawPrefix { get; }
    internal Regex? Regex { get; }
    internal ImmutableArray<HostPattern> Hosts { get; }
    internal ImmutableArray<string> Methods { get; }
    internal RouteTargetData Target { get; }
    internal ForwardingMode ForwardingMode { get; }
    internal string? ReplaceTemplate { get; }

    internal bool MatchesConditions(HostValue? host, string method, MatchConditionFlags flags)
    {
        var hostMatches = Hosts.IsDefaultOrEmpty || (host is not null && Hosts.Any(pattern => pattern.Matches(host!)));
        var methodMatches = Methods.IsDefaultOrEmpty || Methods.Contains(method, StringComparer.Ordinal);

        if (!hostMatches)
        {
            flags.HostMismatch = true;
        }

        if (!methodMatches)
        {
            flags.MethodMismatch = true;
        }

        if (hostMatches && !methodMatches)
        {
            flags.HostSatisfiedMethodMismatch = true;
        }

        if (methodMatches && !hostMatches)
        {
            flags.MethodSatisfiedHostMismatch = true;
        }

        return hostMatches && methodMatches;
    }

    internal bool TryMatchPrefix(string path, out string matchedText)
    {
        matchedText = string.Empty;
        var comparison = MatcherType == RouteMatcherType.PrefixCaseInsensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!path.StartsWith(NormalizedPattern, comparison))
        {
            return false;
        }

        if (!IsRawPrefix &&
            !NormalizedPattern.EndsWith('/') &&
            path.Length > NormalizedPattern.Length &&
            path[NormalizedPattern.Length] != '/')
        {
            return false;
        }

        matchedText = path[..NormalizedPattern.Length];
        return true;
    }

    internal RouteMatch CreateMatch(string normalizedPath, string matchedText) => new(
        Id,
        new RouteTargetReference(
            Target.Type,
            Target is MicroserviceTargetData microservice ? microservice.ServiceId : null,
            Target is StaticFileTargetData staticFile ? staticFile.RootPath : null,
            Target is ExtensionTargetData extension ? extension.HandlerId : null),
        ForwardingMode,
        ReplaceTemplate,
        normalizedPath,
        matchedText);
}

internal sealed class CompiledRouteComparer : IComparer<CompiledRoute>
{
    internal static readonly CompiledRouteComparer Instance = new();

    public int Compare(CompiledRoute? left, CompiledRoute? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var priority = left.Priority.CompareTo(right.Priority);
        if (priority != 0)
        {
            return -priority;
        }

        if (left.MatcherType != RouteMatcherType.Regex)
        {
            var specificity = left.NormalizedPattern.Length.CompareTo(right.NormalizedPattern.Length);
            if (specificity != 0)
            {
                return -specificity;
            }
        }

        var created = left.CreatedAt.CompareTo(right.CreatedAt);
        return created != 0 ? created : RouteMatchSnapshotBuilder.CompareGuidLexically(left.Id, right.Id);
    }
}

internal sealed class HostPattern
{
    private HostPattern(string value, bool wildcard)
    {
        Value = value;
        IsWildcard = wildcard;
    }

    internal string Value { get; }
    internal bool IsWildcard { get; }

    internal static bool TryCreate(string? value, out HostPattern? pattern)
    {
        pattern = null;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
        {
            return false;
        }

        var wildcard = value.StartsWith("*.", StringComparison.Ordinal);
        var hostText = wildcard ? value[2..] : value;
        if (hostText.Contains('*'))
        {
            return false;
        }

        if (!HostValue.TryParse(hostText, out var normalized, out var isIp) || (wildcard && isIp))
        {
            return false;
        }

        pattern = new HostPattern(normalized, wildcard);
        return true;
    }

    internal bool Matches(HostValue host) => IsWildcard
        ? host.Value.Length > Value.Length + 1 &&
          host.Value.EndsWith($".{Value}", StringComparison.Ordinal) &&
          host.Value[..^(Value.Length + 1)].Length > 0
        : host.Value.Equals(Value, StringComparison.Ordinal);
}

internal sealed class HostValue
{
    private HostValue(string value)
    {
        Value = value;
    }

    internal string Value { get; }

    internal static bool TryCreate(string? input, out HostValue? value, out bool isValid)
    {
        if (input is null)
        {
            value = null;
            isValid = true;
            return true;
        }

        if (TryParse(input, out var normalized, out _))
        {
            value = new HostValue(normalized);
            isValid = true;
            return true;
        }

        value = null;
        isValid = false;
        return false;
    }

    internal static bool TryParse(string input, out string normalized, out bool isIp)
    {
        normalized = string.Empty;
        isIp = false;
        if (string.IsNullOrWhiteSpace(input) || input.Any(char.IsWhiteSpace) || input.Any(char.IsControl))
        {
            return false;
        }

        var host = input;
        if (input[0] == '[')
        {
            var close = input.IndexOf(']');
            if (close <= 1 ||
                (close + 1 < input.Length &&
                 (input[close + 1] != ':' || !TryPort(input[(close + 1)..]))))
            {
                return false;
            }

            host = input[1..close];
        }
        else
        {
            var colonCount = input.Count(static character => character == ':');
            if (colonCount > 1)
            {
                host = input;
            }
            else if (colonCount == 1)
            {
                var colon = input.IndexOf(':');
                if (!TryPort(input[(colon + 1)..]))
                {
                    return false;
                }

                host = input[..colon];
            }
        }

        if (IPAddress.TryParse(host, out var address))
        {
            if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            {
                return false;
            }

            normalized = address.ToString().ToUpperInvariant();
            isIp = true;
            return true;
        }

        try
        {
            var idn = new IdnMapping { UseStd3AsciiRules = true };
            var ascii = idn.GetAscii(host);
            if (ascii.EndsWith('.'))
            {
                ascii = ascii[..^1];
            }

            if (ascii.Length == 0 || ascii.Length > 253)
            {
                return false;
            }

            foreach (var label in ascii.Split('.', StringSplitOptions.None))
            {
                if (label.Length is 0 or > 63 ||
                    label[0] == '-' ||
                    label[^1] == '-' ||
                    label.Any(static character =>
                        !(char.IsAsciiLetterOrDigit(character) || character == '-')))
                {
                    return false;
                }
            }

            normalized = ascii.ToUpperInvariant();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryPort(string value)
    {
        if (value.Length < 2 || value[0] != ':' ||
            value[1..].Any(character => !char.IsAsciiDigit(character)) ||
            !int.TryParse(value[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            return false;
        }

        return port is >= 0 and <= 65535;
    }
}

internal sealed class PrefixTrieBuilder
{
    private readonly PrefixTrieBuilderNode _root = new();

    internal void Add(string key, CompiledRoute route)
    {
        var node = _root;
        foreach (var character in key)
        {
            if (!node.Children.TryGetValue(character, out var child))
            {
                child = new PrefixTrieBuilderNode();
                node.Children.Add(character, child);
            }

            node = child;
        }

        node.Routes.Add(route);
    }

    internal PrefixTrie Freeze() => new(FreezeNode(_root));

    private static PrefixTrieNode FreezeNode(PrefixTrieBuilderNode source) => new(
        source.Children.ToImmutableDictionary(pair => pair.Key, pair => FreezeNode(pair.Value)),
        source.Routes.OrderBy(route => route, CompiledRouteComparer.Instance).ToImmutableArray());
}

internal sealed class PrefixTrieBuilderNode
{
    internal Dictionary<char, PrefixTrieBuilderNode> Children { get; } = new();
    internal List<CompiledRoute> Routes { get; } = new();
}

internal sealed class PrefixTrie
{
    private readonly PrefixTrieNode _root;

    internal PrefixTrie(PrefixTrieNode root) => _root = root;

    internal IEnumerable<CompiledRoute> GetCandidates(string path)
    {
        var routes = new List<CompiledRoute>();
        var node = _root;
        routes.AddRange(node.Routes);

        foreach (var character in path)
        {
            if (!node.Children.TryGetValue(character, out var child))
            {
                break;
            }

            node = child;
            routes.AddRange(node.Routes);
        }

        return routes.OrderBy(route => route, CompiledRouteComparer.Instance);
    }
}

internal sealed class PrefixTrieNode
{
    internal PrefixTrieNode(
        ImmutableDictionary<char, PrefixTrieNode> children,
        ImmutableArray<CompiledRoute> routes)
    {
        Children = children;
        Routes = routes;
    }

    internal ImmutableDictionary<char, PrefixTrieNode> Children { get; }
    internal ImmutableArray<CompiledRoute> Routes { get; }
}
