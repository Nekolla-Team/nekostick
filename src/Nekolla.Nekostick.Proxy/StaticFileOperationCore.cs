using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace Nekolla.Nekostick.Proxy;

internal static class StaticFileOperationCore
{
    internal static StaticFileOperationResult OpenVerified(
        string canonicalRootPath,
        string canonicalTargetPath,
        StaticFileOperationAbiDescriptor abi,
        int rootFlags,
        int intermediateDirectoryFlags,
        int finalFileFlags,
        int atFdcwd,
        int atSymlinkNoFollow,
        StaticFileOpenPathFunction openPath,
        StaticFileOpenAtFunction openAt,
        StaticFileFStatFunction fstat,
        StaticFileFStatAtFunction fstatat,
        StaticFileCloseFunction close)
    {
        if (!abi.IsUsable
            || atFdcwd != abi.AtFdcwd
            || atSymlinkNoFollow != abi.AtSymlinkNoFollow)
        {
            return StaticFileOperationResult.FromStatus(StaticFileOperationStatus.UnsupportedAbi);
        }

        if (!TryBuildSafeSegments(
                canonicalRootPath,
                canonicalTargetPath,
                out var rootSegments,
                out var targetSegments)
            || targetSegments.Length <= rootSegments.Length)
        {
            return StaticFileOperationResult.FromStatus(StaticFileOperationStatus.NonRegularFile);
        }

        nint openedStatBuffer = IntPtr.Zero;
        nint directoryEntryStatBuffer = IntPtr.Zero;
        SafeFileHandle? parentHandle = null;
        SafeFileHandle? openedHandle = null;
        try
        {
            openedStatBuffer = Marshal.AllocHGlobal(abi.StatSize);
            directoryEntryStatBuffer = Marshal.AllocHGlobal(abi.StatSize);

            var rootStatus = StaticFileOperationStatus.NativeFailure;
            if (!TryInvokePath(
                    "/",
                    path => openPath(path, rootFlags, 0),
                    out var rootResult)
                || !TryOpenHandle(rootResult, close, out parentHandle, out rootStatus))
            {
                return StaticFileOperationResult.FromStatus(
                    rootStatus == StaticFileOperationStatus.Opened
                        ? StaticFileOperationStatus.NativeFailure
                        : rootStatus);
            }

            for (var index = 0; index < rootSegments.Length; index++)
            {
                if (!TryOpenChildDirectory(
                        rootSegments[index],
                        parentHandle,
                        intermediateDirectoryFlags,
                        openAt,
                        close,
                        out var nextDirectoryHandle,
                        out var status))
                {
                    return StaticFileOperationResult.FromStatus(status);
                }

                parentHandle!.Dispose();
                parentHandle = nextDirectoryHandle;
            }

            var firstTargetSegment = rootSegments.Length;
            for (var index = firstTargetSegment; index < targetSegments.Length - 1; index++)
            {
                if (!TryOpenChildDirectory(
                        targetSegments[index],
                        parentHandle,
                        intermediateDirectoryFlags,
                        openAt,
                        close,
                        out var nextDirectoryHandle,
                        out var status))
                {
                    return StaticFileOperationResult.FromStatus(status);
                }

                parentHandle!.Dispose();
                parentHandle = nextDirectoryHandle;
            }

            var finalName = targetSegments[^1];
            if (!TryOpenChild(
                    finalName,
                    parentHandle,
                    finalFileFlags,
                    openAt,
                    close,
                    out openedHandle,
                    out var finalStatus))
            {
                return StaticFileOperationResult.FromStatus(finalStatus);
            }

            var openedFileDescriptor = GetFileDescriptor(openedHandle);
            var parentFileDescriptor = GetFileDescriptor(parentHandle);
            var openedStatResult = fstat(openedFileDescriptor, openedStatBuffer);
            if (!openedStatResult.IsSucceeded)
            {
                return StaticFileOperationResult.FromStatus(MapNativeStatus(openedStatResult.Status));
            }

            if (!TryInvokePath(
                    finalName,
                    path => fstatat(
                        parentFileDescriptor,
                        path,
                        directoryEntryStatBuffer,
                        atSymlinkNoFollow),
                    out var directoryEntryStatResult))
            {
                return StaticFileOperationResult.FromStatus(StaticFileOperationStatus.NativeFailure);
            }

            if (!directoryEntryStatResult.IsSucceeded)
            {
                return StaticFileOperationResult.FromStatus(
                    MapNativeStatus(directoryEntryStatResult.Status));
            }

            if (!StaticFileIdentityReader.TryRead(
                    openedStatBuffer,
                    abi,
                    out var openedMetadata)
                || !StaticFileIdentityReader.TryRead(
                    directoryEntryStatBuffer,
                    abi,
                    out var directoryEntryMetadata))
            {
                return StaticFileOperationResult.FromStatus(StaticFileOperationStatus.NativeFailure);
            }

            var proof = StaticFileOperationIdentityProof.Verify(
                new StaticFileOperationProofInput(
                    AbiSupported: true,
                    OpenedIsRegularFile: openedMetadata.IsRegularFile(abi),
                    DirectoryEntryIsRegularFile: directoryEntryMetadata.IsRegularFile(abi),
                    OpenedIdentity: openedMetadata.Identity,
                    DirectoryEntryIdentity: directoryEntryMetadata.Identity));
            if (proof != StaticFileOperationProofKind.Succeeded)
            {
                return StaticFileOperationResult.FromStatus(proof switch
                {
                    StaticFileOperationProofKind.IdentityMismatch =>
                        StaticFileOperationStatus.IdentityMismatch,
                    StaticFileOperationProofKind.NonRegularFile =>
                        StaticFileOperationStatus.NonRegularFile,
                    _ => StaticFileOperationStatus.UnsupportedAbi
                });
            }

            if (!openedMetadata.TryGetLastModifiedUtc(out var lastModifiedUtc))
            {
                return StaticFileOperationResult.FromStatus(StaticFileOperationStatus.NativeFailure);
            }

            var openedFile = new StaticOpenedFile(
                openedHandle!,
                openedMetadata.Length,
                lastModifiedUtc);
            openedHandle = null;
            return StaticFileOperationResult.Opened(openedFile);
        }
        catch (Exception)
        {
            return StaticFileOperationResult.FromStatus(StaticFileOperationStatus.NativeFailure);
        }
        finally
        {
            openedHandle?.Dispose();
            parentHandle?.Dispose();
            if (openedStatBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(openedStatBuffer);
            }

            if (directoryEntryStatBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(directoryEntryStatBuffer);
            }
        }
    }

