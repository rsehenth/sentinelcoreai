using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows.Performance;

internal sealed class MemoryPerformanceCollector : IPerformanceCollector
{
    private const string CollectorId = "performance-memory";

    public string Id => CollectorId;
    public string Version => "1.0.0";
    public string Source => "GlobalMemoryStatusEx + WMI PerfOS_Memory";
    public PerformanceSourceCategory Category => PerformanceSourceCategory.Memory;
    public IPerformanceCollectorSession StartSession(PerformanceCollectionContext context) => new Session();

    private sealed class Session : IPerformanceCollectorSession
    {
        public Task<PerformanceCollectorTick> ReadAsync(DateTimeOffset timestampUtc, TimeSpan elapsed,
            CancellationToken cancellationToken) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NativeSystem.TryGetMemory(out var installed, out var available))
                return PerformanceCollectorTick.Empty(CollectorId, timestampUtc,
                    "GlobalMemoryStatusEx indisponível; memória não coletada nesta amostra.");
            var limitations = new List<string>();
            ulong? commitLimit = null, committed = null, cache = null;
            double? pages = null;
            try
            {
                var wmi = new WmiRead(cancellationToken);
                var row = wmi.Query(
                    "SELECT CommitLimit,CommittedBytes,PagesPerSec,CacheBytes " +
                    "FROM Win32_PerfFormattedData_PerfOS_Memory", allowEmpty: true).FirstOrDefault();
                commitLimit = wmi.UInt64Opt(row, "CommitLimit");
                committed = wmi.UInt64Opt(row, "CommittedBytes");
                cache = wmi.UInt64Opt(row, "CacheBytes");
                pages = wmi.UInt64Opt(row, "PagesPerSec") is { } value ? (double)value : null;
                limitations.AddRange(wmi.Limitations);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                limitations.Add(
                    $"Commit e paginação indisponíveis ({error.GetType().Name}); memória física permanece válida.");
            }
            var reading = PerformanceNormalizers.NormalizeMemory(installed, available,
                commitLimit, committed, pages, cache);
            return new PerformanceCollectorTick(CollectorId, timestampUtc, false, null, reading, [], [], [],
                reading.Limitations.Concat(limitations).Distinct(StringComparer.Ordinal).ToArray());
        }, cancellationToken);

        public void Dispose() { }
    }
}
