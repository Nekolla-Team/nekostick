using Nekolla.Nekostick.Proxy;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class StaticFileOperationFactoryTests
{
    [Fact]
    public void RepeatedFactoryCallsReturnTheCachedOperation()
    {
        var first = StaticFileOperationFactory.Create();
        var second = StaticFileOperationFactory.Create();

        Assert.Same(first, second);
    }
}
