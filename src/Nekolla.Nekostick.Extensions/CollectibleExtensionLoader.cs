using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Nekolla.Nekostick.Contracts;

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
        ExtensionLoadHandle? handle)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
        Handle = handle;
    }

    /// <summary>Gets whether loading succeeded.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the safe failure category.</summary>
    public ExtensionFailureCode FailureCode { get; }

    /// <summary>Gets the lifecycle handle on success.</summary>
    public ExtensionLoadHandle? Handle { get; }

    internal static ExtensionLoadResult Success(ExtensionLoadHandle handle) =>
        new(true, ExtensionFailureCode.None, handle);

    internal static ExtensionLoadResult Failure(ExtensionFailureCode code) =>
        new(false, code, null);
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
    private ExtensionLoadContext? _loadContext;
    private Assembly? _entryAssembly;
    private Type? _entryType;
    private ExtensionRuntimeState _state;

    internal ExtensionLoadHandle(
        ExtensionManifest manifest,
        ExtensionLoadContext loadContext,
        Assembly entryAssembly,
        Type entryType)
    {
        _manifest = manifest;
        _loadContext = loadContext;
        _entryAssembly = entryAssembly;
        _entryType = entryType;
        _weakContext = new WeakReference(loadContext);
        _state = ExtensionRuntimeState.Loaded;
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
        catch (Exception)
        {
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

    /// <summary>Creates a loader for one host API version and approved contract catalog.</summary>
    /// <param name="hostApiVersion">The host API version used for compatibility validation.</param>
    /// <param name="contractCatalog">The host-owned shared contract catalog.</param>
    public CollectibleExtensionLoader(
        SemVersion hostApiVersion,
        ExtensionContractCatalog? contractCatalog = null)
    {
        _hostApiVersion = hostApiVersion;
        _contractCatalog = contractCatalog ?? ExtensionContractCatalog.CreateDefault();
    }

    /// <summary>Loads an entry assembly from the manifest's approved extension root.</summary>
    /// <param name="manifest">The manifest returned by explicit discovery.</param>
    /// <returns>A safe result with no raw exception or path data.</returns>
    public ExtensionLoadResult Load(ExtensionManifest? manifest)
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
            loadContext = new ExtensionLoadContext(entryPath, root, _contractCatalog);
            var entryAssembly = loadContext.LoadFromAssemblyPath(entryPath);
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

            var handle = new ExtensionLoadHandle(manifest, loadContext, entryAssembly, entryType);
            loadContext = null;
            return ExtensionLoadResult.Success(handle);
        }
        catch (ContractsIdentityException)
        {
            loadContext?.Unload();
            return ExtensionLoadResult.Failure(ExtensionFailureCode.ContractsIdentityMismatch);
        }
        catch (Exception)
        {
            loadContext?.Unload();
            return ExtensionLoadResult.Failure(ExtensionFailureCode.LoadFailed);
        }
    }
}

internal sealed class ExtensionLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _root;
    private readonly ExtensionContractCatalog _contractCatalog;
    private readonly Assembly _contractsAssembly = typeof(HostApiVersion).Assembly;
    private readonly AssemblyName _contractsIdentity = typeof(HostApiVersion).Assembly.GetName();

    internal ExtensionLoadContext(
        string entryAssemblyPath,
        string root,
        ExtensionContractCatalog contractCatalog)
        : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
        _root = root;
        _contractCatalog = contractCatalog;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (_contractCatalog.TryResolveAssembly(assemblyName, _root, out var approvedPath))
        {
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

        if (!CanonicalPath.TryCanonicalFileInRoot(_root, resolvedPath, out var canonicalPath))
        {
            throw new InvalidOperationException();
        }

        return LoadFromAssemblyPath(canonicalPath);
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
