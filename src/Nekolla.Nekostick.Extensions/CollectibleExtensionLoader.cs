using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Nekolla.Nekostick.Contracts;
using Microsoft.Extensions.Logging;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Describes the lifecycle state of a collectible extension load.</summary>
public enum ExtensionRuntimeState
{
    /// <summary>The entry assembly is loaded and validated.</summary>
    Loaded,

    /// <summary>The collectible context is being released.</summary>
    Unloading,

    /// <summary>The collectible context was confirmed released.</summary>
    Unloaded,

    /// <summary>The context was not confirmed released after the bounded check.</summary>
    UnloadNotConfirmed
}

/// <summary>Represents a safe result from collectible extension loading.</summary>
public sealed class ExtensionLoadResult
{
    private ExtensionLoadResult(
        bool succeeded,
        ExtensionFailureCode failureCode,
        ExtensionLoadHandle? handle,
        Exception? exception)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        Handle = handle;
        Exception = exception;
    }

    /// <summary>Gets whether loading succeeded.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the safe load failure category.</summary>
    public ExtensionFailureCode FailureCode { get; }

    /// <summary>Gets the loaded extension handle when successful.</summary>
    public ExtensionLoadHandle? Handle { get; }

    /// <summary>Gets the original load exception for optional debug diagnostics.</summary>
    internal Exception? Exception { get; }

    internal static ExtensionLoadResult Success(ExtensionLoadHandle handle) =>
        new(true, ExtensionFailureCode.None, handle, null);

    internal static ExtensionLoadResult Failure(
        ExtensionFailureCode code,
        Exception? exception = null) =>
        new(false, code, null, exception);
}

/// <summary>Represents the bounded result of a collectible context unload request.</summary>
public sealed class ExtensionUnloadResult
{
    private ExtensionUnloadResult(bool succeeded, ExtensionFailureCode failureCode, ExtensionRuntimeState state)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        State = state;
    }

    /// <summary>Gets whether the context was confirmed released.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the safe unload result category.</summary>
    public ExtensionFailureCode FailureCode { get; }

    /// <summary>Gets the state after the unload request.</summary>
    public ExtensionRuntimeState State { get; }

    internal static ExtensionUnloadResult Create(
        bool succeeded,
        ExtensionFailureCode code,
        ExtensionRuntimeState state) => new(succeeded, code, state);
}

/// <summary>Owns one collectible extension context and its lifecycle-safe release handle.</summary>
public sealed class ExtensionLoadHandle : IDisposable
{
    private readonly object _gate = new();
    private readonly WeakReference _weakContext;
    private readonly ExtensionManifest _manifest;
    private readonly ILogger? _logger;
    private ExtensionLoadContext? _loadContext;
    private Assembly? _entryAssembly;
    private Type? _entryType;
    private ExtensionRuntimeState _state;

    internal ExtensionLoadHandle(
        ExtensionManifest manifest,
        ExtensionLoadContext loadContext,
        Assembly entryAssembly,
        Type entryType,
        ILogger? logger = null)
    {
        _manifest = manifest;
        _loadContext = loadContext;
        _entryAssembly = entryAssembly;
        _entryType = entryType;
        _weakContext = new WeakReference(loadContext);
        _state = ExtensionRuntimeState.Loaded;
        _logger = logger;
    }

    /// <summary>Gets the manifest associated with this load.</summary>
    public ExtensionManifest Manifest => _manifest;

