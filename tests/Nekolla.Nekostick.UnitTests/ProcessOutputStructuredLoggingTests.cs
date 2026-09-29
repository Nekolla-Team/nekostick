using System.Diagnostics;
using System.Net;
using System.Text;
using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ProcessOutputStructuredLoggingTests
{
    [Fact]
    public void SinkLogsChildOutputTextAtTraceWithStreamMetadata()
    {
        var logger = new CapturingLogger();
        var sink = new HostProcessOutputLogSink(logger);
        var serviceId = Guid.Parse("0198a1af-6e94-7b25-9732-59c9075b14f6");
        var timestamp = new DateTimeOffset(2026, 8, 19, 12, 34, 56, TimeSpan.Zero);
        const string stdoutSecret = "stdout-secret-marker-7e0f";
        const string stderrSecret = "stderr-secret-marker-9a2c";

        sink.OnLine(new ProcessOutputRecord(
            serviceId,
            ProcessOutputStream.Stdout,
            timestamp,
            stdoutSecret,
            true));
        sink.OnLine(new ProcessOutputRecord(
            serviceId,
            ProcessOutputStream.Stderr,
            timestamp,
            stderrSecret,
            false));

        Assert.Equal(2, logger.Entries.Count);
        AssertOutputLine(logger.Entries[0], serviceId, "stdout", timestamp, stdoutSecret, true);
        AssertOutputLine(logger.Entries[1], serviceId, "stderr", timestamp, stderrSecret, false);
    }

    [Fact]
    public void SinkReportsLineDropsAndByteGapsAsWarningEvents()
    {
        var logger = new CapturingLogger();
        var sink = new HostProcessOutputLogSink(logger);
        var serviceId = Guid.Parse("0198a1af-6e94-7b25-9732-59c9075b14f6");

        sink.OnDropped(serviceId, ProcessOutputStream.Stderr, 7);
        sink.OnDropped(serviceId, (ProcessOutputStream)99, 1);
        sink.OnDropped(serviceId, ProcessOutputStream.Stdout, 0);
        sink.OnGap(serviceId, ProcessOutputStream.Stdout, 11);
        sink.OnGap(serviceId, (ProcessOutputStream)99, 1);
        sink.OnGap(serviceId, ProcessOutputStream.Stderr, 0);

        Assert.Equal(2, logger.Entries.Count);
        var dropped = logger.Entries[0];
        Assert.Equal(LogLevel.Warning, dropped.Level);
        Assert.Equal(1009, dropped.EventId.Id);
        Assert.Null(dropped.Exception);
        Assert.Equal(serviceId, Assert.IsType<Guid>(dropped.Fields["ServiceId"]));
        Assert.Equal("stderr", Assert.IsType<string>(dropped.Fields["Stream"]));
        Assert.IsType<DateTimeOffset>(dropped.Fields["Timestamp"]);
        Assert.Equal(7L, Assert.IsType<long>(dropped.Fields["DroppedCount"]));

        var gap = logger.Entries[1];
        Assert.Equal(LogLevel.Warning, gap.Level);
        Assert.Equal(1077, gap.EventId.Id);
        Assert.Null(gap.Exception);
        Assert.Equal(serviceId, Assert.IsType<Guid>(gap.Fields["ServiceId"]));
        Assert.Equal("stdout", Assert.IsType<string>(gap.Fields["Stream"]));
        Assert.IsType<DateTimeOffset>(gap.Fields["Timestamp"]);
        Assert.Equal(11L, Assert.IsType<long>(gap.Fields["DroppedBytes"]));
    }

    [Fact]
    public void ExecutorCaptureSubscriptionsEvaluateTheirGateAndDetach()
    {
        var sink = new DiscardingOutputSink();
        var traceEnabled = false;
        var executor = new PosixProcessExecutor();
        using var subscription = executor.SubscribeOutputCapture(sink, () => traceEnabled);

        Assert.False(executor.HasActiveOutputCapture);
        traceEnabled = true;
        Assert.True(executor.HasActiveOutputCapture);
        subscription.Dispose();
        Assert.False(executor.HasActiveOutputCapture);
    }

    [Fact]
    public async Task TraceDisabledLeavesCaptureDetachedAndSinkUnused()
    {
        var helperPath = RequireNativeHelperPath();
        var logger = new CapturingLogger();
        var executor = new PosixProcessExecutor(helperPath, TimeSpan.FromSeconds(2));
        using var subscription = executor.SubscribeOutputCapture(
            new HostProcessOutputLogSink(logger),
            static () => false);

        Assert.False(executor.HasActiveOutputCapture);
        var serviceId = Guid.Parse("0198a1af-6e94-7b25-9732-59c9075b14f6");
        var fifoPath = CreateFifo();
        try
        {
            var start = await executor.StartAsync(
                CreateLaunch(serviceId, $"read _ < {fifoPath}; printf trace-off"),
                TestContext.Current.CancellationToken);
            Assert.Equal(ProcessOperationStatus.Accepted, start.Status);
            AssertNoCaptureBindings(executor);

            var rawSink = new RecordingChunkSink();
            using var rawSubscription = executor.TrySubscribeOutput(
                serviceId,
                ProcessOutputStream.Stdout,
                rawSink) ?? throw new InvalidOperationException("The running fixture must expose stdout.");
            await File.WriteAllTextAsync(
                fifoPath,
                "\n",
                TestContext.Current.CancellationToken);
            var rawBytes = await rawSink.FirstChunk.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Equal("trace-off", Encoding.UTF8.GetString(rawBytes));
            await executor.StopAsync(
                serviceId,
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            await executor.CleanupAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);

            Assert.DoesNotContain(logger.Entries, entry => entry.EventId.Id == 1076);
        }
        finally
        {
            File.Delete(fifoPath);
        }
    }

    [Fact]
    public async Task TraceEnabledCapturesTextAndStreamThroughStructuredSink()
    {
        var helperPath = RequireNativeHelperPath();
        var logger = new CapturingLogger();
        var executor = new PosixProcessExecutor(helperPath, TimeSpan.FromSeconds(2));
        using var subscription = executor.SubscribeOutputCapture(
            new HostProcessOutputLogSink(logger),
            static () => true);

        Assert.True(executor.HasActiveOutputCapture);
        var serviceId = Guid.Parse("0198a1af-6e94-7b25-9732-59c9075b14f6");
        var start = await executor.StartAsync(
            CreateLaunch(serviceId, "printf trace-on; sleep 2"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ProcessOperationStatus.Accepted, start.Status);

        var trace = await logger.TraceEntry.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        AssertOutputLine(
            trace,
            serviceId,
            "stdout",
            trace.Fields["Timestamp"] is DateTimeOffset timestamp ? timestamp : default,
            "trace-on",
            truncated: false);

        await executor.StopAsync(
            serviceId,
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        await executor.CleanupAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReadyMarkerReaderLeavesChildBytesForFanout()
    {
        var method = typeof(PosixProcessExecutor).GetMethod(
            "ReadReadyMarkerAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var childBytes = Encoding.UTF8.GetBytes("child-output\n");
        await using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes("NK_READY\n").Concat(childBytes).ToArray());

        var markerRead = (ValueTask<bool>)method!.Invoke(
            null,
            [stream, TestContext.Current.CancellationToken])!;
        Assert.True(await markerRead);

        var remaining = new byte[childBytes.Length];
        var read = await stream.ReadAsync(remaining, TestContext.Current.CancellationToken);
        Assert.Equal(childBytes.Length, read);
        Assert.Equal(childBytes, remaining);
    }

    private static void AssertNoCaptureBindings(PosixProcessExecutor executor)
    {
        var leasesField = typeof(PosixProcessExecutor).GetField(
            "leases",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(leasesField);
        var entry = Assert.Single((IEnumerable)leasesField!.GetValue(executor)!)!;
        var lease = entry.GetType().GetProperty("Value")!.GetValue(entry)!;

        var bindingsField = lease.GetType().GetField(
            "captureBindings",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(bindingsField);
        Assert.Empty((ICollection)bindingsField!.GetValue(lease)!);

        foreach (var propertyName in new[] { "Stdout", "Stderr" })
        {
            var fanout = lease.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lease)!;
            var subscribersField = fanout.GetType().GetField(
                "subscribers",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(subscribersField);
            Assert.Empty((ICollection)subscribersField!.GetValue(fanout)!);
        }
    }
    private static string CreateFifo()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "nekostick-trace-fifo-" + Guid.NewGuid().ToString("N"));
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "mkfifo",
            ArgumentList = { path },
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        Assert.NotNull(process);
        process!.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    private static ProcessLaunchSpecification CreateLaunch(Guid serviceId, string command) =>
        new(
            serviceId,
            "/bin/sh",
            "/tmp",
            ImmutableArray.Create("-c", command),
            new ProcessEnvironment(new Dictionary<string, string>()));

    private static string RequireNativeHelperPath()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            Assert.Skip("POSIX process helper tests are unsupported on this platform.");
            return string.Empty;
        }

        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = baseDirectory; current is not null; current = current.Parent)
        {
            var candidates = new[]
            {
                Path.Combine(current.FullName, "Nekolla.Nekostick.NativeHelper"),
                Path.Combine(current.FullName, "src", "Nekolla.Nekostick.Host", ".nativehelper", "Debug", runtimeIdentifier, "Nekolla.Nekostick.NativeHelper"),
                Path.Combine(current.FullName, "src", "Nekolla.Nekostick.NativeHelper", "bin", "Debug", "net10.0", runtimeIdentifier, "Nekolla.Nekostick.NativeHelper"),
                Path.Combine(current.FullName, "src", "Nekolla.Nekostick.NativeHelper", "bin", "Debug", "net10.0", "Nekolla.Nekostick.NativeHelper")
            };
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        Assert.Skip("The compiled native process helper is unavailable; build the repository test artifacts first.");
        return string.Empty;
    }

    [Fact]
    public void HostProcessOutputLogSinkUsesItsLoggerCategoryForTraceGate()
    {
        var sinkCategory = new CategoryFilteredLogger("Nekolla.Nekostick.Host.HostProcessOutputLogSink");
        var startupCategory = new CategoryFilteredLogger("Nekolla.Nekostick.Host.Startup");

        Assert.True(new HostProcessOutputLogSink(sinkCategory).IsTraceEnabled);
        Assert.False(new HostProcessOutputLogSink(startupCategory).IsTraceEnabled);
    }

    [Fact]
    public void SafeConsoleEnforcesConfiguredLevelAcrossCategories()
    {
        using var provider = new SafeConsoleLoggerProvider();
        var supervision = provider.CreateLogger(HostLoggerCategory.Supervision);
        var startup = provider.CreateLogger(HostLoggerCategory.Startup);
        var framework = provider.CreateLogger("Microsoft.AspNetCore");

        Assert.True(supervision.IsEnabled(LogLevel.Information));
        Assert.False(supervision.IsEnabled(LogLevel.Debug));
        Assert.True(startup.IsEnabled(LogLevel.Information));
        Assert.True(startup.IsEnabled(LogLevel.Warning));
        Assert.True(framework.IsEnabled(LogLevel.Information));
    }

    [Fact]
    public void EntityFrameworkDebugLogsRequireExplicitOptIn()
    {
        using var excludedApplication = BuildApplication(disableSupervisor: true, includeEfLogs: false, logLevel: "debug");
        using var includedApplication = BuildApplication(disableSupervisor: true, includeEfLogs: true, logLevel: "debug");

        var excludedLogger = excludedApplication.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");
        var includedLogger = includedApplication.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

        Assert.False(excludedLogger.IsEnabled(LogLevel.Debug));
        Assert.True(includedLogger.IsEnabled(LogLevel.Debug));
    }


    [Fact]
    public void SafeConsoleFollowsConfiguredMinimumLevel()
    {
        using var debugProvider = new SafeConsoleLoggerProvider(LogLevel.Debug);
        Assert.True(debugProvider
            .CreateLogger(HostLoggerCategory.Startup)
            .IsEnabled(LogLevel.Debug));

        using var warningProvider = new SafeConsoleLoggerProvider(LogLevel.Warning);
        Assert.False(warningProvider
            .CreateLogger(HostLoggerCategory.Startup)
            .IsEnabled(LogLevel.Information));
        Assert.True(warningProvider
            .CreateLogger(HostLoggerCategory.Startup)
            .IsEnabled(LogLevel.Warning));

        using var noneProvider = new SafeConsoleLoggerProvider(LogLevel.None);
        Assert.False(noneProvider
            .CreateLogger(HostLoggerCategory.Startup)
            .IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void FormatLineWithoutColorEmitsTimestampAndLevelHeaders()
    {
        IReadOnlyList<KeyValuePair<string, object?>> state =
        [
            new("ServiceId", "service-a")
        ];

        var line = SafeConsoleLoggerProvider.FormatLine(
            LogLevel.Warning,
            new EventId(1008, "SupervisedChildOutput"),
            state,
            (_, _) => "Supervised child output. ServiceId: service-a.",
            useColor: false);

        Assert.Matches(@"^\[\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} (?:[+-]\d{2}|UTC)\] \[warn\] HOST_EVENT 1008: ", line);
        Assert.EndsWith("Supervised child output. ServiceId: service-a.", line);
        Assert.DoesNotContain("\x1b[", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatLineWithColorColorsTimestampLevelAndStructuredKeys()
    {
        IReadOnlyList<KeyValuePair<string, object?>> state =
        [
            new("ServiceId", "service-a"),
            new("{OriginalFormat}", "Supervised child output. ServiceId: {ServiceId}.")
        ];

        var line = SafeConsoleLoggerProvider.FormatLine(
            LogLevel.Error,
            new EventId(1008, "SupervisedChildOutput"),
            state,
            (_, _) => "Supervised child output. ServiceId: service-a.",
            useColor: true);

        Assert.Matches(@"^\[\x1b\[35m\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} (?:[+-]\d{2}|UTC)\x1b\[0m\] ", line);
        Assert.Contains("[\x1b[31merror\x1b[0m]", line, StringComparison.Ordinal);
        Assert.Contains("\x1b[36mServiceId\x1b[0m: service-a.", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\x1b[36m{OriginalFormat}", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatLineWithColorLeavesUnstructuredTextUncolored()
    {
        var line = SafeConsoleLoggerProvider.FormatLine(
            LogLevel.Information,
            new EventId(1003, "HostStartupFailed"),
            "plain state",
            (_, _) => "Host startup failed.",
            useColor: true);

        Assert.EndsWith("HOST_EVENT 1003: Host startup failed.", line);
        Assert.Equal(4, line.Split("\x1b[").Length - 1);
    }

    private static WebApplication BuildApplication(
        bool disableSupervisor,
        bool includeEfLogs = false,
        string logLevel = "information")
    {
        var buildApplication = typeof(Program).GetMethod(
            "BuildApplication",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The host application builder is required.");
        var arguments = new List<string>
        {
            "run",
            "--connection-string",
            "Host=127.0.0.1;Database=process_output_tests",
            "--log-level",
            logLevel
        };
        if (includeEfLogs)
        {
            arguments.Add(BootstrapDefaults.IncludeEfLogsOption);
        }

        var command = CliCommandParser.Parse(
            arguments,
            new Dictionary<string, string?>()).Command
            ?? throw new InvalidOperationException("The test run command must parse.");

        return Assert.IsType<WebApplication>(buildApplication.Invoke(
            null,
            [
                new CliCommand(
                    CliCommandKind.Run,
                    command.BootstrapOptions,
                    new RunOptions(
                        skipExtensions: true,
                        disableSupervisor: disableSupervisor,
                        readOnly: true)),
                IPAddress.Loopback
            ]));
    }
    private static void AssertOutputLine(
        CapturedLog entry,
        Guid serviceId,
        string stream,
        DateTimeOffset timestamp,
        string childOutputText,
        bool truncated)
    {
        Assert.Equal(LogLevel.Trace, entry.Level);
        Assert.Equal(1076, entry.EventId.Id);
        Assert.Null(entry.Exception);
        Assert.Equal(serviceId, Assert.IsType<Guid>(entry.Fields["ServiceId"]));
        Assert.Equal(stream, Assert.IsType<string>(entry.Fields["Stream"]));
        Assert.Equal(timestamp, Assert.IsType<DateTimeOffset>(entry.Fields["Timestamp"]));
        Assert.Equal(childOutputText, Assert.IsType<string>(entry.Fields["Text"]));
        Assert.Equal(truncated, Assert.IsType<bool>(entry.Fields["Truncated"]));
        Assert.Contains(childOutputText, entry.FormattedMessage, StringComparison.Ordinal);
    }

    private sealed class DiscardingOutputSink : IProcessOutputSink
    {
        public void OnLine(ProcessOutputRecord record)
        {
        }

        public void OnDropped(Guid serviceId, ProcessOutputStream stream, long count)
        {
        }
    }

    private sealed class RecordingChunkSink : IProcessOutputChunkSink
    {
        internal TaskCompletionSource<byte[]> FirstChunk { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnChunk(ReadOnlyMemory<byte> chunk, DateTimeOffset timestamp) =>
            FirstChunk.TrySetResult(chunk.ToArray());

        public void OnCompleted(ProcessOutputCompletion completion) { }

        public void OnDropped(long byteCount) { }
    }

    private sealed class CategoryFilteredLogger : ILogger
    {
        internal CategoryFilteredLogger(string category) => Category = category;

        private string Category { get; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            string.Equals(
                Category,
                "Nekolla.Nekostick.Host.HostProcessOutputLogSink",
                StringComparison.Ordinal)
            && logLevel == LogLevel.Trace;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
    private sealed class CapturingLogger : ILogger
    {
        private readonly List<CapturedLog> entries = [];

        internal List<CapturedLog> Entries => entries;
        internal TaskCompletionSource<CapturedLog> TraceEntry { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var value in values)
                {
                    fields[value.Key] = value.Value;
                }
            }

            var entry = new CapturedLog(
                logLevel,
                eventId,
                exception,
                formatter(state, exception),
                fields);
            lock (entries)
            {
                entries.Add(entry);
            }

            if (eventId.Id == 1076)
            {
                TraceEntry.TrySetResult(entry);
            }
        }
    }

    private sealed record CapturedLog(
        LogLevel Level,
        EventId EventId,
        Exception? Exception,
        string FormattedMessage,
        IReadOnlyDictionary<string, object?> Fields);


}
