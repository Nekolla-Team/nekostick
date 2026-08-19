using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Proxy;
using Nekolla.Nekostick.Routing;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

public sealed class HostStaticTargetExecutionTests
{
    [Fact]
    public async Task PublishedStaticRouteExecutesGetAndHeadWithMetadataAndNoFallback()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = HostIntegrationTestSupport.CreateTempRoot();
        try
        {
            var content = Encoding.UTF8.GetBytes("static fixture content\n");
            await File.WriteAllBytesAsync(
                Path.Combine(root, "index.html"),
                content,
                cancellationToken);

            var route = HostIntegrationTestSupport.CreateRoute(
                HostIntegrationTestSupport.NewId(),
                "/assets",
                new StaticFileRouteTargetConfiguration(Path.GetFullPath(root)),
                ForwardingMode.Strip);
            var snapshot = HostIntegrationTestSupport.CreateSnapshot([route]);
            var holder = new HostConfigurationSnapshotHolder();
            var publication = HostIntegrationTestSupport.PublishSnapshot(holder, snapshot);
            Assert.Equal(IntegrationStageKind.SnapshotPublished, publication.Kind);
            Assert.Same(snapshot, holder.Current);

            var match = HostIntegrationTestSupport.Match(holder, "/assets/index.html");
            Assert.Equal(RouteMatchStatus.Matched, match.Status);
            Assert.Equal(route.Id, match.Match!.RouteId);

            using var services = HostIntegrationTestSupport.CreateProxyServices(
                new FixedEndpointResolver(ImmutableDictionary<Guid, MicroserviceEndpointResolution>.Empty));
            var targetExecutor = HostIntegrationTestSupport.CreateHostTargetExecutor(
                services.GetRequiredService<MicroserviceHttpExecutor>());

            var get = HostIntegrationTestSupport.CreateContext(
                "/assets/index.html",
                cancellationToken: cancellationToken);
            var getDisposition = await HostIntegrationTestSupport.ExecuteMatchedTargetAsync(
                holder,
                targetExecutor,
                get);

            Assert.Equal(HostTargetExecutionDisposition.Handled, getDisposition.TargetDisposition);
            Assert.Equal(StatusCodes.Status200OK, get.Response.StatusCode);
            Assert.True(content.AsSpan().SequenceEqual(HostIntegrationTestSupport.ResponseBody(get)));
            Assert.True(content.Length.ToString(CultureInfo.InvariantCulture).AsSpan().SequenceEqual(
                get.Response.Headers.ContentLength!.Value.ToString(CultureInfo.InvariantCulture).AsSpan()));
            Assert.False(string.IsNullOrWhiteSpace(get.Response.Headers.ETag));
            Assert.False(string.IsNullOrWhiteSpace(get.Response.Headers.LastModified));

            var head = HostIntegrationTestSupport.CreateContext(
                "/assets/index.html",
                method: "HEAD",
                cancellationToken: cancellationToken);
            var headDisposition = await HostIntegrationTestSupport.ExecuteMatchedTargetAsync(
                holder,
                targetExecutor,
                head);

            Assert.Equal(HostTargetExecutionDisposition.Handled, headDisposition.TargetDisposition);
            Assert.Equal(StatusCodes.Status200OK, head.Response.StatusCode);
            Assert.Empty(HostIntegrationTestSupport.ResponseBody(head));
            Assert.True(content.Length.ToString(CultureInfo.InvariantCulture).AsSpan().SequenceEqual(
                head.Response.Headers.ContentLength!.Value.ToString(CultureInfo.InvariantCulture).AsSpan()));
            Assert.True(get.Response.Headers.ETag.ToString().AsSpan().SequenceEqual(
                head.Response.Headers.ETag.ToString().AsSpan()));
            Assert.True(get.Response.Headers.LastModified.ToString().AsSpan().SequenceEqual(
                head.Response.Headers.LastModified.ToString().AsSpan()));


            var directory = HostIntegrationTestSupport.CreateContext(
                "/assets",
                cancellationToken: cancellationToken);
            var directoryDisposition = await HostIntegrationTestSupport.ExecuteMatchedTargetAsync(
                holder,
                targetExecutor,
                directory);

            Assert.Equal(HostTargetExecutionDisposition.Handled, directoryDisposition.TargetDisposition);
            Assert.Equal(StatusCodes.Status200OK, directory.Response.StatusCode);
            Assert.True(content.AsSpan().SequenceEqual(HostIntegrationTestSupport.ResponseBody(directory)));
            var dispatched = HostIntegrationTestSupport.CreateContext(
                "/assets/index.html",
                cancellationToken: cancellationToken);
            await HostIntegrationTestSupport.DispatchWithRealHostDispatcherAsync(
                holder,
                targetExecutor,
                dispatched);

            Assert.Equal(StatusCodes.Status200OK, dispatched.Response.StatusCode);
            Assert.True(content.AsSpan().SequenceEqual(HostIntegrationTestSupport.ResponseBody(dispatched)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StaticMissingUnsafeAndUnsupportedRequestsHaveSafeDispositions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = HostIntegrationTestSupport.CreateTempRoot();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "known.txt"),
                "known",
                new UTF8Encoding(false),
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(root, "known.txt.gz"),
                "compressed",
                Encoding.UTF8,
                cancellationToken);
            Directory.CreateDirectory(Path.Combine(root, "empty-directory"));
            var route = HostIntegrationTestSupport.CreateRoute(
                HostIntegrationTestSupport.NewId(),
                "/assets",
                new StaticFileRouteTargetConfiguration(Path.GetFullPath(root)),
                ForwardingMode.Strip);
            var snapshot = HostIntegrationTestSupport.CreateSnapshot([route]);
            var holder = new HostConfigurationSnapshotHolder();
            var publication = HostIntegrationTestSupport.PublishSnapshot(holder, snapshot);
            Assert.Equal(IntegrationStageKind.SnapshotPublished, publication.Kind);

            using var services = HostIntegrationTestSupport.CreateProxyServices(
                new FixedEndpointResolver(ImmutableDictionary<Guid, MicroserviceEndpointResolution>.Empty));
            var targetExecutor = HostIntegrationTestSupport.CreateHostTargetExecutor(
                services.GetRequiredService<MicroserviceHttpExecutor>());

            var missing = HostIntegrationTestSupport.CreateContext(
                "/assets/missing.txt",
                cancellationToken: cancellationToken);
            var missingDisposition = await HostIntegrationTestSupport.ExecuteMatchedTargetAsync(
                holder,
                targetExecutor,
                missing);
            Assert.Equal(HostTargetExecutionDisposition.Unknown, missingDisposition.TargetDisposition);

            var missingDispatched = HostIntegrationTestSupport.CreateContext(
                "/assets/missing.txt",
                cancellationToken: cancellationToken);
            await HostIntegrationTestSupport.DispatchWithRealHostDispatcherAsync(
                holder,
                targetExecutor,
                missingDispatched);
            Assert.Equal(StatusCodes.Status404NotFound, missingDispatched.Response.StatusCode);
            Assert.True("Not found.".AsSpan().SequenceEqual(
                Encoding.UTF8.GetString(HostIntegrationTestSupport.ResponseBody(missingDispatched)).AsSpan()));

            var emptyDirectory = HostIntegrationTestSupport.CreateContext(
                "/assets/empty-directory",
                cancellationToken: cancellationToken);
            var emptyDirectoryDisposition = await HostIntegrationTestSupport.ExecuteMatchedTargetAsync(
                holder,
                targetExecutor,
                emptyDirectory);
            Assert.Equal(
                HostTargetExecutionDisposition.Unknown,
                emptyDirectoryDisposition.TargetDisposition);

            var emptyDirectoryDispatched = HostIntegrationTestSupport.CreateContext(
                "/assets/empty-directory",
                cancellationToken: cancellationToken);
            await HostIntegrationTestSupport.DispatchWithRealHostDispatcherAsync(
                holder,
                targetExecutor,
                emptyDirectoryDispatched);
            Assert.Equal(StatusCodes.Status404NotFound, emptyDirectoryDispatched.Response.StatusCode);
            Assert.True("Not found.".AsSpan().SequenceEqual(
                Encoding.UTF8.GetString(HostIntegrationTestSupport.ResponseBody(emptyDirectoryDispatched)).AsSpan()));

            var identity = HostIntegrationTestSupport.CreateContext(
                "/assets/known.txt",
                cancellationToken: cancellationToken);
            await HostIntegrationTestSupport.DispatchWithRealHostDispatcherAsync(
                holder,
                targetExecutor,
                identity);
            Assert.Equal(StatusCodes.Status200OK, identity.Response.StatusCode);
            Assert.True("known".AsSpan().SequenceEqual(
                Encoding.UTF8.GetString(HostIntegrationTestSupport.ResponseBody(identity)).AsSpan()));
            Assert.False(identity.Response.Headers.ContainsKey("Content-Encoding"));

            var unsupported = HostIntegrationTestSupport.CreateContext(
                "/assets/known.txt",
                method: "DELETE",
                cancellationToken: cancellationToken);
            var unsupportedDisposition = await HostIntegrationTestSupport.ExecuteMatchedTargetAsync(
                holder,
                targetExecutor,
                unsupported);
            Assert.Equal(HostTargetExecutionDisposition.BadRequest, unsupportedDisposition.TargetDisposition);

            var unsafeMatch = HostIntegrationTestSupport.Match(
                holder,
                "/assets/%2e%2e/secret");
            Assert.Equal(RouteMatchStatus.Matched, unsafeMatch.Status);

            var unsafeTargetRequest = HostIntegrationTestSupport.CreateContext(
                "/assets/../secret",
                rawPath: "/assets/%2e%2e/secret",
                cancellationToken: cancellationToken);
            var unsafeTargetDisposition = await HostIntegrationTestSupport.ExecuteMatchedTargetAsync(
                holder,
                targetExecutor,
                unsafeTargetRequest);
            Assert.Equal(
                HostTargetExecutionDisposition.BadRequest,
                unsafeTargetDisposition.TargetDisposition);
            Assert.Empty(HostIntegrationTestSupport.ResponseBody(unsafeTargetRequest));

            var unsafeRequest = HostIntegrationTestSupport.CreateContext(
                "/assets/../secret",
                rawPath: "/assets/%2e%2e/secret",
                cancellationToken: cancellationToken);
            await HostIntegrationTestSupport.DispatchWithRealHostDispatcherAsync(
                holder,
                targetExecutor,
                unsafeRequest);
            Assert.Equal(StatusCodes.Status400BadRequest, unsafeRequest.Response.StatusCode);
            Assert.True("Bad request.".AsSpan().SequenceEqual(
                Encoding.UTF8.GetString(HostIntegrationTestSupport.ResponseBody(unsafeRequest)).AsSpan()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
