namespace Nekolla.Nekostick.Domain;

/// <summary>Identifies a safe bootstrap parsing failure.</summary>
public enum BootstrapErrorCode
{
    /// <summary>The required PostgreSQL connection string was absent.</summary>
    MissingConnectionString,

    /// <summary>The listen address was empty or unsafe.</summary>
    InvalidListenAddress,

    /// <summary>The listen port was not an integer in the valid TCP range.</summary>
    InvalidListenPort,

    /// <summary>The node identifier was empty, too long, or unsafe.</summary>
    InvalidNodeId,

    /// <summary>The requested log level was not a supported name.</summary>
    InvalidLogLevel,


    /// <summary>An option was repeated.</summary>
    DuplicateOption,

    /// <summary>An option had no value.</summary>
    MissingOptionValue,

    /// <summary>The command or option was not supported.</summary>
    UnsupportedArgument,

    /// <summary>The argument collection was invalid.</summary>
    InvalidArguments,

    /// <summary>The EF log inclusion switch was not a supported Boolean value.</summary>
    InvalidIncludeEfLogs
}

/// <summary>Contains a bootstrap error without echoing supplied values.</summary>
public sealed record BootstrapParseError
{
    /// <summary>Creates a safe bootstrap parse error.</summary>
    /// <param name="code">The stable error category.</param>
    /// <param name="message">The fixed safe message.</param>
    public BootstrapParseError(BootstrapErrorCode code, string message)
    {
        Code = code;
        Message = string.IsNullOrWhiteSpace(message)
            ? throw new ArgumentException("A safe error message is required.", nameof(message))
            : message;
    }

    /// <summary>Gets the stable error category.</summary>
    public BootstrapErrorCode Code { get; }

    /// <summary>Gets the safe error message.</summary>
    public string Message { get; }
}

/// <summary>Contains the validated startup values required before database access.</summary>
public sealed record BootstrapOptions
{
    private BootstrapOptions(
        string connectionString,
        string listenAddress,
        int listenPort,
        string nodeId,
        string minimumLevel,
        bool includeEfLogs)
    {
        ConnectionString = connectionString;
        ListenAddress = listenAddress;
        ListenPort = listenPort;
        NodeId = nodeId;
        MinimumLevel = minimumLevel;
        IncludeEfLogs = includeEfLogs;
    }

    /// <summary>Gets the PostgreSQL connection string. Callers must treat it as secret.</summary>
    public string ConnectionString { get; }

    /// <summary>Gets the validated listen address.</summary>
    public string ListenAddress { get; }

    /// <summary>Gets the validated listen port.</summary>
    public int ListenPort { get; }

    /// <summary>Gets the stable node identifier.</summary>
    public string NodeId { get; }

    /// <summary>Gets the canonical minimum log level name accepted by the .NET LogLevel enum.</summary>
    public string MinimumLevel { get; }
    /// <summary>Gets whether Entity Framework Core logs are included at the configured framework level.</summary>
    public bool IncludeEfLogs { get; }

    internal static BootstrapOptions CreateValidated(
        string connectionString,
        string listenAddress,
        int listenPort,
        string nodeId,
        string minimumLevel,
        bool includeEfLogs) => new(
            connectionString, listenAddress, listenPort, nodeId, minimumLevel, includeEfLogs);
}

/// <summary>Registers bootstrap environment names and safe defaults.</summary>
public static class BootstrapDefaults
{
    /// <summary>The environment variable for the database connection string.</summary>
    public const string ConnectionStringEnvironmentVariable = "NEKOSTICK_CONNECTION_STRING";

    /// <summary>The environment variable for the listen address.</summary>
    public const string ListenAddressEnvironmentVariable = "NEKOSTICK_LISTEN_ADDRESS";

    /// <summary>The environment variable for the listen port.</summary>
    public const string ListenPortEnvironmentVariable = "NEKOSTICK_LISTEN_PORT";

    /// <summary>The environment variable for the node identifier.</summary>
    public const string NodeIdEnvironmentVariable = "NEKOSTICK_NODE_ID";

    /// <summary>The environment variable for the minimum log level.</summary>
    public const string LogLevelEnvironmentVariable = "NEKOSTICK_LOG_LEVEL";
    /// <summary>The environment variable controlling EF log inclusion.</summary>
    public const string IncludeEfLogsEnvironmentVariable = "NEKOSTICK_INCLUDE_EF_LOGS";

    /// <summary>The CLI option for the database connection string.</summary>
    public const string ConnectionStringOption = "--connection-string";

    /// <summary>The CLI option for the listen address.</summary>
    public const string ListenAddressOption = "--listen-address";

    /// <summary>The CLI option for the listen port.</summary>
    public const string ListenPortOption = "--listen-port";

    /// <summary>The CLI option for the node identifier.</summary>
    public const string NodeIdOption = "--node-id";

    /// <summary>The CLI option for the minimum log level.</summary>
    public const string LogLevelOption = "--log-level";
    /// <summary>The CLI switch controlling EF log inclusion.</summary>
    public const string IncludeEfLogsOption = "--include-ef-logs";

    /// <summary>The default loopback listen address.</summary>
    public const string DefaultListenAddress = "127.0.0.1";

    /// <summary>The default HTTP listen port.</summary>
    public const int DefaultListenPort = 8080;

    /// <summary>The default single-node identifier.</summary>
    public const string DefaultNodeId = "0";

    /// <summary>The default minimum log level name.</summary>
    public const string DefaultLogLevel = "Information";

    /// <summary>The maximum node identifier length in UTF-16 characters.</summary>
    public const int MaxNodeIdLength = 128;
}

/// <summary>Parses bootstrap values using CLI, environment, then default precedence.</summary>
public static class BootstrapOptionsParser
{
    /// <summary>Parses bootstrap values from command-line arguments and an explicit environment.</summary>
    /// <param name="arguments">The complete command-line argument sequence.</param>
    /// <param name="environment">The environment values to consult.</param>
    /// <returns>Validated bootstrap values or a safe error.</returns>
    public static BootstrapParseResult Parse(
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        var commandResult = CliCommandParser.Parse(arguments, environment);
        return commandResult.IsSuccess
            ? BootstrapParseResult.Success(commandResult.Command!.BootstrapOptions)
            : BootstrapParseResult.Failure(commandResult.Error!);
    }
}
