using System.Text.Json;

namespace Nekolla.Nekostick.Host;

internal static class DiagnosticJson
{
    private const string StatusSerializationFallback =
        "{\"command\":\"status\",\"configurationVersion\":null,\"databaseState\":\"unavailable\",\"configurationRevisionState\":\"not-read\",\"nodeRegistrationState\":\"not-registered\",\"extensionSummary\":{\"loaded\":0,\"failed\":0,\"state\":\"not-started\"},\"processSummary\":{\"managed\":0,\"running\":0,\"state\":\"not-started\"},\"exitCode\":1}";

    private const string DoctorSerializationFallback =
        "{\"command\":\"doctor\",\"configurationVersion\":null,\"database\":\"unavailable\",\"migration\":\"unavailable\",\"snapshotValidity\":\"unavailable\",\"extension\":\"not-checked\",\"localDirectory\":\"not-checked\",\"exitCode\":1}";

    private const string GenericSerializationFallback =
        "{\"command\":\"diagnostic\",\"exitCode\":1}";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    internal static string Serialize<T>(T report) =>
        TrySerialize(report, out var json)
            ? json
            : GetFallback(report);

    internal static int Write(StatusReport report)
    {
        if (TrySerialize(report, out var json))
        {
            Console.Out.WriteLine(json);
            return report.ExitCode;
        }

        Console.Out.WriteLine(StatusSerializationFallback);
        return 1;
    }

    internal static int Write(DoctorReport report)
    {
        if (TrySerialize(report, out var json))
        {
            Console.Out.WriteLine(json);
            return report.ExitCode;
        }

        Console.Out.WriteLine(DoctorSerializationFallback);
        return 1;
    }

    private static bool TrySerialize<T>(T report, out string json)
    {
        try
        {
            json = JsonSerializer.Serialize(report, Options);
            return true;
        }
        catch (Exception)
        {
            json = string.Empty;
            return false;
        }
    }

    private static string GetFallback<T>(T report) => report switch
    {
        StatusReport => StatusSerializationFallback,
        DoctorReport => DoctorSerializationFallback,
        _ => GenericSerializationFallback
    };
}

internal sealed record StatusReport
{
    internal StatusReport(
        long? configurationVersion,
        string databaseState,
        string configurationRevisionState,
        int exitCode)
    {
        ConfigurationVersion = configurationVersion;
        DatabaseState = databaseState;
        ConfigurationRevisionState = configurationRevisionState;
        ExitCode = exitCode;
    }

    public string Command { get; } = "status";
    public long? ConfigurationVersion { get; }
    public string DatabaseState { get; }
    public string ConfigurationRevisionState { get; }
    public string NodeRegistrationState { get; } = "not-registered";
    public ExtensionSummary ExtensionSummary { get; } = new();
    public ProcessSummary ProcessSummary { get; } = new();
    public int ExitCode { get; }
}

internal sealed record ExtensionSummary
{
    public int Loaded { get; }
    public int Failed { get; }
    public string State { get; } = "not-started";
}

internal sealed record ProcessSummary
{
    public int Managed { get; }
    public int Running { get; }
    public string State { get; } = "not-started";
}

internal sealed record DoctorReport
{
    internal DoctorReport(
        long? configurationVersion,
        string database,
        string migration,
        string snapshotValidity,
        int exitCode)
    {
        ConfigurationVersion = configurationVersion;
        Database = database;
        Migration = migration;
        SnapshotValidity = snapshotValidity;
        ExitCode = exitCode;
    }

    public string Command { get; } = "doctor";
    public long? ConfigurationVersion { get; }
    public string Database { get; }
    public string Migration { get; }
    public string SnapshotValidity { get; }
    public string Extension { get; } = "not-checked";
    public string LocalDirectory { get; } = "not-checked";
    public int ExitCode { get; }
}
