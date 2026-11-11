using System.Text.RegularExpressions;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// Guardas estruturais da Fase 5. O projeto de testes não referencia WPF: o contrato visual é verificado pela
/// estrutura do XAML/code-behind e o contrato de baixo impacto pela ausência de chamadas proibidas.
/// </summary>
public sealed class MonitoringUiContractTests
{
    private static string AppSource(string fileName) =>
        File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", fileName));

    private static string Source(params string[] parts) =>
        File.ReadAllText(TestRepository.Combine(parts));

    [Fact]
    public void NavigationAndTabExposeMonitoringWithoutShadowingHistoryShortcut()
    {
        var xaml = AppSource("MainWindow.xaml");
        var monitoringKey = UiResourceText.Resource("Monitoramento");
        Assert.Contains($"Content=\"{monitoringKey}\"", xaml);
        Assert.Contains($"<TabItem x:Name=\"MonitoringTab\" Header=\"{monitoringKey}\">", xaml);
        Assert.Contains($"ToolTip=\"{UiResourceText.Resource("Monitoramento — sessões autorizadas e incidentes")}\"", xaml);
        Assert.Contains($"ToolTip=\"{UiResourceText.Resource("Histórico — auditorias salvas neste computador (Ctrl+9)")}\"", xaml);
        var history = xaml.IndexOf($"Content=\"{UiResourceText.Resource("Histórico")}\"", StringComparison.Ordinal);
        var monitoring = xaml.IndexOf($"Content=\"{monitoringKey}\"", StringComparison.Ordinal);
        Assert.True(history >= 0 && monitoring > history);
    }

    [Fact]
    public void MonitoringPageExposesAuthorizationDurationSourcesIncidentsAndHistory()
    {
        var xaml = AppSource("MainWindow.xaml");
        foreach (var control in new[]
                 {
                     "MonitoringPresetCombo", "MonitoringAuthorizationCheck", "MonitoringStartButton",
                     "MonitoringStopButton", "MonitoringReportButton", "MonitoringIncidentCategoryCombo",
                     "MonitoringIncidentOccurredCombo", "MonitoringIncidentDescription", "MonitoringIncidentButton",
                     "MonitoringStatusText", "MonitoringProgress", "MonitoringElapsedText",
                     "MonitoringRemainingText", "MonitoringSamplesValue", "MonitoringEventsValue",
                     "MonitoringIncidentsValue", "MonitoringBudgetValue", "MonitoringTabs",
                     "MonitoringIncidentList", "MonitoringIncidentDetail", "MonitoringTimelineList",
                     "MonitoringBudgetDetail", "MonitoringHistoryList", "MonitoringCorrelateButton",
                     "MonitoringEventsCheck", "MonitoringPerformanceCheck", "MonitoringNetworkCheck",
                     "MonitoringGraphicsCheck", "CorrelationMonitoringCheck"
                 })
            Assert.Contains($"x:Name=\"{control}\"", xaml);

        Assert.Contains($"Header=\"{UiResourceText.Resource("Incidentes")}\"", xaml);
        Assert.Contains($"Header=\"{UiResourceText.Resource("Linha do tempo")}\"", xaml);
        Assert.Contains($"Header=\"{UiResourceText.Resource("Orçamento e limites")}\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"StartMonitoring\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"RegisterIncident\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"CorrelateIncident\"", xaml);
        Assert.Matches(new Regex(
            @"<TextBox\s+x:Name=""MonitoringIncidentDetail""[^>]*IsReadOnly=""True""",
            RegexOptions.Singleline), xaml);
        Assert.Matches(new Regex(
            @"<TextBox\s+x:Name=""MonitoringBudgetDetail""[^>]*IsReadOnly=""True""",
            RegexOptions.Singleline), xaml);
    }

