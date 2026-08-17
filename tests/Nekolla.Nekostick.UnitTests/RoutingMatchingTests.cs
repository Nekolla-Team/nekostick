using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Routing;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class RoutingMatchingTests
{
    [Theory]
    [InlineData("/a/./b/../c", "/a/c")]
    [InlineData("/a//b///c", "/a//b///c")]
    [InlineData("/a/%2e/%2E%2E", "/a/%2e/%2E%2E")]
    [InlineData("/a/%2F/%5C", "/a/%2F/%5C")]
    [InlineData("/a/../b", "/b")]
    public void PathNormalizationRemovesOnlyLiteralDotSegments(
        string input,
        string expected)
    {
        var result = RoutePathNormalizer.Normalize(input);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.NormalizedPath);
    }

    [Theory]
    [InlineData("/bad%")]
    [InlineData("/bad%2")]
    [InlineData("/bad%GG")]
    public void MalformedPercentEncodingIsAnInvalidRequestCandidate(string path)
    {
        var result = RoutingTestData.Build(
                RoutingTestData.CreateRoute(
                    RoutingTestData.Id(101),
                    RouteMatcherType.Exact,
                    "/bad"))
            .Match(new RouteMatchInput(path, "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.InvalidRequest, result.Status);
        Assert.Equal(PathNormalizationErrorCode.InvalidPercentEncoding, result.InvalidRequestCode);
        Assert.Null(result.Match);
    }

    [Fact]
    public void ExactAndCaseInsensitivePathMatchingPreservesPercentEncoding()
    {
        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(102),
                RouteMatcherType.ExactCaseInsensitive,
                "/Files/%2F"));

        var result = snapshot.Match(new RouteMatchInput("/files/%2F", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        Assert.Equal(RoutingTestData.Id(102), result.Match!.RouteId);
        Assert.Equal("/files/%2F", result.Match.NormalizedPath);
    }

    [Fact]
    public void FixedMatcherRankCannotBeReversedByPriority()
    {
        var exactId = RoutingTestData.Id(110);
        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(111),
                RouteMatcherType.Regex,
                "/rank",
                priority: 10_000),
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(112),
                RouteMatcherType.PrefixCaseInsensitive,
                "/rank",
                priority: 9_000),
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(113),
                RouteMatcherType.Prefix,
                "/rank",
                priority: 8_000),
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(114),
                RouteMatcherType.ExactCaseInsensitive,
                "/rank",
                priority: 7_000),
            RoutingTestData.CreateRoute(
                exactId,
                RouteMatcherType.Exact,
                "/rank",
                priority: -10_000));

        var result = snapshot.Match(new RouteMatchInput("/rank", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        Assert.Equal(exactId, result.Match!.RouteId);
    }

    [Fact]
    public void PriorityPrecedesNonRegexMatchedLengthForTheSameMatcher()
    {
        var highPriorityId = RoutingTestData.Id(120);
        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(121),
                RouteMatcherType.Prefix,
                "/api/users/*",
                priority: -100),
            RoutingTestData.CreateRoute(
                highPriorityId,
                RouteMatcherType.Prefix,
                "/api/*",
                priority: 100));

        var result = snapshot.Match(new RouteMatchInput("/api/users/list", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        Assert.Equal(highPriorityId, result.Match!.RouteId);
    }

    [Fact]
    public void NonRegexMatchedLengthWinsAfterEqualPriority()
    {
        var longerId = RoutingTestData.Id(122);
        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(123),
                RouteMatcherType.Prefix,
                "/api/*"),
            RoutingTestData.CreateRoute(
                longerId,
                RouteMatcherType.Prefix,
                "/api/users/*"));

        var result = snapshot.Match(new RouteMatchInput("/api/users/list", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        Assert.Equal(longerId, result.Match!.RouteId);
        Assert.Equal("/api/users/", result.Match.MatchedText);
    }

    [Fact]
    public void CreatedAtThenUuidLexicalOrderProvideStableTies()
    {
        var earlierCreated = RoutingTestData.Id(130);
        var lexicallyLowerButLater = RoutingTestData.Id(129);
        var createdSnapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                lexicallyLowerButLater,
                RouteMatcherType.Exact,
                "/tie",
                createdAt: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
            RoutingTestData.CreateRoute(
                earlierCreated,
                RouteMatcherType.Exact,
                "/tie",
                createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var createdResult = createdSnapshot.Match(
            new RouteMatchInput("/tie", "example.test", "GET"));

        Assert.Equal(earlierCreated, createdResult.Match!.RouteId);

        var lexicalSnapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(132),
                RouteMatcherType.Exact,
                "/tie",
                createdAt: DateTimeOffset.UnixEpoch),
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(131),
                RouteMatcherType.Exact,
                "/tie",
                createdAt: DateTimeOffset.UnixEpoch));

        var lexicalResult = lexicalSnapshot.Match(
            new RouteMatchInput("/tie", "example.test", "GET"));

        Assert.Equal(RoutingTestData.Id(131), lexicalResult.Match!.RouteId);
    }

    [Fact]
    public void PrefixSegmentAndRawWildcardSemanticsRemainDistinctWithTrailingSlashRetry()
    {
        var segmentId = RoutingTestData.Id(140);
        var segmentSnapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(segmentId, RouteMatcherType.Prefix, "/api/*"));

        var segmentMatch = segmentSnapshot.Match(
            new RouteMatchInput("/api/users", "example.test", "GET"));
        var segmentBoundaryMiss = segmentSnapshot.Match(
            new RouteMatchInput("/apix", "example.test", "GET"));
        var segmentRootRetryMatch = segmentSnapshot.Match(
            new RouteMatchInput("/api", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, segmentMatch.Status);
        Assert.Equal(segmentId, segmentMatch.Match!.RouteId);
        Assert.Equal("/api/", segmentMatch.Match.MatchedText);
        Assert.Equal(RouteMatchStatus.NoMatch, segmentBoundaryMiss.Status);
        Assert.Equal(RouteNoMatchReason.NoRoute, segmentBoundaryMiss.NoMatchReason);
        Assert.Equal(RouteMatchStatus.Matched, segmentRootRetryMatch.Status);
        Assert.Equal(segmentId, segmentRootRetryMatch.Match!.RouteId);
        Assert.Equal("/api/", segmentRootRetryMatch.Match.MatchedText);
        Assert.Equal("/api", segmentRootRetryMatch.Match.NormalizedPath);

        var prefixCaseInsensitiveId = RoutingTestData.Id(142);
        var prefixCaseInsensitiveSnapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                prefixCaseInsensitiveId,
                RouteMatcherType.PrefixCaseInsensitive,
                "/API/*"));
        var prefixCaseInsensitiveMatch = prefixCaseInsensitiveSnapshot.Match(
            new RouteMatchInput("/api/users", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, prefixCaseInsensitiveMatch.Status);
        Assert.Equal(prefixCaseInsensitiveId, prefixCaseInsensitiveMatch.Match!.RouteId);

        var rawId = RoutingTestData.Id(141);
        var rawSnapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(rawId, RouteMatcherType.Prefix, "/api*"));
        var rawMatch = rawSnapshot.Match(
            new RouteMatchInput("/apix", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, rawMatch.Status);
        Assert.Equal(rawId, rawMatch.Match!.RouteId);
        Assert.Equal("/api", rawMatch.Match.MatchedText);
    }

    [Fact]
    public void HostlessRoutesAcceptNullAndEmptyHostValues()
    {
        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(RoutingTestData.Id(150), RouteMatcherType.Exact, "/public"));

        foreach (var host in new string?[] { null, string.Empty })
        {
            var result = snapshot.Match(new RouteMatchInput("/public", host, "GET"));

            Assert.Equal(RouteMatchStatus.Matched, result.Status);
            Assert.Equal(RoutingTestData.Id(150), result.Match!.RouteId);
        }
    }

    [Fact]
    public void HostAndMethodConditionsAreNormalizedAndExplainNoMatch()
    {
        var hostAndMethodSnapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(151),
                RouteMatcherType.Exact,
                "/admin",
                hostPatterns: ImmutableArray.Create("api.example.test"),
                methods: ImmutableArray.Create("get")));

        var match = hostAndMethodSnapshot.Match(
            new RouteMatchInput("/admin", "API.EXAMPLE.TEST", "gEt"));
        var hostMiss = hostAndMethodSnapshot.Match(
            new RouteMatchInput("/admin", "other.example.test", "GET"));
        var methodMiss = hostAndMethodSnapshot.Match(
            new RouteMatchInput("/admin", "api.example.test", "POST"));
        var bothMiss = hostAndMethodSnapshot.Match(
            new RouteMatchInput("/admin", "other.example.test", "POST"));

        Assert.Equal(RouteMatchStatus.Matched, match.Status);
        Assert.Equal(RouteMatchStatus.NoMatch, hostMiss.Status);
        Assert.Equal(RouteNoMatchReason.HostMismatch, hostMiss.NoMatchReason);
        Assert.Equal(RouteMatchStatus.NoMatch, methodMiss.Status);
        Assert.Equal(RouteNoMatchReason.MethodMismatch, methodMiss.NoMatchReason);
        Assert.Equal(RouteMatchStatus.NoMatch, bothMiss.Status);
        Assert.Equal(RouteNoMatchReason.ConditionMismatch, bothMiss.NoMatchReason);
    }

    [Fact]
    public void HostConstrainedRouteWithAbsentHostIsNoMatch()
    {
        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(152),
                RouteMatcherType.Exact,
                "/private",
                hostPatterns: ImmutableArray.Create("api.example.test")));

        foreach (var host in new string?[] { null, string.Empty })
        {
            var result = snapshot.Match(new RouteMatchInput("/private", host, "GET"));

            Assert.Equal(RouteMatchStatus.NoMatch, result.Status);
            Assert.Equal(RouteNoMatchReason.HostMismatch, result.NoMatchReason);
        }
    }

    [Fact]
    public void HostWildcardMatchesSubdomainsButNotTheBaseDomain()
    {
        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(153),
                RouteMatcherType.Exact,
                "/tenant",
                hostPatterns: ImmutableArray.Create("*.example.test")));

        var subdomain = snapshot.Match(
            new RouteMatchInput("/tenant", "one.example.test", "GET"));
        var baseDomain = snapshot.Match(
            new RouteMatchInput("/tenant", "example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, subdomain.Status);
        Assert.Equal(RouteMatchStatus.NoMatch, baseDomain.Status);
        Assert.Equal(RouteNoMatchReason.HostMismatch, baseDomain.NoMatchReason);
    }

    [Fact]
    public void TrailingSlashRetrySelectsOnlyAfterTheInitialPathHasNoUsableMatch()
    {
        var retryId = RoutingTestData.Id(160);
        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(161),
                RouteMatcherType.Exact,
                "/retry",
                hostPatterns: ImmutableArray.Create("allowed.example.test")),
            RoutingTestData.CreateRoute(retryId, RouteMatcherType.Exact, "/retry/"));

        var result = snapshot.Match(
            new RouteMatchInput("/retry", "other.example.test", "GET"));

        Assert.Equal(RouteMatchStatus.Matched, result.Status);
        Assert.Equal(retryId, result.Match!.RouteId);
        Assert.Equal("/retry", result.Match.NormalizedPath);
    }

    [Fact]
    public void NoMatchReasonsDistinguishAbsentPathFromConditions()
    {
        var snapshot = RoutingTestData.Build(
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(170),
                RouteMatcherType.Exact,
                "/host",
                hostPatterns: ImmutableArray.Create("api.example.test")),
            RoutingTestData.CreateRoute(
                RoutingTestData.Id(171),
                RouteMatcherType.Exact,
                "/method",
                methods: ImmutableArray.Create("POST")));

        var absent = snapshot.Match(new RouteMatchInput("/absent", "example.test", "GET"));
        var host = snapshot.Match(new RouteMatchInput("/host", "example.test", "GET"));
        var method = snapshot.Match(new RouteMatchInput("/method", "example.test", "GET"));

        Assert.Equal(RouteNoMatchReason.NoRoute, absent.NoMatchReason);
        Assert.Equal(RouteNoMatchReason.HostMismatch, host.NoMatchReason);
        Assert.Equal(RouteNoMatchReason.MethodMismatch, method.NoMatchReason);
    }

    [Fact]
    public void InvalidRouteConfigurationIsRejectedBySnapshotBuild()
    {
        var malformedPath = RoutingTestData.CreateRoute(
            RoutingTestData.Id(180),
            RouteMatcherType.Exact,
            "/bad%2");
        var malformedPrefix = RoutingTestData.CreateRoute(
            RoutingTestData.Id(181),
            RouteMatcherType.Prefix,
            "/api*tail");
        var malformedRegex = RoutingTestData.CreateRoute(
            RoutingTestData.Id(182),
            RouteMatcherType.Regex,
            "[");
        var malformedHost = RoutingTestData.CreateRoute(
            RoutingTestData.Id(183),
            RouteMatcherType.Exact,
            "/host",
            hostPatterns: ImmutableArray.Create("bad host"));
        var malformedMethod = RoutingTestData.CreateRoute(
            RoutingTestData.Id(184),
            RouteMatcherType.Exact,
            "/method",
            methods: ImmutableArray.Create(string.Empty));

        var result = RouteMatchSnapshotBuilder.Build(
            new[] { malformedPath, malformedPrefix, malformedRegex, malformedHost, malformedMethod });

        Assert.False(result.IsSuccess);
        Assert.Equal(5, result.Errors.Length);
        Assert.Contains(result.Errors, error =>
            error.RouteId == malformedPath.Id &&
            error.Code == RouteConfigurationErrorCode.InvalidPathPattern);
        Assert.Contains(result.Errors, error =>
            error.RouteId == malformedPrefix.Id &&
            error.Code == RouteConfigurationErrorCode.InvalidPrefixWildcard);
        Assert.Contains(result.Errors, error =>
            error.RouteId == malformedRegex.Id &&
            error.Code == RouteConfigurationErrorCode.InvalidRegex);
        Assert.Contains(result.Errors, error =>
            error.RouteId == malformedHost.Id &&
            error.Code == RouteConfigurationErrorCode.InvalidHostPattern);
        Assert.Contains(result.Errors, error =>
            error.RouteId == malformedMethod.Id &&
            error.Code == RouteConfigurationErrorCode.InvalidMethod);
    }

    [Fact]
    public void RawPrefixCannotUseStripForwardingAndInvalidRegexIsSafe()
    {
        var rawStrip = RoutingTestData.CreateRoute(
            RoutingTestData.Id(190),
            RouteMatcherType.Prefix,
            "/api*",
            forwarding: new ForwardingConfiguration(ForwardingMode.Strip, null));
        var invalidRegex = RoutingTestData.CreateRoute(
            RoutingTestData.Id(191),
            RouteMatcherType.Regex,
            "(?<unsupported>");

        var rawResult = RouteMatchSnapshotBuilder.Build(new[] { rawStrip });
        var regexResult = RouteMatchSnapshotBuilder.Build(new[] { invalidRegex });

        Assert.False(rawResult.IsSuccess);
        Assert.Contains(rawResult.Errors, error =>
            error.Code == RouteConfigurationErrorCode.InvalidForwarding);
        Assert.False(regexResult.IsSuccess);
        Assert.Contains(regexResult.Errors, error =>
            error.Code == RouteConfigurationErrorCode.InvalidRegex);
    }
}
