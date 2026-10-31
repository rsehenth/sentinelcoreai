using System.Globalization;
using System.Management;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows.Performance;

internal sealed class StoragePerformanceCollector : IPerformanceCollector
{
    private const string CollectorId = "performance-storage";
    private const string Fields =
        "Name,DiskReadBytesPerSec,DiskWriteBytesPerSec,DiskReadsPerSec,DiskWritesPerSec," +
        "AvgDisksecPerRead,AvgDisksecPerWrite,CurrentDiskQueueLength,PercentDiskTime ";

    private const string PhysicalQuery =
        "SELECT " + Fields + "FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk";

    private const string VolumeQuery =
        "SELECT " + Fields + "FROM Win32_PerfFormattedData_PerfDisk_LogicalDisk";

    public string Id => CollectorId;
    public string Version => "1.0.0";
    public string Source => "WMI PerfDisk PhysicalDisk + LogicalDisk (somente leitura)";
    public PerformanceSourceCategory Category => PerformanceSourceCategory.Storage;
    public IPerformanceCollectorSession StartSession(PerformanceCollectionContext context) => new Session(context);

    private sealed class Session(PerformanceCollectionContext context) : IPerformanceCollectorSession
    {
        private readonly int limit = context.Limits?.MaxStorageInstances
            ?? PerformanceLimits.Default.MaxStorageInstances;
        private bool first = true;

        public Task<PerformanceCollectorTick> ReadAsync(DateTimeOffset timestampUtc, TimeSpan elapsed,
            CancellationToken cancellationToken) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wmi = new WmiRead(cancellationToken);
            var physical = wmi.Query(PhysicalQuery, allowEmpty: true);
            var volumes = wmi.Query(VolumeQuery, allowEmpty: true);
            if (wmi.LastError?.ErrorCode == ManagementStatus.InvalidClass)
                return PerformanceCollectorTick.NotSupported(CollectorId, timestampUtc,
                    "Classes de desempenho de disco indisponíveis nesta edição do Windows (InvalidClass).");
            if (physical.Count == 0 && volumes.Count == 0)
                return PerformanceCollectorTick.Empty(CollectorId, timestampUtc,
                    "Contadores de disco não expostos; armazenamento não coletado.");

            var limitations = new List<string>(wmi.Limitations);
            var valid = !first;
            if (first)
            {
                first = false;
                limitations.Add("Primeira amostra do provedor WMI; valores podem ser zero.");
            }
            limitations.Add(
                "Latência do provedor WMI formatado tem resolução de 1 s; valores abaixo de 1 s aparecem como 0.");

            var readings = new List<StorageReading>();
            foreach (var row in physical)
            {
                var name = wmi.TextOpt(row, "Name");
                if (name is null || name.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                var diskNumber = PerformanceNormalizers.ParseDiskNumber(name);
                var stable = diskNumber is { } number && context.HardwareDiskIds is { } disks &&
                    disks.TryGetValue(number, out var hardware) ? hardware : null;
                readings.Add(CreateReading(row, StorageScope.Physical, name, diskNumber, null, stable, valid));
            }
            foreach (var row in volumes)
            {
                var name = wmi.TextOpt(row, "Name");
                if (name is null || name.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                var letter = name.Length == 2 && name[1] == ':' && char.IsLetter(name[0])
                    ? name[..1] : null;
                var stable = letter is not null && context.HardwareVolumeIds is { } volumeIds &&
                    volumeIds.TryGetValue(letter, out var hardware) ? hardware : null;
                readings.Add(CreateReading(row, StorageScope.Volume, name, null, letter, stable, valid));
            }
            if (readings.Count > limit)
            {
                readings = readings
                    .OrderByDescending(reading => (reading.ReadBytesPerSecond ?? 0) +
                        (reading.WriteBytesPerSecond ?? 0) + (reading.ReadOperationsPerSecond ?? 0) +
                        (reading.WriteOperationsPerSecond ?? 0))
                    .Take(limit)
                    .ToList();
                limitations.Add(
                    $"Limite de {limit} instâncias de armazenamento acompanhadas atingido; " +
                    "instâncias com menos atividade omitidas.");
            }
            return new PerformanceCollectorTick(CollectorId, timestampUtc, false, null, null, [], [], readings,
                limitations.Distinct(StringComparer.Ordinal).ToArray());
        }, cancellationToken);

        private static StorageReading CreateReading(Dictionary<string, object?> row, StorageScope scope,
            string name, int? diskNumber, string? letter, string? stable, bool valid) =>
            PerformanceNormalizers.NormalizeStorage(scope, name, diskNumber, letter,
                NumberOrNull(row, "DiskReadBytesPerSec"), NumberOrNull(row, "DiskWriteBytesPerSec"),
                NumberOrNull(row, "DiskReadsPerSec"), NumberOrNull(row, "DiskWritesPerSec"),
                NumberOrNull(row, "AvgDisksecPerRead"), NumberOrNull(row, "AvgDisksecPerWrite"),
                NumberOrNull(row, "CurrentDiskQueueLength"), NumberOrNull(row, "PercentDiskTime"),
                stable, valid);

        private static double? NumberOrNull(Dictionary<string, object?> row, string field) =>
            row.TryGetValue(field, out var value) && value is not null and not DBNull &&
            double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                ? Math.Max(0, number)
                : null;

        public void Dispose() { }
    }
}
