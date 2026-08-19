using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class RestartBackoffPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DefaultMatchesDocumentedRuntimePolicy()
    {
        var policy = RestartBackoffPolicy.Default;

        Assert.Equal(TimeSpan.FromSeconds(1), policy.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.MaximumDelay);
        Assert.Equal(TimeSpan.Zero, policy.MaximumJitter);
        Assert.Equal(10, policy.MaximumAttempts);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.AttemptWindow);
    }

    [Fact]
    public void DefaultRestartPlanningUsesExponentialCapAndStopsAfterTenAttempts()
    {
        var policy = RestartBackoffPolicy.Default;
        var state = RestartAttemptState.Empty;
        var expectedDelays = new[]
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30)
        };

        for (var index = 0; index < expectedDelays.Length; index++)
        {
            var plan = RestartPlanner.Plan(
                ServiceRestartPolicy.OnFailure,
                successfulExit: false,
                state,
                Now,
                policy,
                new NoRestartJitter(),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(plan.ShouldRestart);
            Assert.Equal(index + 1, plan.Attempt);
            Assert.Equal(expectedDelays[index], plan.BaseDelay);
            Assert.Equal(TimeSpan.Zero, plan.Jitter);
            Assert.Equal(Now.Add(expectedDelays[index]), plan.NotBefore!.Value);
            state = plan.NextAttemptState;
        }

        var exhausted = RestartPlanner.Plan(
            ServiceRestartPolicy.OnFailure,
            successfulExit: false,
            state,
            Now,
            policy,
            new NoRestartJitter(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(exhausted.ShouldRestart);
        Assert.Equal(ServiceStateReasonCode.RestartAttemptLimitReached, exhausted.Reason);
        Assert.Equal(10, exhausted.NextAttemptState.Attempts);
    }

    [Fact]
    public void ExplicitBackoffValuesRemainUnchanged()
    {
        var policy = new RestartBackoffPolicy(
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(7),
            TimeSpan.FromMilliseconds(50),
            3,
            TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromMilliseconds(250), policy.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(7), policy.MaximumDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(50), policy.MaximumJitter);
        Assert.Equal(3, policy.MaximumAttempts);
        Assert.Equal(TimeSpan.FromMinutes(2), policy.AttemptWindow);
    }
}
