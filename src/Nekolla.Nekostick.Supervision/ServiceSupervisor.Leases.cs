using System.Threading;

namespace Nekolla.Nekostick.Supervision;

public sealed partial class ServiceSupervisor
{
    /// <summary>Serializes lease renewal with process lifecycle operations.</summary>
    /// <param name="now">The UTC operation instant.</param>
    /// <param name="policy">The lease timing policy, or the documented default.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A fixed-code result with the renewed lease only when it is usable.</returns>
    public async ValueTask<SupervisorOperationResult> RenewLeaseAsync(
        DateTimeOffset now,
        PortLeasePolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(SupervisorOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled, Snapshot);
        }

        try
        {
            return await RenewLeaseCoreAsync(now, policy, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async ValueTask<SupervisorOperationResult> RenewLeaseCoreAsync(
        DateTimeOffset now,
        PortLeasePolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= PortLeasePolicy.Default;
        var current = Volatile.Read(ref lease);
        if (current is null)
        {
            return Result(SupervisorOperationStatus.Unavailable, ServiceStateReasonCode.PortLeaseUnavailable, Snapshot);
        }

        if (current.IsExpired(now))
        {
            Interlocked.CompareExchange(ref lease, null, current);
            return Result(SupervisorOperationStatus.Unavailable, ServiceStateReasonCode.PortLeaseExpired, Snapshot);
        }

        if (!policy.IsRenewalDue(current, now))
        {
            return Result(SupervisorOperationStatus.Applied, ServiceStateReasonCode.None, Snapshot, current);
        }

        PortLeaseOperationResult operation;
        try
        {
            var renewal = new PortLeaseRenewal(
                current.NodeId,
                current.ServiceId,
                current.Port,
                current.Version,
                policy.TimeToLive);
            operation = await leaseStore.ApplyAsync(PortLeaseIntent.Renew(renewal), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.CompareExchange(ref lease, null, current);
            return Result(SupervisorOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled, Snapshot);
        }
        catch
        {
            Interlocked.CompareExchange(ref lease, null, current);
            return Result(SupervisorOperationStatus.Failed, ServiceStateReasonCode.DatabaseUnavailable, Snapshot);
        }

        var renewed = operation.Lease;
        var usable = operation.Status == PortLeaseOperationStatus.Applied &&
            renewed is not null &&
            renewed.NodeId == current.NodeId &&
            renewed.ServiceId == current.ServiceId &&
            renewed.Port == current.Port &&
            !renewed.IsExpired(now);
        if (!usable)
        {
            Interlocked.CompareExchange(ref lease, null, current);
            var reason = operation.Status switch
            {
                PortLeaseOperationStatus.Conflict => ServiceStateReasonCode.PortLeaseConflict,
                PortLeaseOperationStatus.Cancelled => ServiceStateReasonCode.Cancelled,
                PortLeaseOperationStatus.DatabaseUnavailable => ServiceStateReasonCode.DatabaseUnavailable,
                PortLeaseOperationStatus.NotFound => ServiceStateReasonCode.PortLeaseExpired,
                _ => ServiceStateReasonCode.PortLeaseUnavailable
            };
            var status = operation.Status switch
            {
                PortLeaseOperationStatus.Conflict => SupervisorOperationStatus.Conflict,
                PortLeaseOperationStatus.Cancelled => SupervisorOperationStatus.Cancelled,
                PortLeaseOperationStatus.Rejected => SupervisorOperationStatus.Rejected,
                _ => SupervisorOperationStatus.Unavailable
            };
            return Result(status, reason, Snapshot);
        }

        Volatile.Write(ref lease, renewed);
        return Result(SupervisorOperationStatus.Applied, ServiceStateReasonCode.None, Snapshot, renewed);
    }
}
