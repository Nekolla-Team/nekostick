using System.Collections;
using System.Diagnostics;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Extensions;
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
    public async Task InvalidAndUnconfiguredServiceIdsReturnNotFound()
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

        var subscription = await facade.SubscribeAsync(
            ServiceId,
            ExtensionServiceOutputStream.Stdout,
            new RecordingSink(),
            TestContext.Current.CancellationToken);
        Assert.False(subscription.Succeeded);
        Assert.Equal(ExtensionServiceOutputCode.NotFound, subscription.Code);
    }

    [Fact]
    public async Task ConfiguredServiceWithoutExecutorReturnsUnsupported()
    {
        await using var facade = CreateFacade(configured: true, executor: null);

        var result = await facade.SubscribeAsync(
            ServiceId,
            ExtensionServiceOutputStream.Stdout,
            new RecordingSink(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ExtensionServiceOutputCode.Unsupported, result.Code);
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
    public async Task SubscribeAfterPumpEndReturnsOpenedAndImmediateProcessExitedCompletion()
    {
        var helperPath = RequireNativeHelperPath();
        var executor = new PosixProcessExecutor(helperPath, TimeSpan.FromSeconds(2));
        await using var facade = CreateFacade(configured: true, executor: executor);
        var fifoPath = CreateFifo();
        var captureStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var captureSink = new BlockingCaptureSink(captureStarted, releaseCapture);
        using var captureSubscription = executor.SubscribeOutputCapture(captureSink);
        var lateResult = new TaskCompletionSource<ExtensionServiceOutputSubscriptionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateSink = new RecordingSink();
        var rawSink = new CompletionActionChunkSink(() =>
        {
            try
            {
                var result = facade.SubscribeAsync(
                    ServiceId,
                    ExtensionServiceOutputStream.Stdout,
                    lateSink,
                    CancellationToken.None).AsTask().GetAwaiter().GetResult();
                lateResult.TrySetResult(result);
            }
            catch (Exception exception)
            {
                lateResult.TrySetException(exception);
            }
        });

        try
        {
            var start = await executor.StartAsync(
                CreateLaunch($"read _ < {fifoPath}; printf hold; exit 0"),
                TestContext.Current.CancellationToken);
            Assert.Equal(ProcessOperationStatus.Accepted, start.Status);
            using var rawSubscription = executor.TrySubscribeOutput(
                ServiceId,
                ProcessOutputStream.Stdout,
                rawSink) ?? throw new InvalidOperationException("The running fixture must expose stdout.");

            await File.WriteAllTextAsync(
                fifoPath,
                "\n",
                TestContext.Current.CancellationToken);
            await captureStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            var subscribed = await lateResult.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.True(subscribed.Succeeded);
            Assert.Equal(ExtensionServiceOutputCode.Opened, subscribed.Code);
            Assert.NotNull(subscribed.Subscription);

            var completion = await lateSink.Completed.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Equal(ExtensionServiceOutputCompletionReason.ProcessExited, completion);
            releaseCapture.TrySetResult(true);
            await subscribed.Subscription!.DisposeAsync();
        }
        finally
        {
            releaseCapture.TrySetResult(true);
            await executor.StopAsync(
                ServiceId,
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            File.Delete(fifoPath);
        }
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

    [Fact]
    public async Task DisposeAfterDisposeAsyncWaitsForBlockedSinkAndCallbackGuardReturnsReentrant()
    {
        var helperPath = RequireNativeHelperPath();
        var executor = new PosixProcessExecutor(helperPath, TimeSpan.FromSeconds(2));
        await using var facade = CreateFacade(configured: true, executor: executor);
        var pending = typeof(ExtensionServiceOutputFacade).GetField(
            "_pendingDisposals",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pending);
        var callbackEntered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ExtensionLifecycleOperationResult? lifecycleResult = null;
        var lifecycle = new ExtensionLifecycleApi(
            () => null,
            _ => ValueTask.FromResult(new ExtensionLifecycleOperationResult(
                true,
                ExtensionLifecycleOperationCode.Accepted,
                null)),
            _ => ValueTask.FromResult(new ExtensionLifecycleOperationResult(
                true,
                ExtensionLifecycleOperationCode.Accepted,
                null)));
        var sink = new RecordingSink
        {
            OnChunkAction = _ =>
            {
                lifecycleResult = lifecycle.RequestUnloadAsync().AsTask().GetAwaiter().GetResult();
                callbackEntered.TrySetResult(null);
                releaseCallback.Task.GetAwaiter().GetResult();
            }
        };
        var fifoPath = CreateFifo();

        var start = await executor.StartAsync(
            CreateLaunch($"read _ < {fifoPath}; printf payload; sleep 3"),
            TestContext.Current.CancellationToken);
        Assert.Equal(ProcessOperationStatus.Accepted, start.Status);
        try
        {
            var subscribed = await facade.SubscribeAsync(
                ServiceId,
                ExtensionServiceOutputStream.Stdout,
                sink,
                TestContext.Current.CancellationToken);
            Assert.True(subscribed.Succeeded);
            Assert.NotNull(subscribed.Subscription);
            await File.WriteAllTextAsync(
                fifoPath,
                "\n",
                TestContext.Current.CancellationToken);
            await callbackEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            subscribed.Subscription!.Dispose();
            var disposeTask = subscribed.Subscription.DisposeAsync().AsTask();
            Assert.False(disposeTask.IsCompleted);
            var facadeDisposeTask = facade.DisposeAsync().AsTask();
            Assert.False(facadeDisposeTask.IsCompleted);
            Assert.NotNull(lifecycleResult);
            Assert.Equal(ExtensionLifecycleOperationCode.Reentrant, lifecycleResult!.Code);

            releaseCallback.TrySetResult(null);
            await disposeTask.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            await facadeDisposeTask.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Empty((ICollection)pending!.GetValue(facade)!);
        }
        finally
        {
            await executor.StopAsync(
                ServiceId,
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            File.Delete(fifoPath);
        }
    }

    [Fact]
    public async Task QuiescedSyncDisposedSubscriptionStaysPrunedAcrossRepeatedAsyncDispose()
    {
        var facade = CreateFacade(configured: true, executor: null);
        var pending = typeof(ExtensionServiceOutputFacade).GetField(
            "_pendingDisposals",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var wrapperType = typeof(ExtensionServiceOutputFacade).GetNestedType(
            "ExtensionServiceOutputSubscription",
            BindingFlags.NonPublic);
        var markDetached = typeof(ExtensionServiceOutputFacade).GetMethod(
            "MarkDetached",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var untrack = typeof(ExtensionServiceOutputFacade).GetMethod(
            "Untrack",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pending);
        Assert.NotNull(wrapperType);
        Assert.NotNull(markDetached);
        Assert.NotNull(untrack);

        var onDetached = markDetached!.CreateDelegate<Action<IDisposable>>(facade);
        var untrackResource = untrack!.CreateDelegate<Action<IDisposable>>(facade);
        var quiesced = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var onQuiesced = (Action<IDisposable>)(resource =>
        {
            untrackResource(resource);
            quiesced.TrySetResult(null);
        });
        var underlying = new BlockingAsyncDisposable();
        var subscription = Assert.IsAssignableFrom<IExtensionServiceOutputSubscription>(
            Activator.CreateInstance(
                wrapperType!,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [underlying, NullLogger.Instance, onDetached, onQuiesced],
                culture: null));

        subscription.Dispose();
        await underlying.DisposeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Single((ICollection)pending!.GetValue(facade)!);

        underlying.Release();
        await quiesced.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Empty((ICollection)pending.GetValue(facade)!);

        await subscription.DisposeAsync();
        await subscription.DisposeAsync();
        Assert.Empty((ICollection)pending.GetValue(facade)!);
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

    private static string CreateFifo()
    {
        var path = Path.Combine(Path.GetTempPath(), "nekostick-fifo-" + Guid.NewGuid().ToString("N"));
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "mkfifo",
            ArgumentList = { path },
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        Assert.NotNull(process);
        process!.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return path;
    }

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

    private sealed class RecordingSink : IExtensionServiceOutputSink
    {
        public Action<ExtensionServiceOutputChunk>? OnChunkAction { get; init; }

        internal TaskCompletionSource<ExtensionServiceOutputCompletionReason> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnChunk(ExtensionServiceOutputChunk chunk) => OnChunkAction?.Invoke(chunk);

        public void OnCompleted(ExtensionServiceOutputCompletionReason reason) => Completed.TrySetResult(reason);

        public void OnDropped(long byteCount) { }
    }

    private sealed class BlockingCaptureSink : IProcessOutputSink
    {
        private readonly TaskCompletionSource<bool> started;
        private readonly TaskCompletionSource<bool> release;

        internal BlockingCaptureSink(
            TaskCompletionSource<bool> started,
            TaskCompletionSource<bool> release)
        {
            this.started = started;
            this.release = release;
        }

        public void OnLine(ProcessOutputRecord record)
        {
            started.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
        }

        public void OnDropped(Guid serviceId, ProcessOutputStream stream, long count) { }
    }

    private sealed class CompletionActionChunkSink : IProcessOutputChunkSink
    {
        private readonly Action onCompleted;

        internal CompletionActionChunkSink(Action onCompleted) => this.onCompleted = onCompleted;

        public void OnChunk(ReadOnlyMemory<byte> chunk, DateTimeOffset timestamp) { }

        public void OnCompleted(ProcessOutputCompletion completion) => onCompleted();

        public void OnDropped(long byteCount) { }
    }

    private sealed class BlockingAsyncDisposable : IDisposable, IAsyncDisposable
    {
        private readonly TaskCompletionSource<object?> release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<object?> DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose() { }

        public ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult(null);
            return new ValueTask(release.Task);
        }

        internal void Release() => release.TrySetResult(null);
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
