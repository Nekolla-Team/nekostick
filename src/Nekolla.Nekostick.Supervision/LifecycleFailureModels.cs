using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Supervision;

/// <summary>Contains one bounded, non-sensitive health observation.</summary>
public sealed record HealthObservationResult
{
    /// <summary>Creates a health observation result.</summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="status">The fixed observation outcome.</param>
    /// <param name="observedAt">The UTC observation instant.</param>
    /// <param name="duration">The elapsed probe duration.</param>
    /// <param name="attempt">The one-based attempt number.</param>
    public HealthObservationResult(
        Guid serviceId,
        HealthObservationStatus status,
        DateTimeOffset observedAt,
        TimeSpan duration,
        int attempt)
    {
        if (serviceId == Guid.Empty)
        {
            throw new ArgumentException("A service identifier is required.", nameof(serviceId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);

        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        ServiceId = serviceId;
        Status = status;
        ObservedAt = observedAt.ToUniversalTime();
        Duration = duration;
        Attempt = attempt;
    }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the fixed outcome without diagnostic text.</summary>
    public HealthObservationStatus Status { get; }

    /// <summary>Gets the UTC observation instant.</summary>
    public DateTimeOffset ObservedAt { get; }

    /// <summary>Gets the elapsed observation duration.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Gets the one-based observation attempt.</summary>
    public int Attempt { get; }

    /// <summary>Gets the corresponding domain health state.</summary>
    public ServiceHealthState HealthState => Status switch
    {
        HealthObservationStatus.Healthy => ServiceHealthState.Healthy,
        HealthObservationStatus.Unhealthy or HealthObservationStatus.TimedOut => ServiceHealthState.Unhealthy,
        _ => ServiceHealthState.Unknown
    };
}

/// <summary>Identifies a health observation outcome without exposing probe details.</summary>
public enum HealthObservationStatus
{
    /// <summary>The health check succeeded.</summary>
    Healthy,

    /// <summary>The health check completed and reported failure.</summary>
    Unhealthy,

    /// <summary>The health check exceeded its permitted duration.</summary>
    TimedOut,

    /// <summary>The health check was cancelled.</summary>
    Cancelled,

    /// <summary>The health check could not be performed.</summary>
    Unavailable
}
