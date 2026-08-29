using System.Net;

namespace Nekolla.Nekostick.Domain;

/// <summary>Identifies one supported local CLI command.</summary>
public enum CliCommandKind
{
    /// <summary>Starts the node using bootstrap configuration.</summary>
    Run,

    /// <summary>Reads non-secret node and configuration status.</summary>
    Status,

    /// <summary>Runs non-secret startup and schema diagnostics.</summary>
    Doctor
}

/// <summary>Contains startup-only safety switches.</summary>
public sealed record RunOptions
{
    /// <summary>Creates startup switches.</summary>
    /// <param name="skipExtensions">Whether extensions must not be loaded.</param>
    /// <param name="disableSupervisor">Whether child-process supervision is disabled.</param>
    /// <param name="readOnly">Whether host configuration writes are disabled.</param>
    public RunOptions(bool skipExtensions, bool disableSupervisor, bool readOnly)
    {
        SkipExtensions = skipExtensions;
        DisableSupervisor = disableSupervisor;
        ReadOnly = readOnly;
    }

    /// <summary>Gets whether extension loading is disabled for this invocation.</summary>
    public bool SkipExtensions { get; }

    /// <summary>Gets whether service supervision is disabled for this invocation.</summary>
    public bool DisableSupervisor { get; }

    /// <summary>Gets whether configuration writes are disabled for this invocation.</summary>
    public bool ReadOnly { get; }
}

/// <summary>Represents a fully parsed local CLI command.</summary>
public sealed record CliCommand
{
    /// <summary>Creates a parsed CLI command.</summary>
    /// <param name="kind">The selected command.</param>
    /// <param name="bootstrapOptions">The validated bootstrap options.</param>
    /// <param name="runOptions">The invocation safety switches.</param>
    public CliCommand(CliCommandKind kind, BootstrapOptions bootstrapOptions, RunOptions runOptions)
    {
        Kind = kind;
        BootstrapOptions = bootstrapOptions ?? throw new ArgumentNullException(nameof(bootstrapOptions));
        RunOptions = runOptions ?? throw new ArgumentNullException(nameof(runOptions));
    }

    /// <summary>Gets the selected command.</summary>
    public CliCommandKind Kind { get; }

    /// <summary>Gets the validated bootstrap options.</summary>
    public BootstrapOptions BootstrapOptions { get; }

    /// <summary>Gets the invocation-only safety switches.</summary>
    public RunOptions RunOptions { get; }
}

/// <summary>Contains a parsed CLI command or a safe parse error.</summary>
public sealed class CliParseResult
{
    private CliParseResult(CliCommand command)
    {
        Command = command;
        IsSuccess = true;
    }

    private CliParseResult(BootstrapParseError error)
    {
        Error = error;
    }

    /// <summary>Gets whether parsing succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets the parsed command when successful.</summary>
    public CliCommand? Command { get; }

    /// <summary>Gets the safe error when parsing failed.</summary>
    public BootstrapParseError? Error { get; }

    internal static CliParseResult Success(CliCommand command) => new(command);

    internal static CliParseResult Failure(BootstrapParseError error) => new(error);
}

/// <summary>Contains validated bootstrap values or a safe parse error.</summary>
public sealed class BootstrapParseResult
{
    private BootstrapParseResult(BootstrapOptions options)
    {
        IsSuccess = true;
        Options = options;
    }

    private BootstrapParseResult(BootstrapParseError error) => Error = error;

    /// <summary>Gets whether parsing succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets validated bootstrap options when successful.</summary>
    public BootstrapOptions? Options { get; }

    /// <summary>Gets a safe parse error when parsing failed.</summary>
    public BootstrapParseError? Error { get; }

    internal static BootstrapParseResult Success(BootstrapOptions options) => new(options);

    internal static BootstrapParseResult Failure(BootstrapParseError error) => new(error);
}

/// <summary>Parses the supported command and bootstrap switches without echoing arguments.</summary>
public static class CliCommandParser
{
    private static readonly string[] ValueOptions =
    [
        BootstrapDefaults.ConnectionStringOption,
        BootstrapDefaults.ListenAddressOption,
        BootstrapDefaults.ListenPortOption,
        BootstrapDefaults.NodeIdOption,
        BootstrapDefaults.LogLevelOption,
        BootstrapDefaults.DataDirectoryOption
    ];

