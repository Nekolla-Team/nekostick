using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace Nekolla.Nekostick.Supervision;

internal interface IProcessLiveness
{
    bool IsRunning(Guid serviceId);

    // Implementations that cannot bind a service check to the requested process generation fail closed.
    bool IsRunning(Guid serviceId, ProcessInstanceId instanceId) => false;
}

/// <summary>Receives bounded structured output from a supervised child process.</summary>
public interface IProcessOutputSink
{
    /// <summary>Receives one accepted child-output line.</summary>
    /// <param name="record">The bounded output record.</param>
    void OnLine(ProcessOutputRecord record);

    /// <summary>Receives an aggregate count of child-output lines dropped by the limiter.</summary>
    /// <param name="serviceId">The service that emitted the dropped output.</param>
    /// <param name="stream">The child-output stream subject to the limit.</param>
    /// <param name="count">The positive aggregate number of dropped lines.</param>
    void OnDropped(Guid serviceId, ProcessOutputStream stream, long count);
}

/// <summary>Represents one bounded line of supervised child output.</summary>
/// <param name="ServiceId">The service that emitted the output.</param>
/// <param name="Stream">The child-output stream that emitted the line.</param>
/// <param name="Timestamp">The UTC time at which the line was captured.</param>
/// <param name="Level">The capture-assigned log severity name.</param>
/// <param name="Text">The UTF-8 decoded, bounded output text.</param>
/// <param name="Truncated">Whether text beyond the line limit was discarded.</param>
public sealed record ProcessOutputRecord(
    Guid ServiceId,
    ProcessOutputStream Stream,
    DateTimeOffset Timestamp,
    string Level,
    string Text,
    bool Truncated);

/// <summary>Identifies the captured child-output stream.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "This public enum intentionally identifies a process output stream.")]
public enum ProcessOutputStream
{
    /// <summary>The standard-output stream.</summary>
    Stdout,

    /// <summary>The standard-error stream.</summary>
    Stderr
}

internal sealed class NullProcessOutputSink : IProcessOutputSink
{
    internal static NullProcessOutputSink Instance { get; } = new();
    public void OnLine(ProcessOutputRecord record) { }
    public void OnDropped(Guid serviceId, ProcessOutputStream stream, long count) { }
}

internal sealed class ProcessOutputBudget
{
    private readonly int maximumLines;
    private readonly int maximumBytes;
    private DateTimeOffset windowStart = DateTimeOffset.UtcNow;
    private int lines;
    private int bytes;
    private long dropped;

    internal ProcessOutputBudget(int maximumLines, int maximumBytes)
    {
        this.maximumLines = maximumLines;
        this.maximumBytes = maximumBytes;
    }

    internal bool TryAccept(int byteCount, DateTimeOffset now, out long droppedCount)
    {
        lock (this)
        {
            if (now - windowStart >= TimeSpan.FromSeconds(1))
            {
                droppedCount = dropped;
                dropped = 0;
                lines = 0;
                bytes = 0;
                windowStart = now;
            }
            else
            {
                droppedCount = 0;
            }

            if (lines >= maximumLines || bytes > maximumBytes - Math.Min(byteCount, maximumBytes))
            {
                dropped++;
                return false;
            }

            lines++;
            bytes += byteCount;
            return true;
        }
    }
    internal long Flush()
    {
        lock (this)
        {
            var count = dropped;
            dropped = 0;
            return count;
        }
    }

}

internal static class ProcessOutputCapture
{
    private const int MaximumOutputLineLength = 16 * 1024;

    internal static async Task ReadAsync(
        TextReader reader,
        Guid serviceId,
        ProcessOutputStream stream,
        ProcessOutputBudget budget,
        IProcessOutputSink sink,
        CancellationToken cancellationToken,
        bool skipMarker = false)
    {
        var buffer = ArrayPool<char>.Shared.Rent(4096);
        var line = new StringBuilder(1024);
        var truncated = false;
        try
        {
            if (skipMarker)
            {
                // The helper readiness marker has already been consumed by StartAsync.
            }

            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (line.Length != 0)
                    {
                        Emit(serviceId, stream, line, truncated, budget, sink);
                    }
                    return;
                }

                for (var index = 0; index < read; index++)
                {
                    var character = buffer[index];
                    if (character == '\n')
                    {
                        Emit(serviceId, stream, line, truncated, budget, sink);
                        line.Clear();
                        truncated = false;
                    }
                    else if (character != '\r')
                    {
                        if (line.Length < MaximumOutputLineLength)
                        {
                            line.Append(character);
                        }
                        else
                        {
                            truncated = true;
                        }
                    }
                }
            }
        }
        finally
        {
            var remainingDropped = budget.Flush();
            if (remainingDropped > 0)
            {
                sink.OnDropped(serviceId, stream, remainingDropped);
            }

            ArrayPool<char>.Shared.Return(buffer);
            reader.Dispose();
        }
    }

    private static void Emit(
        Guid serviceId,
        ProcessOutputStream stream,
        StringBuilder line,
        bool truncated,
        ProcessOutputBudget budget,
        IProcessOutputSink sink)
    {
        var text = line.ToString();
        var now = DateTimeOffset.UtcNow;
        var accepted = budget.TryAccept(Encoding.UTF8.GetByteCount(text), now, out var dropped);
        if (dropped > 0)
        {
            sink.OnDropped(serviceId, stream, dropped);
        }
        if (!accepted)
        {
            return;
        }

        sink.OnLine(new ProcessOutputRecord(
            serviceId,
            stream,
            now,
            stream == ProcessOutputStream.Stdout ? "Information" : "Warning",
            text,
            truncated));
    }
}

internal static partial class PosixProcessSignals
{
    internal static bool TrySignalProcess(int processId, int signal)
    {
        if (processId <= 1)
        {
            return false;
        }

        try
        {
            var hostGroup = GetProcessGroup(0);
            if (hostGroup == processId || GetProcessGroup(processId) != processId)
            {
                return false;
            }

            return Kill(processId, signal) == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TrySignalGroup(int processGroupId, int signal)
    {
        if (processGroupId <= 1)
        {
            return false;
        }

        try
        {
            var hostGroup = GetProcessGroup(0);
            if (hostGroup == processGroupId || GetProcessGroup(processGroupId) != processGroupId)
            {
                return false;
            }

            return Kill(-processGroupId, signal) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static int GetProcessGroup(int processId) => OperatingSystem.IsMacOS() ? GetProcessGroupDarwin(processId) : GetProcessGroupLinux(processId);
    private static int Kill(int processId, int signal) => OperatingSystem.IsMacOS() ? KillDarwin(processId, signal) : KillLinux(processId, signal);
    [DllImport("libSystem.B.dylib", EntryPoint = "getpgid", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetProcessGroupDarwin(int processId);

    [DllImport("libc.so.6", EntryPoint = "getpgid", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetProcessGroupLinux(int processId);

    [DllImport("libSystem.B.dylib", EntryPoint = "kill", CallingConvention = CallingConvention.Cdecl)]
    private static extern int KillDarwin(int processId, int signal);

    [DllImport("libc.so.6", EntryPoint = "kill", CallingConvention = CallingConvention.Cdecl)]
    private static extern int KillLinux(int processId, int signal);
}
