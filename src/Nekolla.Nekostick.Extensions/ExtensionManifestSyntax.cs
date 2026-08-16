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
