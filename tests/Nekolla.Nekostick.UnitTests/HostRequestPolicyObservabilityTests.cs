using System.Collections.Immutable;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Routing;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostRequestPolicyObservabilityTests
{
    [Fact]
    public async Task RouteResourceOverridesTightenBodyAndHeaderAndNullInherits()
    {
        var routeId = RoutingTestData.Id(910);
        const string sensitive = "sensitive-request-admission-marker";
        var route = CreateRoute(routeId, new StaticFileRouteTargetConfiguration(Path.GetTempPath()), maxBody: 3, maxHeader: 64);
        var settings = CreateSettings(maxBody: 8, maxHeader: 128);
        var target = new CountingTarget();
        var logger = new CapturingLogger();
        var dispatcher = CreateDispatcher(CreateSnapshot(route, settings), target, logger: logger);

        var bodyContext = CreateContext("/selected", Encoding.UTF8.GetBytes(sensitive));
        await dispatcher.DispatchAsync(bodyContext);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, bodyContext.Response.StatusCode);
        AssertResponseMessage(bodyContext, "Payload too large.", sensitive);
        Assert.Equal(0, target.CallCount);

        var headerContext = CreateContext("/selected", []);
        AddSensitiveRequestMarkers(headerContext, sensitive);
        headerContext.Request.Headers["X-Route"] = sensitive;
        await dispatcher.DispatchAsync(headerContext);
        Assert.Equal(StatusCodes.Status431RequestHeaderFieldsTooLarge, headerContext.Response.StatusCode);
        AssertResponseMessage(headerContext, "Request header fields too large.", sensitive);
        Assert.Equal(0, target.CallCount);
        var resourceEvents = logger.Entries
            .Where(entry => entry.EventId == HostEventIds.AdmissionResourceRejected)
            .ToArray();
        Assert.Equal(2, resourceEvents.Length);
        Assert.All(resourceEvents, entry =>
        {
            Assert.Equal(LogLevel.Warning, entry.Level);
            AssertAdmissionEventShape(entry, sensitive);
        });
        Assert.Contains(resourceEvents, entry => Equals(entry.Fields["FailureKind"], HostRequestAdmissionFailureKind.RequestBody) && Equals(entry.Fields["StatusCode"], StatusCodes.Status413PayloadTooLarge) && Equals(entry.Fields["RouteId"], routeId) && Equals(entry.Fields["TargetType"], RouteTargetType.StaticFile));
        Assert.Contains(resourceEvents, entry => Equals(entry.Fields["FailureKind"], HostRequestAdmissionFailureKind.RequestHeaders) && Equals(entry.Fields["StatusCode"], StatusCodes.Status431RequestHeaderFieldsTooLarge) && Equals(entry.Fields["RouteId"], routeId) && Equals(entry.Fields["TargetType"], RouteTargetType.StaticFile));

        var inherited = CreateRoute(RoutingTestData.Id(911), new StaticFileRouteTargetConfiguration(Path.GetTempPath()));
        var inheritedDispatcher = CreateDispatcher(CreateSnapshot(inherited, CreateSettings(maxBody: 3)), new CountingTarget());
        var inheritedContext = CreateContext("/selected", new byte[] { 1, 2, 3, 4 });
        await inheritedDispatcher.DispatchAsync(inheritedContext);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, inheritedContext.Response.StatusCode);
    }

    [Fact]
    public async Task RouteConcurrencyLeaseIsReleasedAndNullInheritanceUsesGlobalOnly()
    {
        var route = CreateRoute(RoutingTestData.Id(912), new StaticFileRouteTargetConfiguration(Path.GetTempPath()), maxConcurrent: 1);
        var snapshot = CreateSnapshot(route, CreateSettings(maxConcurrent: 4));
        var admission = new HostRequestAdmission();
        var firstGlobal = await admission.TryAcquireGlobalAsync(snapshot, CreateContext("/selected", []));
        var secondGlobal = await admission.TryAcquireGlobalAsync(snapshot, CreateContext("/selected", []));
        Assert.NotNull(firstGlobal.Lease);
        Assert.NotNull(secondGlobal.Lease);
        var match = GetMatch(snapshot);

        var firstRoute = await admission.TryAcquireRouteAsync(snapshot, match, CreateContext("/selected", []));
        var rejectedRoute = await admission.TryAcquireRouteAsync(snapshot, match, CreateContext("/selected", []));
        Assert.NotNull(firstRoute.Lease);
        Assert.Equal(HostRequestAdmissionFailureKind.Concurrency, rejectedRoute.Rejection?.Kind);

        firstRoute.Lease!.Dispose();
        var releasedRoute = await admission.TryAcquireRouteAsync(snapshot, match, CreateContext("/selected", []));
        Assert.Null(releasedRoute.Rejection);
        releasedRoute.Lease!.Dispose();
        firstGlobal.Lease!.Dispose();
        secondGlobal.Lease!.Dispose();

        var inheritedRoute = CreateRoute(RoutingTestData.Id(913), new StaticFileRouteTargetConfiguration(Path.GetTempPath()));
        var inheritedSnapshot = CreateSnapshot(inheritedRoute, CreateSettings(maxConcurrent: 1));
        var inheritedMatch = GetMatch(inheritedSnapshot);
        var inherited = await admission.TryAcquireRouteAsync(
            inheritedSnapshot,
            inheritedMatch,
            CreateContext("/selected", []));
        Assert.Null(inherited.Lease);
    }

    [Fact]
    public async Task RouteReadTimeoutUsesOverrideAndEmitsOneTypedRejection()
    {
        const string sensitive = "sensitive-request-timeout-marker";
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var route = CreateRoute(
            RoutingTestData.Id(914),
            new StaticFileRouteTargetConfiguration(Path.GetTempPath()),
            readTimeout: TimeSpan.FromSeconds(1));
        var logger = new CapturingLogger();
        var dispatcher = CreateDispatcher(
            CreateSnapshot(route, CreateSettings(readTimeout: TimeSpan.FromSeconds(30))),
            new CountingTarget(),
            new HostRequestAdmission(clock),
            logger);
        var body = new BlockingReadStream();
        var context = CreateContext("/selected", body);
        AddSensitiveRequestMarkers(context, sensitive);
        var pending = dispatcher.DispatchAsync(context);
        await body.ReadStarted;
        await clock.DelayScheduled;
        clock.Advance(TimeSpan.FromSeconds(1));
        await pending;

        Assert.Equal(StatusCodes.Status408RequestTimeout, context.Response.StatusCode);
        AssertResponseMessage(context, "Request timeout.", sensitive);
        var timeoutEvent = Assert.Single(
            logger.Entries,
            entry => entry.EventId == HostEventIds.AdmissionResourceRejected);
        Assert.Equal(HostRequestAdmissionFailureKind.RequestReadTimeout, timeoutEvent.Fields["FailureKind"]);
        Assert.Equal(StatusCodes.Status408RequestTimeout, timeoutEvent.Fields["StatusCode"]);
        Assert.Equal(LogLevel.Warning, timeoutEvent.Level);
        AssertAdmissionEventShape(timeoutEvent, sensitive);
    }

    [Fact]
    public async Task GlobalAndRouteRateRejectionsEmitBoundedTypedEvents()
    {
        const string sensitive = "sensitive-request-rate-marker";
        var globalLogger = new CapturingLogger();
        var globalRoute = CreateRoute(RoutingTestData.Id(915), new StaticFileRouteTargetConfiguration(Path.GetTempPath()));
        var globalSnapshot = CreateSnapshot(
            globalRoute,
            CreateSettings(globalPolicy: CreatePolicy(1, RateLimitRetryAfterBehavior.FromReplenishmentPeriod)));
        var globalDispatcher = CreateDispatcher(globalSnapshot, new CountingTarget(), logger: globalLogger);
        var globalFirst = CreateContext("/selected", []);
        AddSensitiveRequestMarkers(globalFirst, sensitive);
        await globalDispatcher.DispatchAsync(globalFirst);
        var globalRejected = CreateContext("/selected", []);
        AddSensitiveRequestMarkers(globalRejected, sensitive);
        await globalDispatcher.DispatchAsync(globalRejected);
        AssertResponseMessage(globalRejected, "Too many requests.", sensitive);
        var globalEvent = Assert.Single(globalLogger.Entries, entry => entry.EventId == HostEventIds.AdmissionResourceRejected);
        Assert.Equal(HostRequestAdmissionFailureKind.RateLimit, globalEvent.Fields["FailureKind"]);
        Assert.Equal(StatusCodes.Status429TooManyRequests, globalEvent.Fields["StatusCode"]);
        Assert.Null(globalEvent.Fields["RouteId"]);
        Assert.Equal(true, globalEvent.Fields["RetryAfterPresent"]);
        Assert.Equal(3600, globalEvent.Fields["RetryAfterSeconds"]);
        Assert.Null(globalEvent.Fields["TargetType"]);
        Assert.Equal(LogLevel.Warning, globalEvent.Level);
        AssertAdmissionEventShape(globalEvent, sensitive);

        var routeLogger = new CapturingLogger();
        var routeId = RoutingTestData.Id(916);
        var route = CreateRoute(
            routeId,
            new StaticFileRouteTargetConfiguration(Path.GetTempPath()),
            policy: CreatePolicy(1));
        var routeDispatcher = CreateDispatcher(
            CreateSnapshot(route, CreateSettings()),
            new CountingTarget(),
            logger: routeLogger);
        var routeFirst = CreateContext("/selected", []);
        AddSensitiveRequestMarkers(routeFirst, sensitive);
        await routeDispatcher.DispatchAsync(routeFirst);
        var routeRejected = CreateContext("/selected", []);
        AddSensitiveRequestMarkers(routeRejected, sensitive);
        await routeDispatcher.DispatchAsync(routeRejected);
        AssertResponseMessage(routeRejected, "Too many requests.", sensitive);
        var routeEvent = Assert.Single(routeLogger.Entries, entry => entry.EventId == HostEventIds.AdmissionResourceRejected);
        Assert.Equal(routeId, routeEvent.Fields["RouteId"]);
        Assert.Equal(RouteTargetType.StaticFile, routeEvent.Fields["TargetType"]);
        Assert.Equal(false, routeEvent.Fields["RetryAfterPresent"]);
        Assert.Equal(LogLevel.Warning, routeEvent.Level);
        AssertAdmissionEventShape(routeEvent, sensitive);
    }

    [Fact]
    public async Task MatchedSummaryStaticRejectionAndProxyFailureContainOnlyCategories()
    {
        const string sensitive = "sensitive-request-target-marker";
        var handledLogger = new CapturingLogger();
        var handledRoute = CreateRoute(
            RoutingTestData.Id(917),
            new StaticFileRouteTargetConfiguration(Path.Combine(Path.GetTempPath(), sensitive)));
        var handledDispatcher = CreateDispatcher(
            CreateSnapshot(handledRoute, CreateSettings()),
            new ResultTarget(RouteTargetExecutionResult.Handled, StatusCodes.Status202Accepted),
            logger: handledLogger);
        var handledContext = CreateContext("/selected", []);
        handledContext.Request.QueryString = new QueryString("?marker=" + sensitive);
        handledContext.Request.Host = new HostString(sensitive);
        handledContext.Request.Headers["X-Sensitive"] = sensitive;
        await handledDispatcher.DispatchAsync(handledContext);
        var summary = Assert.Single(handledLogger.Entries, entry => entry.EventId == HostEventIds.RouteOutcomeSummary);
        Assert.Equal(handledRoute.Id, summary.Fields["RouteId"]);
        Assert.Equal(RouteTargetExecutionResult.Handled, summary.Fields["Outcome"]);
        Assert.Equal(StatusCodes.Status202Accepted, summary.Fields["StatusCode"]);
        Assert.Equal(LogLevel.Warning, summary.Level);
        AssertSafe(handledLogger, sensitive);

        var staticLogger = new CapturingLogger();
        var staticRoute = CreateRoute(RoutingTestData.Id(918), new StaticFileRouteTargetConfiguration(Path.GetTempPath()));
        var staticDispatcher = CreateDispatcher(
            CreateSnapshot(staticRoute, CreateSettings()),
            new ResultTarget(RouteTargetExecutionResult.NotFound),
            logger: staticLogger);
        var staticContext = CreateContext("/selected", []);
        staticContext.Request.Host = new HostString(sensitive);
        staticContext.Request.Headers["X-Sensitive"] = sensitive;
        await staticDispatcher.DispatchAsync(staticContext);
        var staticEvent = Assert.Single(
            staticLogger.Entries,
            entry => entry.EventId == HostEventIds.StaticRejection);
        Assert.Equal(LogLevel.Warning, staticEvent.Level);
        Assert.Equal(staticRoute.Id, staticEvent.Fields["RouteId"]);
        Assert.Equal(RouteTargetType.StaticFile, staticEvent.Fields["TargetType"]);
        Assert.Equal(RouteTargetExecutionResult.NotFound, staticEvent.Fields["Outcome"]);
        Assert.Equal(StatusCodes.Status404NotFound, staticEvent.Fields["StatusCode"]);
        Assert.Single(staticLogger.Entries, entry => entry.EventId == HostEventIds.RouteOutcomeSummary);
        AssertSafe(staticLogger, sensitive);

        var serviceId = RoutingTestData.Id(919);
        var proxyLogger = new CapturingLogger();
        var proxyRoute = CreateRoute(
            RoutingTestData.Id(920),
            new MicroserviceRouteTargetConfiguration(serviceId));
        var proxyDispatcher = CreateDispatcher(
            CreateSnapshot(proxyRoute, CreateSettings()),
            new ResultTarget(RouteTargetExecutionResult.BadGateway),
            logger: proxyLogger);
        var proxyContext = CreateContext("/selected", []);
        proxyContext.Request.QueryString = new QueryString("?marker=" + sensitive);
        proxyContext.Request.Headers["X-Sensitive"] = sensitive;
        await proxyDispatcher.DispatchAsync(proxyContext);
        var proxyEvent = Assert.Single(proxyLogger.Entries, entry => entry.EventId == HostEventIds.ProxyFailure);
        Assert.Equal(serviceId, proxyEvent.Fields["ServiceId"]);
        Assert.Equal(RouteTargetType.Microservice, proxyEvent.Fields["TargetType"]);
        Assert.Equal(RouteTargetExecutionResult.BadGateway, proxyEvent.Fields["Outcome"]);
        Assert.Equal(StatusCodes.Status502BadGateway, proxyEvent.Fields["StatusCode"]);
        Assert.Equal(LogLevel.Warning, proxyEvent.Level);
        Assert.Single(proxyLogger.Entries, entry => entry.EventId == HostEventIds.RouteOutcomeSummary);
        AssertSafe(proxyLogger, sensitive);
    }

    [Fact]
    public async Task FallbackPreparationRejectionEmitsOneTypedGlobalEvent()
    {
        const string sensitive = "sensitive-request-fallback-marker";
        var logger = new CapturingLogger();
        var route = CreateRoute(RoutingTestData.Id(921), new StaticFileRouteTargetConfiguration(Path.GetTempPath()));
        var dispatcher = CreateDispatcher(
            CreateSnapshot(route, CreateSettings(maxBody: 3)),
            new CountingTarget(),
            logger: logger);

        var context = CreateContext("/no-match", Encoding.UTF8.GetBytes(sensitive));
        AddSensitiveRequestMarkers(context, sensitive);
        await dispatcher.DispatchAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        AssertResponseMessage(context, "Payload too large.", sensitive);
        var rejection = Assert.Single(
            logger.Entries,
            entry => entry.EventId == HostEventIds.AdmissionResourceRejected);
        Assert.Equal(HostRequestAdmissionFailureKind.RequestBody, rejection.Fields["FailureKind"]);
        Assert.Null(rejection.Fields["RouteId"]);
        Assert.Null(rejection.Fields["TargetType"]);
        AssertAdmissionEventShape(rejection, sensitive);
    }

    private static readonly string[] AdmissionFieldNames =
    [
        "FailureKind",
        "RetryAfterPresent",
        "RetryAfterSeconds",
        "RouteId",
        "StatusCode",
        "TargetType"
    ];

    private static void AssertAdmissionEventShape(CapturedLog entry, params string[] sensitiveValues)
    {
        var fields = entry.Fields.Keys
            .Where(key => key != "{OriginalFormat}")
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            AdmissionFieldNames.OrderBy(key => key, StringComparer.Ordinal),
            fields);

        Assert.Equal(HostEventIds.AdmissionResourceRejected, entry.EventId);
        foreach (var sensitive in sensitiveValues)
        {
            Assert.DoesNotContain(sensitive, entry.FormattedMessage, StringComparison.Ordinal);
            foreach (var value in entry.Fields.Values)
            {
                if (value is not null)
                {
                    Assert.DoesNotContain(sensitive, value.ToString(), StringComparison.Ordinal);
                }
            }
        }

        var connectionValue = IPAddress.Loopback.ToString();
        Assert.DoesNotContain(connectionValue, entry.FormattedMessage, StringComparison.Ordinal);
        foreach (var value in entry.Fields.Values)
        {
            if (value is not null)
            {
                Assert.DoesNotContain(connectionValue, value.ToString(), StringComparison.Ordinal);
            }
        }
    }

    private static void AssertResponseMessage(
        DefaultHttpContext context,
        string expected,
        string sensitive)
    {
        var body = Assert.IsType<MemoryStream>(context.Response.Body);
        var text = Encoding.UTF8.GetString(body.ToArray());
        Assert.Equal(expected, text);
        Assert.DoesNotContain(sensitive, text, StringComparison.Ordinal);
    }

    private static void AddSensitiveRequestMarkers(DefaultHttpContext context, string marker)
    {
        context.Request.QueryString = new QueryString("?marker=" + marker);
        context.Request.Host = new HostString(marker + ".example.test");
        context.Request.Headers["X-Sensitive"] = marker;
        context.Features.Get<IHttpRequestFeature>()!.RawTarget =
            (context.Request.Path.Value ?? "/") + "?marker=" + marker;
    }

    private static void AssertSafe(CapturingLogger logger, string sensitive)
    {
        foreach (var entry in logger.Entries)
        {
            Assert.DoesNotContain(sensitive, entry.FormattedMessage, StringComparison.Ordinal);
            foreach (var value in entry.Fields.Values)
            {
                if (value is not null && value.ToString() is { } rendered)
                {
                    Assert.DoesNotContain(sensitive, rendered, StringComparison.Ordinal);
                }
            }
        }
    }

    private static HostRouteDispatcher CreateDispatcher(
        HostRoutingSnapshot snapshot,
        IRouteTargetExecutor target,
        HostRequestAdmission? admission = null,
        CapturingLogger? logger = null) =>
        new(
            new FixedSnapshotAccessor(snapshot),
            NoOpRouteFallbackDispatcher.Instance,
            target,
            admission ?? new HostRequestAdmission(),
            logger ?? new CapturingLogger());

    private static HostRoutingSnapshot CreateSnapshot(
        RouteConfiguration route,
        GlobalSettingsConfiguration settings) =>
        new(
            new HostConfigurationSnapshot(
                1,
                settings,
                ImmutableArray.Create(route),
                ImmutableArray<ServiceConfiguration>.Empty,
                ImmutableArray<ExtensionRecordConfiguration>.Empty,
                ImmutableArray<ExtensionSettingsConfiguration>.Empty),
            RoutingTestData.Build(route));

    private static RouteMatch GetMatch(HostRoutingSnapshot snapshot)
    {
        var result = snapshot.Matcher.Match(new RouteMatchInput("/selected", "example.test", "GET"));
        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        return result.Match!;
    }

    private static RouteConfiguration CreateRoute(
        Guid id,
        RouteTargetConfiguration target,
        ClientIpRatePolicyConfiguration? policy = null,
        long? maxBody = null,
        long? maxHeader = null,
        int? maxConcurrent = null,
        TimeSpan? readTimeout = null) =>
        new(
            id,
            true,
            new RouteMatcherConfiguration(RouteMatcherType.Exact, "/selected", default, default),
            target,
            0,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            "{}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1,
            policy,
            maxBody,
            maxHeader,
            maxConcurrent,
            readTimeout);

    private static GlobalSettingsConfiguration CreateSettings(
        long maxBody = GlobalSettingsConfiguration.HardMaximumRequestBodyBytes,
        long maxHeader = GlobalSettingsConfiguration.HardMaximumRequestHeaderBytes,
        int maxConcurrent = 8,
        TimeSpan? readTimeout = null,
        ClientIpRatePolicyConfiguration? globalPolicy = null) =>
        new(
            version: 1,
            maxRequestBodyBytes: maxBody,
            maxRequestHeaderBytes: maxHeader,
            maxConcurrentRequests: maxConcurrent,
            requestReadTimeout: readTimeout,
            configurationPollInterval: TimeSpan.FromSeconds(1),
            clientIpRatePolicy: globalPolicy);

    private static ClientIpRatePolicyConfiguration CreatePolicy(
        long tokenLimit,
        RateLimitRetryAfterBehavior retryAfterBehavior = RateLimitRetryAfterBehavior.None) =>
        new(
            tokenLimit,
            tokensPerPeriod: 1,
            replenishmentPeriod: TimeSpan.FromHours(1),
            queueLimit: 0,
            rejectionBehavior: RateLimitRejectionBehavior.Reject,
            retryAfterBehavior: retryAfterBehavior);

    private static DefaultHttpContext CreateContext(string path, byte[] body)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Method = "GET";
        context.Request.Host = new HostString("example.test");
        context.Request.Path = path;
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static DefaultHttpContext CreateContext(string path, Stream body)
    {
        var context = CreateContext(path, []);
        context.Request.Body = body;
        context.Request.ContentLength = null;
        return context;
    }

    private sealed class FixedSnapshotAccessor(HostRoutingSnapshot snapshot) : IHostRoutingSnapshotAccessor
    {
        public HostRoutingSnapshot Current { get; } = snapshot;
    }

    private sealed class CountingTarget : IRouteTargetExecutor
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

    private sealed class ResultTarget(RouteTargetExecutionResult result, int? statusCode = null) : IRouteTargetExecutor
    {
        public async ValueTask<RouteTargetExecutionResult> ExecuteAsync(
            HttpContext context,
            HostRoutingSnapshot snapshot,
            RouteMatch match,
            CancellationToken cancellationToken)
        {
            if (statusCode is { } status)
            {
                context.Response.StatusCode = status;
            }

            await Task.CompletedTask;
            return result;
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        internal List<CapturedLog> Entries { get; } = [];

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

            Entries.Add(new CapturedLog(logLevel, eventId, formatter(state, exception), fields));
        }
    }

    private sealed record CapturedLog(
        LogLevel Level,
        EventId EventId,
        string FormattedMessage,
        IReadOnlyDictionary<string, object?> Fields);

    private sealed class ManualClock(DateTimeOffset initial) : IHostRequestAdmissionClock
    {
        private readonly object _gate = new();
        private readonly List<(DateTimeOffset Due, TaskCompletionSource Completion)> _delays = [];
        private DateTimeOffset _now = initial;
        private readonly TaskCompletionSource _scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset UtcNow { get { lock (_gate) return _now; } }
        internal Task DelayScheduled => _scheduled.Task;

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _delays.Add((_now + delay, completion));
            _scheduled.TrySetResult();
            return new ValueTask(completion.Task);
        }

        internal void Advance(TimeSpan amount)
        {
            List<TaskCompletionSource> due = [];
            lock (_gate)
            {
                _now += amount;
                for (var i = _delays.Count - 1; i >= 0; i--)
                {
                    if (_delays[i].Due <= _now)
                    {
                        due.Add(_delays[i].Completion);
                        _delays.RemoveAt(i);
                    }
                }
            }

            foreach (var completion in due) completion.TrySetResult();
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task ReadStarted => _started.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            return new ValueTask<int>(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith(static _ => 0));
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
