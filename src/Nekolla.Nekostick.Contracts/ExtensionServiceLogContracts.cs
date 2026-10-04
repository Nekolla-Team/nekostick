using System.Collections.Immutable;

namespace Nekolla.Nekostick.Contracts;

/// <summary>Identifies one entry in a service's ordered log feed.</summary>
public enum ExtensionServiceLogEntryKind
{
    /// <summary>Raw bytes emitted on stdout or stderr.</summary>
    Output,

    /// <summary>A new process generation started.</summary>
    GenerationStarted,

    /// <summary>A process generation exited.</summary>
    ProcessExited,

    /// <summary>A service startup attempt failed.</summary>
    StartupFailed,

    /// <summary>The lifecycle state observed when a subscription was created.</summary>
    CurrentState,

    /// <summary>One or more retained entries are missing.</summary>
    Gap,

    /// <summary>The service log feed ended for a lifecycle reason.</summary>
    Termination
}

/// <summary>Identifies why a service log subscription was terminated by the host.</summary>
public enum ExtensionServiceLogTerminationReason
{
    /// <summary>The owning extension instance unloaded or reloaded.</summary>
    ExtensionUnloaded,

    /// <summary>The service was disabled in host configuration.</summary>
    ServiceDisabled,

    /// <summary>The service was removed from host configuration.</summary>
    ServiceRemoved,

    /// <summary>The host shut down.</summary>
    HostShutdown
}

/// <summary>Identifies the result category of a service log subscription request.</summary>
public enum ExtensionServiceLogCode
{
    /// <summary>No result code was assigned.</summary>
    None,

    /// <summary>The subscription was created.</summary>
    Subscribed,

    /// <summary>The service identifier or sink is invalid.</summary>
    InvalidArgument,

    /// <summary>The requested cursor is beyond the latest service log sequence.</summary>
    InvalidCursor,

    /// <summary>The service is not present in the current host configuration.</summary>
    NotFound,

    /// <summary>The host does not provide service log capture.</summary>
    Unsupported,

    /// <summary>The request was cancelled before the subscription was created.</summary>
    Cancelled,

    /// <summary>The subscription request failed safely.</summary>
    Failed
}

/// <summary>Represents one sequenced service log entry or subscribe-time marker.</summary>
public sealed record ExtensionServiceLogEntry
{
    private const int MaximumFailureReasonLength = 512;

