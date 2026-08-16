using System.Collections.Immutable;
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

    private static RouteConfiguration CreateRoute(Guid id, string pattern, string target) =>
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
            1);

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

    private static async Task<int> DispatchAsync(
        HostRoutingSnapshot snapshot,
        DefaultHttpContext context,
        CapturingLogger logger)
    {
        var dispatcher = new HostRouteDispatcher(
            new FixedSnapshotAccessor(snapshot),
            new DecliningFallbackDispatcher(),
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

    private sealed class DecliningFallbackDispatcher : IRouteFallbackDispatcher
    {
        public ValueTask<bool> TryDispatchAsync(HttpContext context, RouteNoMatchReason reason) =>
            ValueTask.FromResult(false);
    }

    private sealed class CapturingLogger : ILogger
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
        EventId EventId,
        string FormattedMessage,
        IReadOnlyDictionary<string, object?> Fields);
}
