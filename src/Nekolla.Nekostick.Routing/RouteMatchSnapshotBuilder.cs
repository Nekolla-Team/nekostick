using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Routing;

/// <summary>Builds immutable route matching snapshots from Contract or Domain models.</summary>
public static class RouteMatchSnapshotBuilder
{
    /// <summary>
    /// Per-match budget for route regexes. The NonBacktracking engine matches in linear time,
    /// so this only guards against degenerate automata; it must also tolerate heavily loaded
    /// machines where a trivial match can be starved well beyond a few tens of milliseconds.
    /// </summary>
    private const int RegexMatchTimeoutMilliseconds = 250;

    /// <summary>Builds a snapshot from immutable Contract route values.</summary>
    /// <param name="routes">The route configurations to validate and compile.</param>
    /// <returns>A snapshot or safe configuration errors. Invalid routes are never included.</returns>
    public static RouteSnapshotBuildResult Build(IEnumerable<RouteConfiguration> routes) =>
        Build(routes, DotNetRouteRegexEvaluator.Instance);

    /// <summary>Builds a snapshot from immutable Contract route values with an internal regex seam.</summary>
    /// <param name="routes">The route configurations to validate and compile.</param>
    /// <param name="regexEvaluator">The deterministic evaluator used for compiled regex routes.</param>
    /// <returns>A snapshot or safe configuration errors. Invalid routes are never included.</returns>
    internal static RouteSnapshotBuildResult Build(
        IEnumerable<RouteConfiguration> routes,
        IRouteRegexEvaluator regexEvaluator)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(regexEvaluator);
        var inputs = new List<RouteCandidateInput>();

        foreach (var route in routes)
        {
            inputs.Add(route is null
                ? RouteCandidateInput.Missing()
                : RouteCandidateInput.FromContract(route));
        }

        return BuildCore(inputs, regexEvaluator);
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

        return BuildCore(inputs, DotNetRouteRegexEvaluator.Instance);
    }

    private static RouteSnapshotBuildResult BuildCore(
        List<RouteCandidateInput> inputs,
        IRouteRegexEvaluator regexEvaluator)
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

        return RouteSnapshotBuildResult.Success(RouteMatchSnapshot.Create(candidates, regexEvaluator));
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
                        TimeSpan.FromMilliseconds(RegexMatchTimeoutMilliseconds));
                    // Pay the one-time engine construction cost at build time so the first
                    // request-path evaluation cannot burn its match budget on lazy setup.
                    regex.IsMatch(string.Empty);
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
                !ForwardedPathContract.IsValidReplacementTemplate(
                    input.Forwarding.ReplaceTemplate,
                    regex,
                    input.MatcherType))
            {
                errors.Add(new RouteConfigurationError(
                    input.Id,
                    RouteConfigurationErrorCode.InvalidReplacementTemplate));
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

    private static bool ContainsUnsafePatternCharacter(string value) => value.Any(char.IsControl);

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
