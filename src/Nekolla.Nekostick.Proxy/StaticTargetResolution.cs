using System.IO;
using Microsoft.Extensions.Logging;

namespace Nekolla.Nekostick.Proxy;

public sealed partial class StaticTargetDefinition
{
    /// <summary>
    /// Resolves a routing-normalized absolute request path.
    /// Literal and decoded dot traversal, invalid percent encodings, NUL values, missing targets,
    /// directories without a fixed index, and canonical paths outside the root are never openable.
    /// </summary>
    /// <param name="normalizedRequestPath">The absolute path produced by the routing layer.</param>
    /// <param name="logger">The optional structured logger for filesystem probe failures.</param>
    /// <returns>A typed, non-sensitive resolution result.</returns>
    public StaticFileResolution Resolve(
        string normalizedRequestPath,
        ILogger? logger = null)
    {
        if (!TryValidateRequestPath(normalizedRequestPath))
        {
            return CreateInvalidResolution(StaticFileFailureReason.InvalidRequestPath);
        }

        var rootResult = CanonicalizeExistingPath(
            _rootPath,
            boundaryRoot: null,
            recursionDepth: 0,
            logger: logger);
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

        var targetResult = CanonicalizeExistingPath(
            targetPath,
            canonicalRoot,
            recursionDepth: 0,
            logger: logger);
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
            return ResolveDirectoryIndex(canonicalRoot, canonicalTarget, logger);
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
    /// <param name="logger">The optional structured logger for filesystem probe failures.</param>
    /// <returns>A typed, non-sensitive resolution result.</returns>
    public StaticFileResolution ResolveRequest(
        string method,
        string normalizedRequestPath,
        ILogger? logger = null) =>
        StaticFileRequestMapper.Map(this, method, normalizedRequestPath, logger);
}
