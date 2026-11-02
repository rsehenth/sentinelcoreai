using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class CorrelationRuleTests
{
    private static CorrelationInvestigation Analyze(CorrelationEvidenceInput input,
        CorrelationLimits? limits = null) => CorrelationSamples.NormalizeAndAnalyze(input, limits);

    private static async Task<EventInvestigation> EventGraphAsync(DateTimeOffset at, long recordId,
        bool withApplicationFailure, DateTimeOffset? werAt = null)
    {
        var events = new List<RawEventRecord> { GraphicsSamples.DisplayTdr(recordId: recordId, at: at) };
        if (withApplicationFailure)
            events.Add(GraphicsSamples.ApplicationError(recordId: recordId + 1, at: at.AddSeconds(42)));
        var reports = werAt is null
            ? Array.Empty<RawWerReport>()
            : [GraphicsSamples.LiveKernel("wer-lk-117", "117", at: werAt)];
        return await CorrelationSamples.EventsAsync(events: events, reports: reports);
    }

    [Fact]
    public async Task GraphicsEventAndWerWithinTheRuleWindowProduceTemporalAssociation()
    {
        var at = CorrelationSamples.Now.AddHours(-2);
        var investigation = await EventGraphAsync(at, 200, withApplicationFailure: false,
            werAt: at.AddSeconds(60));
        var result = Analyze(CorrelationSamples.Input(events: [investigation],
            sources: CorrelationSources.Events));

        var relation = Assert.Single(result.Relations,
            item => item.RuleId == CorrelationGuidance.GraphicsWerRuleId);
        Assert.Equal(CorrelationRelationKind.TemporalAssociation, relation.Kind);
        Assert.Equal(CorrelationGuidance.RuleVersion, relation.RuleVersion);
        Assert.Equal(CausalityStatus.NotAssessed, relation.Causality);
        Assert.Equal(60, relation.MaximumGapSeconds!.Value, 3);
        Assert.Equal(2, relation.EvidenceIds.Count);
        Assert.Contains(relation.Limitations, limitation =>
            limitation == CorrelationGuidance.CausalityLimitation);
    }

    [Fact]
    public async Task WerOutsideTheRuleWindowProducesNoAssociation()
    {
        var at = CorrelationSamples.Now.AddHours(-2);
        var investigation = await EventGraphAsync(at, 201, withApplicationFailure: false,
            werAt: at.AddMinutes(10));
        var result = Analyze(CorrelationSamples.Input(events: [investigation],
            sources: CorrelationSources.Events));

        Assert.DoesNotContain(result.Relations,
            item => item.RuleId == CorrelationGuidance.GraphicsWerRuleId);
        var note = Assert.Single(result.AnalysisNotes,
            item => item.RuleId == CorrelationGuidance.GraphicsWerRuleId);
        Assert.Equal(CorrelationNoteReason.ConditionNotObserved, note.Reason);
    }

    [Fact]
    public async Task RuleWindowBoundaryIsInclusiveAndOneSecondBeyondIsRejected()
    {
        var at = CorrelationSamples.Now.AddHours(-2);
        var inside = Analyze(CorrelationSamples.Input(events:
            [await EventGraphAsync(at, 202, false, at.AddSeconds(300))], sources: CorrelationSources.Events));
        var outside = Analyze(CorrelationSamples.Input(events:
            [await EventGraphAsync(at, 203, false, at.AddSeconds(301))], sources: CorrelationSources.Events));

        Assert.Single(inside.Relations, item => item.RuleId == CorrelationGuidance.GraphicsWerRuleId);
        Assert.DoesNotContain(outside.Relations,
            item => item.RuleId == CorrelationGuidance.GraphicsWerRuleId);
    }

    [Fact]
    public async Task GraphicsEventAndApplicationFailureUseTheirOwnSmallerWindow()
    {
        var at = CorrelationSamples.Now.AddHours(-2);
        var investigation = await EventGraphAsync(at, 204, withApplicationFailure: true);
        var result = Analyze(CorrelationSamples.Input(events: [investigation],
            sources: CorrelationSources.Events));

        var relation = Assert.Single(result.Relations,
            item => item.RuleId == CorrelationGuidance.GraphicsApplicationRuleId);
        Assert.Equal(42, relation.MaximumGapSeconds!.Value, 3);
        Assert.Contains("falha de aplicativo", relation.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("não atribui", relation.Statement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PerformanceObservationRequiresTemporalOverlapWithAWindowsEvent()
    {
        var from = CorrelationSamples.Now.AddHours(-2);
        var performance = CorrelationSamples.Performance(fromUtc: from, toUtc: from.AddMinutes(1));
        var events = await CorrelationSamples.EventsAsync(events:
            [GraphicsSamples.ApplicationError(recordId: 30, at: from.AddSeconds(30))]);

        var result = Analyze(CorrelationSamples.Input(performance: [performance], events: [events],
            sources: CorrelationSources.Performance | CorrelationSources.Events));

        var relation = Assert.Single(result.Relations,
            item => item.RuleId == CorrelationGuidance.PerformanceEventRuleId);
        Assert.Contains("sobrepõem", relation.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(relation.Limitations, limitation =>
            limitation.Contains("inventários estáticos", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PerformanceObservationWithoutTheEventsSourceReportsNotSelected()
    {
        var from = CorrelationSamples.Now.AddHours(-2);
        var performance = CorrelationSamples.Performance(fromUtc: from, toUtc: from.AddMinutes(1));
        var result = Analyze(CorrelationSamples.Input(performance: [performance],
            sources: CorrelationSources.Performance));

        Assert.DoesNotContain(result.Relations,
            item => item.RuleId == CorrelationGuidance.PerformanceEventRuleId);
        var note = Assert.Single(result.AnalysisNotes,
            item => item.RuleId == CorrelationGuidance.PerformanceEventRuleId);
        Assert.Equal(CorrelationNoteReason.NotSelected, note.Reason);
    }

    [Fact]
    public async Task NetworkObservationAndNetworkEventNeverClaimCausality()
    {
        var from = CorrelationSamples.NetworkAt;
        var network = CorrelationSamples.Network(fromUtc: from, toUtc: from.AddMinutes(1));
        var networkEvent = new RawEventRecord("System", "Tcpip", 4231, 31, from.AddSeconds(30),
            EventLevel.Warning, 3, null, null, "Recursos de conexão esgotados.", false, []);
        var events = await CorrelationSamples.EventsAsync(now: CorrelationSamples.Now, events: [networkEvent]);

        var result = Analyze(CorrelationSamples.Input(network: [network], events: [events],
            sources: CorrelationSources.Network | CorrelationSources.Events));

        var relation = Assert.Single(result.Relations,
            item => item.RuleId == CorrelationGuidance.NetworkEventRuleId);
        Assert.Equal(CausalityStatus.NotAssessed, relation.Causality);
        Assert.Contains("não é causa", relation.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("causou", relation.Statement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SameProcessIdentityWithOverlappingWindowsProducesIdentityAssociation()
    {
        var at = CorrelationSamples.Now.AddHours(-2);
        var performance = CorrelationSamples.Performance(subject: "4242:100000",
            fromUtc: at, toUtc: at.AddMinutes(1));
        var network = CorrelationSamples.Network(subject: "4242:100000",
            fromUtc: at.AddSeconds(30), toUtc: at.AddSeconds(50));

        var result = Analyze(CorrelationSamples.Input(performance: [performance], network: [network],
            sources: CorrelationSources.Performance | CorrelationSources.Network));

        var relation = Assert.Single(result.Relations,
            item => item.RuleId == CorrelationGuidance.ProcessIdentityRuleId);
        Assert.Equal(CorrelationRelationKind.IdentityAssociation, relation.Kind);
        Assert.Contains("PID + hora de criação", relation.Statement, StringComparison.Ordinal);
        Assert.Contains("separadas por até 60 s", relation.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void UnverifiableProcessIdentityIsNotUsedForAssociation()
    {
        var at = CorrelationSamples.Now.AddHours(-2);
        var performance = CorrelationSamples.Performance(subject: "4242:unknown",
            fromUtc: at, toUtc: at.AddMinutes(1));
        var network = CorrelationSamples.Network(subject: "4242:unknown",
            fromUtc: at.AddSeconds(10), toUtc: at.AddSeconds(40));

        var result = Analyze(CorrelationSamples.Input(performance: [performance], network: [network],
            sources: CorrelationSources.Performance | CorrelationSources.Network));

        Assert.DoesNotContain(result.Relations,
            item => item.RuleId == CorrelationGuidance.ProcessIdentityRuleId);
        Assert.Contains(result.Evidence, evidence => evidence.Limitations.Any(limitation =>
            limitation.Contains("sem hora de criação verificável", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EvidenceBeforeAndAfterBootAreNotPairedAsOneSequence()
    {
        var boot = CorrelationSamples.Now.AddHours(-2);
        var audit = CorrelationSamples.AuditWithBoot(boot, CorrelationSamples.Now.AddMinutes(-30));
        var before = GraphicsSamples.DisplayTdr(recordId: 810, at: boot.AddSeconds(-30));
        var after = GraphicsSamples.ApplicationError(recordId: 811, at: boot.AddSeconds(30));
        var investigation = await CorrelationSamples.EventsAsync(events: [before, after]);

        var withBoot = Analyze(CorrelationSamples.Input(audits: [audit], events: [investigation],
            sources: CorrelationSources.Audits | CorrelationSources.Events));
        var withoutBoot = Analyze(CorrelationSamples.Input(events: [investigation],
            sources: CorrelationSources.Events));

        Assert.DoesNotContain(withBoot.Relations,
            item => item.RuleId == CorrelationGuidance.GraphicsApplicationRuleId);
        Assert.Single(withoutBoot.Relations,
            item => item.RuleId == CorrelationGuidance.GraphicsApplicationRuleId);
    }

    [Fact]
    public async Task SameInputProducesTheSameEvidenceAndRelationOrder()
    {
        var at = CorrelationSamples.Now.AddHours(-2);
        var investigation = await EventGraphAsync(at, 900, withApplicationFailure: true,
            werAt: at.AddSeconds(45));
        var input = CorrelationSamples.Input(events: [investigation], sources: CorrelationSources.Events);

        var first = Analyze(input);
        var second = Analyze(input);

        Assert.Equal(first.Evidence.Select(item => item.Id), second.Evidence.Select(item => item.Id));
        Assert.Equal(first.Timeline.Select(item => item.EvidenceId), second.Timeline.Select(item => item.EvidenceId));
        Assert.Equal(first.Relations.Select(item => item.Id), second.Relations.Select(item => item.Id));
        Assert.Equal(first.Relations.Select(item => item.RuleId), second.Relations.Select(item => item.RuleId));
    }

    [Fact]
    public async Task EventsWithoutRecordIdAreNotMergedAcrossInvestigations()
    {
        var at = CorrelationSamples.Now.AddHours(-2);
        var raw = new RawEventRecord("System", "Display", 4101, null, at, EventLevel.Warning, 3,
            null, null, "Sem RecordId.", false, []);
        var first = await CorrelationSamples.EventsAsync(events: [raw]);
        var second = await CorrelationSamples.EventsAsync(events: [raw]);

        var result = Analyze(CorrelationSamples.Input(events: [first, second],
            sources: CorrelationSources.Events));

        Assert.Equal(2, result.Evidence.Count);
        Assert.Equal(0, result.Coverage.MergedReferences);
        Assert.All(result.Evidence, evidence => Assert.Contains(evidence.Limitations,
            limitation => limitation.Contains("sem RecordId", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task DivergentRepresentationsOfTheSameEventAreDeclared()
    {
        var at = CorrelationSamples.Now.AddHours(-2);
        var first = await CorrelationSamples.EventsAsync(events:
            [GraphicsSamples.DisplayTdr(recordId: 950, at: at)]);
        var second = await CorrelationSamples.EventsAsync(events:
            [GraphicsSamples.DisplayTdr(recordId: 950, at: at.AddSeconds(5))]);

        var result = Analyze(CorrelationSamples.Input(events: [first, second],
            sources: CorrelationSources.Events));

        var evidence = Assert.Single(result.Evidence);
        Assert.Equal(2, evidence.Origins.Count);
        Assert.Contains(evidence.Limitations, limitation =>
            limitation.Contains("divergem no horário", StringComparison.Ordinal));
    }

    [Fact]
    public void SamePidWithDifferentCreationTicksIsNotTreatedAsTheSameProcess()
    {
        var at = CorrelationSamples.Now.AddHours(-2);
        var performance = CorrelationSamples.Performance(subject: "4242:100000",
            fromUtc: at, toUtc: at.AddMinutes(1));
        var network = CorrelationSamples.Network(subject: "4242:200000",
            fromUtc: at.AddSeconds(10), toUtc: at.AddSeconds(40));

        var result = Analyze(CorrelationSamples.Input(performance: [performance], network: [network],
            sources: CorrelationSources.Performance | CorrelationSources.Network));

        Assert.DoesNotContain(result.Relations,
            item => item.RuleId == CorrelationGuidance.ProcessIdentityRuleId);
    }

    [Fact]
    public void SameIdentityInDistantWindowsIsNotAssociated()
    {
        var at = CorrelationSamples.Now.AddHours(-2);
        var performance = CorrelationSamples.Performance(subject: "4242:100000",
            fromUtc: at, toUtc: at.AddMinutes(1));
        var network = CorrelationSamples.Network(subject: "4242:100000",
            fromUtc: at.AddHours(-10), toUtc: at.AddHours(-10).AddMinutes(1));

        var result = Analyze(CorrelationSamples.Input(performance: [performance], network: [network],
            sources: CorrelationSources.Performance | CorrelationSources.Network));

        Assert.DoesNotContain(result.Relations,
            item => item.RuleId == CorrelationGuidance.ProcessIdentityRuleId);
    }

    [Fact]
    public async Task DeviceIdentityUsesTheTechnicalStableIdAndNotTheCommercialName()
    {
        var at = CorrelationSamples.Now.AddSeconds(-30);
        var hardware = CorrelationSamples.Snapshot(at,
            GraphicsSamples.Gpu(stableId: "gpu-1", name: "Nome comercial do inventário"),
            GraphicsSamples.Gpu(stableId: "gpu-2", name: "Nome compartilhado"));
        var graphics = await CorrelationSamples.GraphicsAsync(hardware:
        [
            GraphicsSamples.Hardware("gpu", HardwareCategory.Gpu, _ =>
                [GraphicsSamples.Gpu(stableId: "gpu-1", name: "Nome comercial da investigação")])
        ]);

        var result = Analyze(CorrelationSamples.Input(hardware: [hardware], graphics: [graphics],
            sources: CorrelationSources.Hardware | CorrelationSources.Graphics));

        var relation = Assert.Single(result.Relations,
            item => item.RuleId == CorrelationGuidance.DeviceIdentityRuleId);
        Assert.Contains("gpu-1", relation.Statement, StringComparison.Ordinal);
        Assert.DoesNotContain("gpu-2", relation.Statement, StringComparison.Ordinal);
        Assert.Contains("nome comercial", relation.Statement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DriverChangeFollowedByEventProducesObservedSequenceOnly()
    {
        var before = CorrelationSamples.Now.AddHours(-4);
        var after = CorrelationSamples.Now.AddHours(-1);
        var first = CorrelationSamples.Snapshot(before,
            GraphicsSamples.Gpu(stableId: "gpu-1", driverVersion: "31.0.15.1000"));
        var second = CorrelationSamples.Snapshot(after,
            GraphicsSamples.Gpu(stableId: "gpu-1", driverVersion: "32.0.16.2000"));
        var events = await CorrelationSamples.EventsAsync(events:
            [GraphicsSamples.DisplayTdr(recordId: 500, at: after.AddMinutes(30))]);

        var result = Analyze(CorrelationSamples.Input(hardware: [first, second], events: [events],
            sources: CorrelationSources.Hardware | CorrelationSources.Events));

        var sequences = result.Relations
            .Where(item => item.RuleId == CorrelationGuidance.ChangeSequenceRuleId).ToArray();
        Assert.NotEmpty(sequences);
        Assert.All(sequences, relation =>
        {
            Assert.Equal(CorrelationRelationKind.ObservedSequence, relation.Kind);
            Assert.Equal(CausalityStatus.NotAssessed, relation.Causality);
            Assert.Contains("não indica causa", relation.Statement, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task EventsBeforeTheObservedChangeAreNotAttributedToIt()
    {
        var before = CorrelationSamples.Now.AddHours(-4);
        var after = CorrelationSamples.Now.AddHours(-1);
        var first = CorrelationSamples.Snapshot(before,
            GraphicsSamples.Gpu(stableId: "gpu-1", driverVersion: "31.0.15.1000"));
        var second = CorrelationSamples.Snapshot(after,
            GraphicsSamples.Gpu(stableId: "gpu-1", driverVersion: "32.0.16.2000"));
        var events = await CorrelationSamples.EventsAsync(events:
            [GraphicsSamples.DisplayTdr(recordId: 501, at: before.AddMinutes(10))]);

        var result = Analyze(CorrelationSamples.Input(hardware: [first, second], events: [events],
            sources: CorrelationSources.Hardware | CorrelationSources.Events));

        Assert.DoesNotContain(result.Relations,
            item => item.RuleId == CorrelationGuidance.ChangeSequenceRuleId);
    }

    [Fact]
    public void CancelledTokenStopsRuleEvaluation()
    {
        var input = CorrelationSamples.Input(sources: CorrelationSources.Events);
        var window = EventWindows.Resolve(EventWindow.Last24Hours, CorrelationSamples.Now);
        var normalized = new CorrelationEvidenceNormalizer().Normalize(input, window,
            CorrelationLimits.Default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new CorrelationEngine().Analyze(
            normalized.Evidence, normalized.Sources, normalized.Boot, window, CorrelationLimits.Default,
            cancellation.Token));
    }

    [Fact]
    public void EmptyEvidenceProducesAnExplicitNotEvaluableResult()
    {
        var result = Analyze(CorrelationSamples.Input(sources: CorrelationSources.Events));

        var relation = Assert.Single(result.Relations);
        Assert.Equal(CorrelationRelationKind.InsufficientEvidence, relation.Kind);
        Assert.StartsWith(CorrelationGuidance.InsufficientEvidenceStatement, relation.Statement,
            StringComparison.Ordinal);
        Assert.Contains("não prova que não exista relação", relation.Statement,
            StringComparison.OrdinalIgnoreCase);
        var note = Assert.Single(result.AnalysisNotes,
            item => item.RuleId == CorrelationGuidance.InsufficientEvidenceRuleId);
        Assert.Equal(CorrelationNoteReason.InsufficientEvidence, note.Reason);
    }

    [Fact]
    public async Task EveryRelationDeclaresCausalityNotAssessedAndNeverAssertsACause()
    {
        var at = CorrelationSamples.Now.AddHours(-2);
        var investigation = await EventGraphAsync(at, 600, withApplicationFailure: true,
            werAt: at.AddSeconds(60));
        var result = Analyze(CorrelationSamples.Input(events: [investigation],
            sources: CorrelationSources.Events));

        Assert.NotEmpty(result.Relations);
        foreach (var relation in result.Relations)
        {
            Assert.Equal(CausalityStatus.NotAssessed, relation.Causality);
            Assert.DoesNotContain("causou", relation.Statement, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("provocou", relation.Statement, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(relation.Limitations, limitation =>
                limitation.Contains("não é causalidade", StringComparison.OrdinalIgnoreCase) ||
                limitation.Contains("não demonstram que", StringComparison.OrdinalIgnoreCase));
        }
    }
}
