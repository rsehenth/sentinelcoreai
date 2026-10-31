using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class PerformanceStatisticsTests
{
    private static PerformanceSample Sample(int index, double cpu, double? availablePercent = null,
        ulong? privateBytes = null, double? diskLatency = null, bool cpuValid = true)
    {
        var memory = availablePercent is null ? PerformanceSamples.Memory()
            : PerformanceSamples.Memory(available: (ulong)(16UL * 1073741824 * availablePercent.Value / 100));
        IReadOnlyList<ProcessReading> processes = privateBytes is null
            ? [PerformanceSamples.Process()]
            : [PerformanceSamples.Process(privateBytes: privateBytes.Value)];
        IReadOnlyList<StorageReading> storage =
        [
            PerformanceSamples.Disk(readLatency: diskLatency, writeLatency: diskLatency,
                read: diskLatency is null ? 0.0 : 5 * 1048576)
        ];
        return PerformanceSamples.Sample(index, cpu: PerformanceSamples.Cpu(total: cpu, valid: cpuValid),
            memory: memory, processes: processes, storage: storage);
    }

    [Fact]
    public void DescriptiveStatisticsUseOnlyValidSamples()
    {
        var samples = new[] { Sample(0, 10), Sample(1, 20), Sample(2, 30), Sample(3, 40),
            Sample(4, 999, cpuValid: false) };
        var stats = new PerformanceStatisticsEngine().Compute(samples, PerformanceRuleOptions.Default);
        var cpu = stats.CpuTotalPercent;
        Assert.Equal(4, cpu.ValidSamples);
        Assert.Equal(5, cpu.TotalSamples);
        Assert.Equal(10, cpu.Min);
        Assert.Equal(40, cpu.Max);
        Assert.Equal(25, cpu.Mean);
        Assert.Null(cpu.P95); // menos de 20 amostras válidas
        Assert.Contains(cpu.Limitations, l => l.Contains("percentis"));
    }

    [Fact]
    public void PercentilesRequireEnoughSamples()
    {
        var samples = Enumerable.Range(0, 20).Select(i => Sample(i, i + 1)).ToArray();
        var stats = new PerformanceStatisticsEngine().Compute(samples, PerformanceRuleOptions.Default);
        // Nearest-rank: P95 de 20 valores = 19º valor; P99 = 20º.
        Assert.Equal(19, stats.CpuTotalPercent.P95);
        Assert.Equal(20, stats.CpuTotalPercent.P99);
        Assert.Equal(10.5, stats.CpuTotalPercent.Median);
    }

    [Fact]
    public void GrowthAndSlopeAreComputedForPrivateBytes()
    {
        var samples = Enumerable.Range(0, 11)
            .Select(i => Sample(i, 1, privateBytes: (ulong)(100 + i * 10) * 1048576)).ToArray();
        var stats = new PerformanceStatisticsEngine().Compute(samples, PerformanceRuleOptions.Default);
        var process = Assert.Single(stats.Processes);
        Assert.Equal(200UL * 1048576, process.PrivateBytes.Last);
        Assert.Equal(100, process.PrivateBytes.GrowthPercent!.Value, 3);
        Assert.NotNull(process.PrivateBytes.SlopePerMinute);
    }

    [Fact]
    public void TimeBeyondThresholdUsesSampleIntervals()
    {
        var samples = Enumerable.Range(0, 11)
            .Select(i => Sample(i, i < 6 ? 90 : 40)).ToArray();
        var stats = new PerformanceStatisticsEngine().Compute(samples, new PerformanceRuleOptions());
        Assert.Equal(5, stats.CpuTotalPercent.TimeBeyondThresholdSeconds!.Value, 3);
        Assert.Equal(85, stats.CpuTotalPercent.Threshold);
        Assert.True(stats.CpuTotalPercent.ThresholdIsUpperBound);
    }

    [Fact]
    public void LatencyIgnoresIntervalsWithoutIo()
    {
        var samples = new[] { Sample(0, 1, diskLatency: 10), Sample(1, 1, diskLatency: null),
            Sample(2, 1, diskLatency: 30) };
        var stats = new PerformanceStatisticsEngine().Compute(samples, PerformanceRuleOptions.Default);
        var disk = Assert.Single(stats.Storage);
        Assert.Equal(2, disk.ReadLatencyMs.ValidSamples);
        Assert.Equal(30, disk.ReadLatencyMs.Max);
    }

    [Fact]
    public void ThresholdDurationUsesActualTimestampsWhenIntervalsVary()
    {
        var samples = new[]
        {
            PerformanceSamples.Sample(0, PerformanceSamples.Start, cpu: PerformanceSamples.Cpu(total: 90)),
            PerformanceSamples.Sample(1, PerformanceSamples.Start.AddSeconds(1),
                cpu: PerformanceSamples.Cpu(total: 90)),
            PerformanceSamples.Sample(2, PerformanceSamples.Start.AddSeconds(3),
                cpu: PerformanceSamples.Cpu(total: 90)),
            PerformanceSamples.Sample(3, PerformanceSamples.Start.AddSeconds(4),
                cpu: PerformanceSamples.Cpu(total: 90))
        };
        var stats = new PerformanceStatisticsEngine().Compute(samples, PerformanceRuleOptions.Default);
        // Intervalos reais: 1 s + 2 s + 1 s = 4 s (contagem por amostra daria 3 s).
        Assert.Equal(4, stats.CpuTotalPercent.TimeBeyondThresholdSeconds!.Value, 3);
    }

    [Fact]
    public void SlopeUsesActualTimestampsWhenIntervalsVary()
    {
        var samples = Enumerable.Range(0, 5).Select(index => PerformanceSamples.Sample(index,
            PerformanceSamples.Start.AddSeconds(index * 15),
            processes: [PerformanceSamples.Process(privateBytes: (100UL + (ulong)index * 15) * 1048576)]))
            .ToArray();
        var stats = new PerformanceStatisticsEngine().Compute(samples, PerformanceRuleOptions.Default);
        var process = Assert.Single(stats.Processes);
        Assert.Equal(60UL * 1048576, process.PrivateBytes.SlopePerMinute!.Value, 0);
        Assert.Equal(60, process.PrivateBytes.GrowthPercent!.Value, 3);
    }

    [Fact]
    public void ReusedPidKeepsSeparateProcessStatistics()
    {
        var samples = new[]
        {
            PerformanceSamples.Sample(0,
                processes: [PerformanceSamples.Process(identity: "100:1", pid: 100, cpu: 10)]),
            PerformanceSamples.Sample(1,
                processes: [PerformanceSamples.Process(identity: "100:2", pid: 100, cpu: 50)])
        };
        var stats = new PerformanceStatisticsEngine().Compute(samples, PerformanceRuleOptions.Default);
        Assert.Equal(2, stats.Processes.Count);
        Assert.Equal(10, stats.Processes.Single(process => process.Identity == "100:1").CpuPercent.Mean);
        Assert.Equal(50, stats.Processes.Single(process => process.Identity == "100:2").CpuPercent.Mean);
    }

    [Fact]
    public void UnavailableStorageCountersRemainNullInsteadOfZero()
    {
        var disk = PerformanceSamples.Disk() with
        {
            ReadBytesPerSecond = null, WriteBytesPerSecond = null,
            ReadOperationsPerSecond = null, WriteOperationsPerSecond = null,
            ReadLatencyMs = null, WriteLatencyMs = null
        };
        var stats = new PerformanceStatisticsEngine().Compute(
            [PerformanceSamples.Sample(0, storage: [disk])], PerformanceRuleOptions.Default);
        var storage = Assert.Single(stats.Storage);
        Assert.Equal(0, storage.ReadBytesPerSecond.ValidSamples);
        Assert.Equal(1, storage.ReadBytesPerSecond.TotalSamples);
        Assert.Null(storage.ReadBytesPerSecond.Mean);
        Assert.Null(storage.ReadLatencyMs.Mean);
    }

    [Fact]
    public void SingleSampleHasNoTrendOrGrowth()
    {
        var stats = new PerformanceStatisticsEngine().Compute([Sample(0, 10)],
            PerformanceRuleOptions.Default);
        Assert.Null(stats.CpuTotalPercent.GrowthPercent);
        Assert.Null(stats.CpuTotalPercent.SlopePerMinute);
        Assert.Equal(10, stats.CpuTotalPercent.Mean);
    }

    [Fact]
    public void EmptySamplesProduceUnavailableStatisticsNotZero()
    {
        var stats = new PerformanceStatisticsEngine().Compute([], PerformanceRuleOptions.Default);
        Assert.Null(stats.CpuTotalPercent.Mean);
        Assert.Empty(stats.Processes);
        Assert.Empty(stats.Storage);
        Assert.NotEmpty(stats.Limitations);
    }
}
