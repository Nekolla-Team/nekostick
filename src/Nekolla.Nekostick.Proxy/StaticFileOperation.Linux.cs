using System.Runtime.InteropServices;

namespace Nekolla.Nekostick.Proxy;

internal sealed class LinuxStaticFileOperation : IStaticFileOperation
{
    private const int O_RDONLY = 0;
    private const int O_CLOEXEC = 0x00080000;
    private const int O_NOFOLLOW = 0x00020000;
    private const int O_DIRECTORY = 0x00010000;
    private const int AT_FDCWD = -100;
    private const int AT_SYMLINK_NOFOLLOW = 0x100;

    private readonly StaticFileOperationAbiDescriptor _abi;

    private LinuxStaticFileOperation(StaticFileOperationAbiDescriptor abi)
    {
        _abi = abi;
    }

    internal static IStaticFileOperation Create()
    {
        if (IntPtr.Size != 8
            || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)
            || !LinuxStaticFileNative.IsVerifiedGlibc())
        {
            return new UnsupportedStaticFileOperation();
        }

        var abi = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? StaticFileOperationAbiDescriptor.LinuxGlibcX64
            : StaticFileOperationAbiDescriptor.LinuxGlibcArm64;
        return abi.IsUsable
            ? new LinuxStaticFileOperation(abi)
            : new UnsupportedStaticFileOperation();
    }

    public StaticFileOperationResult OpenReadOnly(string canonicalRootPath, string canonicalTargetPath) =>
        StaticFileOperationCore.OpenVerified(
            canonicalRootPath,
            canonicalTargetPath,
            _abi,
            rootFlags: O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC,
            intermediateDirectoryFlags: O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC,
            finalFileFlags: O_RDONLY | O_NOFOLLOW | O_CLOEXEC,
            atFdcwd: AT_FDCWD,
            atSymlinkNoFollow: AT_SYMLINK_NOFOLLOW,
            LinuxStaticFileNative.Open,
            LinuxStaticFileNative.OpenAt,
            LinuxStaticFileNative.FStat,
            LinuxStaticFileNative.FStatAt,
            LinuxStaticFileNative.Close);
}

internal static partial class LinuxStaticFileNative
{
    internal static bool IsVerifiedGlibc()
    {
        nint library = IntPtr.Zero;
        try
        {
            if (!NativeLibrary.TryLoad("libc.so.6", out library)
                || !HasExport(library, "open")
                || !HasExport(library, "openat")
                || !HasExport(library, "fstat")
                || !HasExport(library, "fstatat")
                || !HasExport(library, "close")
                || !HasExport(library, "gnu_get_libc_version"))
            {
                return false;
            }

            var versionPointer = GnuGetLibcVersion();
            var version = versionPointer == IntPtr.Zero
                ? null
                : Marshal.PtrToStringAnsi(versionPointer);
            return !string.IsNullOrEmpty(version)
                && version![0] >= '0'
                && version[0] <= '9';
        }
        catch (Exception)
        {
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
                catch (Exception)
                {
                }
            }
        }
    }

    private static bool HasExport(nint library, string name)
    {
        try
        {
            return NativeLibrary.GetExport(library, name) != IntPtr.Zero;
        }
        catch (Exception)
        {
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
        return StaticNativeCallResult.Failed(StaticNativeErrorMapper.FromLinuxErrno(error));
    }

    internal static StaticNativeCallResult OpenAt(int directoryFd, nint path, int flags, int mode)
    {
        var fileDescriptor = OpenAtNative(directoryFd, path, flags, mode);
        if (fileDescriptor >= 0)
        {
            return StaticNativeCallResult.Succeeded(fileDescriptor);
        }

        var error = Marshal.GetLastPInvokeError();
        return StaticNativeCallResult.Failed(StaticNativeErrorMapper.FromLinuxErrno(error));
    }

    internal static StaticNativeCallResult FStat(int fileDescriptor, nint statBuffer)
    {
        var result = FStatNative(fileDescriptor, statBuffer);
        if (result == 0)
        {
            return StaticNativeCallResult.Succeeded(0);
        }

        var error = Marshal.GetLastPInvokeError();
        return StaticNativeCallResult.Failed(StaticNativeErrorMapper.FromLinuxErrno(error));
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
        return StaticNativeCallResult.Failed(StaticNativeErrorMapper.FromLinuxErrno(error));
    }

    internal static StaticNativeCallResult Close(int fileDescriptor)
    {
        var result = CloseNative(fileDescriptor);
        if (result == 0)
        {
            return StaticNativeCallResult.Succeeded(0);
        }

        var error = Marshal.GetLastPInvokeError();
        return StaticNativeCallResult.Failed(StaticNativeErrorMapper.FromLinuxErrno(error));
    }

    [LibraryImport("libc.so.6", EntryPoint = "open", SetLastError = true)]
    private static partial int OpenNative(nint path, int flags, int mode);

    [LibraryImport("libc.so.6", EntryPoint = "openat", SetLastError = true)]
    private static partial int OpenAtNative(int directoryFd, nint path, int flags, int mode);

    [LibraryImport("libc.so.6", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FStatNative(int fileDescriptor, nint statBuffer);

    [LibraryImport("libc.so.6", EntryPoint = "fstatat", SetLastError = true)]
    private static partial int FStatAtNative(
        int directoryFd,
        nint path,
        nint statBuffer,
        int flags);

    [LibraryImport("libc.so.6", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseNative(int fileDescriptor);

    [LibraryImport("libc.so.6", EntryPoint = "gnu_get_libc_version")]
    private static partial nint GnuGetLibcVersion();
}
