using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Proxy;
using Nekolla.Nekostick.Routing;
using Yarp.ReverseProxy.Forwarder;

using ContractHeaderRewrite = Nekolla.Nekostick.Contracts.HeaderRewriteConfiguration;
using Xunit;

namespace Nekolla.Nekostick.IntegrationTests;

internal enum IntegrationStageKind
{
    SnapshotPublished,
    SnapshotRejected,
    FixtureReady,
    FixtureNotReady,
    ResolverAvailable,
    ResolverUnavailable,
    ResolverCancelled,
    TargetExecuted
}

internal enum HostTargetExecutionDisposition
{
    Unknown,
    Deferred,
    Handled,
    Unavailable,
    SafeFailure,
    BadRequest,
    NotFound,
    Forbidden,
    BadGateway,
    GatewayTimeout,
    Cancelled
}

internal readonly record struct IntegrationStageEvidence(
    IntegrationStageKind Kind,
    HostTargetExecutionDisposition TargetDisposition = HostTargetExecutionDisposition.Unknown,
    MicroserviceProxyExecutionDisposition? ProxyDisposition = null,
    ForwarderError? ForwarderErrorCategory = null);

internal static class HostIntegrationTestSupport
{
    internal static Guid NewId() => Guid.CreateVersion7();

    internal static string CreateTempRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nekostick-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    internal static ServiceProvider CreateProxyServices(IMicroserviceEndpointResolver resolver)
    {
        var services = new ServiceCollection();
        services.AddMicroserviceProxy();
        services.AddSingleton(resolver);
        return services.BuildServiceProvider();
    }

