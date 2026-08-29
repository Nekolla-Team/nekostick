using System.Text;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Tests.Fixtures.Extension;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed partial class ExtensionRuntimeTests
{
    [Fact]
    public async Task StreamingHandlerRegistrationIsAcceptedAndDispatchedByHandlerId()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        var load = await manager.LoadAsync(
            manifest,
            Settings(
                manifest.Id,
                registerStreamingHandler: true,
                streamingHandlerId: "fixture.streaming"),
            TestContext.Current.CancellationToken);

        Assert.True(load.Succeeded, load.FailureCode.ToString());

        var request = new ExtensionStreamingRequest(
            "POST",
            "/stream",
            Array.Empty<KeyValuePair<string, IEnumerable<string>>>(),
            new MemoryStream(Encoding.UTF8.GetBytes("hello")),
            false);
        var result = await manager.HandleStreamingAsync(
            "fixture.streaming",
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExtensionInvocationState.Handled, result.State);
        Assert.NotNull(result.Response);
        Assert.NotNull(result.Response!.BodyStream);
        using (result)
        {
            using var reader = new StreamReader(result.Response.BodyStream, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.Equal("hello", body);
        }
    }

    [Fact]
    public async Task StreamingHandlerDispatchedByHandlerIdWhenBufferedHandlerSharesExtension()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(
                manifest.Id,
                handlerId: "fixture.handler",
                registerStreamingHandler: true,
                streamingHandlerId: "fixture.streaming"),
            TestContext.Current.CancellationToken)).Succeeded);

        var buffered = await manager.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/buffered"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, buffered.State);

        var streaming = await manager.HandleStreamingAsync(
            "fixture.streaming",
            new ExtensionStreamingRequest(
                "POST",
                "/stream",
                Array.Empty<KeyValuePair<string, IEnumerable<string>>>(),
                new MemoryStream(Encoding.UTF8.GetBytes("streamed")),
                false),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, streaming.State);
        Assert.NotNull(streaming.Response);
        using (streaming)
        {
            using var reader = new StreamReader(streaming.Response!.BodyStream, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.Equal("streamed", body);
        }
    }

    [Fact]
    public async Task StreamingHandlerRequestLeaseHeldUntilResultIsDisposed()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(
                manifest.Id,
                registerStreamingHandler: true,
                streamingHandlerId: "fixture.streaming"),
            TestContext.Current.CancellationToken)).Succeeded);

        var status = manager.GetStatus(manifest.Id)!;
        Assert.Equal(0, status.ActiveRequests);

        var result = await manager.HandleStreamingAsync(
            "fixture.streaming",
            new ExtensionStreamingRequest(
                "POST",
                "/stream",
                Array.Empty<KeyValuePair<string, IEnumerable<string>>>(),
                new MemoryStream(Encoding.UTF8.GetBytes("lease")),
                false),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExtensionInvocationState.Handled, result.State);
        Assert.Equal(1, manager.GetStatus(manifest.Id)!.ActiveRequests);

        result.Dispose();

        // Lease release is synchronous on Dispose.
        Assert.Equal(0, manager.GetStatus(manifest.Id)!.ActiveRequests);
    }

    [Fact]
    public async Task StreamingHandlerResponseReadFromCurrentPositionEmptyWhenAtEnd()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        Assert.True((await manager.LoadAsync(
            manifest,
            Settings(
                manifest.Id,
                registerStreamingHandler: true,
                streamingHandlerId: "fixture.streaming",
                streamingHandlerEmptyResponse: true),
            TestContext.Current.CancellationToken)).Succeeded);

        var result = await manager.HandleStreamingAsync(
            "fixture.streaming",
            new ExtensionStreamingRequest(
                "POST",
                "/stream",
                Array.Empty<KeyValuePair<string, IEnumerable<string>>>(),
                new MemoryStream(Encoding.UTF8.GetBytes("ignored")),
                false),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExtensionInvocationState.Handled, result.State);
        Assert.NotNull(result.Response);
        using (result)
        {
            using var reader = new StreamReader(result.Response!.BodyStream, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.Equal(string.Empty, body);
        }
    }

    [Fact]
    public async Task StreamingHandlerIdConflictsWithBufferedHandlerAreRejected()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);

        var load = await manager.LoadAsync(
            manifest,
            Settings(
                manifest.Id,
                handlerId: "fixture.handler",
                registerStreamingHandler: true,
                streamingHandlerId: "fixture.handler"),
            TestContext.Current.CancellationToken);

        Assert.False(load.Succeeded);
        Assert.Equal(ExtensionFailureCode.LifecycleFailed, load.FailureCode);
    }
}
