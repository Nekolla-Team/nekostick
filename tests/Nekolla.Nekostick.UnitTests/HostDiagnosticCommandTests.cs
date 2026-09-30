using System.Diagnostics;
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
        // Run the CLI in a child process: Console.SetOut is process-global and races with
        // the in-process test framework's own console writes, polluting the captured JSON.
        var hostAssembly = Path.Combine(AppContext.BaseDirectory, "Nekolla.Nekostick.Host.dll");
        Assert.True(File.Exists(hostAssembly), $"The host assembly is missing at '{hostAssembly}'.");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(hostAssembly);
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var cancellation = TestContext.Current.CancellationToken;
        var standardOutput = await process!.StandardOutput.ReadToEndAsync(cancellation);
        var standardError = await process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
