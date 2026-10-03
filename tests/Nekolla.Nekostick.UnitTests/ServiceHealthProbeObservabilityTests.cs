using System.Net;
using System.Net.Http;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ServiceHealthProbeObservabilityTests
{
    private static readonly Guid ServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000073");
    private const string ExpectedTarget = "http://127.0.0.1:18432";

    [Fact]
    public async Task HttpUnhealthyResponseIncludesTargetAndFailureDetail()
    {
        using var client = new HttpClient(new RecordingHandler(HttpStatusCode.ServiceUnavailable));
        using var probe = new ServiceHealthProbe(new RecordingLiveness(), client);

        var result = await probe.ProbeAsync(CreateRequest(TimeSpan.FromSeconds(1)), TestContext.Current.CancellationToken);

        Assert.Equal(HealthObservationStatus.Unhealthy, result.Status);
        Assert.Equal(ExpectedTarget, result.Target);
        Assert.Equal("The HTTP health check returned a non-success response.", result.ErrorMessage);
    }

    [Fact]
    public async Task HttpExceptionIncludesTargetAndExceptionMessage()
    {
        using var client = new HttpClient(new FailingHandler(new HttpRequestException("connection refused")));
        using var probe = new ServiceHealthProbe(new RecordingLiveness(), client);

        var result = await probe.ProbeAsync(CreateRequest(TimeSpan.FromSeconds(1)), TestContext.Current.CancellationToken);

        Assert.Equal(HealthObservationStatus.Unavailable, result.Status);
        Assert.Equal(ExpectedTarget, result.Target);
        Assert.Equal("connection refused", result.ErrorMessage);
    }

    [Fact]
    public async Task UnexpectedHttpFailureKeepsGenericSafeDetail()
    {
        using var client = new HttpClient(new FailingHandler(new InvalidOperationException("internal fault details")));
        using var probe = new ServiceHealthProbe(new RecordingLiveness(), client);

        var result = await probe.ProbeAsync(CreateRequest(TimeSpan.FromSeconds(1)), TestContext.Current.CancellationToken);

        Assert.Equal(HealthObservationStatus.Unavailable, result.Status);
        Assert.Equal(ExpectedTarget, result.Target);
        Assert.Equal("The health check target is unavailable.", result.ErrorMessage);
    }

    [Fact]
    public async Task HttpTransportErrorMessageIsBoundedBeforePublication()
    {
        var exceptionMessage = new string('x', 2048);
        using var client = new HttpClient(new FailingHandler(new HttpRequestException(exceptionMessage)));
        using var probe = new ServiceHealthProbe(new RecordingLiveness(), client);

        var result = await probe.ProbeAsync(CreateRequest(TimeSpan.FromSeconds(1)), TestContext.Current.CancellationToken);

        Assert.Equal(HealthObservationStatus.Unavailable, result.Status);
        Assert.Equal(ExpectedTarget, result.Target);
        Assert.Equal(exceptionMessage[..512], result.ErrorMessage);
    }

    [Fact]
    public async Task ProbeTimeoutIncludesTargetAndTimeoutDetail()
    {
        using var client = new HttpClient(new BlockingHandler());
        using var probe = new ServiceHealthProbe(new RecordingLiveness(), client);

        var result = await probe.ProbeAsync(CreateRequest(TimeSpan.FromMilliseconds(50)), TestContext.Current.CancellationToken);

        Assert.Equal(HealthObservationStatus.TimedOut, result.Status);
        Assert.Equal(ExpectedTarget, result.Target);
        Assert.Equal("The health check timed out.", result.ErrorMessage);
    }

    [Fact]
    public async Task CallerCancellationIncludesTargetAndCancellationDetail()
    {
        using var client = new HttpClient(new BlockingHandler());
        using var probe = new ServiceHealthProbe(new RecordingLiveness(), client);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        var result = await probe.ProbeAsync(CreateRequest(TimeSpan.FromSeconds(1)), cancellation.Token);

        Assert.Equal(HealthObservationStatus.Cancelled, result.Status);
        Assert.Equal(ExpectedTarget, result.Target);
        Assert.Equal("The health check was cancelled.", result.ErrorMessage);
    }

    private static ServiceHealthProbeRequest CreateRequest(TimeSpan timeout) =>
        new(
            ServiceId,
            new HealthCheckDefinition(ServiceHealthCheckKind.Http, timeout, "/health"),
            new LoopbackEndpoint(LoopbackAddressKind.IPv4, 18432));

    private sealed class RecordingLiveness : IProcessLiveness
    {
        public bool IsRunning(Guid serviceId) => true;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public RecordingHandler(HttpStatusCode statusCode) => _statusCode = statusCode;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_statusCode));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public FailingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(_exception);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
