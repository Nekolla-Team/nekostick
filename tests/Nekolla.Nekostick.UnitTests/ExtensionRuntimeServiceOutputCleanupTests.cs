using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed partial class ExtensionRuntimeTests
{
    [Fact]
    public async Task ReleaseBoundsBlockedServiceLogCleanupAndLogsTimeout()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var blockPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var log = new BlockingServiceLog();
        var logger = new TimeoutLogger();
        var factory = new BlockingCapabilityFactory(log);
        await using var manager = new ExtensionRuntimeManager(
            new HostApiVersion(1, 4, 0),
            capabilityFactory: factory,
            logger: logger);

        var callbackClientTask = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        var load = await manager.LoadAsync(
            manifest,
            Settings(
                manifest.Id,
                label: "blocked-service-output",
                verifyBridgeCapabilities: true,
                lifecycleObservationPort: blockPort,
                holdServiceOutput: true),
            TestContext.Current.CancellationToken);
        Assert.True(load.Succeeded, load.FailureCode.ToString());

        TcpClient? callbackClient = null;
        Task<ExtensionRuntimeOperationResult>? unloadTask = null;
        try
        {
            callbackClient = await callbackClientTask.AsTask().WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            using var callbackStream = callbackClient.GetStream();
            var started = new byte[1];
            await callbackStream.ReadExactlyAsync(
                started,
                TestContext.Current.CancellationToken);
            Assert.Equal(1, started[0]);

            var stopwatch = Stopwatch.StartNew();
            unloadTask = manager.UnloadAsync(
                manifest.Id,
                TestContext.Current.CancellationToken).AsTask();
            var timeout = await logger.TimeoutEvent.Task.WaitAsync(
                TimeSpan.FromSeconds(7),
                TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.Equal(2019, timeout.EventId.Id);
            Assert.Equal(LogLevel.Warning, timeout.Level);
            Assert.IsType<TimeoutException>(timeout.Exception);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(7));

            var unload = await unloadTask.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            Assert.NotEqual(ExtensionFailureCode.Cancelled, unload.FailureCode);
            Assert.Equal(1, log.DetachAllCount);

            callbackStream.WriteByte(1);
            log.ReleaseCleanup();
        }
        finally
        {
            if (callbackClient is not null)
            {
                try
                {
                    callbackClient.GetStream().WriteByte(1);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }

                callbackClient.Dispose();
            }

            log.ReleaseCleanup();
            if (unloadTask is not null)
            {
                try
                {
                    await unloadTask.WaitAsync(
                        TimeSpan.FromSeconds(2),
                        TestContext.Current.CancellationToken);
                }
                catch (TimeoutException)
                {
                }
            }
        }
    }

    private sealed class BlockingCapabilityFactory : IExtensionCapabilityFactory
    {
        private readonly BlockingServiceLog _log;

        public BlockingCapabilityFactory(BlockingServiceLog log) => _log = log;

        public ExtensionCapabilitySet Create(string extensionId, Func<string, bool> handlerIsOwned)
        {
            var unsupported = UnsupportedExtensionCapabilities.Create();
            return new ExtensionCapabilitySet(
                unsupported.ConfigurationApi,
                unsupported.Routes,
                unsupported.Services,
                unsupported.Endpoints,
                unsupported.FullConfiguration,
                unsupported.Supervisor,
                unsupported.RouteEvents,
                unsupported.LogWriter,
                unsupported.ExtensionManagement,
                unsupported.HostInfo,
                _log,
                null);
        }
    }


    private sealed class BlockingServiceLog :
        IExtensionServiceOutputApi,
        IExtensionServiceOutputCleanup,
        IExtensionServiceLogCleanup
    {
        private readonly TaskCompletionSource<object?> _cleanupRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<ExtensionServiceOutputStreamResult> OpenStreamAsync(
            Guid serviceId,
            ExtensionServiceOutputStream stream,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtensionServiceOutputStreamResult(
                true,
                ExtensionServiceOutputCode.Opened,
                serviceId,
                new MemoryStream(),
                detail: null));

        public ValueTask<ExtensionServiceLogSubscriptionResult> SubscribeAsync(
            Guid serviceId,
            IExtensionServiceLogSink sink,
            long? sinceSequence = null,
            CancellationToken cancellationToken = default)
        {
            var currentState = new ExtensionServiceLogEntry(
                ExtensionServiceLogEntryKind.CurrentState,
                serviceId,
                sequence: null,
                DateTimeOffset.UtcNow,
                lifecycleState: ExtensionServiceLifecycleState.Unknown);
            _ = Task.Run(() => sink.OnEntry(currentState), CancellationToken.None);
            return ValueTask.FromResult(new ExtensionServiceLogSubscriptionResult(
                true,
                ExtensionServiceLogCode.Subscribed,
                serviceId,
                new CompletedSubscription(),
                detail: null));
        }

        public ValueTask DisposeAsync() => new(_cleanupRelease.Task);
        internal int DetachAllCount => Volatile.Read(ref detachAllCount);

        private int detachAllCount;

        public void DetachAll() => Interlocked.Increment(ref detachAllCount);

        public void ReleaseCleanup() => _cleanupRelease.TrySetResult(null);
    }

    private sealed class CompletedSubscription : IExtensionServiceLogSubscription
    {
        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }


    private sealed class TimeoutLogger : ILogger
    {
        internal TaskCompletionSource<CapturedLog> TimeoutEvent { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal int TimeoutEventCount => Volatile.Read(ref timeoutEventCount);

        private int timeoutEventCount;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var captured = new CapturedLog(logLevel, eventId, exception);
            if (eventId.Id == 2019)
            {
                Interlocked.Increment(ref timeoutEventCount);
                TimeoutEvent.TrySetResult(captured);
            }
        }
    }

    private sealed record CapturedLog(LogLevel Level, EventId EventId, Exception? Exception);
}
