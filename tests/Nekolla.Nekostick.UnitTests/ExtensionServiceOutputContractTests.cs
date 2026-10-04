using Nekolla.Nekostick.Contracts;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ExtensionServiceOutputContractTests
{
    private static readonly Guid ServiceId =
        new("0198a1af-6e94-7b25-9732-59c9075b14f6");


    [Fact]
    public void OutputStreamResultRequiresPayloadAndFailureDetailInvariants()
    {
        using var stream = new MemoryStream();
        var failureDetail = new ExtensionErrorDetail("The configured service process is not running.");

        var opened = new ExtensionServiceOutputStreamResult(
            true,
            ExtensionServiceOutputCode.Opened,
            ServiceId,
            stream,
            detail: null);
        var failed = new ExtensionServiceOutputStreamResult(
            false,
            ExtensionServiceOutputCode.NotRunning,
            ServiceId,
            null,
            failureDetail);

        Assert.True(opened.Succeeded);
        Assert.Same(stream, opened.Stream);
        Assert.Null(opened.Detail);
        Assert.False(failed.Succeeded);
        Assert.Null(failed.Stream);
        Assert.Same(failureDetail, failed.Detail);
        Assert.Throws<ArgumentNullException>(() =>
            new ExtensionServiceOutputStreamResult(
                true,
                ExtensionServiceOutputCode.Opened,
                ServiceId,
                null,
                detail: null));
        Assert.Throws<ArgumentException>(() =>
            new ExtensionServiceOutputStreamResult(
                false,
                ExtensionServiceOutputCode.Failed,
                ServiceId,
                stream,
                new ExtensionErrorDetail("A failed result cannot include a stream.")));
        Assert.Throws<ArgumentNullException>(() =>
            new ExtensionServiceOutputStreamResult(
                false,
                ExtensionServiceOutputCode.NotRunning,
                ServiceId,
                null,
                detail: null));
        Assert.Throws<ArgumentException>(() =>
            new ExtensionServiceOutputStreamResult(
                true,
                ExtensionServiceOutputCode.Opened,
                ServiceId,
                stream,
                new ExtensionErrorDetail("A successful result cannot include error detail.")));

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