    [Fact]
    public void MonitoringPageHasNoRepairOrSystemModificationControls()
    {
        var xaml = AppSource("MainWindow.xaml");
        foreach (var forbidden in new[]
                 {
                     "Reparar", "Corrigir", "Aplicar alteração", "Atualizar driver", "Reiniciar driver",
                     "Alterar resolução", "Alterar HDR", "Executar comando", "Aplicar correção",
                     "Capturar tela", "Gravar teclado", "Instalar serviço", "Criar tarefa agendada"
                 })
            Assert.DoesNotContain(forbidden, xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MonitoringIsNotStartedAutomaticallyOnLoad()
    {
        var source = AppSource("MainWindow.xaml.cs");
        var loaded = source[source.IndexOf("private async void OnLoaded", StringComparison.Ordinal)..
            source.IndexOf("private async Task ReloadAsync", StringComparison.Ordinal)];
        Assert.Contains("ReloadMonitoringAsync()", loaded);
        Assert.DoesNotContain("RunAsync", loaded);
    }

    [Fact]
    public void ClosingWaitsForTheMonitoringCheckpointAndSave()
    {
        var source = AppSource("MainWindow.xaml.cs");
        Assert.Contains("monitoringBusy", source);
        Assert.Contains("monitoringClosingAfterSave", source);
        Assert.Contains("unsavedMonitoring", source);
        Assert.Contains("monitoringCancellation", source);
        Assert.Contains("MonitoringTabs", source);
        Assert.Contains("Nenhuma observação continua após o fechamento", source);
    }

    [Fact]
    public void UserFacingTextKeepsIncidentProvenanceAndNeverDeclaresCausality()
    {
        var source = AppSource("MainWindow.Monitoring.cs");
        Assert.Contains("MonitoringGuidance.CategoryName(", source);
        Assert.Contains("MonitoringGuidance.StatusName(", source);
        Assert.Contains("não é evento do Windows", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("causou", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("defeito confirmado", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Monitoramento desativado", source);
    }

    [Fact]
    public void ReportKeepsMandatoryLimitationsAndIncidentDisclaimer()
    {
        var report = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.Persistence",
            "MonitoringReport.cs"));
        Assert.Contains("relato do usuário", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Causalidade: não avaliada", report);
        Assert.Contains("evento comprovado do Windows", report);
        Assert.Contains("nunca é reescrito", report);
        Assert.DoesNotContain("causou", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MonitoringNeverUsesResidentServicesScheduledTasksOrProcessInjection()
    {
        var files = new[]
        {
            Source("src", "SentinelCore.Application", "MonitoringSessionRunner.cs"),
            Source("src", "SentinelCore.Application", "MonitoringSources.cs"),
            Source("src", "SentinelCore.Infrastructure.Windows", "Monitoring", "MonitoringPerformanceCollector.cs"),
            Source("src", "SentinelCore.Infrastructure.Windows", "Monitoring", "WindowsMonitoringEventSource.cs"),
            Source("src", "SentinelCore.Persistence", "MonitoringReport.cs")
        };
        foreach (var content in files)
            foreach (var forbidden in new[]
                     {
                         "ServiceController", "ServiceInstaller", "Microsoft.Win32.TaskScheduler",
                         "RegisterTaskDefinition", "schtasks",
                         "Registry.LocalMachine", "ManagementObject", "Process.Start", "CreateRemoteThread",
                         "WinDivert", "RawSocket", "CaptureFile"
                     })
                Assert.DoesNotContain(forbidden, content, StringComparison.Ordinal);
    }

    [Fact]
    public void LowImpactCadenceAndRequiredLimitationsAreCentralized()
    {
        var limits = MonitoringLimits.Default;
        Assert.True(limits.PerformanceInterval >= TimeSpan.FromSeconds(10));
        Assert.True(limits.EventInterval >= TimeSpan.FromSeconds(10));
        Assert.True(limits.NetworkInterval >= limits.EventInterval);
        Assert.True(limits.SegmentDuration >= TimeSpan.FromMinutes(1));
        Assert.True(limits.MaxSessionDuration <= TimeSpan.FromMinutes(30));
        Assert.Contains("serviço", MonitoringGuidance.ScopeLimitation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("não declara causa", MonitoringGuidance.CausalityLimitation,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("autorizada", MonitoringGuidance.AuthorizationLimitation,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MonitoringSourceCanBeSelectedForCorrelationButIsOptIn()
    {
        var xaml = AppSource("MainWindow.xaml");
        Assert.Matches(new Regex(
            @"<CheckBox\s+x:Name=""CorrelationMonitoringCheck""[^>]*IsChecked=""False""",
            RegexOptions.Singleline), xaml);
        var source = AppSource("MainWindow.Correlation.cs");
        Assert.Contains("CorrelationSources.Monitoring", source);
        Assert.True(Enum.IsDefined(CorrelationSource.Monitoring));
        Assert.True(CorrelationSources.All.HasFlag(CorrelationSources.Monitoring));
    }
}
