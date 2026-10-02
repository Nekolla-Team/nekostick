using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

public sealed class ServiceOutputStreamingIntegrationTests
{
    private static readonly Guid ServiceId =
        new("01900000-0000-7000-8000-000000000715");

    [Fact]
    public async Task FixtureExtensionSubscriptionReceivesServiceBytesWithoutHelperMarker()
    {
        var helperPath = RequireArtifact(
            "Nekolla.Nekostick.NativeHelper",
            "compiled native process helper");
        var fixturePath = RequireArtifact(
            "Fixtures.Microservice",
            "compiled microservice fixture");
        using var extension = CreateExtensionDirectory();
        var outputGate = Path.Combine(
            Path.GetTempPath(),
            "nekostick-output-gate-" + Guid.NewGuid().ToString("N"));
        var manifestResult = ExtensionManifestDiscovery.Discover(extension.RootPath);
        Assert.True(manifestResult.Succeeded, manifestResult.FailureCode.ToString());
        var manifest = manifestResult.Manifest!;
        var executor = new PosixProcessExecutor(helperPath, TimeSpan.FromSeconds(2));
        var outputFacade = CreateOutputFacade(executor);
        var recordingOutput = new RecordingOutputApi(outputFacade);
        var capabilityFactory = new RecordingCapabilityFactory(recordingOutput);
        await using var manager = new ExtensionRuntimeManager(
            new HostApiVersion(1, 4, 0),
            capabilityFactory: capabilityFactory);

        var start = await executor.StartAsync(
            new ProcessLaunchSpecification(
                ServiceId,
                fixturePath,
                "/tmp",
                ImmutableArray.Create(
                    "--port", "0",
                    "--emit-output",
                    "--output-gate-file", outputGate,
                    "--exit-after-ms", "5000"),
                new ProcessEnvironment(new Dictionary<string, string>())),
            TestContext.Current.CancellationToken);
        Assert.Equal(ProcessOperationStatus.Accepted, start.Status);

        try
        {
            var settings = new ExtensionSettingsConfiguration(
                manifest.Id,
                1,
                JsonSerializer.Serialize(new
                {
                    label = "service-output-integration",
                    verifyBridgeCapabilities = true,
                    holdServiceOutput = true
                }),
                0);
            var load = await manager.LoadAsync(
                manifest,
                settings,
                TestContext.Current.CancellationToken);
            Assert.True(load.Succeeded, load.FailureCode.ToString());
            await File.WriteAllTextAsync(
                outputGate,
                "ready",
                TestContext.Current.CancellationToken);

            var stderr = await recordingOutput.Stderr.Task.WaitAsync(
                TimeSpan.FromSeconds(8),
                TestContext.Current.CancellationToken);
            Assert.Equal("FIXTURE_STDERR_LINE\n", Encoding.UTF8.GetString(stderr));
            Assert.DoesNotContain("NK_READY", Encoding.UTF8.GetString(stderr), StringComparison.Ordinal);
        }
        finally
        {
            await manager.UnloadAsync(manifest.Id, TestContext.Current.CancellationToken);
            await executor.StopAsync(
                ServiceId,
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            await executor.CleanupAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            await outputFacade.DisposeAsync();
            File.Delete(outputGate);
        }
    }

    private static ExtensionServiceOutputFacade CreateOutputFacade(PosixProcessExecutor executor)
    {
        var snapshot = new HostConfigurationSnapshot(
            1,
            new GlobalSettingsConfiguration(version: 1),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray.Create(new ServiceConfiguration(
                ServiceId,
                enabled: true,
                fileName: "/bin/sh",
                argumentList: ImmutableArray<string>.Empty,
                workingDirectory: "/tmp",
                environment: ImmutableDictionary<string, string>.Empty,
                startMode: ServiceStartMode.Lazy,
                restartPolicy: ServiceRestartPolicy.Never,
                healthCheck: new ServiceHealthCheckConfiguration(
                    ServiceHealthCheckType.Process,
                    httpPath: null,
                    timeout: TimeSpan.FromSeconds(1)),
                createdAt: DateTimeOffset.UnixEpoch,
                updatedAt: DateTimeOffset.UnixEpoch,
                version: 1)),
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);
        var holder = new HostConfigurationSnapshotHolder();
        var serviceOwners = ImmutableDictionary<Guid, string?>.Empty
            .Add(ServiceId, "another.extension");
        Assert.Equal(SnapshotAdmission.Accepted, holder.TryReplace(snapshot, dispatchGeneration: null, serviceOwners: serviceOwners));
        var runtimeState = new HostRuntimeState(holder, new HostNodeOptions(false, false, false));
        return new ExtensionServiceOutputFacade(
            "fixture.extension.deterministic",
            runtimeState,
            executor,
            NullLogger.Instance);
    }

    private static string RequireArtifact(string fileName, string description)
    {
        foreach (var candidate in EnumerateArtifactPaths(fileName))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Skip($"The compiled {description} is unavailable; build the repository test artifacts first.");
        return string.Empty;
    }

    private static IEnumerable<string> EnumerateArtifactPaths(string fileName)
    {
        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            yield return Path.Combine(current.FullName, fileName);
            yield return Path.Combine(current.FullName, "tests", "Fixtures.Microservice", "bin", "Debug", "net10.0", fileName);
            yield return Path.Combine(current.FullName, "tests", "Fixtures.Microservice", "bin", "Release", "net10.0", fileName);
            yield return Path.Combine(current.FullName, "src", "Nekolla.Nekostick.Host", ".nativehelper", "Debug", runtimeIdentifier, fileName);
            yield return Path.Combine(current.FullName, "src", "Nekolla.Nekostick.Host", ".nativehelper", "Release", runtimeIdentifier, fileName);
        }
    }

