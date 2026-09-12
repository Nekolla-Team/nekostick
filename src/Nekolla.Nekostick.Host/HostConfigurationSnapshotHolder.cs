using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Persistence;
using Nekolla.Nekostick.Routing;

namespace Nekolla.Nekostick.Host;

/// <summary>Provides lock-free access to the current immutable host configuration snapshot.</summary>
public interface IHostConfigurationSnapshotAccessor
{
    /// <summary>Gets the last complete validated snapshot, if one is available.</summary>
    HostConfigurationSnapshot? Current { get; }

    /// <summary>Gets whether a complete validated snapshot is available.</summary>
    bool HasSnapshot { get; }
}

/// <summary>Provides the atomically published configuration, matcher, and executable route set.</summary>
internal interface IHostRoutingSnapshotAccessor
{
    HostRoutingSnapshot? Current { get; }
}

/// <summary>Provides a short-lived lease over one immutable routing publication.</summary>
internal interface IHostRoutingSnapshotLeaseAccessor
{
    HostRoutingSnapshotLease? TryAcquireLease();
}

/// <summary>Pairs one immutable configuration snapshot with all compiled route indexes.</summary>
internal sealed class HostRoutingSnapshot
{
    internal HostRoutingSnapshot(
        HostConfigurationSnapshot configuration,
        RouteMatchSnapshot matcher,
        ILogger? logger = null)
        : this(
            configuration,
            matcher,
            BuildExecutableRoutesOrEmpty(configuration, logger),
            null,
            ImmutableDictionary<Guid, string?>.Empty,
            logger)
    {
    }

    internal HostRoutingSnapshot(
        HostConfigurationSnapshot configuration,
        RouteMatchSnapshot matcher,
        ImmutableDictionary<Guid, ExecutableRoute> executableRoutes,
        ExtensionDispatchGeneration? dispatchGeneration,
        ImmutableDictionary<Guid, string?> serviceOwners,
        ILogger? logger = null)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        ExecutableRoutes = executableRoutes ?? throw new ArgumentNullException(nameof(executableRoutes));
        ServiceOwners = serviceOwners ?? throw new ArgumentNullException(nameof(serviceOwners));
        DispatchGeneration = dispatchGeneration;
        Publication = new HostSnapshotPublicationState(dispatchGeneration, logger);
    }

    /// <summary>Gets the configuration that produced <see cref="Matcher"/>.</summary>
    internal HostConfigurationSnapshot Configuration { get; }

    /// <summary>Gets the immutable route matcher compiled from <see cref="Configuration"/>.</summary>
    internal RouteMatchSnapshot Matcher { get; }

    /// <summary>Gets the immutable executable route metadata compiled from <see cref="Configuration"/>.</summary>
    internal ImmutableDictionary<Guid, ExecutableRoute> ExecutableRoutes { get; }

    /// <summary>Gets the persisted service ownership paired with <see cref="Configuration"/>.</summary>
    internal ImmutableDictionary<Guid, string?> ServiceOwners { get; }

    /// <summary>Gets the opaque extension dispatch generation paired with this snapshot.</summary>
    internal ExtensionDispatchGeneration? DispatchGeneration { get; }

    internal HostSnapshotPublicationState Publication { get; }

    private static ImmutableDictionary<Guid, ExecutableRoute> BuildExecutableRoutesOrEmpty(
        HostConfigurationSnapshot configuration,
        ILogger? logger)
    {
        if (ExecutableRouteBuilder.TryBuild(configuration, out var routes, logger))
        {
            return routes;
        }

        return ImmutableDictionary<Guid, ExecutableRoute>.Empty;
    }
}

/// <summary>Owns request leases and deferred retirement for one published snapshot.</summary>
internal sealed class HostSnapshotPublicationState
{
    private readonly object _gate = new();
    private readonly ExtensionDispatchGeneration? _generation;
    private readonly ILogger _logger;
    private Task? _retirementTask;
    private TaskCompletionSource<bool>? _retirementCompletion;
    private bool _accepting = true;
    private bool _retirementRequested;
    private bool _retireGeneration;
    private int _activeLeases;

    internal HostSnapshotPublicationState(
        ExtensionDispatchGeneration? generation,
        ILogger? logger = null)
    {
        _generation = generation;
        _logger = logger ?? HostLoggerDefaults.Logger;
    }

    internal HostRoutingSnapshotLease? TryAcquire(HostRoutingSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_accepting)
            {
                return null;
            }

            ExtensionDispatchLease? dispatchLease = null;
            if (_generation is not null)
            {
                dispatchLease = _generation.TryAcquireLease();
                if (dispatchLease is null)
                {
                    return null;
                }
            }

