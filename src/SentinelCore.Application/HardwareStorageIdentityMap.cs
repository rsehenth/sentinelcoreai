using SentinelCore.Domain;

namespace SentinelCore.Application;

public sealed class HardwareStorageIdentityMap
{
    private readonly IReadOnlyDictionary<int, string> disks;
    private readonly IReadOnlyDictionary<string, string> volumes;

    private HardwareStorageIdentityMap(IReadOnlyDictionary<int, string> disks,
        IReadOnlyDictionary<string, string> volumes)
    {
        this.disks = disks;
        this.volumes = volumes;
    }

    public IReadOnlyDictionary<int, string> PhysicalDisks => disks;
    public IReadOnlyDictionary<string, string> Volumes => volumes;

    public static HardwareStorageIdentityMap From(HardwareSnapshot? snapshot)
    {
        var disks = new Dictionary<int, string>();
        var volumes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in snapshot?.Results.SelectMany(result => result.Items) ?? [])
        {
            if (item.Category != HardwareCategory.Storage) continue;
            if (item.Kind == "disk" &&
                int.TryParse(item.Property("disk_number"), out var number))
                disks.TryAdd(number, item.StableId);
            if (item.Kind == "volume" && item.Property("drive_letter") is { Length: > 0 } letter)
                volumes.TryAdd(letter.TrimEnd(':'), item.StableId);
        }
        return new(disks, volumes);
    }

    public string? ForPhysical(int? diskNumber, string instanceName) =>
        diskNumber is { } number && disks.TryGetValue(number, out var stable) ? stable : null;

    public string? ForVolume(string? letter, string instanceName) =>
        !string.IsNullOrWhiteSpace(letter) && volumes.TryGetValue(letter.TrimEnd(':'), out var stable)
            ? stable : null;
}
