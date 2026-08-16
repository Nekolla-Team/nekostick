using System.Text;

namespace Nekolla.Nekostick.Routing;

/// <summary>Describes why a request path could not be normalized.</summary>
public enum PathNormalizationErrorCode
{
    /// <summary>No error is present.</summary>
    None,

    /// <summary>The path was null or empty.</summary>
    MissingPath,

    /// <summary>The path was not absolute.</summary>
    RelativePath,

    /// <summary>The path contained a malformed percent escape.</summary>
    InvalidPercentEncoding,

    /// <summary>The path contained a control character.</summary>
    ControlCharacter,

    /// <summary>The path contained a query delimiter.</summary>
    QueryStringNotAllowed,

    /// <summary>The path contained a fragment delimiter.</summary>
    FragmentNotAllowed,

    /// <summary>The host value was malformed.</summary>
    InvalidHost,

    /// <summary>The method value was missing or malformed.</summary>
    InvalidMethod
}

/// <summary>Contains a safe, non-echoing path normalization error.</summary>
public sealed class PathNormalizationError
{
    /// <summary>Creates an error with the supplied safe error code.</summary>
    /// <param name="code">The normalization error code.</param>
    public PathNormalizationError(PathNormalizationErrorCode code)
    {
        Code = code == PathNormalizationErrorCode.None
            ? PathNormalizationErrorCode.MissingPath
            : code;
    }

    /// <summary>Gets the safe error code.</summary>
    public PathNormalizationErrorCode Code { get; }

    /// <summary>Returns a representation that does not contain the rejected path.</summary>
    public override string ToString() => $"PathNormalizationError({Code})";
}

/// <summary>Represents either a normalized path or a safe normalization error.</summary>
public readonly struct PathNormalizationResult
{
    private PathNormalizationResult(string normalizedPath)
    {
        IsSuccess = true;
        NormalizedPath = normalizedPath;
        Error = null;
    }

    private PathNormalizationResult(PathNormalizationError error)
    {
        IsSuccess = false;
        NormalizedPath = null;
        Error = error;
    }

    /// <summary>Gets whether normalization succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets the normalized path when <see cref="IsSuccess"/> is true.</summary>
    public string? NormalizedPath { get; }

    /// <summary>Gets the safe error when normalization failed.</summary>
    public PathNormalizationError? Error { get; }

    internal static PathNormalizationResult Success(string path) => new(path);

    internal static PathNormalizationResult Failure(PathNormalizationErrorCode code) =>
        new(new PathNormalizationError(code));

    /// <summary>Returns a representation that does not contain the input path.</summary>
    public override string ToString() => IsSuccess
        ? "PathNormalizationResult(Success)"
        : $"PathNormalizationResult(Failure:{Error?.Code.ToString() ?? nameof(PathNormalizationErrorCode.MissingPath)})";
}

/// <summary>Normalizes request paths without decoding their percent-encoded content.</summary>
public static class RoutePathNormalizer
{
    /// <summary>
    /// Normalizes an absolute request path by validating percent escapes and removing only
    /// literal RFC dot segments.
    /// </summary>
    /// <param name="path">The path component without a query string or fragment.</param>
    /// <returns>A typed success or safe failure result.</returns>
    public static PathNormalizationResult Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return PathNormalizationResult.Failure(PathNormalizationErrorCode.MissingPath);
        }

        if (path[0] != '/')
        {
            return PathNormalizationResult.Failure(PathNormalizationErrorCode.RelativePath);
        }

        for (var index = 0; index < path.Length; index++)
        {
            var character = path[index];
            if (char.IsControl(character))
            {
                return PathNormalizationResult.Failure(PathNormalizationErrorCode.ControlCharacter);
            }

            if (character == '?')
            {
                return PathNormalizationResult.Failure(PathNormalizationErrorCode.QueryStringNotAllowed);
            }

            if (character == '#')
            {
                return PathNormalizationResult.Failure(PathNormalizationErrorCode.FragmentNotAllowed);
            }

            if (character == '%')
            {
                if (index + 2 >= path.Length ||
                    !IsHexDigit(path[index + 1]) ||
                    !IsHexDigit(path[index + 2]))
                {
                    return PathNormalizationResult.Failure(PathNormalizationErrorCode.InvalidPercentEncoding);
                }

                index += 2;
            }
        }

        return PathNormalizationResult.Success(RemoveLiteralDotSegments(path));
    }

    /// <summary>Normalizes a path and returns whether the operation succeeded.</summary>
    /// <param name="path">The path component without a query string or fragment.</param>
    /// <param name="result">The typed normalization result.</param>
    /// <returns><see langword="true"/> when the path was normalized.</returns>
    public static bool TryNormalize(string? path, out PathNormalizationResult result)
    {
        result = Normalize(path);
        return result.IsSuccess;
    }

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    // This is the RFC dot-segment algorithm expressed over the original UTF-16 text.
    // It never decodes percent escapes, and therefore %2F and %5C remain ordinary text.
    private static string RemoveLiteralDotSegments(string path)
    {
        var input = path;
        var output = new StringBuilder(path.Length);

        while (input.Length > 0)
        {
            if (input.StartsWith("../", StringComparison.Ordinal) ||
                input.StartsWith("./", StringComparison.Ordinal))
            {
                input = input[(input.IndexOf('/') + 1)..];
                continue;
            }

            if (input.StartsWith("/./", StringComparison.Ordinal))
            {
                input = input[2..];
                continue;
            }

            if (input.Equals("/.", StringComparison.Ordinal))
            {
                input = "/";
                continue;
            }

            if (input.StartsWith("/../", StringComparison.Ordinal))
            {
                input = input[3..];
                RemoveLastOutputSegment(output);
                continue;
            }

            if (input.Equals("/..", StringComparison.Ordinal))
            {
                input = "/";
                RemoveLastOutputSegment(output);
                continue;
            }

            if (input.Equals(".", StringComparison.Ordinal) ||
                input.Equals("..", StringComparison.Ordinal))
            {
                input = string.Empty;
                continue;
            }

            var slashIndex = input.IndexOf('/', 1);
            if (slashIndex < 0)
            {
                output.Append(input);
                input = string.Empty;
            }
            else
            {
                output.Append(input.AsSpan(0, slashIndex));
                input = input[slashIndex..];
            }
        }

        return output.Length == 0 ? "/" : output.ToString();
    }

    private static void RemoveLastOutputSegment(StringBuilder output)
    {
        if (output.Length == 0)
        {
            return;
        }

        var slashIndex = output.ToString().LastIndexOf('/');
        output.Length = slashIndex < 0 ? 0 : slashIndex;
    }
}
