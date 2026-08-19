using System.Collections.Immutable;
using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Routing;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostRequestAdmissionTests
{
    [Fact]
    public async Task InheritedRoutePolicyDoesNotChargeTheGlobalBucketTwice()
    {
        var policy = CreatePolicy(tokenLimit: 1, replenishmentPeriod: TimeSpan.FromHours(1));
        var snapshot = CreateSnapshot(CreateSettings(policy), routePolicy: null);
        var admission = new HostRequestAdmission();
        var context = CreateContext("127.0.0.1");
        var match = GetMatch(snapshot);

        var global = await admission.TryAcquireGlobalAsync(snapshot, context);
        Assert.NotNull(global.Lease);
        global.Lease!.Dispose();

        var firstRoute = await admission.TryAcquireRouteAsync(snapshot, match, context);
        var secondRoute = await admission.TryAcquireRouteAsync(snapshot, match, context);
        var secondGlobal = await admission.TryAcquireGlobalAsync(snapshot, context);

        Assert.Null(firstRoute.Rejection);
        Assert.Null(secondRoute.Rejection);
        Assert.Equal(HostRequestAdmissionFailureKind.RateLimit, secondGlobal.Rejection?.Kind);
    }

    [Fact]
    public async Task GlobalRejectionDoesNotConsumeTheMatchedRouteToken()
    {
        var global = CreatePolicy(tokenLimit: 1, replenishmentPeriod: TimeSpan.FromHours(1));
        var route = CreatePolicy(tokenLimit: 2, replenishmentPeriod: TimeSpan.FromHours(1));
        var snapshot = CreateSnapshot(
            CreateSettings(global, ImmutableArray.Create("127.0.0.0/8")),
            route);
        var target = new RecordingTargetExecutor();
        var dispatcher = new HostRouteDispatcher(
            new FixedSnapshotAccessor(snapshot),
            NoOpRouteFallbackDispatcher.Instance,
            target,
            new HostRequestAdmission(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var first = CreateContext("127.0.0.1", "203.0.113.10");
        var rejected = CreateContext("127.0.0.1", "203.0.113.10");
        var third = CreateContext("127.0.0.2", "203.0.113.10");

        await dispatcher.DispatchAsync(first);
        await dispatcher.DispatchAsync(rejected);
        await dispatcher.DispatchAsync(third);

        Assert.Equal(2, target.CallCount);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, third.Response.StatusCode);
    }

    [Fact]
    public async Task RoutePolicyUsesTrustedForwardedIdentityOnlyAfterMatching()
    {
        var policy = CreatePolicy(tokenLimit: 1, replenishmentPeriod: TimeSpan.FromHours(1));
        var snapshot = CreateSnapshot(
            CreateSettings(null, ImmutableArray.Create("127.0.0.0/8")),
            policy);
        var admission = new HostRequestAdmission();
        var match = GetMatch(snapshot);

        var trustedA = await admission.TryAcquireRouteAsync(
            snapshot,
            match,
            CreateContext("127.0.0.1", "203.0.113.1"));
        var trustedB = await admission.TryAcquireRouteAsync(
            snapshot,
            match,
            CreateContext("127.0.0.1", "203.0.113.2"));
        var untrustedA = await admission.TryAcquireRouteAsync(
            snapshot,
            match,
            CreateContext("198.51.100.2", "203.0.113.3"));
        var untrustedB = await admission.TryAcquireRouteAsync(
            snapshot,
            match,
            CreateContext("198.51.100.2", "203.0.113.4"));

        Assert.Null(trustedA.Rejection);
        Assert.Null(trustedB.Rejection);
        Assert.Null(untrustedA.Rejection);
        Assert.Equal(HostRequestAdmissionFailureKind.RateLimit, untrustedB.Rejection?.Kind);
    }

    [Fact]
    public async Task QueuedTokenRequestWaitsThenConsumesTheNextPeriod()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var registry = new HostRequestRateBucketRegistry(clock);
        var policy = new ClientIpRatePolicyConfiguration(
            tokenLimit: 1,
            tokensPerPeriod: 1,
            replenishmentPeriod: TimeSpan.FromSeconds(2),
            queueLimit: 1,
            rejectionBehavior: RateLimitRejectionBehavior.Queue,
            retryAfterBehavior: RateLimitRetryAfterBehavior.FromReplenishmentPeriod);

        var first = await registry.AcquireAsync("global", "203.0.113.1", policy, TestContext.Current.CancellationToken);
        var queued = registry.AcquireAsync("global", "203.0.113.1", policy, TestContext.Current.CancellationToken).AsTask();
        await Task.Yield();
        Assert.False(queued.IsCompletedSuccessfully);
        var rejected = await registry.AcquireAsync("global", "203.0.113.1", policy, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(2));
        var admitted = await queued;

        Assert.True(first.Acquired);
        Assert.False(rejected.Acquired);
        Assert.False(rejected.Cancelled);
        Assert.True(admitted.Acquired);
    }

    [Fact]
    public async Task RateRejectionWritesConfiguredRetryAfterSeconds()
    {
        var policy = CreatePolicy(
            tokenLimit: 1,
            replenishmentPeriod: TimeSpan.FromSeconds(2),
            retryAfterBehavior: RateLimitRetryAfterBehavior.FromReplenishmentPeriod);
        var snapshot = CreateSnapshot(CreateSettings(policy), routePolicy: null);
        var dispatcher = new HostRouteDispatcher(
            new FixedSnapshotAccessor(snapshot),
            NoOpRouteFallbackDispatcher.Instance,
            new RecordingTargetExecutor(),
            new HostRequestAdmission(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var first = CreateContext("203.0.113.20", "198.51.100.1");
        var rejected = CreateContext("203.0.113.20", "198.51.100.2");
        await dispatcher.DispatchAsync(first);
        await dispatcher.DispatchAsync(rejected);

        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Response.StatusCode);
        Assert.Equal("2", rejected.Response.Headers["Retry-After"].ToString());
    }

    [Fact]
    public async Task OldSnapshotKeepsItsBucketStateWhileItsPublicationLeaseExists()
    {
        var policy = CreatePolicy(tokenLimit: 1, replenishmentPeriod: TimeSpan.FromHours(1));
        var holder = new HostConfigurationSnapshotHolder();
        var first = CreateConfiguration(version: 1, CreateSettings(policy));
        var replacement = CreateConfiguration(version: 2, CreateSettings(null));
        Assert.True(holder.TryReplace(first));
        await using var oldLease = holder.TryAcquireRoutingLease()
            ?? throw new InvalidOperationException("The first publication must accept a lease.");

        var admission = new HostRequestAdmission();
        var context = CreateContext("203.0.113.30");
        var admitted = await admission.TryAcquireGlobalAsync(oldLease.Snapshot, context);
        admitted.Lease!.Dispose();
        Assert.True(holder.TryReplace(replacement));
        var rejected = await admission.TryAcquireGlobalAsync(oldLease.Snapshot, context);

        Assert.Equal(HostRequestAdmissionFailureKind.RateLimit, rejected.Rejection?.Kind);
    }

    [Fact]
    public async Task ConcurrencyPermitIsReleasedAfterTargetException()
    {
        var snapshot = CreateSnapshot(CreateSettings(null, maxConcurrentRequests: 1), routePolicy: null);
        var target = new ThrowFirstTargetExecutor();
        var dispatcher = new HostRouteDispatcher(
            new FixedSnapshotAccessor(snapshot),
            NoOpRouteFallbackDispatcher.Instance,
            target,
            new HostRequestAdmission(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var first = CreateContext("203.0.113.40");
        var second = CreateContext("203.0.113.41");
        await dispatcher.DispatchAsync(first);
        await dispatcher.DispatchAsync(second);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, first.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, second.Response.StatusCode);
        Assert.Equal(2, target.CallCount);
    }

    [Fact]
    public async Task ConcurrencyPermitIsReleasedAfterRequestCancellation()
    {
        var snapshot = CreateSnapshot(CreateSettings(null, maxConcurrentRequests: 1), routePolicy: null);
        using var cancellation = new CancellationTokenSource();
        var target = new CancelFirstTargetExecutor();
        var dispatcher = new HostRouteDispatcher(
            new FixedSnapshotAccessor(snapshot),
            NoOpRouteFallbackDispatcher.Instance,
            target,
            new HostRequestAdmission(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var first = CreateContext("203.0.113.50");
        first.RequestAborted = cancellation.Token;
        var pending = dispatcher.DispatchAsync(first);
        await target.Started;
        cancellation.Cancel();
        await pending;

        var second = CreateContext("203.0.113.51");
        await dispatcher.DispatchAsync(second);

        Assert.Equal(StatusCodes.Status200OK, second.Response.StatusCode);
        Assert.Equal(2, target.CallCount);
    }

    [Fact]
    public async Task RateRejectionAfterResponseStartAbortsWithoutChangingResponse()
    {
        var policy = CreatePolicy(tokenLimit: 1, replenishmentPeriod: TimeSpan.FromHours(1));
        var snapshot = CreateSnapshot(CreateSettings(policy), routePolicy: null);
        var dispatcher = new HostRouteDispatcher(
            new FixedSnapshotAccessor(snapshot),
            NoOpRouteFallbackDispatcher.Instance,
            new RecordingTargetExecutor(),
            new HostRequestAdmission(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        await dispatcher.DispatchAsync(CreateContext("203.0.113.60"));
        var started = new DefaultHttpContext();
        var response = new StartedResponseFeature { StatusCode = StatusCodes.Status202Accepted };
        var lifetime = new RecordingLifetimeFeature();
        started.Features.Set<IHttpResponseFeature>(response);
        started.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        ConfigureContext(started, "203.0.113.60");

        await dispatcher.DispatchAsync(started);

        Assert.True(lifetime.Aborted);
        Assert.Equal(StatusCodes.Status202Accepted, response.StatusCode);
    }

    private static ClientIpRatePolicyConfiguration CreatePolicy(
        long tokenLimit,
        TimeSpan replenishmentPeriod,
        RateLimitRetryAfterBehavior retryAfterBehavior = RateLimitRetryAfterBehavior.None) =>
        new(
            tokenLimit: tokenLimit,
            tokensPerPeriod: 1,
            replenishmentPeriod: replenishmentPeriod,
            queueLimit: 0,
            rejectionBehavior: RateLimitRejectionBehavior.Reject,
            retryAfterBehavior: retryAfterBehavior);

    private static GlobalSettingsConfiguration CreateSettings(
        ClientIpRatePolicyConfiguration? globalPolicy,
        ImmutableArray<string> trustedProxyCidrs = default,
        int maxConcurrentRequests = 8) =>
        new(
            version: 1,
            maxConcurrentRequests: maxConcurrentRequests,
            configurationPollInterval: TimeSpan.FromSeconds(1),
            trustedProxyCidrs: trustedProxyCidrs,
            clientIpRatePolicy: globalPolicy);

    private static HostRoutingSnapshot CreateSnapshot(
        GlobalSettingsConfiguration settings,
        ClientIpRatePolicyConfiguration? routePolicy)
    {
        var configuration = CreateConfiguration(1, settings, routePolicy);
        var matcher = RouteMatchSnapshotBuilder.Build(configuration.Routes).Snapshot;
        Assert.NotNull(matcher);
        return new HostRoutingSnapshot(configuration, matcher!);
    }

    private static HostConfigurationSnapshot CreateConfiguration(
        long version,
        GlobalSettingsConfiguration settings,
        ClientIpRatePolicyConfiguration? routePolicy = null)
    {
        var route = new RouteConfiguration(
            id: Guid.CreateVersion7(),
            enabled: true,
            matcher: new RouteMatcherConfiguration(
                RouteMatcherType.Exact,
                "/limited",
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            target: new StaticFileRouteTargetConfiguration(Path.GetTempPath()),
            priority: 0,
            forwarding: new ForwardingConfiguration(ForwardingMode.Preserve, null),
            requestHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
            responseHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
            metadataJson: "{}",
            createdAt: DateTimeOffset.UnixEpoch,
            updatedAt: DateTimeOffset.UnixEpoch,
            version: 1,
            clientIpRatePolicy: routePolicy);
        return new HostConfigurationSnapshot(
            version,
            settings,
            ImmutableArray.Create(route),
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);
    }

    private static RouteMatch GetMatch(HostRoutingSnapshot snapshot)
    {
        var result = snapshot.Matcher.Match(new RouteMatchInput("/limited", "example.test", "GET"));
        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        return result.Match!;
    }
    private static DefaultHttpContext CreateContext(string remoteAddress, string? forwardedFor = null)
    {
        var context = new DefaultHttpContext();
        ConfigureContext(context, remoteAddress, forwardedFor);
        return context;
    }

    private static void ConfigureContext(
        DefaultHttpContext context,
        string remoteAddress,
        string? forwardedFor = null)
    {
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        context.Request.Method = "GET";
        context.Request.Host = new HostString("example.test");
        context.Request.Path = "/limited";
        context.Request.Body = new MemoryStream();
        context.Response.Body = new MemoryStream();
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }
    }

    private sealed class FixedSnapshotAccessor : IHostRoutingSnapshotAccessor
    {
        internal FixedSnapshotAccessor(HostRoutingSnapshot snapshot) => Current = snapshot;

        public HostRoutingSnapshot Current { get; }
    }

    private sealed class RecordingTargetExecutor : IRouteTargetExecutor
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

    private sealed class ManualClock : IHostRequestAdmissionClock
    {
        private readonly object _gate = new();
        private readonly List<ScheduledDelay> _delays = [];
        private DateTimeOffset _now;

        internal ManualClock(DateTimeOffset now) => _now = now;

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

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled(cancellationToken);
            }

            lock (_gate)
            {
                var completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _delays.Add(new ScheduledDelay(_now + delay, completion));
                return new ValueTask(completion.Task);
            }
        }

        internal void Advance(TimeSpan elapsed)
        {
            List<TaskCompletionSource> due = [];
            lock (_gate)
            {
                _now += elapsed;
                for (var index = _delays.Count - 1; index >= 0; index--)
                {
                    if (_delays[index].Due > _now)
                    {
                        continue;
                    }

                    due.Add(_delays[index].Completion);
                    _delays.RemoveAt(index);
                }
            }

            foreach (var completion in due)
            {
                completion.TrySetResult();
            }
        }

        private sealed record ScheduledDelay(DateTimeOffset Due, TaskCompletionSource Completion);
    }
}
