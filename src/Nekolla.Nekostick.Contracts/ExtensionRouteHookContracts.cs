using System.Text.Json;

namespace Nekolla.Nekostick.Contracts;

/// <summary>Defines public bounds for route observation and action-hook registrations.</summary>
public static class ExtensionRouteHookLimits
{
    /// <summary>The maximum ordinary route observation subscriptions per extension generation.</summary>
    public const int MaximumSubscriptionRegistrations = 256;

    /// <summary>The maximum action-capable route hooks per extension generation.</summary>
    public const int MaximumHookRegistrations = 128;

    /// <summary>The maximum time a Host waits for one action-capable callback.</summary>
    public static readonly TimeSpan MaximumCallbackDuration = TimeSpan.FromMilliseconds(250);
}

/// <summary>Contains one immutable route observation that can be represented by an <see cref="ExtensionEvent" />.</summary>
public sealed record ExtensionRouteEvent
{
    /// <summary>Creates a route observation DTO.</summary>
    /// <param name="routeId">The stable route identifier.</param>
    /// <param name="correlationId">The stable request correlation identifier.</param>
    /// <param name="stage">The trigger or return stage.</param>
    /// <param name="request">The bounded immutable request snapshot.</param>
    /// <param name="response">The bounded immutable response snapshot, when representable.</param>
    /// <param name="occurredAt">The UTC observation time.</param>
    public ExtensionRouteEvent(
        Guid routeId,
        Guid correlationId,
        ExtensionRouteEventStage stage,
        ExtensionRouteRequestSnapshot request,
        ExtensionRouteResponseSnapshot? response = null,
        DateTimeOffset? occurredAt = null)
    {
        RouteId = IdentityValidation.RequireUuidV7(routeId, nameof(routeId));
        CorrelationId = IdentityValidation.RequireUuidV7(correlationId, nameof(correlationId));
        if (stage is not (ExtensionRouteEventStage.Trigger or ExtensionRouteEventStage.Return))
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }

        Request = request ?? throw new ArgumentNullException(nameof(request));
        if (stage == ExtensionRouteEventStage.Trigger && response is not null)
        {
            throw new ArgumentException("A trigger observation cannot contain a response snapshot.", nameof(response));
        }

        Stage = stage;
        Response = response;
        OccurredAt = (occurredAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
    }

    /// <summary>Gets the stable route identifier.</summary>
    public Guid RouteId { get; }

    /// <summary>Gets the request correlation identifier.</summary>
    public Guid CorrelationId { get; }

    /// <summary>Gets whether this is a trigger or return observation.</summary>
    public ExtensionRouteEventStage Stage { get; }

    /// <summary>Gets the bounded immutable request snapshot.</summary>
    public ExtensionRouteRequestSnapshot Request { get; }

    /// <summary>Gets the bounded immutable response snapshot, when representable.</summary>
    public ExtensionRouteResponseSnapshot? Response { get; }

    /// <summary>Gets the UTC time at which the observation was created.</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>Encodes this observation as the standard ordered <see cref="ExtensionEvent" /> bus event.</summary>
    /// <returns>A bounded standard event with a stable route event type and JSON payload.</returns>
    public ExtensionEvent ToExtensionEvent()
    {
        var type = Stage == ExtensionRouteEventStage.Trigger
            ? ExtensionRouteEventTypes.Trigger
            : ExtensionRouteEventTypes.Return;
        return new ExtensionEvent(type, ExtensionRouteEventTypes.Version, JsonSerializer.Serialize(this));
    }
}