    private static bool TryOpenChildDirectory(
        string segment,
        SafeFileHandle? parentHandle,
        int flags,
        StaticFileOpenAtFunction openAt,
        StaticFileCloseFunction close,
        out SafeFileHandle? childHandle,
        out StaticFileOperationStatus status)
    {
        childHandle = null;
        status = StaticFileOperationStatus.NativeFailure;
        if (!TryInvokePath(
                segment,
                path => openAt(GetFileDescriptor(parentHandle), path, flags, 0),
                out var nativeResult))
        {
            return false;
        }

        return TryOpenHandle(nativeResult, close, out childHandle, out status);
    }

    private static bool TryOpenChild(
        string segment,
        SafeFileHandle? parentHandle,
        int flags,
        StaticFileOpenAtFunction openAt,
        StaticFileCloseFunction close,
        out SafeFileHandle? childHandle,
        out StaticFileOperationStatus status)
    {
        childHandle = null;
        status = StaticFileOperationStatus.NativeFailure;
        if (!TryInvokePath(
                segment,
                path => openAt(GetFileDescriptor(parentHandle), path, flags, 0),
                out var nativeResult))
        {
            return false;
        }

        return TryOpenHandle(nativeResult, close, out childHandle, out status);
    }

    private static bool TryBuildSafeSegments(
        string rootPath,
        string targetPath,
        out string[] rootSegments,
        out string[] targetSegments)
    {
        rootSegments = Array.Empty<string>();
        targetSegments = Array.Empty<string>();
        if (!TrySplitAbsolutePath(rootPath, out rootSegments)
            || !TrySplitAbsolutePath(targetPath, out targetSegments)
            || targetSegments.Length < rootSegments.Length)
        {
            return false;
        }

        for (var index = 0; index < rootSegments.Length; index++)
        {
            if (!string.Equals(rootSegments[index], targetSegments[index], StringComparison.Ordinal))
            {
                rootSegments = Array.Empty<string>();
                targetSegments = Array.Empty<string>();
                return false;
            }
        }

        return true;
    }

