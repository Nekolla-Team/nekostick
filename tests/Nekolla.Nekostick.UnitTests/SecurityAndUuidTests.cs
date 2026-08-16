using Nekolla.Nekostick.Domain;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class SecurityAndUuidTests
{
    [Fact]
    public void ConnectionStringRedactionNeverReturnsInput()
    {
        const string connectionString = "Host=synthetic;Username=unit-test;Password=redaction-marker;";

        var redacted = SecretRedactor.RedactConnectionString(connectionString);

        Assert.True(
            !redacted.Contains(connectionString, StringComparison.Ordinal),
            "The redaction output must not contain the supplied connection input.");
        Assert.True(
            !redacted.Contains("redaction-marker", StringComparison.Ordinal),
            "The redaction output must not contain a supplied secret value.");
        Assert.Equal(SecretRedactor.RedactedValue, redacted);
    }

    [Fact]
    public void SystemGeneratorProducesUuidV7Values()
    {
        var generator = new SystemUuidV7Generator();
        var value = generator.Create();

        Assert.True(UuidV7.IsVersion7(value));
    }
}
