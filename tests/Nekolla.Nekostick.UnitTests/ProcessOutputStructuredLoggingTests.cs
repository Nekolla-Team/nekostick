using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ProcessOutputStructuredLoggingTests
{
    [Fact]
    public void SinkNeverLogsRawChildOutputText()
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
            "Warning",
            stdoutSecret,
            true));
        sink.OnLine(new ProcessOutputRecord(
            serviceId,
            ProcessOutputStream.Stderr,
            timestamp,
            "Information",
            stderrSecret,
            false));

        Assert.Equal(2, logger.Entries.Count);
        AssertLine(
            logger.Entries[0],
            LogLevel.Information,
            serviceId,
            "stdout",
            timestamp,
            stdoutSecret,
            true);
        AssertLine(
            logger.Entries[1],
            LogLevel.Warning,
            serviceId,
            "stderr",
            timestamp,
            stderrSecret,
            false);
    }

    [Fact]
    public void SinkReportsAggregateDropsAndContainsLoggerFailures()
    {
        var logger = new CapturingLogger();
        var sink = new HostProcessOutputLogSink(logger);
        var serviceId = Guid.Parse("0198a1af-6e94-7b25-9732-59c9075b14f6");

        sink.OnDropped(serviceId, ProcessOutputStream.Stderr, 7);
        sink.OnDropped(serviceId, (ProcessOutputStream)99, 1);
        sink.OnDropped(serviceId, ProcessOutputStream.Stdout, 0);

        var dropped = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, dropped.Level);
        Assert.Equal(1009, dropped.EventId.Id);
        Assert.Null(dropped.Exception);
        Assert.Equal(serviceId, Assert.IsType<Guid>(dropped.Fields["ServiceId"]));
        Assert.Equal("stderr", Assert.IsType<string>(dropped.Fields["Stream"]));
        Assert.IsType<DateTimeOffset>(dropped.Fields["Timestamp"]);
        Assert.False(dropped.Fields.ContainsKey("Text"));
        Assert.False(Assert.IsType<bool>(dropped.Fields["Truncated"]));
        Assert.True(Assert.IsType<bool>(dropped.Fields["Dropped"]));
        Assert.Equal(7L, Assert.IsType<long>(dropped.Fields["DroppedCount"]));

        var throwingSink = new HostProcessOutputLogSink(new ThrowingLogger());
        throwingSink.OnLine(new ProcessOutputRecord(
            serviceId,
            ProcessOutputStream.Stdout,
            DateTimeOffset.UtcNow,
            "Information",
            "ignored",
            false));
        throwingSink.OnDropped(serviceId, ProcessOutputStream.Stderr, 1);
    }

    [Fact]
    public void ExecutorAcceptsOutputSinkAndHostRegistersItOnlyWhenSupervisorIsActive()
    {
        var suppliedSink = new DiscardingOutputSink();
        var executor = new PosixProcessExecutor(outputSink: suppliedSink);
        var defaultExecutor = new PosixProcessExecutor();
        var nullExecutor = new PosixProcessExecutor(outputSink: null);

        Assert.Same(suppliedSink, GetOutputSink(executor));
        Assert.NotNull(GetOutputSink(defaultExecutor));
        Assert.NotNull(GetOutputSink(nullExecutor));

        using var activeApplication = BuildApplication(disableSupervisor: false);
        var activeSink = activeApplication.Services.GetRequiredService<IProcessOutputSink>();
        var activeExecutor = Assert.IsType<PosixProcessExecutor>(
            activeApplication.Services.GetRequiredService<IProcessExecutor>());

        Assert.IsType<HostProcessOutputLogSink>(activeSink);
        Assert.Same(activeSink, GetOutputSink(activeExecutor));

        using var disabledApplication = BuildApplication(disableSupervisor: true);
        Assert.Null(disabledApplication.Services.GetService<IProcessOutputSink>());
        Assert.Null(disabledApplication.Services.GetService<IProcessExecutor>());
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
    private static void AssertLine(
        CapturedLog entry,
        LogLevel expectedLevel,
        Guid serviceId,
        string stream,
        DateTimeOffset timestamp,
        string childOutputText,
        bool truncated)
    {
        Assert.Equal(expectedLevel, entry.Level);
        Assert.Equal(1008, entry.EventId.Id);
        Assert.Null(entry.Exception);
        Assert.Equal(serviceId, Assert.IsType<Guid>(entry.Fields["ServiceId"]));
        Assert.Equal(stream, Assert.IsType<string>(entry.Fields["Stream"]));
        Assert.Equal(timestamp, Assert.IsType<DateTimeOffset>(entry.Fields["Timestamp"]));
        Assert.False(entry.Fields.ContainsKey("Text"));
        Assert.DoesNotContain(
            childOutputText,
            entry.FormattedMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            childOutputText,
            FormatStructuredFields(entry),
            StringComparison.Ordinal);
        Assert.Equal(truncated, Assert.IsType<bool>(entry.Fields["Truncated"]));
        Assert.False(Assert.IsType<bool>(entry.Fields["Dropped"]));
        Assert.Equal(0L, Assert.IsType<long>(entry.Fields["DroppedCount"]));
    }

    private static string FormatStructuredFields(CapturedLog entry) => string.Join(
        "\n",
        entry.Fields.Select(pair => $"{pair.Key}={pair.Value}"));

    private static IProcessOutputSink GetOutputSink(PosixProcessExecutor executor)
    {
        var outputSinkField = typeof(PosixProcessExecutor).GetField(
            "outputSink",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The executor output sink field is required.");
        return outputSinkField.GetValue(executor) as IProcessOutputSink
            ?? throw new InvalidOperationException("The executor must retain an output sink.");
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

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger failure");
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<CapturedLog> entries = [];

        internal List<CapturedLog> Entries => entries;

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

            entries.Add(new CapturedLog(
                logLevel,
                eventId,
                exception,
                formatter(state, exception),
                fields));
        }
    }

    private sealed record CapturedLog(
        LogLevel Level,
        EventId EventId,
        Exception? Exception,
        string FormattedMessage,
        IReadOnlyDictionary<string, object?> Fields);
}
