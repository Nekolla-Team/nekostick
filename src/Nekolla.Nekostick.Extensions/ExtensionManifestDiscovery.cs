namespace Nekolla.Nekostick.Extensions;

/// <summary>Discovers exactly one manifest at an explicitly supplied extension directory.</summary>
public static class ExtensionManifestDiscovery
{
    /// <summary>Reads and validates one explicit extension directory.</summary>
    /// <param name="extensionDirectory">The exact directory supplied by the caller.</param>
    /// <returns>A safe result without filesystem paths in failure data.</returns>
    public static ManifestDiscoveryResult Discover(string? extensionDirectory)
    {
        if (!CanonicalPath.TryCanonicalDirectory(extensionDirectory, out var root))
        {
            return ManifestDiscoveryResult.Failure(ExtensionFailureCode.InvalidArgument);
        }

        var manifestFiles = new List<(string Name, ManifestSourceFormat Format)>();
        AddExistingManifest(root, "manifest.json", ManifestSourceFormat.Json, manifestFiles);
        AddExistingManifest(root, "manifest.yaml", ManifestSourceFormat.Yaml, manifestFiles);
        AddExistingManifest(root, "manifest.yml", ManifestSourceFormat.Yaml, manifestFiles);

        if (manifestFiles.Count == 0)
        {
            return ManifestDiscoveryResult.Failure(ExtensionFailureCode.ManifestMissing);
        }

        if (manifestFiles.Count != 1)
        {
            return ManifestDiscoveryResult.Failure(ExtensionFailureCode.DuplicateManifest);
        }

        var selected = manifestFiles[0];
        var manifestPath = Path.Combine(root, selected.Name);
        if (!CanonicalPath.TryCanonicalFileInRoot(root, manifestPath, out var canonicalManifestPath))
        {
            return ManifestDiscoveryResult.Failure(ExtensionFailureCode.UnsafePath, selected.Format);
        }

        return selected.Format == ManifestSourceFormat.Json
            ? JsonManifestParser.Parse(root, canonicalManifestPath)
            : YamlManifestParser.Parse(root, canonicalManifestPath);

    }

    private static void AddExistingManifest(
        string root,
        string name,
        ManifestSourceFormat format,
        List<(string Name, ManifestSourceFormat Format)> manifestFiles)
    {
        var path = Path.Combine(root, name);
        if (File.Exists(path))
        {
            manifestFiles.Add((name, format));
        }
    }
}

internal static class CanonicalPath
{
    internal static bool TryCanonicalDirectory(string? input, out string directory)
    {
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(input) || input.Contains('\0'))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(input);
            var pathRoot = Path.GetPathRoot(fullPath);
            if (pathRoot is null)
            {
                return false;
            }

            var relativePath = Path.GetRelativePath(pathRoot, fullPath);
            if (relativePath == ".")
            {
                directory = Normalize(pathRoot);
                return Directory.Exists(directory);
            }

            var current = Normalize(pathRoot);
            var segments = relativePath.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                var info = new DirectoryInfo(current);
                if (!info.Exists)
                {
                    directory = string.Empty;
                    return false;
                }

                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                current = Normalize(target?.FullName ?? info.FullName);
            }

            directory = current;
            return Directory.Exists(directory);
        }
        catch (Exception)
        {
            directory = string.Empty;
            return false;
        }
    }

    internal static bool TryCanonicalFileInRoot(string root, string candidate, out string file)
    {
        file = string.Empty;
        try
        {
            var relativeCandidate = Path.GetRelativePath(root, candidate);
            if (relativeCandidate is "." || relativeCandidate.StartsWith("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(relativeCandidate))
            {
                return false;
            }

            var current = root;
            var segments = relativeCandidate.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length; index++)
            {
                current = Path.Combine(current, segments[index]);
                FileSystemInfo item = index == segments.Length - 1
                    ? new FileInfo(current)
                    : new DirectoryInfo(current);
                if (!item.Exists)
                {
                    return false;
                }

                var target = item.ResolveLinkTarget(returnFinalTarget: true);
                current = Normalize(target?.FullName ?? item.FullName);
            }

            if (!File.Exists(current) || !IsWithin(root, current))
            {
                return false;
            }

            file = current;
            return true;
        }
        catch (Exception)
        {
            file = string.Empty;
            return false;
        }
    }

    internal static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Normalize(candidate);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var separator = normalizedRoot.EndsWith(
            Path.DirectorySeparatorChar.ToString(),
            StringComparison.Ordinal)
            ? string.Empty
            : Path.DirectorySeparatorChar.ToString();
        return normalizedCandidate.Equals(normalizedRoot, comparison) ||
            normalizedCandidate.StartsWith(
                normalizedRoot + separator,
                comparison);
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(full);
    }
}
