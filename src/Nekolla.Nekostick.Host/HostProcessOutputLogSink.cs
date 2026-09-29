using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Supervision;

namespace Nekolla.Nekostick.Host;

/// <summary>Writes supervised child-output lines and drop notifications through the Host logger.</summary>
internal sealed class HostProcessOutputLogSink : IProcessOutputSink
{
    private const string StandardOutput = "stdout";
    private const string StandardError = "stderr";
    private readonly ILogger logger;

    internal HostProcessOutputLogSink(ILogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    internal bool IsTraceEnabled => logger.IsEnabled(LogLevel.Trace);

    public void OnLine(ProcessOutputRecord record)
    {
        if (record is null)
        {
            return;
        }

        var streamName = GetStreamName(record.Stream);
        if (streamName is null)
        {
            return;
        }

        try
        {
            HostProcessOutputLogMessages.ChildOutput(
                logger,
                record.ServiceId,
                streamName,
                record.Timestamp,
                record.Text,
                record.Truncated);
        }
        catch (Exception exception)
        {
            try
            {
                HostLogMessages.ProcessOutputSinkFailure(logger, exception, nameof(OnLine));
            }
            catch
            {
            }
            // Logging must not interrupt child-process capture or lifecycle cleanup.
        }
    }

    public void OnDropped(Guid serviceId, ProcessOutputStream stream, long count)
    {
        if (count <= 0)
        {
            return;
        }

        var streamName = GetStreamName(stream);
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
                count);
        }
        catch (Exception exception)
        {
            try
            {
                HostLogMessages.ProcessOutputSinkCleanupFailure(logger, exception, nameof(OnDropped));
            }
            catch
            {
            }
            // Logging must not interrupt child-process capture or lifecycle cleanup.
        }
    }

    public void OnGap(Guid serviceId, ProcessOutputStream stream, long droppedBytes)
    {
        if (droppedBytes <= 0)
        {
            return;
        }

        var streamName = GetStreamName(stream);
        if (streamName is null)
        {
            return;
        }

        try
        {
            HostProcessOutputLogMessages.OutputGap(
                logger,
                serviceId,
                streamName,
                DateTimeOffset.UtcNow,
                droppedBytes);
        }
        catch (Exception exception)
        {
            try
            {
                HostLogMessages.ProcessOutputSinkCleanupFailure(logger, exception, nameof(OnGap));
            }
            catch
            {
            }
            // Logging must not interrupt child-process capture or lifecycle cleanup.
        }
    }

    private static string? GetStreamName(ProcessOutputStream stream) =>
        stream switch
        {
            ProcessOutputStream.Stdout => StandardOutput,
            ProcessOutputStream.Stderr => StandardError,
            _ => null
        };
}

internal static partial class HostProcessOutputLogMessages
{
    [LoggerMessage(
        EventId = 1076,
        Level = LogLevel.Trace,
        Message = "Supervised child output. ServiceId: {ServiceId}. Stream: {Stream}. Timestamp: {Timestamp}. Text: {Text}. Truncated: {Truncated}.")]
    internal static partial void ChildOutput(
        ILogger logger,
        Guid serviceId,
        string stream,
        DateTimeOffset timestamp,
        string text,
        bool truncated);

    [LoggerMessage(
        EventId = 1009,
        Level = LogLevel.Warning,
        Message = "Supervised child output dropped. ServiceId: {ServiceId}. Stream: {Stream}. Timestamp: {Timestamp}. DroppedCount: {DroppedCount}.")]
    internal static partial void DroppedOutput(
        ILogger logger,
        Guid serviceId,
        string stream,
        DateTimeOffset timestamp,
        long droppedCount);

    [LoggerMessage(
        EventId = 1077,
        Level = LogLevel.Warning,
        Message = "Supervised child output fan-out gap. ServiceId: {ServiceId}. Stream: {Stream}. Timestamp: {Timestamp}. DroppedBytes: {DroppedBytes}.")]
    internal static partial void OutputGap(
        ILogger logger,
        Guid serviceId,
        string stream,
        DateTimeOffset timestamp,
        long droppedBytes);
}
