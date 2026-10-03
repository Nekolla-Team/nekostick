using System.Threading;

namespace Nekolla.Nekostick.Persistence;

/// <summary>Carries trusted caller identity to configuration revision persistence boundaries.</summary>
public static class HostConfigurationWriteContext
{
    private const string DefaultCommittedBy = "host:config-api";
    private static readonly AsyncLocal<string?> CurrentContext = new();

    /// <summary>Enters a write scope attributed to the trusted extension caller.</summary>
    /// <param name="extensionId">The host-bound extension identifier.</param>
    /// <returns>A scope that restores the previous caller identity when disposed.</returns>
    public static IDisposable EnterExtension(string extensionId)
    {
        if (string.IsNullOrWhiteSpace(extensionId) || extensionId.Length > 128 || extensionId.Any(char.IsControl))
        {
            throw new ArgumentException("A safe extension identifier is required.", nameof(extensionId));
        }

        return Enter($"extension:{extensionId}");
    }

    /// <summary>Enters a write scope attributed to a stable host component name.</summary>
    /// <param name="component">The stable host component identifier.</param>
    /// <returns>A scope that restores the previous caller identity when disposed.</returns>
    public static IDisposable EnterHostComponent(string component)
    {
        if (string.IsNullOrWhiteSpace(component) ||
            component.Length > 128 ||
            component.Contains(':') ||
            component.Any(char.IsControl))
        {
            throw new ArgumentException("A safe host component name is required.", nameof(component));
        }

        return Enter($"host:{component}");
    }

    internal static string CurrentCommittedBy => CurrentContext.Value ?? DefaultCommittedBy;

    private static Scope Enter(string committedBy)
    {
        var previous = CurrentContext.Value;
        CurrentContext.Value = committedBy;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private readonly string? _previous = previous;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                CurrentContext.Value = _previous;
            }
        }
    }
}
