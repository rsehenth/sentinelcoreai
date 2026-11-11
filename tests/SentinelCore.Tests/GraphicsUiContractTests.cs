using System.Text.RegularExpressions;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// Guardas estruturais da aba Gráficos &amp; GPU. O projeto de testes não referencia WPF: o contrato visual é
/// verificado pela estrutura do XAML e do code-behind, e o contrato de evidência pelas constantes compartilhadas.
/// </summary>
public sealed class GraphicsUiContractTests
{
    private static string AppSource(string fileName) =>
        File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", fileName));

    [Fact]
    public void NavigationContainsGraphicsEntryAndHistoryKeepsTheLastShortcut()
    {
        var xaml = AppSource("MainWindow.xaml");
        var graphics = UiResourceText.Resource("Gráficos & GPU");
        Assert.Contains($"Content=\"{graphics}\"", xaml);
        Assert.Contains($"ToolTip=\"{UiResourceText.Resource("Gráficos & GPU — diagnóstico somente leitura (Ctrl+7)")}\"", xaml);
        Assert.Contains($"ToolTip=\"{UiResourceText.Resource("Correlação — evidências históricas relacionadas (Ctrl+8)")}\"", xaml);
        Assert.Contains($"<TabItem x:Name=\"GraphicsTab\" Header=\"{graphics}\">", xaml);
    }

    [Fact]
    public void GraphicsPageExposesOnlyReadOnlyActionsAndNoRepairControls()
    {
        var xaml = AppSource("MainWindow.xaml");
        foreach (var control in new[]
                 {
                     "GraphicsPresetCombo", "GraphicsRunButton", "GraphicsCancelButton", "GraphicsReportButton",
                     "GraphicsProgress", "GraphicsStatusText", "GraphicsTabs", "GraphicsAdapterList",
                     "GraphicsMonitorList", "GraphicsDriverList", "GraphicsEventList", "GraphicsWerList",
                     "GraphicsObservationList", "GraphicsObservationDetail", "GraphicsHistoryList"
                 })
            Assert.Contains($"x:Name=\"{control}\"", xaml);

        foreach (var forbidden in new[]
                 {
                     "Reparar", "Corrigir", "Aplicar alteração", "Atualizar driver", "Reiniciar driver",
                     "Alterar resolução", "Alterar HDR", "Alterar frequência"
                 })
            Assert.DoesNotContain(forbidden, xaml, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("AutomationProperties.AutomationId=\"StartGraphicsInvestigation\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"CancelGraphicsInvestigation\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"OpenGraphicsReport\"", xaml);
        Assert.Matches(new Regex(
            @"<TextBox\s+x:Name=""GraphicsObservationDetail""[^>]*IsReadOnly=""True""",
            RegexOptions.Singleline), xaml);
    }

    [Fact]
    public void GraphicsNavigationPrecedesHistorySoCtrlSevenAndEightKeepTheirMeaning()
    {
        var xaml = AppSource("MainWindow.xaml");
        var graphics = xaml.IndexOf($"Content=\"{UiResourceText.Resource("Gráficos & GPU")}\"", StringComparison.Ordinal);
        var history = xaml.IndexOf($"Content=\"{UiResourceText.Resource("Histórico")}\"", StringComparison.Ordinal);
        Assert.True(graphics >= 0 && history > graphics);
    }

    [Fact]
    public void WindowUsesSharedClassificationConstantsInsteadOfHardcodedEventIds()
    {
        var source = AppSource("MainWindow.Graphics.cs");
        Assert.Contains("GraphicsEvidenceClassification.CountLiveKernelReports(", source);
        Assert.DoesNotContain("GraphicsEvidenceClassification.IsLiveKernelCode(", source);
        Assert.Contains("GraphicsEventKind.DisplayDriverReset", source);
        Assert.Contains("Notas de análise", source);
        Assert.DoesNotMatch(new Regex(@"\b4101\b"), source);
        Assert.DoesNotMatch(new Regex(@"\b(117|141|142)\b"), source);
    }

    [Fact]
    public void ClosingWaitsForGraphicsSaveAndCancellationLikeOtherSessions()
    {
        var source = AppSource("MainWindow.xaml.cs");
        Assert.Contains("graphicsBusy", source);
        Assert.Contains("graphicsClosingAfterSave", source);
        Assert.Contains("unsavedGraphics", source);
        Assert.Contains("UpdateGraphicsButtons();", source);
    }

    [Fact]
    public void SharedClassificationTreatsOnlyDisplay4101AsTdrAndKeeps117141142()
    {
        Assert.True(GraphicsEvidenceClassification.IsDisplayDriverReset("Display", 4101));
        Assert.False(GraphicsEvidenceClassification.IsDisplayDriverReset("Display", 4102));
        Assert.False(GraphicsEvidenceClassification.IsDisplayDriverReset("Kernel-Power", 4101));

        Assert.Equal(GraphicsEventKind.DisplayDriverReset,
            GraphicsEvidenceClassification.ClassifyEvent("Display", 4101));
        Assert.Equal(GraphicsEventKind.DisplayDriverOther,
            GraphicsEvidenceClassification.ClassifyEvent("Display", 4102));
        Assert.Equal(GraphicsEventKind.GraphicsDriverVendor,
            GraphicsEvidenceClassification.ClassifyEvent("nvlddmkm", 153));

        foreach (var code in new[] { "117", "141", "142" })
            Assert.True(GraphicsEvidenceClassification.IsLiveKernelCode(code));
        Assert.False(GraphicsEvidenceClassification.IsLiveKernelCode("193"));
        Assert.False(GraphicsEvidenceClassification.IsLiveKernelCode("999"));
    }

    [Fact]
    public void ReportAndUiNeverClaimPhysicalDefectOrCausality()
    {
        var report = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.Persistence", "GraphicsReport.cs"));
        Assert.Contains("não prova defeito físico", report, StringComparison.Ordinal);
        Assert.Contains("Causalidade: não avaliada", report, StringComparison.Ordinal);
        Assert.DoesNotContain("defeito físico confirmado", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("superaquecimento confirmado", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VRAM defeituosa confirmada", report, StringComparison.OrdinalIgnoreCase);
    }
}
