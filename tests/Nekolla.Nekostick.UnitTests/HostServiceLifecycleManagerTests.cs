using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Proxy;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Supervision;
using Nekolla.Nekostick.Tests.Fixtures.Extension;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostServiceLifecycleManagerTests
{
    private static readonly Guid EagerServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000021");

    private static readonly Guid LazyServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000022");

    private static readonly Guid DisabledServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000023");
    private static readonly Guid DependencyServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000024");

    private static readonly Guid ConsumerServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000025");

    private static readonly Guid MissingDependencyServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000026");

    private static readonly Guid DisabledDependencyServiceId =
        Guid.Parse("018f0000-0000-7000-8000-000000000027");

    private static readonly Guid CycleServiceAId =
        Guid.Parse("018f0000-0000-7000-8000-000000000028");

    private static readonly Guid CycleServiceBId =
        Guid.Parse("018f0000-0000-7000-8000-000000000029");
    private static readonly string[] ExpectedTemplateArguments =
        ["--dynamic", "35000", "--legacy", "35000"];

    [Fact]
    public async Task ReconcileStartsOnlyEnabledEagerServices()
    {
        var eager = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var lazy = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var disabled = CreateService(DisabledServiceId, ServiceStartMode.Eager, enabled: false);
        var snapshot = CreateSnapshot(eager, lazy, disabled);
        var executor = new RecordingExecutor();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, new RecordingProbe(), publisher, new RecordingLeaseStore());

        await manager.ReconcileAsync(snapshot, CancellationToken.None);

        Assert.Equal(new[] { EagerServiceId }, executor.StartedServices);
        Assert.True(publisher.Current.ContainsKey(EagerServiceId));
        Assert.False(publisher.Current.ContainsKey(LazyServiceId));
        Assert.False(publisher.Current.ContainsKey(DisabledServiceId));
    }

    [Fact]
    public async Task DependencyGraphRefreshPublishesOnlyAfterEveryActiveConsumerIsReady()
    {
        var root = CreateServiceWithLaunch(
            EagerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: ImmutableDictionary<string, string>.Empty.Add("PUBLISHED_VALUE", "version-one"));
        var rootValue = string.Concat("${PUBLISHED_VALUE@", root.Id, "}");
        var dependent = CreateServiceWithLaunch(
            DependencyServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: ImmutableDictionary<string, string>.Empty.Add("DEPENDENCY_VALUE", rootValue));
        var dependentValue = string.Concat("${DEPENDENCY_VALUE@", dependent.Id, "}");
        var consumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: ImmutableDictionary<string, string>.Empty.Add("CONSUMER_VALUE", dependentValue));
        var unusedConsumer = CreateServiceWithLaunch(
            DisabledServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: ImmutableDictionary<string, string>.Empty.Add("UNUSED_VALUE", rootValue));
        var independent = CreateServiceWithLaunch(
            LazyServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: ImmutableDictionary<string, string>.Empty.Add("STABLE_VALUE", "independent"));
        var snapshotV1 = CreateSnapshot(1, root, dependent, consumer, unusedConsumer, independent);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(snapshotV1));
        var runtime = CreateRuntimeState(snapshotV1, holder);
        await using var runtimeRegistry = new HostServiceRuntimeRegistry();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var probe = new StartupBarrierProbe(consumer.Id, blockedCall: 2);
        var executor = new RecordingExecutor();
        var manager = new HostServiceLifecycleManager(
            executor,
            probe,
            new RecordingLeaseStore(),
            holder,
            publisher,
            runtime,
            new HostRuntimeOptions("Host=unit-test", "node", readOnly: false),
            NullLogger<HostServiceLifecycleManager>.Instance,
            new MicroserviceDrainTracker(),
            new HostNodeOptions(skipExtensions: true, disableSupervisor: true, readOnly: false),
            runtimeManager: null,
            runtimeRegistry: runtimeRegistry);

        try
        {
            Assert.Equal(
                HostServiceReadinessStatus.Ready,
                (await manager.EnsureReadyAsync(
                    snapshotV1,
                    consumer.Id,
                    TestContext.Current.CancellationToken)).Status);
            Assert.Equal(
                HostServiceReadinessStatus.Ready,
                (await manager.EnsureReadyAsync(
                    snapshotV1,
                    independent.Id,
                    TestContext.Current.CancellationToken)).Status);
            var previousView = publisher.CommittedView;
            Assert.Equal(4, previousView.Services.Count);
            Assert.Equal(4, previousView.Endpoints.Count);
            var previousRuntime = runtimeRegistry.ReadCurrent().ToArray();

            var rootV2 = CreateServiceWithLaunch(
                root.Id,
                ServiceStartMode.Lazy,
                enabled: true,
                arguments: ImmutableArray<string>.Empty,
                environment: ImmutableDictionary<string, string>.Empty.Add("PUBLISHED_VALUE", "version-two"),
                version: 2);
            var snapshotV2 = CreateSnapshot(2, rootV2, dependent, consumer, unusedConsumer, independent);
            Assert.True(holder.TryReplace(snapshotV2));

            var refresh = manager.EnsureReadyAsync(
                snapshotV2,
                root.Id,
                TestContext.Current.CancellationToken).AsTask();
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Same(previousView, publisher.CommittedView);
            Assert.Equal(
                previousView.Endpoints.OrderBy(static item => item.Key),
                publisher.Current.OrderBy(static item => item.Key));
            Assert.Equal(previousRuntime, runtimeRegistry.ReadCurrent().ToArray());
            Assert.DoesNotContain(unusedConsumer.Id, executor.StartedServices);

            probe.Release();
            Assert.Equal(
                HostServiceReadinessStatus.Ready,
                (await refresh.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Status);

            var committed = publisher.CommittedView;
            Assert.Equal(4, committed.Services.Count);
            Assert.Equal(4, committed.Endpoints.Count);
            Assert.NotEqual(previousView.Services[root.Id].GenerationId, committed.Services[root.Id].GenerationId);
            Assert.NotEqual(previousView.Services[dependent.Id].GenerationId, committed.Services[dependent.Id].GenerationId);
            Assert.NotEqual(previousView.Services[consumer.Id].GenerationId, committed.Services[consumer.Id].GenerationId);
            Assert.Equal(dependent.Version, committed.Services[dependent.Id].ServiceVersion);
            Assert.Equal(previousView.Services[independent.Id].GenerationId, committed.Services[independent.Id].GenerationId);
            Assert.Equal("version-two", committed.Services[dependent.Id].ResolvedEnvironment["DEPENDENCY_VALUE"]);
            Assert.Equal("version-two", committed.Services[consumer.Id].ResolvedEnvironment["CONSUMER_VALUE"]);
            Assert.Equal(consumer.Version, committed.Services[consumer.Id].ServiceVersion);
            Assert.Equal(
                committed.Services[root.Id].GenerationId,
                committed.Services[dependent.Id].DependencyBindings[root.Id].GenerationId);
            Assert.Equal(
                committed.Services[dependent.Id].GenerationId,
                committed.Services[consumer.Id].DependencyBindings[dependent.Id].GenerationId);
            Assert.Equal(7, executor.StartedServices.Count);
            Assert.DoesNotContain(unusedConsumer.Id, executor.StartedServices);

            var runtimeById = runtimeRegistry.ReadCurrent().ToDictionary(static item => item.ServiceId);
            foreach (var service in committed.Services)
            {
                Assert.Equal(service.Value.GenerationId, runtimeById[service.Key].GenerationId);
                Assert.Equal(service.Value.Endpoint, committed.Endpoints[service.Key]);
            }
            var committedRuntime = runtimeRegistry.ReadCurrent().ToArray();
            var committedView = publisher.CommittedView;
            var previousRootRuntime = previousView.Services[root.Id].Runtime;
            var exitTime = DateTimeOffset.UtcNow;
            var staleExit = new HostServiceRuntimeSnapshot(
                previousRootRuntime.ServiceId,
                previousRootRuntime.ConfigurationVersion,
                previousRootRuntime.ProcessId,
                previousRootRuntime.ProcessInstanceId,
                previousRootRuntime.StartedAt,
                exitTime,
                previousRootRuntime.LastHealthAt,
                ExtensionServiceLifecycleState.Failed,
                previousRootRuntime.Health,
                previousRootRuntime.OwnerExtensionId,
                ExtensionServiceFailureStage.ProcessExit,
                ExtensionServiceFailureCode.ProcessExited,
                processExitCode: 17,
                restartCount: previousRootRuntime.RestartCount,
                stateEnteredAt: exitTime,
                generationId: previousRootRuntime.GenerationId);
            runtimeRegistry.Publish(
                staleExit,
                serviceVersion: root.Version,
                enabled: true,
                preserveServiceVersion: true);
            Assert.Equal(committedRuntime, runtimeRegistry.ReadCurrent().ToArray());
            Assert.Same(committedView, publisher.CommittedView);
        }
        finally
        {
            probe.Release();
            await manager.StopAsync(CancellationToken.None);
        }
    }


    [Fact]
    public async Task DependencyIdentityChangeRebindsWithoutRestartWhenLaunchInputsMatch()
    {
        var dependency = CreateServiceWithLaunch(
            EagerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: ImmutableDictionary<string, string>.Empty.Add("PUBLISHED_VALUE", "stable"));
        var remoteValue = string.Concat("${PUBLISHED_VALUE@", dependency.Id, "}");
        var consumer = CreateServiceWithLaunch(
            DependencyServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray.Create("--value", remoteValue),
            environment: ImmutableDictionary<string, string>.Empty.Add("REMOTE_VALUE", remoteValue));
        var snapshot = CreateSnapshot(1, dependency, consumer);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, new RecordingProbe(), publisher, leaseStore);

        try
        {
            Assert.Equal(
                HostServiceReadinessStatus.Ready,
                (await manager.EnsureReadyAsync(
                    snapshot,
                    consumer.Id,
                    TestContext.Current.CancellationToken)).Status);
            var previousView = publisher.CommittedView;
            var previousDependencyGeneration = previousView.Services[dependency.Id].GenerationId;
            var previousConsumerGeneration = previousView.Services[consumer.Id].GenerationId;

            var restart = await manager.RestartAsync(dependency.Id, TestContext.Current.CancellationToken);

            Assert.True(restart.IsSuccess, string.Join(" | ", restart.Errors.Select(error => $"{error.Code}: {error.Message}")));
            var committed = publisher.CommittedView;
            Assert.NotEqual(previousDependencyGeneration, committed.Services[dependency.Id].GenerationId);
            Assert.Equal(previousConsumerGeneration, committed.Services[consumer.Id].GenerationId);
            Assert.Equal(
                committed.Services[dependency.Id].GenerationId,
                committed.Services[consumer.Id].DependencyBindings[dependency.Id].GenerationId);
            Assert.Equal("stable", committed.Services[consumer.Id].ResolvedEnvironment["REMOTE_VALUE"]);
            await manager.PublishVerifiedEndpointsAsync(committed.Endpoints.Values.ToArray());
            Assert.Same(committed, publisher.CommittedView);
            Assert.Equal(committed.Endpoints[consumer.Id], publisher.Current[consumer.Id]);
            Assert.Equal(2, executor.StartedServices.Count(serviceId => serviceId == dependency.Id));
            Assert.Single(executor.StartedServices, serviceId => serviceId == consumer.Id);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ChangedDependencyReferencesRestartConsumerWhenExpandedInputsMatch()
    {
        var firstDependency = CreateServiceWithLaunch(
            EagerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: ImmutableDictionary<string, string>.Empty.Add("PUBLISHED_VALUE", "same-value"));
        var secondDependency = CreateServiceWithLaunch(
            DependencyServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: ImmutableDictionary<string, string>.Empty.Add("PUBLISHED_VALUE", "same-value"));
        var firstValue = string.Concat("${PUBLISHED_VALUE@", firstDependency.Id, "}");
        var consumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray.Create("--value", firstValue),
            environment: ImmutableDictionary<string, string>.Empty.Add("CONSUMER_VALUE", firstValue));
        var snapshotV1 = CreateSnapshot(1, firstDependency, secondDependency, consumer);
        var executor = new RecordingExecutor();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(
            snapshotV1,
            executor,
            new RecordingProbe(),
            publisher,
            new RecordingLeaseStore(),
            out var holder,
            out _);

        try
        {
            Assert.Equal(
                HostServiceReadinessStatus.Ready,
                (await manager.EnsureReadyAsync(
                    snapshotV1,
                    consumer.Id,
                    TestContext.Current.CancellationToken)).Status);
            Assert.Equal(
                HostServiceReadinessStatus.Ready,
                (await manager.EnsureReadyAsync(
                    snapshotV1,
                    secondDependency.Id,
                    TestContext.Current.CancellationToken)).Status);
            var previousView = publisher.CommittedView;
            var previousConsumerGeneration = previousView.Services[consumer.Id].GenerationId;
            var secondValue = string.Concat("${PUBLISHED_VALUE@", secondDependency.Id, "}");
            var consumerWithNewEdge = CreateServiceWithLaunch(
                consumer.Id,
                ServiceStartMode.Lazy,
                enabled: true,
                arguments: ImmutableArray.Create("--value", secondValue),
                environment: ImmutableDictionary<string, string>.Empty.Add("CONSUMER_VALUE", secondValue));
            var snapshotV2 = CreateSnapshot(2, firstDependency, secondDependency, consumerWithNewEdge);
            Assert.True(holder.TryReplace(snapshotV2));

            var readiness = await manager.EnsureReadyAsync(
                snapshotV2,
                consumer.Id,
                TestContext.Current.CancellationToken);

            Assert.Equal(HostServiceReadinessStatus.Ready, readiness.Status);
            var committed = publisher.CommittedView;
            Assert.NotEqual(previousConsumerGeneration, committed.Services[consumer.Id].GenerationId);
            Assert.Equal(consumer.Version, committed.Services[consumer.Id].ServiceVersion);
            Assert.DoesNotContain(firstDependency.Id, committed.Services[consumer.Id].DependencyBindings.Keys);
            Assert.Equal(
                committed.Services[secondDependency.Id].GenerationId,
                committed.Services[consumer.Id].DependencyBindings[secondDependency.Id].GenerationId);
            Assert.Equal("same-value", committed.Services[consumer.Id].ResolvedEnvironment["CONSUMER_VALUE"]);
            Assert.Equal(2, executor.StartedServices.Count(serviceId => serviceId == consumer.Id));
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AutomaticDependencyRestartRefreshesChangedInputsAtTheSameConfigurationVersion()
    {
        var dependency = CreateServiceWithLaunch(
            EagerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: ImmutableDictionary<string, string>.Empty.Add("PUBLISHED_VALUE", "${PORT}"),
            restartPolicy: Nekolla.Nekostick.Contracts.ServiceRestartPolicy.Always);
        var remoteValue = string.Concat("${PUBLISHED_VALUE@", dependency.Id, "}");
        var consumer = CreateServiceWithLaunch(
            DependencyServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: ImmutableDictionary<string, string>.Empty.Add("REMOTE_VALUE", remoteValue));
        var snapshot = CreateSnapshot(1, dependency, consumer);
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var probe = new AutomaticDependencyRestartProbe(dependency.Id);
        var manager = CreateManager(
            snapshot,
            new RecordingExecutor(),
            probe,
            publisher,
            new RecordingLeaseStore());

        try
        {
            Assert.Equal(
                HostServiceReadinessStatus.Ready,
                (await manager.EnsureReadyAsync(
                    snapshot,
                    consumer.Id,
                    TestContext.Current.CancellationToken)).Status);
            var previousView = publisher.CommittedView;
            var previousDependency = previousView.Services[dependency.Id];
            var previousConsumer = previousView.Services[consumer.Id];
            Assert.Equal("35000", previousDependency.ResolvedEnvironment["PUBLISHED_VALUE"]);
            Assert.Equal("35000", previousConsumer.ResolvedEnvironment["REMOTE_VALUE"]);

            var observeReadyHealth = typeof(HostServiceLifecycleManager).GetMethod(
                "ObserveReadyHealthAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(observeReadyHealth);
            for (var failure = 0; failure < 3; failure++)
            {
                var observation = Assert.IsAssignableFrom<Task>(
                    observeReadyHealth!.Invoke(manager, [TestContext.Current.CancellationToken]));
                await observation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }

            await probe.ReplacementHealthy.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            HostServiceCommittedGraphView committed;
            while (true)
            {
                committed = publisher.CommittedView;
                if (committed.Services.TryGetValue(consumer.Id, out var committedConsumer) &&
                    committedConsumer.ResolvedEnvironment.TryGetValue("REMOTE_VALUE", out var remoteValueAfterRestart) &&
                    !string.Equals(remoteValueAfterRestart, previousConsumer.ResolvedEnvironment["REMOTE_VALUE"], StringComparison.Ordinal))
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            }

            var updatedDependency = committed.Services[dependency.Id];
            var updatedConsumer = committed.Services[consumer.Id];
            Assert.Equal(1, committed.ConfigurationVersion);
            Assert.NotEqual(previousDependency.GenerationId, updatedDependency.GenerationId);
            Assert.NotEqual(previousConsumer.GenerationId, updatedConsumer.GenerationId);
            Assert.Equal(
                updatedDependency.GenerationId,
                updatedConsumer.DependencyBindings[dependency.Id].GenerationId);
            Assert.Equal(
                updatedDependency.ResolvedEnvironment["PUBLISHED_VALUE"],
                updatedConsumer.ResolvedEnvironment["REMOTE_VALUE"]);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task OnDemandServiceIsNotAutomaticallyRetriedAfterDatabaseRecovery()
    {
        var service = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out _,
            out var runtime);
        runtime.MarkDatabaseUnavailable();

        var readiness = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.DatabaseUnavailable, readiness.Status);
        Assert.Empty(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);

        runtime.MarkSnapshotAccepted();
        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Empty(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);
        await manager.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(PortLeaseOperationStatus.Rejected, false)]
    [InlineData(PortLeaseOperationStatus.Conflict, false)]
    [InlineData(PortLeaseOperationStatus.Applied, false)]
    public async Task EagerStartupDoesNotAutomaticallyRetryNonDatabaseLeaseFailures(
        PortLeaseOperationStatus status,
        bool validLease)
    {
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        leaseStore.AcquireResults.Enqueue((status, validLease));
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out _,
            out var runtime);

        await manager.ReconcileAsync(snapshot, TestContext.Current.CancellationToken);
        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.True(runtime.NewServicesAllowed);
        Assert.Single(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EagerDependencyAndConsumerRecoverAfterDatabaseUnavailable()
    {
        var dependency = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var consumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Eager,
            enabled: true,
            ImmutableArray.Create(string.Concat("${PORT@", EagerServiceId, "}")),
            ImmutableDictionary<string, string>.Empty);
        var snapshot = CreateSnapshot(dependency, consumer);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        leaseStore.AcquireResults.Enqueue((PortLeaseOperationStatus.DatabaseUnavailable, true));
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out _,
            out var runtime);

        var initial = await manager.EnsureReadyAsync(
            snapshot,
            consumer.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Unavailable, initial.Status);
        Assert.Single(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);
        Assert.True(manager.TryGet(consumer.Id, out var blockedConsumer));
        Assert.Equal(ExtensionServiceFailureCode.DependencyUnavailable, blockedConsumer.FailureCode);
        Assert.Equal("A required startup dependency is unavailable.", blockedConsumer.FailureReason);

        runtime.MarkSnapshotAccepted();
        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, leaseStore.AcquireIntents.Count);
        Assert.Equal(new[] { dependency.Id, consumer.Id }, executor.StartedServices);
        Assert.True(manager.TryGet(dependency.Id, out var readyDependency));
        Assert.Equal(ExtensionServiceLifecycleState.Running, readyDependency.LifecycleState);
        Assert.True(manager.TryGet(consumer.Id, out var readyConsumer));
        Assert.Equal(ExtensionServiceLifecycleState.Running, readyConsumer.LifecycleState);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PermanentDependencyLeaseFailureDoesNotQueueEagerConsumerRetry()
    {
        var dependency = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var consumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Eager,
            enabled: true,
            ImmutableArray.Create(string.Concat("${PORT@", EagerServiceId, "}")),
            ImmutableDictionary<string, string>.Empty);
        var snapshot = CreateSnapshot(dependency, consumer);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        leaseStore.AcquireResults.Enqueue((PortLeaseOperationStatus.Conflict, true));
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out _,
            out _);

        var initial = await manager.EnsureReadyAsync(
            snapshot,
            consumer.Id,
            TestContext.Current.CancellationToken);
        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Unavailable, initial.Status);
        Assert.Single(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);
        Assert.True(manager.TryGet(consumer.Id, out var blockedConsumer));
        Assert.Equal(ExtensionServiceFailureCode.DependencyUnavailable, blockedConsumer.FailureCode);
        Assert.Equal("A required startup dependency is unavailable.", blockedConsumer.FailureReason);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EagerRetryDeadlineClearsAfterPermanentLeaseFailure()
    {
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        leaseStore.AcquireResults.Enqueue((PortLeaseOperationStatus.DatabaseUnavailable, true));
        leaseStore.AcquireResults.Enqueue((PortLeaseOperationStatus.Conflict, true));
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out _,
            out var runtime);

        var initial = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.DatabaseUnavailable, initial.Status);
        Assert.True(manager.TryGet(service.Id, out var queuedRetry));
        Assert.NotNull(queuedRetry.RetryAt);

        runtime.MarkSnapshotAccepted();
        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, leaseStore.AcquireIntents.Count);
        Assert.True(manager.TryGet(service.Id, out var terminalFailure));
        Assert.Equal(ExtensionServiceFailureCode.PortLeaseUnavailable, terminalFailure.FailureCode);
        Assert.Null(terminalFailure.RetryAt);

        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, leaseStore.AcquireIntents.Count);
        Assert.Empty(executor.StartedServices);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConfigurationOnlyGateClosureDoesNotQueueEagerRetry()
    {
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out _,
            out var runtime);
        runtime.MarkSnapshotRejected();

        var initial = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);
        Assert.True(manager.TryGet(service.Id, out var rejectedGate));
        Assert.Null(rejectedGate.RetryAt);
        runtime.MarkSnapshotAccepted();
        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.DatabaseUnavailable, initial.Status);
        Assert.Empty(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);
        await manager.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("disabled")]
    [InlineData("service-version")]
    [InlineData("global-version")]
    [InlineData("start-mode")]
    public async Task PendingEagerRetryIsDiscardedWhenAcceptedConfigurationChanges(string change)
    {
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out var holder,
            out var runtime);
        runtime.MarkDatabaseUnavailable();

        await manager.ReconcileAsync(snapshot, TestContext.Current.CancellationToken);
        Assert.True(manager.TryGet(service.Id, out var queuedRetry));
        Assert.NotNull(queuedRetry.RetryAt);
        Assert.Empty(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);

        runtime.MarkSnapshotAccepted();
        var updated = change switch
        {
            "removed" => CreateSnapshot(2),
            "disabled" => CreateSnapshot(
                CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: false)),
            "service-version" => CreateSnapshot(
                2,
                CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true, version: 2)),
            "global-version" => CreateSnapshot(2, service),
            "start-mode" => CreateSnapshot(
                CreateService(EagerServiceId, ServiceStartMode.Lazy, enabled: true)),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        Assert.True(holder.TryReplace(updated));

        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Empty(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);
        if (change == "removed")
        {
            Assert.False(manager.TryGet(service.Id, out _));
        }
        else
        {
            Assert.True(manager.TryGet(service.Id, out var discardedRetry));
            Assert.Null(discardedRetry.RetryAt);
        }
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OwnedEagerRetryIsDiscardedWhenExtensionIsDisabled()
    {
        const string owner = "pending-eager-owner";
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var loaded = CreateOwnedSnapshot(
            1,
            service,
            owner,
            Nekolla.Nekostick.Contracts.ExtensionLoadState.Loaded);
        var disabled = CreateOwnedSnapshot(
            1,
            service,
            owner,
            Nekolla.Nekostick.Contracts.ExtensionLoadState.Disabled);
        var serviceOwners = ImmutableDictionary<Guid, string?>.Empty.Add(service.Id, owner);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        var manager = CreateManager(
            loaded,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out var holder,
            out var runtime,
            serviceOwners);
        runtime.MarkDatabaseUnavailable();

        await manager.ReconcileAsync(loaded, TestContext.Current.CancellationToken);
        Assert.True(manager.TryGet(service.Id, out var queuedRetry));
        Assert.NotNull(queuedRetry.RetryAt);
        Assert.Empty(leaseStore.AcquireIntents);

        runtime.MarkSnapshotAccepted();
        Assert.Equal(
            SnapshotAdmission.Accepted,
            holder.TryReplace(disabled, dispatchGeneration: null, serviceOwners));
        await manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Empty(leaseStore.AcquireIntents);
        Assert.Empty(executor.StartedServices);
        Assert.True(manager.TryGet(service.Id, out var disabledRuntime));
        Assert.Null(disabledRuntime.RetryAt);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ManualReadinessJoinsEagerRetryWithoutStartingDuplicateProcess()
    {
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor(blockStart: true);
        var leaseStore = new RecordingLeaseStore();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out _,
            out var runtime);
        runtime.MarkDatabaseUnavailable();
        await manager.ReconcileAsync(snapshot, TestContext.Current.CancellationToken);

        Assert.Empty(leaseStore.AcquireIntents);
        runtime.MarkSnapshotAccepted();
        var retry = manager.RetryWaitingServicesAsync(
            DateTimeOffset.MaxValue,
            TestContext.Current.CancellationToken);
        await executor.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var manual = manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken).AsTask();
        Assert.False(manual.IsCompleted);

        executor.ReleaseStart();
        await retry.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var readiness = await manual;

        Assert.Equal(HostServiceReadinessStatus.Ready, readiness.Status);
        Assert.Single(leaseStore.AcquireIntents);
        Assert.Equal(new[] { EagerServiceId }, executor.StartedServices);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LaunchTemplatesResolveDynamicLegacyAndOwnEnvironmentValues()
    {
        var service = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ["--dynamic", "${PORT}", "--legacy", "$PORT"],
            environment: ImmutableDictionary<string, string>.Empty.Add("SELF", "self-value"));
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            new RecordingLeaseStore());

        var readiness = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Ready, readiness.Status);
        var specification = Assert.Single(executor.StartedSpecifications);
        Assert.Equal(ExpectedTemplateArguments, specification.Arguments);
        Assert.Equal("self-value", specification.Environment.Values["SELF"]);
    }

    [Fact]
    public async Task RemoteTemplateResolvesPublishedDependencyEnvironment()
    {
        var dependency = CreateServiceWithLaunch(
            DependencyServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: ImmutableDictionary<string, string>.Empty.Add("PUBLISHED_VALUE", "ready-dependency"));
        var remoteValue = string.Concat("${PUBLISHED_VALUE@", dependency.Id, "}");
        var consumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray.Create("--value", remoteValue),
            environment: ImmutableDictionary<string, string>.Empty
                .Add("REMOTE_PORT", string.Concat("${PORT@", DependencyServiceId, "}"))
                .Add("REMOTE_VALUE", remoteValue));
        var snapshot = CreateSnapshot(dependency, consumer);
        var executor = new RecordingExecutor();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            new RecordingLeaseStore());

        Assert.Equal(
            HostServiceReadinessStatus.Ready,
            (await manager.EnsureReadyAsync(snapshot, dependency.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(
            HostServiceReadinessStatus.Ready,
            (await manager.EnsureReadyAsync(snapshot, consumer.Id, TestContext.Current.CancellationToken)).Status);

        var specification = Assert.Single(
            executor.StartedSpecifications,
            value => value.ServiceId == consumer.Id);
        Assert.Equal(ImmutableArray.Create("--value", "ready-dependency"), specification.Arguments);
        Assert.Equal("35000", specification.Environment.Values["REMOTE_PORT"]);
        Assert.Equal("ready-dependency", specification.Environment.Values["REMOTE_VALUE"]);
    }

    [Fact]
    public async Task WaitingDependencyDoesNotBypassReadinessThroughEnvironmentEntry()
    {
        var dependency = CreateServiceWithLaunch(
            DependencyServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: ImmutableDictionary<string, string>.Empty,
            fileName: System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"nekostick-missing-{Guid.NewGuid():N}"));
        var consumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: [string.Concat("${PORT@", dependency.Id, "}")],
            environment: ImmutableDictionary<string, string>.Empty);
        var snapshot = CreateSnapshot(dependency, consumer);
        var executor = new RecordingExecutor();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            new RecordingLeaseStore());

        var dependencyReadiness = await manager.EnsureReadyAsync(
            snapshot,
            dependency.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Unavailable, dependencyReadiness.Status);
        Assert.True(manager.TryGet(dependency.Id, out var waitingDependency));
        Assert.Equal(ExtensionServiceLifecycleState.Waiting, waitingDependency.LifecycleState);

        var consumerReadiness = await manager.EnsureReadyAsync(
            snapshot,
            consumer.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Unavailable, consumerReadiness.Status);
        Assert.DoesNotContain(consumer.Id, executor.StartedServices);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StaleEnvironmentWaitsForConfiguredDependencyGeneration()
    {
        var dependencyV1 = CreateServiceWithLaunch(
            DependencyServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: ImmutableDictionary<string, string>.Empty.Add("PUBLISHED_VALUE", "version-one"));
        var remoteValue = string.Concat("${PUBLISHED_VALUE@", dependencyV1.Id, "}");
        var consumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray.Create("--value", remoteValue),
            environment: ImmutableDictionary<string, string>.Empty
                .Add("REMOTE_VALUE", remoteValue)
                .Add("REMOTE_PORT", string.Concat("${PORT@", dependencyV1.Id, "}")));
        var snapshotV1 = CreateSnapshot(1, dependencyV1, consumer);
        var probe = new StartupBarrierProbe(dependencyV1.Id, blockedCall: 2);
        var executor = new RecordingExecutor();
        var manager = CreateManager(
            snapshotV1,
            executor,
            probe,
            new HostServiceEndpointSnapshotPublisher(),
            new RecordingLeaseStore(),
            out var holder,
            out _);

        Assert.Equal(
            HostServiceReadinessStatus.Ready,
            (await manager.EnsureReadyAsync(snapshotV1, dependencyV1.Id, TestContext.Current.CancellationToken)).Status);
        var dependencyV2 = CreateServiceWithLaunch(
            DependencyServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: ImmutableDictionary<string, string>.Empty.Add("PUBLISHED_VALUE", "version-two"),
            version: 2);
        var snapshotV2 = CreateSnapshot(2, dependencyV2, consumer);
        Assert.True(holder.TryReplace(snapshotV2));

        var consumerStartup = manager.EnsureReadyAsync(
            snapshotV2,
            consumer.Id,
            TestContext.Current.CancellationToken).AsTask();
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(consumerStartup.IsCompleted);
        Assert.DoesNotContain(consumer.Id, executor.StartedServices);

        probe.Release();
        var readiness = await consumerStartup.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Ready, readiness.Status);
        var specification = Assert.Single(
            executor.StartedSpecifications,
            value => value.ServiceId == consumer.Id);
        Assert.Equal(ImmutableArray.Create("--value", "version-two"), specification.Arguments);
        Assert.Equal("version-two", specification.Environment.Values["REMOTE_VALUE"]);
        Assert.Equal("35001", specification.Environment.Values["REMOTE_PORT"]);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ManualDependencyRestartCancelsAndRebuildsAffectedGraph()
    {
        var dependencyV1 = CreateServiceWithLaunch(
            DependencyServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: ImmutableDictionary<string, string>.Empty.Add("PUBLISHED_VALUE", "version-one"));
        var remoteValue = string.Concat("${PUBLISHED_VALUE@", dependencyV1.Id, "}");
        var consumerV1 = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray.Create("--value", remoteValue),
            environment: ImmutableDictionary<string, string>.Empty.Add("REMOTE_VALUE", remoteValue));
        var snapshotV1 = CreateSnapshot(1, dependencyV1, consumerV1);
        var probe = new StartupBarrierProbe(consumerV1.Id, blockedCall: 2);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        var manager = CreateManager(
            snapshotV1,
            executor,
            probe,
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out var holder,
            out _);

        Assert.Equal(
            HostServiceReadinessStatus.Ready,
            (await manager.EnsureReadyAsync(snapshotV1, dependencyV1.Id, TestContext.Current.CancellationToken)).Status);
        var originalDependencyLease = Assert.Single(
            leaseStore.HeldLeases,
            value => value.ServiceId == dependencyV1.Id);
        Assert.Equal(
            HostServiceReadinessStatus.Ready,
            (await manager.EnsureReadyAsync(snapshotV1, consumerV1.Id, TestContext.Current.CancellationToken)).Status);
        Assert.True(manager.TryGet(consumerV1.Id, out var originalConsumer));
        var originalProcessInstance = originalConsumer.ProcessInstanceId;
        var originalConsumerLease = Assert.Single(
            leaseStore.HeldLeases,
            value => value.ServiceId == consumerV1.Id);
        var consumerV2 = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray.Create("--value", remoteValue),
            environment: ImmutableDictionary<string, string>.Empty.Add("REMOTE_VALUE", remoteValue),
            version: 2);
        var consumerWarmupSnapshot = CreateSnapshot(2, dependencyV1, consumerV2);
        Assert.True(holder.TryReplace(consumerWarmupSnapshot));

        var consumerStartup = manager.EnsureReadyAsync(
            consumerWarmupSnapshot,
            consumerV2.Id,
            TestContext.Current.CancellationToken).AsTask();
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var dependencyRestart = await manager.RestartAsync(
            dependencyV1.Id,
            TestContext.Current.CancellationToken);
        Assert.True(dependencyRestart.IsSuccess);
        var restartedDependencyLease = Assert.Single(
            leaseStore.HeldLeases,
            value => value.ServiceId == dependencyV1.Id);
        Assert.NotEqual(originalDependencyLease.GenerationId, restartedDependencyLease.GenerationId);

        probe.Release();
        var consumerReadiness = await consumerStartup.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Unavailable, consumerReadiness.Status);
        Assert.True(manager.TryGet(consumerV2.Id, out var consumerState));
        Assert.NotEqual(originalProcessInstance, consumerState.ProcessInstanceId);
        Assert.Equal(ExtensionServiceFailureCode.None, consumerState.FailureCode);
        var heldConsumerLease = Assert.Single(
            leaseStore.HeldLeases,
            value => value.ServiceId == consumerV2.Id);
        Assert.NotEqual(originalConsumerLease.GenerationId, heldConsumerLease.GenerationId);
        await manager.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("disabled")]
    public async Task WaitingConsumerDoesNotRetryWithStaleDependencyBinding(string change)
    {
        var dependencyEnvironment = ImmutableDictionary<string, string>.Empty
            .Add("PUBLISHED_VALUE", "ready-dependency");
        var dependency = CreateServiceWithLaunch(
            DependencyServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: dependencyEnvironment);
        var missingExecutable = System.IO.Path.Combine("/tmp", $"nekostick-missing-{Guid.NewGuid():N}");
        var remoteValue = string.Concat("${PUBLISHED_VALUE@", dependency.Id, "}");
        var consumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray.Create("--value", remoteValue),
            environment: ImmutableDictionary<string, string>.Empty
                .Add("REMOTE_PORT", string.Concat("${PORT@", dependency.Id, "}"))
                .Add("REMOTE_VALUE", remoteValue),
            fileName: missingExecutable);
        var snapshot = CreateSnapshot(dependency, consumer);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            leaseStore,
            out var holder,
            out _);

        try
        {
            Assert.Equal(
                HostServiceReadinessStatus.Ready,
                (await manager.EnsureReadyAsync(snapshot, dependency.Id, TestContext.Current.CancellationToken)).Status);
            Assert.Equal(
                HostServiceReadinessStatus.Unavailable,
                (await manager.EnsureReadyAsync(snapshot, consumer.Id, TestContext.Current.CancellationToken)).Status);
            Assert.True(manager.TryGet(consumer.Id, out var waitingConsumer));
            Assert.Equal(ExtensionServiceLifecycleState.Waiting, waitingConsumer.LifecycleState);
            Assert.DoesNotContain(consumer.Id, executor.StartedServices);
            System.IO.File.WriteAllText(missingExecutable, string.Empty);

            if (change == "generation")
            {
                var dependencyRestart = await manager.RestartAsync(
                    dependency.Id,
                    TestContext.Current.CancellationToken);
                Assert.True(dependencyRestart.IsSuccess);
            }
            else
            {
                var disabledDependency = CreateServiceWithLaunch(
                    DependencyServiceId,
                    ServiceStartMode.Lazy,
                    enabled: false,
                    arguments: ImmutableArray<string>.Empty,
                    environment: dependencyEnvironment);
                Assert.True(holder.TryReplace(CreateSnapshot(2, disabledDependency, consumer)));
            }

            var acquireCount = leaseStore.AcquireIntents.Count;
            await manager.RetryWaitingServicesAsync(
                DateTimeOffset.MaxValue,
                TestContext.Current.CancellationToken);

            Assert.Equal(acquireCount, leaseStore.AcquireIntents.Count);
            Assert.DoesNotContain(consumer.Id, executor.StartedServices);
            Assert.True(manager.TryGet(consumer.Id, out var unavailableConsumer));
            Assert.Equal(ExtensionServiceFailureCode.DependencyUnavailable, unavailableConsumer.FailureCode);
            Assert.Single(leaseStore.HeldLeases, value => value.ServiceId == dependency.Id);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
            if (System.IO.File.Exists(missingExecutable))
            {
                System.IO.File.Delete(missingExecutable);
            }
        }
    }

    [Fact]
    public async Task WaitingConsumerRetriesToReadyWhileDependencyBindingIsUnchanged()
    {
        var dependencyEnvironment = ImmutableDictionary<string, string>.Empty
            .Add("PUBLISHED_VALUE", "ready-dependency");
        var dependency = CreateServiceWithLaunch(
            DependencyServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: dependencyEnvironment);
        var missingExecutable = System.IO.Path.Combine("/tmp", $"nekostick-missing-{Guid.NewGuid():N}");
        var remoteValue = string.Concat("${PUBLISHED_VALUE@", dependency.Id, "}");
        var consumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray.Create("--value", remoteValue),
            environment: ImmutableDictionary<string, string>.Empty
                .Add("REMOTE_PORT", string.Concat("${PORT@", dependency.Id, "}"))
                .Add("REMOTE_VALUE", remoteValue),
            fileName: missingExecutable);
        var snapshot = CreateSnapshot(dependency, consumer);
        var executor = new RecordingExecutor();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            new RecordingLeaseStore());

        try
        {
            Assert.Equal(
                HostServiceReadinessStatus.Ready,
                (await manager.EnsureReadyAsync(snapshot, dependency.Id, TestContext.Current.CancellationToken)).Status);
            Assert.Equal(
                HostServiceReadinessStatus.Unavailable,
                (await manager.EnsureReadyAsync(snapshot, consumer.Id, TestContext.Current.CancellationToken)).Status);
            System.IO.File.WriteAllText(missingExecutable, string.Empty);

            await manager.RetryWaitingServicesAsync(
                DateTimeOffset.MaxValue,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                HostServiceReadinessStatus.Ready,
                (await manager.EnsureReadyAsync(snapshot, consumer.Id, TestContext.Current.CancellationToken)).Status);
            var specification = Assert.Single(
                executor.StartedSpecifications,
                value => value.ServiceId == consumer.Id);
            Assert.Equal(ImmutableArray.Create("--value", "ready-dependency"), specification.Arguments);
            Assert.Equal("35000", specification.Environment.Values["REMOTE_PORT"]);
            Assert.Equal("ready-dependency", specification.Environment.Values["REMOTE_VALUE"]);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
            if (System.IO.File.Exists(missingExecutable))
            {
                System.IO.File.Delete(missingExecutable);
            }
        }
    }

    [Fact]
    public async Task WaitingConsumerRetryDoesNotPromoteAfterDependencyGenerationChangesDuringHealthCheck()
    {
        var dependencyEnvironment = ImmutableDictionary<string, string>.Empty
            .Add("PUBLISHED_VALUE", "ready-dependency");
        var dependency = CreateServiceWithLaunch(
            DependencyServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray<string>.Empty,
            environment: dependencyEnvironment);
        var missingExecutable = System.IO.Path.Combine("/tmp", $"nekostick-missing-{Guid.NewGuid():N}");
        var remoteValue = string.Concat("${PUBLISHED_VALUE@", dependency.Id, "}");
        var consumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Lazy,
            enabled: true,
            arguments: ImmutableArray.Create("--value", remoteValue),
            environment: ImmutableDictionary<string, string>.Empty
                .Add("REMOTE_PORT", string.Concat("${PORT@", dependency.Id, "}"))
                .Add("REMOTE_VALUE", remoteValue),
            fileName: missingExecutable);
        var snapshot = CreateSnapshot(dependency, consumer);
        var probe = new StartupBarrierProbe(consumer.Id, blockedCall: 1);
        var executor = new RecordingExecutor();
        var leaseStore = new RecordingLeaseStore();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, probe, publisher, leaseStore);
        Task? retry = null;

        try
        {
            Assert.Equal(
                HostServiceReadinessStatus.Ready,
                (await manager.EnsureReadyAsync(snapshot, dependency.Id, TestContext.Current.CancellationToken)).Status);
            Assert.Equal(
                HostServiceReadinessStatus.Unavailable,
                (await manager.EnsureReadyAsync(snapshot, consumer.Id, TestContext.Current.CancellationToken)).Status);
            var originalDependencyLease = Assert.Single(
                leaseStore.HeldLeases,
                value => value.ServiceId == dependency.Id);
            System.IO.File.WriteAllText(missingExecutable, string.Empty);

            retry = manager.RetryWaitingServicesAsync(
                DateTimeOffset.MaxValue,
                TestContext.Current.CancellationToken);
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var dependencyRestart = await manager.RestartAsync(
                dependency.Id,
                TestContext.Current.CancellationToken);
            Assert.True(dependencyRestart.IsSuccess);
            var restartedDependencyLease = Assert.Single(
                leaseStore.HeldLeases,
                value => value.ServiceId == dependency.Id);
            Assert.NotEqual(originalDependencyLease.GenerationId, restartedDependencyLease.GenerationId);
            probe.Release();
            await retry.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.True(manager.TryGet(consumer.Id, out var consumerState));
            Assert.Equal(ExtensionServiceFailureCode.DependencyUnavailable, consumerState.FailureCode);
            Assert.DoesNotContain(consumer.Id, publisher.Current.Keys);
            Assert.DoesNotContain(leaseStore.HeldLeases, value => value.ServiceId == consumer.Id);
            Assert.Contains(consumer.Id, executor.StoppedServices);
        }
        finally
        {
            probe.Release();
            if (retry is not null)
            {
                await retry;
            }
            await manager.StopAsync(CancellationToken.None);
            if (System.IO.File.Exists(missingExecutable))
            {
                System.IO.File.Delete(missingExecutable);
            }
        }
    }

    [Fact]
    public async Task ReconcileStartsEagerDependenciesBeforeConsumers()
    {
        var dependency = CreateService(DependencyServiceId, ServiceStartMode.Eager, enabled: true);
        var consumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Eager,
            enabled: true,
            arguments: [string.Concat("${PORT@", DependencyServiceId, "}")],
            environment: ImmutableDictionary<string, string>.Empty);
        var snapshot = CreateSnapshot(dependency, consumer);
        var executor = new RecordingExecutor();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            new RecordingLeaseStore());

        await manager.ReconcileAsync(snapshot, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { DependencyServiceId, ConsumerServiceId }, executor.StartedServices);
        var consumerSpecification = Assert.Single(
            executor.StartedSpecifications,
            value => value.ServiceId == ConsumerServiceId);
        Assert.Equal("35000", consumerSpecification.Arguments[0]);
    }

    [Fact]
    public async Task UnsatisfiedMissingOrDisabledDependencyLeavesOtherEagerServicesRunning()
    {
        var healthy = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var missing = CreateServiceWithLaunch(
            MissingDependencyServiceId,
            ServiceStartMode.Eager,
            enabled: true,
            arguments: [string.Concat("${PORT@", Guid.Parse("018f0000-0000-7000-8000-000000000030"), "}")],
            environment: ImmutableDictionary<string, string>.Empty);
        var disabledDependency = CreateService(DisabledDependencyServiceId, ServiceStartMode.Eager, enabled: false);
        var disabledConsumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Eager,
            enabled: true,
            arguments: [string.Concat("${PORT@", DisabledDependencyServiceId, "}")],
            environment: ImmutableDictionary<string, string>.Empty);
        var snapshot = CreateSnapshot(healthy, missing, disabledDependency, disabledConsumer);
        var executor = new RecordingExecutor();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            publisher,
            new RecordingLeaseStore());

        await manager.ReconcileAsync(snapshot, TestContext.Current.CancellationToken);

        Assert.Contains(EagerServiceId, executor.StartedServices);
        Assert.DoesNotContain(MissingDependencyServiceId, executor.StartedServices);
        Assert.DoesNotContain(ConsumerServiceId, executor.StartedServices);
        Assert.True(publisher.Current.ContainsKey(EagerServiceId));
    }

    [Fact]
    public async Task ReconcileContinuesAfterFailedGraphRefreshAtSameVersion()
    {
        var dependency = CreateService(DependencyServiceId, ServiceStartMode.Eager, enabled: true);
        var consumer = CreateServiceWithLaunch(
            ConsumerServiceId,
            ServiceStartMode.Eager,
            enabled: true,
            arguments: [string.Concat("${PORT@", DependencyServiceId, "}")],
            environment: ImmutableDictionary<string, string>.Empty);
        var initial = CreateSnapshot(1, dependency, consumer);
        var unrelated = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var updated = CreateSnapshot(2, consumer, unrelated);
        var executor = new RecordingExecutor();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(
            initial,
            executor,
            new RecordingProbe(),
            publisher,
            new RecordingLeaseStore(),
            out var holder,
            out _);

        await manager.ReconcileAsync(initial, TestContext.Current.CancellationToken);
        Assert.Contains(DependencyServiceId, executor.StartedServices);
        Assert.Contains(ConsumerServiceId, executor.StartedServices);

        Assert.True(holder.TryReplace(updated));
        await manager.ReconcileAsync(updated, TestContext.Current.CancellationToken);

        Assert.Contains(DependencyServiceId, executor.StoppedServices);
        Assert.Contains(EagerServiceId, executor.StartedServices);
        Assert.Equal(1, executor.StartedServices.Count(value => value == EagerServiceId));
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EagerDependencyCycleFailsWithoutHanging()
    {
        var serviceA = CreateServiceWithLaunch(
            CycleServiceAId,
            ServiceStartMode.Eager,
            enabled: true,
            arguments: [string.Concat("${PORT@", CycleServiceBId, "}")],
            environment: ImmutableDictionary<string, string>.Empty);
        var serviceB = CreateServiceWithLaunch(
            CycleServiceBId,
            ServiceStartMode.Eager,
            enabled: true,
            arguments: [string.Concat("${PORT@", CycleServiceAId, "}")],
            environment: ImmutableDictionary<string, string>.Empty);
        var snapshot = CreateSnapshot(serviceA, serviceB);
        var executor = new RecordingExecutor();
        var manager = CreateManager(
            snapshot,
            executor,
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            new RecordingLeaseStore());

        await manager.ReconcileAsync(snapshot, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Empty(executor.StartedServices);
    }

    [Fact]
    public async Task ReadCurrentReturnsSnapshotsForAnyActiveServiceCount()
    {
        var eager = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(eager);
        var manager = CreateManager(
            snapshot,
            new RecordingExecutor(),
            new RecordingProbe(),
            new HostServiceEndpointSnapshotPublisher(),
            new RecordingLeaseStore());

        await manager.ReconcileAsync(snapshot, CancellationToken.None);

        // A single active service leaves Count != Capacity in the builder;
        // MoveToImmutable would throw here.
        var current = manager.ReadCurrent();
        var entry = Assert.Single(current);
        Assert.Equal(EagerServiceId, entry.ServiceId);
    }
    [Fact]
    public async Task DisabledOwnerServicesStayOutOfReconcileUntilTheOwnerIsEnabled()
    {
        const string owner = "disabled.owner";
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var loadedSnapshot = CreateOwnedSnapshot(
            1,
            service,
            owner,
            Contracts.ExtensionLoadState.Loaded);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.Equal(SnapshotAdmission.Accepted, holder.TryReplace(
            loadedSnapshot,
            dispatchGeneration: null,
            ImmutableDictionary<Guid, string?>.Empty.Add(service.Id, owner)));
        var runtime = CreateRuntimeState(loadedSnapshot, holder);
        var executor = new RecordingExecutor();
        var endpointPublisher = new HostServiceEndpointSnapshotPublisher();
        var manager = new HostServiceLifecycleManager(
            executor,
            new RecordingProbe(),
            new RecordingLeaseStore(),
            holder,
            endpointPublisher,
            runtime,
            new HostRuntimeOptions("Host=unit-test", "node", readOnly: false),
            NullLogger<HostServiceLifecycleManager>.Instance,
            new MicroserviceDrainTracker(),
            new HostNodeOptions(skipExtensions: true, disableSupervisor: true, readOnly: false));

        await manager.ReconcileAsync(loadedSnapshot, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { service.Id }, executor.StartedServices);
        Assert.True(endpointPublisher.Current.ContainsKey(service.Id));

        var disabledSnapshot = CreateOwnedSnapshot(
            2,
            service,
            owner,
            Contracts.ExtensionLoadState.Disabled);
        Assert.Equal(SnapshotAdmission.Accepted, holder.TryReplace(
            disabledSnapshot,
            dispatchGeneration: null,
            ImmutableDictionary<Guid, string?>.Empty.Add(service.Id, owner)));
        await manager.ReconcileAsync(disabledSnapshot, TestContext.Current.CancellationToken);
        Assert.Contains(service.Id, executor.StoppedServices);
        Assert.False(endpointPublisher.Current.ContainsKey(service.Id));
        var disabledReadiness = await manager.EnsureReadyAsync(
            disabledSnapshot,
            service.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Disabled, disabledReadiness.Status);

        var enabledSnapshot = CreateOwnedSnapshot(
            3,
            service,
            owner,
            Contracts.ExtensionLoadState.Loaded);
        Assert.Equal(SnapshotAdmission.Accepted, holder.TryReplace(
            enabledSnapshot,
            dispatchGeneration: null,
            ImmutableDictionary<Guid, string?>.Empty.Add(service.Id, owner)));
        await manager.ReconcileAsync(enabledSnapshot, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { service.Id, service.Id }, executor.StartedServices);
    }


    [Fact]
    public async Task LazyServiceStartsOnlyOnEnsureReadyAndCoalescesConcurrentStarts()
    {
        var service = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor(blockStart: true);
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, new RecordingProbe(), publisher, new RecordingLeaseStore());

        var first = manager.EnsureReadyAsync(snapshot, service.Id, TestContext.Current.CancellationToken).AsTask();
        await executor.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var waiterCancellation = new CancellationTokenSource();
        var second = manager.EnsureReadyAsync(snapshot, service.Id, waiterCancellation.Token).AsTask();

        Assert.False(second.IsCompleted);
        waiterCancellation.Cancel();
        var cancelledWaiter = await second;
        Assert.Equal(HostServiceReadinessStatus.Cancelled, cancelledWaiter.Status);
        Assert.False(first.IsCompleted);

        executor.ReleaseStart();
        var readiness = await first;

        Assert.Equal(HostServiceReadinessStatus.Ready, readiness.Status);
        Assert.Equal(new[] { LazyServiceId }, executor.StartedServices);
        Assert.Single(publisher.Current);
    }

    [Fact]
    public async Task EndpointPublishesOnlyAfterHealthSucceeds()
    {
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var probe = new ControlledProbe();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, new RecordingExecutor(), probe, publisher, new RecordingLeaseStore());

        var readiness = manager.EnsureReadyAsync(snapshot, service.Id, TestContext.Current.CancellationToken).AsTask();
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(readiness.IsCompleted);
        Assert.Empty(publisher.Current);

        probe.Complete(HealthObservationStatus.Healthy);
        var result = await readiness;

        Assert.Equal(HostServiceReadinessStatus.Ready, result.Status);
        Assert.True(publisher.Current.ContainsKey(service.Id));
    }
    [Fact]
    public async Task PublishVerifiedEndpointsFiltersByReadyServiceGenerationAndPort()
    {
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var leaseStore = new RecordingLeaseStore();
        var manager = CreateManager(snapshot, new RecordingExecutor(), new RecordingProbe(), publisher, leaseStore);

        var readiness = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Ready, readiness.Status);

        var readyLease = Assert.Single(leaseStore.HeldLeases);
        publisher.Publish(Array.Empty<HostServiceEndpointLease>());
        await manager.PublishVerifiedEndpointsAsync(
        [
            new HostServiceEndpointLease(service.Id, readyLease.GenerationId, readyLease.Port + 1, readyLease.ExpiresAt),
            new HostServiceEndpointLease(
                service.Id,
                Guid.Parse("018f0000-0000-7000-8000-000000000099"),
                readyLease.Port,
                readyLease.ExpiresAt)
        ]);
        Assert.Empty(publisher.Current);

        var matchingLease = new HostServiceEndpointLease(
            service.Id,
            readyLease.GenerationId,
            readyLease.Port,
            readyLease.ExpiresAt);
        await manager.PublishVerifiedEndpointsAsync([matchingLease]);
        Assert.Equal(matchingLease, publisher.Current[service.Id]);

        await manager.StopAsync(CancellationToken.None);
        await manager.PublishVerifiedEndpointsAsync([matchingLease]);
        Assert.Empty(publisher.Current);
    }

    [Fact]
    public async Task ServiceAndEndpointTransitionsReachServingExtensionCoreEventQueue()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifestResult = ExtensionManifestDiscovery.Discover(fixture.RootPath);
        Assert.True(manifestResult.Succeeded, manifestResult.FailureCode.ToString());
        var manifest = manifestResult.Manifest!;
        await using var extensions = new ExtensionRuntimeManager(HostApiVersion.Current);
        var settings = new ExtensionSettingsConfiguration(
            manifest.Id,
            1,
            JsonSerializer.Serialize(new { publishCoreEvents = true, eventCount = 5 }),
            1);
        Assert.True((await extensions.LoadAsync(
            manifest,
            settings,
            TestContext.Current.CancellationToken)).Succeeded);

        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(snapshot));
        var runtimeState = CreateRuntimeState(snapshot, holder);
        var endpointPublisher = new HostServiceEndpointSnapshotPublisher(extensions);
        var manager = new HostServiceLifecycleManager(
            new RecordingExecutor(),
            new RecordingProbe(),
            new RecordingLeaseStore(),
            holder,
            endpointPublisher,
            runtimeState,
            new HostRuntimeOptions("Host=unit-test", "node", readOnly: false),
            NullLogger<HostServiceLifecycleManager>.Instance,
            new MicroserviceDrainTracker(),
            new HostNodeOptions(skipExtensions: true, disableSupervisor: true, readOnly: false),
            extensions);

        var ready = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Ready, ready.Status);
        await manager.StopAsync(CancellationToken.None);

        var result = await extensions.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/lifecycle-events"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, result.State);
        var body = Encoding.UTF8.GetString(result.Response!.Body.AsSpan());
        Assert.Contains("\"state\":\"Loaded\"", body, StringComparison.Ordinal);
        Assert.Contains($"\"serviceId\":\"{service.Id}\"", body, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"ready\"", body, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"stopped\"", body, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"withdrawn\"", body, StringComparison.Ordinal);
    }


    [Fact]
    public async Task FailedRenewalKeepsEndpointAndDisablesNewServices()
    {
        var service = CreateService(EagerServiceId, ServiceStartMode.Eager, enabled: true);
        var snapshot = CreateSnapshot(service);
        var leaseStore = new RecordingLeaseStore
        {
            AcquireLifetime = TimeSpan.FromSeconds(5)
        };
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var holder = new HostConfigurationSnapshotHolder();
        Assert.True(holder.TryReplace(snapshot));
        var runtime = CreateRuntimeState(snapshot, holder);
        var manager = new HostServiceLifecycleManager(
            new RecordingExecutor(),
            new RecordingProbe(),
            leaseStore,
            holder,
            publisher,
            runtime,
            new HostRuntimeOptions("Host=unit-test", "node", readOnly: false),
            NullLogger<HostServiceLifecycleManager>.Instance,
            new MicroserviceDrainTracker(),
            new HostNodeOptions(skipExtensions: true, disableSupervisor: true, readOnly: false));

        var ready = await manager.EnsureReadyAsync(snapshot, service.Id, TestContext.Current.CancellationToken);
        Assert.Equal(HostServiceReadinessStatus.Ready, ready.Status);
        Assert.True(publisher.Current.ContainsKey(service.Id));

        leaseStore.FailRenewal = true;
        await manager.RenewLeasesAsync(CancellationToken.None);

        Assert.True(publisher.Current.ContainsKey(service.Id));
        Assert.False(runtime.Status.DatabaseAvailable);
        Assert.False(runtime.NewServicesAllowed);
        var endpoint = await new HostServiceEndpointResolver(publisher).ResolveAsync(
            service.Id,
            TestContext.Current.CancellationToken);
        Assert.True(endpoint.IsAvailable);
    }
    [Fact]
    public async Task StopAsyncQuiescesBlockedAutomaticStartupBeforeExecutorCleanup()
    {
        var service = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var snapshot = CreateSnapshot(service);
        var executor = new RecordingExecutor(blockStart: true, ignoreStartCancellation: true);
        var leaseStore = new RecordingLeaseStore();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, new RecordingProbe(), publisher, leaseStore);

        var startup = manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken).AsTask();
        await executor.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var stopping = manager.StopAsync(CancellationToken.None);
        Assert.False(stopping.IsCompleted);

        executor.ReleaseStart();
        await stopping;
        var readiness = await startup;

        Assert.Equal(HostServiceReadinessStatus.Cancelled, readiness.Status);
        Assert.Empty(executor.AcceptedServices);
        Assert.Empty(leaseStore.HeldLeases);
        Assert.Empty(publisher.Current);
        AssertReleaseIntent(leaseStore, new NodeIdentifier("node"), service.Id, 35000, 1);
    }

    [Fact]
    public async Task SameOwnerOutOfRangeAutomaticLeaseIsReleasedWithoutStarting()
    {
        var service = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var snapshot = CreateSnapshot(service);
        var leaseStore = new RecordingLeaseStore
        {
            ReturnedAcquireLeaseFactory = request => CreateReturnedLease(request, 35100, version: 7)
        };
        var executor = new RecordingExecutor();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, new RecordingProbe(), publisher, leaseStore);

        var readiness = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Unavailable, readiness.Status);
        Assert.Empty(executor.StartedServices);
        Assert.Empty(publisher.Current);
        Assert.Empty(leaseStore.HeldLeases);
        AssertReleaseIntent(leaseStore, new NodeIdentifier("node"), service.Id, 35100, 7);
    }

    [Fact]
    public async Task SameOwnerExpiredAutomaticLeaseIsReleasedWithoutStarting()
    {
        var service = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var snapshot = CreateSnapshot(service);
        var leaseStore = new RecordingLeaseStore
        {
            ReturnedAcquireLeaseFactory = request => CreateReturnedLease(request, 35001, version: 8, expired: true)
        };
        var executor = new RecordingExecutor();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, new RecordingProbe(), publisher, leaseStore);

        var readiness = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Unavailable, readiness.Status);
        Assert.Empty(executor.StartedServices);
        Assert.Empty(publisher.Current);
        Assert.Empty(leaseStore.HeldLeases);
        AssertReleaseIntent(leaseStore, new NodeIdentifier("node"), service.Id, 35001, 8);
    }


    [Fact]
    public async Task MismatchedNodeAutomaticLeaseIsNotReleased()
    {
        var service = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var snapshot = CreateSnapshot(service);
        var leaseStore = new RecordingLeaseStore
        {
            ReturnedAcquireLeaseFactory = request => CreateReturnedLease(
                request,
                35003,
                version: 10,
                nodeId: new NodeIdentifier("other-node"))
        };
        var executor = new RecordingExecutor();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, new RecordingProbe(), publisher, leaseStore);

        var readiness = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Unavailable, readiness.Status);
        Assert.Empty(executor.StartedServices);
        Assert.Empty(publisher.Current);
        Assert.Empty(leaseStore.ReleaseIntents);
        Assert.Single(leaseStore.HeldLeases);
    }

    [Fact]
    public async Task MismatchedServiceAutomaticLeaseIsNotReleased()
    {
        var service = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var snapshot = CreateSnapshot(service);
        var leaseStore = new RecordingLeaseStore
        {
            ReturnedAcquireLeaseFactory = request => CreateReturnedLease(request, 35004, version: 11, serviceIdOverride: EagerServiceId)
        };
        var executor = new RecordingExecutor();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, new RecordingProbe(), publisher, leaseStore);

        var readiness = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Unavailable, readiness.Status);
        Assert.Empty(executor.StartedServices);
        Assert.Empty(publisher.Current);
        Assert.Empty(leaseStore.ReleaseIntents);
        Assert.Single(leaseStore.HeldLeases);
    }

    [Fact]
    public async Task MismatchedGenerationAutomaticLeaseIsNotReleased()
    {
        var service = CreateService(LazyServiceId, ServiceStartMode.Lazy, enabled: true);
        var snapshot = CreateSnapshot(service);
        var generationId = Guid.Parse("018f0000-0000-7000-8000-000000000098");
        var leaseStore = new RecordingLeaseStore
        {
            ReturnedAcquireLeaseFactory = request => CreateReturnedLease(
                request,
                35005,
                version: 12,
                generationIdOverride: generationId)
        };
        var executor = new RecordingExecutor();
        var publisher = new HostServiceEndpointSnapshotPublisher();
        var manager = CreateManager(snapshot, executor, new RecordingProbe(), publisher, leaseStore);

        var readiness = await manager.EnsureReadyAsync(
            snapshot,
            service.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(HostServiceReadinessStatus.Unavailable, readiness.Status);
        Assert.Empty(executor.StartedServices);
        Assert.Empty(publisher.Current);
        Assert.Empty(leaseStore.ReleaseIntents);
        var heldLease = Assert.Single(leaseStore.HeldLeases);
        Assert.Equal(generationId, heldLease.GenerationId);
        Assert.NotEqual(Assert.Single(leaseStore.AcquireIntents).Request!.GenerationId, heldLease.GenerationId);
    }

    private static void AssertReleaseIntent(
        RecordingLeaseStore leaseStore,
        NodeIdentifier nodeId,
        Guid serviceId,
        int port,
        long version)
    {
        var intent = Assert.Single(leaseStore.ReleaseIntents);
        Assert.Equal(PortLeaseIntentKind.Release, intent.Kind);
        var release = Assert.IsType<PortLeaseRelease>(intent.Release);
        Assert.Equal(nodeId, release.NodeId);
        Assert.Equal(serviceId, release.ServiceId);
        Assert.Equal(port, release.Port);
        Assert.Equal(version, release.LeaseVersion);
        Assert.Equal(Assert.Single(leaseStore.AcquireIntents).Request!.GenerationId, release.GenerationId);
    }

    private static PortLease CreateReturnedLease(
        PortLeaseRequest request,
        int port,
        long version,
        NodeIdentifier? nodeId = null,
        bool expired = false,
        Guid? serviceIdOverride = null,
        Guid? generationIdOverride = null)
    {
        var owner = nodeId ?? request.NodeId;
        var generationId = generationIdOverride ?? request.GenerationId;
        if (expired)
        {
            return new PortLease(
                owner,
                serviceIdOverride ?? request.ServiceId,
                generationId,
                port,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddTicks(1),
                version);
        }

        var acquiredAt = DateTimeOffset.UtcNow;
        return new PortLease(
            owner,
            serviceIdOverride ?? request.ServiceId,
            generationId,
            port,
            acquiredAt,
            acquiredAt.AddMinutes(5),
            version);
    }


    private static HostServiceLifecycleManager CreateManager(
        HostConfigurationSnapshot snapshot,
        RecordingExecutor executor,
        IServiceHealthProbe probe,
        HostServiceEndpointSnapshotPublisher publisher,
        RecordingLeaseStore leaseStore) =>
        CreateManager(snapshot, executor, probe, publisher, leaseStore, out _, out _);

    private static HostServiceLifecycleManager CreateManager(
        HostConfigurationSnapshot snapshot,
        RecordingExecutor executor,
        IServiceHealthProbe probe,
        HostServiceEndpointSnapshotPublisher publisher,
        RecordingLeaseStore leaseStore,
        out HostConfigurationSnapshotHolder holder,
        out HostRuntimeState runtime,
        ImmutableDictionary<Guid, string?>? serviceOwners = null)
    {
        holder = new HostConfigurationSnapshotHolder();
        if (serviceOwners is null)
        {
            Assert.True(holder.TryReplace(snapshot));
        }
        else
        {
            Assert.Equal(
                SnapshotAdmission.Accepted,
                holder.TryReplace(snapshot, dispatchGeneration: null, serviceOwners));
        }

        runtime = CreateRuntimeState(snapshot, holder);
        return new HostServiceLifecycleManager(
            executor,
            probe,
            leaseStore,
            holder,
            publisher,
            runtime,
            new HostRuntimeOptions("Host=unit-test", "node", readOnly: false),
            NullLogger<HostServiceLifecycleManager>.Instance,
            new MicroserviceDrainTracker(),
            new HostNodeOptions(skipExtensions: true, disableSupervisor: true, readOnly: false));
    }
    private static HostConfigurationSnapshot CreateSnapshot(params ServiceConfiguration[] services) =>
        CreateSnapshot(1, services);

    private static HostConfigurationSnapshot CreateSnapshot(
        long version,
        params ServiceConfiguration[] services) =>
        new(
            version,
            new GlobalSettingsConfiguration(
                version: version,
                autoPortRangeStart: 35000,
                autoPortRangeEnd: 35099),
            default,
            services.ToImmutableArray(),
            default,
            default);

    private static HostRuntimeState CreateRuntimeState(
        HostConfigurationSnapshot snapshot,
        HostConfigurationSnapshotHolder? holder = null)
    {
        holder ??= new HostConfigurationSnapshotHolder();
        if (holder.Current is null)
        {
            Assert.True(holder.TryReplace(snapshot));
        }

        var runtime = new HostRuntimeState(
            holder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false));
        runtime.MarkSnapshotAccepted();
        return runtime;
    }
    private static HostConfigurationSnapshot CreateOwnedSnapshot(
        long version,
        ServiceConfiguration service,
        string owner,
        Contracts.ExtensionLoadState state) =>
        new(
            version,
            new GlobalSettingsConfiguration(
                version: version,
                autoPortRangeStart: 35000,
                autoPortRangeEnd: 35099),
            default,
            ImmutableArray.Create(service),
            ImmutableArray.Create(new ExtensionRecordConfiguration(
                owner,
                "1.0.0",
                state,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                1)),
            default);


    private static ServiceConfiguration CreateService(
        Guid id,
        ServiceStartMode startMode,
        bool enabled,
        long version = 1) =>
        new(
            id,
            enabled,
            "/bin/sh",
            ImmutableArray<string>.Empty,
            "/tmp",
            ImmutableDictionary<string, string>.Empty,
            startMode,
            Nekolla.Nekostick.Contracts.ServiceRestartPolicy.Never,
            new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                null,
                TimeSpan.FromSeconds(1)),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            version);
    private static ServiceConfiguration CreateServiceWithLaunch(
        Guid id,
        ServiceStartMode startMode,
        bool enabled,
        ImmutableArray<string> arguments,
        ImmutableDictionary<string, string> environment,
        long version = 1,
        string? fileName = null,
        Nekolla.Nekostick.Contracts.ServiceRestartPolicy restartPolicy =
            Nekolla.Nekostick.Contracts.ServiceRestartPolicy.Never) =>
        new(
            id,
            enabled,
            fileName ?? "/bin/sh",
            arguments,
            "/tmp",
            environment,
            startMode,
            restartPolicy,
            new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                null,
                TimeSpan.FromSeconds(1)),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            version);

    private static ServiceConfiguration CreateServiceWithEnvironment(
        Guid id,
        ImmutableDictionary<string, string> environment) =>
        new(
            id,
            true,
            "/bin/sh",
            ImmutableArray<string>.Empty,
            "/tmp",
            environment,
            ServiceStartMode.Lazy,
            Nekolla.Nekostick.Contracts.ServiceRestartPolicy.Never,
            new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                null,
                TimeSpan.FromSeconds(1)),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1);

    private sealed class RecordingExecutor : IProcessInstanceExecutor, IProcessLiveness
    {
        private readonly TaskCompletionSource<bool> _startGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _blockStart;
        private readonly bool _ignoreStartCancellation;
        private readonly ConcurrentDictionary<ProcessInstanceId, Guid> _runningInstances = new();

        public RecordingExecutor(bool blockStart = false, bool ignoreStartCancellation = false)
        {
            _blockStart = blockStart;
            _ignoreStartCancellation = ignoreStartCancellation;
        }

        public List<Guid> StartedServices { get; } = [];
        public List<ProcessLaunchSpecification> StartedSpecifications { get; } = [];
        public List<Guid> StoppedServices { get; } = [];
        public List<Guid> AcceptedServices { get; } = [];
        public TaskCompletionSource<bool> StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ProcessOperationResult> StartAsync(
            ProcessLaunchSpecification specification,
            CancellationToken cancellationToken = default)
        {
            StartedServices.Add(specification.ServiceId);
            StartedSpecifications.Add(specification);
            StartEntered.TrySetResult(true);
            if (_blockStart)
            {
                if (_ignoreStartCancellation)
                {
                    await _startGate.Task.ConfigureAwait(false);
                }
                else
                {
                    await _startGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new(ProcessOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled);
            }

            AcceptedServices.Add(specification.ServiceId);
            var instanceId = new ProcessInstanceId(Guid.NewGuid());
            _runningInstances[instanceId] = specification.ServiceId;
            return new(
                ProcessOperationStatus.Accepted,
                ServiceStateReasonCode.StartAccepted,
                instanceId,
                startedAt: DateTimeOffset.UtcNow);
        }

        public ValueTask<ProcessOperationResult> StopAsync(
            Guid serviceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default)
        {
            foreach (var pair in _runningInstances)
            {
                if (pair.Value == serviceId)
                {
                    _runningInstances.TryRemove(pair.Key, out _);
                }
            }

            StoppedServices.Add(serviceId);
            return ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Completed,
                ServiceStateReasonCode.StopCompleted));
        }
        public ValueTask<ProcessOperationResult> StopAsync(
            ProcessInstanceId instanceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default)
        {
            if (_runningInstances.TryRemove(instanceId, out var serviceId))
            {
                StoppedServices.Add(serviceId);
            }

            return ValueTask.FromResult(new ProcessOperationResult(
                ProcessOperationStatus.Completed,
                ServiceStateReasonCode.StopCompleted));
        }


        public void ReleaseStart() => _startGate.TrySetResult(true);
        bool IProcessLiveness.IsRunning(Guid serviceId) =>
            _runningInstances.Any(pair => pair.Value == serviceId);

        bool IProcessLiveness.IsRunning(Guid serviceId, ProcessInstanceId instanceId) =>
            _runningInstances.TryGetValue(instanceId, out var activeServiceId) &&
            activeServiceId == serviceId;
    }

    private sealed class RecordingLeaseStore : IPortLeaseStore
    {
        private readonly ConcurrentDictionary<(Guid ServiceId, Guid GenerationId), PortLease> _heldLeases = new();
        private readonly ConcurrentDictionary<(string NodeId, int Port), (Guid ServiceId, Guid GenerationId)> _portOwners = new();
        private long _nextVersion;

        public TimeSpan AcquireLifetime { get; init; } = TimeSpan.FromMinutes(1);
        public bool FailRenewal { get; set; }
        public Func<PortLeaseRequest, PortLease>? ReturnedAcquireLeaseFactory { get; init; }
        public IReadOnlyCollection<PortLease> HeldLeases => _heldLeases.Values.ToArray();
        public ConcurrentQueue<PortLeaseIntent> ReleaseIntents { get; } = new();
        public ConcurrentQueue<(PortLeaseOperationStatus Status, bool ValidLease)> AcquireResults { get; } = new();
        public ConcurrentQueue<PortLeaseIntent> AcquireIntents { get; } = new();

        public ValueTask<PortLeaseOperationResult> ApplyAsync(
            PortLeaseIntent intent,
            CancellationToken cancellationToken = default)
        {
            if (intent.Kind == PortLeaseIntentKind.Acquire)
            {
                AcquireIntents.Enqueue(intent);
                var outcome = AcquireResults.TryDequeue(out var queued)
                    ? queued
                    : (Status: PortLeaseOperationStatus.Applied, ValidLease: true);
                if (outcome.Status != PortLeaseOperationStatus.Applied)
                {
                    return ValueTask.FromResult(new PortLeaseOperationResult(outcome.Status));
                }

                var request = intent.Request!;
                var lease = ReturnedAcquireLeaseFactory?.Invoke(request);
                if (lease is null)
                {
                    var serviceId = outcome.ValidLease ? request.ServiceId : LazyServiceId;
                    var generationId = request.GenerationId;
                    var owner = (serviceId, generationId);
                    int port;
                    if (request.Port == 0)
                    {
                        port = 0;
                        var rangeStart = request.AutomaticPortRangeStart!.Value;
                        var rangeEnd = request.AutomaticPortRangeEnd!.Value;
                        for (var candidate = rangeStart; candidate <= rangeEnd; candidate++)
                        {
                            if (_portOwners.TryAdd((request.NodeId.Value, candidate), owner))
                            {
                                port = candidate;
                                break;
                            }
                        }

                        if (port == 0)
                        {
                            return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.Conflict));
                        }
                    }
                    else
                    {
                        port = request.Port;
                        if (!_portOwners.TryAdd((request.NodeId.Value, port), owner))
                        {
                            return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.Conflict));
                        }
                    }

                    var now = DateTimeOffset.UtcNow;
                    lease = new PortLease(
                        request.NodeId,
                        serviceId,
                        generationId,
                        port,
                        now,
                        now.Add(AcquireLifetime),
                        Interlocked.Increment(ref _nextVersion));
                    if (!_heldLeases.TryAdd(owner, lease))
                    {
                        _portOwners.TryRemove((request.NodeId.Value, port), out _);
                        return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.Conflict));
                    }
                }
                else
                {
                    if (!outcome.ValidLease)
                    {
                        lease = new PortLease(
                            lease.NodeId,
                            LazyServiceId,
                            lease.GenerationId,
                            lease.Port,
                            lease.AcquiredAt,
                            lease.ExpiresAt,
                            lease.Version);
                    }

                    var owner = (lease.ServiceId, lease.GenerationId);
                    var portKey = (lease.NodeId.Value, lease.Port);
                    if (!_portOwners.TryAdd(portKey, owner))
                    {
                        return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.Conflict));
                    }

                    if (!_heldLeases.TryAdd(owner, lease))
                    {
                        _portOwners.TryRemove(portKey, out _);
                        return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.Conflict));
                    }
                }

                return ValueTask.FromResult(new PortLeaseOperationResult(
                    PortLeaseOperationStatus.Applied,
                    lease));
            }

            if (intent.Kind == PortLeaseIntentKind.Release)
            {
                var release = intent.Release!;
                ReleaseIntents.Enqueue(intent);
                var owner = (release.ServiceId, release.GenerationId);
                if (_heldLeases.TryGetValue(owner, out var lease) &&
                    lease.NodeId == release.NodeId &&
                    lease.Port == release.Port &&
                    lease.Version == release.LeaseVersion &&
                    _heldLeases.TryRemove(owner, out var removed))
                {
                    _portOwners.TryRemove((removed.NodeId.Value, removed.Port), out _);
                }

                return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.NotFound));
            }

            if (intent.Kind == PortLeaseIntentKind.Renew)
            {
                if (FailRenewal)
                {
                    return ValueTask.FromResult(new PortLeaseOperationResult(
                        PortLeaseOperationStatus.DatabaseUnavailable));
                }

                var renewal = intent.Renewal!;
                var owner = (renewal.ServiceId, renewal.GenerationId);
                if (!_heldLeases.TryGetValue(owner, out var lease) ||
                    lease.NodeId != renewal.NodeId ||
                    lease.Port != renewal.Port)
                {
                    return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.NotFound));
                }

                if (lease.Version != renewal.LeaseVersion)
                {
                    return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.Conflict));
                }

                var now = DateTimeOffset.UtcNow;
                var renewed = new PortLease(
                    lease.NodeId,
                    lease.ServiceId,
                    lease.GenerationId,
                    lease.Port,
                    now,
                    now.Add(renewal.TimeToLive),
                    checked(lease.Version + 1));
                return _heldLeases.TryUpdate(owner, renewed, lease)
                    ? ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.Applied, renewed))
                    : ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.Conflict));
            }

            return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.NotFound));
        }
    }


    private sealed class RecordingProbe : IServiceHealthProbe
    {
        public ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new HealthObservationResult(
                request.ServiceId,
                HealthObservationStatus.Healthy,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                1));
    }

    private sealed class AutomaticDependencyRestartProbe : IServiceHealthProbe
    {
        private readonly Guid _dependencyId;
        private readonly ConcurrentDictionary<Guid, int> _calls = new();

        internal AutomaticDependencyRestartProbe(Guid dependencyId) => _dependencyId = dependencyId;

        internal TaskCompletionSource<bool> ReplacementHealthy { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            var attempt = _calls.AddOrUpdate(request.ServiceId, 1, static (_, previous) => previous + 1);
            var status = request.ServiceId == _dependencyId && attempt is >= 2 and <= 4
                ? HealthObservationStatus.Unavailable
                : HealthObservationStatus.Healthy;
            if (request.ServiceId == _dependencyId && attempt >= 5)
            {
                ReplacementHealthy.TrySetResult(true);
            }

            return ValueTask.FromResult(new HealthObservationResult(
                request.ServiceId,
                status,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                attempt));
        }
    }
    private sealed class StartupBarrierProbe : IServiceHealthProbe
    {
        private readonly Guid _serviceId;
        private readonly int _blockedCall;
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _serviceCallCount;

        public StartupBarrierProbe(Guid serviceId, int blockedCall)
        {
            _serviceId = serviceId;
            _blockedCall = blockedCall;
        }

        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.ServiceId == _serviceId && Interlocked.Increment(ref _serviceCallCount) == _blockedCall)
            {
                Entered.TrySetResult(true);
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HealthObservationResult(
                request.ServiceId,
                HealthObservationStatus.Healthy,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                1);
        }

        public void Release() => _release.TrySetResult(true);
    }


    private sealed class ControlledProbe : IServiceHealthProbe
    {
        private readonly TaskCompletionSource<HealthObservationStatus> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            var status = await _result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new HealthObservationResult(
                request.ServiceId,
                status,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                1);
        }

        public void Complete(HealthObservationStatus status) => _result.TrySetResult(status);
    }


}