    /// <summary>Creates a service log entry.</summary>
    /// <param name="kind">The entry kind.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="sequence">The monotonic per-service sequence, or null for a subscribe-time marker or extension-local termination.</param>
    /// <param name="timestamp">The UTC time associated with the entry.</param>
    /// <param name="stream">The output stream for an output entry.</param>
    /// <param name="data">The immutable raw output bytes for an output entry.</param>
    /// <param name="processInstanceId">The opaque process generation identifier, when applicable.</param>
    /// <param name="attemptNumber">The one-based process start attempt number, when applicable.</param>
    /// <param name="processExitCode">The process exit code, when reported.</param>
    /// <param name="lifecycleState">The lifecycle state for a current-state entry.</param>
    /// <param name="failureStage">The lifecycle stage for a startup-failed entry.</param>
    /// <param name="failureCode">The safe failure code for a startup-failed entry.</param>
    /// <param name="failureReason">The bounded safe failure explanation, when available.</param>
    /// <param name="firstMissingSequence">The first sequence missing from a gap entry.</param>
    /// <param name="lastMissingSequence">The last sequence missing from a gap entry.</param>
    /// <param name="terminationReason">The reason carried by a termination entry.</param>
    public ExtensionServiceLogEntry(
        ExtensionServiceLogEntryKind kind,
        Guid serviceId,
        long? sequence,
        DateTimeOffset timestamp,
        ExtensionServiceOutputStream? stream = null,
        ImmutableArray<byte> data = default,
        Guid? processInstanceId = null,
        int? attemptNumber = null,
        int? processExitCode = null,
        ExtensionServiceLifecycleState? lifecycleState = null,
        ExtensionServiceFailureStage failureStage = ExtensionServiceFailureStage.None,
        ExtensionServiceFailureCode failureCode = ExtensionServiceFailureCode.None,
        string? failureReason = null,
        long? firstMissingSequence = null,
        long? lastMissingSequence = null,
        ExtensionServiceLogTerminationReason? terminationReason = null)
    {
        ServiceId = IdentityValidation.RequireUuidV7(serviceId, nameof(serviceId));
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (sequence is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (processInstanceId == Guid.Empty)
        {
            throw new ArgumentException("A process instance identifier cannot be empty.", nameof(processInstanceId));
        }

        if (attemptNumber is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        }

        if (stream is { } outputStream && !Enum.IsDefined(outputStream))
        {
            throw new ArgumentOutOfRangeException(nameof(stream));
        }

        switch (kind)
        {
            case ExtensionServiceLogEntryKind.Output:
                RequireSequence(sequence, nameof(sequence));
                if (stream is null)
                {
                    throw new ArgumentNullException(nameof(stream));
                }

                if (processInstanceId is null)
                {
                    throw new ArgumentNullException(nameof(processInstanceId));
                }
                if (attemptNumber is null)
                {
                    throw new ArgumentNullException(nameof(attemptNumber));
                }
                break;
            case ExtensionServiceLogEntryKind.GenerationStarted:
                RequireSequence(sequence, nameof(sequence));
                if (processInstanceId is null)
                {
                    throw new ArgumentNullException(nameof(processInstanceId));
                }
                if (attemptNumber is null)
                {
                    throw new ArgumentNullException(nameof(attemptNumber));
                }
                break;
            case ExtensionServiceLogEntryKind.ProcessExited:
                RequireSequence(sequence, nameof(sequence));
                if (processInstanceId is null)
                {
                    throw new ArgumentNullException(nameof(processInstanceId));
                }
                if (attemptNumber is null)
                {
                    throw new ArgumentNullException(nameof(attemptNumber));
                }
                break;
            case ExtensionServiceLogEntryKind.StartupFailed:
                RequireSequence(sequence, nameof(sequence));
                if (attemptNumber is null)
                {
                    throw new ArgumentNullException(nameof(attemptNumber));
                }
                if (failureStage == ExtensionServiceFailureStage.None || failureCode == ExtensionServiceFailureCode.None)
                {
                    throw new ArgumentException("A startup failure requires a stage and failure code.");
                }
                break;
            case ExtensionServiceLogEntryKind.CurrentState:
                if (sequence is not null || lifecycleState is null || !Enum.IsDefined(lifecycleState.Value))
                {
                    throw new ArgumentException("A current-state marker requires a lifecycle state and no sequence.");
                }
                break;
            case ExtensionServiceLogEntryKind.Gap:
                RequireSequence(sequence, nameof(sequence));
                if (firstMissingSequence is not > 0 ||
                    lastMissingSequence is not { } lastMissing ||
                    lastMissing < firstMissingSequence.Value ||
                    sequence != lastMissing)
                {
                    throw new ArgumentException("A gap entry requires an ordered missing-sequence range ending at its sequence.");
                }
                break;
            case ExtensionServiceLogEntryKind.Termination:
                if (terminationReason is null || !Enum.IsDefined(terminationReason.Value))
                {
                    throw new ArgumentException("A termination entry requires a reason.", nameof(terminationReason));
                }
                break;
        }

        if (failureReason is { Length: > MaximumFailureReasonLength })
        {
            failureReason = failureReason[..MaximumFailureReasonLength];
        }

        Kind = kind;
        Sequence = sequence;
        Timestamp = timestamp.ToUniversalTime();
        Stream = stream;
        Data = data.IsDefault ? ImmutableArray<byte>.Empty : data;
        ProcessInstanceId = processInstanceId;
        AttemptNumber = attemptNumber;
        ProcessExitCode = processExitCode;
        LifecycleState = lifecycleState;
        FailureStage = failureStage;
        FailureCode = failureCode;
        FailureReason = failureReason;
        FirstMissingSequence = firstMissingSequence;
        LastMissingSequence = lastMissingSequence;
        TerminationReason = terminationReason;
    }

    /// <summary>Gets the entry kind.</summary>
    public ExtensionServiceLogEntryKind Kind { get; }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the monotonic per-service sequence, or null for a subscribe-time marker or extension-local termination.</summary>
    public long? Sequence { get; }

    /// <summary>Gets the UTC event timestamp.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Gets the output stream for an output entry.</summary>
    public ExtensionServiceOutputStream? Stream { get; }

    /// <summary>Gets the immutable raw bytes for an output entry.</summary>
    public ImmutableArray<byte> Data { get; }

    /// <summary>Gets the opaque process generation identifier, when applicable.</summary>
    public Guid? ProcessInstanceId { get; }

    /// <summary>Gets the one-based process start attempt number, when applicable.</summary>
    public int? AttemptNumber { get; }

    /// <summary>Gets the process exit code, when reported.</summary>
    public int? ProcessExitCode { get; }

    /// <summary>Gets the lifecycle state for a current-state entry.</summary>
    public ExtensionServiceLifecycleState? LifecycleState { get; }

    /// <summary>Gets the lifecycle stage for a startup-failed entry.</summary>
    public ExtensionServiceFailureStage FailureStage { get; }

    /// <summary>Gets the safe failure code for a startup-failed entry.</summary>
    public ExtensionServiceFailureCode FailureCode { get; }

    /// <summary>Gets the bounded safe failure explanation, when available.</summary>
    public string? FailureReason { get; }

    /// <summary>Gets the first sequence missing from a gap entry.</summary>
    public long? FirstMissingSequence { get; }

    /// <summary>Gets the last sequence missing from a gap entry.</summary>
    public long? LastMissingSequence { get; }

    /// <summary>Gets the reason carried by a termination entry.</summary>
    public ExtensionServiceLogTerminationReason? TerminationReason { get; }

    private static void RequireSequence(long? sequence, string parameterName)
    {
        if (sequence is null or <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

/// <summary>Receives ordered service log entries and normal completion notifications.</summary>
/// <remarks>
/// Callbacks for one subscription are serialized on a background thread. Sink exceptions are isolated by the host.
/// A slow sink can overflow its bounded delivery queue; the host emits a gap entry before later sequenced entries.
/// </remarks>
public interface IExtensionServiceLogSink
{
    /// <summary>Receives one service log entry.</summary>
    /// <param name="entry">The output, lifecycle, state, gap, or termination entry.</param>
    void OnEntry(ExtensionServiceLogEntry entry);

    /// <summary>Receives notification that the subscription completed normally after a termination entry.</summary>
    void OnCompleted();
}

/// <summary>Represents a caller-owned service log subscription.</summary>
/// <remarks>
/// <see cref="IDisposable.Dispose"/> detaches the subscription on a best-effort basis; one callback may still be
/// in flight. <see cref="IAsyncDisposable.DisposeAsync"/> additionally awaits callback quiescence. It MUST NOT be
/// synchronously waited on from within a callback of the same subscription.
/// </remarks>
public interface IExtensionServiceLogSubscription : IDisposable, IAsyncDisposable
{
}

/// <summary>Contains the safe result of a service log subscription request.</summary>
public sealed record ExtensionServiceLogSubscriptionResult
{
    /// <summary>Creates a service log subscription result.</summary>
    /// <param name="succeeded">Whether the subscription was created.</param>
    /// <param name="code">The fixed result code.</param>
    /// <param name="serviceId">The affected service identifier.</param>
    /// <param name="subscription">The caller-owned subscription when successful.</param>
    /// <param name="detail">The required precise failure cause, or <see langword="null" /> on success.</param>
    public ExtensionServiceLogSubscriptionResult(
        bool succeeded,
        ExtensionServiceLogCode code,
        Guid serviceId,
        IExtensionServiceLogSubscription? subscription,
        ExtensionErrorDetail? detail)
    {
        ServiceId = IdentityValidation.RequireUuidV7(serviceId, nameof(serviceId));
        if (succeeded != (code == ExtensionServiceLogCode.Subscribed) ||
            succeeded != (subscription is not null))
        {
            throw new ArgumentException("The service log subscription result is inconsistent.");
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
        Subscription = subscription;
        Detail = detail;
    }

    /// <summary>Gets whether the subscription was created.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the fixed result code.</summary>
    public ExtensionServiceLogCode Code { get; }

    /// <summary>Gets the affected service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the caller-owned subscription when the operation succeeded.</summary>
    public IExtensionServiceLogSubscription? Subscription { get; }

    /// <summary>Gets the precise failure cause, or <see langword="null" /> on success.</summary>
    public ExtensionErrorDetail? Detail { get; }
}


