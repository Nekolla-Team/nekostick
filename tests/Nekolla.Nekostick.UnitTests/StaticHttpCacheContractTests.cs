using Nekolla.Nekostick.Proxy;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class StaticHttpCacheContractTests
{
    [Fact]
    public void DefaultStaticResponsesUseNoCache()
    {
        using var fixture = new Fixture();
        using var execution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            Fixture.RequestPath);

        var response = AssertResponse(execution, 200);

        Assert.Equal("no-cache", response.Headers.CacheControl);
        Assert.Equal("no-cache", HeaderValue(response.Headers, "Cache-Control"));
    }

    [Fact]
    public void StaticResponsesExposeSyntacticallyLegalWeakEntityTags()
    {
        using var fixture = new Fixture();
        using var execution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            Fixture.RequestPath);

        var response = AssertResponse(execution, 200);
        var entityTag = response.Headers.ETag;

        Assert.Equal(
            $"W/\"{response.Headers.ContentLength}-{response.Headers.LastModifiedUtc.UtcDateTime.Ticks}\"",
            entityTag);
        Assert.StartsWith("W/\"", entityTag, StringComparison.Ordinal);
        Assert.EndsWith("\"", entityTag, StringComparison.Ordinal);
        Assert.True(entityTag.Length > 4);
        foreach (var character in entityTag[3..^1])
        {
            Assert.InRange(character, '!', '~');
            Assert.NotEqual('"', character);
        }

        Assert.Equal(entityTag, HeaderValue(response.Headers, "ETag"));
    }

    [Fact]
    public void IfMatchUsesStrongComparisonAgainstWeakResponseTags()
    {
        using var fixture = new Fixture();
        using var baselineExecution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            Fixture.RequestPath);
        var entityTag = AssertResponse(baselineExecution, 200).Headers.ETag;
        var strongEquivalent = entityTag[2..];

        using var weakCandidate = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            Fixture.RequestPath,
            new StaticHttpRequestHeaders(ifMatch: entityTag));
        using var strongCandidate = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            Fixture.RequestPath,
            new StaticHttpRequestHeaders(ifMatch: strongEquivalent));

        Assert.Equal(412, AssertResponse(weakCandidate, 412).StatusCode);
        Assert.Equal(412, AssertResponse(strongCandidate, 412).StatusCode);
    }

    [Fact]
    public void IfNoneMatchUsesWeakComparisonAgainstWeakResponseTags()
    {
        using var fixture = new Fixture();
        using var baselineExecution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            Fixture.RequestPath);
        var entityTag = AssertResponse(baselineExecution, 200).Headers.ETag;
        var strongEquivalent = entityTag[2..];

        using var weakCandidate = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            Fixture.RequestPath,
            new StaticHttpRequestHeaders(ifNoneMatch: entityTag));
        using var strongCandidate = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            Fixture.RequestPath,
            new StaticHttpRequestHeaders(ifNoneMatch: strongEquivalent));

        Assert.Equal(304, AssertResponse(weakCandidate, 304).StatusCode);
        Assert.Equal(304, AssertResponse(strongCandidate, 304).StatusCode);
    }

    [Fact]
    public async Task ConditionalNoBodyStatusesDoNotExposeAResponseBody()
    {
        using var fixture = new Fixture();
        using var baselineExecution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            Fixture.RequestPath);
        var entityTag = AssertResponse(baselineExecution, 200).Headers.ETag;

        using var preconditionExecution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            Fixture.RequestPath,
            new StaticHttpRequestHeaders(ifMatch: "\"different\""));
        using var notModifiedExecution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            Fixture.RequestPath,
            new StaticHttpRequestHeaders(ifNoneMatch: entityTag));

        await AssertNoBodyAsync(AssertNoBodyStatus(preconditionExecution, 412, 0));
        await AssertNoBodyAsync(AssertNoBodyStatus(notModifiedExecution, 304, null));
    }

    private static StaticHttpResponsePlan AssertResponse(
        StaticHttpExecutionResult execution,
        int expectedStatusCode)
    {
        Assert.Equal(StaticHttpExecutionKind.Response, execution.Kind);
        Assert.True(execution.HasResponse);
        var response = execution.Response;
        Assert.NotNull(response);
        Assert.Equal(expectedStatusCode, response!.StatusCode);
        return response;
    }

    private static StaticHttpResponsePlan AssertNoBodyStatus(
        StaticHttpExecutionResult execution,
        int expectedStatusCode,
        long? expectedContentLength)
    {
        var response = AssertResponse(execution, expectedStatusCode);
        Assert.False(response.HasBody);
        Assert.Equal(0L, response.BodyLength);
        Assert.Null(response.Headers.ContentType);
        Assert.Equal(expectedContentLength, response.Headers.ContentLength);
        return response;
    }

    private static async Task AssertNoBodyAsync(StaticHttpResponsePlan response)
    {
        using var destination = new MemoryStream(Array.Empty<byte>(), writable: false);
        await response.CopyBodyToAsync(destination);
        Assert.Equal(0, destination.Length);
    }

    private static string HeaderValue(StaticHttpResponseHeaders headers, string name)
    {
        var matching = headers.Values
            .Where(header => header.Name.Equals(name, StringComparison.Ordinal))
            .ToArray();
        Assert.Single(matching);
        return matching[0].Value;
    }

    private sealed class Fixture : IDisposable
    {
        internal const string RequestPath = "/document.txt";

        private readonly string _rootPath = Path.Combine(
            Path.GetTempPath(),
            "nekostick-static-cache-contract-" + Guid.NewGuid().ToString("N"));

        internal Fixture()
        {
            Directory.CreateDirectory(_rootPath);
            File.WriteAllText(Path.Combine(_rootPath, "document.txt"), "cache-test");
            Target = new StaticTargetDefinition(_rootPath);
        }

        internal StaticTargetDefinition Target { get; }

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }
}
