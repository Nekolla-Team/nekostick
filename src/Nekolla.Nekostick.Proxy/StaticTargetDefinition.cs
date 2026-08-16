using System.Collections.Immutable;
using System.IO;

namespace Nekolla.Nekostick.Proxy;

/// <summary>
/// Defines the pure, filesystem-facing safety boundary for one static-file route target.
/// It performs no web listening, proxy forwarding, configuration loading, or directory enumeration.
/// </summary>
public sealed class StaticTargetDefinition
{
    private const string DefaultIndexFileName = "index.html";

    private readonly string _rootPath;
    private readonly bool _directoryListingEnabled;
    private readonly StaticFileDeferredHandling _rangeHandling;
    private readonly StaticFileDeferredHandling _headerHandling;

    /// <summary>Creates an immutable static target definition.</summary>
    /// <param name="rootPath">An absolute POSIX root directory path.</param>
    /// <param name="indexFileNames">
    /// Optional fixed single-file names used for directory index lookup.
    /// A default-valued array selects <c>index.html</c>; an empty array disables index lookup.
    /// </param>
    public StaticTargetDefinition(
        string rootPath,
        ImmutableArray<string> indexFileNames = default)
    {
        _rootPath = NormalizeRootPath(rootPath);
        IndexFileNames = NormalizeIndexFileNames(indexFileNames);
        _directoryListingEnabled = false;
        _rangeHandling = StaticFileDeferredHandling.DeferredToHttpLayer;
        _headerHandling = StaticFileDeferredHandling.DeferredToHttpLayer;
    }

    /// <summary>Gets the configured absolute root path.</summary>
    public string RootPath => _rootPath;

    /// <summary>Gets the immutable fixed index-file name policy.</summary>
    public ImmutableArray<string> IndexFileNames { get; }

    /// <summary>Gets whether directory listing is enabled. It is always <see langword="false"/>.</summary>
    public bool DirectoryListingEnabled => _directoryListingEnabled;

    /// <summary>Gets the range-processing policy, which is intentionally deferred to HTTP.</summary>
    public StaticFileDeferredHandling RangeHandling => _rangeHandling;

    /// <summary>Gets the header and conditional-request policy, which is deferred to HTTP.</summary>
    public StaticFileDeferredHandling HeaderHandling => _headerHandling;

    /// <summary>
    /// Resolves a routing-normalized absolute request path.
    /// Literal and decoded dot traversal, invalid percent encodings, NUL values, missing targets,
    /// directories without a fixed index, and canonical paths outside the root are never openable.
    /// </summary>
    /// <param name="normalizedRequestPath">The absolute path produced by the routing layer.</param>
    /// <returns>A typed, non-sensitive resolution result.</returns>
    public StaticFileResolution Resolve(string normalizedRequestPath)
    {
        if (!TryValidateRequestPath(normalizedRequestPath))
        {
            return CreateInvalidResolution(StaticFileFailureReason.InvalidRequestPath);
        }

        var rootResult = CanonicalizeExistingPath(_rootPath, boundaryRoot: null, recursionDepth: 0);
        if (rootResult.Status != CanonicalPathStatus.Success)
        {
            return rootResult.Status == CanonicalPathStatus.Missing
                ? CreateNotFoundResolution(StaticFileFailureReason.RootUnavailable)
                : CreateForbiddenResolution(StaticFileFailureReason.RootUnavailable);
        }

        var canonicalRoot = rootResult.CanonicalPath!;
        if (!Directory.Exists(canonicalRoot))
        {
            return CreateForbiddenResolution(StaticFileFailureReason.RootUnavailable);
        }

        if (!TryBuildTargetPath(canonicalRoot, normalizedRequestPath, out var targetPath))
        {
            return CreateInvalidResolution(StaticFileFailureReason.InvalidRequestPath);
        }

        var targetResult = CanonicalizeExistingPath(targetPath, canonicalRoot, recursionDepth: 0);
        if (targetResult.Status == CanonicalPathStatus.Missing)
        {
            return CreateNotFoundResolution(StaticFileFailureReason.TargetNotFound);
        }

        if (targetResult.Status != CanonicalPathStatus.Success)
        {
            return CreateForbiddenResolution(
                targetResult.Status == CanonicalPathStatus.OutsideRoot
                    ? StaticFileFailureReason.OutsideRoot
                    : StaticFileFailureReason.UnsafeFilesystemTarget);
        }

        var canonicalTarget = targetResult.CanonicalPath!;
        if (!IsWithinRoot(canonicalRoot, canonicalTarget))
        {
            return CreateForbiddenResolution(StaticFileFailureReason.OutsideRoot);
        }

        if (Directory.Exists(canonicalTarget))
        {
            return ResolveDirectoryIndex(canonicalRoot, canonicalTarget);
        }

        if (!File.Exists(canonicalTarget))
        {
            return CreateForbiddenResolution(StaticFileFailureReason.UnsafeFilesystemTarget);
        }

        return CreateResolution(
            StaticFileResolutionKind.FoundFile,
            StaticFileFailureReason.None,
            targetPath,
            canonicalTarget,
            canonicalRoot);
    }

