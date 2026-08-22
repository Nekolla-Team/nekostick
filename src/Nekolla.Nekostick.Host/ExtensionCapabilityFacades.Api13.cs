using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Proxy;

namespace Nekolla.Nekostick.Host;

internal sealed class ExtensionSupervisorFacade : IExtensionSupervisorApi
{
    private readonly IHostServiceRuntimeSnapshotAccessor? _runtime;
    private readonly IMicroserviceForwardingTelemetry? _forwarding;

    internal ExtensionSupervisorFacade(
        IHostServiceRuntimeSnapshotAccessor? runtime,
        IMicroserviceForwardingTelemetry? forwarding)
    {
        _runtime = runtime;
        _forwarding = forwarding;
    }

    public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_runtime is null)
        {
            return ValueTask.FromResult(
                ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Unsupported)));
        }

        try
        {
            var snapshots = _runtime.ReadCurrent();
            var result = snapshots
                .Select(ToContract)
                .ToImmutableArray();
            return ValueTask.FromResult(
                ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>.Success(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ValueTask.FromResult(
                ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.StorageUnavailable)));
        }
    }

    public ValueTask<ConfigurationReadResult<ExtensionServiceRuntimeSnapshot?>> GetAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_runtime is null)
        {
            return ValueTask.FromResult(
                ConfigurationReadResult<ExtensionServiceRuntimeSnapshot?>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.Unsupported)));
        }

        try
        {
            if (!_runtime.TryGet(serviceId, out var runtime))
            {
                return ValueTask.FromResult(
                    ConfigurationReadResult<ExtensionServiceRuntimeSnapshot?>.Success(null));
            }

            return ValueTask.FromResult(
                ConfigurationReadResult<ExtensionServiceRuntimeSnapshot?>.Success(ToContract(runtime)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ValueTask.FromResult(
                ConfigurationReadResult<ExtensionServiceRuntimeSnapshot?>.Failure(
                    new ConfigurationError(ConfigurationErrorCode.StorageUnavailable)));
        }
    }

    private ExtensionServiceRuntimeSnapshot ToContract(HostServiceRuntimeSnapshot value)
    {
        var forwarding = _forwarding?.Read(value.ServiceId) ?? default;
        return new ExtensionServiceRuntimeSnapshot(
            value.ServiceId,
            value.ProcessId,
            value.StartedAt,
            value.Uptime,
            value.LifecycleState,
            value.Health,
            forwarding.ForwardedRequestCount,
            forwarding.ActiveForwardedRequestCount,
            value.LastUpdatedAt,
            value.LastHealthAt);
    }
}

internal sealed class ExtensionLogWriter : IExtensionLogWriter
{
    private static readonly EventId EventId = HostEventIds.ExtensionText;
    private readonly string _extensionId;
    private readonly ILogger _logger;
    internal ExtensionLogWriter(string extensionId, ILogger logger)
    {
        _extensionId = string.IsNullOrWhiteSpace(extensionId)
            ? throw new ArgumentException("An extension identifier is required.", nameof(extensionId))
            : extensionId;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void WriteText(ExtensionLogLevel level, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > ExtensionLogLimits.MaximumTextLength || text.Any(char.IsControl))
        {
            return;
        }

        var logLevel = level == ExtensionLogLevel.Warning ? LogLevel.Warning : LogLevel.Information;
        try
        {
            if (!_logger.IsEnabled(logLevel))
            {
                return;
            }

            _logger.Log(
                logLevel,
                EventId,
                new ExtensionLogState(_extensionId, level, text),
                null,
                static (state, _) => $"Extension custom text submitted. ExtensionId: {state.ExtensionId}. Text: {state.Text}");
        }
        catch
        {
            // Logger provider failures must not escape extension callbacks.
        }
    }

    private sealed class ExtensionLogState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?>[] _values;

        internal ExtensionLogState(string extensionId, ExtensionLogLevel level, string text) =>
            _values =
            [
                new("ExtensionId", extensionId),
                new("Level", level),
                new("Text", text)
            ];

        internal string ExtensionId => (string)_values[0].Value!;
        internal string Text => (string)_values[2].Value!;

        public int Count => _values.Length;
        public KeyValuePair<string, object?> this[int index] => _values[index];
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)_values).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _values.GetEnumerator();
    }
}
