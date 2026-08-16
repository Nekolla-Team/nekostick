namespace Nekolla.Nekostick.Supervision;

/// <summary>Identifies a port lease operation intent.</summary>
public enum PortLeaseIntentKind
{
    /// <summary>Acquire a new lease.</summary>
    Acquire,

    /// <summary>Release an existing lease.</summary>
    Release,

    /// <summary>Renew an existing lease.</summary>
    Renew
}

/// <summary>Contains one immutable port lease request, release, or renewal intent.</summary>
public sealed record PortLeaseIntent
{
    private PortLeaseIntent(
        PortLeaseIntentKind kind,
        PortLeaseRequest? request,
        PortLeaseRelease? release,
        PortLeaseRenewal? renewal)
    {
        Kind = kind;
        Request = request;
        Release = release;
        Renewal = renewal;
    }

    /// <summary>Creates an acquire intent.</summary>
    /// <param name="request">The immutable lease request.</param>
    /// <returns>An acquire intent.</returns>
    public static PortLeaseIntent Acquire(PortLeaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(PortLeaseIntentKind.Acquire, request, null, null);
    }

    /// <summary>Creates a release intent.</summary>
    /// <param name="release">The immutable release request.</param>
    /// <returns>A release intent.</returns>
    public static PortLeaseIntent ReleaseLease(PortLeaseRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return new(PortLeaseIntentKind.Release, null, release, null);
    }

    /// <summary>Creates a renewal intent.</summary>
    /// <param name="renewal">The immutable renewal request.</param>
    /// <returns>A renewal intent.</returns>
    public static PortLeaseIntent Renew(PortLeaseRenewal renewal)
    {
        ArgumentNullException.ThrowIfNull(renewal);
        return new(PortLeaseIntentKind.Renew, null, null, renewal);
    }

    /// <summary>Gets the operation kind.</summary>
    public PortLeaseIntentKind Kind { get; }

    /// <summary>Gets the acquire request, when this is an acquire intent.</summary>
    public PortLeaseRequest? Request { get; }

    /// <summary>Gets the release request, when this is a release intent.</summary>
    public PortLeaseRelease? Release { get; }

    /// <summary>Gets the renewal request, when this is a renewal intent.</summary>
    public PortLeaseRenewal? Renewal { get; }
}
