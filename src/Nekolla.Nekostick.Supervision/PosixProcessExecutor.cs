using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ProcessInstanceId, ProcessLease> leases = new();
    private readonly ConcurrentDictionary<long, Action<ProcessExitObservation>> observers = new();
    private long nextObserverId;

    /// <summary>Creates a helper-backed executor using an absolute extracted helper path.</summary>
    /// <param name="helperPath">The absolute helper executable or DLL path, or null to reject starts.</param>
    /// <param name="defaultStopGracePeriod">The helper's bounded graceful-stop period.</param>
    /// <param name="outputSink">The optional bounded child-output sink.</param>
    /// <param name="logger">The optional supervision logger.</param>
    public PosixProcessExecutor(
        string? helperPath = null,
        TimeSpan? defaultStopGracePeriod = null,
        IProcessOutputSink? outputSink = null,
        ILogger? logger = null)
    {
        this.helperPath = helperPath is not null && Path.IsPathRooted(helperPath) && File.Exists(helperPath)
            ? helperPath
            : null;
        helperGracePeriod = defaultStopGracePeriod ?? TimeSpan.FromSeconds(15);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(helperGracePeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(helperGracePeriod, TimeSpan.FromMinutes(5));

        this.outputSink = outputSink ?? NullProcessOutputSink.Instance;
        _logger = logger ?? NullLogger.Instance;
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
                PosixProcessSignals.TrySignalProcess(lease.ProcessId, SigTerm, _logger);
            }

            try
            {
                await lease.Exited.Task.WaitAsync(gracePeriod, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                var gracefulInstanceId = instanceId.ToString();
                SupervisionLogMessages.ProcessCleanupTimedOut(_logger, "GracefulStop", gracefulInstanceId);
                PosixProcessSignals.TrySignalGroup(lease.ProcessId, SigKill, _logger);
                try
                {
                    await lease.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    var forceReapInstanceId = instanceId.ToString();
                    SupervisionLogMessages.ProcessCleanupTimedOut(_logger, "ForceReap", forceReapInstanceId);
                    // The bounded force-reap wait elapsed; monitor ownership remains contained.
                }
            }
        }
        catch (Exception exception)
        {
            SupervisionLogMessages.ProcessCleanupFailed(_logger, exception, "Cleanup", instanceId.ToString());
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
            SupervisionLogMessages.ProcessStartCancelled(_logger, specification.ServiceId);
            return new(ProcessOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled);
        }

        if (!IsSupportedPlatform || helperPath is null)
        {
            SupervisionLogMessages.ProcessStartValidationRejected(_logger, "PlatformOrHelper", specification.ServiceId);
            return Rejected();
        }

        Process? process = null;
        try
        {
            process = CreateHelperProcess(specification);
            if (!process.Start())
            {
                SupervisionLogMessages.ProcessStartValidationRejected(_logger, "HelperStart", specification.ServiceId);
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
                SupervisionLogMessages.ProcessStartValidationRejected(_logger, "HelperReadyMarker", specification.ServiceId);
                await KillHelperAsync(process, _logger, specification.ServiceId.ToString()).ConfigureAwait(false);
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
                SupervisionLogMessages.ProcessStartValidationRejected(_logger, "LeaseRegistration", specification.ServiceId);
                await KillHelperAsync(process, _logger, specification.ServiceId.ToString()).ConfigureAwait(false);
                return Rejected();
            }

            lease.Monitor = MonitorAsync(lease, process.StandardOutput, process.StandardError);
            if (cancellationToken.IsCancellationRequested)
            {
                SupervisionLogMessages.ProcessStartCancelled(_logger, specification.ServiceId);
                await StopAsync(instanceId, TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
                return new(ProcessOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled);
            }
            return new(ProcessOperationStatus.Accepted, ServiceStateReasonCode.StartAccepted, instanceId, processId, processStartedAt);
        }
        catch (HostEnvironmentExpansionException exception)
        {
            SupervisionLogMessages.ProcessStartValidationRejected(_logger, "EnvironmentExpansion", specification.ServiceId);
            if (process is not null)
            {
                await KillHelperAsync(process, _logger, specification.ServiceId.ToString()).ConfigureAwait(false);
            }

            return exception.MissingPlaceholder is { } placeholder
                ? Rejected(placeholder)
                : Rejected();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SupervisionLogMessages.ProcessStartCancelled(_logger, specification.ServiceId);
            if (process is not null)
            {
                await KillHelperAsync(process, _logger, specification.ServiceId.ToString()).ConfigureAwait(false);
            }

            return new(ProcessOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled);
        }
        catch (Exception exception)
        {
            SupervisionLogMessages.ProcessStartFailed(_logger, exception, specification.ServiceId);
            if (process is not null)
            {
                await KillHelperAsync(process, _logger, specification.ServiceId.ToString()).ConfigureAwait(false);
            }

            return Rejected();
        }
    }

    /// <summary>Expands host placeholders without exposing expanded environment values.</summary>
    /// <param name="value">The opaque configured environment value.</param>
    /// <param name="maximumLength">The bounded expanded value length.</param>
    /// <returns>The value with host placeholders expanded once.</returns>
    private static string ExpandEnvironmentValue(string value, int maximumLength)
    {
        var offset = 0;
        var copiedThrough = 0;
        StringBuilder? expanded = null;
        while (true)
        {
            var start = value.IndexOf("${", offset, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            if (value.IndexOf("${HOST:", start, StringComparison.Ordinal) != start)
            {
                // Read-path compatibility keeps non-HOST ${...} text literal. Skip a
                // complete literal so a nested token cannot be expanded accidentally.
                var literalEnd = value.IndexOf('}', start + 2);
                if (literalEnd < 0)
                {
                    break;
                }

                offset = literalEnd + 1;
                continue;
            }

            var variableStart = start + 7;
            var end = value.IndexOf('}', variableStart);
            if ((start > 0 && value[start - 1] == '\\') ||
                end < 0 ||
                end - start + 1 > 256 ||
                end <= variableStart ||
                !IsValidHostVariableName(value, variableStart, end))
            {
                throw new HostEnvironmentExpansionException();
            }

            var variableName = value.Substring(variableStart, end - variableStart);
            var hostValue = System.Environment.GetEnvironmentVariable(variableName);
            if (hostValue is null)
            {
                throw new HostEnvironmentExpansionException(value.Substring(start, end - start + 1));
            }

            expanded ??= new StringBuilder(Math.Min(value.Length, maximumLength));
            var literalLength = start - copiedThrough;
            if (expanded.Length > maximumLength - literalLength ||
                hostValue.Length > maximumLength - expanded.Length - literalLength)
            {
                throw new HostEnvironmentExpansionException();
            }

            expanded.Append(value, copiedThrough, literalLength);
            expanded.Append(hostValue);
            copiedThrough = end + 1;
            offset = copiedThrough;
        }

        if (expanded is null)
        {
            if (value.Length > maximumLength)
            {
                throw new HostEnvironmentExpansionException();
            }

            return value;
        }

        var suffixLength = value.Length - copiedThrough;
        if (expanded.Length > maximumLength - suffixLength)
        {
            throw new HostEnvironmentExpansionException();
        }

        return expanded.Append(value, copiedThrough, suffixLength).ToString();
    }

    private static bool IsValidHostVariableName(string value, int start, int end)
    {
        if (!(char.IsAsciiLetter(value[start]) || value[start] == '_'))
        {
            return false;
        }

        for (var index = start + 1; index < end; index++)
        {
            var character = value[index];
            if (!(char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class HostEnvironmentExpansionException : InvalidOperationException
    {
        internal HostEnvironmentExpansionException(string? missingPlaceholder = null)
            : base(missingPlaceholder is null
                ? "The process environment contains an invalid host placeholder."
                : $"The host environment variable placeholder {missingPlaceholder} is not defined.")
        {
            MissingPlaceholder = missingPlaceholder;
        }

        internal string? MissingPlaceholder { get; }
    }

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
            // The helper inherits this expanded environment and its direct child inherits
            // it again; launch arguments were already given their independent $PORT pass.
            startInfo.Environment[pair.Key] = ExpandEnvironmentValue(
                pair.Value,
                specification.Limits.MaximumEnvironmentValueLength);
        }

        return new Process { StartInfo = startInfo, EnableRaisingEvents = false };
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
            var multipleInstancesId = serviceId.ToString();
            SupervisionLogMessages.OperationValidationRejected(_logger, "StopMultipleInstances", multipleInstancesId);
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
            var rejectedInstanceId = instanceId.ToString();
            SupervisionLogMessages.OperationValidationRejected(_logger, "StopGracePeriod", rejectedInstanceId);
            return new(ProcessOperationStatus.Rejected, ServiceStateReasonCode.StopRequested);
        }

        if (!leases.TryGetValue(instanceId, out var lease))
        {
            return new(ProcessOperationStatus.Completed, ServiceStateReasonCode.StopCompleted);
        }

        if (Interlocked.Exchange(ref lease.StopRequested, 1) == 0)
        {
            PosixProcessSignals.TrySignalProcess(lease.ProcessId, SigTerm, _logger);
        }

        var stoppedInstanceId = instanceId.ToString();
        try
        {
            await lease.Exited.Task.WaitAsync(gracePeriod, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            SupervisionLogMessages.ProcessStopTimedOut(_logger, lease.ServiceId, stoppedInstanceId);
            PosixProcessSignals.TrySignalGroup(lease.ProcessId, SigKill, _logger);
            try
            {
                await lease.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                SupervisionLogMessages.ProcessStopTimedOut(_logger, lease.ServiceId, stoppedInstanceId);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SupervisionLogMessages.ProcessStopCancelled(_logger, lease.ServiceId, stoppedInstanceId);
            PosixProcessSignals.TrySignalGroup(lease.ProcessId, SigKill, _logger);
            try
            {
                await lease.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                SupervisionLogMessages.ProcessStopTimedOut(_logger, lease.ServiceId, stoppedInstanceId);
                throw;
            }

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
            catch (Exception exception)
            {
                SupervisionLogMessages.ProcessMonitorFailed(
                    _logger,
                    exception,
                    "WaitForExit",
                    lease.ServiceId,
                    lease.InstanceId.ToString());
            }

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SupervisionLogMessages.ProcessOutputCaptureFailed(
                    _logger,
                    exception,
                    "stdout/stderr",
                    lease.ServiceId,
                    lease.InstanceId.ToString());
            }

            if (exited)
            {
                try
                {
                    successfulExit = lease.Process.ExitCode == 0;
                }
                catch (Exception exception)
                {
                    var exitCodeInstanceId = lease.InstanceId.ToString();
                    SupervisionLogMessages.ProcessExitCodeReadFailed(
                        _logger,
                        exception,
                        lease.ServiceId,
                        exitCodeInstanceId);
                    successfulExit = false;
                }
            }
        }
        catch (Exception exception)
        {
            SupervisionLogMessages.ProcessMonitorFailed(
                _logger,
                exception,
                "Monitor",
                lease.ServiceId,
                lease.InstanceId.ToString());
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
                catch (Exception exception)
                {
                    SupervisionLogMessages.ProcessExitObserverFailed(
                        _logger,
                        exception,
                        observation.ServiceId,
                        observation.InstanceId.ToString());
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


    private static async Task KillHelperAsync(Process process, ILogger logger, string entityId)
    {
        try
        {
            if (!process.HasExited)
            {
                PosixProcessSignals.TrySignalProcess(process.Id, SigKill, logger);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            SupervisionLogMessages.ProcessCleanupTimedOut(logger, "KillHelper", entityId);
        }
        catch (Exception exception)
        {
            SupervisionLogMessages.ProcessCleanupFailed(logger, exception, "KillHelper", entityId);
            // Startup cleanup is best effort and never exposes process details.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static ProcessOperationResult Rejected(string? failureMessage = null) =>
        failureMessage is null
            ? new(ProcessOperationStatus.Rejected, ServiceStateReasonCode.StartRejected)
            : new(
                ProcessOperationStatus.Rejected,
                ServiceStateReasonCode.MissingHostEnvironment,
                failureMessage: failureMessage);

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

