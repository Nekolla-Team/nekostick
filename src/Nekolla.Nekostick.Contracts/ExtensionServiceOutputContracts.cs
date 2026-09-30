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

/// <summary>Identifies how an extension service output subscription completed.</summary>
public enum ExtensionServiceOutputCompletionReason
{
    /// <summary>The current service process exited and its output reached end-of-stream.</summary>
    ProcessExited,

    /// <summary>Output processing or host stop/cleanup failed; output may be incomplete.</summary>
    Faulted,

    /// <summary>The host ended the stream or subscription without a process-exit completion.</summary>
    HostTeardown
}

/// <summary>Identifies the result category of an extension service-output operation.</summary>
public enum ExtensionServiceOutputCode
{
    /// <summary>No operation result was assigned.</summary>
    None,

    /// <summary>The requested output stream or subscription was opened.</summary>
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

/// <summary>Represents one raw chunk of extension service output.</summary>
public sealed record ExtensionServiceOutputChunk
{
    /// <summary>Creates a service-output chunk.</summary>
    /// <param name="serviceId">The identifier of the service that emitted the bytes.</param>
    /// <param name="stream">The service-output stream that emitted the bytes.</param>
    /// <param name="timestamp">The time at which the bytes were captured.</param>
    /// <param name="data">The raw bytes captured from the service.</param>
    public ExtensionServiceOutputChunk(
        Guid serviceId,
        ExtensionServiceOutputStream stream,
        DateTimeOffset timestamp,
        byte[] data)
    {
        ServiceId = IdentityValidation.RequireUuidV7(serviceId, nameof(serviceId));
        if (!Enum.IsDefined(stream))
        {
            throw new ArgumentOutOfRangeException(nameof(stream));
        }

        ArgumentNullException.ThrowIfNull(data);
        Stream = stream;
        Timestamp = timestamp.ToUniversalTime();
        Data = data;
    }

    /// <summary>Gets the identifier of the service that emitted the bytes.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the service-output stream that emitted the bytes.</summary>
    public ExtensionServiceOutputStream Stream { get; }

    /// <summary>Gets the UTC time at which the host captured these bytes at the fan-out, not the delivery time.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Gets the raw bytes captured from the service.</summary>
    public byte[] Data { get; }
}

/// <summary>Represents a disposable handle for one service-output subscription.</summary>
/// <remarks>
/// <see cref="IDisposable.Dispose"/> detaches the subscription on a best-effort basis; callbacks may still be
/// in flight. <see cref="IAsyncDisposable.DisposeAsync"/> additionally awaits quiescence of any in-flight
/// sink callback before completing. No callback begins after <see cref="IAsyncDisposable.DisposeAsync"/> completes.
/// After plain <see cref="IDisposable.Dispose"/>, at most one in-flight callback, including
/// <see cref="IExtensionServiceOutputSink.OnCompleted"/>, may still run.
/// <see cref="IAsyncDisposable.DisposeAsync"/> MUST NOT be synchronously waited on with
/// <c>GetAwaiter().GetResult()</c> or <c>Wait()</c> from within a sink callback of the same subscription; that
/// self-wait deadlocks by design. Call <see cref="IDisposable.Dispose"/> or fire-and-forget
/// <see cref="IAsyncDisposable.DisposeAsync"/> from callbacks instead.
/// </remarks>
public interface IExtensionServiceOutputSubscription : IDisposable, IAsyncDisposable
{
}

/// <summary>Receives raw output chunks and lifecycle notifications for one service-output subscription.</summary>
/// <remarks>
/// Callbacks for one subscription are serialized and run on thread-pool threads. A sink must not block;
/// blocking only causes drops for that subscriber. Sink exceptions are swallowed and logged by the host.
/// <see cref="OnCompleted"/> is invoked at most once. No callback begins after
/// <see cref="IAsyncDisposable.DisposeAsync"/> completes; after plain
/// <see cref="IDisposable.Dispose"/>, at most one in-flight callback, including
/// <see cref="OnCompleted"/>, may still run.
/// <see cref="OnDropped"/> may arrive between chunks and signals a silent byte gap of the given size.
/// <see cref="ExtensionServiceOutputChunk.Data"/> is a fresh array owned by the recipient.
/// </remarks>
public interface IExtensionServiceOutputSink
{
    /// <summary>Receives one raw service-output chunk.</summary>
    /// <param name="chunk">The captured output chunk.</param>
    void OnChunk(ExtensionServiceOutputChunk chunk);

    /// <summary>Receives notification that the output stream or subscription has completed.</summary>
    /// <param name="reason">The completion reason.</param>
    void OnCompleted(ExtensionServiceOutputCompletionReason reason);

