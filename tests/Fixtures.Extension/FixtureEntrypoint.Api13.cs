using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Tests.Fixtures.Extension;

public sealed partial class FixtureEntrypoint
{
    private static readonly Guid ProbeId =
        Guid.Parse("01900000-0000-7000-8000-000000000701");

    private static async ValueTask<string> ProbeCapabilitiesAsync(
        IExtensionHostBridge host,
        ExtensionSettingsConfiguration? legacySettings,
        CancellationToken cancellationToken)
    {
        var emptyChanges = new ExtensionConfigurationChangeSet(
            ImmutableArray<ExtensionRouteConfiguration>.Empty,
            ImmutableArray<Guid>.Empty,
            ImmutableArray<ExtensionServiceConfiguration>.Empty,
            ImmutableArray<Guid>.Empty,
            settings: null);
        var fullChanges = new ConfigurationChangeSet(
            new GlobalSettingsConfiguration(),
            ImmutableArray<RouteConfiguration>.Empty,
            ImmutableArray<ServiceConfiguration>.Empty,
            ImmutableArray<ExtensionRecordConfiguration>.Empty,
            ImmutableArray<ExtensionSettingsConfiguration>.Empty);
        var fallbackSettings = legacySettings ??
            new ExtensionSettingsConfiguration("fixture.extension.deterministic", 1, "{}", 0);
        var configurationRead = await host.ConfigurationApi.ReadAsync(cancellationToken).ConfigureAwait(false);
        var configurationApply = await host.ConfigurationApi.ApplyAsync(0, emptyChanges, cancellationToken).ConfigureAwait(false);
        var settingsRead = await host.ConfigurationApi.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        var settingsWrite = await host.ConfigurationApi.WriteSettingsAsync(0, fallbackSettings, cancellationToken).ConfigureAwait(false);
        var fullRead = await host.FullConfiguration.ReadAsync(cancellationToken).ConfigureAwait(false);
        var fullReplace = await host.FullConfiguration.ReplaceAsync(0, fullChanges, cancellationToken).ConfigureAwait(false);
        var routeRead = await host.Routes.ReadOwnedAsync(cancellationToken).ConfigureAwait(false);
        var routeRemove = await host.Routes.RemoveAsync(0, ProbeId, cancellationToken).ConfigureAwait(false);
        var serviceRead = await host.Services.ReadOwnedAsync(cancellationToken).ConfigureAwait(false);
        var serviceRemove = await host.Services.RemoveAsync(0, ProbeId, cancellationToken).ConfigureAwait(false);
        var serviceStart = await host.Services.StartAsync(ProbeId, cancellationToken).ConfigureAwait(false);
        var serviceStop = await host.Services.StopAsync(ProbeId, cancellationToken).ConfigureAwait(false);
        var serviceRestart = await host.Services.RestartAsync(ProbeId, cancellationToken).ConfigureAwait(false);
        var endpoint = await host.Endpoints.ResolveAsync(ProbeId, cancellationToken).ConfigureAwait(false);
        var legacy = host.Configuration.Settings;
        var lifecycleStatus = host.Lifecycle?.Status;
        var endpointCount = host.Endpoints?.Current.Length ?? 0;
        var properties =
            host.ConfigurationApi is not null &&
            host.FullConfiguration is not null &&
            host.Routes is not null &&
            host.Services is not null &&
            host.Endpoints is not null &&
            host.Lifecycle is not null;
        var api13 = await ProbeApi13CapabilitiesAsync(host, cancellationToken).ConfigureAwait(false);
        return $"api={host.ApiVersion};legacy={legacy?.ExtensionId}:{legacy?.SchemaVersion}:{legacy?.Version};properties={properties};" +
            $"lifecycle={lifecycleStatus?.ExtensionId}:{lifecycleStatus?.State};" +
            $"configRead={ReadCode(configurationRead)};configApply={WriteCode(configurationApply)};" +
            $"settingsRead={ReadCode(settingsRead)};settingsWrite={WriteCode(settingsWrite)};" +
            $"fullRead={ReadCode(fullRead)};fullReplace={WriteCode(fullReplace)};" +
            $"routeRead={ReadCode(routeRead)};routeRemove={WriteCode(routeRemove)};" +
            $"serviceRead={ReadCode(serviceRead)};serviceRemove={WriteCode(serviceRemove)};" +
            $"serviceStart={serviceStart.Code};serviceStop={serviceStop.Code};serviceRestart={serviceRestart.Code};" +
            $"endpoints={endpointCount};endpointResolve={(endpoint is null ? "null" : "present")};{api13}";
    }

    private static string ReadCode<T>(ConfigurationReadResult<T> result) =>
        result.IsSuccess
            ? "Success"
            : result.Errors.IsDefaultOrEmpty ? "Unknown" : result.Errors[0].Code.ToString();

    private static string WriteCode(ConfigurationWriteResult result) =>
        result.IsSuccess
            ? "Success"
            : result.Errors.IsDefaultOrEmpty ? "Unknown" : result.Errors[0].Code.ToString();
    private static async ValueTask<string> ProbeApi13CapabilitiesAsync(
        IExtensionHostBridge host,
        CancellationToken cancellationToken)
    {
        var bridge = host as IExtensionHostBridge13;
        if (bridge is null)
        {
            return "api13=Unsupported;sibling=False;supervisor=Unsupported;routeSubscribe=False;routeHook=False;logWriter=Unavailable";
        }

        var supported = ExtensionAbi.IsApi13Supported(host.ApiVersion);
        var supervisor = await bridge.Supervisor.GetAsync(ProbeId, cancellationToken)
            .ConfigureAwait(false);
        var routeSubscribe = bridge.RouteEvents.TrySubscribe(
            static (_, _) => ValueTask.CompletedTask);
        var routeHook = bridge.RouteEvents.TryRegisterHook(
            ExtensionRouteEventStage.Trigger,
            static (_, _) => ValueTask.FromResult(ExtensionRouteHookResult.FailClosed));
        bridge.LogWriter.WriteText(ExtensionLogLevel.Information, "fixture-api13-probe");
        return $"api13={(supported ? "Supported" : "Unsupported")};sibling=True;" +
            $"supervisor={ReadCode(supervisor)};routeSubscribe={routeSubscribe};" +
            $"routeHook={routeHook};logWriter={(supported ? "Called" : "Unsupported")}";
    }
}