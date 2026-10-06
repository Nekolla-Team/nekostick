using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Persistence;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

/// <summary>Exercises the PostgreSQL-backed extension capability facades and owner boundary.</summary>
[Collection(nameof(PostgresIntegrationDefinition))]
public sealed class PostgresExtensionCapabilityIntegrationTests
{
    private const string OwnerExtensionId = "fixture-extension";
    private const string ForeignExtensionId = "foreign-extension";

    private static readonly Guid HostServiceId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000101");

    private static readonly Guid HostRouteId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000102");

    private static readonly Guid OwnerServiceId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000103");

    private static readonly Guid OwnerRouteId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000104");

    private static readonly Guid OwnerHandlerRouteId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000105");

    private static readonly Guid ForeignServiceId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000106");
    private static readonly Guid OwnerEndpointGenerationId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000121");

    private static readonly Guid ForeignEndpointGenerationId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000122");

    private static readonly Guid HostEndpointGenerationId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000123");

    private static readonly Guid ExpiredEndpointGenerationId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000124");

    private static readonly Guid UpdatedOwnerEndpointGenerationId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000125");

    private static readonly Guid UpdatedForeignEndpointGenerationId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000126");

    private static readonly Guid UpdatedHostEndpointGenerationId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000127");

    private static readonly Guid ForeignRouteId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000107");

    private static readonly Guid AtomicCandidateServiceId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000108");
    private static readonly Guid ForeignFullServiceId =
        Guid.Parse("018f0f00-0000-7000-8000-00000000010d");

    private static readonly Guid ForeignFullRouteId =
        Guid.Parse("018f0f00-0000-7000-8000-00000000010e");

    private static readonly Guid AtomicFullCandidateServiceId =
        Guid.Parse("018f0f00-0000-7000-8000-00000000010f");

    private static readonly Guid MissingFullServiceId =
        Guid.Parse("018f0f00-0000-7000-8000-000000000110");

    private const string InitialJsonbRegressionMetadata = """{"arbitrary":{"label":"host-route","ordered":["alpha","beta"],"optional":null,"largeNumber":9007199254740993.00,"scaledNumber":1.2300e2,"duplicate":"discarded","duplicate":"kept"}}""";

    private const string EquivalentJsonbRegressionMetadata = """
        {
          "arbitrary": {
            "scaledNumber": 123.000,
            "duplicate": "discarded",
            "ordered": ["alpha", "beta"],
            "largeNumber": 900719925474099300e-2,
            "optional": null,
            "label": "host-route",
            "duplicate": "kept"
          }
        }
        """;

    private const string InitialJsonbRegressionSettings = """{"enabled":true,"arbitrary":{"label":"owner-settings","ordered":["alpha","beta"],"optional":null,"largeNumber":9007199254740993.00,"scaledNumber":1.2300e2,"duplicate":"discarded","duplicate":"kept"}}""";

    private const string EquivalentJsonbRegressionSettings = """
        {
          "arbitrary": {
            "scaledNumber": 123.000,
            "duplicate": "discarded",
            "ordered": ["alpha", "beta"],
            "largeNumber": 900719925474099300e-2,
            "optional": null,
            "label": "owner-settings",
            "duplicate": "kept"
          },
          "enabled": true
        }
        """;

    [Fact]
    public async Task FullConfigurationReadsAllCollectionsAndOmittedRowsAreDeleted()
    {
        await using var harness = await ExtensionCapabilityPostgresHarness.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = harness.CreateCapability(OwnerExtensionId, static _ => false);
        var initial = await owner.FullConfiguration.ReadAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);

        var populated = await owner.FullConfiguration.ReplaceAsync(
            initial.Value!.Version,
            CreateFullReplacement(initial.Value!, includeForeign: true),
            cancellationToken);
        var committedVersion = RequireCommittedVersion(populated);

        var complete = await owner.FullConfiguration.ReadAsync(cancellationToken);
        Assert.True(complete.IsSuccess, complete.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(complete.Value);
        Assert.Equal(committedVersion, complete.Value!.Version);
        Assert.Contains(ForeignFullRouteId, complete.Value.Routes.Select(value => value.Id));
        var foreignService = Assert.Single(
            complete.Value.Services,
            value => value.Id == ForeignFullServiceId);
        Assert.Equal("full-read-secret", foreignService.Environment["FULL_CONFIGURATION_SECRET"]);
        var foreignRoute = Assert.Single(
            complete.Value.Routes,
            value => value.Id == ForeignFullRouteId);
        using (var metadata = JsonDocument.Parse(foreignRoute.MetadataJson))
        {
            Assert.Equal("foreign", metadata.RootElement.GetProperty("owner").GetString());
            Assert.True(metadata.RootElement.GetProperty("sensitive").GetBoolean());
        }

        Assert.Equal(
            [OwnerExtensionId, ForeignExtensionId],
            complete.Value.ExtensionRecords.Select(value => value.ExtensionId).OrderBy(value => value));
        Assert.Equal(
            [OwnerExtensionId, ForeignExtensionId],
            complete.Value.ExtensionSettings.Select(value => value.ExtensionId).OrderBy(value => value));
        var foreignSettings = Assert.Single(
            complete.Value.ExtensionSettings,
            value => value.ExtensionId == ForeignExtensionId);
        using (var settings = JsonDocument.Parse(foreignSettings.SettingsJson))
        {
            Assert.Equal("foreign-secret", settings.RootElement.GetProperty("token").GetString());
        }

        var ownerScoped = await owner.ConfigurationApi.ReadAsync(cancellationToken);
        Assert.True(ownerScoped.IsSuccess, ownerScoped.Errors.FirstOrDefault()?.Message);
        Assert.Empty(ownerScoped.Value!.Routes);
        Assert.Empty(ownerScoped.Value.Services);
        Assert.Equal(OwnerExtensionId, ownerScoped.Value.Settings!.ExtensionId);

        var omitted = await owner.FullConfiguration.ReplaceAsync(
            complete.Value!.Version,
            CreateFullReplacement(complete.Value!, includeForeign: false),
            cancellationToken);
        RequireCommittedVersion(omitted);
        var afterOmission = await owner.FullConfiguration.ReadAsync(cancellationToken);
        Assert.True(afterOmission.IsSuccess, afterOmission.Errors.FirstOrDefault()?.Message);
        Assert.DoesNotContain(
            ForeignFullServiceId,
            afterOmission.Value!.Services.Select(value => value.Id));
        Assert.DoesNotContain(
            ForeignFullRouteId,
            afterOmission.Value.Routes.Select(value => value.Id));
        Assert.DoesNotContain(
            ForeignExtensionId,
            afterOmission.Value.ExtensionRecords.Select(value => value.ExtensionId));
        Assert.DoesNotContain(
            ForeignExtensionId,
            afterOmission.Value.ExtensionSettings.Select(value => value.ExtensionId));
    }

