using System.Collections.Immutable;

namespace Nekolla.Nekostick.Contracts;

/// <summary>Identifies a target that an extension-owned route may dispatch to.</summary>
public enum ExtensionRouteTargetType
{
    /// <summary>A supervised service owned by the calling extension.</summary>
    Service,

    /// <summary>A handler registered by the calling extension.</summary>
    Handler
}

/// <summary>Provides the restricted target boundary for an extension-owned route.</summary>
public abstract record ExtensionRouteTargetConfiguration
{
    /// <summary>Initializes an extension route target.</summary>
    /// <param name="type">The restricted target type.</param>
    protected ExtensionRouteTargetConfiguration(ExtensionRouteTargetType type) => Type = type;

    /// <summary>Gets the restricted target type.</summary>
    public ExtensionRouteTargetType Type { get; }
}

/// <summary>References an extension-owned supervised service.</summary>
public sealed record ExtensionServiceRouteTarget : ExtensionRouteTargetConfiguration
{
    /// <summary>Creates a service route target reference.</summary>
    /// <param name="serviceId">The caller-owned service identifier.</param>
    public ExtensionServiceRouteTarget(Guid serviceId) : base(ExtensionRouteTargetType.Service)
    {
        ServiceId = IdentityValidation.RequireUuidV7(serviceId, nameof(serviceId));
    }

    /// <summary>Gets the referenced caller-owned service identifier.</summary>
    public Guid ServiceId { get; }
}

/// <summary>References a handler registered by the calling extension.</summary>
public sealed record ExtensionHandlerRouteTarget : ExtensionRouteTargetConfiguration
{
    /// <summary>Creates a handler route target reference.</summary>
    /// <param name="handlerId">The caller-owned stable handler identifier.</param>
    public ExtensionHandlerRouteTarget(string handlerId) : base(ExtensionRouteTargetType.Handler)
    {
        HandlerId = string.IsNullOrWhiteSpace(handlerId)
            ? throw new ArgumentException("A handler identifier is required.", nameof(handlerId))
            : handlerId;
    }

    /// <summary>Gets the referenced caller-owned handler identifier.</summary>
    public string HandlerId { get; }
}

/// <summary>Defines one immutable route configuration visible to an extension.</summary>
public sealed record ExtensionRouteConfiguration
{
    /// <summary>Creates an extension-owned route configuration DTO.</summary>
    /// <param name="id">The route identifier.</param>
    /// <param name="enabled">Whether the route participates in matching.</param>
    /// <param name="matcher">The route matcher and optional host/method constraints.</param>
    /// <param name="target">The restricted caller-owned target.</param>
    /// <param name="priority">The numeric route priority.</param>
    public ExtensionRouteConfiguration(
        Guid id,
        bool enabled,
        RouteMatcherConfiguration matcher,
        ExtensionRouteTargetConfiguration target,
        int priority)
    {
        Id = IdentityValidation.RequireUuidV7(id, nameof(id));
        Enabled = enabled;
        Matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Priority = priority;
    }

    /// <summary>Gets the route identifier.</summary>
    public Guid Id { get; }

    /// <summary>Gets whether the route is enabled.</summary>
    public bool Enabled { get; }

    /// <summary>Gets the route matcher.</summary>
    public RouteMatcherConfiguration Matcher { get; }

    /// <summary>Gets the restricted caller-owned route target.</summary>
    public ExtensionRouteTargetConfiguration Target { get; }

    /// <summary>Gets the numeric route priority.</summary>
    public int Priority { get; }
}

