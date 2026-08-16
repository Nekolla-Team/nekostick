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

internal static class HostConfigurationValueValidator
{
    internal const int MaxTextLength = 4096;
    internal const int MaxExtensionIdLength = 128;
    internal const int MaxHandlerIdLength = 256;
    internal const int MaxEnvironmentKeyLength = 256;
    internal const int MaxEnvironmentValueLength = 64 * 1024;
    internal const int MaxArgumentLength = 64 * 1024;
    internal const int MaxHealthPathLength = 2048;
    internal const int RegexTimeoutMilliseconds = 50;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

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
            Throw();
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
                Throw();
            }

            return document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            Throw();
            return string.Empty;
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
                Throw();
            }

            return result.ToImmutableArray();
        }
        catch (JsonException)
        {
            Throw();
            return ImmutableArray<string>.Empty;
        }
    }

    internal static ImmutableArray<HeaderRewriteConfiguration> DeserializeHeaderRewrites(string value)
    {
        _ = NormalizeJson(value, JsonValueKind.Array);
        try
        {
            var result = JsonSerializer.Deserialize<HeaderRewriteConfiguration[]>(value, JsonOptions);
            return result?.ToImmutableArray() ?? Throw<ImmutableArray<HeaderRewriteConfiguration>>();
        }
        catch (JsonException)
        {
            Throw();
            return ImmutableArray<HeaderRewriteConfiguration>.Empty;
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
                Throw();
            }

            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var pair in result)
            {
                if (!builder.TryAdd(pair.Key, pair.Value))
                {
                    Throw();
                }

                if (!IsSafeEnvironmentKey(pair.Key) || pair.Value is null)
                {
                    Throw();
                }
            }

            return builder.ToImmutable();
        }
        catch (JsonException)
        {
            Throw();
            return ImmutableDictionary<string, string>.Empty;
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

    internal static bool IsSafeAbsolutePath(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength &&
        Path.IsPathRooted(value) && !ContainsControlCharacter(value);

    internal static bool IsSafeText(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !ContainsControlCharacter(value);

    internal static bool IsSafeEnvironmentKey(string? value) =>
        IsSafeText(value, MaxEnvironmentKeyLength) && value!.IndexOf('=') < 0;

    internal static bool IsSafeHttpPath(string? value) =>
        IsSafeText(value, MaxHealthPathLength) && value!.StartsWith('/');

    internal static bool IsValidHostPattern(string? value)
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

    internal static bool IsValidHttpToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var separators = "()<>@,;:" + '\\' + "\"/[]?={} \t";
        return value.Length <= 256 && value.All(character =>
            character > 32 && character < 127 && !separators.Contains(character));
    }

    internal static bool IsValidCidr(string? value)
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

    internal static void ValidateUniqueIds(IEnumerable<Guid> ids, bool requireUuidV7)
    {
        var seen = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if ((requireUuidV7 && !IsUuidV7(id)) || !seen.Add(id))
            {
                Throw();
            }
        }
    }

    internal static void ValidateUniqueText(IEnumerable<string?> values, int maxLength)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!IsSafeText(value, maxLength) || !seen.Add(value!))
            {
                Throw();
            }
        }
    }

    internal static void EnsureSerializedJson<T>(T value, JsonValueKind expectedKind) =>
        _ = NormalizeJson(SerializeJson(value), expectedKind);

    [DoesNotReturn]
    internal static void Throw() => throw new HostConfigurationSemanticValidator.ConfigurationValidationException();

    private static bool ContainsControlCharacter(string value) => value.Any(char.IsControl);

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

    private static T Throw<T>()
    {
        Throw();
        return default!;
    }
}
