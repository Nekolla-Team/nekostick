using Nekolla.Nekostick.Domain;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class BootstrapOptionsTests
{
    [Theory]
    [InlineData("run", CliCommandKind.Run)]
    [InlineData("status", CliCommandKind.Status)]
    [InlineData("doctor", CliCommandKind.Doctor)]
    public void SupportedCommandsAreParsed(string commandName, CliCommandKind expectedKind)
    {
        var result = CliCommandParser.Parse(
            [commandName, "--connection-string", "synthetic-connection-input"],
            new Dictionary<string, string?>());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Command);
        Assert.Equal(expectedKind, result.Command!.Kind);
        Assert.False(result.Command.RunOptions.SkipExtensions);
        Assert.False(result.Command.RunOptions.DisableSupervisor);
        Assert.False(result.Command.RunOptions.ReadOnly);
    }

    [Fact]
    public void MissingCommandDefaultsToRun()
    {
        var result = CliCommandParser.Parse(
            ["--connection-string", "synthetic-connection-input"],
            new Dictionary<string, string?>());

        Assert.True(result.IsSuccess);
        Assert.Equal(CliCommandKind.Run, result.Command!.Kind);
    }

    [Fact]
    public void CliValuesTakePrecedenceOverEnvironmentValues()
    {
        var environment = new Dictionary<string, string?>
        {
            [BootstrapDefaults.ConnectionStringEnvironmentVariable] = "environment-connection",
            [BootstrapDefaults.ListenAddressEnvironmentVariable] = "192.0.2.1",
            [BootstrapDefaults.ListenPortEnvironmentVariable] = "9000",
            [BootstrapDefaults.NodeIdEnvironmentVariable] = "env-node"
        };

        var result = BootstrapOptionsParser.Parse(
            [
                "run",
                "--connection-string", "cli-selected-connection",
                "--listen-address", "127.0.0.2",
                "--listen-port", "8081",
                "--node-id", "cli-node"
            ],
            environment);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.Equal("cli-selected-connection".Length, result.Options!.ConnectionString.Length);
        Assert.Equal("127.0.0.2", result.Options.ListenAddress);
        Assert.Equal(8081, result.Options.ListenPort);
        Assert.Equal("cli-node", result.Options.NodeId);
    }

    [Fact]
    public void EnvironmentValuesTakePrecedenceOverDefaults()
    {
        var result = BootstrapOptionsParser.Parse(
            [],
            new Dictionary<string, string?>
            {
                [BootstrapDefaults.ConnectionStringEnvironmentVariable] = "synthetic-environment-connection",
                [BootstrapDefaults.ListenAddressEnvironmentVariable] = "192.0.2.10",
                [BootstrapDefaults.ListenPortEnvironmentVariable] = "9001",
                [BootstrapDefaults.NodeIdEnvironmentVariable] = "environment-node"
            });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.Equal("synthetic-environment-connection".Length, result.Options!.ConnectionString.Length);
        Assert.Equal("192.0.2.10", result.Options.ListenAddress);
        Assert.Equal(9001, result.Options.ListenPort);
        Assert.Equal("environment-node", result.Options.NodeId);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1 ")]
    [InlineData("127.0.0.1\n")]
    [InlineData("999.0.0.1")]
    [InlineData("255.255.255.255")]
    public void InvalidCliListenAddressesAreRejectedBeforeEnvironmentFallback(string address)
    {
        var result = BootstrapOptionsParser.Parse(
            ["status", "--connection-string", "synthetic-connection-input", "--listen-address", address],
            new Dictionary<string, string?>
            {
                [BootstrapDefaults.ListenAddressEnvironmentVariable] = BootstrapDefaults.DefaultListenAddress
            });

        AssertSafeFailure(result, BootstrapErrorCode.InvalidListenAddress);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("status")]
    [InlineData("doctor")]
    public void InvalidEnvironmentListenAddressesAreRejectedForEveryCommand(string command)
    {
        var result = BootstrapOptionsParser.Parse(
            [command, "--connection-string", "synthetic-connection-input"],
            new Dictionary<string, string?>
            {
                [BootstrapDefaults.ListenAddressEnvironmentVariable] = "not-an-ip-address"
            });

        AssertSafeFailure(result, BootstrapErrorCode.InvalidListenAddress);
    }

    [Fact]
    public void ValidCliListenAddressTakesPrecedenceOverInvalidEnvironmentAddress()
    {
        var result = BootstrapOptionsParser.Parse(
            [
                "doctor",
                "--connection-string", "synthetic-connection-input",
                "--listen-address", "127.0.0.2"
            ],
            new Dictionary<string, string?>
            {
                [BootstrapDefaults.ListenAddressEnvironmentVariable] = "not-an-ip-address"
            });

        Assert.True(result.IsSuccess);
        Assert.Equal("127.0.0.2", result.Options!.ListenAddress);
    }

    [Fact]
    public void DefaultsAreUsedWhenOptionalEnvironmentValuesAreAbsent()
    {
        var result = BootstrapOptionsParser.Parse(
            ["--connection-string", "database-secret"],
            new Dictionary<string, string?>());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.Equal(BootstrapDefaults.DefaultListenAddress, result.Options!.ListenAddress);
        Assert.Equal(BootstrapDefaults.DefaultListenPort, result.Options.ListenPort);
        Assert.Equal(BootstrapDefaults.DefaultNodeId, result.Options.NodeId);
    }

    [Fact]
    public void ConnectionStringIsRequired()
    {
        var result = BootstrapOptionsParser.Parse(
            Array.Empty<string>(),
            new Dictionary<string, string?>());

        Assert.False(result.IsSuccess);
        Assert.Equal(BootstrapErrorCode.MissingConnectionString, result.Error!.Code);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-port")]
    public void InvalidPortsAreRejected(string port)
    {
        var result = BootstrapOptionsParser.Parse(
            ["--connection-string", "database-secret", "--listen-port", port],
            new Dictionary<string, string?>());

        Assert.False(result.IsSuccess);
        Assert.Equal(BootstrapErrorCode.InvalidListenPort, result.Error!.Code);
    }

    [Fact]
    public void EmptyAndOverlongNodeIdsAreRejected()
    {
        var empty = BootstrapOptionsParser.Parse(
            ["--connection-string", "database-secret", "--node-id", " "],
            new Dictionary<string, string?>());
        var overlong = BootstrapOptionsParser.Parse(
            ["--connection-string", "database-secret", "--node-id", new string('n', 129)],
            new Dictionary<string, string?>());

        Assert.False(empty.IsSuccess);
        Assert.Equal(BootstrapErrorCode.InvalidNodeId, empty.Error!.Code);
        Assert.False(overlong.IsSuccess);
        Assert.Equal(BootstrapErrorCode.InvalidNodeId, overlong.Error!.Code);
    }

    [Fact]
    public void RunSafetySwitchesAreInvocationOnly()
    {
        var result = CliCommandParser.Parse(
            [
                "--connection-string", "database-secret",
                "--skip-extensions",
                "--disable-supervisor",
                "--read-only"
            ],
            new Dictionary<string, string?>());

        Assert.True(result.IsSuccess);
        Assert.True(result.Command!.RunOptions.SkipExtensions);
        Assert.True(result.Command.RunOptions.DisableSupervisor);
        Assert.True(result.Command.RunOptions.ReadOnly);
    }

    [Fact]
    public void ValueOptionsAcceptEqualsForm()
    {
        var result = CliCommandParser.Parse(
            [
                "run",
                "--connection-string=synthetic-connection-input",
                "--listen-address=127.0.0.2",
                "--listen-port=8081",
                "--node-id=cli-node"
            ],
            new Dictionary<string, string?>());

        Assert.True(result.IsSuccess);
        Assert.Equal("127.0.0.2", result.Command!.BootstrapOptions.ListenAddress);
        Assert.Equal(8081, result.Command.BootstrapOptions.ListenPort);
        Assert.Equal("cli-node", result.Command.BootstrapOptions.NodeId);
    }

    [Fact]
    public void NullParserInputsAreRejectedWithoutAnException()
    {
        var nullArguments = CliCommandParser.Parse(
            null!,
            new Dictionary<string, string?>());
        var nullEnvironment = CliCommandParser.Parse(
            [],
            null!);
        var nullArgument = CliCommandParser.Parse(
            new string?[] { null }!,
            new Dictionary<string, string?>());

        AssertSafeFailure(nullArguments, BootstrapErrorCode.InvalidArguments);
        AssertSafeFailure(nullEnvironment, BootstrapErrorCode.InvalidArguments);
        AssertSafeFailure(nullArgument, BootstrapErrorCode.InvalidArguments);
    }

    [Fact]
    public void UnsupportedCommandsAndOptionsAreRejected()
    {
        var unsupportedCommand = CliCommandParser.Parse(
            ["unsupported-command", "--connection-string", "synthetic-connection-input"],
            new Dictionary<string, string?>());
        var repeatedCommand = CliCommandParser.Parse(
            ["run", "status", "--connection-string", "synthetic-connection-input"],
            new Dictionary<string, string?>());
        var unsupportedOption = CliCommandParser.Parse(
            ["--connection-string", "synthetic-connection-input", "--unsupported"],
            new Dictionary<string, string?>());
        var flagWithValue = CliCommandParser.Parse(
            ["--connection-string", "synthetic-connection-input", "--read-only=true"],
            new Dictionary<string, string?>());

        AssertSafeFailure(unsupportedCommand, BootstrapErrorCode.UnsupportedArgument);
        AssertSafeFailure(repeatedCommand, BootstrapErrorCode.UnsupportedArgument);
        AssertSafeFailure(unsupportedOption, BootstrapErrorCode.UnsupportedArgument);
        AssertSafeFailure(flagWithValue, BootstrapErrorCode.UnsupportedArgument);
    }

    [Fact]
    public void DuplicateAndMissingOptionsAreRejected()
    {
        var duplicateValue = CliCommandParser.Parse(
            [
                "--connection-string", "synthetic-connection-input",
                "--listen-port", "8080",
                "--listen-port", "8081"
            ],
            new Dictionary<string, string?>());
        var duplicateFlag = CliCommandParser.Parse(
            [
                "--connection-string", "synthetic-connection-input",
                "--read-only", "--read-only"
            ],
            new Dictionary<string, string?>());
        var missingAtEnd = CliCommandParser.Parse(
            ["--connection-string", "synthetic-connection-input", "--node-id"],
            new Dictionary<string, string?>());
        var missingBeforeFlag = CliCommandParser.Parse(
            ["--connection-string", "synthetic-connection-input", "--node-id", "--read-only"],
            new Dictionary<string, string?>());

        AssertSafeFailure(duplicateValue, BootstrapErrorCode.DuplicateOption);
        AssertSafeFailure(duplicateFlag, BootstrapErrorCode.DuplicateOption);
        AssertSafeFailure(missingAtEnd, BootstrapErrorCode.MissingOptionValue);
        AssertSafeFailure(missingBeforeFlag, BootstrapErrorCode.MissingOptionValue);
    }

    [Fact]
    public void InvalidInputIsRejectedWithoutEchoingTheSuppliedValue()
    {
        const string unsafeInput = "synthetic-input-that-must-not-be-echoed";
        var blankConnection = BootstrapOptionsParser.Parse(
            ["--connection-string="],
            new Dictionary<string, string?>());
        var unsafeAddress = BootstrapOptionsParser.Parse(
            ["--connection-string", unsafeInput, "--listen-address", "127.0.0.1\n"],
            new Dictionary<string, string?>());
        var unsafeNodeId = BootstrapOptionsParser.Parse(
            ["--connection-string", unsafeInput, "--node-id", "node\tvalue"],
            new Dictionary<string, string?>());

        AssertSafeFailure(blankConnection, BootstrapErrorCode.MissingConnectionString);
        AssertSafeFailure(unsafeAddress, BootstrapErrorCode.InvalidListenAddress, unsafeInput);
        AssertSafeFailure(unsafeNodeId, BootstrapErrorCode.InvalidNodeId, unsafeInput);
    }

    private static void AssertSafeFailure(
        CliParseResult result,
        BootstrapErrorCode expectedCode,
        string? forbiddenValue = null)
    {
        Assert.False(result.IsSuccess);
        Assert.Null(result.Command);
        Assert.NotNull(result.Error);
        Assert.Equal(expectedCode, result.Error!.Code);

        if (forbiddenValue is not null)
        {
            Assert.True(
                !result.Error.Message.Contains(forbiddenValue, StringComparison.Ordinal),
                "The parse error must not echo supplied input.");
        }
    }

    private static void AssertSafeFailure(
        BootstrapParseResult result,
        BootstrapErrorCode expectedCode,
        string? forbiddenValue = null)
    {
        Assert.False(result.IsSuccess);
        Assert.Null(result.Options);
        Assert.NotNull(result.Error);
        Assert.Equal(expectedCode, result.Error!.Code);

        if (forbiddenValue is not null)
        {
            Assert.True(
                !result.Error.Message.Contains(forbiddenValue, StringComparison.Ordinal),
                "The parse error must not echo supplied input.");
        }
    }
}
