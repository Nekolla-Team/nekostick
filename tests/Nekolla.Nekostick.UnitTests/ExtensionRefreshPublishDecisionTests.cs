using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Host;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ExtensionRefreshPublishDecisionTests
{
    [Fact]
    public void NoChangesSkipsPublicationAndReportsWhy()
    {
        var decision = ExtensionManagementFacade.GetRefreshPublishDecision(
            addedCount: 0,
            versionUpdatedCount: 0,
            contentHashUpdatedCount: 0,
            hasRunningContentHashDrift: false,
            hasLoadedRecordNotRunning: false);

        Assert.False(decision.ShouldPublish);
        Assert.Equal("NoChanges", decision.Reason);
    }

    [Fact]
    public void DurableRecordOrContentHashChangesTriggerPublication()
    {
        AssertTrigger(
            addedCount: 1,
            versionUpdatedCount: 0,
            contentHashUpdatedCount: 0);
        AssertTrigger(
            addedCount: 0,
            versionUpdatedCount: 1,
            contentHashUpdatedCount: 0);
        AssertTrigger(
            addedCount: 0,
            versionUpdatedCount: 0,
            contentHashUpdatedCount: 1);
    }

    [Fact]
    public void RunningContentHashDriftTriggersPublicationWithoutDurableRecordChanges()
    {
        var decision = ExtensionManagementFacade.GetRefreshPublishDecision(
            addedCount: 0,
            versionUpdatedCount: 0,
            contentHashUpdatedCount: 0,
            hasRunningContentHashDrift: true,
            hasLoadedRecordNotRunning: false);

        Assert.True(decision.ShouldPublish);
        Assert.Equal("RunningContentHashDrift", decision.Reason);
    }

    [Fact]
    public void LoadedRecordPresentInScanButMissingFromRuntimeTriggersPublication()
    {
        var decision = ExtensionManagementFacade.GetRefreshPublishDecision(
            addedCount: 0,
            versionUpdatedCount: 0,
            contentHashUpdatedCount: 0,
            hasRunningContentHashDrift: false,
            hasLoadedRecordNotRunning: true);

        Assert.True(decision.ShouldPublish);
        Assert.Equal("LoadedRecordNotRunning", decision.Reason);
    }
    [Theory]
    [InlineData(false, "NoChanges", 0)]
    [InlineData(true, "RunningContentHashDrift", 0)]
    [InlineData(true, "DurableConfigurationChange", 1)]
    [InlineData(true, "LoadedRecordNotRunning", 0)]
    public void RefreshCompletedEventRecordsPublicationDecision(
        bool publishTriggered,
        string publishReason,
        int contentHashUpdated)
    {
        var logger = new CapturingLogger();

        HostLogMessages.ExtensionRefreshCompleted(
            logger,
            "extension.refresh-test",
            added: 0,
            versionUpdated: 0,
            contentHashUpdated: contentHashUpdated,
            missing: 0,
            skipped: 0,
            publishTriggered: publishTriggered,
            publishReason: publishReason);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(1062, entry.EventId.Id);
        Assert.Equal(publishTriggered, (bool)entry.Fields["PublishTriggered"]!);
        Assert.Equal(contentHashUpdated, (int)entry.Fields["ContentHashUpdated"]!);
        Assert.Equal(publishReason, entry.Fields["PublishReason"]);
    }

    private sealed class CapturingLogger : ILogger
    {
        internal List<CapturedLog> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var value in values)
                {
                    fields[value.Key] = value.Value;
                }
            }

            Entries.Add(new CapturedLog(eventId, fields));
        }
    }

    private sealed record CapturedLog(
        EventId EventId,
        IReadOnlyDictionary<string, object?> Fields);

    private static void AssertTrigger(
        int addedCount,
        int versionUpdatedCount,
        int contentHashUpdatedCount)
    {
        var decision = ExtensionManagementFacade.GetRefreshPublishDecision(
            addedCount,
            versionUpdatedCount,
            contentHashUpdatedCount,
            hasRunningContentHashDrift: false,
            hasLoadedRecordNotRunning: false);

        Assert.True(decision.ShouldPublish);
        Assert.Equal("DurableConfigurationChange", decision.Reason);
    }
}
