using System.Text.Json;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Host;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostDiagnosticCommandTests
{
    [Theory]
    [InlineData("status")]
    [InlineData("doctor")]
    public async Task EqualsFormValueOptionDoesNotConsumeDiagnosticCommand(string commandName)
    {
        const string secret = "opaque://diagnostic-secret@example.invalid/db";
        const string malformedAddress = "127.0.0.1\n";
        string[] args =
        [
            $"--connection-string={secret}",
            commandName,
            $"--listen-address={malformedAddress}"
        ];

        await AssertDiagnosticFailureAsync(
            args,
            Enum.Parse<CliCommandKind>(commandName, ignoreCase: true),
            secret);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("doctor")]
    public async Task SeparateFormValueOptionAdvancesToDiagnosticCommand(string commandName)
    {
        const string secret = "opaque://separate-secret@example.invalid/db";
        const string malformedAddress = "127.0.0.1\n";
        string[] args =
        [
            "--connection-string",
            secret,
            commandName,
            "--listen-address",
            malformedAddress
        ];

        await AssertDiagnosticFailureAsync(
            args,
            Enum.Parse<CliCommandKind>(commandName, ignoreCase: true),
            secret);
    }

    [Fact]
    public async Task BooleanSwitchDoesNotConsumeDiagnosticCommand()
    {
        const string secret = "opaque://switch-secret@example.invalid/db";
        const string malformedAddress = "127.0.0.1\n";
        string[] args =
        [
            "--read-only",
            "doctor",
            "--connection-string",
            secret,
            "--listen-address",
            malformedAddress
        ];

        await AssertDiagnosticFailureAsync(args, CliCommandKind.Doctor, secret);
    }

    [Fact]
    public async Task RunSelectionRemainsNonDiagnosticAndSafe()
    {
        const string secret = "opaque://run-secret@example.invalid/db";
        const string malformedAddress = "127.0.0.1\n";
        string[] args =
        [
            "run",
            "--connection-string",
            secret,
            "--listen-address",
            malformedAddress
        ];

        Assert.Null(Program.SelectDiagnosticCommand(args));

        var result = await InvokeMainAsync(args);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("BOOTSTRAP", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.StandardError, StringComparison.Ordinal);
    }

    private static async Task AssertDiagnosticFailureAsync(
        string[] args,
        CliCommandKind expectedCommand,
        string secret)
    {
        var parseResult = CliCommandParser.Parse(args, new Dictionary<string, string?>());
        Assert.False(parseResult.IsSuccess);
        Assert.Equal(BootstrapErrorCode.InvalidListenAddress, parseResult.Error!.Code);

        var selectedCommand = Program.SelectDiagnosticCommand(args);
        Assert.NotNull(selectedCommand);
        Assert.Equal(expectedCommand, selectedCommand!.Value);

        var result = await InvokeMainAsync(args);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(expectedCommand.ToString().ToLowerInvariant(), document.RootElement.GetProperty("command").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.DoesNotContain(secret, result.StandardOutput, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> InvokeMainAsync(string[] args)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await Program.Main(args);
            return new ProcessResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
