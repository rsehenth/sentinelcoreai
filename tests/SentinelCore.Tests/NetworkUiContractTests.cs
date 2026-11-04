using System.Text.RegularExpressions;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// Guardas dos defeitos D2 e D3 da Fase 3.3A. O projeto de testes não referencia o WPF, então o que a
/// interface faz com um ComboBox é verificado pela estrutura do código-fonte da janela, e o que ela exibe
/// sobre eventos Tcpip pela função compartilhada que a interface chama.
/// </summary>
public sealed class NetworkUiContractTests
{
    private static string AppSource(string fileName) =>
        File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", fileName));

    [Theory]
    [InlineData("MainWindow.Network.cs", "NetworkPresetRow")]
    [InlineData("MainWindow.Performance.cs", "PerformancePresetRow")]
    [InlineData("MainWindow.Graphics.cs", "GraphicsPresetRow")]
    [InlineData("MainWindow.Correlation.cs", "CorrelationPresetRow")]
    [InlineData("MainWindow.Events.cs", "EventWindowRow")]
    [InlineData("MainWindow.Events.cs", "EventCategoryRow")]
    [InlineData("MainWindow.Monitoring.cs", "MonitoringPresetRow")]
    [InlineData("MainWindow.Monitoring.cs", "MonitoringCategoryRow")]
    [InlineData("MainWindow.Repairs.cs", "RepairScenarioRow")]
    public void ComboBoxItemRecordsOverrideToStringSoTheSelectionBoxShowsTheLabel(string file, string record)
    {
        // O ControlTemplate de ComboBox em App.xaml exibe SelectionBoxItem; sem ToString() a caixa fechada mostra
        // a representação técnica do record ("NetworkPresetRow { Preset = ..., Label = ... }") em vez do rótulo.
        var source = AppSource(file);
        var pattern = new Regex(
            @"record\s+" + record + @"\([^)]*string\s+Label\)\s*\{\s*public\s+override\s+string\s+ToString\(\)\s*=>\s*Label;\s*\}",
            RegexOptions.CultureInvariant);

        Assert.Matches(pattern, source);
    }

    [Fact]
    public void EveryComboBoxBoundToLabelUsesOnlyRecordsCoveredByTheToStringGuard()
    {
        // Se alguém criar um novo ComboBox com DisplayMemberPath="Label" a lista acima precisa crescer junto.
        var xaml = AppSource("MainWindow.xaml");
        var comboBoxesBoundToLabel = Regex.Matches(xaml, @"<ComboBox\b[^>]*DisplayMemberPath=""Label""", RegexOptions.Singleline).Count;
        Assert.Equal(9, comboBoxesBoundToLabel);
    }

    private static NetworkTcpipEventContext TcpipContext(int attempts, int ephemeral) =>
        new(Guid.NewGuid(), attempts, ephemeral, null, null, "nota");

    [Fact]
    public void TcpipCountsUseTheRealSourceEventIdsAndNeverEvent4227()
    {
        var text = NetworkTcpipEventContext.FormatCounts(TcpipContext(attempts: 7, ephemeral: 4));

        Assert.Contains("4266: 7", text);
        Assert.Contains("4231: 4", text);
        Assert.DoesNotContain("4227", text);
        // Neutra: não classifica cada ID como "falha" nem como "porta dinâmica" sem sustentação técnica.
        Assert.DoesNotContain("falha", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TcpipCountsInTheUiAndInThePersistedMarkdownReportAgreePerEventId()
    {
        var context = TcpipContext(attempts: 7, ephemeral: 4);
        var session = NetworkPersistenceTests.CreateValidSession(sampleCount: 2) with { TcpipEvents = context };

        var ui = NetworkTcpipEventContext.FormatCounts(context);
        var markdown = NetworkReport.Render(session);

        foreach (var (eventId, count) in new[] { (4266, 7), (4231, 4) })
        {
            Assert.Matches(new Regex($@"Eventos Tcpip {eventId}: {count}\b"), ui);
            Assert.Matches(new Regex($@"- Eventos Tcpip {eventId}: {count}\b"), markdown);
        }
        Assert.DoesNotContain("4227", markdown);
    }

    [Fact]
    public void PersistedTcpipTextsAreNeutralAndDoNotClaimWhatEachEventIdMeans()
    {
        // A semântica oficial dos eventos 4231/4266 não foi verificada; nenhum texto persistido ou exibido pode
        // classificá-los como "falha", "limite de tentativas" ou "porta dinâmica".
        var investigation = NetworkSessionRunnerTests.InvestigationWithTcpipEvents();
        var context = NetworkSessionRunner.BuildTcpipContext(investigation)!;
        var session = NetworkPersistenceTests.CreateValidSession(sampleCount: 2) with { TcpipEvents = context };
        var visible = NetworkTcpipEventContext.FormatCounts(context) + "\n" + context.Note + "\n" + NetworkReport.Render(session);

        foreach (var forbidden in new[] { "limite de tentativas", "alocar porta", "alocação de porta", "falha" })
            Assert.DoesNotContain(forbidden, visible, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowSourceRendersTcpipCountsThroughTheSharedFunctionWithoutHardcodedEventIds()
    {
        var source = AppSource("MainWindow.Network.cs");

        Assert.Contains("NetworkTcpipEventContext.FormatCounts(", source);
        Assert.DoesNotContain("4227", source);
        Assert.DoesNotMatch(new Regex(@"\b(4231|4266)\b"), source);
    }

    [Fact]
    public void WindowShowsTheHistoricalQualityAnnotationInTheSessionSummary()
    {
        Assert.Contains("NetworkQualityCatalog.Evaluate(session)", AppSource("MainWindow.Network.cs"));
    }

}
