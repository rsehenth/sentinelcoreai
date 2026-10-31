using SentinelCore.Application;
using SentinelCore.Domain;

namespace SentinelCore.Tests;

public static class PerformanceSamples
{
    public static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    public static CpuReading Cpu(double total = 20, double user = 12, double kernel = 8, bool valid = true) =>
        new(total, user, kernel, 1.5, 8, valid, []);

    public static MemoryReading Memory(ulong installed = 16UL * 1073741824,
        ulong available = 8UL * 1073741824, ulong? commitLimitBytes = 20UL * 1073741824,
        ulong? committedBytes = 10UL * 1073741824) =>
        new(installed, available, installed - available, available * 100d / installed,
            commitLimitBytes, committedBytes,
            commitLimitBytes is > 0 && committedBytes is { } committed
                ? committed * 100d / commitLimitBytes.Value : null,
            commitLimitBytes is { } limit && committedBytes is { } used && limit >= used
                ? limit - used : null,
            1.5, 2UL * 1073741824, true, []);

    public static ProcessReading Process(string identity = "100:1", int pid = 100, string name = "app",
        double cpu = 5, ulong privateBytes = 100UL * 1048576) =>
        new(identity, pid, name, Start, cpu, privateBytes * 2, privateBytes, 0, 0, true, true, false);

    public static StorageReading Disk(string id = "physical:0", int number = 0,
        double read = 1048576, double write = 524288, double? readLatency = 2, double? writeLatency = 3) =>
        new(id, StorageScope.Physical, "0 C:", number, null, read, write, 10, 5,
            readLatency, writeLatency, 0.4, 12, "disk-stable", true);

    public static PerformanceSample Sample(int index, DateTimeOffset? at = null,
        CpuReading? cpu = null, MemoryReading? memory = null,
        IReadOnlyList<ProcessReading>? processes = null, IReadOnlyList<StorageReading>? storage = null) =>
        new(index, at ?? Start.AddSeconds(index), at.HasValue ? (at.Value - Start).TotalSeconds : index, index == 0, cpu ?? Cpu(), memory ?? Memory(),
            processes ?? [Process()], [], storage ?? [Disk()], 40, []);

    public static PerformanceSession Session(int samples = 31)
    {
        var options = PerformanceRuleOptions.Default;
        var readings = Enumerable.Range(0, samples)
            .Select(index => Sample(index, cpu: Cpu(total: 90, user: 60, kernel: 30)))
            .ToArray();
        var statistics = new PerformanceStatisticsEngine().Compute(readings, options);
        var analysis = new PerformanceEvidenceAnalyzer(options).Analyze(readings, statistics);
        var completed = Start.AddSeconds(samples - 1);
        return new PerformanceSession(
            1, Guid.Parse("9a1c2b3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d"),
            SoftwareInfo.Version, SoftwareInfo.PerformanceSchemaVersion,
            PerformanceSessionPreset.Seconds30, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1),
            Start, completed, completed - Start, PerformanceSessionStatus.Completed,
            [new("performance-cpu", "1.0.0", PerformanceSourceCategory.Cpu, "GetSystemTimes",
                PerformanceSourceStatus.Completed, Start, completed, completed - Start, samples, false, [])],
            statistics, analysis.Observations, analysis.Notes, readings,
            new(samples, 0.4, 0.9, 120UL * 1048576, 80UL * 1048576, 35, 70), []);
    }

    public static PerformanceSession MaximalSession()
    {
        var options = PerformanceRuleOptions.Default;
        var processes = Enumerable.Range(0, 20).Select(index => Process(
            identity: $"{1000 + index}:1", pid: 1000 + index, name: $"process-{index:D2}",
            cpu: 5 + index, privateBytes: (100UL + (ulong)index) * 1048576)).ToArray();
        var disks = Enumerable.Range(0, 12).Select(index => Disk(
            id: $"physical:{index}", number: index,
            read: 1048576 + index, write: 524288 + index)).ToArray();
        var readings = Enumerable.Range(0, 181).Select(index => Sample(index,
            cpu: Cpu(total: 90, user: 60, kernel: 30),
            processes: processes, storage: disks)).ToArray();
        var statistics = new PerformanceStatisticsEngine().Compute(readings, options);
        var analysis = new PerformanceEvidenceAnalyzer(options).Analyze(readings, statistics);
        var completed = Start.AddSeconds(180);
        return new PerformanceSession(
            1, Guid.Parse("8b2d4c6e-1f3a-4b5c-9d7e-0a1b2c3d4e5f"),
            SoftwareInfo.Version, SoftwareInfo.PerformanceSchemaVersion,
            PerformanceSessionPreset.Minutes3, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(1),
            Start, completed, completed - Start, PerformanceSessionStatus.Completed,
            [new("performance-cpu", "1.0.0", PerformanceSourceCategory.Cpu, "GetSystemTimes",
                PerformanceSourceStatus.Completed, Start, completed, completed - Start, readings.Length, false, [])],
            statistics, analysis.Observations, analysis.Notes, readings,
            new(readings.Length, 0.4, 0.9, 120UL * 1048576, 80UL * 1048576, 35, 70), []);
    }
}
