using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class CorrelationNormalizationTests
{
    private static RawWerReport WerWithoutEventTime(DateTimeOffset? creationUtc, string reportId = "wer-1") =>
        new(reportId, true, "Sistema", "ReportArchive", "Kernel_117", "LiveKernelEvent", "117",
            null, creationUtc, "Loaded", "app.exe", null, "dxgkrnl.sys", "10.0", "bucket", [], [],
            EvidenceConfidence.High);

    [Fact]
    public async Task EventTimestampIsNormalizedToUtcAndProvenanceIsPreserved()
    {
        var withOffset = new RawEventRecord("System", "Display", 4101, 77,
            new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.FromHours(3)), EventLevel.Warning, 3,
            null, null, "Reinício do driver.", false, []);
        var investigation = await CorrelationSamples.EventsAsync(events: [withOffset]);
        var result = CorrelationSamples.NormalizeAndAnalyze(
            CorrelationSamples.Input(events: [investigation], sources: CorrelationSources.Events));

        var evidence = Assert.Single(result.Evidence);
        Assert.Equal(TimeSpan.Zero, evidence.EffectiveTimestampUtc!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), evidence.EffectiveTimestampUtc);
        Assert.Equal(CorrelationTimestampPrecision.Exact, evidence.Precision);
        Assert.Equal(CorrelationIdentityKind.EventRecord, evidence.IdentityKind);
        var origin = Assert.Single(evidence.Origins);
        Assert.Equal(investigation.Id.ToString("D"), origin.SourceRecordId);
        Assert.Contains("evt:System:77", origin.OriginReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WerWithoutEventTimeUsesCreationTimeAsCoarseAndDeclaresTheDelay()
    {
        var created = CorrelationSamples.Now.AddHours(-2);
        var investigation = await CorrelationSamples.EventsAsync(reports: [WerWithoutEventTime(created)]);
        var result = CorrelationSamples.NormalizeAndAnalyze(
            CorrelationSamples.Input(events: [investigation], sources: CorrelationSources.Events));

        var evidence = Assert.Single(result.Evidence);
        Assert.Equal(CorrelationTimestampPrecision.Coarse, evidence.Precision);
        Assert.Equal(created, evidence.EffectiveTimestampUtc);
        Assert.Contains(evidence.Limitations, limitation =>
            limitation.Contains("pode ser posterior ao acontecimento", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WerWithoutAnyTimestampIsUndatedAndNeverPlacedOnTheTimeline()
    {
        var investigation = await CorrelationSamples.EventsAsync(reports: [WerWithoutEventTime(null)]);
        var result = CorrelationSamples.NormalizeAndAnalyze(
            CorrelationSamples.Input(events: [investigation], sources: CorrelationSources.Events));

        var evidence = Assert.Single(result.Evidence);
        Assert.Null(evidence.EffectiveTimestampUtc);
        Assert.Equal(CorrelationTimestampPrecision.Unknown, evidence.Precision);
        Assert.False(evidence.HasReliableTimestamp);
        Assert.Empty(result.Timeline);
        Assert.Equal(1, result.Coverage.UndatedEvidence);
        Assert.Contains(evidence.Limitations, limitation =>
            limitation.Contains("sem horário confiável", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImplausibleTimestampIsTreatedAsUnknownInsteadOfBeingUsed()
    {
        var ancient = new DateTimeOffset(1999, 12, 31, 23, 59, 0, TimeSpan.Zero);
        var investigation = await CorrelationSamples.EventsAsync(events:
            [GraphicsSamples.DisplayTdr(recordId: 5, at: ancient)]);
        var result = CorrelationSamples.NormalizeAndAnalyze(
            CorrelationSamples.Input(events: [investigation], sources: CorrelationSources.Events));

        var evidence = Assert.Single(result.Evidence);
        Assert.Null(evidence.EffectiveTimestampUtc);
        Assert.Equal(CorrelationTimestampPrecision.Unknown, evidence.Precision);
        Assert.Contains(evidence.Limitations, limitation =>
            limitation.Contains("fora da faixa plausível", StringComparison.Ordinal));
        Assert.Empty(result.Timeline);
    }

    [Fact]
    public void ObservationIntervalKeepsItsWindowAndIsMarkedAsIntervalPrecision()
    {
        var performance = CorrelationSamples.Performance();
        var result = CorrelationSamples.NormalizeAndAnalyze(
            CorrelationSamples.Input(performance: [performance], sources: CorrelationSources.Performance));

        var evidence = Assert.Single(result.Evidence);
        Assert.Equal(CorrelationTimestampPrecision.Interval, evidence.Precision);
        Assert.NotNull(evidence.WindowFromUtc);
        Assert.NotNull(evidence.WindowToUtc);
        Assert.True(evidence.WindowFromUtc < evidence.WindowToUtc);
        var timeline = Assert.Single(result.Timeline);
        Assert.Equal(evidence.WindowFromUtc, timeline.TimestampUtc);
        Assert.Equal(CorrelationTimestampPrecision.Interval, timeline.Precision);
        Assert.Contains(evidence.Limitations, limitation =>
            limitation.Contains("janela observada", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SameEventSeenByEventsAndGraphicsIsConsolidatedWithBothOrigins()
    {
        var tdr = GraphicsSamples.DisplayTdr(recordId: 12345, at: CorrelationSamples.Now.AddHours(-2));
        var wer = GraphicsSamples.LiveKernel("wer-117", "117", at: CorrelationSamples.Now.AddHours(-2));
        var events = await CorrelationSamples.EventsAsync(events: [tdr], reports: [wer]);
        var graphics = await CorrelationSamples.GraphicsAsync(events: [tdr], reports: [wer]);

        var result = CorrelationSamples.NormalizeAndAnalyze(
            CorrelationSamples.Input(events: [events], graphics: [graphics],
                sources: CorrelationSources.Events | CorrelationSources.Graphics));

        var eventEvidence = Assert.Single(result.Evidence, item => item.Id == "evt:System:12345");
        Assert.Equal(2, eventEvidence.Origins.Count);
        Assert.Contains(eventEvidence.Origins, origin => origin.Source == CorrelationSource.Events);
        Assert.Contains(eventEvidence.Origins, origin => origin.Source == CorrelationSource.Graphics);

        var werEvidence = Assert.Single(result.Evidence, item => item.Id == "wer:Sistema:ReportArchive:wer-117");
        Assert.Equal(2, werEvidence.Origins.Count);
        Assert.Equal(2, result.Coverage.MergedReferences);
        Assert.Equal(result.Coverage.EvidenceFound,
            result.Coverage.DistinctOccurrences + result.Coverage.MergedReferences);
        Assert.Equal(result.Coverage.MergedReferences,
            result.Evidence.Sum(item => item.Origins.Count - 1));
        Assert.Contains(werEvidence.Origins, origin => origin.Source == CorrelationSource.Events);
        Assert.Contains(werEvidence.Origins, origin => origin.Source == CorrelationSource.Graphics);
    }

    [Fact]
    public async Task DifferentEventsAtTheSameInstantRemainDistinct()
    {
        var at = CorrelationSamples.Now.AddHours(-2);
        var investigation = await CorrelationSamples.EventsAsync(events:
        [
            GraphicsSamples.DisplayTdr(recordId: 1, at: at),
            GraphicsSamples.DisplayTdr(recordId: 2, at: at)
        ]);

        var result = CorrelationSamples.NormalizeAndAnalyze(
            CorrelationSamples.Input(events: [investigation], sources: CorrelationSources.Events));

        Assert.Equal(2, result.Evidence.Count);
        Assert.Equal(0, result.Coverage.MergedReferences);
        Assert.Equal(2, result.Timeline.Count);
    }

    [Fact]
    public async Task TimelineIsOrderedAndContainsOnlyEvidenceWithReliableTime()
    {
        var at = CorrelationSamples.Now.AddHours(-3);
        var events = await CorrelationSamples.EventsAsync(events:
        [
            GraphicsSamples.DisplayTdr(recordId: 10, at: at.AddMinutes(30)),
            GraphicsSamples.DisplayTdr(recordId: 11, at: at)
        ], reports: [WerWithoutEventTime(null)]);

        var result = CorrelationSamples.NormalizeAndAnalyze(
            CorrelationSamples.Input(events: [events], sources: CorrelationSources.Events));

        Assert.Equal(3, result.Evidence.Count);
        Assert.Equal(2, result.Timeline.Count);
        Assert.True(result.Timeline[0].TimestampUtc <= result.Timeline[1].TimestampUtc);
        Assert.DoesNotContain(result.Timeline, item => item.EvidenceId.StartsWith("wer:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordsOutsideTheRequestedWindowAreNotNormalized()
    {
        var old = GraphicsSamples.DisplayTdr(recordId: 90, at: CorrelationSamples.Now.AddDays(-5));
        var investigation = await CorrelationSamples.EventsAsync(events: [old]);
        var result = CorrelationSamples.NormalizeAndAnalyze(
            CorrelationSamples.Input(events: [investigation], sources: CorrelationSources.Events));

        Assert.Empty(result.Evidence);
        Assert.Empty(result.Timeline);
    }

    [Fact]
    public async Task BootReferenceFromAuditSeparatesWindowsSessions()
    {
        var boot = CorrelationSamples.Now.AddHours(-4);
        var audit = CorrelationSamples.AuditWithBoot(boot);
        var beforeBoot = GraphicsSamples.DisplayTdr(recordId: 40, at: boot.AddMinutes(-30));
        var afterBoot = GraphicsSamples.DisplayTdr(recordId: 41, at: boot.AddMinutes(30));
        var investigation = await CorrelationSamples.EventsAsync(events: [beforeBoot, afterBoot]);

        var result = CorrelationSamples.NormalizeAndAnalyze(CorrelationSamples.Input(
            audits: [audit], events: [investigation],
            sources: CorrelationSources.Audits | CorrelationSources.Events));

        Assert.Equal(boot, result.Boot.LastBootUtc);
        Assert.Equal(2, result.Evidence.Count);
        Assert.Equal(2, result.Timeline.Count);
        Assert.Equal(0, result.Coverage.UndatedEvidence);
    }

    [Fact]
    public void StaleBootReferenceIsRejectedAndDeclared()
    {
        var audit = CorrelationSamples.AuditWithBoot(CorrelationSamples.Now.AddDays(-5),
            CorrelationSamples.Now.AddDays(-3));
        var result = CorrelationSamples.NormalizeAndAnalyze(CorrelationSamples.Input(
            audits: [audit], sources: CorrelationSources.Audits));

        Assert.Null(result.Boot.LastBootUtc);
        Assert.Contains("desatualizada", result.Boot.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingBootTimestampIsDeclaredInsteadOfGuessed()
    {
        var audit = Samples.Audit(CorrelationSamples.Now.AddHours(-1));
        var result = CorrelationSamples.NormalizeAndAnalyze(CorrelationSamples.Input(
            audits: [audit], sources: CorrelationSources.Audits));

        Assert.Null(result.Boot.LastBootUtc);
        Assert.Contains("indisponível", result.Boot.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Evidence);
    }
}
