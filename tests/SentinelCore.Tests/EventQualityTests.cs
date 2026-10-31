using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class EventQualityTests
{
    [Fact]
    public void ReportsOutsideThePeriodAreAffectedWithEvidence()
    {
        var investigation = Samples.Investigation();
        var inside = investigation.Reports[0];
        var outside = inside with { ReportId = "r2", EventTimeUtc = investigation.PeriodToUtc.AddMinutes(1) };
        var withoutTime = inside with { ReportId = "r3", EventTimeUtc = null, CreationTimeUtc = null };

        var quality = EventQualityRules.Evaluate(investigation with { Reports = [inside, outside, withoutTime] });

        Assert.Equal(EventQualityState.Affected, quality.State);
        Assert.Equal(1, quality.ReportsOutsidePeriod);
        Assert.Equal(1, quality.ReportsWithoutEffectiveTime);
        Assert.Equal(EvidenceConfidence.Medium, quality.Confidence);
        Assert.Contains(quality.Evidence, e => e.ReportId == "r2");
        Assert.Contains("superestimadas", quality.Summary);
        Assert.Contains("não são invalidados", quality.Summary);
    }

    [Fact]
    public void CleanReportsAreVerified()
    {
        var investigation = Samples.Investigation();
        var quality = EventQualityRules.Evaluate(investigation);
        Assert.Equal(EventQualityState.Verified, quality.State);
        Assert.Equal(0, quality.ReportsOutsidePeriod);
        Assert.Equal(EvidenceConfidence.High, quality.Confidence);
    }

    [Fact]
    public void MissingTimeWithoutOutsideEvidenceIsUndetermined()
    {
        var investigation = Samples.Investigation();
        var withoutTime = investigation.Reports[0] with { EventTimeUtc = null, CreationTimeUtc = null };
        var quality = EventQualityRules.Evaluate(investigation with { Reports = [withoutTime] });
        Assert.Equal(EventQualityState.Undetermined, quality.State);
        Assert.Equal(1, quality.ReportsWithoutEffectiveTime);
    }

    [Fact]
    public void NoReportsMeansNotApplicable()
    {
        var quality = EventQualityRules.Evaluate(Samples.Investigation() with { Reports = [] });
        Assert.Equal(EventQualityState.NotApplicable, quality.State);
    }

    [Fact]
    public void BoundariesAreInsideThePeriod()
    {
        var investigation = Samples.Investigation();
        var atStart = investigation.Reports[0] with { ReportId = "a", EventTimeUtc = investigation.PeriodFromUtc };
        var atEnd = investigation.Reports[0] with { ReportId = "b", EventTimeUtc = investigation.PeriodToUtc };
        var quality = EventQualityRules.Evaluate(investigation with { Reports = [atStart, atEnd] });
        Assert.Equal(EventQualityState.Verified, quality.State);
    }

    [Fact]
    public void EvaluationNeverMutatesTheInvestigation()
    {
        var investigation = Samples.Investigation();
        var before = System.Text.Json.JsonSerializer.Serialize(investigation, AuditJson.Options);
        _ = EventQualityRules.Evaluate(investigation);
        var after = System.Text.Json.JsonSerializer.Serialize(investigation, AuditJson.Options);
        Assert.Equal(before, after);
        Assert.Equal("EVT-WER-WINDOW", EventQualityRules.WerWindowRuleId);
        Assert.Equal("1.0", EventQualityRules.WerWindowRuleVersion);
    }

    [Fact]
    public async Task LoadingWritesQualityProjectionWithoutChangingTheRecord()
    {
        var root = Path.Combine(Path.GetTempPath(), "SentinelCoreTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SentinelCore.Persistence.MemoryStore(root);
            await store.SaveInvestigationAsync(Samples.Investigation());
            var path = Directory.GetFiles(Path.Combine(root, "memory", "events"), "*.md").Single();
            var before = File.ReadAllBytes(path);
            var loaded = await new SentinelCore.Persistence.MemoryStore(root).LoadInvestigationsAsync();
            Assert.Single(loaded.Investigations);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.True(File.Exists(Path.Combine(root, "memory", "QUALITY.md")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
