using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Tests.Fixtures.Extension;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed partial class ExtensionRuntimeTests
{
    [Fact]
    public void LoaderRejectsContractCatalogEntriesInsideExtensionRoots()
    {
        using var fixture = TestExtensionDirectory.CreateJson(TypedContractManifestJson());
        var manifest = Discover(fixture.RootPath);
        var contractsAssembly = typeof(IExtensionLogger).Assembly;
        var catalog = new ExtensionContractCatalog(
            [new ExtensionContractCatalogEntry(
                contractsAssembly.GetName().FullName!,
                Path.Combine(fixture.RootPath, "Nekolla.Nekostick.Contracts.dll"))]);
        var loader = new CollectibleExtensionLoader(new SemVersion(1, 0, 0), catalog);

        var result = loader.Load(manifest);

        Assert.False(result.Succeeded);
        Assert.Equal(ExtensionFailureCode.ContractCatalogUnavailable, result.FailureCode);
    }

    [Fact]
    public void LoaderRejectsEntryTypesOutsideThePublicEntrypointAbi()
    {
        using var fixture = TestExtensionDirectory.CreateJson(
            RuntimeManifestJson(entryType: typeof(KnownFixtureService).FullName!));
        var manifest = Discover(fixture.RootPath);
        var loader = new CollectibleExtensionLoader(new SemVersion(1, 0, 0));

        var result = loader.Load(manifest);

        Assert.False(result.Succeeded);
        Assert.Equal(ExtensionFailureCode.EntryTypeNotCompatible, result.FailureCode);
    }

    [Fact]
    public void SuccessfulCollectibleLoadHasAReachableBoundedUnloadResult()
    {
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson());
        var manifest = Discover(fixture.RootPath);
        var unload = LoadAndUnloadCollectibleExtension(manifest);

        Assert.True(unload.Succeeded);
        Assert.Equal(ExtensionRuntimeState.Unloaded, unload.State);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ExtensionUnloadResult LoadAndUnloadCollectibleExtension(ExtensionManifest manifest)
    {
        var loader = new CollectibleExtensionLoader(new SemVersion(1, 0, 0));
        var loaded = loader.Load(manifest);

        Assert.True(loaded.Succeeded, loaded.FailureCode.ToString());
        Assert.NotNull(loaded.Handle);
        return loaded.Handle!.Unload();
    }

    private static string Body(ExtensionInvocationResult result) =>
        Encoding.UTF8.GetString(result.Response!.Body.AsSpan());

    private static ExtensionManifest Discover(string rootPath)
    {
        var result = ExtensionManifestDiscovery.Discover(rootPath);
        Assert.True(result.Succeeded, result.FailureCode.ToString());
        return result.Manifest!;
    }

    private static ExtensionSettingsConfiguration Settings(
        string extensionId,
        string label = "fixture",
        string handlerId = "fixture.handler",
        bool startFails = false,
        bool stopFails = false,
        bool previousStoppedFails = false,
        bool handlerFails = false,
        bool registerFallback = false,
        string? duplicateOption = null,
        bool startTask = false,
        bool publishOrderedEvents = false,
        bool publishBoundedEvents = false,
        bool publishCoreEvents = false,
        bool typedContractExchange = false,
        int eventCount = 3,
        bool verifyBridgeCapabilities = false,
        bool requestLifecycleFromHandler = false,
        bool requestLifecycleFromFallback = false,
        bool requestLifecycleFromTask = false,
        bool requestLifecycleFromEvent = false,
        bool unregisterHandlerOnInvocation = false,
        bool unregisterFallbackOnInvocation = false,
        bool reregisterHandlerAfterUnregister = false,
        string? attemptUnregisterHandlerId = null,
        bool attemptUnregisterFallback = false,
        bool includeFallbackCount = false,
        bool requestLifecycleFromStart = false,
        bool requestLifecycleFromPreviousStopped = false,
        bool requestLifecycleFromStop = false,
        int lifecycleObservationPort = 0,
        int unregisterBarrierPort = 0)
    {
        var json = JsonSerializer.Serialize(new
        {
            label,
            handlerId,
            startFails,
            stopFails,
            previousStoppedFails,
            handlerFails,
            registerFallback,
            duplicateHandler = duplicateOption == "duplicateHandler",
            duplicateFallback = duplicateOption == "duplicateFallback",
            startTask,
            publishOrderedEvents,
            publishBoundedEvents,
            publishCoreEvents,
            typedContractExchange,
            eventCount,
            verifyBridgeCapabilities,
            requestLifecycleFromHandler,
            requestLifecycleFromFallback,
            requestLifecycleFromTask,
            requestLifecycleFromEvent,
            unregisterHandlerOnInvocation,
            unregisterFallbackOnInvocation,
            reregisterHandlerAfterUnregister,
            attemptUnregisterHandlerId,
            attemptUnregisterFallback,
            includeFallbackCount,
            requestLifecycleFromStart,
            requestLifecycleFromPreviousStopped,
            requestLifecycleFromStop,
            lifecycleObservationPort,
            unregisterBarrierPort
        });
        return new ExtensionSettingsConfiguration(extensionId, 1, json, 0);
    }

    private static string TypedContractManifestJson()
    {
        var assemblyIdentity = JsonSerializer.Serialize(typeof(IExtensionLogger).Assembly.GetName().FullName);
        var typeIdentity = JsonSerializer.Serialize(typeof(IExtensionLogger).FullName);
        var baseManifest = RuntimeManifestJson();
        return baseManifest[..^1] +
            ",\n  \"exports\": [{\"contractId\": \"fixture.logger\", \"version\": \"1.0.0\", \"assemblyIdentity\": " +
            assemblyIdentity + ", \"typeIdentity\": " + typeIdentity + "}],\n" +
            "  \"imports\": [{\"contractId\": \"fixture.logger\", \"versionRange\": \">=1.0.0\", \"assemblyIdentity\": " +
            assemblyIdentity + ", \"typeIdentity\": " + typeIdentity + "}]\n}";
    }

    private static string RuntimeManifestJson(
        string id = "fixture.extension.deterministic",
        string version = "1.0.0",
        string? entryType = null)
    {
        return "{\n" +
            "  \"schemaVersion\": 1,\n" +
            "  \"id\": " + JsonSerializer.Serialize(id) + ",\n" +
            "  \"version\": " + JsonSerializer.Serialize(version) + ",\n" +
            "  \"entryAssembly\": \"Fixtures.Extension.dll\",\n" +
            "  \"entryType\": " + JsonSerializer.Serialize(
                entryType ?? typeof(FixtureEntrypoint).FullName!) + ",\n" +
            "  \"dependencies\": [],\n" +
            "  \"requiredHostApiVersion\": \">=1.0.0\"\n" +
            "}";
    }
}
