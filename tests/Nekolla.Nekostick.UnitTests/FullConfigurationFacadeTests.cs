using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class FullConfigurationFacadeTests
{
    [Fact]
    public async Task ReadOnlyHostFacadeReadsFullConfigurationButRejectsReplacement()
    {
        var inner = new RecordingHostConfigApi();
        var services = new ServiceCollection();
        services.AddScoped<IHostConfigApi>(_ =>
            new HostConfigApiReadOnlyDecorator(
                inner,
                new HostRuntimeOptions("synthetic-storage", "test-node", readOnly: true)));
        await using var provider = services.BuildServiceProvider();
        var facade = new ExtensionFullConfigurationFacade(
            provider.GetRequiredService<IServiceScopeFactory>());

        var read = await facade.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(read.IsSuccess);
        Assert.Same(inner.Snapshot, read.Value);

        var write = await facade.ReplaceAsync(
            inner.Snapshot.Version,
            CreateChanges(),
            TestContext.Current.CancellationToken);
        Assert.False(write.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.Unsupported, write.Errors.Single().Code);
        Assert.Null(write.NewVersion);
        Assert.Equal(0, inner.WriteSnapshotCalls);
    }

    [Fact]
    public async Task MissingCapabilityFullFacadeReturnsSafeUnsupportedResults()
    {
        var facade = UnsupportedExtensionCapabilities.Create().FullConfiguration;
        var read = await facade.ReadAsync(TestContext.Current.CancellationToken);
        Assert.False(read.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.Unsupported, read.Errors.Single().Code);
        Assert.Null(read.Value);

        var write = await facade.ReplaceAsync(
            expectedVersion: 0,
            changes: CreateChanges(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(write.IsSuccess);
        Assert.Equal(ConfigurationErrorCode.Unsupported, write.Errors.Single().Code);
        Assert.Null(write.NewVersion);
    }

    private static ConfigurationChangeSet CreateChanges() =>
        new(
            new GlobalSettingsConfiguration(version: 1),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);

    private sealed class RecordingHostConfigApi : IHostConfigApi
    {
        internal HostConfigurationSnapshot Snapshot { get; } = new(
            1,
            new GlobalSettingsConfiguration(version: 1),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);

        internal int WriteSnapshotCalls { get; private set; }

        public HostApiVersion ApiVersion => HostApiVersion.Current;

        public ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConfigurationReadResult<HostConfigurationSnapshot>.Success(Snapshot));

        public ValueTask<ConfigurationWriteResult> WriteSnapshotAsync(
            long expectedVersion,
            ConfigurationChangeSet changes,
            CancellationToken cancellationToken = default)
        {
            WriteSnapshotCalls++;
            return ValueTask.FromResult(ConfigurationWriteResult.Success(expectedVersion + 1));
        }

        public ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadExtensionSettingsAsync(
            string extensionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationReadResult<ExtensionSettingsConfiguration>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.NotFound)));

        public ValueTask<ConfigurationWriteResult> WriteExtensionSettingsAsync(
            string extensionId,
            long expectedVersion,
            ExtensionSettingsConfiguration settings,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationWriteResult.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Unsupported)));
    }
}
