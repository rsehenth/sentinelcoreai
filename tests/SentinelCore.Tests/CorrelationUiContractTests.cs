using System.Text.RegularExpressions;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// Guardas estruturais da Fase 3.4. O projeto de testes não referencia WPF: o contrato visual é verificado pela
/// estrutura do XAML e do code-behind, e o contrato de evidência pelas constantes compartilhadas.
/// </summary>
public sealed class CorrelationUiContractTests
{
    private static string AppSource(string fileName) => TestRepository.AppSource(fileName);

    private static string Source(string fileName) =>
        File.ReadAllText(TestRepository.Combine("src", "SentinelCore.Application", fileName));

    [Fact]
    public void NavigationContainsCorrelationEntryAndKeepsHistoryAsTheLastShortcut()
    {
        var xaml = AppSource("MainWindow.xaml");
        Assert.Contains("Content=\"Correlação\"", xaml);
        Assert.Contains("Ctrl+8", xaml);
        Assert.Contains("Ctrl+9", xaml);
        var document = System.Xml.Linq.XDocument.Parse(xaml);
        Assert.Contains(document.Descendants(), element => element.Name.LocalName == "TabItem" &&
            (string?)element.Attribute("Header") == "Correlação");
        var correlation = xaml.IndexOf("Content=\"Correlação\"", StringComparison.Ordinal);
        var history = xaml.IndexOf("Content=\"Histórico\"", StringComparison.Ordinal);
        Assert.True(correlation >= 0 && history > correlation);
    }

    [Fact]
    public void CorrelationPageExposesInvestigationTimelineRelationsCoverageAndHistory()
    {
        var xaml = AppSource("MainWindow.xaml");
        foreach (var control in new[]
                 {
                     "CorrelationPresetCombo", "CorrelationRunButton", "CorrelationCancelButton",
                     "CorrelationReportButton", "CorrelationAuditCheck", "CorrelationHardwareCheck",
                     "CorrelationEventCheck", "CorrelationPerformanceCheck", "CorrelationNetworkCheck",
                     "CorrelationGraphicsCheck", "CorrelationProgress", "CorrelationStatusText",
                     "CorrelationTabs", "CorrelationTimelineList", "CorrelationRelationList",
                     "CorrelationRelationDetail", "CorrelationEvidenceList", "CorrelationEvidenceDetail",
                     "CorrelationSourceList", "CorrelationCoverageDetail", "CorrelationHistoryList"
                 })
            Assert.Contains($"x:Name=\"{control}\"", xaml);

        Assert.Contains("Header=\"Linha do tempo\"", xaml);
        Assert.Contains("Header=\"Relações\"", xaml);
        Assert.Contains("Header=\"Evidências\"", xaml);
        Assert.Contains("Header=\"Cobertura\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"StartCorrelation\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"OpenCorrelationReport\"", xaml);
        Assert.Matches(new Regex(
            @"<TextBox\s+x:Name=""CorrelationRelationDetail""[^>]*IsReadOnly=""True""",
            RegexOptions.Singleline), xaml);
        Assert.Matches(new Regex(
            @"<TextBox\s+x:Name=""CorrelationEvidenceDetail""[^>]*IsReadOnly=""True""",
            RegexOptions.Singleline), xaml);
    }

    [Fact]
    public void CorrelationPageHasNoRepairOrModificationControls()
    {
        var xaml = AppSource("MainWindow.xaml");
        foreach (var forbidden in new[]
                 {
                     "Reparar", "Corrigir", "Aplicar alteração", "Atualizar driver", "Reiniciar driver",
                     "Alterar resolução", "Alterar HDR", "Executar comando", "Aplicar correção"
                 })
            Assert.DoesNotContain(forbidden, xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorrelationNeverReadsHardwareOrEventLogDirectly()
    {
        var reader = Source("CorrelationEvidenceReader.cs");
        foreach (var forbidden in new[]
                 {
                     "ManagementObject", "EventLogReader", "EventLogQuery", "System.Management",
                     "Microsoft.Win32.Registry", "IHardwareCollector", "IEventEvidenceCollector"
                 })
            Assert.DoesNotContain(forbidden, reader, StringComparison.Ordinal);

        var runner = Source("CorrelationRunner.cs");
        Assert.DoesNotContain("IHardwareCollector", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("IEventEvidenceCollector", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowUsesSharedGuidanceInsteadOfLocalCausalClaimsOrHardcodedRuleIds()
    {
        var source = AppSource("MainWindow.Correlation.cs");
        Assert.Contains("CorrelationGuidance.SourceName(", source);
        Assert.Contains("CorrelationGuidance.PrecisionName(", source);
        Assert.Contains("CorrelationGuidance.RelationKindName(", source);
        Assert.DoesNotContain("CORR-TEMPORAL", source);
        Assert.DoesNotContain("causou", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("causalidade não avaliada", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClosingWaitsForCorrelationSaveAndCancellation()
    {
        var source = AppSource("MainWindow.xaml.cs");
        Assert.Contains("correlationBusy", source);
        Assert.Contains("correlationClosingAfterSave", source);
        Assert.Contains("unsavedCorrelation", source);
        Assert.Contains("correlationCancellation", source);
        Assert.Contains("CorrelationTabs", source);
    }

    [Fact]
    public void CorrelationIsNotStartedAutomaticallyOnLoad()
    {
        var source = AppSource("MainWindow.xaml.cs");
        var loaded = source[source.IndexOf("private async void OnLoaded", StringComparison.Ordinal)..
            source.IndexOf("private async Task ReloadAsync", StringComparison.Ordinal)];
        Assert.Contains("ReloadCorrelationAsync()", loaded);
        Assert.DoesNotContain("RunAsync", loaded);
    }

    [Fact]
    public void ReportNeverDeclaresCausalityAndKeepsScopeLimitation()
    {
        var report = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.Persistence",
            "CorrelationReport.cs"));
        Assert.Contains("Causalidade: não avaliada", report);
        Assert.Contains("Correlação não é causalidade", report);
        Assert.Contains("CorrelationGuidance.InsufficientEvidenceStatement", report);
        Assert.DoesNotContain("defeito físico confirmado", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("causou", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharedRulesAreVersionedIdentifiableAndIndependentPerWindow()
    {
        var limits = new CorrelationLimits();
        Assert.True(limits.GraphicsWerWindow > limits.GraphicsApplicationWindow);
        Assert.True(limits.ChangeEventWindow > limits.GraphicsWerWindow);
        Assert.Equal("1.0", CorrelationGuidance.RuleVersion);
        Assert.NotEqual(CorrelationGuidance.GraphicsWerRuleId, CorrelationGuidance.GraphicsApplicationRuleId);
        Assert.NotEqual(CorrelationGuidance.PerformanceEventRuleId, CorrelationGuidance.NetworkEventRuleId);
        Assert.Equal(CausalityStatus.NotAssessed, CausalityStatus.NotAssessed);
        Assert.Contains("CORRELAÇÃO NÃO AVALIÁVEL", CorrelationGuidance.InsufficientEvidenceStatement,
            StringComparison.Ordinal);
    }
}