    /// <summary>Parses arguments against an explicit environment map.</summary>
    /// <param name="arguments">The command-line arguments.</param>
    /// <param name="environment">The environment values.</param>
    /// <returns>A parsed command or safe error.</returns>
    public static CliParseResult Parse(
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        if (arguments is null || environment is null)
        {
            return CliParseResult.Failure(new BootstrapParseError(
                BootstrapErrorCode.InvalidArguments,
                "The command arguments are invalid."));
        }

        var values = arguments.ToArray();

        var optionValues = new Dictionary<string, string?>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var command = CliCommandKind.Run;
        var commandSeen = false;

        for (var index = 0; index < values.Length; index++)
        {
            var argument = values[index];
            if (argument is null)
            {
                return Failure(BootstrapErrorCode.InvalidArguments, "The command arguments are invalid.");
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (commandSeen || !TryParseCommand(argument, out command))
                {
                    return Failure(BootstrapErrorCode.UnsupportedArgument, "The command is unsupported.");
                }

                commandSeen = true;
                continue;
            }

            var equalsIndex = argument.IndexOf('=');
            var optionName = equalsIndex < 0 ? argument : argument[..equalsIndex];
            if (Array.IndexOf(ValueOptions, optionName) >= 0)
            {
                if (optionValues.ContainsKey(optionName))
                {
                    return Failure(BootstrapErrorCode.DuplicateOption, "A command option was repeated.");
                }

                string? optionValue;
                if (equalsIndex >= 0)
                {
                    optionValue = argument[(equalsIndex + 1)..];
                }
                else if (index + 1 >= values.Length || values[index + 1] is null ||
                    values[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return Failure(BootstrapErrorCode.MissingOptionValue, "A command option value is missing.");
                }
                else
                {
                    optionValue = values[++index];
                }

                optionValues.Add(optionName, optionValue);
                continue;
            }

            if (equalsIndex >= 0 || !IsFlag(optionName))
            {
                return Failure(BootstrapErrorCode.UnsupportedArgument, "A command option is unsupported.");
            }

            if (!flags.Add(optionName))
            {
                return Failure(BootstrapErrorCode.DuplicateOption, "A command option was repeated.");
            }
        }

        var connectionString = Resolve(optionValues, environment, BootstrapDefaults.ConnectionStringOption,
            BootstrapDefaults.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return Failure(BootstrapErrorCode.MissingConnectionString,
                "A PostgreSQL connection string is required.");
        }

        var listenAddress = Resolve(optionValues, environment, BootstrapDefaults.ListenAddressOption,
            BootstrapDefaults.ListenAddressEnvironmentVariable) ?? BootstrapDefaults.DefaultListenAddress;
        if (!IsValidListenAddress(listenAddress))
        {
            return Failure(BootstrapErrorCode.InvalidListenAddress, "The listen address is invalid.");
        }

        var listenPortText = Resolve(optionValues, environment, BootstrapDefaults.ListenPortOption,
            BootstrapDefaults.ListenPortEnvironmentVariable) ??
            BootstrapDefaults.DefaultListenPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!int.TryParse(listenPortText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var listenPort) ||
            listenPort is < 1 or > 65535)
        {
            return Failure(BootstrapErrorCode.InvalidListenPort, "The listen port is invalid.");
        }

        var nodeId = Resolve(optionValues, environment, BootstrapDefaults.NodeIdOption,
            BootstrapDefaults.NodeIdEnvironmentVariable) ?? BootstrapDefaults.DefaultNodeId;
        if (!IsValidNodeId(nodeId))
        {
            return Failure(BootstrapErrorCode.InvalidNodeId, "The node identifier is invalid.");
        }

        var logLevel = Resolve(optionValues, environment, BootstrapDefaults.LogLevelOption,
            BootstrapDefaults.LogLevelEnvironmentVariable) ?? BootstrapDefaults.DefaultLogLevel;
        if (!TryNormalizeLogLevel(logLevel, out var normalizedLogLevel))
        {
            return Failure(BootstrapErrorCode.InvalidLogLevel, "The log level is invalid.");
        }

        var dataDirectoryText = Resolve(
            optionValues,
            environment,
            BootstrapDefaults.DataDirectoryOption,
            BootstrapDefaults.DataDirectoryEnvironmentVariable) ?? BootstrapDefaults.DefaultDataDirectory;
        if (!TryNormalizeDirectoryPath(dataDirectoryText, out var dataDirectory))
        {
            return Failure(BootstrapErrorCode.InvalidDataDirectory, "The data directory is invalid.");
        }

        var includeEfLogs = flags.Contains(BootstrapDefaults.IncludeEfLogsOption);
        if (!includeEfLogs && !TryParseBoolean(
                environment.TryGetValue(BootstrapDefaults.IncludeEfLogsEnvironmentVariable, out var includeEfLogsValue)
                    ? includeEfLogsValue
                    : null,
                out includeEfLogs))
        {
            return Failure(BootstrapErrorCode.InvalidIncludeEfLogs, "The EF log inclusion switch is invalid.");
        }

        var options = BootstrapOptions.CreateValidated(
            connectionString,
            listenAddress,
            listenPort,
            nodeId,
            normalizedLogLevel,
            includeEfLogs,
            dataDirectory);
        var runOptions = new RunOptions(
            flags.Contains("--skip-extensions"),
            flags.Contains("--disable-supervisor"),
            flags.Contains("--read-only"));
        return CliParseResult.Success(new CliCommand(command, options, runOptions));
    }

