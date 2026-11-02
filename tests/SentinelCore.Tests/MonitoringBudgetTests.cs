using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// Orçamento explícito de recursos: custo por leitura, oportunidades perdidas, limites de amostras e eventos,
/// orçamento de armazenamento e falha de gravação. Nenhum limite é aplicado em silêncio.
/// </summary>
public sealed class MonitoringBudgetTests
{
    private static MonitoringLimits Limits(Func<MonitoringLimits, MonitoringLimits>? configure = null) =>
        configure is null ? MonitoringSamples.FastLimits() : configure(MonitoringSamples.FastLimits());

    [Fact]
    public async Task OverBudgetReadSkipsTheNextOpportunityAndIsDeclared()
    {
        var store = new FakeMonitoringStore();
        var limits = Limits(seed => seed with { MaxCollectorTick = TimeSpan.FromMilliseconds(40) });
        var runner = MonitoringSamples.Runner(store,
            [new SlowMonitoringSource(TimeSpan.FromMilliseconds(120))], limits);

        var session = await runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Performance, TimeSpan.FromMilliseconds(600)));

        Assert.True(session.Budget.TickBudgetExceeded);
        Assert.True(session.Budget.MissedOpportunities > 0);
        Assert.Equal(MonitoringSourceStatus.Partial, session.Sources[0].Status);
        Assert.Contains(session.Sources[0].Limitations,
            item => item.Contains("excedeu o orçamento", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(session.Limitations,
            item => item.Contains("indisponível", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Limitations,
            item => item.Contains("orçamento", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SampleLimitStopsTheSourceAndMarksPartialCoverage()
    {
        var store = new FakeMonitoringStore();
        var limits = Limits(seed => seed with { MaxSamplesPerSource = 2 });
        var runner = MonitoringSamples.Runner(store,
            [new PerformanceMonitoringSource(new FakeMonitoringPerformanceCollector())], limits);

        var session = await runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Performance, TimeSpan.FromMilliseconds(900)));

        var source = session.Sources[0];
        Assert.True(source.LimitHit);
        Assert.Equal(2, source.ObservationCount);
        Assert.Equal(MonitoringSourceStatus.Partial, source.Status);
        Assert.Contains(source.Limitations,
            item => item.Contains("Limite de 2 amostras", StringComparison.Ordinal));
        Assert.Equal(MonitoringCoverageStatus.Partial, session.CoverageStatus);
    }

    [Fact]
    public async Task StorageBudgetStopsWritingWithoutDeletingConfirmedSegments()
    {
        var store = new FakeMonitoringStore();
        var limits = Limits(seed => seed with { MaxSessionBytes = 1 });
        var runner = MonitoringSamples.Runner(store,
            [new PerformanceMonitoringSource(new FakeMonitoringPerformanceCollector())], limits);

        var session = await runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Performance, TimeSpan.FromSeconds(2)));

        Assert.True(session.Budget.StorageBudgetReached);
        Assert.Equal(1, store.SegmentWrites);
        Assert.Single(store.Segments);
        Assert.Contains(session.Limitations,
            item => item.Contains("orçamento de armazenamento", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(MonitoringCoverageStatus.Complete, session.CoverageStatus);
    }

    [Fact]
    public async Task SegmentWriteFailureIsDeclaredAndNeverPresentedAsEvidence()
    {
        var store = new FakeMonitoringStore
        {
            SegmentFailure = _ => new IOException("Disco cheio (simulado).")
        };
        var runner = MonitoringSamples.Runner(store,
            [new PerformanceMonitoringSource(new FakeMonitoringPerformanceCollector())]);

        var session = await runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Performance, TimeSpan.FromMilliseconds(700)));

        Assert.Equal(MonitoringExecutionStatus.Failed, session.Status);
        Assert.Equal(0, store.SegmentWrites);
        Assert.Empty(session.Segments);
        Assert.False(session.Sources[0].LimitHit);
        Assert.False(session.Budget.SampleLimitReached);
        Assert.Equal(MonitoringSourceStatus.Partial, session.Sources[0].Status);
        Assert.Contains(session.Limitations,
            item => item.Contains("Falha de gravação", StringComparison.Ordinal));
        Assert.NotEqual(MonitoringCoverageStatus.Complete, session.CoverageStatus);
    }

    [Fact]
    public async Task EventLimitDropsAdditionalEventsAndDeclaresTheLimit()
    {
        var store = new FakeMonitoringStore();
        var limits = Limits(seed => seed with { MaxEventsPerSession = 1 });
        var events = new FakeMonitoringEventSource(_ =>
            [MonitoringSamples.Tdr(800, MonitoringSamples.Now),
                MonitoringSamples.ApplicationCrash(801, MonitoringSamples.Now)]);
        var runner = MonitoringSamples.Runner(store, [new EventMonitoringSource(events)], limits);

        var session = await runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Events, TimeSpan.FromMilliseconds(600)));

        Assert.True(session.Budget.EventLimitReached);
        Assert.Equal(1, session.Budget.EventsCollected);
        Assert.True(store.Segments.SelectMany(segment => segment.Events).Count() <= 1);
        Assert.Contains(session.Sources[0].Limitations,
            item => item.Contains("Limite de 1 eventos da sessão", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RequestedSourceWithoutProviderIsReportedUnavailable()
    {
        var store = new FakeMonitoringStore();
        var runner = MonitoringSamples.Runner(store,
            [new PerformanceMonitoringSource(new FakeMonitoringPerformanceCollector())]);

        var session = await runner.RunAsync(MonitoringSamples.FastRequest(
            MonitoringSources.Performance | MonitoringSources.Network, TimeSpan.FromMilliseconds(500)));

        var network = session.Sources.Single(source => source.Source == MonitoringSource.Network);
        Assert.Equal(MonitoringSourceStatus.Unavailable, network.Status);
        Assert.Equal(0, network.ObservationCount);
        Assert.Contains(network.Limitations,
            item => item.Contains("indisponível", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(MonitoringCoverageStatus.Partial, session.CoverageStatus);
    }

    [Fact]
    public async Task AllSourcesUnavailableProducesFailedSessionWithoutFabricatedEvidence()
    {
        var store = new FakeMonitoringStore();
        var runner = MonitoringSamples.Runner(store, [new UnavailableMonitoringSource()]);

        var session = await runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Network, TimeSpan.FromMilliseconds(500)));

        Assert.Equal(MonitoringExecutionStatus.Failed, session.Status);
        Assert.Equal(MonitoringCoverageStatus.Unavailable, session.CoverageStatus);
        Assert.Empty(session.Segments);
        Assert.Equal(0, store.SegmentWrites);
        Assert.All(session.Sources, source => Assert.Equal(MonitoringSourceStatus.Unavailable, source.Status));
    }
}
