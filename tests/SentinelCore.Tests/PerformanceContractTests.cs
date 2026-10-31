using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class PerformanceContractTests
{
    [Fact]
    public void VersionAndSchemaAreAdvanced()
    {
        Assert.Equal("0.3.0-preview.5", SoftwareInfo.Version);
        Assert.Equal("2", SoftwareInfo.PerformanceSchemaVersion);
        Assert.Equal("1", SoftwareInfo.NetworkSchemaVersion);
    }

    [Fact]
    public void PresetsResolveToThirtySixtyAndOneHundredEightySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), PerformanceSessionRequest.ForPreset(PerformanceSessionPreset.Seconds30).Duration);
        Assert.Equal(TimeSpan.FromMinutes(1), PerformanceSessionRequest.ForPreset(PerformanceSessionPreset.Minute1).Duration);
        Assert.Equal(TimeSpan.FromMinutes(3), PerformanceSessionRequest.ForPreset(PerformanceSessionPreset.Minutes3).Duration);
        Assert.Throws<ArgumentOutOfRangeException>(() => PerformanceSessionRequest.ForPreset(PerformanceSessionPreset.Custom));
    }

    [Fact]
    public void ObservationAlwaysDeclaresCausalityNotAssessed()
    {
        var observation = new PerformanceObservation(
            "PERF-CPU-SUSTAINED@x", "PERF-CPU-SUSTAINED", "1.0", PerformanceObservationKind.SustainedCpu,
            "CPU sustentada", "CPU total ficou em 90% por 24 s.",
            EvidenceSeverity.PotentialImpact, EvidenceConfidence.High, CausalityStatus.NotAssessed,
            "cpu.total.percent", "%", 85, null, null,
            DateTimeOffset.Parse("2026-10-30T12:00:00Z"), DateTimeOffset.Parse("2026-10-30T12:00:24Z"),
            TimeSpan.FromSeconds(24), 24, 24, 88, 98, 91, 97,
            "GetSystemTimes", [], []);
        Assert.Equal(CausalityStatus.NotAssessed, observation.Causality);
        Assert.Null(observation.Subject);
    }

    [Fact]
    public void SessionRoundTripsThroughTheAuditJsonOptions()
    {
        var empty = new MetricStatistics("cpu.total.percent", "%", 0, 0, null, null, null, null,
            null, null, null, null, null, null, null, true, null, []);
        var stats = new PerformanceStatistics(TimeSpan.Zero, 0, 0, 0, empty, empty, empty, empty,
            empty, empty, empty, empty, [], [], []);
        var session = new PerformanceSession(1, Guid.NewGuid(), SoftwareInfo.Version,
            SoftwareInfo.PerformanceSchemaVersion, PerformanceSessionPreset.Seconds30,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1),
            DateTimeOffset.Parse("2026-10-30T12:00:00Z"), DateTimeOffset.Parse("2026-10-30T12:00:30Z"),
            TimeSpan.FromSeconds(30), PerformanceSessionStatus.Completed,
            [new("performance-cpu", "1.0.0", PerformanceSourceCategory.Cpu, "fake",
                PerformanceSourceStatus.Completed, DateTimeOffset.Parse("2026-10-30T12:00:00Z"),
                DateTimeOffset.Parse("2026-10-30T12:00:30Z"), TimeSpan.FromSeconds(30), 30, false, [])],
            stats, [], [], [], new(30, 0.4, 0.9, 120UL * 1048576, 80UL * 1048576, 35, 70), []);
        var json = System.Text.Json.JsonSerializer.Serialize(session, AuditJson.Options);
        var copy = System.Text.Json.JsonSerializer.Deserialize<PerformanceSession>(json, AuditJson.Options);
        Assert.NotNull(copy);
        Assert.Equal(session.Id, copy!.Id);
        Assert.Equal(session.Observations.Count, copy.Observations.Count);
        Assert.Equal(session.Samples.Count, copy.Samples.Count);
        Assert.Equal(PerformanceSessionStatus.Completed, copy.Status);
    }
}
