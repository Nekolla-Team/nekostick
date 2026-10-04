namespace Nekolla.Nekostick.Contracts;

/// <summary>Identifies one extension-visible standard service-output stream.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711",
    Justification = "This public enum intentionally identifies a service output stream.")]
public enum ExtensionServiceOutputStream
{
    /// <summary>The standard-output stream.</summary>
    Stdout,

    /// <summary>The standard-error stream.</summary>
    Stderr
}


/// <summary>Identifies the result category of an extension service-output operation.</summary>
public enum ExtensionServiceOutputCode
{
    /// <summary>No operation result was assigned.</summary>
    None,
    /// <summary>The requested output stream was opened.</summary>
    Opened,

    /// <summary>The identifier is invalid or no configured service has that identifier.</summary>
    NotFound,

    /// <summary>No live or retained output pump is available for the configured service.</summary>
    NotRunning,

    /// <summary>Service output is not supported for the requested service.</summary>
    Unsupported,

    /// <summary>The operation failed safely.</summary>
    Failed
}



/// <summary>Contains the safe result of opening one service-output stream.</summary>
public sealed record ExtensionServiceOutputStreamResult
{
    /// <summary>Creates a service-output stream result.</summary>
    /// <param name="succeeded">Whether the stream was opened.</param>
    /// <param name="code">The stable result category.</param>
    /// <param name="serviceId">The affected service identifier.</param>
    /// <param name="stream">The readable output stream when <paramref name="succeeded" /> is <see langword="true" />.</param>
    /// <param name="detail">The required precise failure cause, or <see langword="null" /> on success.</param>
    public ExtensionServiceOutputStreamResult(
        bool succeeded,
        ExtensionServiceOutputCode code,
        Guid serviceId,
        System.IO.Stream? stream,
        ExtensionErrorDetail? detail)
    {
        ServiceId = IdentityValidation.RequireUuidV7(serviceId, nameof(serviceId));
        if (succeeded != (code == ExtensionServiceOutputCode.Opened))
        {
            throw new ArgumentException("The service-output result code is inconsistent.", nameof(code));
        }

        if (succeeded && stream is null)
        {
            throw new ArgumentNullException(nameof(stream), "A successful result must include a stream.");
        }

        if (!succeeded && stream is not null)
        {
            throw new ArgumentException("An unsuccessful result cannot include a stream.", nameof(stream));
        }

        if (succeeded && detail is not null)
        {
            throw new ArgumentException("A successful result cannot include error detail.", nameof(detail));
        }

        if (!succeeded && detail is null)
        {
            throw new ArgumentNullException(nameof(detail), "An unsuccessful result must include error detail.");
        }

        Succeeded = succeeded;
        Code = code;
        Stream = stream;
        Detail = detail;
    }

    /// <summary>Gets whether the stream was opened.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the stable service-output result category.</summary>
    public ExtensionServiceOutputCode Code { get; }

    /// <summary>Gets the affected service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the readable output stream when the operation succeeded.</summary>
    public System.IO.Stream? Stream { get; }

    /// <summary>Gets the precise failure cause, or <see langword="null" /> on success.</summary>
    public ExtensionErrorDetail? Detail { get; }
}


/// <summary>Provides raw stdout/stderr streams and ordered service log subscriptions for configured services.</summary>
/// <remarks>
/// Any loaded extension may open the stdout/stderr of any configured service in the current host configuration.
/// Output content excludes helper protocol markers. Each stream is bound to the current process instance and reaches
/// end-of-stream when that process exits. When the executor is available, a configured service without a live or
/// retained output pump returns <see cref="ExtensionServiceOutputCode.NotRunning" />. Domain failures are returned
/// through result codes rather than exceptions. Configuration is checked when <see cref="OpenStreamAsync" /> is
/// called. An established stream keeps receiving output from the bound process generation until it exits, even if
/// the service is later removed from configuration. Output produced before <see cref="OpenStreamAsync" /> is not
/// replayed. The stream is read-only, non-seekable, one per call, and caller-disposed; it may contain silent byte
/// gaps after buffer overflow.
/// </remarks>
public interface IExtensionServiceOutputApi
{
    /// <summary>Opens a readable raw output stream for one configured running service.</summary>
    /// <param name="serviceId">The configured service identifier.</param>
    /// <param name="stream">The output stream to open (stdout or stderr).</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A safe result containing the readable stream when opened.</returns>
    ValueTask<ExtensionServiceOutputStreamResult> OpenStreamAsync(
        Guid serviceId,
        ExtensionServiceOutputStream stream,
        CancellationToken cancellationToken = default);
    /// <summary>Subscribes to the ordered output and lifecycle feed for one configured service.</summary>
    /// <param name="serviceId">The configured service identifier.</param>
    /// <param name="sink">The receiver for ordered log entries.</param>
    /// <param name="sinceSequence">An optional cursor; only entries with larger sequences are replayed.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A safe result containing the caller-owned subscription when created.</returns>
    /// <remarks>
    /// A configured but disabled service still yields a subscription, which terminates with
    /// <see cref="ExtensionServiceLogTerminationReason.ServiceDisabled"/> once no process generation remains. If no
    /// generation is active, the termination entry and completion are delivered immediately.
    ///
    /// Each service has one monotonic sequence shared by output and lifecycle entries. A subscription without a cursor
    /// receives a current-state marker first, then retained entries and live entries. A cursor replays entries with a
    /// greater sequence; an explicit gap entry reports any older entries evicted by the byte-bounded, drop-oldest replay
    /// buffer. The per-service buffer budget includes a fixed metadata allowance and bounded failure-reason bytes;
    /// entries too large to retain are delivered live and reported as gaps to later subscribers. Slow callback delivery
    /// queues also drop oldest entries and report gaps before later sequenced entries. Sequence numbers are process-local
    /// and reset when the host restarts, so a cursor applies only to the current host session. With no cursor, replay
    /// begins after sequence 0; after eviction or reopen, a leading <see cref="ExtensionServiceLogEntryKind.Gap"/>
    /// covers sequence 1 through one less than the oldest retained sequence, or through the latest sequence if none remain.
    /// </remarks>
    ValueTask<ExtensionServiceLogSubscriptionResult> SubscribeAsync(
        Guid serviceId,
        IExtensionServiceLogSink sink,
        long? sinceSequence = null,
        CancellationToken cancellationToken = default);

}
