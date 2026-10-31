using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class PerformanceRuleMessageTests
{
    private const ulong GiB = 1073741824UL;

    private static PerformanceAnalysis Analyze(IReadOnlyList<PerformanceSample> samples)
    {
        var rules = PerformanceRuleOptions.Default;
        var stats = new PerformanceStatisticsEngine().Compute(samples, rules);
        return new PerformanceEvidenceAnalyzer(rules).Analyze(samples, stats);
    }

    private static PerformanceSample Commit(int index, bool lowHeadroom, double usage = 95)
    {
        var memory = lowHeadroom
            ? PerformanceSamples.Memory(commitLimitBytes: 10 * GiB, committedBytes: 9 * GiB)
            : PerformanceSamples.Memory(commitLimitBytes: 20 * GiB, committedBytes: 10 * GiB);
        return PerformanceSamples.Sample(index, memory: memory with { CommitUsagePercent = usage });
    }

    private static PerformanceAnalysisNote CommitNote(PerformanceAnalysis analysis) =>
        Assert.Single(analysis.Notes, note => note.RuleId == "PERF-MEM-COMMIT-MARGIN");

    [Fact]
    public void CommitRuleReportsPreconditionNotObservedInsteadOfNoValidSamples()
    {
        var samples = Enumerable.Range(0, 30).Select(index => Commit(index, lowHeadroom: false)).ToArray();
        var note = CommitNote(Analyze(samples));
        Assert.True(note.Evaluated);
        Assert.Equal(PerformanceNoteReason.PreconditionNotObserved, note.Reason);
        Assert.Contains("pré-condição", note.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Amostras válidas insuficientes", note.Detail);
        Assert.DoesNotContain("amostras válidas", note.Detail);
        Assert.Contains("satisfazem a pré-condição", note.Detail);
        Assert.Contains("0 de 30", note.Detail);
    }

    [Fact]
    public void CommitRuleRequiresContiguousLowHeadroomAndUsage()
    {
        var samples = Enumerable.Range(0, 30)
            .Select(index => Commit(index, lowHeadroom: index is < 8 or >= 20)).ToArray();
        var analysis = Analyze(samples);
        Assert.DoesNotContain(analysis.Observations,
            item => item.RuleId == "PERF-MEM-COMMIT-MARGIN");
        Assert.Equal(PerformanceNoteReason.ConditionNotObserved, CommitNote(analysis).Reason);
    }

    [Fact]
    public void CommitRuleReportsUnavailableWhenCommitCountersAreMissing()
    {
        var samples = Enumerable.Range(0, 30).Select(index => PerformanceSamples.Sample(index,
            memory: PerformanceSamples.Memory(commitLimitBytes: null, committedBytes: null))).ToArray();
        var note = CommitNote(Analyze(samples));
        Assert.False(note.Evaluated);
        Assert.Equal(PerformanceNoteReason.Unavailable, note.Reason);
        Assert.Contains("indisponível", note.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0 de 8", note.Detail);
    }

    [Fact]
    public void CommitRuleReportsInsufficientValidSamplesWithRealCounts()
    {
        var samples = Enumerable.Range(0, 4).Select(index => Commit(index, lowHeadroom: true)).ToArray();
        var note = CommitNote(Analyze(samples));
        Assert.False(note.Evaluated);
        Assert.Equal(PerformanceNoteReason.InsufficientSamples, note.Reason);
        Assert.Contains("4 de 8", note.Detail);
    }

    [Fact]
    public void CommitRuleObservesContiguousCondition()
    {
        var samples = Enumerable.Range(0, 20).Select(index => Commit(index, lowHeadroom: true)).ToArray();
        var analysis = Analyze(samples);
        Assert.Single(analysis.Observations, item => item.RuleId == "PERF-MEM-COMMIT-MARGIN");
        Assert.Equal(PerformanceNoteReason.Observed, CommitNote(analysis).Reason);
    }

    [Fact]
    public void CpuRuleSeparatesUnavailableFromInvalidSamples()
    {
        var unavailable = Enumerable.Range(0, 30)
            .Select(index => PerformanceSamples.Sample(index) with { Cpu = null }).ToArray();
        var unavailableNote = Assert.Single(Analyze(unavailable).Notes,
            note => note.RuleId == "PERF-CPU-SUSTAINED");
        Assert.False(unavailableNote.Evaluated);
        Assert.Equal(PerformanceNoteReason.Unavailable, unavailableNote.Reason);

        var invalid = Enumerable.Range(0, 30)
            .Select(index => PerformanceSamples.Sample(index,
                cpu: PerformanceSamples.Cpu(valid: false))).ToArray();
        var invalidNote = Assert.Single(Analyze(invalid).Notes,
            note => note.RuleId == "PERF-CPU-SUSTAINED");
        Assert.False(invalidNote.Evaluated);
        Assert.Equal(PerformanceNoteReason.InvalidSamples, invalidNote.Reason);
        Assert.Contains("inválid", invalidNote.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiskLatencyDoesNotClaimWithinLimitWithoutValidSamples()
    {
        var samples = Enumerable.Range(0, 30).Select(index => PerformanceSamples.Sample(index,
            storage: [PerformanceSamples.Disk(readLatency: null, writeLatency: null) with { Valid = false }]))
            .ToArray();
        var note = Assert.Single(Analyze(samples).Notes, item => item.RuleId == "PERF-STORAGE-LATENCY");
        Assert.DoesNotContain("dentro do limite", note.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(PerformanceNoteReason.Observed, note.Reason);
    }

    [Fact]
    public void IoPeakIgnoresInvalidSamplesAndDoesNotTreatFilteredPointsAsAdjacent()
    {
        var samples = Enumerable.Range(0, 30).Select(index => PerformanceSamples.Sample(index,
            storage: [index is 0 or 10 or 20
                ? (PerformanceSamples.Disk(read: 50 * 1048576, write: 0) with { Valid = false })
                : PerformanceSamples.Disk(read: 1048576, write: 0)])).ToArray();
        var analysis = Analyze(samples);
        Assert.DoesNotContain(analysis.Observations, item => item.RuleId == "PERF-STORAGE-IO-PEAK");
        var note = Assert.Single(analysis.Notes, item => item.RuleId == "PERF-STORAGE-IO-PEAK");
        Assert.Equal(PerformanceNoteReason.ConditionNotObserved, note.Reason);
    }
}
