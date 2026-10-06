using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ProcessOutputFanoutTests
{
    private static readonly Guid ServiceId =
        new("0198a1af-6e94-7b25-9732-59c9075b14f6");

    [Fact]
    public async Task FanoutDeliversEachChunkToMultipleSubscribers()
    {
        using var fanout = CreateFanout(new MemoryStream(Encoding.UTF8.GetBytes("one\ntwo\n")));
        var first = new RecordingChunkSink();
        var second = new RecordingChunkSink();
        using var firstSubscription = fanout.Subscribe(first);
        using var secondSubscription = fanout.Subscribe(second);

        fanout.Start();
        await WaitAsync(fanout.Completion);
        await WaitAsync(first.Completed.Task);
        await WaitAsync(second.Completed.Task);

        Assert.Equal("one\ntwo\n", first.Bytes);
        Assert.Equal("one\ntwo\n", second.Bytes);
        Assert.Equal(ProcessOutputCompletion.Completed, first.CompletionReason);
        Assert.Equal(ProcessOutputCompletion.Completed, second.CompletionReason);
        Assert.Empty(first.Dropped);
        Assert.Empty(second.Dropped);
    }

    [Fact]
    public async Task FanoutDrainsTheSourceWhenThereAreNoSubscribers()
    {
        var source = new TrackingMemoryStream(Encoding.UTF8.GetBytes(new string('x', 32 * 1024)));
        using var fanout = CreateFanout(source);

        fanout.Start();
        await WaitAsync(fanout.Completion);

        Assert.Equal(source.Length, source.Position);
    }

    [Fact]
    public async Task FanoutDropsWholeChunksForAFullSlowSubscriberBuffer()
    {
        var source = new ControlledChunkStream(initialChunkCount: 64);
        using var fanout = CreateFanout(source);
        var sink = new SlowChunkSink();
        using var subscription = fanout.Subscribe(sink);

        fanout.Start();
        await WaitAsync(sink.FirstChunkStarted.Task);
        await WaitAsync(source.InitialBurstCompleted.Task);
        source.AllowPostGapChunk();
        sink.ReleaseFirstChunk();
        await WaitAsync(fanout.Completion);
        await WaitAsync(sink.Completed.Task);

        Assert.True(sink.DroppedBytes > 0);
        Assert.Equal(0, sink.DroppedBytes % ProcessOutputFanout.PumpBufferSize);
        Assert.All(sink.Chunks, chunk => Assert.Equal(ProcessOutputFanout.PumpBufferSize, chunk.Length));
        Assert.Equal(ProcessOutputCompletion.Completed, sink.CompletionReason);
    }

    [Fact]
    public async Task FanoutReportsDroppedBytesImmediatelyBeforeTheFirstPostGapChunk()
    {
        var source = new ControlledChunkStream(initialChunkCount: 40);
        using var fanout = CreateFanout(source);
        var sink = new SlowChunkSink();
        using var subscription = fanout.Subscribe(sink);

        fanout.Start();
        await WaitAsync(sink.FirstChunkStarted.Task);
        await WaitAsync(source.InitialBurstCompleted.Task);
        sink.ReleaseFirstChunk();
        await WaitAsync(sink.ReceivedAtLeast32Chunks.Task);
        source.AllowPostGapChunk();
        await WaitAsync(fanout.Completion);
        await WaitAsync(sink.Completed.Task);

        var postGapIndex = sink.Events.FindIndex(static value => value == "chunk:238");
        Assert.True(postGapIndex >= 0);
        Assert.StartsWith("dropped:", sink.Events[postGapIndex - 1], StringComparison.Ordinal);
        Assert.True(sink.DroppedBytes > 0);
        Assert.Equal(0, sink.DroppedBytes % ProcessOutputFanout.PumpBufferSize);
    }

    [Fact]
    public async Task FanoutUsesCompletedForEofAndFaultedForSourceFailure()
    {
        using var eofFanout = CreateFanout(new MemoryStream(Encoding.UTF8.GetBytes("done")));
        var eofSink = new RecordingChunkSink();
        using var eofSubscription = eofFanout.Subscribe(eofSink);
        eofFanout.Start();
        await WaitAsync(eofFanout.Completion);
        await WaitAsync(eofSink.Completed.Task);
        Assert.Equal(ProcessOutputCompletion.Completed, eofSink.CompletionReason);

        using var faultFanout = CreateFanout(new ThrowingStream());
        var faultSink = new RecordingChunkSink();
        using var faultSubscription = faultFanout.Subscribe(faultSink);
        faultFanout.Start();
        await WaitAsync(faultFanout.Completion);
        await WaitAsync(faultSink.Completed.Task);
        Assert.Equal(ProcessOutputCompletion.Faulted, faultSink.CompletionReason);
    }

    [Fact]
    public async Task FaultedStreamReadExposesOnlyTheSanitizedIOException()
    {
        using var fanout = CreateFanout(new ThrowingStream());
        using var output = fanout.OpenStream();

        fanout.Start();
        await WaitAsync(fanout.Completion);

        var exception = await Assert.ThrowsAsync<IOException>(
            async () => await output.ReadExactlyAsync(
                new byte[16],
                TestContext.Current.CancellationToken));
        Assert.Equal("The process output stream terminated unexpectedly.", exception.Message);
        Assert.DoesNotContain("private source details", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposingFanoutCompletesSubscribersWithTeardown()
    {
        var source = new BlockingStream();
        using var fanout = CreateFanout(source);
        var sink = new RecordingChunkSink();
        using var subscription = fanout.Subscribe(sink);

        fanout.Start();
        await WaitAsync(source.ReadStarted);
        fanout.Dispose();
        await WaitAsync(sink.Completed.Task);

        Assert.Equal(ProcessOutputCompletion.Teardown, sink.CompletionReason);
    }

    [Fact]
    public async Task SubscribersAttachedAfterCompletionReceiveImmediateCompletionWithoutReplay()
    {
        using var fanout = CreateFanout(new MemoryStream(Encoding.UTF8.GetBytes("not-replayed")));
        fanout.Start();
        await WaitAsync(fanout.Completion);

        var callback = new RecordingChunkSink();
        using var subscription = fanout.Subscribe(callback);
        await WaitAsync(callback.Completed.Task);

        Assert.Empty(callback.Chunks);
        Assert.Equal(ProcessOutputCompletion.Completed, callback.CompletionReason);

        using var output = fanout.OpenStream();
        var buffer = new byte[1];
        Assert.Equal(0, await output.ReadAsync(buffer, TestContext.Current.CancellationToken));
    }

    private static ProcessOutputFanout CreateFanout(Stream source) =>
        new(source, ServiceId, ProcessOutputStream.Stdout, NullLogger.Instance);

    private static Task WaitAsync(Task task) =>
        task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private static byte[] CreateChunks(int count, int size)
    {
        var bytes = new byte[count * size];
        for (var chunk = 0; chunk < count; chunk++)
        {
            bytes.AsSpan(chunk * size, size).Fill((byte)chunk);
        }

        return bytes;
    }


    private class RecordingChunkSink : IProcessOutputChunkSink
    {
        internal List<byte[]> Chunks { get; } = [];
        internal List<long> Dropped { get; } = [];
        internal TaskCompletionSource<ProcessOutputCompletion> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ProcessOutputCompletion? CompletionReason { get; private set; }
        internal string Bytes => Encoding.UTF8.GetString(Chunks.SelectMany(static chunk => chunk).ToArray());

        public virtual void OnChunk(ReadOnlyMemory<byte> chunk, DateTimeOffset timestamp) =>
            Chunks.Add(chunk.ToArray());

        public virtual void OnCompleted(ProcessOutputCompletion completion)
        {
            CompletionReason = completion;
            Completed.TrySetResult(completion);
        }

        public virtual void OnDropped(long byteCount) => Dropped.Add(byteCount);
    }

    private sealed class SlowChunkSink : RecordingChunkSink
    {
        private readonly TaskCompletionSource<bool> releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int chunkCount;

        internal TaskCompletionSource<bool> FirstChunkStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReceivedAtLeast32Chunks { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<string> Events { get; } = [];
        internal long DroppedBytes { get; private set; }

        public override void OnChunk(ReadOnlyMemory<byte> chunk, DateTimeOffset timestamp)
        {
            var current = Interlocked.Increment(ref chunkCount);
            if (current == 1)
            {
                FirstChunkStarted.TrySetResult(true);
                releaseFirst.Task.GetAwaiter().GetResult();
            }

            base.OnChunk(chunk, timestamp);
            Events.Add($"chunk:{chunk.Span[0]}");
            if (current >= 32)
            {
                ReceivedAtLeast32Chunks.TrySetResult(true);
            }
        }

        public override void OnDropped(long byteCount)
        {
            DroppedBytes += byteCount;
            Events.Add($"dropped:{byteCount}");
            base.OnDropped(byteCount);
        }

        internal void ReleaseFirstChunk() => releaseFirst.TrySetResult(true);
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        internal TrackingMemoryStream(byte[] buffer)
            : base(buffer, writable: false)
        {
        }
    }

    private sealed class ThrowingStream : Stream
    {
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
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("private source details");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new InvalidOperationException("private source details"));
    }

    private sealed class BlockingStream : Stream
    {
        private readonly TaskCompletionSource<bool> readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task ReadStarted => readStarted.Task;

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
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            readStarted.TrySetResult(true);
            await disposed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            disposed.TrySetResult(true);
            base.Dispose(disposing);
        }
    }

    private sealed class ControlledChunkStream : Stream
    {
        private readonly int initialChunkCount;
        private readonly TaskCompletionSource<bool> allowPostGap =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int chunkIndex;

        internal ControlledChunkStream(int initialChunkCount) => this.initialChunkCount = initialChunkCount;

        internal TaskCompletionSource<bool> InitialBurstCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void AllowPostGapChunk() => allowPostGap.TrySetResult(true);

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
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (chunkIndex == initialChunkCount)
            {
                InitialBurstCompleted.TrySetResult(true);
                await allowPostGap.Task.WaitAsync(cancellationToken);
            }

            if (chunkIndex > initialChunkCount)
            {
                return 0;
            }

            var size = Math.Min(buffer.Length, ProcessOutputFanout.PumpBufferSize);
            buffer.Span[..size].Fill(chunkIndex == initialChunkCount ? (byte)238 : (byte)chunkIndex);
            chunkIndex++;
            return size;
        }
    }
}
