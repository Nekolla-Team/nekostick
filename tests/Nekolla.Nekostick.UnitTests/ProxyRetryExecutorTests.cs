using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Proxy;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ProxyRetryExecutorTests
{
    private static readonly Guid ServiceId =
        Guid.Parse("01900000-0000-0000-0000-000000000701");

    [Fact]
    public async Task BodylessConnectionFailureIsRetriedWithFreshForwarderAttempt()
    {
        var forwarder = new SequenceForwarder(
            (ForwarderError.Request, new HttpRequestException()),
            (ForwarderError.None, null));
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool);
        var context = CreateContext();
        var request = CreateRequest(new ProxyRetryConfiguration(
            maxRetries: 1,
            initialBackoff: TimeSpan.FromMilliseconds(1),
            maximumBackoff: TimeSpan.FromMilliseconds(1)));

        var result = await executor.ExecuteAsync(
            context,
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.Handled, result.Disposition);
        Assert.Equal(2, forwarder.CallCount);
    }

    [Fact]
    public async Task BodyBearingRequestIsNeverReplayed()
    {
        var forwarder = new SequenceForwarder(
            (ForwarderError.Request, new HttpRequestException()),
            (ForwarderError.None, null));
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool);
        var context = CreateContext();
        context.Request.ContentLength = 3;
        context.Request.Body = new MemoryStream(new byte[] { 1, 2, 3 });

        var result = await executor.ExecuteAsync(
            context,
            CreateRequest(new ProxyRetryConfiguration(maxRetries: 1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.BadGateway, result.Disposition);
        Assert.Equal(1, forwarder.CallCount);
    }

    [Fact]
    public async Task ChunkedRequestIsNeverReplayed()
    {
        var forwarder = new SequenceForwarder(
            (ForwarderError.Request, new IOException()),
            (ForwarderError.None, null));
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool);
        var context = CreateContext();
        context.Request.Headers["Transfer-Encoding"] = "chunked";

        var result = await executor.ExecuteAsync(
            context,
            CreateRequest(new ProxyRetryConfiguration(maxRetries: 1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.BadGateway, result.Disposition);
        Assert.Equal(1, forwarder.CallCount);
    }

    [Fact]
    public async Task WebSocketRequestIsNeverReplayed()
    {
        var forwarder = new SequenceForwarder(
            (ForwarderError.Request, new HttpRequestException()),
            (ForwarderError.None, null));
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool);
        var context = CreateContext();
        context.Request.Headers["Connection"] = "Upgrade";
        context.Request.Headers["Upgrade"] = "websocket";

        var result = await executor.ExecuteAsync(
            context,
            CreateRequest(new ProxyRetryConfiguration(maxRetries: 1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.BadGateway, result.Disposition);
        Assert.Equal(1, forwarder.CallCount);
    }

    [Fact]
    public async Task ZeroRetryPolicyMakesOneAttempt()
    {
        var forwarder = new SequenceForwarder(
            (ForwarderError.Request, new HttpRequestException()),
            (ForwarderError.None, null));
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool);

        var result = await executor.ExecuteAsync(
            CreateContext(),
            CreateRequest(ProxyRetryConfiguration.Default),
            TestContext.Current.CancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.BadGateway, result.Disposition);
        Assert.Equal(1, forwarder.CallCount);
    }

    [Fact]
    public async Task ExhaustedRetriesMapToBadGateway()
    {
        var forwarder = new SequenceForwarder(
            (ForwarderError.Request, new HttpRequestException()));
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool);

        var result = await executor.ExecuteAsync(
            CreateContext(),
            CreateRequest(new ProxyRetryConfiguration(
                maxRetries: 2,
                initialBackoff: TimeSpan.FromMilliseconds(1),
                maximumBackoff: TimeSpan.FromMilliseconds(1))),
            TestContext.Current.CancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.BadGateway, result.Disposition);
        Assert.Equal(3, forwarder.CallCount);
    }

    [Fact]
    public async Task ExternalCancellationDoesNotRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var forwarder = new CancelingForwarder(cancellation);
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool);

        var result = await executor.ExecuteAsync(
            CreateContext(),
            CreateRequest(new ProxyRetryConfiguration(maxRetries: 2)),
            cancellation.Token);

        Assert.Equal(MicroserviceProxyExecutionDisposition.Cancelled, result.Disposition);
        Assert.Equal(1, forwarder.CallCount);
    }

    [Fact]
    public async Task AttemptTelemetryContainsOnlySafeFields()
    {
        const string sensitive = "sensitive-request-marker";
        var routeId = Guid.Parse("01900000-0000-0000-0000-000000000702");
        var logger = new CapturingLogger();
        var forwarder = new SequenceForwarder(
            (ForwarderError.Request, new HttpRequestException()));
        using var pool = new MicroserviceHttpInvokerPool();
        var executor = new MicroserviceHttpExecutor(
            forwarder,
            new FixedEndpointResolver(),
            pool,
            logger);
        var context = CreateContext();
        context.Request.Path = "/" + sensitive;
        context.Request.Headers["X-Sensitive"] = sensitive;

        var result = await executor.ExecuteAsync(
            context,
            CreateRequest(ProxyRetryConfiguration.Default, routeId),
            TestContext.Current.CancellationToken);

        Assert.Equal(MicroserviceProxyExecutionDisposition.BadGateway, result.Disposition);
        var entry = Assert.Single(logger.Entries, value => value.EventId.Id == 2001);
        var detail = Assert.Single(logger.Entries, value => value.EventId.Id == 2002);
        Assert.Equal(routeId, entry.Fields["RouteId"]);
        Assert.Equal(ServiceId, entry.Fields["ServiceId"]);
        Assert.Equal(1, entry.Fields["Attempt"]);
        Assert.DoesNotContain(sensitive, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            sensitive,
            string.Join("|", entry.Fields.Values.Select(value => value?.ToString())),
            StringComparison.Ordinal);
        Assert.NotNull(detail.Message);
        Assert.DoesNotContain(sensitive, detail.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            sensitive,
            string.Join("|", detail.Fields.Values.Select(value => value?.ToString())),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RetryBackoffIsJitteredAndCapped()
    {
        var policy = new ProxyRetryConfiguration(
            maxRetries: 4,
            initialBackoff: TimeSpan.FromMilliseconds(200),
            maximumBackoff: TimeSpan.FromSeconds(2));

        Assert.Equal(
            TimeSpan.FromMilliseconds(200),
            MicroserviceHttpExecutor.CalculateRetryDelay(policy, 1, 0));
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            MicroserviceHttpExecutor.CalculateRetryDelay(policy, 4, 1));
    }

    private static MicroserviceProxyRequest CreateRequest(
        ProxyRetryConfiguration retryPolicy,
        Guid? routeId = null) =>
        new(
            ServiceId,
            "/",
            new MicroserviceTimeoutPolicy(
                connectTimeout: TimeSpan.FromSeconds(2),
                activityTimeout: TimeSpan.FromSeconds(5),
                httpTotalTimeout: TimeSpan.FromSeconds(17),
                websocketIdleTimeout: TimeSpan.FromSeconds(23)),
            retryPolicy: retryPolicy,
            routeId: routeId);

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.ContentLength = 0;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class FixedEndpointResolver : IMicroserviceEndpointResolver
    {
        public ValueTask<MicroserviceEndpointResolution> ResolveAsync(
            Guid serviceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                MicroserviceEndpointResolution.Available(
                    new MicroserviceEndpoint("http://127.0.0.1")));
    }

    private sealed class SequenceForwarder : IHttpForwarder
    {
        private readonly (ForwarderError Error, Exception? Exception)[] _results;
        private int _next;

        internal SequenceForwarder(
            params (ForwarderError Error, Exception? Exception)[] results) =>
            _results = results;

        internal int CallCount { get; private set; }

        public ValueTask<ForwarderError> SendAsync(
            HttpContext httpContext,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer)
        {
            CallCount++;
            var result = _results[Math.Min(_next++, _results.Length - 1)];
            httpContext.Features.Set<IForwarderErrorFeature>(
                new ErrorFeature(result.Error, result.Exception));
            return ValueTask.FromResult(result.Error);
        }
    }

    private sealed class CancelingForwarder : IHttpForwarder
    {
        private readonly CancellationTokenSource _cancellation;

        internal CancelingForwarder(CancellationTokenSource cancellation) =>
            _cancellation = cancellation;

        internal int CallCount { get; private set; }

        public ValueTask<ForwarderError> SendAsync(
            HttpContext httpContext,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer)
        {
            CallCount++;
            _cancellation.Cancel();
            throw new OperationCanceledException();
        }
    }

    private sealed class ErrorFeature : IForwarderErrorFeature
    {
        internal ErrorFeature(ForwarderError error, Exception? exception)
        {
            Error = error;
            Exception = exception;
        }

        public ForwarderError Error { get; }

        public Exception? Exception { get; }
    }

    private sealed class CapturingLogger : ILogger<MicroserviceHttpExecutor>
    {
        internal List<CapturedEntry> Entries { get; } = [];

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

            Entries.Add(new CapturedEntry(
                logLevel,
                eventId,
                formatter(state, exception),
                fields));
        }
    }

    private sealed record CapturedEntry(
        LogLevel Level,
        EventId EventId,
        string Message,
        IReadOnlyDictionary<string, object?> Fields);
}
