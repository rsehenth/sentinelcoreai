using SentinelCore.Domain;

namespace SentinelCore.Application;

public enum HardwareChangeType
{
    Added, Removed, DriverChanged, FirmwareChanged, CapacityChanged, ConfigurationChanged, StateChanged
}

public sealed record HardwareChange(HardwareChangeType Type, HardwareCategory Category, string Kind, string StableId,
    string? Name, string? Field, string? Before, string? After);

public sealed record HardwareDiff(Guid FromId, Guid ToId, IReadOnlyList<HardwareChange> Changes,
    IReadOnlyList<HardwareCategory> NotComparable, IReadOnlyList<string> Limitations);

// Reports observations only. A changed driver is not evidence of cause or health.
public static class HardwareDiffEngine
{
    private const string CapacityKey = "capacity_bytes";

    public static HardwareDiff Compare(HardwareSnapshot from, HardwareSnapshot to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var changes = new List<HardwareChange>();
        var notComparable = new List<HardwareCategory>();
        var limitations = new List<string>();
        var fromResults = from.Results.ToDictionary(r => r.Category);
        var toResults = to.Results.ToDictionary(r => r.Category);
        var categories = fromResults.Keys.Union(toResults.Keys).OrderBy(c => c).ToArray();

        foreach (var category in categories)
        {
            fromResults.TryGetValue(category, out var before);
            toResults.TryGetValue(category, out var after);
            if (!IsPresenceComparable(before, after, category, limitations))
                notComparable.Add(category);
        }

        var beforeItems = Index(from.Results);
        var afterItems = Index(to.Results);
        foreach (var (key, item) in afterItems)
        {
            if (beforeItems.ContainsKey(key)) continue;
            if (!notComparable.Contains(item.Category))
                changes.Add(Change(HardwareChangeType.Added, item, null, null, null));
        }
        foreach (var (key, item) in beforeItems)
        {
            if (afterItems.ContainsKey(key)) continue;
            if (!notComparable.Contains(item.Category))
                changes.Add(Change(HardwareChangeType.Removed, item, null, null, null));
        }
        foreach (var (key, before) in beforeItems)
        {
            if (!afterItems.TryGetValue(key, out var after)) continue;
            CompareItem(before, after, changes);
        }
        var ordered = changes
            .OrderBy(c => c.Category).ThenBy(c => c.Type).ThenBy(c => c.Kind, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Field, StringComparer.Ordinal)
            .ToArray();
        return new(from.Id, to.Id, ordered, notComparable, limitations);
    }

    private static bool IsPresenceComparable(HardwareCollectorResult? before, HardwareCollectorResult? after,
        HardwareCategory category, List<string> limitations)
    {
        if (before is null || after is null)
        {
            limitations.Add($"{category}: categoria ausente em um dos inventários; adições e remoções não foram avaliadas.");
            return false;
        }
        if (before.Status == HardwareCollectionStatus.Completed && after.Status == HardwareCollectionStatus.Completed)
            return true;
        var reason = before.Status != HardwareCollectionStatus.Completed ? before.Status : after.Status;
        limitations.Add($"{category}: coleta {reason} em um dos inventários; adições e remoções não foram avaliadas.");
        return false;
    }

    private static Dictionary<(HardwareCategory Category, string Kind, string StableId), HardwareItem> Index(
        IReadOnlyList<HardwareCollectorResult> results)
    {
        var index = new Dictionary<(HardwareCategory, string, string), HardwareItem>();
        foreach (var item in results.SelectMany(r => r.Items))
            index[(item.Category, item.Kind, item.StableId)] = item;
        return index;
    }

    private static void CompareItem(HardwareItem before, HardwareItem after, List<HardwareChange> changes)
    {
        if (!Same(before.DriverVersion, after.DriverVersion))
            changes.Add(Change(HardwareChangeType.DriverChanged, after, "driver_version", before.DriverVersion, after.DriverVersion));
        if (before.DriverDateUtc != after.DriverDateUtc)
            changes.Add(Change(HardwareChangeType.DriverChanged, after, "driver_date",
                before.DriverDateUtc?.ToUniversalTime().ToString("O"), after.DriverDateUtc?.ToUniversalTime().ToString("O")));
        if (!Same(before.DriverProvider, after.DriverProvider))
            changes.Add(Change(HardwareChangeType.DriverChanged, after, "driver_provider", before.DriverProvider, after.DriverProvider));
        if (!Same(before.DriverInfName, after.DriverInfName))
            changes.Add(Change(HardwareChangeType.DriverChanged, after, "driver_inf", before.DriverInfName, after.DriverInfName));
        if (!Same(before.FirmwareVersion, after.FirmwareVersion))
            changes.Add(Change(HardwareChangeType.FirmwareChanged, after, "firmware_version", before.FirmwareVersion, after.FirmwareVersion));
        if (!Same(before.WindowsState, after.WindowsState))
            changes.Add(Change(HardwareChangeType.StateChanged, after, "windows_state", before.WindowsState, after.WindowsState));
        if (before.Presence != after.Presence)
            changes.Add(Change(HardwareChangeType.StateChanged, after, "presence", before.Presence.ToString(), after.Presence.ToString()));
        if (!Same(before.Name, after.Name))
            changes.Add(Change(HardwareChangeType.ConfigurationChanged, after, "name", before.Name, after.Name));

        var names = before.Properties.Select(p => p.Name).Union(after.Properties.Select(p => p.Name))
            .OrderBy(n => n, StringComparer.Ordinal);
        foreach (var name in names)
        {
            var oldValue = before.Properties.FirstOrDefault(p => p.Name == name);
            var newValue = after.Properties.FirstOrDefault(p => p.Name == name);
            if (oldValue is null && newValue is null) continue;
            if (oldValue?.Volatile == true || newValue?.Volatile == true) continue;
            if (Same(oldValue?.Value, newValue?.Value)) continue;
            var type = name == CapacityKey ? HardwareChangeType.CapacityChanged : HardwareChangeType.ConfigurationChanged;
            changes.Add(Change(type, after, name, oldValue?.Value, newValue?.Value));
        }
    }

    private static HardwareChange Change(HardwareChangeType type, HardwareItem item, string? field, string? before,
        string? after) => new(type, item.Category, item.Kind, item.StableId, item.Name, field, before, after);

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
}
