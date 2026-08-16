using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Proxy;

namespace Nekolla.Nekostick.IntegrationTests;

internal sealed class InProcessHostTargetServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private HttpClient? _client;
    private readonly Channel<IntegrationStageEvidence> _stages =
        Channel.CreateUnbounded<IntegrationStageEvidence>();

    private InProcessHostTargetServer(WebApplication app)
    {
        _app = app;
    }

    internal HttpClient Client => _client!;

    internal static async Task<InProcessHostTargetServer> StartAsync(
        HostConfigurationSnapshotHolder holder,
        IMicroserviceEndpointResolver resolver,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(
                IPAddress.Loopback,
                0,
                listenOptions => listenOptions.Protocols = HttpProtocols.Http1));
        builder.Services.AddMicroserviceProxy();
        builder.Services.AddSingleton(resolver);
        var app = builder.Build();
        app.UseWebSockets();
        var targetExecutor = HostIntegrationTestSupport.CreateHostTargetExecutor(
            app.Services.GetRequiredService<MicroserviceHttpExecutor>());
        var server = new InProcessHostTargetServer(app);
        app.Run(context => server.HandleRequestAsync(holder, targetExecutor, context));

        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses;
            var address = new Uri(addresses.Single());
            var client = new HttpClient
            {
                BaseAddress = new UriBuilder(Uri.UriSchemeHttp, address.Host, address.Port).Uri
            };
            server._client = client;
            return server;
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal ValueTask<IntegrationStageEvidence> WaitForStageAsync(
        CancellationToken cancellationToken) =>
        _stages.Reader.ReadAsync(cancellationToken);

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The test server exposes only safe target stages and generic HTTP failures.")]
    private async Task HandleRequestAsync(
        HostConfigurationSnapshotHolder holder,
        object targetExecutor,
        HttpContext context)
    {
        IntegrationStageEvidence stage;
        try
        {
            stage = await HostIntegrationTestSupport.ExecuteMatchedTargetAsync(
                holder,
                targetExecutor,
                (DefaultHttpContext)context).ConfigureAwait(false);
        }
        catch (Exception)
        {
            stage = new(
                IntegrationStageKind.TargetExecuted,
                HostTargetExecutionDisposition.SafeFailure);
        }

        await _stages.Writer.WriteAsync(stage).ConfigureAwait(false);
        if (stage.TargetDisposition == HostTargetExecutionDisposition.Handled
            || context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = stage.TargetDisposition switch
        {
            HostTargetExecutionDisposition.BadRequest => StatusCodes.Status400BadRequest,
            HostTargetExecutionDisposition.NotFound => StatusCodes.Status404NotFound,
            HostTargetExecutionDisposition.Forbidden => StatusCodes.Status403Forbidden,
            HostTargetExecutionDisposition.BadGateway => StatusCodes.Status502BadGateway,
            HostTargetExecutionDisposition.GatewayTimeout => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status503ServiceUnavailable
        };
        context.Response.ContentType = "text/plain; charset=utf-8";
        try
        {
            await context.Response.WriteAsync(
                stage.TargetDisposition switch
                {
                    HostTargetExecutionDisposition.BadRequest => "Bad request.",
                    HostTargetExecutionDisposition.NotFound => "Not found.",
                    HostTargetExecutionDisposition.Forbidden => "Forbidden.",
                    HostTargetExecutionDisposition.BadGateway => "Bad gateway.",
                    HostTargetExecutionDisposition.GatewayTimeout => "Gateway timeout.",
                    _ => "Service unavailable."
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _client?.Dispose();
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
