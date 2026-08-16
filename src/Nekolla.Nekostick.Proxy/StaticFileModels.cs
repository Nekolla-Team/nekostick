using System.IO;

namespace Nekolla.Nekostick.Proxy;

/// <summary>Describes the outcome of static-file path resolution.</summary>
public enum StaticFileResolutionKind
{
    /// <summary>The request resolved to a regular file.</summary>
    FoundFile,

    /// <summary>The request resolved to a directory and a fixed index file was found.</summary>
    DirectoryIndexCandidate,

    /// <summary>No safe file or fixed index file was found.</summary>
    NotFound,

    /// <summary>The request or filesystem target was rejected by a safety boundary.</summary>
    Forbidden,

    /// <summary>The request was not a valid normalized path.</summary>
    Invalid
}

/// <summary>Describes a non-sensitive reason for an unsuccessful resolution.</summary>
public enum StaticFileFailureReason
{
    /// <summary>No failure occurred.</summary>
    None,

    /// <summary>The request path was not an absolute normalized path.</summary>
    InvalidRequestPath,

    /// <summary>The request method is outside the static-file method policy.</summary>
    UnsupportedMethod,

    /// <summary>The configured root could not be used as a directory.</summary>
    RootUnavailable,

    /// <summary>The requested target does not exist.</summary>
    TargetNotFound,

    /// <summary>No configured fixed index file exists for the directory.</summary>
    DirectoryIndexMissing,

    /// <summary>Directory listing is disabled by policy.</summary>
    DirectoryListingDisabled,

    /// <summary>A path would leave the canonical root.</summary>
    OutsideRoot,

    /// <summary>A link or filesystem target could not be safely canonicalized.</summary>
    UnsafeFilesystemTarget,

    /// <summary>The target changed or became unavailable during opening.</summary>
    TargetChanged,

    /// <summary>The target could not be accessed with the permitted operation.</summary>
    AccessDenied,

    /// <summary>The resolution belongs to another static target.</summary>
    ResolutionNotOwned
}

/// <summary>Describes the outcome of the read-only file-opening boundary.</summary>
public enum StaticFileOpenKind
{
    /// <summary>A read-only file stream was opened and revalidated.</summary>
    Opened,

    /// <summary>The file was not available at opening time.</summary>
    NotFound,

    /// <summary>The file was rejected by a safety boundary.</summary>
    Forbidden,

    /// <summary>The supplied resolution was not openable.</summary>
    Invalid
}

/// <summary>Specifies handling intentionally left to the HTTP layer.</summary>
public enum StaticFileDeferredHandling
{
    /// <summary>The proxy core does not process this concern.</summary>
    DeferredToHttpLayer
}

/// <summary>Contains an immutable, non-sensitive static-file resolution result.</summary>
public sealed class StaticFileResolution
{
    internal StaticFileResolution(
        StaticTargetDefinition owner,
        StaticFileResolutionKind kind,
        StaticFileFailureReason failureReason,
        string? lexicalPath,
        string? canonicalPath,
        string? canonicalRootPath,
        string? contentType)
    {
        Owner = owner;
        Kind = kind;
        FailureReason = failureReason;
        LexicalPath = lexicalPath;
        CanonicalPath = canonicalPath;
        CanonicalRootPath = canonicalRootPath;
        ContentType = contentType;
    }

    /// <summary>Gets the resolution kind.</summary>
    public StaticFileResolutionKind Kind { get; }

    /// <summary>Gets the non-sensitive failure reason, if resolution failed.</summary>
    public StaticFileFailureReason FailureReason { get; }

    /// <summary>Gets whether a file can be passed to the read-only opening boundary.</summary>
    public bool IsOpenable => Kind is StaticFileResolutionKind.FoundFile
        or StaticFileResolutionKind.DirectoryIndexCandidate;

    /// <summary>Gets the fixed content type selected from the built-in extension map.</summary>
    public string? ContentType { get; }

    /// <summary>Returns a non-sensitive representation that never contains a request or filesystem path.</summary>
    public override string ToString() => $"StaticFileResolution:{Kind}";

    internal StaticTargetDefinition Owner { get; }

    internal string? LexicalPath { get; }

    internal string? CanonicalPath { get; }

    internal string? CanonicalRootPath { get; }
}

/// <summary>Owns an opened read-only static file and its fixed content type.</summary>
public sealed class StaticFileReadHandle : IDisposable
{
    internal StaticFileReadHandle(FileStream stream, string contentType)
    {
        Stream = stream;
        ContentType = contentType;
    }

    /// <summary>Gets the read-only file stream.</summary>
    public FileStream Stream { get; }

    /// <summary>Gets the fixed content type selected without user configuration.</summary>
    public string ContentType { get; }

    /// <summary>Gets the length observed from the opened stream.</summary>
    public long Length => Stream.Length;

    /// <summary>Disposes the opened stream.</summary>
    public void Dispose() => Stream.Dispose();

    /// <summary>Returns a non-sensitive representation.</summary>
    public override string ToString() => "StaticFileReadHandle";
}

/// <summary>Contains the result of opening a previously safe static-file resolution.</summary>
public sealed class StaticFileOpenResult : IDisposable
{
    internal StaticFileOpenResult(
        StaticFileOpenKind kind,
        StaticFileFailureReason failureReason,
        StaticFileReadHandle? handle)
    {
        Kind = kind;
        FailureReason = failureReason;
        Handle = handle;
    }

    /// <summary>Gets the opening outcome.</summary>
    public StaticFileOpenKind Kind { get; }

    /// <summary>Gets the non-sensitive failure reason, if opening failed.</summary>
    public StaticFileFailureReason FailureReason { get; }

    /// <summary>Gets whether a revalidated read-only file was opened.</summary>
    public bool IsOpened => Kind == StaticFileOpenKind.Opened && Handle is not null;

    /// <summary>Gets the opened file handle, or <see langword="null"/> for a failed open.</summary>
    public StaticFileReadHandle? Handle { get; }

    /// <summary>Disposes the opened file, if any.</summary>
    public void Dispose() => Handle?.Dispose();

    /// <summary>Returns a non-sensitive representation that never contains a filesystem path.</summary>
    public override string ToString() => $"StaticFileOpenResult:{Kind}";
}