            _activeLeases++;
            return new HostRoutingSnapshotLease(snapshot, this, dispatchLease);
        }
    }

    internal Task BeginRetirement(bool retireGeneration)
    {
        lock (_gate)
        {
            _accepting = false;
            _retirementRequested = true;
            _retireGeneration |= retireGeneration;
            if (_activeLeases == 0)
            {
                StartRetirementLocked();
            }
            else
            {
                _retirementCompletion ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return _retirementTask ?? _retirementCompletion!.Task;
        }
    }

    internal void Release(ExtensionDispatchLease? dispatchLease)
    {
        dispatchLease?.Dispose();
        lock (_gate)
        {
            if (_activeLeases > 0)
            {
                _activeLeases--;
            }

            if (_retirementRequested && _activeLeases == 0)
            {
                StartRetirementLocked();
            }
        }
    }

    private void StartRetirementLocked()
    {
        if (_retirementTask is not null)
        {
            return;
        }

        _retirementTask = RetireAsync(_retireGeneration);
    }

    private async Task RetireAsync(bool retireGeneration)
    {
        try
        {
            if (retireGeneration && _generation is not null)
            {
                try
                {
                    await _generation.RetireAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    HostLogMessages.SnapshotRetirementCleanupFailed(
                        _logger,
                        exception,
                        "ExtensionGenerationRetirement");
                }
            }
        }
        finally
        {
            _retirementCompletion?.TrySetResult(true);
        }
    }

    internal Task RetirementTask
    {
        get
        {
            lock (_gate)
            {
                return _retirementTask ?? _retirementCompletion?.Task ?? Task.CompletedTask;
            }
        }
    }
}

/// <summary>Captures one immutable Host snapshot and its matching extension lease.</summary>
internal sealed class HostRoutingSnapshotLease : IDisposable, IAsyncDisposable
{
    private HostSnapshotPublicationState? _publication;
    private ExtensionDispatchLease? _dispatchLease;

    internal HostRoutingSnapshotLease(
        HostRoutingSnapshot snapshot,
        HostSnapshotPublicationState publication,
        ExtensionDispatchLease? dispatchLease)
    {
        Snapshot = snapshot;
        _publication = publication;
        _dispatchLease = dispatchLease;
    }

    internal HostRoutingSnapshot Snapshot { get; }

    internal ExtensionDispatchLease? DispatchLease => _dispatchLease;

    internal static HostRoutingSnapshotLease? Capture(HostRoutingSnapshot? snapshot) =>
        snapshot?.Publication.TryAcquire(snapshot);

    public void Dispose()
    {
        var publication = Interlocked.Exchange(ref _publication, null);
        if (publication is null)
        {
            return;
        }

        var lease = Interlocked.Exchange(ref _dispatchLease, null);
        publication.Release(lease);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Adapts the holder's atomic publication to the Host-internal routing accessor.</summary>
internal sealed class HostRoutingSnapshotAccessor : IHostRoutingSnapshotAccessor, IHostRoutingSnapshotLeaseAccessor
{
    private readonly HostConfigurationSnapshotHolder _holder;

    internal HostRoutingSnapshotAccessor(HostConfigurationSnapshotHolder holder)
    {
        _holder = holder ?? throw new ArgumentNullException(nameof(holder));
    }

    public HostRoutingSnapshot? Current => _holder.RoutingSnapshot;

    public HostRoutingSnapshotLease? TryAcquireLease() => _holder.TryAcquireRoutingLease();
}

/// <summary>Holds complete immutable configuration and replaces it atomically after validation.</summary>
public sealed class HostConfigurationSnapshotHolder : IHostConfigurationSnapshotAccessor, IHostRoutingSnapshotLeaseAccessor, IAsyncDisposable
{
    private readonly object _replacementGate = new();
    private readonly ILogger _logger;

    /// <summary>Creates the holder with an optional diagnostic logger.</summary>
    /// <param name="logger">The optional host logger for snapshot validation and retirement diagnostics.</param>
    public HostConfigurationSnapshotHolder(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    private HostRoutingSnapshot? _published;
    private HostConfigurationSnapshot? _staged;
    private bool _disposed;
    /// <inheritdoc />
    public HostConfigurationSnapshot? Current => Volatile.Read(ref _published)?.Configuration;

    internal HostRoutingSnapshot? RoutingSnapshot => Volatile.Read(ref _published);

    /// <inheritdoc />
    public bool HasSnapshot => Current is not null;

    /// <summary>Gets the current snapshot using the host configuration terminology.</summary>
    public HostConfigurationSnapshot? Snapshot => Current;

    /// <summary>Gets whether a validated candidate snapshot is staged before publication.</summary>
    internal bool HasStagedSnapshot
    {
        get
        {
            lock (_replacementGate)
            {
                return _staged is not null;
            }
        }
    }

    /// <inheritdoc />
    public bool TryReplace(HostConfigurationSnapshot snapshot) => TryReplace(snapshot, null, null);
    /// <summary>Stages a validated snapshot for capability admission before runtime publication.</summary>
    internal bool TryStage(HostConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!HostConfigurationSnapshotValidator.IsComplete(snapshot, _logger) ||
            !HostConfigurationSemanticValidator.TryValidateSnapshot(snapshot, _logger))
        {
            return false;
        }
        lock (_replacementGate)
        {
            if (_disposed)
            {
                return false;
            }

            var published = Volatile.Read(ref _published);
            if (published is not null && snapshot.Version < published.Configuration.Version)
            {
                return false;
            }

            var staged = Volatile.Read(ref _staged);
            if (staged is not null && snapshot.Version < staged.Version)
            {
                return false;
            }

            Volatile.Write(ref _staged, snapshot);
            return true;
        }
    }

    /// <summary>Clears a staged snapshot when its publication attempt does not complete.</summary>
    internal void ClearStaged(HostConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_replacementGate)
        {
            if (ReferenceEquals(Volatile.Read(ref _staged), snapshot))
            {
                Volatile.Write(ref _staged, null);
            }
        }
    }
    internal bool TryReplace(
        HostConfigurationSnapshot snapshot,
        ExtensionDispatchGeneration? dispatchGeneration) =>
        TryReplace(snapshot, dispatchGeneration, null);

    internal bool TryReplace(
        HostConfigurationSnapshot snapshot,
        ExtensionDispatchGeneration? dispatchGeneration,
        ImmutableDictionary<Guid, string?>? serviceOwners)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!HostConfigurationSnapshotValidator.IsComplete(snapshot, _logger) ||
            !HostConfigurationSemanticValidator.TryValidateSnapshot(snapshot, _logger))
        {
            return false;
        }

