namespace Nekolla.Nekostick.Extensions;

/// <summary>Coordinates dispatch entry for one extension identifier across instance replacements.</summary>
/// <remarks>
/// Open entrants dispatch against the current serving instance; suspended entrants wait until the
/// turnstile resumes (reload committed or extension permanently unavailable). The turnstile is owned
/// by the runtime manager and survives the instance swap it guards.
/// </remarks>
internal sealed class ExtensionDispatchTurnstile
{
    // Entry waits must give up strictly before the owning instance's drain deadline
    // (LifecycleTimeout), so a handler blocked on a suspended turnstile unwinds via its own
    // InvalidOperationException and lets the drain complete instead of failing the stop.
    private static readonly TimeSpan EntryTimeout =
        ExtensionRuntimeManager.LifecycleTimeout - TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private TaskCompletionSource _resume = NewCompletionSource();
    private bool _suspended;
    private ExtensionInstance? _current;

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Points open entries at the given instance without changing the suspension state.</summary>
    /// <param name="instance">The instance entrants dispatch to, or null when none is loaded.</param>
    internal void SetCurrent(ExtensionInstance? instance)
    {
        lock (_gate)
        {
            _current = instance;
        }
    }

    /// <summary>Suspends entry; subsequent entrants wait until <see cref="Resume" />.</summary>
    internal void Suspend()
    {
        lock (_gate)
        {
            if (_suspended)
            {
                return;
            }

            _suspended = true;
            if (_resume.Task.IsCompleted)
            {
                _resume = NewCompletionSource();
            }
        }
    }

    /// <summary>Resumes entry and points entrants at the given instance.</summary>
    /// <param name="instance">The replacement instance, or null when the extension is permanently unavailable; null waiters observe an unavailable result.</param>
    internal void Resume(ExtensionInstance? instance)
    {
        lock (_gate)
        {
            _current = instance;
            _suspended = false;
            // Complete under the lock: the suspended-implies-incomplete invariant must never
            // be violated, or waiters would capture a completed task and spin. Continuations
            // run asynchronously, so completing here is safe.
            _resume.TrySetResult();
        }
    }

    /// <summary>Enters dispatch against the current serving instance, waiting while suspended.</summary>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <returns>The entered instance (the caller MUST call <see cref="ExtensionInstance.LeaveRequest" /> when done), or null when the extension is unavailable.</returns>
    internal ValueTask<ExtensionInstance?> EnterAsync(CancellationToken cancellationToken) =>
        EnterAsync(null, cancellationToken);

    /// <summary>Enters dispatch against one specific instance, waiting while suspended.</summary>
    /// <param name="target">The instance the entrant intends to call; when null, the current instance is used. A stopped target yields null even after resume, so stale references fail instead of reaching a replacement transparently.</param>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <returns>The entered instance (the caller MUST call <see cref="ExtensionInstance.LeaveRequest" /> when done), or null when unavailable.</returns>
    internal async ValueTask<ExtensionInstance?> EnterAsync(
        ExtensionInstance? target,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (!_suspended)
                {
                    var candidate = target ?? _current;
                    return candidate is not null && candidate.TryEnterRequest() ? candidate : null;
                }

                wait = _resume.Task;
            }

            try
            {
                // A suspension is bounded; on timeout the entrant observes an unavailable result
                // instead of waiting indefinitely (import cycles). See EntryTimeout above.
                await wait.WaitAsync(EntryTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }
    }

    /// <summary>Enters synchronously, blocking the calling thread while suspended.</summary>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <returns>The entered instance, or null when the extension is unavailable.</returns>
    internal ExtensionInstance? Enter(CancellationToken cancellationToken) =>
        Enter(null, cancellationToken);

    /// <summary>Enters synchronously against one specific instance, blocking while suspended.</summary>
    /// <param name="target">The instance the entrant intends to call; when null, the current instance is used.</param>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <returns>The entered instance, or null when unavailable.</returns>
    internal ExtensionInstance? Enter(ExtensionInstance? target, CancellationToken cancellationToken)
    {
        // Fast path: open turnstiles enter without allocating a Task.
        lock (_gate)
        {
            if (!_suspended)
            {
                var candidate = target ?? _current;
                return candidate is not null && candidate.TryEnterRequest() ? candidate : null;
            }
        }

        return EnterAsync(target, cancellationToken).AsTask().GetAwaiter().GetResult();
    }
}
