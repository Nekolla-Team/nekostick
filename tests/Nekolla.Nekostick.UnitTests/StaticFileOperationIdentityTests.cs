using Nekolla.Nekostick.Proxy;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class StaticFileOperationIdentityTests
{
    [Fact]
    public void MatchingDeviceAndInodeSucceeds()
    {
        var identity = new StaticFileIdentity(7, 11);
        var result = StaticFileOperationIdentityProof.Verify(
            new StaticFileOperationProofInput(true, true, true, identity, identity));

        Assert.Equal(StaticFileOperationProofKind.Succeeded, result);
    }

    [Fact]
    public void DeviceOrInodeMismatchFailsClosed()
    {
        var result = StaticFileOperationIdentityProof.Verify(
            new StaticFileOperationProofInput(
                true,
                true,
                true,
                new StaticFileIdentity(7, 11),
                new StaticFileIdentity(8, 11)));

        Assert.Equal(StaticFileOperationProofKind.IdentityMismatch, result);
    }

    [Fact]
    public void NonRegularFileFailsClosed()
    {
        var identity = new StaticFileIdentity(7, 11);
        var result = StaticFileOperationIdentityProof.Verify(
            new StaticFileOperationProofInput(true, false, true, identity, identity));

        Assert.Equal(StaticFileOperationProofKind.NonRegularFile, result);
    }

    [Fact]
    public void UnsupportedAbiFailsClosed()
    {
        var identity = new StaticFileIdentity(7, 11);
        var result = StaticFileOperationIdentityProof.Verify(
            new StaticFileOperationProofInput(false, true, true, identity, identity));

        Assert.Equal(StaticFileOperationProofKind.UnsupportedAbi, result);
    }

    [Theory]
    [InlineData(1, (int)StaticNativeCallStatus.AccessDenied)]
    [InlineData(13, (int)StaticNativeCallStatus.AccessDenied)]
    [InlineData(2, (int)StaticNativeCallStatus.NotFound)]
    [InlineData(20, (int)StaticNativeCallStatus.NotFound)]
    [InlineData(40, (int)StaticNativeCallStatus.LinkRejected)]
    [InlineData(5, (int)StaticNativeCallStatus.Failed)]
    public void LinuxNativeErrorsMapToTypedStatus(int error, int expected)
    {
        Assert.Equal((StaticNativeCallStatus)expected, StaticNativeErrorMapper.FromLinuxErrno(error));
    }

    [Fact]
    public void DarwinEloopMapsToLinkRejected()
    {
        Assert.Equal(
            StaticNativeCallStatus.LinkRejected,
            StaticNativeErrorMapper.FromDarwinErrno(62));
    }
}
