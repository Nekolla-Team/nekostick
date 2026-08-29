using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
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
                ownerPort,
                activeUntil,
                OwnerExtensionId),
            new HostServiceEndpointLease(
                ForeignServiceId,
                foreignPort,
                activeUntil,
                ForeignExtensionId),
            new HostServiceEndpointLease(
                HostServiceId,
                hostPort,
                activeUntil),
            new HostServiceEndpointLease(
                expiredServiceId,
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
        Assert.Equal(ForeignExtensionId, published[ForeignServiceId].OwnerExtensionId);
        Assert.Null(published[HostServiceId].OwnerExtensionId);
        Assert.DoesNotContain(expiredServiceId, published.Keys);

        var ownerSnapshot = owner.Endpoints.Current;
        Assert.Single(ownerSnapshot);
        Assert.Equal(OwnerServiceId, ownerSnapshot[0].ServiceId);
        Assert.Equal(ownerPort, ownerSnapshot[0].Port);
        Assert.Equal(ownerSnapshot[0], await owner.Endpoints.ResolveAsync(OwnerServiceId, cancellationToken));
        Assert.Null(await owner.Endpoints.ResolveAsync(ForeignServiceId, cancellationToken));
        Assert.Null(await owner.Endpoints.ResolveAsync(HostServiceId, cancellationToken));
        Assert.Null(await owner.Endpoints.ResolveAsync(expiredServiceId, cancellationToken));

        var foreignSnapshot = foreign.Endpoints.Current;
        Assert.Single(foreignSnapshot);
        Assert.Equal(ForeignServiceId, foreignSnapshot[0].ServiceId);
        Assert.Equal(foreignPort, foreignSnapshot[0].Port);
        Assert.Equal(foreignSnapshot[0], await foreign.Endpoints.ResolveAsync(ForeignServiceId, cancellationToken));
        Assert.Null(await foreign.Endpoints.ResolveAsync(OwnerServiceId, cancellationToken));
        Assert.Null(await foreign.Endpoints.ResolveAsync(HostServiceId, cancellationToken));
        Assert.Null(await foreign.Endpoints.ResolveAsync(expiredServiceId, cancellationToken));

        const int updatedOwnerPort = 21011;
        const int updatedForeignPort = 21012;
        const int updatedHostPort = 21013;
        harness.EndpointPublisher.Publish(
        [
            new HostServiceEndpointLease(
                OwnerServiceId,
                updatedOwnerPort,
                DateTimeOffset.UtcNow.AddMinutes(1),
                OwnerExtensionId),
            new HostServiceEndpointLease(
                ForeignServiceId,
                updatedForeignPort,
                DateTimeOffset.UtcNow.AddMinutes(1),
                ForeignExtensionId),
            new HostServiceEndpointLease(
                HostServiceId,
                updatedHostPort,
                DateTimeOffset.UtcNow.AddMinutes(1))
        ]);

        Assert.Equal(ownerPort, published[OwnerServiceId].Port);
        Assert.Equal(ownerPort, ownerSnapshot[0].Port);
        Assert.Equal(foreignPort, foreignSnapshot[0].Port);
        Assert.Equal(updatedOwnerPort, (await owner.Endpoints.ResolveAsync(OwnerServiceId, cancellationToken))!.Port);
        Assert.Equal(updatedForeignPort, (await foreign.Endpoints.ResolveAsync(ForeignServiceId, cancellationToken))!.Port);
        Assert.Null(await owner.Endpoints.ResolveAsync(ForeignServiceId, cancellationToken));
        Assert.Null(await owner.Endpoints.ResolveAsync(HostServiceId, cancellationToken));
        Assert.Null(await foreign.Endpoints.ResolveAsync(OwnerServiceId, cancellationToken));
        Assert.Null(await foreign.Endpoints.ResolveAsync(HostServiceId, cancellationToken));
        Assert.True(expectedVersion > initial.Value!.Version);
    }

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
        new(
            id,
            enabled: true,
            fileName: "/usr/bin/full-configuration-service",
            argumentList: ImmutableArray.Create("--full-configuration"),
            workingDirectory: "/tmp",
            environment: ImmutableDictionary<string, string>.Empty.Add(
                "FULL_CONFIGURATION_SECRET",
                secret),
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
            fileName: "/usr/bin/fixture-service",
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
        private int disposed;

        private ExtensionCapabilityPostgresHarness(
            PostgresConfigurationTestScope scope,
            ServiceProvider services,
            HostConfigurationRefreshService refreshService,
            HostServiceLifecycleManager lifecycleManager,
            HostConfigurationSnapshotHolder snapshotHolder,
            HostRuntimeState runtimeState,
            HostServiceEndpointSnapshotPublisher endpointPublisher,
            IExtensionCapabilityFactory capabilityFactory)
        {
            this.scope = scope;
            this.services = services;
            this.refreshService = refreshService;
            this.lifecycleManager = lifecycleManager;
            SnapshotHolder = snapshotHolder;
            RuntimeState = runtimeState;
            EndpointPublisher = endpointPublisher;
            CapabilityFactory = capabilityFactory;
        }

        internal PostgresTestDatabase Database => scope.Database;
        internal HostConfigurationSnapshotHolder SnapshotHolder { get; }
        internal HostRuntimeState RuntimeState { get; }
        internal HostServiceEndpointSnapshotPublisher EndpointPublisher { get; }
        internal IExtensionCapabilityFactory CapabilityFactory { get; }

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
                        new Nekolla.Nekostick.Proxy.MicroserviceDrainTracker());
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
                var signal = new InitialRefreshSignal();
                var refreshService = new HostConfigurationRefreshService(
                    snapshotHolder,
                    services.GetRequiredService<IHostConfigurationSnapshotReader>(),
                    signal,
                    runtimeState,
                    services.GetRequiredService<IServiceScopeFactory>(),
                    runtimeOptions,
                    services.GetRequiredService<HostConfigurationPublisher>(),
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
                    snapshotHolder,
                    runtimeState,
                    endpointPublisher,
                    services.GetRequiredService<IExtensionCapabilityFactory>());
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
        public ValueTask<ProcessOperationResult> StartAsync(
            ProcessLaunchSpecification specification,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                new ProcessOperationResult(
                    ProcessOperationStatus.Accepted,
                    ServiceStateReasonCode.StartAccepted,
                    new ProcessInstanceId(Guid.CreateVersion7())));

        public ValueTask<ProcessOperationResult> StopAsync(
            Guid serviceId,
            TimeSpan gracePeriod,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                new ProcessOperationResult(
                    ProcessOperationStatus.Completed,
                    ServiceStateReasonCode.StopCompleted));
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
        private readonly ConcurrentDictionary<Guid, PortLease> leases = new();

        public ValueTask<PortLeaseOperationResult> ApplyAsync(
            PortLeaseIntent intent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (intent.Kind)
            {
                case PortLeaseIntentKind.Acquire when intent.Request is { } request:
                {
                    var now = DateTimeOffset.UtcNow;
                    var port = request.AutomaticPortRangeStart ?? 21000;
                    var lease = new PortLease(
                        request.NodeId,
                        request.ServiceId,
                        port,
                        now,
                        now.AddMinutes(1),
                        version: 1);
                    leases[request.ServiceId] = lease;
                    return ValueTask.FromResult(new PortLeaseOperationResult(
                        PortLeaseOperationStatus.Applied,
                        lease));
                }
                case PortLeaseIntentKind.Renew when intent.Renewal is { } renewal:
                {
                    if (!leases.TryGetValue(renewal.ServiceId, out var current))
                    {
                        return ValueTask.FromResult(new PortLeaseOperationResult(
                            PortLeaseOperationStatus.NotFound));
                    }

                    var now = DateTimeOffset.UtcNow;
                    var lease = new PortLease(
                        renewal.NodeId,
                        renewal.ServiceId,
                        renewal.Port,
                        current.AcquiredAt,
                        now.AddMinutes(1),
                        checked(current.Version + 1));
                    leases[renewal.ServiceId] = lease;
                    return ValueTask.FromResult(new PortLeaseOperationResult(
                        PortLeaseOperationStatus.Applied,
                        lease));
                }
                case PortLeaseIntentKind.Release when intent.Release is { } release:
                    if (!leases.TryRemove(release.ServiceId, out var released))
                    {
                        return ValueTask.FromResult(new PortLeaseOperationResult(
                            PortLeaseOperationStatus.NotFound));
                    }

                    return ValueTask.FromResult(new PortLeaseOperationResult(
                        PortLeaseOperationStatus.Applied,
                        released));
                default:
                    return ValueTask.FromResult(new PortLeaseOperationResult(
                        PortLeaseOperationStatus.Rejected));
            }
        }
    }
}