/// <summary>Defines the safe process-start subset visible to an extension.</summary>
/// <remarks>
/// Environment overrides and supervisor/runtime handles are intentionally not part of this DTO.
/// The host binds ownership and applies its process policy when it maps this value.
/// </remarks>
public sealed record ExtensionServiceConfiguration
{
    /// <summary>Creates an extension-owned service configuration DTO.</summary>
    /// <param name="id">The service identifier.</param>
    /// <param name="enabled">Whether the service may run.</param>
    /// <param name="fileName">The absolute executable path.</param>
    /// <param name="argumentList">The immutable argument list.</param>
    /// <param name="workingDirectory">The absolute working directory.</param>
    /// <param name="startMode">The service start mode.</param>
    /// <param name="restartPolicy">The restart policy.</param>
    /// <param name="healthCheck">The health-check settings.</param>
    /// <param name="createdAt">The UTC creation timestamp.</param>
    /// <param name="updatedAt">The UTC update timestamp.</param>
    /// <param name="version">The optimistic-concurrency version.</param>
    public ExtensionServiceConfiguration(
        Guid id,
        bool enabled,
        string fileName,
        ImmutableArray<string> argumentList,
        string workingDirectory,
        ServiceStartMode startMode,
        ServiceRestartPolicy restartPolicy,
        ServiceHealthCheckConfiguration healthCheck,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version)
    {
        Id = IdentityValidation.RequireUuidV7(id, nameof(id));
        Enabled = enabled;
        if (string.IsNullOrWhiteSpace(fileName) || !Path.IsPathRooted(fileName))
        {
            throw new ArgumentException("An absolute executable path is required.", nameof(fileName));
        }

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathRooted(workingDirectory))
        {
            throw new ArgumentException("An absolute working directory is required.", nameof(workingDirectory));
        }

        FileName = fileName;
        ArgumentList = argumentList.IsDefault ? ImmutableArray<string>.Empty : argumentList;
        WorkingDirectory = workingDirectory;
        StartMode = startMode;
        RestartPolicy = restartPolicy;
        HealthCheck = healthCheck ?? throw new ArgumentNullException(nameof(healthCheck));
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
        Version = version < 0 ? throw new ArgumentOutOfRangeException(nameof(version)) : version;
    }

    /// <summary>Gets the service identifier.</summary>
    public Guid Id { get; }

    /// <summary>Gets whether the service is enabled.</summary>
    public bool Enabled { get; }

    /// <summary>Gets the absolute executable path.</summary>
    public string FileName { get; }

    /// <summary>Gets the immutable process arguments.</summary>
    public ImmutableArray<string> ArgumentList { get; }

    /// <summary>Gets the absolute working directory.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Gets the start mode.</summary>
    public ServiceStartMode StartMode { get; }

    /// <summary>Gets the restart policy.</summary>
    public ServiceRestartPolicy RestartPolicy { get; }

    /// <summary>Gets the health-check settings.</summary>
    public ServiceHealthCheckConfiguration HealthCheck { get; }

    /// <summary>Gets the UTC creation timestamp assigned by the host.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets the UTC update timestamp assigned by the host.</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>Gets the optimistic-concurrency version assigned by the host.</summary>
    public long Version { get; }
}

/// <summary>Contains a caller-owned immutable configuration snapshot.</summary>
public sealed record ExtensionConfigurationSnapshot
{
    /// <summary>Creates an extension-owned configuration snapshot.</summary>
    /// <param name="version">The global optimistic-concurrency version.</param>
    /// <param name="routes">The caller-owned routes.</param>
    /// <param name="services">The caller-owned services.</param>
    /// <param name="settings">The caller-owned settings, when present.</param>
    public ExtensionConfigurationSnapshot(
        long version,
        ImmutableArray<ExtensionRouteConfiguration> routes,
        ImmutableArray<ExtensionServiceConfiguration> services,
        ExtensionSettingsConfiguration? settings)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);

        Version = version;
        Routes = routes.IsDefault ? ImmutableArray<ExtensionRouteConfiguration>.Empty : routes;
        Services = services.IsDefault ? ImmutableArray<ExtensionServiceConfiguration>.Empty : services;
        Settings = settings;
    }

    /// <summary>Gets the global optimistic-concurrency version.</summary>
    public long Version { get; }

    /// <summary>Gets the immutable caller-owned routes.</summary>
    public ImmutableArray<ExtensionRouteConfiguration> Routes { get; }

    /// <summary>Gets the immutable caller-owned services.</summary>
    public ImmutableArray<ExtensionServiceConfiguration> Services { get; }

    /// <summary>Gets the optional caller-owned settings.</summary>
    public ExtensionSettingsConfiguration? Settings { get; }
}

