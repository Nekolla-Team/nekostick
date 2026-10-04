using System.Globalization;
using System.Text;

namespace Nekolla.Nekostick.Extensions;

internal static class ExtensionIdentifierSyntax
{
    internal static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
        {
            return false;
        }

        var segmentLength = 0;
        foreach (var character in value)
        {
            var isLowerAlphaNumeric = character is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (isLowerAlphaNumeric)
            {
                segmentLength++;
                continue;
            }

            if (character is '.' or '-')
            {
                if (segmentLength == 0)
                {
                    return false;
                }

                segmentLength = 0;
                continue;
            }

            return false;
        }

        return segmentLength > 0;
    }
}

internal static class ManifestNameSyntax
{
    internal static bool IsValidEntryAssembly(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 512 ||
            !value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            value[0] is '/' or '\\' ||
            value.Contains('\\') ||
            value.Contains(':'))
        {
            return false;
        }

        var segments = value.Split('/', StringSplitOptions.None);
        foreach (var segment in segments)
        {
            if (segment is "" or "." or ".." || segment.Any(char.IsControl))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsValidEntryType(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 512)
        {
            return false;
        }

        var segmentLength = 0;
        var segmentStart = true;
        foreach (var character in value)
        {
            var isIdentifierCharacter = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
            if (isIdentifierCharacter || character == '_')
            {
                if (segmentStart && character is >= '0' and <= '9')
                {
                    return false;
                }

                segmentLength++;
                segmentStart = false;
                continue;
            }

            if (character is '.' or '+')
            {
                if (segmentLength == 0)
                {
                    return false;
                }

                segmentLength = 0;
                segmentStart = true;
                continue;
            }

            return false;
        }

        return segmentLength > 0;
    }
}

internal static class ExtensionDiagnosticText
{
    internal const string IdentifierSyntaxRule =
        "between 1 and 128 lowercase ASCII letters/digits, with non-empty segments separated by '.' or '-'";

    private const int MaxValueLength = 128;

    internal static string Value(string? value)
    {
        if (value is null)
        {
            return "<null>";
        }

        var valueLength = Math.Min(value.Length, MaxValueLength);
        var isTruncated = valueLength < value.Length;
        var escapedValue = new StringBuilder(valueLength * 6 + 48);
        escapedValue.Append('\'');
        for (var index = 0; index < valueLength; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character) &&
                index + 1 < valueLength &&
                char.IsLowSurrogate(value[index + 1]))
            {
                escapedValue.Append(character).Append(value[++index]);
                continue;
            }

            switch (character)
            {
                case '\\':
                    escapedValue.Append("\\\\");
                    break;
                case '\'':
                    escapedValue.Append("\\'");
                    break;
                case '\0':
                    escapedValue.Append("\\0");
                    break;
                case '\a':
                    escapedValue.Append("\\a");
                    break;
                case '\b':
                    escapedValue.Append("\\b");
                    break;
                case '\f':
                    escapedValue.Append("\\f");
                    break;
                case '\n':
                    escapedValue.Append("\\n");
                    break;
                case '\r':
                    escapedValue.Append("\\r");
                    break;
                case '\t':
                    escapedValue.Append("\\t");
                    break;
                case '\v':
                    escapedValue.Append("\\v");
                    break;
                case '\u2028':
                case '\u2029':
                    escapedValue.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    break;
                default:
                    if (char.IsControl(character) || char.IsSurrogate(character))
                    {
                        escapedValue.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        escapedValue.Append(character);
                    }

                    break;
            }
        }

        if (isTruncated)
        {
            escapedValue.Append("...");
        }

        escapedValue.Append('\'');
        if (isTruncated)
        {
            escapedValue.Append(" (truncated from ").Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(" characters)");
        }

        return escapedValue.ToString();
    }
}
