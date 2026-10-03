using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed partial class ExtensionRuntimeTests
{
    [Fact]
    public async Task NegotiatedCapabilityVersionsFailClosedAndRespectFactoryBoundaries()
    {
        using var api10Fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        using var api11Fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        using var api12Fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        using var api13Fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        using var api14Fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var api10Manifest = Discover(api10Fixture.RootPath);
        var api11Manifest = Discover(api11Fixture.RootPath);
        var api12Manifest = Discover(api12Fixture.RootPath);
        var api13Manifest = Discover(api13Fixture.RootPath);
        var api14Manifest = Discover(api14Fixture.RootPath);
        var api10Factory = new RecordingCapabilityFactory();
        var api11Factory = new RecordingCapabilityFactory();
        var api12Factory = new RecordingCapabilityFactory();
        var api13Factory = new RecordingCapabilityFactory(provideServiceOutput: true);
        var api14Factory = new RecordingCapabilityFactory(provideServiceOutput: true);

        await using (var api10 = new ExtensionRuntimeManager(
                         new HostApiVersion(1, 0, 0),
                         capabilityFactory: api10Factory))
        {
            Assert.True(
                (await api10.LoadAsync(
                    api10Manifest,
                    Settings(
                        api10Manifest.Id,
                        label: "api10",
                        verifyBridgeCapabilities: true,
                        requestLifecycleFromStart: true),
                    TestContext.Current.CancellationToken)).Succeeded);

            var result = await api10.HandleAsync(
                "fixture.handler",
                new ExtensionHandlerRequest("GET", "/api10-gated"),
                TestContext.Current.CancellationToken);
            var body = Body(result);
            Assert.Contains("lifecycle=:;", body, StringComparison.Ordinal);
            Assert.Contains("start-lifecycle=reload=Unsupported;unload=Unsupported;state=", body, StringComparison.Ordinal);
            Assert.Contains("legacy=fixture.extension.deterministic:1:0", body, StringComparison.Ordinal);
            Assert.Contains("configRead=Unsupported", body, StringComparison.Ordinal);
            Assert.Contains("fullRead=Unsupported", body, StringComparison.Ordinal);
            Assert.Contains("routeRead=Unsupported", body, StringComparison.Ordinal);
            Assert.Contains("serviceRead=Unsupported", body, StringComparison.Ordinal);
            Assert.Contains("endpoints=0", body, StringComparison.Ordinal);
            Assert.Contains("api14=bridge14;serviceOutputOpen=Unsupported;serviceOutputSubscribe=Unsupported", body, StringComparison.Ordinal);
            Assert.Contains("start-lifecycle=reload=Unsupported;unload=Unsupported", body, StringComparison.Ordinal);
            Assert.Equal(0, api10Factory.CreateCount);
            Assert.Equal(
                new HostApiVersion(1, 0, 0),
                UnsupportedExtensionCapabilities.Create(new HostApiVersion(1, 0, 0)).ConfigurationApi.ApiVersion);
        }

        await using (var api11 = new ExtensionRuntimeManager(
                         new HostApiVersion(1, 1, 0),
                         capabilityFactory: api11Factory))
        {
            Assert.True(
                (await api11.LoadAsync(
                    api11Manifest,
                    Settings(api11Manifest.Id, label: "api11", verifyBridgeCapabilities: true),
                    TestContext.Current.CancellationToken)).Succeeded);

            var result = await api11.HandleAsync(
                "fixture.handler",
                new ExtensionHandlerRequest("GET", "/api11-gated"),
                TestContext.Current.CancellationToken);
            var body = Body(result);
            Assert.Contains("fullRead=Unsupported;fullReplace=Unsupported", body, StringComparison.Ordinal);
            Assert.Equal(1, api11Factory.CreateCount);
            Assert.NotNull(api11Factory.LastFullConfiguration);
            Assert.Equal(0, api11Factory.LastFullConfiguration!.ReadCount);
            Assert.Equal(0, api11Factory.LastFullConfiguration.WriteCount);
        }

        await using (var api12 = new ExtensionRuntimeManager(
                         new HostApiVersion(1, 2, 0),
                         capabilityFactory: api12Factory))
        {
            Assert.True(
                (await api12.LoadAsync(
                    api12Manifest,
                    Settings(api12Manifest.Id, label: "api12", verifyBridgeCapabilities: true),
                    TestContext.Current.CancellationToken)).Succeeded);

            var result = await api12.HandleAsync(
                "fixture.handler",
                new ExtensionHandlerRequest("GET", "/api12-gated"),
                TestContext.Current.CancellationToken);
            var body = Body(result);
            Assert.Contains("fullRead=NotFound;fullReplace=NotFound", body, StringComparison.Ordinal);
            Assert.Equal(1, api12Factory.CreateCount);
            Assert.NotNull(api12Factory.LastFullConfiguration);
            Assert.Equal(1, api12Factory.LastFullConfiguration!.ReadCount);
            Assert.Equal(1, api12Factory.LastFullConfiguration.WriteCount);
        }

        await using (var api13 = new ExtensionRuntimeManager(
                         new HostApiVersion(1, 3, 2),
                         capabilityFactory: api13Factory))
        {
            Assert.True(
                (await api13.LoadAsync(
                    api13Manifest,
                    Settings(api13Manifest.Id, label: "api13", verifyBridgeCapabilities: true),
                    TestContext.Current.CancellationToken)).Succeeded);

            var result = await api13.HandleAsync(
                "fixture.handler",
                new ExtensionHandlerRequest("GET", "/api13-gated"),
                TestContext.Current.CancellationToken);
            Assert.Equal(ExtensionInvocationState.Handled, result.State);
            var body = Body(result);
            Assert.Contains(
                "api14=bridge14;serviceOutputOpen=Unsupported;serviceOutputSubscribe=Unsupported",
                body,
                StringComparison.Ordinal);
            Assert.Equal(1, api13Factory.CreateCount);
        }

        await using (var api14 = new ExtensionRuntimeManager(
                         new HostApiVersion(1, 4, 0),
                         capabilityFactory: api14Factory))
        {
            Assert.True(
                (await api14.LoadAsync(
                    api14Manifest,
                    Settings(api14Manifest.Id, label: "api14", verifyBridgeCapabilities: true),
                    TestContext.Current.CancellationToken)).Succeeded);

            var result = await api14.HandleAsync(
                "fixture.handler",
                new ExtensionHandlerRequest("GET", "/api14-gated"),
                TestContext.Current.CancellationToken);
            var body = Body(result);
            Assert.Equal(ExtensionInvocationState.Handled, result.State);
            Assert.Contains("api14=bridge14;serviceOutputOpen=Opened;serviceOutputSubscribe=Subscribed", body, StringComparison.Ordinal);
            Assert.Equal(1, api14Factory.CreateCount);
        }

    }

    private sealed class RecordingCapabilityFactory : IExtensionCapabilityFactory
    {
        private readonly bool _provideServiceOutput;

        internal RecordingCapabilityFactory(bool provideServiceOutput = false) =>
            _provideServiceOutput = provideServiceOutput;

        internal int CreateCount;
        internal RecordingFullConfiguration? LastFullConfiguration { get; private set; }

        public ExtensionCapabilitySet Create(string extensionId, Func<string, bool> handlerIsOwned)
        {
            Interlocked.Increment(ref CreateCount);
            var unsupported = UnsupportedExtensionCapabilities.Create();
            LastFullConfiguration = new RecordingFullConfiguration();
            return new ExtensionCapabilitySet(
                unsupported.ConfigurationApi,
                unsupported.Routes,
                unsupported.Services,
                unsupported.Endpoints,
                LastFullConfiguration,
                unsupported.Supervisor,
                unsupported.RouteEvents,
                unsupported.LogWriter,
                unsupported.ExtensionManagement,
                unsupported.HostInfo,
                _provideServiceOutput ? new RecordingVersionGateServiceOutput() : null,
                null);
        }
    }

    private sealed class RecordingVersionGateServiceOutput : IExtensionServiceOutputApi
    {
        public ValueTask<ExtensionServiceOutputStreamResult> OpenStreamAsync(
            Guid serviceId,
            ExtensionServiceOutputStream stream,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtensionServiceOutputStreamResult(
                true,
                ExtensionServiceOutputCode.Opened,
                serviceId,
                new MemoryStream()));
        public ValueTask<ExtensionServiceLogSubscriptionResult> SubscribeAsync(
            Guid serviceId,
            IExtensionServiceLogSink sink,
            long? sinceSequence = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtensionServiceLogSubscriptionResult(
                true,
                ExtensionServiceLogCode.Subscribed,
                serviceId,
                new RecordingSubscription()));
    }


    private sealed class RecordingSubscription : IExtensionServiceLogSubscription
    {
        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }


    private sealed class RecordingFullConfiguration : IExtensionFullConfigurationApi
    {
        internal int ReadCount;
        internal int WriteCount;

        public ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ReadCount);
            return ValueTask.FromResult(
                ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.NotFound)));
        }

        public ValueTask<ConfigurationWriteResult> ReplaceAsync(
            long expectedVersion,
            ConfigurationChangeSet changes,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref WriteCount);
            return ValueTask.FromResult(
                ConfigurationWriteResult.Failure(
                    new ConfigurationError(ConfigurationErrorCode.NotFound)));
        }
    }
}
