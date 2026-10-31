using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentinelCore.Domain;

public enum DiagnosticStatus { Pending, Running, Completed, Partial, Failed, Cancelled }
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
    DiagnosticStatus Status, string Source, InventoryData? Data, IReadOnlyList<string> Limitations);

public sealed record AuditRecord(int FormatVersion, Guid Id, AuditKind Kind, string SoftwareVersion,
    DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, TimeSpan Duration,
    DiagnosticStatus Status, IReadOnlyList<CollectorResult> Results);

public sealed record AuditProgress(string CollectorId, DiagnosticStatus Status, int Finished, int Total);
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
