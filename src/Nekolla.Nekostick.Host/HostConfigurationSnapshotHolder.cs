using System.Collections.Immutable;
using System.Globalization;
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

/// <summary>Describes how the holder admitted a staged or replacement snapshot.</summary>
internal enum SnapshotAdmission
{
    /// <summary>The snapshot was staged or published.</summary>
    Accepted,
    /// <summary>A strictly newer snapshot version is already staged or published; the goal is achieved.</summary>
    Superseded,
    /// <summary>The snapshot was rejected (validation failure, disposed holder, missing dispatch generation, ...).</summary>
    Rejected
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
    public bool TryReplace(HostConfigurationSnapshot snapshot) =>
        TryReplace(snapshot, null, null) == SnapshotAdmission.Accepted;
    /// <summary>Stages a validated snapshot for capability admission before runtime publication.</summary>
    internal SnapshotAdmission TryStage(HostConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!HostConfigurationSnapshotValidator.IsComplete(snapshot, _logger) ||
            !HostConfigurationSemanticValidator.TryValidateSnapshot(snapshot, _logger))
        {
            return SnapshotAdmission.Rejected;
        }
        lock (_replacementGate)
        {
            if (_disposed)
            {
                return SnapshotAdmission.Rejected;
            }

            var published = Volatile.Read(ref _published);
            if (published is not null && snapshot.Version < published.Configuration.Version)
            {
                return SnapshotAdmission.Superseded;
            }

            var staged = Volatile.Read(ref _staged);
            if (staged is not null && snapshot.Version < staged.Version)
            {
                return SnapshotAdmission.Superseded;
            }

            Volatile.Write(ref _staged, snapshot);
            return SnapshotAdmission.Accepted;
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
    internal SnapshotAdmission TryReplace(
        HostConfigurationSnapshot snapshot,
        ExtensionDispatchGeneration? dispatchGeneration) =>
        TryReplace(snapshot, dispatchGeneration, null);

    internal SnapshotAdmission TryReplace(
        HostConfigurationSnapshot snapshot,
        ExtensionDispatchGeneration? dispatchGeneration,
        ImmutableDictionary<Guid, string?>? serviceOwners)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!HostConfigurationSnapshotValidator.IsComplete(snapshot, _logger) ||
            !HostConfigurationSemanticValidator.TryValidateSnapshot(snapshot, _logger))
        {
            return SnapshotAdmission.Rejected;
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
            return SnapshotAdmission.Rejected;
        }

        if (!routeBuild.IsSuccess || routeBuild.Snapshot is null ||
            !ExecutableRouteBuilder.TryBuild(snapshot, out var executableRoutes, _logger))
        {
            return SnapshotAdmission.Rejected;
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
                return SnapshotAdmission.Rejected;
            }

            previous = Volatile.Read(ref _published);
            if (previous is not null && snapshot.Version < previous.Configuration.Version)
            {
                return SnapshotAdmission.Superseded;
            }

            if (previous?.DispatchGeneration is not null && dispatchGeneration is null)
            {
                return SnapshotAdmission.Rejected;
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

        return SnapshotAdmission.Accepted;
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
    internal static bool IsComplete(HostConfigurationSnapshot snapshot, ILogger? logger = null) =>
        IsComplete(snapshot, out _, logger);

    internal static bool IsComplete(
        HostConfigurationSnapshot snapshot,
        out string? failureMessage,
        ILogger? logger = null)
    {
        failureMessage = null;

        try
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.Version < 0)
            {
                return Reject(logger, "SnapshotVersionNegative", out failureMessage, snapshot.Version.ToString(CultureInfo.InvariantCulture));
            }

            if (snapshot.GlobalSettings is null)
            {
                return Reject(logger, "GlobalSettingsMissing", out failureMessage);
            }

            if (snapshot.GlobalSettings.TrustedProxyCidrs.Any(value =>
                    value is null || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)))
            {
                return Reject(logger, "TrustedProxyCidrs", out failureMessage);
            }

            if (snapshot.GlobalSettings.ConfigurationPollInterval < TimeSpan.FromSeconds(1) ||
                snapshot.GlobalSettings.ConfigurationPollInterval.Ticks % TimeSpan.TicksPerSecond != 0)
            {
                return Reject(
                    logger,
                    "ConfigurationPollInterval",
                    out failureMessage,
                    snapshot.GlobalSettings.ConfigurationPollInterval.ToString());
            }

