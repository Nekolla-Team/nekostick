using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Supervision;

/// <summary>Performs bounded Process, loopback TCP, and HTTP/1.1 health observations.</summary>
public sealed class ServiceHealthProbe : IServiceHealthProbe, IDisposable
{
    private readonly IProcessLiveness? processLiveness;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;

    /// <summary>Creates a probe with no process liveness source.</summary>
    public ServiceHealthProbe()
        : this(null)
    {
    }

    /// <summary>Creates a probe that can perform process checks through the supplied executor.</summary>
    /// <param name="processExecutor">The process executor used only for safe liveness checks.</param>
    public ServiceHealthProbe(IProcessExecutor? processExecutor)
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
    }

    internal ServiceHealthProbe(IProcessLiveness processLiveness, HttpClient httpClient)
    {
        this.processLiveness = processLiveness ?? throw new ArgumentNullException(nameof(processLiveness));
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ownsHttpClient = false;
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
            status = HealthObservationStatus.Cancelled;
        }
        catch (OperationCanceledException)
        {
            status = HealthObservationStatus.TimedOut;
        }
        catch
        {
            status = HealthObservationStatus.Unavailable;
        }

        stopwatch.Stop();
        return new HealthObservationResult(
            request.ServiceId,
            status,
            startedAt,
            stopwatch.Elapsed,
            1);
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
    private static bool IsSafeHttpPath(string? value, out string path)
    {
        path = value ?? string.Empty;
        return path.StartsWith('/') &&
            !path.Any(char.IsControl) &&
            path.Length <= 4096;
    }
}