    /// <summary>Gets the current lifecycle state.</summary>
    public ExtensionRuntimeState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }
    internal IExtensionEntrypoint CreateEntrypoint(IExtensionHostBridge hostBridge)
    {
        ArgumentNullException.ThrowIfNull(hostBridge);
        Type entryType;
        lock (_gate)
        {
            if (_state != ExtensionRuntimeState.Loaded || _entryType is null)
            {
                throw new InvalidOperationException("The extension load is not active.");
            }

            entryType = _entryType;
        }

        var bridgeConstructor = entryType.GetConstructor(new[] { typeof(IExtensionHostBridge) });
        var entry = bridgeConstructor is not null
            ? bridgeConstructor.Invoke(new object?[] { hostBridge })
            : Activator.CreateInstance(entryType);
        return entry as IExtensionEntrypoint ??
            throw new InvalidOperationException("The extension entrypoint is incompatible.");
    }


    /// <summary>Requests unload and verifies collection for at most three GC cycles.</summary>
    /// <returns>A safe bounded unload result.</returns>
    public ExtensionUnloadResult Unload()
    {
        try
        {
            var preparation = PrepareAndRequestUnload();
            var immediateResult = preparation.ImmediateResult;
            if (immediateResult is not null)
            {
                return immediateResult;
            }

            return ConfirmUnload(preparation.WeakContext);
        }
        catch (Exception exception)
        {
            if (_logger is { } logger)
            {
                ExtensionLogMessages.ExtensionUnloadNotConfirmed(logger, exception, nameof(Unload));
            }

            lock (_gate)
            {
                _state = ExtensionRuntimeState.UnloadNotConfirmed;
            }

            return ExtensionUnloadResult.Create(
                false,
                ExtensionFailureCode.UnloadNotConfirmed,
                ExtensionRuntimeState.UnloadNotConfirmed);
        }
    }

    /// <summary>Requests unload when the handle is disposed.</summary>
    public void Dispose() => _ = Unload();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private UnloadPreparation PrepareAndRequestUnload()
    {
        ExtensionLoadContext? context;
        lock (_gate)
        {
            if (_state == ExtensionRuntimeState.Unloaded)
            {
                return UnloadPreparation.Immediate(
                    _weakContext,
                    ExtensionUnloadResult.Create(true, ExtensionFailureCode.AlreadyUnloaded, _state));
            }

            if (_state == ExtensionRuntimeState.Unloading)
            {
                return UnloadPreparation.Immediate(
                    _weakContext,
                    ExtensionUnloadResult.Create(false, ExtensionFailureCode.UnloadInProgress, _state));
            }

            _state = ExtensionRuntimeState.Unloading;
            context = _loadContext;
            var entryAssembly = _entryAssembly;
            var entryType = _entryType;
            GC.KeepAlive(entryAssembly);
            GC.KeepAlive(entryType);
            _loadContext = null;
            _entryAssembly = null;
            _entryType = null;
        }

        context?.Unload();
        return UnloadPreparation.ForConfirmation(_weakContext);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ExtensionUnloadResult ConfirmUnload(WeakReference weakContext)
    {
        for (var cycle = 0; cycle < 3; cycle++)
        {
            if (!weakContext.IsAlive)
            {
                lock (_gate)
                {
                    _state = ExtensionRuntimeState.Unloaded;
                }

                return ExtensionUnloadResult.Create(
                    true,
                    ExtensionFailureCode.None,
                    ExtensionRuntimeState.Unloaded);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        lock (_gate)
        {
            _state = ExtensionRuntimeState.UnloadNotConfirmed;
        }

        return ExtensionUnloadResult.Create(
            false,
            ExtensionFailureCode.UnloadNotConfirmed,
            ExtensionRuntimeState.UnloadNotConfirmed);
    }

    private readonly struct UnloadPreparation
    {
        private UnloadPreparation(
            WeakReference weakContext,
            ExtensionUnloadResult? immediateResult)
        {
            WeakContext = weakContext;
            ImmediateResult = immediateResult;
        }

        internal WeakReference WeakContext { get; }

        internal ExtensionUnloadResult? ImmediateResult { get; }

        internal static UnloadPreparation ForConfirmation(
            WeakReference weakContext) => new(weakContext, null);

        internal static UnloadPreparation Immediate(
            WeakReference weakContext,
            ExtensionUnloadResult result) => new(weakContext, result);
    }
}

/// <summary>Loads one previously discovered manifest in a collectible context.</summary>
public sealed class CollectibleExtensionLoader
{
    private readonly SemVersion _hostApiVersion;
    private readonly ExtensionContractCatalog _contractCatalog;
    private readonly ILogger? _logger;

    /// <summary>Creates a loader for one host API version and approved contract catalog.</summary>
    /// <param name="hostApiVersion">The host API version used for compatibility validation.</param>
    /// <param name="contractCatalog">The host-owned shared contract catalog.</param>
    /// <param name="logger">The optional host logger for load and unload diagnostics.</param>
    public CollectibleExtensionLoader(
        SemVersion hostApiVersion,
        ExtensionContractCatalog? contractCatalog = null,
        ILogger? logger = null)
    {
        _hostApiVersion = hostApiVersion;
        _contractCatalog = contractCatalog ?? ExtensionContractCatalog.CreateDefault();
        _logger = logger;
    }

    /// <summary>Loads an entry assembly from the manifest's approved extension root.</summary>
    /// <param name="manifest">The manifest returned by explicit discovery.</param>
    /// <param name="contentHash">The optional recorded content digest used to key the per-content shadow load path.</param>
    /// <returns>A safe result with no raw exception or path data.</returns>
    public ExtensionLoadResult Load(ExtensionManifest? manifest, string? contentHash = null)
    {
        if (manifest is null)
        {
            return ExtensionLoadResult.Failure(ExtensionFailureCode.InvalidArgument);
        }

        if (!manifest.RequiredHostApiVersion.IsSatisfiedBy(_hostApiVersion))
        {
            return ExtensionLoadResult.Failure(ExtensionFailureCode.HostApiIncompatible);
        }

        if (!CanonicalPath.TryCanonicalDirectory(manifest.ExtensionDirectory, out var root) ||
            !CanonicalPath.IsWithin(root, manifest.EntryAssemblyPath) ||
            !CanonicalPath.TryCanonicalFileInRoot(root, manifest.EntryAssemblyPath, out var entryPath))
        {
            return ExtensionLoadResult.Failure(ExtensionFailureCode.UnsafePath);
        }
        foreach (var export in manifest.Exports)
        {
            if (_contractCatalog.ValidateDeclaration(
                    manifest.ExtensionDirectory,
                    export.AssemblyIdentity,
                    export.TypeIdentity) != ExtensionFailureCode.None)
            {
                return ExtensionLoadResult.Failure(ExtensionFailureCode.ContractCatalogUnavailable);
            }
        }

        foreach (var import in manifest.Imports)
        {
            if (_contractCatalog.ValidateDeclaration(
                    manifest.ExtensionDirectory,
                    import.AssemblyIdentity,
                    import.TypeIdentity) != ExtensionFailureCode.None)
            {
                return ExtensionLoadResult.Failure(ExtensionFailureCode.ContractCatalogUnavailable);
            }
        }

        ExtensionLoadContext? loadContext = null;
        try
        {
            var shadowRoot = ExtensionAssemblyShadowLink.TryCreate(
                manifest.Id,
                contentHash,
                root,
                entryPath,
                _logger);
            var loadEntryPath = shadowRoot is null
                ? entryPath
                : Path.Combine(shadowRoot, Path.GetRelativePath(root, entryPath));
            loadContext = new ExtensionLoadContext(loadEntryPath, root, _contractCatalog, shadowRoot);
            var entryAssembly = loadContext.LoadFromAssemblyPath(loadEntryPath);
            var entryType = entryAssembly.GetType(manifest.EntryType, throwOnError: false, ignoreCase: false);
            if (entryType is null)
            {
                loadContext.Unload();
                return ExtensionLoadResult.Failure(ExtensionFailureCode.EntryTypeMissing);
            }

            if (!typeof(IExtensionEntrypoint).IsAssignableFrom(entryType) ||
                !entryType.IsClass || entryType.IsAbstract)
            {
                loadContext.Unload();
                return ExtensionLoadResult.Failure(ExtensionFailureCode.EntryTypeNotCompatible);
            }

            var handle = new ExtensionLoadHandle(manifest, loadContext, entryAssembly, entryType, _logger);
            loadContext = null;
            return ExtensionLoadResult.Success(handle);
        }
        catch (ContractsIdentityException exception)
        {
            loadContext?.Unload();
            return ExtensionLoadResult.Failure(ExtensionFailureCode.ContractsIdentityMismatch, exception);
        }
        catch (Exception exception)
        {
            loadContext?.Unload();
            return ExtensionLoadResult.Failure(ExtensionFailureCode.LoadFailed, exception);
        }
    }
}

internal sealed class ExtensionLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _root;
    private readonly string? _shadowRoot;
    private readonly ExtensionContractCatalog _contractCatalog;
    private readonly Assembly _contractsAssembly = typeof(HostApiVersion).Assembly;
    private readonly AssemblyName _contractsIdentity = typeof(HostApiVersion).Assembly.GetName();

    internal ExtensionLoadContext(
        string entryAssemblyPath,
        string root,
        ExtensionContractCatalog contractCatalog,
        string? shadowRoot = null)
        : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
        _root = root;
        _contractCatalog = contractCatalog;
        _shadowRoot = shadowRoot;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (_contractCatalog.TryResolveAssembly(
                assemblyName,
                _root,
                out var approvedPath,
                out var approvedAssembly))
        {
            if (approvedAssembly is not null)
            {
                return AssemblyIdentityMatches(assemblyName, approvedAssembly.GetName())
                    ? approvedAssembly
                    : throw new ContractsIdentityException();
            }

            return AssemblyIdentityMatches(assemblyName, _contractsIdentity)
                ? _contractsAssembly
                : LoadFromAssemblyPath(approvedPath);
        }

        if (string.Equals(assemblyName.Name, _contractsIdentity.Name, StringComparison.Ordinal))
        {
            if (!AssemblyIdentityMatches(assemblyName, _contractsIdentity))
            {
                throw new ContractsIdentityException();
            }

            return _contractsAssembly;
        }

        var resolvedPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (resolvedPath is null)
        {
            return null;
        }

        var loadPath = ResolveLoadPath(resolvedPath);
        return LoadFromAssemblyPath(loadPath);
    }

    private string ResolveLoadPath(string resolvedPath)
    {
        // The shadow root is a directory symlink into the approved extension root. Containment is
        // still enforced against the canonical real root, but the bytes are read through the
        // per-content shadow path so the runtime cannot serve a stale image cached for the real
        // path of an in-place replaced file.
        if (_shadowRoot is not null && CanonicalPath.IsWithin(_shadowRoot, resolvedPath))
        {
            var realCandidate = Path.Combine(_root, Path.GetRelativePath(_shadowRoot, resolvedPath));
            if (!CanonicalPath.TryCanonicalFileInRoot(_root, realCandidate, out var shadowedCanonical))
            {
                throw new InvalidOperationException();
            }

            return Path.Combine(_shadowRoot, Path.GetRelativePath(_root, shadowedCanonical));
        }

        if (!CanonicalPath.TryCanonicalFileInRoot(_root, resolvedPath, out var canonicalPath))
        {
            throw new InvalidOperationException();
        }

        return canonicalPath;
    }

    private static bool AssemblyIdentityMatches(AssemblyName requested, AssemblyName approved)
    {
        var requestedToken = requested.GetPublicKeyToken() ?? Array.Empty<byte>();
        var approvedToken = approved.GetPublicKeyToken() ?? Array.Empty<byte>();
        return string.Equals(requested.Name, approved.Name, StringComparison.Ordinal) &&
            requested.Version == approved.Version &&
            string.Equals(requested.CultureName, approved.CultureName, StringComparison.OrdinalIgnoreCase) &&
            requestedToken.AsSpan().SequenceEqual(approvedToken);
    }
}

