using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Routing;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class RoutingRegexAndOrderingTests
{
    [Fact]
    public void RegexMatchesAreAnchoredToTheCompleteNormalizedPath()
    {
        var routeId = RoutingTestData.Id(300);
        var snapshot = Build(
            RoutingTestData.CreateRoute(routeId, RouteMatcherType.Regex, "/anchored"));

        var complete = snapshot.Match(new RouteMatchInput("/anchored", "example.test", "GET"));
        var prefix = snapshot.Match(new RouteMatchInput("/before/anchored", "example.test", "GET"));
        var suffix = snapshot.Match(new RouteMatchInput("/anchored/after", "example.test", "GET"));
        var trailingSlash = snapshot.Match(new RouteMatchInput("/anchored/", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, complete.Status);
        Assert.Equal(routeId, complete.Match!.RouteId);
        Assert.Equal(RouteMatchStatus.NoMatch, prefix.Status);
        Assert.Equal(RouteMatchStatus.NoMatch, suffix.Status);
        Assert.Equal(RouteMatchStatus.NoMatch, trailingSlash.Status);
    }

    [Fact]
    public void RegexPriorityIsScopedToTheRegexLaneAfterMatcherRank()
    {
        var exactId = RoutingTestData.Id(301);
        var regexId = RoutingTestData.Id(302);
        var snapshot = Build(
            RoutingTestData.CreateRoute(
                regexId,
                RouteMatcherType.Regex,
                "/ordered",
                priority: 10_000),
            RoutingTestData.CreateRoute(
                exactId,
                RouteMatcherType.Exact,
                "/ordered",
                priority: -10_000));

        var result = snapshot.Match(new RouteMatchInput("/ordered", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        Assert.Equal(exactId, result.Match!.RouteId);

        var regexPriorityWinner = RoutingTestData.Id(303);
        var regexPrioritySnapshot = Build(
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(304),
                RouteMatcherType.Regex,
                "/ordered",
                priority: -1),
            RoutingTestData.CreateRoute(
                regexPriorityWinner,
                RouteMatcherType.Regex,
                "/ordered",
                priority: 1));

        var regexResult = regexPrioritySnapshot.Match(
            new RouteMatchInput("/ordered", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, regexResult.Status);
        Assert.Equal(regexPriorityWinner, regexResult.Match!.RouteId);
    }

    [Fact]
    public void RegexCreatedAtThenUuidLexicalOrderAreStableWithinTheRegexLane()
    {
        var earlierId = RoutingTestData.Id(305);
        var laterId = RoutingTestData.Id(306);
        var createdSnapshot = Build(
            RoutingTestData.CreateRoute(
                earlierId,
                RouteMatcherType.Regex,
                "/ordered",
                createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            RoutingTestData.CreateRoute(
                laterId,
                RouteMatcherType.Regex,
                "/ordered",
                createdAt: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)));

        var createdResult = createdSnapshot.Match(
            new RouteMatchInput("/ordered", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, createdResult.Status);
        Assert.Equal(earlierId, createdResult.Match!.RouteId);

        var lowerLexicalId = RoutingTestData.Id(307);
        var lexicalSnapshot = Build(
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(308),
                RouteMatcherType.Regex,
                "/ordered",
                createdAt: DateTimeOffset.UnixEpoch),
            RoutingTestData.CreateRoute(
                lowerLexicalId,
                RouteMatcherType.Regex,
                "/ordered",
                createdAt: DateTimeOffset.UnixEpoch));

        var lexicalResult = lexicalSnapshot.Match(
            new RouteMatchInput("/ordered", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, lexicalResult.Status);
        Assert.Equal(lowerLexicalId, lexicalResult.Match!.RouteId);
    }

    [Fact]
    public void RegexCompilationAnd4096CharacterBoundaryAreValidatedAtSnapshotBuild()
    {
        var validId = RoutingTestData.Id(309);
        var tooLongId = RoutingTestData.Id(310);
        var invalidId = RoutingTestData.Id(311);
        var validPattern = "/a(?#" + new string('x', 4090) + ")";
        var tooLongPattern = "/" + new string('a', 4096);
        Assert.Equal(4096, validPattern.Length);
        var result = RouteMatchSnapshotBuilder.Build(
            new[]
            {
                RoutingTestData.CreateRoute(validId, RouteMatcherType.Regex, validPattern),
                RoutingTestData.CreateRoute(tooLongId, RouteMatcherType.Regex, tooLongPattern),
                RoutingTestData.CreateRoute(invalidId, RouteMatcherType.Regex, "[")
            });

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error =>
            error.RouteId == tooLongId &&
            error.Code == RouteConfigurationErrorCode.RegexTooLong);
        Assert.Contains(result.Errors, error =>
            error.RouteId == invalidId &&
            error.Code == RouteConfigurationErrorCode.InvalidRegex);
        Assert.DoesNotContain(result.Errors, error => error.RouteId == validId);
    }

    private static RouteMatchSnapshot Build(params RouteConfiguration[] routes)
    {
        var result = RouteMatchSnapshotBuilder.Build(
            routes,
            new DeterministicRegexEvaluator(ImmutableDictionary<Guid, RouteRegexEvaluationOutcome>.Empty));
        return result.Snapshot ?? throw new InvalidOperationException("The test route set must compile.");
    }
}
