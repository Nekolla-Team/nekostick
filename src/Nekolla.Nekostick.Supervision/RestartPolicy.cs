using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Supervision;

/// <summary>Supplies bounded jitter for a calculated restart delay.</summary>
public interface IRestartJitter
{
    /// <summary>Calculates a non-negative jitter amount within the supplied bound.</summary>
    /// <param name="maximumJitter">The maximum permitted jitter.</param>
    /// <param name="attempt">The one-based restart attempt.</param>
    /// <returns>A jitter duration between zero and the supplied bound.</returns>
    TimeSpan GetJitter(TimeSpan maximumJitter, int attempt);
}

/// <summary>Provides deterministic zero jitter for tests and exact schedules.</summary>
public sealed class NoRestartJitter : IRestartJitter
{
    /// <summary>Returns zero without inspecting any sensitive value.</summary>
    /// <param name="maximumJitter">The permitted jitter bound.</param>
    /// <param name="attempt">The one-based restart attempt.</param>
    /// <returns>Zero duration.</returns>
    public TimeSpan GetJitter(TimeSpan maximumJitter, int attempt) => TimeSpan.Zero;
}

/// <summary>Defines an immutable exponential restart backoff.</summary>
public sealed record RestartBackoffPolicy
{
    /// <summary>Creates a bounded exponential restart policy.</summary>
    /// <param name="initialDelay">The delay before attempt one.</param>
    /// <param name="maximumDelay">The delay cap before jitter.</param>
    /// <param name="maximumJitter">The injected jitter bound.</param>
    /// <param name="maximumAttempts">The maximum attempts in one window.</param>
    /// <param name="attemptWindow">The attempt-window duration.</param>
    public RestartBackoffPolicy(
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        TimeSpan maximumJitter,
        int maximumAttempts,
        TimeSpan attemptWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDelay, initialDelay, nameof(initialDelay));

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumJitter, TimeSpan.Zero);

        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumJitter,
            TimeSpan.MaxValue - maximumDelay);

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(attemptWindow, TimeSpan.Zero);

        InitialDelay = initialDelay;
        MaximumDelay = maximumDelay;
        MaximumJitter = maximumJitter;
        MaximumAttempts = maximumAttempts;
        AttemptWindow = attemptWindow;
    }

    /// <summary>Gets the initial delay. The default is exactly 1 second.</summary>
    public TimeSpan InitialDelay { get; }

    /// <summary>Gets the pre-jitter delay cap. The default is exactly 30 seconds.</summary>
    public TimeSpan MaximumDelay { get; }

    /// <summary>Gets the maximum injected jitter.</summary>
    public TimeSpan MaximumJitter { get; }

    /// <summary>Gets the maximum attempts in one window. The default is exactly 10 attempts.</summary>
    public int MaximumAttempts { get; }

    /// <summary>Gets the duration of the attempt window. The default is exactly 5 minutes.</summary>
    public TimeSpan AttemptWindow { get; }

    /// <summary>Gets a policy whose first delays are exactly 1, 2, 4, 8, 16, and 30 seconds, with zero injected jitter and at most 10 attempts per 5-minute window.</summary>
    public static RestartBackoffPolicy Default => new(
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(30),
        TimeSpan.Zero,
        10,
        TimeSpan.FromMinutes(5));

    /// <summary>Calculates a one-based exponential delay before jitter.</summary>
    /// <param name="attempt">The one-based attempt number.</param>
    /// <returns>The bounded base delay.</returns>
    public TimeSpan GetBaseDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        var delayTicks = InitialDelay.Ticks;
        for (var index = 1; index < attempt && delayTicks < MaximumDelay.Ticks; index++)
        {
            delayTicks = delayTicks > MaximumDelay.Ticks / 2
                ? MaximumDelay.Ticks
                : delayTicks * 2;
        }

        return TimeSpan.FromTicks(Math.Min(delayTicks, MaximumDelay.Ticks));
    }
}

/// <summary>Contains a pure restart planning result.</summary>
public sealed record RestartPlan
{
    /// <summary>Creates a restart plan.</summary>
    /// <param name="shouldRestart">Whether a restart is permitted.</param>
    /// <param name="cancelled">Whether cancellation prevented planning.</param>
    /// <param name="attempt">The planned one-based attempt number.</param>
    /// <param name="baseDelay">The exact exponential base delay.</param>
    /// <param name="jitter">The injected bounded jitter.</param>
    /// <param name="notBefore">The UTC time at which the restart may run.</param>
    /// <param name="nextAttemptState">The immutable state after planning.</param>
    /// <param name="reason">The safe reason code.</param>
    public RestartPlan(
        bool shouldRestart,
        bool cancelled,
        int attempt,
        TimeSpan baseDelay,
        TimeSpan jitter,
        DateTimeOffset? notBefore,
        RestartAttemptState nextAttemptState,
        ServiceStateReasonCode reason)
    {
        ShouldRestart = shouldRestart;
        Cancelled = cancelled;
        Attempt = attempt;
        BaseDelay = baseDelay;
        Jitter = jitter;
        Delay = baseDelay + jitter;
        NotBefore = notBefore?.ToUniversalTime();
        NextAttemptState = nextAttemptState;
        Reason = reason;
    }

