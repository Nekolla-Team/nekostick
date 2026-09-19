using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Isolates one imported contract object behind the provider extension's dispatch turnstile.</summary>
/// <remarks>
/// Every interface call enters the provider's turnstile, so a call never observes a provider that is
/// mid-replacement: calls arriving while the provider is suspended wait for the replacement, and the
/// provider's drain waits for in-flight calls. When the captured provider instance is gone, the proxy
/// re-resolves the import and rebinds to the replacement before failing; calls against a permanently
/// unavailable provider fail fast with <see cref="InvalidOperationException" />.
/// </remarks>
/// <typeparam name="TContract">The shared contract interface type.</typeparam>
internal class ExtensionContractProxy<TContract> : DispatchProxy
    where TContract : class
{
    private static readonly MethodInfo s_invokeTaskAsync = typeof(ExtensionContractProxy<TContract>)
        .GetMethod(nameof(InvokeTaskAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo s_invokeTaskOfTAsync = typeof(ExtensionContractProxy<TContract>)
        .GetMethod(nameof(InvokeTaskOfTAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo s_invokeValueTaskAsync = typeof(ExtensionContractProxy<TContract>)
        .GetMethod(nameof(InvokeValueTaskAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo s_invokeValueTaskOfTAsync = typeof(ExtensionContractProxy<TContract>)
        .GetMethod(nameof(InvokeValueTaskOfTAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private volatile Binding _binding = null!;
    private Func<(ExtensionInstance Provider, object Target, ExtensionDispatchTurnstile Turnstile)?> _resolver = null!;
    private string _providerId = string.Empty;

    /// <summary>Wraps one resolved contract object behind the provider's turnstile.</summary>
    /// <param name="target">The provider's contract implementation.</param>
    /// <param name="turnstile">The provider extension's dispatch turnstile.</param>
    /// <param name="providerId">The provider extension identifier used in failure messages.</param>
    /// <param name="provider">The provider instance the target belongs to.</param>
    /// <param name="resolver">Re-resolves the import after a provider replacement; returns null when no provider is available.</param>
    /// <returns>The isolated contract reference handed to the consumer.</returns>
    internal static TContract Wrap(
        TContract target,
        ExtensionDispatchTurnstile turnstile,
        string providerId,
        ExtensionInstance provider,
        Func<(ExtensionInstance Provider, object Target, ExtensionDispatchTurnstile Turnstile)?> resolver)
    {
        var proxy = Create<TContract, ExtensionContractProxy<TContract>>();
        var self = (ExtensionContractProxy<TContract>)(object)proxy;
        self._binding = new Binding(target, provider, turnstile);
        self._providerId = providerId;
        self._resolver = resolver;
        return proxy;
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2012:Use ValueTasks correctly",
        Justification = "DispatchProxy.Invoke returns object, so the wrapped ValueTask must be boxed; it is awaited exactly once by the caller.")]
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var returnType = targetMethod.ReturnType;
        if (returnType == typeof(Task))
        {
            return InvokeTaskAsync(targetMethod, args);
        }

        if (returnType == typeof(ValueTask))
        {
            return InvokeValueTaskAsync(targetMethod, args);
        }

        if (returnType.IsGenericType)
        {
            var definition = returnType.GetGenericTypeDefinition();
            if (definition == typeof(Task<>))
            {
                return s_invokeTaskOfTAsync
                    .MakeGenericMethod(returnType.GetGenericArguments())
                    .Invoke(this, [targetMethod, args]);
            }

            if (definition == typeof(ValueTask<>))
            {
                return s_invokeValueTaskOfTAsync
                    .MakeGenericMethod(returnType.GetGenericArguments())
                    .Invoke(this, [targetMethod, args]);
            }
        }

        var instance = Enter(out var binding);
        try
        {
            return InvokeTarget(binding, targetMethod, args);
        }
        finally
        {
            instance.LeaveRequest();
        }
    }

    private ExtensionInstance Enter(out Binding binding)
    {
        for (var attempt = 0; ; attempt++)
        {
            binding = _binding;
            var entered = binding.Turnstile.Enter(binding.Provider, CancellationToken.None);
            if (entered is not null)
            {
                return entered;
            }

            if (attempt >= 2 || !TryRebind(ref binding))
            {
                throw Unavailable();
            }
        }
    }

    private async ValueTask<(ExtensionInstance Instance, Binding Binding)> EnterAsync()
    {
        Binding? binding = null;
        for (var attempt = 0; ; attempt++)
        {
            binding = _binding;
            var entered = await binding.Turnstile.EnterAsync(binding.Provider, CancellationToken.None)
                .ConfigureAwait(false);
            if (entered is not null)
            {
                return (entered, binding);
            }

            if (attempt >= 2 || !TryRebind(ref binding))
            {
                throw Unavailable();
            }
        }
    }

    private bool TryRebind(ref Binding binding)
    {
        if (_resolver() is not { } resolved ||
            ReferenceEquals(resolved.Provider, binding.Provider) ||
            !resolved.Provider.IsServing)
        {
            // A resolved but not-yet-serving provider (a pre-commit generation candidate) would
            // re-enter the same suspended turnstile and stack a second full entry wait past the
            // owning drain deadline; refusing the rebind lets the call unwind via Unavailable
            // while post-commit rebinds still succeed (committed providers are serving).
            return false;
        }

        // The captured provider is gone; atomically re-point the proxy at the replacement so
        // concurrent callers observe one consistent binding.
        binding = new Binding((TContract)resolved.Target, resolved.Provider, resolved.Turnstile);
        _binding = binding;
        _providerId = resolved.Provider.Manifest.Id;
        return true;
    }

    private InvalidOperationException Unavailable() =>
        new($"The contract provider '{_providerId}' is unavailable; it may be reloading or stopped.");

    private static object? InvokeTarget(Binding binding, MethodInfo targetMethod, object?[]? args)
    {
        try
        {
            return targetMethod.Invoke(binding.Target, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            // Preserve the pre-proxy contract: consumers observe the real exception, not the
            // reflection wrapper.
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private async Task InvokeTaskAsync(MethodInfo targetMethod, object?[]? args)
    {
        var (instance, binding) = await EnterAsync().ConfigureAwait(false);
        try
        {
            await ((Task)InvokeTarget(binding, targetMethod, args)!).ConfigureAwait(false);
        }
        finally
        {
            instance.LeaveRequest();
        }
    }

    private async Task<TResult> InvokeTaskOfTAsync<TResult>(MethodInfo targetMethod, object?[]? args)
    {
        var (instance, binding) = await EnterAsync().ConfigureAwait(false);
        try
        {
            return await ((Task<TResult>)InvokeTarget(binding, targetMethod, args)!).ConfigureAwait(false);
        }
        finally
        {
            instance.LeaveRequest();
        }
    }

    private async ValueTask InvokeValueTaskAsync(MethodInfo targetMethod, object?[]? args)
    {
        var (instance, binding) = await EnterAsync().ConfigureAwait(false);
        try
        {
            await ((ValueTask)InvokeTarget(binding, targetMethod, args)!).ConfigureAwait(false);
        }
        finally
        {
            instance.LeaveRequest();
        }
    }

    private async ValueTask<TResult> InvokeValueTaskOfTAsync<TResult>(MethodInfo targetMethod, object?[]? args)
    {
        var (instance, binding) = await EnterAsync().ConfigureAwait(false);
        try
        {
            return await ((ValueTask<TResult>)InvokeTarget(binding, targetMethod, args)!).ConfigureAwait(false);
        }
        finally
        {
            instance.LeaveRequest();
        }
    }

    private sealed record Binding(
        TContract Target,
        ExtensionInstance Provider,
        ExtensionDispatchTurnstile Turnstile);
}

/// <summary>Provides runtime-typed contract proxy creation.</summary>
internal static class ExtensionContractProxyFactory
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, MethodInfo> s_closed = new();
    /// <summary>Wraps one resolved contract object behind the provider's turnstile for a runtime contract type.</summary>
    /// <param name="contractType">The shared contract interface type.</param>
    /// <param name="target">The provider's contract implementation.</param>
    /// <param name="turnstile">The provider extension's dispatch turnstile.</param>
    /// <param name="providerId">The provider extension identifier used in failure messages.</param>
    /// <param name="provider">The provider instance the target belongs to.</param>
    /// <param name="resolver">Re-resolves the import after a provider replacement; returns null when no provider is available.</param>
    /// <returns>The isolated contract reference handed to the consumer.</returns>
    internal static object Wrap(
        Type contractType,
        object target,
        ExtensionDispatchTurnstile turnstile,
        string providerId,
        ExtensionInstance provider,
        Func<(ExtensionInstance Provider, object Target, ExtensionDispatchTurnstile Turnstile)?> resolver) =>
        s_closed.GetOrAdd(contractType, static type => typeof(ExtensionContractProxy<>)
                .MakeGenericType(type)
                .GetMethod("Wrap", BindingFlags.Static | BindingFlags.NonPublic)!)
            .Invoke(null, [target, turnstile, providerId, provider, resolver])!;
}
