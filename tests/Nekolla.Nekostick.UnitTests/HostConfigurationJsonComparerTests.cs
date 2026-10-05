using Nekolla.Nekostick.Persistence;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostConfigurationJsonComparerTests
{
    [Theory]
    [InlineData("{\"z\":[{\"nested\":true},2],\"a\":\"value\"}", " { \"a\":\"value\", \"z\":[{\"nested\":true},2] } \n")]
    [InlineData("{\"value\":\"\\u0061\"}", "{\"value\":\"a\"}")]
    [InlineData("{\"value\":\"line\\n\"}", "{\"value\":\"line\\u000A\"}")]
    [InlineData("[{\"value\":1}]", "[{\"value\":1.0}]")]
    public void AreEquivalentReturnsTrueForSemanticallyEquivalentJson(string left, string right)
    {
        Assert.True(HostConfigurationJsonComparer.AreEquivalent(left, right));
    }

    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("1.0", "1e0")]
    [InlineData("1E+0", "1")]
    [InlineData("900719925474099312345678901234567890.1000", "900719925474099312345678901234567890.1")]
    [InlineData("1e400", "10e399")]
    public void AreEquivalentUsesExactNumericValue(string left, string right)
    {
        Assert.True(HostConfigurationJsonComparer.AreEquivalent(left, right));
    }

    [Theory]
    [InlineData("9007199254740992", "9007199254740993")]
    [InlineData("1e400", "1e401")]
    public void AreEquivalentDoesNotRoundDistinctNumbers(string left, string right)
    {
        Assert.False(HostConfigurationJsonComparer.AreEquivalent(left, right));
    }

    [Theory]
    [InlineData("{\"key\":1,\"key\":2}", "{\"key\":2}")]
    [InlineData("{\"key\":1,\"\\u006Bey\":2}", "{\"key\":2}")]
    [InlineData("{\"outer\":{\"key\":1,\"key\":2}}", "{\"outer\":{\"key\":2}}")]
    [InlineData("{\"key\":{\"discarded\":true},\"key\":2}", "{\"key\":2}")]
    [InlineData("{\"key\":1,\"key\":2}", "{\"key\":9,\"key\":2}")]
    public void AreEquivalentUsesLastDuplicatePropertyValue(string left, string right)
    {
        Assert.True(HostConfigurationJsonComparer.AreEquivalent(left, right));
    }

    [Theory]
    [InlineData("[1,2]", "[2,1]")]
    [InlineData("[1]", "[1,1]")]
    [InlineData("null", "{}")]
    [InlineData("{\"value\":null}", "{}")]
    [InlineData("1", "\"1\"")]
    [InlineData("true", "1")]
    [InlineData("[]", "{}")]
    [InlineData("{\"key\":1,\"key\":2}", "{\"key\":2,\"key\":1}")]
    [InlineData("\"A\"", "\"a\"")]
    public void AreEquivalentReturnsFalseForDifferentJsonbValues(string left, string right)
    {
        Assert.False(HostConfigurationJsonComparer.AreEquivalent(left, right));
    }

    [Fact]
    public void AreEquivalentHandlesDeepObjectsWithDifferentValues()
    {
        const int depth = 60;
        var prefix = string.Concat(Enumerable.Repeat("{\"value\":", depth));
        var suffix = new string('}', depth);

        Assert.False(HostConfigurationJsonComparer.AreEquivalent(
            string.Concat(prefix, "0", suffix),
            string.Concat(prefix, "1", suffix)));
    }

}
