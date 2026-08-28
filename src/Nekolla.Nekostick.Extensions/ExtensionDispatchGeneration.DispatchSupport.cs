using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
 

namespace Nekolla.Nekostick.Extensions;

/// <summary>Provides an additive route-registration seam for Host capability factories.</summary>
/// <remarks>Legacy capability factories remain valid and receive the unsupported route-events facade.</remarks>
public interface IExtensionCapabilityFactoryRouteEvents
{
    /// <summary>Creates capabilities with the generation-wide route registration surface.</summary>
    /// <param name="extensionId">The Host-validated extension identifier.</param>
    /// <param name="handlerIsOwned">Checks a stable handler identifier owned by the extension.</param>
    /// <param name="routeEvents">The generation-wide route registration surface.</param>
    /// <returns>The immutable capability set.</returns>
    ExtensionCapabilitySet CreateWithRouteEvents(
        string extensionId,
        Func<string, bool> handlerIsOwned,
        IExtensionRouteEvents routeEvents);
}

/// <summary>Contains one immutable result after ordered route hooks have run.</summary>
public sealed record ExtensionRouteHookDispatchResult
{
    private ExtensionRouteHookDispatchResult(
        bool succeeded,
        bool cancelled,
        ExtensionRouteRequestSnapshot request,
        ExtensionRouteResponseSnapshot? response)
    {
        Succeeded = succeeded;
        Cancelled = cancelled;
        Request = request;
        Response = response;
    }

    /// <summary>Gets whether every hook returned a valid, stage-legal result.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets whether the ordered pipeline must cancel forwarding or response delivery.</summary>
    public bool Cancelled { get; }

    /// <summary>Gets the final immutable request snapshot.</summary>
    public ExtensionRouteRequestSnapshot Request { get; }

    /// <summary>Gets the final immutable response snapshot, when the stage supplies one.</summary>
    public ExtensionRouteResponseSnapshot? Response { get; }

    internal static ExtensionRouteHookDispatchResult Success(
        ExtensionRouteRequestSnapshot request,
        ExtensionRouteResponseSnapshot? response) =>
        new(true, false, request, response);

    internal static ExtensionRouteHookDispatchResult FailClosed(
        ExtensionRouteRequestSnapshot request,
        ExtensionRouteResponseSnapshot? response) =>
        new(false, true, request, response);
}

/// <summary>Captures route registrations made by one extension startup for one candidate generation.</summary>
internal sealed class ExtensionRouteRegistrationSet : IExtensionRouteEvents
{
    private readonly object _gate = new();
    private readonly ImmutableHashSet<Guid> _ownedRoutes;
    private readonly Func<Func<ExtensionEvent, CancellationToken, ValueTask>, bool>? _subscribeToQueue;
    private readonly List<ExtensionRouteSubscriptionRegistration> _subscriptions = new();
    private readonly List<ExtensionRouteHookRegistration> _hooks = new();
    private long _nextSequence;
    private bool _retired;

    internal ExtensionRouteRegistrationSet(
        string extensionId,
        ImmutableArray<Guid> ownedRoutes,
        Func<Func<ExtensionEvent, CancellationToken, ValueTask>, bool>? subscribeToQueue = null)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            throw new ArgumentException("An extension identifier is required.", nameof(extensionId));
        }

        _ownedRoutes = (ownedRoutes.IsDefault ? ImmutableArray<Guid>.Empty : ownedRoutes)
            .Where(IsUuidV7)
            .ToImmutableHashSet();
        _subscribeToQueue = subscribeToQueue;
    }

    internal ImmutableArray<ExtensionRouteSubscriptionRegistration> Subscriptions
    {
        get
        {
            lock (_gate)
            {
                return _subscriptions.ToImmutableArray();
            }
        }
    }

    internal ImmutableArray<ExtensionRouteHookRegistration> Hooks
    {
        get
        {
            lock (_gate)
            {
                return _hooks.ToImmutableArray();
            }
        }
    }
    internal bool HasSameOwnedRoutes(ImmutableArray<Guid> routes)
    {
        var normalized = (routes.IsDefault ? ImmutableArray<Guid>.Empty : routes).Where(IsUuidV7).ToImmutableHashSet();
        lock (_gate)
        {
            return _ownedRoutes.SetEquals(normalized);
        }
    }

    public bool TrySubscribe(Func<ExtensionEvent, CancellationToken, ValueTask> callback)
    {
        if (callback is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (_retired || _subscriptions.Count >= ExtensionRouteHookLimits.MaximumSubscriptionRegistrations)
            {
                return false;
            }

            if (_subscribeToQueue is not null && !_subscribeToQueue(callback))
            {
                return false;
            }

            _subscriptions.Add(new ExtensionRouteSubscriptionRegistration(
                NextSequenceLocked(),
                callback));
            return true;
        }
    }

    public bool TryRegisterHook(
        ExtensionRouteEventStage stage,
        Func<ExtensionRouteHookContext, CancellationToken, ValueTask<ExtensionRouteHookResult>> callback)
    {
        if (stage is not (ExtensionRouteEventStage.Trigger or ExtensionRouteEventStage.Return) || callback is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (_retired || _hooks.Count >= ExtensionRouteHookLimits.MaximumHookRegistrations)
            {
                return false;
            }

            _hooks.Add(new ExtensionRouteHookRegistration(
                stage,
                NextSequenceLocked(),
                callback));
            return true;
        }
    }

    internal void Retire()
    {
        lock (_gate)
        {
            if (_retired)
            {
                return;
            }

            _retired = true;
            _subscriptions.Clear();
            _hooks.Clear();
        }
    }


    private long NextSequenceLocked() => ++_nextSequence;

    private static bool IsUuidV7(Guid value)
    {
        if (value == Guid.Empty)
        {
            return false;
        }

        var text = value.ToString("D");
        return text[14] == '7' && text[19] is '8' or '9' or 'a' or 'b';
    }
}

