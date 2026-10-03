using System.Buffers;
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
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ProcessInstanceId, ProcessLease> leases = new();
    private readonly ConcurrentDictionary<long, Action<ProcessExitObservation>> observers = new();
    private readonly ConcurrentDictionary<long, CaptureSubscription> captureSubscriptions = new();
    private readonly ConcurrentDictionary<long, OutputTapSubscription> outputTapSubscriptions = new();
    private long nextObserverId;
    private long nextCaptureSubscriptionId;
    private long nextOutputTapSubscriptionId;

    /// <summary>Creates a helper-backed executor using an absolute extracted helper path.</summary>
    /// <param name="helperPath">The absolute helper executable or DLL path, or null to reject starts.</param>
    /// <param name="defaultStopGracePeriod">The helper's bounded graceful-stop period.</param>
    /// <param name="logger">The optional supervision logger.</param>
    public PosixProcessExecutor(
        string? helperPath = null,
        TimeSpan? defaultStopGracePeriod = null,
        ILogger? logger = null)
    {
        this.helperPath = helperPath is not null && Path.IsPathRooted(helperPath) && File.Exists(helperPath)
            ? helperPath
            : null;
        helperGracePeriod = defaultStopGracePeriod ?? TimeSpan.FromSeconds(15);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(helperGracePeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(helperGracePeriod, TimeSpan.FromMinutes(5));

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

    /// <summary>Subscribes to structured child-output capture for future process generations.</summary>
    /// <param name="sink">The sink that receives bounded decoded output lines.</param>
    /// <param name="enabledGate">An optional gate evaluated when each process generation starts.</param>
    /// <returns>A subscription that detaches the sink.</returns>
    public IDisposable SubscribeOutputCapture(IProcessOutputSink sink, Func<bool>? enabledGate = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var id = Interlocked.Increment(ref nextCaptureSubscriptionId);
        var subscription = new CaptureSubscription(this, id, sink, enabledGate);
        captureSubscriptions[id] = subscription;
        return subscription;
    }
    /// <summary>Subscribes to raw output and lifecycle events for future process generations.</summary>
    /// <param name="tap">The service log tap that receives every generation's output.</param>
    /// <returns>A subscription that detaches the tap.</returns>
    public IDisposable SubscribeOutputTap(IProcessOutputTap tap)
    {
        ArgumentNullException.ThrowIfNull(tap);
        var id = Interlocked.Increment(ref nextOutputTapSubscriptionId);
        var subscription = new OutputTapSubscription(this, id, tap);
        outputTapSubscriptions[id] = subscription;
        return subscription;
    }

    /// <summary>Gets whether any capture subscription is enabled for a new process generation.</summary>
    public bool HasActiveOutputCapture => captureSubscriptions.Values.Any(subscription => subscription.IsEnabled());

    /// <summary>Opens a raw readable stream for the current process generation.</summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="stream">The child-output stream to open.</param>
    /// <param name="output">The bounded raw output stream when the service is running.</param>
    /// <returns><see langword="true"/> when a current process generation was found.</returns>
    public bool TryOpenOutputStream(Guid serviceId, ProcessOutputStream stream, out Stream output)
    {
        if (!TryGetCurrentLease(serviceId, out var lease) || !TryGetFanout(lease, stream, out var fanout))
        {
            output = Stream.Null;
            return false;
        }

        output = fanout.OpenStream();
        return true;
    }

    /// <summary>Subscribes to raw chunks from the current process generation.</summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="stream">The child-output stream to subscribe to.</param>
    /// <param name="sink">The sink that receives raw chunks and completion notifications.</param>
    /// <returns>A subscription, or null when the service is not running.</returns>
    public IDisposable? TrySubscribeOutput(
        Guid serviceId,
        ProcessOutputStream stream,
        IProcessOutputChunkSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!TryGetCurrentLease(serviceId, out var lease) || !TryGetFanout(lease, stream, out var fanout))
        {
            return null;
        }

        return fanout.Subscribe(sink);
    }

    private bool TryGetCurrentLease(Guid serviceId, out ProcessLease lease)
    {
        ProcessLease? current = null;
        foreach (var candidate in leases.Values)
        {
            if (candidate.ServiceId != serviceId || candidate.Exited.Task.IsCompleted)
            {
                continue;
            }

            if (current is null || candidate.StartedAt > current.StartedAt)
            {
                current = candidate;
            }
        }

        lease = current!;
        return current is not null;
    }

    private static bool TryGetFanout(
        ProcessLease lease,
        ProcessOutputStream stream,
        out ProcessOutputFanout fanout)
    {
        switch (stream)
        {
            case ProcessOutputStream.Stdout:
                fanout = lease.Stdout;
                return true;
            case ProcessOutputStream.Stderr:
                fanout = lease.Stderr;
                return true;
            default:
                fanout = null!;
                return false;
        }
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
                    lease.FailOutputs();
                    // The bounded force-reap wait elapsed; monitor ownership remains contained.
                }
            }
        }
        catch (Exception exception)
        {
            SupervisionLogMessages.ProcessCleanupFailed(_logger, exception, "Cleanup", instanceId.ToString());
            lease.FailOutputs();
            // Owned-process cleanup is best effort and never exposes process details.
        }
    }

    private static async ValueTask<bool> ReadReadyMarkerAsync(Stream stderr, CancellationToken cancellationToken)
    {
        const string expected = "NK_READY";
        const int maximumMarkerLength = 64;
        var oneByte = ArrayPool<byte>.Shared.Rent(1);
        var position = 0;
        var length = 0;
        var matches = true;

        try
        {
            // One-byte reads avoid a buffering reader consuming early child output after the marker.
            while (true)
            {
                var read = await stderr.ReadAsync(oneByte.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return false;
                }

                var value = oneByte[0];
                if (value == (byte)'\n')
                {
                    return matches && position == expected.Length;
                }

                length++;
                if (length > maximumMarkerLength)
                {
                    return false;
                }

                if (matches)
                {
                    if (position >= expected.Length || value != (byte)expected[position])
                    {
                        matches = false;
                    }
                    else
                    {
                        position++;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(oneByte);
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
            var markerReady = await ReadReadyMarkerAsync(process.StandardError.BaseStream, startupTimeout.Token).ConfigureAwait(false);
            if (!markerReady)
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
                specification.AttemptNumber,
                _logger);
            if (!leases.TryAdd(instanceId, lease))
            {
                SupervisionLogMessages.ProcessStartValidationRejected(_logger, "LeaseRegistration", specification.ServiceId);
                lease.DisposeOutputs();
                await KillHelperAsync(process, _logger, specification.ServiceId.ToString()).ConfigureAwait(false);
                return Rejected();
            }

            AttachOutputTaps(lease);
            AttachCaptureConsumers(lease);
            lease.StartOutputPumps();
            lease.Monitor = MonitorAsync(lease);
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

    private void AttachCaptureConsumers(ProcessLease lease)
    {
        foreach (var subscription in captureSubscriptions.Values)
        {
            if (!subscription.IsEnabled())
            {
                continue;
            }

            var binding = new CaptureBinding(lease, subscription);
            if (!subscription.TryAttach(lease, binding))
            {
                binding.Dispose();
            }
        }
    }
    private void AttachOutputTaps(ProcessLease lease)
    {
        foreach (var subscription in outputTapSubscriptions.Values)
        {
            if (subscription.IsEnabled)
            {
                subscription.TryAttach(lease);
            }
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
                lease.FailOutputs();
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
                lease.FailOutputs();
                throw;
            }

            return new(ProcessOperationStatus.Cancelled, ServiceStateReasonCode.Cancelled);
        }
        catch
        {
            lease.FailOutputs();
            throw;
        }

        return new(ProcessOperationStatus.Completed, ServiceStateReasonCode.StopCompleted);
    }

    private async Task MonitorAsync(ProcessLease lease)
    {
        var exited = false;
        var successfulExit = false;
        int? exitCode = null;
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
                await Task.WhenAll(lease.GetDrainTasks()).ConfigureAwait(false);
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
                    exitCode = lease.Process.ExitCode;
                    successfulExit = exitCode == 0;
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
            lease.FailOutputs();
            SupervisionLogMessages.ProcessMonitorFailed(
                _logger,
                exception,
                "Monitor",
                lease.ServiceId,
                lease.InstanceId.ToString());
        }
        finally
        {
            lease.DisposeCaptureBindings();
            foreach (var subscription in captureSubscriptions.Values)
            {
                subscription.RemoveBinding(lease.InstanceId);
            }

            var exitedAt = DateTimeOffset.UtcNow;
            lease.NotifyOutputTapsExited(exitCode, exitedAt);
            lease.DisposeOutputs();
            leases.TryRemove(lease.InstanceId, out _);
            lease.Process.Dispose();
            lease.Exited.TrySetResult(true);
            if (exited)
            {
                PublishExit(lease, successfulExit, exitCode, exitedAt);
            }
        }
    }

    private void PublishExit(ProcessLease lease, bool successfulExit, int? exitCode, DateTimeOffset exitedAt)
    {
        var callbacks = observers.Values.ToArray();
        if (callbacks.Length == 0)
        {
            return;
        }

        var observation = new ProcessExitObservation(lease.ServiceId, lease.InstanceId, successfulExit, exitedAt, exitCode);
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

    private void RemoveCaptureSubscription(CaptureSubscription subscription)
    {
        captureSubscriptions.TryRemove(subscription.Id, out _);
        foreach (var lease in leases.Values)
        {
            lease.RemoveCaptureBinding(subscription.Id);
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
        // Distro-packaged runtimes report custom RIDs (arch-x64, fedora-x64, linux-musl-x64)
        // that run the portable helper identically; OS + architecture are the real constraints.
        (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) &&
        (RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.X64);

    private sealed record HelperLaunchRequest(string FileName, string WorkingDirectory, string[] Arguments);

    private sealed class OutputTapSubscription : IDisposable
    {
        private readonly object gate = new();
        private readonly PosixProcessExecutor owner;
        private readonly IProcessOutputTap tap;
        private readonly ConcurrentDictionary<ProcessInstanceId, OutputTapBinding> bindings = new();
        private int disposed;

        internal OutputTapSubscription(PosixProcessExecutor owner, long id, IProcessOutputTap tap)
        {
            this.owner = owner;
            Id = id;
            this.tap = tap;
        }

        internal long Id { get; }

        internal bool IsEnabled => Volatile.Read(ref disposed) == 0;

        internal void TryAttach(ProcessLease lease)
        {
            lock (gate)
            {
                if (disposed != 0)
                {
                    return;
                }

                var binding = new OutputTapBinding(lease, this);
                if (!bindings.TryAdd(lease.InstanceId, binding))
                {
                    return;
                }

                lease.AddOutputTapBinding(Id, binding);
                try
                {
                    binding.Start();
                }
                catch
                {
                    binding.Dispose();
                    throw;
                }
            }
        }

        internal void RemoveBinding(ProcessInstanceId instanceId) => bindings.TryRemove(instanceId, out _);

        internal void OnGenerationStarted(ProcessLease lease)
        {
            if (!IsEnabled)
            {
                return;
            }

            try
            {
                tap.OnGenerationStarted(lease.ServiceId, lease.InstanceId, lease.AttemptNumber, lease.StartedAt);
            }
            catch (Exception exception)
            {
                LogFailure(exception, nameof(IProcessOutputTap.OnGenerationStarted), lease.ServiceId);
            }
        }

        internal void OnOutputChunk(
            ProcessLease lease,
            ProcessOutputStream stream,
            ReadOnlyMemory<byte> chunk,
            DateTimeOffset capturedAt)
        {
            if (!IsEnabled)
            {
                return;
            }

            try
            {
                tap.OnOutputChunk(
                    lease.ServiceId,
                    lease.InstanceId,
                    lease.AttemptNumber,
                    stream,
                    chunk,
                    capturedAt);
            }
            catch (Exception exception)
            {
                LogFailure(exception, nameof(IProcessOutputTap.OnOutputChunk), lease.ServiceId);
            }
        }

        internal void OnGenerationExited(ProcessLease lease, int? exitCode, DateTimeOffset exitedAt)
        {
            if (!IsEnabled)
            {
                return;
            }

            try
            {
                tap.OnGenerationExited(
                    lease.ServiceId,
                    lease.InstanceId,
                    lease.AttemptNumber,
                    exitCode,
                    exitedAt);
            }
            catch (Exception exception)
            {
                LogFailure(exception, nameof(IProcessOutputTap.OnGenerationExited), lease.ServiceId);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                owner.outputTapSubscriptions.TryRemove(Id, out _);
                foreach (var binding in bindings.Values)
                {
                    binding.Dispose();
                }

                bindings.Clear();
            }
        }

        private void LogFailure(Exception exception, string operation, Guid serviceId) =>
            SupervisionLogMessages.ProcessOutputSubscriberFailed(
                owner._logger,
                exception,
                operation,
                "service-log",
                serviceId);
    }

    private sealed class OutputTapBinding : IDisposable
    {
        private readonly object gate = new();
        private readonly ProcessLease lease;
        private readonly OutputTapSubscription subscription;
        private IDisposable? stdoutSubscription;
        private IDisposable? stderrSubscription;
        private int disposed;

        internal OutputTapBinding(ProcessLease lease, OutputTapSubscription subscription)
        {
            this.lease = lease;
            this.subscription = subscription;
        }

        internal void Start()
        {
            lock (gate)
            {
                if (disposed != 0)
                {
                    return;
                }

                IDisposable? stdout = null;
                IDisposable? stderr = null;
                try
                {
                    subscription.OnGenerationStarted(lease);
                    stdout = lease.Stdout.SubscribeTap(
                        (chunk, capturedAt) => subscription.OnOutputChunk(
                            lease,
                            ProcessOutputStream.Stdout,
                            chunk,
                            capturedAt));
                    stderr = lease.Stderr.SubscribeTap(
                        (chunk, capturedAt) => subscription.OnOutputChunk(
                            lease,
                            ProcessOutputStream.Stderr,
                            chunk,
                            capturedAt));
                    stdoutSubscription = stdout;
                    stderrSubscription = stderr;
                }
                catch
                {
                    stdout?.Dispose();
                    stderr?.Dispose();
                    throw;
                }
            }
        }

        internal void NotifyExited(int? exitCode, DateTimeOffset exitedAt)
        {
            lock (gate)
            {
                if (disposed != 0)
                {
                    return;
                }
            }

            subscription.OnGenerationExited(lease, exitCode, exitedAt);
        }

        public void Dispose()
        {
            IDisposable? stdout;
            IDisposable? stderr;
            lock (gate)
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                stdout = stdoutSubscription;
                stderr = stderrSubscription;
                stdoutSubscription = null;
                stderrSubscription = null;
            }

            stdout?.Dispose();
            stderr?.Dispose();
            subscription.RemoveBinding(lease.InstanceId);
            lease.RemoveOutputTapBinding(subscription.Id);
        }
    }

    private sealed class CaptureSubscription : IDisposable, IProcessOutputSink
    {
        private readonly PosixProcessExecutor owner;
        private readonly IProcessOutputSink sink;
        private readonly Func<bool>? enabledGate;
        private readonly ConcurrentDictionary<ProcessInstanceId, CaptureBinding> bindings = new();
        private int disposed;

        internal CaptureSubscription(
            PosixProcessExecutor owner,
            long id,
            IProcessOutputSink sink,
            Func<bool>? enabledGate)
        {
            this.owner = owner;
            Id = id;
            this.sink = sink;
            this.enabledGate = enabledGate;
        }

        internal long Id { get; }

        internal bool IsEnabled()
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return false;
            }

            if (enabledGate is null)
            {
                return true;
            }

            try
            {
                return enabledGate();
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal bool TryAttach(ProcessLease lease, CaptureBinding binding)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return false;
            }

            lease.AddCaptureBinding(Id, binding);
            if (!bindings.TryAdd(lease.InstanceId, binding))
            {
                lease.RemoveCaptureBinding(Id);
                return false;
            }

            if (Volatile.Read(ref disposed) != 0 && bindings.TryRemove(lease.InstanceId, out _))
            {
                lease.RemoveCaptureBinding(Id);
                return false;
            }

            return true;
        }

        internal void RemoveBinding(ProcessInstanceId instanceId) => bindings.TryRemove(instanceId, out _);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            owner.RemoveCaptureSubscription(this);
            foreach (var binding in bindings.Values)
            {
                binding.Dispose();
            }
        }

        public void OnLine(ProcessOutputRecord record)
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                sink.OnLine(record);
            }
        }

        public void OnDropped(Guid serviceId, ProcessOutputStream stream, long count)
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                sink.OnDropped(serviceId, stream, count);
            }
        }

        public void OnGap(Guid serviceId, ProcessOutputStream stream, long droppedBytes)
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                sink.OnGap(serviceId, stream, droppedBytes);
            }
        }
    }

    private sealed class CaptureBinding : IDisposable
    {
        private ProcessOutputCapture.CallbackConsumer? stdoutConsumer;
        private ProcessOutputCapture.CallbackConsumer? stderrConsumer;
        private IDisposable? stdoutSubscription;
        private IDisposable? stderrSubscription;
        private int disposed;

        internal CaptureBinding(ProcessLease lease, IProcessOutputSink sink)
        {
            var budget = new ProcessOutputBudget(MaximumOutputLinesPerSecond, MaximumOutputBytesPerSecond);
            var stdout = ProcessOutputCapture.CreateCallbackConsumer(
                lease.ServiceId,
                ProcessOutputStream.Stdout,
                budget,
                sink);
            var stderr = ProcessOutputCapture.CreateCallbackConsumer(
                lease.ServiceId,
                ProcessOutputStream.Stderr,
                budget,
                sink);
            IDisposable? stdoutHandle = null;
            IDisposable? stderrHandle = null;
            try
            {
                stdoutHandle = lease.Stdout.Subscribe(stdout);
                stderrHandle = lease.Stderr.Subscribe(stderr);
                stdoutConsumer = stdout;
                stderrConsumer = stderr;
                stdoutSubscription = stdoutHandle;
                stderrSubscription = stderrHandle;
                StdoutTask = stdout.Completion;
                StderrTask = stderr.Completion;
            }
            catch
            {
                stdoutHandle?.Dispose();
                stderrHandle?.Dispose();
                stdout.Dispose();
                stderr.Dispose();
                throw;
            }
        }

        internal Task StdoutTask { get; private set; } = Task.CompletedTask;
        internal Task StderrTask { get; private set; } = Task.CompletedTask;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                stdoutSubscription?.Dispose();
                stderrSubscription?.Dispose();
                stdoutConsumer?.Dispose();
                stderrConsumer?.Dispose();
            }
        }
    }

    private sealed class ProcessLease
    {
        private readonly ConcurrentDictionary<long, CaptureBinding> captureBindings = new();
        private readonly ConcurrentDictionary<long, OutputTapBinding> outputTapBindings = new();

        internal ProcessLease(
            ProcessInstanceId instanceId,
            Guid serviceId,
            Process process,
            DateTimeOffset startedAt,
            int attemptNumber,
            ILogger logger)
        {
            InstanceId = instanceId;
            ServiceId = serviceId;
            Process = process;
            ProcessId = process.Id;
            StartedAt = startedAt.ToUniversalTime();
            AttemptNumber = attemptNumber;
            Stdout = new ProcessOutputFanout(process.StandardOutput.BaseStream, serviceId, ProcessOutputStream.Stdout, logger);
            Stderr = new ProcessOutputFanout(process.StandardError.BaseStream, serviceId, ProcessOutputStream.Stderr, logger);
        }

        internal ProcessInstanceId InstanceId { get; }
        internal Guid ServiceId { get; }
        internal Process Process { get; }
        internal int ProcessId { get; }
        internal DateTimeOffset StartedAt { get; }
        internal int AttemptNumber { get; }
        internal ProcessOutputFanout Stdout { get; }
        internal ProcessOutputFanout Stderr { get; }
        internal Task? Monitor { get; set; }
        internal int StopRequested;
        internal TaskCompletionSource<bool> Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void AddCaptureBinding(long id, CaptureBinding binding) => captureBindings[id] = binding;
        internal void AddOutputTapBinding(long id, OutputTapBinding binding) => outputTapBindings[id] = binding;

        internal void RemoveOutputTapBinding(long id) => outputTapBindings.TryRemove(id, out _);

        internal void NotifyOutputTapsExited(int? exitCode, DateTimeOffset exitedAt)
        {
            foreach (var binding in outputTapBindings.Values)
            {
                binding.NotifyExited(exitCode, exitedAt);
            }
        }

        internal void RemoveCaptureBinding(long id)
        {
            if (captureBindings.TryRemove(id, out var binding))
            {
                binding.Dispose();
            }
        }

        internal Task[] GetDrainTasks()
        {
            var bindings = captureBindings.Values.ToArray();
            var tasks = new Task[2 + (bindings.Length * 2)];
            tasks[0] = Stdout.Completion;
            tasks[1] = Stderr.Completion;
            var index = 2;
            foreach (var binding in bindings)
            {
                tasks[index++] = binding.StdoutTask;
                tasks[index++] = binding.StderrTask;
            }

            return tasks;
        }

        internal void StartOutputPumps()
        {
            Stdout.Start();
            Stderr.Start();
        }

        internal void FailOutputs()
        {
            Stdout.Fail();
            Stderr.Fail();
        }

        internal void DisposeCaptureBindings()
        {
            foreach (var binding in captureBindings.Values)
            {
                binding.Dispose();
            }

            captureBindings.Clear();
        }

        internal void DisposeOutputs()
        {
            foreach (var binding in outputTapBindings.Values)
            {
                binding.Dispose();
            }

            outputTapBindings.Clear();
            Stdout.Dispose();
            Stderr.Dispose();
        }
    }
}

