using Microsoft.Extensions.Logging;

namespace Nekolla.Nekostick.Proxy;

internal static partial class ProxyLogMessages
{
    [LoggerMessage(
        EventId = 4010,
        Level = LogLevel.Debug,
        Message = "Proxy drain wait timed out. ServiceId: {ServiceId}. Port: {Port}.")]
    internal static partial void DrainWaitTimedOut(
        ILogger logger,
        Exception exception,
        Guid serviceId,
        int port);

    [LoggerMessage(
        EventId = 4011,
        Level = LogLevel.Debug,
        Message = "Proxy cancellation raced with disposal. Operation: {Operation}.")]
    internal static partial void CancellationDisposedRace(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 4012,
        Level = LogLevel.Warning,
        Message = "Static file identity metadata read failed. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void StaticFileIdentityReadFailed(
        ILogger logger,
        Exception exception,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 4013,
        Level = LogLevel.Warning,
        Message = "Static file native library verification failed. Platform: {Platform}. Occurrences: {Occurrences}.")]
    internal static partial void NativeLibraryVerificationFailed(
        ILogger logger,
        Exception exception,
        string platform,
        long occurrences);

    [LoggerMessage(
        EventId = 4014,
        Level = LogLevel.Debug,
        Message = "Static file native library release failed. Platform: {Platform}.")]
    internal static partial void NativeLibraryReleaseFailed(
        ILogger logger,
        Exception exception,
        string platform);

    [LoggerMessage(
        EventId = 4015,
        Level = LogLevel.Warning,
        Message = "Static file native export lookup failed. Platform: {Platform}. Export: {Export}. Occurrences: {Occurrences}.")]
    internal static partial void NativeExportLookupFailed(
        ILogger logger,
        Exception exception,
        string platform,
        string export,
        long occurrences);

    [LoggerMessage(
        EventId = 4016,
        Level = LogLevel.Warning,
        Message = "Static file native operation failed. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void NativeOperationFailed(
        ILogger logger,
        Exception exception,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 4017,
        Level = LogLevel.Warning,
        Message = "Static file native path invocation failed. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void NativePathInvocationFailed(
        ILogger logger,
        Exception exception,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 4018,
        Level = LogLevel.Warning,
        Message = "Static file native handle creation failed. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void NativeHandleCreationFailed(
        ILogger logger,
        Exception exception,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 4019,
        Level = LogLevel.Debug,
        Message = "Static file native handle close raced with disposal. Operation: {Operation}.")]
    internal static partial void NativeHandleCloseFailed(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 4020,
        Level = LogLevel.Warning,
        Message = "Static file operation creation failed. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void StaticFileOperationCreationFailed(
        ILogger logger,
        Exception exception,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 4021,
        Level = LogLevel.Debug,
        Message = "Static path canonicalization was rejected. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void StaticPathCanonicalizationRejected(
        ILogger logger,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 4022,
        Level = LogLevel.Debug,
        Message = "Static path probe found no target. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void StaticPathProbeMissing(
        ILogger logger,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 4023,
        Level = LogLevel.Warning,
        Message = "Static path probe failed. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void StaticPathProbeFailed(
        ILogger logger,
        Exception exception,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 4024,
        Level = LogLevel.Debug,
        Message = "Static link probe found no target. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void StaticLinkProbeMissing(
        ILogger logger,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 4025,
        Level = LogLevel.Warning,
        Message = "Static link probe failed. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void StaticLinkProbeFailed(
        ILogger logger,
        Exception exception,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 4026,
        Level = LogLevel.Warning,
        Message = "Static file open operation failed. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void StaticOpenOperationFailed(
        ILogger logger,
        Exception exception,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 4027,
        Level = LogLevel.Debug,
        Message = "Static file open target was not found. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void StaticOpenTargetMissing(
        ILogger logger,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 4028,
        Level = LogLevel.Warning,
        Message = "Static file open target failed validation. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void StaticOpenTargetFailed(
        ILogger logger,
        Exception exception,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 4029,
        Level = LogLevel.Debug,
        Message = "Static file open target validation was rejected. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void StaticOpenTargetValidationRejected(
        ILogger logger,
        string operation,
        long occurrences);

    [LoggerMessage(
        EventId = 4030,
        Level = LogLevel.Warning,
        Message = "Static file operation is unsupported on this platform; static route targets fail closed. Operation: {Operation}. Occurrences: {Occurrences}.")]
    internal static partial void StaticFileOperationUnsupported(
        ILogger logger,
        string operation,
        long occurrences);
}
