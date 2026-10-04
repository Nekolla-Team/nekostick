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
        if (options.StartCancelled)
        {
            throw new OperationCanceledException("Fixture start deliberately cancelled.");
        }

        if (options.ReportStatus is { } reportedStatusCode)
        {
            context.Host.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, reportedStatusCode));
        }

        var state = new FixtureState(options, context.Host.Lifecycle, context.Registration);
        if (options.VerifyBridgeCapabilities)
        {
            state.CapabilityProbe = await ProbeCapabilitiesAsync(
                    context.Host,
                    legacySettings,
                    options.HoldServiceOutput,
                    options.LifecycleObservationPort,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.TypedContractExchange)
        {
            const string contractId = "fixture.logger";
            var exportResult = context.Contracts.TryExport<IExtensionLogger>(
                contractId,
                new FixtureContractLogger());
            if (exportResult is ExtensionContractExportFailureResult exportFailure)
            {
                throw new InvalidOperationException(
                    $"Fixture typed contract export failed with {exportFailure.Code}: {exportFailure.Detail.Message}");
            }

            if (!ReferenceEquals(exportResult, ExtensionContractExportResult.Success))
            {
                throw new InvalidOperationException("Fixture typed contract export returned an unknown result type.");
            }

            switch (context.Contracts.TryImport<IExtensionLogger>(contractId))
            {
                case ExtensionContractImportSuccessResult<IExtensionLogger> imported:
                    imported.Contract.Report(ExtensionLogLevel.Information, "fixture-contract-imported");
                    state.TypedContractExchangeSucceeded = true;
                    break;
                case ExtensionContractImportFailureResult<IExtensionLogger> importFailure:
                    throw new InvalidOperationException(
                        $"Fixture typed contract import failed with {importFailure.Code}: {importFailure.Detail.Message}");
                default:
                    throw new InvalidOperationException(
                        "Fixture typed contract import returned an unknown result type.");
            }
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
            var subscriptionResult = context.Host.Events.TrySubscribe(async (@event, token) =>
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
            });
            EnsureEventSubscriptionSucceeded(subscriptionResult, "Fixture settings-changed subscription");
        }

        if (options.RegisterHandler)
        {
            EnsureRegistrationSucceeded(
                context.Registration.TryRegisterHandler(new FixtureHandler(state, options.HandlerId)),
                "Fixture handler registration");
        }

        if (options.RegisterStreamingHandler)
        {
            EnsureRegistrationSucceeded(
                context.Registration.TryRegisterStreamingHandler(
                    new FixtureStreamingHandler(state, options.StreamingHandlerId)),
                "Fixture streaming handler registration");
        }

        if (options.RegisterFallback || options.DuplicateFallback)
        {
            EnsureRegistrationSucceeded(
                context.Registration.TryRegisterFallback(new FixtureFallback(state)),
                "Fixture fallback registration");
        }

        if (options.DuplicateHandler)
        {
            // ignores the failure result from its registration surface.
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
                options.AttemptUnregisterHandlerId).Succeeded;
        }

        if (options.AttemptUnregisterFallback)
        {
            state.FallbackUnregisterResult = context.Registration.TryUnregisterFallback().Succeeded;
        }

        if (options.StartTask || options.RequestLifecycleFromTask)
        {
            var startResult = await context.Host.Tasks.StartAsync(
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
            EnsureTaskStartSucceeded(startResult, "Fixture task start");
        }

        if (options.PublishOrderedEvents || options.PublishCoreEvents || options.RequestLifecycleFromEvent)
        {
            var expected = options.EventCount;
            var subscriptionResult = context.Host.Events.TrySubscribe(async (@event, _) =>
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
            });
            EnsureEventSubscriptionSucceeded(subscriptionResult, "Fixture event subscription");

            if (options.PublishOrderedEvents)
            {
                for (var index = 0; index < expected; index++)
                {
                    EnsureEventPublished(
                        context.Host.Events.TryPublish(
                            new ExtensionEvent("fixture.ordered", 1, $"event-{index}")),
                        "Fixture ordered event publication");
                }
            }
        }

        if (options.PublishBoundedEvents)
        {
            var callbackStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var subscriptionResult = context.Host.Events.TrySubscribe(async (@event, token) =>
            {
                if (string.Equals(@event.Type, "fixture.block", StringComparison.Ordinal))
                {
                    callbackStarted.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                }
            });
            EnsureEventSubscriptionSucceeded(subscriptionResult, "Fixture bounded event subscription");

            EnsureEventPublished(
                context.Host.Events.TryPublish(new ExtensionEvent("fixture.block", 1, "block")),
                "Fixture blocking event publication");

            await callbackStarted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < 1024; index++)
            {
                EnsureEventPublished(
                    context.Host.Events.TryPublish(
                        new ExtensionEvent("fixture.bounded", 1, $"queued-{index}")),
                    "Fixture bounded event publication");
            }

            var newestResult = context.Host.Events.TryPublish(
                new ExtensionEvent("fixture.bounded", 1, "newest"));
            if (newestResult is not ExtensionEventPublishFailureResult
                { Code: ExtensionEventPublishFailureCode.QueueFull })
            {
                throw newestResult is ExtensionEventPublishFailureResult failure
                    ? new InvalidOperationException(
                        $"Fixture bounded event publication failed with {failure.Code}: {failure.Detail.Message}")
                    : new InvalidOperationException(
                        "Fixture bounded event was accepted when the queue was expected to be full.");
            }
        }

        if (options.StartDelayMilliseconds > 0)
        {
            await Task.Delay(options.StartDelayMilliseconds, cancellationToken).ConfigureAwait(false);
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
    private static void EnsureEventSubscriptionSucceeded(
        ExtensionEventSubscribeResult result,
        string operation)
    {
        if (result is ExtensionEventSubscribeFailureResult failure)
        {
            throw new InvalidOperationException(
                $"{operation} failed with {failure.Code}: {failure.Detail.Message}");
        }

        if (!ReferenceEquals(result, ExtensionEventSubscribeResult.Success))
        {
            throw new InvalidOperationException($"{operation} returned an unknown result type.");
        }
    }

    private static void EnsureRegistrationSucceeded(
        ExtensionRegistrationResult result,
        string operation)
    {
        if (result is ExtensionRegistrationFailureResult failure)
        {
            throw new InvalidOperationException(
                $"{operation} failed with {failure.Code}: {failure.Detail.Message}");
        }

        if (!ReferenceEquals(result, ExtensionRegistrationResult.Success))
        {
            throw new InvalidOperationException($"{operation} returned an unknown result type.");
        }
    }

    private static void EnsureTaskStartSucceeded(
        ExtensionTaskStartResult result,
        string operation)
    {
        if (result is ExtensionTaskStartFailureResult failure)
        {
            throw new InvalidOperationException(
                $"{operation} failed with {failure.Code}: {failure.Detail.Message}");
        }

        if (!ReferenceEquals(result, ExtensionTaskStartResult.Success))
        {
            throw new InvalidOperationException($"{operation} returned an unknown result type.");
        }
    }

    private static void EnsureEventPublished(
        ExtensionEventPublishResult result,
        string operation)
    {
        if (result is ExtensionEventPublishFailureResult failure)
        {
            throw new InvalidOperationException(
                $"{operation} failed with {failure.Code}: {failure.Detail.Message}");
        }

        if (!ReferenceEquals(result, ExtensionEventPublishResult.Success))
        {
            throw new InvalidOperationException($"{operation} returned an unknown result type.");
        }
    }
}
