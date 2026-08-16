using System.IO;

namespace Nekolla.Nekostick.Proxy;

public sealed partial class StaticTargetDefinition
{
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
}
