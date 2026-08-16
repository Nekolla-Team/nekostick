using System.Collections.Immutable;
using System.Globalization;
using Nekolla.Nekostick.Domain;

namespace Nekolla.Nekostick.Supervision;

/// <summary>Identifies a safe process-launch validation failure.</summary>
public enum ProcessLaunchValidationCode
{
    /// <summary>The executable path is not an absolute POSIX path.</summary>
    InvalidExecutablePath,

    /// <summary>The working directory is not an absolute POSIX path.</summary>
    InvalidWorkingDirectory,

    /// <summary>The argument collection or one argument is outside its bound.</summary>
    InvalidArguments,

    /// <summary>The environment key or value is outside its bound.</summary>
    InvalidEnvironment,

    /// <summary>The environment entry count is outside its bound.</summary>
    EnvironmentLimitExceeded,

    /// <summary>The service identifier is missing.</summary>
    InvalidServiceIdentifier,

    /// <summary>The service port is outside the TCP port range.</summary>
    InvalidPort
}

/// <summary>Defines bounded process-launch input limits.</summary>
public sealed record ProcessLaunchLimits
{
    /// <summary>Creates process-launch limits.</summary>
    /// <param name="maximumArguments">The maximum argument count.</param>
    /// <param name="maximumArgumentLength">The maximum UTF-16 argument length.</param>
    /// <param name="maximumEnvironmentEntries">The maximum environment entry count.</param>
    /// <param name="maximumEnvironmentKeyLength">The maximum environment key length.</param>
    /// <param name="maximumEnvironmentValueLength">The maximum environment value length.</param>
    public ProcessLaunchLimits(
        int maximumArguments,
        int maximumArgumentLength,
        int maximumEnvironmentEntries,
        int maximumEnvironmentKeyLength,
        int maximumEnvironmentValueLength)
    {
        if (maximumArguments < 0 || maximumArgumentLength < 1 || maximumEnvironmentEntries < 0 ||
            maximumEnvironmentKeyLength < 1 || maximumEnvironmentValueLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumArguments));
        }

        MaximumArguments = maximumArguments;
        MaximumArgumentLength = maximumArgumentLength;
        MaximumEnvironmentEntries = maximumEnvironmentEntries;
        MaximumEnvironmentKeyLength = maximumEnvironmentKeyLength;
        MaximumEnvironmentValueLength = maximumEnvironmentValueLength;
    }

    /// <summary>Gets the maximum argument count.</summary>
    public int MaximumArguments { get; }

    /// <summary>Gets the maximum argument length.</summary>
    public int MaximumArgumentLength { get; }

    /// <summary>Gets the maximum environment entry count.</summary>
    public int MaximumEnvironmentEntries { get; }

    /// <summary>Gets the maximum environment key length.</summary>
    public int MaximumEnvironmentKeyLength { get; }

    /// <summary>Gets the maximum environment value length.</summary>
    public int MaximumEnvironmentValueLength { get; }

    /// <summary>Gets conservative default process-launch bounds.</summary>
    public static ProcessLaunchLimits Default => new(256, 32 * 1024, 128, 256, 16 * 1024);
}

/// <summary>Stores environment overrides without exposing enumeration or secret values.</summary>
public sealed class ProcessEnvironment
{
    private readonly ImmutableDictionary<string, string> values;

    /// <summary>Creates a bounded, immutable environment override set.</summary>
    /// <param name="entries">The environment entries, which are never formatted or logged.</param>
    /// <param name="limits">The bounds to enforce.</param>
    public ProcessEnvironment(
        IReadOnlyDictionary<string, string> entries,
        ProcessLaunchLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        limits ??= ProcessLaunchLimits.Default;
        if (entries.Count > limits.MaximumEnvironmentEntries)
        {
            throw new ArgumentException("The environment entry limit was exceeded.", nameof(entries));
        }

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!IsValidEnvironmentKey(entry.Key, limits.MaximumEnvironmentKeyLength) ||
                !IsValidEnvironmentValue(entry.Value, limits.MaximumEnvironmentValueLength))
            {
                throw new ArgumentException("The environment contains an invalid entry.", nameof(entries));
            }

            if (!builder.TryAdd(entry.Key, entry.Value))
            {
                throw new ArgumentException("The environment contains a duplicate key.", nameof(entries));
            }
        }

        values = builder.ToImmutable();
        Limits = limits;
    }

    /// <summary>Gets the number of environment overrides without exposing values.</summary>
    public int Count => values.Count;

    /// <summary>Gets the bounds used to validate this environment.</summary>
    public ProcessLaunchLimits Limits { get; }

    /// <summary>Determines whether an exact environment key exists without returning its value.</summary>
    /// <param name="key">The environment key.</param>
    /// <returns><see langword="true"/> when the key exists.</returns>
    public bool ContainsKey(string key) => key is not null && values.ContainsKey(key);

    /// <summary>Returns a fixed redacted marker and never formats environment values.</summary>
    /// <returns>A fixed marker.</returns>
    public override string ToString() => "[REDACTED]";

    internal ImmutableDictionary<string, string> Values => values;

    private static bool IsValidEnvironmentKey(string? key, int maximumLength) =>
        key is not null && !string.IsNullOrWhiteSpace(key) && key.Length <= maximumLength &&
        key.All(character => character != '=' && character != '\0' && !char.IsControl(character));

    private static bool IsValidEnvironmentValue(string? value, int maximumLength) =>
        value is not null && value.Length <= maximumLength &&
        value.All(character => character != '\0' && !char.IsControl(character));
}

