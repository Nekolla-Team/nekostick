using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
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

public sealed class HostExtensionSseStreamingIntegrationTests
{
    private const string ExtensionId = "fixture.extension.deterministic";
    private const string SseHandlerId = "fixture.sse-events";
    private const string SsePath = "/sse-events";
    private static readonly string[] ExpectedFramePayloads = ["fixture-sse-1", "fixture-sse-2", "fixture-sse-3"];

    [Fact]
    public async Task SseRouteWithReturnHooksStreamsEveryFrameIncrementallyOverLoopbackKestrel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixtureRoot = CreateFixtureRoot();
        var manager = new ExtensionRuntimeManager(
            HostApiVersion.Current,
            capabilityFactory: new RouteHookCapabilityFactory());
        var holder = new HostConfigurationSnapshotHolder();
        try
        {
            var manifestResult = ExtensionManifestDiscovery.Discover(fixtureRoot);
            Assert.True(manifestResult.Succeeded, manifestResult.FailureCode.ToString());
            var routeId = Guid.CreateVersion7();
            var settings = new ExtensionSettingsConfiguration(
                ExtensionId,
                schemaVersion: 1,
                settingsJson: JsonSerializer.Serialize(new
                {
                    label = "sse-loopback",
                    registerSseHandler = true,
                    sseHandlerId = SseHandlerId
                }),
                version: 1);
            var prepared = await manager.PrepareGenerationAsync(
                ImmutableArray.Create(new ExtensionRuntimeDescriptor(
                    manifestResult.Manifest!,
                    settings,
                    handlerIds: [SseHandlerId],
                    routeIds: [routeId])),
                previous: null,
                cancellationToken: cancellationToken);
            Assert.True(prepared.Succeeded, prepared.FailureCode.ToString());
            var preparation = prepared.Preparation!;
            var ready = await preparation.ReadyToPublishAsync(cancellationToken);
            Assert.True(ready.Succeeded, ready.FailureCode.ToString());
            Assert.True(await preparation.CompletePublicationAsync());

            Assert.True(ReplaceWithGeneration(holder, CreateSnapshot(settings, routeId), ready.Generation!));
            await using var app = await StartLoopbackAsync(holder, cancellationToken);

            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(TimeSpan.FromSeconds(10));
            var requestTimestamp = Stopwatch.GetTimestamp();
            using var response = await app.Client.GetAsync(
                SsePath,
                HttpCompletionOption.ResponseHeadersRead,
                bound.Token);
            var headersElapsed = Stopwatch.GetElapsedTime(requestTimestamp);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(
                response.Content.Headers.ContentType?.MediaType is not null
                && response.Content.Headers.ContentType.MediaType.AsSpan().SequenceEqual(
                    "text/event-stream".AsSpan()));

            var frameTimestamps = new List<long>();
            var framePayloads = new List<string>();
            var content = new StringBuilder();
            await using var responseStream = await response.Content.ReadAsStreamAsync(bound.Token);
            using var reader = new StreamReader(
                responseStream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false);
            while (true)
            {
                var line = await reader.ReadLineAsync(bound.Token);
                if (line is null)
                {
                    break;
                }

                content.Append(line).Append('\n');
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    framePayloads.Add(line["data: ".Length..]);
                    frameTimestamps.Add(Stopwatch.GetTimestamp());
                }
            }

            Assert.Equal(ExpectedFramePayloads, framePayloads);
            Assert.Equal(
                "data: fixture-sse-1\n\ndata: fixture-sse-2\n\ndata: fixture-sse-3\n\n",
                content.ToString());
            Assert.True(
                headersElapsed < TimeSpan.FromSeconds(10),
                $"The response headers were observed only after {headersElapsed.TotalMilliseconds:F0}ms.");
            var frameSpan = Stopwatch.GetElapsedTime(frameTimestamps[0], frameTimestamps[^1]);
            Assert.True(
                frameSpan >= TimeSpan.FromMilliseconds(100),
                $"The first and last frames arrived {frameSpan.TotalMilliseconds:F0}ms apart; frames produced "
                + "200ms apart must be delivered incrementally instead of being buffered until the stream ends.");
        }
        finally
        {
            await holder.DisposeAsync();
            await manager.DisposeAsync();
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static HostConfigurationSnapshot CreateSnapshot(
        ExtensionSettingsConfiguration settings,
        Guid routeId)
    {
        var route = new RouteConfiguration(
            routeId,
            enabled: true,
            new RouteMatcherConfiguration(
                RouteMatcherType.Exact,
                SsePath,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            new ExtensionHandlerRouteTargetConfiguration(SseHandlerId),
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
        // The internal TryReplace returns SnapshotAdmission; integration tests reach it via
        // reflection, so compare against the enum member name instead of casting.
        return string.Equals(
            method.Invoke(holder, [snapshot, generation])?.ToString(),
            "Accepted",
            StringComparison.Ordinal);
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

    private static string CreateFixtureRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nekostick-extension-sse-" + Guid.NewGuid().ToString("N"));
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

    private sealed class RouteHookCapabilityFactory
        : IExtensionCapabilityFactory, IExtensionCapabilityFactoryRouteEvents
    {
        public ExtensionCapabilitySet Create(string extensionId, Func<string, bool> handlerIsOwned) =>
            throw new NotSupportedException();

        public ExtensionCapabilitySet CreateWithRouteEvents(
            string extensionId,
            Func<string, bool> handlerIsOwned,
            IExtensionRouteEvents routeEvents)
        {
            Assert.Same(
                ExtensionRouteRegistrationResult.Success,
                routeEvents.TryRegisterHook(
                    ExtensionRouteEventStage.Trigger,
                    static (_, _) => ValueTask.FromResult(
                        new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue))));
            Assert.Same(
                ExtensionRouteRegistrationResult.Success,
                routeEvents.TryRegisterHook(
                    ExtensionRouteEventStage.Return,
                    static (_, _) => ValueTask.FromResult(
                        new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue))));
            var unsupported = UnsupportedExtensionCapabilities.Create();
            return new ExtensionCapabilitySet(
                unsupported.ConfigurationApi,
                unsupported.Routes,
                unsupported.Services,
                unsupported.Endpoints,
                unsupported.FullConfiguration,
                unsupported.Supervisor,
                routeEvents,
                unsupported.LogWriter);
        }
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