    /// <summary>Resolves a path after applying the pure static-file method policy.</summary>
    /// <param name="method">The method token; only <c>GET</c> and <c>HEAD</c> are accepted.</param>
    /// <param name="normalizedRequestPath">The absolute path produced by the routing layer.</param>
    /// <returns>A typed, non-sensitive resolution result.</returns>
    public StaticFileResolution ResolveRequest(string method, string normalizedRequestPath) =>
        StaticFileRequestMapper.Map(this, method, normalizedRequestPath);

    /// <summary>
    /// Opens a resolved file read-only and revalidates the canonical root and target after opening.
    /// The stream is exposed only after the post-open check succeeds, and directories are rejected.
    /// </summary>
    /// <param name="resolution">A successful resolution created by this target.</param>
    /// <returns>A typed open result; failed results contain no filesystem path.</returns>
    public StaticFileOpenResult OpenRead(StaticFileResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        if (!ReferenceEquals(resolution.Owner, this))
        {
            return CreateOpenFailure(StaticFileOpenKind.Invalid, StaticFileFailureReason.ResolutionNotOwned);
        }

        if (!resolution.IsOpenable
            || resolution.LexicalPath is null
            || resolution.CanonicalPath is null
            || resolution.CanonicalRootPath is null)
        {
            return CreateOpenFailure(StaticFileOpenKind.Invalid, resolution.FailureReason);
        }

        var currentRoot = CanonicalizeExistingPath(_rootPath, boundaryRoot: null, recursionDepth: 0);
        if (currentRoot.Status != CanonicalPathStatus.Success
            || !string.Equals(currentRoot.CanonicalPath, resolution.CanonicalRootPath, StringComparison.Ordinal)
            || !Directory.Exists(currentRoot.CanonicalPath))
        {
            return CreateOpenFailure(StaticFileOpenKind.Forbidden, StaticFileFailureReason.TargetChanged);
        }

        var beforeOpen = CanonicalizeExistingPath(
            resolution.LexicalPath,
            currentRoot.CanonicalPath,
            recursionDepth: 0);
        if (!IsSameSafeFile(beforeOpen, resolution.CanonicalPath, currentRoot.CanonicalPath))
        {
            return OpenFailureForPathStatus(beforeOpen.Status);
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                resolution.LexicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.SequentialScan);

            var afterOpen = CanonicalizeExistingPath(
                resolution.LexicalPath,
                currentRoot.CanonicalPath,
                recursionDepth: 0);
            if (!IsSameSafeFile(afterOpen, resolution.CanonicalPath, currentRoot.CanonicalPath)
                || Directory.Exists(afterOpen.CanonicalPath)
                || !File.Exists(afterOpen.CanonicalPath))
            {
                stream.Dispose();
                stream = null;
                return OpenFailureForPathStatus(afterOpen.Status == CanonicalPathStatus.Success
                    ? CanonicalPathStatus.OutsideRoot
                    : afterOpen.Status);
            }

            var handle = new StaticFileReadHandle(
                stream,
                resolution.ContentType ?? StaticContentTypeMap.GetContentType(afterOpen.CanonicalPath));
            stream = null;
            return new StaticFileOpenResult(
                StaticFileOpenKind.Opened,
                StaticFileFailureReason.None,
                handle);
        }
        catch (FileNotFoundException)
        {
            return CreateOpenFailure(StaticFileOpenKind.NotFound, StaticFileFailureReason.TargetNotFound);
        }
        catch (DirectoryNotFoundException)
        {
            return CreateOpenFailure(StaticFileOpenKind.NotFound, StaticFileFailureReason.TargetNotFound);
        }
        catch (UnauthorizedAccessException)
        {
            return CreateOpenFailure(StaticFileOpenKind.Forbidden, StaticFileFailureReason.AccessDenied);
        }
        catch (IOException)
        {
            return CreateOpenFailure(StaticFileOpenKind.Forbidden, StaticFileFailureReason.TargetChanged);
        }
        catch (ArgumentException)
        {
            return CreateOpenFailure(StaticFileOpenKind.Invalid, StaticFileFailureReason.InvalidRequestPath);
        }
        catch (NotSupportedException)
        {
            return CreateOpenFailure(StaticFileOpenKind.Forbidden, StaticFileFailureReason.AccessDenied);
        }
        finally
        {
            stream?.Dispose();
        }
    }

    internal StaticFileResolution CreateInvalidResolution(StaticFileFailureReason reason) =>
        CreateResolution(StaticFileResolutionKind.Invalid, reason, null, null, null);

    internal StaticFileResolution CreateForbiddenResolution(StaticFileFailureReason reason) =>
        CreateResolution(StaticFileResolutionKind.Forbidden, reason, null, null, null);

    private StaticFileResolution CreateNotFoundResolution(StaticFileFailureReason reason) =>
        CreateResolution(StaticFileResolutionKind.NotFound, reason, null, null, null);

    private StaticFileResolution ResolveDirectoryIndex(string canonicalRoot, string canonicalDirectory)
    {
        foreach (var indexFileName in IndexFileNames)
        {
            if (!TryBuildChildPath(canonicalDirectory, indexFileName, out var indexPath))
            {
                return CreateForbiddenResolution(StaticFileFailureReason.UnsafeFilesystemTarget);
            }

            var indexResult = CanonicalizeExistingPath(indexPath, canonicalRoot, recursionDepth: 0);
            if (indexResult.Status == CanonicalPathStatus.Missing)
            {
                continue;
            }

            if (indexResult.Status != CanonicalPathStatus.Success)
            {
                return CreateForbiddenResolution(
                    indexResult.Status == CanonicalPathStatus.OutsideRoot
                        ? StaticFileFailureReason.OutsideRoot
                        : StaticFileFailureReason.UnsafeFilesystemTarget);
            }

            var canonicalIndex = indexResult.CanonicalPath!;
            if (!IsWithinRoot(canonicalRoot, canonicalIndex))
            {
                return CreateForbiddenResolution(StaticFileFailureReason.OutsideRoot);
            }

            if (Directory.Exists(canonicalIndex))
            {
                continue;
            }

            if (!File.Exists(canonicalIndex))
            {
                return CreateForbiddenResolution(StaticFileFailureReason.UnsafeFilesystemTarget);
            }

            return CreateResolution(
                StaticFileResolutionKind.DirectoryIndexCandidate,
                StaticFileFailureReason.None,
                indexPath,
                canonicalIndex,
                canonicalRoot);
        }

        return CreateNotFoundResolution(IndexFileNames.IsEmpty
            ? StaticFileFailureReason.DirectoryListingDisabled
            : StaticFileFailureReason.DirectoryIndexMissing);
    }

    private StaticFileResolution CreateResolution(
        StaticFileResolutionKind kind,
        StaticFileFailureReason failureReason,
        string? lexicalPath,
        string? canonicalPath,
        string? canonicalRootPath) =>
        new(
            this,
            kind,
            failureReason,
            lexicalPath,
            canonicalPath,
            canonicalRootPath,
            kind is StaticFileResolutionKind.FoundFile or StaticFileResolutionKind.DirectoryIndexCandidate
                ? StaticContentTypeMap.GetContentType(canonicalPath!)
                : null);

    private static StaticFileOpenResult CreateOpenFailure(
        StaticFileOpenKind kind,
        StaticFileFailureReason reason) =>
        new(kind, reason, handle: null);

    private static StaticFileOpenResult OpenFailureForPathStatus(CanonicalPathStatus status) =>
        status == CanonicalPathStatus.Missing
            ? CreateOpenFailure(StaticFileOpenKind.NotFound, StaticFileFailureReason.TargetNotFound)
            : CreateOpenFailure(
                StaticFileOpenKind.Forbidden,
                status == CanonicalPathStatus.OutsideRoot
                    ? StaticFileFailureReason.OutsideRoot
                    : StaticFileFailureReason.TargetChanged);

    private static bool IsSameSafeFile(
        CanonicalPathResult result,
        string expectedCanonicalPath,
        string canonicalRoot)
    {
        return result.Status == CanonicalPathStatus.Success
            && result.CanonicalPath is not null
            && IsWithinRoot(canonicalRoot, result.CanonicalPath)
            && string.Equals(result.CanonicalPath, expectedCanonicalPath, StringComparison.Ordinal)
            && !Directory.Exists(result.CanonicalPath)
            && File.Exists(result.CanonicalPath);
    }

    private static string NormalizeRootPath(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)
            || !rootPath.StartsWith('/')
            || rootPath.Any(static character => character == '\0' || char.IsControl(character)))
        {
            throw new ArgumentException("An absolute POSIX static root path is required.", nameof(rootPath));
        }

        try
        {
            var fullPath = Path.GetFullPath(rootPath);
            if (!fullPath.StartsWith('/'))
            {
                throw new ArgumentException("An absolute POSIX static root path is required.", nameof(rootPath));
            }

            return TrimTrailingSeparators(fullPath);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("An absolute POSIX static root path is required.", nameof(rootPath));
        }
        catch (NotSupportedException)
        {
            throw new ArgumentException("An absolute POSIX static root path is required.", nameof(rootPath));
        }
    }

    private static ImmutableArray<string> NormalizeIndexFileNames(ImmutableArray<string> indexFileNames)
    {
        if (indexFileNames.IsDefault)
        {
            return ImmutableArray.Create(DefaultIndexFileName);
        }

        if (indexFileNames.IsEmpty)
        {
            return ImmutableArray<string>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<string>(indexFileNames.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var indexFileName in indexFileNames)
        {
            if (string.IsNullOrEmpty(indexFileName)
                || indexFileName is "." or ".."
                || indexFileName.Contains('/')
                || indexFileName.Contains('\\')
                || indexFileName.Any(static character => character == '\0' || char.IsControl(character))
                || !seen.Add(indexFileName))
            {
                throw new ArgumentException("Index names must be unique, safe single file names.", nameof(indexFileNames));
            }

            builder.Add(indexFileName);
        }

        return builder.MoveToImmutable();
    }

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
                    targetPath = Path.GetFullPath(targetPath + "/" + segment);
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

    private static bool TryBuildChildPath(string directoryPath, string childName, out string childPath)
    {
        try
        {
            childPath = Path.GetFullPath(directoryPath + "/" + childName);
            return IsWithinRoot(directoryPath, childPath);
        }
        catch (ArgumentException)
        {
            childPath = string.Empty;
            return false;
        }
        catch (IOException)
        {
            childPath = string.Empty;
            return false;
        }
        catch (NotSupportedException)
        {
            childPath = string.Empty;
            return false;
        }
    }

    private static CanonicalPathResult CanonicalizeExistingPath(
        string path,
        string? boundaryRoot,
        int recursionDepth)
    {
        if (recursionDepth > 64)
        {
            return new(CanonicalPathStatus.Unsafe, null);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return new(CanonicalPathStatus.Error, null);
        }
        catch (NotSupportedException)
        {
            return new(CanonicalPathStatus.Error, null);
        }

        if (!fullPath.StartsWith('/'))
        {
            return new(CanonicalPathStatus.Unsafe, null);
        }

        var currentPath = "/";
        var segments = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var nextPath = currentPath == "/"
                ? "/" + segment
                : currentPath + "/" + segment;
            var probe = ProbePath(nextPath);
            switch (probe.Status)
            {
                case PathProbeStatus.Missing:
                    return new(CanonicalPathStatus.Missing, null);
                case PathProbeStatus.Error:
                    return new(CanonicalPathStatus.Error, null);
                case PathProbeStatus.Unsafe:
                    return new(CanonicalPathStatus.Unsafe, null);
                case PathProbeStatus.Link:
                {
                    var linkTarget = MakeAbsoluteLinkTarget(nextPath, probe.LinkTarget!);
                    if (boundaryRoot is not null && !IsWithinRoot(boundaryRoot, linkTarget))
                    {
                        return new(CanonicalPathStatus.OutsideRoot, null);
                    }

                    var resolvedLink = CanonicalizeExistingPath(linkTarget, boundaryRoot, recursionDepth + 1);
                    if (resolvedLink.Status != CanonicalPathStatus.Success)
                    {
                        return resolvedLink;
                    }

                    currentPath = resolvedLink.CanonicalPath!;
                    break;
                }
                case PathProbeStatus.Present:
                    currentPath = nextPath;
                    break;
            }

            if (boundaryRoot is not null && !IsWithinRoot(boundaryRoot, currentPath))
            {
                return new(CanonicalPathStatus.OutsideRoot, null);
            }
        }

        return new(CanonicalPathStatus.Success, TrimTrailingSeparators(currentPath));
    }

    private static PathProbeResult ProbePath(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            var linkTarget = fileInfo.LinkTarget;
            if (linkTarget is not null)
            {
                return new(PathProbeStatus.Link, linkTarget);
            }

            var attributes = fileInfo.Attributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                var directoryInfo = new DirectoryInfo(path);
                linkTarget = directoryInfo.LinkTarget;
                return linkTarget is null
                    ? new(PathProbeStatus.Unsafe, null)
                    : new(PathProbeStatus.Link, linkTarget);
            }

            return new(PathProbeStatus.Present, null);
        }
        catch (FileNotFoundException)
        {
            return new(PathProbeStatus.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new(PathProbeStatus.Missing, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new(PathProbeStatus.Error, null);
        }
        catch (IOException)
        {
            return new(PathProbeStatus.Error, null);
        }
        catch (ArgumentException)
        {
            return new(PathProbeStatus.Error, null);
        }
        catch (NotSupportedException)
        {
            return new(PathProbeStatus.Error, null);
        }
    }

    private static string MakeAbsoluteLinkTarget(string linkPath, string linkTarget)
    {
        var absoluteTarget = linkTarget.StartsWith('/')
            ? linkTarget
            : Path.GetDirectoryName(linkPath) + "/" + linkTarget;
        return Path.GetFullPath(absoluteTarget);
    }

    private static bool IsWithinRoot(string rootPath, string candidatePath)
    {
        var normalizedRoot = TrimTrailingSeparators(rootPath);
        var normalizedCandidate = TrimTrailingSeparators(candidatePath);
        return normalizedRoot == "/"
            ? normalizedCandidate.StartsWith('/')
            : string.Equals(normalizedRoot, normalizedCandidate, StringComparison.Ordinal)
                || normalizedCandidate.StartsWith(normalizedRoot + "/", StringComparison.Ordinal);
    }

    private static string TrimTrailingSeparators(string path)
    {
        if (path.Length == 0)
        {
            return path;
        }

        var end = path.Length;
        while (end > 1 && path[end - 1] == '/')
        {
            end--;
        }

        return end == path.Length ? path : path[..end];
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

    private enum CanonicalPathStatus
    {
        Success,
        Missing,
        OutsideRoot,
        Unsafe,
        Error
    }

    private readonly struct CanonicalPathResult
    {
        public CanonicalPathResult(CanonicalPathStatus status, string? canonicalPath)
        {
            Status = status;
            CanonicalPath = canonicalPath;
        }

        public CanonicalPathStatus Status { get; }

        public string? CanonicalPath { get; }
    }

    private enum PathProbeStatus
    {
        Present,
        Link,
        Missing,
        Unsafe,
        Error
    }

    private readonly struct PathProbeResult
    {
        public PathProbeResult(PathProbeStatus status, string? linkTarget)
        {
            Status = status;
            LinkTarget = linkTarget;
        }

        public PathProbeStatus Status { get; }

        public string? LinkTarget { get; }
    }
}

internal static class StaticContentTypeMap
{
    public static string GetContentType(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
        {
            return "text/html; charset=utf-8";
        }

        if (extension.Equals(".css", StringComparison.OrdinalIgnoreCase))
        {
            return "text/css; charset=utf-8";
        }

        if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase))
        {
            return "text/javascript; charset=utf-8";
        }

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return "application/json; charset=utf-8";
        }

        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return "application/xml; charset=utf-8";
        }

        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return "text/plain; charset=utf-8";
        }

        if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return "image/svg+xml";
        }

        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return "image/png";
        }

        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return "image/jpeg";
        }

        if (extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            return "image/gif";
        }

        if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return "image/webp";
        }

        if (extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            return "image/x-icon";
        }

        if (extension.Equals(".wasm", StringComparison.OrdinalIgnoreCase))
        {
            return "application/wasm";
        }

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return "application/pdf";
        }

        return "application/octet-stream";
    }
}