internal sealed record ExtensionRouteSubscriptionRegistration(
    long RegistrationSequence,
    Func<ExtensionEvent, CancellationToken, ValueTask> Callback);

internal sealed record ExtensionRouteHookRegistration(
    ExtensionRouteEventStage Stage,
    long RegistrationSequence,
    Func<ExtensionRouteHookContext, CancellationToken, ValueTask<ExtensionRouteHookResult>> Callback);

public sealed partial class ExtensionDispatchGeneration
{
    private ImmutableArray<ExtensionRouteSubscriptionRegistration> _routeSubscriptions;
    private ImmutableArray<ExtensionRouteHookRegistration> _routeHooks;
    private readonly CancellationTokenSource _routeRetirement = new();

    /// <summary>Gets whether this generation has any global route observer or hook.</summary>
    public bool HasRouteObservers(Guid routeId)
    {
        if (routeId == Guid.Empty || IsRetiring)
        {
            return false;
        }

        return !_routeSubscriptions.IsDefaultOrEmpty || !_routeHooks.IsDefaultOrEmpty;
    }

    /// <summary>Gets whether this generation has any global action-capable route hook.</summary>
    public bool HasRouteHooks(Guid routeId) =>
        routeId != Guid.Empty && !IsRetiring && !_routeHooks.IsDefaultOrEmpty;

    /// <summary>Publishes one ordinary route observation through standard extension event queues.</summary>
    public int PublishRouteEvent(ExtensionRouteEvent? observation)
    {
        if (observation is null)
        {
            return 0;
        }

        ExtensionEvent extensionEvent;
        try
        {
            extensionEvent = observation.ToExtensionEvent();
        }
        catch
        {
            return 0;
        }

        // Admission and enqueue share the generation gate. Retirement therefore
        // linearizes either before this entire operation or after every enqueue.
        lock (_gate)
        {
            if (_retirementRequested || _released)
            {
                return 0;
            }

            var recipients = new HashSet<ExtensionInstance>();
            foreach (var subscription in _routeSubscriptions)
            {
                var context = _contexts.FirstOrDefault(value =>
                    value.RouteRegistrations?.Subscriptions.Any(item =>
                        ReferenceEquals(item.Callback, subscription.Callback)) == true);
                if (context is not null)
                {
                    recipients.Add(context.Instance);
                }
            }

            var published = 0;
            foreach (var recipient in recipients)
            {
                if (recipient.TryPublishEvent(extensionEvent))
                {
                    published++;
                }
            }

            return published;
        }
    }

