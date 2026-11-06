using Microsoft.Data.Sqlite;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows.Performance;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class QualityGateRemediationTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "SentinelCoreQGTests", Guid.NewGuid().ToString("N"));

    private MemoryStore NewStore() => new(testRoot);

    public void Dispose()
    {
        if (Directory.Exists(testRoot))
        {
            try { Directory.Delete(testRoot, recursive: true); } catch { }
        }
    }

    #region QG-01 & QG-04: Continuidade Temporal e Tempo Monotônico

    [Fact]
    public void QG01_CpuGapInMiddleDoesNotFabricateSustainedObservation()
    {
        // t=0..9 at 95% (10s), t=10..19 missing/gap, t=20..29 at 95% (10s).
        // Neither segment reaches 20s minimum. The analyzer MUST NOT bridge the gap.
        var samples = new List<PerformanceSample>();
        for (var i = 0; i <= 9; i++)
            samples.Add(PerformanceSamples.Sample(i, cpu: PerformanceSamples.Cpu(total: 95)));
        // Gap of 10 seconds: next sample is at index 20, elapsed 20s
        for (var i = 20; i <= 29; i++)
            samples.Add(PerformanceSamples.Sample(i, cpu: PerformanceSamples.Cpu(total: 95)));

        var rules = new PerformanceRuleOptions(SustainedCpuPercent: 85, SustainedCpuMinimumSamples: 10, SustainedCpuMinimumSeconds: 20);
        var stats = new PerformanceStatisticsEngine().Compute(samples, rules);
        var analysis = new PerformanceEvidenceAnalyzer(rules).Analyze(samples, stats);

        Assert.DoesNotContain(analysis.Observations, o => o.RuleId == "PERF-CPU-SUSTAINED");
        Assert.True(stats.CpuTotalPercent.TimeBeyondThresholdSeconds == null || stats.CpuTotalPercent.TimeBeyondThresholdSeconds < 20,
            $"TimeBeyondThresholdSeconds was {stats.CpuTotalPercent.TimeBeyondThresholdSeconds}, should not bridge the gap to 29s");
    }

    [Fact]
    public void QG01_14Scenarios_ContinuousValidSamplesAreSustained()
    {
        // Scenario 1: Continuous valid samples (30 samples, 0..29, 95% CPU)
        var samples = Enumerable.Range(0, 30)
            .Select(i => PerformanceSamples.Sample(i, cpu: PerformanceSamples.Cpu(total: 95)))
            .ToArray();
        var rules = PerformanceRuleOptions.Default;
        var stats = new PerformanceStatisticsEngine().Compute(samples, rules);
        var analysis = new PerformanceEvidenceAnalyzer(rules).Analyze(samples, stats);

        var obs = Assert.Single(analysis.Observations, o => o.RuleId == "PERF-CPU-SUSTAINED");
        Assert.True(obs.Duration >= TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void QG01_14Scenarios_GapAtBeginningDoesNotBreakSubsequentSustainedRun()
    {
        // Scenario 2: Gap at beginning: t=0..4 missing, t=5..30 valid 95% (26 samples, 25s)
        var samples = Enumerable.Range(5, 26)
            .Select(i => PerformanceSamples.Sample(i, cpu: PerformanceSamples.Cpu(total: 95)))
            .ToArray();
        var rules = PerformanceRuleOptions.Default;
        var stats = new PerformanceStatisticsEngine().Compute(samples, rules);
        var analysis = new PerformanceEvidenceAnalyzer(rules).Analyze(samples, stats);

        var obs = Assert.Single(analysis.Observations, o => o.RuleId == "PERF-CPU-SUSTAINED");
        Assert.True(obs.Duration >= TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void QG01_14Scenarios_GapAtEndDoesNotPreventPriorSustainedRun()
    {
        // Scenario 4: Gap at end: t=0..24 valid 95% (25 samples, 24s), t=25..35 missing
        var samples = Enumerable.Range(0, 25)
            .Select(i => PerformanceSamples.Sample(i, cpu: PerformanceSamples.Cpu(total: 95)))
            .ToArray();
        var rules = PerformanceRuleOptions.Default;
        var stats = new PerformanceStatisticsEngine().Compute(samples, rules);
        var analysis = new PerformanceEvidenceAnalyzer(rules).Analyze(samples, stats);

        var obs = Assert.Single(analysis.Observations, o => o.RuleId == "PERF-CPU-SUSTAINED");
        Assert.True(obs.Duration >= TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void QG01_14Scenarios_MultipleGapsSeparateShortBursts()
    {
        // Scenario 5: Multiple gaps: t=0..5 (95%), gap, t=10..15 (95%), gap, t=20..25 (95%)
        var samples = new List<PerformanceSample>();
        for (var i = 0; i <= 5; i++) samples.Add(PerformanceSamples.Sample(i, cpu: PerformanceSamples.Cpu(total: 95)));
        for (var i = 10; i <= 15; i++) samples.Add(PerformanceSamples.Sample(i, cpu: PerformanceSamples.Cpu(total: 95)));
        for (var i = 20; i <= 25; i++) samples.Add(PerformanceSamples.Sample(i, cpu: PerformanceSamples.Cpu(total: 95)));

        var rules = PerformanceRuleOptions.Default;
        var stats = new PerformanceStatisticsEngine().Compute(samples, rules);
        var analysis = new PerformanceEvidenceAnalyzer(rules).Analyze(samples, stats);

        Assert.DoesNotContain(analysis.Observations, o => o.RuleId == "PERF-CPU-SUSTAINED");
    }

    [Fact]
    public void QG01_14Scenarios_RepeatedOrOutOfOrderTimestampsAreNotContinuous()
    {
        // Scenario 6 & 7: Repeated or out-of-order elapsed timestamps
        var samples = new List<PerformanceSample>
        {
            new(0, PerformanceSamples.Start, 0.0, false, PerformanceSamples.Cpu(95), PerformanceSamples.Memory(), [], [], [], 40, []),
            new(1, PerformanceSamples.Start.AddSeconds(1), 1.0, false, PerformanceSamples.Cpu(95), PerformanceSamples.Memory(), [], [], [], 40, []),
            new(2, PerformanceSamples.Start.AddSeconds(2), 1.0, false, PerformanceSamples.Cpu(95), PerformanceSamples.Memory(), [], [], [], 40, []), // Repeated elapsed
            new(3, PerformanceSamples.Start.AddSeconds(3), 0.5, false, PerformanceSamples.Cpu(95), PerformanceSamples.Memory(), [], [], [], 40, [])  // Out of order elapsed
        };

        var rules = new PerformanceRuleOptions(SustainedCpuMinimumSamples: 3, SustainedCpuMinimumSeconds: 2);
        var stats = new PerformanceStatisticsEngine().Compute(samples, rules);
        var analysis = new PerformanceEvidenceAnalyzer(rules).Analyze(samples, stats);

        Assert.DoesNotContain(analysis.Observations, o => o.RuleId == "PERF-CPU-SUSTAINED");
    }

    [Fact]
    public void QG01_14Scenarios_IrregularIntervalsBeyondToleranceBreakContinuity()
    {
        // Scenario 8: Irregular intervals: index 0 (0s), index 1 (5s), index 2 (10s), index 3 (15s)
        // With nominal interval of 1s, 5s jumps are gaps, not continuous
        var samples = new List<PerformanceSample>();
        for (var i = 0; i < 10; i++)
        {
            var elapsed = i * 5.0;
            samples.Add(new(i, PerformanceSamples.Start.AddSeconds(elapsed), elapsed, false,
                PerformanceSamples.Cpu(95), PerformanceSamples.Memory(), [], [], [], 40, []));
        }

        var rules = new PerformanceRuleOptions(SustainedCpuMinimumSamples: 5, SustainedCpuMinimumSeconds: 20);
        var stats = new PerformanceStatisticsEngine().Compute(samples, rules);
        var analysis = new PerformanceEvidenceAnalyzer(rules).Analyze(samples, stats);

        Assert.DoesNotContain(analysis.Observations, o => o.RuleId == "PERF-CPU-SUSTAINED");
    }

    [Fact]
    public void QG01_14Scenarios_CollectorTemporarilyUnavailableAndRecovered()
    {
        // Scenario 9 & 10: Collector unavailable (null/invalid) in middle, then recovered
        var samples = new List<PerformanceSample>();
        for (var i = 0; i < 10; i++)
            samples.Add(PerformanceSamples.Sample(i, cpu: PerformanceSamples.Cpu(total: 95)));
        // 5 ticks of null CPU (collector unavailable)
        for (var i = 10; i < 15; i++)
            samples.Add(PerformanceSamples.Sample(i, cpu: null));
        // 10 ticks recovered
        for (var i = 15; i < 25; i++)
            samples.Add(PerformanceSamples.Sample(i, cpu: PerformanceSamples.Cpu(total: 95)));

        var rules = PerformanceRuleOptions.Default; // requires 20s, 10 samples
        var stats = new PerformanceStatisticsEngine().Compute(samples, rules);
        var analysis = new PerformanceEvidenceAnalyzer(rules).Analyze(samples, stats);

        Assert.DoesNotContain(analysis.Observations, o => o.RuleId == "PERF-CPU-SUSTAINED");
    }

    [Fact]
    public void QG01_14Scenarios_ConditionTrueBeforeAndAfterLongGapIsNotSustained()
    {
        // Scenario 11 & 12: True before (12s) and after (12s) long gap of 60s
        var samples = new List<PerformanceSample>();
        for (var i = 0; i <= 12; i++)
            samples.Add(new(i, PerformanceSamples.Start.AddSeconds(i), i, false, PerformanceSamples.Cpu(95), PerformanceSamples.Memory(), [], [], [], 40, []));
        for (var i = 0; i <= 12; i++)
        {
            var idx = 13 + i;
            var elapsed = 75.0 + i;
            samples.Add(new(idx, PerformanceSamples.Start.AddSeconds(elapsed), elapsed, false, PerformanceSamples.Cpu(95), PerformanceSamples.Memory(), [], [], [], 40, []));
        }

        var rules = PerformanceRuleOptions.Default;
        var stats = new PerformanceStatisticsEngine().Compute(samples, rules);
        var analysis = new PerformanceEvidenceAnalyzer(rules).Analyze(samples, stats);

        Assert.DoesNotContain(analysis.Observations, o => o.RuleId == "PERF-CPU-SUSTAINED");
    }

    [Fact]
    public void QG01_14Scenarios_ProcessTerminatedOrPidReusedBreaksContinuity()
    {
        // Scenario 13 & 14: Process private memory growth interrupted by process termination or PID reuse
        // Process A (pid 100, StartTime 1) runs from index 0..8, then exits.
        // Process B (pid 100, StartTime 2) runs from index 9..20.
        // Even though both have PID 100, they have different identities! Growth must not bridge across them.
        var samples = new List<PerformanceSample>();
        for (var i = 0; i <= 8; i++)
        {
            var procA = new ProcessReading("100:1000", 100, "worker", PerformanceSamples.Start, 10,
                (ulong)(100 + i * 10) * 1048576, (ulong)(100 + i * 10) * 1048576, 0, 0, true, true, false);
            samples.Add(PerformanceSamples.Sample(i, processes: [procA]));
        }
        for (var i = 9; i <= 25; i++)
        {
            var procB = new ProcessReading("100:2000", 100, "worker", PerformanceSamples.Start.AddSeconds(9), 10,
                (ulong)(100 + (i - 9) * 10) * 1048576, (ulong)(100 + (i - 9) * 10) * 1048576, 0, 0, true, true, false);
            samples.Add(PerformanceSamples.Sample(i, processes: [procB]));
        }

        var rules = new PerformanceRuleOptions(PrivateGrowthMinimumSamples: 10, PrivateGrowthMinimumSeconds: 20);
        var stats = new PerformanceStatisticsEngine().Compute(samples, rules);
        var analysis = new PerformanceEvidenceAnalyzer(rules).Analyze(samples, stats);

        Assert.DoesNotContain(analysis.Observations, o => o.RuleId == "PERF-PROC-PRIVATE-GROWTH");
    }

    [Fact]
    public void QG04_UtcJumpDoesNotDistortDurationOrSlopeOrBeyondThreshold()
    {
        // 10 samples with 1s monotonic interval (elapsed = 0..9s), but with synthetic +60s jump in TimestampUtc at sample 5
        var samples = new List<PerformanceSample>();
        for (var i = 0; i < 10; i++)
        {
            var utc = i < 5 ? PerformanceSamples.Start.AddSeconds(i) : PerformanceSamples.Start.AddSeconds(i + 60);
            samples.Add(new(i, utc, i * 1.0, false, PerformanceSamples.Cpu(95), PerformanceSamples.Memory(), [], [], [], 40, []));
        }

        var rules = new PerformanceRuleOptions(SustainedCpuMinimumSamples: 8, SustainedCpuMinimumSeconds: 15);
        var stats = new PerformanceStatisticsEngine().Compute(samples, rules);
        var analysis = new PerformanceEvidenceAnalyzer(rules).Analyze(samples, stats);

        // 9s monotonic span is NOT >= 15s. With civil clock jump, it previously became 69s!
        Assert.DoesNotContain(analysis.Observations, o => o.RuleId == "PERF-CPU-SUSTAINED");
        // TimeBeyondThresholdSeconds must reflect monotonic seconds (9s), not 69s!
        Assert.True(stats.CpuTotalPercent.TimeBeyondThresholdSeconds == null || stats.CpuTotalPercent.TimeBeyondThresholdSeconds <= 10,
            $"TimeBeyondThresholdSeconds was {stats.CpuTotalPercent.TimeBeyondThresholdSeconds}, civil jump distorted it!");
    }

    #endregion

    #region QG-03: Latência de Gravação e Leitura de Armazenamento

    [Fact]
    public void QG03_HighWriteLatencyWithLowReadLatencyGeneratesWriteObservation()
    {
        // 30 samples: Read latency = 2ms, Write latency = 1000ms
        var samples = Enumerable.Range(0, 30).Select(i => PerformanceSamples.Sample(i,
            storage: [PerformanceSamples.Disk(readLatency: 2, writeLatency: 1000, read: 1048576, write: 1048576)])).ToArray();

        var rules = new PerformanceRuleOptions(DiskLatencyMilliseconds: 50, DiskLatencyMinimumSamples: 8, DiskLatencyMinimumSeconds: 15);
        var stats = new PerformanceStatisticsEngine().Compute(samples, rules);
        var analysis = new PerformanceEvidenceAnalyzer(rules).Analyze(samples, stats);

        var obs = Assert.Single(analysis.Observations, o => o.RuleId == "PERF-STORAGE-LATENCY");
        Assert.Contains("gravação", obs.Title + " " + obs.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.True(obs.ObservedMean >= 900);
    }

    [Fact]
    public async Task QG03_BothReadAndWriteHighLatencyGenerateSeparateObservations()
    {
        // 30 samples: Read latency = 150ms, Write latency = 200ms
        var samples = Enumerable.Range(0, 30).Select(i => PerformanceSamples.Sample(i,
            storage: [PerformanceSamples.Disk(readLatency: 150, writeLatency: 200, read: 1048576, write: 1048576)])).ToArray();

        var rules = new PerformanceRuleOptions(DiskLatencyMilliseconds: 50, DiskLatencyMinimumSamples: 8, DiskLatencyMinimumSeconds: 15);
        var stats = new PerformanceStatisticsEngine().Compute(samples, rules);
        var analysis = new PerformanceEvidenceAnalyzer(rules).Analyze(samples, stats);

        var diskObs = analysis.Observations.Where(o => o.RuleId == "PERF-STORAGE-LATENCY").ToArray();
        Assert.Equal(2, diskObs.Length);
        Assert.NotEqual(diskObs[0].Id, diskObs[1].Id);
        Assert.Contains(diskObs, o => o.Statement.Contains("leitura", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diskObs, o => o.Statement.Contains("gravação", StringComparison.OrdinalIgnoreCase));

        // Verify that persisting a session with both observations succeeds in SQLite without UNIQUE constraint error
        var tempDir = Path.Combine(Path.GetTempPath(), "sentinel_qg03_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MemoryStore(tempDir);
            var session = PerformanceSamples.Session() with { Observations = diskObs };
            await store.SaveSessionAsync(session);

            var loaded = await store.LoadSessionsAsync();
            Assert.Single(loaded.Sessions);
            Assert.Equal(2, loaded.Sessions[0].Observations.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    #endregion

    #region QG-02 & QG-05: Cobertura Completa e Agendamento do Runner

    [Fact]
    public async Task QG02_SourceWithEmptyReadsProducesFailedOrPartialAndNeverCompleteCoverage()
    {
        // A collector that returns Empty for every tick
        var emptyCpu = new FakeCollector("performance-cpu", PerformanceSourceCategory.Cpu,
            _ => PerformanceCollectorTick.Empty("performance-cpu", DateTimeOffset.UtcNow, "API falhou"));
        var runner = new PerformanceSessionRunner([emptyCpu, CreateMemoryCollector()]);
        var session = await runner.RunAsync(new PerformanceSessionRequest(PerformanceSessionPreset.Custom,
            TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100)));

        Assert.Equal(PerformanceSessionStatus.Completed, session.Status);
        var source = Assert.Single(session.Sources, s => s.CollectorId == "performance-cpu");
        Assert.Equal(PerformanceSourceStatus.Failed, source.Status);
        Assert.NotNull(session.Coverage);
        Assert.NotEqual(PerformanceCoverageStatus.Complete, session.Coverage.Status);
    }

    private static FakeCollector CreateMemoryCollector() => new("performance-memory",
        PerformanceSourceCategory.Memory, _ => new("performance-memory", DateTimeOffset.UtcNow, false,
            null, PerformanceSamples.Memory(), [], [], [], []));

    [Fact]
    public async Task QG02_IntermittentSourceExceptionProducesPartialCoverageNeverComplete()
    {
        // Collector fails on tick 1, succeeds on other ticks
        var intermittentCpu = new FakeCollector("performance-cpu", PerformanceSourceCategory.Cpu,
            index => index == 1
                ? throw new InvalidOperationException("falha intermitente")
                : new("performance-cpu", DateTimeOffset.UtcNow, false, PerformanceSamples.Cpu(40, 25, 15), null, [], [], [], []));
        var runner = new PerformanceSessionRunner([intermittentCpu]);
        var session = await runner.RunAsync(new PerformanceSessionRequest(PerformanceSessionPreset.Custom,
            TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(100)));

        var source = Assert.Single(session.Sources);
        Assert.Equal(PerformanceSourceStatus.Partial, source.Status);
        Assert.NotNull(session.Coverage);
        Assert.Equal(PerformanceCoverageStatus.Partial, session.Coverage.Status);
    }

    [Fact]
    public async Task QG05_ModerateDelayAdvancesGridAndRecordsMissedOpportunityWithoutBurst()
    {
        // First tick takes 150ms on a 100ms interval (1.5x interval delay)
        var runner = new PerformanceSessionRunner([new SlowFirstTickCollector(TimeSpan.FromMilliseconds(150))]);
        var session = await runner.RunAsync(new PerformanceSessionRequest(PerformanceSessionPreset.Custom,
            TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(100)));

        Assert.NotNull(session.Coverage);
        Assert.True(session.Coverage.MissedOpportunities > 0,
            $"Expected missed opportunities > 0 for 1.5x interval delay, got {session.Coverage.MissedOpportunities}");
        Assert.Equal(PerformanceCoverageStatus.Partial, session.Coverage.Status);

        // Subsequent samples must not burst with interval < 50ms
        for (var i = 2; i < session.Samples.Count; i++)
        {
            var gapMs = (session.Samples[i].ElapsedSeconds - session.Samples[i - 1].ElapsedSeconds) * 1000;
            Assert.True(gapMs >= 50, $"Accelerated burst observed: interval between sample {i-1} and {i} was only {gapMs:F1}ms");
        }
    }

    #endregion

    #region QG-06, QG-07, QG-08, QG-09: Infraestrutura Windows

    [Fact]
    public void QG06_ProcessCollectorEmitsLimitationWhenEligibleCountExceedsLimitEvenWithOverlappingRankings()
    {
        // 50 eligible processes, all having cpu=10, mem=100MB.
        // The top 10 CPU and top 10 memory completely overlap.
        // Union count is 10 <= 20. But 30-40 processes were omitted!
        // The collector MUST report limitation and count.
        var readings = Enumerable.Range(0, 50).Select(i =>
            PerformanceSamples.Process($"proc-{i}:1", 1000 + i, $"proc-{i}", 10, 100UL * 1048576)).ToList();
        var limitations = new List<string>();

        var selected = ProcessPerformanceCollector.SelectInternal(readings, limitations, limit: 20);

        Assert.Contains(limitations, l => l.StartsWith("Limite de 20 processos acompanhados", StringComparison.Ordinal));
        Assert.True(selected.Count <= 20);
        Assert.Contains("50 processos elegíveis", limitations[0]);
    }

    [Fact]
    public void QG07_CpuCollectorResetsBaselineOnFailureSoRecoveryDoesNotInflateDelta()
    {
        // When previous is null (after failure), NormalizeCpu treats current as baseline, not inflated delta
        var reading = PerformanceNormalizers.NormalizeCpu(null, new CpuCounterSample(100, 200, 300), 1.0, 8, null);
        Assert.False(reading.Valid);
        Assert.Equal(0, reading.TotalPercent);
        Assert.Contains(reading.Limitations, l => l.Contains("Primeira amostra usada como base", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task QG08_MultiGroupTopologyAbove64CpusReportedAsUnavailableWithLimitation()
    {
        // Injecting topology with 128 logical processors across 2 processor groups
        var collector = new CpuPerformanceCollector(forcedProcessors: 128, forcedGroupCount: 2);
        var session = collector.StartSession(new PerformanceCollectionContext(Guid.NewGuid(), null, null));
        var tick = await session.ReadAsync(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.NotNull(tick.Cpu);
        Assert.False(tick.Cpu.Valid);
        Assert.Contains(tick.Limitations, l => l.Contains("> 64 CPUs") || l.Contains("múltiplos grupos"));
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public void QG09_ProcessIoCountersDoNotUseProcessHandle()
    {
        var currentPid = Environment.ProcessId;
        var success = NativeSystem.TryGetProcessIoCounters(currentPid, out var read, out var write);
        Assert.True(success, "Querying process I/O counters with limited query access must succeed for current process");
    }

    #endregion

    #region QG-10 & QG-11: Persistência e Integridade SQLite/Markdown

    [Fact]
    public async Task QG10_StructuralCorruptionWithValidChecksumDoesNotBlockHistoryOrNewSaves()
    {
        var store = NewStore();
        // 1. Save a valid session
        var validSession = PerformanceSamples.Session();
        await store.SaveSessionAsync(validSession);

        // 2. Craft a second record with valid envelope and checksum, but structurally invalid JSON (CpuTotalPercent = null)
        var invalidId = Guid.NewGuid();
        var invalidJson = $$"""
        {
          "FormatVersion": 2,
          "Id": "{{invalidId}}",
          "SoftwareVersion": "0.3.0-preview.3",
          "SchemaVersion": "5",
          "Preset": "Seconds30",
          "RequestedDuration": "00:00:30",
          "SampleInterval": "00:00:01",
          "StartedUtc": "2026-10-31T12:00:00Z",
          "CompletedUtc": "2026-10-31T12:00:30Z",
          "Duration": "00:00:30",
          "Status": "Completed",
          "Sources": [
            {
              "CollectorId": "performance-cpu",
              "CollectorVersion": "1.0",
              "Category": "Cpu",
              "Source": "Test",
              "Status": "Completed",
              "StartedUtc": "2026-10-31T12:00:00Z",
              "CompletedUtc": "2026-10-31T12:00:30Z",
              "Duration": "00:00:30",
              "SamplesRead": 30,
              "LimitHit": false,
              "Limitations": []
            }
          ],
          "Statistics": {
            "Duration": "00:00:30",
            "TotalSamples": 0,
            "ValidCpuSamples": 0,
            "ValidMemorySamples": 0,
            "CpuTotalPercent": null,
            "CpuUserPercent": null,
            "CpuKernelPercent": null,
            "ProcessorQueueLength": null,
            "MemoryAvailablePercent": null,
            "MemoryUsedBytes": null,
            "CommitUsagePercent": null,
            "PagesPerSecond": null,
            "Processes": [],
            "Storage": [],
            "Limitations": []
          },
          "Observations": [],
          "AnalysisNotes": [],
          "Samples": [],
          "SelfOverhead": { "Samples": 0, "CpuPercentMean": 0, "CpuPercentMax": 0, "WorkingSetBytesMax": 0, "PrivateBytesMax": 0, "CollectionDurationMsMean": 0, "CollectionDurationMsMax": 0 },
          "Limitations": []
        }
        """;
        var dir = Path.Combine(testRoot, "memory", "performance");
        Directory.CreateDirectory(dir);
        var envelope = RecordEnvelope.Wrap("# Relatório Corrompido\n", invalidJson);
        File.WriteAllText(Path.Combine(dir, $"{invalidId:D}.md"), envelope);

        // 3. Load sessions: must NOT throw NullReferenceException!
        // Must load the valid session, exclude the corrupted one with a warning, and keep the file intact.
        var loaded = await store.LoadSessionsAsync();
        Assert.Single(loaded.Sessions, s => s.Id == validSession.Id);
        Assert.Contains(loaded.Warnings, w => w.Contains(invalidId.ToString("D")));
        Assert.True(File.Exists(Path.Combine(dir, $"{invalidId:D}.md")));

        // 4. Saving another new valid session must succeed!
        var thirdSession = PerformanceSamples.Session() with { Id = Guid.NewGuid() };
        var bytes = await store.SaveSessionAsync(thirdSession);
        Assert.True(bytes > 0);
    }

    [Fact]
    public async Task QG11_PayloadHashInSqliteMatchesOriginalEnvelopeChecksum()
    {
        var store = NewStore();
        // Create a Format 1 legacy record envelope with known content and hash
        var legacyId = Guid.NewGuid();
        var legacySession = PerformanceSamples.Session() with { Id = legacyId, FormatVersion = 1, Coverage = null };
        var json = CanonicalJson(legacySession);
        var expectedHash = RecordEnvelope.Hash(json);
        var envelope = RecordEnvelope.Wrap("# Relatório Legado\n", json);

        var dir = Path.Combine(testRoot, "memory", "performance");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"{legacyId:D}.md");
        RecordEnvelope.AtomicWrite(filePath, envelope, overwrite: false);

        // Load sessions to trigger reconciliation & SQLite index creation
        var loaded = await store.LoadSessionsAsync();
        Assert.True(loaded.Sessions.Count > 0, $"Sessions was empty! Warnings: {string.Join("; ", loaded.Warnings)}");
        Assert.Single(loaded.Sessions, s => s.Id == legacyId);

        // Query SQLite to verify PayloadHash matches expectedHash
        using var db = new SqliteConnection($"Data Source={Path.Combine(testRoot, "data", "sentinelcore.db")};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT PayloadHash FROM PerformanceSessions WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", legacyId.ToString("D"));
        var indexedHash = (string?)cmd.ExecuteScalar();

        Assert.Equal(expectedHash, indexedHash);
    }

    #endregion

    private static string CanonicalJson(object value) =>
        System.Text.Json.JsonSerializer.Serialize(value, AuditJson.Options).Replace("\r\n", "\n");

    private sealed class FakeCollector(string id, PerformanceSourceCategory category,
        Func<int, PerformanceCollectorTick> tick) : IPerformanceCollector
    {
        public int Reads;
        public string Id => id;
        public string Version => "1.0";
        public string Source => "Fake: testes";
        public PerformanceSourceCategory Category => category;
        public IPerformanceCollectorSession StartSession(PerformanceCollectionContext context) => new Session(this, tick);
        private sealed class Session(FakeCollector owner, Func<int, PerformanceCollectorTick> tick)
            : IPerformanceCollectorSession
        {
            public Task<PerformanceCollectorTick> ReadAsync(DateTimeOffset at, TimeSpan elapsed,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var index = owner.Reads++;
                return Task.FromResult(tick(index));
            }
            public void Dispose() { }
        }
    }

    private sealed class SlowFirstTickCollector(TimeSpan firstDelay) : IPerformanceCollector
    {
        public string Id => "performance-cpu";
        public string Version => "1.0";
        public string Source => "Fake lento: testes";
        public PerformanceSourceCategory Category => PerformanceSourceCategory.Cpu;
        public IPerformanceCollectorSession StartSession(PerformanceCollectionContext context) => new Session(firstDelay);
        private sealed class Session(TimeSpan firstDelay) : IPerformanceCollectorSession
        {
            private bool first = true;
            public async Task<PerformanceCollectorTick> ReadAsync(DateTimeOffset at, TimeSpan elapsed,
                CancellationToken ct)
            {
                if (first)
                {
                    first = false;
                    await Task.Delay(firstDelay, ct);
                }
                return new("performance-cpu", at, false, PerformanceSamples.Cpu(40, 25, 15),
                    null, [], [], [], []);
            }
            public void Dispose() { }
        }
    }
}
