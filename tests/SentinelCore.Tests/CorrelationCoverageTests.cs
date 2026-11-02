using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class CorrelationCoverageTests
{
    private static (EvidenceCorrelationRunner Runner, CorrelationSamples.FakeAuditStore Audits,
        CorrelationSamples.FakeHardwareStore Hardware, CorrelationSamples.FakeEventStore Events,
        CorrelationSamples.FakePerformanceStore Performance, CorrelationSamples.FakeNetworkStore Network,
        CorrelationSamples.FakeGraphicsStore Graphics) Build(CorrelationLimits? limits = null)
    {
        var audits = new CorrelationSamples.FakeAuditStore();
        var hardware = new CorrelationSamples.FakeHardwareStore();
        var events = new CorrelationSamples.FakeEventStore();
        var performance = new CorrelationSamples.FakePerformanceStore();
        var network = new CorrelationSamples.FakeNetworkStore();
        var graphics = new CorrelationSamples.FakeGraphicsStore();
        var options = limits ?? CorrelationLimits.Default;
        var reader = new CorrelationEvidenceReader(audits, hardware, events, performance, network, graphics,
            options);
        return (new EvidenceCorrelationRunner(reader, CorrelationSamples.Clock(), options), audits, hardware,
            events, performance, network, graphics);
    }

    [Fact]
    public async Task CompleteSourcesProduceCompletedInvestigationWithCompleteCoverage()
    {
        var (runner, audits, _, events, _, _, _) = Build();
        audits.Records.Add(CorrelationSamples.AuditWithBoot(CorrelationSamples.Now.AddHours(-5)));
        events.Records.Add(await CorrelationSamples.EventsAsync(events:
            [GraphicsSamples.DisplayTdr(recordId: 1, at: CorrelationSamples.Now.AddHours(-2))]));

        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Audits | CorrelationSources.Events));

        Assert.Equal(CorrelationStatus.Completed, investigation.Status);
        Assert.Equal(CorrelationCoverageStatus.Complete, investigation.Coverage.Status);
        Assert.Equal(2, investigation.Coverage.ExpectedSources);
        Assert.Equal(2, investigation.Coverage.CompletedSources);
        Assert.NotEmpty(investigation.Evidence);
        Assert.Contains(investigation.Limitations, item => item == CorrelationGuidance.CausalityLimitation);
        Assert.Contains(investigation.Limitations, item => item == CorrelationGuidance.ScopeLimitation);
    }

    [Fact]
    public async Task UnavailableStoreDowngradesCoverageButKeepsAvailableEvidence()
    {
        var (runner, _, _, events, performance, _, _) = Build();
        events.Records.Add(await CorrelationSamples.EventsAsync(events:
            [GraphicsSamples.DisplayTdr(recordId: 2, at: CorrelationSamples.Now.AddHours(-2))]));
        performance.Failure = new IOException("armazenamento indisponível");

        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Events | CorrelationSources.Performance));

        Assert.Equal(CorrelationStatus.Partial, investigation.Status);
        Assert.Equal(CorrelationCoverageStatus.Partial, investigation.Coverage.Status);
        Assert.Equal(1, investigation.Coverage.CompletedSources);
        Assert.Equal(1, investigation.Coverage.UnavailableSources);
        var source = Assert.Single(investigation.Sources,
            item => item.Source == CorrelationSource.Performance);
        Assert.Equal(CorrelationSourceStatus.Unavailable, source.Status);
        Assert.Contains(source.Limitations, item =>
            item.Contains("indisponível", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(investigation.Evidence);
    }

    [Fact]
    public async Task AllSelectedSourcesUnavailableProducesFailedInvestigation()
    {
        var (runner, _, _, events, performance, _, _) = Build();
        events.Failure = new IOException("sem eventos");
        performance.Failure = new IOException("sem desempenho");

        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Events | CorrelationSources.Performance));

        Assert.Equal(CorrelationStatus.Failed, investigation.Status);
        Assert.Equal(CorrelationCoverageStatus.Unavailable, investigation.Coverage.Status);
        Assert.Equal(2, investigation.Coverage.UnavailableSources);
        Assert.Contains(investigation.Limitations, item =>
            item.Contains("Todas as fontes selecionadas", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SourceWithoutRecordsCoveringTheWindowIsReportedAsStale()
    {
        var (runner, _, _, events, _, _, _) = Build();
        events.Records.Add(await CorrelationSamples.EventsAsync(now: CorrelationSamples.Now.AddDays(-3), events:
            [GraphicsSamples.DisplayTdr(recordId: 3, at: CorrelationSamples.Now.AddDays(-3))]));

        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Events));

        Assert.Equal(CorrelationStatus.Partial, investigation.Status);
        var source = Assert.Single(investigation.Sources,
            item => item.Source == CorrelationSource.Events);
        Assert.Equal(CorrelationSourceStatus.Partial, source.Status);
        Assert.Contains(source.Limitations, item =>
            item.Contains("desatualizada", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(investigation.Evidence);
        Assert.Contains(investigation.Relations,
            item => item.Kind == CorrelationRelationKind.InsufficientEvidence);
    }

    [Fact]
    public async Task NoResultsIsNotUnavailabilityAndRemainsExplicit()
    {
        var (runner, audits, _, _, _, _, _) = Build();
        audits.Records.Add(CorrelationSamples.AuditWithBoot(CorrelationSamples.Now.AddHours(-2)));

        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Audits | CorrelationSources.Events));

        Assert.Equal(CorrelationStatus.Partial, investigation.Status);
        Assert.Equal(CorrelationCoverageStatus.Partial, investigation.Coverage.Status);
        Assert.Empty(investigation.Evidence);
        var events = Assert.Single(investigation.Sources, item => item.Source == CorrelationSource.Events);
        Assert.Equal(CorrelationSourceStatus.Partial, events.Status);
        Assert.Contains(events.Limitations, item =>
            item.Contains("Nenhum registro histórico persistido", StringComparison.Ordinal));
        var relation = Assert.Single(investigation.Relations);
        Assert.Equal(CorrelationRelationKind.InsufficientEvidence, relation.Kind);
        Assert.StartsWith(CorrelationGuidance.InsufficientEvidenceStatement, relation.Statement,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvidenceLimitIsExplicitAndDowngradesCoverage()
    {
        var limits = new CorrelationLimits { MaxEvidence = 1 };
        var (runner, _, _, events, _, _, _) = Build(limits);
        events.Records.Add(await CorrelationSamples.EventsAsync(events:
        [
            GraphicsSamples.DisplayTdr(recordId: 10, at: CorrelationSamples.Now.AddHours(-2)),
            GraphicsSamples.DisplayTdr(recordId: 11, at: CorrelationSamples.Now.AddHours(-2).AddSeconds(5))
        ]));

        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Events, Limits: limits));

        Assert.Equal(CorrelationStatus.Partial, investigation.Status);
        Assert.True(investigation.Coverage.LimitHit);
        Assert.Single(investigation.Evidence);
        Assert.Contains(investigation.Limitations, item => item == CorrelationGuidance.LimitLimitation);
    }

    [Fact]
    public async Task PartialHistoricalRecordsDowngradeCorrelationCoverage()
    {
        var (runner, audits, hardware, events, performance, network, graphics) = Build();
        audits.Records.Add(CorrelationSamples.AuditWithBoot(CorrelationSamples.Now.AddHours(-5)) with
        {
            Status = DiagnosticStatus.Partial
        });
        hardware.Records.Add(CorrelationSamples.Snapshot(CorrelationSamples.Now.AddHours(-3)) with
        {
            Status = HardwareCollectionStatus.Partial
        });
        var tdr = GraphicsSamples.DisplayTdr(recordId: 30, at: CorrelationSamples.Now.AddHours(-2));
        events.Records.Add((await CorrelationSamples.EventsAsync(events: [tdr])) with
        {
            Status = EventInvestigationStatus.Partial
        });
        var performanceSession = CorrelationSamples.Performance();
        performance.Records.Add(performanceSession with
        {
            Coverage = (performanceSession.Coverage ?? PerformanceCoverage.FromLegacy(performanceSession)) with
            {
                Status = PerformanceCoverageStatus.Partial
            }
        });
        var networkSession = CorrelationSamples.Network();
        network.Records.Add(networkSession with
        {
            Coverage = networkSession.Coverage! with { Status = NetworkCoverageStatus.Partial }
        });
        var graphicsInvestigation = await CorrelationSamples.GraphicsAsync(events: [tdr]);
        graphics.Records.Add(graphicsInvestigation with
        {
            Status = GraphicsInvestigationStatus.Partial,
            Coverage = graphicsInvestigation.Coverage with { Status = GraphicsCoverageStatus.Partial }
        });

        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Audits | CorrelationSources.Hardware | CorrelationSources.Events |
                     CorrelationSources.Performance | CorrelationSources.Network | CorrelationSources.Graphics));

        Assert.Equal(CorrelationStatus.Partial, investigation.Status);
        Assert.Equal(CorrelationCoverageStatus.Partial, investigation.Coverage.Status);
        Assert.Equal(6, investigation.Coverage.PartialSources);
        Assert.Equal(0, investigation.Coverage.CompletedSources);
        Assert.All(investigation.Sources.Where(source => source.Status != CorrelationSourceStatus.Skipped), source =>
        {
            Assert.Equal(CorrelationSourceStatus.Partial, source.Status);
            Assert.Contains(source.Limitations,
                item => item.Contains("registro(s) de origem", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task RunnerConfiguredLimitsApplyWhenRequestOmitsThem()
    {
        var limits = new CorrelationLimits { MaxEvidence = 1 };
        var (runner, _, _, events, _, _, _) = Build(limits);
        events.Records.Add(await CorrelationSamples.EventsAsync(events:
        [
            GraphicsSamples.DisplayTdr(recordId: 20, at: CorrelationSamples.Now.AddHours(-2)),
            GraphicsSamples.DisplayTdr(recordId: 21, at: CorrelationSamples.Now.AddHours(-2).AddSeconds(5))
        ]));

        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Events));

        Assert.Equal(CorrelationStatus.Partial, investigation.Status);
        Assert.True(investigation.Coverage.LimitHit);
        Assert.Single(investigation.Evidence);
    }

    [Fact]
    public async Task WarningsFromAStoreDowngradeThatSourceToPartial()
    {
        var (runner, _, _, events, _, _, _) = Build();
        events.Records.Add(await CorrelationSamples.EventsAsync(events:
            [GraphicsSamples.DisplayTdr(recordId: 12, at: CorrelationSamples.Now.AddHours(-2))]));
        events.Warnings.Add("Registro X não recuperado; original preservado.");

        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Events));

        var source = Assert.Single(investigation.Sources,
            item => item.Source == CorrelationSource.Events);
        Assert.Equal(CorrelationSourceStatus.Partial, source.Status);
        Assert.Equal(CorrelationStatus.Partial, investigation.Status);
    }

    [Fact]
    public async Task CancellationBeforeReadingProducesCancelledInvestigationWithoutFabricatedEvidence()
    {
        var (runner, _, _, events, _, _, _) = Build();
        events.Records.Add(await CorrelationSamples.EventsAsync(events:
            [GraphicsSamples.DisplayTdr(recordId: 13, at: CorrelationSamples.Now.AddHours(-2))]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Events), cancellationToken: cancellation.Token);

        Assert.Equal(CorrelationStatus.Cancelled, investigation.Status);
        Assert.Equal(CorrelationExecutionStatus.Cancelled, investigation.Execution);
        Assert.Equal(CorrelationCoverageStatus.Unavailable, investigation.Coverage.Status);
        Assert.Empty(investigation.Evidence);
        Assert.Contains(investigation.Limitations, item =>
            item.Contains("nenhuma evidência foi consolidada", StringComparison.OrdinalIgnoreCase));
        var source = Assert.Single(investigation.Sources,
            item => item.Source == CorrelationSource.Events);
        Assert.Equal(CorrelationSourceStatus.Cancelled, source.Status);
    }

    [Fact]
    public async Task SourcesNotSelectedAreSkippedAndDoNotCountAsExpected()
    {
        var (runner, _, _, events, _, _, _) = Build();
        events.Records.Add(await CorrelationSamples.EventsAsync(events:
            [GraphicsSamples.DisplayTdr(recordId: 14, at: CorrelationSamples.Now.AddHours(-2))]));

        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Events));

        Assert.Equal(1, investigation.Coverage.ExpectedSources);
        Assert.All(investigation.Sources.Where(item => item.Source != CorrelationSource.Events),
            item => Assert.Equal(CorrelationSourceStatus.Skipped, item.Status));
        Assert.Equal(CorrelationStatus.Completed, investigation.Status);
    }

    [Fact]
    public async Task UndatedEvidenceDowngradesCoverageToPartial()
    {
        var (runner, _, _, events, _, _, _) = Build();
        events.Records.Add(await CorrelationSamples.EventsAsync(reports:
        [
            new RawWerReport("wer-undated", true, "Sistema", "ReportArchive", "Kernel_117",
                "LiveKernelEvent", "117", null, null, "Loaded", "app.exe", null, "dxgkrnl.sys", "10.0",
                "bucket", [], [], EvidenceConfidence.High)
        ]));

        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Events));

        Assert.Equal(CorrelationStatus.Partial, investigation.Status);
        Assert.Equal(CorrelationCoverageStatus.Partial, investigation.Coverage.Status);
        Assert.Equal(1, investigation.Coverage.UndatedEvidence);
        Assert.Contains(investigation.Limitations, item =>
            item.Contains("sem horário confiável", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReaderDistinguishesUnavailableStorageFromMissingComposition()
    {
        var events = new CorrelationSamples.FakeEventStore();
        events.Records.Add(await CorrelationSamples.EventsAsync(events:
            [GraphicsSamples.DisplayTdr(recordId: 90, at: CorrelationSamples.Now.AddHours(-2))]));
        var reader = new CorrelationEvidenceReader(events: events);

        var input = await reader.ReadAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Events | CorrelationSources.Network));

        Assert.Equal(CorrelationSourceStatus.Completed, Assert.Single(input.SourceReads,
            item => item.Source == CorrelationSource.Events).Status);
        var network = Assert.Single(input.SourceReads, item => item.Source == CorrelationSource.Network);
        Assert.Equal(CorrelationSourceStatus.Unavailable, network.Status);
        Assert.Contains(network.Limitations, item => item.Contains("não disponível", StringComparison.Ordinal));
        Assert.Equal(CorrelationSourceStatus.Skipped, Assert.Single(input.SourceReads,
            item => item.Source == CorrelationSource.Graphics).Status);
    }
}
