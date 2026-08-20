using System.Text.Json;
using System.Text.RegularExpressions;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Persistence;

internal static class HostConfigurationRouteValidator
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailer",
        "transfer-encoding",
        "upgrade"
    };

    internal static void Validate(
        RouteConfiguration? value,
        GlobalSettingsConfiguration globalSettings,
        HashSet<Guid> serviceIds)
    {
        if (value is null || value.Matcher is null || value.Target is null || value.Forwarding is null ||
            !HostConfigurationValueValidator.IsUuidV7(value.Id) || value.Version < 0 ||
            !Enum.IsDefined(value.Matcher.Type) || !Enum.IsDefined(value.Target.Type) ||
            !Enum.IsDefined(value.Forwarding.Mode))
        {
            HostConfigurationValueValidator.Throw();
        }

        var matcher = value.Matcher;
        if (matcher is null || string.IsNullOrWhiteSpace(matcher.Pattern) ||
            matcher.Pattern.Length > PersistenceDatabaseDefaults.MaxRoutePatternLength ||
            ContainsControlCharacter(matcher.Pattern))
        {
            HostConfigurationValueValidator.Throw();
        }

        ValidateConditions(matcher);
        switch (matcher.Type)
        {
            case RouteMatcherType.Exact:
            case RouteMatcherType.ExactCaseInsensitive:
                if (!IsValidRoutePathPattern(matcher.Pattern) || matcher.Pattern.Contains('*'))
                {
                    HostConfigurationValueValidator.Throw();
                }

                break;
            case RouteMatcherType.Prefix:
            case RouteMatcherType.PrefixCaseInsensitive:
                ValidatePrefixPattern(matcher.Pattern);
                break;
            case RouteMatcherType.Regex:
                try
                {
                    _ = new Regex(
                        $"\\A(?:{matcher.Pattern})\\z",
                        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                        TimeSpan.FromMilliseconds(HostConfigurationValueValidator.RegexTimeoutMilliseconds));
                }
                catch (ArgumentException)
                {
                    HostConfigurationValueValidator.Throw();
                }
                catch (NotSupportedException)
                {
                    HostConfigurationValueValidator.Throw();
                }

                break;
            default:
                HostConfigurationValueValidator.Throw();
                break;
        }

        ValidateForwarding(value.Forwarding, matcher);
        HostConfigurationRatePolicyValidator.Validate(value.ClientIpRatePolicy);
        ValidateResourceOverrides(value, globalSettings);
        ValidateHeaderRewrites(value.RequestHeaderRewrites, requestSide: true);
        ValidateHeaderRewrites(value.ResponseHeaderRewrites, requestSide: false);
        HostConfigurationValueValidator.EnsureSerializedJson(matcher.HostPatterns, JsonValueKind.Array);
        HostConfigurationValueValidator.EnsureSerializedJson(matcher.Methods, JsonValueKind.Array);
        HostConfigurationValueValidator.EnsureSerializedJson(value.RequestHeaderRewrites, JsonValueKind.Array);
        HostConfigurationValueValidator.EnsureSerializedJson(value.ResponseHeaderRewrites, JsonValueKind.Array);
        _ = HostConfigurationValueValidator.NormalizeJson(value.MetadataJson, JsonValueKind.Object);

        switch (value.Target)
        {
            case MicroserviceRouteTargetConfiguration microservice:
                if (!HostConfigurationValueValidator.IsUuidV7(microservice.ServiceId) ||
                    !serviceIds.Contains(microservice.ServiceId))
                {
                    HostConfigurationValueValidator.Throw();
                }

                break;
            case StaticFileRouteTargetConfiguration staticFile:
                if (!HostConfigurationValueValidator.IsSafeAbsolutePath(
                        staticFile.RootPath,
                        HostConfigurationValueValidator.MaxTextLength))
                {
                    HostConfigurationValueValidator.Throw();
                }

                break;
            case ExtensionHandlerRouteTargetConfiguration handler:
                if (!HostConfigurationValueValidator.IsSafeText(
                        handler.HandlerId,
                        HostConfigurationValueValidator.MaxHandlerIdLength))
                {
                    HostConfigurationValueValidator.Throw();
                }

                break;
            default:
                HostConfigurationValueValidator.Throw();
                break;
        }
    }

    private static void ValidateResourceOverrides(
        RouteConfiguration value,
        GlobalSettingsConfiguration globalSettings)
    {
        if (value.ProxyRetries is not null &&
            !ProxyRetryPersistenceDefaults.IsValidRetryPolicy(value.ProxyRetries))
        {
            HostConfigurationValueValidator.Throw();
        }

        if (value.MaxRequestBodyBytes is { } maxRequestBodyBytes &&
            (maxRequestBodyBytes <= 0 ||
             maxRequestBodyBytes > GlobalSettingsConfiguration.HardMaximumRequestBodyBytes ||
             maxRequestBodyBytes > globalSettings.MaxRequestBodyBytes))
        {
            HostConfigurationValueValidator.Throw();
        }

        if (value.MaxRequestHeaderBytes is { } maxRequestHeaderBytes &&
            (maxRequestHeaderBytes <= 0 ||
             maxRequestHeaderBytes > GlobalSettingsConfiguration.HardMaximumRequestHeaderBytes ||
             maxRequestHeaderBytes > globalSettings.MaxRequestHeaderBytes))
        {
            HostConfigurationValueValidator.Throw();
        }

        if (value.MaxConcurrentRequests is { } maxConcurrentRequests &&
            (maxConcurrentRequests <= 0 ||
             maxConcurrentRequests > globalSettings.MaxConcurrentRequests))
        {
            HostConfigurationValueValidator.Throw();
        }

        if (value.RequestReadTimeout is { } requestReadTimeout &&
            (requestReadTimeout <= TimeSpan.Zero ||
             requestReadTimeout.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
             requestReadTimeout > TimeSpan.FromDays(1) ||
             requestReadTimeout > globalSettings.RequestReadTimeout))
        {
            HostConfigurationValueValidator.Throw();
        }
    }

    private static void ValidateConditions(RouteMatcherConfiguration value)
    {
        HostConfigurationValueValidator.ValidateUniqueText(
            value.HostPatterns,
            HostConfigurationValueValidator.MaxTextLength);
        HostConfigurationValueValidator.ValidateUniqueText(value.Methods, 32);
        foreach (var host in value.HostPatterns)
        {
            if (!HostConfigurationValueValidator.IsValidHostPattern(host))
            {
                HostConfigurationValueValidator.Throw();
            }
        }

        foreach (var method in value.Methods)
        {
            if (!HostConfigurationValueValidator.IsValidHttpToken(method))
            {
                HostConfigurationValueValidator.Throw();
            }
        }
    }

    private static void ValidatePrefixPattern(string pattern)
    {
        if (!IsValidRoutePathPattern(pattern.TrimEnd('*')) || pattern.Count(value => value == '*') > 1 ||
            (pattern.Contains('*') && pattern[^1] != '*') || pattern.Contains("\\*", StringComparison.Ordinal))
        {
            HostConfigurationValueValidator.Throw();
        }
    }

    private static void ValidateForwarding(
        ForwardingConfiguration value,
        RouteMatcherConfiguration matcher)
    {
        if (value is null || !Enum.IsDefined(value.Mode))
        {
            HostConfigurationValueValidator.Throw();
        }

        if (value.Mode == ForwardingMode.Strip &&
            (matcher.Type == RouteMatcherType.Regex ||
             ((matcher.Type is RouteMatcherType.Prefix or RouteMatcherType.PrefixCaseInsensitive) &&
                  IsRawPrefixPattern(matcher.Pattern))))
        {
            HostConfigurationValueValidator.Throw();
        }

        if (value.Mode == ForwardingMode.Replace)
        {
            if (string.IsNullOrWhiteSpace(value.ReplaceTemplate) ||
                value.ReplaceTemplate.Length > HostConfigurationValueValidator.MaxTextLength ||
                ContainsControlCharacter(value.ReplaceTemplate) ||
                value.ReplaceTemplate.Contains('?') ||
                value.ReplaceTemplate.Contains('#') ||
                value.ReplaceTemplate.Contains('\\') ||
                !HasAbsoluteTemplatePrefix(value.ReplaceTemplate) ||
                !HasValidTemplateTokens(value.ReplaceTemplate, matcher))
            {
                HostConfigurationValueValidator.Throw();
            }
        }
        else if (value.ReplaceTemplate is not null)
        {
            HostConfigurationValueValidator.Throw();
        }
    }

    private static void ValidateHeaderRewrites(
        IEnumerable<HeaderRewriteConfiguration> values,
        bool requestSide)
    {
        if (values is null)
        {
            HostConfigurationValueValidator.Throw();
        }

        foreach (var rewrite in values)
        {
            if (rewrite is null || !Enum.IsDefined(rewrite.Operation) ||
                !HostConfigurationValueValidator.IsValidHttpToken(rewrite.Name) ||
                HopByHopHeaders.Contains(rewrite.Name) ||
                IsProtectedHeader(rewrite.Name, rewrite.Operation, requestSide))
            {
                HostConfigurationValueValidator.Throw();
            }

            if (rewrite.Operation == HeaderRewriteOperation.Remove)
            {
                if (rewrite.Value is not null)
                {
                    HostConfigurationValueValidator.Throw();
                }
            }
            else if (!HeaderRewriteTemplate.TryCompile(rewrite.Value, out _))
            {
                HostConfigurationValueValidator.Throw();
            }
        }
    }

    private static bool IsProtectedHeader(
        string name,
        HeaderRewriteOperation operation,
        bool requestSide) =>
        name.Equals("Host", StringComparison.OrdinalIgnoreCase)
            ? !requestSide || operation != HeaderRewriteOperation.Set
            : name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
              || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
              || name.StartsWith("Proxy", StringComparison.OrdinalIgnoreCase)
              || name.Equals("TE", StringComparison.OrdinalIgnoreCase)
              || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
              || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
              || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
              || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
              || name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
              || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
              || name.Equals("X-Real-IP", StringComparison.OrdinalIgnoreCase);

    private static bool HasValidTemplateTokens(string value, RouteMatcherConfiguration matcher)
    {
        var braceMatches = Regex.Matches(value, "\\{[^{}]*\\}", RegexOptions.CultureInvariant);
        var consumedEnd = 0;
        foreach (Match match in braceMatches)
        {
            if (value[consumedEnd..match.Index].Contains('{') ||
                value[consumedEnd..match.Index].Contains('}'))
            {
                return false;
            }

            consumedEnd = match.Index + match.Length;
        }

        if (value[consumedEnd..].Contains('{') || value[consumedEnd..].Contains('}'))
        {
            return false;
        }

        foreach (Match match in braceMatches)
        {
            if (match.Value is not "{path}" and not "{match}")
            {
                return false;
            }
        }

        var groupNumbers = Array.Empty<int>();
        if (matcher.Type == RouteMatcherType.Regex)
        {
            try
            {
                var regex = new Regex(
                    $"\\A(?:{matcher.Pattern})\\z",
                    RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                    TimeSpan.FromMilliseconds(HostConfigurationValueValidator.RegexTimeoutMilliseconds));
                groupNumbers = regex.GetGroupNumbers();
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        for (var index = value.IndexOf('$'); index >= 0; index = value.IndexOf('$', index + 1))
        {
            var match = Regex.Match(
                value[index..],
                "^\\$[0-9]+",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            if (!match.Success || matcher.Type != RouteMatcherType.Regex ||
                !int.TryParse(match.Value.AsSpan(1), out var groupNumber) ||
                !groupNumbers.Contains(groupNumber))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasAbsoluteTemplatePrefix(string value) =>
        value.StartsWith('/') ||
        value.StartsWith("{path}", StringComparison.Ordinal) ||
        value.StartsWith("{match}", StringComparison.Ordinal) ||
        (value.StartsWith("$0", StringComparison.Ordinal) &&
         (value.Length == 2 || !char.IsDigit(value[2])));

    private static bool IsAbsoluteRoutePattern(string value) =>
        value.StartsWith('/') && !value.Contains('?');

    private static bool IsValidRoutePathPattern(string value)
    {
        if (!IsAbsoluteRoutePattern(value) || value.Contains('#') ||
            value.Contains('\\'))
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '%' &&
                (index + 2 >= value.Length || !IsHex(value[index + 1]) || !IsHex(value[index + 2])))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool IsRawPrefixPattern(string value) =>
        value.Length > 1 && value.EndsWith('*') && value[^2] != '/';

    private static bool ContainsControlCharacter(string value) => value.Any(char.IsControl);
}
