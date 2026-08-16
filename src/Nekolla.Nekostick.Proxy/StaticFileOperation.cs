using Microsoft.Win32.SafeHandles;

namespace Nekolla.Nekostick.Proxy;

internal interface IStaticFileOperation
{
    StaticFileOperationResult OpenReadOnly(string canonicalRootPath, string canonicalTargetPath);
}

internal enum StaticFileOperationStatus
{
    Opened,
    NotFound,
    AccessDenied,
    LinkRejected,
    IdentityMismatch,
    NonRegularFile,
    UnsupportedAbi,
    NativeFailure
}

internal enum StaticNativeCallStatus
{
    Succeeded,
    NotFound,
    AccessDenied,
    LinkRejected,
    Failed
}

internal readonly record struct StaticNativeCallResult(
    StaticNativeCallStatus Status,
    int Value)
{
    internal bool IsSucceeded => Status == StaticNativeCallStatus.Succeeded;

    internal static StaticNativeCallResult Succeeded(int value) =>
        new(StaticNativeCallStatus.Succeeded, value);

    internal static StaticNativeCallResult Failed(StaticNativeCallStatus status) =>
        status == StaticNativeCallStatus.Succeeded
            ? new(StaticNativeCallStatus.Failed, -1)
            : new(status, -1);
}

internal static class StaticNativeErrorMapper
{
    internal static StaticNativeCallStatus FromLinuxErrno(int error) => error switch
    {
        1 or 13 => StaticNativeCallStatus.AccessDenied,
        2 or 20 => StaticNativeCallStatus.NotFound,
        40 => StaticNativeCallStatus.LinkRejected,
        _ => StaticNativeCallStatus.Failed
    };

    internal static StaticNativeCallStatus FromDarwinErrno(int error) => error switch
    {
        1 or 13 => StaticNativeCallStatus.AccessDenied,
        2 or 20 => StaticNativeCallStatus.NotFound,
        62 => StaticNativeCallStatus.LinkRejected,
        _ => StaticNativeCallStatus.Failed
    };
}

internal readonly record struct StaticFileOperationAbiDescriptor(
    bool IsSupported,
    int PointerSize,
    int StatSize,
    int DeviceOffset,
    int DeviceSize,
    int InodeOffset,
    int InodeSize,
    int ModeOffset,
    int ModeSize,
    int SizeOffset,
    int SizeSize,
    int ModificationSecondsOffset,
    int ModificationSecondsSize,
    int ModificationNanosecondsOffset,
    int ModificationNanosecondsSize,
    uint RegularModeMask,
    uint RegularModeValue,
    int AtFdcwd,
    int AtSymlinkNoFollow)
{
    internal static StaticFileOperationAbiDescriptor LinuxGlibcX64 => new(
        IsSupported: true,
        PointerSize: 8,
        StatSize: 144,
        DeviceOffset: 0,
        DeviceSize: 8,
        InodeOffset: 8,
        InodeSize: 8,
        ModeOffset: 24,
        ModeSize: 4,
        SizeOffset: 48,
        SizeSize: 8,
        ModificationSecondsOffset: 88,
        ModificationSecondsSize: 8,
        ModificationNanosecondsOffset: 96,
        ModificationNanosecondsSize: 8,
        RegularModeMask: 0xF000U,
        RegularModeValue: 0x8000U,
        AtFdcwd: -100,
        AtSymlinkNoFollow: 0x100);

    internal static StaticFileOperationAbiDescriptor LinuxGlibcArm64 => new(
        IsSupported: true,
        PointerSize: 8,
        StatSize: 144,
        DeviceOffset: 0,
        DeviceSize: 8,
        InodeOffset: 8,
        InodeSize: 8,
        ModeOffset: 24,
        ModeSize: 4,
        SizeOffset: 48,
        SizeSize: 8,
        ModificationSecondsOffset: 88,
        ModificationSecondsSize: 8,
        ModificationNanosecondsOffset: 96,
        ModificationNanosecondsSize: 8,
        RegularModeMask: 0xF000U,
        RegularModeValue: 0x8000U,
        AtFdcwd: -100,
        AtSymlinkNoFollow: 0x100);

    internal static StaticFileOperationAbiDescriptor DarwinX64 => new(
        IsSupported: true,
        PointerSize: 8,
        StatSize: 144,
        DeviceOffset: 0,
        DeviceSize: 4,
        InodeOffset: 8,
        InodeSize: 8,
        ModeOffset: 4,
        ModeSize: 2,
        SizeOffset: 96,
        SizeSize: 8,
        ModificationSecondsOffset: 48,
        ModificationSecondsSize: 8,
        ModificationNanosecondsOffset: 56,
        ModificationNanosecondsSize: 8,
        RegularModeMask: 0xF000U,
        RegularModeValue: 0x8000U,
        AtFdcwd: -2,
        AtSymlinkNoFollow: 0x20);

    internal static StaticFileOperationAbiDescriptor DarwinArm64 => new(
        IsSupported: true,
        PointerSize: 8,
        StatSize: 144,
        DeviceOffset: 0,
        DeviceSize: 4,
        InodeOffset: 8,
        InodeSize: 8,
        ModeOffset: 4,
        ModeSize: 2,
        SizeOffset: 96,
        SizeSize: 8,
        ModificationSecondsOffset: 48,
        ModificationSecondsSize: 8,
        ModificationNanosecondsOffset: 56,
        ModificationNanosecondsSize: 8,
        RegularModeMask: 0xF000U,
        RegularModeValue: 0x8000U,
        AtFdcwd: -2,
        AtSymlinkNoFollow: 0x20);

    internal static StaticFileOperationAbiDescriptor Unsupported => new(
        IsSupported: false,
        PointerSize: 0,
        StatSize: 0,
        DeviceOffset: 0,
        DeviceSize: 0,
        InodeOffset: 0,
        InodeSize: 0,
        ModeOffset: 0,
        ModeSize: 0,
        SizeOffset: 0,
        SizeSize: 0,
        ModificationSecondsOffset: 0,
        ModificationSecondsSize: 0,
        ModificationNanosecondsOffset: 0,
        ModificationNanosecondsSize: 0,
        RegularModeMask: 0,
        RegularModeValue: 0,
        AtFdcwd: 0,
        AtSymlinkNoFollow: 0);

    internal bool IsUsable =>
        IsSupported
        && PointerSize == IntPtr.Size
        && StatSize > 0
        && Fits(DeviceOffset, DeviceSize)
        && Fits(InodeOffset, InodeSize)
        && Fits(ModeOffset, ModeSize)
        && Fits(SizeOffset, SizeSize)
        && Fits(ModificationSecondsOffset, ModificationSecondsSize)
        && Fits(ModificationNanosecondsOffset, ModificationNanosecondsSize)
        && RegularModeMask != 0
        && IsKnownProfile;

    private bool IsKnownProfile =>
        this == LinuxGlibcX64
        || this == LinuxGlibcArm64
        || this == DarwinX64
        || this == DarwinArm64;

    private bool Fits(int offset, int size) =>
        offset >= 0 && size > 0 && offset <= StatSize - size;
}