            if (!AreUniqueIds(snapshot.Services.Select(value => value?.Id), out var duplicateServiceId))
            {
                return Reject(logger, "DuplicateServiceId", out failureMessage, duplicateServiceId?.ToString());
            }

            if (!AreUniqueIds(snapshot.Routes.Select(value => value?.Id), out var duplicateRouteId))
            {
                return Reject(logger, "DuplicateRouteId", out failureMessage, duplicateRouteId?.ToString());
            }

            if (!AreUniqueStrings(
                    snapshot.ExtensionRecords.Select(value => value?.ExtensionId),
                    out var duplicateExtensionRecordId))
            {
                return Reject(logger, "DuplicateExtensionRecordId", out failureMessage, duplicateExtensionRecordId);
            }

            if (!AreUniqueStrings(
                    snapshot.ExtensionSettings.Select(value => value?.ExtensionId),
                    out var duplicateExtensionSettingsId))
            {
                return Reject(logger, "DuplicateExtensionSettingsId", out failureMessage, duplicateExtensionSettingsId);
            }

            var serviceIds = snapshot.Services.Select(value => value.Id).ToHashSet();
            var extensionIds = snapshot.ExtensionRecords
                .Select(value => value.ExtensionId)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var route in snapshot.Routes)
            {
                if (route is null)
                {
                    return Reject(logger, "RouteMissing", out failureMessage);
                }

                if (!IsValidJsonObject(route.MetadataJson, logger))
                {
                    return Reject(logger, "RouteMetadataInvalid", out failureMessage, route.Id.ToString());
                }

                if (!AreValidRewrites(route.RequestHeaderRewrites))
                {
                    return Reject(logger, "RouteRequestHeaderRewritesInvalid", out failureMessage, route.Id.ToString());
                }

                if (!AreValidRewrites(route.ResponseHeaderRewrites))
                {
                    return Reject(logger, "RouteResponseHeaderRewritesInvalid", out failureMessage, route.Id.ToString());
                }

                switch (route.Target)
                {
                    case MicroserviceRouteTargetConfiguration microservice:
                        if (!serviceIds.Contains(microservice.ServiceId))
                        {
                            return Reject(
                                logger,
                                "RouteTargetServiceMissing",
                                out failureMessage,
                                microservice.ServiceId.ToString());
                        }

                        break;
                    // Handler identifiers are runtime registration names, not extension identifiers;
                    // ownership is enforced when the route is written, and dispatch fails closed.
                    case ExtensionHandlerRouteTargetConfiguration:
                    case StaticFileRouteTargetConfiguration:
                        break;
                    case null:
                        return Reject(logger, "RouteTargetMissing", out failureMessage, route.Id.ToString());
                    default:
                        return Reject(logger, "RouteTargetUnknown", out failureMessage, route.Id.ToString());
                }
            }

            foreach (var service in snapshot.Services)
            {
                if (service is null)
                {
                    return Reject(logger, "ServiceMissing", out failureMessage);
                }

                if (!IsValidJsonArray(service.ArgumentList, logger))
                {
                    return Reject(logger, "ServiceArgumentListInvalid", out failureMessage, service.Id.ToString());
                }

                if (!IsValidJsonObject(service.Environment, logger))
                {
                    return Reject(logger, "ServiceEnvironmentInvalid", out failureMessage, service.Id.ToString());
                }

                if (service.ArgumentList.Any(value => value is null || value.Any(char.IsControl)))
                {
                    return Reject(logger, "ServiceArgumentInvalid", out failureMessage, service.Id.ToString());
                }

                if (service.Environment.Any(value =>
                        string.IsNullOrWhiteSpace(value.Key) ||
                        value.Key.Any(char.IsControl) ||
                        value.Value is null ||
                        value.Value.Any(char.IsControl)))
                {
                    return Reject(logger, "ServiceEnvironmentEntryInvalid", out failureMessage, service.Id.ToString());
                }
            }

            foreach (var settings in snapshot.ExtensionSettings)
            {
                if (settings is null)
                {
                    return Reject(logger, "ExtensionSettingsMissing", out failureMessage);
                }

                if (!extensionIds.Contains(settings.ExtensionId))
                {
                    return Reject(logger, "ExtensionSettingsUnknownExtension", out failureMessage, settings.ExtensionId);
                }

                if (!IsValidJson(settings.SettingsJson, logger))
                {
                    return Reject(logger, "ExtensionSettingsJsonInvalid", out failureMessage, settings.ExtensionId);
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
            failureMessage = $"The host configuration snapshot could not be validated because its data caused {exception.GetType().Name}.";
            return false;
        }
    }

    private static bool Reject(
        ILogger? logger,
        string check,
        out string? failureMessage,
        string? detail = null)
    {
        HostLogMessages.ConfigurationSnapshotIncomplete(
            logger ?? HostLoggerDefaults.Logger,
            check,
            detail);

        var actualValue = JsonSerializer.Serialize(detail);
        failureMessage = check switch
        {
            "SnapshotVersionNegative" => $"Snapshot Version value {actualValue} must be non-negative.",
            "GlobalSettingsMissing" => "The GlobalSettings field is required in a complete host configuration snapshot.",
            "TrustedProxyCidrs" => "GlobalSettings.TrustedProxyCidrs contains a null, blank, or control-character entry; each CIDR must be non-empty and control-free.",
            "ConfigurationPollInterval" => $"GlobalSettings.ConfigurationPollInterval is {actualValue}; it must be at least one second and a whole number of seconds.",
            "DuplicateServiceId" => $"The Services collection contains a missing or duplicate service ID ({actualValue}).",
            "DuplicateRouteId" => $"The Routes collection contains a missing or duplicate route ID ({actualValue}).",
            "DuplicateExtensionRecordId" => $"The ExtensionRecords collection contains a missing or duplicate extension ID ({actualValue}).",
            "DuplicateExtensionSettingsId" => $"The ExtensionSettings collection contains a missing or duplicate extension ID ({actualValue}).",
            "RouteMissing" => "The Routes collection contains a null route.",
            "RouteMetadataInvalid" => $"Route {actualValue} has invalid MetadataJson; the field must contain a JSON object.",
            "RouteRequestHeaderRewritesInvalid" => $"Route {actualValue} has invalid RequestHeaderRewrites values.",
            "RouteResponseHeaderRewritesInvalid" => $"Route {actualValue} has invalid ResponseHeaderRewrites values.",
            "RouteTargetServiceMissing" => $"Route target service ID {actualValue} was not found in the Services collection.",
            "RouteTargetMissing" => $"Route {actualValue} is missing its required target.",
            "RouteTargetUnknown" => $"Route {actualValue} has an unsupported target configuration type.",
            "ServiceMissing" => "The Services collection contains a null service.",
            "ServiceArgumentListInvalid" => $"Service {actualValue} has an ArgumentList field that is not a JSON array.",
            "ServiceEnvironmentInvalid" => $"Service {actualValue} has an Environment field that is not a JSON object.",
            "ServiceArgumentInvalid" => $"Service {actualValue} has an ArgumentList item that is null or contains a control character.",
            "ServiceEnvironmentEntryInvalid" => $"Service {actualValue} has an Environment key or value that is null, blank, or contains a control character.",
            "ExtensionSettingsMissing" => "The ExtensionSettings collection contains a null settings entry.",
            "ExtensionSettingsUnknownExtension" => $"Extension settings for ID {actualValue} reference no ExtensionRecords entry.",
            "ExtensionSettingsJsonInvalid" => $"Extension settings for ID {actualValue} contain invalid SettingsJson.",
            _ => $"The host configuration snapshot failed completeness validation rule '{check}' for value {actualValue}."
        };
        return false;
    }

    private static bool AreUniqueIds(IEnumerable<Guid?> values, out Guid? offendingId)
    {
        offendingId = null;
        var seen = new HashSet<Guid>();
        foreach (var value in values)
        {
            if (value is null)
            {
                return false;
            }

            if (!seen.Add(value.Value))
            {
                offendingId = value;
                return false;
            }
        }

        return true;
    }

    private static bool AreUniqueStrings(IEnumerable<string?> values, out string? offendingValue)
    {
        offendingValue = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is null || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || !seen.Add(value))
            {
                offendingValue = value;
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