    /// <summary>Runs global hooks serially for the requested stage and fails closed on any unsafe callback outcome.</summary>
    /// <param name="routeId">The matched stable route identifier retained in each hook context.</param>
    /// <param name="correlationId">The request correlation identifier.</param>
    /// <param name="stage">The trigger or return stage.</param>
    /// <param name="request">The current bounded request snapshot.</param>
    /// <param name="response">The current bounded response snapshot at return stage.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The final snapshots, or a cancellation result with no unsafe mutation.</returns>
    public async ValueTask<ExtensionRouteHookDispatchResult> DispatchRouteHooksAsync(
        Guid routeId,
        Guid correlationId,
        ExtensionRouteEventStage stage,
        ExtensionRouteRequestSnapshot request,
        ExtensionRouteResponseSnapshot? response,
        CancellationToken cancellationToken = default)
    {
        if (request is null ||
            routeId == Guid.Empty ||
            correlationId == Guid.Empty ||
            stage is not (ExtensionRouteEventStage.Trigger or ExtensionRouteEventStage.Return) ||
            IsRetiring)
        {
            return ExtensionRouteHookDispatchResult.FailClosed(request!, response);
        }

        var currentRequest = request;
        var currentResponse = response;
        foreach (var registration in _routeHooks)
        {
            if (IsRetiring)
            {
                return ExtensionRouteHookDispatchResult.FailClosed(request, response);
            }
            if (registration.Stage != stage)
            {
                continue;
            }

            var context = new ExtensionRouteHookContext(
                routeId,
                correlationId,
                stage,
                currentRequest,
                currentResponse);
            var result = await InvokeRouteHookAsync(registration, context, cancellationToken).ConfigureAwait(false);
            if (IsRetiring || result is null || !result.IsValidFor(context))
            {
                return ExtensionRouteHookDispatchResult.FailClosed(request, response);
            }

            switch (result.Action)
            {
                case ExtensionRouteHookAction.Continue:
                    break;
                case ExtensionRouteHookAction.ReplaceRequest when result.Request is not null:
                    currentRequest = result.Request;
                    break;
                case ExtensionRouteHookAction.ReplaceResponse when result.Response is not null:
                    currentResponse = result.Response;
                    break;
                case ExtensionRouteHookAction.CancelForwarding:
                    return ExtensionRouteHookDispatchResult.FailClosed(request, response);
                default:
                    return ExtensionRouteHookDispatchResult.FailClosed(request, response);
            }
        }

        return IsRetiring
            ? ExtensionRouteHookDispatchResult.FailClosed(request, response)
            : ExtensionRouteHookDispatchResult.Success(currentRequest, currentResponse);
    }

    internal void InitializeRouteDispatch()
    {
        var subscriptions = new List<ExtensionRouteSubscriptionRegistration>();
        var hooks = new List<ExtensionRouteHookRegistration>();
        foreach (var context in _contexts)
        {
            if (context.RouteRegistrations is null)
            {
                continue;
            }

            subscriptions.AddRange(context.RouteRegistrations.Subscriptions);
            hooks.AddRange(context.RouteRegistrations.Hooks);
        }

        _routeSubscriptions = subscriptions
            .OrderBy(value => value.RegistrationSequence)
            .Take(ExtensionRouteHookLimits.MaximumSubscriptionRegistrations)
            .ToImmutableArray();
        _routeHooks = hooks
            .OrderBy(value => value.RegistrationSequence)
            .Take(ExtensionRouteHookLimits.MaximumHookRegistrations)
            .ToImmutableArray();
    }

    internal void CancelRouteDispatch()
    {
        lock (_gate)
        {
            _retirementRequested = true;
            _routeSubscriptions = ImmutableArray<ExtensionRouteSubscriptionRegistration>.Empty;
            _routeHooks = ImmutableArray<ExtensionRouteHookRegistration>.Empty;
        }

        try
        {
            _routeRetirement.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void DisposeRouteDispatch() => _routeRetirement.Dispose();

    private async ValueTask<ExtensionRouteHookResult?> InvokeRouteHookAsync(
        ExtensionRouteHookRegistration registration,
        ExtensionRouteHookContext context,
        CancellationToken requestCancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            requestCancellation,
            _routeRetirement.Token);
        linked.CancelAfter(ExtensionRouteHookLimits.MaximumCallbackDuration);

        var timeoutTask = Task.Delay(ExtensionRouteHookLimits.MaximumCallbackDuration, linked.Token);
        Task<ExtensionRouteHookResult> callbackTask;
        try
        {
            // A callback may block before returning its ValueTask. Invoke it on a
            // worker so the dispatcher can enforce the hard deadline itself.
            callbackTask = Task.Run(async () =>
            {
                using var callbackScope = ExtensionCallbackGuard.Enter(ExtensionCallbackKind.Route);
                return await registration.Callback(context, linked.Token).ConfigureAwait(false);
            });
        }
        catch (Exception)
        {
            return ExtensionRouteHookResult.FailClosed;
        }

        try
        {
            var completed = await Task.WhenAny(callbackTask, timeoutTask).ConfigureAwait(false);
            if (!ReferenceEquals(completed, callbackTask) || !callbackTask.IsCompletedSuccessfully)
            {
                linked.Cancel();
                ObserveLateHook(callbackTask);
                return ExtensionRouteHookResult.FailClosed;
            }

            return await callbackTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            linked.Cancel();
            ObserveLateHook(callbackTask);
            return ExtensionRouteHookResult.FailClosed;
        }
    }

    private static void ObserveLateHook(Task<ExtensionRouteHookResult> callbackTask)
    {
        _ = callbackTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}