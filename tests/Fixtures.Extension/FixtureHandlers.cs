using System.Text;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Tests.Fixtures.Extension;

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

        if (_state.Options.SubscribeSettingsChanged)
        {
            try
            {
                await _state.SettingsChangedComplete.Task.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // No settings-changed event arrived for this extension; render the current state.
            }
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

/// <summary>Provides a streaming handler registered by <see cref="FixtureEntrypoint" />.</summary>
public sealed class FixtureStreamingHandler : IExtensionStreamingHandler
{
    private readonly FixtureState _state;

    /// <inheritdoc />
    public string HandlerId { get; }

    /// <summary>Creates a streaming handler over one private fixture state.</summary>
    public FixtureStreamingHandler(FixtureState state, string handlerId)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        HandlerId = handlerId;
    }

    /// <inheritdoc />
    public async ValueTask<ExtensionStreamingResponse> HandleStreamingAsync(
        ExtensionStreamingRequest request,
        CancellationToken cancellationToken)
    {
        if (_state.Options.RequestLifecycleFromHandler)
        {
            _state.HandlerLifecycleResult = await _state.RequestLifecycleAsync().ConfigureAwait(false);
        }

        if (_state.Options.HandlerFails)
        {
            throw new InvalidOperationException(FixtureSignals.HandlerFailure);
        }

        var bodyStream = request.BodyStream;
        Stream responseBody;
        if (_state.Options.StreamingHandlerEmptyResponse)
        {
            var empty = new MemoryStream();
            var filler = Encoding.UTF8.GetBytes("should-be-ignored");
            await empty.WriteAsync(filler, cancellationToken).ConfigureAwait(false);
            responseBody = empty;
        }
        else
        {
            var buffer = new MemoryStream();
            if (bodyStream is not null)
            {
                await bodyStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            buffer.Position = 0;
            responseBody = buffer;
        }

        var payload = _state.RenderPayload();
        return new ExtensionStreamingResponse(
            200,
            new[]
            {
                new KeyValuePair<string, IEnumerable<string>>(
                    "Content-Type",
                    ["text/plain; charset=utf-8"]),
                new KeyValuePair<string, IEnumerable<string>>(
                    "X-Fixture-Label",
                    [payload])
            },
            responseBody);
    }
}