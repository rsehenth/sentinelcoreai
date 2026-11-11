using System.Xml.Linq;
using Xunit;

namespace SentinelCore.Tests;

public sealed class RecoveryCenterUiContractTests
{
    private static string Source(string file) => TestRepository.AppSource(file);
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void NavigationAndPageRemainInTheSameOrder()
    {
        var document = XDocument.Parse(Source("MainWindow.xaml"));
        var navigation = document.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "NavList");
        var pages = document.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "Pages");
        Assert.Equal(navigation.Elements().Count(), pages.Elements().Count());
        Assert.Equal("Central de Recuperação", (string?)navigation.Elements().Last().Attribute("Content"));
        Assert.Equal("Central de Recuperação", (string?)pages.Elements().Last().Attribute("Header"));
        Assert.Contains("RecoverySection.Initialize(recovery)", Source("MainWindow.xaml.cs"));
    }

    [Fact]
    public void HistoryIsReadOnlyAndFiltersAreExplicit()
    {
        var document = XDocument.Parse(Source("RecoveryCenterView.xaml"));
        var names = document.Descendants().Select(e => (string?)e.Attribute(Xaml + "Name")).Where(n => n is not null).ToHashSet();
        foreach (var name in new[] { "SummaryText", "SummaryCards", "WarningsText", "SearchText", "ModuleCombo",
                     "OutcomeCombo", "OriginCombo", "CapabilityCombo", "TypeCombo", "StateCombo", "RiskCombo",
                     "AvailableCombo", "ConflictCombo", "InterventionCombo", "FromDate", "ToDate", "HistoryGrid",
                     "DetailsText", "TimelineText", "ReportPathText", "AnalyzeButton", "RecoverButton" })
            Assert.Contains(name, names);
        foreach (var property in new[] { "IsReadOnly", "CanUserAddRows", "CanUserDeleteRows" })
            Assert.Contains(document.Descendants(), e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == property);
        Assert.Contains("RecoveryCenter.Filter(snapshot, filter)", Source("RecoveryCenterView.xaml.cs"));
    }

    [Fact]
    public void ProductionComposesHistoryWithoutRecoveryOrWindowsExecutor()
    {
        var app = Source("App.xaml.cs");
        Assert.Contains("recovery: new RecoveryCenter(store, tweaks: tweakManager)", app);
        Assert.Contains("TweakExecutionMode.RealDisabled", app);
        Assert.DoesNotContain("new RecoveryCenter(store, tweakRecovery)", app);
        var view = Source("RecoveryCenterView.xaml.cs");
        Assert.Contains("SyntheticRecoveryAvailable", view);
        Assert.Contains("center?.CanAnalyze(operation) == true", view);
        Assert.Contains("!center.CanAnalyze(operation)", view);
        var backend = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.Application", "RecoveryCenter.cs"));
        Assert.Contains("SyntheticRecoveryAvailable && IsSyntheticTweak(operation)", backend);
        Assert.Contains("RealRecoveryAvailable && operation is { Module: RecoveryModule.Tweaks,", backend);
        Assert.Contains("TweakManager.BuildRecoveryConfirmationText", view);
        Assert.DoesNotContain("Process.Start", view);
        Assert.DoesNotContain("HkcuRegistryAccess", view);
        Assert.DoesNotContain("RepairCenter.Execute", view);
        Assert.DoesNotContain("DebloatRemovalManager", view);
    }

    [Fact]
    public void UnavailableCountsAndFixtureLimitationsAreVisible()
    {
        var xaml = Source("RecoveryCenterView.xaml");
        var view = Source("RecoveryCenterView.xaml.cs");
        Assert.Contains("Fixtures são identificadas como sintéticas", xaml);
        Assert.Contains("Uma intenção não comprova início de execução", xaml);
        Assert.Contains("não recuperam configurações, licenças, contas ou dados pessoais", xaml);
        Assert.Contains("Não determinado", view);
        Assert.Contains("Contagens não determinadas", view);
        Assert.Contains("fonte {s.Status}; contagem total indisponível", view);
        Assert.Contains("Leitura não concluída; não representa ausência de operações", view);
        Assert.Contains("IsEnabled=\"False\"", xaml);
    }
}
