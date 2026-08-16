using System.Collections.Immutable;

namespace Nekolla.Nekostick.Proxy;

/// <summary>
/// Defines the pure, filesystem-facing safety boundary for one static-file route target.
/// It performs no web listening, proxy forwarding, configuration loading, or directory enumeration.
/// </summary>
public sealed partial class StaticTargetDefinition
{
    private const string DefaultIndexFileName = "index.html";

    private readonly string _rootPath;
    private readonly bool _directoryListingEnabled;
    private readonly StaticFileDeferredHandling _rangeHandling;
    private readonly StaticFileDeferredHandling _headerHandling;

    /// <summary>Creates an immutable static target definition.</summary>
    /// <param name="rootPath">An absolute POSIX root directory path.</param>
    /// <param name="indexFileNames">
    /// Optional fixed single-file names used for directory index lookup.
    /// A default-valued array selects <c>index.html</c>; an empty array disables index lookup.
    /// </param>
    public StaticTargetDefinition(
        string rootPath,
        ImmutableArray<string> indexFileNames = default)
    {
        _rootPath = NormalizeRootPath(rootPath);
        IndexFileNames = NormalizeIndexFileNames(indexFileNames);
        _directoryListingEnabled = false;
        _rangeHandling = StaticFileDeferredHandling.DeferredToHttpLayer;
        _headerHandling = StaticFileDeferredHandling.DeferredToHttpLayer;
    }

    /// <summary>Gets the configured absolute root path.</summary>
    public string RootPath => _rootPath;

    /// <summary>Gets the immutable fixed index-file name policy.</summary>
    public ImmutableArray<string> IndexFileNames { get; }

    /// <summary>Gets whether directory listing is enabled. It is always <see langword="false"/>.</summary>
    public bool DirectoryListingEnabled => _directoryListingEnabled;

    /// <summary>Gets the range-processing policy, which is intentionally deferred to HTTP.</summary>
    public StaticFileDeferredHandling RangeHandling => _rangeHandling;

    /// <summary>Gets the header and conditional-request policy, which is deferred to HTTP.</summary>
    public StaticFileDeferredHandling HeaderHandling => _headerHandling;
}
