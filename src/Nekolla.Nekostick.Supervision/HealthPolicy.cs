using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Supervision;

/// <summary>Identifies the action selected by a health retry policy.</summary>
public enum HealthRetryAction
{
    /// <summary>The observation is accepted as healthy.</summary>
    Healthy,

    /// <summary>Another observation may be attempted.</summary>
    Retry,

    /// <summary>The failure threshold was reached.</summary>
    Failed,

    /// <summary>The health deadline was reached.</summary>
    TimedOut,

    /// <summary>Cancellation was requested.</summary>
    Cancelled
}

/// <summary>Contains immutable health retry state.</summary>
public readonly record struct HealthRetryState
{
    /// <summary>Creates health retry state.</summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="attempt">The number of the next observation attempt.</param>
    /// <param name="consecutiveFailures">The failure count in the current sequence.</param>
    /// <param name="deadline">The UTC retry deadline.</param>
    public HealthRetryState(Guid serviceId, int attempt, int consecutiveFailures, DateTimeOffset deadline)
    {
        if (serviceId == Guid.Empty)
        {
            throw new ArgumentException("A service identifier is required.", nameof(serviceId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        ArgumentOutOfRangeException.ThrowIfNegative(consecutiveFailures);

        ServiceId = serviceId;
        Attempt = attempt;
        ConsecutiveFailures = consecutiveFailures;
        Deadline = deadline.ToUniversalTime();
    }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the next one-based observation attempt.</summary>
    public int Attempt { get; }

    /// <summary>Gets the consecutive failure count.</summary>
    public int ConsecutiveFailures { get; }

    /// <summary>Gets the UTC retry deadline.</summary>
    public DateTimeOffset Deadline { get; }

    /// <summary>Creates initial retry state from a startup instant and timeout.</summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="startedAt">The UTC startup instant.</param>
    /// <param name="timeout">The allowed health sequence duration.</param>
    /// <returns>Initial retry state.</returns>
    public static HealthRetryState Start(Guid serviceId, DateTimeOffset startedAt, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        return new(serviceId, 1, 0, startedAt.ToUniversalTime().Add(timeout));
    }
}

/// <summary>Contains the immutable result of a health retry decision.</summary>
public sealed record HealthRetryDecision
{
    /// <summary>Creates a health retry decision.</summary>
    /// <param name="action">The selected action.</param>
    /// <param name="nextAttemptAt">The next attempt instant, when retrying.</param>
    /// <param name="nextState">The state for a subsequent decision.</param>
    /// <param name="reason">The safe reason code.</param>
    public HealthRetryDecision(
        HealthRetryAction action,
        DateTimeOffset? nextAttemptAt,
        HealthRetryState nextState,
        ServiceStateReasonCode reason)
    {
        Action = action;
        NextAttemptAt = nextAttemptAt?.ToUniversalTime();
        NextState = nextState;
        Reason = reason;
    }

    /// <summary>Gets the selected retry action.</summary>
    public HealthRetryAction Action { get; }

    /// <summary>Gets the next UTC attempt instant when action is retry.</summary>
    public DateTimeOffset? NextAttemptAt { get; }

    /// <summary>Gets the immutable subsequent retry state.</summary>
    public HealthRetryState NextState { get; }

    /// <summary>Gets the safe reason code.</summary>
    public ServiceStateReasonCode Reason { get; }
}

/// <summary>Defines bounded health timeout, interval, and failure-threshold policy.</summary>
public sealed record HealthRetryPolicy
{
    /// <summary>Creates a health retry policy.</summary>
    /// <param name="startupTimeout">The total startup health timeout.</param>
    /// <param name="retryInterval">The delay between health attempts.</param>
    /// <param name="probeTimeout">The maximum duration of one probe.</param>
    /// <param name="failureThreshold">The consecutive failure threshold.</param>
    public HealthRetryPolicy(
        TimeSpan startupTimeout,
        TimeSpan retryInterval,
        TimeSpan probeTimeout,
        int failureThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(startupTimeout, TimeSpan.Zero);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryInterval, TimeSpan.Zero);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(probeTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(probeTimeout, startupTimeout);

        ArgumentOutOfRangeException.ThrowIfLessThan(failureThreshold, 1);

        StartupTimeout = startupTimeout;
        RetryInterval = retryInterval;
        ProbeTimeout = probeTimeout;
        FailureThreshold = failureThreshold;
    }

    /// <summary>Gets the documented startup timeout.</summary>
    public TimeSpan StartupTimeout { get; }

    /// <summary>Gets the documented startup retry interval.</summary>
    public TimeSpan RetryInterval { get; }

    /// <summary>Gets the maximum duration of one probe.</summary>
    public TimeSpan ProbeTimeout { get; }

    /// <summary>Gets the consecutive failure threshold.</summary>
    public int FailureThreshold { get; }

    /// <summary>Gets the default policy.</summary>
    public static HealthRetryPolicy Default => new(
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        3);

    /// <summary>Chooses a retry action without performing a probe or waiting.</summary>
    /// <param name="state">The current retry state.</param>
    /// <param name="observation">The safe health result.</param>
    /// <param name="now">The current UTC instant.</param>
    /// <param name="cancellationToken">The cancellation token to observe.</param>
    /// <returns>A pure retry decision.</returns>
    public HealthRetryDecision Decide(
        HealthRetryState state,
        HealthObservationResult observation,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.ServiceId != state.ServiceId)
        {
            throw new ArgumentException("The health result belongs to another service.", nameof(observation));
        }

        var utcNow = now.ToUniversalTime();
        if (cancellationToken.IsCancellationRequested || observation.Status == HealthObservationStatus.Cancelled)
        {
            return new HealthRetryDecision(
                HealthRetryAction.Cancelled,
                null,
                state,
                ServiceStateReasonCode.Cancelled);
        }

        if (observation.Status == HealthObservationStatus.Healthy)
        {
            return new HealthRetryDecision(
                HealthRetryAction.Healthy,
                null,
                state,
                ServiceStateReasonCode.Healthy);
        }

        var failures = checked(state.ConsecutiveFailures + 1);
        if (utcNow >= state.Deadline || observation.ObservedAt >= state.Deadline)
        {
            return new HealthRetryDecision(
                HealthRetryAction.TimedOut,
                null,
                new HealthRetryState(state.ServiceId, state.Attempt, failures, state.Deadline),
                ServiceStateReasonCode.HealthTimeout);
        }

        if (failures >= FailureThreshold)
        {
            return new HealthRetryDecision(
                HealthRetryAction.Failed,
                null,
                new HealthRetryState(state.ServiceId, state.Attempt, failures, state.Deadline),
                ServiceStateReasonCode.HealthFailureThreshold);
        }

        var nextAttemptAt = utcNow.Add(RetryInterval);
        if (nextAttemptAt >= state.Deadline)
        {
            return new HealthRetryDecision(
                HealthRetryAction.TimedOut,
                null,
                new HealthRetryState(state.ServiceId, state.Attempt, failures, state.Deadline),
                ServiceStateReasonCode.HealthTimeout);
        }

        return new HealthRetryDecision(
            HealthRetryAction.Retry,
            nextAttemptAt,
            new HealthRetryState(state.ServiceId, checked(state.Attempt + 1), failures, state.Deadline),
            ServiceStateReasonCode.HealthCheckFailed);
    }
}
