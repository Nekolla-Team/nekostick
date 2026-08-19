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

public sealed class HostRequestPolicyRejectionEventTests
{
    [Fact]
    public async Task MatchedStaticBodyRejectionCarriesRouteIdentityAndTargetType()
    {
        const string sensitive = "sensitive-request-body-marker";
        var routeId = RoutingTestData.Id(940);
        var route = CreateRoute(routeId, maxBodyBytes: 3);
        var logger = new CapturingLogger();
        var dispatcher = CreateDispatcher(CreateSnapshot(route), NoOpRouteTargetExecutor.Instance, logger);
        var context = CreateContext(Encoding.UTF8.GetBytes(sensitive));
        AddSensitiveRequestMarkers(context, sensitive);

        await dispatcher.DispatchAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        AssertResponseMessage(context, "Payload too large.", sensitive);
        var entry = Assert.Single(logger.Entries, value => value.EventId == HostEventIds.AdmissionResourceRejected);
        Assert.Equal(HostRequestAdmissionFailureKind.RequestBody, entry.Fields["FailureKind"]);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, entry.Fields["StatusCode"]);
        Assert.Equal(routeId, entry.Fields["RouteId"]);
        Assert.Equal(RouteTargetType.StaticFile, entry.Fields["TargetType"]);
        AssertAdmissionEventShape(entry, sensitive);
        Assert.Single(logger.Entries);
    }

    [Fact]
    public async Task RouteConcurrencyRejectionCarriesRouteIdentityAndReleasesAfterTarget()
    {
        const string sensitive = "sensitive-request-concurrency-marker";
        var routeId = RoutingTestData.Id(941);
        var route = CreateRoute(routeId, maxConcurrentRequests: 1);
        var logger = new CapturingLogger();
        var target = new BlockingTargetExecutor();
        var snapshot = CreateSnapshot(route, maxGlobalConcurrentRequests: 4);
        var dispatcher = CreateDispatcher(snapshot, target, logger);

        var firstContext = CreateContext(Encoding.UTF8.GetBytes(sensitive));
        AddSensitiveRequestMarkers(firstContext, sensitive);
        var first = dispatcher.DispatchAsync(firstContext);
        await target.Started.WaitAsync(TestContext.Current.CancellationToken);
        var rejectedContext = CreateContext([]);
        AddSensitiveRequestMarkers(rejectedContext, sensitive);
        await dispatcher.DispatchAsync(rejectedContext);

        Assert.Equal(StatusCodes.Status429TooManyRequests, rejectedContext.Response.StatusCode);
        AssertResponseMessage(rejectedContext, "Too many concurrent requests.", sensitive);
        var entry = Assert.Single(logger.Entries, value => value.EventId == HostEventIds.AdmissionResourceRejected);
        Assert.Equal(HostRequestAdmissionFailureKind.Concurrency, entry.Fields["FailureKind"]);
        Assert.Equal(StatusCodes.Status429TooManyRequests, entry.Fields["StatusCode"]);
        Assert.Equal(routeId, entry.Fields["RouteId"]);
        Assert.Equal(RouteTargetType.StaticFile, entry.Fields["TargetType"]);
        Assert.False((bool)entry.Fields["RetryAfterPresent"]!);
        AssertAdmissionEventShape(entry, sensitive);

        target.Release();
        await first.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Single(logger.Entries, value => value.EventId == HostEventIds.AdmissionResourceRejected);
    }
    [Fact]
    public async Task SimultaneousGlobalAdmissionRejectsContenderAndReleasesLease()
    {
        const string sensitive = "sensitive-global-concurrency-marker";
        var route = CreateRoute(RoutingTestData.Id(942));
        var logger = new CapturingLogger();
        var target = new BlockingTargetExecutor();
        var dispatcher = CreateDispatcher(
            CreateSnapshot(route, maxGlobalConcurrentRequests: 1),
            target,
            logger);

        var firstContext = CreateContext(Encoding.UTF8.GetBytes(sensitive));
        AddSensitiveRequestMarkers(firstContext, sensitive);
        var first = dispatcher.DispatchAsync(firstContext);
        await target.Started.WaitAsync(TestContext.Current.CancellationToken);

        var rejectedContext = CreateContext([]);
        AddSensitiveRequestMarkers(rejectedContext, sensitive);
        await dispatcher.DispatchAsync(rejectedContext);

        Assert.Equal(StatusCodes.Status429TooManyRequests, rejectedContext.Response.StatusCode);
        AssertResponseMessage(rejectedContext, "Too many concurrent requests.", sensitive);
        var rejection = Assert.Single(logger.Entries, value => value.EventId == HostEventIds.AdmissionResourceRejected);
        Assert.Equal(HostRequestAdmissionFailureKind.Concurrency, rejection.Fields["FailureKind"]);
        Assert.Null(rejection.Fields["RouteId"]);
        Assert.Null(rejection.Fields["TargetType"]);
        AssertAdmissionEventShape(rejection, sensitive);
        Assert.False(first.IsCompleted);

        target.Release();
        await first.WaitAsync(TestContext.Current.CancellationToken);

        var releasedContext = CreateContext([]);
        AddSensitiveRequestMarkers(releasedContext, sensitive);
        await dispatcher.DispatchAsync(releasedContext);
        Assert.Equal(StatusCodes.Status200OK, releasedContext.Response.StatusCode);
        Assert.Equal(2, target.CallCount);
        Assert.Single(logger.Entries, value => value.EventId == HostEventIds.AdmissionResourceRejected);
    }

    [Fact]
    public async Task GlobalRateQueueAdmitsOneWaiterAndRejectsTheNextWithoutTimingSleeps()
    {
        const string sensitive = "sensitive-global-rate-queue-marker";
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var policy = CreatePolicy(
            tokenLimit: 1,
            tokensPerPeriod: 1,
            replenishmentPeriod: TimeSpan.FromSeconds(5),
            queueLimit: 1,
            rejectionBehavior: RateLimitRejectionBehavior.Queue,
            retryAfterBehavior: RateLimitRetryAfterBehavior.FromReplenishmentPeriod);
        var route = CreateRoute(RoutingTestData.Id(943));
        var logger = new CapturingLogger();
        var target = new CountingTargetExecutor();
        var dispatcher = CreateDispatcher(
            CreateSnapshot(route, maxGlobalConcurrentRequests: 4, globalPolicy: policy),
            target,
            logger,
            admission: new HostRequestAdmission(clock));

        var firstContext = CreateContext([]);
        AddSensitiveRequestMarkers(firstContext, sensitive);
        await dispatcher.DispatchAsync(firstContext);

        var queuedContext = CreateContext([]);
        AddSensitiveRequestMarkers(queuedContext, sensitive);
        var queued = dispatcher.DispatchAsync(queuedContext);
        await clock.DelayScheduled.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(queued.IsCompleted);

        var rejectedContext = CreateContext([]);
        AddSensitiveRequestMarkers(rejectedContext, sensitive);
        await dispatcher.DispatchAsync(rejectedContext);

        Assert.Equal(StatusCodes.Status429TooManyRequests, rejectedContext.Response.StatusCode);
        AssertResponseMessage(rejectedContext, "Too many requests.", sensitive);
        var rejection = Assert.Single(logger.Entries, value => value.EventId == HostEventIds.AdmissionResourceRejected);
        Assert.Equal(HostRequestAdmissionFailureKind.RateLimit, rejection.Fields["FailureKind"]);
        Assert.Equal(5, rejection.Fields["RetryAfterSeconds"] is int seconds ? seconds : 0);
        AssertAdmissionEventShape(rejection, sensitive);

        clock.Advance(TimeSpan.FromSeconds(5));
        await queued.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StatusCodes.Status200OK, queuedContext.Response.StatusCode);
        Assert.Equal(2, target.CallCount);
        Assert.Single(logger.Entries, value => value.EventId == HostEventIds.AdmissionResourceRejected);
    }


    private static HostRouteDispatcher CreateDispatcher(
        HostRoutingSnapshot snapshot,
        IRouteTargetExecutor target,
        CapturingLogger logger,
        HostRequestAdmission? admission = null) =>
        new(
            new FixedSnapshotAccessor(snapshot),
            NoOpRouteFallbackDispatcher.Instance,
            target,
            admission ?? new HostRequestAdmission(),
            logger);

    private static HostRoutingSnapshot CreateSnapshot(
        RouteConfiguration route,
        int maxGlobalConcurrentRequests = 8,
        ClientIpRatePolicyConfiguration? globalPolicy = null) =>
        new(
            new HostConfigurationSnapshot(
                1,
                new GlobalSettingsConfiguration(
                    version: 1,
                    maxConcurrentRequests: maxGlobalConcurrentRequests,
                    configurationPollInterval: TimeSpan.FromSeconds(1),
                    clientIpRatePolicy: globalPolicy),
                ImmutableArray.Create(route),
                ImmutableArray<ServiceConfiguration>.Empty,
                ImmutableArray<ExtensionRecordConfiguration>.Empty,
                ImmutableArray<ExtensionSettingsConfiguration>.Empty),
            RoutingTestData.Build(route));

    private static RouteConfiguration CreateRoute(
        Guid routeId,
        ClientIpRatePolicyConfiguration? policy = null,
        long? maxBodyBytes = null,
        int? maxConcurrentRequests = null) =>
        new(
            routeId,
            true,
            new RouteMatcherConfiguration(RouteMatcherType.Exact, "/selected", default, default),
            new StaticFileRouteTargetConfiguration(Path.GetTempPath()),
            0,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            "{}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1,
            clientIpRatePolicy: policy,
            maxRequestBodyBytes: maxBodyBytes,
            maxConcurrentRequests: maxConcurrentRequests);
    private static ClientIpRatePolicyConfiguration CreatePolicy(
        long tokenLimit,
        long tokensPerPeriod,
        TimeSpan replenishmentPeriod,
        int queueLimit,
        RateLimitRejectionBehavior rejectionBehavior,
        RateLimitRetryAfterBehavior retryAfterBehavior) =>
        new(
            tokenLimit,
            tokensPerPeriod,
            replenishmentPeriod,
            queueLimit,
            rejectionBehavior,
            retryAfterBehavior);

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


    private static DefaultHttpContext CreateContext(byte[] body)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Method = "GET";
        context.Request.Host = new HostString("example.test");
        context.Request.Path = "/selected";
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class FixedSnapshotAccessor(HostRoutingSnapshot snapshot) : IHostRoutingSnapshotAccessor
    {
        public HostRoutingSnapshot Current { get; } = snapshot;
    }

    private sealed class BlockingTargetExecutor : IRouteTargetExecutor
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        internal Task Started => _started.Task;
        internal int CallCount => Volatile.Read(ref _callCount);

        public async ValueTask<RouteTargetExecutionResult> ExecuteAsync(
            HttpContext context,
            HostRoutingSnapshot snapshot,
            RouteMatch match,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return RouteTargetExecutionResult.Handled;
        }

        internal void Release() => _release.TrySetResult();
    }

    private sealed class CountingTargetExecutor : IRouteTargetExecutor
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<RouteTargetExecutionResult> ExecuteAsync(
            HttpContext context,
            HostRoutingSnapshot snapshot,
            RouteMatch match,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(RouteTargetExecutionResult.Handled);
        }
    }

    private sealed class ManualClock(DateTimeOffset initial) : IHostRequestAdmissionClock
    {
        private readonly object _gate = new();
        private readonly List<(DateTimeOffset Due, TaskCompletionSource Completion)> _delays = [];
        private readonly TaskCompletionSource _scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DateTimeOffset _now = initial;

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

        internal Task DelayScheduled => _scheduled.Task;

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled(cancellationToken);
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _delays.Add((_now + delay, completion));
            }

            _scheduled.TrySetResult();
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
}
