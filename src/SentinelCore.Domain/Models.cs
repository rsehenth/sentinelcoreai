using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentinelCore.Domain;

public enum DiagnosticStatus { Pending, Running, Completed, Partial, Failed, Cancelled, Interrupted }
public enum AuditKind { Basic, Full }
public enum FreshnessState { None, Current, Expiring, Outdated, Unsuccessful, InvalidDate }

public sealed record OperatingSystemInfo(string? Name, string? Edition, string? Version, string? Build,
    string? Architecture, DateTimeOffset? LastBootUtc);
public sealed record ProcessorInfo(string? Manufacturer, string? Model, int? PhysicalCores, int? LogicalProcessors);
public sealed record MemoryInfo(ulong? InstalledBytes, ulong? AvailableBytes);
public sealed record BoardInfo(string? Manufacturer, string? Model, string? BiosManufacturer,
    string? BiosVersion, DateTimeOffset? FirmwareDateUtc);
public sealed record GpuInfo(string? Name, string? Manufacturer, string? DriverVersion);
public sealed record DiskInfo(string? Device, string? Model, ulong? CapacityBytes, string? Interface, string? ReportedStatus);
public sealed record VolumeInfo(string? Device, string? FileSystem, ulong? CapacityBytes, ulong? FreeBytes);

// Each category has a typed payload. Null means unavailable, never zero by substitution.
public sealed record InventoryData(
    OperatingSystemInfo? OperatingSystem = null,
    IReadOnlyList<ProcessorInfo>? Processors = null,
    MemoryInfo? Memory = null,
    BoardInfo? Board = null,
    IReadOnlyList<GpuInfo>? Gpus = null,
    IReadOnlyList<DiskInfo>? Disks = null,
    IReadOnlyList<VolumeInfo>? Volumes = null);

public sealed record CollectorResult(Guid AuditId, string CollectorId, string CollectorVersion,
    DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, TimeSpan Duration,
    DiagnosticStatus Status, string Source, InventoryData? Data, IReadOnlyList<string> Limitations)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AuditEvidenceReference? Evidence { get; init; }
}

public sealed record AuditEvidenceReference(CorrelationSource Source, Guid RecordId, string Summary)
{
    public static bool IsDiagnosticModule(CorrelationSource source) => source is CorrelationSource.Hardware
        or CorrelationSource.Events or CorrelationSource.Performance or CorrelationSource.Network
        or CorrelationSource.Graphics or CorrelationSource.Monitoring;
    public static string CollectorIdFor(CorrelationSource source) => $"audit-{source.ToString().ToLowerInvariant()}";
}

public sealed record AuditRecord(int FormatVersion, Guid Id, AuditKind Kind, string SoftwareVersion,
    DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, TimeSpan Duration,
    DiagnosticStatus Status, IReadOnlyList<CollectorResult> Results)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PlannedSteps { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InterruptionReason { get; init; }
}

public sealed record AuditProgress(string CollectorId, DiagnosticStatus Status, int Finished, int Total)
{
    public CollectorResult? Result { get; init; }
    public string? Activity { get; init; }
    public bool IsSaving { get; init; }
}
public sealed record FreshnessResult(FreshnessState State, DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? ExpiresUtc, DiagnosticStatus? LatestAttemptStatus);
public sealed record StoreSnapshot(IReadOnlyList<AuditRecord> Audits, IReadOnlyList<string> Warnings);

public interface IDiagnosticCollector
{
    string Id { get; }
    string Version { get; }
    string Source { get; }
    Task<CollectorResult> CollectAsync(Guid auditId, CancellationToken cancellationToken);
}

public interface IAuditStore
{
    Task<StoreSnapshot> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AuditRecord audit, CancellationToken cancellationToken = default);
    string GetReportPath(Guid auditId);
}

public interface IAuditCheckpointStore
{
    Task<IAuditRunLease> AcquireAuditLeaseAsync(Guid auditId);
    Task<StoreSnapshot> ReadInterruptedAuditsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads authoritative audit history without rebuilding indexes or projections.</summary>
public interface IAuditHistoryReader
{
    Task<StoreSnapshot> ReadAuditHistoryAsync(CancellationToken cancellationToken = default);
}

public interface IAuditRunLease : IDisposable
{
    Task SaveCheckpointAsync(AuditRecord checkpoint);
    Task CompleteAsync(AuditRecord audit);
}

public interface IAuditDiagnosticStore : IHardwareSnapshotStore, IEventInvestigationStore,
    IPerformanceSessionStore, INetworkSessionStore, IGraphicsInvestigationStore, IMonitoringSessionStore;

public static class AuditJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter() }
    };
}

public static class Units
{
    public static ulong? KiBToBytes(ulong? value) => value.HasValue ? checked(value.Value * 1024UL) : null;
    public static string GiB(ulong? value) => value.HasValue ? $"{value.Value / 1073741824d:N1} GiB" : "Indisponível";
}
