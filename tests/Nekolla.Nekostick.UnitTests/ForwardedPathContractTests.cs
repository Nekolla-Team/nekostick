using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Routing;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ForwardedPathContractTests
{
    [Fact]
    public void PreserveUsesTheNormalizedPathAndProducesOnlyAPath()
    {
        var routeId = RoutingTestData.Id(400);
        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                routeId,
                RouteMatcherType.Exact,
                "/preserve/item//%2F",
                forwarding: new ForwardingConfiguration(ForwardingMode.Preserve, null)));

        var result = snapshot.Match(
            new RouteMatchInput("/preserve/./item//%2F", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        Assert.Equal(routeId, result.Match!.RouteId);
        Assert.Equal("/preserve/item//%2F", result.Match.NormalizedPath);
        Assert.Equal("/preserve/item//%2F", result.Match.MatchedText);
        Assert.Equal("/preserve/item//%2F", result.Match.ForwardedPath);
        Assert.DoesNotContain("?", result.Match.ForwardedPath);
    }

    [Fact]
    public void StripExactAndSegmentPrefixKeepTheRemainingPathTextual()
    {
        var exactId = RoutingTestData.Id(401);
        var exactSnapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                exactId,
                RouteMatcherType.Exact,
                "/exact",
                forwarding: new ForwardingConfiguration(ForwardingMode.Strip, null)));

        var exactResult = exactSnapshot.Match(
            new RouteMatchInput("/exact", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, exactResult.Status);
        Assert.Equal(exactId, exactResult.Match!.RouteId);
        Assert.Equal("/", exactResult.Match.ForwardedPath);

        var prefixId = RoutingTestData.Id(402);
        var prefixSnapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                prefixId,
                RouteMatcherType.Prefix,
                "/api/*",
                forwarding: new ForwardingConfiguration(ForwardingMode.Strip, null)));

        var prefixResult = prefixSnapshot.Match(
            new RouteMatchInput("/api//%2F//item", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, prefixResult.Status);
        Assert.Equal(prefixId, prefixResult.Match!.RouteId);
        Assert.Equal("/api//%2F//item", prefixResult.Match.NormalizedPath);
        Assert.Equal("/api/", prefixResult.Match.MatchedText);
        Assert.Equal("/%2F//item", prefixResult.Match.ForwardedPath);
    }

    [Fact]
    public void StripEmptyRemainderFromTrailingSlashRetryNormalizesToRoot()
    {
        var routeId = RoutingTestData.Id(403);
        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                routeId,
                RouteMatcherType.Prefix,
                "/empty/*",
                forwarding: new ForwardingConfiguration(ForwardingMode.Strip, null)));

        var result = snapshot.Match(
            new RouteMatchInput("/empty", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        Assert.Equal(routeId, result.Match!.RouteId);
        Assert.Equal("/empty", result.Match.NormalizedPath);
        Assert.Equal("/empty/", result.Match.MatchedText);
        Assert.Equal("/", result.Match.ForwardedPath);
    }

    [Fact]
    public void RawPrefixStripIsRejectedWhileRawPrefixPreserveRemainsAValidMatch()
    {
        var rawStrip = RoutingTestData.CreateRoute(
            RoutingTestData.Id(404),
            RouteMatcherType.Prefix,
            "/raw*",
            forwarding: new ForwardingConfiguration(ForwardingMode.Strip, null));

        var rejected = RouteMatchSnapshotBuilder.Build(new[] { rawStrip });

        Assert.False(rejected.IsSuccess);
        Assert.Null(rejected.Snapshot);
        Assert.Contains(rejected.Errors, error =>
            error.RouteId == rawStrip.Id &&
            error.Code == RouteConfigurationErrorCode.InvalidForwarding);

        var rawPreserveId = RoutingTestData.Id(405);
        var rawPreserve = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                rawPreserveId,
                RouteMatcherType.Prefix,
                "/raw*",
                forwarding: new ForwardingConfiguration(ForwardingMode.Preserve, null)));
        var result = rawPreserve.Match(
            new RouteMatchInput("/raw//%2F", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        Assert.Equal(rawPreserveId, result.Match!.RouteId);
        Assert.Equal("/raw", result.Match.MatchedText);
        Assert.Equal("/raw//%2F", result.Match.ForwardedPath);
    }

    [Fact]
    public void ReplaceExpandsPathMatchAndAllRegexCaptureGroups()
    {
        var routeId = RoutingTestData.Id(410);
        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                routeId,
                RouteMatcherType.Regex,
                "(/items)/(alpha)",
                forwarding: new ForwardingConfiguration(
                    ForwardingMode.Replace,
                    "/forward/{path}/{match}/$0/$1/$2")));

        var result = snapshot.Match(
            new RouteMatchInput("/items/alpha", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        Assert.Equal(routeId, result.Match!.RouteId);
        Assert.Equal(
            "/forward//items/alpha//items/alpha//items/alpha//items/alpha",
            result.Match.ForwardedPath);
    }

    [Fact]
    public void ReplaceEncodesGeneratedPathCharactersWhilePreservingExistingEscapes()
    {
        var routeId = RoutingTestData.Id(411);
        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                routeId,
                RouteMatcherType.Regex,
                "/items/hello world/%2F",
                forwarding: new ForwardingConfiguration(
                    ForwardingMode.Replace,
                    "/safe/{path}")));

        var result = snapshot.Match(
            new RouteMatchInput("/items/hello world/%2F", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        Assert.Equal("/safe//items/hello%20world/%2F", result.Match!.ForwardedPath);
    }

    [Fact]
    public void InvalidReplacementTemplatesAreRejectedDuringSnapshotBuild()
    {
        var missingGroup = RoutingTestData.CreateRoute(
            RoutingTestData.Id(406),
            RouteMatcherType.Regex,
            "(/capture)",
            forwarding: new ForwardingConfiguration(ForwardingMode.Replace, "/out/$2"));
        var nonRegexGroup = RoutingTestData.CreateRoute(
            RoutingTestData.Id(407),
            RouteMatcherType.Prefix,
            "/prefix/*",
            forwarding: new ForwardingConfiguration(ForwardingMode.Replace, "/out/$0"));
        var unknownToken = RoutingTestData.CreateRoute(
            RoutingTestData.Id(408),
            RouteMatcherType.Exact,
            "/token",
            forwarding: new ForwardingConfiguration(ForwardingMode.Replace, "/out/{segment}"));
        var queryTemplate = RoutingTestData.CreateRoute(
            RoutingTestData.Id(409),
            RouteMatcherType.Exact,
            "/query-template",
            forwarding: new ForwardingConfiguration(ForwardingMode.Replace, "/out?next"));

        var nonAbsoluteCapture = RoutingTestData.CreateRoute(
            RoutingTestData.Id(410),
            RouteMatcherType.Regex,
            "(/capture-output)",
            forwarding: new ForwardingConfiguration(ForwardingMode.Replace, "$1"));
        var controlTemplate = RoutingTestData.CreateRoute(
            RoutingTestData.Id(411),
            RouteMatcherType.Exact,
            "/control-template",
            forwarding: new ForwardingConfiguration(ForwardingMode.Replace, "/out/\r\n"));

        var result = RouteMatchSnapshotBuilder.Build(
            new[]
            {
                missingGroup,
                nonRegexGroup,
                unknownToken,
                queryTemplate,
                nonAbsoluteCapture,
                controlTemplate
            });

        Assert.False(result.IsSuccess);
        Assert.Null(result.Snapshot);
        Assert.Equal(6, result.Errors.Length);
        Assert.All(result.Errors, error =>
            Assert.Equal(RouteConfigurationErrorCode.InvalidReplacementTemplate, error.Code));
    }
}
