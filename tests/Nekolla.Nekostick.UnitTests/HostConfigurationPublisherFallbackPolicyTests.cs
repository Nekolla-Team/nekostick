using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Persistence;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostConfigurationPublisherFallbackPolicyTests
{
    [Fact]
    public async Task ColdStartExcludesLifecycleFailedBindingAndPublishesHealthyRoute()
    {
        var failed = LifecycleFailure("cold.failed", "cold.failed.handler");
        var healthy = Healthy("cold.healthy", "cold.healthy.handler");
        await using var scenario = PublisherScenario.Create(failed, healthy);
        var snapshot = scenario.CreateSnapshot(17);

        Assert.Equal(
            PublishOutcome.Published,
            await scenario.Publisher.PublishAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken));

        var generation = CurrentGeneration(scenario);
        await AssertHandlerStateAsync(
            generation,
            healthy.HandlerId,
            ExtensionInvocationState.Handled,
            TestContext.Current.CancellationToken);
        await AssertHandlerStateAsync(
            generation,
            failed.HandlerId,
            ExtensionInvocationState.Unavailable,
            TestContext.Current.CancellationToken);
        Assert.Empty(generation.GetUnavailableHandlerIds([healthy.HandlerId]));
        Assert.Contains(failed.HandlerId, generation.GetUnavailableHandlerIds([failed.HandlerId]));

        var nodeStates = HostConfigurationPublisher.ApplyUnavailableBindingNodeStates(
            snapshot.ExtensionRecords
                .Select(static record => new ExtensionNodeStateWrite(
                    record.ExtensionId,
                    record.ContentHash,
                    record.LoadState))
                .ToImmutableArray(),
            generation);
        var failedNodeState = Assert.Single(nodeStates, state => state.ExtensionId == failed.Id);
        Assert.Equal(ExtensionLoadState.Failed, failedNodeState.LoadState);
        Assert.Equal(ExtensionFailureCode.LifecycleFailed.ToString(), failedNodeState.FailureCode);

        var exclusion = Assert.Single(scenario.HostLogger.Entries, static entry => entry.EventId.Id == 1100);
        Assert.Equal(LogLevel.Warning, exclusion.LogLevel);
        Assert.Equal(generation.GenerationId, exclusion.Fields["GenerationId"]);
        Assert.Equal(snapshot.Version, exclusion.Fields["Version"]);
        Assert.Contains(
            $"{failed.Id}={ExtensionFailureCode.LifecycleFailed}",
            Assert.IsType<string>(exclusion.Fields["Bindings"]));

        var applied = Assert.Single(scenario.HostLogger.Entries, static entry => entry.EventId.Id == 1109);
        Assert.Equal(snapshot.Version, applied.Fields["Version"]);
        Assert.True((bool)applied.Fields["GenerationChanged"]!);
        Assert.DoesNotContain(scenario.HostLogger.Entries, static entry => entry.EventId.Id == 1064);

        var candidateFailure = Assert.Single(
            scenario.ExtensionLogger.Entries,
            static entry => entry.EventId.Id == 2002);
        Assert.Equal(failed.Id, candidateFailure.Fields["ExtensionId"]);
    }

    [Fact]
    public async Task WarmReplacementFailureKeepsPreviousGenerationServing()
    {
        var original = Healthy("warm.extension", "warm.handler");
        await using var scenario = PublisherScenario.Create(original);
        var initialSnapshot = scenario.CreateSnapshot(17);
        Assert.Equal(
            PublishOutcome.Published,
            await scenario.Publisher.PublishAsync(
                initialSnapshot,
                cancellationToken: TestContext.Current.CancellationToken));

        var previousGeneration = CurrentGeneration(scenario);
        await AssertHandlerStateAsync(
            previousGeneration,
            original.HandlerId,
            ExtensionInvocationState.Handled,
            TestContext.Current.CancellationToken);
        var replacement = original with
        {
            SettingsJson = Settings(original.HandlerId, startFails: true),
            SettingsVersion = 2
        };
        var replacementSnapshot = scenario.CreateSnapshot(18, replacement);
        Assert.Equal(
            PublishOutcome.Published,
            await scenario.Publisher.PublishAsync(
                replacementSnapshot,
                cancellationToken: TestContext.Current.CancellationToken));

        await AssertHandlerStateAsync(
            CurrentGeneration(scenario),
            original.HandlerId,
            ExtensionInvocationState.Handled,
            TestContext.Current.CancellationToken);
        AssertUnsafeFallback(scenario, original.Id, ExtensionFailureCode.LifecycleFailed);
        Assert.Equal(previousGeneration.GenerationId, CurrentGeneration(scenario).GenerationId);
        var failedCandidate = CreateGeneration(
            CreateBindingStatus(original.Id, false, ExtensionFailureCode.LifecycleFailed));
        var fallbackNodeStates = HostConfigurationPublisher.ApplyUnavailableBindingNodeStates(
            replacementSnapshot.ExtensionRecords
                .Select(static record => new ExtensionNodeStateWrite(
                    record.ExtensionId,
                    record.ContentHash,
                    record.LoadState))
                .ToImmutableArray(),
            failedCandidate,
            previousGeneration);
        var previousNodeState = Assert.Single(
            fallbackNodeStates,
            state => state.ExtensionId == original.Id);
        Assert.Equal(ExtensionLoadState.Loaded, previousNodeState.LoadState);
        Assert.Equal("None", previousNodeState.FailureCode);
    }

    [Fact]
    public async Task RunningZeroHandlerWithHardDependentPublishesWithoutFailureClassification()
    {
        var provider = Healthy("empty.provider", "empty.provider.handler");
        var dependent = Healthy(
            "empty.dependent",
            "empty.dependent.handler",
            Dependency(provider.Id));
        await using var scenario = PublisherScenario.Create(provider, dependent);
        var initialSnapshot = scenario.CreateSnapshot(17);
        Assert.Equal(
            PublishOutcome.Published,
            await scenario.Publisher.PublishAsync(
                initialSnapshot,
                cancellationToken: TestContext.Current.CancellationToken));

        var previousGeneration = CurrentGeneration(scenario);
        await AssertHandlerStateAsync(
            previousGeneration,
            provider.HandlerId,
            ExtensionInvocationState.Handled,
            TestContext.Current.CancellationToken);

        var zeroHandlerProvider = provider with
        {
            SettingsJson = Settings(provider.HandlerId, registerHandler: false),
            SettingsVersion = 2
        };
        var snapshot = scenario.CreateSnapshot(18, false, zeroHandlerProvider, dependent);
        Assert.Equal(
            PublishOutcome.Published,
            await scenario.Publisher.PublishAsync(
                snapshot,
                cancellationToken: TestContext.Current.CancellationToken));

        var generation = CurrentGeneration(scenario);
        Assert.NotEqual(previousGeneration.GenerationId, generation.GenerationId);
        var providerBinding = Assert.Single(
            generation.Bindings,
            binding => binding.ExtensionId == provider.Id);
        Assert.False(providerBinding.Available);
        Assert.Equal(ExtensionFailureCode.None, providerBinding.FailureCode);
        Assert.Contains(
            generation.Contexts,
            context => context.Instance.Manifest.Id == provider.Id);

        var dependentBinding = Assert.Single(
            generation.Bindings,
            binding => binding.ExtensionId == dependent.Id);
        Assert.True(dependentBinding.Available);
        await AssertHandlerStateAsync(
            generation,
            dependent.HandlerId,
            ExtensionInvocationState.Handled,
            TestContext.Current.CancellationToken);

        var nodeStates = HostConfigurationPublisher.ApplyUnavailableBindingNodeStates(
            snapshot.ExtensionRecords
                .Select(static record => new ExtensionNodeStateWrite(
                    record.ExtensionId,
                    record.ContentHash,
                    record.LoadState))
                .ToImmutableArray(),
            generation);
        var providerNodeState = Assert.Single(
            nodeStates,
            state => state.ExtensionId == provider.Id);
        Assert.Equal(ExtensionLoadState.Loaded, providerNodeState.LoadState);
        Assert.Equal("None", providerNodeState.FailureCode);
        Assert.DoesNotContain(scenario.HostLogger.Entries, static entry => entry.EventId.Id == 1064);
        Assert.DoesNotContain(scenario.HostLogger.Entries, static entry => entry.EventId.Id == 1100);
    }

    [Fact]
    public async Task HardDependentOfFailedBindingTriggersColdFallback()
    {
        var failed = LifecycleFailure("hard.failed", "hard.failed.handler");
        var dependent = Healthy("hard.dependent", "hard.dependent.handler", Dependency(failed.Id));
        await using var scenario = PublisherScenario.Create(failed, dependent);
        var snapshot = scenario.CreateSnapshot(17);

        Assert.Equal(
            PublishOutcome.Published,
            await scenario.Publisher.PublishAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken));

        await AssertHandlerStateAsync(
            CurrentGeneration(scenario),
            dependent.HandlerId,
            ExtensionInvocationState.Unavailable,
            TestContext.Current.CancellationToken);
        AssertUnsafeFallback(scenario, failed.Id, ExtensionFailureCode.LifecycleFailed);
    }

    [Fact]
    public async Task TransitiveHardDependentOfFailedBindingTriggersColdFallback()
    {
        var failed = LifecycleFailure("chain.failed", "chain.failed.handler");
        var middle = Healthy("chain.middle", "chain.middle.handler", Dependency(failed.Id));
        var dependent = Healthy("chain.dependent", "chain.dependent.handler", Dependency(middle.Id));
        await using var scenario = PublisherScenario.Create(failed, middle, dependent);
        var snapshot = scenario.CreateSnapshot(17);

        Assert.Equal(
            PublishOutcome.Published,
            await scenario.Publisher.PublishAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken));

        var generation = CurrentGeneration(scenario);
        await AssertHandlerStateAsync(
            generation,
            middle.HandlerId,
            ExtensionInvocationState.Unavailable,
            TestContext.Current.CancellationToken);
        await AssertHandlerStateAsync(
            generation,
            dependent.HandlerId,
            ExtensionInvocationState.Unavailable,
            TestContext.Current.CancellationToken);
        AssertUnsafeFallback(scenario, failed.Id, ExtensionFailureCode.LifecycleFailed);
    }

    [Fact]
    public async Task OptionalDependencyOnFailedBindingAllowsDependentToServe()
    {
        var failed = LifecycleFailure("optional.failed", "optional.failed.handler");
        var dependent = Healthy("optional.dependent", "optional.dependent.handler", OptionalDependency(failed.Id));
        await using var scenario = PublisherScenario.Create(failed, dependent);
        var snapshot = scenario.CreateSnapshot(17);

        Assert.Equal(
            PublishOutcome.Published,
            await scenario.Publisher.PublishAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken));

        var generation = CurrentGeneration(scenario);
        await AssertHandlerStateAsync(
            generation,
            dependent.HandlerId,
            ExtensionInvocationState.Handled,
            TestContext.Current.CancellationToken);
        await AssertHandlerStateAsync(
            generation,
            failed.HandlerId,
            ExtensionInvocationState.Unavailable,
            TestContext.Current.CancellationToken);
        var exclusion = Assert.Single(scenario.HostLogger.Entries, static entry => entry.EventId.Id == 1100);
        Assert.Contains(
            $"{failed.Id}={ExtensionFailureCode.LifecycleFailed}",
            Assert.IsType<string>(exclusion.Fields["Bindings"]));
        Assert.DoesNotContain(scenario.HostLogger.Entries, static entry => entry.EventId.Id == 1064);
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("HandlerConflict")]
    public async Task UnsafeFailureCodesTriggerColdFallback(string failureCode)
    {
        var failed = failureCode switch
        {
            nameof(ExtensionFailureCode.Cancelled) => CancelledFailure("unsafe.cancelled", "unsafe.cancelled.handler"),
            nameof(ExtensionFailureCode.HandlerConflict) => HandlerConflictFailure("unsafe.conflict", "unsafe.conflict.handler"),
            _ => throw new ArgumentOutOfRangeException(nameof(failureCode))
        };
        var expectedFailureCode = Enum.Parse<ExtensionFailureCode>(failureCode);
        await using var scenario = PublisherScenario.Create(failed);
        var snapshot = scenario.CreateSnapshot(17);

        Assert.Equal(
            PublishOutcome.Published,
            await scenario.Publisher.PublishAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken));

        await AssertHandlerStateAsync(
            CurrentGeneration(scenario),
            failed.HandlerId,
            ExtensionInvocationState.Unavailable,
            TestContext.Current.CancellationToken);
        AssertUnsafeFallback(scenario, failed.Id, expectedFailureCode);
    }

    [Theory]
    [InlineData(ExtensionFailureCode.Cancelled)]
    [InlineData(ExtensionFailureCode.RuntimeUnavailable)]
    [InlineData(ExtensionFailureCode.InvalidArgument)]
    [InlineData(ExtensionFailureCode.HandlerConflict)]
    public async Task SpecifiedUnsafeFailureCodesAlwaysAbortPartialPublication(
        ExtensionFailureCode failureCode)
    {
        await using var generation = CreateUnavailableGeneration(failureCode);

        Assert.True(
            HostConfigurationPublisher.HasUnsafeUnavailableBinding(
                generation,
                null,
                ImmutableArray<ExtensionRuntimeDescriptor>.Empty),
            failureCode.ToString());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ContractImportEdgesRespectOptionality(bool optional, bool expectedUnsafe)
    {
        const string providerId = "contract.provider";
        const string importerId = "contract.importer";
        using var providerDirectory = TestExtensionDirectory.CreateJson(
            ContractManifestJson(providerId, ContractExportJson(), "[]"));
        using var importerDirectory = TestExtensionDirectory.CreateJson(
            ContractManifestJson(importerId, "[]", ContractImportJson(optional)));

        var providerDiscovery = ExtensionManifestDiscovery.Discover(providerDirectory.RootPath);
        var importerDiscovery = ExtensionManifestDiscovery.Discover(importerDirectory.RootPath);
        Assert.True(providerDiscovery.Succeeded, providerDiscovery.FailureCode.ToString());
        Assert.True(importerDiscovery.Succeeded, importerDiscovery.FailureCode.ToString());

        await using var generation = CreateGeneration(
            CreateBindingStatus(providerId, false, ExtensionFailureCode.LifecycleFailed),
            CreateBindingStatus(importerId, true, ExtensionFailureCode.None));
        var desired = ImmutableArray.Create(
            new ExtensionRuntimeDescriptor(providerDiscovery.Manifest!),
            new ExtensionRuntimeDescriptor(importerDiscovery.Manifest!));

        Assert.Equal(
            expectedUnsafe,
            HostConfigurationPublisher.HasUnsafeUnavailableBinding(generation, null, desired));
    }

    private static ExtensionDispatchGeneration CurrentGeneration(PublisherScenario scenario) =>
        Assert.IsType<ExtensionDispatchGeneration>(scenario.Holder.RoutingSnapshot?.DispatchGeneration);

    private static ExtensionDispatchGeneration CreateUnavailableGeneration(ExtensionFailureCode failureCode) =>
        CreateGeneration(CreateBindingStatus("unsafe.extension", false, failureCode));

    private static ExtensionGenerationBindingStatus CreateBindingStatus(
        string extensionId,
        bool available,
        ExtensionFailureCode failureCode) =>
        new(
            extensionId,
            "1.0.0",
            available,
            false,
            failureCode,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty,
            false,
            failureCode == ExtensionFailureCode.None
                ? null
                : new ExtensionErrorDetail(
                    $"Binding for extension '{extensionId}' failed with {failureCode}."));

    private static ExtensionDispatchGeneration CreateGeneration(
        params ExtensionGenerationBindingStatus[] bindings) =>
        new(
            generationId: 1,
            handlers: ImmutableDictionary<string, ExtensionDispatchBinding>.Empty,
            fallback: null,
            contexts: Array.Empty<ExtensionDispatchContext>(),
            bindings: bindings.ToImmutableArray(),
            owner: new object());

    private static async Task AssertHandlerStateAsync(
        ExtensionDispatchGeneration generation,
        string handlerId,
        ExtensionInvocationState expected,
        CancellationToken cancellationToken)
    {
        var result = await generation.HandleAsync(
            handlerId,
            new ExtensionHandlerRequest("GET", "/"),
            cancellationToken);
        Assert.Equal(expected, result.State);
    }

    private static void AssertUnsafeFallback(
        PublisherScenario scenario,
        string failedExtensionId,
        ExtensionFailureCode failureCode)
    {
        var fallback = Assert.Single(scenario.HostLogger.Entries, static entry => entry.EventId.Id == 1064);
        Assert.Equal(LogLevel.Warning, fallback.LogLevel);
        Assert.True((bool)fallback.Fields["FallbackPublished"]!);
        Assert.Contains(
            $"{failedExtensionId}={failureCode}",
            Assert.IsType<string>(fallback.Fields["Bindings"]));
        Assert.DoesNotContain(scenario.HostLogger.Entries, static entry => entry.EventId.Id == 1100);
    }

    private static ExtensionSpec Healthy(
        string extensionId,
        string handlerId,
        string dependenciesJson = "[]") =>
        new(extensionId, handlerId, Settings(handlerId), dependenciesJson);

    private static ExtensionSpec LifecycleFailure(string extensionId, string handlerId) =>
        new(extensionId, handlerId, Settings(handlerId, startFails: true));

    private static ExtensionSpec CancelledFailure(string extensionId, string handlerId) =>
        new(extensionId, handlerId, Settings(handlerId, startCancelled: true));

    private static ExtensionSpec HandlerConflictFailure(string extensionId, string handlerId) =>
        new(extensionId, handlerId, Settings(handlerId, duplicateHandler: true));

    private static string Dependency(string extensionId) =>
        $$"""[{"id":"{{extensionId}}","versionRange":">=1.0.0"}]""";

    private static string OptionalDependency(string extensionId) =>
        $$"""[{"id":"{{extensionId}}","versionRange":">=1.0.0","optional":true}]""";

    private static string Settings(
        string handlerId,
        bool startFails = false,
        bool startCancelled = false,
        bool duplicateHandler = false,
        bool registerHandler = true) =>
        JsonSerializer.Serialize(new
        {
            label = handlerId,
            handlerId,
            startFails,
            startCancelled,
            duplicateHandler,
            registerHandler
        });

    private static string RuntimeManifestJson(ExtensionSpec extension) =>
        $$"""
        {
          "schemaVersion": 1,
          "id": "{{extension.Id}}",
          "version": "1.0.0",
          "entryAssembly": "Fixtures.Extension.dll",
          "entryType": "Nekolla.Nekostick.Tests.Fixtures.Extension.FixtureEntrypoint",
          "dependencies": {{extension.DependenciesJson}},
          "requiredHostApiVersion": ">=1.0.0"
        }
        """;

    private static string ContractManifestJson(
        string extensionId,
        string exportsJson,
        string importsJson) =>
        $$"""
        {
          "schemaVersion": 1,
          "id": "{{extensionId}}",
          "version": "1.0.0",
          "entryAssembly": "Fixtures.Extension.dll",
          "entryType": "Nekolla.Nekostick.Tests.Fixtures.Extension.FixtureEntrypoint",
          "dependencies": [],
          "requiredHostApiVersion": ">=1.0.0",
          "exports": {{exportsJson}},
          "imports": {{importsJson}}
        }
        """;

    private static string ContractExportJson() =>
        JsonSerializer.Serialize(new[]
        {
            new
            {
                contractId = "shared.logger",
                version = "1.0.0",
                assemblyIdentity = typeof(IExtensionLogger).Assembly.FullName,
                typeIdentity = typeof(IExtensionLogger).FullName
            }
        });

    private static string ContractImportJson(bool optional) =>
        JsonSerializer.Serialize(new[]
        {
            new
            {
                contractId = "shared.logger",
                versionRange = ">=1.0.0",
                assemblyIdentity = typeof(IExtensionLogger).Assembly.FullName,
                typeIdentity = typeof(IExtensionLogger).FullName,
                optional
            }
        });

    private sealed record ExtensionSpec(
        string Id,
        string HandlerId,
        string SettingsJson,
        string DependenciesJson = "[]",
        long SettingsVersion = 1)
    {
        internal Guid RouteId { get; init; } = Guid.CreateVersion7();
    }

    private sealed class PublisherScenario : IAsyncDisposable
    {
        private readonly ExtensionSpec[] _extensions;
        private readonly List<TestExtensionDirectory> _fixtureDirectories;

        private PublisherScenario(
            ExtensionSpec[] extensions,
            List<TestExtensionDirectory> fixtureDirectories,
            HostConfigurationSnapshotHolder holder,
            ExtensionRuntimeManager runtimeManager,
            HostConfigurationPublisher publisher,
            CapturingLogger<HostConfigurationPublisher> hostLogger,
            CapturingLogger<ExtensionRuntimeManager> extensionLogger)
        {
            _extensions = extensions;
            _fixtureDirectories = fixtureDirectories;
            Holder = holder;
            RuntimeManager = runtimeManager;
            Publisher = publisher;
            HostLogger = hostLogger;
            ExtensionLogger = extensionLogger;
        }

        internal HostConfigurationSnapshotHolder Holder { get; }

        internal ExtensionRuntimeManager RuntimeManager { get; }

        internal HostConfigurationPublisher Publisher { get; }

        internal CapturingLogger<HostConfigurationPublisher> HostLogger { get; }

        internal CapturingLogger<ExtensionRuntimeManager> ExtensionLogger { get; }

        internal static PublisherScenario Create(params ExtensionSpec[] extensions)
        {
            if (extensions.Length == 0)
            {
                throw new ArgumentException("At least one extension is required.", nameof(extensions));
            }

            var fixtureDirectories = new List<TestExtensionDirectory>(extensions.Length);
            try
            {
                foreach (var extension in extensions)
                {
                    fixtureDirectories.Add(TestExtensionDirectory.CreateJson(RuntimeManifestJson(extension)));
                }

                var extensionsRoot = Path.Combine(fixtureDirectories[0].RootPath, "extensions");
                Directory.CreateDirectory(extensionsRoot);
                for (var index = 0; index < extensions.Length; index++)
                {
                    StageExtension(fixtureDirectories[index], extensions[index].Id, extensionsRoot);
                }

                var hostLogger = new CapturingLogger<HostConfigurationPublisher>();
                var extensionLogger = new CapturingLogger<ExtensionRuntimeManager>();
                var runtimeManager = new ExtensionRuntimeManager(
                    HostApiVersion.Current,
                    logger: extensionLogger);
                var holder = new HostConfigurationSnapshotHolder(hostLogger);
                var publisher = new HostConfigurationPublisher(
                    holder,
                    runtimeManager,
                    new HostNodeOptions(
                        skipExtensions: false,
                        disableSupervisor: true,
                        readOnly: true,
                        extensionsRootPath: extensionsRoot),
                    hostLogger);
                return new PublisherScenario(
                    extensions.ToArray(),
                    fixtureDirectories,
                    holder,
                    runtimeManager,
                    publisher,
                    hostLogger,
                    extensionLogger);
            }
            catch
            {
                foreach (var fixtureDirectory in fixtureDirectories)
                {
                    fixtureDirectory.Dispose();
                }

                throw;
            }
        }

        internal HostConfigurationSnapshot CreateSnapshot(long version, params ExtensionSpec[] extensions) =>
            CreateSnapshot(version, true, extensions);

        internal HostConfigurationSnapshot CreateSnapshot(
            long version,
            bool includeRoutes,
            params ExtensionSpec[] extensions)
        {
            var snapshotExtensions = extensions.Length == 0 ? _extensions : extensions;
            var now = DateTimeOffset.UnixEpoch;
            var routes = includeRoutes
                ? snapshotExtensions
                    .Select(extension => new RouteConfiguration(
                        extension.RouteId,
                        enabled: true,
                        matcher: new RouteMatcherConfiguration(
                            RouteMatcherType.Exact,
                            "/" + extension.Id.Replace('.', '/'),
                            ImmutableArray<string>.Empty,
                            ImmutableArray.Create("GET")),
                        target: new ExtensionHandlerRouteTargetConfiguration(extension.HandlerId),
                        priority: 0,
                        forwarding: new ForwardingConfiguration(ForwardingMode.Preserve, null),
                        requestHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
                        responseHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
                        metadataJson: "{}",
                        createdAt: now,
                        updatedAt: now,
                        version: 1,
                        ownerExtensionId: extension.Id))
                    .ToImmutableArray()
                : ImmutableArray<RouteConfiguration>.Empty;
            var records = snapshotExtensions
                .Select(extension => new ExtensionRecordConfiguration(
                    extension.Id,
                    "1.0.0",
                    ExtensionLoadState.Loaded,
                    now,
                    now,
                    recordVersion: 1))
                .ToImmutableArray();
            var settings = snapshotExtensions
                .Select(extension => new ExtensionSettingsConfiguration(
                    extension.Id,
                    schemaVersion: 1,
                    settingsJson: extension.SettingsJson,
                    version: extension.SettingsVersion))
                .ToImmutableArray();

            return new HostConfigurationSnapshot(
                version,
                new GlobalSettingsConfiguration(version: version),
                routes,
                ImmutableArray<ServiceConfiguration>.Empty,
                records,
                settings);
        }

        public async ValueTask DisposeAsync()
        {
            await Publisher.DisposeAsync();
            await Holder.DisposeAsync();
            await RuntimeManager.DisposeAsync();
            foreach (var fixtureDirectory in _fixtureDirectories)
            {
                fixtureDirectory.Dispose();
            }
        }

        private static void StageExtension(
            TestExtensionDirectory fixtureDirectory,
            string extensionId,
            string extensionsRoot)
        {
            var installPath = Path.Combine(extensionsRoot, extensionId);
            Directory.CreateDirectory(installPath);
            foreach (var file in Directory.EnumerateFiles(fixtureDirectory.RootPath))
            {
                File.Copy(file, Path.Combine(installPath, Path.GetFileName(file)));
            }
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        internal List<CapturedLog> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var value in values)
                {
                    fields[value.Key] = value.Value;
                }
            }

            Entries.Add(new CapturedLog(eventId, logLevel, fields));
        }
    }

    private sealed record CapturedLog(
        EventId EventId,
        LogLevel LogLevel,
        IReadOnlyDictionary<string, object?> Fields);
}