/// <summary>Contains a validated POSIX process launch specification.</summary>
public sealed class ProcessLaunchSpecification
{
    /// <summary>Creates a process launch specification without touching the filesystem.</summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="fileName">The absolute POSIX executable path.</param>
    /// <param name="workingDirectory">The absolute POSIX working directory.</param>
    /// <param name="arguments">The immutable process arguments.</param>
    /// <param name="environment">The bounded environment overrides.</param>
    /// <param name="limits">The launch bounds.</param>
    public ProcessLaunchSpecification(
        Guid serviceId,
        string fileName,
        string workingDirectory,
        ImmutableArray<string> arguments,
        ProcessEnvironment environment,
        ProcessLaunchLimits? limits = null)
    {
        limits ??= ProcessLaunchLimits.Default;
        if (serviceId == Guid.Empty)
        {
            throw new ArgumentException("A service identifier is required.", nameof(serviceId));
        }

        ValidatePosixAbsolutePath(fileName, ProcessLaunchValidationCode.InvalidExecutablePath, nameof(fileName));
        ValidatePosixAbsolutePath(
            workingDirectory,
            ProcessLaunchValidationCode.InvalidWorkingDirectory,
            nameof(workingDirectory));
        ArgumentNullException.ThrowIfNull(environment);
        ValidateArguments(arguments, limits);
        if (environment.Limits.MaximumEnvironmentEntries > limits.MaximumEnvironmentEntries ||
            environment.Count > limits.MaximumEnvironmentEntries)
        {
            throw new ArgumentException("The environment entry limit was exceeded.", nameof(environment));
        }

        ServiceId = serviceId;
        FileName = fileName;
        WorkingDirectory = workingDirectory;
        Arguments = arguments.IsDefault ? ImmutableArray<string>.Empty : arguments;
        Environment = environment;
        Limits = limits;
    }

    /// <summary>Gets the service identifier.</summary>
    public Guid ServiceId { get; }

    /// <summary>Gets the absolute POSIX executable path.</summary>
    public string FileName { get; }

    /// <summary>Gets the absolute POSIX working directory.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Gets immutable process arguments.</summary>
    public ImmutableArray<string> Arguments { get; }

    /// <summary>Gets opaque environment overrides whose values cannot be enumerated through this type.</summary>
    public ProcessEnvironment Environment { get; }

    /// <summary>Gets the launch bounds.</summary>
    public ProcessLaunchLimits Limits { get; }

    /// <summary>Returns a fixed safe marker and never formats command or environment data.</summary>
    /// <returns>A fixed marker.</returns>
    public override string ToString() => "[PROCESS_LAUNCH_SPECIFICATION]";

    /// <summary>Builds a POSIX launch specification from a domain service and assigned loopback port.</summary>
    /// <param name="service">The immutable domain service definition.</param>
    /// <param name="port">The assigned loopback port.</param>
    /// <param name="address">The permitted loopback address family.</param>
    /// <param name="limits">The launch bounds.</param>
    /// <returns>A validated launch specification.</returns>
    public static ProcessLaunchSpecification FromServiceDefinition(
        ServiceDefinition service,
        int port,
        LoopbackAddressKind address,
        ProcessLaunchLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var host = address switch
        {
            LoopbackAddressKind.IPv4 => "127.0.0.1",
            LoopbackAddressKind.IPv6 => "::1",
            _ => throw new ArgumentOutOfRangeException(nameof(address))
        };
        var portText = port.ToString(CultureInfo.InvariantCulture);
        var arguments = service.Arguments.IsDefault
            ? ImmutableArray<string>.Empty
            : service.Arguments.Select(argument => argument.Replace("$PORT", portText, StringComparison.Ordinal)).ToImmutableArray();
        var environment = new Dictionary<string, string>(service.Environment, StringComparer.Ordinal)
        {
            ["PORT"] = portText,
            ["HOST"] = host
        };
        return new ProcessLaunchSpecification(
            service.Id,
            service.FileName,
            service.WorkingDirectory,
            arguments,
            new ProcessEnvironment(environment, limits),
            limits);
    }

    private static void ValidateArguments(ImmutableArray<string> arguments, ProcessLaunchLimits limits)
    {
        if (!arguments.IsDefault && arguments.Length > limits.MaximumArguments)
        {
            throw new ArgumentException("The argument limit was exceeded.", nameof(arguments));
        }

        foreach (var argument in arguments)
        {
            if (argument is null || argument.Length > limits.MaximumArgumentLength ||
                argument.Any(character => character == '\0' || char.IsControl(character)))
            {
                throw new ArgumentException("The arguments contain an invalid value.", nameof(arguments));
            }
        }
    }

    private static void ValidatePosixAbsolutePath(
        string? path,
        ProcessLaunchValidationCode code,
        string parameterName)
    {
        if (path is null || string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') ||
            !Path.IsPathRooted(path) || path.Contains('\\') ||
            path.Any(character => character == '\0' || char.IsControl(character)) ||
            path.Length > 4096)
        {
            throw new ArgumentException($"The POSIX path is invalid ({code}).", parameterName);
        }
    }
}
