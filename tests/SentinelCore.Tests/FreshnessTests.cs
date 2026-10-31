using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class FixedTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

public static class Samples
{
    public static AuditRecord Audit(DateTimeOffset at, DiagnosticStatus status = DiagnosticStatus.Completed,
        AuditKind kind = AuditKind.Basic, Guid? id = null)
    {
        var auditId = id ?? Guid.NewGuid();
        var result = new CollectorResult(auditId, "memory", "1.0", at, at, TimeSpan.Zero,
            status, "Fake: unit test only", new InventoryData(Memory: new MemoryInfo(16UL * 1073741824, null)),
            status == DiagnosticStatus.Partial ? ["AvailableBytes indisponível"] : []);
        return new(1, auditId, kind, "0.1.0", at, at, TimeSpan.Zero, status, [result]);
    }

    public static EventInvestigation Investigation(Guid? id = null)
    {
        var at = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var first = new EventLogEvidence("evt:System:10", "applications:crash", "System", "Application Error",
            1000, 10, at, EventLevel.Error, 2, null, null, "Falha simulada", false, [], []);
        var second = first with { Id = "evt:System:11", RecordId = 11, OccurredUtc = at.AddMinutes(5) };
        var report = new WerEvidence("wer:Sistema:ReportArchive:r1", "applications:wer", "r1", true, "Sistema",
            "ReportArchive", "AppCrash_app_1", "AppCrash", null, at, at, "Loaded", "app.exe", null, "app.dll",
            "1.0", "bucket", [], [], EvidenceConfidence.High);
        return new(1, id ?? Guid.NewGuid(), "0.3.0-preview.1", "1", EventWindow.Last7Days,
            at.AddDays(-7), at, at, at.AddSeconds(3), TimeSpan.FromSeconds(3), EventInvestigationStatus.Completed,
            [
                new("System", true, true, true, null),
                new("WER — Sistema/ReportArchive", true, true, true, null)
            ],
            [
                new("event-log", "1.0.0", "Windows Event Log (leitura nativa)", EventSourceStatus.Completed,
                    at, at.AddSeconds(1), TimeSpan.FromSeconds(1), 2, 0, false, []),
                new("wer-metadata", "1.0.0", "Windows Error Reporting (metadados)", EventSourceStatus.Completed,
                    at, at.AddSeconds(2), TimeSpan.FromSeconds(2), 0, 1, false, [])
            ],
            [
                new("applications:crash", EventCategory.Applications, "Falhas de aplicativos", "Application Error",
                    1000, null, EvidenceSeverity.PotentialImpact, EvidenceConfidence.High,
                    CausalityStatus.NotAssessed, 2, 0, 0, at, at.AddMinutes(5), ["System"], ["Log de Eventos"], []),
                new("applications:wer", EventCategory.Applications, "Relatórios de falha (WER)", null, null, null,
                    EvidenceSeverity.PotentialImpact, EvidenceConfidence.High, CausalityStatus.NotAssessed,
                    0, 1, 0, at, at, [], ["WER — Sistema/ReportArchive"], [])
            ],
            [first, second], [report], []);
    }
}

public sealed class FreshnessTests
{
    private readonly FixedTime clock = new();
    [Fact] public void NoHistory() => Assert.Equal(FreshnessState.None, new FreshnessEngine(clock).Evaluate([], AuditKind.Basic).State);
    [Fact] public void BasicAtSixDaysIsExpiring() => Assert.Equal(FreshnessState.Expiring,
        new FreshnessEngine(clock).Evaluate([Samples.Audit(clock.Now.AddDays(-6))], AuditKind.Basic).State);
    [Fact] public void BasicOlderThanSevenDaysIsOutdated() => Assert.Equal(FreshnessState.Outdated,
        new FreshnessEngine(clock).Evaluate([Samples.Audit(clock.Now.AddDays(-7).AddSeconds(-1))], AuditKind.Basic).State);
    [Fact] public void BasicAtSevenDaysIsOutdated() => Assert.Equal(FreshnessState.Outdated,
        new FreshnessEngine(clock).Evaluate([Samples.Audit(clock.Now.AddDays(-7))], AuditKind.Basic).State);
    [Fact] public void FullOlderThanThirtyDaysIsOutdated() => Assert.Equal(FreshnessState.Outdated,
        new FreshnessEngine(clock).Evaluate([Samples.Audit(clock.Now.AddDays(-31), kind: AuditKind.Full)], AuditKind.Full).State);
    [Fact] public void FutureDateIsInvalid() => Assert.Equal(FreshnessState.InvalidDate,
        new FreshnessEngine(clock).Evaluate([Samples.Audit(clock.Now.AddMinutes(1))], AuditKind.Basic).State);
    [Fact] public void FailedAttemptDoesNotRenewLastSuccess()
    {
        var result = new FreshnessEngine(clock).Evaluate([Samples.Audit(clock.Now, DiagnosticStatus.Failed), Samples.Audit(clock.Now.AddDays(-8))], AuditKind.Basic);
        Assert.Equal(FreshnessState.Outdated, result.State);
        Assert.Equal(clock.Now.AddDays(-8), result.LastSuccessUtc);
        Assert.Equal(DiagnosticStatus.Failed, result.LatestAttemptStatus);
    }
    [Theory]
    [InlineData(DiagnosticStatus.Cancelled)] [InlineData(DiagnosticStatus.Partial)] [InlineData(DiagnosticStatus.Failed)]
    public void UnsuccessfulOnlyDoesNotCreateValidity(DiagnosticStatus status)
    {
        var result = new FreshnessEngine(clock).Evaluate([Samples.Audit(clock.Now, status)], AuditKind.Basic);
        Assert.Equal(FreshnessState.Unsuccessful, result.State);
        Assert.Null(result.LastSuccessUtc);
    }
    [Fact] public void AuditKindsAreIndependent() => Assert.Equal(FreshnessState.None,
        new FreshnessEngine(clock).Evaluate([Samples.Audit(clock.Now)], AuditKind.Full).State);
    [Fact] public void RecentSuccessIsCurrent() => Assert.Equal(FreshnessState.Current,
        new FreshnessEngine(clock).Evaluate([Samples.Audit(clock.Now.AddHours(-1))], AuditKind.Basic).State);
}
