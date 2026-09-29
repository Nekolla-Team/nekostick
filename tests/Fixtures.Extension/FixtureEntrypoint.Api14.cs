using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Tests.Fixtures.Extension;

public sealed partial class FixtureEntrypoint
{
    private static readonly Guid OutputProbeId =
        Guid.Parse("01900000-0000-7000-8000-000000000715");

    private static async ValueTask<string> ProbeApi14CapabilitiesAsync(
        IExtensionHostBridge host,
        bool holdServiceOutput,
        int serviceOutputBlockPort,
        CancellationToken cancellationToken)
    {
        if (host is not IExtensionHostBridge14 bridge)
        {
            return "api14=unavailable";
        }

        var opened = await bridge.ServiceOutput.OpenStreamAsync(
                OutputProbeId,
                ExtensionServiceOutputStream.Stdout,
                cancellationToken)
            .ConfigureAwait(false);
        var subscribed = await bridge.ServiceOutput.SubscribeAsync(
                OutputProbeId,
                ExtensionServiceOutputStream.Stderr,
                holdServiceOutput && serviceOutputBlockPort > 0
                    ? new ProbeSink(serviceOutputBlockPort)
                    : new ProbeSink(0),
                cancellationToken)
            .ConfigureAwait(false);

        if (!holdServiceOutput && subscribed.Subscription is { } subscription)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }

        if (opened.Stream is { } stream)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        return $"api14=bridge14;serviceOutputOpen={opened.Code};serviceOutputSubscribe={subscribed.Code}";
    }

    private sealed class ProbeSink : IExtensionServiceOutputSink
    {
        private readonly int _blockPort;

        public ProbeSink(int blockPort) => _blockPort = blockPort;

        public void OnChunk(ExtensionServiceOutputChunk chunk)
        {
            if (_blockPort <= 0)
            {
                return;
            }

            using var client = new System.Net.Sockets.TcpClient();
            client.Connect("127.0.0.1", _blockPort);
            using var stream = client.GetStream();
            stream.WriteByte(1);
            Span<byte> release = stackalloc byte[1];
            stream.ReadExactly(release);
        }

        public void OnCompleted(ExtensionServiceOutputCompletionReason reason) { }

        public void OnDropped(long byteCount) { }
    }
}