internal sealed class StaticFileOperationResult : IDisposable
{
    private StaticOpenedFile? _openedFile;

    private StaticFileOperationResult(StaticFileOperationStatus status, StaticOpenedFile? openedFile)
    {
        Status = status;
        _openedFile = openedFile;
    }

    internal StaticFileOperationStatus Status { get; }

    internal bool IsOpened => Status == StaticFileOperationStatus.Opened && _openedFile is not null;

    internal static StaticFileOperationResult Opened(StaticOpenedFile openedFile) =>
        new(StaticFileOperationStatus.Opened, openedFile);

    internal static StaticFileOperationResult FromStatus(StaticFileOperationStatus status) =>
        new(status, openedFile: null);

    internal StaticOpenedFile? TransferOpenedFile()
    {
        var openedFile = _openedFile;
        _openedFile = null;
        return openedFile;
    }

    public void Dispose()
    {
        _openedFile?.Dispose();
        _openedFile = null;
    }
}
internal sealed class StaticOpenedFile : IDisposable
{
    private SafeFileHandle? _handle;

    internal StaticOpenedFile(
        SafeFileHandle handle,
        long length,
        DateTimeOffset lastModifiedUtc)
    {
        _handle = handle;
        Length = length;
        LastModifiedUtc = lastModifiedUtc.ToUniversalTime();
    }

    internal long Length { get; }

    internal DateTimeOffset LastModifiedUtc { get; }

    internal SafeFileHandle? TransferHandle()
    {
        var handle = _handle;
        _handle = null;
        return handle;
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}

internal delegate StaticNativeCallResult StaticFileOpenPathFunction(nint path, int flags, int mode);

internal delegate StaticNativeCallResult StaticFileOpenAtFunction(
    int directoryFd,
    nint path,
    int flags,
    int mode);

internal delegate StaticNativeCallResult StaticFileFStatFunction(int fileDescriptor, nint statBuffer);

internal delegate StaticNativeCallResult StaticFileFStatAtFunction(
    int directoryFd,
    nint path,
    nint statBuffer,
    int flags);

internal delegate StaticNativeCallResult StaticFileCloseFunction(int fileDescriptor);
