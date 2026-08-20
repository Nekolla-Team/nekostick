using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Host;

/// <summary>Creates identity-bound extension capability facades from host composition.</summary>
public sealed class ExtensionCapabilityFactory : IExtensionCapabilityFactory
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HostRuntimeState _runtimeState;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>Creates the host capability factory.</summary>
    public ExtensionCapabilityFactory(
        IServiceScopeFactory scopeFactory,
        HostRuntimeState runtimeState,
        IServiceProvider serviceProvider)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <inheritdoc />
    public ExtensionCapabilitySet Create(string extensionId, Func<string, bool> handlerIsOwned)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            throw new ArgumentException("An extension identifier is required.", nameof(extensionId));
        }

        var configuration = new ExtensionConfigurationFacade(
            extensionId,
            _scopeFactory,
            _runtimeState,
            handlerIsOwned);
        return new ExtensionCapabilitySet(
            configuration,
            new ExtensionRouteFacade(configuration),
            new ExtensionServiceFacade(
                configuration,
                _scopeFactory,
                _runtimeState,
                _serviceProvider.GetService<IHostServiceLifecycleCoordinator>()),
            new ExtensionEndpointFacade(
                extensionId,
                _serviceProvider.GetService<IHostServiceEndpointSnapshotAccessor>()),
            new ExtensionFullConfigurationFacade(_scopeFactory));

    }
}

internal sealed class ExtensionFullConfigurationFacade : IExtensionFullConfigurationApi
{
    private readonly IServiceScopeFactory _scopeFactory;

    internal ExtensionFullConfigurationFacade(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var hostConfig = scope.ServiceProvider.GetService<IHostConfigApi>();
        return hostConfig is null
            ? ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                new ConfigurationError(ConfigurationErrorCode.Unsupported))
            : await hostConfig.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ConfigurationWriteResult> ReplaceAsync(
        long expectedVersion,
        ConfigurationChangeSet changes,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var hostConfig = scope.ServiceProvider.GetService<IHostConfigApi>();
        return hostConfig is null
            ? ConfigurationWriteResult.Failure(
                new ConfigurationError(ConfigurationErrorCode.Unsupported))
            : await hostConfig.WriteSnapshotAsync(
                expectedVersion,
                changes,
                cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class ExtensionEndpointFacade : IExtensionEndpointApi
{
    private readonly string _extensionId;
    private readonly IHostServiceEndpointSnapshotAccessor? _accessor;

    internal ExtensionEndpointFacade(
        string extensionId,
        IHostServiceEndpointSnapshotAccessor? accessor)
    {
        _extensionId = extensionId ?? throw new ArgumentNullException(nameof(extensionId));
        _accessor = accessor;
    }

    public ImmutableArray<ExtensionEndpointLease> Current
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return (_accessor?.Current ?? ImmutableDictionary<Guid, HostServiceEndpointLease>.Empty)
                .Values
                .Where(value =>
                    value.IsActive(now) &&
                    string.Equals(value.OwnerExtensionId, _extensionId, StringComparison.Ordinal))
                .Select(value => new ExtensionEndpointLease(value.ServiceId, value.Port, value.ExpiresAt))
                .ToImmutableArray();
        }
    }

    public ValueTask<ExtensionEndpointLease?> ResolveAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        if (_accessor is null ||
            !_accessor.Current.TryGetValue(serviceId, out var value) ||
            !value.IsActive(now) ||
            !string.Equals(value.OwnerExtensionId, _extensionId, StringComparison.Ordinal))
        {
            return ValueTask.FromResult<ExtensionEndpointLease?>(null);
        }

        return ValueTask.FromResult<ExtensionEndpointLease?>(
            new ExtensionEndpointLease(value.ServiceId, value.Port, value.ExpiresAt));
    }
}
internal sealed class ExtensionConfigurationFacade : IExtensionConfigurationApi
{
    private readonly string _extensionId;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HostRuntimeState _runtimeState;
    private readonly Func<string, bool> _handlerIsOwned;

    internal ExtensionConfigurationFacade(
        string extensionId,
        IServiceScopeFactory scopeFactory,
        HostRuntimeState runtimeState,
        Func<string, bool> handlerIsOwned)
    {
        _extensionId = extensionId;
        _scopeFactory = scopeFactory;
        _runtimeState = runtimeState;
        _handlerIsOwned = handlerIsOwned;
    }