    /// <summary>Gets whether a restart is permitted.</summary>
    public bool ShouldRestart { get; }

    /// <summary>Gets whether cancellation prevented a restart plan.</summary>
    public bool Cancelled { get; }

    /// <summary>Gets the planned one-based attempt number.</summary>
    public int Attempt { get; }

    /// <summary>Gets the exact exponential delay before jitter.</summary>
    public TimeSpan BaseDelay { get; }

    /// <summary>Gets the bounded injected jitter.</summary>
    public TimeSpan Jitter { get; }

    /// <summary>Gets the total delay.</summary>
    public TimeSpan Delay { get; }

    /// <summary>Gets the UTC instant at which a permitted restart may run.</summary>
    public DateTimeOffset? NotBefore { get; }

    /// <summary>Gets the immutable attempt state after planning.</summary>
    public RestartAttemptState NextAttemptState { get; }

    /// <summary>Gets the safe reason code.</summary>
    public ServiceStateReasonCode Reason { get; }
}

/// <summary>Plans restarts without starting processes or waiting.</summary>
public static class RestartPlanner
{
    /// <summary>Creates a cancellation-aware restart plan.</summary>
    /// <param name="policy">The domain restart policy.</param>
    /// <param name="successfulExit">Whether the process exited successfully.</param>
    /// <param name="attemptState">The immutable attempt state.</param>
    /// <param name="now">The current UTC instant.</param>
    /// <param name="backoff">The bounded backoff policy.</param>
    /// <param name="jitter">The injected jitter provider.</param>
    /// <param name="cancellationToken">The cancellation token to observe.</param>
    /// <returns>A pure restart plan.</returns>
    public static RestartPlan Plan(
        ServiceRestartPolicy policy,
        bool successfulExit,
        RestartAttemptState attemptState,
        DateTimeOffset now,
        RestartBackoffPolicy backoff,
        IRestartJitter jitter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backoff);
        ArgumentNullException.ThrowIfNull(jitter);

        if (cancellationToken.IsCancellationRequested)
        {
            return NoPlan(attemptState, ServiceStateReasonCode.Cancelled, cancelled: true);
        }

        if (policy == ServiceRestartPolicy.Never ||
            policy == ServiceRestartPolicy.OnFailure && successfulExit)
        {
            return NoPlan(attemptState, ServiceStateReasonCode.RestartPolicyDisabled);
        }

        var utcNow = now.ToUniversalTime();
        var state = attemptState.IsWindowExpired(utcNow, backoff.AttemptWindow)
            ? RestartAttemptState.Empty
            : attemptState;
        var attempt = checked(state.Attempts + 1);
        if (attempt > backoff.MaximumAttempts)
        {
            return NoPlan(state, ServiceStateReasonCode.RestartAttemptLimitReached);
        }

        var baseDelay = backoff.GetBaseDelay(attempt);
        var boundedJitter = jitter.GetJitter(backoff.MaximumJitter, attempt);
        if (boundedJitter < TimeSpan.Zero || boundedJitter > backoff.MaximumJitter)
        {
            throw new ArgumentException("The jitter provider returned a value outside its bound.", nameof(jitter));
        }

        var delay = baseDelay + boundedJitter;
        var notBefore = utcNow.Add(delay);
        var nextState = state.RecordAttempt(utcNow, backoff.AttemptWindow);
        return new RestartPlan(
            shouldRestart: true,
            cancelled: false,
            attempt: attempt,
            baseDelay: baseDelay,
            jitter: boundedJitter,
            notBefore: notBefore,
            nextAttemptState: nextState,
            reason: ServiceStateReasonCode.ProcessExited);
    }

    private static RestartPlan NoPlan(
        RestartAttemptState state,
        ServiceStateReasonCode reason,
        bool cancelled = false) => new(
        shouldRestart: false,
        cancelled: cancelled,
        attempt: 0,
        baseDelay: TimeSpan.Zero,
        jitter: TimeSpan.Zero,
        notBefore: null,
        nextAttemptState: state,
        reason: reason);
}
