using System.Text.RegularExpressions;
using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// O projeto de testes não referencia WPF. O contrato visual é verificado pela estrutura do XAML e pela
/// ausência de comandos livres, execução automática e botões decorativos no code-behind.
/// </summary>
public sealed class RepairUiContractTests
{
    private static string AppSource(string fileName) =>
        File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", fileName));

    [Fact]
    public void NavigationAndTabExposeTheWindowsRepairCenterAfterPersonalization()
    {
        var xaml = AppSource("MainWindow.xaml");
        var repairsKey = UiResourceText.Resource("Reparos do Windows");
        Assert.Contains($"Content=\"{repairsKey}\"", xaml);
        Assert.Contains($"<TabItem x:Name=\"RepairsTab\" Header=\"{repairsKey}\">", xaml);
        var personalization = xaml.IndexOf($"Content=\"{UiResourceText.Resource("Personalização & Debloat")}\"", StringComparison.Ordinal);
        var repairs = xaml.IndexOf($"Content=\"{repairsKey}\"", StringComparison.Ordinal);
        Assert.True(personalization >= 0 && repairs > personalization);
    }

    [Fact]
    public void RepairPageExposesFourCategoriesVolumeSelectionPlanOutputAndHistory()
    {
        var xaml = AppSource("MainWindow.xaml");
        foreach (var control in new[]
                 {
                     "RepairsTab", "RepairsScroll", "RepairsModeText", "RepairsModeDetail",
                     "RepairsSyntheticBanner", "RepairsRefreshButton", "RepairsVolumeCombo",
                     "RepairsVolumeText", "RepairsCardsHost", "RepairsDetailsTitle", "RepairsDetailsMeta",
                     "RepairsDetailsText", "RepairsScenarioCombo", "RepairsConfirmationHint",
                     "RepairsConfirmationText", "RepairsAuthorizeButton", "RepairsCancelButton",
                     "RepairsRetrySaveButton", "RepairsProgress", "RepairsProgressLabel", "RepairsStatusText",
                     "RepairsHistoryList", "RepairsUnfinishedText", "RepairsReportButton", "RepairsOutputText",
                     "RepairsDocumentedText"
                 })
            Assert.Contains($"x:Name=\"{control}\"", xaml);

        Assert.Contains("RepairCards", xaml);
        Assert.Contains("RepairHistory", xaml);
        Assert.Contains("RepairPlan", xaml);
        Assert.Contains("RepairOutput", xaml);
        Assert.Contains("RepairDocumented", xaml);
        Assert.Contains($"Text=\"{UiResourceText.Resource("Operações documentadas, indisponíveis")}\"", xaml);
        Assert.Contains($"Text=\"{UiResourceText.Resource("Volumes locais validados")}\"", xaml);
        Assert.Contains($"Text=\"{UiResourceText.Resource("Demonstração sintética")}\"", xaml);
        Assert.Contains($"Text=\"{UiResourceText.Resource("Saída e interpretação")}\"", xaml);

        Assert.Equal(4, RepairCategories.All.Count);
        Assert.Contains(RepairCategories.Integrity, RepairCategories.All);
        Assert.Contains(RepairCategories.Image, RepairCategories.All);
        Assert.Contains(RepairCategories.Volumes, RepairCategories.All);
        Assert.Contains(RepairCategories.Recovery, RepairCategories.All);
    }

    [Fact]
    public void VolumeIsAValidatedComboBoxAndThereIsNoFreeFormCommandInput()
    {
        var xaml = AppSource("MainWindow.xaml");
        Assert.Matches(new Regex(
            @"<ComboBox\s+x:Name=""RepairsVolumeCombo""[^>]*DisplayMemberPath=""DisplayText""",
            RegexOptions.Singleline), xaml);
        Assert.DoesNotContain("x:Name=\"RepairCommand\"", xaml);
        Assert.DoesNotContain("x:Name=\"RepairArguments\"", xaml);
        Assert.DoesNotContain("x:Name=\"RepairsCommandText\"", xaml);
        Assert.DoesNotContain("x:Name=\"RepairsArgumentsText\"", xaml);
        Assert.DoesNotContain("x:Name=\"RepairsExecutableText\"", xaml);
        Assert.DoesNotContain("x:Name=\"RepairsPathText\"", xaml);
    }

