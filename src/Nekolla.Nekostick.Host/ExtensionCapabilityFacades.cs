using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Proxy;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Supervision;
using Nekolla.Nekostick.Persistence;

namespace Nekolla.Nekostick.Host;

/// <summary>Creates identity-bound extension capability facades from host composition.</summary>
public sealed class ExtensionCapabilityFactory : IExtensionCapabilityFactory, IExtensionCapabilityFactoryRouteEvents
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
    public ExtensionCapabilitySet Create(string extensionId, Func<string, bool> handlerIsOwned) =>
        CreateCore(extensionId, handlerIsOwned, null);

    /// <inheritdoc />
    public ExtensionCapabilitySet CreateWithRouteEvents(
        string extensionId,
        Func<string, bool> handlerIsOwned,
        IExtensionRouteEvents routeEvents) =>
        CreateCore(extensionId, handlerIsOwned, routeEvents);

    private ExtensionCapabilitySet CreateCore(
        string extensionId,
        Func<string, bool> handlerIsOwned,
        IExtensionRouteEvents? routeEvents)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            throw new ArgumentException("An extension identifier is required.", nameof(extensionId));
        }

        var runtimeManager = _serviceProvider.GetService<ExtensionRuntimeManager>()
            ?? throw new InvalidOperationException("The extension runtime manager is unavailable.");
        var lifecycle = _serviceProvider.GetService<IHostServiceLifecycleCoordinator>();
        var configuration = new ExtensionConfigurationFacade(
            extensionId,
            _scopeFactory,
            _runtimeState,
            runtimeManager.ApiVersion,
            handlerIsOwned);
        var logger = _serviceProvider.GetService<ILoggerFactory>()?.CreateLogger(HostLoggerCategory.Extensions)
            ?? NullLogger.Instance;
        var processExecutor = _serviceProvider.GetService<IProcessExecutor>() as PosixProcessExecutor;
        var management = ExtensionAbi.IsCompatible(new HostApiVersion(1, 3, 2), runtimeManager.ApiVersion)
            ? new ExtensionManagementFacade(
                extensionId,
                _scopeFactory,
                _runtimeState,
                runtimeManager,
                _serviceProvider,
                logger: logger)
            : null;
        var runtimeAccessor = _serviceProvider.GetService<IHostServiceRuntimeSnapshotAccessor>();
        var runtimeStateSource = _serviceProvider.GetService<IHostServiceRuntimeStateSource>();
        var supervisor = new ExtensionSupervisorFacade(
            extensionId,
            _runtimeState,
            lifecycle,
            runtimeAccessor,
            _serviceProvider.GetService<IMicroserviceForwardingTelemetry>(),
            logger);
        var runtimeState = runtimeAccessor is not null && runtimeStateSource is not null
            ? new ExtensionServiceRuntimeStateFacade(
                extensionId,
                runtimeAccessor,
                runtimeStateSource,
                supervisor.ToContract,
                logger)
            : null;

        return new ExtensionCapabilitySet(
            configuration,
            new ExtensionRouteFacade(configuration),
            new ExtensionServiceFacade(
                configuration,
                _scopeFactory,
                _runtimeState,
                lifecycle,
                logger: logger),
            new ExtensionEndpointFacade(
                extensionId,
                _serviceProvider.GetService<IHostServiceEndpointSnapshotAccessor>(),
                logger: logger),
            new ExtensionFullConfigurationFacade(extensionId, _scopeFactory, _runtimeState),
            supervisor,
            routeEvents,
            new ExtensionLogWriter(extensionId, logger),
            management,
            BuildHostInfoSnapshot,
            new ExtensionServiceLogFacade(
                extensionId,
                _runtimeState,
                lifecycle is HostServiceLifecycleManager lifecycleManager
                    ? lifecycleManager.ServiceLogBufferRegistry
                    : _serviceProvider.GetService<HostServiceLogBufferRegistry>(),
                runtimeAccessor,
                new ExtensionServiceOutputFacade(extensionId, _runtimeState, processExecutor, logger),
                logger),
            runtimeState);
    }
    private ExtensionHostInfoSnapshot BuildHostInfoSnapshot()
    {
        var status = _runtimeState.Status;
        var snapshot = _runtimeState.CurrentSnapshot;
        var observation = _runtimeState.ReadLastSnapshotObservation();
        var runtimeOptions = _serviceProvider.GetService<HostRuntimeOptions>();
        var readiness = status.Readiness switch
        {
            HostReadinessState.Ready => ExtensionHostReadinessState.Ready,
            HostReadinessState.Degraded => ExtensionHostReadinessState.Degraded,
            HostReadinessState.Unready => ExtensionHostReadinessState.Unready,
            HostReadinessState.Publishing => ExtensionHostReadinessState.Publishing,
            _ => ExtensionHostReadinessState.Unknown
        };

        return new ExtensionHostInfoSnapshot(
            runtimeOptions?.NodeId,
            _runtimeState.NodeOptions.ReadOnly,
            _runtimeState.NodeOptions.SkipExtensions,
            _runtimeState.NodeOptions.DisableSupervisor,
            status.DatabaseAvailable,
            status.SnapshotAvailable,
            status.ConfigurationValid,
            snapshot?.Version,
            observation.State,
            observation.At,
            readiness);
    }
}


