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

    [Fact]
    public void LoaderLoadsThroughAPerContentShadowLink()
    {
        const string extensionId = "fixture.extension.shadow.link";
        var hash = new string('c', 64);
        var linkPath = Path.Combine(ShadowLinkRoot, extensionId + "-" + hash);
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson(extensionId));
        var manifest = Discover(fixture.RootPath);
        var loader = new CollectibleExtensionLoader(new SemVersion(1, 0, 0));
        try
        {
            var loaded = loader.Load(manifest, "sha256:" + hash);

            Assert.True(loaded.Succeeded, loaded.FailureCode.ToString());
            Assert.NotNull(new DirectoryInfo(linkPath).LinkTarget);
            Assert.NotEmpty(Directory.GetFiles(linkPath, "Fixtures.Extension.dll"));
            loaded.Handle!.Dispose();
        }
        finally
        {
            DeleteShadowLink(linkPath);
        }
    }

    [Fact]
    public void LoaderReadsFreshBytesAfterAnInPlaceReplacementWithANewContentHash()
    {
        const string extensionId = "fixture.extension.shadow.fresh";
        var firstHash = new string('d', 64);
        var secondHash = new string('e', 64);
        var firstLink = Path.Combine(ShadowLinkRoot, extensionId + "-" + firstHash);
        var secondLink = Path.Combine(ShadowLinkRoot, extensionId + "-" + secondHash);
        using var fixture = TestExtensionDirectory.CreateJson(RuntimeManifestJson(extensionId));
        var manifest = Discover(fixture.RootPath);
        var loader = new CollectibleExtensionLoader(new SemVersion(1, 0, 0));
        try
        {
            var first = loader.Load(manifest, "sha256:" + firstHash);
            Assert.True(first.Succeeded, first.FailureCode.ToString());

            // Replace the payload in place while the previous generation's context stays alive.
            var entryPath = Path.Combine(fixture.RootPath, "Fixtures.Extension.dll");
            var stagedPath = entryPath + ".staged";
            File.WriteAllBytes(stagedPath, new byte[64 * 1024]);
            File.Move(stagedPath, entryPath, overwrite: true);

            // A stale image cached for the real path would load successfully; the fresh garbage
            // bytes behind the new shadow path must fail instead.
            var second = loader.Load(manifest, "sha256:" + secondHash);

            Assert.False(second.Succeeded);
            Assert.Equal(ExtensionFailureCode.LoadFailed, second.FailureCode);
            first.Handle!.Dispose();
        }
        finally
        {
            DeleteShadowLink(firstLink);
            DeleteShadowLink(secondLink);
        }
    }

    private static readonly string ShadowLinkRoot = Path.Combine(
        "/tmp",
        "nekostick",
        "extension-assembly-temp");

    private static void DeleteShadowLink(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is not null)
            {
                info.Delete();
            }
        }
        catch (IOException)
        {
        }
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
        string streamingHandlerId = "fixture.streaming",
        bool registerStreamingHandler = false,
        bool streamingHandlerEmptyResponse = false,
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
        int unregisterBarrierPort = 0,
        bool subscribeSettingsChanged = false,
        bool readDataDirectory = false)
    {
        var json = JsonSerializer.Serialize(new
        {
            label,
            handlerId,
            streamingHandlerId,
            registerStreamingHandler,
            streamingHandlerEmptyResponse,
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
            unregisterBarrierPort,
            subscribeSettingsChanged,
            readDataDirectory
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
