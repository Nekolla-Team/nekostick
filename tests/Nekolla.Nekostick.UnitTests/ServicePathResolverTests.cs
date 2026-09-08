using Nekolla.Nekostick.Host;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ServicePathResolverTests
{
    [Fact]
    public void AbsolutePathsPassThroughUnchanged()
    {
        var absolute = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "opt", "svc", "app");
        Assert.Equal(absolute, ServicePathResolver.Resolve("/var/lib/nekostick", absolute));
    }

    [Fact]
    public void RelativePathsResolveAgainstTheDataDirectory()
    {
        var resolved = ServicePathResolver.Resolve("/var/lib/nekostick", "svc/bin/app");
        Assert.Equal(
            Path.GetFullPath(Path.Combine("/var/lib/nekostick", "svc/bin/app")),
            resolved);
        Assert.True(Path.IsPathRooted(resolved));
    }

    [Fact]
    public void DotSegmentsNormalizeInsideTheDataDirectory()
    {
        var resolved = ServicePathResolver.Resolve("/var/lib/nekostick", "svc/./bin/../bin/app");
        Assert.EndsWith(Path.Combine("svc", "bin", "app"), resolved);
    }

    [Theory]
    [InlineData("../outside/app")]
    [InlineData("svc/../../outside/app")]
    public void EscapingRelativePathsAreRejected(string configuredPath)
    {
        Assert.Throws<InvalidOperationException>(
            () => ServicePathResolver.Resolve("/var/lib/nekostick", configuredPath));
    }
}
