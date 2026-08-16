using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Nekolla.Nekostick.Contracts;

/// <summary>Identifies a safe, stable configuration operation failure.</summary>
public enum ConfigurationErrorCode
{
    /// <summary>The submitted configuration failed semantic validation.</summary>
    Validation,

    /// <summary>The caller supplied an obsolete optimistic-concurrency version.</summary>
    ConcurrencyConflict,

    /// <summary>The requested stable configuration item does not exist.</summary>
    NotFound,

    /// <summary>The requested operation is outside the supported contract.</summary>
    Unsupported,

    /// <summary>The backing configuration store was unavailable.</summary>
    StorageUnavailable
}

/// <summary>Contains a safe configuration error without exception or secret data.</summary>
public sealed record ConfigurationError
{
    /// <summary>Creates a configuration error.</summary>
    /// <param name="code">The stable error category.</param>
    public ConfigurationError(ConfigurationErrorCode code)
    {
        Code = code;
        Message = code switch
        {
            ConfigurationErrorCode.Validation => "Configuration validation failed.",
            ConfigurationErrorCode.ConcurrencyConflict => "Configuration version conflict.",
            ConfigurationErrorCode.NotFound => "Configuration item was not found.",
            ConfigurationErrorCode.Unsupported => "Configuration operation is unsupported.",
            ConfigurationErrorCode.StorageUnavailable => "Configuration storage is unavailable.",
            _ => "Configuration operation failed."
        };
    }

    /// <summary>Gets the stable error category.</summary>
    public ConfigurationErrorCode Code { get; }

    /// <summary>Gets the safe error message.</summary>
    public string Message { get; }
}

/// <summary>Represents either a configuration read value or safe errors.</summary>
/// <typeparam name="T">The immutable value type returned by the read.</typeparam>
public sealed class ConfigurationReadResult<T>
{
    private ConfigurationReadResult(T value)
    {
        Value = value;
        Errors = ImmutableArray<ConfigurationError>.Empty;
        IsSuccess = true;
    }

    private ConfigurationReadResult(ImmutableArray<ConfigurationError> errors)
    {
        Value = default;
        Errors = errors.IsDefaultOrEmpty
            ? throw new ArgumentException("At least one configuration error is required.", nameof(errors))
            : errors;
        IsSuccess = false;
    }

    /// <summary>Gets whether the read succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets the immutable value when the read succeeded.</summary>
    public T? Value { get; }

    /// <summary>Gets safe errors when the read failed.</summary>
    public ImmutableArray<ConfigurationError> Errors { get; }

    /// <summary>Creates a successful read result.</summary>
    /// <param name="value">The immutable value.</param>
    /// <returns>A successful result.</returns>
    [SuppressMessage(
        "Design",
        "CA1000:Do not declare static members on generic types",
        Justification = "The generic result factory keeps construction type-safe at the public contract boundary.")]
    public static ConfigurationReadResult<T> Success(T value) => new(value);

    /// <summary>Creates a failed read result.</summary>
    /// <param name="errors">The safe errors.</param>
    /// <returns>A failed result.</returns>
    [SuppressMessage(
        "Design",
        "CA1000:Do not declare static members on generic types",
        Justification = "The generic result factory keeps construction type-safe at the public contract boundary.")]
    public static ConfigurationReadResult<T> Failure(params ConfigurationError[] errors) =>
        new(errors.ToImmutableArray());
}

/// <summary>Represents the result of an atomic configuration write.</summary>
public sealed class ConfigurationWriteResult
{
    private ConfigurationWriteResult(long newVersion)
    {
        IsSuccess = true;
        NewVersion = newVersion;
        Errors = ImmutableArray<ConfigurationError>.Empty;
    }

    private ConfigurationWriteResult(ImmutableArray<ConfigurationError> errors)
    {
        IsSuccess = false;
        Errors = errors.IsDefaultOrEmpty
            ? throw new ArgumentException("At least one configuration error is required.", nameof(errors))
            : errors;
    }

    /// <summary>Gets whether the write was committed.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets the committed global version when successful.</summary>
    public long? NewVersion { get; }

    /// <summary>Gets safe errors when the write was rejected.</summary>
    public ImmutableArray<ConfigurationError> Errors { get; }

    /// <summary>Creates a successful write result.</summary>
    /// <param name="newVersion">The committed global version.</param>
    /// <returns>A successful result.</returns>
    public static ConfigurationWriteResult Success(long newVersion) => new(newVersion);

    /// <summary>Creates a failed write result.</summary>
    /// <param name="errors">The safe errors.</param>
    /// <returns>A failed result.</returns>
    public static ConfigurationWriteResult Failure(params ConfigurationError[] errors) =>
        new(errors.ToImmutableArray());
}