    [Fact]
    public async Task FullConfigurationJsonbSemanticNoOpsPreserveVersionsAndAvoidServiceRestart()
    {
        await using var harness = await ExtensionCapabilityPostgresHarness.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = harness.CreateCapability(OwnerExtensionId, static _ => false);
        var initialResult = await owner.FullConfiguration.ReadAsync(cancellationToken);
        Assert.True(initialResult.IsSuccess, initialResult.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initialResult.Value);
        var initial = initialResult.Value!;

        var configured = await owner.FullConfiguration.ReplaceAsync(
            initial.Version,
            CreateJsonbRegressionSetup(initial),
            cancellationToken);
        var configuredVersion = RequireCommittedVersion(configured);
        var configuredRead = await owner.FullConfiguration.ReadAsync(cancellationToken);
        Assert.True(configuredRead.IsSuccess, configuredRead.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(configuredRead.Value);
        var baseline = configuredRead.Value!;
        Assert.Equal(configuredVersion, baseline.Version);

        var baselineRoute = Assert.Single(baseline.Routes, value => value.Id == HostRouteId);
        var baselineSettings = Assert.Single(
            baseline.ExtensionSettings,
            value => value.ExtensionId == OwnerExtensionId);
        Assert.Contains("kept", baselineRoute.MetadataJson);
        Assert.DoesNotContain("discarded", baselineRoute.MetadataJson);
        Assert.Contains("kept", baselineSettings.SettingsJson);
        Assert.DoesNotContain("discarded", baselineSettings.SettingsJson);
        Assert.NotEqual(EquivalentJsonbRegressionMetadata, baselineRoute.MetadataJson);
        Assert.NotEqual(EquivalentJsonbRegressionSettings, baselineSettings.SettingsJson);
        Assert.Equal(["10.0.0.0/8", "192.168.0.0/16"], baseline.GlobalSettings.TrustedProxyCidrs);
        Assert.Equal(["api.example.test", "admin.example.test"], baselineRoute.Matcher.HostPatterns);
        Assert.Equal(["GET", "POST"], baselineRoute.Matcher.Methods);
        Assert.Single(baselineRoute.RequestHeaderRewrites);
        Assert.Single(baselineRoute.ResponseHeaderRewrites);
        var baselineService = Assert.Single(baseline.Services, value => value.Id == HostServiceId);
        Assert.Equal(["--host", "--integration"], baselineService.ArgumentList);
        Assert.Equal("stable-value", baselineService.Environment["FULL_CONFIGURATION_KEEP"]);

        await harness.PublishAndReconcileAsync(baseline, cancellationToken);
        var processInstance = Assert.Single(harness.StartedProcessInstances);
        Assert.Equal([HostServiceId], harness.StartedServiceIds);
        var initialEndpoints = harness.EndpointPublisher.Current;
        Assert.Equal([HostServiceId], initialEndpoints.Keys);
        var endpoint = initialEndpoints[HostServiceId];
        var lease = Assert.IsType<PortLease>(harness.GetCurrentLease(HostServiceId));
        Assert.Equal(endpoint.Port, lease.Port);
        AssertHostServiceLifecycleUnchanged(harness, processInstance, endpoint, lease);

        await using var notificationListener = harness.CreateConnection();
        await notificationListener.OpenAsync(cancellationToken);
        await using (var listenCommand = new NpgsqlCommand(
                         "LISTEN nekostick_config_changed;",
                         notificationListener))
        {
            await listenCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var notifications = new ConcurrentQueue<NpgsqlNotificationEventArgs>();
        void OnNotification(object? _, NpgsqlNotificationEventArgs args) => notifications.Enqueue(args);
        notificationListener.Notification += OnNotification;
        using var notificationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        notificationTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var noOp = await owner.FullConfiguration.ReplaceAsync(
                baseline.Version,
                ReplaceJsonbValues(
                    baseline,
                    hostRouteMetadataJson: EquivalentJsonbRegressionMetadata,
                    ownerSettingsJson: EquivalentJsonbRegressionSettings),
                cancellationToken);
            Assert.Equal(baseline.Version, RequireCommittedVersion(noOp));

            var noOpRead = await owner.FullConfiguration.ReadAsync(cancellationToken);
            Assert.True(noOpRead.IsSuccess, noOpRead.Errors.FirstOrDefault()?.Message);
            Assert.NotNull(noOpRead.Value);
            var afterNoOp = noOpRead.Value!;
            AssertFullSnapshotSemanticNoOp(baseline, afterNoOp);
            Assert.Equal(baselineRoute.MetadataJson, Assert.Single(
                afterNoOp.Routes,
                value => value.Id == HostRouteId).MetadataJson);
            Assert.Equal(baselineSettings.SettingsJson, Assert.Single(
                afterNoOp.ExtensionSettings,
                value => value.ExtensionId == OwnerExtensionId).SettingsJson);
            var noOpReadiness = await harness.LifecycleManager.EnsureReadyAsync(
                afterNoOp,
                HostServiceId,
                cancellationToken);
            Assert.Equal(HostServiceReadinessStatus.Ready, noOpReadiness.Status);
            AssertHostServiceLifecycleUnchanged(harness, processInstance, endpoint, lease);

            var routeBefore = Assert.Single(afterNoOp.Routes, value => value.Id == HostRouteId);
            var reorderedMetadata = ReorderJsonbRegressionArray(routeBefore.MetadataJson);
            Assert.NotEqual(routeBefore.MetadataJson, reorderedMetadata);
            var reorderedRouteWrite = await owner.FullConfiguration.ReplaceAsync(
                afterNoOp.Version,
                ReplaceJsonbValues(afterNoOp, hostRouteMetadataJson: reorderedMetadata),
                cancellationToken);
            Assert.Equal(checked(afterNoOp.Version + 1), RequireCommittedVersion(reorderedRouteWrite));
            var reorderedRouteRead = await owner.FullConfiguration.ReadAsync(cancellationToken);
            Assert.True(reorderedRouteRead.IsSuccess, reorderedRouteRead.Errors.FirstOrDefault()?.Message);
            Assert.NotNull(reorderedRouteRead.Value);
            var afterArrayReorder = reorderedRouteRead.Value!;
            AssertOnlyHostRouteChanged(afterNoOp, afterArrayReorder, HostRouteId);
            using (var metadata = JsonDocument.Parse(
                       Assert.Single(afterArrayReorder.Routes, value => value.Id == HostRouteId).MetadataJson))
            {
                var arbitrary = metadata.RootElement.GetProperty("arbitrary");
                Assert.Equal(
                    ["beta", "alpha"],
                    arbitrary.GetProperty("ordered").EnumerateArray()
                        .Select(value => value.GetString()!)
                        .ToArray());
                Assert.Equal(JsonValueKind.Null, arbitrary.GetProperty("optional").ValueKind);
            }
            await AssertConfigurationNotificationAsync(
                notificationListener,
                notifications,
                afterArrayReorder.Version,
                notificationTimeout.Token);
            await harness.PublishAndReconcileAsync(afterArrayReorder, cancellationToken);
            AssertHostServiceLifecycleUnchanged(harness, processInstance, endpoint, lease);

            var settingsBefore = Assert.Single(
                afterArrayReorder.ExtensionSettings,
                value => value.ExtensionId == OwnerExtensionId);
            var settingsWithoutNull = RemoveJsonbRegressionOptionalProperty(settingsBefore.SettingsJson);
            var missingNullWrite = await owner.FullConfiguration.ReplaceAsync(
                afterArrayReorder.Version,
                ReplaceJsonbValues(afterArrayReorder, ownerSettingsJson: settingsWithoutNull),
                cancellationToken);
            Assert.Equal(checked(afterArrayReorder.Version + 1), RequireCommittedVersion(missingNullWrite));
            var missingNullRead = await owner.FullConfiguration.ReadAsync(cancellationToken);
            Assert.True(missingNullRead.IsSuccess, missingNullRead.Errors.FirstOrDefault()?.Message);
            Assert.NotNull(missingNullRead.Value);
            var afterMissingNull = missingNullRead.Value!;
            AssertOnlyOwnerSettingsChanged(afterArrayReorder, afterMissingNull, OwnerExtensionId);
            using (var settings = JsonDocument.Parse(
                       Assert.Single(afterMissingNull.ExtensionSettings,
                           value => value.ExtensionId == OwnerExtensionId).SettingsJson))
            {
                Assert.False(settings.RootElement.GetProperty("arbitrary")
                    .TryGetProperty("optional", out _));
            }
            await AssertConfigurationNotificationAsync(
                notificationListener,
                notifications,
                afterMissingNull.Version,
                notificationTimeout.Token);
            await harness.PublishAndReconcileAsync(afterMissingNull, cancellationToken);
            AssertHostServiceLifecycleUnchanged(harness, processInstance, endpoint, lease);

            var settingsWithPreciseNumber = ReplaceJsonbRegressionLargeNumber(
                Assert.Single(afterMissingNull.ExtensionSettings,
                    value => value.ExtensionId == OwnerExtensionId).SettingsJson,
                9007199254740992L);
            var preciseNumberWrite = await owner.FullConfiguration.ReplaceAsync(
                afterMissingNull.Version,
                ReplaceJsonbValues(afterMissingNull, ownerSettingsJson: settingsWithPreciseNumber),
                cancellationToken);
            Assert.Equal(checked(afterMissingNull.Version + 1), RequireCommittedVersion(preciseNumberWrite));
            var preciseNumberRead = await owner.FullConfiguration.ReadAsync(cancellationToken);
            Assert.True(preciseNumberRead.IsSuccess, preciseNumberRead.Errors.FirstOrDefault()?.Message);
            Assert.NotNull(preciseNumberRead.Value);
            var afterPreciseNumberChange = preciseNumberRead.Value!;
            AssertOnlyOwnerSettingsChanged(afterMissingNull, afterPreciseNumberChange, OwnerExtensionId);
            using (var settings = JsonDocument.Parse(
                       Assert.Single(afterPreciseNumberChange.ExtensionSettings,
                           value => value.ExtensionId == OwnerExtensionId).SettingsJson))
            {
                Assert.Equal(
                    9007199254740992L,
                    settings.RootElement.GetProperty("arbitrary").GetProperty("largeNumber").GetInt64());
            }
            await AssertConfigurationNotificationAsync(
                notificationListener,
                notifications,
                afterPreciseNumberChange.Version,
                notificationTimeout.Token);
            await harness.PublishAndReconcileAsync(afterPreciseNumberChange, cancellationToken);
            AssertHostServiceLifecycleUnchanged(harness, processInstance, endpoint, lease);

            var settingsWithFalseToken = SetJsonbRegressionEnabledToken(
                Assert.Single(afterPreciseNumberChange.ExtensionSettings,
                    value => value.ExtensionId == OwnerExtensionId).SettingsJson,
                false);
            var falseTokenWrite = await owner.FullConfiguration.ReplaceAsync(
                afterPreciseNumberChange.Version,
                ReplaceJsonbValues(afterPreciseNumberChange, ownerSettingsJson: settingsWithFalseToken),
                cancellationToken);
            Assert.Equal(
                checked(afterPreciseNumberChange.Version + 1),
                RequireCommittedVersion(falseTokenWrite));
            var falseTokenRead = await owner.FullConfiguration.ReadAsync(cancellationToken);
            Assert.True(falseTokenRead.IsSuccess, falseTokenRead.Errors.FirstOrDefault()?.Message);
            Assert.NotNull(falseTokenRead.Value);
            var afterFalseTokenChange = falseTokenRead.Value!;
            AssertOnlyOwnerSettingsChanged(afterPreciseNumberChange, afterFalseTokenChange, OwnerExtensionId);
            using (var settings = JsonDocument.Parse(
                       Assert.Single(afterFalseTokenChange.ExtensionSettings,
                           value => value.ExtensionId == OwnerExtensionId).SettingsJson))
            {
                Assert.False(settings.RootElement.GetProperty("enabled").GetBoolean());
            }
            await AssertConfigurationNotificationAsync(
                notificationListener,
                notifications,
                afterFalseTokenChange.Version,
                notificationTimeout.Token);
            await harness.PublishAndReconcileAsync(afterFalseTokenChange, cancellationToken);
            AssertHostServiceLifecycleUnchanged(harness, processInstance, endpoint, lease);

            using var noExtraNotificationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            noExtraNotificationTimeout.CancelAfter(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await WaitForConfigurationNotificationAsync(
                    notificationListener,
                    notifications,
                    noExtraNotificationTimeout.Token));
            Assert.Empty(notifications);
        }
        finally
        {
            notificationListener.Notification -= OnNotification;
        }
    }


    [Fact]
    public async Task FullConfigurationRejectsStaleGlobalAndEntityVersionsWithoutPartialMutation()
    {
        await using var harness = await ExtensionCapabilityPostgresHarness.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = harness.CreateCapability(OwnerExtensionId, static _ => false);
        var initial = await owner.FullConfiguration.ReadAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);
        var before = initial.Value!;
        Assert.True(before.Version > 0);

        var staleGlobal = await owner.FullConfiguration.ReplaceAsync(
            before.Version - 1,
            new ConfigurationChangeSet(
                CreateChangedGlobalSettings(before.GlobalSettings),
                before.Routes,
                before.Services,
                before.ExtensionRecords,
                before.ExtensionSettings),
            cancellationToken);
        AssertConfigurationError(staleGlobal, ConfigurationErrorCode.ConcurrencyConflict);

        var afterGlobalConflict = await owner.FullConfiguration.ReadAsync(cancellationToken);
        Assert.True(afterGlobalConflict.IsSuccess, afterGlobalConflict.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(afterGlobalConflict.Value);
        AssertSnapshotCollectionsUnchanged(before, afterGlobalConflict.Value!);
        Assert.Equal(
            before.GlobalSettings.MaxConcurrentRequests,
            afterGlobalConflict.Value!.GlobalSettings.MaxConcurrentRequests);

        var completeAfterGlobalConflict = afterGlobalConflict.Value!;
        var existingService = Assert.Single(completeAfterGlobalConflict.Services);
        var staleService = CopyService(existingService, existingService.Version + 1);
        var staleEntity = await owner.FullConfiguration.ReplaceAsync(
            completeAfterGlobalConflict.Version,
            new ConfigurationChangeSet(
                completeAfterGlobalConflict.GlobalSettings,
                completeAfterGlobalConflict.Routes,
                completeAfterGlobalConflict.Services
                    .Select(value => value.Id == existingService.Id ? staleService : value)
                    .Append(CreateFullService(AtomicFullCandidateServiceId, "candidate-secret"))
                    .ToImmutableArray(),
                completeAfterGlobalConflict.ExtensionRecords,
                completeAfterGlobalConflict.ExtensionSettings),
            cancellationToken);
        AssertConfigurationError(staleEntity, ConfigurationErrorCode.ConcurrencyConflict);
        var afterEntityConflict = await owner.FullConfiguration.ReadAsync(cancellationToken);
        Assert.True(afterEntityConflict.IsSuccess, afterEntityConflict.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(afterEntityConflict.Value);
        AssertSnapshotCollectionsUnchanged(before, afterEntityConflict.Value!);
        Assert.DoesNotContain(
            AtomicFullCandidateServiceId,
            afterEntityConflict.Value!.Services.Select(value => value.Id));
    }

    [Fact]
    public async Task WriteSnapshotReturnsSafeEnvironmentErrorsWithoutMutatingConfiguration()
    {
        await using var harness = await ExtensionCapabilityPostgresHarness.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = harness.Database.CreateContext();
        await using var api = new EfHostConfigApi(context);
        var beforeResult = await api.ReadSnapshotAsync(cancellationToken);
        Assert.True(beforeResult.IsSuccess, beforeResult.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(beforeResult.Value);
        var before = beforeResult.Value!;

        const string secretSentinel = "public-write-environment-secret-sentinel";
        var firstInvalidServiceId = Guid.Parse("018f0f00-0000-7000-8000-000000000112");
        var secondInvalidServiceId = Guid.Parse("018f0f00-0000-7000-8000-000000000113");
        var firstInvalidService = CreateFullService(
            firstInvalidServiceId,
            ImmutableDictionary<string, string>.Empty
                .Add("SAFE_TEMPLATE", secretSentinel + "-${BROKEN")
                .Add("BAD\nKEY", secretSentinel));
        var secondInvalidService = CreateFullService(
            secondInvalidServiceId,
            ImmutableDictionary<string, string>.Empty
                .Add(
                    "SAFE_LONG",
                    secretSentinel + new string('V', (64 * 1024) + 1))
                .Add("BAD=VALUEKEY", secretSentinel + "-${BROKEN"));
        var changes = new ConfigurationChangeSet(
            before.GlobalSettings,
            before.Routes,
            before.Services.Add(firstInvalidService).Add(secondInvalidService),
            before.ExtensionRecords,
            before.ExtensionSettings);

        var rejected = await api.WriteSnapshotAsync(before.Version, changes, cancellationToken);

        Assert.False(rejected.IsSuccess);
        Assert.Null(rejected.NewVersion);
        Assert.Equal(5, rejected.Errors.Length);
        Assert.All(rejected.Errors, error => Assert.Equal(ConfigurationErrorCode.Validation, error.Code));
        var messages = rejected.Errors.Select(error => error.Message).ToArray();
        var allMessages = string.Join(Environment.NewLine, messages);
        Assert.Contains(
            messages,
            message => message.Contains("Environment value for key 'SAFE_TEMPLATE'", StringComparison.Ordinal));
        Assert.Contains(
            messages,
            message => message.Contains("Environment value for key 'SAFE_LONG'", StringComparison.Ordinal));
        Assert.DoesNotContain(secretSentinel, allMessages);
        Assert.Contains(
            messages,
            message => message.Contains("Environment key 'BAD=VALUEKEY'", StringComparison.Ordinal) &&
                message.Contains("contains '='", StringComparison.Ordinal));
        Assert.Contains(
            messages,
            message => message.Contains("Environment value for key 'BAD=VALUEKEY'", StringComparison.Ordinal) &&
                message.Contains("malformed template placeholder", StringComparison.Ordinal));
        Assert.DoesNotContain("BAD\nKEY", allMessages);
        Assert.All(messages, message => Assert.DoesNotContain("\n", message));

        await using var afterContext = harness.Database.CreateContext();
        await using var afterApi = new EfHostConfigApi(afterContext);
        var afterResult = await afterApi.ReadSnapshotAsync(cancellationToken);
        Assert.True(afterResult.IsSuccess, afterResult.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(afterResult.Value);
        var after = afterResult.Value!;
        AssertSnapshotCollectionsUnchanged(before, after);
        Assert.Equal(JsonSerializer.Serialize(before.GlobalSettings), JsonSerializer.Serialize(after.GlobalSettings));
    }

    [Fact]
    public async Task FullConfigurationRollsBackInvalidCrossCategoryReplacement()
    {
        await using var harness = await ExtensionCapabilityPostgresHarness.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = harness.CreateCapability(OwnerExtensionId, static _ => false);
        var initial = await owner.FullConfiguration.ReadAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);
        var before = initial.Value!;
        var candidateService = CreateFullService(AtomicFullCandidateServiceId, "rollback-secret");
        var invalidRoute = CreateFullRoute(
            Guid.Parse("018f0f00-0000-7000-8000-000000000111"),
            MissingFullServiceId,
            "rollback-route");

        var rejected = await owner.FullConfiguration.ReplaceAsync(
            before.Version,
            new ConfigurationChangeSet(
                before.GlobalSettings,
                before.Routes.Add(invalidRoute),
                before.Services.Add(candidateService),
                before.ExtensionRecords,
                before.ExtensionSettings),
            cancellationToken);
        AssertConfigurationError(rejected, ConfigurationErrorCode.Validation);

        var after = await owner.FullConfiguration.ReadAsync(cancellationToken);
        Assert.True(after.IsSuccess, after.Errors.FirstOrDefault()?.Message);
        AssertSnapshotCollectionsUnchanged(before, after.Value!);
        Assert.DoesNotContain(
            AtomicFullCandidateServiceId,
            after.Value!.Services.Select(value => value.Id));
    }

    [Fact]
    public async Task OwnedReadsStampOwnersAndRejectForeignOrHostMutations()
    {
        await using var harness = await ExtensionCapabilityPostgresHarness.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = harness.CreateCapability(OwnerExtensionId, handlerId => handlerId == "owned-handler");
        var foreign = harness.CreateCapability(ForeignExtensionId, static _ => false);

        var initial = await owner.ConfigurationApi.ReadAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);
        Assert.Empty(initial.Value!.Routes);
        Assert.Empty(initial.Value!.Services);
        Assert.Equal(OwnerExtensionId, initial.Value!.Settings?.ExtensionId);

        var expectedVersion = initial.Value!.Version;
        var foreignServiceWrite = await foreign.Services.UpsertAsync(
            expectedVersion,
            CreateExtensionService(ForeignServiceId),
            cancellationToken);
        expectedVersion = RequireCommittedVersion(foreignServiceWrite);

        var foreignRouteWrite = await foreign.Routes.UpsertAsync(
            expectedVersion,
            CreateExtensionRoute(
                ForeignRouteId,
                new ExtensionServiceRouteTarget(ForeignServiceId)),
            cancellationToken);
        expectedVersion = RequireCommittedVersion(foreignRouteWrite);

        var ownerServiceWrite = await owner.Services.UpsertAsync(
            expectedVersion,
            CreateExtensionService(OwnerServiceId),
            cancellationToken);
        expectedVersion = RequireCommittedVersion(ownerServiceWrite);

        var ownerRouteWrite = await owner.Routes.UpsertAsync(
            expectedVersion,
            CreateExtensionRoute(
                OwnerRouteId,
                new ExtensionServiceRouteTarget(OwnerServiceId)),
            cancellationToken);
        expectedVersion = RequireCommittedVersion(ownerRouteWrite);

        var ownerHandlerRouteWrite = await owner.Routes.UpsertAsync(
            expectedVersion,
            CreateExtensionRoute(
                OwnerHandlerRouteId,
                new ExtensionHandlerRouteTarget("owned-handler")),
            cancellationToken);
        expectedVersion = RequireCommittedVersion(ownerHandlerRouteWrite);

        var owned = await owner.ConfigurationApi.ReadAsync(cancellationToken);
        Assert.True(owned.IsSuccess, owned.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(owned.Value);
        Assert.Equal(expectedVersion, owned.Value!.Version);
        Assert.Equal(
            [OwnerRouteId, OwnerHandlerRouteId],
            owned.Value!.Routes.Select(value => value.Id).OrderBy(value => value));
        Assert.Equal([OwnerServiceId], owned.Value!.Services.Select(value => value.Id));
        Assert.DoesNotContain(HostRouteId, owned.Value!.Routes.Select(value => value.Id));
        Assert.DoesNotContain(HostServiceId, owned.Value!.Services.Select(value => value.Id));
        Assert.DoesNotContain(ForeignRouteId, owned.Value!.Routes.Select(value => value.Id));
        Assert.DoesNotContain(ForeignServiceId, owned.Value!.Services.Select(value => value.Id));

        var foreignRouteRemoval = await owner.Routes.RemoveAsync(
            expectedVersion,
            ForeignRouteId,
            cancellationToken);
        AssertConfigurationError(foreignRouteRemoval, ConfigurationErrorCode.NotFound);

        var hostServiceRemoval = await owner.Services.RemoveAsync(
            expectedVersion,
            HostServiceId,
            cancellationToken);
        AssertConfigurationError(hostServiceRemoval, ConfigurationErrorCode.NotFound);

        var foreignServiceUpsert = await owner.Services.UpsertAsync(
            expectedVersion,
            CreateExtensionService(ForeignServiceId),
            cancellationToken);
        AssertConfigurationError(foreignServiceUpsert, ConfigurationErrorCode.Validation);

        var foreignServiceTarget = await owner.Routes.UpsertAsync(
            expectedVersion,
            CreateExtensionRoute(
                Guid.Parse("018f0f00-0000-7000-8000-000000000109"),
                new ExtensionServiceRouteTarget(ForeignServiceId)),
            cancellationToken);
        AssertConfigurationError(foreignServiceTarget, ConfigurationErrorCode.NotFound);

        var foreignHandlerTarget = await owner.Routes.UpsertAsync(
            expectedVersion,
            CreateExtensionRoute(
                Guid.Parse("018f0f00-0000-7000-8000-00000000010a"),
                new ExtensionHandlerRouteTarget("foreign-handler")),
            cancellationToken);
        AssertConfigurationError(foreignHandlerTarget, ConfigurationErrorCode.Validation);

        await using var context = harness.Database.CreateContext();
        var hostRoute = await context.Routes.AsNoTracking().SingleAsync(
            value => value.Id == HostRouteId,
            cancellationToken);
        var hostService = await context.Services.AsNoTracking().SingleAsync(
            value => value.Id == HostServiceId,
            cancellationToken);
        var ownerRoute = await context.Routes.AsNoTracking().SingleAsync(
            value => value.Id == OwnerRouteId,
            cancellationToken);
        var ownerService = await context.Services.AsNoTracking().SingleAsync(
            value => value.Id == OwnerServiceId,
            cancellationToken);
        var foreignRoute = await context.Routes.AsNoTracking().SingleAsync(
            value => value.Id == ForeignRouteId,
            cancellationToken);
        var foreignService = await context.Services.AsNoTracking().SingleAsync(
            value => value.Id == ForeignServiceId,
            cancellationToken);

        Assert.Null(hostRoute.OwnerExtensionId);
        Assert.Null(hostService.OwnerExtensionId);
        Assert.Equal(OwnerExtensionId, ownerRoute.OwnerExtensionId);
        Assert.Equal(OwnerExtensionId, ownerService.OwnerExtensionId);
        Assert.Equal(ForeignExtensionId, foreignRoute.OwnerExtensionId);
        Assert.Equal(ForeignExtensionId, foreignService.OwnerExtensionId);

        var global = await owner.FullConfiguration.ReadAsync(cancellationToken);
        Assert.True(global.IsSuccess, global.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(global.Value);
        Assert.Null(global.Value!.Routes.Single(value => value.Id == HostRouteId).OwnerExtensionId);
        Assert.Equal(
            OwnerExtensionId,
            global.Value.Routes.Single(value => value.Id == OwnerRouteId).OwnerExtensionId);
        Assert.Equal(
            ForeignExtensionId,
            global.Value.Routes.Single(value => value.Id == ForeignRouteId).OwnerExtensionId);
    }

    [Fact]
    public async Task SettingsIdentityIsBoundAndJsonIsNormalizedWhileVersionsConflictSafely()
    {
        await using var harness = await ExtensionCapabilityPostgresHarness.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = harness.CreateCapability(OwnerExtensionId, static _ => false);

        var initial = await owner.ConfigurationApi.ReadSettingsAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(initial.Value);
        var current = initial.Value!;

        var spoof = await owner.ConfigurationApi.WriteSettingsAsync(
            current.Version,
            new ExtensionSettingsConfiguration(
                ForeignExtensionId,
                schemaVersion: 9,
                settingsJson: "{\"spoofed\":true}",
                version: current.Version),
            cancellationToken);
        AssertConfigurationError(spoof, ConfigurationErrorCode.Validation);

        var normalized = new ExtensionSettingsConfiguration(
            OwnerExtensionId,
            schemaVersion: current.SchemaVersion + 1,
            settingsJson: " { \"limit\": 7, \"enabled\": true } ",
            version: current.Version);
        var committed = await owner.ConfigurationApi.WriteSettingsAsync(
            current.Version,
            normalized,
            cancellationToken);
        var committedVersion = RequireCommittedVersion(committed);

        var afterCommit = await owner.ConfigurationApi.ReadSettingsAsync(cancellationToken);
        Assert.True(afterCommit.IsSuccess, afterCommit.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(afterCommit.Value);
        Assert.Equal(OwnerExtensionId, afterCommit.Value!.ExtensionId);
        Assert.Equal(normalized.SchemaVersion, afterCommit.Value!.SchemaVersion);
        using var settingsDocument = JsonDocument.Parse(afterCommit.Value!.SettingsJson);
        Assert.Equal(7, settingsDocument.RootElement.GetProperty("limit").GetInt32());
        Assert.True(settingsDocument.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(committedVersion, afterCommit.Value!.Version);

        var staleSettings = await owner.ConfigurationApi.WriteSettingsAsync(
            current.Version,
            new ExtensionSettingsConfiguration(
                OwnerExtensionId,
                schemaVersion: normalized.SchemaVersion + 1,
                settingsJson: "{\"enabled\":false}",
                version: current.Version),
            cancellationToken);
        AssertConfigurationError(staleSettings, ConfigurationErrorCode.ConcurrencyConflict);

        var staleApply = await owner.ConfigurationApi.ApplyAsync(
            expectedVersion: committedVersion,
            new ExtensionConfigurationChangeSet(
                ImmutableArray<ExtensionRouteConfiguration>.Empty,
                ImmutableArray<Guid>.Empty,
                ImmutableArray<ExtensionServiceConfiguration>.Empty,
                ImmutableArray<Guid>.Empty,
                settings: null),
            cancellationToken);
        AssertConfigurationError(staleApply, ConfigurationErrorCode.ConcurrencyConflict);
    }

    [Fact]
    public async Task MissingSettingsDocumentReturnsNoSettings()
    {
        await using var harness = await ExtensionCapabilityPostgresHarness.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fresh = harness.CreateCapability(
            "capability.no-settings." + Guid.NewGuid().ToString("N"),
            static _ => false);

        var read = await fresh.ConfigurationApi.ReadSettingsAsync(cancellationToken);

        Assert.False(read.IsSuccess);
        Assert.Contains(read.Errors, error => error.Code == ConfigurationErrorCode.NoSettings);
    }

    [Fact]
    public async Task FailedAtomicApplyPreservesUnrelatedRowsAndNullableHostOwnership()
    {
        await using var harness = await ExtensionCapabilityPostgresHarness.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = harness.CreateCapability(OwnerExtensionId, static _ => false);

        var initial = await owner.ConfigurationApi.ReadAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        var expectedVersion = initial.Value!.Version;
        var ownerServiceWrite = await owner.Services.UpsertAsync(
            expectedVersion,
            CreateExtensionService(OwnerServiceId),
            cancellationToken);
        expectedVersion = RequireCommittedVersion(ownerServiceWrite);

        var before = await harness.ReadSnapshotAsync(cancellationToken);
        Assert.True(before.IsSuccess, before.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(before.Value);
        var beforeSnapshot = before.Value!;

        var failed = await owner.ConfigurationApi.ApplyAsync(
            expectedVersion,
            new ExtensionConfigurationChangeSet(
                ImmutableArray<ExtensionRouteConfiguration>.Empty,
                ImmutableArray.Create(HostRouteId),
                ImmutableArray.Create(CreateExtensionService(AtomicCandidateServiceId)),
                ImmutableArray<Guid>.Empty,
                settings: null),
            cancellationToken);
        AssertConfigurationError(failed, ConfigurationErrorCode.NotFound);

        var after = await harness.ReadSnapshotAsync(cancellationToken);
        Assert.True(after.IsSuccess, after.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(after.Value);
        Assert.Equal(beforeSnapshot.Version, after.Value!.Version);
        Assert.Equal(
            beforeSnapshot.Routes.Select(value => value.Id).OrderBy(value => value),
            after.Value!.Routes.Select(value => value.Id).OrderBy(value => value));
        Assert.Equal(
            beforeSnapshot.Services.Select(value => value.Id).OrderBy(value => value),
            after.Value!.Services.Select(value => value.Id).OrderBy(value => value));
        Assert.DoesNotContain(AtomicCandidateServiceId, after.Value!.Services.Select(value => value.Id));

        await using var context = harness.Database.CreateContext();
        var hostRoute = await context.Routes.AsNoTracking().SingleAsync(
            value => value.Id == HostRouteId,
            cancellationToken);
        var ownerService = await context.Services.AsNoTracking().SingleAsync(
            value => value.Id == OwnerServiceId,
            cancellationToken);
        Assert.Null(hostRoute.OwnerExtensionId);
        Assert.Equal(OwnerExtensionId, ownerService.OwnerExtensionId);

        foreach (var table in new[] { "routes", "services" })
        {
            var nullable = await harness.Database.ExecuteScalarAsync<string>(
                "SELECT is_nullable FROM information_schema.columns " +
                "WHERE table_schema = @schema_name AND table_name = @table_name " +
                "AND column_name = 'owner_extension_id';",
                new NpgsqlParameter("schema_name", harness.Database.Schema),
                new NpgsqlParameter("table_name", table));
            Assert.Equal("YES", nullable);
        }
    }

    [Fact]
    public async Task ServiceStartAndOwnerScopedEndpointViewsAreSafeWhileStopRestartRemainUnsupported()
    {
        await using var harness = await ExtensionCapabilityPostgresHarness.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = harness.CreateCapability(OwnerExtensionId, static _ => false);
        var foreign = harness.CreateCapability(ForeignExtensionId, static _ => false);

        var initial = await owner.ConfigurationApi.ReadAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
        var serviceWrite = await owner.Services.UpsertAsync(
            initial.Value!.Version,
            CreateExtensionService(OwnerServiceId),
            cancellationToken);
        var expectedVersion = RequireCommittedVersion(serviceWrite);

        var started = await owner.Services.StartAsync(OwnerServiceId, cancellationToken);
        Assert.True(started.Succeeded);
        Assert.Equal(ExtensionServiceOperationCode.Accepted, started.Code);
        Assert.Equal(OwnerServiceId, started.ServiceId);

        var stop = await owner.Services.StopAsync(OwnerServiceId, cancellationToken);
        Assert.False(stop.Succeeded);
        Assert.Equal(ExtensionServiceOperationCode.Unsupported, stop.Code);
        var restart = await owner.Services.RestartAsync(OwnerServiceId, cancellationToken);
        Assert.False(restart.Succeeded);
        Assert.Equal(ExtensionServiceOperationCode.Unsupported, restart.Code);

        var missing = await owner.Services.StartAsync(
            Guid.Parse("018f0f00-0000-7000-8000-00000000010b"),
            cancellationToken);
        Assert.False(missing.Succeeded);
        Assert.Equal(ExtensionServiceOperationCode.NotFound, missing.Code);

        var expiredServiceId = Guid.Parse("018f0f00-0000-7000-8000-00000000010c");
        const int ownerPort = 21001;
        const int foreignPort = 21002;
        const int hostPort = 21003;
        const int expiredPort = 21004;
        var activeUntil = DateTimeOffset.UtcNow.AddMinutes(1);
        harness.EndpointPublisher.Publish(
        [
            new HostServiceEndpointLease(
                OwnerServiceId,
                OwnerEndpointGenerationId,
                ownerPort,
                activeUntil,
                OwnerExtensionId),
            new HostServiceEndpointLease(
                ForeignServiceId,
                ForeignEndpointGenerationId,
                foreignPort,
                activeUntil,
                ForeignExtensionId),
            new HostServiceEndpointLease(
                HostServiceId,
                HostEndpointGenerationId,
                hostPort,
                activeUntil),
            new HostServiceEndpointLease(
                expiredServiceId,
                ExpiredEndpointGenerationId,
                expiredPort,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                OwnerExtensionId)
        ]);

        var published = harness.EndpointPublisher.Current;
        Assert.Equal(3, published.Count);
        var publishedAt = DateTimeOffset.UtcNow;
        Assert.True(published[OwnerServiceId].IsActive(publishedAt));
        Assert.True(published[ForeignServiceId].IsActive(publishedAt));
        Assert.True(published[HostServiceId].IsActive(publishedAt));
        Assert.Equal(OwnerExtensionId, published[OwnerServiceId].OwnerExtensionId);
        Assert.Equal(OwnerEndpointGenerationId, published[OwnerServiceId].GenerationId);
        Assert.Equal(ForeignEndpointGenerationId, published[ForeignServiceId].GenerationId);
        Assert.Equal(HostEndpointGenerationId, published[HostServiceId].GenerationId);
        Assert.Equal(ForeignExtensionId, published[ForeignServiceId].OwnerExtensionId);
        Assert.Null(published[HostServiceId].OwnerExtensionId);
        Assert.DoesNotContain(expiredServiceId, published.Keys);

        var ownerSnapshot = owner.Endpoints.Current;
        Assert.Single(ownerSnapshot);
        Assert.Equal(OwnerServiceId, ownerSnapshot[0].ServiceId);
        Assert.Equal(ownerPort, ownerSnapshot[0].Port);
        Assert.Equal(OwnerEndpointGenerationId, ownerSnapshot[0].GenerationId);
        var ownerResolution = Assert.IsType<ExtensionEndpointResolutionSuccessResult>(
            await owner.Endpoints.ResolveAsync(OwnerServiceId, cancellationToken));
        Assert.Equal(ownerSnapshot[0], ownerResolution.Lease);
        Assert.Equal(OwnerEndpointGenerationId, ownerResolution.Lease.GenerationId);
        AssertEndpointNotFound(await owner.Endpoints.ResolveAsync(ForeignServiceId, cancellationToken));
        AssertEndpointNotFound(await owner.Endpoints.ResolveAsync(HostServiceId, cancellationToken));
        AssertEndpointNotFound(await owner.Endpoints.ResolveAsync(expiredServiceId, cancellationToken));

        var foreignSnapshot = foreign.Endpoints.Current;
        Assert.Single(foreignSnapshot);
        Assert.Equal(ForeignServiceId, foreignSnapshot[0].ServiceId);
        Assert.Equal(foreignPort, foreignSnapshot[0].Port);
        var foreignResolution = Assert.IsType<ExtensionEndpointResolutionSuccessResult>(
            await foreign.Endpoints.ResolveAsync(ForeignServiceId, cancellationToken));
        Assert.Equal(foreignSnapshot[0], foreignResolution.Lease);
        Assert.Equal(ForeignEndpointGenerationId, foreignSnapshot[0].GenerationId);
        AssertEndpointNotFound(await foreign.Endpoints.ResolveAsync(OwnerServiceId, cancellationToken));
        AssertEndpointNotFound(await foreign.Endpoints.ResolveAsync(HostServiceId, cancellationToken));
        AssertEndpointNotFound(await foreign.Endpoints.ResolveAsync(expiredServiceId, cancellationToken));

        Assert.Equal(ForeignEndpointGenerationId, foreignResolution.Lease.GenerationId);
        const int updatedOwnerPort = 21011;
        const int updatedForeignPort = 21012;
        const int updatedHostPort = 21013;
        harness.EndpointPublisher.Publish(
        [
            new HostServiceEndpointLease(
                OwnerServiceId,
                UpdatedOwnerEndpointGenerationId,
                updatedOwnerPort,
                DateTimeOffset.UtcNow.AddMinutes(1),
                OwnerExtensionId),
            new HostServiceEndpointLease(
                ForeignServiceId,
                UpdatedForeignEndpointGenerationId,
                updatedForeignPort,
                DateTimeOffset.UtcNow.AddMinutes(1),
                ForeignExtensionId),
            new HostServiceEndpointLease(
                HostServiceId,
                UpdatedHostEndpointGenerationId,
                updatedHostPort,
                DateTimeOffset.UtcNow.AddMinutes(1))
        ]);
        Assert.Equal(UpdatedHostEndpointGenerationId, harness.EndpointPublisher.Current[HostServiceId].GenerationId);

        Assert.Equal(ownerPort, published[OwnerServiceId].Port);
        Assert.Equal(ownerPort, ownerSnapshot[0].Port);
        Assert.Equal(foreignPort, foreignSnapshot[0].Port);
        var updatedOwnerResolution = Assert.IsType<ExtensionEndpointResolutionSuccessResult>(
            await owner.Endpoints.ResolveAsync(OwnerServiceId, cancellationToken));
        Assert.Equal(updatedOwnerPort, updatedOwnerResolution.Lease.Port);
        Assert.Equal(UpdatedOwnerEndpointGenerationId, updatedOwnerResolution.Lease.GenerationId);
        var updatedForeignResolution = Assert.IsType<ExtensionEndpointResolutionSuccessResult>(
            await foreign.Endpoints.ResolveAsync(ForeignServiceId, cancellationToken));
        Assert.Equal(updatedForeignPort, updatedForeignResolution.Lease.Port);
        AssertEndpointNotFound(await owner.Endpoints.ResolveAsync(ForeignServiceId, cancellationToken));
        Assert.Equal(UpdatedForeignEndpointGenerationId, updatedForeignResolution.Lease.GenerationId);
        AssertEndpointNotFound(await owner.Endpoints.ResolveAsync(HostServiceId, cancellationToken));
        AssertEndpointNotFound(await foreign.Endpoints.ResolveAsync(OwnerServiceId, cancellationToken));
        AssertEndpointNotFound(await foreign.Endpoints.ResolveAsync(HostServiceId, cancellationToken));
        Assert.True(expectedVersion > initial.Value!.Version);
    }

    [Fact]
    public async Task ServicePathsMayBeRelativeToTheDataDirectoryButMustNotEscape()
    {
        await using var harness = await ExtensionCapabilityPostgresHarness.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = harness.CreateCapability(OwnerExtensionId, static _ => false);

        var initial = await owner.ConfigurationApi.ReadAsync(cancellationToken);
        Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);

        var relativeWrite = await owner.Services.UpsertAsync(
            initial.Value!.Version,
            CreateServiceWithPaths(
                Guid.Parse("018f0f00-0000-7000-8000-000000000110"),
                fileName: "svc/bin/app",
                workingDirectory: "svc"),
            cancellationToken);
        var committedVersion = RequireCommittedVersion(relativeWrite);

        var escapingFileName = await owner.Services.UpsertAsync(
            committedVersion,
            CreateServiceWithPaths(
                Guid.Parse("018f0f00-0000-7000-8000-000000000111"),
                fileName: "../outside/app",
                workingDirectory: "svc"),
            cancellationToken);
        AssertConfigurationError(escapingFileName, ConfigurationErrorCode.Validation);

        var escapingWorkingDirectory = await owner.Services.UpsertAsync(
            committedVersion,
            CreateServiceWithPaths(
                Guid.Parse("018f0f00-0000-7000-8000-000000000112"),
                fileName: "svc/bin/app",
                workingDirectory: "svc/../../outside"),
            cancellationToken);
        AssertConfigurationError(escapingWorkingDirectory, ConfigurationErrorCode.Validation);
    }

    private static ExtensionServiceConfiguration CreateServiceWithPaths(
        Guid id,
        string fileName,
        string workingDirectory) =>
        new(
            id,
            enabled: true,
            fileName: fileName,
            argumentList: ImmutableArray<string>.Empty,
            workingDirectory: workingDirectory,
            startMode: ServiceStartMode.Lazy,
            restartPolicy: ServiceRestartPolicy.OnFailure,
            healthCheck: new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                httpPath: null,
                timeout: TimeSpan.FromSeconds(1)),
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            version: 0);

    private static ConfigurationChangeSet CreateFullReplacement(
        HostConfigurationSnapshot snapshot,
        bool includeForeign)
    {
        var routes = includeForeign
            ? snapshot.Routes.Add(CreateFullRoute(
                ForeignFullRouteId,
                ForeignFullServiceId,
                "foreign-route"))
            : snapshot.Routes
                .Where(value => value.Id != ForeignFullRouteId)
                .ToImmutableArray();
        var services = includeForeign
            ? snapshot.Services.Add(CreateFullService(ForeignFullServiceId, "full-read-secret"))
            : snapshot.Services
                .Where(value => value.Id != ForeignFullServiceId)
                .ToImmutableArray();
        var extensionRecords = includeForeign
            ? snapshot.ExtensionRecords.Add(new ExtensionRecordConfiguration(
                ForeignExtensionId,
                "2.0.0",
                ExtensionLoadState.Discovered,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                recordVersion: 0))
            : snapshot.ExtensionRecords
                .Where(value => value.ExtensionId != ForeignExtensionId)
                .ToImmutableArray();
        var extensionSettings = includeForeign
            ? snapshot.ExtensionSettings.Add(new ExtensionSettingsConfiguration(
                ForeignExtensionId,
                schemaVersion: 7,
                settingsJson: "{\"token\":\"foreign-secret\",\"enabled\":true}",
                version: 0))
            : snapshot.ExtensionSettings
                .Where(value => value.ExtensionId != ForeignExtensionId)
                .ToImmutableArray();
        return new ConfigurationChangeSet(
            snapshot.GlobalSettings,
            routes,
            services,
            extensionRecords,
            extensionSettings);
    }

    private static GlobalSettingsConfiguration CreateChangedGlobalSettings(
        GlobalSettingsConfiguration source) =>
        new(
            source.Version,
            source.AutoPortRangeStart,
            source.AutoPortRangeEnd,
            source.MaxRequestBodyBytes,
            source.MaxConcurrentRequests + 1,
            source.ConfigurationPollInterval,
            source.TrustedProxyCidrs,
            source.ProxyTimeouts,
            source.MaxRequestHeaderBytes,
            source.RequestReadTimeout,
            source.ClientIpRatePolicy,
            source.ProxyRetries);

    private static ServiceConfiguration CopyService(
        ServiceConfiguration source,
        long version) =>
        new(
            source.Id,
            source.Enabled,
            source.FileName,
            source.ArgumentList,
            source.WorkingDirectory,
            source.Environment,
            source.StartMode,
            source.RestartPolicy,
            source.HealthCheck,
            source.CreatedAt,
            source.UpdatedAt,
            version);

    private static ServiceConfiguration CreateFullService(Guid id, string secret) =>
        CreateFullService(
            id,
            ImmutableDictionary<string, string>.Empty.Add("FULL_CONFIGURATION_SECRET", secret));

    private static ServiceConfiguration CreateFullService(
        Guid id,
        ImmutableDictionary<string, string> environment) =>
        new(
            id,
            enabled: true,
            fileName: "/usr/bin/full-configuration-service",
            argumentList: ImmutableArray.Create("--full-configuration"),
            workingDirectory: "/tmp",
            environment: environment,
            startMode: ServiceStartMode.Lazy,
            restartPolicy: ServiceRestartPolicy.OnFailure,
            healthCheck: new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                httpPath: null,
                timeout: TimeSpan.FromSeconds(1)),
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            version: 0);

    private static RouteConfiguration CreateFullRoute(
        Guid id,
        Guid serviceId,
        string routeName) =>
        new(
            id,
            enabled: true,
            matcher: new RouteMatcherConfiguration(
                RouteMatcherType.Exact,
                "/full-configuration/" + routeName,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            target: new MicroserviceRouteTargetConfiguration(serviceId),
            priority: 10,
            forwarding: new ForwardingConfiguration(ForwardingMode.Preserve, null),
            requestHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
            responseHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
            metadataJson: "{\"owner\":\"foreign\",\"sensitive\":true}",
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            version: 0);

    private static ConfigurationChangeSet CreateJsonbRegressionSetup(
        HostConfigurationSnapshot snapshot)
    {
        var globalSettings = new GlobalSettingsConfiguration(
            snapshot.GlobalSettings.Version,
            snapshot.GlobalSettings.AutoPortRangeStart,
            snapshot.GlobalSettings.AutoPortRangeEnd,
            snapshot.GlobalSettings.MaxRequestBodyBytes,
            snapshot.GlobalSettings.MaxConcurrentRequests,
            snapshot.GlobalSettings.ConfigurationPollInterval,
            ["10.0.0.0/8", "192.168.0.0/16"],
            snapshot.GlobalSettings.ProxyTimeouts,
            snapshot.GlobalSettings.MaxRequestHeaderBytes,
            snapshot.GlobalSettings.RequestReadTimeout,
            snapshot.GlobalSettings.ClientIpRatePolicy,
            snapshot.GlobalSettings.ProxyRetries);
        var routes = snapshot.Routes
            .Select(value => value.Id == HostRouteId
                ? CopyRoute(
                    value,
                    InitialJsonbRegressionMetadata,
                    matcher: new RouteMatcherConfiguration(
                        value.Matcher.Type,
                        value.Matcher.Pattern,
                        ["api.example.test", "admin.example.test"],
                        ["GET", "POST"]),
                    requestHeaderRewrites: ImmutableArray.Create(
                        new HeaderRewriteConfiguration(
                            HeaderRewriteOperation.Set,
                            "X-Integration-Request",
                            "preserved-request")),
                    responseHeaderRewrites: ImmutableArray.Create(
                        new HeaderRewriteConfiguration(
                            HeaderRewriteOperation.Set,
                            "X-Integration-Response",
                            "preserved-response")))
                : value)
            .ToImmutableArray();
        var services = snapshot.Services
            .Select(value => value.Id == HostServiceId ? CreateLifecycleService(value) : value)
            .ToImmutableArray();
        var extensionSettings = snapshot.ExtensionSettings
            .Select(value => value.ExtensionId == OwnerExtensionId
                ? CopyExtensionSettings(value, InitialJsonbRegressionSettings)
                : value)
            .ToImmutableArray();
        return new ConfigurationChangeSet(
            globalSettings,
            routes,
            services,
            snapshot.ExtensionRecords,
            extensionSettings);
    }

    private static ConfigurationChangeSet ReplaceJsonbValues(
        HostConfigurationSnapshot snapshot,
        string? hostRouteMetadataJson = null,
        string? ownerSettingsJson = null)
    {
        var routes = hostRouteMetadataJson is null
            ? snapshot.Routes
            : snapshot.Routes
                .Select(value => value.Id == HostRouteId
                    ? CopyRoute(value, hostRouteMetadataJson)
                    : value)
                .ToImmutableArray();
        var extensionSettings = ownerSettingsJson is null
            ? snapshot.ExtensionSettings
            : snapshot.ExtensionSettings
                .Select(value => value.ExtensionId == OwnerExtensionId
                    ? CopyExtensionSettings(value, ownerSettingsJson)
                    : value)
                .ToImmutableArray();
        return new ConfigurationChangeSet(
            snapshot.GlobalSettings,
            routes,
            snapshot.Services,
            snapshot.ExtensionRecords,
            extensionSettings);
    }

    private static RouteConfiguration CopyRoute(
        RouteConfiguration source,
        string metadataJson,
        RouteMatcherConfiguration? matcher = null,
        ImmutableArray<HeaderRewriteConfiguration>? requestHeaderRewrites = null,
        ImmutableArray<HeaderRewriteConfiguration>? responseHeaderRewrites = null) =>
        new(
            source.Id,
            source.Enabled,
            matcher ?? source.Matcher,
            source.Target,
            source.Priority,
            source.Forwarding,
            requestHeaderRewrites ?? source.RequestHeaderRewrites,
            responseHeaderRewrites ?? source.ResponseHeaderRewrites,
            metadataJson,
            source.CreatedAt,
            source.UpdatedAt,
            source.Version,
            source.ClientIpRatePolicy,
            source.MaxRequestBodyBytes,
            source.MaxRequestHeaderBytes,
            source.MaxConcurrentRequests,
            source.RequestReadTimeout,
            source.ProxyRetries,
            source.OwnerExtensionId);

    private static ExtensionSettingsConfiguration CopyExtensionSettings(
        ExtensionSettingsConfiguration source,
        string settingsJson) =>
        new(
            source.ExtensionId,
            source.SchemaVersion,
            settingsJson,
            source.Version);

    private static ServiceConfiguration CreateLifecycleService(ServiceConfiguration source) =>
        new(
            source.Id,
            source.Enabled,
            fileName: "fixture-service",
            argumentList: ["--host", "--integration"],
            workingDirectory: source.WorkingDirectory,
            environment: ImmutableDictionary<string, string>.Empty
                .Add("FULL_CONFIGURATION_KEEP", "stable-value")
                .Add("FULL_CONFIGURATION_SECOND", "another-stable-value"),
            startMode: source.StartMode,
            restartPolicy: source.RestartPolicy,
            healthCheck: source.HealthCheck,
            createdAt: source.CreatedAt,
            updatedAt: source.UpdatedAt,
            version: source.Version);

    private static string ReorderJsonbRegressionArray(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root["arbitrary"]!.AsObject()["ordered"] = new JsonArray(
            JsonValue.Create("beta"),
            JsonValue.Create("alpha"));
        return root.ToJsonString();
    }

    private static string RemoveJsonbRegressionOptionalProperty(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.True(root["arbitrary"]!.AsObject().Remove("optional"));
        return root.ToJsonString();
    }

    private static string ReplaceJsonbRegressionLargeNumber(string json, long value)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root["arbitrary"]!.AsObject()["largeNumber"] = JsonValue.Create(value);
        return root.ToJsonString();
    }

    private static string SetJsonbRegressionEnabledToken(string json, bool enabled)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root["enabled"] = JsonValue.Create(enabled);
        return root.ToJsonString();
    }

    private static void AssertFullSnapshotSemanticNoOp(
        HostConfigurationSnapshot before,
        HostConfigurationSnapshot after)
    {
        AssertSnapshotCollectionsUnchanged(before, after);
        Assert.Equal(
            JsonSerializer.Serialize(before.GlobalSettings),
            JsonSerializer.Serialize(after.GlobalSettings));
        Assert.Equal(
            before.Routes
                .Select(value => (value.Id, value.CreatedAt, value.UpdatedAt))
                .OrderBy(value => value.Id),
            after.Routes
                .Select(value => (value.Id, value.CreatedAt, value.UpdatedAt))
                .OrderBy(value => value.Id));
        Assert.Equal(
            before.Services
                .Select(value => (value.Id, value.CreatedAt, value.UpdatedAt))
                .OrderBy(value => value.Id),
            after.Services
                .Select(value => (value.Id, value.CreatedAt, value.UpdatedAt))
                .OrderBy(value => value.Id));
        Assert.Equal(
            before.ExtensionRecords
                .Select(value => (value.ExtensionId, value.CreatedAt, value.UpdatedAt))
                .OrderBy(value => value.ExtensionId),
            after.ExtensionRecords
                .Select(value => (value.ExtensionId, value.CreatedAt, value.UpdatedAt))
                .OrderBy(value => value.ExtensionId));
        AssertServicesUnchanged(before.Services, after.Services);
    }

    private static void AssertOnlyHostRouteChanged(
        HostConfigurationSnapshot before,
        HostConfigurationSnapshot after,
        Guid routeId)
    {
        Assert.Equal(checked(before.Version + 1), after.Version);
        AssertGlobalSettingsUnchanged(before.GlobalSettings, after.GlobalSettings);
        Assert.Equal(before.Routes.Length, after.Routes.Length);
        foreach (var expected in before.Routes)
        {
            var actual = Assert.Single(after.Routes, value => value.Id == expected.Id);
            if (expected.Id == routeId)
            {
                Assert.Equal(checked(expected.Version + 1), actual.Version);
                Assert.Equal(expected.CreatedAt, actual.CreatedAt);
                Assert.NotEqual(expected.MetadataJson, actual.MetadataJson);
            }
            else
            {
                Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
            }
        }

        AssertServicesUnchanged(before.Services, after.Services);
        AssertExtensionRecordsUnchanged(before.ExtensionRecords, after.ExtensionRecords);
        AssertExtensionSettingsUnchanged(before.ExtensionSettings, after.ExtensionSettings);
    }

    private static void AssertOnlyOwnerSettingsChanged(
        HostConfigurationSnapshot before,
        HostConfigurationSnapshot after,
        string extensionId)
    {
        Assert.Equal(checked(before.Version + 1), after.Version);
        AssertGlobalSettingsUnchanged(before.GlobalSettings, after.GlobalSettings);
        Assert.Equal(before.Routes.Length, after.Routes.Length);
        foreach (var expected in before.Routes)
        {
            var actual = Assert.Single(after.Routes, value => value.Id == expected.Id);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        }

        AssertServicesUnchanged(before.Services, after.Services);
        AssertExtensionRecordsUnchanged(before.ExtensionRecords, after.ExtensionRecords);
        Assert.Equal(before.ExtensionSettings.Length, after.ExtensionSettings.Length);
        foreach (var expected in before.ExtensionSettings)
        {
            var actual = Assert.Single(
                after.ExtensionSettings,
                value => value.ExtensionId == expected.ExtensionId);
            if (expected.ExtensionId == extensionId)
            {
                Assert.Equal(checked(expected.Version + 1), actual.Version);
                Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
                Assert.NotEqual(expected.SettingsJson, actual.SettingsJson);
            }
            else
            {
                Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
            }
        }
    }

    private static void AssertGlobalSettingsUnchanged(
        GlobalSettingsConfiguration expected,
        GlobalSettingsConfiguration actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    private static void AssertServicesUnchanged(
        ImmutableArray<ServiceConfiguration> expectedServices,
        ImmutableArray<ServiceConfiguration> actualServices)
    {
        Assert.Equal(expectedServices.Length, actualServices.Length);
        foreach (var expected in expectedServices)
        {
            var actual = Assert.Single(actualServices, value => value.Id == expected.Id);
            Assert.Equal(expected.Enabled, actual.Enabled);
            Assert.Equal(expected.FileName, actual.FileName);
            Assert.Equal(expected.ArgumentList.ToArray(), actual.ArgumentList.ToArray());
            Assert.Equal(expected.WorkingDirectory, actual.WorkingDirectory);
            Assert.Equal(
                expected.Environment.OrderBy(value => value.Key).ToArray(),
                actual.Environment.OrderBy(value => value.Key).ToArray());
            Assert.Equal(expected.StartMode, actual.StartMode);
            Assert.Equal(expected.RestartPolicy, actual.RestartPolicy);
            Assert.Equal(expected.HealthCheck, actual.HealthCheck);
            Assert.Equal(expected.CreatedAt, actual.CreatedAt);
            Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
            Assert.Equal(expected.Version, actual.Version);
        }
    }

    private static void AssertExtensionRecordsUnchanged(
        ImmutableArray<ExtensionRecordConfiguration> expectedRecords,
        ImmutableArray<ExtensionRecordConfiguration> actualRecords)
    {
        Assert.Equal(expectedRecords.Length, actualRecords.Length);
        foreach (var expected in expectedRecords)
        {
            var actual = Assert.Single(
                actualRecords,
                value => value.ExtensionId == expected.ExtensionId);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        }
    }

    private static void AssertExtensionSettingsUnchanged(
        ImmutableArray<ExtensionSettingsConfiguration> expectedSettings,
        ImmutableArray<ExtensionSettingsConfiguration> actualSettings)
    {
        Assert.Equal(expectedSettings.Length, actualSettings.Length);
        foreach (var expected in expectedSettings)
        {
            var actual = Assert.Single(
                actualSettings,
                value => value.ExtensionId == expected.ExtensionId);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        }
    }

    private static void AssertHostServiceLifecycleUnchanged(
        ExtensionCapabilityPostgresHarness harness,
        ProcessInstanceId expectedProcessInstance,
        HostServiceEndpointLease expectedEndpoint,
        PortLease expectedLease)
    {
        Assert.Equal([HostServiceId], harness.StartedServiceIds);
        Assert.Equal([expectedProcessInstance], harness.StartedProcessInstances);
        Assert.Empty(harness.StoppedServiceIds);
        Assert.Equal([HostServiceId], harness.AcquiredLeaseServiceIds);
        Assert.Empty(harness.ReleasedLeaseServiceIds);
        var currentLease = Assert.IsType<PortLease>(harness.GetCurrentLease(HostServiceId));
        var currentEndpoint = harness.EndpointPublisher.Current[HostServiceId];
        Assert.Equal(expectedLease, currentLease);
        Assert.Equal(expectedEndpoint, currentEndpoint);
        Assert.Equal(expectedLease.GenerationId, currentLease.GenerationId);
        Assert.Equal(expectedLease.GenerationId, expectedEndpoint.GenerationId);
        Assert.Equal(expectedLease.GenerationId, currentEndpoint.GenerationId);
    }

    private static async Task AssertConfigurationNotificationAsync(
        NpgsqlConnection listener,
        ConcurrentQueue<NpgsqlNotificationEventArgs> notifications,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var notification = await WaitForConfigurationNotificationAsync(
            listener,
            notifications,
            cancellationToken);
        Assert.Equal("nekostick_config_changed", notification.Channel);
        Assert.Equal(
            expectedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            notification.Payload);
    }

    private static async Task<NpgsqlNotificationEventArgs> WaitForConfigurationNotificationAsync(
        NpgsqlConnection listener,
        ConcurrentQueue<NpgsqlNotificationEventArgs> notifications,
        CancellationToken cancellationToken)
    {
        NpgsqlNotificationEventArgs? notification;
        while (!notifications.TryDequeue(out notification))
        {
            await listener.WaitAsync(cancellationToken);
        }

        return notification!;
    }

    private static void AssertSnapshotCollectionsUnchanged(
        HostConfigurationSnapshot before,
        HostConfigurationSnapshot after)
    {
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.GlobalSettings.Version, after.GlobalSettings.Version);
        Assert.Equal(
            before.Routes.Select(value => (value.Id, value.Version, value.MetadataJson)).OrderBy(value => value.Id),
            after.Routes.Select(value => (value.Id, value.Version, value.MetadataJson)).OrderBy(value => value.Id));
        Assert.Equal(
            before.Services.Select(value => (value.Id, value.Version)).OrderBy(value => value.Id),
            after.Services.Select(value => (value.Id, value.Version)).OrderBy(value => value.Id));
        Assert.Equal(
            before.ExtensionRecords
                .Select(value => (value.ExtensionId, value.RecordVersion))
                .OrderBy(value => value.ExtensionId),
            after.ExtensionRecords
                .Select(value => (value.ExtensionId, value.RecordVersion))
                .OrderBy(value => value.ExtensionId));
        Assert.Equal(
            before.ExtensionSettings
                .Select(value => (value.ExtensionId, value.Version, value.SettingsJson))
                .OrderBy(value => value.ExtensionId),
            after.ExtensionSettings
                .Select(value => (value.ExtensionId, value.Version, value.SettingsJson))
                .OrderBy(value => value.ExtensionId));
        foreach (var service in before.Services)
        {
            var current = Assert.Single(after.Services, value => value.Id == service.Id);
            Assert.Equal(
                service.Environment.OrderBy(value => value.Key).ToArray(),
                current.Environment.OrderBy(value => value.Key).ToArray());
        }
    }

    private static long RequireCommittedVersion(ConfigurationWriteResult result)
    {
        Assert.True(result.IsSuccess, result.Errors.FirstOrDefault()?.Message);
        Assert.NotNull(result.NewVersion);
        return result.NewVersion!.Value;
    }

    private static void AssertConfigurationError(
        ConfigurationWriteResult result,
        ConfigurationErrorCode expectedCode)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, result.Errors.Single().Code);
        Assert.Null(result.NewVersion);
    }

    private static ExtensionServiceConfiguration CreateExtensionService(Guid id) =>
        new(
            id,
            enabled: true,
            // Relative executable path: resolved per node against the harness data directory,
            // which CreateAsync provisions with a dummy fixture-service file.
            fileName: "fixture-service",
            argumentList: ImmutableArray.Create("--integration"),
            workingDirectory: "/tmp",
            startMode: ServiceStartMode.Lazy,
            restartPolicy: ServiceRestartPolicy.OnFailure,
            healthCheck: new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                httpPath: null,
                timeout: TimeSpan.FromSeconds(1)),
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            version: 0);

    private static ExtensionRouteConfiguration CreateExtensionRoute(
        Guid id,
        ExtensionRouteTargetConfiguration target) =>
        new(
            id,
            enabled: true,
            matcher: new RouteMatcherConfiguration(
                RouteMatcherType.Exact,
                "/extension-owned",
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            target,
            priority: 10);

    private sealed class ExtensionCapabilityPostgresHarness : IAsyncDisposable
    {
        private readonly PostgresConfigurationTestScope scope;
        private readonly ServiceProvider services;
        private readonly HostConfigurationRefreshService refreshService;
        private readonly HostServiceLifecycleManager lifecycleManager;
        private readonly HostConfigurationPublisher publisher;
        private readonly TestProcessExecutor processExecutor;
        private readonly TestPortLeaseStore leaseStore;
        private int disposed;

        private ExtensionCapabilityPostgresHarness(
            PostgresConfigurationTestScope scope,
            ServiceProvider services,
            HostConfigurationRefreshService refreshService,
            HostServiceLifecycleManager lifecycleManager,
            HostConfigurationPublisher publisher,
            TestProcessExecutor processExecutor,
            TestPortLeaseStore leaseStore,
            HostConfigurationSnapshotHolder snapshotHolder,
            HostRuntimeState runtimeState,
            HostServiceEndpointSnapshotPublisher endpointPublisher,
            IExtensionCapabilityFactory capabilityFactory,
            string dataDirectory)
        {
            this.scope = scope;
            this.services = services;
            this.refreshService = refreshService;
            this.lifecycleManager = lifecycleManager;
            this.publisher = publisher;
            this.processExecutor = processExecutor;
            this.leaseStore = leaseStore;
            SnapshotHolder = snapshotHolder;
            RuntimeState = runtimeState;
            EndpointPublisher = endpointPublisher;
            CapabilityFactory = capabilityFactory;
            DataDirectory = dataDirectory;
        }

        internal PostgresTestDatabase Database => scope.Database;
        internal HostConfigurationSnapshotHolder SnapshotHolder { get; }
        internal HostRuntimeState RuntimeState { get; }
        internal string DataDirectory { get; }
        internal HostServiceEndpointSnapshotPublisher EndpointPublisher { get; }
        internal IExtensionCapabilityFactory CapabilityFactory { get; }
        internal HostServiceLifecycleManager LifecycleManager => lifecycleManager;
        internal ImmutableArray<Guid> StartedServiceIds => processExecutor.StartedServiceIds;
        internal ImmutableArray<ProcessInstanceId> StartedProcessInstances => processExecutor.StartedProcessInstances;
        internal ImmutableArray<Guid> StoppedServiceIds => processExecutor.StoppedServiceIds;
        internal ImmutableArray<Guid> AcquiredLeaseServiceIds => leaseStore.AcquiredServiceIds;
        internal ImmutableArray<Guid> ReleasedLeaseServiceIds => leaseStore.ReleasedServiceIds;

        internal PortLease? GetCurrentLease(Guid serviceId) => leaseStore.GetCurrentLease(serviceId);

        internal NpgsqlConnection CreateConnection() => scope.CreateConnection();

        internal async Task PublishAndReconcileAsync(
            HostConfigurationSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            var outcome = await publisher.PublishAsync(snapshot, cancellationToken: cancellationToken);
            Assert.Equal(PublishOutcome.Published, outcome);
            await lifecycleManager.ReconcileAsync(snapshot, cancellationToken);
            var readiness = await lifecycleManager.EnsureReadyAsync(snapshot, HostServiceId, cancellationToken);
            Assert.Equal(HostServiceReadinessStatus.Ready, readiness.Status);
        }

        internal static async Task<ExtensionCapabilityPostgresHarness> CreateAsync()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var scope = await PostgresConfigurationTestScope.CreateAsync();
            try
            {
                var initial = await scope.Api.ReadSnapshotAsync(cancellationToken);
                Assert.True(initial.IsSuccess, initial.Errors.FirstOrDefault()?.Message);
                Assert.NotNull(initial.Value);
                var seed = CreateSeedChangeSet(initial.Value!);
                var seeded = await scope.Api.WriteSnapshotAsync(
                    initial.Value!.Version,
                    seed,
                    cancellationToken);
                Assert.True(seeded.IsSuccess, seeded.Errors.FirstOrDefault()?.Message);

                var current = await scope.Api.ReadSnapshotAsync(cancellationToken);
                Assert.True(current.IsSuccess, current.Errors.FirstOrDefault()?.Message);
                Assert.NotNull(current.Value);

                var snapshotHolder = new HostConfigurationSnapshotHolder();
                Assert.True(snapshotHolder.TryReplace(current.Value!));
                var runtimeState = new HostRuntimeState(
                    snapshotHolder,
                    new HostNodeOptions(skipExtensions: true, disableSupervisor: true, readOnly: false));
                var runtimeOptions = new HostRuntimeOptions(
                    IntegrationTestBoundary.RequirePostgresConnectionString(),
                    "capability-integration-" + Guid.NewGuid().ToString("N"),
                    readOnly: false);
                var endpointPublisher = new HostServiceEndpointSnapshotPublisher();
                var processExecutor = new TestProcessExecutor();
                var healthProbe = new TestHealthProbe();
                var leaseStore = new TestPortLeaseStore();
                var serviceCollection = new ServiceCollection();
                serviceCollection.AddLogging();
                serviceCollection.AddSingleton(snapshotHolder);
                serviceCollection.AddSingleton(runtimeState);
                serviceCollection.AddSingleton(runtimeOptions);
                serviceCollection.AddSingleton<IDbContextFactory<NekostickDbContext>>(
                    new TestDbContextFactory(scope.Database));
                serviceCollection.AddScoped<NekostickDbContext>(_ => scope.Database.CreateContext());
                serviceCollection.AddScoped<EfHostConfigApi>();
                serviceCollection.AddScoped<IHostConfigApi>(provider =>
                    provider.GetRequiredService<EfHostConfigApi>());
                serviceCollection.AddScoped<IExtensionOwnedConfigurationApi>(provider =>
                    new EfExtensionOwnedConfigurationApi(
                        provider.GetRequiredService<EfHostConfigApi>()));
                serviceCollection.AddScoped<IConfigurationRevisionReader, EfConfigurationRevisionReader>();
                serviceCollection.AddSingleton<IHostConfigurationSnapshotReader, EfHostConfigurationSnapshotReader>();
                serviceCollection.AddSingleton(endpointPublisher);
                serviceCollection.AddSingleton<IHostServiceEndpointSnapshotAccessor>(endpointPublisher);
                var dataDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "nekostick-cap-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dataDirectory);
                // The fixture executable only needs to exist: the supervisor preflights File.Exists
                // on the resolved path and the fake executor never launches it.
                File.WriteAllBytes(Path.Combine(dataDirectory, "fixture-service"), []);
                serviceCollection.AddSingleton<IHostServiceLifecycleCoordinator>(provider =>
                {
                    var manager = new HostServiceLifecycleManager(
                        processExecutor,
                        healthProbe,
                        leaseStore,
                        snapshotHolder,
                        endpointPublisher,
                        runtimeState,
                        runtimeOptions,
                        provider.GetRequiredService<ILogger<HostServiceLifecycleManager>>(),
                        new Nekolla.Nekostick.Proxy.MicroserviceDrainTracker(),
                        new HostNodeOptions(
                            skipExtensions: true,
                            disableSupervisor: true,
                            readOnly: false,
                            dataDirectory: dataDirectory));
                    return manager;
                });
                serviceCollection.AddSingleton<ExtensionRuntimeManager>(provider =>
                    new ExtensionRuntimeManager(
                        HostApiVersion.Current,
                        capabilityFactory: provider.GetRequiredService<IExtensionCapabilityFactory>()));
                serviceCollection.AddSingleton<IExtensionCapabilityFactory>(provider =>
                    new ExtensionCapabilityFactory(
                        provider.GetRequiredService<IServiceScopeFactory>(),
                        runtimeState,
                        provider));
                serviceCollection.AddSingleton<HostConfigurationPublisher>(provider =>
                    new HostConfigurationPublisher(
                        snapshotHolder,
                        provider.GetRequiredService<ExtensionRuntimeManager>(),
                        new HostNodeOptions(skipExtensions: true, disableSupervisor: true, readOnly: false),
                        NullLogger<HostConfigurationPublisher>.Instance));

                var services = serviceCollection.BuildServiceProvider();
                var publisher = services.GetRequiredService<HostConfigurationPublisher>();
                var signal = new InitialRefreshSignal();
                var refreshService = new HostConfigurationRefreshService(
                    snapshotHolder,
                    services.GetRequiredService<IHostConfigurationSnapshotReader>(),
                    signal,
                    runtimeState,
                    services.GetRequiredService<IServiceScopeFactory>(),
                    runtimeOptions,
                    publisher,
                    NullLogger<HostConfigurationRefreshService>.Instance);
                await refreshService.StartAsync(cancellationToken);
                await signal.FirstHintObserved.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                await WaitForWritableAsync(runtimeState, cancellationToken);

                var lifecycleManager = (HostServiceLifecycleManager)services
                    .GetRequiredService<IHostServiceLifecycleCoordinator>();
                return new ExtensionCapabilityPostgresHarness(
                    scope,
                    services,
                    refreshService,
                    lifecycleManager,
                    publisher,
                    processExecutor,
                    leaseStore,
                    snapshotHolder,
                    runtimeState,
                    endpointPublisher,
                    services.GetRequiredService<IExtensionCapabilityFactory>(),
                    dataDirectory);
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }

        internal ExtensionCapabilitySet CreateCapability(
            string extensionId,
            Func<string, bool> handlerIsOwned) =>
            CapabilityFactory.Create(extensionId, handlerIsOwned);

        internal async Task<ConfigurationReadResult<HostConfigurationSnapshot>> ReadSnapshotAsync(
            CancellationToken cancellationToken) =>
            await scope.Api.ReadSnapshotAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await lifecycleManager.StopAsync(CancellationToken.None);
            }
            finally
            {
                await refreshService.StopAsync(CancellationToken.None);
                await services.DisposeAsync();
                await scope.DisposeAsync();
                await SnapshotHolder.DisposeAsync();
                try
                {
                    Directory.Delete(DataDirectory, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort temp directory cleanup only.
                }
            }
        }

        private static ConfigurationChangeSet CreateSeedChangeSet(
            HostConfigurationSnapshot snapshot)
        {
            var now = DateTimeOffset.UtcNow;
            var extensionRecord = new ExtensionRecordConfiguration(
                OwnerExtensionId,
                "1.0.0",
                ExtensionLoadState.Loaded,
                now,
                now,
                recordVersion: 0);
            var settings = new ExtensionSettingsConfiguration(
                OwnerExtensionId,
                schemaVersion: 1,
                settingsJson: "{\"enabled\":true}",
                version: 0);
            var hostService = CreateHostService(HostServiceId);
            var hostRoute = new RouteConfiguration(
                HostRouteId,
                enabled: true,
                matcher: new RouteMatcherConfiguration(
                    RouteMatcherType.Exact,
                    "/host-owned",
                    ImmutableArray<string>.Empty,
                    ImmutableArray<string>.Empty),
                target: new MicroserviceRouteTargetConfiguration(HostServiceId),
                priority: 1,
                forwarding: new ForwardingConfiguration(ForwardingMode.Preserve, null),
                requestHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
                responseHeaderRewrites: ImmutableArray<HeaderRewriteConfiguration>.Empty,
                metadataJson: "{}",
                createdAt: now,
                updatedAt: now,
                version: 0);
            return new ConfigurationChangeSet(
                snapshot.GlobalSettings,
                ImmutableArray.Create(hostRoute),
                ImmutableArray.Create(hostService),
                ImmutableArray.Create(extensionRecord),
                ImmutableArray.Create(settings));
        }

        private static ServiceConfiguration CreateHostService(Guid id) =>
            new(
                id,
                enabled: true,
                fileName: "/usr/bin/host-service",
                argumentList: ImmutableArray.Create("--host"),
                workingDirectory: "/tmp",
                environment: ImmutableDictionary<string, string>.Empty,
                startMode: ServiceStartMode.Lazy,
                restartPolicy: ServiceRestartPolicy.OnFailure,
                healthCheck: new ServiceHealthCheckConfiguration(
                    ServiceHealthCheckType.Process,
                    httpPath: null,
                    timeout: TimeSpan.FromSeconds(1)),
                createdAt: DateTimeOffset.UtcNow,
                updatedAt: DateTimeOffset.UtcNow,
                version: 0);

        private static async Task WaitForWritableAsync(
            HostRuntimeState runtimeState,
            CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (!runtimeState.ConfigurationWritesAllowed)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            }
        }
    }

