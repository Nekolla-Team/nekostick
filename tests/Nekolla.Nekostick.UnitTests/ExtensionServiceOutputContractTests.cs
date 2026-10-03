using Nekolla.Nekostick.Contracts;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ExtensionServiceOutputContractTests
{
    private static readonly Guid ServiceId =
        new("0198a1af-6e94-7b25-9732-59c9075b14f6");


    [Fact]
    public void OutputStreamResultRequiresPayloadExactlyWhenSucceeded()
    {
        using var stream = new MemoryStream();

        var opened = new ExtensionServiceOutputStreamResult(
            true,
            ExtensionServiceOutputCode.Opened,
            ServiceId,
            stream);
        var failed = new ExtensionServiceOutputStreamResult(
            false,
            ExtensionServiceOutputCode.NotRunning,
            ServiceId,
            null);

        Assert.Same(stream, opened.Stream);
        Assert.Null(failed.Stream);
        Assert.Throws<ArgumentNullException>(() =>
            new ExtensionServiceOutputStreamResult(
                true,
                ExtensionServiceOutputCode.Opened,
                ServiceId,
                null));
        Assert.Throws<ArgumentException>(() =>
            new ExtensionServiceOutputStreamResult(
                false,
                ExtensionServiceOutputCode.Failed,
                ServiceId,
                new MemoryStream()));
    }


    [Fact]
    public void OutputEnumsExposeTheStreamSurface()
    {
        Assert.Equal(
            [ExtensionServiceOutputStream.Stdout, ExtensionServiceOutputStream.Stderr],
            Enum.GetValues<ExtensionServiceOutputStream>());
        Assert.Equal(
            [
                ExtensionServiceOutputCode.None,
                ExtensionServiceOutputCode.Opened,
                ExtensionServiceOutputCode.NotFound,
                ExtensionServiceOutputCode.NotRunning,
                ExtensionServiceOutputCode.Unsupported,
                ExtensionServiceOutputCode.Failed
            ],
            Enum.GetValues<ExtensionServiceOutputCode>());
    }


    [Fact]
    public void CapabilitySetCarriesTheOptionalServiceOutputAndRuntimeStateCapabilities()
    {
        var unsupported = Nekolla.Nekostick.Extensions.UnsupportedExtensionCapabilities.Create();
        var set = new ExtensionCapabilitySet(
            unsupported.ConfigurationApi,
            unsupported.Routes,
            unsupported.Services,
            unsupported.Endpoints,
            unsupported.FullConfiguration,
            unsupported.Supervisor,
            unsupported.RouteEvents,
            unsupported.LogWriter,
            unsupported.ExtensionManagement,
            unsupported.HostInfo,
            unsupported.ServiceOutput,
            unsupported.ServiceRuntimeState);

        Assert.Same(unsupported.ServiceOutput, set.ServiceOutput);
        Assert.Same(unsupported.ServiceRuntimeState, set.ServiceRuntimeState);
        Assert.Equal(new HostApiVersion(1, 4, 0), HostApiVersion.Current);
    }
}
