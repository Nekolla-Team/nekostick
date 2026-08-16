using System.Collections.Immutable;
using System.Text.Json;
using Nekolla.Nekostick.Domain;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class OraclePhaseADiagnosticsTests
{
    private const string OpaqueSecretToken =
        "oracle-phase-a-opaque-secret-7f4e9b2c";
    private const string ExceptionText =
        "System.InvalidOperationException: synthetic diagnostic failure";
    private static readonly string[] ExpectedDiagnosticPropertyNames =
        ["Checks", "Command", "ExitCode"];
    private static readonly string[] ExpectedParseErrorPropertyNames =
        ["Code", "Message"];

    [Theory]
    [InlineData(DiagnosticCheckStatus.Passed, 0)]
    [InlineData(DiagnosticCheckStatus.Failed, 1)]
    [InlineData(DiagnosticCheckStatus.Skipped, 1)]
    [InlineData(DiagnosticCheckStatus.Unknown, 1)]
    public void RedactedDiagnosticExitCodeIsZeroOnlyWhenEveryCheckPasses(
        DiagnosticCheckStatus status,
        int expectedExitCode)
    {
        var report = new RedactedDiagnostic(
            CliCommandKind.Doctor,
            ImmutableArray.Create(
                new DiagnosticCheckResult(DiagnosticCheckKind.DatabaseConnection, status)));

        Assert.Equal(expectedExitCode, report.ExitCode);
        Assert.Equal(report.ExitCode == 0, report.Checks.All(
            check => check.Status == DiagnosticCheckStatus.Passed));
    }

    [Fact]
    public void RedactedDiagnosticJsonHasFixedOrderIndependentShapeAndNoSensitiveText()
    {
        var report = new RedactedDiagnostic(
            CliCommandKind.Status,
            ImmutableArray.Create(
                new DiagnosticCheckResult(
                    DiagnosticCheckKind.ConfigurationSnapshot,
                    DiagnosticCheckStatus.Unknown),
                new DiagnosticCheckResult(
                    DiagnosticCheckKind.DatabaseConnection,
                    DiagnosticCheckStatus.Failed)));

        var json = report.ToJson();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(
            ExpectedDiagnosticPropertyNames,
            root.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal((int)CliCommandKind.Status, root.GetProperty("Command").GetInt32());
        Assert.Equal(2, root.GetProperty("Checks").GetArrayLength());
        Assert.Equal(1, root.GetProperty("ExitCode").GetInt32());
        Assert.DoesNotContain(OpaqueSecretToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain(ExceptionText, json, StringComparison.Ordinal);
    }

    [Fact]
    public void BootstrapParseErrorSerializesAsSafeFixedDataWithoutEchoingInputs()
    {
        var result = CliCommandParser.Parse(
            [
                "doctor",
                "--connection-string", OpaqueSecretToken,
                "--node-id", ExceptionText + "\t"
            ],
            new Dictionary<string, string?>());

        Assert.False(result.IsSuccess);
        Assert.Null(result.Command);
        Assert.NotNull(result.Error);

        var error = result.Error!;
        var json = JsonSerializer.Serialize(error);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            ExpectedParseErrorPropertyNames,
            document.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(BootstrapErrorCode.InvalidNodeId, error.Code);
        Assert.Equal("The node identifier is invalid.", error.Message);
        Assert.DoesNotContain(OpaqueSecretToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain(ExceptionText, json, StringComparison.Ordinal);
    }
}
