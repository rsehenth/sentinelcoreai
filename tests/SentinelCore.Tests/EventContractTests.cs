using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class EventContractTests
{
    [Fact]
    public void EvidenceContractSeparatesSeverityConfidenceAndCausality()
    {
        var group = new EvidenceGroup("applications:crash", EventCategory.Applications,
            "Falhas de aplicativos", null, null, null, EvidenceSeverity.ObservedImpact,
            EvidenceConfidence.High, CausalityStatus.NotAssessed, 12, 3, 1,
            DateTimeOffset.Parse("2026-10-01T10:00:00Z"), DateTimeOffset.Parse("2026-10-02T10:00:00Z"),
            ["Application"], ["Log de Eventos"], []);
        Assert.Equal(EvidenceSeverity.ObservedImpact, group.Severity);
        Assert.Equal(EvidenceConfidence.High, group.Confidence);
        Assert.Equal(CausalityStatus.NotAssessed, group.Causality);
        Assert.Equal(12, group.EventRecordCount);
        Assert.Equal(3, group.DistinctReportCount);
    }

    [Fact]
    public void VersionIdentifiesPreviewAndEventSchema()
    {
        Assert.Equal(TestRepository.ProductVersion(), SoftwareInfo.Version);
        Assert.Equal("1", SoftwareInfo.EventSchemaVersion);
        Assert.Equal("1", SoftwareInfo.SnapshotsSchemaVersion);
    }

    [Fact]
    public void DefaultLimitsAreExplicitAndFinite()
    {
        var limits = InvestigationLimits.Default;
        Assert.InRange(limits.MaxEventsPerChannel, 1, 10000);
        Assert.InRange(limits.MaxTotalEvents, 1, 20000);
        Assert.InRange(limits.MaxMessageChars, 1, 10000);
        Assert.True(limits.CollectionTime <= TimeSpan.FromMinutes(2));
    }
}
