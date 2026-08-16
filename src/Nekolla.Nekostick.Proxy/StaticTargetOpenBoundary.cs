using System.IO;
using Microsoft.Win32.SafeHandles;

namespace Nekolla.Nekostick.Proxy;

public sealed partial class StaticTargetDefinition
{
    /// <summary>
    /// Opens a resolved file read-only and revalidates the canonical root and target after opening.
    /// The stream is exposed only after the post-open check succeeds, and directories are rejected.
    /// </summary>
    /// <param name="resolution">A successful resolution created by this target.</param>
    /// <returns>A typed open result; failed results contain no filesystem path.</returns>
    public StaticFileOpenResult OpenRead(StaticFileResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return OpenRead(resolution, StaticFileOperationFactory.Create());
    }

    internal StaticFileOpenResult OpenRead(
        StaticFileResolution resolution,
        IStaticFileOperation operation)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(operation);

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

        StaticFileOperationResult operationResult;
        try
        {
            operationResult = operation.OpenReadOnly(
                currentRoot.CanonicalPath,
                resolution.CanonicalPath);
        }
        catch (Exception)
        {
            return CreateOpenFailure(StaticFileOpenKind.Forbidden, StaticFileFailureReason.TargetChanged);
        }

        using (operationResult)
        {
            if (!operationResult.IsOpened)
            {
                return OpenFailureForOperation(operationResult.Status);
            }

            var openedFile = operationResult.TransferOpenedFile();
            if (openedFile is null)
            {
                return CreateOpenFailure(StaticFileOpenKind.Forbidden, StaticFileFailureReason.TargetChanged);
            }

            using (openedFile)
            {
                return CreateFileStreamResult(
                    resolution,
                    currentRoot.CanonicalPath,
                    openedFile);
            }
        }
    }

    private static StaticFileOpenResult CreateFileStreamResult(
        StaticFileResolution resolution,
        string canonicalRootPath,
        StaticOpenedFile openedFile)
    {
        FileStream? stream = null;
        SafeFileHandle? safeHandle = null;
        try
        {
            var afterOpen = CanonicalizeExistingPath(
                resolution.LexicalPath!,
                canonicalRootPath,
                recursionDepth: 0);
            if (!IsSameSafeFile(afterOpen, resolution.CanonicalPath!, canonicalRootPath)
                || Directory.Exists(afterOpen.CanonicalPath)
                || !File.Exists(afterOpen.CanonicalPath))
            {
                return OpenFailureForPathStatus(afterOpen.Status == CanonicalPathStatus.Success
                    ? CanonicalPathStatus.OutsideRoot
                    : afterOpen.Status);
            }

            safeHandle = openedFile.TransferHandle();
            if (safeHandle is null)
            {
                return CreateOpenFailure(StaticFileOpenKind.Forbidden, StaticFileFailureReason.TargetChanged);
            }

            stream = new FileStream(safeHandle, FileAccess.Read, 4096, isAsync: false);
            safeHandle = null;
            var openedCanonicalPath = afterOpen.CanonicalPath!;
            var handle = new StaticFileReadHandle(
                stream,
                resolution.ContentType ?? StaticContentTypeMap.GetContentType(openedCanonicalPath),
                openedFile.Length,
                openedFile.LastModifiedUtc);
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
        catch (Exception)
        {
            return CreateOpenFailure(StaticFileOpenKind.Forbidden, StaticFileFailureReason.TargetChanged);
        }
        finally
        {
            stream?.Dispose();
            safeHandle?.Dispose();
        }
    }

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

    private static StaticFileOpenResult OpenFailureForOperation(StaticFileOperationStatus status) =>
        status switch
        {
            StaticFileOperationStatus.NotFound =>
                CreateOpenFailure(StaticFileOpenKind.NotFound, StaticFileFailureReason.TargetNotFound),
            StaticFileOperationStatus.AccessDenied =>
                CreateOpenFailure(StaticFileOpenKind.Forbidden, StaticFileFailureReason.AccessDenied),
            StaticFileOperationStatus.NonRegularFile =>
                CreateOpenFailure(
                    StaticFileOpenKind.Forbidden,
                    StaticFileFailureReason.UnsafeFilesystemTarget),
            StaticFileOperationStatus.UnsupportedAbi =>
                CreateOpenFailure(StaticFileOpenKind.Forbidden, StaticFileFailureReason.AccessDenied),
            StaticFileOperationStatus.LinkRejected
                or StaticFileOperationStatus.IdentityMismatch
                or StaticFileOperationStatus.NativeFailure =>
                CreateOpenFailure(StaticFileOpenKind.Forbidden, StaticFileFailureReason.TargetChanged),
            _ => CreateOpenFailure(StaticFileOpenKind.Forbidden, StaticFileFailureReason.TargetChanged)
        };

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
}
