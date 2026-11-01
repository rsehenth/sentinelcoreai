using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// R9 — o truncamento de mensagem precisa ser visível. O texto apresentado é parcial; nenhum conteúdo ausente
/// é reconstruído e truncamento não é tratado como ausência de evidência.
/// </summary>
public sealed class GraphicsMessageTruncationTests
{
    private static Task<GraphicsInvestigation> RunAsync(IReadOnlyList<RawEventRecord> events, int maxMessageChars) =>
        GraphicsSamples.Runner(events: GraphicsSamples.RealisticEvents(events: events),
                limits: new GraphicsLimits { MaxMessageChars = maxMessageChars })
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last24Hours));

    [Fact]
    public async Task MessageBelowTheLimitIsPresentedAsIs()
    {
        var message = new string('a', 20);
        var investigation = await RunAsync([GraphicsSamples.DisplayTdr(message: message)], 40);

        var evidence = Assert.Single(investigation.Events);
        Assert.False(evidence.MessageTruncated);
        Assert.Equal(message, evidence.Message);
        var report = GraphicsReport.Render(investigation);
        Assert.Contains(message, report, StringComparison.Ordinal);
        Assert.DoesNotContain(GraphicsMessagePresentation.TruncatedMarker, report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageExactlyAtTheLimitIsNotMarkedAsTruncated()
    {
        var message = new string('b', 40);
        var investigation = await RunAsync([GraphicsSamples.DisplayTdr(message: message)], 40);

        var evidence = Assert.Single(investigation.Events);
        Assert.False(evidence.MessageTruncated);
        Assert.Equal(message, evidence.Message);
        Assert.DoesNotContain(GraphicsMessagePresentation.TruncatedMarker,
            GraphicsReport.Render(investigation), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageAboveTheLimitIsMarkedAndTheMissingTailIsNotInvented()
    {
        var message = new string('x', 30) + "FINAL-INVISIVEL";
        var investigation = await RunAsync([GraphicsSamples.DisplayTdr(message: message)], 40);

        var evidence = Assert.Single(investigation.Events);
        Assert.True(evidence.MessageTruncated);
        Assert.Equal(40, evidence.Message!.Length);
        Assert.DoesNotContain("FINAL-INVISIVEL", evidence.Message, StringComparison.Ordinal);

        var report = GraphicsReport.Render(investigation);
        Assert.Contains(GraphicsMessagePresentation.TruncatedMarker, report, StringComparison.Ordinal);
        Assert.Contains("conteúdo apresentado é parcial", report, StringComparison.Ordinal);
        Assert.Contains("não é ausência de evidência", report, StringComparison.Ordinal);
        Assert.DoesNotContain("FINAL-INVISIVEL", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageTruncatedByTheOriginalCollectorKeepsItsFlag()
    {
        var investigation = await RunAsync(
            [GraphicsSamples.DisplayTdr(message: new string('c', 12), messageTruncated: true)], 1200);

        var evidence = Assert.Single(investigation.Events);
        Assert.True(evidence.MessageTruncated);
        Assert.Equal(12, evidence.Message!.Length);
        Assert.Contains(evidence.Limitations, limitation =>
            limitation.Contains("Mensagem truncada", StringComparison.Ordinal));

        var report = GraphicsReport.Render(investigation);
        Assert.Contains(GraphicsMessagePresentation.TruncatedMarker, report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullMessageIsNotConfusedWithTruncation()
    {
        var record = new RawEventRecord("System", "Display",
            GraphicsEvidenceClassification.DisplayDriverResetEventId, 100, GraphicsSamples.StartUtc.AddHours(-2),
            EventLevel.Warning, 3, null, null, null, false, []);
        var investigation = await RunAsync([record], 40);

        var evidence = Assert.Single(investigation.Events);
        Assert.Null(evidence.Message);
        Assert.False(evidence.MessageTruncated);
        Assert.Equal("—", GraphicsMessagePresentation.Text(evidence.Message, evidence.MessageTruncated));
        Assert.DoesNotContain(GraphicsMessagePresentation.TruncatedMarker,
            GraphicsReport.Render(investigation), StringComparison.Ordinal);
    }

    [Fact]
    public void PresentationMarksPartialContentWithoutCompletingIt()
    {
        Assert.Equal("mensagem", GraphicsMessagePresentation.Text("mensagem", false));
        Assert.StartsWith(GraphicsMessagePresentation.TruncatedMarker,
            GraphicsMessagePresentation.Text("mensagem", true), StringComparison.Ordinal);
        Assert.Contains("mensagem", GraphicsMessagePresentation.Text("mensagem", true), StringComparison.Ordinal);
        Assert.Equal("—", GraphicsMessagePresentation.Text(null, false));
        Assert.Contains(GraphicsMessagePresentation.TruncatedMarker,
            GraphicsMessagePresentation.Text(null, true), StringComparison.Ordinal);
    }

    [Fact]
    public void EventListAndSummarySurfaceTheTruncationFlag()
    {
        var xaml = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "MainWindow.xaml"));
        Assert.Contains("Text=\"{Binding Message}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding Message}\"", xaml, StringComparison.Ordinal);

        var source = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App",
            "MainWindow.Graphics.cs"));
        Assert.Contains("GraphicsMessagePresentation.Text(", source, StringComparison.Ordinal);
        Assert.Contains("MessageTruncated", source, StringComparison.Ordinal);
        Assert.Contains("Mensagens truncadas", source, StringComparison.Ordinal);
    }
}
