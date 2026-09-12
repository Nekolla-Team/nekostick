using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Nekolla.Nekostick.Proxy;

public sealed partial class StaticTargetDefinition
{
    /// <summary>
    /// Opens a resolved file read-only and revalidates the canonical root and target after opening.
    /// The stream is exposed only after the post-open check succeeds, and directories are rejected.
    /// </summary>
    /// <param name="resolution">A successful resolution created by this target.</param>
    /// <param name="logger">The optional structured logger for filesystem open failures.</param>
    /// <returns>A typed open result; failed results contain no filesystem path.</returns>
    public StaticFileOpenResult OpenRead(
        StaticFileResolution resolution,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return OpenRead(
            resolution,
            StaticFileOperationFactory.Create(logger),
            logger);
    }

    internal StaticFileOpenResult OpenRead(
        StaticFileResolution resolution,
        IStaticFileOperation operation,
        ILogger? logger = null)
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

        var currentRoot = CanonicalizeExistingPath(
            _rootPath,
            boundaryRoot: null,
            recursionDepth: 0,
            logger: logger);
        if (currentRoot.Status != CanonicalPathStatus.Success
            || !string.Equals(currentRoot.CanonicalPath, resolution.CanonicalRootPath, StringComparison.Ordinal)
            || !Directory.Exists(currentRoot.CanonicalPath))
        {
            return CreateOpenFailure(StaticFileOpenKind.Forbidden, StaticFileFailureReason.TargetChanged);
        }

        var beforeOpen = CanonicalizeExistingPath(
            resolution.LexicalPath,
            currentRoot.CanonicalPath,
            recursionDepth: 0,
            logger: logger);
        if (!IsSameSafeFile(beforeOpen, resolution.CanonicalPath, currentRoot.CanonicalPath))
        {
            return OpenFailureForPathStatus(beforeOpen.Status);
        }

        StaticFileOperationResult operationResult;
        try
        {
            operationResult = operation.OpenReadOnly(
                currentRoot.CanonicalPath,
                resolution.CanonicalPath,
                logger);
        }
        catch (Exception exception)
        {
            if (ProxyLogThrottle.TryAcquire("StaticTargetDefinition.OpenRead.Operation", out var occurrences))
            {
                ProxyLogMessages.StaticOpenOperationFailed(
                    logger ?? NullLogger.Instance,
                    exception,
                    "StaticTargetDefinition.OpenRead.Operation",
                    occurrences);
            }

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
                    openedFile,
                    logger);
            }
        }
    }

    private static StaticFileOpenResult CreateFileStreamResult(
        StaticFileResolution resolution,
        string canonicalRootPath,
        StaticOpenedFile openedFile,
        ILogger? logger = null)
    {
        FileStream? stream = null;
        SafeFileHandle? safeHandle = null;
        try
        {
            var afterOpen = CanonicalizeExistingPath(
                resolution.LexicalPath!,
                canonicalRootPath,
                recursionDepth: 0,
                logger: logger);
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
            if (ProxyLogThrottle.TryAcquire("StaticTargetDefinition.CreateFileStreamResult.Missing", out var occurrences))
            {
                ProxyLogMessages.StaticOpenTargetMissing(
                    logger ?? NullLogger.Instance,
                    "StaticTargetDefinition.CreateFileStreamResult",
                    occurrences);
            }

            return CreateOpenFailure(StaticFileOpenKind.NotFound, StaticFileFailureReason.TargetNotFound);
        }
        catch (DirectoryNotFoundException)
        {
            if (ProxyLogThrottle.TryAcquire("StaticTargetDefinition.CreateFileStreamResult.Missing", out var occurrences))
            {
                ProxyLogMessages.StaticOpenTargetMissing(
                    logger ?? NullLogger.Instance,
                    "StaticTargetDefinition.CreateFileStreamResult",
                    occurrences);
            }

            return CreateOpenFailure(StaticFileOpenKind.NotFound, StaticFileFailureReason.TargetNotFound);
        }
        catch (UnauthorizedAccessException exception)
        {
            if (ProxyLogThrottle.TryAcquire("StaticTargetDefinition.CreateFileStreamResult.Error", out var occurrences))
            {
                ProxyLogMessages.StaticOpenTargetFailed(
                    logger ?? NullLogger.Instance,
                    exception,
                    "StaticTargetDefinition.CreateFileStreamResult",
                    occurrences);
            }

            return CreateOpenFailure(StaticFileOpenKind.Forbidden, StaticFileFailureReason.AccessDenied);
        }
        catch (IOException exception)
        {
            if (ProxyLogThrottle.TryAcquire("StaticTargetDefinition.CreateFileStreamResult.Error", out var occurrences))
            {
                ProxyLogMessages.StaticOpenTargetFailed(
                    logger ?? NullLogger.Instance,
                    exception,
                    "StaticTargetDefinition.CreateFileStreamResult",
                    occurrences);
            }

            return CreateOpenFailure(StaticFileOpenKind.Forbidden, StaticFileFailureReason.TargetChanged);
        }
        catch (ArgumentException)
        {
            if (ProxyLogThrottle.TryAcquire("StaticTargetDefinition.CreateFileStreamResult.Validation", out var occurrences))
            {
                ProxyLogMessages.StaticOpenTargetValidationRejected(
                    logger ?? NullLogger.Instance,
                    "StaticTargetDefinition.CreateFileStreamResult",
                    occurrences);
            }

            return CreateOpenFailure(StaticFileOpenKind.Invalid, StaticFileFailureReason.InvalidRequestPath);
        }
        catch (NotSupportedException)
        {
            if (ProxyLogThrottle.TryAcquire("StaticTargetDefinition.CreateFileStreamResult.Validation", out var occurrences))
            {
                ProxyLogMessages.StaticOpenTargetValidationRejected(
                    logger ?? NullLogger.Instance,
                    "StaticTargetDefinition.CreateFileStreamResult",
                    occurrences);
            }

            return CreateOpenFailure(StaticFileOpenKind.Forbidden, StaticFileFailureReason.AccessDenied);
        }
        catch (Exception exception)
        {
            if (ProxyLogThrottle.TryAcquire("StaticTargetDefinition.CreateFileStreamResult.Error", out var occurrences))
            {
                ProxyLogMessages.StaticOpenTargetFailed(
                    logger ?? NullLogger.Instance,
                    exception,
                    "StaticTargetDefinition.CreateFileStreamResult",
                    occurrences);
            }

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
