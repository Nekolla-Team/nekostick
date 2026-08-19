using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Routing;

internal static class ForwardedPathContract
{
    internal static string Build(
        ForwardingMode mode,
        RouteMatcherType matcherType,
        string normalizedPath,
        string matchedText,
        string? replaceTemplate,
        Match? regexMatch)
    {
        return mode switch
        {
            ForwardingMode.Preserve => normalizedPath,
            ForwardingMode.Strip => BuildStrippedPath(matcherType, normalizedPath, matchedText),
            ForwardingMode.Replace => BuildReplacement(
                replaceTemplate!,
                normalizedPath,
                matchedText,
                regexMatch),
            _ => normalizedPath
        };
    }

    internal static bool IsValidReplacementTemplate(
        string? template,
        Regex? regex,
        RouteMatcherType matcherType)
    {
        if (string.IsNullOrWhiteSpace(template) ||
            template.Length > 4096 ||
            ContainsUnsafeTemplateCharacter(template) ||
            !StartsWithAbsoluteReplacement(template, regex, matcherType))
        {
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
                    return false;
                }

                index = end;
            }
            else if (template[index] == '$')
            {
                if (index + 1 >= template.Length || !char.IsDigit(template[index + 1]))
                {
                    return false;
                }

                var end = index + 1;
                while (end < template.Length && char.IsDigit(template[end]))
                {
                    end++;
                }

                if (matcherType != RouteMatcherType.Regex ||
                    regex is null ||
                    !int.TryParse(
                        template[(index + 1)..end],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var group) ||
                    !regex.GetGroupNumbers().Contains(group))
                {
                    return false;
                }

                index = end - 1;
            }
            else if (template[index] == '%')
            {
                if (index + 2 >= template.Length ||
                    !IsHexDigit(template[index + 1]) ||
                    !IsHexDigit(template[index + 2]))
                {
                    return false;
                }

                index += 2;
            }
            else if (template[index] == '}')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';

    private static string BuildStrippedPath(
        RouteMatcherType matcherType,
        string normalizedPath,
        string matchedText)
    {
        if (matcherType is RouteMatcherType.Exact or RouteMatcherType.ExactCaseInsensitive)
        {
            return "/";
        }

        if (normalizedPath.StartsWith(matchedText, StringComparison.Ordinal))
        {
            return EnsureAbsolute(normalizedPath[matchedText.Length..]);
        }

        // The trailing-slash retry matches against a synthetic path one character longer
        // than the normalized request path. Its strip result is still the route root.
        return "/";
    }

    private static string BuildReplacement(
        string template,
        string normalizedPath,
        string matchedText,
        Match? regexMatch)
    {
        var result = new StringBuilder(template.Length + normalizedPath.Length);

        for (var index = 0; index < template.Length; index++)
        {
            switch (template[index])
            {
                case '{':
                {
                    var end = template.IndexOf('}', index + 1);
                    var token = template[index..(end + 1)];
                    result.Append(token switch
                    {
                        "{path}" => normalizedPath,
                        "{match}" => matchedText,
                        _ => string.Empty
                    });
                    index = end;
                    break;
                }
                case '$':
                {
                    var end = index + 1;
                    while (end < template.Length && char.IsDigit(template[end]))
                    {
                        end++;
                    }

                    var groupText = template[(index + 1)..end];
                    _ = int.TryParse(groupText, NumberStyles.None, CultureInfo.InvariantCulture, out var group);
                    if (group == 0)
                    {
                        result.Append(regexMatch?.Groups[0].Value ?? matchedText);
                    }
                    else if (regexMatch is not null && group < regexMatch.Groups.Count)
                    {
                        result.Append(regexMatch.Groups[group].Value);
                    }

                    index = end - 1;
                    break;
                }
                default:
                    result.Append(template[index]);
                    break;
            }
        }

        return EncodePath(EnsureAbsolute(result.ToString()));
    }

    private static string EncodePath(string path)
    {
        var encoded = new StringBuilder(path.Length);
        Span<byte> bytes = stackalloc byte[4];
        for (var index = 0; index < path.Length; index++)
        {
            var character = path[index];
            if (character == '/')
            {
                encoded.Append('/');
                continue;
            }

            if (character == '%' &&
                index + 2 < path.Length &&
                IsHexDigit(path[index + 1]) &&
                IsHexDigit(path[index + 2]))
            {
                encoded.Append(path.AsSpan(index, 3));
                index += 2;
                continue;
            }

            var charCount = char.IsHighSurrogate(character) &&
                index + 1 < path.Length &&
                char.IsLowSurrogate(path[index + 1])
                ? 2
                : 1;
            if (charCount == 1 && character <= 0x7f && IsPchar(character))
            {
                encoded.Append(character);
                continue;
            }

            var byteCount = Encoding.UTF8.GetBytes(path.AsSpan(index, charCount), bytes);
            for (var byteIndex = 0; byteIndex < byteCount; byteIndex++)
            {
                encoded.Append('%');
                encoded.Append(bytes[byteIndex].ToString("X2", CultureInfo.InvariantCulture));
            }

            index += charCount - 1;
        }

        return encoded.ToString();
    }

    private static bool IsPchar(char value) =>
        value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-' or '.' or '_' or '~'
            or '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '='
            or ':' or '@';

    private static string EnsureAbsolute(string path) =>
        path.Length == 0 ? "/" : path[0] == '/' ? path : "/" + path;

    private static bool ContainsUnsafeTemplateCharacter(string value) =>
        value.Any(character => char.IsControl(character) || character is '?' or '#' or '\\');

    private static bool StartsWithAbsoluteReplacement(
        string value,
        Regex? regex,
        RouteMatcherType matcherType)
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
}
