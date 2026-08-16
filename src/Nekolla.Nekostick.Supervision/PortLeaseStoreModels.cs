namespace Nekolla.Nekostick.Supervision;

/// <summary>Identifies the result of applying a port lease intent.</summary>
public enum PortLeaseOperationStatus
{
    /// <summary>The intent succeeded.</summary>
    Applied,

    /// <summary>The lease conflicted with an existing lease.</summary>
    Conflict,

    /// <summary>The lease was not found.</summary>
    NotFound,

    /// <summary>The persistence gate is unavailable.</summary>
    DatabaseUnavailable,

    /// <summary>The intent was cancelled.</summary>
    Cancelled,

    /// <summary>The intent was rejected by validation or concurrency.</summary>
    Rejected
}

/// <summary>Contains a safe result from a future port lease store.</summary>
public sealed record PortLeaseOperationResult
{
    /// <summary>Creates a port lease operation result.</summary>
    /// <param name="status">The fixed operation status.</param>
    /// <param name="lease">The resulting lease, when available.</param>
    public PortLeaseOperationResult(PortLeaseOperationStatus status, PortLease? lease = null)
    {
        if (status == PortLeaseOperationStatus.Applied && lease is null)
        {
            throw new ArgumentException("An applied lease operation requires a lease.", nameof(lease));
        }

        Lease = lease;
        Status = status;
    }

    /// <summary>Gets the fixed operation status.</summary>
    public PortLeaseOperationStatus Status { get; }

    /// <summary>Gets the resulting lease when the operation applied.</summary>
    public PortLease? Lease { get; }
}

/// <summary>Defines the narrow persistence boundary for port lease intents.</summary>
public interface IPortLeaseStore
{
    /// <summary>Applies one validated lease intent through a future persistence adapter.</summary>
    /// <param name="intent">The immutable operation intent.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A safe operation result without database details.</returns>
    ValueTask<PortLeaseOperationResult> ApplyAsync(
        PortLeaseIntent intent,
        CancellationToken cancellationToken = default);
}
