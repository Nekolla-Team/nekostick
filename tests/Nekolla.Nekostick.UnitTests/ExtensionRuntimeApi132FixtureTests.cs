using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed partial class ExtensionRuntimeTests
{
    [Fact]
    public async Task SettingsChangedEventReachesOnlyTheSubscribedOwner()
    {
        const string firstId = "first.settings.extension";
        const string secondId = "second.settings.extension";
        using var firstFixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson(firstId));
        using var secondFixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson(secondId));
        var firstManifest = Discover(firstFixture.RootPath);
        var secondManifest = Discover(secondFixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        Assert.True((await manager.LoadAsync(
            firstManifest,
            Settings(firstId, subscribeSettingsChanged: true),
            TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await manager.LoadAsync(
            secondManifest,
            Settings(secondId, handlerId: "second.handler", subscribeSettingsChanged: true),
            TestContext.Current.CancellationToken)).Succeeded);

        var payload = JsonSerializer.Serialize(new { extensionId = firstId });
        Assert.Equal(
            1,
            manager.PublishCoreEvent(
                new ExtensionCoreEvent(
                    ExtensionCoreEventKind.ExtensionSettingsChanged,
                    1,
                    payload),
                firstId));

        var firstResult = await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/settings-changed"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, firstResult.State);
        var firstBody = Body(firstResult);
        Assert.Contains("settings-changed=1", firstBody, StringComparison.Ordinal);
        Assert.Contains("settings-read=Unsupported", firstBody, StringComparison.Ordinal);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var secondResult = await manager.HandleAsync(
            "second.handler",
            new ExtensionHandlerRequest("GET", "/settings-changed"),
            timeout.Token);
        Assert.Equal(ExtensionInvocationState.Handled, secondResult.State);
        var secondBody = Body(secondResult);
        Assert.Contains("settings-changed=0", secondBody, StringComparison.Ordinal);
        Assert.DoesNotContain("settings-changed=1", secondBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DataDirectoryFlowsToExtensionBridge()
    {
        const string extensionId = "data.directory.extension";
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson(extensionId));
        var manifest = Discover(fixture.RootPath);
        var dataDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var manager = new ExtensionRuntimeManager(
            HostApiVersion.Current,
            dataDirectory: dataDirectory);

        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(extensionId, readDataDirectory: true),
            TestContext.Current.CancellationToken)).Succeeded);

        var result = await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/data-directory"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, result.State);
        var body = Body(result);
        Assert.Contains($"data-directory={dataDirectory}", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamingHandlerEchoesRequestBody()
    {
        const string extensionId = "streaming.echo.extension";
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson(extensionId));
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(extensionId, registerStreamingHandler: true),
            TestContext.Current.CancellationToken)).Succeeded);

        var requestBytes = Encoding.UTF8.GetBytes("hello-stream");
        var requestStream = new MemoryStream(requestBytes);
        await using var result = await manager.HandleStreamingAsync(
            "fixture.streaming",
            new ExtensionStreamingRequest("POST", "/stream", bodyStream: requestStream),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, result.State);
        Assert.NotNull(result.Response);
        var responseStream = result.Response!.BodyStream;
        using var reader = new StreamReader(responseStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Equal("hello-stream", body);
        Assert.Contains("X-Fixture-Label", result.Response!.Headers.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StreamingHandlerWithMemoryStreamAtEndYieldsEmptyBody()
    {
        const string extensionId = "streaming.empty.extension";
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson(extensionId));
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(
                extensionId,
                registerStreamingHandler: true,
                streamingHandlerEmptyResponse: true),
            TestContext.Current.CancellationToken)).Succeeded);

        var requestStream = new MemoryStream(Encoding.UTF8.GetBytes("discard"));
        await using var result = await manager.HandleStreamingAsync(
            "fixture.streaming",
            new ExtensionStreamingRequest("POST", "/stream", bodyStream: requestStream),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, result.State);
        Assert.NotNull(result.Response);
        var responseStream = result.Response!.BodyStream;
        using var reader = new StreamReader(responseStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Empty(body);
        Assert.Equal(200, result.Response!.StatusCode);
    }
}
