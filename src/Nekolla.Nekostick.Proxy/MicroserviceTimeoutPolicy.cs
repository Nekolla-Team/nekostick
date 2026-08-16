namespace Nekolla.Nekostick.Proxy;

/// <summary>Contains immutable timeout budgets for one microservice forwarding request.</summary>
public sealed class MicroserviceTimeoutPolicy
{
    private static readonly TimeSpan MaximumTimeout =
        TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>Creates a validated timeout policy.</summary>
    public MicroserviceTimeoutPolicy(
        TimeSpan connectTimeout,
        TimeSpan activityTimeout,
        TimeSpan httpTotalTimeout,
        TimeSpan websocketIdleTimeout)
    {
        ConnectTimeout = Validate(connectTimeout, nameof(connectTimeout));
        ActivityTimeout = Validate(activityTimeout, nameof(activityTimeout));
        HttpTotalTimeout = Validate(httpTotalTimeout, nameof(httpTotalTimeout));
        WebSocketIdleTimeout = Validate(websocketIdleTimeout, nameof(websocketIdleTimeout));
    }

    /// <summary>Gets the destination connection establishment timeout.</summary>
    public TimeSpan ConnectTimeout { get; }

    /// <summary>Gets the normal HTTP activity timeout.</summary>
    public TimeSpan ActivityTimeout { get; }

    /// <summary>Gets the normal HTTP total request timeout.</summary>
    public TimeSpan HttpTotalTimeout { get; }

    /// <summary>Gets the independent WebSocket idle activity timeout.</summary>
    public TimeSpan WebSocketIdleTimeout { get; }

    private static TimeSpan Validate(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}
