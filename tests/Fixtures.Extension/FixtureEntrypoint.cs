using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Tests.Fixtures.Extension;

/// <summary>Provides one deterministic ABI entrypoint for direct runtime evidence.</summary>
public sealed class FixtureEntrypoint : IExtensionEntry
{
    private readonly IExtensionHostBridge _constructedHost;
    private FixtureState? _state;

    /// <summary>Creates the fixture through the public host bridge constructor.</summary>
    /// <param name="host">The narrow public host bridge.</param>
    public FixtureEntrypoint(IExtensionHostBridge host)
    {
        _constructedHost = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <inheritdoc />
    public async ValueTask StartAsync(
        IExtensionStartContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = _constructedHost;

        var legacySettings = context.Host.Configuration.Settings;
        var options = FixtureMode.Parse(legacySettings?.SettingsJson);
        if (options.StartFails)
        {
            throw new InvalidOperationException("Fixture start deliberately failed.");
        }

        var state = new FixtureState(options, context.Host.Lifecycle, context.Registration);
        if (options.VerifyBridgeCapabilities)
        {
            state.CapabilityProbe = await ProbeCapabilitiesAsync(
                    context.Host,
                    legacySettings,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.TypedContractExchange)
        {
            const string contractId = "fixture.logger";
            if (!context.Contracts.TryExport<IExtensionLogger>(
                    contractId,
                    new FixtureContractLogger()) ||
                !context.Contracts.TryImport<IExtensionLogger>(contractId, out var imported) ||
                imported is null)
            {
                throw new InvalidOperationException("Fixture typed contract exchange failed.");
            }

            state.TypedContractExchangeSucceeded = true;
        }
        _state = state;
        if (options.RequestLifecycleFromStart)
        {
            state.StartLifecycleResult = await state.RequestLifecycleAsync().ConfigureAwait(false);
        }

        if (!context.Registration.TryRegisterHandler(
                new FixtureHandler(state, options.HandlerId)))
        {
            throw new InvalidOperationException("Fixture handler registration failed.");
        }

        if ((options.RegisterFallback || options.DuplicateFallback) &&
            !context.Registration.TryRegisterFallback(new FixtureFallback(state)))
        {
            throw new InvalidOperationException("Fixture fallback registration failed.");
        }

        if (options.DuplicateHandler)
        {
            // The runtime must reject the second distinct owner even if an entrypoint
            // ignores the false return value from its registration surface.
            _ = context.Registration.TryRegisterHandler(
                new FixtureHandler(state, options.HandlerId));
        }

        if (options.DuplicateFallback)
        {
            _ = context.Registration.TryRegisterFallback(new FixtureFallback(state));
        }


        if (!string.IsNullOrWhiteSpace(options.AttemptUnregisterHandlerId))
        {
            state.HandlerUnregisterResult = context.Registration.TryUnregisterHandler(
                options.AttemptUnregisterHandlerId);
        }

        if (options.AttemptUnregisterFallback)
        {
            state.FallbackUnregisterResult = context.Registration.TryUnregisterFallback();
        }

        if (options.StartTask || options.RequestLifecycleFromTask)
        {
            _ = await context.Host.Tasks.StartAsync(
                options.RequestLifecycleFromTask
                    ? "fixture.lifecycle-task"
                    : "fixture.long-lived-task",
                async token =>
                {
                    if (options.RequestLifecycleFromTask)
                    {
                        state.TaskLifecycleResult = await state.RequestLifecycleAsync().ConfigureAwait(false);
                        state.CallbackComplete.TrySetResult(true);
                        return;
                    }

                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }

        if (options.PublishOrderedEvents || options.PublishCoreEvents || options.RequestLifecycleFromEvent)
        {
            var expected = options.EventCount;
            if (!context.Host.Events.TrySubscribe(async (@event, _) =>
                {
                    if (options.RequestLifecycleFromEvent)
                    {
                        state.EventLifecycleResult = await state.RequestLifecycleAsync().ConfigureAwait(false);
                        state.CallbackComplete.TrySetResult(true);
                    }

                    if (options.PublishCoreEvents ||
                        string.Equals(@event.Type, "fixture.ordered", StringComparison.Ordinal))
                    {
                        state.EventPayloads.Enqueue(@event.PayloadJson);
                        if (state.EventPayloads.Count >= expected)
                        {
                            state.EventsComplete.TrySetResult(true);
                        }
                    }

                    return;
                }))
            {
                throw new InvalidOperationException("Fixture event subscription failed.");
            }

            if (options.PublishOrderedEvents)
            {
                for (var index = 0; index < expected; index++)
                {
                    if (!context.Host.Events.TryPublish(
                            new ExtensionEvent("fixture.ordered", 1, $"event-{index}")))
                    {
                        throw new InvalidOperationException("Fixture ordered event was dropped unexpectedly.");
                    }
                }
            }
        }

        if (options.PublishBoundedEvents)
        {
            var callbackStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!context.Host.Events.TrySubscribe(async (@event, token) =>
                {
                    if (string.Equals(@event.Type, "fixture.block", StringComparison.Ordinal))
                    {
                        callbackStarted.TrySetResult(true);
                        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                    }
                }))
            {
                throw new InvalidOperationException("Fixture bounded event subscription failed.");
            }

            if (!context.Host.Events.TryPublish(new ExtensionEvent("fixture.block", 1, "block")))
            {
                throw new InvalidOperationException("Fixture blocking event was dropped unexpectedly.");
            }

            await callbackStarted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < 1024; index++)
            {
                if (!context.Host.Events.TryPublish(
                        new ExtensionEvent("fixture.bounded", 1, $"queued-{index}")))
                {
                    throw new InvalidOperationException("Fixture bounded queue filled too early.");
                }
            }

            _ = context.Host.Events.TryPublish(new ExtensionEvent("fixture.bounded", 1, "newest"));
        }
    }
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
        return $"api={host.ApiVersion};legacy={legacy?.ExtensionId}:{legacy?.SchemaVersion}:{legacy?.Version};properties={properties};" +
            $"lifecycle={lifecycleStatus?.ExtensionId}:{lifecycleStatus?.State};" +
            $"configRead={ReadCode(configurationRead)};configApply={WriteCode(configurationApply)};" +
            $"settingsRead={ReadCode(settingsRead)};settingsWrite={WriteCode(settingsWrite)};" +
            $"fullRead={ReadCode(fullRead)};fullReplace={WriteCode(fullReplace)};" +
            $"routeRead={ReadCode(routeRead)};routeRemove={WriteCode(routeRemove)};" +
            $"serviceRead={ReadCode(serviceRead)};serviceRemove={WriteCode(serviceRemove)};" +
            $"serviceStart={serviceStart.Code};serviceStop={serviceStop.Code};serviceRestart={serviceRestart.Code};" +
            $"endpoints={endpointCount};endpointResolve={(endpoint is null ? "null" : "present")}";
    }

    private static string ReadCode<T>(ConfigurationReadResult<T> result) =>
        result.IsSuccess
            ? "Success"
            : result.Errors.IsDefaultOrEmpty ? "Unknown" : result.Errors[0].Code.ToString();

    private static string WriteCode(ConfigurationWriteResult result) =>
        result.IsSuccess
            ? "Success"
            : result.Errors.IsDefaultOrEmpty ? "Unknown" : result.Errors[0].Code.ToString();

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        var state = _state;
        if (state?.Options.StopFails == true)
        {
            throw new InvalidOperationException("Fixture stop deliberately failed.");
        }

        if (state?.Options.RequestLifecycleFromStop == true)
        {
            var result = await state.RequestLifecycleAsync().ConfigureAwait(false);
            if (!result.StartsWith(
                    "reload=Reentrant;unload=Reentrant;",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Fixture stop lifecycle request was not reentrant.");
            }

            await state.PublishLifecycleObservationAsync(result, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask OnPreviousStoppedAsync(CancellationToken cancellationToken)
    {
        var state = _state;
        if (state?.Options.PreviousStoppedFails == true)
        {
            throw new InvalidOperationException("Fixture previous-stopped hook deliberately failed.");
        }

        if (state?.Options.RequestLifecycleFromPreviousStopped == true)
        {
            state.PreviousStoppedLifecycleResult = await state.RequestLifecycleAsync().ConfigureAwait(false);
        }

        if (state is not null)
        {
            state.PreviousStopped = true;
        }
    }
}

/// <summary>Provides the bounded handler registered by <see cref="FixtureEntrypoint" />.</summary>
public sealed class FixtureHandler : IExtensionHandler
{
    private readonly FixtureState _state;
    /// <inheritdoc />
    public string HandlerId { get; }

    /// <summary>Creates a handler over one private fixture state.</summary>
    public FixtureHandler(FixtureState state, string handlerId)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        HandlerId = handlerId;
    }
    /// <inheritdoc />
    public async ValueTask<ExtensionHandlerResponse> HandleAsync(
        ExtensionHandlerRequest request,
        CancellationToken cancellationToken)
    {
        if (_state.Options.RequestLifecycleFromHandler)
        {
            _state.HandlerLifecycleResult = await _state.RequestLifecycleAsync().ConfigureAwait(false);
        }

        if (_state.Options.UnregisterHandlerOnInvocation)
        {
            _state.HandlerUnregisterResult = _state.Registration.TryUnregisterHandler(HandlerId);
            if (_state.Options.ReregisterHandlerAfterUnregister &&
                _state.Options.UnregisterBarrierPort <= 0)
            {
                _state.ReregisterHandlerResult = _state.Registration.TryRegisterHandler(
                    new FixtureHandler(_state, HandlerId));
            }
        }

        if (_state.Options.UnregisterBarrierPort > 0 &&
            Interlocked.Exchange(ref _state.UnregisterBarrierClaimed, 1) == 0)
        {
            await _state.WaitAtBarrierAsync(cancellationToken).ConfigureAwait(false);
            if (_state.Options.ReregisterHandlerAfterUnregister)
            {
                _state.ReregisterHandlerResult = _state.Registration.TryRegisterHandler(
                    new FixtureHandler(_state, HandlerId));
            }
        }

        if (_state.Options.HandlerFails)
        {
            throw new InvalidOperationException(FixtureSignals.HandlerFailure);
        }

        if (_state.Options.PublishOrderedEvents || _state.Options.PublishCoreEvents)
        {
            await _state.EventsComplete.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_state.Options.RequestLifecycleFromTask || _state.Options.RequestLifecycleFromEvent)
        {
            await _state.CallbackComplete.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var payload = _state.RenderPayload();
        return new ExtensionHandlerResponse(
            200,
            new[] { new KeyValuePair<string, IEnumerable<string>>("Content-Type", ["text/plain; charset=utf-8"]) },
            Encoding.UTF8.GetBytes(payload));
    }
}

/// <summary>Provides the optional global fallback registered by the fixture.</summary>
public sealed class FixtureFallback : IExtensionFallback
{
    private readonly FixtureState _state;

    /// <summary>Creates a fallback over one private fixture state.</summary>
    public FixtureFallback(FixtureState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <inheritdoc />
    public async ValueTask<ExtensionFallbackResult> HandleAsync(
        ExtensionFallbackRequest request,
        CancellationToken cancellationToken)
    {
        if (_state.Options.RequestLifecycleFromFallback)
        {
            _state.FallbackLifecycleResult = await _state.RequestLifecycleAsync().ConfigureAwait(false);
        }

        if (_state.Options.UnregisterFallbackOnInvocation)
        {
            _state.FallbackUnregisterResult = _state.Registration.TryUnregisterFallback();
        }

        var count = _state.Options.IncludeFallbackCount
            ? Interlocked.Increment(ref _state.FallbackInvocationCount)
            : 0;
        var payload = _state.Options.IncludeFallbackCount
            ? $"{_state.Options.Label}:{request.Reason}:{count}"
            : $"{_state.Options.Label}:{request.Reason}";
        payload = _state.AppendObservations(payload);
        return ExtensionFallbackResult.HandledResponse(
            new ExtensionHandlerResponse(404, body: Encoding.UTF8.GetBytes(payload)));
    }
}

internal sealed class FixtureContractLogger : IExtensionLogger
{
    public void Report(ExtensionLogLevel level, string code)
    {
    }
}

/// <summary>Stores only instance-local deterministic observations for one fixture load.</summary>
public sealed class FixtureState
{
    internal FixtureState(
        FixtureMode options,
        IExtensionLifecycleApi lifecycle,
        IExtensionRegistration registration)
    {
        Options = options;
        Lifecycle = lifecycle;
        Registration = registration;
    }

    internal FixtureMode Options { get; }
    internal IExtensionLifecycleApi Lifecycle { get; }
    internal IExtensionRegistration Registration { get; }
    internal int UnregisterBarrierClaimed;
    internal bool PreviousStopped { get; set; }
    internal int FallbackInvocationCount;
    internal ConcurrentQueue<string> EventPayloads { get; } = new();
    internal bool TypedContractExchangeSucceeded { get; set; }
    internal string? CapabilityProbe { get; set; }
    internal string? HandlerLifecycleResult { get; set; }
    internal string? FallbackLifecycleResult { get; set; }
    internal string? TaskLifecycleResult { get; set; }
    internal string? EventLifecycleResult { get; set; }
    internal bool? HandlerUnregisterResult { get; set; }
    internal bool? FallbackUnregisterResult { get; set; }
    internal bool? ReregisterHandlerResult { get; set; }
    internal string? StartLifecycleResult { get; set; }
    internal string? PreviousStoppedLifecycleResult { get; set; }
    internal TaskCompletionSource<bool> EventsComplete { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> CallbackComplete { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal async ValueTask<string> RequestLifecycleAsync()
    {
        var reload = await Lifecycle.RequestReloadAsync().ConfigureAwait(false);
        var unload = await Lifecycle.RequestUnloadAsync().ConfigureAwait(false);
        return $"reload={reload.Code};unload={unload.Code};state={reload.Status?.State}";
    }
    internal async ValueTask WaitAtBarrierAsync(CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", Options.UnregisterBarrierPort, cancellationToken)
            .ConfigureAwait(false);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
        var release = new byte[1];
        await stream.ReadExactlyAsync(release, cancellationToken).ConfigureAwait(false);
    }
    internal async ValueTask PublishLifecycleObservationAsync(
        string result,
        CancellationToken cancellationToken)
    {
        if (Options.LifecycleObservationPort <= 0)
        {
            return;
        }

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", Options.LifecycleObservationPort, cancellationToken)
            .ConfigureAwait(false);
        var payload = Encoding.UTF8.GetBytes(result);
        if (payload.Length > byte.MaxValue)
        {
            throw new InvalidOperationException("Fixture lifecycle observation is too large.");
        }

        var stream = client.GetStream();
        await stream.WriteAsync(new[] { (byte)payload.Length }, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }


    internal string RenderPayload()
    {
        var payload = !Options.PublishOrderedEvents && !Options.PublishCoreEvents
            ? $"{Options.Label}:{(PreviousStopped ? "previous-stopped" : "started")}"
            : string.Join(',', EventPayloads);
        return AppendObservations(payload);
    }

    internal string AppendObservations(string payload)
    {
        var observations = new List<string>();
        if (CapabilityProbe is not null)
        {
            observations.Add(CapabilityProbe);
        }

        if (StartLifecycleResult is not null)
        {
            observations.Add($"start-lifecycle={StartLifecycleResult}");
        }

        if (PreviousStoppedLifecycleResult is not null)
        {
            observations.Add($"previous-stopped-lifecycle={PreviousStoppedLifecycleResult}");
        }

        if (HandlerLifecycleResult is not null)
        {
            observations.Add($"handler-lifecycle={HandlerLifecycleResult}");
        }

        if (FallbackLifecycleResult is not null)
        {
            observations.Add($"fallback-lifecycle={FallbackLifecycleResult}");
        }

        if (TaskLifecycleResult is not null)
        {
            observations.Add($"task-lifecycle={TaskLifecycleResult}");
        }

        if (EventLifecycleResult is not null)
        {
            observations.Add($"event-lifecycle={EventLifecycleResult}");
        }

        if (HandlerUnregisterResult is { } handlerUnregister)
        {
            observations.Add($"handler-unregister={handlerUnregister}");
        }

        if (FallbackUnregisterResult is { } fallbackUnregister)
        {
            observations.Add($"fallback-unregister={fallbackUnregister}");
        }

        if (ReregisterHandlerResult is { } reregisterHandler)
        {
            observations.Add($"handler-reregister={reregisterHandler}");
        }

        return observations.Count == 0
            ? payload
            : $"{payload}:{string.Join('|', observations)}";
    }
}

/// <summary>Defines deterministic fixture behavior selected by extension-owned JSON settings.</summary>
public sealed record FixtureMode(
        string Label,
        string HandlerId,
        bool StartFails,
        bool StopFails,
        bool PreviousStoppedFails,
        bool HandlerFails,
        bool RegisterFallback,
        bool DuplicateHandler,
        bool DuplicateFallback,
        bool StartTask,
        bool PublishOrderedEvents,
        bool PublishBoundedEvents,
        bool PublishCoreEvents,
        bool TypedContractExchange,
        bool IncludeFallbackCount,
        int EventCount,
        bool VerifyBridgeCapabilities,
        bool RequestLifecycleFromHandler,
        bool RequestLifecycleFromFallback,
        bool RequestLifecycleFromTask,
        bool RequestLifecycleFromEvent,
        bool UnregisterHandlerOnInvocation,
        bool UnregisterFallbackOnInvocation,
        bool ReregisterHandlerAfterUnregister,
        string? AttemptUnregisterHandlerId,
        bool AttemptUnregisterFallback,
        bool RequestLifecycleFromStart,
        bool RequestLifecycleFromPreviousStopped,
        bool RequestLifecycleFromStop,
        int LifecycleObservationPort,
        int UnregisterBarrierPort)
    {
        /// <summary>Reads the small test-only settings document.</summary>
        public static FixtureMode Parse(string? settingsJson)
        {
            if (string.IsNullOrWhiteSpace(settingsJson))
            {
                return Default;
            }

            using var document = JsonDocument.Parse(settingsJson);
            var root = document.RootElement;
            return new FixtureMode(
                ReadString(root, "label", "fixture"),
                ReadString(root, "handlerId", "fixture.handler"),
                ReadBool(root, "startFails"),
                ReadBool(root, "stopFails"),
                ReadBool(root, "previousStoppedFails"),
                ReadBool(root, "handlerFails"),
                ReadBool(root, "registerFallback"),
                ReadBool(root, "duplicateHandler"),
                ReadBool(root, "duplicateFallback"),
                ReadBool(root, "startTask"),
                ReadBool(root, "publishOrderedEvents"),
                ReadBool(root, "publishBoundedEvents"),
                ReadBool(root, "publishCoreEvents"),
                ReadBool(root, "typedContractExchange"),
                ReadBool(root, "includeFallbackCount"),
                ReadInt(root, "eventCount", 3),
                ReadBool(root, "verifyBridgeCapabilities"),
                ReadBool(root, "requestLifecycleFromHandler"),
                ReadBool(root, "requestLifecycleFromFallback"),
                ReadBool(root, "requestLifecycleFromTask"),
                ReadBool(root, "requestLifecycleFromEvent"),
                ReadBool(root, "unregisterHandlerOnInvocation"),
                ReadBool(root, "unregisterFallbackOnInvocation"),
                ReadBool(root, "reregisterHandlerAfterUnregister"),
                ReadOptionalString(root, "attemptUnregisterHandlerId"),
                ReadBool(root, "attemptUnregisterFallback"),
                ReadBool(root, "requestLifecycleFromStart"),
                ReadBool(root, "requestLifecycleFromPreviousStopped"),
                ReadBool(root, "requestLifecycleFromStop"),
                ReadPort(root, "lifecycleObservationPort"),
                ReadPort(root, "unregisterBarrierPort"));
        }

        private static string ReadString(JsonElement root, string name, string fallback) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;

        private static string? ReadOptionalString(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static bool ReadBool(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        private static int ReadInt(JsonElement root, string name, int fallback) =>
            root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var result) &&
            result is > 0 and <= 1024
                ? result
                : fallback;
        private static int ReadPort(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var result) &&
            result is > 0 and <= 65535
                ? result
                : 0;

        private static FixtureMode Default { get; } = new(
            "fixture",
            "fixture.handler",
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            3,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            null,
            false,
            false,
            false,
            false,
            0,
            0);
    }