    [Fact]
    public void SimulationAndSyntheticFixtureWarningsAreExplicit()
    {
        var code = AppSource("MainWindow.Repairs.cs");
        Assert.Contains("RepairGuidance.RealExecutionUnavailable", code, StringComparison.Ordinal);
        Assert.Contains("RepairGuidance.SyntheticDisclaimer", code, StringComparison.Ordinal);
        Assert.Contains("RepairGuidance.CancellationLimitation", code, StringComparison.Ordinal);
        Assert.Contains("RepairGuidance.NoRollback", code, StringComparison.Ordinal);
        Assert.Contains("RepairGuidance.LocalOnly", code, StringComparison.Ordinal);
        Assert.Contains("fixture sintética", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resultado real do Windows", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExecutionRequiresTypedConfirmationAndARealReportButtonState()
    {
        var xaml = AppSource("MainWindow.xaml");
        var code = AppSource("MainWindow.Repairs.cs");
        Assert.Contains("RepairManager.BuildConfirmationText(plan)", code, StringComparison.Ordinal);
        Assert.Contains("RepairsConfirmationText.Text.Trim()", code, StringComparison.Ordinal);
        Assert.Contains("repairPlan is { SimulationAllowed: true }", code, StringComparison.Ordinal);
        Assert.Contains("RepairsAuthorizeButton.IsEnabled", code, StringComparison.Ordinal);
        Assert.Matches(new Regex(
            @"<Button\s+x:Name=""RepairsAuthorizeButton""[^>]*IsEnabled=""False""",
            RegexOptions.Singleline), xaml);
        Assert.Matches(new Regex(
            @"<Button\s+x:Name=""RepairsReportButton""[^>]*IsEnabled=""False""",
            RegexOptions.Singleline), xaml);
        Assert.Matches(new Regex(
            @"<Button\s+x:Name=""RepairsRetrySaveButton""[^>]*IsEnabled=""False""",
            RegexOptions.Singleline), xaml);
    }

    [Fact]
    public void LoadingThePageNeverExecutesARepairAutomatically()
    {
        var source = AppSource("MainWindow.xaml.cs");
        var loaded = source[source.IndexOf("private async void OnLoaded", StringComparison.Ordinal)..
            source.IndexOf("private async Task ReloadAsync", StringComparison.Ordinal)];
        // V2: a abertura lê só o histórico do store; o preflight aguarda a página Reparos (ver StartupIsolationTests).
        Assert.Contains("ReloadRepairHistoryAsync()", loaded);
        Assert.DoesNotContain("ExecuteAsync", loaded);
        Assert.DoesNotContain("Authorize(", loaded);
    }

    [Fact]
    public void ClosingWaitsForTheSyntheticExecutionAndPersistence()
    {
        var source = AppSource("MainWindow.xaml.cs");
        Assert.Contains("repairsBusy", source);
        Assert.Contains("repairClosingAfterSave", source);
        Assert.Contains("repairCancellation?.Cancel()", source);
        Assert.Contains("A interrupção real de uma ferramenta oficial não é garantida", source);
    }

    [Fact]
    public void RepairCodeDoesNotStartProcessesOrUseShells()
    {
        var code = AppSource("MainWindow.Repairs.cs");
        foreach (var forbidden in new[]
                 {
                     "Process.Start", "ProcessStartInfo", "System.Diagnostics.Process", "cmd.exe",
                     "powershell", "Start-Process", "bcdedit", "reg.exe"
                 })
            Assert.DoesNotContain(forbidden, code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoPercentageIsInventedInTheRepairProgress()
    {
        var xaml = AppSource("MainWindow.xaml");
        Assert.Matches(new Regex(
            @"<ProgressBar\s+x:Name=""RepairsProgress""[^>]*IsIndeterminate=""False""",
            RegexOptions.Singleline), xaml);
        Assert.DoesNotContain("RepairsProgress.Value", AppSource("MainWindow.Repairs.cs"));
        Assert.Contains("sem percentual confiável", AppSource("MainWindow.Repairs.cs"));
    }

    [Fact]
    public void RepairReportKeepsMandatoryDisclaimersAndPrivacyNote()
    {
        var report = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.Persistence",
            "RepairReport.cs"));
        Assert.Contains("RepairGuidance.SyntheticDisclaimer", report, StringComparison.Ordinal);
        Assert.Contains("RepairGuidance.NoRollback", report, StringComparison.Ordinal);
        Assert.Contains("RepairGuidance.NoAutomaticRepair", report, StringComparison.Ordinal);
        Assert.Contains("RepairGuidance.ExitCodeLimitation", report, StringComparison.Ordinal);
        Assert.Contains("RepairGuidance.LocalOnly", report, StringComparison.Ordinal);
        Assert.Contains("perfil do usuário é redigido", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("não declara que o Windows foi reparado", report, StringComparison.OrdinalIgnoreCase);
    }
}
