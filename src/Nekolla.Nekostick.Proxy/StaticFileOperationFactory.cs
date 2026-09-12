using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nekolla.Nekostick.Proxy;

internal static class StaticFileOperationFactory
{
    private readonly record struct OperationInitialization(
        IStaticFileOperation Operation,
        Exception? Exception);

    private static readonly object InitializationGate = new();
    private static OperationInitialization? Initialization;

    internal static IStaticFileOperation Create(ILogger? logger = null)
    {
        var initialization = GetOrCreateOperation(logger);
        var activeLogger = logger ?? NullLogger.Instance;
        if (initialization.Exception is { } exception &&
            ProxyLogThrottle.TryAcquire("StaticFileOperationFactory.Create", out var occurrences))
        {
            ProxyLogMessages.StaticFileOperationCreationFailed(
                activeLogger,
                exception,
                "StaticFileOperationFactory.Create",
                occurrences);
        }
        else if (initialization.Operation is UnsupportedStaticFileOperation &&
            ProxyLogThrottle.TryAcquire("StaticFileOperationFactory.Unsupported", out var unsupportedOccurrences))
        {
            // A non-exception verification failure (for example an unsupported architecture) would
            // otherwise degrade every static request with no diagnostic.
            ProxyLogMessages.StaticFileOperationUnsupported(
                activeLogger,
                "StaticFileOperationFactory.Create",
                unsupportedOccurrences);
        }

        return initialization.Operation;
    }

    private static OperationInitialization GetOrCreateOperation(ILogger? logger)
    {
        lock (InitializationGate)
        {
            return Initialization ??= CreateOperation(logger);
        }
    }

    private static OperationInitialization CreateOperation(ILogger? logger)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return new(LinuxStaticFileOperation.Create(logger), null);
            }

            if (OperatingSystem.IsMacOS())
            {
                return new(DarwinStaticFileOperation.Create(logger), null);
            }
        }
        catch (Exception exception)
        {
            return new(new UnsupportedStaticFileOperation(), exception);
        }

        return new(new UnsupportedStaticFileOperation(), null);
    }
}

internal sealed class UnsupportedStaticFileOperation : IStaticFileOperation
{
    public StaticFileOperationResult OpenReadOnly(
        string canonicalRootPath,
        string canonicalTargetPath,
        ILogger? logger = null) =>
        StaticFileOperationResult.FromStatus(StaticFileOperationStatus.UnsupportedAbi);
}

