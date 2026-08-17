using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Nekolla.Nekostick.Host;

/// <summary>Extracts the embedded process helper into a verified per-user cache location.</summary>
internal static class NativeHelperExtractor
{
    internal const string ResourceName =
        "Nekolla.Nekostick.Host.Resources.Nekolla.Nekostick.NativeHelper";

    private const string CacheRelativePath = "nekolla/native-helper";
    private const string HelperFileName = "Nekolla.Nekostick.NativeHelper";

    internal static string? TryExtract()
    {
        try
        {
            if (!IsSupportedRuntime())
            {
                return null;
            }

            using var resource = typeof(NativeHelperExtractor).Assembly
                .GetManifestResourceStream(ResourceName);
            if (resource is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            var bytes = buffer.ToArray();
            if (bytes.Length == 0)
            {
                return null;
            }

            var hash = SHA256.HashData(bytes);
            var hashText = Convert.ToHexString(hash).ToLowerInvariant();
            var cacheRoot = GetCacheRoot();
            if (cacheRoot is null)
            {
                return null;
            }

            var directory = Path.Combine(cacheRoot, CacheRelativePath, hashText);
            if (!TryPrepareDirectory(directory))
            {
                return null;
            }

            var destination = Path.Combine(directory, HelperFileName);
            if (TryValidateAndMakeExecutable(destination, bytes.Length, hash))
            {
                return destination;
            }

            if (HasPathEntry(destination) && !IsRegularFile(destination))
            {
                return null;
            }

            return TryExtractAtomically(destination, bytes, hash)
                ? destination
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryExtractAtomically(string destination, byte[] bytes, byte[] expectedHash)
    {
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.SequentialScan))
            {
                output.Write(bytes, 0, bytes.Length);
                output.Flush(flushToDisk: true);
            }

            SetOwnerExecutable(temporary);
            if (HasPathEntry(destination) && !IsRegularFile(destination))
            {
                return false;
            }

            File.Move(temporary, destination, overwrite: true);
            return TryValidateAndMakeExecutable(destination, bytes.Length, expectedHash);
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
                // A failed cleanup cannot make an unverified helper executable.
            }
        }
    }

    private static bool TryValidateAndMakeExecutable(string path, int expectedLength, byte[] expectedHash)
    {
        try
        {
            if (!IsRegularFile(path))
            {
                return false;
            }

            var fileInfo = new FileInfo(path);
            if (fileInfo.Length != expectedLength)
            {
                return false;
            }

            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            var actualHash = SHA256.HashData(input);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                return false;
            }

            SetOwnerExecutable(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryPrepareDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var directoryInfo = new DirectoryInfo(directory);
            if (directoryInfo.LinkTarget is not null)
            {
                return false;
            }

            var attributes = directoryInfo.Attributes;
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRegularFile(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.LinkTarget is not null)
            {
                return false;
            }

            var attributes = fileInfo.Attributes;
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasPathEntry(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            return fileInfo.Exists || Directory.Exists(path) || fileInfo.LinkTarget is not null;
        }
        catch
        {
            return true;
        }
    }

    private static void SetOwnerExecutable(string path)
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static bool IsSupportedRuntime() =>
        (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) &&
        (RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.X64) &&
        (RuntimeInformation.RuntimeIdentifier is "osx-arm64" or "osx-x64" or "linux-arm64" or "linux-x64");

    private static string? GetCacheRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home) || !Path.IsPathRooted(home))
        {
            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Caches");
        }

        var xdgCacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgCacheHome))
        {
            return Path.IsPathRooted(xdgCacheHome) ? xdgCacheHome : null;
        }

        return Path.Combine(home, ".cache");
    }

}
