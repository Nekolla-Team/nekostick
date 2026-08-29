using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostConfigurationPublisherSettingsEventTests
{
    private const string FirstExtensionId = "first.settings.extension";
    private const string SecondExtensionId = "second.settings.extension";

    [Fact]
    public async Task ChangedSettingsEmitsExtensionSettingsChangedForOwnerOnly()
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        await LoadAsync(manager, FirstExtensionId, "first.handler", eventCount: 4);
        await LoadAsync(manager, SecondExtensionId, "second.handler", eventCount: 2);

        await using var publisher = CreatePublisher(manager);
        var previous = CreateSnapshot(
            1,
            Settings(FirstExtensionId, 1, "a"),
            Settings(SecondExtensionId, 1, "b"));
        var current = CreateSnapshot(
            2,
            Settings(FirstExtensionId, 2, "a-changed"),
            Settings(SecondExtensionId, 1, "b"));

        PublishSnapshotEvents(publisher, current, previous);

        var firstBody = await HandleEventsAsync(manager, "first.handler");
        var expectedPayload = JsonSerializer.Serialize(new { extensionId = FirstExtensionId });
        Assert.Contains(expectedPayload, firstBody, StringComparison.Ordinal);

        var secondBody = await HandleEventsAsync(manager, "second.handler");
        Assert.DoesNotContain(expectedPayload, secondBody, StringComparison.Ordinal);

        Assert.Equal(ExtensionLoadState.Loaded, manager.GetStatus(FirstExtensionId)!.State);
        Assert.Equal(ExtensionLoadState.Loaded, manager.GetStatus(SecondExtensionId)!.State);
    }

    [Fact]
    public async Task UnchangedSettingsEmitNoExtensionSettingsChanged()
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        await LoadAsync(manager, FirstExtensionId, "first.handler", eventCount: 3);
        await LoadAsync(manager, SecondExtensionId, "second.handler", eventCount: 2);

        await using var publisher = CreatePublisher(manager);
        var previous = CreateSnapshot(
            1,
            Settings(FirstExtensionId, 1, "a"),
            Settings(SecondExtensionId, 1, "b"));
        var current = CreateSnapshot(
            2,
            Settings(FirstExtensionId, 1, "a"),
            Settings(SecondExtensionId, 1, "b"));

        PublishSnapshotEvents(publisher, current, previous);

        var expectedPayload = JsonSerializer.Serialize(new { extensionId = FirstExtensionId });
        var firstBody = await HandleEventsAsync(manager, "first.handler");
        Assert.DoesNotContain(expectedPayload, firstBody, StringComparison.Ordinal);

        var secondBody = await HandleEventsAsync(manager, "second.handler");
        Assert.DoesNotContain(expectedPayload, secondBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddedSettingsEntryEmitsExtensionSettingsChanged()
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        await LoadAsync(manager, FirstExtensionId, "first.handler", eventCount: 3);

        await using var publisher = CreatePublisher(manager);
        var previous = CreateSnapshot(1);
        var current = CreateSnapshot(2, Settings(FirstExtensionId, 1, "a"));

        PublishSnapshotEvents(publisher, current, previous);

        var body = await HandleEventsAsync(manager, "first.handler");
        var expectedPayload = JsonSerializer.Serialize(new { extensionId = FirstExtensionId });
        Assert.Contains(expectedPayload, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovedSettingsEntryEmitsExtensionSettingsChanged()
    {
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        await LoadAsync(manager, FirstExtensionId, "first.handler", eventCount: 3);

        await using var publisher = CreatePublisher(manager);
        var previous = CreateSnapshot(1, Settings(FirstExtensionId, 1, "a"));
        var current = CreateSnapshot(2);

        PublishSnapshotEvents(publisher, current, previous);

        var body = await HandleEventsAsync(manager, "first.handler");
        var expectedPayload = JsonSerializer.Serialize(new { extensionId = FirstExtensionId });
        Assert.Contains(expectedPayload, body, StringComparison.Ordinal);
    }

    private static async ValueTask<string> HandleEventsAsync(
        ExtensionRuntimeManager manager,
        string handlerId)
    {
        var result = await manager.HandleAsync(
            handlerId,
            new ExtensionHandlerRequest("GET", "/events"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, result.State);
        return Encoding.UTF8.GetString(result.Response!.Body.AsSpan());
    }

    private static async ValueTask<ExtensionManifest> LoadAsync(
        ExtensionRuntimeManager manager,
        string extensionId,
        string handlerId,
        int eventCount)
    {
        using var directory = TestExtensionDirectory.CreateJson(RuntimeManifestJson(extensionId));
        var manifest = Discover(directory.RootPath);
        var result = await manager.LoadAsync(
            manifest,
            Settings(extensionId, handlerId, eventCount),
            TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.FailureCode.ToString());
        return manifest;
    }

    private static HostConfigurationPublisher CreatePublisher(ExtensionRuntimeManager manager) =>
        new(
            new HostConfigurationSnapshotHolder(),
            manager,
            new HostNodeOptions(skipExtensions: true, disableSupervisor: false, readOnly: false),
            NullLogger<HostConfigurationPublisher>.Instance);

    private static void PublishSnapshotEvents(
        HostConfigurationPublisher publisher,
        HostConfigurationSnapshot current,
        HostConfigurationSnapshot? previous)
    {
        var method = typeof(HostConfigurationPublisher).GetMethod(
            "PublishSnapshotEvents",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(publisher, [current, previous]);
    }

    private static HostConfigurationSnapshot CreateSnapshot(
        long version,
        params ExtensionSettingsConfiguration[] settings) =>
        new(
            version,
            new GlobalSettingsConfiguration(version: version),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray.Create(settings));

    private static ExtensionSettingsConfiguration Settings(
        string extensionId,
        string handlerId,
        int eventCount,
        long version = 1,
        string label = "settings-event") =>
        new(
            extensionId,
            1,
            JsonSerializer.Serialize(new
            {
                label,
                handlerId,
                publishCoreEvents = true,
                eventCount
            }),
            version);

    private static ExtensionSettingsConfiguration Settings(
        string extensionId,
        long version,
        string label) =>
        new(
            extensionId,
            1,
            JsonSerializer.Serialize(new { label }),
            version);

    private static string RuntimeManifestJson(string id) =>
        "{\n" +
        "  \"schemaVersion\": 1,\n" +
        "  \"id\": " + JsonSerializer.Serialize(id) + ",\n" +
        "  \"version\": \"1.0.0\",\n" +
        "  \"entryAssembly\": \"Fixtures.Extension.dll\",\n" +
        "  \"entryType\": \"Nekolla.Nekostick.Tests.Fixtures.Extension.FixtureEntrypoint\",\n" +
        "  \"dependencies\": [],\n" +
        "  \"requiredHostApiVersion\": \">=1.0.0\"\n" +
        "}";

    private static ExtensionManifest Discover(string rootPath)
    {
        var result = ExtensionManifestDiscovery.Discover(rootPath);
        Assert.True(result.Succeeded, result.FailureCode.ToString());
        return result.Manifest!;
    }
}