        serviceOwners ??= snapshot.Services.ToImmutableDictionary(
            static value => value.Id,
            static _ => (string?)null);

        RouteSnapshotBuildResult routeBuild;
        try
        {
            routeBuild = RouteMatchSnapshotBuilder.Build(snapshot.Routes);
        }
        catch (Exception exception)
        {
            HostLogMessages.SnapshotValidationFailed(_logger, exception, "RouteSnapshotBuild");
            return false;
        }

        if (!routeBuild.IsSuccess || routeBuild.Snapshot is null ||
            !ExecutableRouteBuilder.TryBuild(snapshot, out var executableRoutes, _logger))
        {
            return false;
        }

        var publication = new HostRoutingSnapshot(
            snapshot,
            routeBuild.Snapshot,
            executableRoutes,
            dispatchGeneration,
            serviceOwners,
            _logger);
        HostRoutingSnapshot? previous;
        lock (_replacementGate)
        {
            if (_disposed)
            {
                return false;
            }

            previous = Volatile.Read(ref _published);
            if (previous is not null && snapshot.Version < previous.Configuration.Version)
            {
                return false;
            }

            if (previous?.DispatchGeneration is not null && dispatchGeneration is null)
            {
                return false;
            }

            previous?.Publication.BeginRetirement(
                retireGeneration: previous.DispatchGeneration is not null &&
                    !ReferenceEquals(previous.DispatchGeneration, dispatchGeneration));
            Interlocked.Exchange(ref _published, publication);
            if (ReferenceEquals(Volatile.Read(ref _staged), snapshot))
            {
                Volatile.Write(ref _staged, null);
            }
        }

        return true;
    }


    internal HostRoutingSnapshotLease? TryAcquireRoutingLease() =>
        HostRoutingSnapshotLease.Capture(Volatile.Read(ref _published));
    HostRoutingSnapshotLease? IHostRoutingSnapshotLeaseAccessor.TryAcquireLease() => TryAcquireRoutingLease();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        HostRoutingSnapshot? previous;
        Task retirement;
        lock (_replacementGate)
        {
            _disposed = true;
            previous = Interlocked.Exchange(ref _published, null);
            Volatile.Write(ref _staged, null);
            retirement = previous?.Publication.BeginRetirement(retireGeneration: previous.DispatchGeneration is not null)
                ?? Task.CompletedTask;
        }

        await retirement.ConfigureAwait(false);
        if (previous?.Publication.RetirementTask is { } finalRetirement)
        {
            await finalRetirement.ConfigureAwait(false);
        }
    }
}