internal sealed class ContractsIdentityException : Exception
{
}

/// <summary>
/// Maintains per-content directory symlinks under the shared temp root so every distinct payload
/// generation is loaded through a path the runtime assembly image cache has never seen.
/// </summary>
internal static class ExtensionAssemblyShadowLink
{
    private static int _cleanupRunning;
    private static readonly string TempRoot = Path.Combine(
        "/tmp",
        "nekostick",
        "extension-assembly-temp");

    /// <summary>
    /// Returns the shadow directory for one extension content generation, creating the directory
    /// symlink when missing, or <see langword="null" /> when shadowing is unavailable and the
    /// caller must load from the real path.
    /// </summary>
    internal static string? TryCreate(
        string extensionId,
        string? contentHash,
        string extensionRoot,
        string entryAssemblyPath,
        ILogger? logger)
    {
        try
        {
            var suffix = ResolveSuffix(contentHash, entryAssemblyPath);
            if (suffix is null)
            {
                return null;
            }

            Directory.CreateDirectory(TempRoot);
            var linkPath = Path.Combine(TempRoot, Sanitize(extensionId) + "-" + suffix);
            if (LinkTargets(linkPath, extensionRoot))
            {
                return linkPath;
            }

            // A name owned by different content is never rewritten: identical names imply
            // identical bytes, so the loser simply loads from the real path instead.
            if (new DirectoryInfo(linkPath).LinkTarget is not null || Directory.Exists(linkPath))
            {
                return null;
            }

            try
            {
                Directory.CreateSymbolicLink(linkPath, extensionRoot);
            }
            catch (IOException)
            {
                // A concurrent loader won the creation race; accept its link only when it names
                // the same content generation.
            }

            return LinkTargets(linkPath, extensionRoot) ? linkPath : null;
        }
        catch (Exception exception)
        {
            if (logger is { } target)
            {
                ExtensionLogMessages.ExtensionAssemblyShadowLinkUnavailable(target, exception, extensionId);
            }

            return null;
        }
    }

