using Nekolla.Nekostick.Contracts;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ExtensionServiceSubscriptionContractTests
{
    private static readonly Guid ServiceId =
        new("0198a1af-6e94-7b25-9732-59c9075b14f6");

    [Fact]
    public void ServiceLogSubscriptionResultRequiresPayloadAndFailureDetail()
    {
        var subscription = new TestSubscription();
        var failureDetail = new ExtensionErrorDetail("Service log capture is unsupported.");
        var succeeded = new ExtensionServiceLogSubscriptionResult(
            true,
            ExtensionServiceLogCode.Subscribed,
            ServiceId,
            subscription,
            detail: null);
        var failed = new ExtensionServiceLogSubscriptionResult(
            false,
            ExtensionServiceLogCode.Unsupported,
            ServiceId,
            null,
            failureDetail);

        Assert.True(succeeded.Succeeded);
        Assert.Same(subscription, succeeded.Subscription);
        Assert.Null(succeeded.Detail);
        Assert.False(failed.Succeeded);
        Assert.Null(failed.Subscription);
        Assert.Same(failureDetail, failed.Detail);
        Assert.Throws<ArgumentException>(() =>
            new ExtensionServiceLogSubscriptionResult(
                true,
                ExtensionServiceLogCode.Unsupported,
                ServiceId,
                subscription,
                detail: null));

        Assert.Throws<ArgumentException>(() =>
            new ExtensionServiceLogSubscriptionResult(
                true,
                ExtensionServiceLogCode.Subscribed,
                ServiceId,
                null,
                detail: null));
        Assert.Throws<ArgumentException>(() =>
            new ExtensionServiceLogSubscriptionResult(
                false,
                ExtensionServiceLogCode.Unsupported,
                ServiceId,
                subscription,
                failureDetail));
        Assert.Throws<ArgumentNullException>(() =>
            new ExtensionServiceLogSubscriptionResult(
                false,
                ExtensionServiceLogCode.Unsupported,
                ServiceId,
                null,
                detail: null));
        Assert.Throws<ArgumentException>(() =>
            new ExtensionServiceLogSubscriptionResult(
                true,
                ExtensionServiceLogCode.Subscribed,
                ServiceId,
                subscription,
                new ExtensionErrorDetail("A successful result cannot include error detail.")));
    }

    [Fact]
    public void RuntimeStateSubscriptionResultRequiresPayloadAndFailureDetail()
    {
        var subscription = new TestSubscription();
        var failureDetail = new ExtensionErrorDetail("Runtime-state subscriptions are unsupported.");
        var succeeded = new ExtensionServiceRuntimeStateSubscriptionResult(
            true,
            ExtensionServiceRuntimeStateSubscriptionCode.Subscribed,
            subscription,
            detail: null);
        var failed = new ExtensionServiceRuntimeStateSubscriptionResult(
            false,
            ExtensionServiceRuntimeStateSubscriptionCode.Unsupported,
            null,
            failureDetail);

        Assert.True(succeeded.Succeeded);
        Assert.Same(subscription, succeeded.Subscription);
        Assert.Null(succeeded.Detail);
        Assert.False(failed.Succeeded);
        Assert.Null(failed.Subscription);
        Assert.Same(failureDetail, failed.Detail);
        Assert.Throws<ArgumentException>(() =>
            new ExtensionServiceRuntimeStateSubscriptionResult(
                true,
                ExtensionServiceRuntimeStateSubscriptionCode.Unsupported,
                subscription,
                detail: null));

        Assert.Throws<ArgumentException>(() =>
            new ExtensionServiceRuntimeStateSubscriptionResult(
                true,
                ExtensionServiceRuntimeStateSubscriptionCode.Subscribed,
                null,
                detail: null));
        Assert.Throws<ArgumentException>(() =>
            new ExtensionServiceRuntimeStateSubscriptionResult(
                false,
                ExtensionServiceRuntimeStateSubscriptionCode.Unsupported,
                subscription,
                failureDetail));
        Assert.Throws<ArgumentNullException>(() =>
            new ExtensionServiceRuntimeStateSubscriptionResult(
                false,
                ExtensionServiceRuntimeStateSubscriptionCode.Unsupported,
                null,
                detail: null));
        Assert.Throws<ArgumentException>(() =>
            new ExtensionServiceRuntimeStateSubscriptionResult(
                true,
                ExtensionServiceRuntimeStateSubscriptionCode.Subscribed,
                subscription,
                new ExtensionErrorDetail("A successful result cannot include error detail.")));
    }

    private sealed class TestSubscription : IExtensionServiceLogSubscription, IExtensionServiceRuntimeStateSubscription
    {
        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
