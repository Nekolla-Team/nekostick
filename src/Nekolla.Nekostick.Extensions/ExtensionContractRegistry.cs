using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Extensions;

/// <summary>Owns one generation's startup-only typed shared-contract exchange.</summary>
internal sealed class ExtensionContractRegistry : IExtensionContractRegistry, IDisposable
{
    private readonly object _gate = new();
    private readonly ImmutableDictionary<string, ExtensionContractExport> _exports;
    private readonly ImmutableDictionary<string, ExtensionContractImport> _imports;
    private readonly Func<string, Type, object?> _resolveProvider;
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);
    private bool _startupOpen = true;
    private bool _disposed;

    internal ExtensionContractRegistry(
        ImmutableArray<ExtensionContractExport> exports,
        ImmutableArray<ExtensionContractImport> imports,
        Func<string, Type, object?> resolveProvider)
    {
        _exports = exports.ToImmutableDictionary(static declaration => declaration.ContractId, StringComparer.Ordinal);
        _imports = imports.ToImmutableDictionary(static declaration => declaration.ContractId, StringComparer.Ordinal);
        _resolveProvider = resolveProvider;
    }

    public bool TryExport<TContract>(string contractId, TContract implementation)
        where TContract : class
    {
        if (string.IsNullOrWhiteSpace(contractId) || implementation is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_startupOpen || _disposed ||
                !_exports.TryGetValue(contractId, out var declaration) ||
                !TypeMatches<TContract>(declaration.TypeIdentity) ||
                _values.ContainsKey(contractId))
            {
                return false;
            }

            _values.Add(contractId, implementation);
            return true;
        }
    }

    public bool TryImport<TContract>(string contractId, out TContract? contract)
        where TContract : class
    {
        contract = null;
        if (string.IsNullOrWhiteSpace(contractId))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_startupOpen || _disposed ||
                !_imports.TryGetValue(contractId, out var declaration) ||
                !TypeMatches<TContract>(declaration.TypeIdentity))
            {
                return false;
            }

            if (_values.TryGetValue(contractId, out var ownValue) && ownValue is TContract ownContract)
            {
                contract = ownContract;
                return true;
            }
        }

        var resolved = _resolveProvider(contractId, typeof(TContract));
        if (resolved is not TContract typed)
        {
            return false;
        }

        contract = typed;
        return true;
    }

    internal bool TryResolveExport(string contractId, Type contractType, out object? value)
    {
        value = null;
        lock (_gate)
        {
            if (_disposed || !_values.TryGetValue(contractId, out var candidate) ||
                !contractType.IsInstanceOfType(candidate))
            {
                return false;
            }

            value = candidate;
            return true;
        }
    }

    internal void CompleteStartup()
    {
        lock (_gate)
        {
            _startupOpen = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _startupOpen = false;
            _values.Clear();
        }
    }

    private static bool TypeMatches<TContract>(string declaredTypeIdentity)
        where TContract : class =>
        string.Equals(
            typeof(TContract).FullName,
            declaredTypeIdentity,
            StringComparison.Ordinal);
}
