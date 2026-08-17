using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Persistence;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostPortLeaseStoreAdapterTests
{
    private static readonly Guid ServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000011");

    private static readonly DateTimeOffset AcquiredAt =
        new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FixedAcquirePreservesRequestAndMapsAppliedLease()
    {
        var store = new RecordingStore
        {
            AcquireResult = AppliedPersistenceLease(port: 23456, version: 4)
        };
        var runtime = CreateRuntimeState(accepted: true);
        var adapter = new HostPortLeaseStoreAdapter(store, runtime);
        var request = new PortLeaseRequest(
            new NodeIdentifier("node"),
            ServiceId,
            23456,
            TimeSpan.FromMinutes(1),
            expectedVersion: 3);

        var result = await adapter.ApplyAsync(
            PortLeaseIntent.Acquire(request),
            CancellationToken.None);

        Assert.Equal(PortLeaseOperationStatus.Applied, result.Status);
        Assert.NotNull(result.Lease);
        Assert.Equal(ServiceId, result.Lease!.ServiceId);
        Assert.Equal(23456, result.Lease.Port);
        Assert.Equal(4, result.Lease.Version);
        Assert.NotNull(store.AcquireRequest);
        Assert.Equal("node", store.AcquireRequest!.NodeId);
        Assert.Equal(ServiceId, store.AcquireRequest.ServiceId);
        Assert.Equal(23456, store.AcquireRequest.Port);
        Assert.Equal(TimeSpan.FromMinutes(1), store.AcquireRequest.TimeToLive);
        Assert.Equal(3, store.AcquireRequest.ExpectedVersion);
        Assert.Null(store.AcquireRequest.AutomaticPortRangeStart);
        Assert.Null(store.AcquireRequest.AutomaticPortRangeEnd);
        Assert.True(runtime.Status.DatabaseAvailable);
    }

    [Fact]
    public async Task AutomaticAcquirePreservesRangeBoundsExactly()
    {
        var store = new RecordingStore
        {
            AcquireResult = AppliedPersistenceLease(port: 31004, version: 2)
        };
        var runtime = CreateRuntimeState(accepted: true);
        var adapter = new HostPortLeaseStoreAdapter(store, runtime);
        var request = PortLeaseRequest.Automatic(
            new NodeIdentifier("node"),
            ServiceId,
            TimeSpan.FromSeconds(45),
            rangeStart: 31000,
            rangeEnd: 31010,
            expectedVersion: 8);

        var result = await adapter.ApplyAsync(
            PortLeaseIntent.Acquire(request),
            CancellationToken.None);

        Assert.Equal(PortLeaseOperationStatus.Applied, result.Status);
        Assert.NotNull(store.AcquireRequest);
        Assert.Equal(0, store.AcquireRequest!.Port);
        Assert.Equal(31000, store.AcquireRequest.AutomaticPortRangeStart);
        Assert.Equal(31010, store.AcquireRequest.AutomaticPortRangeEnd);
        Assert.Equal(8, store.AcquireRequest.ExpectedVersion);
    }

    [Fact]
    public async Task DatabaseUnavailableResultTransitionsRuntimeToFailClosed()
    {
        var store = new RecordingStore
        {
            AcquireResult = PersistencePortLeaseOperationResult.Unavailable()
        };
        var runtime = CreateRuntimeState(accepted: true);
        var adapter = new HostPortLeaseStoreAdapter(store, runtime);
        var request = new PortLeaseRequest(
            new NodeIdentifier("node"),
            ServiceId,
            23456,
            TimeSpan.FromMinutes(1));

        var result = await adapter.ApplyAsync(
            PortLeaseIntent.Acquire(request),
            CancellationToken.None);

        Assert.Equal(PortLeaseOperationStatus.DatabaseUnavailable, result.Status);
        Assert.Null(result.Lease);
        Assert.False(runtime.Status.DatabaseAvailable);
        Assert.False(runtime.NewLeasesAllowed);
        Assert.False(runtime.NewServicesAllowed);
    }

    [Fact]
    public async Task PersistenceErrorMapsToUnavailableAndMarksRuntimeUnavailable()
    {
        var store = new RecordingStore
        {
            ThrowError = true
        };
        var runtime = CreateRuntimeState(accepted: true);
        var adapter = new HostPortLeaseStoreAdapter(store, runtime);
        var request = new PortLeaseRequest(
            new NodeIdentifier("node"),
            ServiceId,
            23456,
            TimeSpan.FromMinutes(1));

        var result = await adapter.ApplyAsync(
            PortLeaseIntent.Acquire(request),
            CancellationToken.None);

        Assert.Equal(PortLeaseOperationStatus.DatabaseUnavailable, result.Status);
        Assert.Null(result.Lease);
        Assert.False(runtime.Status.DatabaseAvailable);
        Assert.False(runtime.NewLeasesAllowed);
    }

    [Fact]
    public async Task CancellationMapsToCancelledWithoutChangingRuntimeState()
    {
        var store = new RecordingStore
        {
            ThrowCancellation = true
        };
        var runtime = CreateRuntimeState(accepted: true);
        var adapter = new HostPortLeaseStoreAdapter(store, runtime);
        var request = new PortLeaseRequest(
            new NodeIdentifier("node"),
            ServiceId,
            23456,
            TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await adapter.ApplyAsync(
            PortLeaseIntent.Acquire(request),
            cancellation.Token);

        Assert.Equal(PortLeaseOperationStatus.Cancelled, result.Status);
        Assert.True(runtime.Status.DatabaseAvailable);
        Assert.True(runtime.NewLeasesAllowed);
    }

    [Fact]
    public async Task UnavailableRuntimeRejectsAcquireWithoutCallingPersistence()
    {
        var store = new RecordingStore
        {
            AcquireResult = AppliedPersistenceLease(port: 23456, version: 1)
        };
        var runtime = CreateRuntimeState(accepted: false);
        var adapter = new HostPortLeaseStoreAdapter(store, runtime);
        var request = new PortLeaseRequest(
            new NodeIdentifier("node"),
            ServiceId,
            23456,
            TimeSpan.FromMinutes(1));

        var result = await adapter.ApplyAsync(
            PortLeaseIntent.Acquire(request),
            CancellationToken.None);

        Assert.Equal(PortLeaseOperationStatus.DatabaseUnavailable, result.Status);
        Assert.Null(store.AcquireRequest);
        Assert.False(runtime.Status.DatabaseAvailable);
        Assert.False(runtime.NewLeasesAllowed);
    }

    private static HostRuntimeState CreateRuntimeState(bool accepted)
    {
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(new HostConfigurationSnapshot(
            1,
            new GlobalSettingsConfiguration(version: 1),
            default,
            default,
            default,
            default)));
        var runtime = new HostRuntimeState(
            holder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false));
        if (accepted)
        {
            runtime.MarkSnapshotAccepted();
        }

        return runtime;
    }

    private static PersistencePortLeaseOperationResult AppliedPersistenceLease(int port, long version) =>
        new(
            PersistencePortLeaseOperationStatus.Applied,
            new PersistencePortLease(
                "node",
                ServiceId,
                port,
                AcquiredAt,
                AcquiredAt.AddMinutes(1),
                version));

    private sealed class RecordingStore : IPersistencePortLeaseStore
    {
        public PersistencePortLeaseAcquireRequest? AcquireRequest { get; private set; }
        public PersistencePortLeaseOperationResult AcquireResult { get; init; } = PersistencePortLeaseOperationResult.Unavailable();
        public bool ThrowCancellation { get; init; }
        public bool ThrowError { get; init; }

        public ValueTask<PersistencePortLeaseOperationResult> AcquireAsync(
            PersistencePortLeaseAcquireRequest request,
            CancellationToken cancellationToken = default)
        {
            AcquireRequest = request;
            if (ThrowCancellation)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (ThrowError)
            {
                throw new InvalidOperationException("persistence failure");
            }

            return ValueTask.FromResult(AcquireResult);
        }

        public ValueTask<PersistencePortLeaseOperationResult> RenewAsync(
            PersistencePortLeaseRenewRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PersistencePortLeaseOperationResult.Unavailable());

        public ValueTask<PersistencePortLeaseOperationResult> ReleaseAsync(
            PersistencePortLeaseReleaseRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PersistencePortLeaseOperationResult.Unavailable());

        public ValueTask<PersistencePortLeaseSnapshotResult> ReadActiveAsync(
            string nodeId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new PersistencePortLeaseSnapshotResult(PersistencePortLeaseSnapshotStatus.Available));
    }
}
