using System.Buffers;
using Microsoft.Extensions.Logging;

namespace Nekolla.Nekostick.Supervision;

/// <summary>Identifies why a supervised process-output stream completed.</summary>
public enum ProcessOutputCompletion
{
    /// <summary>The process exited and the stream reached end-of-stream normally.</summary>
    Completed,

    /// <summary>The stream terminated because output processing failed.</summary>
    Faulted,

    /// <summary>The host tore down the stream before normal process completion.</summary>
    Teardown
}

/// <summary>Receives raw bytes from one stream of a supervised child process.</summary>
public interface IProcessOutputChunkSink
{
    /// <summary>Receives one raw output chunk.</summary>
    /// <param name="chunk">The chunk, valid only for the duration of this callback.</param>
    /// <param name="timestamp">The UTC time at which the pump read the chunk.</param>
    void OnChunk(ReadOnlyMemory<byte> chunk, DateTimeOffset timestamp);

    /// <summary>Receives the stream completion notification.</summary>
    /// <param name="completion">The sanitized stream completion reason.</param>
    void OnCompleted(ProcessOutputCompletion completion);

    /// <summary>Receives the aggregate number of bytes dropped for this subscriber.</summary>
    /// <param name="byteCount">The positive number of bytes dropped by the bounded subscriber buffer.</param>
    void OnDropped(long byteCount);
}


/// <summary>Drains one helper output stream and fans raw bytes out to bounded subscribers.</summary>
internal sealed class ProcessOutputFanout : IDisposable
{
    internal const int PumpBufferSize = 8 * 1024;
    internal const int SubscriberBufferCapacityBytes = 256 * 1024;
    internal const int SubscriberBufferCapacityChunks = 4096;

    private const string StreamReadFailureMessage = "The process output stream terminated unexpectedly.";
    private readonly Stream source;
    private readonly Guid serviceId;
    private readonly ProcessOutputStream stream;
    private readonly ILogger logger;
    private readonly object gate = new();
    private OutputSubscriber[] subscribers = Array.Empty<OutputSubscriber>();
    private readonly TaskCompletionSource<bool> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool started;
    private bool disposed;
    private bool completed;
    private ProcessOutputCompletion completionReason;

    internal ProcessOutputFanout(
        Stream source,
        Guid serviceId,
        ProcessOutputStream stream,
        ILogger logger)
    {
        this.source = source;
        this.serviceId = serviceId;
        this.stream = stream;
        this.logger = logger;
    }

    internal Task Completion => completion.Task;

    internal Stream OpenStream()
    {
        var buffer = new OutputSubscriber();
        var subscriber = new StreamSubscriber(this, buffer);
        Attach(buffer);
        return subscriber;
    }

    internal IDisposable Subscribe(IProcessOutputChunkSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var buffer = new OutputSubscriber();
        var subscriber = new CallbackSubscriber(buffer, sink, serviceId, stream, logger);
        Attach(buffer);
        subscriber.Start();
        return new Subscription(this, buffer, subscriber);
    }

    internal void Start()
    {
        lock (gate)
        {
            if (started)
            {
                return;
            }

            started = true;
            if (disposed)
            {
                completion.TrySetResult(true);
                return;
            }
        }

        _ = Task.Run(PumpAsync);
    }

    internal void Fail()
    {
        Complete(ProcessOutputCompletion.Faulted);
    }

    public void Dispose()
    {
        OutputSubscriber[] snapshot;
        ProcessOutputCompletion completion;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (!completed)
            {
                completed = true;
                completionReason = ProcessOutputCompletion.Teardown;
            }

            completion = completionReason;
            snapshot = subscribers;
            subscribers = Array.Empty<OutputSubscriber>();
            if (!started)
            {
                this.completion.TrySetResult(true);
            }
        }

