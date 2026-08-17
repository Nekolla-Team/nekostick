using System.Collections.Immutable;
using System.Text.Json;
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
    internal HostRoutingSnapshot(HostConfigurationSnapshot configuration, RouteMatchSnapshot matcher)
        : this(configuration, matcher, BuildExecutableRoutesOrEmpty(configuration), null)
    {
    }

    internal HostRoutingSnapshot(
        HostConfigurationSnapshot configuration,
        RouteMatchSnapshot matcher,
        ImmutableDictionary<Guid, ExecutableRoute> executableRoutes,
        ExtensionDispatchGeneration? dispatchGeneration)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        ExecutableRoutes = executableRoutes ?? throw new ArgumentNullException(nameof(executableRoutes));
        DispatchGeneration = dispatchGeneration;
        Publication = new HostSnapshotPublicationState(dispatchGeneration);
    }

    /// <summary>Gets the configuration that produced <see cref="Matcher"/>.</summary>
    internal HostConfigurationSnapshot Configuration { get; }

    /// <summary>Gets the immutable route matcher compiled from <see cref="Configuration"/>.</summary>
    internal RouteMatchSnapshot Matcher { get; }

    /// <summary>Gets the immutable executable route metadata compiled from <see cref="Configuration"/>.</summary>
    internal ImmutableDictionary<Guid, ExecutableRoute> ExecutableRoutes { get; }

    /// <summary>Gets the opaque extension dispatch generation paired with this snapshot.</summary>
    internal ExtensionDispatchGeneration? DispatchGeneration { get; }

    internal HostSnapshotPublicationState Publication { get; }

    private static ImmutableDictionary<Guid, ExecutableRoute> BuildExecutableRoutesOrEmpty(
        HostConfigurationSnapshot configuration)
    {
        if (ExecutableRouteBuilder.TryBuild(configuration, out var routes))
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
    private Task? _retirementTask;
    private TaskCompletionSource<bool>? _retirementCompletion;
    private bool _accepting = true;
    private bool _retirementRequested;
    private bool _retireGeneration;
    private int _activeLeases;

    internal HostSnapshotPublicationState(ExtensionDispatchGeneration? generation) => _generation = generation;

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
                catch
                {
                    // Runtime retirement is bounded and remains responsible for eventual release.
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
    private HostRoutingSnapshot? _published;

    /// <inheritdoc />
    public HostConfigurationSnapshot? Current => Volatile.Read(ref _published)?.Configuration;

    internal HostRoutingSnapshot? RoutingSnapshot => Volatile.Read(ref _published);

    /// <summary>Gets the current snapshot using the host configuration terminology.</summary>
    public HostConfigurationSnapshot? Snapshot => Current;

    /// <inheritdoc />
    public bool HasSnapshot => Current is not null;

    /// <summary>Attempts to replace the current snapshot with a complete validated value.</summary>
    public bool TryReplace(HostConfigurationSnapshot snapshot) => TryReplace(snapshot, null);

    internal bool TryReplace(
        HostConfigurationSnapshot snapshot,
        ExtensionDispatchGeneration? dispatchGeneration)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!HostConfigurationSnapshotValidator.IsComplete(snapshot) ||
            !HostConfigurationSemanticValidator.TryValidateSnapshot(snapshot))
        {
            return false;
        }

        RouteSnapshotBuildResult routeBuild;
        try
        {
            routeBuild = RouteMatchSnapshotBuilder.Build(snapshot.Routes);
        }
        catch (Exception)
        {
            return false;
        }

        if (!routeBuild.IsSuccess || routeBuild.Snapshot is null ||
            !ExecutableRouteBuilder.TryBuild(snapshot, out var executableRoutes))
        {
            return false;
        }

        var publication = new HostRoutingSnapshot(
            snapshot,
            routeBuild.Snapshot,
            executableRoutes,
            dispatchGeneration);
        HostRoutingSnapshot? previous;
        lock (_replacementGate)
        {
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
            previous = Interlocked.Exchange(ref _published, null);
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
    internal static bool IsComplete(HostConfigurationSnapshot snapshot)
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
                    !IsValidJsonObject(route.MetadataJson) ||
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
                    case ExtensionHandlerRouteTargetConfiguration extension:
                        if (!extensionIds.Contains(extension.HandlerId))
                        {
                            return false;
                        }

                        break;
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
                    !IsValidJsonArray(service.ArgumentList) ||
                    !IsValidJsonObject(service.Environment) ||
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
                    !IsValidJson(settings.SettingsJson))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
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

    private static bool IsValidJson<T>(T value)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
            return document.RootElement.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsValidJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsValidJsonArray<T>(IEnumerable<T> values) => IsValidJson(values);

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

    private static bool IsValidJsonObject<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> values)
        where TKey : notnull => IsValidJson(values);

    private static bool IsValidJsonObject(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
