using System.Collections.Concurrent;
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

        var options = FixtureMode.Parse(context.Host.Configuration.Settings?.SettingsJson);
        if (options.StartFails)
        {
            throw new InvalidOperationException("Fixture start deliberately failed.");
        }

        var state = new FixtureState(options);
        _state = state;

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

        if (options.StartTask)
        {
            _ = await context.Host.Tasks.StartAsync(
                "fixture.long-lived-task",
                static async token =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }

        if (options.PublishOrderedEvents)
        {
            var expected = options.EventCount;
            if (!context.Host.Events.TrySubscribe((@event, _) =>
                {
                    state.EventPayloads.Enqueue(@event.PayloadJson);
                    if (state.EventPayloads.Count >= expected)
                    {
                        state.EventsComplete.TrySetResult(true);
                    }

                    return ValueTask.CompletedTask;
                }))
            {
                throw new InvalidOperationException("Fixture event subscription failed.");
            }

            for (var index = 0; index < expected; index++)
            {
                if (!context.Host.Events.TryPublish(
                        new ExtensionEvent(
                            "fixture.ordered",
                            1,
                            $"event-{index}")))
                {
                    throw new InvalidOperationException("Fixture ordered event was dropped unexpectedly.");
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
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        var state = _state;
        if (state?.Options.StopFails == true)
        {
            throw new InvalidOperationException("Fixture stop deliberately failed.");
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnPreviousStoppedAsync(CancellationToken cancellationToken)
    {
        var state = _state;
        if (state?.Options.PreviousStoppedFails == true)
        {
            throw new InvalidOperationException("Fixture previous-stopped hook deliberately failed.");
        }

        if (state is not null)
        {
            state.PreviousStopped = true;
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Provides the bounded handler registered by <see cref="FixtureEntrypoint" />.</summary>
public sealed class FixtureHandler : IExtensionHandler
{
    private readonly FixtureState _state;

    /// <summary>Creates a handler over one private fixture state.</summary>
    public FixtureHandler(FixtureState state, string handlerId)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        HandlerId = handlerId;
    }

    /// <inheritdoc />
    public string HandlerId { get; }

    /// <inheritdoc />
    public async ValueTask<ExtensionHandlerResponse> HandleAsync(
        ExtensionHandlerRequest request,
        CancellationToken cancellationToken)
    {
        if (_state.Options.HandlerFails)
        {
            throw new InvalidOperationException(FixtureSignals.HandlerFailure);
        }

        if (_state.Options.PublishOrderedEvents)
        {
            await _state.EventsComplete.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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
    public ValueTask<ExtensionFallbackResult> HandleAsync(
        ExtensionFallbackRequest request,
        CancellationToken cancellationToken)
    {
        var count = _state.Options.IncludeFallbackCount
            ? Interlocked.Increment(ref _state.FallbackInvocationCount)
            : 0;
        var payload = _state.Options.IncludeFallbackCount
            ? $"{_state.Options.Label}:{request.Reason}:{count}"
            : $"{_state.Options.Label}:{request.Reason}";
        return ValueTask.FromResult(
            ExtensionFallbackResult.HandledResponse(
                new ExtensionHandlerResponse(404, body: Encoding.UTF8.GetBytes(payload))));
    }
}

/// <summary>Stores only instance-local deterministic observations for one fixture load.</summary>
public sealed class FixtureState
{
    internal FixtureState(FixtureMode options)
    {
        Options = options;
    }

    internal FixtureMode Options { get; }

    internal bool PreviousStopped { get; set; }

    internal int FallbackInvocationCount;

    internal ConcurrentQueue<string> EventPayloads { get; } = new();

    internal TaskCompletionSource<bool> EventsComplete { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal string RenderPayload()
    {
        if (!Options.PublishOrderedEvents)
        {
            return $"{Options.Label}:{(PreviousStopped ? "previous-stopped" : "started")}";
        }

        return string.Join(',', EventPayloads);
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
    bool IncludeFallbackCount,
    int EventCount)
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
            ReadBool(root, "includeFallbackCount"),
            ReadInt(root, "eventCount", 3));
    }

    private static string ReadString(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int ReadInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var result) &&
        result is > 0 and <= 1024
            ? result
            : fallback;

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
        3);
}
