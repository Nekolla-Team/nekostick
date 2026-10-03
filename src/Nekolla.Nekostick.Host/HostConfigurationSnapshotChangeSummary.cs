using System.Collections.Immutable;
using System.Text;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Host;

internal sealed class HostConfigurationSnapshotChangeSummary
{
    private HostConfigurationSnapshotChangeSummary(ImmutableArray<CategoryChange> categories)
    {
        IsEmpty = categories.All(static category => category.IsEmpty);
        Text = Format(categories, IsEmpty);
    }

    internal bool IsEmpty { get; }

    internal string Text { get; }

    internal static HostConfigurationSnapshotChangeSummary Create(
        HostConfigurationSnapshot? previous,
        HostConfigurationSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var categories = ImmutableArray.CreateBuilder<CategoryChange>(5);
        categories.Add(CreateGlobalSettingsChange(previous?.GlobalSettings, current.GlobalSettings));
        categories.Add(CompareItems(
            "routes",
            previous is null ? ImmutableArray<RouteConfiguration>.Empty : previous.Routes,
            current.Routes,
            static route => route.Id.ToString("D"),
            static route => route.Version));
        categories.Add(CompareItems(
            "services",
            previous is null ? ImmutableArray<ServiceConfiguration>.Empty : previous.Services,
            current.Services,
            static service => service.Id.ToString("D"),
            static service => service.Version));
        categories.Add(CompareItems(
            "extension-records",
            previous is null ? ImmutableArray<ExtensionRecordConfiguration>.Empty : previous.ExtensionRecords,
            current.ExtensionRecords,
            static record => record.ExtensionId,
            static record => record.RecordVersion));
        categories.Add(CompareItems(
            "extension-settings",
            previous is null ? ImmutableArray<ExtensionSettingsConfiguration>.Empty : previous.ExtensionSettings,
            current.ExtensionSettings,
            static settings => settings.ExtensionId,
            static settings => settings.Version));
        return new HostConfigurationSnapshotChangeSummary(categories.MoveToImmutable());
    }

    internal bool IsSameRevision(
        HostConfigurationSnapshot previous,
        HostConfigurationSnapshot current) =>
        IsEmpty &&
        previous.Version == current.Version &&
        string.Equals(previous.CommittedBy, current.CommittedBy, StringComparison.Ordinal);

    private static CategoryChange CreateGlobalSettingsChange(
        GlobalSettingsConfiguration? previous,
        GlobalSettingsConfiguration current)
    {
        if (previous is null)
        {
            return CategoryChange.Create("global-settings", ["global"], [], []);
        }

        return previous.Version == current.Version
            ? CategoryChange.Empty("global-settings")
            : CategoryChange.Create("global-settings", [], [], ["global"]);
    }

    private static CategoryChange CompareItems<T>(
        string category,
        ImmutableArray<T> previous,
        ImmutableArray<T> current,
        Func<T, string> id,
        Func<T, long> version)
    {
        var previousById = new Dictionary<string, long>(previous.Length, StringComparer.Ordinal);
        foreach (var item in previous)
        {
            previousById.TryAdd(id(item), version(item));
        }

        var currentById = new Dictionary<string, long>(current.Length, StringComparer.Ordinal);
        var added = new List<string>();
        var updated = new List<string>();
        foreach (var item in current)
        {
            var itemId = id(item);
            var itemVersion = version(item);
            if (!currentById.TryAdd(itemId, itemVersion))
            {
                continue;
            }
            if (!previousById.TryGetValue(itemId, out var previousVersion))
            {
                added.Add(itemId);
            }
            else if (previousVersion != itemVersion)
            {
                updated.Add(itemId);
            }
        }

        var removed = new List<string>();
        foreach (var itemId in previousById.Keys)
        {
            if (!currentById.ContainsKey(itemId))
            {
                removed.Add(itemId);
            }
        }

        added.Sort(StringComparer.Ordinal);
        removed.Sort(StringComparer.Ordinal);
        updated.Sort(StringComparer.Ordinal);
        return CategoryChange.Create(
            category,
            added.ToImmutableArray(),
            removed.ToImmutableArray(),
            updated.ToImmutableArray());
    }

    private static string Format(ImmutableArray<CategoryChange> categories, bool isEmpty)
    {
        if (isEmpty)
        {
            return "none";
        }

        var builder = new StringBuilder();
        foreach (var category in categories)
        {
            if (builder.Length != 0)
            {
                builder.Append("; ");
            }

            builder.Append(category.Name);
            AppendIds(builder, " added=", category.AddedIds);
            AppendIds(builder, " removed=", category.RemovedIds);
            AppendIds(builder, " updated=", category.UpdatedIds);
        }

        return builder.ToString();
    }

    private static void AppendIds(StringBuilder builder, string label, ImmutableArray<string> ids)
    {
        builder.Append(label).Append(ids.Length);
        if (ids.IsEmpty)
        {
            return;
        }

        builder.Append('[');
        for (var index = 0; index < ids.Length; index++)
        {
            if (index != 0)
            {
                builder.Append(',');
            }

            builder.Append(ids[index]);
        }

        builder.Append(']');
    }

    private sealed record CategoryChange(
        string Name,
        ImmutableArray<string> AddedIds,
        ImmutableArray<string> RemovedIds,
        ImmutableArray<string> UpdatedIds)
    {
        internal bool IsEmpty => AddedIds.IsEmpty && RemovedIds.IsEmpty && UpdatedIds.IsEmpty;

        internal static CategoryChange Empty(string name) => Create(name, [], [], []);

        internal static CategoryChange Create(
            string name,
            ImmutableArray<string> added,
            ImmutableArray<string> removed,
            ImmutableArray<string> updated) =>
            new(name, added, removed, updated);
    }
}

internal static class HostConfigurationPublicationSemantics
{
    internal static bool ShouldSuppressDuplicatePublication(
        HostConfigurationSnapshot? previous,
        HostConfigurationSnapshot candidate,
        HostConfigurationSnapshotChangeSummary changes,
        bool hasDispatchGenerationChange,
        bool forcedReloadRequested) =>
        previous is not null &&
        !hasDispatchGenerationChange &&
        !forcedReloadRequested &&
        changes.IsSameRevision(previous, candidate);
}
