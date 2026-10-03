using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostConfigurationPublisherDedupTests
{
    private const string ReloadExtensionId = "publisher.reload.extension";

    [Fact]
    public async Task VersionBumpEmitsEmptySummaryAndRepeatedSnapshotIsDeduplicated()
    {
        var logger = new CapturingLogger<HostConfigurationPublisher>();
        await using var runtimeManager = new ExtensionRuntimeManager(HostApiVersion.Current);
        await using var publisher = CreatePublisher(runtimeManager, logger);

        Assert.Equal(PublishOutcome.Published, await publisher.PublishAsync(CreateSnapshot(16), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(PublishOutcome.Published, await publisher.PublishAsync(CreateSnapshot(17), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(PublishOutcome.Published, await publisher.PublishAsync(CreateSnapshot(17), cancellationToken: TestContext.Current.CancellationToken));

        var applied = logger.Entries.Where(static entry => entry.EventId.Id == 1109).ToArray();
        Assert.Equal(2, applied.Length);
        Assert.Equal("unknown", applied[1].Fields["CommittedBy"]);
        Assert.Equal("none", applied[1].Fields["Changes"]);
        Assert.False((bool)applied[1].Fields["ForcedReloadRequested"]!);
        Assert.True((bool)applied[0].Fields["GenerationChanged"]!);
        Assert.False((bool)applied[1].Fields["GenerationChanged"]!);

        var suppressed = Assert.Single(logger.Entries, static entry => entry.EventId.Id == 1110);
        Assert.Equal(LogLevel.Information, suppressed.LogLevel);
        Assert.Equal(17L, suppressed.Fields["Version"]);
        Assert.Equal("duplicate revision, generation unchanged", suppressed.Fields["Reason"]);
    }

    [Fact]
    public async Task ForcedReloadRequestBypassesDedupAndIsRecorded()
    {
        using var stagedExtension = TestExtensionDirectory.CreateJson(RuntimeManifestJson(ReloadExtensionId));
        var extensionsRoot = StageExtension(stagedExtension, ReloadExtensionId);
        var logger = new CapturingLogger<HostConfigurationPublisher>();
        await using var runtimeManager = new ExtensionRuntimeManager(HostApiVersion.Current);
        await using var publisher = new HostConfigurationPublisher(
            new HostConfigurationSnapshotHolder(),
            runtimeManager,
            new HostNodeOptions(
                skipExtensions: false,
                disableSupervisor: true,
                readOnly: true,
                extensionsRootPath: extensionsRoot),
            logger);
        var snapshot = CreateLoadedSnapshot();

        Assert.Equal(PublishOutcome.Published, await publisher.PublishAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(
            PublishOutcome.Published,
            await publisher.PublishAsync(
                snapshot,
                ImmutableHashSet.Create(StringComparer.Ordinal, ReloadExtensionId),
                cancellationToken: TestContext.Current.CancellationToken));

        var applied = logger.Entries.Where(static entry => entry.EventId.Id == 1109).ToArray();
        Assert.Equal(2, applied.Length);
        Assert.False((bool)applied[0].Fields["ForcedReloadRequested"]!);
        Assert.True((bool)applied[1].Fields["ForcedReloadRequested"]!);
        Assert.True((bool)applied[0].Fields["GenerationChanged"]!);
        Assert.True((bool)applied[1].Fields["GenerationChanged"]!);
    }

    private static string StageExtension(TestExtensionDirectory stagedExtension, string extensionId)
    {
        var extensionsRoot = Path.Combine(stagedExtension.RootPath, "extensions");
        var installPath = Path.Combine(extensionsRoot, extensionId);
        Directory.CreateDirectory(installPath);
        foreach (var file in Directory.EnumerateFiles(stagedExtension.RootPath))
        {
            File.Copy(file, Path.Combine(installPath, Path.GetFileName(file)));
        }

        return extensionsRoot;
    }

    private static string RuntimeManifestJson(string extensionId) =>
        $$"""
        {
          "schemaVersion": 1,
          "id": "{{extensionId}}",
          "version": "1.0.0",
          "entryAssembly": "Fixtures.Extension.dll",
          "entryType": "Nekolla.Nekostick.Tests.Fixtures.Extension.FixtureEntrypoint",
          "dependencies": [],
          "requiredHostApiVersion": ">=1.0.0"
        }
        """;

    private static HostConfigurationSnapshot CreateLoadedSnapshot()
    {
        var now = DateTimeOffset.UnixEpoch;
        return new HostConfigurationSnapshot(
            17,
            new GlobalSettingsConfiguration(version: 17),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray.Create(new ExtensionRecordConfiguration(
                ReloadExtensionId,
                "1.0.0",
                ExtensionLoadState.Loaded,
                now,
                now,
                recordVersion: 1)),
            ImmutableArray.Create(new ExtensionSettingsConfiguration(
                ReloadExtensionId,
                schemaVersion: 1,
                settingsJson: """{"label":"publisher-dedup","handlerId":"publisher.reload.handler","publishCoreEvents":true,"eventCount":1}""",
                version: 1)));
    }


    private static HostConfigurationPublisher CreatePublisher(
        ExtensionRuntimeManager runtimeManager,
        ILogger<HostConfigurationPublisher> logger) =>
        new(
            new HostConfigurationSnapshotHolder(),
            runtimeManager,
            new HostNodeOptions(skipExtensions: true, disableSupervisor: true, readOnly: true),
            logger);

    private static HostConfigurationSnapshot CreateSnapshot(long version) =>
        new(
            version,
            new GlobalSettingsConfiguration(version: 1),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);

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
