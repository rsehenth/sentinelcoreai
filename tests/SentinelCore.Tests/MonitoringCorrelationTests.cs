using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// Integração da Fase 5 com o Evidence Correlation Engine da Fase 3.4: as observações da sessão precisam estar
/// efetivamente acessíveis ao motor, eventos mantêm identidade e não são contados duas vezes, e incidentes manuais
/// não se tornam evidência nativa.
/// </summary>
public sealed class MonitoringCorrelationTests
{
    private static readonly DateTimeOffset Now = CorrelationSamples.Now;

    private static MonitoringSession SessionWith(MonitoringSegment segment, MonitoringSources requested) =>
        MonitoringSamples.Session(segment.SessionId, [segment], requested) with
        {
            StartedUtc = Now.AddMinutes(-5),
            CompletedUtc = Now,
            Duration = TimeSpan.FromMinutes(5)
        };

    private static FakeMonitoringStore StoreWith(MonitoringSession session, params MonitoringSegment[] segments)
    {
        var store = new FakeMonitoringStore();
        store.Sessions.Enqueue(session);
        foreach (var segment in segments) store.Segments.Enqueue(segment);
        return store;
    }

    [Fact]
    public async Task MonitoringEventsAreAccessibleAndConsolidatedWithEventInvestigations()
    {
        var tdr = GraphicsSamples.DisplayTdr(recordId: 1234, at: Now.AddHours(-1));
        var events = new CorrelationSamples.FakeEventStore();
        events.Records.Add(await CorrelationSamples.EventsAsync(events: [tdr]));
        var segment = MonitoringSamples.Segment(Guid.NewGuid(), MonitoringSource.Events, 0,
            events: [MonitoringSamples.EventObservation(tdr, 0, timestamp: Now.AddHours(-1))],
            from: Now.AddHours(-2), to: Now.AddMinutes(-30));
        var monitoring = StoreWith(SessionWith(segment, MonitoringSources.Events), segment);

        var reader = new CorrelationEvidenceReader(events: events, monitoring: monitoring);
        var runner = new EvidenceCorrelationRunner(reader, CorrelationSamples.Clock());
        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Events | CorrelationSources.Monitoring));

        var evidence = Assert.Single(investigation.Evidence);
        Assert.Contains("evt:System:1234", evidence.Id, StringComparison.Ordinal);
        Assert.Equal(2, evidence.Origins.Count);
        Assert.Contains(evidence.Origins, origin => origin.Source == CorrelationSource.Events);
        Assert.Contains(evidence.Origins, origin => origin.Source == CorrelationSource.Monitoring);
        Assert.Equal(1, investigation.Coverage.MergedReferences);
        Assert.Contains(investigation.Sources,
            source => source.Source == CorrelationSource.Monitoring && source.RecordsUsed > 0);
    }

    [Fact]
    public async Task MonitoringObservationsFeedTheExistingPerformanceEventRule()
    {
        var crash = MonitoringSamples.ApplicationCrash(2001, Now.AddMinutes(-2).AddSeconds(20));
        var sessionId = Guid.NewGuid();
        var performanceSegment = MonitoringSamples.Segment(sessionId, MonitoringSource.Performance, 0,
            performance: [MonitoringSamples.PerformanceObservation(0, Now.AddMinutes(-2))],
            from: Now.AddMinutes(-3), to: Now.AddMinutes(-1));
        var eventSegment = MonitoringSamples.Segment(sessionId, MonitoringSource.Events, 0,
            events: [MonitoringSamples.EventObservation(crash, 0, Now.AddMinutes(-2).AddSeconds(20))],
            from: Now.AddMinutes(-3), to: Now.AddMinutes(-1));
        var monitoring = StoreWith(SessionWith(performanceSegment,
            MonitoringSources.Performance | MonitoringSources.Events), performanceSegment, eventSegment);

        var reader = new CorrelationEvidenceReader(monitoring: monitoring);
        var runner = new EvidenceCorrelationRunner(reader, CorrelationSamples.Clock());
        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Performance | CorrelationSources.Events | CorrelationSources.Monitoring));

        var relation = Assert.Single(investigation.Relations,
            item => item.RuleId == CorrelationGuidance.PerformanceEventRuleId);
        Assert.Equal(2, relation.EvidenceIds.Count);
        Assert.Equal(CausalityStatus.NotAssessed, relation.Causality);
        Assert.Contains(CorrelationGuidance.CausalityLimitation, relation.Limitations);
    }

    [Fact]
    public async Task ManualIncidentIsNeverConvertedIntoNativeEvidence()
    {
        var sessionId = Guid.NewGuid();
        var incident = MonitoringSamples.Incident(sessionId, Now.AddMinutes(-2),
            "tela preta durante vídeo");
        var session = MonitoringSamples.Session(sessionId, [], incidents: [incident],
            requested: MonitoringSources.Events) with
        {
            StartedUtc = Now.AddMinutes(-5),
            CompletedUtc = Now,
            Duration = TimeSpan.FromMinutes(5)
        };
        var store = StoreWith(session);

        var reader = new CorrelationEvidenceReader(monitoring: store);
        var runner = new EvidenceCorrelationRunner(reader, CorrelationSamples.Clock());
        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Monitoring));

        Assert.Empty(investigation.Evidence);
        Assert.Empty(investigation.Timeline);
        Assert.DoesNotContain(investigation.Evidence,
            item => item.Title.Contains("tela preta", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(investigation.Relations,
            item => item.Kind == CorrelationRelationKind.InsufficientEvidence);
        Assert.Equal(CorrelationSourceStatus.Partial,
            investigation.Sources.Single(source => source.Source == CorrelationSource.Monitoring).Status);
    }

    [Fact]
    public async Task MissingMonitoringStoreIsDeclaredUnavailableInsteadOfAssumedEmpty()
    {
        var reader = new CorrelationEvidenceReader();
        var runner = new EvidenceCorrelationRunner(reader, CorrelationSamples.Clock());

        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Monitoring));

        var source = Assert.Single(investigation.Sources,
            item => item.Source == CorrelationSource.Monitoring);
        Assert.Equal(CorrelationSourceStatus.Unavailable, source.Status);
        Assert.Equal(CorrelationCoverageStatus.Unavailable, investigation.Coverage.Status);
        Assert.Equal(CorrelationStatus.Failed, investigation.Status);
        Assert.Contains(source.Limitations,
            item => item.Contains("não disponível", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InterruptedMonitoringSessionDowngradesCorrelationCoverage()
    {
        var tdr = GraphicsSamples.DisplayTdr(recordId: 3003, at: Now.AddHours(-2));
        var segment = MonitoringSamples.Segment(Guid.NewGuid(), MonitoringSource.Events, 0,
            events: [MonitoringSamples.EventObservation(tdr, 0, timestamp: Now.AddHours(-2))],
            from: Now.AddHours(-2).AddMinutes(-1), to: Now.AddHours(-2).AddMinutes(1));
        var session = SessionWith(segment, MonitoringSources.Events) with
        {
            Status = MonitoringExecutionStatus.Interrupted,
            CoverageStatus = MonitoringCoverageStatus.Partial,
            Recovery = new MonitoringRecoveryInfo(true, Now.AddMinutes(-30), 1,
                "Registro final ausente; estado derivado dos checkpoints confirmados.")
        };
        var store = StoreWith(session, segment);

        var reader = new CorrelationEvidenceReader(monitoring: store);
        var runner = new EvidenceCorrelationRunner(reader, CorrelationSamples.Clock());
        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Monitoring));

        var source = investigation.Sources.Single(item => item.Source == CorrelationSource.Monitoring);
        Assert.Equal(CorrelationSourceStatus.Partial, source.Status);
        Assert.Contains(source.Limitations,
            item => item.Contains("origem", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(CorrelationCoverageStatus.Complete, investigation.Coverage.Status);
    }

    [Fact]
    public async Task SessionOutsideTheWindowDowngradesMonitoringCoverage()
    {
        var old = Now.AddDays(-10);
        var tdr = GraphicsSamples.DisplayTdr(recordId: 5005, at: old);
        var segment = MonitoringSamples.Segment(Guid.NewGuid(), MonitoringSource.Events, 0,
            events: [MonitoringSamples.EventObservation(tdr, 0, timestamp: old)],
            from: old.AddMinutes(-1), to: old.AddMinutes(1));
        var session = MonitoringSamples.Session(segment.SessionId, [segment],
            status: MonitoringExecutionStatus.Completed,
            coverage: MonitoringCoverageStatus.Complete) with
        {
            StartedUtc = old.AddMinutes(-2),
            CompletedUtc = old,
            Duration = TimeSpan.FromMinutes(2)
        };
        var store = StoreWith(session, segment);

        var reader = new CorrelationEvidenceReader(monitoring: store);
        var runner = new EvidenceCorrelationRunner(reader, CorrelationSamples.Clock());
        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Monitoring));

        Assert.Empty(investigation.Evidence);
        var source = investigation.Sources.Single(item => item.Source == CorrelationSource.Monitoring);
        Assert.Equal(CorrelationSourceStatus.Partial, source.Status);
        Assert.Contains(source.Limitations,
            item => item.Contains("Nenhum registro desta fonte cobriu", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(CorrelationCoverageStatus.Complete, investigation.Coverage.Status);
    }

    [Fact]
    public async Task CoveredSessionWithoutAcceptedEvidenceUsesAnHonestLimitation()
    {
        var sessionId = Guid.NewGuid();
        var session = MonitoringSamples.Session(sessionId, [],
            status: MonitoringExecutionStatus.Completed,
            coverage: MonitoringCoverageStatus.Complete) with
        {
            StartedUtc = Now.AddMinutes(-5),
            CompletedUtc = Now,
            Duration = TimeSpan.FromMinutes(5)
        };
        var store = StoreWith(session);

        var reader = new CorrelationEvidenceReader(monitoring: store);
        var runner = new EvidenceCorrelationRunner(reader, CorrelationSamples.Clock());
        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Monitoring));

        var source = investigation.Sources.Single(item => item.Source == CorrelationSource.Monitoring);
        Assert.Equal(CorrelationSourceStatus.Partial, source.Status);
        Assert.Contains(source.Limitations,
            item => item.Contains("não produziram evidência aceita", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(source.Limitations,
            item => item.Contains("Nenhum registro desta fonte cobriu", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MonitoringSourceIsSkippedWhenNotRequested()
    {
        var tdr = GraphicsSamples.DisplayTdr(recordId: 4004, at: Now.AddHours(-1));
        var events = new CorrelationSamples.FakeEventStore();
        events.Records.Add(await CorrelationSamples.EventsAsync(events: [tdr]));
        var segment = MonitoringSamples.Segment(Guid.NewGuid(), MonitoringSource.Events, 0,
            events: [MonitoringSamples.EventObservation(tdr, 0, timestamp: Now.AddHours(-1))],
            from: Now.AddHours(-2), to: Now.AddMinutes(-30));
        var monitoring = StoreWith(SessionWith(segment, MonitoringSources.Events), segment);

        var reader = new CorrelationEvidenceReader(events: events, monitoring: monitoring);
        var runner = new EvidenceCorrelationRunner(reader, CorrelationSamples.Clock());
        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Events));

        var source = investigation.Sources.Single(item => item.Source == CorrelationSource.Monitoring);
        Assert.Equal(CorrelationSourceStatus.Skipped, source.Status);
        Assert.All(investigation.Evidence, item =>
            Assert.DoesNotContain(item.Origins,
                origin => origin.Source == CorrelationSource.Monitoring));
    }
}
