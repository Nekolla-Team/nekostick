using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Supervision;

namespace Nekolla.Nekostick.Host;

/// <summary>Writes supervised child-output metadata through the Host logger.</summary>
internal sealed class HostProcessOutputLogSink : IProcessOutputSink
{
    private const string StandardOutput = "stdout";
    private const string StandardError = "stderr";
    private readonly ILogger logger;

    internal HostProcessOutputLogSink(ILogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void OnLine(ProcessOutputRecord record)
    {
        if (record is null)
        {
            return;
        }

        try
        {
            switch (record.Stream)
            {
                case ProcessOutputStream.Stdout:
                    HostProcessOutputLogMessages.StandardOutput(
                        logger,
                        record.ServiceId,
                        StandardOutput,
                        record.Timestamp,
                        record.Truncated,
                        false,
                        0);
                    break;
                case ProcessOutputStream.Stderr:
                    HostProcessOutputLogMessages.StandardError(
                        logger,
                        record.ServiceId,
                        StandardError,
                        record.Timestamp,
                        record.Truncated,
                        false,
                        0);
                    break;
            }
        }
        catch
        {
            // Logging must not interrupt child-process capture or lifecycle cleanup.
        }
    }

    public void OnDropped(Guid serviceId, ProcessOutputStream stream, long count)
    {
        if (count <= 0)
        {
            return;
        }

        var streamName = stream switch
        {
            ProcessOutputStream.Stdout => StandardOutput,
            ProcessOutputStream.Stderr => StandardError,
            _ => null
        };
        if (streamName is null)
        {
            return;
        }

        try
        {
            HostProcessOutputLogMessages.DroppedOutput(
                logger,
                serviceId,
                streamName,
                DateTimeOffset.UtcNow,
                false,
                true,
                count);
        }
        catch
        {
            // Logging must not interrupt child-process capture or lifecycle cleanup.
        }
    }
}

internal static partial class HostProcessOutputLogMessages
{
    [LoggerMessage(
        EventId = 1008,
        Level = LogLevel.Information,
        Message = "Supervised child output. ServiceId: {ServiceId}. Stream: {Stream}. Timestamp: {Timestamp}. Truncated: {Truncated}. Dropped: {Dropped}. DroppedCount: {DroppedCount}.")]
    internal static partial void StandardOutput(
        ILogger logger,
        Guid serviceId,
        string stream,
        DateTimeOffset timestamp,
        bool truncated,
        bool dropped,
        long droppedCount);

    [LoggerMessage(
        EventId = 1008,
        Level = LogLevel.Warning,
        Message = "Supervised child output. ServiceId: {ServiceId}. Stream: {Stream}. Timestamp: {Timestamp}. Truncated: {Truncated}. Dropped: {Dropped}. DroppedCount: {DroppedCount}.")]
    internal static partial void StandardError(
        ILogger logger,
        Guid serviceId,
        string stream,
        DateTimeOffset timestamp,
        bool truncated,
        bool dropped,
        long droppedCount);

    [LoggerMessage(
        EventId = 1009,
        Level = LogLevel.Warning,
        Message = "Supervised child output dropped. ServiceId: {ServiceId}. Stream: {Stream}. Timestamp: {Timestamp}. Truncated: {Truncated}. Dropped: {Dropped}. DroppedCount: {DroppedCount}.")]
    internal static partial void DroppedOutput(
        ILogger logger,
        Guid serviceId,
        string stream,
        DateTimeOffset timestamp,
        bool truncated,
        bool dropped,
        long droppedCount);
}