/// <summary>Contains the route context supplied to an action-capable hook.</summary>
public sealed record ExtensionRouteHookContext
{
    /// <summary>Creates a route hook context.</summary>
    /// <param name="routeId">The stable route identifier.</param>
    /// <param name="correlationId">The request correlation identifier.</param>
    /// <param name="stage">The hook stage.</param>
    /// <param name="request">The bounded immutable request snapshot.</param>
    /// <param name="response">The bounded immutable response snapshot at return stage, when available.</param>
    public ExtensionRouteHookContext(
        Guid routeId,
        Guid correlationId,
        ExtensionRouteEventStage stage,
        ExtensionRouteRequestSnapshot request,
        ExtensionRouteResponseSnapshot? response = null)
    {
        RouteId = IdentityValidation.RequireUuidV7(routeId, nameof(routeId));
        CorrelationId = IdentityValidation.RequireUuidV7(correlationId, nameof(correlationId));
        if (stage is not (ExtensionRouteEventStage.Trigger or ExtensionRouteEventStage.Return))
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }

        Request = request ?? throw new ArgumentNullException(nameof(request));
        if (stage == ExtensionRouteEventStage.Trigger && response is not null)
        {
            throw new ArgumentException("A trigger hook cannot contain a response snapshot.", nameof(response));
        }

        Stage = stage;
        Response = response;
    }

    /// <summary>Gets the stable route identifier.</summary>
    public Guid RouteId { get; }

    /// <summary>Gets the stable request correlation identifier.</summary>
    public Guid CorrelationId { get; }

    /// <summary>Gets the hook stage.</summary>
    public ExtensionRouteEventStage Stage { get; }

    /// <summary>Gets the bounded immutable request snapshot.</summary>
    public ExtensionRouteRequestSnapshot Request { get; }

    /// <summary>Gets the bounded immutable response snapshot at return stage, when available.</summary>
    public ExtensionRouteResponseSnapshot? Response { get; }
}

/// <summary>Identifies the explicit action returned by a route hook.</summary>
public enum ExtensionRouteHookAction
{
    /// <summary>Continue forwarding with the current snapshots.</summary>
    Continue,

    /// <summary>Replace the request before forwarding; legal only at trigger stage.</summary>
    ReplaceRequest,

    /// <summary>Replace the response before returning; legal only at return stage.</summary>
    ReplaceResponse,

    /// <summary>Cancel forwarding or response delivery.</summary>
    CancelForwarding
}

