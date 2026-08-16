using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostConfigApiReadOnlyDecoratorTests
{
    private static readonly CancellationToken ReadCancellationToken = new(canceled: false);
    private static readonly CancellationToken WriteCancellationToken = new(canceled: false);

    [Fact]
    public async Task ReadOnlyModeRejectsSnapshotWritesWithoutCallingUnderlyingApi()
    {
        var inner = new RecordingHostConfigApi();
        var decorator = CreateDecorator(inner, readOnly: true);

        var result = await decorator.WriteSnapshotAsync(
            expectedVersion: 1,
            changes: CreateChanges(),
            cancellationToken: WriteCancellationToken);

        AssertUnsupported(result);
        Assert.Equal(0, inner.WriteSnapshotCalls);
    }

    [Fact]
    public async Task ReadOnlyModeRejectsExtensionSettingsWritesWithoutCallingUnderlyingApi()
    {
        var inner = new RecordingHostConfigApi();
        var decorator = CreateDecorator(inner, readOnly: true);

        var result = await decorator.WriteExtensionSettingsAsync(
            extensionId: "sample.extension",
            expectedVersion: 0,
            settings: CreateSettings(),
            cancellationToken: WriteCancellationToken);

        AssertUnsupported(result);
        Assert.Equal(0, inner.WriteExtensionSettingsCalls);
    }

    [Fact]
    public async Task ReadOnlyModeDelegatesReadsAndApiVersion()
    {
        var inner = new RecordingHostConfigApi();
        var decorator = CreateDecorator(inner, readOnly: true);

        var snapshotResult = await decorator.ReadSnapshotAsync(ReadCancellationToken);
        var settingsResult = await decorator.ReadExtensionSettingsAsync(
            "sample.extension",
            ReadCancellationToken);

        Assert.Equal(inner.ApiVersion, decorator.ApiVersion);
        Assert.True(snapshotResult.IsSuccess);
        Assert.Same(inner.Snapshot, snapshotResult.Value);
        Assert.True(settingsResult.IsSuccess);
        Assert.Same(inner.Settings, settingsResult.Value);
        Assert.Equal(1, inner.ReadSnapshotCalls);
        Assert.Equal(ReadCancellationToken, inner.LastReadSnapshotCancellationToken);
        Assert.Equal(1, inner.ReadExtensionSettingsCalls);
        Assert.Equal(ReadCancellationToken, inner.LastReadExtensionSettingsCancellationToken);
    }

    [Fact]
    public async Task WritableModeDelegatesBothWrites()
    {
        var inner = new RecordingHostConfigApi();
        var decorator = CreateDecorator(inner, readOnly: false);

        var snapshotResult = await decorator.WriteSnapshotAsync(
            expectedVersion: 3,
            changes: CreateChanges(),
            cancellationToken: WriteCancellationToken);
        var settingsResult = await decorator.WriteExtensionSettingsAsync(
            extensionId: "sample.extension",
            expectedVersion: 0,
            settings: CreateSettings(),
            cancellationToken: WriteCancellationToken);

        Assert.True(snapshotResult.IsSuccess);
        Assert.Equal(41L, snapshotResult.NewVersion);
        Assert.Equal(1, inner.WriteSnapshotCalls);
        Assert.Equal(WriteCancellationToken, inner.LastWriteSnapshotCancellationToken);
        Assert.True(settingsResult.IsSuccess);
        Assert.Equal(42L, settingsResult.NewVersion);
        Assert.Equal(1, inner.WriteExtensionSettingsCalls);
        Assert.Equal(WriteCancellationToken, inner.LastWriteExtensionSettingsCancellationToken);
    }

    private static HostConfigApiReadOnlyDecorator CreateDecorator(
        IHostConfigApi inner,
        bool readOnly) =>
        new(
            inner,
            new HostRuntimeOptions("synthetic-storage", "test-node", readOnly));

    private static ConfigurationChangeSet CreateChanges() =>
        new(
            new GlobalSettingsConfiguration(version: 1),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);

    private static ExtensionSettingsConfiguration CreateSettings() =>
        new("sample.extension", schemaVersion: 1, settingsJson: "{}", version: 0);

    private static void AssertUnsupported(ConfigurationWriteResult result)
    {
        Assert.False(result.IsSuccess);
        Assert.Null(result.NewVersion);
        var error = Assert.Single(result.Errors);
        Assert.Equal(ConfigurationErrorCode.Unsupported, error.Code);
    }

    private sealed class RecordingHostConfigApi : IHostConfigApi
    {
        public HostConfigurationSnapshot Snapshot { get; } = new(
            1,
            new GlobalSettingsConfiguration(version: 1),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);

        public ExtensionSettingsConfiguration Settings { get; } =
            new("sample.extension", schemaVersion: 1, settingsJson: "{}", version: 0);

        public HostApiVersion ApiVersion { get; } = new(7, 8, 9);

        public int ReadSnapshotCalls { get; private set; }

        public int WriteSnapshotCalls { get; private set; }

        public int ReadExtensionSettingsCalls { get; private set; }

        public int WriteExtensionSettingsCalls { get; private set; }

        public CancellationToken LastReadSnapshotCancellationToken { get; private set; }

        public CancellationToken LastWriteSnapshotCancellationToken { get; private set; }

        public CancellationToken LastReadExtensionSettingsCancellationToken { get; private set; }

        public CancellationToken LastWriteExtensionSettingsCancellationToken { get; private set; }

        public ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            ReadSnapshotCalls++;
            LastReadSnapshotCancellationToken = cancellationToken;
            return ValueTask.FromResult(ConfigurationReadResult<HostConfigurationSnapshot>.Success(Snapshot));
        }

        public ValueTask<ConfigurationWriteResult> WriteSnapshotAsync(
            long expectedVersion,
            ConfigurationChangeSet changes,
            CancellationToken cancellationToken = default)
        {
            WriteSnapshotCalls++;
            LastWriteSnapshotCancellationToken = cancellationToken;
            return ValueTask.FromResult(ConfigurationWriteResult.Success(41));
        }

        public ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadExtensionSettingsAsync(
            string extensionId,
            CancellationToken cancellationToken = default)
        {
            ReadExtensionSettingsCalls++;
            LastReadExtensionSettingsCancellationToken = cancellationToken;
            return ValueTask.FromResult(
                ConfigurationReadResult<ExtensionSettingsConfiguration>.Success(Settings));
        }

        public ValueTask<ConfigurationWriteResult> WriteExtensionSettingsAsync(
            string extensionId,
            long expectedVersion,
            ExtensionSettingsConfiguration settings,
            CancellationToken cancellationToken = default)
        {
            WriteExtensionSettingsCalls++;
            LastWriteExtensionSettingsCancellationToken = cancellationToken;
            return ValueTask.FromResult(ConfigurationWriteResult.Success(42));
        }
    }
}
