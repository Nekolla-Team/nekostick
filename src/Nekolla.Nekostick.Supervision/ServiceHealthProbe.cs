using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Supervision;

/// <summary>Performs bounded Process, loopback TCP, and HTTP/1.1 health observations.</summary>
public sealed class ServiceHealthProbe : IServiceHealthProbe, IDisposable
{
    private readonly IProcessLiveness? processLiveness;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly ILogger _logger;

    /// <summary>Creates a probe with no process liveness source.</summary>
    public ServiceHealthProbe()
        : this(null)
    {
    }

    /// <summary>Creates a probe that can perform process checks through the supplied executor.</summary>
    /// <param name="processExecutor">The process executor used only for safe liveness checks.</param>
    /// <param name="logger">The optional supervision logger.</param>
    public ServiceHealthProbe(IProcessExecutor? processExecutor, ILogger? logger = null)
    {
        processLiveness = processExecutor as IProcessLiveness;
        httpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        ownsHttpClient = true;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Creates a probe for controlled process liveness and HTTP dependencies.</summary>
    /// <param name="processLiveness">The process liveness source.</param>
    /// <param name="httpClient">The HTTP client used for probes.</param>
    /// <param name="logger">The optional supervision logger.</param>
    internal ServiceHealthProbe(IProcessLiveness processLiveness, HttpClient httpClient, ILogger? logger = null)
    {
        this.processLiveness = processLiveness ?? throw new ArgumentNullException(nameof(processLiveness));
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ownsHttpClient = false;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<HealthObservationResult> ProbeAsync(
        ServiceHealthProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Definition.Timeout);

        HealthObservationStatus status;
        string? errorMessage = null;
        try
        {
            status = request.Definition.Kind switch
            {
                ServiceHealthCheckKind.Process => ProbeProcess(request.ServiceId),
                ServiceHealthCheckKind.Tcp => await ProbeTcpAsync(request, timeout.Token).ConfigureAwait(false),
                ServiceHealthCheckKind.Http => await ProbeHttpAsync(request, timeout.Token).ConfigureAwait(false),
                _ => HealthObservationStatus.Unavailable
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SupervisionLogMessages.HealthProbeCancelled(_logger, request.ServiceId);
            status = HealthObservationStatus.Cancelled;
        }
        catch (OperationCanceledException)
        {
            SupervisionLogMessages.HealthProbeTimedOut(_logger, request.ServiceId);
            status = HealthObservationStatus.TimedOut;
        }
        catch (Exception exception)
        {
            SupervisionLogMessages.HealthProbeOperationFailed(_logger, exception, request.ServiceId);
            status = HealthObservationStatus.Unavailable;
            if (IsTransportFailure(request.Definition.Kind, exception))
            {
                errorMessage = BoundErrorMessage(exception.Message);
            }
        }

        stopwatch.Stop();
        return new HealthObservationResult(
            request.ServiceId,
            status,
            startedAt,
            stopwatch.Elapsed,
            1,
            DescribeTarget(request),
            errorMessage ?? DescribeError(status, request.Definition.Kind));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private HealthObservationStatus ProbeProcess(Guid serviceId) =>
        processLiveness is not null && processLiveness.IsRunning(serviceId)
            ? HealthObservationStatus.Healthy
            : HealthObservationStatus.Unavailable;

    private static async ValueTask<HealthObservationStatus> ProbeTcpAsync(
        ServiceHealthProbeRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Endpoint.HasValue || !TryGetAddress(request.Endpoint.Value.Address, out var address))
        {
            return HealthObservationStatus.Unavailable;
        }

        using var client = new TcpClient(address.AddressFamily);
        await client.ConnectAsync(address, request.Endpoint.Value.Port, cancellationToken).ConfigureAwait(false);
        return client.Connected ? HealthObservationStatus.Healthy : HealthObservationStatus.Unhealthy;
    }

    private async ValueTask<HealthObservationStatus> ProbeHttpAsync(
        ServiceHealthProbeRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Endpoint.HasValue || !TryGetAddress(request.Endpoint.Value.Address, out var address) ||
            !IsSafeHttpPath(request.Definition.HttpPath, out var path))
        {
            return HealthObservationStatus.Unavailable;
        }

        var builder = new UriBuilder(Uri.UriSchemeHttp, address.ToString(), request.Endpoint.Value.Port, path);
        using var message = new HttpRequestMessage(HttpMethod.Get, builder.Uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var code = (int)response.StatusCode;
        return code is >= 200 and < 400
            ? HealthObservationStatus.Healthy
            : HealthObservationStatus.Unhealthy;
    }

    private static bool TryGetAddress(LoopbackAddressKind kind, out IPAddress address)
    {
        switch (kind)
        {
            case LoopbackAddressKind.IPv4:
                address = IPAddress.Loopback;
                return true;
            case LoopbackAddressKind.IPv6:
                address = IPAddress.IPv6Loopback;
                return true;
            default:
                address = IPAddress.None;
                return false;
        }
    }

    internal static string? DescribeTarget(ServiceHealthProbeRequest request)
    {
        if (request.Definition.Kind == ServiceHealthCheckKind.Process)
        {
            return "process";
        }

        if (!request.Endpoint.HasValue || !TryGetAddress(request.Endpoint.Value.Address, out var address))
        {
            return null;
        }

        var scheme = request.Definition.Kind switch
        {
            ServiceHealthCheckKind.Tcp => "tcp",
            ServiceHealthCheckKind.Http => Uri.UriSchemeHttp,
            _ => null
        };
        return scheme is null
            ? null
            : new UriBuilder(scheme, address.ToString(), request.Endpoint.Value.Port)
                .Uri.GetLeftPart(UriPartial.Authority);
    }

    internal static string BoundErrorMessage(string message) =>
        message.Length <= 512 ? message : message[..512];

    private static bool IsTransportFailure(ServiceHealthCheckKind kind, Exception exception) =>
        (kind == ServiceHealthCheckKind.Tcp || kind == ServiceHealthCheckKind.Http) &&
        (exception is HttpRequestException or SocketException or IOException);

    internal static string? DescribeError(HealthObservationStatus status, ServiceHealthCheckKind kind) => status switch
    {
        HealthObservationStatus.Healthy => null,
        HealthObservationStatus.Unhealthy when kind == ServiceHealthCheckKind.Http =>
            "The HTTP health check returned a non-success response.",
        HealthObservationStatus.Unhealthy => "The health check target reported an unhealthy result.",
        HealthObservationStatus.TimedOut => "The health check timed out.",
        HealthObservationStatus.Cancelled => "The health check was cancelled.",
        HealthObservationStatus.Unavailable when kind == ServiceHealthCheckKind.Process =>
            "The service process is not running.",
        HealthObservationStatus.Unavailable => "The health check target is unavailable.",
        _ => "The health check result is unknown."
    };
    private static bool IsSafeHttpPath(string? value, out string path)
    {
        path = value ?? string.Empty;
        return path.StartsWith('/') &&
            !path.Any(char.IsControl) &&
            path.Length <= 4096;
    }
}
