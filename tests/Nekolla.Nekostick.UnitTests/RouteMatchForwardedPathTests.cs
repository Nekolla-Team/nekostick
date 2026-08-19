using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Routing;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class RouteMatchForwardedPathTests
{
    [Fact]
    public void ReplaceExpandsPathMatchAndAllRegexGroupsFromTheOriginalMatch()
    {
        var routeId = RoutingTestData.Id(410);
        const string inputPath = "/files//%2F/item";
        const string pattern = @"(/files)(//)(%2F)/(item)";
        var template = "/forward/{path}/match/{match}/zero/$0/one/$1/two/$2/three/$3/four/$4";
        var normalized = RoutePathNormalizer.Normalize(inputPath);

        Assert.True(normalized.IsSuccess);
        Assert.Equal(inputPath, normalized.NormalizedPath);

        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                routeId,
                RouteMatcherType.Regex,
                pattern,
                forwarding: new ForwardingConfiguration(ForwardingMode.Replace, template)));

        var result = snapshot.Match(
            new RouteMatchInput(inputPath, "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        Assert.Null(result.NoMatchReason);
        Assert.Empty(result.RegexTimeoutRouteIds);
        Assert.Equal(routeId, result.Match!.RouteId);
        Assert.Equal("/files//%2F/item", result.Match.NormalizedPath);
        Assert.Equal("/files//%2F/item", result.Match.MatchedText);
        Assert.Equal(
            "/forward//files//%2F/item/match//files//%2F/item/zero//files//%2F/item/one//files/two////three/%2F/four/item",
            result.Match.ForwardedPath);
        Assert.DoesNotContain("?", result.Match.ForwardedPath);
    }

    [Fact]
    public void ReplaceUsesTheSelectedHigherPriorityRegexRoute()
    {
        var lowerPriorityId = RoutingTestData.Id(411);
        var selectedId = RoutingTestData.Id(412);
        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                lowerPriorityId,
                RouteMatcherType.Regex,
                @"(/rank)/(one)",
                priority: 1,
                forwarding: new ForwardingConfiguration(ForwardingMode.Replace, "/low/$1/$2")),
            RoutingTestData.CreateRoute(
                selectedId,
                RouteMatcherType.Regex,
                @"(/rank/one)",
                priority: 2,
                forwarding: new ForwardingConfiguration(ForwardingMode.Replace, "/high/$1")));

        var result = snapshot.Match(
            new RouteMatchInput("/rank/one", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        Assert.Equal(selectedId, result.Match!.RouteId);
        Assert.Equal("/high//rank/one", result.Match.ForwardedPath);
    }

    [Fact]
    public void ReplaceKeepsNormalizedPathDistinctFromMatchedTextOnTrailingSlashRetry()
    {
        var routeId = RoutingTestData.Id(413);
        var template = "/forward{path}/matched{match}";
        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                routeId,
                RouteMatcherType.Prefix,
                "/retry/*",
                forwarding: new ForwardingConfiguration(ForwardingMode.Replace, template)));

        var input = new RouteMatchInput("/retry", "example.test", "GET");
        var result = snapshot.Match(input);
        var repeated = snapshot.Match(input);

        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        Assert.Equal(routeId, result.Match!.RouteId);
        Assert.Equal("/retry", result.Match.NormalizedPath);
        Assert.Equal("/retry/", result.Match.MatchedText);
        Assert.Equal("/forward/retry/matched/retry/", result.Match.ForwardedPath);
        Assert.Equal(result.Match.NormalizedPath, repeated.Match!.NormalizedPath);
        Assert.Equal(result.Match.MatchedText, repeated.Match.MatchedText);
        Assert.Equal(result.Match.ForwardedPath, repeated.Match.ForwardedPath);
    }
}
