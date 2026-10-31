namespace SentinelCore.Domain;

public enum HardwareCategory { Cpu, Memory, Board, Gpu, Monitor, Storage, Network, Audio, Usb, Driver, Other }
public enum HardwareCollectionStatus { Running, Completed, Partial, Failed, Cancelled, Unsupported }
public enum DevicePresence { Present, Virtual, Disconnected, Unknown }

// Values are normalized strings so every category shares one durable contract.
// Volatile properties (free space, temperature, uptime) are excluded from diffs.
public sealed record HardwareProperty(string Name, string Value, bool Volatile = false);

public sealed record HardwareItem(
    HardwareCategory Category,
    string Kind,
    string StableId,
    string? Name,
    string? Manufacturer,
    string? Model,
    string? HardwareId,
    string? DriverProvider,
    string? DriverVersion,
    DateTimeOffset? DriverDateUtc,
    string? DriverClass,
    string? DriverInfName,
    string? FirmwareVersion,
    string? WindowsState,
    DevicePresence Presence,
    IReadOnlyList<HardwareProperty> Properties,
    IReadOnlyList<string> Limitations)
{
    public string? Property(string name) =>
        Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal))?.Value;
}

public sealed record HardwareCollectorResult(
    string CollectorId,
    string CollectorVersion,
    HardwareCategory Category,
    string Source,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    TimeSpan Duration,
    HardwareCollectionStatus Status,
    IReadOnlyList<HardwareItem> Items,
    IReadOnlyList<string> Limitations);

public sealed record HardwareSnapshot(
    int FormatVersion,
    Guid Id,
    string SoftwareVersion,
    string SchemaVersion,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    TimeSpan Duration,
    HardwareCollectionStatus Status,
    IReadOnlyList<HardwareCollectorResult> Results);

public sealed record HardwareProgress(string CollectorId, HardwareCollectionStatus Status, int Finished, int Total);

public sealed record HardwareStoreSnapshot(IReadOnlyList<HardwareSnapshot> Snapshots, IReadOnlyList<string> Warnings);

public interface IHardwareCollector
{
    string Id { get; }
    string Version { get; }
    HardwareCategory Category { get; }
    string Source { get; }
    Task<HardwareCollectorResult> CollectAsync(Guid snapshotId, IHardwareCollectionSession session,
        CancellationToken cancellationToken);
}

// One session per snapshot: expensive PnP/driver indexes are built at most once.
public interface IHardwareCollectionSession
{
    T GetOrAdd<T>(string key, Func<T> factory) where T : class;
}

public interface IHardwareSnapshotStore
{
    Task<HardwareStoreSnapshot> LoadSnapshotsAsync(CancellationToken cancellationToken = default);
    Task SaveSnapshotAsync(HardwareSnapshot snapshot, CancellationToken cancellationToken = default);
    string GetHardwareReportPath(Guid snapshotId);
}
