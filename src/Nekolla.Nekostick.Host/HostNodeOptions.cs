namespace Nekolla.Nekostick.Host;

/// <summary>Contains immutable, invocation-local host safety switches.</summary>
public sealed record HostNodeOptions
{
    /// <summary>Creates invocation-local host safety switches.</summary>
    /// <param name="skipExtensions">Whether extension loading is disabled.</param>
    /// <param name="disableSupervisor">Whether process supervision is disabled.</param>
    /// <param name="readOnly">Whether configuration writes are disabled.</param>
    /// <param name="extensionsRootPath">
    /// Extension install root scanned for manifests; defaults to the
    /// <c>extensions</c> directory next to the host assembly.
    /// </param>
    /// <param name="dataDirectory">
    /// Host data directory exposed to extensions; defaults to the <c>data</c> directory
    /// next to the host assembly.
    /// </param>
    public HostNodeOptions(
        bool skipExtensions,
        bool disableSupervisor,
        bool readOnly,
        string? extensionsRootPath = null,
        string? dataDirectory = null)
    {
        SkipExtensions = skipExtensions;
        DisableSupervisor = disableSupervisor;
        ReadOnly = readOnly;
        ExtensionsRootPath = string.IsNullOrWhiteSpace(extensionsRootPath)
            ? Path.Combine(AppContext.BaseDirectory, "extensions")
            : extensionsRootPath;
        DataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.GetFullPath(dataDirectory);
    }

    /// <summary>Gets whether extension loading is disabled for this process.</summary>
    public bool SkipExtensions { get; }

    /// <summary>Gets whether process supervision is disabled for this process.</summary>
    public bool DisableSupervisor { get; }

    /// <summary>Gets whether configuration writes are disabled for this process.</summary>
    public bool ReadOnly { get; }
    /// <summary>Gets the extension install root scanned for manifests.</summary>
    public string ExtensionsRootPath { get; }

    /// <summary>Gets the host data directory exposed to extensions.</summary>
    public string DataDirectory { get; }
}
