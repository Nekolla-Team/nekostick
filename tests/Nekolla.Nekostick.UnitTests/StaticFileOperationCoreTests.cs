using System.Runtime.InteropServices;
using System.Collections.Generic;
using Nekolla.Nekostick.Proxy;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class StaticFileOperationCoreTests
{
    [Fact]
    public void AnchoredTraversalDoesNotUseAtFdcwdForDescendants()
    {
        if (!IsSupportedTestPlatform())
        {
            return;
        }

        var fake = new FakeNative(CurrentAbi());
        using var result = Execute(fake);

        Assert.True(result.IsOpened);
        Assert.Equal(1, fake.OpenPathCount);
        Assert.Equal(4, fake.OpenAtCount);
        Assert.All(fake.OpenAtParentDescriptors, descriptor =>
            Assert.NotEqual(CurrentAbi().AtFdcwd, descriptor));
    }

    [Fact]
    public void RootLinkRejectionFailsClosed()
    {
        if (!IsSupportedTestPlatform())
        {
            return;
        }

        var fake = new FakeNative(CurrentAbi())
        {
            RootFailure = StaticNativeCallStatus.LinkRejected
        };

        using var result = Execute(fake);

        Assert.Equal(StaticFileOperationStatus.LinkRejected, result.Status);
        Assert.False(result.IsOpened);
    }

    [Theory]
    [InlineData((int)StaticNativeCallStatus.NotFound, (int)StaticFileOperationStatus.NotFound)]
    [InlineData((int)StaticNativeCallStatus.AccessDenied, (int)StaticFileOperationStatus.AccessDenied)]
    [InlineData((int)StaticNativeCallStatus.Failed, (int)StaticFileOperationStatus.NativeFailure)]
    public void RootNativeStatusRemainsTyped(int nativeStatus, int operationStatus)
    {
        if (!IsSupportedTestPlatform())
        {
            return;
        }

        var fake = new FakeNative(CurrentAbi())
        {
            RootFailure = (StaticNativeCallStatus)nativeStatus
        };

        using var result = Execute(fake);

        Assert.Equal((StaticFileOperationStatus)operationStatus, result.Status);
        Assert.False(result.IsOpened);
    }

    [Fact]
    public void IntermediateLinkRejectionFailsClosed()
    {
        if (!IsSupportedTestPlatform())
        {
            return;
        }

        var fake = new FakeNative(CurrentAbi())
        {
            FailOpenAtCall = 3,
            OpenAtFailure = StaticNativeCallStatus.LinkRejected
        };

        using var result = Execute(fake);

        Assert.Equal(StaticFileOperationStatus.LinkRejected, result.Status);
        Assert.False(result.IsOpened);
    }

    [Fact]
    public void FinalLinkRejectionFailsClosed()
    {
        if (!IsSupportedTestPlatform())
        {
            return;
        }

        var fake = new FakeNative(CurrentAbi())
        {
            FailOpenAtCall = 4,
            OpenAtFailure = StaticNativeCallStatus.LinkRejected
        };

        using var result = Execute(fake);

        Assert.Equal(StaticFileOperationStatus.LinkRejected, result.Status);
        Assert.False(result.IsOpened);
    }

    [Fact]
    public void IdentityMismatchFailsClosed()
    {
        if (!IsSupportedTestPlatform())
        {
            return;
        }

        var fake = new FakeNative(CurrentAbi())
        {
            IdentityMismatch = true
        };

        using var result = Execute(fake);

        Assert.Equal(StaticFileOperationStatus.IdentityMismatch, result.Status);
        Assert.False(result.IsOpened);
    }

    [Fact]
    public void NonRegularMetadataFailsClosed()
    {
        if (!IsSupportedTestPlatform())
        {
            return;
        }

        var fake = new FakeNative(CurrentAbi())
        {
            NonRegular = true
        };

        using var result = Execute(fake);

        Assert.Equal(StaticFileOperationStatus.NonRegularFile, result.Status);
        Assert.False(result.IsOpened);
    }

    [Fact]
    public void UnsupportedAbiFailsClosedBeforeNativeTraversal()
    {
        var fake = new FakeNative(StaticFileOperationAbiDescriptor.Unsupported);
        using var result = StaticFileOperationCore.OpenVerified(
            "/root/content",
            "/root/content/sub/file",
            StaticFileOperationAbiDescriptor.Unsupported,
            1,
            2,
            4,
            0,
            0,
            fake.Open,
            fake.OpenAt,
            fake.FStat,
            fake.FStatAt,
            FakeNative.Close);

        Assert.Equal(StaticFileOperationStatus.UnsupportedAbi, result.Status);
        Assert.Equal(0, fake.OpenPathCount);
        Assert.Equal(0, fake.OpenAtCount);
    }

    [Fact]
    public void FinalHandleTransfersExactlyOnceAndIntermediateHandlesAreReleased()
    {
        if (!IsSupportedTestPlatform())
        {
            return;
        }

        var fake = new FakeNative(CurrentAbi());
        using var result = Execute(fake);

        var openedFile = result.TransferOpenedFile();
        Assert.NotNull(openedFile);
        Assert.False(result.IsOpened);
        Assert.Null(result.TransferOpenedFile());

        openedFile!.Dispose();
        result.Dispose();
    }

    private static StaticFileOperationResult Execute(FakeNative fake) =>
        StaticFileOperationCore.OpenVerified(
            "/root/content",
            "/root/content/sub/file",
            fake.Abi,
            rootFlags: 1,
            intermediateDirectoryFlags: 2,
            finalFileFlags: 4,
            atFdcwd: fake.Abi.AtFdcwd,
            atSymlinkNoFollow: fake.Abi.AtSymlinkNoFollow,
            fake.Open,
            fake.OpenAt,
            fake.FStat,
            fake.FStatAt,
            FakeNative.Close);

    private static StaticFileOperationAbiDescriptor CurrentAbi() =>
        OperatingSystem.IsMacOS()
            ? StaticFileOperationAbiDescriptor.DarwinX64
            : StaticFileOperationAbiDescriptor.LinuxGlibcX64;

    private static bool IsSupportedTestPlatform() =>
        IntPtr.Size == 8 && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

    private sealed class FakeNative
    {
        internal FakeNative(StaticFileOperationAbiDescriptor abi)
        {
            Abi = abi;
        }

        internal StaticFileOperationAbiDescriptor Abi { get; }

        internal int OpenPathCount { get; private set; }

        internal int OpenAtCount { get; private set; }

        internal List<int> OpenAtParentDescriptors { get; } = new();

        internal StaticNativeCallStatus? RootFailure { get; init; }

        internal int? FailOpenAtCall { get; init; }

        internal StaticNativeCallStatus OpenAtFailure { get; init; } = StaticNativeCallStatus.Failed;

        internal bool IdentityMismatch { get; init; }

        internal bool NonRegular { get; init; }

        internal StaticNativeCallResult Open(nint path, int flags, int mode)
        {
            OpenPathCount++;
            return RootFailure is { } failure
                ? StaticNativeCallResult.Failed(failure)
                : StaticNativeCallResult.Succeeded(OpenDescriptor());
        }

        internal StaticNativeCallResult OpenAt(int directoryFd, nint path, int flags, int mode)
        {
            OpenAtCount++;
            OpenAtParentDescriptors.Add(directoryFd);
            if (FailOpenAtCall == OpenAtCount)
            {
                return StaticNativeCallResult.Failed(OpenAtFailure);
            }

            return StaticNativeCallResult.Succeeded(OpenDescriptor());
        }

        internal StaticNativeCallResult FStat(int fileDescriptor, nint statBuffer)
        {
            WriteMetadata(statBuffer, mismatch: false);
            return StaticNativeCallResult.Succeeded(0);
        }

        internal StaticNativeCallResult FStatAt(
            int directoryFd,
            nint path,
            nint statBuffer,
            int flags)
        {
            WriteMetadata(statBuffer, IdentityMismatch);
            return StaticNativeCallResult.Succeeded(0);
        }

        internal static StaticNativeCallResult Close(int fileDescriptor) =>
            StaticNativeCallResult.Succeeded(0);

        private void WriteMetadata(nint buffer, bool mismatch)
        {
            WriteUnsigned(buffer, Abi.DeviceOffset, Abi.DeviceSize, mismatch ? 8UL : 7UL);
            WriteUnsigned(buffer, Abi.InodeOffset, Abi.InodeSize, 11);
            var mode = NonRegular ? 0x4000U : Abi.RegularModeValue;
            WriteUnsigned(buffer, Abi.ModeOffset, Abi.ModeSize, mode);
            Marshal.WriteInt64(buffer, Abi.SizeOffset, 3);
            Marshal.WriteInt64(buffer, Abi.ModificationSecondsOffset, 1_700_000_000);
            Marshal.WriteInt64(buffer, Abi.ModificationNanosecondsOffset, 100);
        }

        private static int OpenDescriptor()
        {
            var handle = File.OpenHandle(
                "/dev/null",
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            var descriptor = checked((int)handle.DangerousGetHandle().ToInt64());
            handle.SetHandleAsInvalid();
            handle.Dispose();
            return descriptor;
        }

        private static void WriteUnsigned(nint buffer, int offset, int size, ulong value)
        {
            switch (size)
            {
                case 2:
                    Marshal.WriteInt16(buffer, offset, unchecked((short)value));
                    break;
                case 4:
                    Marshal.WriteInt32(buffer, offset, unchecked((int)value));
                    break;
                case 8:
                    Marshal.WriteInt64(buffer, offset, unchecked((long)value));
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }
    }
}
