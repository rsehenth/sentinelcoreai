using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows.Performance;
using Xunit;

namespace SentinelCore.Tests;

public sealed class PerformanceNormalizerTests
{
    private static CpuCounterSample Cpu(ulong idle, ulong kernel, ulong user) => new(idle, kernel, user);

    [Fact]
    public void FirstSampleIsInvalid()
    {
        var reading = PerformanceNormalizers.NormalizeCpu(null, Cpu(0, 0, 0), 1, 8, 2);
        Assert.False(reading.Valid);
        Assert.Equal(0, reading.TotalPercent);
        Assert.Contains(reading.Limitations, l => l.Contains("primeira amostra", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuIsNormalizedToTotalLogicalCapacity()
    {
        // 1 s, 8 CPUs => 8 s de capacidade = 80.000.000 de unidades de 100 ns.
        // 1 s ocupado em um núcleo = 10.000.000 => 12,5% da máquina.
        var previous = Cpu(0, 0, 0);
        var current = Cpu(0, 20_000_000, 10_000_000); // kernel 2 s, idle 0 => kernel ocupado 2 s; user 1 s
        var reading = PerformanceNormalizers.NormalizeCpu(previous, current, 1, 8, 3.5);
        Assert.True(reading.Valid);
        Assert.Equal(37.5, reading.TotalPercent, 3);
        Assert.Equal(12.5, reading.UserPercent, 3);
        Assert.Equal(25.0, reading.KernelPercent, 3);
        Assert.Equal(3.5, reading.ProcessorQueueLength);
        Assert.Equal(8, reading.LogicalProcessors);
    }

    [Fact]
    public void KernelTimeExcludesIdle()
    {
        var previous = Cpu(0, 0, 0);
        var current = Cpu(10_000_000, 10_000_000, 0); // 1 s idle, 1 s kernel, 0 user
        var reading = PerformanceNormalizers.NormalizeCpu(previous, current, 1, 4, null);
        Assert.Equal(0, reading.TotalPercent, 3);
    }

    [Fact]
    public void CpuRatesUseTheCurrentIntervalWhenIntervalsVary()
    {
        var previous = Cpu(0, 0, 0);
        var first = Cpu(0, 2_500_000, 0); // 0,25 s ocupado em 1 s com 4 CPUs.
        Assert.Equal(6.25, PerformanceNormalizers.NormalizeCpu(previous, first, 1, 4, null).TotalPercent, 3);

        var second = Cpu(0, 32_500_000, 0); // +3 s ocupado em 3 s.
        var reading = PerformanceNormalizers.NormalizeCpu(first, second, 3, 4, null);
        Assert.Equal(25, reading.TotalPercent, 3); // Acumulado daria 20,31%.
    }

    [Fact]
    public void ProcessCpuUsesTheCurrentIntervalWhenIntervalsVary()
    {
        var start = DateTimeOffset.Parse("2026-10-30T12:00:00Z");
        var previous = new ProcessCounterSample(10, start, "app", 0, 0, 0, 0, 0, true);
        var current = new ProcessCounterSample(10, start, "app", 10_000_000, 0, 0, 0, 0, true);
        Assert.Equal(6.25,
            PerformanceNormalizers.NormalizeProcess(previous, current, 2, 8, false, new List<string>())
                .CpuPercent, 3);

        var next = new ProcessCounterSample(10, start, "app", 30_000_000, 0, 0, 0, 0, true);
        Assert.Equal(12.5,
            PerformanceNormalizers.NormalizeProcess(current, next, 2, 8, false, new List<string>())
                .CpuPercent, 3);
    }

    [Fact]
    public void CounterRegressionInvalidatesOnlyTheInterval()
    {
        var reading = PerformanceNormalizers.NormalizeCpu(Cpu(50, 50, 50), Cpu(40, 60, 60), 1, 4, null);
        Assert.False(reading.Valid);
        Assert.Contains(reading.Limitations, l => l.Contains("retrocederam"));
    }

    [Fact]
    public void OverflowNearUlongMaximumDoesNotThrow()
    {
        var previous = Cpu(ulong.MaxValue - 10, ulong.MaxValue - 10, ulong.MaxValue - 10);
        var current = Cpu(5, ulong.MaxValue, ulong.MaxValue); // retrocesso aparente
        var reading = PerformanceNormalizers.NormalizeCpu(previous, current, 1, 4, null);
        Assert.False(reading.Valid);
    }

    [Fact]
    public void ValuesAboveCapacityAreScaledAndFlagged()
    {
        var previous = Cpu(0, 0, 0);
        var current = Cpu(0, 40_000_000, 40_000_000); // 8 s de trabalho em 1 s com 4 CPUs
        var reading = PerformanceNormalizers.NormalizeCpu(previous, current, 1, 4, null);
        Assert.Equal(100, reading.TotalPercent, 3);
        Assert.Contains(reading.Limitations, l => l.Contains("capacidade"));
    }

    [Fact]
    public void MemoryConceptsStaySeparated()
    {
        var reading = PerformanceNormalizers.NormalizeMemory(
            installedBytes: 16UL * 1073741824, availableBytes: 4UL * 1073741824,
            commitLimitBytes: 20UL * 1073741824, committedBytes: 10UL * 1073741824,
            pagesPerSecond: 12, systemCacheBytes: 2UL * 1073741824);
        Assert.Equal(12UL * 1073741824, reading.UsedBytes);
        Assert.Equal(25, reading.AvailablePercent!.Value, 3);
        Assert.Equal(50, reading.CommitUsagePercent!.Value, 3);
        Assert.Equal(10UL * 1073741824, reading.CommitHeadroomBytes);
        Assert.True(reading.Valid);
    }

    [Fact]
    public void MissingCommitIsNullNotZero()
    {
        var reading = PerformanceNormalizers.NormalizeMemory(
            16UL * 1073741824, 8UL * 1073741824, null, null, null, null);
        Assert.Null(reading.CommitLimitBytes);
        Assert.Null(reading.CommitUsagePercent);
        Assert.Null(reading.CommitHeadroomBytes);
        Assert.True(reading.Valid);
    }

    [Fact]
    public void ProcessIdentityUsesPidAndStartTime()
    {
        var start = DateTimeOffset.Parse("2026-10-30T12:00:00Z");
        var first = PerformanceNormalizers.ProcessIdentity(123, start);
        var reused = PerformanceNormalizers.ProcessIdentity(123, start.AddMinutes(1));
        Assert.NotEqual(first, reused);
        Assert.StartsWith("123:", first);
    }

    [Fact]
    public void ReusedPidDoesNotMixCounters()
    {
        var start = DateTimeOffset.Parse("2026-10-30T12:00:00Z");
        var previous = new ProcessCounterSample(10, start, "app", 50_000_000, 100, 80, 0, 0, true);
        // Mesmo PID, início posterior: é outro processo.
        var current = new ProcessCounterSample(10, start.AddMinutes(5), "app", 60_000_000, 200, 90, 0, 0, true);
        var limitations = new List<string>();
        var reading = PerformanceNormalizers.NormalizeProcess(previous, current, 1, 8, true, limitations);
        Assert.False(reading.CpuValid);
        Assert.Equal(0, reading.CpuPercent);
        Assert.False(reading.IoValid);
        Assert.True(reading.NewInSession);
        Assert.NotEqual(PerformanceNormalizers.ProcessIdentity(previous.Pid, previous.StartTimeUtc), reading.Identity);
    }

    [Fact]
    public void FirstObservationOfAProcessHasNoRate()
    {
        var current = new ProcessCounterSample(10, PerformanceSamples.Start, "app", 50_000_000, 100, 80, 10, 20, true);
        var reading = PerformanceNormalizers.NormalizeProcess(null, current, 1, 8, true, new List<string>());
        Assert.False(reading.CpuValid);
        Assert.Equal(0, reading.CpuPercent);
        Assert.Equal(100UL, reading.WorkingSetBytes);
        Assert.True(reading.NewInSession);
    }

    [Fact]
    public void ProcessCpuIsAFractionOfTotalCapacity()
    {
        var start = DateTimeOffset.Parse("2026-10-30T12:00:00Z");
        var previous = new ProcessCounterSample(10, start, "app", 0, 0, 0, 0, 0, true);
        // 2 s de CPU em 1 s com 8 núcleos => 25% da máquina.
        var current = new ProcessCounterSample(10, start, "app", 20_000_000, 0, 0, 0, 0, true);
        var reading = PerformanceNormalizers.NormalizeProcess(previous, current, 1, 8, false, new List<string>());
        Assert.True(reading.CpuValid);
        Assert.Equal(25, reading.CpuPercent, 3);
    }

    [Fact]
    public void ProcessCounterResetInvalidatesOnlyThatInterval()
    {
        var start = DateTimeOffset.Parse("2026-10-30T12:00:00Z");
        var previous = new ProcessCounterSample(10, start, "app", 50_000_000, 0, 0, 0, 0, true);
        var current = new ProcessCounterSample(10, start, "app", 10_000_000, 0, 0, 0, 0, true);
        var limitations = new List<string>();
        var reading = PerformanceNormalizers.NormalizeProcess(previous, current, 1, 8, false, limitations);
        Assert.False(reading.CpuValid);
        Assert.Contains(limitations, l => l.Contains("retrocedeu"));
    }

    [Fact]
    public void UnavailableIoDoesNotBecomeZeroLatencyOrZeroRate()
    {
        var start = DateTimeOffset.Parse("2026-10-30T12:00:00Z");
        var previous = new ProcessCounterSample(10, start, "app", 0, 0, 0, 0, 0, false);
        var current = new ProcessCounterSample(10, start, "app", 10_000_000, 0, 0, 0, 0, false);
        var limitations = new List<string>();
        var reading = PerformanceNormalizers.NormalizeProcess(previous, current, 1, 8, false, limitations);
        Assert.True(reading.CpuValid);
        Assert.False(reading.IoValid);
        Assert.Equal(0, reading.IoReadBytesPerSecond);
        Assert.Contains(limitations, l => l.Contains("I/O"));
    }

    [Fact]
    public void SameNameProcessesRemainDistinctIdentities()
    {
        var first = new ProcessCounterSample(10, PerformanceSamples.Start, "chrome", 0, 0, 0, 0, 0, true);
        var second = new ProcessCounterSample(11, PerformanceSamples.Start, "chrome", 0, 0, 0, 0, 0, true);
        Assert.NotEqual(PerformanceNormalizers.ProcessIdentity(first.Pid, first.StartTimeUtc),
            PerformanceNormalizers.ProcessIdentity(second.Pid, second.StartTimeUtc));
    }

    [Fact]
    public void IoRatesUseTheMonotonicInterval()
    {
        var start = DateTimeOffset.Parse("2026-10-30T12:00:00Z");
        var previous = new ProcessCounterSample(10, start, "app", 0, 0, 0, 1_000, 2_000, true);
        var current = new ProcessCounterSample(10, start, "app", 10_000_000, 0, 0, 3_000, 2_000, true);
        var reading = PerformanceNormalizers.NormalizeProcess(previous, current, 2, 8, false, new List<string>());
        Assert.True(reading.IoValid);
        Assert.Equal(1_000, reading.IoReadBytesPerSecond, 3);
        Assert.Equal(0, reading.IoWriteBytesPerSecond, 3);
    }

    [Theory]
    [InlineData("0 D:", 0)]
    [InlineData("3 C:", 3)]
    [InlineData("_Total", null)]
    [InlineData("HarddiskVolume3", null)]
    public void DiskNumberIsParsedOnlyFromTheLeadingInteger(string instance, int? expected) =>
        Assert.Equal(expected, PerformanceNormalizers.ParseDiskNumber(instance));

    [Fact]
    public void ZeroIoMeansLatencyUnavailable()
    {
        Assert.Null(PerformanceNormalizers.LatencyMilliseconds(0.050, 0));
        Assert.Null(PerformanceNormalizers.LatencyMilliseconds(0.050, -1));
        Assert.Equal(50, PerformanceNormalizers.LatencyMilliseconds(0.050, 10)!.Value, 6);
        Assert.Null(PerformanceNormalizers.LatencyMilliseconds(null, 10));
    }

    [Fact]
    public void PhysicalAndVolumeInstancesStaySeparated()
    {
        var physical = PerformanceNormalizers.NormalizeStorage(StorageScope.Physical, "0 C:", 0, null,
            100, 200, 10, 20, 0.010, 0.020, 0.5, 33, "disk-stable", true);
        var volume = PerformanceNormalizers.NormalizeStorage(StorageScope.Volume, "C:", null, "C:",
            100, 200, 10, 20, 0.010, 0.020, 0.5, 33, "volume-stable", true);
        Assert.Equal("physical:0", physical.Id);
        Assert.Equal("volume:C", volume.Id);
        Assert.Equal("C", volume.VolumeLetter);
        Assert.Equal(10, physical.ReadLatencyMs!.Value, 6);
        Assert.Equal(20, physical.WriteLatencyMs!.Value, 6);
    }

    [Fact]
    public void HardwareIdentityMapUsesDiskNumberAndDriveLetter()
    {
        var snapshot = HardwareSamples.Snapshot(
            HardwareSamples.Result(HardwareCategory.Storage, HardwareCollectionStatus.Completed,
                HardwareSamples.Item("SSD", HardwareCategory.Storage, kind: "disk", stableId: "disk-0",
                    properties: [new HardwareProperty("disk_number", "0")]),
                HardwareSamples.Item("Volume C", HardwareCategory.Storage, kind: "volume", stableId: "vol-c",
                    properties: [new HardwareProperty("drive_letter", "C")])));
        var map = HardwareStorageIdentityMap.From(snapshot);
        Assert.Equal("disk-0", map.ForPhysical(0, "0 C:"));
        Assert.Equal("vol-c", map.ForVolume("C:", "C:"));
        Assert.Null(map.ForPhysical(9, "9 X:"));
    }

    [Fact]
    public async Task RealCollectorsReturnTicksWithoutThrowing()
    {
        var collectors = WindowsPerformanceCollectors.Create();
        Assert.Equal(4, collectors.Count);
        Assert.Equal(4, collectors.Select(c => c.Id).Distinct().Count());
        var context = new PerformanceCollectionContext(Guid.NewGuid(), null, null);
        foreach (var collector in collectors)
        {
            using var session = collector.StartSession(context);
            var tick = await session.ReadAsync(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1),
                CancellationToken.None);
            Assert.Equal(collector.Id, tick.CollectorId);
            Assert.NotNull(tick.Limitations);
            if (collector.Category == PerformanceSourceCategory.Cpu)
                Assert.NotNull(tick.Cpu);
            if (collector.Category == PerformanceSourceCategory.Memory)
                Assert.NotNull(tick.Memory);
        }
    }
}
