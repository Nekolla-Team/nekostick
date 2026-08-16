using System.Collections.Immutable;
using System.Text.Json;

namespace Nekolla.Nekostick.Domain;

/// <summary>Identifies a non-secret status or doctor check.</summary>
public enum DiagnosticCheckKind
{
    /// <summary>Database connectivity check.</summary>
    DatabaseConnection,

    /// <summary>Migration/schema state check.</summary>
    Migration,

    /// <summary>Complete configuration snapshot validation.</summary>
    ConfigurationSnapshot,

    /// <summary>Extension directory accessibility check.</summary>
    ExtensionDirectory,

    /// <summary>Local node registration check.</summary>
    NodeRegistration,

    /// <summary>Service supervisor state check.</summary>
    Supervisor
}

/// <summary>Identifies the safe result state of one diagnostic check.</summary>
public enum DiagnosticCheckStatus
{
    /// <summary>The check passed.</summary>
    Passed,

    /// <summary>The check failed.</summary>
    Failed,

    /// <summary>The check was intentionally not applicable.</summary>
    Skipped,

    /// <summary>The check could not produce a result.</summary>
    Unknown
}

/// <summary>Contains only enumerated, non-secret diagnostic information.</summary>
public sealed record DiagnosticCheckResult
{
    /// <summary>Creates a safe diagnostic check result.</summary>
    /// <param name="kind">The fixed check kind.</param>
    /// <param name="status">The check status.</param>
    public DiagnosticCheckResult(DiagnosticCheckKind kind, DiagnosticCheckStatus status)
    {
        Kind = kind;
        Status = status;
    }

    /// <summary>Gets the fixed check kind.</summary>
    public DiagnosticCheckKind Kind { get; }

    /// <summary>Gets the check status.</summary>
    public DiagnosticCheckStatus Status { get; }
}

/// <summary>Represents redacted status or doctor output and its process exit code.</summary>
public sealed record RedactedDiagnostic
{
    /// <summary>Creates redacted diagnostic output.</summary>
    /// <param name="command">The command that produced the report.</param>
    /// <param name="checks">The fixed-shape check results.</param>
    public RedactedDiagnostic(CliCommandKind command, ImmutableArray<DiagnosticCheckResult> checks)
    {
        Command = command;
        Checks = checks.IsDefault ? ImmutableArray<DiagnosticCheckResult>.Empty : checks;
        ExitCode = Checks.All(check => check.Status == DiagnosticCheckStatus.Passed) ? 0 : 1;
    }

    /// <summary>Gets the originating command.</summary>
    public CliCommandKind Command { get; }

    /// <summary>Gets the immutable safe check results.</summary>
    public ImmutableArray<DiagnosticCheckResult> Checks { get; }

    /// <summary>Gets the safe process exit code, either zero or one.</summary>
    public int ExitCode { get; }

    /// <summary>Serializes this fixed-shape report without any sensitive input fields.</summary>
    /// <returns>JSON containing only command, checks, and exit code.</returns>
    public string ToJson() => JsonSerializer.Serialize(this);
}

/// <summary>Provides fixed redaction for sensitive connection-string display sites.</summary>
public static class SecretRedactor
{
    /// <summary>The only value returned for a connection string display request.</summary>
    public const string RedactedValue = "[REDACTED]";

    /// <summary>Returns a fixed marker and never parses or formats the supplied connection string.</summary>
    /// <param name="connectionString">The sensitive value, which is never returned.</param>
    /// <returns>A fixed redaction marker.</returns>
    public static string RedactConnectionString(string connectionString) => RedactedValue;
}