    public HostApiVersion ApiVersion => HostApiVersion.Current;

    public ValueTask<ConfigurationReadResult<ExtensionConfigurationSnapshot>> ReadAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(static (store, id, _, ct) => store.ReadOwnedAsync(id, ct), cancellationToken);

    public ValueTask<ConfigurationWriteResult> ApplyAsync(
        long expectedVersion,
        ExtensionConfigurationChangeSet changes,
        CancellationToken cancellationToken = default)
    {
        if (!_runtimeState.ConfigurationWritesAllowed)
        {
            return ValueTask.FromResult(UnsupportedWrite());
        }

        return ExecuteAsync(
            (store, id, handler, ct) => store.ApplyOwnedAsync(id, expectedVersion, changes, handler, ct),
            cancellationToken,
            _handlerIsOwned);
    }

    public ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadSettingsAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(static (store, id, _, ct) => store.ReadOwnedSettingsAsync(id, ct), cancellationToken);

    public ValueTask<ConfigurationWriteResult> WriteSettingsAsync(
        long expectedVersion,
        ExtensionSettingsConfiguration settings,
        CancellationToken cancellationToken = default)
    {
        if (!_runtimeState.ConfigurationWritesAllowed)
        {
            return ValueTask.FromResult(UnsupportedWrite());
        }

        return ExecuteAsync(
            (store, id, _, ct) => store.WriteOwnedSettingsAsync(id, expectedVersion, settings, ct),
            cancellationToken);
    }

    internal async ValueTask<T> ExecuteAsync<T>(
        Func<IExtensionOwnedConfigurationApi, string, Func<string, bool>?, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken,
        Func<string, bool>? handlerIsOwned = null)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetService<IExtensionOwnedConfigurationApi>();
        if (store is null)
        {
            return typeof(T) == typeof(ConfigurationWriteResult)
                ? (T)(object)UnsupportedWrite()
                : throw new InvalidOperationException("The extension capability store is unavailable.");
        }

        return await operation(store, _extensionId, handlerIsOwned, cancellationToken).ConfigureAwait(false);
    }

    private static ConfigurationWriteResult UnsupportedWrite() =>
        ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.Unsupported));
}

internal sealed class ExtensionRouteFacade : IExtensionRouteApi
{
    private readonly ExtensionConfigurationFacade _configuration;

    internal ExtensionRouteFacade(ExtensionConfigurationFacade configuration) => _configuration = configuration;

    public async ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionRouteConfiguration>>> ReadOwnedAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _configuration.ReadAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.Value is { } value
            ? ConfigurationReadResult<ImmutableArray<ExtensionRouteConfiguration>>.Success(value.Routes)
            : ConfigurationReadResult<ImmutableArray<ExtensionRouteConfiguration>>.Failure(result.Errors.ToArray());
    }

    public ValueTask<ConfigurationWriteResult> UpsertAsync(
        long expectedVersion,
        ExtensionRouteConfiguration route,
        CancellationToken cancellationToken = default) =>
        _configuration.ApplyAsync(
            expectedVersion,
            new ExtensionConfigurationChangeSet(
                ImmutableArray.Create(route),
                ImmutableArray<Guid>.Empty,
                ImmutableArray<ExtensionServiceConfiguration>.Empty,
                ImmutableArray<Guid>.Empty,
                null),
            cancellationToken);

    public ValueTask<ConfigurationWriteResult> RemoveAsync(
        long expectedVersion,
        Guid routeId,
        CancellationToken cancellationToken = default) =>
        _configuration.ApplyAsync(
            expectedVersion,
            new ExtensionConfigurationChangeSet(
                ImmutableArray<ExtensionRouteConfiguration>.Empty,
                ImmutableArray.Create(routeId),
                ImmutableArray<ExtensionServiceConfiguration>.Empty,
                ImmutableArray<Guid>.Empty,
                null),
            cancellationToken);
}

internal sealed class ExtensionServiceFacade : IExtensionServiceApi
{
    private readonly ExtensionConfigurationFacade _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HostRuntimeState _runtimeState;
    private readonly IHostServiceLifecycleCoordinator? _lifecycle;