    private static bool TrySplitAbsolutePath(string path, out string[] segments)
    {
        segments = Array.Empty<string>();
        if (string.IsNullOrEmpty(path)
            || path.Contains('\0')
            || path[0] != '/')
        {
            return false;
        }

        var trimmedPath = TrimTrailingSeparators(path);
        if (trimmedPath == "/")
        {
            return true;
        }

        var candidateSegments = trimmedPath[1..].Split('/');
        foreach (var segment in candidateSegments)
        {
            if (segment.Length == 0
                || segment is "." or ".."
                || segment.Contains('\0'))
            {
                return false;
            }
        }

        segments = candidateSegments;
        return true;
    }

    private static bool TryInvokePath(
        string path,
        Func<nint, StaticNativeCallResult> invoke,
        out StaticNativeCallResult result)
    {
        result = StaticNativeCallResult.Failed(StaticNativeCallStatus.Failed);
        nint buffer = IntPtr.Zero;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(path);
            buffer = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            Marshal.WriteByte(buffer, bytes.Length, 0);
            result = invoke(buffer);
            return true;
        }
        catch (Exception)
        {
            result = StaticNativeCallResult.Failed(StaticNativeCallStatus.Failed);
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static bool TryOpenHandle(
        StaticNativeCallResult nativeResult,
        StaticFileCloseFunction close,
        out SafeFileHandle? handle,
        out StaticFileOperationStatus status)
    {
        handle = null;
        status = nativeResult.IsSucceeded
            ? StaticFileOperationStatus.NativeFailure
            : MapNativeStatus(nativeResult.Status);
        if (!nativeResult.IsSucceeded || nativeResult.Value < 0)
        {
            return false;
        }

        SafeFileHandle? candidate = null;
        try
        {
            candidate = new SafeFileHandle((nint)nativeResult.Value, ownsHandle: true);
            if (candidate.IsInvalid)
            {
                candidate.Dispose();
                candidate = null;
                return false;
            }

            handle = candidate;
            candidate = null;
            status = StaticFileOperationStatus.Opened;
            return true;
        }
        catch (Exception)
        {
            if (candidate is null)
            {
                try
                {
                    _ = close(nativeResult.Value);
                }
                catch (Exception)
                {
                }
            }

            status = StaticFileOperationStatus.NativeFailure;
            return false;
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    private static StaticFileOperationStatus MapNativeStatus(StaticNativeCallStatus status) => status switch
    {
        StaticNativeCallStatus.Succeeded => StaticFileOperationStatus.Opened,
        StaticNativeCallStatus.NotFound => StaticFileOperationStatus.NotFound,
        StaticNativeCallStatus.AccessDenied => StaticFileOperationStatus.AccessDenied,
        StaticNativeCallStatus.LinkRejected => StaticFileOperationStatus.LinkRejected,
        _ => StaticFileOperationStatus.NativeFailure
    };

    private static int GetFileDescriptor(SafeFileHandle? handle)
    {
        if (handle is null || handle.IsClosed || handle.IsInvalid)
        {
            throw new InvalidOperationException();
        }

        var raw = handle.DangerousGetHandle().ToInt64();
        return raw is < 0 or > int.MaxValue ? throw new InvalidOperationException() : (int)raw;
    }

    private static string TrimTrailingSeparators(string path)
    {
        var end = path.Length;
        while (end > 1 && path[end - 1] == '/')
        {
            end--;
        }

        return end == path.Length ? path : path[..end];
    }
}
