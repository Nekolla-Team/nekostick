using System.Collections.Immutable;
using System.Text.Json;
using Nekolla.Nekostick.Domain;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void EmptyDiagnosticReportIsSafeAndSuccessful()
    {
        var report = new RedactedDiagnostic(
            CliCommandKind.Status,
            default);

        Assert.Equal(CliCommandKind.Status, report.Command);
        Assert.Empty(report.Checks);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void DiagnosticExitCodeReflectsEveryCheckStatus()
    {
        var passed = new DiagnosticCheckResult(
            DiagnosticCheckKind.ConfigurationSnapshot,
            DiagnosticCheckStatus.Passed);
        var failed = new DiagnosticCheckResult(
            DiagnosticCheckKind.DatabaseConnection,
            DiagnosticCheckStatus.Failed);

        var successful = new RedactedDiagnostic(
            CliCommandKind.Doctor,
            ImmutableArray.Create(passed));
        var unsuccessful = new RedactedDiagnostic(
            CliCommandKind.Doctor,
            ImmutableArray.Create(passed, failed));

        Assert.Equal(0, successful.ExitCode);
        Assert.Equal(1, unsuccessful.ExitCode);
        Assert.Equal(DiagnosticCheckKind.DatabaseConnection, unsuccessful.Checks[1].Kind);
        Assert.Equal(DiagnosticCheckStatus.Failed, unsuccessful.Checks[1].Status);
    }

    [Fact]
    public void DiagnosticJsonContainsOnlyTheSafeReportShape()
    {
        var report = new RedactedDiagnostic(
            CliCommandKind.Doctor,
            ImmutableArray.Create(
                new DiagnosticCheckResult(
                    DiagnosticCheckKind.Migration,
                    DiagnosticCheckStatus.Unknown)));

        using var document = JsonDocument.Parse(report.ToJson());
        var propertyNames = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(3, propertyNames.Length);
        Assert.Contains("Command", propertyNames);
        Assert.Contains("Checks", propertyNames);
        Assert.Contains("ExitCode", propertyNames);
        Assert.DoesNotContain("ConnectionString", propertyNames);
        Assert.True(
            !report.ToJson().Contains("synthetic-connection-input", StringComparison.Ordinal),
            "Diagnostic JSON must not contain sensitive input.");
    }

    [Fact]
    public void DiagnosticCheckResultPreservesItsSafeEnumValues()
    {
        var result = new DiagnosticCheckResult(
            DiagnosticCheckKind.Supervisor,
            DiagnosticCheckStatus.Skipped);

        Assert.Equal(DiagnosticCheckKind.Supervisor, result.Kind);
        Assert.Equal(DiagnosticCheckStatus.Skipped, result.Status);
    }
}
