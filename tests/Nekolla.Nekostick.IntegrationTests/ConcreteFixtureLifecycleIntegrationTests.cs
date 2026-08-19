using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Supervision;
using Xunit;
using ContractRestartPolicy = Nekolla.Nekostick.Contracts.ServiceRestartPolicy;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>
/// Black-box lifecycle evidence using the compiled Fixtures.Microservice child and the
/// production POSIX helper, process executor, HTTP health probe, and Host composition.
/// </summary>
public sealed class ConcreteFixtureLifecycleIntegrationTests
{
    [Fact]
    public async Task ConcreteExecutorStartsFixtureOnEphemeralPortAndHttpHealthBecomesHealthy()
    {
        await using var fixture = FixtureProcessHarness.Create();
        var start = await fixture.StartAsync(CancellationToken.None);

        Assert.Equal(ProcessOperationStatus.Accepted, start.Status);
        Assert.NotNull(start.InstanceId);

        var healthy = await fixture.WaitForHealthAsync(
            HealthObservationStatus.Healthy,
            TimeSpan.FromSeconds(8),
            CancellationToken.None);

        Assert.Equal(HealthObservationStatus.Healthy, healthy.Status);
        Assert.Equal((int?)200, await fixture.GetHealthStatusCodeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConcreteExecutorStopMakesFixtureHealthEndpointUnreachable()
    {
        await using var fixture = FixtureProcessHarness.Create();
        var start = await fixture.StartAsync(CancellationToken.None);
        Assert.Equal(ProcessOperationStatus.Accepted, start.Status);

        var healthy = await fixture.WaitForHealthAsync(
            HealthObservationStatus.Healthy,
            TimeSpan.FromSeconds(8),
            CancellationToken.None);
        Assert.Equal(HealthObservationStatus.Healthy, healthy.Status);

        var stopped = await fixture.StopAsync(CancellationToken.None);
        Assert.Equal(ProcessOperationStatus.Completed, stopped.Status);

        var unavailable = await fixture.WaitForHealthAsync(
            HealthObservationStatus.Unavailable,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        Assert.Equal(HealthObservationStatus.Unavailable, unavailable.Status);
        Assert.False(await fixture.CanConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PosixExecutorStopsDescendantProcessGroupWithoutKillingTestHost()
    {
        if (!IsSupportedPosix())
        {
            Assert.Skip("POSIX process-group evidence is unsupported on this platform.");
            return;
        }

        var hostProcessId = Environment.ProcessId;
        var hostProcessGroupId = GetProcessGroup(hostProcessId);
        Assert.True(hostProcessGroupId > 1);

        await using var fixture = FixtureProcessHarness.CreateDescendant();
        var start = await fixture.StartAsync(CancellationToken.None);
        Assert.Equal(ProcessOperationStatus.Accepted, start.Status);
        Assert.NotNull(start.InstanceId);

        var evidence = await fixture.WaitForDescendantEvidenceAsync(
            TimeSpan.FromSeconds(8),
            CancellationToken.None);

        Assert.NotEqual(hostProcessGroupId, evidence.LeaderProcessGroupId);
        Assert.NotEqual(evidence.LeaderProcessId, evidence.DescendantProcessId);
        Assert.NotEqual(evidence.LeaderProcessId, evidence.LeaderProcessGroupId);
        Assert.True(evidence.LeaderAlive);
        Assert.True(evidence.DescendantAlive);
        Assert.True(IsProcessAlive(evidence.LeaderProcessId));
        Assert.True(IsProcessAlive(evidence.DescendantProcessId));
        Assert.True(IsProcessAlive(evidence.LeaderProcessGroupId));
        Assert.Equal(evidence.LeaderProcessGroupId, GetProcessGroup(evidence.LeaderProcessId));
        Assert.True(IsProcessAlive(hostProcessId));

        Assert.Equal(evidence.DescendantProcessGroupId, GetProcessGroup(evidence.DescendantProcessId));
        Assert.True(IsProcessInGroup(evidence.LeaderProcessGroupId, evidence.LeaderProcessGroupId));
        Assert.True(IsProcessInGroup(hostProcessId, hostProcessGroupId));

        var stopped = await fixture.StopAsync(CancellationToken.None);
        Assert.Equal(ProcessOperationStatus.Completed, stopped.Status);
        Assert.True(await WaitForProcessGoneAsync(
            evidence.LeaderProcessId,
            TimeSpan.FromSeconds(8),
            CancellationToken.None));
        Assert.True(await WaitForProcessGoneAsync(
            evidence.DescendantProcessId,
            TimeSpan.FromSeconds(8),
            CancellationToken.None));
        Assert.True(await WaitForProcessGroupGoneAsync(
            evidence.LeaderProcessGroupId,
            TimeSpan.FromSeconds(8),
            CancellationToken.None));
        Assert.False(IsProcessInGroup(evidence.LeaderProcessId, evidence.LeaderProcessGroupId));
        Assert.False(IsProcessAlive(evidence.LeaderProcessId));
        Assert.False(IsProcessAlive(evidence.DescendantProcessId));
        Assert.True(IsProcessAlive(hostProcessId));

        Assert.False(IsProcessInGroup(evidence.DescendantProcessId, evidence.DescendantProcessGroupId));
        Assert.True(IsProcessInGroup(hostProcessId, hostProcessGroupId));
    }


    [Fact]
    public async Task FailedLaunchAndFailedHealthNeverBecomeHealthy()
    {
        await using var fixture = FixtureProcessHarness.Create();
        var invalidLaunch = await fixture.StartAsync(
            CancellationToken.None,
            fileName: Path.Combine(fixture.WorkingDirectory, "missing-fixture-executable"));

        Assert.Equal(ProcessOperationStatus.Rejected, invalidLaunch.Status);
        var unavailable = await fixture.WaitForHealthAsync(
            HealthObservationStatus.Unavailable,
            TimeSpan.FromSeconds(3),
            CancellationToken.None);
        Assert.Equal(HealthObservationStatus.Unavailable, unavailable.Status);

        var failedHealth = await FixtureProcessHarness.CreateAsync(
            ["--mode", "fail", "--status-code", "503"],
            CancellationToken.None);
        await using (failedHealth)
        {
            var failedStart = await failedHealth.StartAsync(CancellationToken.None);
            Assert.Equal(ProcessOperationStatus.Accepted, failedStart.Status);

            var unhealthy = await failedHealth.WaitForHealthAsync(
                HealthObservationStatus.Unhealthy,
                TimeSpan.FromSeconds(8),
                CancellationToken.None,
                path: "/fixture/not-found");
            Assert.Equal(HealthObservationStatus.Unhealthy, unhealthy.Status);
        }
    }

    [Fact]
    public async Task FailedHealthNeverPublishesAHostEndpoint()
    {
        await using var host = await HostLifecycleHarness.CreateAsync(
            ContractRestartPolicy.Never,
            additionalArguments: ["--mode", "fail", "--status-code", "503"],
            shortRenewalLease: false,
            CancellationToken.None,
            healthPath: "/fixture/not-found");

        var result = await host.Manager.EnsureReadyAsync(
            host.Snapshot,
            host.ServiceId,
            host.Timeout.Token);

        Assert.False(result.IsReady);
        Assert.Equal(HostServiceReadinessStatus.Unavailable, result.Status);
        Assert.Empty(host.Publisher.Current);
    }

    [Fact]
    public async Task HostCompositionPublishesEndpointOnlyAfterHttpHealthAndWithdrawsItOnRenewalFailure()
    {
        await using var host = await HostLifecycleHarness.CreateAsync(
            ContractRestartPolicy.Never,
            additionalArguments: [],
            shortRenewalLease: true,
            CancellationToken.None);

        var ready = await host.Manager.EnsureReadyAsync(
            host.Snapshot,
            host.ServiceId,
            host.Timeout.Token);

        Assert.True(ready.IsReady);
        Assert.True(host.Publisher.Current.TryGetValue(host.ServiceId, out var endpoint));
        Assert.NotNull(endpoint);
        Assert.Equal(host.Port, endpoint!.Port);

        var health = await host.Probe.ProbeAsync(
            host.HealthRequest,
            host.Timeout.Token);
        Assert.Equal(HealthObservationStatus.Healthy, health.Status);

        host.LeaseStore.FailRenewals = true;
        await host.RenewLeasesAsync();

        Assert.Empty(host.Publisher.Current);
        Assert.False(host.RuntimeState.Status.DatabaseAvailable);
    }

    [Fact]
    public async Task HostCompositionWithdrawsEndpointAfterObservedChildExitWhenRestartIsDisabled()
    {
        await using var host = await HostLifecycleHarness.CreateAsync(
            ContractRestartPolicy.Never,
            additionalArguments: ["--exit-after-ms", "2000"],
            shortRenewalLease: false,
            cancellationToken: TestContext.Current.CancellationToken);

        var ready = await host.Manager.EnsureReadyAsync(
            host.Snapshot,
            host.ServiceId,
            host.Timeout.Token);
        Assert.True(ready.IsReady);

        var unavailable = await host.WaitForHealthAsync(HealthObservationStatus.Unavailable);
        Assert.Equal(HealthObservationStatus.Unavailable, unavailable.Status);

        await host.WaitForPublisherCountAsync(0);
        await host.AssertPublisherRemainsEmptyAsync(TimeSpan.FromSeconds(1));

        Assert.Empty(host.Publisher.Current);
    }

    [Fact]
    public async Task HostCompositionRestartsObservedChildExitWhenRestartIsAlways()
    {
        await using var host = await HostLifecycleHarness.CreateAsync(
            ContractRestartPolicy.Always,
            additionalArguments: ["--exit-after-ms", "1500"],
            shortRenewalLease: false,
            CancellationToken.None);

        var ready = await host.Manager.EnsureReadyAsync(
            host.Snapshot,
            host.ServiceId,
            host.Timeout.Token);
        Assert.True(ready.IsReady);

        var unavailable = await host.WaitForHealthAsync(HealthObservationStatus.Unavailable);
        Assert.Equal(HealthObservationStatus.Unavailable, unavailable.Status);

        await host.WaitForPublisherCountAsync(0);
        await host.WaitForPublisherCountAsync(1);

        var restartedHealth = await host.WaitForHealthAsync(HealthObservationStatus.Healthy);
        Assert.Equal(HealthObservationStatus.Healthy, restartedHealth.Status);
        Assert.True(host.Publisher.Current.ContainsKey(host.ServiceId));
    }

    [Fact]
    public async Task HostCompositionStopWithAlreadyCancelledTokenMakesFixtureEndpointUnreachable()
    {
        await using var host = await HostLifecycleHarness.CreateAsync(
            ContractRestartPolicy.Never,
            additionalArguments: [],
            shortRenewalLease: false,
            CancellationToken.None);

        var ready = await host.Manager.EnsureReadyAsync(
            host.Snapshot,
            host.ServiceId,
            host.Timeout.Token);
        Assert.True(ready.IsReady);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await host.Manager.StopAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        var unavailable = await host.WaitForHealthAsync(HealthObservationStatus.Unavailable);
        Assert.Equal(HealthObservationStatus.Unavailable, unavailable.Status);
        await host.WaitForPortUnavailableAsync();
    }

    [Fact]
    public async Task HostCompositionWithdrawsEndpointAfterTerminalSteadyStateHealthFailure()
    {
        await using var host = await HostLifecycleHarness.CreateAsync(
            ContractRestartPolicy.Never,
            additionalArguments: [],
            shortRenewalLease: false,
            CancellationToken.None);

        var ready = await host.Manager.EnsureReadyAsync(
            host.Snapshot,
            host.ServiceId,
            host.Timeout.Token);
        Assert.True(ready.IsReady);
        Assert.True(host.Publisher.Current.ContainsKey(host.ServiceId));

        host.FailHealth = true;
        await host.ObserveReadyHealthAsync();
        await host.ObserveReadyHealthAsync();
        await host.ObserveReadyHealthAsync();
        await host.WaitForPublisherCountAsync(0);
        await host.WaitForPortUnavailableAsync();

        Assert.Empty(host.Publisher.Current);
    }

    [Fact]
    public async Task HostCompositionUsesOneFullConfiguredAutomaticRangeAcquireBeforeReadiness()
    {
        await using var host = await HostLifecycleHarness.CreateAsync(
            ContractRestartPolicy.Never,
            additionalArguments: [],
            shortRenewalLease: false,
            CancellationToken.None,
            automaticRangeWidth: 10);

        var ready = await host.Manager.EnsureReadyAsync(
            host.Snapshot,
            host.ServiceId,
            host.Timeout.Token);
        Assert.True(ready.IsReady);

        var requests = host.LeaseStore.AutomaticAcquireRequests;
        var request = Assert.Single(requests);
        Assert.Equal(0, request.Port);
        Assert.Equal(host.AutomaticPortRangeStart, request.AutomaticPortRangeStart);
        Assert.Equal(host.AutomaticPortRangeEnd, request.AutomaticPortRangeEnd);
        Assert.True(host.AutomaticPortRangeStart < host.Port);
        Assert.True(host.AutomaticPortRangeEnd > host.Port);
        Assert.Equal(host.NodeId, request.NodeId.Value);
        Assert.Equal(host.ServiceId, request.ServiceId);
    }



    private static bool IsSupportedPosix() =>
        (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) &&
        (RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.X64) &&
        (RuntimeInformation.RuntimeIdentifier is "osx-arm64" or "osx-x64" or "linux-arm64" or "linux-x64");

    private static int GetProcessGroup(int processId)
    {
        if (processId <= 1)
        {
            return -1;
        }

        try
        {
            return OperatingSystem.IsMacOS()
                ? GetProcessGroupDarwin(processId)
                : GetProcessGroupLinux(processId);
        }
        catch
        {
            return -1;
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 1)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }


    private static bool IsProcessInGroup(int processId, int processGroupId) =>
        processGroupId > 1 && GetProcessGroup(processId) == processGroupId;

    private static async Task<bool> WaitForProcessGroupGoneAsync(
        int processGroupId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            while (!bounded.IsCancellationRequested)
            {
                if (GetProcessGroup(processGroupId) <= 0)
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), bounded.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && bounded.IsCancellationRequested)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        return GetProcessGroup(processGroupId) <= 0;
    }
    private static async Task<bool> WaitForProcessGoneAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            while (!bounded.IsCancellationRequested)
            {
                if (!IsProcessAlive(processId))
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), bounded.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && bounded.IsCancellationRequested)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        return !IsProcessAlive(processId);
    }

    private static bool TryReadDescendantEvidence(
        string text,
        out DescendantEvidence evidence)
    {
        evidence = default;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (!root.TryGetProperty("event", out JsonElement eventProperty) ||
                eventProperty.ValueKind != JsonValueKind.String ||
                !string.Equals(eventProperty.GetString(), "descendant-ready", StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("leaderProcessId", out JsonElement leaderProcessProperty) ||
                leaderProcessProperty.ValueKind != JsonValueKind.Number ||
                !leaderProcessProperty.TryGetInt32(out int leaderProcessId))
            {
                return false;
            }

            if (!root.TryGetProperty("leaderProcessGroupId", out JsonElement leaderProcessGroupProperty) ||
                leaderProcessGroupProperty.ValueKind != JsonValueKind.Number ||
                !leaderProcessGroupProperty.TryGetInt32(out int leaderProcessGroupId))
            {
                return false;
            }

            if (!root.TryGetProperty("descendantProcessId", out JsonElement descendantProcessProperty) ||
                descendantProcessProperty.ValueKind != JsonValueKind.Number ||
                !descendantProcessProperty.TryGetInt32(out int descendantProcessId))
            {
                return false;
            }

            if (!root.TryGetProperty("descendantProcessGroupId", out JsonElement descendantProcessGroupProperty) ||
                descendantProcessGroupProperty.ValueKind != JsonValueKind.Number ||
                !descendantProcessGroupProperty.TryGetInt32(out int descendantProcessGroupId))
            {
                return false;
            }

            if (!root.TryGetProperty("leaderAlive", out JsonElement leaderAliveProperty) ||
                leaderAliveProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            bool leaderAlive = leaderAliveProperty.GetBoolean();
            if (!root.TryGetProperty("descendantAlive", out JsonElement descendantAliveProperty) ||
                descendantAliveProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            bool descendantAlive = descendantAliveProperty.GetBoolean();

            evidence = new DescendantEvidence(
                leaderProcessId,
                leaderProcessGroupId,
                descendantProcessId,
                descendantProcessGroupId,
                leaderAlive,
                descendantAlive);
            return leaderProcessId > 1 &&
                leaderProcessGroupId > 1 &&
                descendantProcessId > 1 &&
                descendantProcessGroupId > 1;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "getpgid", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetProcessGroupDarwin(int processId);

    [DllImport("libc.so.6", EntryPoint = "getpgid", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetProcessGroupLinux(int processId);

    private readonly record struct DescendantEvidence(
        int LeaderProcessId,
        int LeaderProcessGroupId,
        int DescendantProcessId,
        int DescendantProcessGroupId,
        bool LeaderAlive,
        bool DescendantAlive);

    private sealed class CapturingOutputSink : IProcessOutputSink
    {
        private readonly ConcurrentQueue<ProcessOutputRecord> _records = new();

        internal bool TryDequeue(out ProcessOutputRecord record)
        {
            if (_records.TryDequeue(out var candidate))
            {
                record = candidate;
                return true;
            }

            record = null!;
            return false;
        }

        public void OnLine(ProcessOutputRecord record) => _records.Enqueue(record);

        public void OnDropped(Guid serviceId, ProcessOutputStream stream, long count)
        {
        }
    }


    private sealed class FixtureProcessHarness : IAsyncDisposable
    {
        private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(3);
        private readonly PosixProcessExecutor _executor;
        private readonly ServiceHealthProbe _probe;
        private readonly Guid _serviceId;
        private ProcessInstanceId? _instanceId;
        private bool _disposed;
        private readonly CapturingOutputSink? _outputSink;


        private FixtureProcessHarness(
            string fixturePath,
            string workingDirectory,
            string helperPath,
            int port,
            ImmutableArray<string> arguments,
            CapturingOutputSink? outputSink)
        {
            FixturePath = fixturePath;
            WorkingDirectory = workingDirectory;
            Port = port;
            Arguments = arguments;
            _outputSink = outputSink;
            _serviceId = Guid.CreateVersion7();
            _executor = new PosixProcessExecutor(helperPath, StopGrace, outputSink);
            _probe = new ServiceHealthProbe(_executor);
        }


        internal string FixturePath { get; }
        internal string WorkingDirectory { get; }
        internal int Port { get; }
        internal ImmutableArray<string> Arguments { get; }
        internal Uri HealthUri => new($"http://127.0.0.1:{Port}/fixture/health");

        internal static FixtureProcessHarness Create() => Create([]);
        internal static FixtureProcessHarness CreateDescendant() =>
            CreateWithOutput(new CapturingOutputSink(), "--mode", "descendant");


        internal static async Task<FixtureProcessHarness> CreateAsync(
            IReadOnlyList<string> additionalArguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Create(additionalArguments.ToArray());
        }

        internal static FixtureProcessHarness Create(params string[] additionalArguments) =>
            CreateWithOutput(null, additionalArguments);

        private static FixtureProcessHarness CreateWithOutput(
            CapturingOutputSink? outputSink,
            params string[] additionalArguments)
        {
            var fixturePath = RuntimeArtifactLocator.RequireFixturePath();
            var helperPath = RuntimeArtifactLocator.RequireNativeHelperPath();
            using var reservation = EphemeralPortReservation.Create();
            var arguments = BuildArguments(reservation.Port, additionalArguments);
            return new FixtureProcessHarness(
                fixturePath,
                Path.GetDirectoryName(fixturePath)!,
                helperPath,
                reservation.Port,
                arguments,
                outputSink);
        }


        internal async Task<ProcessOperationResult> StartAsync(
            CancellationToken cancellationToken,
            string? fileName = null)
        {
            var specification = new ProcessLaunchSpecification(
                _serviceId,
                fileName ?? FixturePath,
                WorkingDirectory,
                Arguments,
                new ProcessEnvironment(new Dictionary<string, string>(StringComparer.Ordinal)));
            var result = await _executor.StartAsync(specification, cancellationToken).ConfigureAwait(false);
            if (result.InstanceId is { } instanceId && result.Status == ProcessOperationStatus.Accepted)
            {
                _instanceId = instanceId;
            }

            return result;
        }

        internal async Task<ProcessOperationResult> StopAsync(CancellationToken cancellationToken)
        {
            var instanceId = _instanceId;
            if (instanceId is null)
            {
                return await _executor.StopAsync(_serviceId, StopGrace, cancellationToken).ConfigureAwait(false);
            }

            var result = await _executor.StopAsync(instanceId.Value, StopGrace, cancellationToken).ConfigureAwait(false);
            _instanceId = null;
            return result;
        }
        internal async Task<DescendantEvidence> WaitForDescendantEvidenceAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var outputSink = _outputSink ?? throw new InvalidOperationException("descendant output capture is unavailable");
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(timeout);
            try
            {
                while (!bounded.IsCancellationRequested)
                {
                    while (outputSink.TryDequeue(out var record))
                    {
                        if (record.Stream == ProcessOutputStream.Stdout &&
                            TryReadDescendantEvidence(record.Text, out var evidence))
                        {
                            return evidence;
                        }
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(25), bounded.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && bounded.IsCancellationRequested)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("descendant readiness evidence was not observed");
        }


        internal async Task<HealthObservationResult> WaitForHealthAsync(
            HealthObservationStatus expected,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            string path = "/fixture/health")
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(timeout);
            HealthObservationResult? latest = null;
            try
            {
                while (!bounded.IsCancellationRequested)
                {
                    latest = await _probe.ProbeAsync(CreateHealthRequest(path), bounded.Token).ConfigureAwait(false);
                    if (latest.Status == expected)
                    {
                        return latest;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(40), bounded.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && bounded.IsCancellationRequested)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            return latest ?? throw new InvalidOperationException("Health polling completed without an observation.");
        }
        internal async Task<int?> GetHealthStatusCodeAsync(CancellationToken cancellationToken)
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromSeconds(2)
            };
            try
            {
                using var response = await client.GetAsync(HealthUri, cancellationToken).ConfigureAwait(false);
                return (int)response.StatusCode;
            }
            catch (HttpRequestException)
            {
                return null;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        internal async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(TimeSpan.FromSeconds(2));
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, Port, bounded.Token).ConfigureAwait(false);
                return client.Connected;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (OperationCanceledException) when (bounded.IsCancellationRequested)
            {
                return false;
            }
        }

        internal ServiceHealthProbeRequest CreateHealthRequest(string path = "/fixture/health") => new(
            _serviceId,
            new HealthCheckDefinition(
                ServiceHealthCheckKind.Http,
                TimeSpan.FromSeconds(1),
                path),
            new LoopbackEndpoint(LoopbackAddressKind.IPv4, Port));

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                using var timeout = new CancellationTokenSource(StopGrace);
                await StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // Cleanup must not hide the assertion that caused disposal.
                try
                {
                    await _executor.StopAsync(_serviceId, StopGrace, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
            finally
            {
                _probe.Dispose();
            }
        }

        private static ImmutableArray<string> BuildArguments(
            int port,
            string[] additionalArguments)
        {
            var builder = ImmutableArray.CreateBuilder<string>(6 + additionalArguments.Length);
            builder.Add("--listen-address");
            builder.Add("127.0.0.1");
            builder.Add("--port");
            builder.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.AddRange(additionalArguments);
            return builder.ToImmutable();
        }
    }

    private sealed class HostLifecycleHarness : IAsyncDisposable
    {
        private readonly ServiceHealthProbe _probe;
        private readonly DelegatingHealthProbe _lifecycleProbe;
        private bool _disposed;

        private HostLifecycleHarness(
            HostServiceLifecycleManager manager,
            HostConfigurationSnapshot snapshot,
            HostServiceEndpointSnapshotPublisher publisher,
            HostRuntimeState runtimeState,
            InMemoryLeaseStore leaseStore,
            ServiceHealthProbe probe,
            DelegatingHealthProbe lifecycleProbe,
            ServiceHealthProbeRequest healthRequest,
            Guid serviceId,
            int port,
            string nodeId,
            CancellationTokenSource timeout)
        {
            Manager = manager;
            Snapshot = snapshot;
            Publisher = publisher;
            RuntimeState = runtimeState;
            LeaseStore = leaseStore;
            _probe = probe;
            _lifecycleProbe = lifecycleProbe;
            HealthRequest = healthRequest;
            ServiceId = serviceId;
            Port = port;
            NodeId = nodeId;
            Timeout = timeout;
        }

        internal HostServiceLifecycleManager Manager { get; }
        internal HostConfigurationSnapshot Snapshot { get; }
        internal HostServiceEndpointSnapshotPublisher Publisher { get; }
        internal HostRuntimeState RuntimeState { get; }
        internal ServiceHealthProbeRequest HealthRequest { get; }
        internal InMemoryLeaseStore LeaseStore { get; }
        internal ServiceHealthProbe Probe => _probe;
        internal bool FailHealth
        {
            set => _lifecycleProbe.FailUnavailable = value;
        }
        internal Guid ServiceId { get; }
        internal int Port { get; }
        internal string NodeId { get; }
        internal int AutomaticPortRangeStart => Snapshot.GlobalSettings.AutoPortRangeStart;
        internal int AutomaticPortRangeEnd => Snapshot.GlobalSettings.AutoPortRangeEnd;
        internal CancellationTokenSource Timeout { get; }


        internal static async Task<HostLifecycleHarness> CreateAsync(
            ContractRestartPolicy restartPolicy,
            IReadOnlyList<string> additionalArguments,
            bool shortRenewalLease,
            CancellationToken cancellationToken,
            string healthPath = "/fixture/health",
            int automaticRangeWidth = 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfNegative(automaticRangeWidth);
            var fixture = FixtureProcessHarness.Create(additionalArguments.ToArray());
            var serviceId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            var rangeStart = automaticRangeWidth == 0
                ? fixture.Port
                : Math.Max(1, fixture.Port - automaticRangeWidth);
            var rangeEnd = automaticRangeWidth == 0
                ? fixture.Port
                : Math.Min(65_535, fixture.Port + automaticRangeWidth);
            var healthRequest = new ServiceHealthProbeRequest(
                serviceId,
                new HealthCheckDefinition(
                    ServiceHealthCheckKind.Http,
                    TimeSpan.FromSeconds(1),
                    healthPath),
                new LoopbackEndpoint(LoopbackAddressKind.IPv4, fixture.Port));
            var service = new ServiceConfiguration(
                serviceId,
                enabled: true,
                fileName: fixture.FixturePath,
                argumentList: ImmutableArray.Create(
                    "--listen-address",
                    "127.0.0.1",
                    "--port",
                    "$PORT").AddRange(additionalArguments),
                workingDirectory: fixture.WorkingDirectory,
                environment: ImmutableDictionary<string, string>.Empty,
                startMode: ServiceStartMode.Lazy,
                restartPolicy: restartPolicy,
                healthCheck: new ServiceHealthCheckConfiguration(
                    ServiceHealthCheckType.Http,
                    healthPath,
                    TimeSpan.FromSeconds(1)),
                createdAt: now,
                updatedAt: now,
                version: 1);
            var snapshot = new HostConfigurationSnapshot(
                1,
                new GlobalSettingsConfiguration(
                    version: 1,
                    autoPortRangeStart: rangeStart,
                    autoPortRangeEnd: rangeEnd,
                    configurationPollInterval: TimeSpan.FromSeconds(1)),
                ImmutableArray<RouteConfiguration>.Empty,
                ImmutableArray.Create(service),
                ImmutableArray<ExtensionRecordConfiguration>.Empty,
                ImmutableArray<ExtensionSettingsConfiguration>.Empty);
            var holder = new HostConfigurationSnapshotHolder();
            Assert.True(holder.TryReplace(snapshot));
            var runtimeState = new HostRuntimeState(holder, new HostNodeOptions(false, false, false));
            MarkSnapshotAccepted(runtimeState);
            var publisher = new HostServiceEndpointSnapshotPublisher();
            var leaseStore = new InMemoryLeaseStore(shortRenewalLease, fixture.Port);
            var executor = new PosixProcessExecutor(
                RuntimeArtifactLocator.RequireNativeHelperPath(),
                TimeSpan.FromSeconds(3));
            var probe = new ServiceHealthProbe(executor);
            var lifecycleProbe = new DelegatingHealthProbe(probe);
            var nodeId = "integration-" + Guid.NewGuid().ToString("N");
            var manager = new HostServiceLifecycleManager(
                executor,
                lifecycleProbe,
                leaseStore,
                holder,
                publisher,
                runtimeState,
                new HostRuntimeOptions("Host=integration-only", nodeId, false));
            await fixture.DisposeAsync().ConfigureAwait(false);
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            // Ownership is transferred to the manager's executor; fixture only provided paths and port.
            await Task.CompletedTask;
            return new HostLifecycleHarness(
                manager,
                snapshot,
                publisher,
                runtimeState,
                leaseStore,
                probe,
                lifecycleProbe,
                healthRequest,
                serviceId,
                fixture.Port,
                nodeId,
                timeout);
        }


        internal async Task<HealthObservationResult> WaitForHealthAsync(HealthObservationStatus expected)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Timeout.Token);
            bounded.CancelAfter(TimeSpan.FromSeconds(8));
            HealthObservationResult latest = new(
                ServiceId,
                HealthObservationStatus.Unavailable,
                DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                1);
            try
            {
                while (!bounded.IsCancellationRequested)
                {
                    latest = await _probe.ProbeAsync(HealthRequest, bounded.Token).ConfigureAwait(false);
                    if (latest.Status == expected)
                    {
                        return latest;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(40), bounded.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!Timeout.IsCancellationRequested && bounded.IsCancellationRequested)
            {
            }

            return latest;
        }

        internal async Task AssertPublisherRemainsEmptyAsync(TimeSpan duration)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Timeout.Token);
            bounded.CancelAfter(duration);
            try
            {
                while (!bounded.IsCancellationRequested)
                {
                    Assert.Empty(Publisher.Current);
                    await Task.Delay(TimeSpan.FromMilliseconds(40), bounded.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!Timeout.IsCancellationRequested && bounded.IsCancellationRequested)
            {
            }
        }

        internal async Task WaitForPublisherCountAsync(int count)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Timeout.Token);
            bounded.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                while (!bounded.IsCancellationRequested)
                {
                    if (Publisher.Current.Count == count)
                    {
                        return;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(40), bounded.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!Timeout.IsCancellationRequested && bounded.IsCancellationRequested)
            {
            }

            Assert.Equal(count, Publisher.Current.Count);
        }
        internal async Task ObserveReadyHealthAsync()
        {
            var method = typeof(HostServiceLifecycleManager).GetMethod(
                "ObserveReadyHealthAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var task = method!.Invoke(Manager, [Timeout.Token]) as Task;
            Assert.NotNull(task);
            await task!.WaitAsync(Timeout.Token).ConfigureAwait(false);
        }


        internal async Task WaitForPortUnavailableAsync()
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Timeout.Token);
            bounded.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                while (!bounded.IsCancellationRequested)
                {
                    if (!bounded.IsCancellationRequested &&
                        !await CanConnectAsync(bounded.Token).ConfigureAwait(false))
                    {
                        return;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(40), bounded.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!Timeout.IsCancellationRequested && bounded.IsCancellationRequested)
            {
            }

            Assert.False(await CanConnectAsync(CancellationToken.None).ConfigureAwait(false));
        }

        internal async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(TimeSpan.FromSeconds(2));
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, Port, bounded.Token).ConfigureAwait(false);
                return client.Connected;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (OperationCanceledException) when (bounded.IsCancellationRequested)
            {
                return false;
            }
        }

        internal async Task RenewLeasesAsync()
        {
            var method = typeof(HostServiceLifecycleManager).GetMethod(
                "RenewLeasesAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var task = method!.Invoke(Manager, [Timeout.Token]) as Task;
            Assert.NotNull(task);
            await task!.WaitAsync(Timeout.Token).ConfigureAwait(false);
        }



        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await Manager.StopAsync(stopTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await Manager.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
            finally
            {
                _probe.Dispose();
                Timeout.Dispose();
            }
        }

        private static void MarkSnapshotAccepted(HostRuntimeState state)
        {
            var method = typeof(HostRuntimeState).GetMethod(
                "MarkSnapshotAccepted",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method!.Invoke(state, null);
        }
    }

    private sealed class DelegatingHealthProbe : IServiceHealthProbe
    {
        private readonly ServiceHealthProbe _inner;
        private int _failUnavailable;

        internal DelegatingHealthProbe(ServiceHealthProbe inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        internal bool FailUnavailable
        {
            set => Interlocked.Exchange(ref _failUnavailable, value ? 1 : 0);
        }

        public ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (Volatile.Read(ref _failUnavailable) != 0 && !cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(new HealthObservationResult(
                    request.ServiceId,
                    HealthObservationStatus.Unavailable,
                    DateTimeOffset.UtcNow,
                    TimeSpan.Zero,
                    1));
            }

            return _inner.ProbeAsync(request, cancellationToken);
        }
    }

    private sealed class InMemoryLeaseStore : IPortLeaseStore
    {
        private readonly bool _shortRenewalLease;
        private readonly int _automaticPort;
        private readonly ConcurrentQueue<PortLeaseRequest> _automaticAcquireRequests = new();
        private long _version;

        internal InMemoryLeaseStore(bool shortRenewalLease, int automaticPort)
        {
            _shortRenewalLease = shortRenewalLease;
            _automaticPort = automaticPort;
        }

        internal bool FailRenewals { get; set; }

        internal IReadOnlyList<PortLeaseRequest> AutomaticAcquireRequests =>
            _automaticAcquireRequests.ToArray();

        public ValueTask<PortLeaseOperationResult> ApplyAsync(
            PortLeaseIntent intent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (intent.Kind == PortLeaseIntentKind.Renew)
            {
                if (FailRenewals)
                {
                    return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.DatabaseUnavailable));
                }

                var renewal = intent.Renewal!;
                return ValueTask.FromResult(new PortLeaseOperationResult(
                    PortLeaseOperationStatus.Applied,
                    CreateLease(renewal.NodeId, renewal.ServiceId, renewal.Port)));
            }

            if (intent.Kind == PortLeaseIntentKind.Release)
            {
                return ValueTask.FromResult(new PortLeaseOperationResult(PortLeaseOperationStatus.NotFound));
            }

            var request = intent.Request!;
            if (request.Port == 0)
            {
                _automaticAcquireRequests.Enqueue(request);
            }

            var acquiredPort = request.Port == 0 ? _automaticPort : request.Port;
            return ValueTask.FromResult(new PortLeaseOperationResult(
                PortLeaseOperationStatus.Applied,
                CreateLease(request.NodeId, request.ServiceId, acquiredPort)));
        }

        private PortLease CreateLease(NodeIdentifier nodeId, Guid serviceId, int port)
        {
            var now = DateTimeOffset.UtcNow;
            var lifetime = _shortRenewalLease ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(30);
            return new PortLease(
                nodeId,
                serviceId,
                port,
                now,
                now.Add(lifetime),
                Interlocked.Increment(ref _version));
        }
    }

    private sealed class EphemeralPortReservation : IDisposable
    {
        private readonly TcpListener _listener;
        private bool _disposed;

        private EphemeralPortReservation(TcpListener listener, int port)
        {
            _listener = listener;
            Port = port;
        }

        internal int Port { get; }

        internal static EphemeralPortReservation Create()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new EphemeralPortReservation(listener, ((IPEndPoint)listener.LocalEndpoint).Port);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _listener.Stop();
        }
    }

    private static class RuntimeArtifactLocator
    {
        internal static string RequireFixturePath() =>
            RequireExisting(
                "Fixtures.Microservice",
                "Fixtures.Microservice executable",
                includeRuntimeIdentifier: false);

        internal static string RequireNativeHelperPath() =>
            RequireExisting(
                "Nekolla.Nekostick.NativeHelper",
                "native process helper",
                includeRuntimeIdentifier: true);

        private static string RequireExisting(string fileName, string description, bool includeRuntimeIdentifier)
        {
            foreach (var candidate in EnumerateCandidates(fileName, includeRuntimeIdentifier))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            Assert.Skip($"The compiled {description} is unavailable; build the repository test artifacts first.");
            return string.Empty;
        }

        private static IEnumerable<string> EnumerateCandidates(string fileName, bool includeRuntimeIdentifier)
        {
            var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
            var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
            for (var current = baseDirectory; current is not null; current = current.Parent)
            {
                yield return Path.Combine(current.FullName, fileName);
                yield return Path.Combine(current.FullName, "tests", "Fixtures.Microservice", "bin", "Debug", "net10.0", fileName);
                yield return Path.Combine(current.FullName, "tests", "Fixtures.Microservice", "bin", "Release", "net10.0", fileName);
                if (includeRuntimeIdentifier)
                {
                    yield return Path.Combine(
                        current.FullName,
                        "src",
                        "Nekolla.Nekostick.Host",
                        ".nativehelper",
                        "Debug",
                        runtimeIdentifier,
                        fileName);
                    yield return Path.Combine(
                        current.FullName,
                        "src",
                        "Nekolla.Nekostick.Host",
                        ".nativehelper",
                        "Release",
                        runtimeIdentifier,
                        fileName);
                    yield return Path.Combine(
                        current.FullName,
                        "src",
                        "Nekolla.Nekostick.NativeHelper",
                        "bin",
                        "Debug",
                        "net10.0",
                        runtimeIdentifier,
                        fileName);
                    yield return Path.Combine(
                        current.FullName,
                        "src",
                        "Nekolla.Nekostick.NativeHelper",
                        "bin",
                        "Release",
                        "net10.0",
                        runtimeIdentifier,
                        fileName);
                    yield return Path.Combine(
                        current.FullName,
                        "src",
                        "Nekolla.Nekostick.NativeHelper",
                        "bin",
                        "Debug",
                        "net10.0",
                        fileName);
                    yield return Path.Combine(
                        current.FullName,
                        "src",
                        "Nekolla.Nekostick.NativeHelper",
                        "bin",
                        "Release",
                        "net10.0",
                        fileName);
                }
            }
        }
    }
}
