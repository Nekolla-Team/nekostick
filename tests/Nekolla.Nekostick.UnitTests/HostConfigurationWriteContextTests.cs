using Nekolla.Nekostick.Persistence;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostConfigurationWriteContextTests
{
    [Fact]
    public async Task NestedWriterIdentitiesFlowAcrossAwaitAndRestorePreviousContext()
    {
        Assert.Equal("host:config-api", HostConfigurationWriteContext.CurrentCommittedBy);

        using (HostConfigurationWriteContext.EnterExtension("extension.alpha"))
        {
            Assert.Equal("extension:extension.alpha", HostConfigurationWriteContext.CurrentCommittedBy);
            await Task.Yield();
            Assert.Equal("extension:extension.alpha", HostConfigurationWriteContext.CurrentCommittedBy);

            using (HostConfigurationWriteContext.EnterHostComponent("config-publisher"))
            {
                Assert.Equal("host:config-publisher", HostConfigurationWriteContext.CurrentCommittedBy);
            }

            Assert.Equal("extension:extension.alpha", HostConfigurationWriteContext.CurrentCommittedBy);
        }

        Assert.Equal("host:config-api", HostConfigurationWriteContext.CurrentCommittedBy);
    }
}
