using System.Collections.Immutable;

namespace Nekolla.Nekostick.Contracts;

/// <summary>Describes one extension management record together with its current runtime state.</summary>
public sealed record ExtensionManagementEntry
{
    /// <summary>Creates an extension management record DTO.</summary>
    /// <param name="extensionId">The stable extension identifier.</param>
    /// <param name="installedVersion">The installed extension version.</param>
    /// <param name="loadState">The persisted public load state.</param>
    /// <param name="createdAt">The UTC creation timestamp.</param>
    /// <param name="updatedAt">The UTC update timestamp.</param>
    /// <param name="recordVersion">The optimistic-concurrency version.</param>
    /// <param name="isRunning">Whether the extension is currently running.</param>
    /// <param name="manifestVersion">The manifest version observed at the latest scan, or <see langword="null" /> when the manifest was absent.</param>
    public ExtensionManagementEntry(
        string extensionId,
        string installedVersion,
        ExtensionLoadState loadState,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long recordVersion,
        bool isRunning,
        string? manifestVersion)
    {
        ExtensionId = string.IsNullOrWhiteSpace(extensionId)
            ? throw new ArgumentException("An extension identifier is required.", nameof(extensionId))
            : extensionId;
        InstalledVersion = string.IsNullOrWhiteSpace(installedVersion)
            ? throw new ArgumentException("An installed extension version is required.", nameof(installedVersion))
            : installedVersion;
        LoadState = loadState;
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
        RecordVersion = recordVersion < 0
            ? throw new ArgumentOutOfRangeException(nameof(recordVersion))
            : recordVersion;
        IsRunning = isRunning;
        ManifestVersion = manifestVersion is null
            ? null
            : string.IsNullOrWhiteSpace(manifestVersion)
                ? throw new ArgumentException("A manifest version is required when supplied.", nameof(manifestVersion))
                : manifestVersion;
    }

    /// <summary>Gets the stable extension identifier.</summary>
    public string ExtensionId { get; }

    /// <summary>Gets the installed extension version.</summary>
    public string InstalledVersion { get; }

    /// <summary>Gets the persisted public load state.</summary>
    public ExtensionLoadState LoadState { get; }

    /// <summary>Gets the UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets the UTC update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>Gets the optimistic-concurrency record version.</summary>
    public long RecordVersion { get; }

    /// <summary>Gets whether the extension is currently running.</summary>
    public bool IsRunning { get; }

    /// <summary>Gets the manifest version observed at the latest scan, or <see langword="null" /> when absent.</summary>
    public string? ManifestVersion { get; }
}

/// <summary>Summarizes extension records discovered during a refresh.</summary>
public sealed record ExtensionRefreshSummary
{
    /// <summary>Creates an extension refresh summary.</summary>
    /// <param name="added">The extension identifiers added to persistence.</param>
    /// <param name="versionUpdated">The extension identifiers whose installed version changed.</param>
    /// <param name="missing">The extension identifiers retained in persistence despite missing files.</param>
    public ExtensionRefreshSummary(
        ImmutableArray<string> added,
        ImmutableArray<string> versionUpdated,
        ImmutableArray<string> missing)
    {
        Added = added.IsDefault ? ImmutableArray<string>.Empty : added;
        VersionUpdated = versionUpdated.IsDefault ? ImmutableArray<string>.Empty : versionUpdated;
        Missing = missing.IsDefault ? ImmutableArray<string>.Empty : missing;
    }

    /// <summary>Gets extension identifiers added to persistence.</summary>
    public ImmutableArray<string> Added { get; }

    /// <summary>Gets extension identifiers whose installed version changed.</summary>
    public ImmutableArray<string> VersionUpdated { get; }

    /// <summary>Gets extension identifiers retained in persistence despite missing files.</summary>
    public ImmutableArray<string> Missing { get; }
}

/// <summary>Provides extension installation record management and refresh operations.</summary>
public interface IExtensionManagementApi
{
    /// <summary>Gets the host API version that provides this management surface.</summary>
    HostApiVersion ApiVersion { get; }

    /// <summary>Lists all extension management records.</summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The immutable management records or a safe error.</returns>
    ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionManagementEntry>>> ListAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Enables an extension and persists its loaded state.</summary>
    /// <param name="extensionId">The stable extension identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed write result or a safe error.</returns>
    ValueTask<ConfigurationWriteResult> EnableAsync(
        string extensionId,
        CancellationToken cancellationToken = default);

    /// <summary>Disables an extension and persists its disabled state.</summary>
    /// <param name="extensionId">The stable extension identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed write result or a safe error.</returns>
    ValueTask<ConfigurationWriteResult> DisableAsync(
        string extensionId,
        CancellationToken cancellationToken = default);

    /// <summary>Reloads an extension through the host publication pipeline.</summary>
    /// <param name="extensionId">The stable extension identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed write result or a safe error.</returns>
    ValueTask<ConfigurationWriteResult> ReloadAsync(
        string extensionId,
        CancellationToken cancellationToken = default);

    /// <summary>Schedules an extension reload to run after the current callback returns, without blocking.</summary>
    /// <remarks>
    /// Unlike <see cref="ReloadAsync" />, this operation is fully synchronous and never awaits generation
    /// replacement, so it is callable from any extension callback context (lifecycle, route handler, event
    /// subscriber, scheduler task). Acceptance means the reload was scheduled, not completed; the target is
    /// revalidated against the latest durable snapshot when the deferred publication runs, and completion can be
    /// observed through <see cref="ListAsync" /> or the supervisor surface. Scheduling is best-effort: a later
    /// publication failure is not reported to the caller.
    /// </remarks>
    /// <param name="extensionId">The stable extension identifier.</param>
    /// <returns><see langword="true" /> when the reload was accepted for scheduling; otherwise <see langword="false" /> (invalid identifier, unsupported capability, or writes disallowed).</returns>
    bool ReloadSoon(string extensionId);

    /// <summary>Deletes an extension record and its owned configuration when the extension is absent from disk.</summary>
    /// <param name="extensionId">The stable extension identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The committed write result or a safe error.</returns>
    ValueTask<ConfigurationWriteResult> DeleteRecordAsync(
        string extensionId,
        CancellationToken cancellationToken = default);

    /// <summary>Refreshes extension records from the current extension directory scan.</summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The refresh summary or a safe error.</returns>
    ValueTask<ConfigurationReadResult<ExtensionRefreshSummary>> RequestRefreshAsync(
        CancellationToken cancellationToken = default);
}