/// <summary>Describes one atomic set of business configuration changes.</summary>
public sealed record ConfigurationChangeSet
{
    /// <summary>Creates an immutable configuration change set.</summary>
    /// <param name="globalSettings">The replacement global settings.</param>
    /// <param name="routes">The complete replacement route set.</param>
    /// <param name="services">The complete replacement service set.</param>
    /// <param name="extensionRecords">The complete replacement extension records.</param>
    /// <param name="extensionSettings">The complete replacement extension settings.</param>
    public ConfigurationChangeSet(
        GlobalSettingsConfiguration globalSettings,
        ImmutableArray<RouteConfiguration> routes,
        ImmutableArray<ServiceConfiguration> services,
        ImmutableArray<ExtensionRecordConfiguration> extensionRecords,
        ImmutableArray<ExtensionSettingsConfiguration> extensionSettings)
    {
        GlobalSettings = globalSettings ?? throw new ArgumentNullException(nameof(globalSettings));
        Routes = routes.IsDefault ? ImmutableArray<RouteConfiguration>.Empty : routes;
        Services = services.IsDefault ? ImmutableArray<ServiceConfiguration>.Empty : services;
        ExtensionRecords = extensionRecords.IsDefault
            ? ImmutableArray<ExtensionRecordConfiguration>.Empty
            : extensionRecords;
        ExtensionSettings = extensionSettings.IsDefault
            ? ImmutableArray<ExtensionSettingsConfiguration>.Empty
            : extensionSettings;
    }

    /// <summary>Gets the replacement global settings.</summary>
    public GlobalSettingsConfiguration GlobalSettings { get; }

    /// <summary>Gets the immutable route set.</summary>
    public ImmutableArray<RouteConfiguration> Routes { get; }

    /// <summary>Gets the immutable service set.</summary>
    public ImmutableArray<ServiceConfiguration> Services { get; }

    /// <summary>Gets the immutable extension records.</summary>
    public ImmutableArray<ExtensionRecordConfiguration> ExtensionRecords { get; }

    /// <summary>Gets the immutable extension settings.</summary>
    public ImmutableArray<ExtensionSettingsConfiguration> ExtensionSettings { get; }
}

/// <summary>Represents a complete immutable configuration snapshot.</summary>
public sealed record HostConfigurationSnapshot
{
    /// <summary>Creates a complete configuration snapshot.</summary>
    /// <param name="version">The global optimistic-concurrency version.</param>
    /// <param name="globalSettings">The global settings.</param>
    /// <param name="routes">The immutable route set.</param>
    /// <param name="services">The immutable service set.</param>
    /// <param name="extensionRecords">The immutable extension records.</param>
    /// <param name="extensionSettings">The immutable extension settings.</param>
    public HostConfigurationSnapshot(
        long version,
        GlobalSettingsConfiguration globalSettings,
        ImmutableArray<RouteConfiguration> routes,
        ImmutableArray<ServiceConfiguration> services,
        ImmutableArray<ExtensionRecordConfiguration> extensionRecords,
        ImmutableArray<ExtensionSettingsConfiguration> extensionSettings)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);

        Version = version;
        GlobalSettings = globalSettings ?? throw new ArgumentNullException(nameof(globalSettings));
        Routes = routes.IsDefault ? ImmutableArray<RouteConfiguration>.Empty : routes;
        Services = services.IsDefault ? ImmutableArray<ServiceConfiguration>.Empty : services;
        ExtensionRecords = extensionRecords.IsDefault
            ? ImmutableArray<ExtensionRecordConfiguration>.Empty
            : extensionRecords;
        ExtensionSettings = extensionSettings.IsDefault
            ? ImmutableArray<ExtensionSettingsConfiguration>.Empty
            : extensionSettings;
    }

    /// <summary>Gets the global snapshot version.</summary>
    public long Version { get; }

    /// <summary>Gets the immutable global settings.</summary>
    public GlobalSettingsConfiguration GlobalSettings { get; }

    /// <summary>Gets the immutable routes.</summary>
    public ImmutableArray<RouteConfiguration> Routes { get; }

    /// <summary>Gets the immutable services.</summary>
    public ImmutableArray<ServiceConfiguration> Services { get; }

    /// <summary>Gets the immutable extension records.</summary>
    public ImmutableArray<ExtensionRecordConfiguration> ExtensionRecords { get; }

    /// <summary>Gets the immutable extension settings.</summary>
    public ImmutableArray<ExtensionSettingsConfiguration> ExtensionSettings { get; }
}
