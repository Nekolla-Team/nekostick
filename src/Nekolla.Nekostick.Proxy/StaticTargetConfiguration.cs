using System.Collections.Immutable;
using System.IO;

namespace Nekolla.Nekostick.Proxy;

public sealed partial class StaticTargetDefinition
{
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
}
