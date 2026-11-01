using System.Text.RegularExpressions;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// R7 — a classificação de WER precisa ser única: tipo do relatório e código do problema, nunca o código
/// isolado. Interface, observações e relatório Markdown devem usar a mesma classificação validada.
/// </summary>
public sealed class GraphicsClassificationTests
{
    private static RawWerReport Report(string id, string? eventType, string? code,
        DateTimeOffset? at = null, string? application = "explorer.exe") =>
        new(id, true, "Sistema", "ReportArchive", $"folder-{id}", eventType, code,
            at ?? GraphicsSamples.StartUtc.AddHours(-1), at ?? GraphicsSamples.StartUtc.AddHours(-1),
            "Loaded", application, null, "dxgkrnl.sys", "10.0.1", "bucket", [], [], EvidenceConfidence.High);

    [Theory]
    [InlineData("LiveKernelEvent", "117", "graphics:live-kernel")]
    [InlineData("LiveKernelEvent", "141", "graphics:live-kernel")]
    [InlineData("LiveKernelEvent", "142", "graphics:live-kernel")]
    [InlineData("Kernel", "141", "graphics:live-kernel")]
    [InlineData("LiveKernelEvent", "999", "other:device")]
    [InlineData("LiveKernelEvent", "193", "other:device")]
    [InlineData("Critical_Update", "141", "other:wer")]
    [InlineData("Critical", "117", "other:wer")]
    [InlineData("NonCritical", "117", "other:wer")]
    [InlineData("AppCrash", "141", "applications:crash")]
    [InlineData("BlueScreen", "117", "power:bugcheck")]
    [InlineData(null, "141", "other:wer")]
    public void WerClassificationRequiresReportTypeAndKnownLiveKernelCode(string? eventType, string? code,
        string expectedGroupId)
    {
        var classification = EventCatalog.ClassifyReport(Report("report", eventType, code));
        Assert.Equal(expectedGroupId, classification.GroupId);
    }

    [Fact]
    public void UnconfirmedWerTypeIsPreservedAsUnknownInsteadOfGraphics()
    {
        var classification = EventCatalog.ClassifyReport(Report("untyped", null, "141"));
        Assert.Equal(EventCategory.Other, classification.Category);
        Assert.Equal(EvidenceSeverity.Unknown, classification.Severity);
    }

    [Fact]
    public void CriticalReportWithLiveKernelCodeStaysInformational()
    {
        var classification = EventCatalog.ClassifyReport(Report("critical", "Critical_Update", "141"));
        Assert.Equal(EventCategory.Other, classification.Category);
        Assert.Equal(EvidenceSeverity.Informational, classification.Severity);
        Assert.NotEqual("graphics:live-kernel", classification.GroupId);
    }

    [Fact]
    public async Task NonLiveKernelReportsDoNotEnterGraphicsEvidenceOrLiveKernelObservations()
    {
        var investigation = await GraphicsSamples.Runner(events: GraphicsSamples.RealisticEvents(reports:
            [
                GraphicsSamples.LiveKernel("lk-117", "117"),
                GraphicsSamples.LiveKernel("lk-141", "141"),
                GraphicsSamples.LiveKernel("lk-142", "142"),
                Report("critical-141", "Critical_Update", "141"),
                Report("noncritical-117", "NonCritical", "117"),
                Report("appcrash-141", "AppCrash", "141"),
                Report("bluescreen-117", "BlueScreen", "117"),
                Report("untyped-141", null, "141"),
                GraphicsSamples.LiveKernel("lk-999", "999")
            ])).RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        Assert.Equal(3, investigation.WerReports.Count);
        Assert.All(investigation.WerReports, report =>
            Assert.True(GraphicsEvidenceClassification.IsLiveKernelKind(report.Kind)));

        foreach (var code in GraphicsEvidenceClassification.LiveKernelProblemCodes)
        {
            var observation = Assert.Single(investigation.Observations,
                item => item.RuleId == $"{GraphicsEvidenceAnalyzer.LiveKernelRulePrefix}-{code}");
            Assert.Equal(1, observation.EvidenceCount);
            Assert.Equal(1, investigation.WerReports.Count(report =>
                report.Kind == GraphicsEvidenceClassification.KindForCode(code)));
        }
    }

    [Fact]
    public async Task ReportMarkdownCountsLiveKernelByValidatedKind()
    {
        var investigation = await GraphicsSamples.Runner(events: GraphicsSamples.RealisticEvents(reports:
            [
                GraphicsSamples.LiveKernel("lk-117", "117"),
                GraphicsSamples.LiveKernel("lk-141", "141"),
                GraphicsSamples.LiveKernel("lk-142", "142"),
                Report("critical-141", "Critical_Update", "141")
            ])).RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        var report = GraphicsReport.Render(investigation);
        Assert.Contains("| 117 | 1 |", report, StringComparison.Ordinal);
        Assert.Contains("| 141 | 1 |", report, StringComparison.Ordinal);
        Assert.Contains("| 142 | 1 |", report, StringComparison.Ordinal);
        Assert.DoesNotContain("| 141 | 2 |", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveKernelWithPartialMetadataIsStillPreservedAndClassified()
    {
        var partial = new RawWerReport("partial", true, "Sistema", "ReportArchive", "Kernel_141",
            "LiveKernelEvent", "141", null, null, null, null, null, null, null, null, [], [],
            EvidenceConfidence.Low);
        var investigation = await GraphicsSamples.Runner(
                events: GraphicsSamples.RealisticEvents(reports: [partial]))
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

        var evidence = Assert.Single(investigation.WerReports);
        Assert.Equal(GraphicsWerKind.LiveKernelEvent141, evidence.Kind);
        Assert.Equal(EvidenceConfidence.Low, evidence.Confidence);
        Assert.Null(evidence.EventTimeUtc);
        Assert.Null(evidence.CreationTimeUtc);
        Assert.Single(investigation.Observations,
            observation => observation.RuleId == $"{GraphicsEvidenceAnalyzer.LiveKernelRulePrefix}-141");
    }

    [Fact]
    public void GraphicsTabCountsLiveKernelFromTheValidatedClassification()
    {
        var source = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "MainWindow.Graphics.cs"));
        Assert.DoesNotContain("IsLiveKernelCode(", source, StringComparison.Ordinal);
        Assert.Contains("CountLiveKernelReports(", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\b(117|141|142)\b"), source);
    }

    [Fact]
    public void SharedCounterCountsOnlyClassifiedLiveKernelKinds()
    {
        var reports = new[]
        {
            Evidence("1", GraphicsWerKind.LiveKernelEvent117),
            Evidence("2", GraphicsWerKind.LiveKernelEvent141),
            Evidence("3", GraphicsWerKind.LiveKernelEvent142),
            Evidence("4", GraphicsWerKind.LiveKernelEventOther),
            Evidence("5", GraphicsWerKind.GraphicsWerOther)
        };

        Assert.Equal(3, GraphicsEvidenceClassification.CountLiveKernelReports(reports));
    }

    private static GraphicsWerEvidence Evidence(string id, GraphicsWerKind kind) =>
        new(id, "graphics:live-kernel", id, true, "Sistema", "ReportArchive", "folder", "LiveKernelEvent",
            "141", null, null, null, null, null, null, null, kind, EvidenceConfidence.Medium, [], []);
}