    internal ExtensionServiceFacade(
        ExtensionConfigurationFacade configuration,
        IServiceScopeFactory scopeFactory,
        HostRuntimeState runtimeState,
        IHostServiceLifecycleCoordinator? lifecycle)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _runtimeState = runtimeState;
        _lifecycle = lifecycle;
    }

    public async ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceConfiguration>>> ReadOwnedAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _configuration.ReadAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.Value is { } value
            ? ConfigurationReadResult<ImmutableArray<ExtensionServiceConfiguration>>.Success(value.Services)
            : ConfigurationReadResult<ImmutableArray<ExtensionServiceConfiguration>>.Failure(result.Errors.ToArray());
    }

    public ValueTask<ConfigurationWriteResult> UpsertAsync(
        long expectedVersion,
        ExtensionServiceConfiguration service,
        CancellationToken cancellationToken = default) =>
        _configuration.ApplyAsync(
            expectedVersion,
            new ExtensionConfigurationChangeSet(
                ImmutableArray<ExtensionRouteConfiguration>.Empty,
                ImmutableArray<Guid>.Empty,
                ImmutableArray.Create(service),
                ImmutableArray<Guid>.Empty,
                null),
            cancellationToken);

    public ValueTask<ConfigurationWriteResult> RemoveAsync(
        long expectedVersion,
        Guid serviceId,
        CancellationToken cancellationToken = default) =>
        _configuration.ApplyAsync(
            expectedVersion,
            new ExtensionConfigurationChangeSet(
                ImmutableArray<ExtensionRouteConfiguration>.Empty,
                ImmutableArray<Guid>.Empty,
                ImmutableArray<ExtensionServiceConfiguration>.Empty,
                ImmutableArray.Create(serviceId),
                null),
            cancellationToken);

    public ValueTask<ExtensionServiceOperationResult> StartAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default) => OperateAsync(serviceId, cancellationToken);

    public ValueTask<ExtensionServiceOperationResult> StopAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ExtensionServiceOperationResult(false, ExtensionServiceOperationCode.Unsupported, serviceId));

    public ValueTask<ExtensionServiceOperationResult> RestartAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ExtensionServiceOperationResult(false, ExtensionServiceOperationCode.Unsupported, serviceId));

    private async ValueTask<ExtensionServiceOperationResult> OperateAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        if (!_runtimeState.ConfigurationWritesAllowed || _lifecycle is null)
        {
            return new(false, ExtensionServiceOperationCode.Unsupported, serviceId);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var hostConfig = scope.ServiceProvider.GetService<IHostConfigApi>();
        if (hostConfig is null)
        {
            return new(false, ExtensionServiceOperationCode.Unsupported, serviceId);
        }

        var owned = await _configuration.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!owned.IsSuccess || owned.Value is not { } ownedValue || !ownedValue.Services.Any(value => value.Id == serviceId))
        {
            return new(false, ExtensionServiceOperationCode.NotFound, serviceId);
        }

        var snapshot = await hostConfig.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.IsSuccess || snapshot.Value is not { } full)
        {
            return new(false, ExtensionServiceOperationCode.Failed, serviceId);
        }

        HostServiceReadinessResult readiness;
        try
        {
            readiness = await _lifecycle.EnsureReadyAsync(full, serviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, ExtensionServiceOperationCode.Cancelled, serviceId);
        }
        catch
        {
            return new(false, ExtensionServiceOperationCode.Failed, serviceId);
        }

        return readiness.Status switch
        {
            HostServiceReadinessStatus.Ready => new(true, ExtensionServiceOperationCode.Accepted, serviceId),
            HostServiceReadinessStatus.Disabled => new(false, ExtensionServiceOperationCode.AlreadyStopped, serviceId),
            HostServiceReadinessStatus.Cancelled => new(false, ExtensionServiceOperationCode.Cancelled, serviceId),
            HostServiceReadinessStatus.DatabaseUnavailable => new(false, ExtensionServiceOperationCode.Unsupported, serviceId),
            _ => new(false, ExtensionServiceOperationCode.Failed, serviceId)
        };
    }
}

