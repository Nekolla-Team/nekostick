namespace Nekolla.Nekostick.Contracts;

/// <summary>Defines immutable global proxy timeout settings.</summary>
public sealed record ProxyTimeoutConfiguration
{
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromDays(1);
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultHttpActivityTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultHttpTotalTimeout = TimeSpan.FromSeconds(100);
    private static readonly TimeSpan DefaultWebSocketIdleTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Gets the default global proxy timeout settings.</summary>
    public static ProxyTimeoutConfiguration Default { get; } = new(
        DefaultConnectTimeout,
        DefaultHttpActivityTimeout,
        DefaultHttpTotalTimeout,
        DefaultWebSocketIdleTimeout);

    /// <summary>Creates global proxy timeout settings.</summary>
    /// <param name="connectTimeout">The maximum time allowed to establish an upstream connection.</param>
    /// <param name="httpActivityTimeout">
    /// The maximum time before the first HTTP response byte and between subsequent response bytes.
    /// </param>
    /// <param name="httpTotalTimeout">The maximum total duration for a normal HTTP request.</param>
    /// <param name="webSocketIdleTimeout">The maximum idle duration for a WebSocket connection.</param>
    public ProxyTimeoutConfiguration(
        TimeSpan? connectTimeout = null,
        TimeSpan? httpActivityTimeout = null,
        TimeSpan? httpTotalTimeout = null,
        TimeSpan? webSocketIdleTimeout = null)
    {
        ConnectTimeout = Validate(
            connectTimeout ?? DefaultConnectTimeout,
            nameof(connectTimeout));
        HttpActivityTimeout = Validate(
            httpActivityTimeout ?? DefaultHttpActivityTimeout,
            nameof(httpActivityTimeout));
        HttpTotalTimeout = Validate(
            httpTotalTimeout ?? DefaultHttpTotalTimeout,
            nameof(httpTotalTimeout));
        WebSocketIdleTimeout = Validate(
            webSocketIdleTimeout ?? DefaultWebSocketIdleTimeout,
            nameof(webSocketIdleTimeout));
    }

    /// <summary>Gets the upstream connection timeout.</summary>
    public TimeSpan ConnectTimeout { get; }

    /// <summary>Gets the HTTP first-byte and activity timeout.</summary>
    public TimeSpan HttpActivityTimeout { get; }

    /// <summary>Gets the normal HTTP total timeout.</summary>
    public TimeSpan HttpTotalTimeout { get; }

    /// <summary>Gets the WebSocket idle timeout.</summary>
    public TimeSpan WebSocketIdleTimeout { get; }

    private static TimeSpan Validate(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero ||
            timeout > MaximumTimeout ||
            timeout.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Proxy timeouts must be positive, whole milliseconds, and no greater than one day.");
        }

        return timeout;
    }
}
