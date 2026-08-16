namespace Nekolla.Nekostick.Supervision;

/// <summary>Contains an immutable service deadline.</summary>
public readonly record struct ServiceDeadline
{
    /// <summary>Creates a service deadline.</summary>
    /// <param name="kind">The deadline kind.</param>
    /// <param name="at">The UTC deadline instant.</param>
    public ServiceDeadline(ServiceDeadlineKind kind, DateTimeOffset at)
    {
        Kind = kind;
        At = at.ToUniversalTime();
    }

    /// <summary>Gets the deadline kind.</summary>
    public ServiceDeadlineKind Kind { get; }

    /// <summary>Gets the UTC instant at which the deadline is reached.</summary>
    public DateTimeOffset At { get; }

    /// <summary>Determines whether the deadline has been reached.</summary>
    /// <param name="now">The instant to compare with the deadline.</param>
    /// <returns><see langword="true"/> when the deadline is reached.</returns>
    public bool IsReached(DateTimeOffset now) => now.ToUniversalTime() >= At;
}

/// <summary>Contains immutable restart-attempt counters for one service window.</summary>
public readonly record struct RestartAttemptState
{
    /// <summary>Creates restart-attempt state.</summary>
    /// <param name="attempts">The number of attempts in the active window.</param>
    /// <param name="windowStartedAt">The start of the active window, if any.</param>
    /// <param name="lastAttemptAt">The last attempt instant, if any.</param>
    public RestartAttemptState(int attempts, DateTimeOffset? windowStartedAt = null, DateTimeOffset? lastAttemptAt = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempts);

        if (attempts == 0 && (windowStartedAt.HasValue || lastAttemptAt.HasValue))
        {
            throw new ArgumentException("Zero attempts cannot have attempt timestamps.", nameof(attempts));
        }

        if (attempts > 0 && (!windowStartedAt.HasValue || !lastAttemptAt.HasValue))
        {
            throw new ArgumentException("Attempt timestamps are required when attempts exist.", nameof(attempts));
        }

        if (windowStartedAt.HasValue && lastAttemptAt.HasValue &&
            lastAttemptAt.Value.ToUniversalTime() < windowStartedAt.Value.ToUniversalTime())
        {
            throw new ArgumentException("The last attempt cannot precede the attempt window.", nameof(lastAttemptAt));
        }

        Attempts = attempts;
        WindowStartedAt = windowStartedAt?.ToUniversalTime();
        LastAttemptAt = lastAttemptAt?.ToUniversalTime();
    }

    /// <summary>Gets the number of restart attempts in the active window.</summary>
    public int Attempts { get; }

    /// <summary>Gets the active restart-window start.</summary>
    public DateTimeOffset? WindowStartedAt { get; }

    /// <summary>Gets the last restart-attempt instant.</summary>
    public DateTimeOffset? LastAttemptAt { get; }

    /// <summary>Gets an empty attempt state.</summary>
    public static RestartAttemptState Empty => new(0);

    /// <summary>Determines whether the active window has expired.</summary>
    /// <param name="now">The instant to compare.</param>
    /// <param name="window">The maximum active-window duration.</param>
    /// <returns><see langword="true"/> when a reset is due.</returns>
    public bool IsWindowExpired(DateTimeOffset now, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        return !WindowStartedAt.HasValue || now.ToUniversalTime() - WindowStartedAt.Value >= window;
    }

    /// <summary>Records one restart attempt immutably.</summary>
    /// <param name="at">The UTC attempt instant.</param>
    /// <param name="window">The active-window duration.</param>
    /// <returns>The next immutable attempt state.</returns>
    public RestartAttemptState RecordAttempt(DateTimeOffset at, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        var utc = at.ToUniversalTime();
        return IsWindowExpired(utc, window)
            ? new RestartAttemptState(1, utc, utc)
            : new RestartAttemptState(checked(Attempts + 1), WindowStartedAt, utc);
    }
}
