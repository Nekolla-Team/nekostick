using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Persistence;

/// <summary>
/// Provides the host publish-time semantic gate for contract-only configuration DTOs.
/// </summary>
/// <remarks>
/// This type is stateless and deliberately accepts no persistence objects. The host may
/// call it before publishing a snapshot or submitting a complete change set without
/// depending on EF entities, a DbContext, or database details.
/// </remarks>
public static class HostConfigurationSemanticValidator
{
    private const int MaxTextLength = 4096;
    private const int MaxExtensionIdLength = 128;
    private const int MaxHandlerIdLength = 256;
    private const int MaxEnvironmentKeyLength = 256;
    private const int MaxEnvironmentValueLength = 64 * 1024;
    private const int MaxArgumentLength = 64 * 1024;
    private const int MaxHealthPathLength = 2048;
    private const int RegexTimeoutMilliseconds = 50;

    private static readonly Regex SemanticVersionPattern = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(RegexTimeoutMilliseconds));

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

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>
    /// Validates a complete persisted snapshot for host publication.
    /// </summary>
    /// <param name="snapshot">The contract-only configuration snapshot.</param>
    /// <returns><see langword="true"/> when every semantic and persisted-version rule passes.</returns>
    public static bool TryValidateSnapshot(HostConfigurationSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return false;
        }

        return TryValidate(() =>
        {
            ValidateConfigurationValues(
                snapshot.GlobalSettings,
                snapshot.Routes,
                snapshot.Services,
                snapshot.ExtensionRecords,
                snapshot.ExtensionSettings);
            ValidatePersistedVersions(snapshot);
        });
    }

    /// <summary>
    /// Validates a complete contract-only change set before host publication or persistence.
    /// </summary>
    /// <param name="changes">The complete replacement configuration change set.</param>
    /// <returns><see langword="true"/> when every semantic rule passes.</returns>
    public static bool TryValidateChangeSet(ConfigurationChangeSet? changes)
    {
        if (changes is null)
        {
            return false;
        }

        return TryValidate(() => ValidateConfigurationValues(
            changes.GlobalSettings,
            changes.Routes,
            changes.Services,
            changes.ExtensionRecords,
            changes.ExtensionSettings));
    }

    /// <summary>
    /// Validates one contract-only extension settings DTO for host publication or persistence.
    /// </summary>
    /// <param name="settings">The extension settings DTO.</param>
    /// <returns><see langword="true"/> when the identifier, schema, version, and JSON pass.</returns>
    public static bool TryValidateExtensionSettings(ExtensionSettingsConfiguration? settings) =>
        settings is not null && TryValidate(() => ValidateExtensionSettings(settings));

    internal static bool IsSafeExtensionId(string? value) => IsSafeText(value, MaxExtensionIdLength);

    internal static bool IsUuidV7(Guid value)
    {
        if (value == Guid.Empty)
        {
            return false;
        }

        var text = value.ToString("D");
        return text[14] == '7' && text[19] is '8' or '9' or 'a' or 'b';
    }

    internal static string NormalizeJson(string? value, JsonValueKind? expectedKind)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > PersistenceDatabaseDefaults.MaxJsonBytes)
        {
            throw new ConfigurationValidationException();
        }

        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            if (expectedKind is not null && document.RootElement.ValueKind != expectedKind.Value)
            {
                throw new ConfigurationValidationException();
            }

            return document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            throw new ConfigurationValidationException();
        }
    }

    internal static ImmutableArray<string> DeserializeStringArray(string value)
    {
        _ = NormalizeJson(value, JsonValueKind.Array);
        try
        {
            var result = JsonSerializer.Deserialize<string[]>(value, JsonOptions);
            if (result is null || result.Any(item => item is null))
            {
                throw new ConfigurationValidationException();
            }

            return result.ToImmutableArray();
        }
        catch (JsonException)
        {
            throw new ConfigurationValidationException();
        }
    }

    internal static ImmutableArray<HeaderRewriteConfiguration> DeserializeHeaderRewrites(string value)
    {
        _ = NormalizeJson(value, JsonValueKind.Array);
        try
        {
            var result = JsonSerializer.Deserialize<HeaderRewriteConfiguration[]>(value, JsonOptions);
            return result?.ToImmutableArray() ?? throw new ConfigurationValidationException();
        }
        catch (JsonException)
        {
            throw new ConfigurationValidationException();
        }
    }

    internal static ImmutableDictionary<string, string> DeserializeEnvironment(string value)
    {
        _ = NormalizeJson(value, JsonValueKind.Object);
        try
        {
            var result = JsonSerializer.Deserialize<Dictionary<string, string>>(value, JsonOptions);
            if (result is null)
            {
                throw new ConfigurationValidationException();
            }

            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var pair in result)
            {
                if (!builder.TryAdd(pair.Key, pair.Value))
                {
                    throw new ConfigurationValidationException();
                }

                if (!IsSafeEnvironmentKey(pair.Key) || pair.Value is null)
                {
                    throw new ConfigurationValidationException();
                }
            }

            return builder.ToImmutable();
        }
        catch (JsonException)
        {
            throw new ConfigurationValidationException();
        }
    }

    internal static string SerializeEnvironment(ImmutableDictionary<string, string> value)
    {
        var sorted = value
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return SerializeJson(sorted);
    }

    internal static string SerializeJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The public DTO validation boundary is deliberately fail-closed and never exposes validation exception details.")]
    private static bool TryValidate(Action validation)
    {
        try
        {
            validation();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ValidateConfigurationValues(
        GlobalSettingsConfiguration globalSettings,
        IEnumerable<RouteConfiguration> routes,
        IEnumerable<ServiceConfiguration> services,
        IEnumerable<ExtensionRecordConfiguration> extensionRecords,
        IEnumerable<ExtensionSettingsConfiguration> extensionSettings)
    {
        if (globalSettings is null)
        {
            throw new ConfigurationValidationException();
        }

        ValidateGlobalSettings(globalSettings);
        var routeArray = routes?.ToArray() ?? throw new ConfigurationValidationException();
        var serviceArray = services?.ToArray() ?? throw new ConfigurationValidationException();
        var extensionArray = extensionRecords?.ToArray() ?? throw new ConfigurationValidationException();
        var settingsArray = extensionSettings?.ToArray() ?? throw new ConfigurationValidationException();

        ValidateUniqueIds(routeArray.Select(value => value?.Id ?? Guid.Empty), true);
        ValidateUniqueIds(serviceArray.Select(value => value?.Id ?? Guid.Empty), true);
        ValidateUniqueText(extensionArray.Select(value => value?.ExtensionId), MaxExtensionIdLength);
        ValidateUniqueText(settingsArray.Select(value => value?.ExtensionId), MaxExtensionIdLength);

        foreach (var service in serviceArray)
        {
            ValidateService(service);
        }

        foreach (var extension in extensionArray)
        {
            ValidateExtensionRecord(extension);
        }

        var serviceIds = serviceArray.Select(value => value.Id).ToHashSet();
        var extensionIds = extensionArray
            .Select(value => value.ExtensionId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var route in routeArray)
        {
            ValidateRoute(route, serviceIds, extensionIds);
        }

        foreach (var setting in settingsArray)
        {
            ValidateExtensionSettings(setting);
            if (!extensionIds.Contains(setting.ExtensionId))
            {
                throw new ConfigurationValidationException();
            }
        }
    }

    private static void ValidateGlobalSettings(GlobalSettingsConfiguration value)
    {
        if (value.Version < 0 || value.AutoPortRangeStart is < 1 or > 65535 ||
            value.AutoPortRangeEnd is < 1 or > 65535 ||
            value.AutoPortRangeStart > value.AutoPortRangeEnd ||
            value.MaxRequestBodyBytes <= 0 || value.MaxConcurrentRequests <= 0 ||
            value.ConfigurationPollInterval <= TimeSpan.Zero ||
            value.ConfigurationPollInterval.Ticks % TimeSpan.TicksPerSecond != 0 ||
            value.ConfigurationPollInterval.TotalSeconds > int.MaxValue)
        {
            throw new ConfigurationValidationException();
        }

        var cidrs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cidr in value.TrustedProxyCidrs)
        {
            if (!IsValidCidr(cidr) || !cidrs.Add(cidr))
            {
                throw new ConfigurationValidationException();
            }
        }

        EnsureSerializedJson(value.TrustedProxyCidrs, JsonValueKind.Array);
    }

    private static void ValidatePersistedVersions(HostConfigurationSnapshot snapshot)
    {
        if (snapshot.Version < 1 || snapshot.GlobalSettings.Version < 1 ||
            snapshot.Routes.Any(value => value.Version < 1) ||
            snapshot.Services.Any(value => value.Version < 1) ||
            snapshot.ExtensionRecords.Any(value => value.RecordVersion < 1) ||
            snapshot.ExtensionSettings.Any(value => value.Version < 1))
        {
            throw new ConfigurationValidationException();
        }
    }

    private static void ValidateRoute(
        RouteConfiguration? value,
        HashSet<Guid> serviceIds,
        HashSet<string> extensionIds)
    {
        if (value is null || value.Matcher is null || value.Target is null || value.Forwarding is null ||
            !IsUuidV7(value.Id) || value.Version < 0 ||
            !Enum.IsDefined(value.Matcher.Type) || !Enum.IsDefined(value.Target.Type) ||
            !Enum.IsDefined(value.Forwarding.Mode))
        {
            throw new ConfigurationValidationException();
        }

        var matcher = value.Matcher;
        if (matcher is null || string.IsNullOrWhiteSpace(matcher.Pattern) ||
            matcher.Pattern.Length > PersistenceDatabaseDefaults.MaxRoutePatternLength ||
            ContainsControlCharacter(matcher.Pattern))
        {
            throw new ConfigurationValidationException();
        }

        ValidateRouteConditions(matcher);
        switch (matcher.Type)
        {
            case RouteMatcherType.Exact:
            case RouteMatcherType.ExactCaseInsensitive:
                if (!IsValidRoutePathPattern(matcher.Pattern) || matcher.Pattern.Contains('*'))
                {
                    throw new ConfigurationValidationException();
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
                        TimeSpan.FromMilliseconds(RegexTimeoutMilliseconds));
                }
                catch (ArgumentException)
                {
                    throw new ConfigurationValidationException();
                }
                catch (NotSupportedException)
                {
                    throw new ConfigurationValidationException();
                }

                break;
            default:
                throw new ConfigurationValidationException();
        }

        ValidateForwarding(value.Forwarding, matcher);
        ValidateHeaderRewrites(value.RequestHeaderRewrites);
        ValidateHeaderRewrites(value.ResponseHeaderRewrites);
        EnsureSerializedJson(value.Matcher.HostPatterns, JsonValueKind.Array);
        EnsureSerializedJson(value.Matcher.Methods, JsonValueKind.Array);
        EnsureSerializedJson(value.RequestHeaderRewrites, JsonValueKind.Array);
        EnsureSerializedJson(value.ResponseHeaderRewrites, JsonValueKind.Array);
        _ = NormalizeJson(value.MetadataJson, JsonValueKind.Object);

        switch (value.Target)
        {
            case MicroserviceRouteTargetConfiguration microservice:
                if (!IsUuidV7(microservice.ServiceId) || !serviceIds.Contains(microservice.ServiceId))
                {
                    throw new ConfigurationValidationException();
                }

                break;
            case StaticFileRouteTargetConfiguration staticFile:
                if (!IsSafeAbsolutePath(staticFile.RootPath, MaxTextLength))
                {
                    throw new ConfigurationValidationException();
                }

                break;
            case ExtensionHandlerRouteTargetConfiguration handler:
                if (!IsSafeText(handler.HandlerId, MaxHandlerIdLength) || !extensionIds.Contains(handler.HandlerId))
                {
                    throw new ConfigurationValidationException();
                }

                break;
            default:
                throw new ConfigurationValidationException();
        }
    }

    private static void ValidateRouteConditions(RouteMatcherConfiguration value)
    {
        ValidateUniqueText(value.HostPatterns, MaxTextLength);
        ValidateUniqueText(value.Methods, 32);
        foreach (var host in value.HostPatterns)
        {
            if (!IsValidHostPattern(host))
            {
                throw new ConfigurationValidationException();
            }
        }

        foreach (var method in value.Methods)
        {
            if (!IsValidHttpToken(method))
            {
                throw new ConfigurationValidationException();
            }
        }
    }

    private static void ValidatePrefixPattern(string pattern)
    {
        if (!IsValidRoutePathPattern(pattern.TrimEnd('*')) || pattern.Count(value => value == '*') > 1 ||
            (pattern.Contains('*') && pattern[^1] != '*') || pattern.Contains("\\*", StringComparison.Ordinal))
        {
            throw new ConfigurationValidationException();
        }
    }

    private static void ValidateForwarding(
        ForwardingConfiguration value,
        RouteMatcherConfiguration matcher)
    {
        if (value is null || !Enum.IsDefined(value.Mode))
        {
            throw new ConfigurationValidationException();
        }

        if (value.Mode == ForwardingMode.Strip &&
            (matcher.Type == RouteMatcherType.Regex ||
             ((matcher.Type is RouteMatcherType.Prefix or RouteMatcherType.PrefixCaseInsensitive) &&
                  IsRawPrefixPattern(matcher.Pattern))))
        {
            throw new ConfigurationValidationException();
        }

        if (value.Mode == ForwardingMode.Replace)
        {
            if (string.IsNullOrWhiteSpace(value.ReplaceTemplate) ||
                value.ReplaceTemplate.Length > MaxTextLength ||
                ContainsControlCharacter(value.ReplaceTemplate) ||
                value.ReplaceTemplate.Contains('?') ||
                value.ReplaceTemplate.Contains('#') ||
                value.ReplaceTemplate.Contains('\\') ||
                !HasAbsoluteTemplatePrefix(value.ReplaceTemplate) ||
                !HasValidTemplateTokens(value.ReplaceTemplate, matcher))
            {
                throw new ConfigurationValidationException();
            }
        }
        else if (value.ReplaceTemplate is not null)
        {
            throw new ConfigurationValidationException();
        }
    }

    private static void ValidateService(ServiceConfiguration? value)
    {
        if (value is null || !IsUuidV7(value.Id) || value.Version < 0 ||
            !IsSafeAbsolutePath(value.FileName, MaxTextLength) ||
            !IsSafeAbsolutePath(value.WorkingDirectory, MaxTextLength) ||
            !Enum.IsDefined(value.StartMode) || !Enum.IsDefined(value.RestartPolicy) ||
            value.ArgumentList.IsDefault || value.Environment is null || value.HealthCheck is null)
        {
            throw new ConfigurationValidationException();
        }

        foreach (var argument in value.ArgumentList)
        {
            if (argument is null || argument.Length > MaxArgumentLength || ContainsControlCharacter(argument))
            {
                throw new ConfigurationValidationException();
            }
        }

        foreach (var pair in value.Environment)
        {
            if (!IsSafeEnvironmentKey(pair.Key) || pair.Value is null ||
                pair.Value.Length > MaxEnvironmentValueLength || ContainsControlCharacter(pair.Value))
            {
                throw new ConfigurationValidationException();
            }
        }

        EnsureSerializedJson(value.ArgumentList, JsonValueKind.Array);
        _ = NormalizeJson(SerializeEnvironment(value.Environment), JsonValueKind.Object);

        var health = value.HealthCheck;
        if (!Enum.IsDefined(health.Type) || health.Timeout <= TimeSpan.Zero ||
            health.Timeout.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            health.Timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ConfigurationValidationException();
        }

        if (health.Type == ServiceHealthCheckType.Http)
        {
            if (!IsSafeHttpPath(health.HttpPath))
            {
                throw new ConfigurationValidationException();
            }
        }
        else if (health.HttpPath is not null)
        {
            throw new ConfigurationValidationException();
        }
    }

    private static void ValidateExtensionRecord(ExtensionRecordConfiguration? value)
    {
        if (value is null || !IsSafeExtensionId(value.ExtensionId) ||
            value.Version.Length > MaxExtensionIdLength || !SemanticVersionPattern.IsMatch(value.Version) ||
            !Enum.IsDefined(value.LoadState) || value.RecordVersion < 0)
        {
            throw new ConfigurationValidationException();
        }
    }

    private static void ValidateExtensionSettings(ExtensionSettingsConfiguration? value)
    {
        if (value is null || !IsSafeExtensionId(value.ExtensionId) || value.SchemaVersion < 0 || value.Version < 0)
        {
            throw new ConfigurationValidationException();
        }

        _ = NormalizeJson(value.SettingsJson, null);
    }

    private static void ValidateHeaderRewrites(IEnumerable<HeaderRewriteConfiguration> values)
    {
        if (values is null)
        {
            throw new ConfigurationValidationException();
        }

        foreach (var rewrite in values)
        {
            if (rewrite is null || !Enum.IsDefined(rewrite.Operation) ||
                !IsValidHttpToken(rewrite.Name) || HopByHopHeaders.Contains(rewrite.Name))
            {
                throw new ConfigurationValidationException();
            }

            if (rewrite.Operation == HeaderRewriteOperation.Remove)
            {
                if (rewrite.Value is not null)
                {
                    throw new ConfigurationValidationException();
                }
            }
            else if (rewrite.Value is null || rewrite.Value.Any(char.IsControl))
            {
                throw new ConfigurationValidationException();
            }
        }
    }

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
                    TimeSpan.FromMilliseconds(RegexTimeoutMilliseconds));
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

    private static bool IsSafeAbsolutePath(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength &&
        Path.IsPathRooted(value) && !ContainsControlCharacter(value);

    private static bool IsSafeText(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !ContainsControlCharacter(value);

    private static bool IsSafeEnvironmentKey(string? value) =>
        IsSafeText(value, MaxEnvironmentKeyLength) && value!.IndexOf('=') < 0;

    private static bool IsSafeHttpPath(string? value) =>
        IsSafeText(value, MaxHealthPathLength) && value!.StartsWith('/');

    private static bool IsValidHostPattern(string? value)
    {
        if (!IsSafeText(value, MaxTextLength) || value!.Contains('/') ||
            value.Contains(' '))
        {
            return false;
        }

        var host = value.StartsWith("*.", StringComparison.Ordinal) ? value[2..] : value;
        if (value.Contains('*') && !value.StartsWith("*.", StringComparison.Ordinal))
        {
            return false;
        }

        if (host.Contains('*'))
        {
            return false;
        }

        if (host.Contains('%'))
        {
            return false;
        }

        if (host.Contains(':'))
        {
            return IPAddress.TryParse(host, out var address) &&
                address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        return host.Length > 0 && host[0] != '.' && !host.EndsWith('.') &&
            host.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '-');
    }

    private static bool IsValidHttpToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var separators = "()<>@,;:" + '\\' + "\"/[]?={} \t";
        return value.Length <= 256 && value.All(character =>
            character > 32 && character < 127 && !separators.Contains(character));
    }

    private static bool IsValidCidr(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Contains('%'))
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
        if (!IPAddress.TryParse(addressText, out var address) ||
            !int.TryParse(prefixText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefix))
        {
            return false;
        }

        var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork
            ? 32
            : address.AddressFamily == AddressFamily.InterNetworkV6
                ? 128
                : -1;
        return maxPrefix >= 0 && prefix is >= 0 and <= 128 && prefix <= maxPrefix;
    }

    private static void ValidateUniqueIds(IEnumerable<Guid> ids, bool requireUuidV7)
    {
        var seen = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if ((requireUuidV7 && !IsUuidV7(id)) || !seen.Add(id))
            {
                throw new ConfigurationValidationException();
            }
        }
    }

    private static void ValidateUniqueText(IEnumerable<string?> values, int maxLength)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!IsSafeText(value, maxLength) || !seen.Add(value!))
            {
                throw new ConfigurationValidationException();
            }
        }
    }

    private static bool ContainsControlCharacter(string value) => value.Any(char.IsControl);

    private static void EnsureSerializedJson<T>(T value, JsonValueKind expectedKind) =>
        _ = NormalizeJson(SerializeJson(value), expectedKind);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    internal sealed class ConfigurationValidationException : Exception
    {
    }
}