internal sealed class ExtensionFullConfigurationFacade : IExtensionFullConfigurationApi
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _extensionId;
    private readonly HostRuntimeState _runtimeState;

    internal ExtensionFullConfigurationFacade(
        string extensionId,
        IServiceScopeFactory scopeFactory,
        HostRuntimeState runtimeState)
    {
        _extensionId = extensionId;
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
    }

    public async ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var hostConfig = scope.ServiceProvider.GetService<IHostConfigApi>();
        return hostConfig is null
            ? ConfigurationReadResult<HostConfigurationSnapshot>.Failure(
                new ConfigurationError(
                    ConfigurationErrorCode.Unsupported,
                    "The host configuration API is unavailable; the full configuration snapshot cannot be read."))
            : await hostConfig.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ConfigurationWriteResult> ReplaceAsync(
        long expectedVersion,
        ConfigurationChangeSet changes,
        CancellationToken cancellationToken = default)
    {
        if (!_runtimeState.ConfigurationWritesAllowed)
        {
            return ConfigurationWriteResult.Failure(
                new ConfigurationError(
                    ConfigurationErrorCode.Unsupported,
                    "The host runtime disallows writes to the full configuration snapshot."));
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var hostConfig = scope.ServiceProvider.GetService<IHostConfigApi>();
        if (hostConfig is null)
        {
            return ConfigurationWriteResult.Failure(
                new ConfigurationError(
                    ConfigurationErrorCode.Unsupported,
                    "The host configuration API is unavailable; the full configuration snapshot cannot be replaced."));
        }

        using var writeContext = HostConfigurationWriteContext.EnterExtension(_extensionId);
        return await hostConfig.WriteSnapshotAsync(
            expectedVersion,
            changes,
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class ExtensionEndpointFacade : IExtensionEndpointApi
{
    private readonly string _extensionId;
    private readonly IHostServiceEndpointSnapshotAccessor? _accessor;
    private readonly ILogger _logger;

    internal ExtensionEndpointFacade(
        string extensionId,
        IHostServiceEndpointSnapshotAccessor? accessor,
        ILogger? logger = null)
    {
        _extensionId = extensionId ?? throw new ArgumentNullException(nameof(extensionId));
        _accessor = accessor;
        _logger = logger ?? NullLogger.Instance;
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
                .Select(value => new ExtensionEndpointLease(
                    value.ServiceId,
                    value.GenerationId,
                    value.Port,
                    value.ExpiresAt))
                .ToImmutableArray();
        }
    }

    public ValueTask<ExtensionEndpointResolutionResult> ResolveAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_accessor is null)
        {
            return ValueTask.FromResult(ExtensionEndpointResolutionResult.Unavailable);
        }

        if (!_accessor.Current.TryGetValue(serviceId, out var value))
        {
            HostLogMessages.ExtensionEndpointResolutionNotFound(
                _logger,
                _extensionId,
                serviceId,
                "NoPublishedLease",
                null);
            return ValueTask.FromResult(ExtensionEndpointResolutionResult.NotFound);
        }

        if (!string.Equals(value.OwnerExtensionId, _extensionId, StringComparison.Ordinal))
        {
            HostLogMessages.ExtensionEndpointResolutionNotFound(
                _logger,
                _extensionId,
                serviceId,
                "OwnerMismatch",
                value.OwnerExtensionId);
            return ValueTask.FromResult(ExtensionEndpointResolutionResult.NotFound);
        }
        var now = DateTimeOffset.UtcNow;
        if (value.ExpiresAt <= now)
        {
            HostLogMessages.ExtensionEndpointResolutionNotFound(
                _logger,
                _extensionId,
                serviceId,
                "ExpiredLease",
                value.OwnerExtensionId);
            return ValueTask.FromResult(ExtensionEndpointResolutionResult.NotFound);
        }

        if (!value.IsActive(now))
        {
            HostLogMessages.ExtensionEndpointResolutionNotFound(
                _logger,
                _extensionId,
                serviceId,
                "InactiveLease",
                value.OwnerExtensionId);
            return ValueTask.FromResult(ExtensionEndpointResolutionResult.NotFound);
        }

        return ValueTask.FromResult<ExtensionEndpointResolutionResult>(
            ExtensionEndpointResolutionResult.Success(
                new ExtensionEndpointLease(
                    value.ServiceId,
                    value.GenerationId,
                    value.Port,
                    value.ExpiresAt)));
    }
}
internal sealed class ExtensionConfigurationFacade : IExtensionConfigurationApi
{
    private readonly string _extensionId;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HostRuntimeState _runtimeState;
    private readonly HostApiVersion _hostApiVersion;
    private readonly Func<string, bool> _handlerIsOwned;

