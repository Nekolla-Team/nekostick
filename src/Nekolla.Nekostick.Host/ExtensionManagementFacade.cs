using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Persistence;

namespace Nekolla.Nekostick.Host;

/// <summary>Provides the identity-bound Host 1.3 extension management capability.</summary>
internal sealed class ExtensionManagementFacade : IExtensionManagementApi
{
    private readonly string _callerExtensionId;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HostRuntimeState _runtimeState;
    private readonly ExtensionRuntimeManager _runtimeManager;
    private readonly IDbContextFactory<NekostickDbContext>? _dbContextFactory;
    private readonly HostConfigurationPublisher? _publisher;
    private readonly IHostConfigurationSnapshotReader? _snapshotReader;
    private readonly HostServiceLifecycleManager? _lifecycle;
    private readonly ILogger _logger;

    /// <summary>Creates an extension management facade for one extension caller.</summary>
    /// <param name="extensionId">The identity of the extension receiving this facade.</param>
    /// <param name="scopeFactory">The factory for short-lived persistence scopes.</param>
    /// <param name="runtimeState">The host capability state used to gate writes.</param>
    /// <param name="runtimeManager">The runtime manager used for running-state snapshots.</param>
    /// <param name="serviceProvider">The root provider containing host publication services.</param>
    /// <param name="logger">The optional host logger for management diagnostics.</param>
    internal ExtensionManagementFacade(
        string extensionId,
        IServiceScopeFactory scopeFactory,
        HostRuntimeState runtimeState,
        ExtensionRuntimeManager runtimeManager,
        IServiceProvider serviceProvider,
        ILogger? logger = null)
    {
        _callerExtensionId = string.IsNullOrWhiteSpace(extensionId)
            ? throw new ArgumentException("An extension identifier is required.", nameof(extensionId))
            : extensionId;
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        _runtimeManager = runtimeManager ?? throw new ArgumentNullException(nameof(runtimeManager));
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _dbContextFactory = serviceProvider.GetService<IDbContextFactory<NekostickDbContext>>();
        _publisher = serviceProvider.GetService<HostConfigurationPublisher>();
        _snapshotReader = serviceProvider.GetService<IHostConfigurationSnapshotReader>();
        _lifecycle = serviceProvider.GetService<HostServiceLifecycleManager>();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public HostApiVersion ApiVersion => _runtimeManager.ApiVersion;

    /// <inheritdoc />
    public async ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionManagementEntry>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var hostConfig = scope.ServiceProvider.GetService<IHostConfigApi>();
        if (hostConfig is null)
        {
            return ConfigurationReadResult<ImmutableArray<ExtensionManagementEntry>>.Failure(
                new ConfigurationError(ConfigurationErrorCode.Unsupported));
        }

        var snapshotResult = await hostConfig.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshotResult.IsSuccess || snapshotResult.Value is not { } snapshot)
        {
            return ConfigurationReadResult<ImmutableArray<ExtensionManagementEntry>>.Failure(
                snapshotResult.Errors.ToArray());
        }

        var scan = ScanExtensions(cancellationToken);
        if (!scan.Succeeded)
        {
            return ConfigurationReadResult<ImmutableArray<ExtensionManagementEntry>>.Failure(
                new ConfigurationError(scan.ErrorCode));
        }

