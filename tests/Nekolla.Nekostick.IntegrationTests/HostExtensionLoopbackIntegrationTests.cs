using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Proxy;
using Nekolla.Nekostick.Routing;
using Nekolla.Nekostick.Tests.Fixtures.Extension;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

public sealed class HostExtensionLoopbackIntegrationTests
{
    private const string ExtensionId = "fixture.extension.deterministic";

    [Fact]
    public async Task ExplicitFixtureGenerationServesHandlerAndExactlyOneFallbackOverLoopbackKestrel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixtureRoot = CreateFixtureRoot();
        var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var holder = new HostConfigurationSnapshotHolder();
        try
        {
            var manifestResult = ExtensionManifestDiscovery.Discover(fixtureRoot);
            Assert.True(manifestResult.Succeeded, manifestResult.FailureCode.ToString());
            var settings = new ExtensionSettingsConfiguration(
                ExtensionId,
                schemaVersion: 1,
                settingsJson: JsonSerializer.Serialize(new
                {
                    label = "loopback",
                    handlerId = ExtensionId,
                    registerFallback = true,
                    includeFallbackCount = true
                }),
                version: 1);
            var prepared = await manager.PrepareGenerationAsync(ImmutableArray.Create(
                new ExtensionRuntimeDescriptor(
                    manifestResult.Manifest!,
                    settings,
                    [ExtensionId],
                    includeFallback: true)), previous: null, cancellationToken: cancellationToken);
            Assert.True(prepared.Succeeded, prepared.FailureCode.ToString());
            var preparation = prepared.Preparation!;
            var ready = await preparation.ReadyToPublishAsync(cancellationToken);
            Assert.True(ready.Succeeded, ready.FailureCode.ToString());
            Assert.True(await preparation.CompletePublicationAsync());

            Assert.True(ReplaceWithGeneration(holder, CreateSnapshot(settings), ready.Generation!));
            await using var app = await StartLoopbackAsync(holder, cancellationToken);
            using var handled = await app.Client.GetAsync("/extension", cancellationToken);
            using var fallback = await app.Client.GetAsync("/missing", cancellationToken);

            Assert.Equal(HttpStatusCode.OK, handled.StatusCode);
            Assert.Equal("loopback:started", await handled.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(HttpStatusCode.NotFound, fallback.StatusCode);
            Assert.Equal("loopback:NoRoute:1", await fallback.Content.ReadAsStringAsync(cancellationToken));
        }
        finally
        {
            await holder.DisposeAsync();
            await manager.DisposeAsync();
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LoopbackRouteHonorsHostAndMethodConstraintsBeforeFallback()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixtureRoot = CreateFixtureRoot();
        var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var holder = new HostConfigurationSnapshotHolder();
        try
        {
            var manifestResult = ExtensionManifestDiscovery.Discover(fixtureRoot);
            Assert.True(manifestResult.Succeeded, manifestResult.FailureCode.ToString());
            var settings = new ExtensionSettingsConfiguration(
                ExtensionId,
                schemaVersion: 1,
                settingsJson: JsonSerializer.Serialize(new
                {
                    label = "constrained-loopback",
                    handlerId = ExtensionId,
                    registerFallback = true,
                    includeFallbackCount = true
                }),
                version: 1);
            var prepared = await manager.PrepareGenerationAsync(ImmutableArray.Create(
                new ExtensionRuntimeDescriptor(
                    manifestResult.Manifest!,
                    settings,
                    [ExtensionId],
                    includeFallback: true)), previous: null, cancellationToken: cancellationToken);
            Assert.True(prepared.Succeeded, prepared.FailureCode.ToString());
            var preparation = prepared.Preparation!;
            var ready = await preparation.ReadyToPublishAsync(cancellationToken);
            Assert.True(ready.Succeeded, ready.FailureCode.ToString());
            Assert.True(await preparation.CompletePublicationAsync());

            Assert.True(ReplaceWithGeneration(
                holder,
                CreateSnapshot(
                    settings,
                    ImmutableArray.Create("allowed.integration"),
                    ImmutableArray.Create("POST")),
                ready.Generation!));
            await using var app = await StartLoopbackAsync(holder, cancellationToken);

            app.Client.DefaultRequestHeaders.Host = "wrong.integration";
            using var wrongHost = await app.Client.PostAsync(
                "/extension",
                new StringContent(string.Empty),
                cancellationToken);
            app.Client.DefaultRequestHeaders.Host = "allowed.integration";
            using var wrongMethod = await app.Client.GetAsync("/extension", cancellationToken);
            using var handled = await app.Client.PostAsync(
                "/extension",
                new StringContent(string.Empty),
                cancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, wrongHost.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, wrongMethod.StatusCode);
            Assert.Equal(HttpStatusCode.OK, handled.StatusCode);
            Assert.Equal("constrained-loopback:started", await handled.Content.ReadAsStringAsync(cancellationToken));
        }
        finally
        {
            await holder.DisposeAsync();
            await manager.DisposeAsync();
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }
 
    private static async Task<LoopbackApp> StartLoopbackAsync(
        HostConfigurationSnapshotHolder holder,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(
                IPAddress.Loopback,
                0,
                listenOptions => listenOptions.Protocols = HttpProtocols.Http1));
        builder.Services.AddMicroserviceProxy();
        var app = builder.Build();
        var targetExecutor = HostIntegrationTestSupport.CreateHostTargetExecutor(
            app.Services.GetRequiredService<MicroserviceHttpExecutor>());
        var hostAssembly = typeof(HostConfigurationSnapshotHolder).Assembly;
        var accessorType = hostAssembly.GetType(
            "Nekolla.Nekostick.Host.HostRoutingSnapshotAccessor",
            throwOnError: true)!;
        var accessor = Activator.CreateInstance(
            accessorType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [holder],
            culture: null)!;
        var fallbackType = hostAssembly.GetType(
            "Nekolla.Nekostick.Host.ExtensionRouteFallbackDispatcher",
            throwOnError: true)!;
        var fallbackConstructor = fallbackType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(value => value.GetParameters().Length == 0);
        var fallback = fallbackConstructor.Invoke(Array.Empty<object>());
        var dispatcherType = hostAssembly.GetType(
            "Nekolla.Nekostick.Host.HostRouteDispatcher",
            throwOnError: true)!;
        var constructor = dispatcherType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(value =>
            {
                var parameters = value.GetParameters();
                return parameters.Length == 3
                    && parameters[2].ParameterType.Name == "IRouteTargetExecutor";
            });
        var dispatcher = constructor.Invoke([accessor, fallback, targetExecutor]);
        var dispatch = dispatcherType.GetMethod(
            "DispatchAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        app.Run(context => (Task)dispatch.Invoke(dispatcher, [context])!);

        try
        {
            await app.StartAsync(cancellationToken);
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses;
            var address = new Uri(addresses.Single());
            var client = new HttpClient
            {
                BaseAddress = new UriBuilder(Uri.UriSchemeHttp, address.Host, address.Port).Uri
            };
            client.DefaultRequestHeaders.Host = "integration.test";
            return new LoopbackApp(app, client);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static HostConfigurationSnapshot CreateSnapshot(ExtensionSettingsConfiguration settings) =>
        CreateSnapshot(
            settings,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty);

    private static HostConfigurationSnapshot CreateSnapshot(
        ExtensionSettingsConfiguration settings,
        ImmutableArray<string> hostPatterns,
        ImmutableArray<string> methods)
    {
        var route = new RouteConfiguration(
            Guid.CreateVersion7(),
            enabled: true,
            new RouteMatcherConfiguration(
                RouteMatcherType.Exact,
                "/extension",
                hostPatterns,
                methods),
            new ExtensionHandlerRouteTargetConfiguration(ExtensionId),
            priority: 0,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<Nekolla.Nekostick.Contracts.HeaderRewriteConfiguration>.Empty,
            ImmutableArray<Nekolla.Nekostick.Contracts.HeaderRewriteConfiguration>.Empty,
            "{}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            version: 1);
        return new HostConfigurationSnapshot(
            1,
            new GlobalSettingsConfiguration(version: 1),
            ImmutableArray.Create(route),
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray.Create(
                new ExtensionRecordConfiguration(
                    ExtensionId,
                    "1.0.0",
                    ExtensionLoadState.Loaded,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    1)),
            ImmutableArray.Create(settings));
    }

    private static bool ReplaceWithGeneration(
        HostConfigurationSnapshotHolder holder,
        HostConfigurationSnapshot snapshot,
        ExtensionDispatchGeneration generation)
    {
        var method = typeof(HostConfigurationSnapshotHolder)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(value =>
            {
                var parameters = value.GetParameters();
                return value.Name == nameof(HostConfigurationSnapshotHolder.TryReplace)
                    && parameters.Length == 2
                    && parameters[1].ParameterType == typeof(ExtensionDispatchGeneration);
            });
        return (bool)method.Invoke(holder, [snapshot, generation])!;
    }

    private static string CreateFixtureRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nekostick-extension-loopback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.Copy(
            GetKnownOutputAssemblyPath(typeof(FixtureEntrypoint).Assembly),
            Path.Combine(root, "Fixtures.Extension.dll"));
        File.Copy(
            GetKnownOutputAssemblyPath(typeof(IExtensionEntrypoint).Assembly),
            Path.Combine(root, "Nekolla.Nekostick.Contracts.dll"));
        File.WriteAllText(
            Path.Combine(root, "manifest.json"),
            "{\n" +
            "  \"schemaVersion\": 1,\n" +
            "  \"id\": \"fixture.extension.deterministic\",\n" +
            "  \"version\": \"1.0.0\",\n" +
            "  \"entryAssembly\": \"Fixtures.Extension.dll\",\n" +
            "  \"entryType\": \"Nekolla.Nekostick.Tests.Fixtures.Extension.FixtureEntrypoint\",\n" +
            "  \"dependencies\": [],\n" +
            "  \"requiredHostApiVersion\": \">=1.0.0\"\n" +
            "}");
        return root;
    }

    private static string GetKnownOutputAssemblyPath(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("The fixture assembly name is unavailable.");
        }

        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, name + ".dll"));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("The fixture assembly is not present in the test output.");
        }

        var actual = AssemblyName.GetAssemblyName(path);
        if (!string.Equals(actual.FullName, assembly.GetName().FullName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fixture assembly identity is not the expected output assembly.");
        }

        return path;
    }

    private sealed class LoopbackApp : IAsyncDisposable
    {
        private readonly WebApplication _app;

        internal LoopbackApp(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        internal HttpClient Client { get; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }
}
