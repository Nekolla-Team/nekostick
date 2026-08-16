using System.Runtime.InteropServices;

namespace Nekolla.Nekostick.Proxy;

internal readonly record struct StaticFileIdentity(ulong Device, ulong Inode);

internal readonly record struct StaticFileMetadata(
    StaticFileIdentity Identity,
    uint Mode,
    long Length,
    long ModificationSeconds,
    long ModificationNanoseconds)
{
    internal bool IsRegularFile(StaticFileOperationAbiDescriptor abi) =>
        (Mode & abi.RegularModeMask) == abi.RegularModeValue;

    internal bool TryGetLastModifiedUtc(out DateTimeOffset value)
    {
        value = default;
        if (ModificationNanoseconds is < 0 or > 999_999_999)
        {
            return false;
        }

        try
        {
            value = DateTimeOffset.FromUnixTimeSeconds(ModificationSeconds)
                .AddTicks(ModificationNanoseconds / 100);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

internal enum StaticFileOperationProofKind
{
    Succeeded,
    IdentityMismatch,
    NonRegularFile,
    UnsupportedAbi
}

internal readonly record struct StaticFileOperationProofInput(
    bool AbiSupported,
    bool OpenedIsRegularFile,
    bool DirectoryEntryIsRegularFile,
    StaticFileIdentity OpenedIdentity,
    StaticFileIdentity DirectoryEntryIdentity);

/// <summary>
/// Deterministic identity proof used by the native operation and its internal test seam.
/// It deliberately accepts only typed metadata and booleans, never paths or native errors.
/// </summary>
internal static class StaticFileOperationIdentityProof
{
    internal static StaticFileOperationProofKind Verify(StaticFileOperationProofInput input)
    {
        if (!input.AbiSupported)
        {
            return StaticFileOperationProofKind.UnsupportedAbi;
        }

        if (!input.OpenedIsRegularFile || !input.DirectoryEntryIsRegularFile)
        {
            return StaticFileOperationProofKind.NonRegularFile;
        }

        return input.OpenedIdentity == input.DirectoryEntryIdentity
            ? StaticFileOperationProofKind.Succeeded
            : StaticFileOperationProofKind.IdentityMismatch;
    }
}

internal static class StaticFileIdentityReader
{
    internal static bool TryRead(
        IntPtr statBuffer,
        StaticFileOperationAbiDescriptor abi,
        out StaticFileMetadata metadata)
    {
        metadata = default;
        try
        {
            if (!abi.IsUsable || statBuffer == IntPtr.Zero)
            {
                return false;
            }

            var device = ReadUnsigned(statBuffer, abi.DeviceOffset, abi.DeviceSize);
            var inode = ReadUnsigned(statBuffer, abi.InodeOffset, abi.InodeSize);
            var mode = checked((uint)ReadUnsigned(statBuffer, abi.ModeOffset, abi.ModeSize));
            var length = Marshal.ReadInt64(statBuffer, abi.SizeOffset);
            var modificationSeconds = Marshal.ReadInt64(
                statBuffer,
                abi.ModificationSecondsOffset);
            var modificationNanoseconds = Marshal.ReadInt64(
                statBuffer,
                abi.ModificationNanosecondsOffset);

            if (length < 0 || modificationNanoseconds is < 0 or > 999_999_999)
            {
                return false;
            }

            metadata = new StaticFileMetadata(
                new StaticFileIdentity(device, inode),
                mode,
                length,
                modificationSeconds,
                modificationNanoseconds);
            return true;
        }
        catch (Exception)
        {
            metadata = default;
            return false;
        }
    }

    private static ulong ReadUnsigned(IntPtr buffer, int offset, int size) =>
        size switch
        {
            2 => unchecked((ushort)Marshal.ReadInt16(buffer, offset)),
            4 => unchecked((uint)Marshal.ReadInt32(buffer, offset)),
            8 => unchecked((ulong)Marshal.ReadInt64(buffer, offset)),
            _ => throw new InvalidOperationException()
        };
}
