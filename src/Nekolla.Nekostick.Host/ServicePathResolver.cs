namespace Nekolla.Nekostick.Host;

/// <summary>Resolves configured service paths against the node-local host data directory.</summary>
internal static class ServicePathResolver
{
    /// <summary>
    /// Returns an absolute configured path unchanged, or resolves a relative path against the
    /// node-local host data directory. Relative paths that escape the data directory after
    /// normalization are rejected fail-closed, so a hand-edited configuration can never point a
    /// process launch outside the managed tree.
    /// </summary>
    /// <param name="dataDirectory">The node-local host data directory.</param>
    /// <param name="configuredPath">The configured executable or working-directory path.</param>
    /// <returns>The absolute path handed to the process executor.</returns>
    internal static string Resolve(string dataDirectory, string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        var fullBase = Path.GetFullPath(dataDirectory);
        var resolved = Path.GetFullPath(Path.Combine(fullBase, configuredPath));
        var baseWithSeparator = fullBase.EndsWith(Path.DirectorySeparatorChar)
            ? fullBase
            : fullBase + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(baseWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The configured service path escapes the host data directory.");
        }

        return resolved;
    }
}
