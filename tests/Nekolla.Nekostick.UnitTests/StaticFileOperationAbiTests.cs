using System.Runtime.InteropServices;
using Nekolla.Nekostick.Proxy;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class StaticFileOperationAbiTests
{
    [Fact]
    public void LinuxX64StatBufferUsesDeclaredProfile()
    {
        AssertProfile(
            StaticFileOperationAbiDescriptor.LinuxGlibcX64,
            expectedStatSize: 144,
            expectedDeviceSize: 8,
            expectedModeSize: 4,
            expectedSizeOffset: 48,
            expectedSecondsOffset: 88,
            expectedNanosecondsOffset: 96);
    }

    [Fact]
    public void LinuxArm64StatBufferUsesDeclaredProfile()
    {
        AssertProfile(
            StaticFileOperationAbiDescriptor.LinuxGlibcArm64,
            expectedStatSize: 144,
            expectedDeviceSize: 8,
            expectedModeSize: 4,
            expectedSizeOffset: 48,
            expectedSecondsOffset: 88,
            expectedNanosecondsOffset: 96);
    }

    [Fact]
    public void DarwinX64StatBufferUsesDeclaredProfile()
    {
        AssertProfile(
            StaticFileOperationAbiDescriptor.DarwinX64,
            expectedStatSize: 144,
            expectedDeviceSize: 4,
            expectedModeSize: 2,
            expectedSizeOffset: 96,
            expectedSecondsOffset: 48,
            expectedNanosecondsOffset: 56);
    }

    [Fact]
    public void DarwinArm64StatBufferUsesDeclaredProfile()
    {
        AssertProfile(
            StaticFileOperationAbiDescriptor.DarwinArm64,
            expectedStatSize: 144,
            expectedDeviceSize: 4,
            expectedModeSize: 2,
            expectedSizeOffset: 96,
            expectedSecondsOffset: 48,
            expectedNanosecondsOffset: 56);
    }

    private static void AssertProfile(
        StaticFileOperationAbiDescriptor abi,
        int expectedStatSize,
        int expectedDeviceSize,
        int expectedModeSize,
        int expectedSizeOffset,
        int expectedSecondsOffset,
        int expectedNanosecondsOffset)
    {
        Assert.True(abi.IsSupported);
        Assert.Equal(8, abi.PointerSize);
        Assert.Equal(expectedStatSize, abi.StatSize);
        Assert.Equal(expectedDeviceSize, abi.DeviceSize);
        Assert.Equal(expectedModeSize, abi.ModeSize);
        Assert.Equal(expectedSizeOffset, abi.SizeOffset);
        Assert.Equal(expectedSecondsOffset, abi.ModificationSecondsOffset);
        Assert.Equal(expectedNanosecondsOffset, abi.ModificationNanosecondsOffset);

        var buffer = Marshal.AllocHGlobal(abi.StatSize);
        try
        {
            WriteUnsigned(buffer, abi.DeviceOffset, abi.DeviceSize, 17);
            WriteUnsigned(buffer, abi.InodeOffset, abi.InodeSize, 29);
            WriteUnsigned(buffer, abi.ModeOffset, abi.ModeSize, abi.RegularModeValue);
            const long expectedLength = 37;
            if (abi == StaticFileOperationAbiDescriptor.DarwinX64
                || abi == StaticFileOperationAbiDescriptor.DarwinArm64)
            {
                const long birthtimeMarker = 8_765_432_100;
                Marshal.WriteInt64(buffer, 80, birthtimeMarker);
                Marshal.WriteInt64(buffer, 96, expectedLength);
            }
            else
            {
                Marshal.WriteInt64(buffer, abi.SizeOffset, expectedLength);
            }

            Marshal.WriteInt64(buffer, abi.ModificationSecondsOffset, 1_700_000_000);
            Marshal.WriteInt64(buffer, abi.ModificationNanosecondsOffset, 123_456_700);

            Assert.True(StaticFileIdentityReader.TryRead(buffer, abi, out var metadata));
            Assert.True(metadata.IsRegularFile(abi));
            Assert.Equal(expectedLength, metadata.Length);
            Assert.Equal(1_700_000_000, metadata.ModificationSeconds);
            Assert.Equal(123_456_700, metadata.ModificationNanoseconds);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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
