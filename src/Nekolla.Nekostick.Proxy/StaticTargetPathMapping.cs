using System.IO;

namespace Nekolla.Nekostick.Proxy;

public sealed partial class StaticTargetDefinition
{
    private static bool TryValidateRequestPath(string requestPath)
    {
        if (string.IsNullOrEmpty(requestPath) || !requestPath.StartsWith('/'))
        {
            return false;
        }

        var segmentStart = 1;
        for (var index = 1; index <= requestPath.Length; index++)
        {
            if (index < requestPath.Length)
            {
                var character = requestPath[index];
                if (character == '\0' || char.IsControl(character))
                {
                    return false;
                }

                if (character == '%')
                {
                    if (index + 2 >= requestPath.Length
                        || !IsHexDigit(requestPath[index + 1])
                        || !IsHexDigit(requestPath[index + 2]))
                    {
                        return false;
                    }

                    var decodedByte = (byte)((HexValue(requestPath[index + 1]) << 4)
                        | HexValue(requestPath[index + 2]));
                    if (decodedByte == 0 || decodedByte < 0x20 || decodedByte == 0x7f)
                    {
                        return false;
                    }

                    index += 2;
                    continue;
                }
            }

            if (index == requestPath.Length || requestPath[index] == '/')
            {
                var segmentLength = index - segmentStart;
                if (segmentLength > 0
                    && IsDotSegment(requestPath.AsSpan(segmentStart, segmentLength)))
                {
                    return false;
                }

                segmentStart = index + 1;
            }
        }

        return true;
    }

    private static bool IsDotSegment(ReadOnlySpan<char> segment)
    {
        var dotCount = 0;
        for (var index = 0; index < segment.Length; index++)
        {
            var character = segment[index];
            if (character == '%')
            {
                if (index + 2 >= segment.Length
                    || !IsHexDigit(segment[index + 1])
                    || !IsHexDigit(segment[index + 2]))
                {
                    return false;
                }

                var decodedByte = (byte)((HexValue(segment[index + 1]) << 4)
                    | HexValue(segment[index + 2]));
                character = (char)decodedByte;
                index += 2;
            }

            if (character != '.')
            {
                return false;
            }

            dotCount++;
            if (dotCount > 2)
            {
                return false;
            }
        }

        return dotCount is 1 or 2;
    }

    private static bool TryBuildTargetPath(
        string canonicalRoot,
        string requestPath,
        out string targetPath)
    {
        targetPath = canonicalRoot;
        var segmentStart = 1;
        try
        {
            for (var index = 1; index <= requestPath.Length; index++)
            {
                if (index != requestPath.Length && requestPath[index] != '/')
                {
                    continue;
                }

                var segmentLength = index - segmentStart;
                if (segmentLength > 0)
                {
                    var segment = requestPath.Substring(segmentStart, segmentLength);
                    targetPath = System.IO.Path.GetFullPath(targetPath + "/" + segment);
                }

                segmentStart = index + 1;
            }

            return IsWithinRoot(canonicalRoot, targetPath);
        }
        catch (ArgumentException)
        {
            targetPath = string.Empty;
            return false;
        }
        catch (IOException)
        {
            targetPath = string.Empty;
            return false;
        }
        catch (NotSupportedException)
        {
            targetPath = string.Empty;
            return false;
        }
    }

    private static bool IsHexDigit(char character) =>
        character is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';

    private static int HexValue(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'a' and <= 'f' => character - 'a' + 10,
        >= 'A' and <= 'F' => character - 'A' + 10,
        _ => 0
    };
}
