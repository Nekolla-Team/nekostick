using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Routing;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class RoutingTimeoutBehaviorTests
{
    [Fact]
    public void TimedOutRegexCandidateIsReportedAndLaterRegexCandidateCanMatch()
    {
        var timedOutId = RoutingTestData.Id(320);
        var matchingId = RoutingTestData.Id(321);
        var evaluator = new DeterministicRegexEvaluator(
            ImmutableDictionary<Guid, RouteRegexEvaluationOutcome>.Empty
                .Add(timedOutId, RouteRegexEvaluationOutcome.TimedOut)
                .Add(matchingId, RouteRegexEvaluationOutcome.Matched));
        var build = RouteMatchSnapshotBuilder.Build(
            new[]
            {
                RoutingTestData.CreateRoute(timedOutId, RouteMatcherType.Regex, "/timeout"),
                RoutingTestData.CreateRoute(matchingId, RouteMatcherType.Regex, "/timeout")
            },
            evaluator);
        var snapshot = build.Snapshot ?? throw new InvalidOperationException("The test route set must compile.");

        var result = snapshot.Match(new RouteMatchInput("/timeout", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        Assert.Equal(matchingId, result.Match!.RouteId);
        Assert.Equal(new[] { timedOutId }, result.RegexTimeoutRouteIds);
    }

    [Fact]
    public void AllTimedOutRegexCandidatesProduceStableSafeNoMatch()
    {
        var firstId = RoutingTestData.Id(322);
        var secondId = RoutingTestData.Id(323);
        var evaluator = new DeterministicRegexEvaluator(
            ImmutableDictionary<Guid, RouteRegexEvaluationOutcome>.Empty
                .Add(firstId, RouteRegexEvaluationOutcome.TimedOut)
                .Add(secondId, RouteRegexEvaluationOutcome.TimedOut));
        var build = RouteMatchSnapshotBuilder.Build(
            new[]
            {
                RoutingTestData.CreateRoute(firstId, RouteMatcherType.Regex, "/all-timeout/?"),
                RoutingTestData.CreateRoute(secondId, RouteMatcherType.Regex, "/all-timeout/?")
            },
            evaluator);
        var snapshot = build.Snapshot ?? throw new InvalidOperationException("The test route set must compile.");

        var first = snapshot.Match(new RouteMatchInput("/all-timeout", "example.test", "GET"));
        var second = snapshot.Match(new RouteMatchInput("/all-timeout", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.NoMatch, first.Status);
        Assert.Equal(RouteNoMatchReason.NoRoute, first.NoMatchReason);
        Assert.Equal(
            new[] { firstId, secondId, firstId, secondId },
            first.RegexTimeoutRouteIds);
        Assert.Equal(first.RegexTimeoutRouteIds, second.RegexTimeoutRouteIds);
    }
}
