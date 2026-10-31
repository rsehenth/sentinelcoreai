using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class PerformanceRuleTests
{
    private static PerformanceAnalysis Analyze(IReadOnlyList<PerformanceSample> samples,
        PerformanceRuleOptions? options = null)
    {
        var rules = options ?? new PerformanceRuleOptions();
        var stats = new PerformanceStatisticsEngine().Compute(samples, rules);
        return new PerformanceEvidenceAnalyzer(rules).Analyze(samples, stats);
    }

    private static PerformanceSample CpuSample(int index, double total) =>
        PerformanceSamples.Sample(index, cpu: PerformanceSamples.Cpu(total: total));

    [Fact]
    public void SustainedCpuAboveThresholdIsObservedNotDiagnosed()
    {
        var samples = Enumerable.Range(0, 30).Select(i => CpuSample(i, i < 22 ? 92 : 20)).ToArray();
        var result = Analyze(samples);
        var observation = Assert.Single(result.Observations,
            item => item.RuleId == "PERF-CPU-SUSTAINED");
        Assert.Equal(PerformanceObservationKind.SustainedCpu, observation.Kind);
        Assert.Equal(CausalityStatus.NotAssessed, observation.Causality);
        Assert.Equal(EvidenceSeverity.PotentialImpact, observation.Severity);
        Assert.Equal(22, observation.SupportingSampleCount);
        Assert.Contains("não confirma", observation.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.True(observation.Duration >= TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void IsolatedCpuPeakIsNotSustained()
    {
        var samples = Enumerable.Range(0, 30).Select(i => CpuSample(i, i == 10 ? 99 : 20)).ToArray();
        var result = Analyze(samples);
        Assert.DoesNotContain(result.Observations, item => item.RuleId == "PERF-CPU-SUSTAINED");
        Assert.Contains(result.Notes, note => note.RuleId == "PERF-CPU-SUSTAINED" && note.Evaluated);
    }

    [Fact]
    public void ThresholdsAreConfigurable()
    {
        var samples = Enumerable.Range(0, 30).Select(i => CpuSample(i, 60)).ToArray();
        var result = Analyze(samples, new PerformanceRuleOptions(SustainedCpuPercent: 50));
        Assert.Contains(result.Observations, item => item.RuleId == "PERF-CPU-SUSTAINED");
    }

    [Fact]
    public void LowAvailableMemoryRequiresDurationAndSamples()
    {
        var samples = Enumerable.Range(0, 40).Select(i => PerformanceSamples.Sample(i,
            memory: PerformanceSamples.Memory(available: i < 20 ? 1UL * 1073741824 : 8UL * 1073741824))).ToArray();
        var result = Analyze(samples);
        var observation = Assert.Single(result.Observations,
            item => item.RuleId == "PERF-MEM-LOW-AVAILABLE");
        Assert.Equal(PerformanceObservationKind.LowAvailableMemory, observation.Kind);
        Assert.Contains("disponível", observation.Statement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommitMarginRequiresUsageAndHeadroom()
    {
        var lowHeadroom = PerformanceSamples.Memory(commitLimitBytes: 10UL * 1073741824,
            committedBytes: 9UL * 1073741824);
        var samples = Enumerable.Range(0, 30).Select(i => PerformanceSamples.Sample(i,
            memory: lowHeadroom)).ToArray();
        var result = Analyze(samples);
        Assert.Contains(result.Observations, item => item.RuleId == "PERF-MEM-COMMIT-MARGIN");
    }

    [Fact]
    public void PrivateGrowthIsObservationalAndNeedsBothPercentAndBytes()
    {
        var growing = Enumerable.Range(0, 40).Select(i => PerformanceSamples.Sample(i,
            processes: [PerformanceSamples.Process(privateBytes: (ulong)(100 + i * 4) * 1048576)])).ToArray();
        var result = Analyze(growing);
        var observation = Assert.Single(result.Observations,
            item => item.RuleId == "PERF-PROC-PRIVATE-GROWTH");
        Assert.Equal(EvidenceSeverity.Informational, observation.Severity);
        Assert.Contains("não confirma vazamento", observation.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("100:1", observation.Subject);

        var tiny = Enumerable.Range(0, 40).Select(i => PerformanceSamples.Sample(i,
            processes: [PerformanceSamples.Process(privateBytes: (ulong)(100 + i) * 1048576)])).ToArray();
        Assert.DoesNotContain(Analyze(tiny).Observations,
            item => item.RuleId == "PERF-PROC-PRIVATE-GROWTH");
    }

    [Fact]
    public void DiskLatencyIgnoresIntervalsWithoutIoAndNeedsSustainedSamples()
    {
        var samples = Enumerable.Range(0, 40).Select(i => PerformanceSamples.Sample(i,
            storage: [PerformanceSamples.Disk(readLatency: i < 20 ? 80 : null,
                read: i < 20 ? 5 * 1048576 : 0.0,
                write: 0, writeLatency: i < 20 ? 3 : null)])).ToArray();
        var result = Analyze(samples);
        Assert.Contains(result.Observations, item => item.RuleId == "PERF-STORAGE-LATENCY");
    }

    [Fact]
    public void IoPeakIsNotPresentedAsPersistentBottleneck()
    {
        var samples = Enumerable.Range(0, 60).Select(i => PerformanceSamples.Sample(i,
            storage: [PerformanceSamples.Disk(read: i is >= 30 and < 36 ? 50 * 1048576 : 2 * 1048576)])).ToArray();
        var result = Analyze(samples);
        var observation = Assert.Single(result.Observations, item => item.RuleId == "PERF-STORAGE-IO-PEAK");
        Assert.Equal(EvidenceSeverity.Informational, observation.Severity);
        Assert.Contains("não indica gargalo persistente", observation.Statement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InsufficientSamplesProduceANoteNotAnObservation()
    {
        var result = Analyze([CpuSample(0, 95)]);
        Assert.Empty(result.Observations);
        Assert.All(result.Notes, note => Assert.False(note.Evaluated));
    }
}
