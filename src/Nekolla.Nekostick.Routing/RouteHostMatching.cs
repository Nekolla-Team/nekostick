using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Nekolla.Nekostick.Routing;

internal sealed class HostPattern
{
    private HostPattern(string value, bool wildcard)
    {
        Value = value;
        IsWildcard = wildcard;
    }

    internal string Value { get; }
    internal bool IsWildcard { get; }

    internal static bool TryCreate(string? value, out HostPattern? pattern)
    {
        pattern = null;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
        {
            return false;
        }

        var wildcard = value.StartsWith("*.", StringComparison.Ordinal);
        var hostText = wildcard ? value[2..] : value;
        if (hostText.Contains('*'))
        {
            return false;
        }

        if (!HostValue.TryParse(hostText, out var normalized, out var isIp) || (wildcard && isIp))
        {
            return false;
        }

        pattern = new HostPattern(normalized, wildcard);
        return true;
    }

    internal bool Matches(HostValue host) => IsWildcard
        ? host.Value.Length > Value.Length + 1 &&
          host.Value.EndsWith($".{Value}", StringComparison.Ordinal) &&
          host.Value[..^(Value.Length + 1)].Length > 0
        : host.Value.Equals(Value, StringComparison.Ordinal);
}

internal sealed class HostValue
{
    private HostValue(string value)
    {
        Value = value;
    }

    internal string Value { get; }

    internal static bool TryCreate(string? input, out HostValue? value, out bool isValid)
    {
        if (string.IsNullOrEmpty(input))
        {
            value = null;
            isValid = true;
            return true;
        }

        if (TryParse(input, out var normalized, out _))
        {
            value = new HostValue(normalized);
            isValid = true;
            return true;
        }

        value = null;
        isValid = false;
        return false;
    }

    internal static bool TryParse(string input, out string normalized, out bool isIp)
    {
        normalized = string.Empty;
        isIp = false;
        if (string.IsNullOrWhiteSpace(input) || input.Any(char.IsWhiteSpace) || input.Any(char.IsControl))
        {
            return false;
        }

        var host = input;
        if (input[0] == '[')
        {
            var close = input.IndexOf(']');
            if (close <= 1 ||
                (close + 1 < input.Length &&
                 (input[close + 1] != ':' || !TryPort(input[(close + 1)..]))))
            {
                return false;
            }

            host = input[1..close];
        }
        else
        {
            var colonCount = input.Count(static character => character == ':');
            if (colonCount > 1)
            {
                host = input;
            }
            else if (colonCount == 1)
            {
                var colon = input.IndexOf(':');
                if (!TryPort(input[(colon + 1)..]))
                {
                    return false;
                }

                host = input[..colon];
            }
        }

        if (IPAddress.TryParse(host, out var address))
        {
            if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            {
                return false;
            }

            normalized = address.ToString().ToUpperInvariant();
            isIp = true;
            return true;
        }

        try
        {
            var idn = new IdnMapping { UseStd3AsciiRules = true };
            var ascii = idn.GetAscii(host);
            if (ascii.EndsWith('.'))
            {
                ascii = ascii[..^1];
            }

            if (ascii.Length == 0 || ascii.Length > 253)
            {
                return false;
            }

            foreach (var label in ascii.Split('.', StringSplitOptions.None))
            {
                if (label.Length is 0 or > 63 ||
                    label[0] == '-' ||
                    label[^1] == '-' ||
                    label.Any(static character =>
                        !(char.IsAsciiLetterOrDigit(character) || character == '-')))
                {
                    return false;
                }
            }

            normalized = ascii.ToUpperInvariant();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryPort(string value)
    {
        if (value.Length < 2 || value[0] != ':' ||
            value[1..].Any(character => !char.IsAsciiDigit(character)) ||
            !int.TryParse(value[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            return false;
        }

        return port is >= 0 and <= 65535;
    }
}
