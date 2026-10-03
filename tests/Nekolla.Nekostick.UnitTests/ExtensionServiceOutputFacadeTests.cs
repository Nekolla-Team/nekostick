using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Supervision;
using ContractRestartPolicy = Nekolla.Nekostick.Contracts.ServiceRestartPolicy;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ExtensionServiceOutputFacadeTests
{
    private static readonly Guid ServiceId =
        new("0198a1af-6e94-7b25-9732-59c9075b14f6");

    [Fact]
    public async Task InvalidAndUnconfiguredServiceIdsReturnNotFoundForOpenStream()
    {
        await using var facade = CreateFacade(configured: false, executor: null);

        var invalid = await facade.OpenStreamAsync(
            Guid.Empty,
            ExtensionServiceOutputStream.Stdout,
            TestContext.Current.CancellationToken);
        Assert.False(invalid.Succeeded);
        Assert.Equal(ExtensionServiceOutputCode.NotFound, invalid.Code);
        Assert.NotEqual(Guid.Empty, invalid.ServiceId);
        Assert.True(UuidV7.IsVersion7(invalid.ServiceId));

        var open = await facade.OpenStreamAsync(
            ServiceId,
            ExtensionServiceOutputStream.Stdout,
            TestContext.Current.CancellationToken);
        Assert.False(open.Succeeded);
        Assert.Equal(ExtensionServiceOutputCode.NotFound, open.Code);

    }


    [Fact]
    public async Task ForeignConfiguredServiceCanBeOpened()
    {
        var helperPath = RequireNativeHelperPath();
        var executor = new PosixProcessExecutor(helperPath, TimeSpan.FromSeconds(2));
        await using var facade = CreateFacade(
            configured: true,
            executor: executor,
            serviceOwnerExtensionId: "another.extension");

        try
        {
            var start = await executor.StartAsync(
                CreateLaunch("sleep 3"),
                TestContext.Current.CancellationToken);
            Assert.Equal(ProcessOperationStatus.Accepted, start.Status);

            var result = await facade.OpenStreamAsync(
                ServiceId,
                ExtensionServiceOutputStream.Stdout,
                TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded);
            Assert.Equal(ExtensionServiceOutputCode.Opened, result.Code);
            Assert.NotNull(result.Stream);
            await result.Stream!.DisposeAsync();
        }
        finally
        {
            await executor.StopAsync(ServiceId, TimeSpan.FromSeconds(2), CancellationToken.None);
            await executor.CleanupAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
    }

    [Fact]
    public async Task ConfiguredStoppedServiceReturnsNotRunning()
    {
        var helperPath = RequireNativeHelperPath();
        var executor = new PosixProcessExecutor(helperPath, TimeSpan.FromSeconds(2));
        await using var facade = CreateFacade(configured: true, executor: executor);

        var result = await facade.OpenStreamAsync(
            ServiceId,
            ExtensionServiceOutputStream.Stdout,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ExtensionServiceOutputCode.NotRunning, result.Code);
    }


    [Fact]
    public async Task DisposingOpenedResourcesPrunesTrackingAcrossCycles()
    {
        var helperPath = RequireNativeHelperPath();
        var executor = new PosixProcessExecutor(helperPath, TimeSpan.FromSeconds(2));
        await using var facade = CreateFacade(configured: true, executor: executor);
        var resources = typeof(ExtensionServiceOutputFacade).GetField(
            "_resources",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(resources);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var start = await executor.StartAsync(CreateLaunch("sleep 3"), TestContext.Current.CancellationToken);
            Assert.Equal(ProcessOperationStatus.Accepted, start.Status);

            try
            {
                var opened = await facade.OpenStreamAsync(ServiceId, ExtensionServiceOutputStream.Stdout, TestContext.Current.CancellationToken);
                Assert.True(opened.Succeeded);
                Assert.NotNull(opened.Stream);
                Assert.Single((ICollection)resources!.GetValue(facade)!);

                await opened.Stream!.DisposeAsync();
                Assert.Empty((ICollection)resources.GetValue(facade)!);
            }
            finally
            {
                await executor.StopAsync(ServiceId, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            }
        }
    }


    private static ExtensionServiceOutputFacade CreateFacade(
        bool configured,
        PosixProcessExecutor? executor,
        string? serviceOwnerExtensionId = "fixture.extension.deterministic")
    {
        var services = configured
            ? ImmutableArray.Create(CreateServiceConfiguration())
            : ImmutableArray<Nekolla.Nekostick.Contracts.ServiceConfiguration>.Empty;
        var snapshot = new HostConfigurationSnapshot(
            1,
            new GlobalSettingsConfiguration(version: 1),
            ImmutableArray<RouteConfiguration>.Empty,
            services,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);
        var holder = new HostConfigurationSnapshotHolder();
        var serviceOwners = configured
            ? ImmutableDictionary<Guid, string?>.Empty.Add(ServiceId, serviceOwnerExtensionId)
            : ImmutableDictionary<Guid, string?>.Empty;
        Assert.Equal(SnapshotAdmission.Accepted, holder.TryReplace(snapshot, dispatchGeneration: null, serviceOwners: serviceOwners));
        var runtimeState = new HostRuntimeState(
            holder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly: false));
        return new ExtensionServiceOutputFacade(
            "fixture.extension.deterministic",
            runtimeState,
            executor,
            NullLogger.Instance);
    }

    private static ProcessLaunchSpecification CreateLaunch(string command) =>
        new(
            ServiceId,
            "/bin/sh",
            "/tmp",
            ImmutableArray.Create("-c", command),
            new ProcessEnvironment(new Dictionary<string, string>()));


    private static string RequireNativeHelperPath()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            Assert.Skip("POSIX process helper tests are unsupported on this platform.");
            return string.Empty;
        }

        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        foreach (var current in EnumerateCandidateDirectories(baseDirectory, runtimeIdentifier))
        {
            var candidate = Path.Combine(current, "Nekolla.Nekostick.NativeHelper");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Skip("The compiled native process helper is unavailable; build the repository test artifacts first.");
        return string.Empty;
    }

    private static IEnumerable<string> EnumerateCandidateDirectories(
        DirectoryInfo baseDirectory,
        string runtimeIdentifier)
    {
        for (var current = baseDirectory; current is not null; current = current.Parent)
        {
            yield return current.FullName;
            yield return Path.Combine(
                current.FullName,
                "src",
                "Nekolla.Nekostick.Host",
                ".nativehelper",
                "Debug",
                runtimeIdentifier);
            yield return Path.Combine(
                current.FullName,
                "src",
                "Nekolla.Nekostick.Host",
                ".nativehelper",
                "Release",
                runtimeIdentifier);
            yield return Path.Combine(
                current.FullName,
                "src",
                "Nekolla.Nekostick.NativeHelper",
                "bin",
                "Debug",
                "net10.0",
                runtimeIdentifier);
            yield return Path.Combine(
                current.FullName,
                "src",
                "Nekolla.Nekostick.NativeHelper",
                "bin",
                "Release",
                "net10.0",
                runtimeIdentifier);
            yield return Path.Combine(
                current.FullName,
                "src",
                "Nekolla.Nekostick.NativeHelper",
                "bin",
                "Debug",
                "net10.0");
            yield return Path.Combine(
                current.FullName,
                "src",
                "Nekolla.Nekostick.NativeHelper",
                "bin",
                "Release",
                "net10.0");
        }
    }


    private static Nekolla.Nekostick.Contracts.ServiceConfiguration CreateServiceConfiguration() =>
        new(
            ServiceId,
            enabled: true,
            fileName: "/bin/sh",
            argumentList: ImmutableArray<string>.Empty,
            workingDirectory: "/tmp",
            environment: ImmutableDictionary<string, string>.Empty,
            startMode: ServiceStartMode.Lazy,
            restartPolicy: ContractRestartPolicy.Never,
            healthCheck: new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                httpPath: null,
                timeout: TimeSpan.FromSeconds(1)),
            createdAt: DateTimeOffset.UnixEpoch,
            updatedAt: DateTimeOffset.UnixEpoch,
            version: 1);
}
