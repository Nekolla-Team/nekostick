using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Routing;

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
        global::Nekolla.Nekostick.Domain.ExtensionHandlerRouteTarget extension => new ExtensionTargetData(extension.HandlerId),
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

    // For every non-Regex matcher, the normalized pattern length is the exact length of the
    // text that the matcher can report as matched. Regex routes deliberately do not use this.
    internal int NonRegexMatchedTextLength => NormalizedPattern.Length;

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

    internal RouteMatch CreateMatch(
        string normalizedPath,
        string matchedText,
        Match? regexMatch = null) => new(
            Id,
            new RouteTargetReference(
                Target.Type,
                Target is MicroserviceTargetData microservice ? microservice.ServiceId : null,
                Target is StaticFileTargetData staticFile ? staticFile.RootPath : null,
                Target is ExtensionTargetData extension ? extension.HandlerId : null),
            ForwardingMode,
            ReplaceTemplate,
            normalizedPath,
            matchedText,
            ForwardedPathContract.Build(
                ForwardingMode,
                MatcherType,
                normalizedPath,
                matchedText,
                ReplaceTemplate,
                regexMatch));
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

        var matcherRank = GetMatcherRank(left.MatcherType).CompareTo(GetMatcherRank(right.MatcherType));
        if (matcherRank != 0)
        {
            return matcherRank;
        }

        var priority = left.Priority.CompareTo(right.Priority);
        if (priority != 0)
        {
            return -priority;
        }

        if (UsesMatchedTextLength(left.MatcherType))
        {
            var specificity = left.NonRegexMatchedTextLength.CompareTo(right.NonRegexMatchedTextLength);
            if (specificity != 0)
            {
                return -specificity;
            }
        }

        var created = left.CreatedAt.CompareTo(right.CreatedAt);
        return created != 0 ? created : RouteMatchSnapshotBuilder.CompareGuidLexically(left.Id, right.Id);
    }

    private static int GetMatcherRank(RouteMatcherType matcherType) => matcherType switch
    {
        RouteMatcherType.Exact => 0,
        RouteMatcherType.ExactCaseInsensitive => 1,
        RouteMatcherType.Prefix => 2,
        RouteMatcherType.PrefixCaseInsensitive => 3,
        RouteMatcherType.Regex => 4,
        _ => int.MaxValue
    };

    private static bool UsesMatchedTextLength(RouteMatcherType matcherType) => matcherType is
        RouteMatcherType.Exact or
        RouteMatcherType.ExactCaseInsensitive or
        RouteMatcherType.Prefix or
        RouteMatcherType.PrefixCaseInsensitive;
}