    internal ExtensionConfigurationFacade(
        string extensionId,
        IServiceScopeFactory scopeFactory,
        HostRuntimeState runtimeState,
        HostApiVersion hostApiVersion,
        Func<string, bool> handlerIsOwned)
    {
        _extensionId = extensionId;
        _scopeFactory = scopeFactory;
        _runtimeState = runtimeState;
        _hostApiVersion = hostApiVersion;
        _handlerIsOwned = handlerIsOwned;
    }

    public HostApiVersion ApiVersion => _hostApiVersion;

    public ValueTask<ConfigurationReadResult<ExtensionConfigurationSnapshot>> ReadAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(static (store, id, _, ct) => store.ReadOwnedAsync(id, ct), cancellationToken);

    public ValueTask<ConfigurationWriteResult> ApplyAsync(
        long expectedVersion,
        ExtensionConfigurationChangeSet changes,
        CancellationToken cancellationToken = default)
    {
        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
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
        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
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
                ? (T)(object)UnsupportedWrite("The extension-owned configuration store is unavailable; the write could not be completed.")
                : throw new InvalidOperationException("The extension capability store is unavailable.");
        }

        return await operation(store, _extensionId, handlerIsOwned, cancellationToken).ConfigureAwait(false);
    }

    private static ConfigurationWriteResult UnsupportedWrite(
        string message = "Extension configuration writes are disallowed by the host runtime.") =>
        ConfigurationWriteResult.Failure(
            new ConfigurationError(ConfigurationErrorCode.Unsupported, message));
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
    private readonly ILogger _logger;
    internal ExtensionServiceFacade(
        ExtensionConfigurationFacade configuration,
        IServiceScopeFactory scopeFactory,
        HostRuntimeState runtimeState,
        IHostServiceLifecycleCoordinator? lifecycle,
        ILogger? logger = null)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _runtimeState = runtimeState;
        _lifecycle = lifecycle;
        _logger = logger ?? NullLogger.Instance;
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
        ValueTask.FromResult(Failure(
            serviceId,
            ExtensionServiceOperationCode.Unsupported,
            $"The extension service API does not support stopping service '{serviceId}'."));

    public ValueTask<ExtensionServiceOperationResult> RestartAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Failure(
            serviceId,
            ExtensionServiceOperationCode.Unsupported,
            $"The extension service API does not support restarting service '{serviceId}'."));

    private async ValueTask<ExtensionServiceOperationResult> OperateAsync(
        Guid serviceId,
        CancellationToken cancellationToken)
    {
        if (!_runtimeState.ConfigurationWritesAllowed)
        {
            return Failure(
                serviceId,
                ExtensionServiceOperationCode.Unsupported,
                $"The host runtime disallows service lifecycle changes; service '{serviceId}' cannot be started.");
        }

        if (_lifecycle is null)
        {
            return Failure(
                serviceId,
                ExtensionServiceOperationCode.Unsupported,
                $"The host service lifecycle coordinator is unavailable; service '{serviceId}' cannot be started.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var hostConfig = scope.ServiceProvider.GetService<IHostConfigApi>();
        if (hostConfig is null)
        {
            return Failure(
                serviceId,
                ExtensionServiceOperationCode.Unsupported,
                $"The host configuration API is unavailable; service '{serviceId}' cannot be started.");
        }

        var owned = await _configuration.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!owned.IsSuccess)
        {
            var reason = owned.Errors.IsDefaultOrEmpty
                ? "the caller-owned service configuration could not be read"
                : owned.Errors[0].Message;
            return Failure(
                serviceId,
                ExtensionServiceOperationCode.Failed,
                $"Service '{serviceId}' ownership could not be verified because {reason}.");
        }

        if (owned.Value is not { } ownedValue || !ownedValue.Services.Any(value => value.Id == serviceId))
        {
            return Failure(
                serviceId,
                ExtensionServiceOperationCode.NotFound,
                $"Service '{serviceId}' was not found in the caller's owned service configuration.");
        }

        var snapshot = await hostConfig.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.IsSuccess)
        {
            var reason = snapshot.Errors.IsDefaultOrEmpty
                ? "the host configuration snapshot could not be read"
                : snapshot.Errors[0].Message;
            return Failure(
                serviceId,
                ExtensionServiceOperationCode.Failed,
                $"Service '{serviceId}' could not be started because {reason}.");
        }

        if (snapshot.Value is not { } full)
        {
            return Failure(
                serviceId,
                ExtensionServiceOperationCode.Failed,
                $"Service '{serviceId}' could not be started because the host configuration snapshot was unavailable.");
        }

        HostServiceReadinessResult readiness;
        try
        {
            readiness = await _lifecycle.EnsureReadyAsync(full, serviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                serviceId,
                ExtensionServiceOperationCode.Cancelled,
                $"Starting service '{serviceId}' was cancelled before the host could establish readiness.");
        }
        catch (Exception exception)
        {
            HostLogMessages.ExtensionServiceLifecycleFailed(_logger, exception, serviceId);
            return Failure(
                serviceId,
                ExtensionServiceOperationCode.Failed,
                $"The host lifecycle operation for service '{serviceId}' failed with {exception.GetType().Name}.");
        }

        return ToOperationResult(serviceId, readiness);
    }

    private static ExtensionServiceOperationResult ToOperationResult(
        Guid serviceId,
        HostServiceReadinessResult readiness)
    {
        if (readiness.Status == HostServiceReadinessStatus.Ready)
        {
            return new ExtensionServiceOperationResult(
                true,
                ExtensionServiceOperationCode.Accepted,
                serviceId);
        }

        if (readiness.Status == HostServiceReadinessStatus.Disabled)
        {
            return Failure(
                serviceId,
                ExtensionServiceOperationCode.AlreadyStopped,
                $"Service '{serviceId}' is disabled and cannot be started in its current configuration.");
        }

        if (readiness.Status == HostServiceReadinessStatus.Cancelled)
        {
            return Failure(
                serviceId,
                ExtensionServiceOperationCode.Cancelled,
                $"The host cancelled the start request for service '{serviceId}'.");
        }

        var runtime = readiness.Snapshot;
        if (runtime?.Reason == ServiceStateReasonCode.PortLeaseConflict)
        {
            return Failure(
                serviceId,
                ExtensionServiceOperationCode.Conflict,
                $"The supervisor rejected service '{serviceId}' because its requested port lease conflicted with another lease.");
        }

        if (runtime?.Reason == ServiceStateReasonCode.Superseded)
        {
            return Failure(
                serviceId,
                ExtensionServiceOperationCode.Conflict,
                $"The supervisor superseded the start request for service '{serviceId}' while using configuration version {readiness.ConfigurationVersion}.");
        }

        if (readiness.Status == HostServiceReadinessStatus.DatabaseUnavailable ||
            runtime?.Reason == ServiceStateReasonCode.DatabaseUnavailable)
        {
            return Failure(
                serviceId,
                ExtensionServiceOperationCode.Failed,
                $"Service '{serviceId}' could not be started because the database gate prevents new service work.");
        }

        var rejectionReason = runtime is null
            ? $"readiness status '{readiness.Status}' without a runtime snapshot"
            : $"supervisor reason '{runtime.Reason}' (lifecycle '{runtime.ObservedLifecycle}', health '{runtime.Health}')";
        return Failure(
            serviceId,
            ExtensionServiceOperationCode.Failed,
            $"The supervisor did not make service '{serviceId}' ready because of {rejectionReason}.");
    }

    private static ExtensionServiceOperationResult Failure(
        Guid serviceId,
        ExtensionServiceOperationCode code,
        string message) =>
        new(false, code, serviceId, new ExtensionErrorDetail(message));
}

