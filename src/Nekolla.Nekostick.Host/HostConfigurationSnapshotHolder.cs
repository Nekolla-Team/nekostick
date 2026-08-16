using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Persistence;

namespace Nekolla.Nekostick.Host;

/// <summary>Provides lock-free access to the current immutable host configuration snapshot.</summary>
public interface IHostConfigurationSnapshotAccessor
{
    /// <summary>Gets the last complete validated snapshot, if one is available.</summary>
    HostConfigurationSnapshot? Current { get; }

    /// <summary>Gets whether a complete validated snapshot is available.</summary>
    bool HasSnapshot { get; }
}

/// <summary>Holds complete immutable configuration and replaces it atomically after validation.</summary>
public sealed class HostConfigurationSnapshotHolder : IHostConfigurationSnapshotAccessor
{
    private readonly object _replacementGate = new();
    private HostConfigurationSnapshot? _current;

    /// <inheritdoc />
    public HostConfigurationSnapshot? Current => Volatile.Read(ref _current);

    /// <summary>Gets the current snapshot using the host configuration terminology.</summary>
    public HostConfigurationSnapshot? Snapshot => Current;

    /// <inheritdoc />
    public bool HasSnapshot => Current is not null;

    /// <summary>Attempts to replace the current snapshot with a complete validated value.</summary>
    /// <param name="snapshot">The immutable candidate snapshot.</param>
    /// <returns><see langword="true"/> when the replacement was committed.</returns>
    public bool TryReplace(HostConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!HostConfigurationSnapshotValidator.IsComplete(snapshot) ||
            !HostConfigurationSemanticValidator.TryValidateSnapshot(snapshot))
        {
            return false;
        }

        lock (_replacementGate)
        {
            var current = Volatile.Read(ref _current);
            if (current is not null && snapshot.Version < current.Version)
            {
                return false;
            }

            Interlocked.Exchange(ref _current, snapshot);
            return true;
        }
    }
}

/// <summary>Validates the complete DTO graph before it is published to the runtime.</summary>
internal static class HostConfigurationSnapshotValidator
{
    internal static bool IsComplete(HostConfigurationSnapshot snapshot)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.Version < 0 || snapshot.GlobalSettings is null)
            {
                return false;
            }

            if (snapshot.GlobalSettings.TrustedProxyCidrs.Any(value =>
                    value is null || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)))
            {
                return false;
            }

            if (snapshot.GlobalSettings.ConfigurationPollInterval < TimeSpan.FromSeconds(1) ||
                snapshot.GlobalSettings.ConfigurationPollInterval.Ticks % TimeSpan.TicksPerSecond != 0)
            {
                return false;
            }

            if (!AreUniqueIds(snapshot.Services.Select(value => value?.Id)) ||
                !AreUniqueIds(snapshot.Routes.Select(value => value?.Id)) ||
                !AreUniqueStrings(snapshot.ExtensionRecords.Select(value => value?.ExtensionId)) ||
                !AreUniqueStrings(snapshot.ExtensionSettings.Select(value => value?.ExtensionId)))
            {
                return false;
            }

            var serviceIds = snapshot.Services.Select(value => value.Id).ToHashSet();
            var extensionIds = snapshot.ExtensionRecords
                .Select(value => value.ExtensionId)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var route in snapshot.Routes)
            {
                if (route is null ||
                    !IsValidJsonObject(route.MetadataJson) ||
                    !AreValidRewrites(route.RequestHeaderRewrites) ||
                    !AreValidRewrites(route.ResponseHeaderRewrites))
                {
                    return false;
                }

                switch (route.Target)
                {
                    case MicroserviceRouteTargetConfiguration microservice:
                        if (!serviceIds.Contains(microservice.ServiceId))
                        {
                            return false;
                        }

                        break;
                    case ExtensionHandlerRouteTargetConfiguration extension:
                        if (!extensionIds.Contains(extension.HandlerId))
                        {
                            return false;
                        }

                        break;
                    case StaticFileRouteTargetConfiguration:
                        break;
                    case null:
                        return false;
                    default:
                        return false;
                }
            }

            foreach (var service in snapshot.Services)
            {
                if (service is null ||
                    !IsValidJsonArray(service.ArgumentList) ||
                    !IsValidJsonObject(service.Environment) ||
                    service.ArgumentList.Any(value => value is null || value.Any(char.IsControl)) ||
                    service.Environment.Any(value =>
                        string.IsNullOrWhiteSpace(value.Key) ||
                        value.Key.Any(char.IsControl) ||
                        value.Value is null ||
                        value.Value.Any(char.IsControl)))
                {
                    return false;
                }
            }

            foreach (var settings in snapshot.ExtensionSettings)
            {
                if (settings is null ||
                    !extensionIds.Contains(settings.ExtensionId) ||
                    !IsValidJson(settings.SettingsJson))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool AreUniqueIds(IEnumerable<Guid?> values)
    {
        var seen = new HashSet<Guid>();
        foreach (var value in values)
        {
            if (value is null)
            {
                return false;
            }

            if (!seen.Add(value.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreUniqueStrings(IEnumerable<string?> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is null || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || !seen.Add(value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidJson<T>(T value)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
            return document.RootElement.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsValidJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsValidJsonArray<T>(IEnumerable<T> values) => IsValidJson(values);

    private static bool AreValidRewrites(IEnumerable<HeaderRewriteConfiguration> rewrites)
    {
        foreach (var rewrite in rewrites)
        {
            if (rewrite is null ||
                string.IsNullOrWhiteSpace(rewrite.Name) ||
                rewrite.Name.Any(char.IsControl) ||
                rewrite.Value?.Any(char.IsControl) == true ||
                rewrite.Operation is not (HeaderRewriteOperation.Remove or HeaderRewriteOperation.Set or HeaderRewriteOperation.Add))
            {
                return false;
            }

            if ((rewrite.Operation is HeaderRewriteOperation.Set or HeaderRewriteOperation.Add) &&
                rewrite.Value is null)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidJsonObject<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> values)
        where TKey : notnull => IsValidJson(values);

    private static bool IsValidJsonObject(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