        var runtimeStatuses = _runtimeManager.GetStatuses()
            .ToImmutableDictionary(static status => status.ExtensionId, StringComparer.Ordinal);
        var running = runtimeStatuses.Values
            .Where(static status => status.State == ExtensionLoadState.Loaded)
            .Select(static status => status.ExtensionId)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var entries = snapshot.ExtensionRecords
            .OrderBy(static record => record.ExtensionId, StringComparer.Ordinal)
            .Select(record =>
            {
                runtimeStatuses.TryGetValue(record.ExtensionId, out var runtimeStatus);
                return new ExtensionManagementEntry(
                    record.ExtensionId,
                    record.Version,
                    record.LoadState,
                    record.CreatedAt,
                    record.UpdatedAt,
                    record.RecordVersion,
                    running.Contains(record.ExtensionId),
                    scan.Manifests.TryGetValue(record.ExtensionId, out var manifest)
                        ? manifest.Version.ToString()
                        : null,
                    record.ContentHash,
                    runtimeStatus?.ReportedStatusKind,
                    runtimeStatus?.ReportedStatusCode);
            })
            .ToImmutableArray();
        return ConfigurationReadResult<ImmutableArray<ExtensionManagementEntry>>.Success(entries);
    }

    /// <inheritdoc />
    public async ValueTask<ConfigurationWriteResult> EnableAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
    {
        if (ExtensionCallbackGuard.IsLifecycleActive)
        {
            // Management writes must not run inside lifecycle callbacks: the publish trigger would deadlock on the publication gate.
            return Reject(nameof(EnableAsync), extensionId, ConfigurationErrorCode.Unsupported);
        }

        if (!CanManage(extensionId))
        {
            return Reject(nameof(EnableAsync), extensionId, ConfigurationErrorCode.Validation);
        }

        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
        {
            return Reject(nameof(EnableAsync), extensionId, ConfigurationErrorCode.Unsupported);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var api = scope.ServiceProvider.GetService<EfHostConfigApi>();
        if (api is null)
        {
            return Reject(nameof(EnableAsync), extensionId, ConfigurationErrorCode.Unsupported);
        }

        var snapshotResult = await api.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshotResult.IsSuccess || snapshotResult.Value is not { } snapshot)
        {
            return ToWriteFailure(snapshotResult.Errors);
        }

        var record = snapshot.ExtensionRecords.FirstOrDefault(value =>
            string.Equals(value.ExtensionId, extensionId, StringComparison.Ordinal));
        if (record is null)
        {
            return Reject(nameof(EnableAsync), extensionId, ConfigurationErrorCode.NotFound);
        }

        if (record.LoadState is not (ExtensionLoadState.Disabled or ExtensionLoadState.Stopped or ExtensionLoadState.Failed))
        {
            return Reject(nameof(EnableAsync), extensionId, ConfigurationErrorCode.Validation);
        }

        var scan = ScanExtensions(cancellationToken);
        if (!scan.Succeeded)
        {
            return FailureWrite(scan.ErrorCode);
        }

        if (!scan.Manifests.TryGetValue(extensionId, out var manifest) ||
            !string.Equals(record.Version, manifest.Version.ToString(), StringComparison.Ordinal))
        {
            return Reject(nameof(EnableAsync), extensionId, ConfigurationErrorCode.Validation);
        }

        foreach (var dependency in manifest.Dependencies)
        {
            // Enabling requires every declared dependency to be loaded and version-satisfied.
            var dependencyRecord = snapshot.ExtensionRecords.FirstOrDefault(value =>
                string.Equals(value.ExtensionId, dependency.Id, StringComparison.Ordinal));
            if (dependencyRecord is null ||
                dependencyRecord.LoadState != ExtensionLoadState.Loaded ||
                !scan.Manifests.TryGetValue(dependency.Id, out var dependencyManifest) ||
                !string.Equals(
                    dependencyRecord.Version,
                    dependencyManifest.Version.ToString(),
                    StringComparison.Ordinal) ||
                !dependency.VersionRange.IsSatisfiedBy(dependencyManifest.Version))
            {
                return Reject(nameof(EnableAsync), extensionId, ConfigurationErrorCode.Validation);
            }
        }

        var contentHash = scan.ContentHashes.TryGetValue(extensionId, out var observedHash)
            ? observedHash
            : null;
        ConfigurationWriteResult result;
        if (_dbContextFactory is null)
        {
            result = await api.SetExtensionLoadStateAsync(
                    extensionId,
                    record.RecordVersion,
                    ExtensionLoadState.Loaded,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            try
            {
                await using var db = await _dbContextFactory
                    .CreateDbContextAsync(cancellationToken)
                    .ConfigureAwait(false);
                var contentPersistence = new EfExtensionRecordContentPersistence(db, logger: _logger);
                result = await contentPersistence.SetLoadStateAndContentHashAsync(
                        extensionId,
                        record.RecordVersion,
                        ExtensionLoadState.Loaded,
                        contentHash,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                HostLogMessages.ExtensionManagementException(
                    _logger,
                    exception,
                    "Enable",
                    _callerExtensionId,
                    extensionId);
                result = FailureWrite(ConfigurationErrorCode.StorageUnavailable);
            }
        }
        if (result.IsSuccess)
        {
            await CompletePublishTriggerAsync(cancellationToken).ConfigureAwait(false);
            HostLogMessages.ExtensionEnabled(
                _logger,
                _callerExtensionId,
                extensionId,
                result.NewVersion);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<ConfigurationWriteResult> DisableAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
    {
        if (ExtensionCallbackGuard.IsLifecycleActive)
        {
            return Reject(nameof(DisableAsync), extensionId, ConfigurationErrorCode.Unsupported);
        }

        if (!CanManage(extensionId))
        {
            return Reject(nameof(DisableAsync), extensionId, ConfigurationErrorCode.Validation);
        }

        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
        {
            return Reject(nameof(DisableAsync), extensionId, ConfigurationErrorCode.Unsupported);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var api = scope.ServiceProvider.GetService<EfHostConfigApi>();
        if (api is null)
        {
            return Reject(nameof(DisableAsync), extensionId, ConfigurationErrorCode.Unsupported);
        }

        var snapshotResult = await api.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshotResult.IsSuccess || snapshotResult.Value is not { } snapshot)
        {
            return ToWriteFailure(snapshotResult.Errors);
        }

        var record = snapshot.ExtensionRecords.FirstOrDefault(value =>
            string.Equals(value.ExtensionId, extensionId, StringComparison.Ordinal));
        if (record is null)
        {
            return Reject(nameof(DisableAsync), extensionId, ConfigurationErrorCode.NotFound);
        }

        if (record.LoadState == ExtensionLoadState.Disabled)
        {
            HostLogMessages.ExtensionDisableNoOp(
                _logger,
                _callerExtensionId,
                extensionId);
            return ConfigurationWriteResult.Success(snapshot.Version);
        }

        var scan = ScanExtensions(cancellationToken);
        if (!scan.Succeeded)
        {
            return FailureWrite(scan.ErrorCode);
        }

        // Disabling is rejected while a loaded extension declares a dependency on the target.
        if (HasLoadedDependent(snapshot, scan, extensionId))
        {
            return Reject(nameof(DisableAsync), extensionId, ConfigurationErrorCode.Validation);
        }

        var result = await api.SetExtensionLoadStateAsync(
                extensionId,
                record.RecordVersion,
                ExtensionLoadState.Disabled,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess)
        {
            await CompletePublishTriggerAsync(cancellationToken).ConfigureAwait(false);
            HostLogMessages.ExtensionDisabled(
                _logger,
                _callerExtensionId,
                extensionId,
                result.NewVersion);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<ConfigurationWriteResult> ReloadAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
    {
        if (ExtensionCallbackGuard.IsLifecycleActive)
        {
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.Unsupported);
        }

        if (!CanManage(extensionId))
        {
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.Validation);
        }

        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
        {
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.Unsupported);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var hostConfig = scope.ServiceProvider.GetService<IHostConfigApi>();
        if (hostConfig is null)
        {
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.Unsupported);
        }

        var snapshotResult = await hostConfig.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshotResult.IsSuccess || snapshotResult.Value is not { } snapshot)
        {
            return ToWriteFailure(snapshotResult.Errors);
        }

        var record = snapshot.ExtensionRecords.FirstOrDefault(value =>
            string.Equals(value.ExtensionId, extensionId, StringComparison.Ordinal));
        if (record is null)
        {
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.NotFound);
        }

        if (record.LoadState != ExtensionLoadState.Loaded)
        {
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.Validation);
        }

        var scan = ScanExtensions(cancellationToken);
        if (!scan.Succeeded)
        {
            return FailureWrite(scan.ErrorCode);
        }

        if (!scan.Manifests.TryGetValue(extensionId, out var scannedManifest) ||
            !string.Equals(record.Version, scannedManifest.Version.ToString(), StringComparison.Ordinal))
        {
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.Validation);
        }

        if (ExtensionCallbackGuard.IsSelfReplacementUnsafe)
        {
            // Reload awaits generation replacement synchronously; from a route/event/scheduler
            // callback the publish may need to drain the calling extension itself (its manifest can
            // be drifted even when the target is another extension), which would deadlock.
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.Unsupported);
        }

        if (_publisher is null)
        {
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.Unsupported);
        }

        var publication = await _publisher
            .RequestExtensionReloadAsync(snapshot, extensionId, cancellationToken)
            .ConfigureAwait(false);
        if (publication.Status == HostConfigurationPublisher.ExtensionReloadPublicationStatus.Published)
        {
            HostLogMessages.ExtensionReloadPublished(
                _logger,
                _callerExtensionId,
                extensionId,
                publication.CommittedVersion);
        }
        else if (publication.Status == HostConfigurationPublisher.ExtensionReloadPublicationStatus.TargetUnavailable)
        {
            HostLogMessages.ExtensionReloadTargetUnavailable(
                _logger,
                _callerExtensionId,
                extensionId);
        }
        else
        {
            HostLogMessages.ExtensionReloadFailed(
                _logger,
                _callerExtensionId,
                extensionId);
        }
        return publication.Status switch
        {
            HostConfigurationPublisher.ExtensionReloadPublicationStatus.Published =>
                ConfigurationWriteResult.Success(publication.CommittedVersion),
            HostConfigurationPublisher.ExtensionReloadPublicationStatus.TargetUnavailable =>
                ValidationWriteFailure(),
            _ => FailureWrite(ConfigurationErrorCode.StorageUnavailable)
        };
    }

    /// <inheritdoc />
    public bool ReloadSoon(string extensionId)
    {
        // No callback-guard veto and no durable validation here: scheduling never blocks on
        // generation replacement, and the deferred publish revalidates the target against the
        // latest durable snapshot (missing/disabled/drifted targets simply do not reload).
        if (!CanManage(extensionId) ||
            !_runtimeState.ExtensionConfigurationWritesAllowed ||
            _publisher is null ||
            _snapshotReader is null)
        {
            return false;
        }

        TriggerReloadDeferred(extensionId);
        HostLogMessages.ExtensionReloadQueued(
            _logger,
            _callerExtensionId,
            extensionId);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<ConfigurationWriteResult> DeleteRecordAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
    {
        if (ExtensionCallbackGuard.IsLifecycleActive)
        {
            return Reject(nameof(DeleteRecordAsync), extensionId, ConfigurationErrorCode.Unsupported);
        }

        if (!CanManage(extensionId))
        {
            return Reject(nameof(DeleteRecordAsync), extensionId, ConfigurationErrorCode.Validation);
        }

        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
        {
            return Reject(nameof(DeleteRecordAsync), extensionId, ConfigurationErrorCode.Unsupported);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var api = scope.ServiceProvider.GetService<EfHostConfigApi>();
        if (api is null)
        {
            return Reject(nameof(DeleteRecordAsync), extensionId, ConfigurationErrorCode.Unsupported);
        }

        var snapshotResult = await api.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshotResult.IsSuccess || snapshotResult.Value is not { } snapshot)
        {
            return ToWriteFailure(snapshotResult.Errors);
        }

        var record = snapshot.ExtensionRecords.FirstOrDefault(value =>
            string.Equals(value.ExtensionId, extensionId, StringComparison.Ordinal));
        if (record is null)
        {
            return Reject(nameof(DeleteRecordAsync), extensionId, ConfigurationErrorCode.NotFound);
        }

        var scan = ScanExtensions(cancellationToken);
        if (!scan.Succeeded)
        {
            return FailureWrite(scan.ErrorCode);
        }

        if (scan.Manifests.ContainsKey(extensionId) || scan.DuplicateIds.Contains(extensionId))
        {
            return Reject(nameof(DeleteRecordAsync), extensionId, ConfigurationErrorCode.Validation);
        }

        if (scan.HasUnreadableDirectories)
        {
            // Unreadable directories make the absence check unreliable; refuse the delete.
            return FailureWrite(ConfigurationErrorCode.StorageUnavailable);
        }

        // Deleting is rejected while a loaded extension declares a dependency on the target.
        if (HasLoadedDependent(snapshot, scan, extensionId))
        {
            return Reject(nameof(DeleteRecordAsync), extensionId, ConfigurationErrorCode.Validation);
        }

        if (_lifecycle is not null)
        {
            await _lifecycle.StopOwnedServicesAsync(extensionId, cancellationToken).ConfigureAwait(false);
        }

        var result = await api.DeleteExtensionRecordCascadeAsync(
                extensionId,
                record.RecordVersion,
                cancellationToken)
            .ConfigureAwait(false);
        // Publish either way: on failure the owned services stopped above must be reconciled back.
        await CompletePublishTriggerAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            HostLogMessages.ExtensionRecordDeleted(
                _logger,
                _callerExtensionId,
                extensionId,
                result.NewVersion);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<ConfigurationReadResult<ExtensionRefreshSummary>> RequestRefreshAsync(
        CancellationToken cancellationToken = default)
    {
        if (ExtensionCallbackGuard.IsLifecycleActive)
        {
            return RefreshRejected(nameof(RequestRefreshAsync), ConfigurationErrorCode.Unsupported);
        }

        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
        {
            return RefreshRejected(nameof(RequestRefreshAsync), ConfigurationErrorCode.Unsupported);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var api = scope.ServiceProvider.GetService<EfHostConfigApi>();
        if (api is null)
        {
            return RefreshRejected(nameof(RequestRefreshAsync), ConfigurationErrorCode.Unsupported);
        }

        var snapshotResult = await api.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshotResult.IsSuccess || snapshotResult.Value is not { } snapshot)
        {
            return ConfigurationReadResult<ExtensionRefreshSummary>.Failure(
                snapshotResult.Errors.ToArray());
        }

        var scan = ScanExtensions(cancellationToken);
        if (!scan.Succeeded)
        {
            return ConfigurationReadResult<ExtensionRefreshSummary>.Failure(
                new ConfigurationError(scan.ErrorCode));
        }

        if (scan.DuplicateIds.Count != 0)
        {
            return ConfigurationReadResult<ExtensionRefreshSummary>.Failure(
                new ConfigurationError(ConfigurationErrorCode.Validation));
        }

        var records = snapshot.ExtensionRecords.ToDictionary(
            static value => value.ExtensionId,
            StringComparer.Ordinal);
        var added = scan.Manifests.Keys
            .Where(id => !records.ContainsKey(id))
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var versionUpdated = new List<string>();

        if (added.Length != 0)
        {
            var now = DateTimeOffset.UtcNow;
            var additions = added
                .Select(id => new ExtensionRecordConfiguration(
                    id,
                    scan.Manifests[id].Version.ToString(),
                    ExtensionLoadState.Disabled,
                    now,
                    now,
                    recordVersion: 0,
                    contentHash: scan.ContentHashes.TryGetValue(id, out var hash) ? hash : null))
                .ToImmutableArray();
            var persisted = await api.PersistDiscoveredExtensionRecordsAsync(
                    ExtensionLoadState.Disabled,
                    snapshot.Version,
                    additions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!persisted.IsSuccess)
            {
                return ConfigurationReadResult<ExtensionRefreshSummary>.Failure(
                    persisted.Errors.ToArray());
            }

        }
        foreach (var pair in scan.Manifests.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!records.TryGetValue(pair.Key, out var record))
            {
                continue;
            }

            var installedVersion = pair.Value.Version.ToString();
            var versionChanged = !string.Equals(record.Version, installedVersion, StringComparison.Ordinal);
            var observedHash = scan.ContentHashes.TryGetValue(pair.Key, out var hash) ? hash : null;
            // A transiently uncomputable digest (file lock or IO blip) must never overwrite the
            // durable pin or fail the whole refresh: treat the hash as unknown for this pass.
            var hashObservable = observedHash is not null;
            var hashChanged = hashObservable &&
                !string.Equals(record.ContentHash, observedHash, StringComparison.OrdinalIgnoreCase);
            if (!versionChanged && !hashChanged)
            {
                continue;
            }

            ConfigurationWriteResult updated;
            if (_dbContextFactory is null)
            {
                if (!versionChanged)
                {
                    continue;
                }

                updated = await api.UpdateExtensionInstalledVersionAsync(
                        pair.Key,
                        record.RecordVersion,
                        installedVersion,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (versionChanged && !hashObservable)
            {
                // Version bumped while the new digest is uncomputable: clear the stale pin
                // (unknown over stale); the next publish reports the extension ContentHashMissing.
                updated = await api.UpdateExtensionInstalledVersionAsync(
                        pair.Key,
                        record.RecordVersion,
                        installedVersion,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await using var db = await _dbContextFactory
                        .CreateDbContextAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var contentPersistence = new EfExtensionRecordContentPersistence(db, logger: _logger);
                    updated = versionChanged
                        ? await contentPersistence.UpdateInstalledVersionAndContentHashAsync(
                                pair.Key,
                                record.RecordVersion,
                                installedVersion,
                                observedHash!,
                                cancellationToken)
                            .ConfigureAwait(false)
                        : await contentPersistence.SetContentHashAsync(
                                pair.Key,
                                record.RecordVersion,
                                observedHash!,
                                cancellationToken)
                            .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    HostLogMessages.ExtensionManagementException(
                        _logger,
                        exception,
                        "RequestRefresh",
                        _callerExtensionId,
                        null);
                    updated = FailureWrite(ConfigurationErrorCode.StorageUnavailable);
                }
            }

            if (!updated.IsSuccess)
            {
                return ConfigurationReadResult<ExtensionRefreshSummary>.Failure(
                    updated.Errors.ToArray());
            }

            if (versionChanged)
            {
                versionUpdated.Add(pair.Key);
            }
        }

        // Reload is derived from descriptor identity at publish time: a pinned digest that
        // differs from the running binding's digest re-candidates the extension on any publish,
        // so refresh only needs to trigger the publish, not carry a reload set.
        await CompletePublishTriggerAsync(cancellationToken).ConfigureAwait(false);

        var missing = records.Keys
            .Where(id => !scan.Manifests.ContainsKey(id))
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToImmutableArray();
        HostLogMessages.ExtensionRefreshCompleted(
            _logger,
            _callerExtensionId,
            added.Length,
            versionUpdated.Count,
            missing.Length,
            scan.Skipped.Length);
        return ConfigurationReadResult<ExtensionRefreshSummary>.Success(
            new ExtensionRefreshSummary(
                added.ToImmutableArray(),
                versionUpdated.OrderBy(static id => id, StringComparer.Ordinal).ToImmutableArray(),
                missing,
                scan.Skipped));
    }

    private static bool CanManage(string? extensionId) =>
        !string.IsNullOrWhiteSpace(extensionId) &&
        extensionId.Length <= 128 &&
        !extensionId.Any(char.IsControl);

    private static bool HasLoadedDependent(
        HostConfigurationSnapshot snapshot,
        ExtensionScanResult scan,
        string extensionId) =>
        snapshot.ExtensionRecords
            .Where(value => value.LoadState == ExtensionLoadState.Loaded &&
                !string.Equals(value.ExtensionId, extensionId, StringComparison.Ordinal))
            .Any(value => scan.Manifests.TryGetValue(value.ExtensionId, out var dependentManifest) &&
                dependentManifest.Dependencies.Any(dependency =>
                    string.Equals(dependency.Id, extensionId, StringComparison.Ordinal)));

    private async ValueTask CompletePublishTriggerAsync(CancellationToken cancellationToken)
    {
        // From route/event/scheduler callbacks the awaited publish may need to drain the calling
        // extension itself (its manifest can be drifted even when the write targets another
        // extension), which would deadlock; trigger the publish after the callback returns.
        if (ExtensionCallbackGuard.IsSelfReplacementUnsafe)
        {
            TriggerPublishDeferred();
            return;
        }

        await TriggerPublishAsync(cancellationToken).ConfigureAwait(false);
    }

    private void TriggerReloadDeferred(string extensionId)
    {
        // Fire-and-forget forced reload: safe from every callback context because the caller never
        // awaits it; the publication gate serializes it behind any in-flight publish.
        _ = RunDeferredReloadAsync();

        async Task RunDeferredReloadAsync()
        {
            try
            {
                // Yield first so synchronously-completing readers cannot re-enter the publication
                // pipeline before the calling callback unwinds.
                await Task.Yield();
                var loaded = await _snapshotReader!.ReadCompleteAsync(CancellationToken.None).ConfigureAwait(false);
                if (!loaded.IsSuccess || loaded.Value is not { } snapshot)
                {
                    _runtimeState.MarkSnapshotRejected();
                    return;
                }

                var publication = await _publisher!
                    .RequestExtensionReloadAsync(snapshot, extensionId, CancellationToken.None)
                    .ConfigureAwait(false);
                if (publication.Status == HostConfigurationPublisher.ExtensionReloadPublicationStatus.Published)
                {
                    _runtimeState.MarkSnapshotAccepted();
                }
                else
                {
                    _runtimeState.MarkSnapshotRejected();
                }
            }
            catch (Exception exception)
            {
                HostLogMessages.ExtensionManagementBackgroundFailed(
                    _logger,
                    exception,
                    "DeferredReload",
                    _callerExtensionId);
            }
        }
    }

    private void TriggerPublishDeferred()
    {
        if (_publisher is null || _snapshotReader is null)
        {
            return;
        }

        // The calling route/event callback blocks generation drain of this extension; run the
        // publish after the callback returns. The PG revision NOTIFY remains the durable trigger.
        _ = RunDeferredPublishAsync();

        async Task RunDeferredPublishAsync()
        {
            try
            {
                // Yield first so synchronously-completing readers cannot re-enter the publication
                // pipeline before the calling callback unwinds.
                await Task.Yield();
                await TriggerPublishAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                HostLogMessages.ExtensionManagementBackgroundFailed(
                    _logger,
                    exception,
                    "DeferredPublish",
                    _callerExtensionId);
            }
        }
    }

    private async ValueTask TriggerPublishAsync(CancellationToken cancellationToken)
    {
        if (_publisher is null || _snapshotReader is null)
        {
            return;
        }

        var loaded = await _snapshotReader.ReadCompleteAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess || loaded.Value is not { } snapshot)
        {
            _runtimeState.MarkSnapshotRejected();
            return;
        }

        if (await _publisher.PublishAsync(snapshot, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            _runtimeState.MarkSnapshotAccepted();
        }
        else
        {
            _runtimeState.MarkSnapshotRejected();
        }
    }

    private ExtensionScanResult ScanExtensions(CancellationToken cancellationToken)
    {
        var manifests = new Dictionary<string, ExtensionManifest>(StringComparer.Ordinal);
        var contentHashes = new Dictionary<string, string?>(StringComparer.Ordinal);
        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new List<ExtensionScanSkip>();
        var installRoot = _runtimeState.NodeOptions.ExtensionsRootPath;
        if (!Directory.Exists(installRoot))
        {
            return ExtensionScanResult.Success(manifests, contentHashes, duplicateIds, skipped);
        }

        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(installRoot)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception)
        {
            HostLogMessages.ExtensionScanFailed(_logger, exception, "EnumerateDirectories");
            return ExtensionScanResult.Failure(ConfigurationErrorCode.StorageUnavailable);
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ManifestDiscoveryResult discovered;
            try
            {
                discovered = ExtensionManifestDiscovery.Discover(directory, _logger);
            }
            catch (Exception exception)
            {
                HostLogMessages.ExtensionScanFailed(_logger, exception, "DiscoverManifest");
                skipped.Add(new ExtensionScanSkip(
                    DirectoryName(directory),
                    ExtensionFailureCode.LoadFailed.ToString()));
                continue;
            }

            if (!discovered.Succeeded || discovered.Manifest is not { } manifest)
            {
                skipped.Add(new ExtensionScanSkip(
                    DirectoryName(directory),
                    discovered.FailureCode.ToString()));
                continue;
            }

            if (duplicateIds.Contains(manifest.Id))
            {
                continue;
            }

            if (!manifests.TryAdd(manifest.Id, manifest))
            {
                manifests.Remove(manifest.Id);
                contentHashes.Remove(manifest.Id);
                duplicateIds.Add(manifest.Id);
                continue;
            }

            contentHashes[manifest.Id] = ExtensionContentDigest.TryCompute(manifest, _logger);
        }

        ExtensionAssemblyShadowLink.ScheduleInvalidLinkCleanup(_logger);
        return ExtensionScanResult.Success(manifests, contentHashes, duplicateIds, skipped);
    }

    private static string DirectoryName(string directory)
    {
        var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private static ConfigurationWriteResult ToWriteFailure(
        ImmutableArray<ConfigurationError> errors) =>
        ConfigurationWriteResult.Failure(errors.ToArray());

    private static ConfigurationWriteResult FailureWrite(ConfigurationErrorCode code) =>
        ConfigurationWriteResult.Failure(new ConfigurationError(code));

    private static ConfigurationWriteResult ValidationWriteFailure() =>
        FailureWrite(ConfigurationErrorCode.Validation);

    private static ConfigurationWriteResult NotFoundWriteFailure() =>
        FailureWrite(ConfigurationErrorCode.NotFound);

    private static ConfigurationWriteResult UnsupportedWriteFailure() =>
        FailureWrite(ConfigurationErrorCode.Unsupported);

    /// <summary>Reports one rejected refresh and returns its safe failure.</summary>
    private ConfigurationReadResult<ExtensionRefreshSummary> RefreshRejected(
        string operation,
        ConfigurationErrorCode errorCode)
    {
        if (_logger is { } logger)
        {
            HostLogMessages.ExtensionManagementRejected(
                logger,
                operation,
                _callerExtensionId,
                targetExtensionId: null,
                errorCode);
        }

        return ConfigurationReadResult<ExtensionRefreshSummary>.Failure(new ConfigurationError(errorCode));
    }

    /// <summary>Reports one rejected management operation and returns its safe failure.</summary>
    private ConfigurationWriteResult Reject(
        string operation,
        string? targetExtensionId,
        ConfigurationErrorCode errorCode)
    {
        if (_logger is { } logger)
        {
            HostLogMessages.ExtensionManagementRejected(
                logger,
                operation,
                _callerExtensionId,
                targetExtensionId,
                errorCode);
        }

        return FailureWrite(errorCode);
    }

    private sealed record ExtensionScanResult(
        bool Succeeded,
        ConfigurationErrorCode ErrorCode,
        ImmutableDictionary<string, ExtensionManifest> Manifests,
        ImmutableDictionary<string, string?> ContentHashes,
        ImmutableHashSet<string> DuplicateIds,
        ImmutableArray<ExtensionScanSkip> Skipped)
    {
        internal bool HasUnreadableDirectories => Skipped.Length != 0;

        internal static ExtensionScanResult Success(
            Dictionary<string, ExtensionManifest> manifests,
            Dictionary<string, string?> contentHashes,
            HashSet<string> duplicateIds,
            List<ExtensionScanSkip> skipped) =>
            new(
                true,
                ConfigurationErrorCode.Validation,
                manifests.ToImmutableDictionary(StringComparer.Ordinal),
                contentHashes.ToImmutableDictionary(StringComparer.Ordinal),
                duplicateIds.ToImmutableHashSet(StringComparer.Ordinal),
                skipped.ToImmutableArray());

        internal static ExtensionScanResult Failure(ConfigurationErrorCode errorCode) =>
            new(
                false,
                errorCode,
                ImmutableDictionary<string, ExtensionManifest>.Empty,
                ImmutableDictionary<string, string?>.Empty,
                ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal),
                ImmutableArray<ExtensionScanSkip>.Empty);
    }
}
