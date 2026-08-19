using System.Net;
using System.Net.Http;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ServiceHealthProbeTests
{
    private static readonly Guid ServiceId = Guid.Parse("018f0000-0000-7000-8000-000000000021");

    [Theory]
    [InlineData(true, HealthObservationStatus.Healthy)]
    [InlineData(false, HealthObservationStatus.Unavailable)]
    public async Task ProcessHealthUsesOnlyProcessLiveness(bool isRunning, HealthObservationStatus expected)
    {
        using var probe = new ServiceHealthProbe(new RecordingLiveness(isRunning));
        var request = new ServiceHealthProbeRequest(
            ServiceId,
            new HealthCheckDefinition(ServiceHealthCheckKind.Process, TimeSpan.FromSeconds(1)));

        var result = await probe.ProbeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void TcpHealthRequiresAValidatedLoopbackEndpoint()
    {
        var definition = new HealthCheckDefinition(ServiceHealthCheckKind.Tcp, TimeSpan.FromSeconds(1));

        var exception = Assert.Throws<ArgumentException>(
            () => new ServiceHealthProbeRequest(ServiceId, definition));

        Assert.Equal("endpoint", exception.ParamName);
    }

    [Fact]
    public void HttpHealthRequiresAPathAndValidatedLoopbackEndpoint()
    {
        Assert.Throws<ArgumentException>(
            () => new HealthCheckDefinition(ServiceHealthCheckKind.Http, TimeSpan.FromSeconds(1)));

        var definition = new HealthCheckDefinition(
            ServiceHealthCheckKind.Http,
            TimeSpan.FromSeconds(1),
            "/health");

        var exception = Assert.Throws<ArgumentException>(
            () => new ServiceHealthProbeRequest(ServiceId, definition));

        Assert.Equal("endpoint", exception.ParamName);
    }

    [Fact]
    public async Task HttpHealthUsesBoundedGetPathAndMapsSuccessRange()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        using var client = new HttpClient(handler);
        using var probe = new ServiceHealthProbe(new RecordingLiveness(true), client);
        var request = new ServiceHealthProbeRequest(
            ServiceId,
            new HealthCheckDefinition(ServiceHealthCheckKind.Http, TimeSpan.FromSeconds(1), "/health"),
            new LoopbackEndpoint(LoopbackAddressKind.IPv4, 18432));

        var result = await probe.ProbeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HealthObservationStatus.Healthy, result.Status);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("http://127.0.0.1:18432/health", handler.RequestUri?.ToString());
        Assert.Equal(HttpVersion.Version11, handler.Version);
    }

    [Fact]
    public async Task HttpHealthRejectsUnsafePathBeforeSendingRequest()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        using var probe = new ServiceHealthProbe(new RecordingLiveness(true), client);
        var request = new ServiceHealthProbeRequest(
            ServiceId,
            new HealthCheckDefinition(ServiceHealthCheckKind.Http, TimeSpan.FromSeconds(1), "../secret"),
            new LoopbackEndpoint(LoopbackAddressKind.IPv4, 18432));

        var result = await probe.ProbeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HealthObservationStatus.Unavailable, result.Status);
        Assert.Equal(0, handler.CallCount);
    }

    private sealed class RecordingLiveness : IProcessLiveness, IProcessExecutor
    {
        private readonly bool isRunning;

        public RecordingLiveness(bool isRunning) => this.isRunning = isRunning;

        public bool IsRunning(Guid serviceId) => isRunning;

        public ValueTask<ProcessOperationResult> StartAsync(
            ProcessLaunchSpecification specification,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Rejected,
                ServiceStateReasonCode.StartRejected));

        public ValueTask<ProcessOperationResult> StopAsync(
            Guid serviceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Completed,
                ServiceStateReasonCode.StopCompleted));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode statusCode;

        public RecordingHandler(HttpStatusCode statusCode) => this.statusCode = statusCode;

        public int CallCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public Version? Version { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Version = request.Version;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }
}
