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
                new ConfigurationError(
                    ConfigurationErrorCode.Unsupported,
                    "The host configuration API is unavailable; extension management records cannot be listed."));
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
                new ConfigurationError(scan.ErrorCode, scan.ErrorMessage));
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
            return Reject(nameof(EnableAsync), extensionId, ConfigurationErrorCode.Unsupported,
                "EnableAsync cannot run from an active extension lifecycle callback because publication could wait on the callback.");
        }

        if (!CanManage(extensionId))
        {
            return Reject(nameof(EnableAsync), extensionId, ConfigurationErrorCode.Validation,
                InvalidExtensionIdentifierMessage(extensionId));
        }

        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
        {
            return Reject(nameof(EnableAsync), extensionId, ConfigurationErrorCode.Unsupported,
                $"Extension '{extensionId}' cannot be enabled because the host disallows extension configuration writes.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var api = scope.ServiceProvider.GetService<EfHostConfigApi>();
        if (api is null)
        {
            return Reject(nameof(EnableAsync), extensionId, ConfigurationErrorCode.Unsupported,
                $"The host extension persistence API is unavailable; extension '{extensionId}' cannot be enabled.");
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
            return Reject(nameof(EnableAsync), extensionId, ConfigurationErrorCode.NotFound,
                $"Extension record '{extensionId}' was not found.");
        }

        if (record.LoadState is not (ExtensionLoadState.Disabled or ExtensionLoadState.Stopped or ExtensionLoadState.Failed))
        {
            return Reject(nameof(EnableAsync), extensionId, ConfigurationErrorCode.Validation,
                $"Extension record '{extensionId}' has load state '{record.LoadState}'; enabling requires Disabled, Stopped, or Failed.");
        }

        var scan = ScanExtensions(cancellationToken);
        if (!scan.Succeeded)
        {
            return FailureWrite(scan.ErrorCode, scan.ErrorMessage);
        }

        if (!scan.Manifests.TryGetValue(extensionId, out var manifest))
        {
            return Reject(
                nameof(EnableAsync),
                extensionId,
                ConfigurationErrorCode.Validation,
                $"No valid manifest was found for extension record '{extensionId}'.");
        }

        if (!string.Equals(record.Version, manifest.Version.ToString(), StringComparison.Ordinal))
        {
            return Reject(
                nameof(EnableAsync),
                extensionId,
                ConfigurationErrorCode.Validation,
                $"Extension record '{extensionId}' stores version '{record.Version}', but its manifest declares version '{manifest.Version}'.");
        }

        foreach (var dependency in manifest.Dependencies)
        {
            // Enabling requires every declared non-optional dependency to be loaded and version-satisfied.
            if (dependency.Optional)
            {
                continue;
            }

            var dependencyRecord = snapshot.ExtensionRecords.FirstOrDefault(value =>
                string.Equals(value.ExtensionId, dependency.Id, StringComparison.Ordinal));
            if (dependencyRecord is null)
            {
                return Reject(
                    nameof(EnableAsync),
                    extensionId,
                    ConfigurationErrorCode.Validation,
                    $"Required dependency '{dependency.Id}' for extension '{extensionId}' has no persisted extension record.");
            }

            if (dependencyRecord.LoadState != ExtensionLoadState.Loaded)
            {
                return Reject(
                    nameof(EnableAsync),
                    extensionId,
                    ConfigurationErrorCode.Validation,
                    $"Required dependency '{dependency.Id}' for extension '{extensionId}' has load state '{dependencyRecord.LoadState}', but must be Loaded.");
            }

            if (!scan.Manifests.TryGetValue(dependency.Id, out var dependencyManifest))
            {
                return Reject(
                    nameof(EnableAsync),
                    extensionId,
                    ConfigurationErrorCode.Validation,
                    $"Required dependency '{dependency.Id}' for extension '{extensionId}' has no valid installed manifest.");
            }

            var actualVersion = dependencyManifest.Version.ToString();
            if (!string.Equals(dependencyRecord.Version, actualVersion, StringComparison.Ordinal))
            {
                return Reject(
                    nameof(EnableAsync),
                    extensionId,
                    ConfigurationErrorCode.Validation,
                    $"Required dependency '{dependency.Id}' for extension '{extensionId}' has persisted version '{dependencyRecord.Version}', but its installed manifest declares '{actualVersion}'.");
            }

            if (!dependency.VersionRange.IsSatisfiedBy(dependencyManifest.Version))
            {
                return Reject(
                    nameof(EnableAsync),
                    extensionId,
                    ConfigurationErrorCode.Validation,
                    $"Required dependency '{dependency.Id}' for extension '{extensionId}' has installed version '{actualVersion}', which does not satisfy required range '{dependency.VersionRange}'.");
            }
        }

        var contentHash = scan.ContentHashes.TryGetValue(extensionId, out var observedHash)
            ? observedHash
            : null;
        ConfigurationWriteResult result;
        if (_dbContextFactory is null)
        {
            using (HostConfigurationWriteContext.EnterExtension(_callerExtensionId))
            {
                result = await api.SetExtensionLoadStateAsync(
                        extensionId,
                        record.RecordVersion,
                        ExtensionLoadState.Loaded,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            try
            {
                await using var db = await _dbContextFactory
                    .CreateDbContextAsync(cancellationToken)
                    .ConfigureAwait(false);
                var contentPersistence = new EfExtensionRecordContentPersistence(db, logger: _logger);
                using (HostConfigurationWriteContext.EnterExtension(_callerExtensionId))
                {
                    result = await contentPersistence.SetLoadStateAndContentHashAsync(
                            extensionId,
                            record.RecordVersion,
                            ExtensionLoadState.Loaded,
                            contentHash,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
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
                result = FailureWrite(
                    ConfigurationErrorCode.StorageUnavailable,
                    $"Extension record '{extensionId}' could not be enabled because the host database operation failed while updating its load state and content hash.");
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
            return Reject(nameof(DisableAsync), extensionId, ConfigurationErrorCode.Unsupported,
                "DisableAsync cannot run from an active extension lifecycle callback because publication could wait on the callback.");
        }

        if (!CanManage(extensionId))
        {
            return Reject(nameof(DisableAsync), extensionId, ConfigurationErrorCode.Validation,
                InvalidExtensionIdentifierMessage(extensionId));
        }

        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
        {
            return Reject(nameof(DisableAsync), extensionId, ConfigurationErrorCode.Unsupported,
                $"Extension '{extensionId}' cannot be disabled because the host disallows extension configuration writes.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var api = scope.ServiceProvider.GetService<EfHostConfigApi>();
        if (api is null)
        {
            return Reject(nameof(DisableAsync), extensionId, ConfigurationErrorCode.Unsupported,
                $"The host extension persistence API is unavailable; extension '{extensionId}' cannot be disabled.");
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
            return Reject(nameof(DisableAsync), extensionId, ConfigurationErrorCode.NotFound,
                $"Extension record '{extensionId}' was not found.");
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
            return FailureWrite(scan.ErrorCode, scan.ErrorMessage);
        }

        // Disabling is rejected while a loaded extension declares a dependency on the target.
        var loadedDependent = FindLoadedDependent(snapshot, scan, extensionId);
        if (loadedDependent is { } dependentId)
        {
            return Reject(
                nameof(DisableAsync),
                extensionId,
                ConfigurationErrorCode.Validation,
                $"Loaded extension '{dependentId}' declares a required dependency on extension '{extensionId}', so the target cannot be disabled.");
        }

        ConfigurationWriteResult result;
        using (HostConfigurationWriteContext.EnterExtension(_callerExtensionId))
        {
            result = await api.SetExtensionLoadStateAsync(
                    extensionId,
                    record.RecordVersion,
                    ExtensionLoadState.Disabled,
                    cancellationToken)
                .ConfigureAwait(false);
        }
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
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.Unsupported,
                "ReloadAsync cannot run from an active extension lifecycle callback because publication could wait on the callback.");
        }

        if (!CanManage(extensionId))
        {
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.Validation,
                InvalidExtensionIdentifierMessage(extensionId));
        }

        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
        {
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.Unsupported,
                $"Extension '{extensionId}' cannot be reloaded because the host disallows extension configuration writes.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var hostConfig = scope.ServiceProvider.GetService<IHostConfigApi>();
        if (hostConfig is null)
        {
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.Unsupported,
                $"The host configuration API is unavailable; extension '{extensionId}' cannot be reloaded.");
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
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.NotFound,
                $"Extension record '{extensionId}' was not found.");
        }

        if (record.LoadState != ExtensionLoadState.Loaded)
        {
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.Validation,
                $"Extension record '{extensionId}' has load state '{record.LoadState}'; reloading requires Loaded.");
        }

        var scan = ScanExtensions(cancellationToken);
        if (!scan.Succeeded)
        {
            return FailureWrite(scan.ErrorCode, scan.ErrorMessage);
        }

        if (!scan.Manifests.TryGetValue(extensionId, out var scannedManifest))
        {
            return Reject(
                nameof(ReloadAsync),
                extensionId,
                ConfigurationErrorCode.Validation,
                $"No valid installed manifest was found for extension '{extensionId}'.");
        }

        if (!string.Equals(record.Version, scannedManifest.Version.ToString(), StringComparison.Ordinal))
        {
            return Reject(
                nameof(ReloadAsync),
                extensionId,
                ConfigurationErrorCode.Validation,
                $"Extension record '{extensionId}' stores version '{record.Version}', but its installed manifest declares '{scannedManifest.Version}'.");
        }

        if (ExtensionCallbackGuard.IsSelfReplacementUnsafe)
        {
            // Reload awaits generation replacement synchronously; from a route/event/scheduler
            // callback the publish may need to drain the calling extension itself (its manifest can
            // be drifted even when the target is another extension), which would deadlock.
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.Unsupported,
                $"Reloading extension '{extensionId}' from this callback could wait for the caller's own generation to drain.");
        }

        if (_publisher is null)
        {
            return Reject(nameof(ReloadAsync), extensionId, ConfigurationErrorCode.Unsupported,
                $"The extension reload publisher is unavailable; extension '{extensionId}' cannot be reloaded.");
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
                ValidationWriteFailure(
                    publication.FailureDetail?.Message ??
                    $"Extension '{extensionId}' became unavailable before its reload could be published."),
            HostConfigurationPublisher.ExtensionReloadPublicationStatus.Failed =>
                FailureWrite(
                    ConfigurationErrorCode.Unsupported,
                    publication.FailureDetail?.Message ??
                    $"Extension '{extensionId}' reload publication returned status '{publication.Status}' without a precise failure detail."),
            _ => FailureWrite(
                ConfigurationErrorCode.Unsupported,
                $"Extension '{extensionId}' reload publication returned unrecognized status '{publication.Status}'.")
        };
    }

    /// <inheritdoc />
    public ExtensionReloadScheduleResult ReloadSoon(string extensionId)
    {
        if (!CanManage(extensionId))
        {
            if (_logger is { } logger)
            {
                HostLogMessages.ExtensionManagementRejected(
                    logger,
                    nameof(ReloadSoon),
                    _callerExtensionId,
                    extensionId,
                    ConfigurationErrorCode.Validation);
            }

            return ExtensionReloadScheduleResult.Failure(
                ExtensionReloadScheduleFailureCode.InvalidArgument,
                new ExtensionErrorDetail(InvalidExtensionIdentifierMessage(extensionId)));
        }

        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
        {
            if (_logger is { } logger)
            {
                HostLogMessages.ExtensionManagementRejected(
                    logger,
                    nameof(ReloadSoon),
                    _callerExtensionId,
                    extensionId,
                    ConfigurationErrorCode.Unsupported);
            }

            return ExtensionReloadScheduleResult.Failure(
                ExtensionReloadScheduleFailureCode.WritesDisallowed,
                new ExtensionErrorDetail(
                    $"Extension '{extensionId}' cannot be reloaded because the host disallows extension configuration writes."));
        }

        if (_publisher is null || _snapshotReader is null)
        {
            if (_logger is { } logger)
            {
                HostLogMessages.ExtensionManagementRejected(
                    logger,
                    nameof(ReloadSoon),
                    _callerExtensionId,
                    extensionId,
                    ConfigurationErrorCode.Unsupported);
            }

            var failureMessage = _publisher is null
                ? _snapshotReader is null
                    ? $"Extension '{extensionId}' cannot be scheduled for reload because the host extension reload publisher and configuration snapshot reader are unavailable."
                    : $"Extension '{extensionId}' cannot be scheduled for reload because the host extension reload publisher is unavailable."
                : $"Extension '{extensionId}' cannot be scheduled for reload because the host configuration snapshot reader is unavailable.";
            return ExtensionReloadScheduleResult.Failure(
                ExtensionReloadScheduleFailureCode.Unsupported,
                new ExtensionErrorDetail(failureMessage));
        }

        TriggerReloadDeferred(extensionId);
        HostLogMessages.ExtensionReloadQueued(
            _logger,
            _callerExtensionId,
            extensionId);
        return ExtensionReloadScheduleResult.Success;
    }

    /// <inheritdoc />
    public async ValueTask<ConfigurationWriteResult> DeleteRecordAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
    {
        if (ExtensionCallbackGuard.IsLifecycleActive)
        {
            return Reject(nameof(DeleteRecordAsync), extensionId, ConfigurationErrorCode.Unsupported,
                "DeleteRecordAsync cannot run from an active extension lifecycle callback because publication could wait on the callback.");
        }

        if (!CanManage(extensionId))
        {
            return Reject(nameof(DeleteRecordAsync), extensionId, ConfigurationErrorCode.Validation,
                InvalidExtensionIdentifierMessage(extensionId));
        }

        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
        {
            return Reject(nameof(DeleteRecordAsync), extensionId, ConfigurationErrorCode.Unsupported,
                $"Extension record '{extensionId}' cannot be deleted because the host disallows extension configuration writes.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var api = scope.ServiceProvider.GetService<EfHostConfigApi>();
        if (api is null)
        {
            return Reject(nameof(DeleteRecordAsync), extensionId, ConfigurationErrorCode.Unsupported,
                $"The host extension persistence API is unavailable; extension record '{extensionId}' cannot be deleted.");
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
            return Reject(nameof(DeleteRecordAsync), extensionId, ConfigurationErrorCode.NotFound,
                $"Extension record '{extensionId}' was not found.");
        }

        var scan = ScanExtensions(cancellationToken);
        if (!scan.Succeeded)
        {
            return FailureWrite(scan.ErrorCode, scan.ErrorMessage);
        }

        if (scan.DuplicateIds.Contains(extensionId))
        {
            return Reject(
                nameof(DeleteRecordAsync),
                extensionId,
                ConfigurationErrorCode.Validation,
                $"Multiple installed manifests declare extension identifier '{extensionId}', so its record cannot be deleted safely.");
        }

        if (scan.Manifests.ContainsKey(extensionId))
        {
            return Reject(
                nameof(DeleteRecordAsync),
                extensionId,
                ConfigurationErrorCode.Validation,
                $"An installed manifest still declares extension identifier '{extensionId}', so its record cannot be deleted.");
        }

        if (scan.HasUnreadableDirectories)
        {
            var skippedDirectoryNames = string.Join(
                ", ",
                scan.Skipped.Select(static skipped => skipped.DirectoryName).OrderBy(static name => name, StringComparer.Ordinal));
            return FailureWrite(
                ConfigurationErrorCode.StorageUnavailable,
                $"Extension record '{extensionId}' cannot be deleted because scanning skipped directories '{skippedDirectoryNames}' and could not verify that the extension is absent.");
        }

        // Deleting is rejected while a loaded extension declares a dependency on the target.
        var loadedDependent = FindLoadedDependent(snapshot, scan, extensionId);
        if (loadedDependent is { } dependentId)
        {
            return Reject(
                nameof(DeleteRecordAsync),
                extensionId,
                ConfigurationErrorCode.Validation,
                $"Loaded extension '{dependentId}' declares a required dependency on extension '{extensionId}', so the target cannot be deleted.");
        }

        if (_lifecycle is not null)
        {
            await _lifecycle.StopOwnedServicesAsync(extensionId, cancellationToken).ConfigureAwait(false);
        }

        ConfigurationWriteResult result;
        using (HostConfigurationWriteContext.EnterExtension(_callerExtensionId))
        {
            result = await api.DeleteExtensionRecordCascadeAsync(
                    extensionId,
                    record.RecordVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }
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
            return RefreshRejected(
                nameof(RequestRefreshAsync),
                ConfigurationErrorCode.Unsupported,
                "RequestRefreshAsync cannot run from an active extension lifecycle callback because publication could wait on the callback.");
        }

        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
        {
            return RefreshRejected(
                nameof(RequestRefreshAsync),
                ConfigurationErrorCode.Unsupported,
                "Extension configuration writes are disallowed, so extension records cannot be refreshed.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var api = scope.ServiceProvider.GetService<EfHostConfigApi>();
        if (api is null)
        {
            return RefreshRejected(
                nameof(RequestRefreshAsync),
                ConfigurationErrorCode.Unsupported,
                "The host extension persistence API is unavailable; extension records cannot be refreshed.");
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
                new ConfigurationError(scan.ErrorCode, scan.ErrorMessage));
        }

        if (scan.DuplicateIds.Count != 0)
        {
            var duplicateIds = string.Join(
                ", ",
                scan.DuplicateIds.OrderBy(static id => id, StringComparer.Ordinal));
            return ConfigurationReadResult<ExtensionRefreshSummary>.Failure(
                new ConfigurationError(
                    ConfigurationErrorCode.Validation,
                    $"Extension refresh found duplicate extension identifiers: {duplicateIds}."));
        }

        var records = snapshot.ExtensionRecords.ToDictionary(
            static value => value.ExtensionId,
            StringComparer.Ordinal);
        var added = scan.Manifests.Keys
            .Where(id => !records.ContainsKey(id))
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var versionUpdated = new List<string>();
        var contentHashUpdated = 0;

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
            ConfigurationWriteResult persisted;
            using (HostConfigurationWriteContext.EnterExtension(_callerExtensionId))
            {
                persisted = await api.PersistDiscoveredExtensionRecordsAsync(
                        ExtensionLoadState.Disabled,
                        snapshot.Version,
                        additions,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
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

                using (HostConfigurationWriteContext.EnterExtension(_callerExtensionId))
                {
                    updated = await api.UpdateExtensionInstalledVersionAsync(
                            pair.Key,
                            record.RecordVersion,
                            installedVersion,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else if (versionChanged && !hashObservable)
            {
                // Version bumped while the new digest is uncomputable: clear the stale pin
                // (unknown over stale); the next publish reports the extension ContentHashMissing.
                using (HostConfigurationWriteContext.EnterExtension(_callerExtensionId))
                {
                    updated = await api.UpdateExtensionInstalledVersionAsync(
                            pair.Key,
                            record.RecordVersion,
                            installedVersion,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                try
                {
                    await using var db = await _dbContextFactory
                        .CreateDbContextAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var contentPersistence = new EfExtensionRecordContentPersistence(db, logger: _logger);
                    using (HostConfigurationWriteContext.EnterExtension(_callerExtensionId))
                    {
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
                    updated = FailureWrite(
                        ConfigurationErrorCode.StorageUnavailable,
                        $"Extension record '{pair.Key}' could not be updated during refresh because the backing database operation failed while persisting its {(versionChanged ? "installed version and content hash" : "content hash")}.");
                }
            }

            if (!updated.IsSuccess)
            {
                return ConfigurationReadResult<ExtensionRefreshSummary>.Failure(
                    updated.Errors.ToArray());
            }
            if (hashChanged && _dbContextFactory is not null)
            {
                contentHashUpdated++;
            }

            if (versionChanged)
            {
                versionUpdated.Add(pair.Key);
            }
        }

        var hasDurableChanges = added.Length != 0 || versionUpdated.Count != 0 || contentHashUpdated != 0;
        var hasLoadedRecordNotRunning = false;
        var runningContentHashDrift = false;
        if (!hasDurableChanges)
        {
            var runningExtensionIds = _runtimeManager.GetStatuses()
                .Where(static status => status.State == ExtensionLoadState.Loaded)
                .Select(static status => status.ExtensionId)
                .ToHashSet(StringComparer.Ordinal);
            hasLoadedRecordNotRunning = snapshot.ExtensionRecords.Any(record =>
                record.LoadState == ExtensionLoadState.Loaded &&
                scan.Manifests.ContainsKey(record.ExtensionId) &&
                !runningExtensionIds.Contains(record.ExtensionId));
            runningContentHashDrift = _publisher?.HasRunningContentHashDrift(snapshot, scan.ContentHashes) == true;
        }

        var publishDecision = GetRefreshPublishDecision(
            added.Length,
            versionUpdated.Count,
            contentHashUpdated,
            runningContentHashDrift,
            hasLoadedRecordNotRunning);
        if (publishDecision.ShouldPublish)
        {
            await CompletePublishTriggerAsync(cancellationToken).ConfigureAwait(false);
        }

        var missing = records.Keys
            .Where(id => !scan.Manifests.ContainsKey(id))
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToImmutableArray();
        HostLogMessages.ExtensionRefreshCompleted(
            _logger,
            _callerExtensionId,
            added.Length,
            versionUpdated.Count,
            contentHashUpdated,
            missing.Length,
            scan.Skipped.Length,
            publishDecision.ShouldPublish,
            publishDecision.Reason);
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

    private static string InvalidExtensionIdentifierMessage(string? extensionId)
    {
        if (extensionId is null)
        {
            return "The extensionId argument is null; it must be non-whitespace, no longer than 128 characters, and contain no control characters.";
        }

        if (extensionId.Length == 0)
        {
            return "The extensionId argument is empty; it must be non-whitespace, no longer than 128 characters, and contain no control characters.";
        }

        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return "The extensionId argument is whitespace; it must be non-whitespace, no longer than 128 characters, and contain no control characters.";
        }

        if (extensionId.Length > 128)
        {
            return $"The extensionId argument has actual length {extensionId.Length}; maximum allowed length is 128.";
        }

        foreach (var character in extensionId)
        {
            if (char.IsControl(character))
            {
                return $"The extensionId argument contains control character U+{(int)character:X4}; control characters are not allowed.";
            }
        }

        return "The extensionId argument violates the identifier rules; it must be non-whitespace, no longer than 128 characters, and contain no control characters.";
    }

    internal static (bool ShouldPublish, string Reason) GetRefreshPublishDecision(
        int addedCount,
        int versionUpdatedCount,
        int contentHashUpdatedCount,
        bool hasRunningContentHashDrift,
        bool hasLoadedRecordNotRunning)
    {
        if (addedCount != 0 || versionUpdatedCount != 0 || contentHashUpdatedCount != 0)
        {
            return (true, "DurableConfigurationChange");
        }

        if (hasLoadedRecordNotRunning)
        {
            return (true, "LoadedRecordNotRunning");
        }

        return hasRunningContentHashDrift
            ? (true, "RunningContentHashDrift")
            : (false, "NoChanges");
    }

    private static string? FindLoadedDependent(
        HostConfigurationSnapshot snapshot,
        ExtensionScanResult scan,
        string extensionId) =>
        snapshot.ExtensionRecords
            .Where(value => value.LoadState == ExtensionLoadState.Loaded &&
                !string.Equals(value.ExtensionId, extensionId, StringComparison.Ordinal))
            .FirstOrDefault(value => scan.Manifests.TryGetValue(value.ExtensionId, out var dependentManifest) &&
                dependentManifest.Dependencies.Any(dependency =>
                    !dependency.Optional &&
                    string.Equals(dependency.Id, extensionId, StringComparison.Ordinal)))
            ?.ExtensionId;

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

        var outcome = await _publisher.PublishAsync(
            snapshot,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (outcome != PublishOutcome.Failed)
        {
            _runtimeState.MarkSnapshotAccepted();
        }
        else
        {
            _runtimeState.MarkSnapshotRejected();
            HostLogMessages.ConfigurationSnapshotRejected(_logger, "PublishFailed");
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
            return ExtensionScanResult.Failure(
                ConfigurationErrorCode.StorageUnavailable,
                "Extension directory scan could not enumerate directories under the configured extension root because the filesystem enumeration operation failed.");
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
                var directoryName = DirectoryName(directory);
                skipped.Add(new ExtensionScanSkip(
                    directoryName,
                    ExtensionFailureCode.LoadFailed.ToString(),
                    new ExtensionErrorDetail(
                        $"Manifest discovery for extension directory '{directoryName}' failed with {exception.GetType().Name}.")));
                continue;
            }

            if (!discovered.Succeeded || discovered.Manifest is not { } manifest)
            {
                var directoryName = DirectoryName(directory);
                var failureMessage = discovered.FailureDetail?.Message ??
                    $"Manifest discovery for extension directory '{directoryName}' returned failure code '{discovered.FailureCode}' without a manifest.";
                skipped.Add(new ExtensionScanSkip(
                    directoryName,
                    discovered.FailureCode.ToString(),
                    new ExtensionErrorDetail(failureMessage)));
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

    private static ConfigurationWriteResult FailureWrite(ConfigurationErrorCode code, string message) =>
        ConfigurationWriteResult.Failure(new ConfigurationError(code, message));

    private static ConfigurationWriteResult ValidationWriteFailure(string message) =>
        FailureWrite(ConfigurationErrorCode.Validation, message);

    /// <summary>Reports one rejected refresh and returns its safe failure.</summary>
    private ConfigurationReadResult<ExtensionRefreshSummary> RefreshRejected(
        string operation,
        ConfigurationErrorCode errorCode,
        string message)
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

        return ConfigurationReadResult<ExtensionRefreshSummary>.Failure(
            new ConfigurationError(errorCode, message));
    }

    /// <summary>Reports one rejected management operation and returns its safe failure.</summary>
    private ConfigurationWriteResult Reject(
        string operation,
        string? targetExtensionId,
        ConfigurationErrorCode errorCode,
        string message)
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

        return FailureWrite(errorCode, message);
    }

    private sealed record ExtensionScanResult(
        bool Succeeded,
        ConfigurationErrorCode ErrorCode,
        string ErrorMessage,
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
                string.Empty,
                manifests.ToImmutableDictionary(StringComparer.Ordinal),
                contentHashes.ToImmutableDictionary(StringComparer.Ordinal),
                duplicateIds.ToImmutableHashSet(StringComparer.Ordinal),
                skipped.ToImmutableArray());

        internal static ExtensionScanResult Failure(
            ConfigurationErrorCode errorCode,
            string errorMessage) =>
            new(
                false,
                errorCode,
                errorMessage,
                ImmutableDictionary<string, ExtensionManifest>.Empty,
                ImmutableDictionary<string, string?>.Empty,
                ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal),
                ImmutableArray<ExtensionScanSkip>.Empty);
    }
}
