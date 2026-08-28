using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly HostConfigurationPublisher? _publisher;
    private readonly IHostConfigurationSnapshotReader? _snapshotReader;
    private readonly HostServiceLifecycleManager? _lifecycle;

    /// <summary>Creates an extension management facade for one extension caller.</summary>
    /// <param name="extensionId">The identity of the extension receiving this facade.</param>
    /// <param name="scopeFactory">The factory for short-lived persistence scopes.</param>
    /// <param name="runtimeState">The host capability state used to gate writes.</param>
    /// <param name="runtimeManager">The runtime manager used for running-state snapshots.</param>
    /// <param name="serviceProvider">The root provider containing host publication services.</param>
    internal ExtensionManagementFacade(
        string extensionId,
        IServiceScopeFactory scopeFactory,
        HostRuntimeState runtimeState,
        ExtensionRuntimeManager runtimeManager,
        IServiceProvider serviceProvider)
    {
        _callerExtensionId = string.IsNullOrWhiteSpace(extensionId)
            ? throw new ArgumentException("An extension identifier is required.", nameof(extensionId))
            : extensionId;
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        _runtimeManager = runtimeManager ?? throw new ArgumentNullException(nameof(runtimeManager));
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _publisher = serviceProvider.GetService<HostConfigurationPublisher>();
        _snapshotReader = serviceProvider.GetService<IHostConfigurationSnapshotReader>();
        _lifecycle = serviceProvider.GetService<HostServiceLifecycleManager>();
    }

    /// <inheritdoc />
    public HostApiVersion ApiVersion => HostApiVersion.Current;

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

        var running = _runtimeManager.GetStatuses()
            .Where(static status => status.State == ExtensionLoadState.Loaded)
            .Select(static status => status.ExtensionId)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var entries = snapshot.ExtensionRecords
            .OrderBy(static record => record.ExtensionId, StringComparer.Ordinal)
            .Select(record => new ExtensionManagementEntry(
                record.ExtensionId,
                record.Version,
                record.LoadState,
                record.CreatedAt,
                record.UpdatedAt,
                record.RecordVersion,
                running.Contains(record.ExtensionId),
                scan.Manifests.TryGetValue(record.ExtensionId, out var manifest)
                    ? manifest.Version.ToString()
                    : null))
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
            return UnsupportedWriteFailure();
        }

        if (!CanManage(extensionId))
        {
            return ValidationWriteFailure();
        }

        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
        {
            return UnsupportedWriteFailure();
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var api = scope.ServiceProvider.GetService<EfHostConfigApi>();
        if (api is null)
        {
            return UnsupportedWriteFailure();
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
            return NotFoundWriteFailure();
        }

        if (record.LoadState is not (ExtensionLoadState.Disabled or ExtensionLoadState.Stopped or ExtensionLoadState.Failed))
        {
            return ValidationWriteFailure();
        }

        var scan = ScanExtensions(cancellationToken);
        if (!scan.Succeeded)
        {
            return FailureWrite(scan.ErrorCode);
        }

        if (!scan.Manifests.TryGetValue(extensionId, out var manifest) ||
            !string.Equals(record.Version, manifest.Version.ToString(), StringComparison.Ordinal))
        {
            return ValidationWriteFailure();
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
                return ValidationWriteFailure();
            }
        }

        var result = await api.SetExtensionLoadStateAsync(
                extensionId,
                record.RecordVersion,
                ExtensionLoadState.Loaded,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess)
        {
            await CompletePublishTriggerAsync(cancellationToken).ConfigureAwait(false);
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
            return UnsupportedWriteFailure();
        }

        if (!CanManage(extensionId))
        {
            return ValidationWriteFailure();
        }

        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
        {
            return UnsupportedWriteFailure();
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var api = scope.ServiceProvider.GetService<EfHostConfigApi>();
        if (api is null)
        {
            return UnsupportedWriteFailure();
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
            return NotFoundWriteFailure();
        }

        if (record.LoadState == ExtensionLoadState.Disabled)
        {
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
            return ValidationWriteFailure();
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
            return UnsupportedWriteFailure();
        }

        if (!CanManage(extensionId))
        {
            return ValidationWriteFailure();
        }

        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
        {
            return UnsupportedWriteFailure();
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var hostConfig = scope.ServiceProvider.GetService<IHostConfigApi>();
        if (hostConfig is null)
        {
            return UnsupportedWriteFailure();
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
            return NotFoundWriteFailure();
        }

        if (record.LoadState != ExtensionLoadState.Loaded)
        {
            return ValidationWriteFailure();
        }

        var scan = ScanExtensions(cancellationToken);
        if (!scan.Succeeded)
        {
            return FailureWrite(scan.ErrorCode);
        }

        if (!scan.Manifests.TryGetValue(extensionId, out var scannedManifest) ||
            !string.Equals(record.Version, scannedManifest.Version.ToString(), StringComparison.Ordinal))
        {
            return ValidationWriteFailure();
        }

        if (ExtensionCallbackGuard.IsSelfReplacementUnsafe)
        {
            // Reload awaits generation replacement synchronously; from a route/event/scheduler
            // callback the publish may need to drain the calling extension itself (its manifest can
            // be drifted even when the target is another extension), which would deadlock.
            return UnsupportedWriteFailure();
        }

        if (_publisher is null)
        {
            return UnsupportedWriteFailure();
        }

        var publication = await _publisher
            .RequestExtensionReloadAsync(snapshot, extensionId, cancellationToken)
            .ConfigureAwait(false);
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
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<ConfigurationWriteResult> DeleteRecordAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
    {
        if (ExtensionCallbackGuard.IsLifecycleActive)
        {
            return UnsupportedWriteFailure();
        }

        if (!CanManage(extensionId))
        {
            return ValidationWriteFailure();
        }

        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
        {
            return UnsupportedWriteFailure();
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var api = scope.ServiceProvider.GetService<EfHostConfigApi>();
        if (api is null)
        {
            return UnsupportedWriteFailure();
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
            return NotFoundWriteFailure();
        }

        var scan = ScanExtensions(cancellationToken);
        if (!scan.Succeeded)
        {
            return FailureWrite(scan.ErrorCode);
        }

        if (scan.Manifests.ContainsKey(extensionId) || scan.DuplicateIds.Contains(extensionId))
        {
            return ValidationWriteFailure();
        }

        if (scan.HasUnreadableDirectories)
        {
            // Unreadable directories make the absence check unreliable; refuse the delete.
            return FailureWrite(ConfigurationErrorCode.StorageUnavailable);
        }

        // Deleting is rejected while a loaded extension declares a dependency on the target.
        if (HasLoadedDependent(snapshot, scan, extensionId))
        {
            return ValidationWriteFailure();
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

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<ConfigurationReadResult<ExtensionRefreshSummary>> RequestRefreshAsync(
        CancellationToken cancellationToken = default)
    {
        if (ExtensionCallbackGuard.IsLifecycleActive)
        {
            return ConfigurationReadResult<ExtensionRefreshSummary>.Failure(
                new ConfigurationError(ConfigurationErrorCode.Unsupported));
        }

        if (!_runtimeState.ExtensionConfigurationWritesAllowed)
        {
            return ConfigurationReadResult<ExtensionRefreshSummary>.Failure(
                new ConfigurationError(ConfigurationErrorCode.Unsupported));
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var api = scope.ServiceProvider.GetService<EfHostConfigApi>();
        if (api is null)
        {
            return ConfigurationReadResult<ExtensionRefreshSummary>.Failure(
                new ConfigurationError(ConfigurationErrorCode.Unsupported));
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
                    recordVersion: 0))
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
            if (!records.TryGetValue(pair.Key, out var record) ||
                string.Equals(record.Version, pair.Value.Version.ToString(), StringComparison.Ordinal))
            {
                continue;
            }

            var updated = await api.UpdateExtensionInstalledVersionAsync(
                    pair.Key,
                    record.RecordVersion,
                    pair.Value.Version.ToString(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!updated.IsSuccess)
            {
                return ConfigurationReadResult<ExtensionRefreshSummary>.Failure(
                    updated.Errors.ToArray());
            }

            versionUpdated.Add(pair.Key);
        }

        // Refresh may bump the caller's own installed version, so it always counts as self-affecting.
        await CompletePublishTriggerAsync(cancellationToken).ConfigureAwait(false);

        var missing = records.Keys
            .Where(id => !scan.Manifests.ContainsKey(id))
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToImmutableArray();
        return ConfigurationReadResult<ExtensionRefreshSummary>.Success(
            new ExtensionRefreshSummary(
                added.ToImmutableArray(),
                versionUpdated.OrderBy(static id => id, StringComparer.Ordinal).ToImmutableArray(),
                missing));
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
            catch
            {
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
            catch
            {
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

    private static ExtensionScanResult ScanExtensions(CancellationToken cancellationToken)
    {
        var manifests = new Dictionary<string, ExtensionManifest>(StringComparer.Ordinal);
        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
        var hasUnreadableDirectories = false;
        var installRoot = Path.Combine(AppContext.BaseDirectory, "extensions");
        if (!Directory.Exists(installRoot))
        {
            return ExtensionScanResult.Success(manifests, duplicateIds, hasUnreadableDirectories);
        }

        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(installRoot)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return ExtensionScanResult.Failure(ConfigurationErrorCode.StorageUnavailable);
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ManifestDiscoveryResult discovered;
            try
            {
                discovered = ExtensionManifestDiscovery.Discover(directory);
            }
            catch
            {
                hasUnreadableDirectories = true;
                continue;
            }

            if (!discovered.Succeeded || discovered.Manifest is not { } manifest)
            {
                hasUnreadableDirectories = true;
                continue;
            }

            if (duplicateIds.Contains(manifest.Id))
            {
                continue;
            }

            if (!manifests.TryAdd(manifest.Id, manifest))
            {
                manifests.Remove(manifest.Id);
                duplicateIds.Add(manifest.Id);
            }
        }

        return ExtensionScanResult.Success(manifests, duplicateIds, hasUnreadableDirectories);
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

    private sealed record ExtensionScanResult(
        bool Succeeded,
        ConfigurationErrorCode ErrorCode,
        ImmutableDictionary<string, ExtensionManifest> Manifests,
        ImmutableHashSet<string> DuplicateIds,
        bool HasUnreadableDirectories)
    {
        internal static ExtensionScanResult Success(
            Dictionary<string, ExtensionManifest> manifests,
            HashSet<string> duplicateIds,
            bool hasUnreadableDirectories) =>
            new(
                true,
                ConfigurationErrorCode.Validation,
                manifests.ToImmutableDictionary(StringComparer.Ordinal),
                duplicateIds.ToImmutableHashSet(StringComparer.Ordinal),
                hasUnreadableDirectories);

        internal static ExtensionScanResult Failure(ConfigurationErrorCode errorCode) =>
            new(
                false,
                errorCode,
                ImmutableDictionary<string, ExtensionManifest>.Empty,
                ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal),
                false);
    }
}
