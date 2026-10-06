using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yarp.ReverseProxy.Forwarder;

namespace Nekolla.Nekostick.Proxy;

/// <summary>Registers the transport-only microservice proxy core.</summary>
public static class MicroserviceProxyServiceCollectionExtensions
{
    /// <summary>
    /// Adds YARP forwarding, a bounded safe invoker pool, the unavailable resolver default,
    /// and the singleton executor, drain tracker, and graph admission coordinator.
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
        services.TryAddSingleton<IMicroserviceDrainTracker>(serviceProvider =>
            new MicroserviceDrainTracker(
                serviceProvider.GetRequiredService<ILogger<MicroserviceDrainTracker>>()));
        services.TryAddSingleton<IMicroserviceAdmissionCoordinator, MicroserviceAdmissionCoordinator>();
        services.TryAddSingleton<MicroserviceHttpExecutor>(serviceProvider =>
            new MicroserviceHttpExecutor(
                serviceProvider.GetRequiredService<IHttpForwarder>(),
                serviceProvider.GetRequiredService<IMicroserviceEndpointResolver>(),
                serviceProvider.GetRequiredService<MicroserviceHttpInvokerPool>(),
                serviceProvider.GetRequiredService<IMicroserviceDrainTracker>(),
                serviceProvider.GetRequiredService<IMicroserviceAdmissionCoordinator>(),
                serviceProvider.GetRequiredService<ILogger<MicroserviceHttpExecutor>>(),
                serviceProvider.GetRequiredService<IMicroserviceForwardingTelemetry>()));
        return services;
    }
}
