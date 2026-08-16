namespace Nekolla.Nekostick.Host;

/// <summary>Contains immutable, invocation-local host safety switches.</summary>
public sealed record HostNodeOptions
{
    /// <summary>Creates invocation-local host safety switches.</summary>
    /// <param name="skipExtensions">Whether extension loading is disabled.</param>
    /// <param name="disableSupervisor">Whether process supervision is disabled.</param>
    /// <param name="readOnly">Whether configuration writes are disabled.</param>
    public HostNodeOptions(bool skipExtensions, bool disableSupervisor, bool readOnly)
    {
        SkipExtensions = skipExtensions;
        DisableSupervisor = disableSupervisor;
        ReadOnly = readOnly;
    }

    /// <summary>Gets whether extension loading is disabled for this process.</summary>
    public bool SkipExtensions { get; }

    /// <summary>Gets whether process supervision is disabled for this process.</summary>
    public bool DisableSupervisor { get; }

    /// <summary>Gets whether configuration writes are disabled for this process.</summary>
    public bool ReadOnly { get; }
}
