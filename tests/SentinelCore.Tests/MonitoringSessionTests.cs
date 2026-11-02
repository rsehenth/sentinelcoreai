using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// Contratos da sessão de monitoramento: autorização explícita, duração, concorrência, cancelamento,
/// disponibilidade das fontes e relógio monotônico. Nenhum teste inicia observação real do Windows.
/// </summary>
public sealed class MonitoringSessionTests
{
    private static List<IMonitoringSource> Sources(params IMonitoringSource[] sources) => [.. sources];

    [Fact]
    public async Task SessionRequiresExplicitAuthorizationAndWritesNothing()
    {
        var store = new FakeMonitoringStore();
        var runner = MonitoringSamples.Runner(store,
            Sources(new PerformanceMonitoringSource(new FakeMonitoringPerformanceCollector())));

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync(MonitoringSamples.FastRequest(MonitoringSources.Performance, authorized: false)));

        Assert.Contains("autorização", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.SegmentWrites);
        Assert.Equal(0, store.SessionWrites);
    }

    [Fact]
    public async Task InvalidDurationsAndMissingSourcesAreRejected()
    {
        var store = new FakeMonitoringStore();
        var runner = MonitoringSamples.Runner(store,
            Sources(new PerformanceMonitoringSource(new FakeMonitoringPerformanceCollector())));

        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
            new MonitoringRequest(MonitoringDurationPreset.Minutes5, TimeSpan.FromMinutes(5),
                MonitoringSources.None, Authorized: true)));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
            new MonitoringRequest(MonitoringDurationPreset.Minutes5, TimeSpan.FromMinutes(10),
                MonitoringSources.Performance, Authorized: true)));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
            new MonitoringRequest(MonitoringDurationPreset.Custom, TimeSpan.FromMinutes(31),
                MonitoringSources.Performance, Authorized: true)));
        Assert.Equal(0, store.SegmentWrites);
    }

    [Fact]
    public void ExpectedTicksRespectsExactAndInexactGrids()
    {
        var exact = new MonitoringRequest(MonitoringDurationPreset.Minutes5, TimeSpan.FromMinutes(5),
            MonitoringSources.Performance, Authorized: true);
        var inexact = MonitoringSamples.FastRequest(MonitoringSources.Performance,
            TimeSpan.FromMilliseconds(750));

        Assert.Equal(20, exact.ExpectedTicks(TimeSpan.FromSeconds(15)));
        Assert.Equal(8, inexact.ExpectedTicks(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(0, inexact.ExpectedTicks(TimeSpan.Zero));
    }

    [Fact]
    public async Task SessionObservesSelectedSourcesAndPersistsCheckpoints()
    {
        var store = new FakeMonitoringStore();
        var events = new FakeMonitoringEventSource(index => [MonitoringSamples.Tdr(100 + index,
            MonitoringSamples.Now.AddSeconds(index))]);
        var runner = MonitoringSamples.Runner(store,
        [
            new PerformanceMonitoringSource(new FakeMonitoringPerformanceCollector()),
            new EventMonitoringSource(events)
        ]);

        var session = await runner.RunAsync(MonitoringSamples.FastRequest(
            MonitoringSources.Performance | MonitoringSources.Events | MonitoringSources.Graphics));

        Assert.Equal(MonitoringExecutionStatus.Completed, session.Status);
        Assert.NotEmpty(session.Segments);
        Assert.True(store.SegmentWrites > 0);
        Assert.Equal(store.SegmentWrites, session.Segments.Count);
        Assert.True(session.Budget.SamplesCollected > 0);
        Assert.True(session.Budget.EventsCollected > 0);
        Assert.True(events.Polls > 0);
        Assert.Contains(MonitoringGuidance.AuthorizationLimitation, session.Limitations);
        Assert.Contains(MonitoringGuidance.ScopeLimitation, session.Limitations);
        Assert.Contains(MonitoringGuidance.CausalityLimitation, session.Limitations);
        Assert.Contains(MonitoringGuidance.BudgetLimitation, session.Limitations);
        Assert.All(session.Segments,
            reference => Assert.True(reference.ObservationCount > 0));
        Assert.Equal(session.Segments.Count, session.Segments.Select(item => item.SegmentId).Distinct().Count());
        Assert.Contains(session.Sources, source => source.Source == MonitoringSource.Graphics);
        Assert.Equal(MonitoringSourceStatus.Completed,
            session.Sources.Single(source => source.Source == MonitoringSource.Graphics).Status);
    }

    [Fact]
    public async Task GraphicsEventsArePartitionedWithoutDuplicationBetweenSources()
    {
        var store = new FakeMonitoringStore();
        var events = new FakeMonitoringEventSource(index =>
        [
            MonitoringSamples.Tdr(500 + index * 2, MonitoringSamples.Now),
            MonitoringSamples.ApplicationCrash(501 + index * 2, MonitoringSamples.Now)
        ]);
        var runner = MonitoringSamples.Runner(store, Sources(new EventMonitoringSource(events)));

        var session = await runner.RunAsync(MonitoringSamples.FastRequest(
            MonitoringSources.Events | MonitoringSources.Graphics));

        var eventSegments = store.Segments.Where(segment => segment.Source == MonitoringSource.Events).ToArray();
        var graphicsSegments = store.Segments
            .Where(segment => segment.Source == MonitoringSource.Graphics).ToArray();
        Assert.All(eventSegments.SelectMany(segment => segment.Events),
            item => Assert.NotEqual(EventCategory.Graphics, item.Category));
        Assert.NotEmpty(graphicsSegments.SelectMany(segment => segment.Events));
        Assert.All(graphicsSegments.SelectMany(segment => segment.Events),
            item => Assert.Equal(EventCategory.Graphics, item.Category));
        var identities = store.Segments.SelectMany(segment => segment.Events)
            .Select(item => $"evt:{item.Channel}:{item.RecordId}").ToArray();
        Assert.Equal(identities.Length, identities.Distinct(StringComparer.Ordinal).Count());
        Assert.All(session.Sources, source => Assert.True(source.ObservationCount > 0));
    }

    [Fact]
    public async Task GraphicsOnlySelectionDiscardsOtherEventsAndDeclaresTheLimitation()
    {
        var store = new FakeMonitoringStore();
        var events = new FakeMonitoringEventSource(_ =>
            [MonitoringSamples.Tdr(600, MonitoringSamples.Now),
                MonitoringSamples.ApplicationCrash(601, MonitoringSamples.Now)]);
        var runner = MonitoringSamples.Runner(store, Sources(new EventMonitoringSource(events)));

        var session = await runner.RunAsync(MonitoringSamples.FastRequest(MonitoringSources.Graphics));

        Assert.Single(session.Sources);
        Assert.Equal(MonitoringSource.Graphics, session.Sources[0].Source);
        Assert.All(store.Segments.SelectMany(segment => segment.Events),
            item => Assert.Equal(EventCategory.Graphics, item.Category));
        Assert.Contains(session.Sources[0].Limitations,
            item => item.Contains("Somente fontes gráficas", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CancellationStopsTheSessionAndPreservesConfirmedCheckpoints()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeMonitoringStore();
        var runner = MonitoringSamples.Runner(store,
            Sources(new CancellingMonitoringSource(3, cancellation)));

        var session = await runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Performance, TimeSpan.FromSeconds(2)),
            cancellationToken: cancellation.Token);

        Assert.Equal(MonitoringExecutionStatus.Cancelled, session.Status);
        Assert.True(store.SegmentWrites > 0);
        Assert.Contains(session.Limitations,
            item => item.Contains("cancelada", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(MonitoringSourceStatus.Cancelled, session.Sources[0].Status);
        Assert.Equal(MonitoringCoverageStatus.Partial, session.CoverageStatus);
    }

    [Fact]
    public async Task UnavailableSourceIsDeclaredWhileTheOtherSourceContinues()
    {
        var store = new FakeMonitoringStore();
        var runner = MonitoringSamples.Runner(store,
        [
            new PerformanceMonitoringSource(new FakeMonitoringPerformanceCollector()),
            new UnavailableMonitoringSource()
        ]);

        var session = await runner.RunAsync(MonitoringSamples.FastRequest(
            MonitoringSources.Performance | MonitoringSources.Network));

        Assert.Equal(MonitoringExecutionStatus.Completed, session.Status);
        Assert.Equal(MonitoringCoverageStatus.Partial, session.CoverageStatus);
        var network = session.Sources.Single(source => source.Source == MonitoringSource.Network);
        Assert.Equal(MonitoringSourceStatus.Unavailable, network.Status);
        Assert.Equal(0, network.ObservationCount);
        Assert.Contains(session.Limitations,
            item => item.Contains("indisponível", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DurationUsesMonotonicClockEvenWhenTheSystemClockJumps()
    {
        var start = MonitoringSamples.Now;
        var store = new FakeMonitoringStore();
        var runner = new MonitoringSessionRunner(
            Sources(new PerformanceMonitoringSource(new FakeMonitoringPerformanceCollector())),
            store, new JumpingClock(start), MonitoringSamples.FastLimits());

        var session = await runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Performance, TimeSpan.FromMilliseconds(500)));

        Assert.Equal(start, session.StartedUtc);
        Assert.True(session.CompletedUtc - session.StartedUtc > TimeSpan.FromHours(1),
            "O relógio civil saltou durante a sessão.");
        Assert.True(session.Duration < TimeSpan.FromSeconds(5),
            "A duração precisa vir do relógio monotônico, não do relógio civil.");
    }

    [Fact]
    public async Task OnlyOneSessionRunsAtATime()
    {
        var store = new FakeMonitoringStore();
        var blocking = new BlockingMonitoringSource();
        var runner = MonitoringSamples.Runner(store, Sources(blocking));

        var running = runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Performance, TimeSpan.FromSeconds(1)));
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(MonitoringSamples.FastRequest(MonitoringSources.Performance)));

        blocking.Release.TrySetResult();
        var session = await running;
        Assert.Contains(session.Sources, source => source.Status != MonitoringSourceStatus.Unavailable);
    }

    [Fact]
    public async Task ProgressReportsSamplesEventsAndBudgetState()
    {
        var store = new FakeMonitoringStore();
        var events = new FakeMonitoringEventSource(_ => [MonitoringSamples.Tdr(700, MonitoringSamples.Now)]);
        var runner = MonitoringSamples.Runner(store,
        [
            new PerformanceMonitoringSource(new FakeMonitoringPerformanceCollector()),
            new EventMonitoringSource(events)
        ]);
        var reports = new List<MonitoringProgress>();

        await runner.RunAsync(MonitoringSamples.FastRequest(
                MonitoringSources.Performance | MonitoringSources.Events),
            new SynchronousProgress<MonitoringProgress>(reports.Add));

        Assert.NotEmpty(reports);
        Assert.Contains(reports, report => report.Samples > 0);
        Assert.Contains(reports, report => report.Events > 0);
        Assert.All(reports, report => Assert.False(string.IsNullOrWhiteSpace(report.Phase)));
    }

    [Fact]
    public async Task EventObservationsUseTheRunnerTickTimestampAndNotTheProbeClock()
    {
        var passed = MonitoringSamples.Now.AddSeconds(-5);
        var source = new EventMonitoringSource(new FakeMonitoringEventSource(
            _ => [MonitoringSamples.Tdr(900, MonitoringSamples.Now)]));
        var session = await source.StartAsync(CancellationToken.None);
        try
        {
            var tick = await session.ReadAsync(passed, CancellationToken.None);
            var observation = Assert.Single(tick.Events);
            Assert.Equal(passed, observation.TimestampUtc);
            Assert.Equal(MonitoringSamples.Now, observation.OccurredUtc);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task SourceWithoutAnyValidObservationIsPartialAndDeclared()
    {
        var store = new FakeMonitoringStore();
        var runner = MonitoringSamples.Runner(store,
            Sources(new PerformanceMonitoringSource(new FakeEmptyPerformanceCollector())));

        var session = await runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Performance, TimeSpan.FromMilliseconds(500)));

        var source = Assert.Single(session.Sources);
        Assert.Equal(0, source.ObservationCount);
        Assert.Equal(MonitoringSourceStatus.Partial, source.Status);
        Assert.Contains(source.Limitations,
            item => item.Contains("Nenhuma observação válida", StringComparison.Ordinal));
        Assert.NotEqual(MonitoringCoverageStatus.Complete, session.CoverageStatus);
    }

    [Fact]
    public async Task IncompleteNetworkReadingIsNotConvertedIntoZeroCounts()
    {
        var store = new FakeMonitoringStore();
        var runner = MonitoringSamples.Runner(store,
            Sources(new NetworkMonitoringSource([new FakeNetworkCollector(incomplete: true)])));

        var session = await runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Network, TimeSpan.FromMilliseconds(500)));

        var source = Assert.Single(session.Sources);
        Assert.Equal(0, source.ObservationCount);
        Assert.Equal(MonitoringSourceStatus.Partial, source.Status);
        Assert.Contains(source.Limitations,
            item => item.Contains("Nenhuma observação válida", StringComparison.Ordinal));
        Assert.DoesNotContain(store.Segments.SelectMany(segment => segment.Network),
            observation => observation.TcpConnections == 0 && observation.Incomplete);
    }

    [Fact]
    public async Task TruncatedEventCycleDowngradesTheEventSource()
    {
        var store = new FakeMonitoringStore();
        var events = new FakeMonitoringEventSource(
            _ => [MonitoringSamples.Tdr(3000, MonitoringSamples.Now)], limitHit: true);
        var runner = MonitoringSamples.Runner(store, Sources(new EventMonitoringSource(events)));

        var session = await runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Events, TimeSpan.FromMilliseconds(400)));

        var source = Assert.Single(session.Sources);
        Assert.True(source.LimitHit);
        Assert.Equal(MonitoringSourceStatus.Partial, source.Status);
        Assert.Contains(source.Limitations,
            item => item.Contains("truncamento", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IncompleteNetworkReadingWithCountsIsPartialAndPreservesNoAddress()
    {
        var endpoint = new NetworkEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4,
            "10.1.2.3", 50000, "10.9.9.9", 443, TcpConnectionState.Established, 42,
            "42:100", "fake.exe", NetworkProcessStatus.Resolved, MonitoringSamples.Now, "fake");
        var store = new FakeMonitoringStore();
        var runner = MonitoringSamples.Runner(store,
            Sources(new NetworkMonitoringSource([new FakeNetworkCollector([endpoint], incomplete: true)])));

        var session = await runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Network, TimeSpan.FromMilliseconds(400)));

        var source = Assert.Single(session.Sources);
        Assert.Equal(MonitoringSourceStatus.Partial, source.Status);
        var observation = store.Segments.SelectMany(segment => segment.Network).First();
        Assert.True(observation.Incomplete);
        Assert.Equal(1, observation.TcpConnections);
        Assert.DoesNotContain("10.1.2.3", System.Text.Json.JsonSerializer.Serialize(observation));
    }

    [Fact]
    public async Task UnavailableSourceKeepsItsStatusWhenTheSessionIsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeMonitoringStore();
        var runner = MonitoringSamples.Runner(store,
        [
            new CancellingMonitoringSource(2, cancellation),
            new UnavailableMonitoringSource()
        ]);

        var session = await runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Performance | MonitoringSources.Network,
                TimeSpan.FromSeconds(2)), cancellationToken: cancellation.Token);

        Assert.Equal(MonitoringExecutionStatus.Cancelled, session.Status);
        var network = session.Sources.Single(source => source.Source == MonitoringSource.Network);
        Assert.Equal(MonitoringSourceStatus.Unavailable, network.Status);
    }

    [Fact]
    public async Task CancellationWithoutObservationsHasUnavailableCoverage()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new FakeMonitoringStore();
        var runner = MonitoringSamples.Runner(store,
            Sources(new PerformanceMonitoringSource(new FakeMonitoringPerformanceCollector())));

        var session = await runner.RunAsync(
            MonitoringSamples.FastRequest(MonitoringSources.Performance, TimeSpan.FromSeconds(1)),
            cancellationToken: cancellation.Token);

        Assert.Equal(MonitoringExecutionStatus.Cancelled, session.Status);
        Assert.Equal(MonitoringCoverageStatus.Unavailable, session.CoverageStatus);
        Assert.Empty(session.Segments);
    }

    [Fact]
    public void InvalidIncidentDescriptionLimitIsRejected()
    {
        var limits = MonitoringSamples.FastLimits() with { MaxIncidentDescriptionLength = 500 };
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.Validate());
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
