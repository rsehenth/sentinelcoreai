using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class GraphicsInvestigationRunnerTests
{
    [Fact]
    public async Task MultipleAdaptersKeepStableIdentityAndOnlyReportedMetrics()
    {
        var gpu = GraphicsSamples.Hardware("gpu", HardwareCategory.Gpu, _ =>
        [
            GraphicsSamples.Gpu("gpu-a", "GPU A", "31.0.15.3623", 4294967295),
            GraphicsSamples.Gpu("gpu-b", "GPU B", "30.0.14.7111", 2147483648)
        ]);

        var investigation = await GraphicsSamples.Runner(hardware: GraphicsSamples.RealisticHardware(gpu))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        Assert.Equal(2, investigation.Adapters.Count);
        Assert.Equal(2, investigation.Adapters.Select(adapter => adapter.StableId).Distinct().Count());
        Assert.Equal(4294967295UL, investigation.Adapters.Single(item => item.StableId == "gpu-a")
            .ReportedAdapterRamBytes);
        Assert.All(investigation.Adapters, adapter =>
        {
            Assert.DoesNotContain(adapter.Properties, property =>
                property.Name.Contains("temperature", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("power", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("clock", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("utilization", StringComparison.OrdinalIgnoreCase));
        });
        Assert.Contains(investigation.Limitations, limitation =>
            limitation.Contains("Temperatura", StringComparison.Ordinal));
        Assert.Equal(GraphicsInvestigationStatus.Completed, investigation.Status);
        Assert.Equal(GraphicsCoverageStatus.Complete, investigation.Coverage.Status);
    }

    [Fact]
    public async Task Display4101ProducesTdrEvidenceWithoutCausalClaim()
    {
        var investigation = await GraphicsSamples.Runner(
                events: GraphicsSamples.RealisticEvents(events: [GraphicsSamples.DisplayTdr()]))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        var tdr = Assert.Single(investigation.Events);
        Assert.Equal(GraphicsEventKind.DisplayDriverReset, tdr.Kind);
        Assert.Equal(GraphicsEvidenceClassification.DisplayDriverResetEventId, tdr.EventId);
        Assert.Equal("System", tdr.Channel);
        Assert.NotNull(tdr.Message);

        var observation = Assert.Single(investigation.Observations,
            item => item.RuleId == GraphicsEvidenceAnalyzer.TdrRuleId);
        Assert.Equal(1, observation.EvidenceCount);
        Assert.Equal(EvidenceSeverity.ObservedImpact, observation.Severity);
        Assert.Equal(CausalityStatus.NotAssessed, observation.Causality);
        Assert.Contains("não identifica a causa", observation.Statement, StringComparison.Ordinal);
        Assert.Contains("não prova defeito físico", observation.Statement, StringComparison.Ordinal);
        Assert.NotEmpty(observation.Limitations);
        Assert.Equal("Log de Eventos do Windows — provedor Display", observation.Source);
    }

    [Theory]
    [InlineData("117")]
    [InlineData("141")]
    [InlineData("142")]
    public async Task LiveKernelCodesArePreservedAndClassified(string code)
    {
        var investigation = await GraphicsSamples.Runner(
                events: GraphicsSamples.RealisticEvents(
                    reports: [GraphicsSamples.LiveKernel("report-" + code, code)]))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        var report = Assert.Single(investigation.WerReports);
        Assert.Equal(code, report.ProblemCode);
        Assert.Equal($"GFX-LIVEKERNEL-{code}", Assert.Single(investigation.Observations,
            item => item.RuleId == $"GFX-LIVEKERNEL-{code}").RuleId);
        var observation = investigation.Observations.Single(item => item.RuleId == $"GFX-LIVEKERNEL-{code}");
        Assert.Equal(EvidenceConfidence.Medium, observation.Confidence);
        Assert.Contains(code, observation.Statement, StringComparison.Ordinal);
        Assert.Contains("não conclui", observation.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownWerCodeIsNotForcedIntoLiveKernelGraphics()
    {
        var investigation = await GraphicsSamples.Runner(
                events: GraphicsSamples.RealisticEvents(
                    reports: [GraphicsSamples.LiveKernel("report-999", "999")]))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        Assert.Empty(investigation.WerReports);
        Assert.DoesNotContain(investigation.Observations,
            observation => observation.RuleId.StartsWith(GraphicsEvidenceAnalyzer.LiveKernelRulePrefix,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplicationCrashIsNotPromotedToHardwareGraphicsInstability()
    {
        var investigation = await GraphicsSamples.Runner(
                events: GraphicsSamples.RealisticEvents(
                    events: [GraphicsSamples.ApplicationError()],
                    reports: [GraphicsSamples.AppCrash("app-crash")]))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        Assert.Empty(investigation.Events);
        Assert.Empty(investigation.WerReports);
        Assert.DoesNotContain(investigation.Observations,
            observation => observation.Kind == GraphicsObservationKind.DisplayDriverReset);
        Assert.Contains(investigation.Limitations, limitation =>
            limitation.Contains("Nenhuma evidência gráfica", StringComparison.Ordinal));
        Assert.All(investigation.Observations, observation =>
            Assert.Equal(CausalityStatus.NotAssessed, observation.Causality));
    }

    [Fact]
    public async Task UnavailableGpuSourceIsExplicitAndCoverageIsPartial()
    {
        var failingGpu = new GraphicsSamples.FakeHardwareCollector("gpu", HardwareCategory.Gpu, "1.0.0",
            _ => new HardwareCollectorResult("gpu", "1.0.0", HardwareCategory.Gpu, "FakeGraphicsCollector",
                GraphicsSamples.StartUtc, GraphicsSamples.StartUtc, TimeSpan.Zero, HardwareCollectionStatus.Failed,
                [], ["Fonte de GPU indisponível neste teste."]));

        var investigation = await GraphicsSamples.Runner(
                hardware: GraphicsSamples.RealisticHardware(gpu: failingGpu))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        Assert.Empty(investigation.Adapters);
        Assert.True(investigation.Coverage.AdaptersUnavailable);
        Assert.Equal(GraphicsCoverageStatus.Partial, investigation.Coverage.Status);
        Assert.Equal(GraphicsInvestigationStatus.Partial, investigation.Status);
        Assert.Contains("gpu", investigation.Coverage.UnavailableSources);
        Assert.Contains(investigation.Limitations, limitation =>
            limitation.Contains("Adaptadores gráficos indisponíveis", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationPreservesCollectedEvidenceAndMarksExecutionCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var gpu = new GraphicsSamples.FakeHardwareCollector("gpu", HardwareCategory.Gpu, "1.0.0", _ =>
        {
            cancellation.Cancel();
            return new HardwareCollectorResult("gpu", "1.0.0", HardwareCategory.Gpu, "FakeGraphicsCollector",
                GraphicsSamples.StartUtc, GraphicsSamples.StartUtc.AddMilliseconds(5), TimeSpan.FromMilliseconds(5),
                HardwareCollectionStatus.Completed, [GraphicsSamples.Gpu()], []);
        });

        var investigation = await GraphicsSamples.Runner(hardware: GraphicsSamples.RealisticHardware(gpu: gpu))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours),
                cancellationToken: cancellation.Token);

        Assert.Equal(GraphicsExecutionStatus.Cancelled, investigation.Coverage.Execution);
        Assert.Equal(GraphicsInvestigationStatus.Cancelled, investigation.Status);
        Assert.Equal(GraphicsCoverageStatus.Partial, investigation.Coverage.Status);
        Assert.Single(investigation.Adapters);
        Assert.Contains(investigation.Limitations, limitation =>
            limitation.Contains("cancelada", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DriverAndMonitorChangesAreDetectedBetweenInvestigations()
    {
        var runner = GraphicsSamples.Runner();
        var first = await runner.RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        var changedGpu = GraphicsSamples.Hardware("gpu", HardwareCategory.Gpu, _ =>
            [GraphicsSamples.Gpu(driverVersion: "32.0.15.5200")]);
        var changedMonitor = GraphicsSamples.Hardware("monitor", HardwareCategory.Monitor, _ =>
            [GraphicsSamples.Monitor(width: 2560, height: 1440, refresh: "144", hdr: true)]);

        var second = await GraphicsSamples.Runner(
                hardware: GraphicsSamples.RealisticHardware(changedGpu, changedMonitor))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours), first);

        Assert.True(second.ChangesComparable);
        Assert.Contains(second.Changes, change => change.Type == GraphicsChangeType.DriverChanged &&
            change.Field == "driver_version");
        Assert.Contains(second.Changes, change => change.Type == GraphicsChangeType.ConfigurationChanged &&
            change.Field == "width");
        Assert.Equal(first.Id, second.PreviousInvestigationId);
        Assert.Contains(second.Observations,
            observation => observation.Kind == GraphicsObservationKind.DriverChange);
        Assert.All(second.Observations, observation =>
            Assert.Equal(CausalityStatus.NotAssessed, observation.Causality));
    }

    [Fact]
    public async Task MissingPreviousInvestigationMakesComparisonNotEvaluated()
    {
        var investigation = await GraphicsSamples.Runner()
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));
        Assert.False(investigation.ChangesComparable);
        Assert.Empty(investigation.Changes);
        Assert.Contains(investigation.AnalysisNotes, note =>
            note.RuleId == GraphicsEvidenceAnalyzer.AdapterChangeRuleId && !note.Evaluated);
    }

    [Fact]
    public async Task CurrentGpuFailureNeverBecomesAdapterRemoval()
    {
        var first = await GraphicsSamples.Runner()
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));
        var failingGpu = new GraphicsSamples.FakeHardwareCollector("gpu", HardwareCategory.Gpu, "1.0.0",
            _ => new HardwareCollectorResult("gpu", "1.0.0", HardwareCategory.Gpu, "FakeGraphicsCollector",
                GraphicsSamples.StartUtc, GraphicsSamples.StartUtc, TimeSpan.Zero, HardwareCollectionStatus.Failed,
                [], ["GPU indisponível nesta execução."]));

        var second = await GraphicsSamples.Runner(hardware: GraphicsSamples.RealisticHardware(gpu: failingGpu))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours), first);

        Assert.DoesNotContain(second.Changes, change => change.Type == GraphicsChangeType.AdapterRemoved);
        Assert.DoesNotContain(second.Observations,
            observation => observation.RuleId == GraphicsEvidenceAnalyzer.AdapterChangeRuleId);
        Assert.Contains(second.AnalysisNotes, note =>
            note.RuleId == GraphicsEvidenceAnalyzer.AdapterChangeRuleId &&
            note.Reason == GraphicsNoteReason.PreconditionNotObserved);
        Assert.Contains(second.Limitations, limitation =>
            limitation.Contains("a ausência de um adaptador não indica remoção", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviousTruncatedReadingNeverBecomesAdapterAdded()
    {
        var twoAdapters = GraphicsSamples.Hardware("gpu", HardwareCategory.Gpu, _ =>
            [GraphicsSamples.Gpu("gpu-a", "GPU A"), GraphicsSamples.Gpu("gpu-b", "GPU B")]);
        var truncated = await GraphicsSamples.Runner(
                hardware: GraphicsSamples.RealisticHardware(gpu: twoAdapters),
                limits: new GraphicsLimits { MaxAdapters = 1 })
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));
        var full = await GraphicsSamples.Runner(hardware: GraphicsSamples.RealisticHardware(gpu: twoAdapters))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours), truncated);

        Assert.DoesNotContain(full.Changes, change => change.Type == GraphicsChangeType.AdapterAdded);
        Assert.Contains(full.AnalysisNotes, note =>
            note.RuleId == GraphicsEvidenceAnalyzer.AdapterChangeRuleId &&
            note.Reason == GraphicsNoteReason.PreconditionNotObserved);
    }

    [Fact]
    public async Task EventProjectionAppliesItsOwnMessageLimit()
    {
        var longMessage = new string('x', 400);
        var investigation = await GraphicsSamples.Runner(
                events: GraphicsSamples.RealisticEvents(
                    events: [GraphicsSamples.DisplayTdr(message: longMessage)]),
                limits: new GraphicsLimits { MaxMessageChars = 40 })
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        var evidence = Assert.Single(investigation.Events);
        Assert.Equal(40, evidence.Message!.Length);
        Assert.True(evidence.MessageTruncated);
        Assert.Contains(evidence.Limitations, limitation =>
            limitation.Contains("limite de evidência gráfica", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnavailableEventSourcesProduceUnavailableNotesNotAbsenceClaims()
    {
        var investigation = await GraphicsSamples.Runner(events:
            [
                new GraphicsSamples.FailingEventCollector(GraphicsEvidenceClassification.EventLogCollectorId,
                    "Windows Event Log (leitura nativa)"),
                new GraphicsSamples.FailingEventCollector(GraphicsEvidenceClassification.WerMetadataCollectorId,
                    "Windows Error Reporting (metadados)")
            ]).RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        var availabilityNotes = investigation.AnalysisNotes.Where(note =>
            note.RuleId == GraphicsEvidenceAnalyzer.TdrRuleId ||
            note.RuleId.StartsWith(GraphicsEvidenceAnalyzer.LiveKernelRulePrefix, StringComparison.Ordinal) ||
            note.RuleId == GraphicsEvidenceAnalyzer.WerRuleId).ToArray();
        Assert.NotEmpty(availabilityNotes);
        Assert.All(availabilityNotes, note =>
        {
            Assert.False(note.Evaluated);
            Assert.Equal(GraphicsNoteReason.Unavailable, note.Reason);
        });
        Assert.DoesNotContain(availabilityNotes, note => note.Detail.Contains("Nenhum evento", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CurrentMonitorFailureOrTruncationNeverBecomesMonitorRemoval()
    {
        var first = await GraphicsSamples.Runner()
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        var failingMonitor = new GraphicsSamples.FakeHardwareCollector("monitor", HardwareCategory.Monitor,
            "1.0.0", _ => new HardwareCollectorResult("monitor", "1.0.0", HardwareCategory.Monitor,
                "FakeGraphicsCollector", GraphicsSamples.StartUtc, GraphicsSamples.StartUtc, TimeSpan.Zero,
                HardwareCollectionStatus.Failed, [], ["Monitor indisponível nesta execução."]));
        var failed = await GraphicsSamples.Runner(
                hardware: GraphicsSamples.RealisticHardware(monitor: failingMonitor))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours), first);
        Assert.DoesNotContain(failed.Changes, change => change.Type == GraphicsChangeType.MonitorRemoved);

        var twoMonitors = GraphicsSamples.Hardware("monitor", HardwareCategory.Monitor, _ =>
            [GraphicsSamples.Monitor("monitor-a", "Monitor A"), GraphicsSamples.Monitor("monitor-b", "Monitor B")]);
        var second = await GraphicsSamples.Runner(hardware: GraphicsSamples.RealisticHardware(monitor: twoMonitors))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));
        var truncated = await GraphicsSamples.Runner(
                hardware: GraphicsSamples.RealisticHardware(monitor: twoMonitors),
                limits: new GraphicsLimits { MaxMonitors = 1 })
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours), second);

        Assert.Single(truncated.Monitors);
        var monitorSource = truncated.Sources.Single(item => item.Category == GraphicsSourceCategory.Monitors);
        Assert.True(monitorSource.LimitHit);
        Assert.Contains(monitorSource.Limitations, limitation =>
            limitation.Contains("Limite interno", StringComparison.Ordinal));
        Assert.DoesNotContain(truncated.Changes, change => change.Type == GraphicsChangeType.MonitorRemoved);
        Assert.Contains(truncated.AnalysisNotes, note =>
            note.RuleId == GraphicsEvidenceAnalyzer.MonitorChangeRuleId && !note.Evaluated);
    }

    [Fact]
    public async Task DriverVersionChangeIsRecordedExactlyOnce()
    {
        var first = await GraphicsSamples.Runner()
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));
        var changedGpu = GraphicsSamples.Hardware("gpu", HardwareCategory.Gpu, _ =>
            [GraphicsSamples.Gpu(driverVersion: "32.0.15.5200")]);
        var second = await GraphicsSamples.Runner(hardware: GraphicsSamples.RealisticHardware(changedGpu))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours), first);

        var versionChanges = second.Changes.Where(change =>
            change.Type == GraphicsChangeType.DriverChanged && change.Field == "driver_version").ToArray();
        Assert.Single(versionChanges);
        var observation = Assert.Single(second.Observations,
            item => item.RuleId == GraphicsEvidenceAnalyzer.DriverChangeRuleId);
        Assert.Equal(1, observation.EvidenceCount);
        Assert.Single(second.Observations, item => item.Kind == GraphicsObservationKind.DriverChange);
    }

    [Fact]
    public async Task NonLiveKernelReportWithMatchingCodeIsNotPromoted()
    {
        var investigation = await GraphicsSamples.Runner(
                events: GraphicsSamples.RealisticEvents(reports:
                [
                    GraphicsSamples.LiveKernel("blue", "141", eventType: "BlueScreen"),
                    GraphicsSamples.LiveKernel("app", "117", eventType: "AppCrash")
                ]))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        Assert.Empty(investigation.WerReports);
        Assert.DoesNotContain(investigation.Observations,
            observation => observation.Kind == GraphicsObservationKind.LiveKernelEvent);
    }

    [Fact]
    public async Task WerReportOutsideTheRequestedWindowIsExcludedAndQualityIsDeclared()
    {
        var outside = GraphicsSamples.LiveKernel("outside", "141",
            at: GraphicsSamples.StartUtc.AddDays(-30));
        var investigation = await GraphicsSamples.Runner(
                events: GraphicsSamples.RealisticEvents(reports: [outside]))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        Assert.Empty(investigation.WerReports);
        Assert.Contains(investigation.Limitations, limitation =>
            limitation.Contains(EventQualityRules.WerWindowRuleId, StringComparison.Ordinal));
        Assert.DoesNotContain(investigation.Observations,
            observation => observation.Kind == GraphicsObservationKind.LiveKernelEvent);
    }

    [Fact]
    public async Task DriverSourceReportsRawReadsAndOnlyGraphicalEvidence()
    {
        var driverCollector = GraphicsSamples.Hardware("driver", HardwareCategory.Driver, _ =>
        [
            GraphicsSamples.Driver("display-a", "Display A", hardwareId: "PCI\\VEN_1111"),
            GraphicsSamples.Driver("display-b", "Display B", hardwareId: "PCI\\VEN_2222"),
            GraphicsSamples.Driver("net-a", "Rede A", deviceClass: "Net", hardwareId: "PCI\\VEN_3333"),
            GraphicsSamples.Driver("net-b", "Rede B", deviceClass: "Net", hardwareId: "PCI\\VEN_4444"),
            GraphicsSamples.Driver("usb-a", "USB A", deviceClass: "USB", hardwareId: "USB\\VID_5555")
        ]);

        var investigation = await GraphicsSamples.Runner(
                hardware: GraphicsSamples.RealisticHardware(driver: driverCollector))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        var source = investigation.Sources.Single(item => item.CollectorId == "driver");
        Assert.Equal(5, source.RecordsRead);
        Assert.Equal(investigation.Drivers.Count, source.EvidenceCount);
        Assert.Equal(3, investigation.Drivers.Count);
    }

    [Fact]
    public async Task AdapterLimitProducesExplicitPartialCoverage()
    {
        var twoAdapters = GraphicsSamples.Hardware("gpu", HardwareCategory.Gpu, _ =>
            [GraphicsSamples.Gpu("gpu-a", "GPU A"), GraphicsSamples.Gpu("gpu-b", "GPU B")]);

        var investigation = await GraphicsSamples.Runner(
                hardware: GraphicsSamples.RealisticHardware(gpu: twoAdapters),
                limits: new GraphicsLimits { MaxAdapters = 1 })
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        Assert.Single(investigation.Adapters);
        Assert.Equal(GraphicsCoverageStatus.Partial, investigation.Coverage.Status);
        var source = investigation.Sources.Single(item => item.CollectorId == "gpu");
        Assert.True(source.LimitHit);
        Assert.Contains(source.Limitations, limitation =>
            limitation.Contains("Limite interno", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationBeforeTheFirstSourceProducesPersistableCancelledRecord()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var investigation = await GraphicsSamples.Runner()
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours),
                cancellationToken: cancellation.Token);

        Assert.Equal(GraphicsInvestigationStatus.Cancelled, investigation.Status);
        Assert.Empty(investigation.Sources);
        Assert.Null(investigation.EventReadingId);
        Assert.Equal(GraphicsCoverageStatus.Unavailable, investigation.Coverage.Status);
    }

    [Fact]
    public async Task InvalidCollectorsAndWindowsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new GraphicsInvestigationRunner([], []));
        var duplicate = GraphicsSamples.Hardware("gpu", HardwareCategory.Gpu, _ => [GraphicsSamples.Gpu()]);
        Assert.Throws<ArgumentException>(() => new GraphicsInvestigationRunner(
            [duplicate, duplicate], GraphicsSamples.RealisticEvents()));

        var runner = GraphicsSamples.Runner();
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
            new GraphicsInvestigationRequest(EventWindow.Custom)));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
            new GraphicsInvestigationRequest(EventWindow.Custom,
                GraphicsSamples.StartUtc.AddDays(-1), GraphicsSamples.StartUtc.AddDays(-2))));
    }
}
