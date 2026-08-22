using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yarp.ReverseProxy.Forwarder;

namespace Nekolla.Nekostick.Proxy;

/// <summary>Registers the transport-only microservice proxy core.</summary>
public static class MicroserviceProxyServiceCollectionExtensions
{
    /// <summary>
    /// Adds YARP forwarding, a bounded safe invoker pool, the unavailable resolver default,
    /// and the singleton microservice executor.
    /// </summary>
    /// <param name="services">The application service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddMicroserviceProxy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLogging();
        services.AddHttpForwarder();
        services.TryAddSingleton<IMicroserviceEndpointResolver>(
            UnavailableMicroserviceEndpointResolver.Instance);
        services.TryAddSingleton<MicroserviceHttpInvokerPool>();
        services.TryAddSingleton<IMicroserviceForwardingTelemetry, MicroserviceForwardingTelemetry>();
        services.TryAddSingleton<MicroserviceHttpExecutor>();
        return services;
    }
}