/// <summary>Describes one atomic set of caller-owned configuration changes.</summary>
public sealed record ExtensionConfigurationChangeSet
{
    /// <summary>Creates an extension-owned configuration change set.</summary>
    /// <param name="upserts">The route replacements or additions.</param>
    /// <param name="removedRouteIds">The route identifiers to remove.</param>
    /// <param name="serviceUpserts">The service replacements or additions.</param>
    /// <param name="removedServiceIds">The service identifiers to remove.</param>
    /// <param name="settings">The optional settings replacement; <see langword="null" /> leaves settings unchanged.</param>
    public ExtensionConfigurationChangeSet(
        ImmutableArray<ExtensionRouteConfiguration> upserts,
        ImmutableArray<Guid> removedRouteIds,
        ImmutableArray<ExtensionServiceConfiguration> serviceUpserts,
        ImmutableArray<Guid> removedServiceIds,
        ExtensionSettingsConfiguration? settings)
    {
        Upserts = upserts.IsDefault ? ImmutableArray<ExtensionRouteConfiguration>.Empty : upserts;
        RemovedRouteIds = NormalizeIds(removedRouteIds, nameof(removedRouteIds));
        ServiceUpserts = serviceUpserts.IsDefault
            ? ImmutableArray<ExtensionServiceConfiguration>.Empty
            : serviceUpserts;
        RemovedServiceIds = NormalizeIds(removedServiceIds, nameof(removedServiceIds));
        Settings = settings;
    }

    /// <summary>Gets the route replacements or additions.</summary>
    public ImmutableArray<ExtensionRouteConfiguration> Upserts { get; }

    /// <summary>Gets the route identifiers to remove.</summary>
    public ImmutableArray<Guid> RemovedRouteIds { get; }

    /// <summary>Gets the service replacements or additions.</summary>
    public ImmutableArray<ExtensionServiceConfiguration> ServiceUpserts { get; }

    /// <summary>Gets the service identifiers to remove.</summary>
    public ImmutableArray<Guid> RemovedServiceIds { get; }

    /// <summary>Gets the optional settings replacement.</summary>
    public ExtensionSettingsConfiguration? Settings { get; }

    private static ImmutableArray<Guid> NormalizeIds(ImmutableArray<Guid> ids, string parameterName)
    {
        if (ids.IsDefault)
        {
            return ImmutableArray<Guid>.Empty;
        }

        foreach (var id in ids)
        {
            IdentityValidation.RequireUuidV7(id, parameterName);
        }

        return ids;
    }
}

/// <summary>Defines the extension-scoped configuration facade.</summary>
/// <remarks>
/// The host binds the caller identity to this facade; callers cannot select an owner.
/// Reads contain only caller-owned records. Applies are optimistic-versioned, atomic, validated,
/// and publish notifications only after commit. Domain failures are returned as safe results.
/// </remarks>
public interface IExtensionConfigurationApi
{
    /// <summary>Gets the semantic version of this configuration contract.</summary>
    HostApiVersion ApiVersion { get; }

    /// <summary>Reads an owned-only immutable snapshot.</summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The snapshot or safe errors.</returns>
    ValueTask<ConfigurationReadResult<ExtensionConfigurationSnapshot>> ReadAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Atomically applies owned route, service, and optional settings changes.</summary>
    /// <param name="expectedVersion">The caller's expected global version.</param>
    /// <param name="changes">The immutable owned change set.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed version or safe errors.</returns>
    ValueTask<ConfigurationWriteResult> ApplyAsync(
        long expectedVersion,
        ExtensionConfigurationChangeSet changes,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the caller's own persisted settings document.</summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The settings document or safe errors.</returns>
    ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadSettingsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Atomically writes the caller's own settings document.</summary>
    /// <param name="expectedVersion">The caller's expected settings version.</param>
    /// <param name="settings">The immutable settings DTO.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed version or safe errors.</returns>
    ValueTask<ConfigurationWriteResult> WriteSettingsAsync(
        long expectedVersion,
        ExtensionSettingsConfiguration settings,
        CancellationToken cancellationToken = default);
}
