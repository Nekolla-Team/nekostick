using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ExtensionServiceOutputContractTests
{
    private static readonly Guid ServiceId =
        new("0198a1af-6e94-7b25-9732-59c9075b14f6");

    [Fact]
    public void OutputChunkValidatesUuidStreamAndNormalizesTimestamp()
    {
        var local = new DateTimeOffset(2026, 8, 19, 12, 34, 56, TimeSpan.FromHours(5));
        var data = new byte[] { 1, 2, 3 };
        var chunk = new ExtensionServiceOutputChunk(
            ServiceId,
            ExtensionServiceOutputStream.Stderr,
            local,
            data);

        Assert.Equal(ServiceId, chunk.ServiceId);
        Assert.Equal(ExtensionServiceOutputStream.Stderr, chunk.Stream);
        Assert.Equal(TimeSpan.Zero, chunk.Timestamp.Offset);
        Assert.Equal(local.UtcDateTime, chunk.Timestamp.UtcDateTime);
        Assert.Same(data, chunk.Data);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExtensionServiceOutputChunk(
                ServiceId,
                (ExtensionServiceOutputStream)99,
                local,
                data));
        Assert.Throws<ArgumentException>(() =>
            new ExtensionServiceOutputChunk(
                Guid.Parse("0198a1af-6e94-4b25-9732-59c9075b14f6"),
                ExtensionServiceOutputStream.Stdout,
                local,
                data));
    }

    [Fact]
    public void OutputResultsRequirePayloadExactlyWhenSucceeded()
    {
        using var stream = new MemoryStream();
        var subscription = new TestSubscription();

        var opened = new ExtensionServiceOutputStreamResult(
            true,
            ExtensionServiceOutputCode.Opened,
            ServiceId,
            stream);
        var failedStream = new ExtensionServiceOutputStreamResult(
            false,
            ExtensionServiceOutputCode.NotRunning,
            ServiceId,
            null);
        var subscribed = new ExtensionServiceOutputSubscriptionResult(
            true,
            ExtensionServiceOutputCode.Opened,
            ServiceId,
            subscription);
        var failedSubscription = new ExtensionServiceOutputSubscriptionResult(
            false,
            ExtensionServiceOutputCode.Unsupported,
            ServiceId,
            null);

        Assert.Same(stream, opened.Stream);
        Assert.Null(failedStream.Stream);
        Assert.Same(subscription, subscribed.Subscription);
        Assert.Null(failedSubscription.Subscription);
        Assert.Throws<ArgumentNullException>(() =>
            new ExtensionServiceOutputStreamResult(
                true,
                ExtensionServiceOutputCode.Opened,
                ServiceId,
                null));
        Assert.Throws<ArgumentException>(() =>
            new ExtensionServiceOutputStreamResult(
                false,
                ExtensionServiceOutputCode.Failed,
                ServiceId,
                new MemoryStream()));
        Assert.Throws<ArgumentNullException>(() =>
            new ExtensionServiceOutputSubscriptionResult(
                true,
                ExtensionServiceOutputCode.Opened,
                ServiceId,
                null));
        Assert.Throws<ArgumentException>(() =>
            new ExtensionServiceOutputSubscriptionResult(
                false,
                ExtensionServiceOutputCode.Failed,
                ServiceId,
                new TestSubscription()));
    }

    [Fact]
    public void OutputEnumsExposeTheStableSurface()
    {
        Assert.Equal(
            [ExtensionServiceOutputStream.Stdout, ExtensionServiceOutputStream.Stderr],
            Enum.GetValues<ExtensionServiceOutputStream>());
        Assert.Equal(
            [
                ExtensionServiceOutputCompletionReason.ProcessExited,
                ExtensionServiceOutputCompletionReason.Faulted,
                ExtensionServiceOutputCompletionReason.HostTeardown
            ],
            Enum.GetValues<ExtensionServiceOutputCompletionReason>());
        Assert.Equal(
            [
                ExtensionServiceOutputCode.None,
                ExtensionServiceOutputCode.Opened,
                ExtensionServiceOutputCode.NotFound,
                ExtensionServiceOutputCode.NotRunning,
                ExtensionServiceOutputCode.Unsupported,
                ExtensionServiceOutputCode.Failed
            ],
            Enum.GetValues<ExtensionServiceOutputCode>());
    }

    [Fact]
    public void CapabilitySetCarriesTheOptionalServiceOutputSurface()
    {
        var unsupported = Nekolla.Nekostick.Extensions.UnsupportedExtensionCapabilities.Create();
        var set = new ExtensionCapabilitySet(
            unsupported.ConfigurationApi,
            unsupported.Routes,
            unsupported.Services,
            unsupported.Endpoints,
            unsupported.FullConfiguration,
            unsupported.Supervisor,
            unsupported.RouteEvents,
            unsupported.LogWriter,
            unsupported.ExtensionManagement,
            unsupported.HostInfo,
            unsupported.ServiceOutput);

        Assert.Same(unsupported.ServiceOutput, set.ServiceOutput);
        Assert.Equal(new HostApiVersion(1, 4, 0), HostApiVersion.Current);
    }

    private sealed class TestSubscription : IExtensionServiceOutputSubscription
    {
        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
