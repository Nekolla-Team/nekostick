using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed partial class ExtensionRuntimeTests
{
    [Fact]
    public async Task StagedFixtureProbeDistinguishesApi13FromNegotiatedApi12()
    {
        using var currentFixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        using var negotiated12Fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var currentManifest = Discover(currentFixture.RootPath);
        var negotiated12Manifest = Discover(negotiated12Fixture.RootPath);

        var capabilityFactory = new Api13ProbeCapabilityFactory();
        await using var current = new ExtensionRuntimeManager(
            HostApiVersion.Current,
            capabilityFactory: capabilityFactory);
        Assert.True(
            (await current.LoadAsync(
                currentManifest,
                Settings(
                    currentManifest.Id,
                    label: "api13-probe",
                    verifyBridgeCapabilities: true),
                TestContext.Current.CancellationToken)).Succeeded);

        var currentResult = await current.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/api13-current"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, currentResult.State);
        var currentBody = Body(currentResult);
        Assert.Contains("api=1.3.0", currentBody, StringComparison.Ordinal);
        Assert.Contains(
            "api13=Supported;sibling=True;supervisor=NotFound;routeSubscribe=True;routeHook=True;logWriter=Called",
            currentBody,
            StringComparison.Ordinal);
        AssertLegacyBridgeOutput(currentBody, currentManifest.Id);
        Assert.Equal(1, capabilityFactory.LogWriter.WriteCount);

        await using var negotiated12 = new ExtensionRuntimeManager(
            new HostApiVersion(1, 2, 0),
            capabilityFactory: capabilityFactory);
        Assert.True(
            (await negotiated12.LoadAsync(
                negotiated12Manifest,
                Settings(
                    negotiated12Manifest.Id,
                    label: "api12-probe",
                    verifyBridgeCapabilities: true),
                TestContext.Current.CancellationToken)).Succeeded);

        var negotiatedResult = await negotiated12.HandleAsync(
            "fixture.handler",
            new ExtensionHandlerRequest("GET", "/api13-12"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionInvocationState.Handled, negotiatedResult.State);
        var negotiatedBody = Body(negotiatedResult);
        Assert.Contains("api=1.2.0", negotiatedBody, StringComparison.Ordinal);
        Assert.Contains(
            "api13=Unsupported;sibling=True;supervisor=Unsupported;routeSubscribe=False;routeHook=False;logWriter=Unsupported",
            negotiatedBody,
            StringComparison.Ordinal);
        AssertLegacyBridgeOutput(negotiatedBody, negotiated12Manifest.Id);
        Assert.Contains("api12-probe:started", negotiatedBody, StringComparison.Ordinal);
        Assert.Equal(1, capabilityFactory.LogWriter.WriteCount);
    }

    private static void AssertLegacyBridgeOutput(string body, string extensionId)
    {
        Assert.Contains($"legacy={extensionId}:1:0;properties=True", body, StringComparison.Ordinal);
        Assert.Contains("configRead=Unsupported;configApply=Unsupported", body, StringComparison.Ordinal);
        Assert.Contains("settingsRead=Unsupported;settingsWrite=Unsupported", body, StringComparison.Ordinal);
        Assert.Contains("fullRead=Unsupported;fullReplace=Unsupported", body, StringComparison.Ordinal);
        Assert.Contains("routeRead=Unsupported;routeRemove=Unsupported", body, StringComparison.Ordinal);
        Assert.Contains(
            "serviceRead=Unsupported;serviceRemove=Unsupported;serviceStart=Unsupported;serviceStop=Unsupported;serviceRestart=Unsupported",
            body,
            StringComparison.Ordinal);
    }

    private sealed class Api13ProbeCapabilityFactory : IExtensionCapabilityFactory, IExtensionCapabilityFactoryRouteEvents
    {
        internal RecordingLogWriter LogWriter { get; } = new();

        public ExtensionCapabilitySet Create(string extensionId, Func<string, bool> handlerIsOwned) =>
            UnsupportedExtensionCapabilities.Create();

        public ExtensionCapabilitySet CreateWithRouteEvents(
            string extensionId,
            Func<string, bool> handlerIsOwned,
            IExtensionRouteEvents routeEvents)
        {
            var unsupported = UnsupportedExtensionCapabilities.Create();
            return new ExtensionCapabilitySet(
                unsupported.ConfigurationApi,
                unsupported.Routes,
                unsupported.Services,
                unsupported.Endpoints,
                unsupported.FullConfiguration,
                new RecordingSupervisor(),
                routeEvents,
                LogWriter);
        }
    }

    private sealed class RecordingSupervisor : IExtensionSupervisorApi
    {
        public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>> ReadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>.Success(
                    ImmutableArray<ExtensionServiceRuntimeSnapshot>.Empty));

        public ValueTask<ConfigurationReadResult<ExtensionServiceRuntimeSnapshot?>> GetAsync(
            Guid serviceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ConfigurationReadResult<ExtensionServiceRuntimeSnapshot?>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.NotFound)));
    }

    private sealed class RecordingLogWriter : IExtensionLogWriter
    {
        internal int WriteCount;

        public void WriteText(ExtensionLogLevel level, string text) =>
            Interlocked.Increment(ref WriteCount);
    }
}
