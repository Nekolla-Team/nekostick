using System.Collections.Immutable;
using System.Net;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Routing;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostRequestBodyAdmissionTests
{
    [Fact]
    public async Task ChunkedStaticBodyOverTheSnapshotLimitReturns413()
    {
        var dispatcher = CreateDispatcher(CreateSettings(maximumBodyBytes: 3));
        var context = CreateContext(new byte[] { 1, 2, 3, 4 });
        context.Request.Headers.TransferEncoding = "chunked";
        context.Request.ContentLength = null;

        await dispatcher.DispatchAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
    }

    [Fact]
    public async Task MicroserviceAndExtensionBodiesUseTheSameChunkedLimit()
    {
        RouteTargetConfiguration[] targets =
        [
            new MicroserviceRouteTargetConfiguration(Guid.CreateVersion7()),
            new ExtensionHandlerRouteTargetConfiguration("test.handler")
        ];

        foreach (var routeTarget in targets)
        {
            var target = new DrainingTargetExecutor();
            var dispatcher = CreateDispatcher(
                CreateSettings(maximumBodyBytes: 3),
                target,
                routeTarget: routeTarget);
            var context = CreateContext(new byte[] { 1, 2, 3, 4 });
            context.Request.ContentLength = null;
            context.Request.Headers.TransferEncoding = "chunked";

            await dispatcher.DispatchAsync(context);

            Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
            Assert.Equal(1, target.CallCount);
        }
    }

    [Fact]
    public async Task SnapshotHeaderLimitReturns431BeforeTargetExecution()
    {
        var target = new CountingTargetExecutor();
        var dispatcher = CreateDispatcher(CreateSettings(maximumHeaderBytes: 16), target);
        var context = CreateContext(Array.Empty<byte>());
        context.Request.Headers["X-Test"] = new string('a', 16);

        await dispatcher.DispatchAsync(context);

        Assert.Equal(StatusCodes.Status431RequestHeaderFieldsTooLarge, context.Response.StatusCode);
        Assert.Equal(0, target.CallCount);
    }

    [Fact]
    public async Task StaticBodyReadDeadlineReturns408()
    {
        var clock = new ControllableClock(DateTimeOffset.UnixEpoch);
        var dispatcher = CreateDispatcher(
            CreateSettings(requestReadTimeout: TimeSpan.FromSeconds(1)),
            admission: new HostRequestAdmission(clock));
        var body = new BlockingReadStream();
        var context = CreateContext(body);
        var pending = dispatcher.DispatchAsync(context);

        await body.ReadStarted;
        await clock.DelayScheduled;
        clock.Advance(TimeSpan.FromSeconds(1));
        await pending;

        Assert.Equal(StatusCodes.Status408RequestTimeout, context.Response.StatusCode);
    }

    [Fact]
    public async Task MicroserviceAndExtensionBodiesUseTheSameReadDeadline()
    {
        RouteTargetConfiguration[] targets =
        [
            new MicroserviceRouteTargetConfiguration(Guid.CreateVersion7()),
            new ExtensionHandlerRouteTargetConfiguration("test.handler")
        ];

        foreach (var routeTarget in targets)
        {
            var clock = new ControllableClock(DateTimeOffset.UnixEpoch);
            var dispatcher = CreateDispatcher(
                CreateSettings(requestReadTimeout: TimeSpan.FromSeconds(1)),
                new DrainingTargetExecutor(),
                admission: new HostRequestAdmission(clock),
                routeTarget: routeTarget);
            var body = new BlockingReadStream();
            var context = CreateContext(body);
            var pending = dispatcher.DispatchAsync(context);

            await body.ReadStarted;
            await clock.DelayScheduled;
            clock.Advance(TimeSpan.FromSeconds(1));
            await pending;

            Assert.Equal(StatusCodes.Status408RequestTimeout, context.Response.StatusCode);
        }
    }

    [Fact]
    public async Task RouteBodyAndHeaderOverridesRejectBeforeEveryTargetType()
    {
        RouteTargetConfiguration[] targets =
        [
            new StaticFileRouteTargetConfiguration(Path.GetTempPath()),
            new MicroserviceRouteTargetConfiguration(Guid.CreateVersion7()),
            new ExtensionHandlerRouteTargetConfiguration("test.handler")
        ];

        foreach (var routeTarget in targets)
        {
            var bodyTarget = new CountingTargetExecutor();
            var bodyDispatcher = CreateDispatcher(
                CreateSettings(maximumBodyBytes: 8, maximumHeaderBytes: 128),
                bodyTarget,
                routeTarget: routeTarget,
                routeMaximumBodyBytes: 3);
            var body = CreateContext(new byte[] { 1, 2, 3, 4 });

            await bodyDispatcher.DispatchAsync(body);

            Assert.Equal(StatusCodes.Status413PayloadTooLarge, body.Response.StatusCode);
            Assert.Equal(0, bodyTarget.CallCount);

            var headerTarget = new CountingTargetExecutor();
            var headerDispatcher = CreateDispatcher(
                CreateSettings(maximumBodyBytes: 8, maximumHeaderBytes: 128),
                headerTarget,
                routeTarget: routeTarget,
                routeMaximumHeaderBytes: 16);
            var header = CreateContext(Array.Empty<byte>());
            header.Request.Headers["X-Route"] = new string('x', 32);

            await headerDispatcher.DispatchAsync(header);

            Assert.Equal(StatusCodes.Status431RequestHeaderFieldsTooLarge, header.Response.StatusCode);
            Assert.Equal(0, headerTarget.CallCount);
        }
    }

    [Fact]
    public async Task RouteReadTimeoutOverrideAppliesToEveryTargetType()
    {
        RouteTargetConfiguration[] targets =
        [
            new StaticFileRouteTargetConfiguration(Path.GetTempPath()),
            new MicroserviceRouteTargetConfiguration(Guid.CreateVersion7()),
            new ExtensionHandlerRouteTargetConfiguration("test.handler")
        ];

        foreach (var routeTarget in targets)
        {
            var clock = new ControllableClock(DateTimeOffset.UnixEpoch);
            var dispatcher = CreateDispatcher(
                CreateSettings(requestReadTimeout: TimeSpan.FromSeconds(30)),
                new DrainingTargetExecutor(),
                new HostRequestAdmission(clock),
                routeTarget,
                routeRequestReadTimeout: TimeSpan.FromSeconds(1));
            var body = new BlockingReadStream();
            var context = CreateContext(body);
            var pending = dispatcher.DispatchAsync(context);

            await body.ReadStarted;
            await clock.DelayScheduled;
            clock.Advance(TimeSpan.FromSeconds(1));
            await pending;

            Assert.Equal(StatusCodes.Status408RequestTimeout, context.Response.StatusCode);
        }
    }

    [Fact]
    public async Task CompletedBodyReadCancelsItsDeadlineTimer()
    {
        var clock = new CancellationObservingClock();
        using var guard = new HostRequestBodyGuard(
            new MemoryStream([1]),
            maximumBytes: 2,
            readTimeout: TimeSpan.FromSeconds(1),
            requestAborted: CancellationToken.None,
            admissionContext: new HostRequestAdmissionContext(),
            clock: clock);
        var buffer = new byte[1];

        var count = await guard.ReadAsync(buffer, TestContext.Current.CancellationToken);
        await clock.CancellationObserved.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
    }

    private static HostRouteDispatcher CreateDispatcher(
        GlobalSettingsConfiguration settings,
        IRouteTargetExecutor? target = null,
        HostRequestAdmission? admission = null,
        RouteTargetConfiguration? routeTarget = null,
        long? routeMaximumBodyBytes = null,
        long? routeMaximumHeaderBytes = null,
        TimeSpan? routeRequestReadTimeout = null)
    {
        var configuration = CreateConfiguration(
            settings,
            routeTarget ?? new StaticFileRouteTargetConfiguration(Path.GetTempPath()),
            routeMaximumBodyBytes,
            routeMaximumHeaderBytes,
            routeRequestReadTimeout);
        var build = RouteMatchSnapshotBuilder.Build(configuration.Routes);
        var snapshot = new HostRoutingSnapshot(
            configuration,
            build.Snapshot ?? throw new InvalidOperationException("The route must compile."));
        return new HostRouteDispatcher(
            new FixedSnapshotAccessor(snapshot),
            NoOpRouteFallbackDispatcher.Instance,
            target ?? NoOpRouteTargetExecutor.Instance,
            admission ?? new HostRequestAdmission(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    private static HostConfigurationSnapshot CreateConfiguration(
        GlobalSettingsConfiguration settings,
        RouteTargetConfiguration target,
        long? routeMaximumBodyBytes,
        long? routeMaximumHeaderBytes,
        TimeSpan? routeRequestReadTimeout)
    {
        var route = new RouteConfiguration(
            id: Guid.CreateVersion7(),
            enabled: true,
            matcher: new RouteMatcherConfiguration(
                RouteMatcherType.Exact,
                "/limited",
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            target: target,
            priority: 0,
            forwarding: new ForwardingConfiguration(ForwardingMode.Preserve, null),
            requestHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
            responseHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
            metadataJson: "{}",
            createdAt: DateTimeOffset.UnixEpoch,
            updatedAt: DateTimeOffset.UnixEpoch,
            version: 1,
            maxRequestBodyBytes: routeMaximumBodyBytes,
            maxRequestHeaderBytes: routeMaximumHeaderBytes,
            requestReadTimeout: routeRequestReadTimeout);
        return new HostConfigurationSnapshot(
            version: 1,
            globalSettings: settings,
            routes: ImmutableArray.Create(route),
            services: ImmutableArray<ServiceConfiguration>.Empty,
            extensionRecords: ImmutableArray<ExtensionRecordConfiguration>.Empty,
            extensionSettings: ImmutableArray<ExtensionSettingsConfiguration>.Empty);
    }

    private static GlobalSettingsConfiguration CreateSettings(
        long maximumBodyBytes = GlobalSettingsConfiguration.HardMaximumRequestBodyBytes,
        long maximumHeaderBytes = GlobalSettingsConfiguration.HardMaximumRequestHeaderBytes,
        TimeSpan? requestReadTimeout = null) =>
        new(
            version: 1,
            maxRequestBodyBytes: maximumBodyBytes,
            maxRequestHeaderBytes: maximumHeaderBytes,
            requestReadTimeout: requestReadTimeout,
            maxConcurrentRequests: 8,
            configurationPollInterval: TimeSpan.FromSeconds(1));

    private static DefaultHttpContext CreateContext(byte[] body)
    {
        var context = CreateContext(new MemoryStream(body));
        context.Request.ContentLength = body.Length;
        return context;
    }

    private static DefaultHttpContext CreateContext(Stream body)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Method = "POST";
        context.Request.Host = new HostString("example.test");
        context.Request.Path = "/limited";
        context.Request.Body = body;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class FixedSnapshotAccessor : IHostRoutingSnapshotAccessor
    {
        internal FixedSnapshotAccessor(HostRoutingSnapshot snapshot) => Current = snapshot;

        public HostRoutingSnapshot Current { get; }
    }

    private sealed class CountingTargetExecutor : IRouteTargetExecutor
    {
        internal int CallCount { get; private set; }

        public ValueTask<RouteTargetExecutionResult> ExecuteAsync(
            HttpContext context,
            HostRoutingSnapshot snapshot,
            RouteMatch match,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(RouteTargetExecutionResult.Handled);
        }
    }

    private sealed class DrainingTargetExecutor : IRouteTargetExecutor
    {
        internal int CallCount { get; private set; }

        public async ValueTask<RouteTargetExecutionResult> ExecuteAsync(
            HttpContext context,
            HostRoutingSnapshot snapshot,
            RouteMatch match,
            CancellationToken cancellationToken)
        {
            CallCount++;
            await context.Request.Body.CopyToAsync(Stream.Null, cancellationToken);
            return RouteTargetExecutionResult.Handled;
        }
    }

    private sealed class ControllableClock : IHostRequestAdmissionClock
    {
        private readonly object _gate = new();
        private readonly List<ScheduledDelay> _delays = [];
        private readonly TaskCompletionSource _delayScheduled = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private DateTimeOffset _now;

        internal ControllableClock(DateTimeOffset now) => _now = now;

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (_gate)
                {
                    return _now;
                }
            }
        }

        internal Task DelayScheduled => _delayScheduled.Task;

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _delays.Add(new ScheduledDelay(_now + delay, completion));
            }

            _delayScheduled.TrySetResult();
            return new ValueTask(completion.Task);
        }

        internal void Advance(TimeSpan elapsed)
        {
            List<TaskCompletionSource> due = [];
            lock (_gate)
            {
                _now += elapsed;
                for (var index = _delays.Count - 1; index >= 0; index--)
                {
                    if (_delays[index].Due <= _now)
                    {
                        due.Add(_delays[index].Completion);
                        _delays.RemoveAt(index);
                    }
                }
            }

            foreach (var completion in due)
            {
                completion.TrySetResult();
            }
        }

        private sealed record ScheduledDelay(DateTimeOffset Due, TaskCompletionSource Completion);
    }

    private sealed class CancellationObservingClock : IHostRequestAdmissionClock
    {
        private readonly TaskCompletionSource _cancellationObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        internal Task CancellationObserved => _cancellationObserved.Task;

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _ = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                _cancellationObserved);
            return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task ReadStarted => _readStarted.Task;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            return new ValueTask<int>(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ContinueWith(
                    static _ => 0,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.Default));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
