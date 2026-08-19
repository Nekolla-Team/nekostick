using System.Globalization;
using System.Text;
using Nekolla.Nekostick.Proxy;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class StaticHttpExecutorTests
{
    [Fact]
    public void OrdinaryTempRootResolvesAndOpensAReadOnlyFile()
    {
        using var fixture = new StaticFileFixture();

        var resolution = fixture.Target.Resolve(StaticFileFixture.RequestPath);
        Assert.Equal(StaticFileResolutionKind.FoundFile, resolution.Kind);
        Assert.Equal(StaticFileFailureReason.None, resolution.FailureReason);
        Assert.True(resolution.IsOpenable);

        using var opened = fixture.Target.OpenRead(resolution);
        Assert.Equal(StaticFileOpenKind.Opened, opened.Kind);
        Assert.Equal(StaticFileFailureReason.None, opened.FailureReason);
        Assert.NotNull(opened.Handle);
        Assert.False(opened.Handle!.Stream.CanWrite);
    }

    [Fact]
    public async Task GetCreatesStreamingSuccessPlanWithSafeFileMetadata()
    {
        using var fixture = new StaticFileFixture();
        using var execution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            StaticFileFixture.RequestPath);

        Assert.Equal(StaticHttpExecutionKind.Response, execution.Kind);
        var response = AssertResponse(execution, 200);
        Assert.True(response.HasBody);
        Assert.Equal((long)StaticFileFixture.PayloadBytes.Length, response.BodyLength);
        Assert.Equal("text/plain; charset=utf-8", response.Headers.ContentType);
        Assert.Equal(StaticFileFixture.PayloadBytes.Length, response.Headers.ContentLength);
        Assert.Equal(
            $"W/\"{StaticFileFixture.PayloadBytes.Length}-{response.Headers.LastModifiedUtc.UtcDateTime.Ticks}\"",
            response.Headers.ETag);
        Assert.Equal(
            response.Headers.LastModifiedUtc.ToString("R", CultureInfo.InvariantCulture),
            response.Headers.LastModified);
        Assert.Equal(response.Headers.ETag, HeaderValue(response.Headers, "ETag"));
        Assert.Equal(response.Headers.LastModified, HeaderValue(response.Headers, "Last-Modified"));
        Assert.Equal("text/plain; charset=utf-8", HeaderValue(response.Headers, "Content-Type"));
        Assert.Equal(StaticFileFixture.PayloadBytes.Length.ToString(CultureInfo.InvariantCulture),
            HeaderValue(response.Headers, "Content-Length"));
        Assert.Equal("bytes", HeaderValue(response.Headers, "Accept-Ranges"));

        using var destination = new MemoryStream();
        await response.CopyBodyToAsync(destination, TestContext.Current.CancellationToken);

        Assert.Equal(StaticFileFixture.PayloadBytes, destination.ToArray());
    }

    [Fact]
    public async Task HeadKeepsEquivalentMetadataWithoutCopyingAFileBody()
    {
        using var fixture = new StaticFileFixture();
        using var getExecution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            StaticFileFixture.RequestPath);
        using var headExecution = StaticHttpExecutor.Execute(
            fixture.Target,
            "HEAD",
            StaticFileFixture.RequestPath);

        var getResponse = AssertResponse(getExecution, 200);
        var headResponse = AssertResponse(headExecution, 200);

        Assert.Equal(getResponse.Headers.ContentType, headResponse.Headers.ContentType);
        Assert.Equal(getResponse.Headers.ContentLength, headResponse.Headers.ContentLength);
        Assert.Equal(getResponse.Headers.ETag, headResponse.Headers.ETag);
        Assert.Equal(getResponse.Headers.LastModifiedUtc, headResponse.Headers.LastModifiedUtc);
        Assert.Equal(getResponse.Headers.LastModified, headResponse.Headers.LastModified);
        Assert.Equal(getResponse.Headers.ContentRange, headResponse.Headers.ContentRange);
        Assert.Equal(getResponse.Headers.CacheControl, headResponse.Headers.CacheControl);
        Assert.Equal(getResponse.Headers.Values, headResponse.Headers.Values);
        Assert.False(headResponse.HasBody);
        Assert.Equal(0L, headResponse.BodyLength);

        using var nonWritableDestination = new MemoryStream(Array.Empty<byte>(), writable: false);
        await headResponse.CopyBodyToAsync(
            nonWritableDestination,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, nonWritableDestination.Length);
    }

    [Fact]
    public async Task ConditionalHeadersApplyIfMatchThenIfNoneMatchThenLastModifiedPrecedence()
    {
        using var fixture = new StaticFileFixture();
        using var baselineExecution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            StaticFileFixture.RequestPath);
        var baseline = AssertResponse(baselineExecution, 200);
        var currentTag = baseline.Headers.ETag;
        var future = baseline.Headers.LastModifiedUtc.AddMinutes(1).ToString("R", CultureInfo.InvariantCulture);
        var past = baseline.Headers.LastModifiedUtc.AddMinutes(-1).ToString("R", CultureInfo.InvariantCulture);

        using var ifMatchFailure = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            StaticFileFixture.RequestPath,
            new StaticHttpRequestHeaders(
                ifMatch: "\"does-not-match\"",
                ifNoneMatch: currentTag,
                ifModifiedSince: future));
        var preconditionFailed = AssertResponse(ifMatchFailure, 412);
        Assert.False(preconditionFailed.HasBody);
        Assert.Equal(0L, preconditionFailed.BodyLength);
        Assert.Null(preconditionFailed.Headers.ContentType);
        Assert.Equal(0L, preconditionFailed.Headers.ContentLength);
        await AssertNoBodyAsync(preconditionFailed);

        using var ifNonePresentButNotMatching = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            StaticFileFixture.RequestPath,
            new StaticHttpRequestHeaders(
                ifNoneMatch: "\"does-not-match\"",
                ifModifiedSince: future));
        AssertResponse(ifNonePresentButNotMatching, 200);

        using var notModifiedByTag = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            StaticFileFixture.RequestPath,
            new StaticHttpRequestHeaders(ifNoneMatch: currentTag, ifModifiedSince: past));
        var tagNotModified = AssertResponse(notModifiedByTag, 304);
        Assert.False(tagNotModified.HasBody);
        Assert.Equal(0L, tagNotModified.BodyLength);
        Assert.Null(tagNotModified.Headers.ContentType);
        Assert.Null(tagNotModified.Headers.ContentLength);
        await AssertNoBodyAsync(tagNotModified);

        using var notModifiedByDate = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            StaticFileFixture.RequestPath,
            new StaticHttpRequestHeaders(ifModifiedSince: future));
        var dateNotModified = AssertResponse(notModifiedByDate, 304);
        Assert.False(dateNotModified.HasBody);
        Assert.Null(dateNotModified.Headers.ContentLength);
        await AssertNoBodyAsync(dateNotModified);
    }

    [Fact]
    public async Task SingleByteRangeProduces206AndCopiesOnlyTheRequestedSubset()
    {
        using var fixture = new StaticFileFixture();
        using var execution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            StaticFileFixture.RequestPath,
            new StaticHttpRequestHeaders(range: "bytes=2-5"));

        var response = AssertResponse(execution, 206);
        Assert.True(response.HasBody);
        Assert.Equal(4, response.BodyLength);
        Assert.Equal(4, response.Headers.ContentLength);
        Assert.Equal("bytes 2-5/10", response.Headers.ContentRange);
        Assert.Equal("bytes 2-5/10", HeaderValue(response.Headers, "Content-Range"));

        using var destination = new MemoryStream();
        await response.CopyBodyToAsync(destination, TestContext.Current.CancellationToken);

        Assert.Equal(Encoding.UTF8.GetBytes("2345"), destination.ToArray());
    }

    [Fact]
    public void HeadRangeKeepsRangeContentLengthWithoutABodyLength()
    {
        using var fixture = new StaticFileFixture();
        using var execution = StaticHttpExecutor.Execute(
            fixture.Target,
            "HEAD",
            StaticFileFixture.RequestPath,
            new StaticHttpRequestHeaders(range: "bytes=2-5"));

        var response = AssertResponse(execution, 206);
        Assert.False(response.HasBody);
        Assert.Equal(0L, response.BodyLength);
        Assert.Equal(4L, response.Headers.ContentLength);
        Assert.Equal("bytes 2-5/10", response.Headers.ContentRange);
        Assert.Equal("4", HeaderValue(response.Headers, "Content-Length"));
    }

    [Theory]
    [InlineData("items=0-1", StaticHttpExecutionKind.InvalidRange)]
    [InlineData("bytes=0-1,3-4", StaticHttpExecutionKind.MultipleRangesNotSupported)]
    public void InvalidOrMultipleRangesBecomeTypedSafeFailures(
        string range,
        StaticHttpExecutionKind expectedKind)
    {
        using var fixture = new StaticFileFixture();
        using var execution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            StaticFileFixture.RequestPath,
            new StaticHttpRequestHeaders(range: range));

        Assert.Equal(expectedKind, execution.Kind);
        Assert.False(execution.HasResponse);
        Assert.Null(execution.Response);
    }

    [Fact]
    public async Task UnsatisfiableRangeProducesSafe416WithTotalLength()
    {
        using var fixture = new StaticFileFixture();
        using var execution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            StaticFileFixture.RequestPath,
            new StaticHttpRequestHeaders(range: "bytes=99-100"));

        var response = AssertResponse(execution, 416);
        Assert.False(response.HasBody);
        Assert.Equal(0L, response.BodyLength);
        Assert.Equal(0L, response.Headers.ContentLength);
        Assert.Equal("bytes */10", response.Headers.ContentRange);
        Assert.Equal("bytes */10", HeaderValue(response.Headers, "Content-Range"));
        await AssertNoBodyAsync(response);
    }

    [Fact]
    public void CachePolicyIsEmittedWithoutChangingTheStaticResponseShape()
    {
        using var fixture = new StaticFileFixture();
        using var defaultExecution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            StaticFileFixture.RequestPath,
            options: StaticHttpExecutionOptions.Default);
        using var customExecution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            StaticFileFixture.RequestPath,
            options: new StaticHttpExecutionOptions("private, max-age=0"));
        using var omittedExecution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            StaticFileFixture.RequestPath,
            options: new StaticHttpExecutionOptions(null));

        var defaultResponse = AssertResponse(defaultExecution, 200);
        var customResponse = AssertResponse(customExecution, 200);
        var omittedResponse = AssertResponse(omittedExecution, 200);

        Assert.Equal("no-cache", defaultResponse.Headers.CacheControl);
        Assert.Equal("no-cache", HeaderValue(defaultResponse.Headers, "Cache-Control"));
        Assert.Equal("private, max-age=0", customResponse.Headers.CacheControl);
        Assert.Equal("private, max-age=0", HeaderValue(customResponse.Headers, "Cache-Control"));
        Assert.Null(omittedResponse.Headers.CacheControl);
        Assert.DoesNotContain(
            omittedResponse.Headers.Values,
            header => header.Name.Equals("Cache-Control", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingUnsafeAndDirectoryTargetsReturnNonSensitiveTypedOutcomes()
    {
        using var fixture = new StaticFileFixture();
        using var missing = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            "/missing.txt");
        using var unsafePath = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            "/../outside.txt");
        using var encodedUnsafePath = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            "/%2e%2e/outside.txt");
        using var controlPath = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            "/bad%1f.txt");
        using var nulPath = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            "/bad%00.txt");
        using var directory = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            "/directory");

        AssertSafeFailure(missing, StaticHttpExecutionKind.NotFound, fixture.RootPath);
        AssertSafeFailure(unsafePath, StaticHttpExecutionKind.InvalidRequest, fixture.RootPath);
        AssertSafeFailure(encodedUnsafePath, StaticHttpExecutionKind.InvalidRequest, fixture.RootPath);
        AssertSafeFailure(controlPath, StaticHttpExecutionKind.InvalidRequest, fixture.RootPath);
        AssertSafeFailure(nulPath, StaticHttpExecutionKind.InvalidRequest, fixture.RootPath);
        AssertSafeFailure(directory, StaticHttpExecutionKind.DirectoryListingDisabled, fixture.RootPath);

        var directoryResolution = fixture.Target.Resolve("/directory");
        Assert.Equal(StaticFileResolutionKind.NotFound, directoryResolution.Kind);
        Assert.Equal(StaticFileFailureReason.DirectoryIndexMissing, directoryResolution.FailureReason);
    }

    [Fact]
    public async Task DirectoryIndexHtmlIsServedWithoutDirectoryListing()
    {
        using var fixture = new StaticFileFixture();
        var indexPath = Path.Combine(fixture.RootPath, "directory", "index.html");
        var indexBytes = Encoding.UTF8.GetBytes("index content");
        File.WriteAllBytes(indexPath, indexBytes);

        using var execution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            "/directory");

        var response = AssertResponse(execution, 200);
        using var destination = new MemoryStream();
        await response.CopyBodyToAsync(destination, TestContext.Current.CancellationToken);
        Assert.Equal(indexBytes, destination.ToArray());
    }

    [Fact]
    public async Task PrecompressedSiblingsDoNotChangeIdentityOnlyServing()
    {
        using var fixture = new StaticFileFixture();
        File.WriteAllBytes(
            Path.Combine(fixture.RootPath, "document.txt.gz"),
            Encoding.UTF8.GetBytes("compressed payload"));

        using var execution = StaticHttpExecutor.Execute(
            fixture.Target,
            "GET",
            StaticFileFixture.RequestPath);

        var response = AssertResponse(execution, 200);
        using var destination = new MemoryStream();
        await response.CopyBodyToAsync(destination, TestContext.Current.CancellationToken);
        Assert.Equal(StaticFileFixture.PayloadBytes, destination.ToArray());
        Assert.DoesNotContain(
            response.Headers.Values,
            header => header.Name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OutsideRootSymlinkIsRejectedByThePublicSafetyBoundary()
    {
        using var fixture = new StaticFileFixture();
        var outsidePath = Path.Combine(
            Path.GetTempPath(),
            "nekostick-static-http-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        var linkPath = Path.Combine(fixture.RootPath, "outside-link.txt");

        File.WriteAllBytes(outsidePath, StaticFileFixture.PayloadBytes);
        try
        {
            File.CreateSymbolicLink(linkPath, outsidePath);

            var resolution = fixture.Target.Resolve("/outside-link.txt");
            Assert.Equal(StaticFileResolutionKind.Forbidden, resolution.Kind);
            Assert.Equal(StaticFileFailureReason.OutsideRoot, resolution.FailureReason);

            using var execution = StaticHttpExecutor.Execute(
                fixture.Target,
                "GET",
                "/outside-link.txt");
            Assert.Equal(StaticHttpExecutionKind.InvalidMapping, execution.Kind);
            Assert.False(execution.HasResponse);
        }
        finally
        {
            File.Delete(outsidePath);
        }
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

    private static async Task AssertNoBodyAsync(StaticHttpResponsePlan response)
    {
        using var destination = new MemoryStream(Array.Empty<byte>(), writable: false);
        await response.CopyBodyToAsync(destination);
        Assert.Equal(0, destination.Length);
    }

    private static void AssertSafeFailure(
        StaticHttpExecutionResult execution,
        StaticHttpExecutionKind expectedKind,
        string rootPath)
    {
        Assert.Equal(expectedKind, execution.Kind);
        Assert.False(execution.HasResponse);
        Assert.Null(execution.Response);
        Assert.Equal($"StaticHttpExecutionResult:{expectedKind}", execution.ToString());
        Assert.DoesNotContain(rootPath, execution.ToString(), StringComparison.Ordinal);
    }

    private static string HeaderValue(StaticHttpResponseHeaders headers, string name)
    {
        var matching = headers.Values
            .Where(header => header.Name.Equals(name, StringComparison.Ordinal))
            .ToArray();
        Assert.Single(matching);
        return matching[0].Value;
    }

    private sealed class StaticFileFixture : IDisposable
    {
        internal const string RequestPath = "/document.txt";
        internal const string Payload = "0123456789";
        internal static readonly byte[] PayloadBytes = Encoding.UTF8.GetBytes(Payload);

        internal StaticFileFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "nekostick-static-http-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);

            var documentPath = Path.Combine(RootPath, "document.txt");
            File.WriteAllBytes(documentPath, PayloadBytes);
            File.SetLastWriteTimeUtc(
                documentPath,
                new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            Directory.CreateDirectory(Path.Combine(RootPath, "directory"));

            Target = new StaticTargetDefinition(RootPath);
        }

        internal string RootPath { get; }

        internal StaticTargetDefinition Target { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