    private sealed class InitialRefreshSignal : IConfigurationChangeSignal
    {
        private readonly TaskCompletionSource observed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int count;

        internal Task FirstHintObserved => observed.Task;

        public Task WaitForHintAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref count, 1) == 0)
            {
                observed.TrySetResult();
                return Task.CompletedTask;
            }

            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<NekostickDbContext>
    {
        private readonly PostgresTestDatabase database;

        internal TestDbContextFactory(PostgresTestDatabase database) => this.database = database;

        public NekostickDbContext CreateDbContext() => database.CreateContext();

        public Task<NekostickDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(database.CreateContext());
    }

    private sealed class TestProcessExecutor : IProcessExecutor
    {
        private readonly ConcurrentQueue<Guid> startedServiceIds = new();
        private readonly ConcurrentQueue<ProcessInstanceId> startedProcessInstances = new();
        private readonly ConcurrentQueue<Guid> stoppedServiceIds = new();

        internal ImmutableArray<Guid> StartedServiceIds => startedServiceIds.ToImmutableArray();
        internal ImmutableArray<ProcessInstanceId> StartedProcessInstances => startedProcessInstances.ToImmutableArray();
        internal ImmutableArray<Guid> StoppedServiceIds => stoppedServiceIds.ToImmutableArray();

        public ValueTask<ProcessOperationResult> StartAsync(
            ProcessLaunchSpecification specification,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instanceId = new ProcessInstanceId(Guid.CreateVersion7());
            startedServiceIds.Enqueue(specification.ServiceId);
            startedProcessInstances.Enqueue(instanceId);
            return ValueTask.FromResult(
                new ProcessOperationResult(
                    ProcessOperationStatus.Accepted,
                    ServiceStateReasonCode.StartAccepted,
                    instanceId));
        }

        public ValueTask<ProcessOperationResult> StopAsync(
            Guid serviceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stoppedServiceIds.Enqueue(serviceId);
            return ValueTask.FromResult(
                new ProcessOperationResult(
                    ProcessOperationStatus.Completed,
                    ServiceStateReasonCode.StopCompleted));
        }
    }

    private sealed class TestHealthProbe : IServiceHealthProbe
    {
        public ValueTask<HealthObservationResult> ProbeAsync(
            ServiceHealthProbeRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                new HealthObservationResult(
                    request.ServiceId,
                    HealthObservationStatus.Healthy,
                    DateTimeOffset.UtcNow,
                    TimeSpan.Zero,
                    attempt: 1));
    }

    private sealed class TestPortLeaseStore : IPortLeaseStore
    {
        private readonly object gate = new();
        private readonly Dictionary<(NodeIdentifier NodeId, Guid ServiceId, Guid GenerationId), PortLease> leases = [];
        private readonly Dictionary<(NodeIdentifier NodeId, int Port), (NodeIdentifier NodeId, Guid ServiceId, Guid GenerationId)> occupiedPorts = [];
        private readonly Dictionary<Guid, (NodeIdentifier NodeId, Guid ServiceId, Guid GenerationId)> currentLeaseKeys = [];
        private readonly ConcurrentQueue<Guid> acquiredServiceIds = new();
        private readonly ConcurrentQueue<Guid> releasedServiceIds = new();

        internal ImmutableArray<Guid> AcquiredServiceIds => acquiredServiceIds.ToImmutableArray();
        internal ImmutableArray<Guid> ReleasedServiceIds => releasedServiceIds.ToImmutableArray();

        internal PortLease? GetCurrentLease(Guid serviceId)
        {
            lock (gate)
            {
                return currentLeaseKeys.TryGetValue(serviceId, out var leaseKey) &&
                    leases.TryGetValue(leaseKey, out var lease) &&
                    !lease.IsExpired(DateTimeOffset.UtcNow)
                    ? lease
                    : null;
            }
        }

        public ValueTask<PortLeaseOperationResult> ApplyAsync(
            PortLeaseIntent intent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (intent.Kind)
            {
                case PortLeaseIntentKind.Acquire when intent.Request is { } request:
                {
                    acquiredServiceIds.Enqueue(request.ServiceId);
                    lock (gate)
                    {
                        var now = DateTimeOffset.UtcNow;
                        RemoveExpiredLeases(now);
                        var leaseKey = (request.NodeId, request.ServiceId, request.GenerationId);
                        if (leases.ContainsKey(leaseKey))
                        {
                            return ValueTask.FromResult(new PortLeaseOperationResult(
                                PortLeaseOperationStatus.Conflict));
                        }

                        var port = request.Port;
                        if (port == 0)
                        {
                            var rangeStart = request.AutomaticPortRangeStart ?? 21000;
                            var rangeEnd = request.AutomaticPortRangeEnd ?? rangeStart;
                            port = rangeStart;
                            while (port <= rangeEnd && occupiedPorts.ContainsKey((request.NodeId, port)))
                            {
                                port++;
                            }

                            if (port > rangeEnd)
                            {
                                return ValueTask.FromResult(new PortLeaseOperationResult(
                                    PortLeaseOperationStatus.Conflict));
                            }
                        }

                        if (occupiedPorts.ContainsKey((request.NodeId, port)))
                        {
                            return ValueTask.FromResult(new PortLeaseOperationResult(
                                PortLeaseOperationStatus.Conflict));
                        }

                        var lease = new PortLease(
                            request.NodeId,
                            request.ServiceId,
                            request.GenerationId,
                            port,
                            now,
                            now.AddMinutes(1),
                            version: 1);
                        leases.Add(leaseKey, lease);
                        occupiedPorts.Add((request.NodeId, port), leaseKey);
                        currentLeaseKeys[request.ServiceId] = leaseKey;
                        return ValueTask.FromResult(new PortLeaseOperationResult(
                            PortLeaseOperationStatus.Applied,
                            lease));
                    }
                }
                case PortLeaseIntentKind.Renew when intent.Renewal is { } renewal:
                {
                    lock (gate)
                    {
                        var leaseKey = (renewal.NodeId, renewal.ServiceId, renewal.GenerationId);
                        if (!leases.TryGetValue(leaseKey, out var current))
                        {
                            return ValueTask.FromResult(new PortLeaseOperationResult(
                                PortLeaseOperationStatus.NotFound));
                        }

                        if (current.Port != renewal.Port || current.Version != renewal.LeaseVersion ||
                            current.IsExpired(DateTimeOffset.UtcNow))
                        {
                            return ValueTask.FromResult(new PortLeaseOperationResult(
                                PortLeaseOperationStatus.Conflict));
                        }

                        var now = DateTimeOffset.UtcNow;
                        var lease = new PortLease(
                            renewal.NodeId,
                            renewal.ServiceId,
                            renewal.GenerationId,
                            renewal.Port,
                            current.AcquiredAt,
                            now.AddMinutes(1),
                            checked(current.Version + 1));
                        leases[leaseKey] = lease;
                        return ValueTask.FromResult(new PortLeaseOperationResult(
                            PortLeaseOperationStatus.Applied,
                            lease));
                    }
                }
                case PortLeaseIntentKind.Release when intent.Release is { } release:
                {
                    releasedServiceIds.Enqueue(release.ServiceId);
                    lock (gate)
                    {
                        var leaseKey = (release.NodeId, release.ServiceId, release.GenerationId);
                        if (!leases.TryGetValue(leaseKey, out var released))
                        {
                            return ValueTask.FromResult(new PortLeaseOperationResult(
                                PortLeaseOperationStatus.NotFound));
                        }

                        if (released.Port != release.Port || released.Version != release.LeaseVersion)
                        {
                            return ValueTask.FromResult(new PortLeaseOperationResult(
                                PortLeaseOperationStatus.Conflict));
                        }

                        RemoveLease(leaseKey, released, DateTimeOffset.UtcNow);
                        return ValueTask.FromResult(new PortLeaseOperationResult(
                            PortLeaseOperationStatus.Applied,
                            released));
                    }
                }
                default:
                    return ValueTask.FromResult(new PortLeaseOperationResult(
                        PortLeaseOperationStatus.Rejected));
            }
        }

        private void RemoveExpiredLeases(DateTimeOffset now)
        {
            foreach (var leaseKey in leases
                         .Where(value => value.Value.IsExpired(now))
                         .Select(value => value.Key)
                         .ToArray())
            {
                RemoveLease(leaseKey, leases[leaseKey], now);
            }
        }

        private void RemoveLease(
            (NodeIdentifier NodeId, Guid ServiceId, Guid GenerationId) leaseKey,
            PortLease lease,
            DateTimeOffset now)
        {
            leases.Remove(leaseKey);
            var portKey = (leaseKey.NodeId, lease.Port);
            if (occupiedPorts.TryGetValue(portKey, out var currentOwner) && currentOwner == leaseKey)
            {
                occupiedPorts.Remove(portKey);
            }

            if (currentLeaseKeys.TryGetValue(leaseKey.ServiceId, out var currentLeaseKey) &&
                currentLeaseKey == leaseKey)
            {
                RefreshCurrentLease(leaseKey.ServiceId, now);
            }
        }

        private void RefreshCurrentLease(Guid serviceId, DateTimeOffset now)
        {
            var hasLease = false;
            var latestKey = default((NodeIdentifier NodeId, Guid ServiceId, Guid GenerationId));
            var latestAcquiredAt = DateTimeOffset.MinValue;
            foreach (var entry in leases)
            {
                if (entry.Key.ServiceId != serviceId || entry.Value.IsExpired(now))
                {
                    continue;
                }

                if (!hasLease || entry.Value.AcquiredAt > latestAcquiredAt ||
                    (entry.Value.AcquiredAt == latestAcquiredAt &&
                        entry.Key.GenerationId.CompareTo(latestKey.GenerationId) > 0))
                {
                    hasLease = true;
                    latestKey = entry.Key;
                    latestAcquiredAt = entry.Value.AcquiredAt;
                }
            }

            if (hasLease)
            {
                currentLeaseKeys[serviceId] = latestKey;
            }
            else
            {
                currentLeaseKeys.Remove(serviceId);
            }
        }
    }


    private static void AssertEndpointNotFound(ExtensionEndpointResolutionResult result)
    {
        Assert.Same(ExtensionEndpointResolutionResult.NotFound, result);
        var failure = Assert.IsType<ExtensionEndpointResolutionFailureResult>(result);
        Assert.Equal(ExtensionEndpointResolutionFailureCode.NotFound, failure.Code);
        Assert.Equal("No active endpoint lease was found for the service.", failure.Detail.Message);
    }
}
