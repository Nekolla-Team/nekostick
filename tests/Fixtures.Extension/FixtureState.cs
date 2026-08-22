using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Tests.Fixtures.Extension;

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