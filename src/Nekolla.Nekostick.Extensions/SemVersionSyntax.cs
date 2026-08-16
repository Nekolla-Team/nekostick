namespace Nekolla.Nekostick.Extensions;

internal static class SemVersionSyntax
{
    internal static bool TryParseNumericIdentifier(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 10 ||
            text.Length > 1 && text[0] == '0')
        {
            return false;
        }

        foreach (var character in text)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }

            var digit = character - '0';
            if (value > (int.MaxValue - digit) / 10)
            {
                return false;
            }

            value = value * 10 + digit;
        }

        return true;
    }

    internal static bool IsValidIdentifiers(
        string? text,
        bool allowEmpty,
        bool rejectNumericLeadingZeros)
    {
        if (string.IsNullOrEmpty(text))
        {
            return allowEmpty;
        }

        var identifiers = text.Split('.', StringSplitOptions.None);
        foreach (var identifier in identifiers)
        {
            if (identifier.Length == 0)
            {
                return false;
            }

            if (rejectNumericLeadingZeros && identifier.Length > 1 && identifier[0] == '0' &&
                identifier.All(static character => character is >= '0' and <= '9'))
            {
                return false;
            }

            foreach (var character in identifier)
            {
                var isAlphaNumeric = character is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z';
                if (!isAlphaNumeric && character != '-')
                {
                    return false;
                }
            }
        }

        return true;
    }
}
