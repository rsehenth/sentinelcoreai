using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class PerformanceRunnerTests
{
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

    private static FakeCollector Cpu(bool fail = false) => new("performance-cpu",
        PerformanceSourceCategory.Cpu, _ => fail
            ? throw new InvalidOperationException("falha simulada")
            : new("performance-cpu", DateTimeOffset.UtcNow, false, PerformanceSamples.Cpu(40, 25, 15),
                null, [], [], [], []));

    private static FakeCollector Memory() => new("performance-memory",
        PerformanceSourceCategory.Memory, _ => new("performance-memory", DateTimeOffset.UtcNow, false,
            null, PerformanceSamples.Memory(), [], [], [], []));

    private static FakeCollector Processes() => new("performance-processes",
        PerformanceSourceCategory.Processes, _ => new("performance-processes", DateTimeOffset.UtcNow,
            false, null, null, [PerformanceSamples.Process()], [], [], []));

    private static FakeCollector Storage() => new("performance-storage",
        PerformanceSourceCategory.Storage, _ => new("performance-storage", DateTimeOffset.UtcNow,
            false, null, null, [], [], [PerformanceSamples.Disk()], []));

    private static PerformanceSessionRequest Request() =>
        new(PerformanceSessionPreset.Custom, TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(100));

    [Fact]
    public async Task CompletedSessionKeepsStatisticsAndSamples()
    {
        var runner = new PerformanceSessionRunner([Cpu(), Memory(), Processes(), Storage()], selfMeterFactory: () => new FakeSelfOverheadMeter());
        var session = await runner.RunAsync(new PerformanceSessionRequest(PerformanceSessionPreset.Custom,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(250)));
        Assert.Equal(PerformanceSessionStatus.Completed, session.Status);
        Assert.Equal(4, session.Sources.Count);
        Assert.True(session.Samples.Count >= 3);
        Assert.Equal(session.Samples.Count, session.Statistics.TotalSamples);
        Assert.All(session.Observations, o => Assert.Equal(CausalityStatus.NotAssessed, o.Causality));
        Assert.True(session.SelfOverhead.Samples >= 3);
        Assert.Equal(2, session.FormatVersion);
        var coverage = Assert.IsType<PerformanceCoverage>(session.Coverage);
        Assert.Equal(PerformanceExecutionStatus.Completed, coverage.Execution);
        Assert.Equal(PerformanceCoverageStatus.Complete, coverage.Status);
        Assert.Equal(coverage.ExpectedSamples, coverage.CollectedSamples);
        Assert.Equal(0, coverage.MissedOpportunities);
        Assert.Empty(coverage.UnavailableCollectors);
    }

    [Fact]
    public async Task ProcessExitsArePreservedInTheSession()
    {
        var exit = new ProcessExitReading("123:639000000000000000", 123, "app",
            DateTimeOffset.UtcNow, "Processo encerrado");
        var collector = new FakeCollector("performance-processes", PerformanceSourceCategory.Processes,
            index => new("performance-processes", DateTimeOffset.UtcNow, false, null, null,
                [PerformanceSamples.Process()], index == 0 ? [] : [exit], [], []));
        var runner = new PerformanceSessionRunner([collector], selfMeterFactory: () => new FakeSelfOverheadMeter());
        var session = await runner.RunAsync(Request());
        Assert.Contains(session.Samples,
            sample => sample.Exits.Any(e => e.Identity == exit.Identity && e.Reason == "Processo encerrado"));
    }

    [Fact]
    public async Task UnavailableCollectorKeepsExecutionCompletedAndCoveragePartial()
    {
        var runner = new PerformanceSessionRunner([Cpu(fail: true), Memory(), Processes(), Storage()], selfMeterFactory: () => new FakeSelfOverheadMeter());
        var session = await runner.RunAsync(Request());
        Assert.Equal(PerformanceSessionStatus.Completed, session.Status);
        Assert.Equal(PerformanceSourceStatus.Failed,
            session.Sources.Single(s => s.CollectorId == "performance-cpu").Status);
        Assert.NotEmpty(session.Samples);
        var coverage = Assert.IsType<PerformanceCoverage>(session.Coverage);
        Assert.Equal(PerformanceExecutionStatus.Completed, coverage.Execution);
        Assert.Equal(PerformanceCoverageStatus.Partial, coverage.Status);
        Assert.Contains("performance-cpu", coverage.UnavailableCollectors);
    }

    [Fact]
    public async Task AllFailedIsFailed()
    {
        var runner = new PerformanceSessionRunner([Cpu(fail: true)], selfMeterFactory: () => new FakeSelfOverheadMeter());
        var session = await runner.RunAsync(Request());
        Assert.Equal(PerformanceSessionStatus.Failed, session.Status);
        var coverage = Assert.IsType<PerformanceCoverage>(session.Coverage);
        Assert.Equal(PerformanceExecutionStatus.Failed, coverage.Execution);
        Assert.Equal(PerformanceCoverageStatus.Unavailable, coverage.Status);
    }

    [Fact]
    public async Task CancellationPreservesCollectedSamples()
    {
        using var cts = new CancellationTokenSource();
        var runner = new PerformanceSessionRunner([Cpu(), Memory(), Processes(), Storage()], selfMeterFactory: () => new FakeSelfOverheadMeter());
        var running = runner.RunAsync(new PerformanceSessionRequest(PerformanceSessionPreset.Custom,
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100)), cancellationToken: cts.Token);
        await Task.Delay(150);
        cts.Cancel();
        var session = await running;
        Assert.Equal(PerformanceSessionStatus.Cancelled, session.Status);
        Assert.NotEmpty(session.Samples);
        Assert.All(session.Sources, source => Assert.Equal(PerformanceSourceStatus.Cancelled, source.Status));
        var coverage = Assert.IsType<PerformanceCoverage>(session.Coverage);
        Assert.Equal(PerformanceExecutionStatus.Cancelled, coverage.Execution);
        Assert.Equal(PerformanceCoverageStatus.Partial, coverage.Status);
        Assert.True(coverage.CollectedSamples < coverage.ExpectedSamples);
    }

    [Fact]
    public async Task OnlyOneSessionCanRunAtATime()
    {
        var runner = new PerformanceSessionRunner([Cpu()], selfMeterFactory: () => new FakeSelfOverheadMeter());
        var running = runner.RunAsync(new PerformanceSessionRequest(PerformanceSessionPreset.Custom,
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Request()));
        await running;
    }

    [Fact]
    public async Task RequestExceedingInjectedSampleLimitIsRejected()
    {
        var runner = new PerformanceSessionRunner([Cpu()],
            limits: new PerformanceLimits { MaxSamples = 2, MaxSessionDuration = TimeSpan.FromSeconds(1) }, selfMeterFactory: () => new FakeSelfOverheadMeter());
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
            new PerformanceSessionRequest(PerformanceSessionPreset.Custom,
                TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100))));
    }

    [Fact]
    public void LegacyCoverageDerivesSampleLimitFromPreservedLimitations()
    {
        var session = PerformanceSamples.Session() with
        {
            Limitations = ["Limite de 300 amostras atingido durante a sessão."]
        };
        var coverage = PerformanceCoverage.FromLegacy(session);
        Assert.True(coverage.SampleLimitReached);
        Assert.Equal(PerformanceCoverageStatus.Partial, coverage.Status);
    }

    [Fact]
    public async Task ProcessLimitProducesCompletedExecutionWithPartialCoverage()
    {
        var limited = new FakeCollector("performance-processes", PerformanceSourceCategory.Processes,
            _ => new("performance-processes", DateTimeOffset.UtcNow, false, null, null,
                [PerformanceSamples.Process()], [], [],
                ["Limite de 20 processos acompanhados atingido; processos com menos atividade omitidos."]));
        var runner = new PerformanceSessionRunner([Cpu(), limited], selfMeterFactory: () => new FakeSelfOverheadMeter());
        var session = await runner.RunAsync(Request());
        Assert.Equal(PerformanceSessionStatus.Completed, session.Status);
        var coverage = Assert.IsType<PerformanceCoverage>(session.Coverage);
        Assert.Equal(PerformanceExecutionStatus.Completed, coverage.Execution);
        Assert.Equal(PerformanceCoverageStatus.Partial, coverage.Status);
        Assert.True(coverage.ProcessLimitReached);
        Assert.Contains(session.Limitations,
            limitation => limitation.Contains("limite", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PerSourceReadDurationIsRecorded()
    {
        var runner = new PerformanceSessionRunner([new SlowFirstTickCollector(
            TimeSpan.FromMilliseconds(60))], selfMeterFactory: () => new FakeSelfOverheadMeter());
        var session = await runner.RunAsync(Request());
        var source = Assert.Single(session.Sources);
        Assert.NotNull(source.MeanReadMilliseconds);
        Assert.NotNull(source.MaxReadMilliseconds);
        Assert.True(source.MaxReadMilliseconds >= 50, $"Máxima: {source.MaxReadMilliseconds:F1} ms");
    }

    [Fact]
    public void NonIntegralDurationUsesFloorPlusOneExpectedSamples()
    {
        var request = new PerformanceSessionRequest(PerformanceSessionPreset.Custom,
            TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(100));
        Assert.Equal(3, request.ExpectedSamples);
    }

    [Fact]
    public async Task ProgressReportsEveryTickAndNeverClaimsCompletionEarly()
    {
        var recorder = new RecordingProgress();
        var runner = new PerformanceSessionRunner([Cpu()], selfMeterFactory: () => new FakeSelfOverheadMeter());
        var session = await runner.RunAsync(Request(), progress: recorder);
        Assert.Equal(session.Samples.Count, recorder.Updates.Count);
        Assert.All(recorder.Updates, update =>
            Assert.InRange(update.SamplesCollected, 1, update.ExpectedSamples));
    }

    [Fact]
    public async Task SlowCollectionSkipsMissedTicksInsteadOfBursting()
    {
        var runner = new PerformanceSessionRunner([new SlowFirstTickCollector(
            TimeSpan.FromMilliseconds(350))], selfMeterFactory: () => new FakeSelfOverheadMeter());
        var session = await runner.RunAsync(new PerformanceSessionRequest(PerformanceSessionPreset.Custom,
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100)));
        Assert.Equal(PerformanceSessionStatus.Completed, session.Status);
        var coverage = Assert.IsType<PerformanceCoverage>(session.Coverage);
        Assert.Equal(PerformanceCoverageStatus.Partial, coverage.Status);
        Assert.True(coverage.MissedOpportunities > 0, $"Perdidas: {coverage.MissedOpportunities}");
        Assert.Contains(session.Limitations, l => l.Contains("oportunidade", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Samples.Count < 11, $"Amostras: {session.Samples.Count}");
        for (var i = 2; i < session.Samples.Count; i++)
        {
            var gap = (session.Samples[i].TimestampUtc - session.Samples[i - 1].TimestampUtc).TotalMilliseconds;
            Assert.True(gap >= 40, $"Intervalo {gap:F0} ms acelerado na amostra {i}.");
        }
    }

    [Fact]
    public async Task CollectorsReceiveTheTickIntervalNotCumulativeElapsed()
    {
        var intervals = new List<TimeSpan>();
        var collector = new ElapsedCapturingCollector(intervals);
        var runner = new PerformanceSessionRunner([collector], selfMeterFactory: () => new FakeSelfOverheadMeter());
        var session = await runner.RunAsync(Request());
        Assert.True(intervals.Count >= 3);
        Assert.Equal(TimeSpan.Zero, intervals[0]);
        var sum = intervals.Aggregate(TimeSpan.Zero, (total, value) => total + value);
        Assert.InRange(Math.Abs((sum.TotalSeconds - session.Samples[^1].ElapsedSeconds) * 1000), 0, 100);
    }

    [Fact]
    public async Task SessionDurationLimitIsHonoredWhenInjected()
    {
        var runner = new PerformanceSessionRunner([Cpu()],
            limits: new PerformanceLimits { MaxSessionDuration = TimeSpan.FromMilliseconds(200) }, selfMeterFactory: () => new FakeSelfOverheadMeter());
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(Request()));
    }

    private sealed class ElapsedCapturingCollector(List<TimeSpan> intervals) : IPerformanceCollector
    {
        public string Id => "performance-cpu";
        public string Version => "1.0";
        public string Source => "Fake: intervalos";
        public PerformanceSourceCategory Category => PerformanceSourceCategory.Cpu;
        public IPerformanceCollectorSession StartSession(PerformanceCollectionContext context) => new Session(intervals);
        private sealed class Session(List<TimeSpan> intervals) : IPerformanceCollectorSession
        {
            public Task<PerformanceCollectorTick> ReadAsync(DateTimeOffset at, TimeSpan elapsed,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                intervals.Add(elapsed);
                return Task.FromResult(new PerformanceCollectorTick("performance-cpu", at, false,
                    PerformanceSamples.Cpu(40, 25, 15), null, [], [], [], []));
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

    private sealed class RecordingProgress : IProgress<PerformanceProgress>
    {
        public List<PerformanceProgress> Updates { get; } = [];
        public void Report(PerformanceProgress value) => Updates.Add(value);
    }
}
