using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Routing;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostRouteDispatcherDiagnosticsTests
{
    [Fact]
    public async Task TimeoutLogDeduplicatesStableRouteIdsAndContainsOnlySafeStructuredValues()
    {
        var firstId = RoutingTestData.Id(340);
        var secondId = RoutingTestData.Id(341);
        const string requestPath = "/request-path-marker";
        const string rawTarget = "/request-path-marker?raw-target-marker";
        const string host = "sensitive-host-marker.example";
        const string method = "SENSITIVE-METHOD";
        const string regexPattern = "(?:/request-path-marker|/regex-pattern-marker)(?:/)?";
        const string target = "/sensitive-target-marker";
        const string header = "sensitive-header-marker";
        const string cookie = "sensitive-cookie-marker";
        const string authorization = "sensitive-authorization-marker";
        const string connection = "sensitive-connection-marker";

        var routes = new[]
        {
            CreateRoute(firstId, regexPattern, target),
            CreateRoute(secondId, regexPattern, target)
        };
        var evaluator = new DeterministicRegexEvaluator(
            ImmutableDictionary<Guid, RouteRegexEvaluationOutcome>.Empty
                .Add(firstId, RouteRegexEvaluationOutcome.TimedOut)
                .Add(secondId, RouteRegexEvaluationOutcome.TimedOut));
        var build = RouteMatchSnapshotBuilder.Build(routes, evaluator);
        var matcher = build.Snapshot ?? throw new InvalidOperationException("The test route set must compile.");
        var directResult = matcher.Match(new RouteMatchInput(requestPath, host, method));

        Assert.Equal(
            new[] { firstId, secondId, firstId, secondId },
            directResult.RegexTimeoutRouteIds);

        var configuration = RoutingTestData.CreateSnapshot(
            1,
            ImmutableArray.CreateRange(routes));
        var snapshot = new HostRoutingSnapshot(configuration, matcher);
        var logger = new CapturingLogger();
        var context = CreateContext(
            requestPath,
            rawTarget,
            host,
            method,
            header,
            cookie,
            authorization,
            connection);

        await DispatchAsync(snapshot, context, logger);

        var timeoutLog = Assert.Single(
            logger.Entries,
            entry => entry.EventId == HostEventIds.RouteRegexEvaluationTimedOut);
        var routeIds = Assert.IsType<Guid[]>(timeoutLog.Fields["RouteIds"]);

        Assert.Equal(new[] { firstId, secondId }, routeIds);
        Assert.Equal(2, Assert.IsType<int>(timeoutLog.Fields["Count"]));

        var recorded = BuildRecordedText(timeoutLog);
        foreach (var sensitiveValue in new[]
        {
            requestPath,
            rawTarget,
            host,
            method,
            regexPattern,
            target,
            header,
            cookie,
            authorization,
            connection
        })
        {
            Assert.DoesNotContain(sensitiveValue, recorded, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NormalDispatchDoesNotCreateRegexTimeoutEvent()
    {
        var route = RoutingTestData.CreateRoute(
            RoutingTestData.Id(342),
            RouteMatcherType.Exact,
            "/normal");
        var matcherBuild = RouteMatchSnapshotBuilder.Build(
            new[] { route },
            new DeterministicRegexEvaluator(
                ImmutableDictionary<Guid, RouteRegexEvaluationOutcome>.Empty));
        var matcher = matcherBuild.Snapshot ?? throw new InvalidOperationException("The test route set must compile.");
        var snapshot = new HostRoutingSnapshot(
            RoutingTestData.CreateSnapshot(1, ImmutableArray.Create(route)),
            matcher);
        var logger = new CapturingLogger();
        var context = CreateContext(
            "/normal",
            "/normal",
            "example.test",
            "GET",
            "header",
            "cookie",
            "authorization",
            "connection");

        var statusCode = await DispatchAsync(snapshot, context, logger);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusCode);
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.EventId == HostEventIds.RouteRegexEvaluationTimedOut);
    }

    [Fact]
    public async Task GenericServiceUnavailableWarningIsCorrelatedAndSanitized()
    {
        var routeId = RoutingTestData.Id(343);
        const string requestPath = "/request-path-marker";
        const string rawTarget = "/request-path-marker?query-secret-marker";
        const string host = "sensitive-host-marker.example";
        const string method = "SENSITIVE-METHOD";
        const string targetRoot = "/sensitive-target-marker";
        const string extensionId = "safe-extension-id-marker";
        const string traceIdentifier = "request-trace-identifier-marker";
        const string header = "sensitive-header-marker";
        const string cookie = "sensitive-cookie-marker";
        const string authorization = "sensitive-authorization-marker";
        const string connection = "sensitive-connection-marker";

        var route = CreateRoute(routeId, requestPath, targetRoot, extensionId);
        var configuration = RoutingTestData.CreateSnapshot(17, ImmutableArray.Create(route));
        var snapshot = new HostRoutingSnapshot(configuration, RoutingTestData.Build(new[] { route }));
        var logger = new CapturingLogger();
        var context = CreateContext(
            requestPath,
            rawTarget,
            host,
            method,
            header,
            cookie,
            authorization,
            connection);
        context.TraceIdentifier = traceIdentifier;

        using var activity = new Activity("generic-503-diagnostic")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();

        var statusCode = await DispatchAsync(snapshot, context, logger);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusCode);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(
            context.Response.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        Assert.Equal("Service unavailable.", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));

        var unavailableLog = Assert.Single(
            logger.Entries,
            entry => entry.EventId == HostEventIds.GenericServiceUnavailable);
        Assert.Equal(LogLevel.Warning, unavailableLog.Level);
        Assert.Equal(
            HostGenericUnavailableReason.TargetDeferred,
            Assert.IsType<HostGenericUnavailableReason>(unavailableLog.Fields["Reason"]));
        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            Assert.IsType<int>(unavailableLog.Fields["StatusCode"]));
        Assert.Equal(traceIdentifier, unavailableLog.Fields["TraceIdentifier"]);
        Assert.Equal(activity.TraceId.ToString(), unavailableLog.Fields["ActivityTraceId"]);
        Assert.Equal(17L, Assert.IsType<long>(unavailableLog.Fields["ConfigurationVersion"]));
        Assert.Null(unavailableLog.Fields["PublicationGenerationId"]);
        Assert.Equal(routeId, unavailableLog.Fields["RouteId"]);
        Assert.Equal(RouteTargetType.StaticFile, Assert.IsType<RouteTargetType>(unavailableLog.Fields["TargetType"]));
        Assert.Equal(extensionId, unavailableLog.Fields["OwnerExtensionId"]);
        Assert.Null(unavailableLog.Fields["FailureDetail"]);

        var recorded = BuildRecordedText(unavailableLog);
        foreach (var sensitiveValue in new[]
        {
            requestPath,
            rawTarget,
            host,
            method,
            targetRoot,
            header,
            cookie,
            authorization,
            connection
        })
        {
            Assert.DoesNotContain(sensitiveValue, recorded, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GenericServiceUnavailableCarriesExtensionFailureDetailWhenPresent()
    {
        var routeId = RoutingTestData.Id(344);
        const string extensionId = "safe-extension-id-marker";
        const string failureDetail = "entrypoint replaced\nwhile the request was parked";
        var route = CreateRoute(routeId, "/failure-detail", "/failure-detail-target", extensionId);
        var configuration = RoutingTestData.CreateSnapshot(18, ImmutableArray.Create(route));
        var snapshot = new HostRoutingSnapshot(configuration, RoutingTestData.Build(new[] { route }));
        var logger = new CapturingLogger();
        var context = CreateContext(
            "/failure-detail",
            "/failure-detail",
            "example.test",
            "GET",
            "header",
            "cookie",
            "authorization",
            "connection");

        var statusCode = await DispatchAsync(
            new FixedSnapshotAccessor(snapshot),
            context,
            logger,
            new UnavailableWithFailureDetailExecutor(failureDetail));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusCode);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(
            context.Response.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        Assert.Equal("Service unavailable.", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));

        var unavailableLog = Assert.Single(
            logger.Entries,
            entry => entry.EventId == HostEventIds.GenericServiceUnavailable);
        Assert.Equal(
            HostGenericUnavailableReason.TargetUnavailable,
            Assert.IsType<HostGenericUnavailableReason>(unavailableLog.Fields["Reason"]));
        Assert.Equal(routeId, unavailableLog.Fields["RouteId"]);
        Assert.Equal("'" + failureDetail.Replace("\n", "\\n", StringComparison.Ordinal) + "'", unavailableLog.Fields["FailureDetail"]);
    }

    [Fact]
    public async Task SnapshotLeaseUnavailableLogsLastPublishedVersionWhenAvailable()
    {
        var route = RoutingTestData.CreateRoute(
            RoutingTestData.Id(345),
            RouteMatcherType.Exact,
            "/lease-unavailable");
        var configuration = RoutingTestData.CreateSnapshot(19, ImmutableArray.Create(route));
        var snapshot = new HostRoutingSnapshot(configuration, RoutingTestData.Build(new[] { route }));
        var logger = new CapturingLogger();
        var context = CreateContext(
            "/lease-unavailable",
            "/lease-unavailable",
            "example.test",
            "GET",
            "header",
            "cookie",
            "authorization",
            "connection");

        var statusCode = await DispatchAsync(
            new UnleaseableSnapshotAccessor(snapshot),
            context,
            logger);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusCode);
        var unavailableLog = Assert.Single(
            logger.Entries,
            entry => entry.EventId == HostEventIds.GenericServiceUnavailable);
        Assert.Equal(
            HostGenericUnavailableReason.SnapshotLeaseUnavailable,
            Assert.IsType<HostGenericUnavailableReason>(unavailableLog.Fields["Reason"]));
        Assert.Equal(19L, Assert.IsType<long>(unavailableLog.Fields["ConfigurationVersion"]));
        Assert.Null(unavailableLog.Fields["PublicationGenerationId"]);
        Assert.Null(unavailableLog.Fields["RouteId"]);
        Assert.Null(unavailableLog.Fields["OwnerExtensionId"]);
        Assert.Null(unavailableLog.Fields["FailureDetail"]);
    }

    [Fact]
    public void RuntimeStateTransitionLogsOnlyChangesWithCallerAndCurrentVersion()
    {
        var holder = new HostConfigurationSnapshotHolder();
        var snapshot = new HostConfigurationSnapshot(
            1,
            new GlobalSettingsConfiguration(version: 1),
            default,
            default,
            default,
            default);
        Assert.True(holder.TryReplace(snapshot));
        var logger = new CapturingLogger();
        var state = new HostRuntimeState(
            holder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false),
            logger);

        state.MarkDatabaseUnavailable();
        state.MarkDatabaseUnavailable();
        state.MarkSnapshotAccepted();
        state.MarkSnapshotAccepted();

        var transitions = logger.Entries
            .Where(entry => entry.EventId == HostEventIds.RuntimeStateTransition)
            .ToArray();
        Assert.Equal(2, transitions.Length);

        var unavailable = Assert.Single(
            transitions,
            entry => entry.Fields["Reason"] is HostRuntimeStateTransitionReason.DatabaseUnavailable);
        Assert.Equal(LogLevel.Warning, unavailable.Level);
        Assert.Equal(nameof(RuntimeStateTransitionLogsOnlyChangesWithCallerAndCurrentVersion), unavailable.Fields["Caller"]);
        Assert.Equal(1L, Assert.IsType<long>(unavailable.Fields["ConfigurationVersion"]));

        var accepted = Assert.Single(
            transitions,
            entry => entry.Fields["Reason"] is HostRuntimeStateTransitionReason.SnapshotAccepted);
        Assert.Equal(LogLevel.Information, accepted.Level);
        Assert.Equal(nameof(RuntimeStateTransitionLogsOnlyChangesWithCallerAndCurrentVersion), accepted.Fields["Caller"]);
        Assert.Equal(1L, Assert.IsType<long>(accepted.Fields["ConfigurationVersion"]));
    }

    private static RouteConfiguration CreateRoute(
        Guid id,
        string pattern,
        string target,
        string? ownerExtensionId = null) =>
        new(
            id,
            true,
            new RouteMatcherConfiguration(RouteMatcherType.Regex, pattern, default, default),
            new StaticFileRouteTargetConfiguration(target),
            0,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            "{}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1,
            ownerExtensionId: ownerExtensionId);

    private static DefaultHttpContext CreateContext(
        string path,
        string rawTarget,
        string host,
        string method,
        string header,
        string cookie,
        string authorization,
        string connection)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Host = new HostString(host);
        context.Request.Headers["X-Sensitive-Header"] = header;
        context.Request.Headers["Cookie"] = cookie;
        context.Request.Headers["Authorization"] = authorization;
        context.TraceIdentifier = connection;
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = rawTarget;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static Task<int> DispatchAsync(
        HostRoutingSnapshot snapshot,
        DefaultHttpContext context,
        CapturingLogger logger) =>
        DispatchAsync(new FixedSnapshotAccessor(snapshot), context, logger);

    private static Task<int> DispatchAsync(
        IHostRoutingSnapshotAccessor snapshotAccessor,
        DefaultHttpContext context,
        CapturingLogger logger) =>
        DispatchAsync(snapshotAccessor, context, logger, NoOpRouteTargetExecutor.Instance);

    private static async Task<int> DispatchAsync(
        IHostRoutingSnapshotAccessor snapshotAccessor,
        DefaultHttpContext context,
        CapturingLogger logger,
        IRouteTargetExecutor targetExecutor)
    {
        var dispatcher = new HostRouteDispatcher(
            snapshotAccessor,
            new DecliningFallbackDispatcher(),
            targetExecutor,
            logger);

        await dispatcher.DispatchAsync(context);
        return context.Response.StatusCode;
    }

    private static string BuildRecordedText(CapturedLog entry) =>
        entry.FormattedMessage + "\n" + string.Join(
            "\n",
            entry.Fields.Select(pair => $"{pair.Key}={pair.Value}"));

    private sealed class FixedSnapshotAccessor : IHostRoutingSnapshotAccessor
    {
        internal FixedSnapshotAccessor(HostRoutingSnapshot current) => Current = current;

        public HostRoutingSnapshot Current { get; }
    }

    private sealed class UnleaseableSnapshotAccessor : IHostRoutingSnapshotAccessor, IHostRoutingSnapshotLeaseAccessor
    {
        internal UnleaseableSnapshotAccessor(HostRoutingSnapshot current) => Current = current;

        public HostRoutingSnapshot Current { get; }

        public HostRoutingSnapshotLease? TryAcquireLease() => null;
    }

    private sealed class UnavailableWithFailureDetailExecutor : IRouteTargetExecutor
    {
        private readonly string _failureDetail;

        internal UnavailableWithFailureDetailExecutor(string failureDetail) => _failureDetail = failureDetail;

        public ValueTask<RouteTargetExecutionResult> ExecuteAsync(
            HttpContext context,
            HostRoutingSnapshot snapshot,
            RouteMatch match,
            CancellationToken cancellationToken)
        {
            // Mirrors HostRouteTargetExecutor, which records the sanitized extension failure
            // detail for the dispatcher's generic 503 diagnostics.
            HostRouteTargetFailureDetail.Set(context, _failureDetail);
            return ValueTask.FromResult(RouteTargetExecutionResult.Unavailable);
        }
    }

    private sealed class DecliningFallbackDispatcher : IRouteFallbackDispatcher
    {
        public ValueTask<bool> TryDispatchAsync(HttpContext context, RouteNoMatchReason reason) =>
            ValueTask.FromResult(false);
    }

    private sealed class CapturingLogger : ILogger<HostRuntimeState>
    {
        private readonly List<CapturedLog> _entries = new();

        internal IReadOnlyList<CapturedLog> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;

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
                foreach (var pair in values)
                {
                    fields[pair.Key] = pair.Value;
                }
            }

            _entries.Add(new CapturedLog(
                logLevel,
                eventId,
                formatter(state, exception),
                fields));
        }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed record CapturedLog(
        LogLevel Level,
        EventId EventId,
        string FormattedMessage,
        IReadOnlyDictionary<string, object?> Fields);
}