    /// <summary>Receives notification of a silent gap containing dropped output bytes.</summary>
    /// <param name="byteCount">The positive number of dropped bytes.</param>
    void OnDropped(long byteCount);
}

/// <summary>Contains the safe result of opening one service-output stream.</summary>
public sealed record ExtensionServiceOutputStreamResult
{
    /// <summary>Creates a service-output stream result.</summary>
    /// <param name="succeeded">Whether the stream was opened.</param>
    /// <param name="code">The stable result category.</param>
    /// <param name="serviceId">The affected service identifier.</param>
    /// <param name="stream">The readable output stream when <paramref name="succeeded" /> is <see langword="true" />.</param>
    public ExtensionServiceOutputStreamResult(
        bool succeeded,
        ExtensionServiceOutputCode code,
        Guid serviceId,
        System.IO.Stream? stream)
    {
        ServiceId = IdentityValidation.RequireUuidV7(serviceId, nameof(serviceId));
        if (succeeded && stream is null)
        {
            throw new ArgumentNullException(nameof(stream), "A successful result must include a stream.");
        }

        if (!succeeded && stream is not null)
        {
            throw new ArgumentException("An unsuccessful result cannot include a stream.", nameof(stream));
        }

        Succeeded = succeeded;
        Code = code;
        Stream = stream;
    }

    /// <summary>Gets whether the stream was opened.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the stable service-output result category.</summary>
    public ExtensionServiceOutputCode Code { get; }

    /// <summary>Gets the affected service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the readable output stream when the operation succeeded.</summary>
    public System.IO.Stream? Stream { get; }
}

/// <summary>Contains the safe result of subscribing to one service-output stream.</summary>
public sealed record ExtensionServiceOutputSubscriptionResult
{
    /// <summary>Creates a service-output subscription result.</summary>
    /// <param name="succeeded">Whether the subscription was created.</param>
    /// <param name="code">The stable result category.</param>
    /// <param name="serviceId">The affected service identifier.</param>
    /// <param name="subscription">The subscription handle when <paramref name="succeeded" /> is <see langword="true" />.</param>
    public ExtensionServiceOutputSubscriptionResult(
        bool succeeded,
        ExtensionServiceOutputCode code,
        Guid serviceId,
        IExtensionServiceOutputSubscription? subscription)
    {
        ServiceId = IdentityValidation.RequireUuidV7(serviceId, nameof(serviceId));
        if (succeeded && subscription is null)
        {
            throw new ArgumentNullException(nameof(subscription), "A successful result must include a subscription.");
        }

        if (!succeeded && subscription is not null)
        {
            throw new ArgumentException("An unsuccessful result cannot include a subscription.", nameof(subscription));
        }

        Succeeded = succeeded;
        Code = code;
        Subscription = subscription;
    }

    /// <summary>Gets whether the subscription was created.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the stable service-output result category.</summary>
    public ExtensionServiceOutputCode Code { get; }

    /// <summary>Gets the affected service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the subscription handle when the operation succeeded.</summary>
    public IExtensionServiceOutputSubscription? Subscription { get; }
}

/// <summary>Provides stdout/stderr streams and subscriptions for configured services.</summary>
/// <remarks>
/// Any loaded extension may open or subscribe to the live stdout/stderr of any service in the current host
/// configuration. Output content excludes helper protocol markers. Each stream or subscription is bound to
/// the current process instance; a stream reaches end-of-stream and a sink receives completion when that
/// process exits. When the executor is available, a configured service without a live or retained output pump returns
/// <see cref="ExtensionServiceOutputCode.NotRunning"/>. Domain failures are returned through result codes
/// rather than exceptions. Configuration is checked when <see cref="OpenStreamAsync"/> or
/// <see cref="SubscribeAsync"/> is called. An established stream or subscription keeps receiving output from
/// the bound process generation until that generation exits, even if the service is later removed from the
/// configuration. Output produced before <see cref="OpenStreamAsync"/> or <see cref="SubscribeAsync"/> is not
/// replayed. Subscribing moments after the pump completes returns
/// <see cref="ExtensionServiceOutputCode.Opened"/>, followed by immediate
/// <see cref="IExtensionServiceOutputSink.OnCompleted"/>; it does not return
/// <see cref="ExtensionServiceOutputCode.NotRunning"/>.
/// The <see cref="System.IO.Stream"/> from <see cref="OpenStreamAsync"/> is read-only, non-seekable, one
/// per call, and caller-disposed. It may contain silent byte gaps after buffer overflow; use the sink API
/// if gap notification matters.
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

    /// <summary>Subscribes to raw output chunks for one configured running service.</summary>
    /// <param name="serviceId">The configured service identifier.</param>
    /// <param name="stream">The output stream to subscribe to (stdout or stderr).</param>
    /// <param name="sink">The sink receiving chunks and lifecycle notifications.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A safe result containing the subscription handle when subscribed.</returns>
    ValueTask<ExtensionServiceOutputSubscriptionResult> SubscribeAsync(
        Guid serviceId,
        ExtensionServiceOutputStream stream,
        IExtensionServiceOutputSink sink,
        CancellationToken cancellationToken = default);
}