    private static TempExtension CreateExtensionDirectory() =>
        TempExtension.Create(
            typeof(ServiceOutputStreamingIntegrationTests).Assembly,
            "fixture.extension.deterministic");

    private sealed class TempExtension : IDisposable
    {
        private TempExtension(string rootPath) => RootPath = rootPath;

        internal string RootPath { get; }

        internal static TempExtension Create(Assembly testAssembly, string extensionId)
        {
            var root = Path.Combine(Path.GetTempPath(), "nekostick-extension-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "Fixtures.Extension.dll"),
                Path.Combine(root, "Fixtures.Extension.dll"));
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "Nekolla.Nekostick.Contracts.dll"),
                Path.Combine(root, "Nekolla.Nekostick.Contracts.dll"));
            File.WriteAllText(
                Path.Combine(root, "manifest.json"),
                "{\"schemaVersion\":1,\"id\":\"" + extensionId + "\",\"version\":\"1.0.0\",\"entryAssembly\":\"Fixtures.Extension.dll\",\"entryType\":\"Nekolla.Nekostick.Tests.Fixtures.Extension.FixtureEntrypoint\",\"dependencies\":[],\"requiredHostApiVersion\":\">=1.0.0\"}");
            return new TempExtension(root);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, true);
            }
        }
    }

    private sealed class RecordingCapabilityFactory : IExtensionCapabilityFactory
    {
        private readonly IExtensionServiceOutputApi _output;

        public RecordingCapabilityFactory(IExtensionServiceOutputApi output) => _output = output;

        public ExtensionCapabilitySet Create(string extensionId, Func<string, bool> handlerIsOwned)
        {
            var unsupported = UnsupportedExtensionCapabilities.Create();
            return new ExtensionCapabilitySet(
                unsupported.ConfigurationApi,
                unsupported.Routes,
                unsupported.Services,
                unsupported.Endpoints,
                unsupported.FullConfiguration,
                unsupported.Supervisor,
                unsupported.RouteEvents,
                unsupported.LogWriter,
                unsupported.ExtensionManagement,
                unsupported.HostInfo,
                _output);
        }
    }

    private sealed class RecordingOutputApi : IExtensionServiceOutputApi, IAsyncDisposable
    {
        private readonly IExtensionServiceOutputApi _inner;

        public RecordingOutputApi(IExtensionServiceOutputApi inner) => _inner = inner;

        internal TaskCompletionSource<byte[]> Stderr { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ExtensionServiceOutputStreamResult> OpenStreamAsync(
            Guid serviceId,
            ExtensionServiceOutputStream stream,
            CancellationToken cancellationToken = default) =>
            _inner.OpenStreamAsync(serviceId, stream, cancellationToken);

        public async ValueTask<ExtensionServiceOutputSubscriptionResult> SubscribeAsync(
            Guid serviceId,
            ExtensionServiceOutputStream stream,
            IExtensionServiceOutputSink sink,
            CancellationToken cancellationToken = default)
        {
            var result = await _inner.SubscribeAsync(
                serviceId,
                stream,
                new RecordingSink(stream, sink, Stderr),
                cancellationToken);
            return result;
        }

        public ValueTask DisposeAsync() =>
            _inner is IAsyncDisposable disposable
                ? disposable.DisposeAsync()
                : ValueTask.CompletedTask;
    }

    private sealed class RecordingSink : IExtensionServiceOutputSink
    {
        private readonly ExtensionServiceOutputStream _stream;
        private readonly IExtensionServiceOutputSink _inner;
        private readonly TaskCompletionSource<byte[]> _stderr;
        private readonly List<byte> _stderrBytes = [];
        private readonly object _gate = new();

        public RecordingSink(
            ExtensionServiceOutputStream stream,
            IExtensionServiceOutputSink inner,
            TaskCompletionSource<byte[]> stderr)
        {
            _stream = stream;
            _inner = inner;
            _stderr = stderr;
        }

        public void OnChunk(ExtensionServiceOutputChunk chunk)
        {
            if (_stream == ExtensionServiceOutputStream.Stderr)
            {
                lock (_gate)
                {
                    _stderrBytes.AddRange(chunk.Data);
                    if (chunk.Data.Contains((byte)'\n'))
                    {
                        _stderr.TrySetResult(_stderrBytes.ToArray());
                    }
                }
            }

            _inner.OnChunk(chunk);
        }

        public void OnCompleted(ExtensionServiceOutputCompletionReason reason) => _inner.OnCompleted(reason);

        public void OnDropped(long byteCount) => _inner.OnDropped(byteCount);
    }

}