/// <summary>Contains an immutable, bounded route hook action result.</summary>
public sealed record ExtensionRouteHookResult
{
    /// <summary>Creates a hook result and validates action payload shape.</summary>
    /// <param name="action">The explicit hook action.</param>
    /// <param name="request">The replacement request for <see cref="ExtensionRouteHookAction.ReplaceRequest" />.</param>
    /// <param name="response">The replacement response for <see cref="ExtensionRouteHookAction.ReplaceResponse" />.</param>
    public ExtensionRouteHookResult(
        ExtensionRouteHookAction action,
        ExtensionRouteRequestSnapshot? request = null,
        ExtensionRouteResponseSnapshot? response = null)
    {
        switch (action)
        {
            case ExtensionRouteHookAction.Continue:
            case ExtensionRouteHookAction.CancelForwarding:
                if (request is not null || response is not null)
                {
                    throw new ArgumentException("This hook action cannot carry replacement snapshots.");
                }

                break;
            case ExtensionRouteHookAction.ReplaceRequest:
                if (request is null || response is not null)
                {
                    throw new ArgumentException("Request replacement requires only a request snapshot.");
                }

                break;
            case ExtensionRouteHookAction.ReplaceResponse:
                if (response is null || request is not null)
                {
                    throw new ArgumentException("Response replacement requires only a response snapshot.");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }

        Action = action;
        Request = request;
        Response = response;
    }

    /// <summary>Gets the explicit hook action.</summary>
    public ExtensionRouteHookAction Action { get; }

    /// <summary>Gets the replacement request, when the action is <see cref="ExtensionRouteHookAction.ReplaceRequest" />.</summary>
    public ExtensionRouteRequestSnapshot? Request { get; }

    /// <summary>Gets the replacement response, when the action is <see cref="ExtensionRouteHookAction.ReplaceResponse" />.</summary>
    public ExtensionRouteResponseSnapshot? Response { get; }

    /// <summary>Gets the canonical fail-closed result synthesized by the Host for callback failure.</summary>
    /// <remarks>This is equivalent to <see cref="ExtensionRouteHookAction.CancelForwarding" /> and carries no replacement.</remarks>
    public static ExtensionRouteHookResult FailClosed { get; } = new(ExtensionRouteHookAction.CancelForwarding);

    /// <summary>Determines whether the action is legal at one hook stage.</summary>
    /// <param name="stage">The stage at which the host received the result.</param>
    /// <returns><see langword="true" /> when the action is legal for that stage.</returns>
    public bool IsValidFor(ExtensionRouteEventStage stage) =>
        Action switch
        {
            ExtensionRouteHookAction.Continue or ExtensionRouteHookAction.CancelForwarding => true,
            ExtensionRouteHookAction.ReplaceRequest => stage == ExtensionRouteEventStage.Trigger,
            ExtensionRouteHookAction.ReplaceResponse => stage == ExtensionRouteEventStage.Return,
            _ => false
        };

    /// <summary>Determines whether the action is legal for a complete hook context.</summary>
    /// <param name="context">The context that produced this result.</param>
    /// <returns><see langword="true" /> when the action is legal for that context.</returns>
    public bool IsValidFor(ExtensionRouteHookContext context) =>
        context is not null && IsValidFor(context.Stage);
}

/// <summary>Provides global ordinary route observations and action-capable hooks.</summary>
/// <remarks>
/// Ordinary subscriptions are best-effort and are delivered through the generation's ordered standard event
/// queue; they never run inline on route execution. Each event payload identifies its route through the
/// <see cref="ExtensionRouteEvent" /> data encoded in the standard event payload. A generation accepts at most
/// <see cref="ExtensionRouteHookLimits.MaximumSubscriptionRegistrations" /> subscriptions and
/// <see cref="ExtensionRouteHookLimits.MaximumHookRegistrations" /> hooks. Each successful registration receives
/// a monotonically increasing generation-local sequence, and all hooks for a route and stage execute serially in
/// that sequence order.
///
/// Registration is generation-scoped. When a generation begins retirement, the Host rejects new dispatch,
/// cancels active callback tokens, waits only through the bounded callback deadline, and then releases every
/// callback reference. A callback token links request cancellation and generation retirement and is cancelled no
/// later than <see cref="ExtensionRouteHookLimits.MaximumCallbackDuration" /> after invocation begins. The Host
/// stops waiting at that deadline and ignores a late result.
///
/// A thrown, null, timed-out, cancelled, invalid, or stage-illegal result fails closed as
/// <see cref="ExtensionRouteHookResult.FailClosed" />: no replacement is applied and forwarding or response
/// delivery is cancelled. Only an explicit <see cref="ExtensionRouteHookAction.CancelForwarding" /> (or that
/// synthesized fail-closed result) cancels forwarding. Only <see cref="ExtensionRouteHookAction.ReplaceRequest" />
/// at trigger and <see cref="ExtensionRouteHookAction.ReplaceResponse" /> at return are legal replacements.
/// </remarks>
public interface IExtensionRouteEvents
{
    /// <summary>Subscribes to global standard event-bus route observations.</summary>
    /// <param name="callback">The asynchronous standard event-bus callback.</param>
    /// <returns><see langword="true" /> when the subscription was accepted before the generation cap.</returns>
    bool TrySubscribe(Func<ExtensionEvent, CancellationToken, ValueTask> callback);

    /// <summary>Registers a global asynchronous action-capable hook for one stage.</summary>
    /// <param name="stage">The trigger or return stage at which the hook runs.</param>
    /// <param name="callback">
    /// The callback that receives route identity through <see cref="ExtensionRouteHookContext" /> and must
    /// return a validated result within <see cref="ExtensionRouteHookLimits.MaximumCallbackDuration" /> while
    /// observing cancellation.
    /// </param>
    /// <returns><see langword="true" /> when the hook registration was accepted before the generation cap.</returns>
    bool TryRegisterHook(
        ExtensionRouteEventStage stage,
        Func<ExtensionRouteHookContext, CancellationToken, ValueTask<ExtensionRouteHookResult>> callback);
}