        foreach (var subscriber in snapshot)
        {
            try
            {
                subscriber.Complete(completion);
            }
            catch (Exception exception)
            {
                LogSubscriberFailure(exception, "Complete");
            }
        }
        source.Dispose();
    }

    private void Attach(OutputSubscriber subscriber)
    {
        ProcessOutputCompletion completion;
        lock (gate)
        {
            if (!completed && !disposed)
            {
                var current = subscribers;
                var updated = new OutputSubscriber[current.Length + 1];
                current.CopyTo(updated, 0);
                updated[^1] = subscriber;
                subscribers = updated;
                return;
            }

            completion = completionReason;
        }

        try
        {
            subscriber.Complete(completion);
        }
        catch (Exception exception)
        {
            LogSubscriberFailure(exception, "Complete");
        }
    }

    private void Detach(OutputSubscriber subscriber)
    {
        lock (gate)
        {
            var current = subscribers;
            var index = Array.IndexOf(current, subscriber);
            if (index >= 0)
            {
                var updated = new OutputSubscriber[current.Length - 1];
                if (index > 0)
                {
                    Array.Copy(current, 0, updated, 0, index);
                }
                if (index < updated.Length)
                {
                    Array.Copy(current, index + 1, updated, index, updated.Length - index);
                }
                subscribers = updated;
            }
        }

        subscriber.Detach();
    }

    private void Complete(ProcessOutputCompletion reason)
    {
        OutputSubscriber[] snapshot;
        lock (gate)
        {
            if (completed)
            {
                return;
            }

            completed = true;
            completionReason = reason;
            snapshot = subscribers;
            subscribers = Array.Empty<OutputSubscriber>();
        }

        foreach (var subscriber in snapshot)
        {
            try
            {
                subscriber.Complete(reason);
            }
            catch (Exception exception)
            {
                LogSubscriberFailure(exception, "Complete");
            }
        }
    }

    private async Task PumpAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(PumpBufferSize);
        Exception? error = null;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, PumpBufferSize), CancellationToken.None)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var timestamp = DateTimeOffset.UtcNow;
                var snapshot = Volatile.Read(ref subscribers);

                if (snapshot.Length == 0)
                {
                    continue;
                }

                var chunk = buffer.AsMemory(0, read);
                foreach (var subscriber in snapshot)
                {
                    try
                    {
                        subscriber.Offer(chunk, timestamp);
                    }
                    catch (Exception exception)
                    {
                        LogSubscriberFailure(exception, "Offer");
                    }
                }
            }
        }
        catch (Exception exception)
        {
            error = exception;
            SupervisionLogMessages.ProcessOutputFanoutFailed(logger, exception, stream.ToString(), serviceId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            Complete(error is null ? ProcessOutputCompletion.Completed : ProcessOutputCompletion.Faulted);
            completion.TrySetResult(true);
        }
    }

    private void LogSubscriberFailure(Exception exception, string operation) =>
        SupervisionLogMessages.ProcessOutputSubscriberFailed(
            logger,
            exception,
            operation,
            stream.ToString(),
            serviceId);

    private sealed class Subscription : IDisposable, IAsyncDisposable
    {
        private ProcessOutputFanout? owner;
        private OutputSubscriber? subscriber;
        private readonly CallbackSubscriber callbackSubscriber;

        internal Subscription(
            ProcessOutputFanout owner,
            OutputSubscriber subscriber,
            CallbackSubscriber callbackSubscriber)
        {
            this.owner = owner;
            this.subscriber = subscriber;
            this.callbackSubscriber = callbackSubscriber;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref this.owner, null);
            var subscriber = Interlocked.Exchange(ref this.subscriber, null);
            if (owner is not null && subscriber is not null)
            {
                owner.Detach(subscriber);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Dispose();
            await callbackSubscriber.Completion.ConfigureAwait(false);
        }
    }

    private sealed class OutputSubscriber : IDisposable
    {
        private readonly object gate = new();
        private readonly Queue<OutputChunk> queue = new();
        private readonly SemaphoreSlim signal = new(0);
        private bool detached;
        private bool completed;
        private bool completionDelivered;
        private ProcessOutputCompletion completionReason;
        private int queuedBytes;
        private long droppedBytes;

        internal void Offer(ReadOnlyMemory<byte> chunk, DateTimeOffset timestamp)
        {
            if (chunk.Length == 0)
            {
                return;
            }

            lock (gate)
            {
                if (detached || completed)
                {
                    return;
                }

                if (chunk.Length > SubscriberBufferCapacityBytes - queuedBytes ||
                    queue.Count >= SubscriberBufferCapacityChunks)
                {
                    droppedBytes = SaturatingAdd(droppedBytes, chunk.Length);
                    return;
                }

                var droppedBefore = droppedBytes;
                droppedBytes = 0;
                var rented = ArrayPool<byte>.Shared.Rent(chunk.Length);
                chunk.Span.CopyTo(rented.AsSpan(0, chunk.Length));
                queue.Enqueue(new OutputChunk(rented, chunk.Length, timestamp, droppedBefore));
                queuedBytes += chunk.Length;
                if (signal.CurrentCount == 0)
                {
                    signal.Release();
                }
            }

        }
        internal void Complete(ProcessOutputCompletion reason)
        {
            lock (gate)
            {
                if (detached || completed)
                {
                    return;
                }

                completed = true;
                completionReason = reason;
            }

            signal.Release();
        }

        internal void Detach()
        {
            lock (gate)
            {
                if (detached)
                {
                    return;
                }

                detached = true;
                completed = true;
                completionDelivered = true;
                droppedBytes = 0;
                while (queue.TryDequeue(out var chunk))
                {
                    ArrayPool<byte>.Shared.Return(chunk.Buffer);
                }

                queuedBytes = 0;
            }

            signal.Release();
        }

        internal ValueTask WaitForDataAsync(CancellationToken cancellationToken) =>
            new(signal.WaitAsync(cancellationToken));

        internal void WaitForData() => signal.Wait();

        public void Dispose() => Detach();

        internal bool IsDetached
        {
            get
            {
                lock (gate)
                {
                    return detached;
                }
            }
        }

        internal bool TryRead(
            Span<byte> destination,
            out int bytesRead,
            out ProcessOutputCompletion? completion)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(detached, nameof(StreamSubscriber));

                if (queue.TryPeek(out var chunk))
                {
                    var copyLength = Math.Min(destination.Length, chunk.Length - chunk.Offset);
                    chunk.Buffer.AsSpan(chunk.Offset, copyLength).CopyTo(destination);
                    chunk.Offset += copyLength;
                    queuedBytes -= copyLength;
                    bytesRead = copyLength;
                    if (chunk.Offset == chunk.Length)
                    {
                        queue.Dequeue();
                        ArrayPool<byte>.Shared.Return(chunk.Buffer);
                    }

                    completion = null;
                    return true;
                }

                if (completed)
                {
                    bytesRead = 0;
                    completion = completionReason;
                    return false;
                }

                bytesRead = 0;
                completion = null;
                return false;
            }
        }

        internal bool TryTake(out OutputChunk? chunk)
        {
            lock (gate)
            {
                if (detached)
                {
                    chunk = null;
                    return false;
                }

                if (!queue.TryDequeue(out chunk))
                {
                    return false;
                }

                queuedBytes -= chunk.Length;
                return true;
            }
        }


        internal bool TryTakeCompletion(out ProcessOutputCompletion reason, out long dropped)
        {
            lock (gate)
            {
                if (detached || !completed || queue.Count != 0 || completionDelivered)
                {
                    reason = default;
                    dropped = 0;
                    return false;
                }

                completionDelivered = true;
                reason = completionReason;
                dropped = droppedBytes;
                droppedBytes = 0;
                return true;
            }
        }

        internal static void Return(OutputChunk chunk) => ArrayPool<byte>.Shared.Return(chunk.Buffer);

        private static long SaturatingAdd(long value, int increment) =>
            value > long.MaxValue - increment ? long.MaxValue : value + increment;
    }

    private sealed class StreamSubscriber : Stream
    {
        private readonly ProcessOutputFanout owner;
        private readonly OutputSubscriber buffer;
        private int disposed;

        internal StreamSubscriber(ProcessOutputFanout owner, OutputSubscriber buffer)
        {
            this.owner = owner;
            this.buffer = buffer;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] bytes, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > bytes.Length - count)
            {
                throw new ArgumentException("The buffer range is outside the destination array.", nameof(count));
            }

            return Read(bytes.AsSpan(offset, count));
        }

        public override int Read(Span<byte> bytes)
        {
            if (bytes.Length == 0)
            {
                return 0;
            }

            while (true)
            {
                if (buffer.TryRead(bytes, out var bytesRead, out var completion))
                {
                    return bytesRead;
                }

                if (completion is { } reason)
                {
                    if (reason != ProcessOutputCompletion.Completed)
                    {
                        throw new IOException(StreamReadFailureMessage);
                    }

                    return 0;
                }

                buffer.WaitForData();
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> bytes,
            CancellationToken cancellationToken = default)
        {
            if (bytes.Length == 0)
            {
                return 0;
            }

            while (true)
            {
                if (buffer.TryRead(bytes.Span, out var bytesRead, out var completion))
                {
                    return bytesRead;
                }

                if (completion is { } reason)
                {
                    if (reason != ProcessOutputCompletion.Completed)
                    {
                        throw new IOException(StreamReadFailureMessage);
                    }

                    return 0;
                }

                await buffer.WaitForDataAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] bytes, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Detach(buffer);
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CallbackSubscriber
    {
        private readonly OutputSubscriber buffer;
        private readonly IProcessOutputChunkSink sink;
        private readonly Guid serviceId;
        private readonly ProcessOutputStream stream;
        private readonly ILogger logger;
        private Task? drainTask;

        internal CallbackSubscriber(
            OutputSubscriber buffer,
            IProcessOutputChunkSink sink,
            Guid serviceId,
            ProcessOutputStream stream,
            ILogger logger)
        {
            this.buffer = buffer;
            this.sink = sink;
            this.serviceId = serviceId;
            this.stream = stream;
            this.logger = logger;
        }

        internal Task Completion => Volatile.Read(ref drainTask) ?? Task.CompletedTask;

        internal void Start()
        {
            if (ExecutionContext.IsFlowSuppressed())
            {
                drainTask = Task.Run(DrainAsync);
                return;
            }

            using (ExecutionContext.SuppressFlow())
            {
                drainTask = Task.Run(DrainAsync);
            }
        }

        private async Task DrainAsync()
        {
            try
            {
                while (true)
                {
                    await buffer.WaitForDataAsync(CancellationToken.None).ConfigureAwait(false);
                    while (buffer.TryTake(out var chunk))
                    {
                        try
                        {
                            if (chunk!.DroppedBefore > 0)
                            {
                                NotifyDropped(chunk.DroppedBefore);
                            }

                            NotifyChunk(chunk!);
                        }
                        finally
                        {
                            OutputSubscriber.Return(chunk!);
                        }
                    }

                    if (buffer.TryTakeCompletion(out var reason, out var dropped))
                    {
                        if (dropped > 0)
                        {
                            NotifyDropped(dropped);
                        }

                        NotifyCompleted(reason);
                        return;
                    }

                    if (buffer.IsDetached)
                    {
                        return;
                    }
                }
            }
            catch (Exception exception)
            {
                SupervisionLogMessages.ProcessOutputSubscriberFailed(
                    logger,
                    exception,
                    "Drain",
                    stream.ToString(),
                    serviceId);
            }
        }

        private void NotifyChunk(OutputChunk chunk)
        {
            try
            {
                sink.OnChunk(chunk.Buffer.AsMemory(0, chunk.Length), chunk.Timestamp);
            }
            catch (Exception exception)
            {
                SupervisionLogMessages.ProcessOutputSubscriberFailed(
                    logger,
                    exception,
                    "Chunk",
                    stream.ToString(),
                    serviceId);
            }
        }

        private void NotifyDropped(long dropped)
        {
            try
            {
                sink.OnDropped(dropped);
            }
            catch (Exception exception)
            {
                SupervisionLogMessages.ProcessOutputSubscriberFailed(
                    logger,
                    exception,
                    "Dropped",
                    stream.ToString(),
                    serviceId);
            }
        }

        private void NotifyCompleted(ProcessOutputCompletion reason)
        {
            try
            {
                sink.OnCompleted(reason);
            }
            catch (Exception exception)
            {
                SupervisionLogMessages.ProcessOutputSubscriberFailed(
                    logger,
                    exception,
                    "Completed",
                    stream.ToString(),
                    serviceId);
            }
        }
    }

    private sealed class OutputChunk
    {
        internal OutputChunk(byte[] buffer, int length, DateTimeOffset timestamp, long droppedBefore)
        {
            Buffer = buffer;
            Length = length;
            Timestamp = timestamp;
            DroppedBefore = droppedBefore;
        }

        internal byte[] Buffer { get; }
        internal int Length { get; }
        internal DateTimeOffset Timestamp { get; }
        internal long DroppedBefore { get; }
        internal int Offset { get; set; }
    }
}
