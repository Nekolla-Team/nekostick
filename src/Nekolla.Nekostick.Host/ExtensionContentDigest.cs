using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Nekolla.Nekostick.Extensions;

namespace Nekolla.Nekostick.Host;

/// <summary>Computes the canonical local extension content digest.</summary>
internal static class ExtensionContentDigest
{
    private static readonly string[] ManifestNames = ["manifest.json", "manifest.yaml", "manifest.yml"];

    /// <summary>
    /// Computes SHA-256 over a length-prefixed manifest followed by a length-prefixed entry assembly.
    /// </summary>
    /// <param name="manifest">The validated manifest whose files are hashed.</param>
    /// <param name="logger">The optional host logger for digest diagnostics.</param>
    /// <returns>The canonical digest, or <see langword="null" /> when either file cannot be read.</returns>
    internal static string? TryCompute(ExtensionManifest? manifest, ILogger? logger = null)
    {
        if (manifest is null)
        {
            return null;
        }

        try
        {
            var manifestPath = FindManifestPath(manifest.ExtensionDirectory);
            if (manifestPath is null || string.IsNullOrWhiteSpace(manifest.EntryAssemblyPath))
            {
                return null;
            }

            using var manifestStream = OpenRead(manifestPath);
            using var entryAssemblyStream = OpenRead(manifest.EntryAssemblyPath);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendLengthPrefixed(digest, manifestStream);
            AppendLengthPrefixed(digest, entryAssemblyStream);
            var bytes = digest.GetHashAndReset();
            return $"sha256:{Convert.ToHexString(bytes).ToLowerInvariant()}";
        }
        catch (Exception exception)
        {
            HostLogMessages.ExtensionContentDigestFailed(
                logger ?? HostLoggerDefaults.Logger,
                exception,
                manifest.Id);
            // A local install can be removed or replaced while it is being scanned. The durable
            // content hash is intentionally nullable so management and publication remain usable.
            return null;
        }
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);

    private static string? FindManifestPath(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        string? selected = null;
        foreach (var name in ManifestNames)
        {
            var candidate = Path.Combine(root, name);
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (selected is not null)
            {
                return null;
            }

            selected = candidate;
        }

        return selected;
    }

    private static void AppendLengthPrefixed(IncrementalHash digest, Stream stream)
    {
        var expectedLength = stream.Length;
        Span<byte> length = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(length, checked((ulong)expectedLength));
        digest.AppendData(length);
        using var sink = new IncrementalHashStream(digest);
        stream.CopyTo(sink);
        if (sink.BytesWritten != expectedLength || stream.Length != expectedLength)
        {
            throw new IOException("The extension content changed while it was being hashed.");
        }
    }

    /// <summary>Adapts a hash sink to the synchronous stream copy API without buffering file contents.</summary>
    private sealed class IncrementalHashStream : Stream
    {
        private readonly IncrementalHash _digest;
        internal long BytesWritten { get; private set; }

        internal IncrementalHashStream(IncrementalHash digest) => _digest = digest;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            _digest.AppendData(buffer.AsSpan(offset, count));
            BytesWritten = checked(BytesWritten + count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _digest.AppendData(buffer);
            BytesWritten = checked(BytesWritten + buffer.Length);
        }
    }
}