/// <summary>Validates the complete DTO graph before it is published to the runtime.</summary>
internal static class HostConfigurationSnapshotValidator
{
    internal static bool IsComplete(HostConfigurationSnapshot snapshot, ILogger? logger = null)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.Version < 0 || snapshot.GlobalSettings is null)
            {
                return false;
            }

            if (snapshot.GlobalSettings.TrustedProxyCidrs.Any(value =>
                    value is null || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)))
            {
                return false;
            }

            if (snapshot.GlobalSettings.ConfigurationPollInterval < TimeSpan.FromSeconds(1) ||
                snapshot.GlobalSettings.ConfigurationPollInterval.Ticks % TimeSpan.TicksPerSecond != 0)
            {
                return false;
            }

            if (!AreUniqueIds(snapshot.Services.Select(value => value?.Id)) ||
                !AreUniqueIds(snapshot.Routes.Select(value => value?.Id)) ||
                !AreUniqueStrings(snapshot.ExtensionRecords.Select(value => value?.ExtensionId)) ||
                !AreUniqueStrings(snapshot.ExtensionSettings.Select(value => value?.ExtensionId)))
            {
                return false;
            }

            var serviceIds = snapshot.Services.Select(value => value.Id).ToHashSet();
            var extensionIds = snapshot.ExtensionRecords
                .Select(value => value.ExtensionId)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var route in snapshot.Routes)
            {
                if (route is null ||
                    !IsValidJsonObject(route.MetadataJson, logger) ||
                    !AreValidRewrites(route.RequestHeaderRewrites) ||
                    !AreValidRewrites(route.ResponseHeaderRewrites))
                {
                    return false;
                }

                switch (route.Target)
                {
                    case MicroserviceRouteTargetConfiguration microservice:
                        if (!serviceIds.Contains(microservice.ServiceId))
                        {
                            return false;
                        }

                        break;
                    // Handler identifiers are runtime registration names, not extension identifiers;
                    // ownership is enforced when the route is written, and dispatch fails closed.
                    case ExtensionHandlerRouteTargetConfiguration:
                    case StaticFileRouteTargetConfiguration:
                        break;
                    case null:
                        return false;
                    default:
                        return false;
                }
            }

            foreach (var service in snapshot.Services)
            {
                if (service is null ||
                    !IsValidJsonArray(service.ArgumentList, logger) ||
                    !IsValidJsonObject(service.Environment, logger) ||
                    service.ArgumentList.Any(value => value is null || value.Any(char.IsControl)) ||
                    service.Environment.Any(value =>
                        string.IsNullOrWhiteSpace(value.Key) ||
                        value.Key.Any(char.IsControl) ||
                        value.Value is null ||
                        value.Value.Any(char.IsControl)))
                {
                    return false;
                }
            }

            foreach (var settings in snapshot.ExtensionSettings)
            {
                if (settings is null ||
                    !extensionIds.Contains(settings.ExtensionId) ||
                    !IsValidJson(settings.SettingsJson, logger))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception)
        {
            HostLogMessages.SnapshotValidationFailed(
                logger ?? HostLoggerDefaults.Logger,
                exception,
                nameof(IsComplete));
            return false;
        }
    }

    private static bool AreUniqueIds(IEnumerable<Guid?> values)
    {
        var seen = new HashSet<Guid>();
        foreach (var value in values)
        {
            if (value is null)
            {
                return false;
            }

            if (!seen.Add(value.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreUniqueStrings(IEnumerable<string?> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is null || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || !seen.Add(value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidJson<T>(T value, ILogger? logger = null)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
            return document.RootElement.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
        }
        catch (Exception exception)
        {
            HostLogMessages.SnapshotValidationFailed(
                logger ?? HostLoggerDefaults.Logger,
                exception,
                "SnapshotJsonSerialization");
            return false;
        }
    }

    private static bool IsValidJson(string value, ILogger? logger = null)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
        }
        catch (Exception exception)
        {
            HostLogMessages.SnapshotValidationFailed(
                logger ?? HostLoggerDefaults.Logger,
                exception,
                "SnapshotJsonValidation");
            return false;
        }
    }

    private static bool IsValidJsonArray<T>(IEnumerable<T> values, ILogger? logger = null) => IsValidJson(values, logger);

    private static bool AreValidRewrites(IEnumerable<HeaderRewriteConfiguration> rewrites)
    {
        foreach (var rewrite in rewrites)
        {
            if (rewrite is null ||
                string.IsNullOrWhiteSpace(rewrite.Name) ||
                rewrite.Name.Any(char.IsControl) ||
                rewrite.Value?.Any(char.IsControl) == true ||
                rewrite.Operation is not (HeaderRewriteOperation.Remove or HeaderRewriteOperation.Set or HeaderRewriteOperation.Add))
            {
                return false;
            }

            if ((rewrite.Operation is HeaderRewriteOperation.Set or HeaderRewriteOperation.Add) &&
                rewrite.Value is null)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidJsonObject<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> values,
        ILogger? logger = null)
        where TKey : notnull => IsValidJson(values, logger);

    private static bool IsValidJsonObject(string value, ILogger? logger = null)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (Exception exception)
        {
            HostLogMessages.SnapshotValidationFailed(
                logger ?? HostLoggerDefaults.Logger,
                exception,
                "SnapshotJsonObjectValidation");
            return false;
        }
    }
}
