using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ProcessOutputCaptureContractTests
{
    private const int MaximumLinesPerSecond = 200;
    private const int MaximumBytesPerSecond = 1024 * 1024;
    private const int MaximumLineLength = 16 * 1024;
    private static readonly Guid ServiceId =
        new("4d9e6b7a-4f31-4ca6-bf9a-2a6f2c7d8e10");

    [Fact]
    public void CallbackConsumerEmitsStdoutAndStderrRecordsWithStreamMetadata()
    {
        var stdoutSink = new RecordingSink();
        using var stdout = ProcessOutputCapture.CreateCallbackConsumer(
            ServiceId,
            ProcessOutputStream.Stdout,
            CreateBudget(),
            stdoutSink);

        stdout.OnChunk(Encoding.UTF8.GetBytes("first\r\nlast\n"), DateTimeOffset.UtcNow);
        stdout.OnCompleted(ProcessOutputCompletion.Completed);

        Assert.Equal(2, stdoutSink.Records.Count);
        AssertRecord(stdoutSink.Records[0], ProcessOutputStream.Stdout, "first");
        AssertRecord(stdoutSink.Records[1], ProcessOutputStream.Stdout, "last");
        Assert.Empty(stdoutSink.Drops);

        var stderrSink = new RecordingSink();
        using var stderr = ProcessOutputCapture.CreateCallbackConsumer(
            ServiceId,
            ProcessOutputStream.Stderr,
            CreateBudget(),
            stderrSink);

        stderr.OnChunk(Encoding.UTF8.GetBytes("failure\n"), DateTimeOffset.UtcNow);
        stderr.OnCompleted(ProcessOutputCompletion.Completed);

        var stderrRecord = Assert.Single(stderrSink.Records);
        AssertRecord(stderrRecord, ProcessOutputStream.Stderr, "failure");
        Assert.Empty(stderrSink.Drops);
    }

    [Fact]
    public void CallbackConsumerTruncatesALineAt16KiBAndStripsCrLf()
    {
        var sink = new RecordingSink();
        using var consumer = ProcessOutputCapture.CreateCallbackConsumer(
            ServiceId,
            ProcessOutputStream.Stdout,
            CreateBudget(),
            sink);

        consumer.OnChunk(
            Encoding.UTF8.GetBytes(new string('x', MaximumLineLength + 17) + "\r\n"),
            DateTimeOffset.UtcNow);
        consumer.OnCompleted(ProcessOutputCompletion.Completed);

        var record = Assert.Single(sink.Records);
        Assert.Equal(new string('x', MaximumLineLength), record.Text);
        Assert.True(record.Truncated);
        Assert.Equal(ServiceId, record.ServiceId);
        Assert.Equal(ProcessOutputStream.Stdout, record.Stream);
        Assert.Empty(sink.Drops);
    }

    [Fact]
    public void CallbackConsumerReportsDroppedLineWhenCaptureCompletes()
    {
        var sink = new RecordingSink();
        using var consumer = ProcessOutputCapture.CreateCallbackConsumer(
            ServiceId,
            ProcessOutputStream.Stderr,
            new ProcessOutputBudget(maximumLines: 0, maximumBytes: MaximumBytesPerSecond),
            sink);

        consumer.OnChunk(Encoding.UTF8.GetBytes("dropped\n"), DateTimeOffset.UtcNow);
        consumer.OnCompleted(ProcessOutputCompletion.Completed);

        Assert.Empty(sink.Records);
        var dropped = Assert.Single(sink.Drops);
        Assert.Equal(ServiceId, dropped.ServiceId);
        Assert.Equal(ProcessOutputStream.Stderr, dropped.Stream);
        Assert.Equal(1, dropped.Count);
    }

    [Fact]
    public void ProcessOutputBudgetEnforces200LinesWithinADeterministicSecond()
    {
        var budget = CreateBudget();
        var firstWindow = DateTimeOffset.UtcNow.AddSeconds(2);

        for (var index = 0; index < MaximumLinesPerSecond; index++)
        {
            Assert.True(
                budget.TryAccept(
                    ProcessOutputStream.Stdout,
                    byteCount: 1,
                    firstWindow,
                    out var dropped));
            Assert.Equal(0, dropped);
        }

        Assert.False(
            budget.TryAccept(
                ProcessOutputStream.Stdout,
                byteCount: 1,
                firstWindow,
                out var sameWindowDrops));
        Assert.Equal(0, sameWindowDrops);
        Assert.True(
            budget.TryAccept(
                ProcessOutputStream.Stdout,
                byteCount: 1,
                firstWindow.AddSeconds(1),
                out var nextWindowDrops));
        Assert.Equal(1, nextWindowDrops);
    }

    [Fact]
    public void ProcessOutputBudgetEnforces1MiBWithinADeterministicSecond()
    {
        var budget = CreateBudget();
        var firstWindow = DateTimeOffset.UtcNow.AddSeconds(2);

        Assert.True(
            budget.TryAccept(
                ProcessOutputStream.Stdout,
                MaximumBytesPerSecond,
                firstWindow,
                out var initialDrops));
        Assert.Equal(0, initialDrops);
        Assert.False(
            budget.TryAccept(
                ProcessOutputStream.Stdout,
                1,
                firstWindow,
                out _));
        Assert.False(
            budget.TryAccept(
                ProcessOutputStream.Stdout,
                1,
                firstWindow,
                out _));
        Assert.True(
            budget.TryAccept(
                ProcessOutputStream.Stdout,
                1,
                firstWindow.AddSeconds(1),
                out var nextWindowDrops));
        Assert.Equal(2, nextWindowDrops);
    }

    [Fact]
    public void ProcessOutputBudgetKeepsDroppedCountsLabeledByStream()
    {
        var budget = new ProcessOutputBudget(maximumLines: 1, maximumBytes: MaximumBytesPerSecond);
        var now = DateTimeOffset.UtcNow.AddSeconds(2);

        Assert.True(budget.TryAccept(ProcessOutputStream.Stdout, 1, now, out _));
        Assert.False(budget.TryAccept(ProcessOutputStream.Stdout, 1, now, out _));
        Assert.False(budget.TryAccept(ProcessOutputStream.Stderr, 1, now, out _));

        Assert.True(
            budget.TryAccept(
                ProcessOutputStream.Stderr,
                1,
                now.AddSeconds(1),
                out var stderrDrops));
        Assert.Equal(1, stderrDrops);
        Assert.Equal(1, budget.Flush(ProcessOutputStream.Stdout));
    }

    [Fact]
    public void CallbackConsumerReportsDropsAtWindowRollover()
    {
        var sink = new RecordingSink();
        using var consumer = ProcessOutputCapture.CreateCallbackConsumer(
            ServiceId,
            ProcessOutputStream.Stdout,
            new ProcessOutputBudget(maximumLines: 1, maximumBytes: MaximumBytesPerSecond),
            sink);
        var firstWindow = DateTimeOffset.UtcNow.AddSeconds(2);

        consumer.OnChunk(Encoding.UTF8.GetBytes("first\nsecond\n"), firstWindow);
        consumer.OnChunk(Encoding.UTF8.GetBytes("third\n"), firstWindow.AddSeconds(1));
        consumer.OnCompleted(ProcessOutputCompletion.Completed);

        Assert.Equal(["first", "third"], sink.Records.Select(static record => record.Text));
        var dropped = Assert.Single(sink.Drops);
        Assert.Equal(ProcessOutputStream.Stdout, dropped.Stream);
        Assert.Equal(1, dropped.Count);
    }

    [Fact]
    public void CallbackConsumerTeardownDropsPartialLine()
    {
        var sink = new RecordingSink();
        using var consumer = ProcessOutputCapture.CreateCallbackConsumer(
            ServiceId,
            ProcessOutputStream.Stdout,
            CreateBudget(),
            sink);

        consumer.OnChunk(Encoding.UTF8.GetBytes("partial"), DateTimeOffset.UtcNow);
        consumer.OnCompleted(ProcessOutputCompletion.Teardown);

        Assert.Empty(sink.Records);
        Assert.Empty(sink.Drops);
    }

    [Fact]
    public void CallbackConsumerCompletedFlushesPartialLine()
    {
        var sink = new RecordingSink();
        using var consumer = ProcessOutputCapture.CreateCallbackConsumer(
            ServiceId,
            ProcessOutputStream.Stdout,
            CreateBudget(),
            sink);

        consumer.OnChunk(Encoding.UTF8.GetBytes("partial"), DateTimeOffset.UtcNow);
        consumer.OnCompleted(ProcessOutputCompletion.Completed);

        var record = Assert.Single(sink.Records);
        Assert.Equal("partial", record.Text);
        Assert.False(record.Truncated);
    }

    [Fact]
    public void CallbackConsumerGapResetsLineStateAndForwardsGap()
    {
        var sink = new RecordingSink();
        using var consumer = ProcessOutputCapture.CreateCallbackConsumer(
            ServiceId,
            ProcessOutputStream.Stderr,
            CreateBudget(),
            sink);

        consumer.OnChunk(Encoding.UTF8.GetBytes("before"), DateTimeOffset.UtcNow);
        consumer.OnDropped(7);
        consumer.OnChunk(Encoding.UTF8.GetBytes("after\n"), DateTimeOffset.UtcNow);
        consumer.OnCompleted(ProcessOutputCompletion.Completed);

        var record = Assert.Single(sink.Records);
        Assert.Equal("after", record.Text);
        Assert.True(record.Truncated);
        var gap = Assert.Single(sink.Gaps);
        Assert.Equal(ServiceId, gap.ServiceId);
        Assert.Equal(ProcessOutputStream.Stderr, gap.Stream);
        Assert.Equal(7, gap.Count);
    }

    [Fact]
    public async Task FanoutGapResetsUtf8DecoderBeforePostGapBytes()
    {
        var source = new Utf8GapStream();
        using var fanout = new ProcessOutputFanout(
            source,
            ServiceId,
            ProcessOutputStream.Stderr,
            NullLogger.Instance);
        var sink = new BlockingLineSink(source.PostGapDelivered);
        using var consumer = ProcessOutputCapture.CreateCallbackConsumer(
            ServiceId,
            ProcessOutputStream.Stderr,
            new ProcessOutputBudget(maximumLines: 1_000_000, maximumBytes: 1_000_000),
            sink);
        using var subscription = fanout.Subscribe(consumer);

        fanout.Start();
        await WaitAsync(sink.FirstLineStarted.Task);
        await WaitAsync(source.InitialBurstCompleted.Task);
        sink.ReleaseFirstLine();
        source.AllowPostGap();
        await WaitAsync(source.PostGapDelivered.Task);
        await WaitAsync(fanout.Completion);
        await WaitAsync(consumer.Completion);

        Assert.Equal("prefix", sink.Records[0].Text);
        var postGapRecord = Assert.Single(sink.Records, record => record.Truncated);
        Assert.DoesNotContain("😀", postGapRecord.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", postGapRecord.Text, StringComparison.Ordinal);
        Assert.Single(sink.Gaps);
    }

    [Fact]
    public void CallbackConsumerGapResetsUtf8DecoderAndLineState()
    {
        var sink = new RecordingSink();
        using var consumer = ProcessOutputCapture.CreateCallbackConsumer(
            ServiceId,
            ProcessOutputStream.Stderr,
            CreateBudget(),
            sink);
        var timestamp = DateTimeOffset.UtcNow;

        consumer.OnChunk(new byte[] { (byte)'a', (byte)'b', (byte)'c', 0xf0, 0x9f }, timestamp);
        consumer.OnDropped(2);
        consumer.OnChunk(new byte[] { 0x98, 0x80, (byte)'\n' }, timestamp);
        consumer.OnCompleted(ProcessOutputCompletion.Completed);

        var record = Assert.Single(sink.Records);
        Assert.DoesNotContain("abc", record.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("😀", record.Text, StringComparison.Ordinal);
        Assert.True(record.Truncated);
        var gap = Assert.Single(sink.Gaps);
        Assert.Equal(2, gap.Count);
    }

    [Fact]
    public void CallbackConsumersPreserveBudgetAndDropLabelsAcrossStreams()
    {
        var sink = new RecordingSink();
        var budget = new ProcessOutputBudget(maximumLines: 1, maximumBytes: MaximumBytesPerSecond);
        using var stdout = ProcessOutputCapture.CreateCallbackConsumer(
            ServiceId,
            ProcessOutputStream.Stdout,
            budget,
            sink);
        using var stderr = ProcessOutputCapture.CreateCallbackConsumer(
            ServiceId,
            ProcessOutputStream.Stderr,
            budget,
            sink);

        stdout.OnChunk(Encoding.UTF8.GetBytes("out\n"), DateTimeOffset.UtcNow);
        stderr.OnChunk(Encoding.UTF8.GetBytes("err\n"), DateTimeOffset.UtcNow);
        stdout.OnCompleted(ProcessOutputCompletion.Completed);
        stderr.OnCompleted(ProcessOutputCompletion.Completed);

        var record = Assert.Single(sink.Records);
        Assert.Equal(ProcessOutputStream.Stdout, record.Stream);
        var dropped = Assert.Single(sink.Drops);
        Assert.Equal(ProcessOutputStream.Stderr, dropped.Stream);
        Assert.Equal(1, dropped.Count);
    }

    private static ProcessOutputBudget CreateBudget() =>
        new(MaximumLinesPerSecond, MaximumBytesPerSecond);

    private static void AssertRecord(
        ProcessOutputRecord record,
        ProcessOutputStream stream,
        string text)
    {
        Assert.Equal(ServiceId, record.ServiceId);
        Assert.Equal(stream, record.Stream);
        Assert.Equal(text, record.Text);
        Assert.False(record.Truncated);
        Assert.Equal(TimeSpan.Zero, record.Timestamp.Offset);
    }

    private sealed class RecordingSink : IProcessOutputSink
    {
        internal List<ProcessOutputRecord> Records { get; } = [];

        internal List<DroppedOutput> Drops { get; } = [];

        internal List<GapOutput> Gaps { get; } = [];

        public void OnLine(ProcessOutputRecord record) => Records.Add(record);

        public void OnDropped(Guid serviceId, ProcessOutputStream stream, long count) =>
            Drops.Add(new DroppedOutput(serviceId, stream, count));

        public void OnGap(Guid serviceId, ProcessOutputStream stream, long droppedBytes) =>
            Gaps.Add(new GapOutput(serviceId, stream, droppedBytes));
    }

    private sealed record DroppedOutput(
        Guid ServiceId,
        ProcessOutputStream Stream,
        long Count);

    private sealed record GapOutput(
        Guid ServiceId,
        ProcessOutputStream Stream,
        long Count);

    private static Task WaitAsync(Task task) =>
        task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private sealed class BlockingLineSink : IProcessOutputSink
    {
        private readonly TaskCompletionSource<bool> releaseFirstLine =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> postGapDelivered;
        private int lineCount;

        internal BlockingLineSink(TaskCompletionSource<bool> postGapDelivered)
        {
            this.postGapDelivered = postGapDelivered;
        }

        internal TaskCompletionSource<bool> FirstLineStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<ProcessOutputRecord> Records { get; } = [];

        internal List<long> Gaps { get; } = [];

        public void OnLine(ProcessOutputRecord record)
        {
            Records.Add(record);
            if (Interlocked.Increment(ref lineCount) == 1)
            {
                FirstLineStarted.TrySetResult(true);
                releaseFirstLine.Task.GetAwaiter().GetResult();
            }
        }

        public void OnDropped(Guid serviceId, ProcessOutputStream stream, long count) { }

        public void OnGap(Guid serviceId, ProcessOutputStream stream, long droppedBytes)
        {
            Gaps.Add(droppedBytes);
            postGapDelivered.TrySetResult(true);
        }

        internal void ReleaseFirstLine() => releaseFirstLine.TrySetResult(true);
    }

    private sealed class Utf8GapStream : Stream
    {
        private readonly TaskCompletionSource<bool> allowPostGap =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool postGapSent;

        private int chunkIndex;
        internal TaskCompletionSource<bool> InitialBurstCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> PostGapDelivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (chunkIndex == 0)
            {
                Encoding.UTF8.GetBytes("prefix\n").CopyTo(buffer.Span);
                chunkIndex++;
                return 7;
            }

            if (chunkIndex == 1)
            {
                buffer.Span[0] = (byte)'a';
                buffer.Span[1] = (byte)'b';
                buffer.Span[2] = (byte)'c';
                buffer.Span[3] = 0xf0;
                buffer.Span[4] = 0x9f;
                chunkIndex++;
                return 5;
            }

            if (chunkIndex <= 40)
            {
                var filler = buffer.Span[..ProcessOutputFanout.PumpBufferSize];
                for (var index = 0; index < filler.Length; index += 2)
                {
                    filler[index] = (byte)'x';
                    filler[index + 1] = (byte)'\n';
                }

                chunkIndex++;
                return filler.Length;
            }

            if (chunkIndex == 41)
            {
                chunkIndex++;
                InitialBurstCompleted.TrySetResult(true);
                await allowPostGap.Task.WaitAsync(cancellationToken);
            }

            if (postGapSent)
            {
                await PostGapDelivered.Task.WaitAsync(cancellationToken);
                return 0;
            }

            postGapSent = true;
            buffer.Span[0] = 0x98;
            buffer.Span[1] = 0x80;
            buffer.Span[2] = (byte)'\n';
            return 3;
        }

        internal void AllowPostGap() => allowPostGap.TrySetResult(true);
    }

}
