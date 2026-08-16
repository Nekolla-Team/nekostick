using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Forwarder;

namespace Nekolla.Nekostick.Proxy;

/// <summary>Applies the proxy request and response header security policy.</summary>
public sealed partial class MicroserviceHttpTransformer : HttpTransformer
{
    private readonly ImmutableArray<CompiledHeaderRewrite> _requestRewrites;
    private readonly ImmutableArray<CompiledHeaderRewrite> _responseRewrites;
    private readonly TrustedProxyPolicy _trustedProxyPolicy;
    private readonly RequestHeaderExpansionContext _headerExpansionContext;
    private RequestHeaderExpansionContext? _effectiveExpansionContext;
    private readonly CancellationToken _defaultCancellationToken;

    /// <summary>Creates a transformer for one immutable proxy request.</summary>
    /// <param name="request">The safe proxy request.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    public MicroserviceHttpTransformer(
        MicroserviceProxyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _requestRewrites = request.CompiledRequestHeaderRewrites;
        _responseRewrites = request.CompiledResponseHeaderRewrites;
        _trustedProxyPolicy = request.TrustedProxyPolicy;
        _headerExpansionContext = request.HeaderExpansionContext;
        _defaultCancellationToken = cancellationToken;
    }

    /// <inheritdoc />
    public override ValueTask TransformRequestAsync(
        HttpContext httpContext,
        HttpRequestMessage proxyRequest,
        string destinationPrefix,
        CancellationToken cancellationToken) =>
        TransformRequestCoreAsync(
            httpContext,
            proxyRequest,
            destinationPrefix,
            cancellationToken.CanBeCanceled ? cancellationToken : _defaultCancellationToken);

    /// <inheritdoc />
    public override ValueTask<bool> TransformResponseAsync(
        HttpContext httpContext,
        HttpResponseMessage? proxyResponse,
        CancellationToken cancellationToken) =>
        TransformResponseCoreAsync(
            httpContext,
            proxyResponse,
            cancellationToken.CanBeCanceled ? cancellationToken : _defaultCancellationToken);

    private async ValueTask TransformRequestCoreAsync(
        HttpContext httpContext,
        HttpRequestMessage proxyRequest,
        string destinationPrefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(proxyRequest);
        cancellationToken.ThrowIfCancellationRequested();

        await base.TransformRequestAsync(
            httpContext,
            proxyRequest,
            destinationPrefix,
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var preserveWebSocketUpgrade = httpContext.WebSockets.IsWebSocketRequest
            && HasWebSocketUpgrade(proxyRequest.Headers);
        var preserveChunkedFraming = proxyRequest.Content is not null
            && proxyRequest.Headers.TransferEncodingChunked == true;
        StripHopByHopHeaders(
            proxyRequest.Headers,
            proxyRequest.Content?.Headers,
            httpContext.Request.Headers,
            preserveWebSocketUpgrade,
            preserveChunkedFraming);
        if (preserveWebSocketUpgrade)
        {
            RestoreWebSocketUpgrade(proxyRequest.Headers);
        }

        var expansionContext = ResolveExpansionContext(httpContext);
        if (!ApplyRewrites(
                proxyRequest.Headers,
                proxyRequest.Content,
                _requestRewrites,
                expansionContext))
        {
            throw new InvalidOperationException();
        }

        StripHopByHopHeaders(
            proxyRequest.Headers,
            proxyRequest.Content?.Headers,
            httpContext.Request.Headers,
            preserveWebSocketUpgrade,
            preserveChunkedFraming);
        if (preserveWebSocketUpgrade)
        {
            RestoreWebSocketUpgrade(proxyRequest.Headers);
        }

        ApplyTrustedIdentityHeaders(
            httpContext,
            proxyRequest.Headers,
            proxyRequest.Content,
            expansionContext.EffectiveClientIdentity!.Value);
    }

    private async ValueTask<bool> TransformResponseCoreAsync(
        HttpContext httpContext,
        HttpResponseMessage? proxyResponse,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        cancellationToken.ThrowIfCancellationRequested();

        var upstreamConnectionTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (proxyResponse is not null)
        {
            AddConnectionTokens(proxyResponse.Headers, upstreamConnectionTokens);
        }

        if (!await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var expansionContext = ResolveExpansionContext(httpContext);
        var responseHeaders = httpContext.Response.Headers;
        var preserveWebSocketUpgrade = IsWebSocketUpgradeResponse(httpContext);
        StripResponseHopByHopHeaders(
            responseHeaders,
            upstreamConnectionTokens,
            preserveWebSocketUpgrade);
        if (!ApplyResponseRewrites(
                responseHeaders,
                _responseRewrites,
                expansionContext))
        {
            throw new InvalidOperationException();
        }

        StripResponseHopByHopHeaders(
            responseHeaders,
            upstreamConnectionTokens,
            preserveWebSocketUpgrade);
        if (preserveWebSocketUpgrade)
        {
            RestoreWebSocketUpgrade(responseHeaders);
        }

        StripIdentityHeaders(responseHeaders);
        return true;
    }

    private RequestHeaderExpansionContext ResolveExpansionContext(HttpContext httpContext)
    {
        if (_effectiveExpansionContext is not null)
        {
            return _effectiveExpansionContext;
        }

        var effectiveClientIdentity = _headerExpansionContext.EffectiveClientIdentity
            ?? ResolveEffectiveClientIdentity(httpContext, _trustedProxyPolicy);
        _effectiveExpansionContext = _headerExpansionContext
            .WithEffectiveClientIdentity(effectiveClientIdentity);
        return _effectiveExpansionContext;
    }
}
