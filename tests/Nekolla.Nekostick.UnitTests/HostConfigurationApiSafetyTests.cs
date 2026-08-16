using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostConfigurationSafetyTests
{
    private const string ConnectionSecret = "configuration-secret";

    private const string RawDatabaseFailure = "raw-database-exception-details";

    [Fact]
    public void ConfigurationResultsExposeOnlySafeErrorBranches()
    {
        var error = new ConfigurationError(ConfigurationErrorCode.StorageUnavailable);
        var read = ConfigurationReadResult<HostConfigurationSnapshot>.Failure(error);
        var write = ConfigurationWriteResult.Failure(error);
        var serialized = JsonSerializer.Serialize(new { ReadErrors = read.Errors, WriteErrors = write.Errors });

        Assert.False(read.IsSuccess);
        Assert.Null(read.Value);
        Assert.Single(read.Errors);
        Assert.Same(error, read.Errors[0]);
        Assert.False(write.IsSuccess);
        Assert.Null(write.NewVersion);
        Assert.Single(write.Errors);
        Assert.Same(error, write.Errors[0]);
        Assert.Equal("Configuration storage is unavailable.", error.Message);
        Assert.DoesNotContain(ConnectionSecret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(RawDatabaseFailure, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeStartsFailClosedUntilACompleteSnapshotIsAccepted()
    {
        var holder = new HostConfigurationSnapshotHolder();
        var state = CreateRuntimeState(holder, readOnly: false);

        Assert.False(state.IsReady);
        AssertFailClosed(state.Status);
    }

    [Fact]
    public void AcceptedSnapshotEnablesRuntimeCapabilities()
    {
        var holder = CreateHolder();
        var state = CreateRuntimeState(holder, readOnly: false);

        state.MarkSnapshotAccepted();

        Assert.True(state.IsReady);
        Assert.True(state.ConfigurationWritesAllowed);
        Assert.True(state.NewLeasesAllowed);
        Assert.True(state.NewServicesAllowed);
        Assert.Equal(HostReadinessState.Ready, state.Status.Readiness);
    }

    [Fact]
    public void DatabaseUnavailableKeepsSnapshotRoutableButDisablesCapabilities()
    {
        var holder = CreateHolder();
        var state = CreateRuntimeState(holder, readOnly: false);
        state.MarkSnapshotAccepted();

        state.MarkDatabaseUnavailable();

        var status = state.Status;
        Assert.True(status.SnapshotAvailable);
        Assert.False(status.DatabaseAvailable);
        Assert.False(status.ConfigurationValid);
        Assert.False(status.ConfigurationWritesAllowed);
        Assert.False(status.NewLeasesAllowed);
        Assert.False(status.NewServicesAllowed);
        Assert.Equal(HostReadinessState.Degraded, status.Readiness);
    }

    [Fact]
    public void RejectedSnapshotPreservesDatabaseStateButDisablesConfigurationCapabilities()
    {
        var holder = CreateHolder();
        var state = CreateRuntimeState(holder, readOnly: false);
        state.MarkSnapshotAccepted();

        state.MarkSnapshotRejected();

        var status = state.Status;
        Assert.True(status.SnapshotAvailable);
        Assert.True(status.DatabaseAvailable);
        Assert.False(status.ConfigurationValid);
        Assert.False(status.ConfigurationWritesAllowed);
        Assert.False(status.NewLeasesAllowed);
        Assert.False(status.NewServicesAllowed);
        Assert.Equal(HostReadinessState.Degraded, status.Readiness);
    }

    [Fact]
    public void ReadOnlyRuntimeKeepsSnapshotAndReadinessWithoutAllowingWrites()
    {
        var holder = CreateHolder();
        var state = CreateRuntimeState(holder, readOnly: true);
        state.MarkSnapshotAccepted();

        var status = state.Status;
        Assert.True(status.SnapshotAvailable);
        Assert.True(status.DatabaseAvailable);
        Assert.True(status.ConfigurationValid);
        Assert.False(status.ConfigurationWritesAllowed);
        Assert.True(status.NewLeasesAllowed);
        Assert.True(status.NewServicesAllowed);
        Assert.Equal(HostReadinessState.Ready, status.Readiness);
    }

    private static HostConfigurationSnapshotHolder CreateHolder()
    {
        var holder = new HostConfigurationSnapshotHolder();
        var snapshot = new HostConfigurationSnapshot(
            1,
            new GlobalSettingsConfiguration(version: 1),
            default,
            default,
            default,
            default);

        Assert.True(holder.TryReplace(snapshot));
        return holder;
    }

    private static HostRuntimeState CreateRuntimeState(
        HostConfigurationSnapshotHolder holder,
        bool readOnly)
    {
        return new HostRuntimeState(
            holder,
            new HostNodeOptions(skipExtensions: false, disableSupervisor: false, readOnly));
    }

    private static void AssertFailClosed(HostRuntimeStatus status)
    {
        Assert.False(status.SnapshotAvailable);
        Assert.False(status.DatabaseAvailable);
        Assert.False(status.ConfigurationValid);
        Assert.False(status.ConfigurationWritesAllowed);
        Assert.False(status.NewLeasesAllowed);
        Assert.False(status.NewServicesAllowed);
        Assert.Equal(HostReadinessState.Unready, status.Readiness);
    }
}
