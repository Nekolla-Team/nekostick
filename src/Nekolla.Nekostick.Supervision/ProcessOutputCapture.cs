using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    /// <summary>Receives a raw-byte gap detected while consuming a fan-out stream.</summary>
    /// <param name="serviceId">The service that emitted the output.</param>
    /// <param name="stream">The child-output stream containing the gap.</param>
    /// <param name="droppedBytes">The positive number of raw bytes omitted from the capture.</param>
    void OnGap(Guid serviceId, ProcessOutputStream stream, long droppedBytes) { }
}

/// <summary>Represents one bounded line of supervised child output.</summary>
/// <param name="ServiceId">The service that emitted the output.</param>
/// <param name="Stream">The child-output stream that emitted the line.</param>
/// <param name="Timestamp">The UTC time at which the line was captured.</param>
/// <param name="Text">The UTF-8 decoded, bounded output text.</param>
/// <param name="Truncated">Whether text beyond the line limit was discarded.</param>
public sealed record ProcessOutputRecord(
    Guid ServiceId,
    ProcessOutputStream Stream,
    DateTimeOffset Timestamp,
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


internal sealed class ProcessOutputBudget
{
    private readonly int maximumLines;
    private readonly int maximumBytes;
    private DateTimeOffset windowStart = DateTimeOffset.UtcNow;
    private int lines;
    private int bytes;
    private long stdoutDropped;
    private long stderrDropped;

    internal ProcessOutputBudget(int maximumLines, int maximumBytes)
    {
        this.maximumLines = maximumLines;
        this.maximumBytes = maximumBytes;
    }

    internal bool TryAccept(
        ProcessOutputStream stream,
        int byteCount,
        DateTimeOffset now,
        out long droppedCount)
    {
        lock (this)
        {
            if (now - windowStart >= TimeSpan.FromSeconds(1))
            {
                droppedCount = TakeDropped(stream);
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
                AddDropped(stream);
                return false;
            }

            lines++;
            bytes += byteCount;
            return true;
        }
    }

    internal long Flush(ProcessOutputStream stream)
    {
        lock (this)
        {
            return TakeDropped(stream);
        }
    }

    private long TakeDropped(ProcessOutputStream stream)
    {
        if (stream == ProcessOutputStream.Stdout)
        {
            var count = stdoutDropped;
            stdoutDropped = 0;
            return count;
        }

        var stderrCount = stderrDropped;
        stderrDropped = 0;
        return stderrCount;
    }

    private void AddDropped(ProcessOutputStream stream)
    {
        if (stream == ProcessOutputStream.Stdout)
        {
            stdoutDropped = SaturatingAdd(stdoutDropped);
        }
        else
        {
            stderrDropped = SaturatingAdd(stderrDropped);
        }
    }

    private static long SaturatingAdd(long value) => value == long.MaxValue ? value : value + 1;
}

internal static class ProcessOutputCapture
{
    private const int MaximumOutputLineLength = 16 * 1024;

    private static void Emit(
        Guid serviceId,
        ProcessOutputStream stream,
        StringBuilder line,
        bool truncated,
        ProcessOutputBudget budget,
        IProcessOutputSink sink,
        DateTimeOffset now)
    {
        var text = line.ToString();
        var accepted = budget.TryAccept(stream, Encoding.UTF8.GetByteCount(text), now, out var dropped);
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
            text,
            truncated));
    }
    internal static CallbackConsumer CreateCallbackConsumer(
        Guid serviceId,
        ProcessOutputStream stream,
        ProcessOutputBudget budget,
        IProcessOutputSink sink) =>
        new(serviceId, stream, budget, sink);

    internal sealed class CallbackConsumer : IProcessOutputChunkSink, IDisposable
    {
        private readonly Guid serviceId;
        private readonly ProcessOutputStream stream;
        private readonly ProcessOutputBudget budget;
        private readonly IProcessOutputSink sink;
        private readonly Decoder decoder = new UTF8Encoding(false, false).GetDecoder();
        private readonly char[] characters = ArrayPool<char>.Shared.Rent(4096);
        private readonly StringBuilder line = new(1024);
        private readonly TaskCompletionSource<bool> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object gate = new();
        private bool truncated;
        private bool firstCharacter = true;
        private bool bufferReturned;
        private int terminal;

        internal CallbackConsumer(
            Guid serviceId,
            ProcessOutputStream stream,
            ProcessOutputBudget budget,
            IProcessOutputSink sink)
        {
            this.serviceId = serviceId;
            this.stream = stream;
            this.budget = budget;
            this.sink = sink;
        }

        internal Task Completion => completion.Task;

        public void OnChunk(ReadOnlyMemory<byte> chunk, DateTimeOffset timestamp)
        {
            lock (gate)
            {
                if (terminal != 0)
                {
                    return;
                }

                try
                {
                    var remaining = chunk.Span;
                    while (!remaining.IsEmpty)
                    {
                        decoder.Convert(
                            remaining,
                            characters.AsSpan(),
                            flush: false,
                            out var bytesUsed,
                            out var charsUsed,
                            out _);
                        ProcessCharacters(characters.AsSpan(0, charsUsed), timestamp);
                        if (bytesUsed == 0 && charsUsed == 0)
                        {
                            break;
                        }

                        remaining = remaining[bytesUsed..];
                    }
                }
                catch (Exception exception)
                {
                    terminal = 1;
                    completion.TrySetException(exception);
                    ReturnCharacters();
                    throw;
                }
            }
        }

        public void OnCompleted(ProcessOutputCompletion reason)
        {
            lock (gate)
            {
                if (Interlocked.Exchange(ref terminal, 1) != 0)
                {
                    return;
                }

                try
                {
                    var completionTimestamp = DateTimeOffset.UtcNow;
                    if (reason == ProcessOutputCompletion.Completed)
                    {
                        FlushDecoder(completionTimestamp);
                    }

                    if (reason == ProcessOutputCompletion.Completed && line.Length != 0)
                    {
                        Emit(serviceId, stream, line, truncated, budget, sink, completionTimestamp);
                    }

                    FlushBudget();
                    completion.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                    throw;
                }
                finally
                {
                    ReturnCharacters();
                }
            }
        }

        public void OnDropped(long byteCount)
        {
            lock (gate)
            {
                if (terminal != 0)
                {
                    return;
                }

                decoder.Reset();
                line.Clear();
                truncated = true;
                try
                {
                    sink.OnGap(serviceId, stream, byteCount);
                }
                catch (Exception exception)
                {
                    terminal = 1;
                    completion.TrySetException(exception);
                    ReturnCharacters();
                    throw;
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (Interlocked.Exchange(ref terminal, 1) == 0)
                {
                    ReturnCharacters();
                    completion.TrySetResult(true);
                }
            }
        }

        private void FlushDecoder(DateTimeOffset timestamp)
        {
            while (true)
            {
                decoder.Convert(
                    ReadOnlySpan<byte>.Empty,
                    characters.AsSpan(),
                    flush: true,
                    out _,
                    out var charsUsed,
                    out var completed);
                ProcessCharacters(characters.AsSpan(0, charsUsed), timestamp);
                if (completed)
                {
                    return;
                }
            }
        }

        private void ProcessCharacters(ReadOnlySpan<char> characters, DateTimeOffset timestamp)
        {
            foreach (var character in characters)
            {
                if (firstCharacter)
                {
                    firstCharacter = false;
                    if (character == '\uFEFF')
                    {
                        continue;
                    }
                }
                if (character == '\n')
                {
                    Emit(serviceId, stream, line, truncated, budget, sink, timestamp);
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

        private void FlushBudget()
        {
            var remainingDropped = budget.Flush(stream);
            if (remainingDropped > 0)
            {
                sink.OnDropped(serviceId, stream, remainingDropped);
            }
        }

        private void ReturnCharacters()
        {
            if (!bufferReturned)
            {
                bufferReturned = true;
                ArrayPool<char>.Shared.Return(characters);
            }
        }
    }
}

internal static partial class PosixProcessSignals
{
    internal static bool TrySignalProcess(int processId, int signal, ILogger? logger = null)
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
        catch (Exception exception)
        {
            SupervisionLogMessages.ProcessSignalFailed(
                logger ?? NullLogger.Instance,
                exception,
                "SignalProcess",
                processId,
                signal);
            return false;
        }
    }

    internal static bool TrySignalGroup(int processGroupId, int signal, ILogger? logger = null)
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
        catch (Exception exception)
        {
            SupervisionLogMessages.ProcessSignalFailed(
                logger ?? NullLogger.Instance,
                exception,
                "SignalGroup",
                processGroupId,
                signal);
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