    private static string? Resolve(
        Dictionary<string, string?> cli,
        IReadOnlyDictionary<string, string?> environment,
        string cliName,
        string environmentName)
    {
        if (cli.TryGetValue(cliName, out var cliValue))
        {
            return cliValue;
        }

        return environment.TryGetValue(environmentName, out var environmentValue)
            ? environmentValue
            : null;
    }

    private static bool IsFlag(string name) => name is "--skip-extensions" or "--disable-supervisor" or
        "--read-only" or BootstrapDefaults.IncludeEfLogsOption;

    private static bool IsValidListenAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
        {
            return false;
        }

        return IPAddress.TryParse(value, out var address) &&
            address is not null &&
            !address.Equals(IPAddress.None);
    }

    private static bool TryParseCommand(string value, out CliCommandKind command)
    {
        command = value switch
        {
            "run" => CliCommandKind.Run,
            "status" => CliCommandKind.Status,
            "doctor" => CliCommandKind.Doctor,
            _ => default
        };
        return value is "run" or "status" or "doctor";
    }

    private static bool IsSafeText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(character => !char.IsControl(character));

    private static bool IsValidNodeId(string? value) =>
        IsSafeText(value) && value!.Length <= BootstrapDefaults.MaxNodeIdLength;

    private static bool TryNormalizeDirectoryPath(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(value);
            return Path.IsPathFullyQualified(normalized);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryNormalizeLogLevel(string? value, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = value.Trim() switch
        {
            var text when text.Equals("trace", StringComparison.OrdinalIgnoreCase) => "Trace",
            var text when text.Equals("debug", StringComparison.OrdinalIgnoreCase) => "Debug",
            var text when text.Equals("information", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("info", StringComparison.OrdinalIgnoreCase) => "Information",
            var text when text.Equals("warning", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("warn", StringComparison.OrdinalIgnoreCase) => "Warning",
            var text when text.Equals("error", StringComparison.OrdinalIgnoreCase) => "Error",
            var text when text.Equals("critical", StringComparison.OrdinalIgnoreCase) => "Critical",
            var text when text.Equals("none", StringComparison.OrdinalIgnoreCase) => "None",
            _ => string.Empty
        };
        return normalized.Length > 0;
    }
    private static bool TryParseBoolean(string? value, out bool result)
    {
        if (value is null)
        {
            result = false;
            return true;
        }

        return bool.TryParse(value.Trim(), out result);
    }


    private static CliParseResult Failure(BootstrapErrorCode code, string message) =>
        CliParseResult.Failure(new BootstrapParseError(code, message));
}
