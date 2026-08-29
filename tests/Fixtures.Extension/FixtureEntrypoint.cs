using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Tests.Fixtures.Extension;

/// <summary>Provides one deterministic ABI entrypoint for direct runtime evidence.</summary>
public sealed partial class FixtureEntrypoint : IExtensionEntry
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

        if (options.ReadDataDirectory)
        {
            var bridge13 = context.Host as IExtensionHostBridge13;
            state.DataDirectoryValue = bridge13 is null
                ? "unavailable"
                : string.IsNullOrEmpty(bridge13.DataDirectory)
                    ? "empty"
                    : bridge13.DataDirectory;
        }

        if (options.SubscribeSettingsChanged)
        {
            if (!context.Host.Events.TrySubscribe(async (@event, token) =>
                {
                    if (!string.Equals(
                            @event.Type,
                            nameof(ExtensionCoreEventKind.ExtensionSettingsChanged),
                            StringComparison.Ordinal))
                    {
                        return;
                    }

                    Interlocked.Increment(ref state.SettingsChangedEventCount);
                    var read = await context.Host.ConfigurationApi.ReadSettingsAsync(token)
                        .ConfigureAwait(false);
                    state.SettingsChangedReadResult = read.IsSuccess
                        ? $"Success:{read.Value?.ExtensionId ?? "null"}"
                        : read.Errors.IsDefaultOrEmpty
                            ? "Unknown"
                            : read.Errors[0].Code.ToString();
                    state.SettingsChangedComplete.TrySetResult(true);
                }))
            {
                throw new InvalidOperationException("Fixture settings-changed subscription failed.");
            }
        }

        if (!context.Registration.TryRegisterHandler(
                new FixtureHandler(state, options.HandlerId)))
        {
            throw new InvalidOperationException("Fixture handler registration failed.");
        }

        if (options.RegisterStreamingHandler &&
            !context.Registration.TryRegisterStreamingHandler(
                new FixtureStreamingHandler(state, options.StreamingHandlerId)))
        {
            throw new InvalidOperationException("Fixture streaming handler registration failed.");
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
