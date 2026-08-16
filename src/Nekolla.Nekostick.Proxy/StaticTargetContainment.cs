using System.IO;

namespace Nekolla.Nekostick.Proxy;

public sealed partial class StaticTargetDefinition
{
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

            if (boundaryRoot is not null
                && !IsWithinOrAncestorOfRoot(boundaryRoot, currentPath))
            {
                return new(CanonicalPathStatus.OutsideRoot, null);
            }
        }

        return new(CanonicalPathStatus.Success, TrimTrailingSeparators(currentPath));
    }

    private static PathProbeResult ProbePath(string path)
    {
        var fileProbe = ProbeLinkTarget(path, directory: false);
        var directoryProbe = ProbeLinkTarget(path, directory: true);

        if (fileProbe.Status == LinkProbeStatus.Link)
        {
            return new(PathProbeStatus.Link, fileProbe.LinkTarget);
        }

        if (directoryProbe.Status == LinkProbeStatus.Link)
        {
            return new(PathProbeStatus.Link, directoryProbe.LinkTarget);
        }

        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return new(PathProbeStatus.Unsafe, null);
            }

            var expectedProbe = (attributes & FileAttributes.Directory) != 0
                ? directoryProbe
                : fileProbe;
            return expectedProbe.Status == LinkProbeStatus.NoLink
                ? new(PathProbeStatus.Present, null)
                : new(PathProbeStatus.Error, null);
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

    private static LinkProbeResult ProbeLinkTarget(string path, bool directory)
    {
        try
        {
            FileSystemInfo info = directory
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            var linkTarget = info.ResolveLinkTarget(returnFinalTarget: false);
            return linkTarget is null
                ? new(LinkProbeStatus.NoLink, null)
                : new(LinkProbeStatus.Link, linkTarget.FullName);
        }
        catch (FileNotFoundException)
        {
            return new(LinkProbeStatus.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new(LinkProbeStatus.Missing, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new(LinkProbeStatus.Error, null);
        }
        catch (IOException)
        {
            return new(LinkProbeStatus.Error, null);
        }
        catch (ArgumentException)
        {
            return new(LinkProbeStatus.Error, null);
        }
        catch (NotSupportedException)
        {
            return new(LinkProbeStatus.Error, null);
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

    private static bool IsWithinOrAncestorOfRoot(string boundaryRoot, string candidatePath) =>
        IsWithinRoot(boundaryRoot, candidatePath)
        || IsWithinRoot(candidatePath, boundaryRoot);

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

    private enum LinkProbeStatus
    {
        NoLink,
        Link,
        Missing,
        Error
    }

    private readonly struct LinkProbeResult
    {
        public LinkProbeResult(LinkProbeStatus status, string? linkTarget)
        {
            Status = status;
            LinkTarget = linkTarget;
        }

        public LinkProbeStatus Status { get; }

        public string? LinkTarget { get; }
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
