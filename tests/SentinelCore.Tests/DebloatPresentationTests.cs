using System.Xml.Linq;
using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>Structural safety contracts; these do not replace validation of the rendered WPF surface.</summary>
public sealed class DebloatPresentationTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static string Source(string name) => File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", name));

    [Fact]
    public void ConfirmationIsEditableAndExecutionStartsDisabled()
    {
        var document = XDocument.Parse(Source("DebloatView.xaml"));
        var confirmation = document.Descendants(Presentation + "TextBox")
            .Single(item => (string?)item.Attribute(Xaml + "Name") == "ConfirmationText");
        Assert.Equal("False", (string?)confirmation.Attribute("IsReadOnly"));
        Assert.Equal("False", (string?)confirmation.Attribute("IsEnabled"));
        var execute = document.Descendants(Presentation + "Button")
            .Single(item => (string?)item.Attribute(Xaml + "Name") == "ExecuteButton");
        Assert.Equal("False", (string?)execute.Attribute("IsEnabled"));
        Assert.Contains("simulação", UiResourceText.ResolveAttribute(execute, "Content"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InventoryIsIndividualReadOnlyAndSyntheticWarningIsVisible()
    {
        var document = XDocument.Parse(Source("DebloatView.xaml"));
        Assert.Contains(document.Descendants(Presentation + "TextBlock"),
            item => UiResourceText.ResolveAttribute(item, "Text") == DebloatLimits.SyntheticWarning);
        var gridStyle = document.Descendants(Presentation + "Style")
            .Single(item => (string?)item.Attribute("TargetType") == "DataGrid");
        Assert.Contains(gridStyle.Elements(Presentation + "Setter"), item =>
            (string?)item.Attribute("Property") == "SelectionMode" && (string?)item.Attribute("Value") == "Single");
        Assert.Contains(gridStyle.Elements(Presentation + "Setter"), item =>
            (string?)item.Attribute("Property") == "IsReadOnly" && (string?)item.Attribute("Value") == "True");
        var source = Source("DebloatView.xaml.cs");
        Assert.Contains("DebloatExecutionMode.Synthetic", source);
        Assert.DoesNotContain("DebloatExecutionMode.Real", source);
        Assert.DoesNotContain("Process.Start", source);
        Assert.DoesNotContain("System.Management", source);
    }

    [Fact]
    public void CatalogAndInventoryAreVisuallyDistinguished()
    {
        var document = XDocument.Parse(Source("DebloatView.xaml"));
        var labels = document.Descendants(Presentation + "TextBlock")
            .Select(item => UiResourceText.ResolveAttribute(item, "Text")).ToArray();
        Assert.Contains("Catálogo de aplicativos", labels);
        Assert.Contains("Inventário de aplicativos", labels);
        // A coluna de estado do inventário nativo é exatamente a projeção de presença: o texto de
        // "não verificado" só chega à grade por DebloatPresentation.Presence, nunca por ausência inventada.
        Assert.Contains(document.Descendants(Presentation + "DataGridTextColumn"),
            item => (string?)item.Attribute("Binding") == "{Binding State}");
        var view = Source("DebloatView.xaml.cs");
        Assert.Contains("public string State => Loc.T(DebloatPresentation.Presence(Assessment.Presence));", view);
        Assert.DoesNotContain("não instalado", DebloatPresentation.Presence(DebloatPresence.NotVerified),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Não verificado — inventário real indisponível",
            DebloatPresentation.Presence(DebloatPresence.NotVerified), StringComparison.Ordinal);
        Assert.DoesNotContain(labels, text => text.Contains("não encontrado", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("não instalado", StringComparison.OrdinalIgnoreCase));
        var headers = document.Descendants(Presentation + "DataGridTextColumn")
            .Select(item => UiResourceText.ResolveAttribute(item, "Header")).ToArray();
        Assert.Contains("Origem do registro", headers);
        Assert.Contains("Presença no inventário", headers);
    }

    [Fact]
    public void ReinstallationIsDocumentaryAndNoRecoveryActionIsExposed()
    {
        var document = XDocument.Parse(Source("DebloatView.xaml"));
        Assert.Contains(document.Descendants(Presentation + "TextBlock"), item =>
            UiResourceText.ResolveAttribute(item, "Text") == DebloatLimits.RecoveryWarning);
        var labels = document.Descendants(Presentation + "Button")
            .Select(item => UiResourceText.ResolveAttribute(item, "Content")).ToArray();
        Assert.DoesNotContain(labels, label => label.Contains("rollback", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("reinstalar", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("remover", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NativeLabelsDistinguishOriginsStatesAndCompleteness()
    {
        Assert.Equal(DebloatPresentation.NativeInventoryOrigin,
            DebloatPresentation.InventorySource(DebloatInventoryOrigin.NativeAppx));
        Assert.Equal(DebloatPresentation.InventoryOrigin,
            DebloatPresentation.InventorySource(DebloatInventoryOrigin.Synthetic));
        Assert.Equal("Consulta não executada — inventário não consultado.",
            DebloatPresentation.QueryState(DebloatInventoryQueryState.NotExecuted));
        Assert.Equal("Consulta concluída sem resultados — inventário vazio observado.",
            DebloatPresentation.QueryState(DebloatInventoryQueryState.CompletedNoResults));
        Assert.Contains("parcialmente", DebloatPresentation.QueryState(DebloatInventoryQueryState.Partial));
        Assert.Contains("negada", DebloatPresentation.QueryState(DebloatInventoryQueryState.AccessDenied));
        Assert.Contains("interrompida", DebloatPresentation.QueryState(DebloatInventoryQueryState.Interrupted));
        Assert.Contains("Falha de enumeração", DebloatPresentation.QueryState(DebloatInventoryQueryState.Failed));
        Assert.Contains("indisponível", DebloatPresentation.QueryState(DebloatInventoryQueryState.Unavailable));
        Assert.Equal(DebloatNativeMessages.OutOfCatalog,
            DebloatPresentation.CatalogMatch(DebloatCatalogMatchKind.NotInCatalog));
        Assert.Equal("Presente no inventário nativo (não homologado).",
            DebloatPresentation.Presence(DebloatPresence.PresentInNativeInventory));
        Assert.Equal(DebloatPresentation.NativeInventoryOrigin,
            DebloatPresentation.Origin(DebloatPresence.PresentInNativeInventory));
        Assert.Contains("parcial", DebloatPresentation.Completeness(DebloatObservationCompleteness.Partial));
    }
}