    private static bool LinkTargets(string linkPath, string extensionRoot)
    {
        var info = new DirectoryInfo(linkPath);
        if (info.LinkTarget is null)
        {
            return false;
        }

        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        return target is not null &&
            string.Equals(
                Path.GetFullPath(target.FullName),
                Path.GetFullPath(extensionRoot),
                StringComparison.Ordinal);
    }

    private static string? ResolveSuffix(string? contentHash, string entryAssemblyPath)
    {
        if (!string.IsNullOrWhiteSpace(contentHash))
        {
            var value = contentHash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? contentHash["sha256:".Length..]
                : contentHash;
            return Sanitize(value);
        }

        // Without a recorded digest the entry assembly bytes still yield a content-true key.
        try
        {
            using var stream = new FileStream(
                entryAssemblyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Schedules a fire-and-forget background sweep removing shadow links whose target directory
    /// is gone. Failures are logged; a clean sweep completes silently. Concurrent runs collapse
    /// into one.
    /// </summary>
    internal static void ScheduleInvalidLinkCleanup(ILogger? logger)
    {
        if (Interlocked.Exchange(ref _cleanupRunning, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                CleanupInvalidLinks(logger);
            }
            catch (Exception exception)
            {
                if (logger is { } target)
                {
                    ExtensionLogMessages.ExtensionAssemblyShadowLinkCleanupFailed(
                        target,
                        exception,
                        "Sweep");
                }
            }
            finally
            {
                Interlocked.Exchange(ref _cleanupRunning, 0);
            }
        });
    }

    internal static void CleanupInvalidLinks(ILogger? logger)
    {
        if (!Directory.Exists(TempRoot))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(TempRoot))
        {
            try
            {
                FileSystemInfo info = new DirectoryInfo(entry);
                if (info.LinkTarget is null)
                {
                    info = new FileInfo(entry);
                }

                if (info.LinkTarget is null)
                {
                    // Regular entries are not created by the shadow loader and are left alone.
                    continue;
                }

                if (!Directory.Exists(entry))
                {
                    // A symlink whose target is missing or is not a directory can never serve a
                    // payload generation again. File.Delete unlinks the entry itself, which also
                    // works for broken directory symlinks (DirectoryInfo.Delete follows the link
                    // and would throw for a missing target).
                    File.Delete(entry);
                }
            }
            catch (Exception exception)
            {
                if (logger is { } target)
                {
                    ExtensionLogMessages.ExtensionAssemblyShadowLinkCleanupFailed(
                        target,
                        exception,
                        "SweepEntry");
                }
            }
        }
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(
                char.IsLetterOrDigit(character) || character is '-' or '.' or '_' ? character : '-');
        }

        return builder.ToString();
    }
}
