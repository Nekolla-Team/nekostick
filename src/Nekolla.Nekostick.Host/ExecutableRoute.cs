using System.Collections.Immutable;
using ContractHeaderRewrite = Nekolla.Nekostick.Contracts.HeaderRewriteConfiguration;
using ContractHeaderRewriteOperation = Nekolla.Nekostick.Contracts.HeaderRewriteOperation;
using Nekolla.Nekostick.Contracts;
using ProxyHeaderRewrite = Nekolla.Nekostick.Proxy.HeaderRewriteConfiguration;
using ProxyHeaderRewriteOperation = Nekolla.Nekostick.Proxy.HeaderRewriteOperation;
using Nekolla.Nekostick.Proxy;

namespace Nekolla.Nekostick.Host;

/// <summary>Contains the immutable execution metadata compiled from one route.</summary>
internal sealed class ExecutableRoute
{
    internal ExecutableRoute(
        RouteConfiguration configuration,
        StaticTargetDefinition? staticTarget,
        ImmutableArray<ProxyHeaderRewrite> requestHeaderRewrites,
        ImmutableArray<ProxyHeaderRewrite> responseHeaderRewrites,
        TrustedProxyPolicy trustedProxyPolicy,
        MicroserviceTimeoutPolicy timeoutPolicy,
        ProxyRetryConfiguration retryPolicy)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        StaticTarget = staticTarget;
        RequestHeaderRewrites = requestHeaderRewrites.IsDefault
            ? ImmutableArray<ProxyHeaderRewrite>.Empty
            : requestHeaderRewrites;
        ResponseHeaderRewrites = responseHeaderRewrites.IsDefault
            ? ImmutableArray<ProxyHeaderRewrite>.Empty
            : responseHeaderRewrites;
        TrustedProxyPolicy = trustedProxyPolicy ?? throw new ArgumentNullException(nameof(trustedProxyPolicy));
        TimeoutPolicy = timeoutPolicy ?? throw new ArgumentNullException(nameof(timeoutPolicy));
        RetryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
    }

    /// <summary>Gets the complete immutable route configuration.</summary>
    internal RouteConfiguration Configuration { get; }

    /// <summary>Gets the complete immutable route configuration.</summary>
    internal RouteConfiguration Route => Configuration;

    /// <summary>Gets the precompiled static target, when the route is static.</summary>
    internal StaticTargetDefinition? StaticTarget { get; }

    /// <summary>Gets the immutable request rewrite definitions used by Proxy.</summary>
    internal ImmutableArray<ProxyHeaderRewrite> RequestHeaderRewrites { get; }

    /// <summary>Gets the immutable response rewrite definitions used by Proxy.</summary>
    internal ImmutableArray<ProxyHeaderRewrite> ResponseHeaderRewrites { get; }

    /// <summary>Gets the precompiled trusted peer policy for this route.</summary>
    internal TrustedProxyPolicy TrustedProxyPolicy { get; }
    /// <summary>Gets the immutable timeout policy compiled for this route.</summary>
    internal MicroserviceTimeoutPolicy TimeoutPolicy { get; }

    /// <summary>Gets the immutable retry policy compiled for this route.</summary>
    internal ProxyRetryConfiguration RetryPolicy { get; }
}

/// <summary>Builds all route execution metadata without touching the filesystem.</summary>
internal static class ExecutableRouteBuilder
{
    internal static bool TryBuild(
        HostConfigurationSnapshot snapshot,
        out ImmutableDictionary<Guid, ExecutableRoute> routes)
    {
        routes = ImmutableDictionary<Guid, ExecutableRoute>.Empty;
        try
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            var trustedProxyPolicy = new TrustedProxyPolicy(snapshot.GlobalSettings.TrustedProxyCidrs);
            var timeoutPolicy = CreateTimeoutPolicy(snapshot.GlobalSettings.ProxyTimeouts);
            var retryPolicy = snapshot.GlobalSettings.ProxyRetries;
            var builder = ImmutableDictionary.CreateBuilder<Guid, ExecutableRoute>();

            foreach (var route in snapshot.Routes)
            {
                if (route is null || !builder.TryAdd(route.Id, Create(route, trustedProxyPolicy, timeoutPolicy, retryPolicy)))
                {
                    return false;
                }
            }

            routes = builder.ToImmutable();
            return true;
        }
        catch (Exception)
        {
            routes = ImmutableDictionary<Guid, ExecutableRoute>.Empty;
            return false;
        }
    }

    private static ExecutableRoute Create(
        RouteConfiguration route,
        TrustedProxyPolicy trustedProxyPolicy,
        MicroserviceTimeoutPolicy timeoutPolicy,
        ProxyRetryConfiguration retryPolicy)
    {
        var resolvedRetryPolicy = route.ProxyRetries ?? retryPolicy;
        var staticTarget = route.Target switch
        {
            StaticFileRouteTargetConfiguration staticConfiguration =>
                new StaticTargetDefinition(staticConfiguration.RootPath),
            MicroserviceRouteTargetConfiguration => null,
            ExtensionHandlerRouteTargetConfiguration => null,
            _ => throw new InvalidOperationException()
        };

        return new ExecutableRoute(
            route,
            staticTarget,
            ConvertRewrites(route.RequestHeaderRewrites, requestSide: true),
            ConvertRewrites(route.ResponseHeaderRewrites, requestSide: false),
            trustedProxyPolicy,
            timeoutPolicy,
            resolvedRetryPolicy);
    }

    private static MicroserviceTimeoutPolicy CreateTimeoutPolicy(ProxyTimeoutConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new MicroserviceTimeoutPolicy(
            configuration.ConnectTimeout,
            configuration.HttpActivityTimeout,
            configuration.HttpTotalTimeout,
            configuration.WebSocketIdleTimeout);
    }

    private static ImmutableArray<ProxyHeaderRewrite> ConvertRewrites(
        IEnumerable<ContractHeaderRewrite> rewrites,
        bool requestSide)
    {
        var builder = ImmutableArray.CreateBuilder<ProxyHeaderRewrite>();
        foreach (var rewrite in rewrites)
        {
            if (rewrite is null)
            {
                throw new ArgumentException("A header rewrite cannot be null.", nameof(rewrites));
            }

            if (rewrite.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                && (!requestSide || rewrite.Operation != ContractHeaderRewriteOperation.Set))
            {
                throw new ArgumentException("The Host header rewrite is not allowed.", nameof(rewrites));
            }

            builder.Add(new ProxyHeaderRewrite(
                ConvertOperation(rewrite.Operation),
                rewrite.Name,
                rewrite.Value));
        }

        return builder.ToImmutable();
    }

    private static ProxyHeaderRewriteOperation ConvertOperation(
        ContractHeaderRewriteOperation operation) => operation switch
    {
        ContractHeaderRewriteOperation.Remove => ProxyHeaderRewriteOperation.Remove,
        ContractHeaderRewriteOperation.Set => ProxyHeaderRewriteOperation.Set,
        ContractHeaderRewriteOperation.Add => ProxyHeaderRewriteOperation.Add,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };
}
