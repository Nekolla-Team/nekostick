using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nekolla.Nekostick.Proxy;

internal sealed class DarwinStaticFileOperation : IStaticFileOperation
{
    private const int O_RDONLY = 0;
    private const int O_CLOEXEC = 0x01000000;
    private const int O_NOFOLLOW = 0x00000100;
    private const int O_DIRECTORY = 0x00100000;
    private const int AT_FDCWD = -2;
    private const int AT_SYMLINK_NOFOLLOW = 0x20;

    private readonly StaticFileOperationAbiDescriptor _abi;

    private DarwinStaticFileOperation(StaticFileOperationAbiDescriptor abi)
    {
        _abi = abi;
    }

    internal static IStaticFileOperation Create(ILogger? logger = null)
    {
        if (IntPtr.Size != 8
            || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)
            || !DarwinStaticFileNative.IsVerifiedLibSystem(logger))
        {
            return new UnsupportedStaticFileOperation();
        }

        var abi = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? StaticFileOperationAbiDescriptor.DarwinX64
            : StaticFileOperationAbiDescriptor.DarwinArm64;
        return abi.IsUsable
            ? new DarwinStaticFileOperation(abi)
            : new UnsupportedStaticFileOperation();
    }

    public StaticFileOperationResult OpenReadOnly(
        string canonicalRootPath,
        string canonicalTargetPath,
        ILogger? logger = null) =>
        StaticFileOperationCore.OpenVerified(
            canonicalRootPath,
            canonicalTargetPath,
            _abi,
            rootFlags: O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC,
            intermediateDirectoryFlags: O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC,
            finalFileFlags: O_RDONLY | O_NOFOLLOW | O_CLOEXEC,
            atFdcwd: AT_FDCWD,
            atSymlinkNoFollow: AT_SYMLINK_NOFOLLOW,
            DarwinStaticFileNative.Open,
            DarwinStaticFileNative.OpenAt,
            DarwinStaticFileNative.FStat,
            DarwinStaticFileNative.FStatAt,
            DarwinStaticFileNative.Close,
            logger);
}

internal static partial class DarwinStaticFileNative
{
    internal static bool IsVerifiedLibSystem(ILogger? logger = null)
    {
        nint library = IntPtr.Zero;
        try
        {
            if (!NativeLibrary.TryLoad("libSystem.B.dylib", out library)
                || !HasExport(library, "open", logger)
                || !HasExport(library, "openat", logger)
                || !HasExport(library, "fstat", logger)
                || !HasExport(library, "fstatat", logger)
                || !HasExport(library, "close", logger))
            {
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            if (ProxyLogThrottle.TryAcquire("DarwinStaticFileNative.IsVerifiedLibSystem", out var occurrences))
            {
                ProxyLogMessages.NativeLibraryVerificationFailed(
                    logger ?? NullLogger.Instance,
                    exception,
                    "Darwin",
                    occurrences);
            }

            return false;
        }
        finally
        {
            if (library != IntPtr.Zero)
            {
                try
                {
                    NativeLibrary.Free(library);
                }
                catch (Exception exception)
                {
                    ProxyLogMessages.NativeLibraryReleaseFailed(
                        logger ?? NullLogger.Instance,
                        exception,
                        "Darwin");
                }
            }
        }
    }

    private static bool HasExport(nint library, string name, ILogger? logger = null)
    {
        try
        {
            return NativeLibrary.GetExport(library, name) != IntPtr.Zero;
        }
        catch (Exception exception)
        {
            if (ProxyLogThrottle.TryAcquire("DarwinStaticFileNative.HasExport", out var occurrences))
            {
                ProxyLogMessages.NativeExportLookupFailed(
                    logger ?? NullLogger.Instance,
                    exception,
                    "Darwin",
                    name,
                    occurrences);
            }

            return false;
        }
    }

    internal static StaticNativeCallResult Open(nint path, int flags, int mode)
    {
        var fileDescriptor = OpenNative(path, flags, mode);
        if (fileDescriptor >= 0)
        {
            return StaticNativeCallResult.Succeeded(fileDescriptor);
        }

        var error = Marshal.GetLastPInvokeError();
        return StaticNativeCallResult.Failed(StaticNativeErrorMapper.FromDarwinErrno(error));
    }

    internal static StaticNativeCallResult OpenAt(int directoryFd, nint path, int flags, int mode)
    {
        var fileDescriptor = OpenAtNative(directoryFd, path, flags, mode);
        if (fileDescriptor >= 0)
        {
            return StaticNativeCallResult.Succeeded(fileDescriptor);
        }

        var error = Marshal.GetLastPInvokeError();
        return StaticNativeCallResult.Failed(StaticNativeErrorMapper.FromDarwinErrno(error));
    }

    internal static StaticNativeCallResult FStat(int fileDescriptor, nint statBuffer)
    {
        var result = FStatNative(fileDescriptor, statBuffer);
        if (result == 0)
        {
            return StaticNativeCallResult.Succeeded(0);
        }

        var error = Marshal.GetLastPInvokeError();
        return StaticNativeCallResult.Failed(StaticNativeErrorMapper.FromDarwinErrno(error));
    }

    internal static StaticNativeCallResult FStatAt(
        int directoryFd,
        nint path,
        nint statBuffer,
        int flags)
    {
        var result = FStatAtNative(directoryFd, path, statBuffer, flags);
        if (result == 0)
        {
            return StaticNativeCallResult.Succeeded(0);
        }

        var error = Marshal.GetLastPInvokeError();
        return StaticNativeCallResult.Failed(StaticNativeErrorMapper.FromDarwinErrno(error));
    }

    internal static StaticNativeCallResult Close(int fileDescriptor)
    {
        var result = CloseNative(fileDescriptor);
        if (result == 0)
        {
            return StaticNativeCallResult.Succeeded(0);
        }

        var error = Marshal.GetLastPInvokeError();
        return StaticNativeCallResult.Failed(StaticNativeErrorMapper.FromDarwinErrno(error));
    }

    [LibraryImport("libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
    private static partial int OpenNative(nint path, int flags, int mode);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "openat", SetLastError = true)]
    private static partial int OpenAtNative(int directoryFd, nint path, int flags, int mode);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FStatNative(int fileDescriptor, nint statBuffer);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "fstatat", SetLastError = true)]
    private static partial int FStatAtNative(
        int directoryFd,
        nint path,
        nint statBuffer,
        int flags);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseNative(int fileDescriptor);
}
