using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Routing;

/// <summary>Contains immutable route indexes and performs pure route selection.</summary>
public sealed class RouteMatchSnapshot
{
    private readonly ImmutableDictionary<string, ImmutableArray<CompiledRoute>> _exact;
    private readonly ImmutableDictionary<string, ImmutableArray<CompiledRoute>> _exactCaseInsensitive;
    private readonly PrefixTrie _prefix;
    private readonly PrefixTrie _prefixCaseInsensitive;
    private readonly ImmutableArray<CompiledRoute> _regex;
    private readonly IRouteRegexEvaluator _regexEvaluator;

    internal RouteMatchSnapshot(
        ImmutableDictionary<string, ImmutableArray<CompiledRoute>> exact,
        ImmutableDictionary<string, ImmutableArray<CompiledRoute>> exactCaseInsensitive,
        PrefixTrie prefix,
        PrefixTrie prefixCaseInsensitive,
        ImmutableArray<CompiledRoute> regex,
        IRouteRegexEvaluator regexEvaluator,
        int routeCount)
    {
        _exact = exact;
        _exactCaseInsensitive = exactCaseInsensitive;
        _prefix = prefix;
        _prefixCaseInsensitive = prefixCaseInsensitive;
        _regex = regex;
        _regexEvaluator = regexEvaluator;
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
        var first = Find(normalized.NormalizedPath, normalized.NormalizedPath, host, method, timeoutIds, flags);
        if (first is not null)
        {
            return RouteMatchResult.Matched(first, timeoutIds.ToImmutable());
        }

        if (!normalized.NormalizedPath.Equals("/", StringComparison.Ordinal) &&
            !normalized.NormalizedPath.EndsWith('/'))
        {
            var alternatePath = normalized.NormalizedPath + "/";
            var alternate = Find(alternatePath, normalized.NormalizedPath, host, method, timeoutIds, flags);
            if (alternate is not null)
            {
                return RouteMatchResult.Matched(alternate, timeoutIds.ToImmutable());
            }
        }

        return RouteMatchResult.NoMatch(flags.GetReason(), timeoutIds.ToImmutable());
    }

    internal static RouteMatchSnapshot Create(
        List<CompiledRoute> routes,
        IRouteRegexEvaluator regexEvaluator)
    {
        ArgumentNullException.ThrowIfNull(regexEvaluator);
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
            regexEvaluator,
            routes.Count);
    }

    private RouteMatch? Find(
        string lookupPath,
        string normalizedPath,
        HostValue? host,
        string method,
        ImmutableArray<Guid>.Builder timeoutIds,
        MatchConditionFlags flags)
    {
        var exactResult = FindExact(_exact, lookupPath, lookupPath, normalizedPath, host, method, flags);
        if (exactResult is not null)
        {
            return exactResult;
        }

        var exactCiResult = FindExact(
            _exactCaseInsensitive,
            FoldOrdinal(lookupPath),
            lookupPath,
            normalizedPath,
            host,
            method,
            flags);
        if (exactCiResult is not null)
        {
            return exactCiResult;
        }

        var prefixResult = FindPrefix(_prefix, lookupPath, lookupPath, normalizedPath, host, method, flags);
        if (prefixResult is not null)
        {
            return prefixResult;
        }

        var prefixCiResult = FindPrefix(
            _prefixCaseInsensitive,
            FoldOrdinal(lookupPath),
            lookupPath,
            normalizedPath,
            host,
            method,
            flags);
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

            var evaluation = _regexEvaluator.EvaluateMatch(route.Id, route.Regex, lookupPath);
            if (evaluation.Outcome == RouteRegexEvaluationOutcome.TimedOut)
            {
                timeoutIds.Add(route.Id);
                continue;
            }

            if (evaluation.Outcome != RouteRegexEvaluationOutcome.Matched)
            {
                continue;
            }

            flags.PathCandidateSeen = true;
            if (!route.MatchesConditions(host, method, flags))
            {
                continue;
            }

            return route.CreateMatch(normalizedPath, lookupPath, evaluation.Match);
        }

        return null;
    }

    private static RouteMatch? FindExact(
        ImmutableDictionary<string, ImmutableArray<CompiledRoute>> index,
        string key,
        string matchPath,
        string normalizedPath,
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
                return route.CreateMatch(normalizedPath, matchPath);
            }
        }

        return null;
    }

    private static RouteMatch? FindPrefix(
        PrefixTrie trie,
        string lookupPath,
        string matchPath,
        string normalizedPath,
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
                return route.CreateMatch(normalizedPath, matchedText);
            }
        }

        return null;
    }

    private static bool TryNormalizeMethod(string? value, out string method)
    {
        method = string.Empty;
        if (!IsRequestMethodToken(value))
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
