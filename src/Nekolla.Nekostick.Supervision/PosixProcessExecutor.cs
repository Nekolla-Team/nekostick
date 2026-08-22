using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Nekolla.Nekostick.Supervision;

/// <summary>Starts and stops bounded service process generations through the RID-gated helper.</summary>
public sealed class PosixProcessExecutor : IProcessInstanceExecutor, IProcessLiveness, IProcessExitObserver, IProcessExecutorCleanup
{
    private const int SigTerm = 15;
    private const int SigKill = 9;
    private const int MaximumOutputLinesPerSecond = 200;
    private const int MaximumOutputBytesPerSecond = 1024 * 1024;
    private readonly string? helperPath;
    private readonly TimeSpan helperGracePeriod;
    private readonly IProcessOutputSink outputSink;
    private readonly ConcurrentDictionary<ProcessInstanceId, ProcessLease> leases = new();
    private readonly ConcurrentDictionary<long, Action<ProcessExitObservation>> observers = new();
    private long nextObserverId;

    /// <summary>Creates a helper-backed executor using an absolute extracted helper path.</summary>
    /// <param name="helperPath">The absolute helper executable or DLL path, or null to reject starts.</param>
    /// <param name="defaultStopGracePeriod">The helper's bounded graceful-stop period.</param>
    /// <param name="outputSink">The optional bounded child-output sink.</param>
    public PosixProcessExecutor(
        string? helperPath = null,
        TimeSpan? defaultStopGracePeriod = null,
        IProcessOutputSink? outputSink = null)
    {
        this.helperPath = helperPath is not null && Path.IsPathRooted(helperPath) && File.Exists(helperPath)
            ? helperPath
            : null;
        helperGracePeriod = defaultStopGracePeriod ?? TimeSpan.FromSeconds(15);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(helperGracePeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(helperGracePeriod, TimeSpan.FromMinutes(5));

        this.outputSink = outputSink ?? NullProcessOutputSink.Instance;
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action<ProcessExitObservation> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var id = Interlocked.Increment(ref nextObserverId);
        observers[id] = observer;
        return new ObserverSubscription(observers, id);
    }

    /// <inheritdoc />
    public async ValueTask CleanupAsync(
        TimeSpan gracePeriod,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (gracePeriod <= TimeSpan.Zero || gracePeriod > TimeSpan.FromMinutes(5))
        {
            return;
        }

        var instanceIds = leases.Keys.ToArray();
        if (instanceIds.Length == 0)
        {
            return;
        }

        await Task.WhenAll(instanceIds.Select(instanceId => CleanupInstanceAsync(instanceId, gracePeriod))).ConfigureAwait(false);
    }

    private async Task CleanupInstanceAsync(ProcessInstanceId instanceId, TimeSpan gracePeriod)
    {
        if (!leases.TryGetValue(instanceId, out var lease))
        {
            return;
        }

        try
        {
            if (Interlocked.Exchange(ref lease.StopRequested, 1) == 0)
            {
                PosixProcessSignals.TrySignalProcess(lease.ProcessId, SigTerm);
            }

            try
            {
                await lease.Exited.Task.WaitAsync(gracePeriod, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                PosixProcessSignals.TrySignalGroup(lease.ProcessId, SigKill);
                try
                {
                    await lease.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The bounded force-reap wait elapsed; monitor ownership remains contained.
                }
            }
        }
        catch
        {
            // Owned-process cleanup is best effort and never exposes process details.
        }
    }

    /// <inheritdoc />
    public async ValueTask<ProcessOperationResult> StartAsync(
        ProcessLaunchSpecification specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (cancellationToken.IsCancellationRequested)
        {
            return new(ProcessOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled);
        }

        if (!IsSupportedPlatform || helperPath is null)
        {
            return Rejected();
        }

        Process? process = null;
        try
        {
            process = CreateHelperProcess(specification);
            if (!process.Start())
            {
                return Rejected();
            }
            var processStartedAt = DateTimeOffset.UtcNow;

            var launchRequest = new HelperLaunchRequest(
                specification.FileName,
                specification.WorkingDirectory,
                specification.Arguments.ToArray());
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(launchRequest)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();

            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var marker = await process.StandardError.ReadLineAsync(startupTimeout.Token).ConfigureAwait(false);
            if (!string.Equals(marker, "NK_READY", StringComparison.Ordinal))
            {
                await KillHelperAsync(process).ConfigureAwait(false);
                return Rejected();
            }

            var instanceId = new ProcessInstanceId(Guid.NewGuid());
            var processId = process.Id;
            var lease = new ProcessLease(
                instanceId,
                specification.ServiceId,
                process,
                processStartedAt,
                new ProcessOutputBudget(MaximumOutputLinesPerSecond, MaximumOutputBytesPerSecond));
            if (!leases.TryAdd(instanceId, lease))
            {
                await KillHelperAsync(process).ConfigureAwait(false);
                return Rejected();
            }

            lease.Monitor = MonitorAsync(lease, process.StandardOutput, process.StandardError);
            if (cancellationToken.IsCancellationRequested)
            {
                await StopAsync(instanceId, TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
                return new(ProcessOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled);
            }

            return new(ProcessOperationStatus.Accepted, ServiceStateReasonCode.StartAccepted, instanceId, processId, processStartedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (process is not null)
            {
                await KillHelperAsync(process).ConfigureAwait(false);
            }

            return new(ProcessOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled);
        }
        catch
        {
            if (process is not null)
            {
                await KillHelperAsync(process).ConfigureAwait(false);
            }

            return Rejected();
        }
    }

    /// <summary>Stops the current process generation for a service.</summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="gracePeriod">The graceful stop period.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A safe fixed-code operation result.</returns>
    public ValueTask<ProcessOperationResult> StopAsync(
        Guid serviceId,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken = default)
    {
        var matches = leases.Values.Where(lease => lease.ServiceId == serviceId).ToArray();
        if (matches.Length > 1)
        {
            return ValueTask.FromResult(new ProcessOperationResult(ProcessOperationStatus.Rejected, ServiceStateReasonCode.StopRequested));
        }

        return matches.Length == 0
            ? ValueTask.FromResult(new ProcessOperationResult(ProcessOperationStatus.Completed, ServiceStateReasonCode.StopCompleted))
            : StopAsync(matches[0].InstanceId, gracePeriod, cancellationToken);
    }
    /// <inheritdoc />
    public async ValueTask<ProcessOperationResult> StopAsync(
        ProcessInstanceId instanceId,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken = default)
    {
        if (gracePeriod <= TimeSpan.Zero || gracePeriod > TimeSpan.FromMinutes(5))
        {
            return new(ProcessOperationStatus.Rejected, ServiceStateReasonCode.StopRequested);
        }

        if (!leases.TryGetValue(instanceId, out var lease))
        {
            return new(ProcessOperationStatus.Completed, ServiceStateReasonCode.StopCompleted);
        }

        if (Interlocked.Exchange(ref lease.StopRequested, 1) == 0)
        {
            PosixProcessSignals.TrySignalProcess(lease.ProcessId, SigTerm);
        }

        try
        {
            await lease.Exited.Task.WaitAsync(gracePeriod, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            PosixProcessSignals.TrySignalGroup(lease.ProcessId, SigKill);
            await lease.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PosixProcessSignals.TrySignalGroup(lease.ProcessId, SigKill);
            await lease.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            return new(ProcessOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled);
        }

        return new(ProcessOperationStatus.Completed, ServiceStateReasonCode.StopCompleted);
    }

    private async Task MonitorAsync(ProcessLease lease, TextReader stdout, TextReader stderr)
    {
        var stdoutTask = ProcessOutputCapture.ReadAsync(
            stdout,
            lease.ServiceId,
            ProcessOutputStream.Stdout,
            lease.Budget,
            outputSink,
            CancellationToken.None);
        var stderrTask = ProcessOutputCapture.ReadAsync(
            stderr,
            lease.ServiceId,
            ProcessOutputStream.Stderr,
            lease.Budget,
            outputSink,
            CancellationToken.None,
            skipMarker: true);
        var exited = false;
        var successfulExit = false;
        try
        {
            try
            {
                await lease.Process.WaitForExitAsync().ConfigureAwait(false);
                exited = true;
            }
            catch
            {
                // Process details and exception text never cross the executor boundary.
            }

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch
            {
                // Output capture failures are contained after both readers finish.
            }

            if (exited)
            {
                try
                {
                    successfulExit = lease.Process.ExitCode == 0;
                }
                catch
                {
                    successfulExit = false;
                }
            }
        }
        finally
        {
            leases.TryRemove(lease.InstanceId, out _);
            lease.Process.Dispose();
            lease.Exited.TrySetResult(true);
            if (exited)
            {
                PublishExit(lease, successfulExit, DateTimeOffset.UtcNow);
            }
        }
    }

    private void PublishExit(ProcessLease lease, bool successfulExit, DateTimeOffset exitedAt)
    {
        var callbacks = observers.Values.ToArray();
        if (callbacks.Length == 0)
        {
            return;
        }

        var observation = new ProcessExitObservation(lease.ServiceId, lease.InstanceId, successfulExit, exitedAt);
        _ = Task.Run(() =>
        {
            foreach (var callback in callbacks)
            {
                try
                {
                    callback(observation);
                }
                catch
                {
                    // Observer failures are isolated from executor lifecycle cleanup.
                }
            }
        });
    }


    private sealed class ObserverSubscription : IDisposable
    {
        private readonly ConcurrentDictionary<long, Action<ProcessExitObservation>> observers;
        private readonly long id;
        private int disposed;

        internal ObserverSubscription(
            ConcurrentDictionary<long, Action<ProcessExitObservation>> observers,
            long id)
        {
            this.observers = observers;
            this.id = id;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                observers.TryRemove(id, out _);
            }
        }
    }

    bool IProcessLiveness.IsRunning(Guid serviceId) =>
        leases.Values.Any(lease => lease.ServiceId == serviceId && !lease.Exited.Task.IsCompleted);

    bool IProcessLiveness.IsRunning(Guid serviceId, ProcessInstanceId instanceId) =>
        leases.TryGetValue(instanceId, out var lease) &&
        lease.ServiceId == serviceId &&
        !lease.Exited.Task.IsCompleted;

    private Process CreateHelperProcess(ProcessLaunchSpecification specification)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath!.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : helperPath,
            WorkingDirectory = specification.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false, false),
            StandardErrorEncoding = new UTF8Encoding(false, false)
        };
        if (helperPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(helperPath);
        }

        startInfo.ArgumentList.Add("--grace-ms");
        startInfo.ArgumentList.Add(((int)helperGracePeriod.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
        foreach (var pair in specification.Environment.Values)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return new Process { StartInfo = startInfo, EnableRaisingEvents = false };
    }

    private static async Task KillHelperAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                PosixProcessSignals.TrySignalProcess(process.Id, SigKill);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch
        {
            // Startup cleanup is best effort and never exposes process details.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static ProcessOperationResult Rejected() =>
        new(ProcessOperationStatus.Rejected, ServiceStateReasonCode.StartRejected);

    private static bool IsSupportedPlatform =>
        (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) &&
        (RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.X64) &&
        (RuntimeInformation.RuntimeIdentifier is "osx-arm64" or "osx-x64" or "linux-arm64" or "linux-x64");

    private sealed record HelperLaunchRequest(string FileName, string WorkingDirectory, string[] Arguments);

    private sealed class ProcessLease
    {
        internal ProcessLease(
            ProcessInstanceId instanceId,
            Guid serviceId,
            Process process,
            DateTimeOffset startedAt,
            ProcessOutputBudget budget)
        {
            InstanceId = instanceId;
            ServiceId = serviceId;
            Process = process;
            ProcessId = process.Id;
            StartedAt = startedAt.ToUniversalTime();
            Budget = budget;
        }

        internal ProcessInstanceId InstanceId { get; }
        internal Guid ServiceId { get; }
        internal Process Process { get; }
        internal int ProcessId { get; }
        internal DateTimeOffset StartedAt { get; }
        internal ProcessOutputBudget Budget { get; }
        internal Task? Monitor { get; set; }
        internal int StopRequested;
        internal TaskCompletionSource<bool> Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    }
}

