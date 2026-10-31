using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows.Performance;

public static class WindowsPerformanceCollectors
{
    public static IReadOnlyList<IPerformanceCollector> Create() =>
    [
        new CpuPerformanceCollector(),
        new MemoryPerformanceCollector(),
        new ProcessPerformanceCollector(),
        new StoragePerformanceCollector()
    ];
}