    internal static IntegrationStageEvidence PublishSnapshot(
        HostConfigurationSnapshotHolder holder,
        HostConfigurationSnapshot snapshot) =>
        new(holder.TryReplace(snapshot)
            ? IntegrationStageKind.SnapshotPublished
            : IntegrationStageKind.SnapshotRejected);

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Resolver probing exposes only a fixed safe stage.")]
    internal static async ValueTask<IntegrationStageEvidence> ProbeResolverAsync(
        IMicroserviceEndpointResolver resolver,
        Guid serviceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await resolver.ResolveAsync(serviceId, cancellationToken).ConfigureAwait(false);
            return new(resolution.IsAvailable
                ? IntegrationStageKind.ResolverAvailable
                : IntegrationStageKind.ResolverUnavailable);
        }
        catch (OperationCanceledException)
        {
            return new(IntegrationStageKind.ResolverCancelled);
        }
        catch (Exception)
        {
            return new(IntegrationStageKind.ResolverUnavailable);
        }
    }

    internal static void AssertSafeForwarderErrorEvidence(IntegrationStageEvidence evidence)
    {
        var error = evidence.ForwarderErrorCategory;
        Assert.True(
            error is null || Enum.IsDefined(error.Value),
            $"ForwarderErrorCategory:{error?.ToString() ?? "None"}");
    }

    internal static void AssertNoForwarderErrorForHandled(IntegrationStageEvidence evidence) =>
        Assert.Null(evidence.ForwarderErrorCategory);

    internal static HostConfigurationSnapshot CreateSnapshot(
        IEnumerable<RouteConfiguration> routes,
        IEnumerable<Guid>? serviceIds = null,
        ImmutableArray<string> trustedProxyCidrs = default,
        ProxyTimeoutConfiguration? proxyTimeouts = null)
    {
        var now = DateTimeOffset.UtcNow;
        var services = serviceIds is null
            ? ImmutableArray<ServiceConfiguration>.Empty
            : serviceIds.Distinct().Select(id => CreateService(id, now)).ToImmutableArray();

        return new HostConfigurationSnapshot(
            1,
            new GlobalSettingsConfiguration(
                version: 1,
                configurationPollInterval: TimeSpan.FromSeconds(1),
                trustedProxyCidrs: trustedProxyCidrs,
                proxyTimeouts: proxyTimeouts),
            routes.ToImmutableArray(),
            services,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);
    }

    internal static RouteConfiguration CreateRoute(
        Guid routeId,
        string pattern,
        RouteTargetConfiguration target,
        ForwardingMode forwardingMode,
        string? replaceTemplate = null,
        RouteMatcherType matcherType = RouteMatcherType.Prefix,
        ImmutableArray<ContractHeaderRewrite> requestHeaderRewrites = default,
        ImmutableArray<ContractHeaderRewrite> responseHeaderRewrites = default) =>
        new(
            routeId,
            enabled: true,
            new RouteMatcherConfiguration(
                matcherType,
                pattern,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            target,
            priority: 0,
            new ForwardingConfiguration(forwardingMode, replaceTemplate),
            requestHeaderRewrites,
            responseHeaderRewrites,
            "{}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            version: 1);

    internal static DefaultHttpContext CreateContext(
        string path,
        string method = "GET",
        string? query = null,
        string? rawPath = null,
        CancellationToken cancellationToken = default)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Protocol = "HTTP/1.1";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("integration.test");
        context.Request.Path = path;
        context.Request.PathBase = PathString.Empty;
        context.Request.QueryString = query is null ? QueryString.Empty : new QueryString(query);
        context.Request.Body = new MemoryStream();
        context.RequestAborted = cancellationToken;
        context.Response.Body = new MemoryStream();
        if (rawPath is not null)
        {
            context.Features.Get<IHttpRequestFeature>()!.RawTarget = rawPath + (query ?? string.Empty);
        }

        return context;
    }

    internal static byte[] ResponseBody(DefaultHttpContext context) =>
        ((MemoryStream)context.Response.Body).ToArray();

    internal static RouteMatchResult Match(
        HostConfigurationSnapshotHolder holder,
        string path,
        string method = "GET")
    {
        var routing = GetRoutingSnapshot(holder);
        var matcher = (RouteMatchSnapshot)GetProperty(routing, "Matcher");
        return matcher.Match(new RouteMatchInput(path, "integration.test", method));
    }

    internal static object CreateHostTargetExecutor(MicroserviceHttpExecutor executor)
    {
        var hostAssembly = typeof(HostConfigurationSnapshotHolder).Assembly;
        var executorType = hostAssembly.GetType(
            "Nekolla.Nekostick.Host.HostRouteTargetExecutor",
            throwOnError: true)!;
        var constructor = executorType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(value =>
            {
                var parameters = value.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType == typeof(MicroserviceHttpExecutor)
                    && parameters[1].ParameterType == typeof(IHostServiceLifecycleCoordinator)
                    && !parameters[1].ParameterType.IsValueType;
            });
        return constructor.Invoke([executor, null]);
    }

    internal static async Task<IntegrationStageEvidence> ExecuteMatchedTargetAsync(
        HostConfigurationSnapshotHolder holder,
        object hostTargetExecutor,
        DefaultHttpContext context)
    {
        var matchResult = Match(
            holder,
            GetRawOriginPath(context),
            context.Request.Method);
        Assert.Equal(RouteMatchStatus.Matched, matchResult.Status);
        Assert.NotNull(matchResult.Match);
        var result = await InvokeHostTargetAsync(
            holder,
            hostTargetExecutor,
            context,
            matchResult.Match!).ConfigureAwait(false);
        var targetDisposition = ParseTargetDisposition(result);
        var forwarderError = context.Features.Get<IForwarderErrorFeature>()?.Error;
        return new(
            IntegrationStageKind.TargetExecuted,
            targetDisposition,
            matchResult.Match!.Target.Type == RouteTargetType.Microservice
                ? MapProxyDisposition(targetDisposition)
                : null,
            forwarderError);
    }

    internal static async Task DispatchWithRealHostDispatcherAsync(
        HostConfigurationSnapshotHolder holder,
        object hostTargetExecutor,
        DefaultHttpContext context)
    {
        var hostAssembly = typeof(HostConfigurationSnapshotHolder).Assembly;
        var accessorType = hostAssembly.GetType(
            "Nekolla.Nekostick.Host.HostRoutingSnapshotAccessor",
            throwOnError: true)!;
        var accessor = Activator.CreateInstance(
            accessorType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [holder],
            culture: null)!;

        var fallbackType = hostAssembly.GetType(
            "Nekolla.Nekostick.Host.NoOpRouteFallbackDispatcher",
            throwOnError: true)!;
        var fallback = fallbackType.GetField(
            "Instance",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

        var dispatcherType = hostAssembly.GetType(
            "Nekolla.Nekostick.Host.HostRouteDispatcher",
            throwOnError: true)!;
        var constructor = dispatcherType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(value =>
                value.GetParameters().Length == 3
                && value.GetParameters()[2].ParameterType.Name == "IRouteTargetExecutor");
        var dispatcher = constructor.Invoke([accessor, fallback, hostTargetExecutor]);
        var dispatch = dispatcherType.GetMethod(
            "DispatchAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        await ((Task)dispatch.Invoke(dispatcher, [context])!).ConfigureAwait(false);
    }

    internal static async Task TransformRequestAsync(
        HttpContext context,
        MicroserviceProxyRequest request,
        HttpRequestMessage proxyRequest)
    {
        var transformer = new MicroserviceHttpTransformer(request, context.RequestAborted);
        await transformer.TransformRequestAsync(
            context,
            proxyRequest,
            "http://integration.test",
            context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task<object?> InvokeHostTargetAsync(
        HostConfigurationSnapshotHolder holder,
        object hostTargetExecutor,
        DefaultHttpContext context,
        RouteMatch match)
    {
        var routing = GetRoutingSnapshot(holder);
        var execute = hostTargetExecutor.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(value => value.Name == "ExecuteAsync" && value.GetParameters().Length == 4);
        var pending = execute.Invoke(
            hostTargetExecutor,
            [context, routing, match, context.RequestAborted])!;
        var result = await AwaitValueTaskAsync(pending).ConfigureAwait(false);
        return result;
    }

    private static object GetRoutingSnapshot(HostConfigurationSnapshotHolder holder) =>
        GetProperty(holder, "RoutingSnapshot");

    private static string GetRawOriginPath(DefaultHttpContext context)
    {
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (!string.IsNullOrEmpty(rawTarget) && rawTarget[0] == '/')
        {
            var queryIndex = rawTarget.IndexOf('?');
            return queryIndex < 0 ? rawTarget : rawTarget[..queryIndex];
        }

        return context.Request.Path.Value!;
    }

    private static object GetProperty(object instance, string name) =>
        instance.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static async Task<object?> AwaitValueTaskAsync(object valueTask)
    {
        var asTask = valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        var task = (Task)asTask;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private static HostTargetExecutionDisposition ParseTargetDisposition(object? result) =>
        result is not null
            && Enum.TryParse<HostTargetExecutionDisposition>(
                result.ToString(),
                ignoreCase: false,
                out var disposition)
            ? disposition
            : HostTargetExecutionDisposition.Unknown;

    private static MicroserviceProxyExecutionDisposition? MapProxyDisposition(
        HostTargetExecutionDisposition disposition) => disposition switch
    {
        HostTargetExecutionDisposition.Handled => MicroserviceProxyExecutionDisposition.Handled,
        HostTargetExecutionDisposition.Unavailable => MicroserviceProxyExecutionDisposition.Unavailable,
        HostTargetExecutionDisposition.BadRequest => MicroserviceProxyExecutionDisposition.BadRequest,
        HostTargetExecutionDisposition.BadGateway => MicroserviceProxyExecutionDisposition.BadGateway,
        HostTargetExecutionDisposition.GatewayTimeout => MicroserviceProxyExecutionDisposition.GatewayTimeout,
        HostTargetExecutionDisposition.Cancelled => MicroserviceProxyExecutionDisposition.Cancelled,
        _ => null
    };

    private static ServiceConfiguration CreateService(Guid id, DateTimeOffset now) =>
        new(
            id,
            enabled: true,
            fileName: "/integration/no-process",
            argumentList: ImmutableArray<string>.Empty,
            workingDirectory: "/",
            environment: ImmutableDictionary<string, string>.Empty,
            startMode: ServiceStartMode.Lazy,
            restartPolicy: ServiceRestartPolicy.Never,
            healthCheck: new ServiceHealthCheckConfiguration(
                ServiceHealthCheckType.Process,
                httpPath: null,
                timeout: TimeSpan.FromSeconds(1)),
            createdAt: now,
            updatedAt: now,
            version: 1);
}

internal sealed class FixedEndpointResolver : IMicroserviceEndpointResolver
{
    private readonly ImmutableDictionary<Guid, MicroserviceEndpointResolution> _resolutions;

    internal FixedEndpointResolver(
        ImmutableDictionary<Guid, MicroserviceEndpointResolution> resolutions)
    {
        _resolutions = resolutions;
    }

    public ValueTask<MicroserviceEndpointResolution> ResolveAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _resolutions.GetValueOrDefault(serviceId)
            ?? MicroserviceEndpointResolution.Unavailable);
    }
}
