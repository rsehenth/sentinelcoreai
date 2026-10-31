using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class EventDeduplicationTests
{
    private static RawEventRecord Event(string channel, long? record, string provider, int id,
        string time = "2026-10-30T12:00:00Z") => new(channel, provider, id, record,
        DateTimeOffset.Parse(time), EventLevel.Error, 2, null, null, "mensagem", false, []);

    private static RawWerReport Report(string id, string source, string store, string type,
        string? code = null, bool fromFile = true, EvidenceConfidence confidence = EvidenceConfidence.High) =>
        new(id, fromFile, source, store, "folder", type, code, null, null, null, null, null, null, null,
            null, [], [], confidence);

    [Fact]
    public void SameChannelRecordIdIsTheSameRecord()
    {
        var result = EvidenceDeduplicator.Build(
            [Event("System", 10, "Application Error", 1000), Event("System", 10, "Application Error", 1000)], []);
        Assert.Single(result.Events);
        Assert.Equal(1, result.Groups.Single(g => g.Id == "applications:crash").EventRecordCount);
    }

    [Fact]
    public void DistinctRecordsWithSameCodeRemainDistinct()
    {
        var result = EvidenceDeduplicator.Build(
            [Event("System", 10, "Application Error", 1000), Event("System", 11, "Application Error", 1000)], []);
        Assert.Equal(2, result.Events.Count);
        Assert.Equal(2, result.Groups.Single(g => g.Id == "applications:crash").EventRecordCount);
    }

    [Fact]
    public void SameReportInQueueAndArchiveIsOneDistinctReportWithRePresentation()
    {
        var result = EvidenceDeduplicator.Build([], [
            Report("A", "Sistema", "ReportQueue", "AppCrash"),
            Report("A", "Sistema", "ReportArchive", "AppCrash")]);
        var group = result.Groups.Single(g => g.Id == "applications:crash");
        Assert.Equal(1, group.DistinctReportCount);
        Assert.Equal(1, group.RePresentationCount);
        Assert.Equal(2, result.Reports.Count);
    }

    [Fact]
    public void DifferentReportsWithSameCodeAreNotMerged()
    {
        var result = EvidenceDeduplicator.Build([], [
            Report("A", "Sistema", "ReportArchive", "LiveKernelEvent", "141"),
            Report("B", "Sistema", "ReportArchive", "LiveKernelEvent", "141")]);
        Assert.Equal(2, result.Groups.Single(g => g.Id == "graphics:live-kernel").DistinctReportCount);
    }

    [Fact]
    public void ReportsWithoutIdentityAreNeverMergedByCodeOrTime()
    {
        var result = EvidenceDeduplicator.Build([], [
            Report("folder:one", "Sistema", "ReportQueue", "Kernel", "141", false, EvidenceConfidence.Low),
            Report("folder:two", "Sistema", "ReportQueue", "Kernel", "141", false, EvidenceConfidence.Low)]);
        var group = result.Groups.Single(g => g.Id == "graphics:live-kernel");
        Assert.Equal(2, group.DistinctReportCount);
        Assert.Equal(0, group.RePresentationCount);
        Assert.Equal(EvidenceConfidence.Low, group.Confidence);
    }

    [Fact]
    public void ThousandsOfRecordsAndFewerReportsKeepSeparateCounts()
    {
        var events = Enumerable.Range(0, 1200).Select(i =>
            Event("System", i, "Display", 4101)).ToArray();
        var reports = Enumerable.Range(0, 12).Select(i =>
            Report($"R{i}", "Sistema", "ReportArchive", "LiveKernelEvent", "141")).ToArray();
        var result = EvidenceDeduplicator.Build(events, reports);
        Assert.Equal(1200, result.Groups.Single(g => g.Id == "graphics:tdr").EventRecordCount);
        Assert.Equal(12, result.Groups.Single(g => g.Id == "graphics:live-kernel").DistinctReportCount);
    }

    [Fact]
    public void GroupSeverityIsMaxAndConfidenceIsMin()
    {
        var result = EvidenceDeduplicator.Build(
            [Event("System", 1, "Application Error", 1000)],
            [Report("A", "Sistema", "ReportQueue", "AppCrash", fromFile: false, confidence: EvidenceConfidence.Low)]);
        var group = result.Groups.Single(g => g.Id == "applications:crash");
        Assert.Equal(EvidenceSeverity.ObservedImpact, group.Severity);
        Assert.Equal(EvidenceConfidence.Low, group.Confidence);
        Assert.Equal(CausalityStatus.NotAssessed, group.Causality);
    }

    [Fact]
    public void EventWithoutRecordIdCarriesIdentityLimitation()
    {
        var result = EvidenceDeduplicator.Build([Event("System", null, "disk", 51)], []);
        Assert.Contains(result.Events[0].Limitations, l => l.Contains("RecordId"));
        Assert.Equal(EvidenceConfidence.Medium, result.Groups.Single().Confidence);
    }

    [Fact]
    public void RecordsWithoutRecordIdAreNotMergedEvenWhenTimeAndCodeMatch()
    {
        var at = "2026-10-30T12:00:00Z";
        var result = EvidenceDeduplicator.Build(
            [Event("System", null, "disk", 51, at), Event("System", null, "disk", 51, at)], []);
        Assert.Equal(2, result.Events.Count);
        Assert.Equal(2, result.Groups.Single().EventRecordCount);
    }

    [Fact]
    public void EmptyInputProducesNoFabricatedGroup()
    {
        var result = EvidenceDeduplicator.Build([], []);
        Assert.Empty(result.Groups);
        Assert.Empty(result.Events);
        Assert.Empty(result.Reports);
    }
}
