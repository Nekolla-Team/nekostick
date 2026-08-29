using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostDataDirectoryTests
{
    [Fact]
    public async Task ExtensionHostBridgeReturnsConfiguredDataDirectory()
    {
        const string configuredDirectory = "/tmp/nekostick-configured-data";
        using var tasks = new ExtensionTaskTracker(_ => ValueTask.CompletedTask);
        await using var events = new ExtensionEventQueue(_ => ValueTask.CompletedTask);
        using var contracts = new ExtensionContractRegistry(
            ImmutableArray<ExtensionContractExport>.Empty,
            ImmutableArray<ExtensionContractImport>.Empty,
            static (_, _) => null);

        var bridge = new ExtensionHostBridge(
            HostApiVersion.Current,
            settings: null,
            tasks,
            events,
            contracts,
            UnsupportedExtensionCapabilities.Create(),
            UnsupportedExtensionCapabilities.CreateLifecycle(),
            reportStatus: _ => { },
            reportLog: (_, _) => { },
            dataDirectory: configuredDirectory);

        Assert.Equal(configuredDirectory, bridge.DataDirectory);
    }
}
