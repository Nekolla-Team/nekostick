using System.Collections.Generic;
using System.IO;
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
    public async Task ReadAsyncEmitsStdoutAndStderrRecordsWithStreamSpecificLevels()
    {
        var stdoutReader = new DeterministicTextReader("first\nlast\n", chunkSize: 2);
        var stdoutSink = new RecordingSink();

        await ProcessOutputCapture.ReadAsync(
            stdoutReader,
            ServiceId,
            ProcessOutputStream.Stdout,
            CreateBudget(),
            stdoutSink,
            CancellationToken.None);

        Assert.Equal(2, stdoutSink.Records.Count);
        AssertRecord(stdoutSink.Records[0], ProcessOutputStream.Stdout, "Information", "first");
        AssertRecord(stdoutSink.Records[1], ProcessOutputStream.Stdout, "Information", "last");
        Assert.Empty(stdoutSink.Drops);
        Assert.True(stdoutReader.WasDisposed);

        var stderrReader = new DeterministicTextReader("failure\n", chunkSize: 1);
        var stderrSink = new RecordingSink();

        await ProcessOutputCapture.ReadAsync(
            stderrReader,
            ServiceId,
            ProcessOutputStream.Stderr,
            CreateBudget(),
            stderrSink,
            CancellationToken.None);

        var stderrRecord = Assert.Single(stderrSink.Records);
        AssertRecord(stderrRecord, ProcessOutputStream.Stderr, "Warning", "failure");
        Assert.Empty(stderrSink.Drops);
        Assert.True(stderrReader.WasDisposed);
    }

    [Fact]
    public async Task ReadAsyncTruncatesALineAt16KiBAndMarksTheRecord()
    {
        var reader = new DeterministicTextReader(
            new string('x', MaximumLineLength + 17) + "\n",
            chunkSize: 257);
        var sink = new RecordingSink();

        await ProcessOutputCapture.ReadAsync(
            reader,
            ServiceId,
            ProcessOutputStream.Stdout,
            CreateBudget(),
            sink,
            CancellationToken.None);

        var record = Assert.Single(sink.Records);
        Assert.Equal(new string('x', MaximumLineLength), record.Text);
        Assert.True(record.Truncated);
        Assert.Equal(ServiceId, record.ServiceId);
        Assert.Equal(ProcessOutputStream.Stdout, record.Stream);
        Assert.Empty(sink.Drops);
        Assert.True(reader.WasDisposed);
    }

    [Fact]
    public async Task ReadAsyncReportsDroppedLineWhenCaptureCompletes()
    {
        var reader = new DeterministicTextReader("dropped\n", chunkSize: 3);
        var sink = new RecordingSink();

        await ProcessOutputCapture.ReadAsync(
            reader,
            ServiceId,
            ProcessOutputStream.Stderr,
            new ProcessOutputBudget(maximumLines: 0, maximumBytes: MaximumBytesPerSecond),
            sink,
            CancellationToken.None);

        Assert.Empty(sink.Records);
        var dropped = Assert.Single(sink.Drops);
        Assert.Equal(ServiceId, dropped.ServiceId);
        Assert.Equal(ProcessOutputStream.Stderr, dropped.Stream);
        Assert.Equal(1, dropped.Count);
        Assert.True(reader.WasDisposed);
    }

    [Fact]
    public void ProcessOutputBudgetEnforces200LinesWithinADeterministicSecond()
    {
        var budget = CreateBudget();
        var firstWindow = DateTimeOffset.MaxValue.AddDays(-1);

        for (var index = 0; index < MaximumLinesPerSecond; index++)
        {
            Assert.True(budget.TryAccept(1, firstWindow, out var dropped));
            Assert.Equal(0, dropped);
        }

        Assert.False(budget.TryAccept(1, firstWindow, out var sameWindowDrops));
        Assert.Equal(0, sameWindowDrops);
        Assert.True(
            budget.TryAccept(
                1,
                firstWindow.AddSeconds(1),
                out var nextWindowDrops));
        Assert.Equal(1, nextWindowDrops);
    }

    [Fact]
    public void ProcessOutputBudgetEnforces1MiBWithinADeterministicSecond()
    {
        var budget = CreateBudget();
        var firstWindow = DateTimeOffset.MaxValue.AddDays(-1);

        Assert.True(
            budget.TryAccept(
                MaximumBytesPerSecond,
                firstWindow,
                out var initialDrops));
        Assert.Equal(0, initialDrops);
        Assert.False(budget.TryAccept(1, firstWindow, out _));
        Assert.False(budget.TryAccept(1, firstWindow, out _));
        Assert.True(
            budget.TryAccept(
                1,
                firstWindow.AddSeconds(1),
                out var nextWindowDrops));
        Assert.Equal(2, nextWindowDrops);
    }

    [Fact]
    public async Task ReadAsyncCancellationDisposesTheReaderAndCompletesResources()
    {
        var reader = new CancellationTextReader();
        var sink = new RecordingSink();
        using var cancellation = new CancellationTokenSource();
        var capture = ProcessOutputCapture.ReadAsync(
            reader,
            ServiceId,
            ProcessOutputStream.Stdout,
            CreateBudget(),
            sink,
            cancellation.Token);

        await reader.ReadStarted;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await capture);

        Assert.True(reader.WasDisposed);
        Assert.Empty(sink.Records);
        Assert.Empty(sink.Drops);
    }

    private static ProcessOutputBudget CreateBudget() =>
        new(MaximumLinesPerSecond, MaximumBytesPerSecond);

    private static void AssertRecord(
        ProcessOutputRecord record,
        ProcessOutputStream stream,
        string level,
        string text)
    {
        Assert.Equal(ServiceId, record.ServiceId);
        Assert.Equal(stream, record.Stream);
        Assert.Equal(level, record.Level);
        Assert.Equal(text, record.Text);
        Assert.False(record.Truncated);
        Assert.Equal(TimeSpan.Zero, record.Timestamp.Offset);
    }

    private sealed class RecordingSink : IProcessOutputSink
    {
        internal List<ProcessOutputRecord> Records { get; } = [];

        internal List<DroppedOutput> Drops { get; } = [];

        public void OnLine(ProcessOutputRecord record) => Records.Add(record);

        public void OnDropped(Guid serviceId, ProcessOutputStream stream, long count) =>
            Drops.Add(new DroppedOutput(serviceId, stream, count));
    }

    private sealed record DroppedOutput(
        Guid ServiceId,
        ProcessOutputStream Stream,
        long Count);

    private sealed class DeterministicTextReader : TextReader
    {
        private readonly string text;
        private readonly int chunkSize;
        private int position;

        internal DeterministicTextReader(string text, int chunkSize)
        {
            this.text = text;
            this.chunkSize = chunkSize;
        }

        internal bool WasDisposed { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position == text.Length)
            {
                return ValueTask.FromResult(0);
            }

            var count = Math.Min(
                Math.Min(chunkSize, buffer.Length),
                text.Length - position);
            text.AsSpan(position, count).CopyTo(buffer.Span);
            position += count;
            return ValueTask.FromResult(count);
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class CancellationTextReader : TextReader
    {
        private readonly TaskCompletionSource<bool> readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task ReadStarted => readStarted.Task;

        internal bool WasDisposed { get; private set; }

        public override async ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            readStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
