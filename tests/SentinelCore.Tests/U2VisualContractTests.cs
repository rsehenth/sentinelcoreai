using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>U2: contratos de apresentação lidos somente dos arquivos do repositório (sem WPF, sem App, sem coletores).</summary>
public sealed class U2VisualContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static string AppFile(string name) => TestRepository.Combine("src", "SentinelCore.App", name);
    private static string Source(string name) => TestRepository.AppSource(name);
    private static XDocument Window() => XDocument.Parse(Source("MainWindow.xaml"));

    [Fact]
    public void ShellKeepsThirteenGroupedPagesWithNamesForTheCollapsedMenu()
    {
        var document = Window();
        var nav = document.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "NavList");
        var items = nav.Elements().ToArray();
        Assert.Equal(13, items.Length);
        foreach (var item in items)
        {
            // Recolhido, só o ícone aparece: o nome vem do conteúdo (UIA); a dica descreve a mesma área.
            var label = (string)item.Attribute("Content")!;
            var expectedTip = (string?)item.Attribute(Xaml + "Name") == "TweaksNav"
                ? "Personalização, inventário Appx/MSIX e simulação Debloat" : label;
            Assert.StartsWith(expectedTip, (string?)item.Attribute("ToolTip"), StringComparison.Ordinal);
            Assert.NotNull(item.Attribute("Tag"));
        }
        var groups = items.Select(i => i.Attributes().FirstOrDefault(a => a.Name.LocalName == "Nav.Group")?.Value).ToArray();
        Assert.False(string.IsNullOrEmpty(groups[0]));
        Assert.Equal(3, groups.Count(g => !string.IsNullOrEmpty(g)));
        Assert.Contains("bloqueada", groups.Last(g => !string.IsNullOrEmpty(g)));

        var toggle = document.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "SidebarToggle");
        Assert.Equal("OnToggleSidebar", (string?)toggle.Attribute("Click"));
        Assert.False(string.IsNullOrWhiteSpace(toggle.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name").Value));
        var window = document.Root!;
        Assert.Equal("960", (string?)window.Attribute("MinWidth"));
        Assert.Equal("520", (string?)window.Attribute("MinHeight"));
    }

    [Fact]
    public void ShellCodeBehindKeepsShortcutsAndLimitsTheSidebarOnNarrowWindows()
    {
        var source = Source("MainWindow.xaml.cs");
        Assert.Contains("key is >= Key.D1 and <= Key.D9 ? key - Key.D1 : -1", source);
        Assert.Contains("e.Key == Key.B", source);
        Assert.Contains("SidebarForcedCollapseWidth", source);
        Assert.Contains("SizeChanged += OnShellSizeChanged", source);
    }

    [Fact]
    public void DashboardOffersEachOtherPageExactlyOnceAndKeepsAuditActions()
    {
        var document = Window();
        var nav = document.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "NavList");
        var labels = nav.Elements().Select(e => (string)e.Attribute("Content")!).ToArray();
        var tiles = document.Descendants().Where(e => (string?)e.Attribute("Click") == "OnNavigate").ToArray();
        var targets = tiles.Select(t => int.Parse((string)t.Attribute("Tag")!, CultureInfo.InvariantCulture)).OrderBy(i => i).ToArray();
        Assert.Equal(Enumerable.Range(1, 12), targets);
        foreach (var tile in tiles)
            Assert.Equal("Abrir " + labels[int.Parse((string)tile.Attribute("Tag")!, CultureInfo.InvariantCulture)],
                tile.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name").Value);
        foreach (var name in new[] { "RunButton", "CancelButton", "ReportButton", "ExecutionStatus", "AuditProgressBar" })
            Assert.Single(document.Descendants(), e => (string?)e.Attribute(Xaml + "Name") == name);
        // O cancelamento e o relatório seguem desabilitados até haver o que cancelar/abrir.
        Assert.Equal("False", (string?)document.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "CancelButton").Attribute("IsEnabled"));
        Assert.Equal("False", (string?)document.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "ReportButton").Attribute("IsEnabled"));
    }

    [Fact]
    public void StatusBarKeepsTheBlockedExecutionNoticeVisibleOnEveryPage()
    {
        var xaml = Source("MainWindow.xaml");
        var bar = xaml.IndexOf("Rodapé global", StringComparison.Ordinal);
        Assert.True(bar > xaml.LastIndexOf("</TabControl>", StringComparison.Ordinal) - 20);
        var text = xaml[bar..];
        Assert.Contains("execução real de tweaks, reparos, remoção e recuperação bloqueada", text);
        Assert.Contains("nada é enviado", text);
        Assert.Contains("x:Name=\"VersionText\"", text);
    }

    [Fact]
    public void PageHeadersShareTheIconStylesInsteadOfCopiedColors()
    {
        var xaml = Source("MainWindow.xaml");
        Assert.DoesNotContain("#2F6482", xaml);
        Assert.True(Regex.Matches(xaml, @"Style=""\{StaticResource PageIcon\}""").Count >= 10);
        Assert.Contains("PageIcon", Source("RecoveryCenterView.xaml"));
        foreach (var header in new[] { "Desempenho", "Rede", "Gráficos & GPU" })
            Assert.Contains($"<TextBlock Text=\"{header.Replace("&", "&amp;")}\" Style=\"{{StaticResource PageTitle}}\"/>", xaml);
    }

    [Fact]
    public void IntegrityWarningsAreSeparatedFromRunWarningsAndNeverReplacedSilently()
    {
        var main = Source("MainWindow.xaml.cs");
        Assert.Contains("private readonly Dictionary<string, string[]> integrityWarnings", main);
        Assert.Contains("Histórico incompleto — ", main);
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(AppFile("MainWindow.xaml"))!, "MainWindow*.cs"))
        {
            var code = File.ReadAllText(file);
            if (file.EndsWith("MainWindow.xaml.cs", StringComparison.Ordinal))
                Assert.Single(Regex.Matches(code, @"WarningBanner\.Visibility = "));
            else
                Assert.DoesNotContain("WarningBanner.Visibility", code);
            Assert.DoesNotContain("ShowWarning(string.Join(\"\\n\"", code);
        }
        Assert.Contains("SetIntegrity(\"Reparos\"", Source("MainWindow.Repairs.cs"));
    }

    [Fact]
    public void PresentationLabelsCoverEveryEnumValueWithoutChangingPersistedNames()
    {
        var ui = Source("UiText.cs");
        foreach (var name in Enum.GetNames<TransactionState>()) Assert.Contains($"TransactionState.{name} =>", ui);
        foreach (var name in Enum.GetNames<RecoveryOutcome>()) Assert.Contains($"RecoveryOutcome.{name} =>", ui);
        foreach (var name in Enum.GetNames<RecoveryConflictKind>()) Assert.Contains($"RecoveryConflictKind.{name} =>", ui);
        foreach (var name in Enum.GetNames<OperationScope>()) Assert.Contains($"OperationScope.{name} =>", ui);
        // Somente a camada App apresenta rótulos: nenhum enum de Domain foi renomeado e o valor desconhecido mantém o nome.
        Assert.Equal(4, Regex.Matches(ui, @"_ => \w+\.ToString\(\)").Count);
        Assert.Contains("UiText.Risk(View.Risk)", Source("MainWindow.Tweaks.cs"));
        Assert.DoesNotContain("\"Risco: \" + View.Risk;", Source("MainWindow.Tweaks.cs"));
    }

    [Fact]
    public void ThemeTokensKeepReadableContrastAndInteractiveOutlines()
    {
        var tokens = XDocument.Load(AppFile("App.xaml")).Descendants(Presentation + "SolidColorBrush")
            .Where(e => e.Attribute(Xaml + "Key") is not null)
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => (string)e.Attribute("Color")!);
        double Channel(int value) { var x = value / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        double Luminance(string hex) => 0.2126 * Channel(Convert.ToInt32(hex[1..3], 16)) + 0.7152 * Channel(Convert.ToInt32(hex[3..5], 16)) + 0.0722 * Channel(Convert.ToInt32(hex[5..7], 16));
        double Ratio(string foreground, string background)
        {
            var (a, b) = (Luminance(tokens[foreground]), Luminance(tokens[background]));
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }
        foreach (var (foreground, background, minimum) in new[]
        {
            ("Text", "Bg", 4.5), ("Text", "Surface", 4.5), ("Muted", "Surface", 4.5), ("Muted", "Bg", 4.5), ("Subtle", "Surface", 4.5),
            ("Subtle", "Sidebar", 4.5), ("Accent", "Surface", 4.5), ("Accent", "AccentSoft", 4.5), ("Warn", "WarnSoft", 4.5),
            ("OnAccent", "Accent", 4.5), ("Danger", "Surface", 4.5), ("Success", "Surface", 4.5), ("FieldLine", "Surface", 3.0), ("FieldLine", "Bg", 3.0)
        })
            Assert.True(Ratio(foreground, background) >= minimum, $"{foreground} sobre {background}: {Ratio(foreground, background):F2}:1");
    }

    [Fact]
    public void InteractiveControlTemplatesKeepAKeyboardFocusState()
    {
        var app = XDocument.Load(AppFile("App.xaml"));
        foreach (var type in new[] { "Button", "CheckBox", "ListBoxItem", "TextBox", "ComboBox", "TabItem" })
        {
            var styles = app.Descendants(Presentation + "Style").Where(s => (string?)s.Attribute("TargetType") == type).ToArray();
            Assert.NotEmpty(styles);
            Assert.Contains(styles, s => s.ToString().Contains("IsKeyboardFocus", StringComparison.Ordinal));
        }
        Assert.Contains("IsKeyboardFocused", Source("App.xaml").Substring(Source("App.xaml").IndexOf("TargetType=\"Expander\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void RecoveryViewKeepsBlockedRecoveryStatesAndTheTextualHistory()
    {
        var xaml = Source("RecoveryCenterView.xaml");
        Assert.Equal(8, Regex.Matches(xaml, @"<DataGridTextColumn ElementStyle=""\{StaticResource RecoveryCell\}""").Count);
        Assert.Contains("Binding=\"{Binding StateText}\"", xaml);
        Assert.Contains("<UniformGrid Columns=\"4\"/>", xaml);
        Assert.Contains("IsEnabled=\"False\"", xaml);
        var code = Source("RecoveryCenterView.xaml.cs");
        Assert.Contains("UiText.Recorded", code);
        Assert.Contains("Operation.State", code); // o filtro continua usando o valor gravado, não o rótulo
    }

    [Fact]
    public void EmptyListsSayNothingWasLoadedInsteadOfClaimingAbsence()
    {
        var app = Source("App.xaml");
        Assert.Equal(2, Regex.Matches(app, @"x:Name=""Empty""").Count);
        Assert.Equal(2, Regex.Matches(app, @"Trigger Property=""HasItems"" Value=""False""").Count);
        var xaml = Source("MainWindow.xaml");
        foreach (var name in new[] { "HardwareSnapshotList", "HardwareItemList", "EventHistoryList", "PerformanceHistoryList", "NetworkHistoryList",
                     "GraphicsHistoryList", "CorrelationHistoryList", "HistoryList", "MonitoringHistoryList", "TweakHistoryList", "RepairsHistoryList" })
        {
            var tag = Regex.Match(xaml, $@"<ListBox x:Name=""{name}""[^>]*?\sTag=""([^""]+)""");
            Assert.True(tag.Success, name);
            Assert.Contains("carregad", tag.Groups[1].Value);
        }
        // O botão "Detalhes" não usa Tag (reservada ao ícone do botão) para guardar identificadores.
        Assert.DoesNotContain("Tag=\"{Binding OperationId}\"", xaml);
    }

    [Fact]
    public void EventSearchIsNotSharingARowWithTheCategoryFilterAndWideRowsWrapInsteadOfClipping()
    {
        var document = Window();
        // Lado a lado a busca ficava com ~100 DIP em 1000 px de largura e o texto era cortado: pai deve ser empilhado, nunca um Grid de colunas.
        var search = document.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "EventSearch");
        var combo = document.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "EventCategoryCombo");
        Assert.Equal("StackPanel", search.Parent!.Parent!.Name.LocalName);
        Assert.Same(search.Parent!.Parent, combo.Parent);
        // Linhas horizontais de ações podiam passar da largura útil e esconder o botão (página rola só na vertical): WrapPanel.
        foreach (var name in new[] { "TweakRollbackButton", "RepairsRetrySaveButton", "MonitoringIncidentButton" })
            Assert.Equal("WrapPanel", document.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == name).Parent!.Name.LocalName);
    }

    [Fact]
    public void FullHeightPagesScrollInsteadOfCrushingTheirListsAndTheMinimumFitsA1080pScreenAt200Percent()
    {
        var document = Window();
        Assert.Equal("960", (string?)document.Root!.Attribute("MinWidth"));
        Assert.Equal("520", (string?)document.Root.Attribute("MinHeight")); // 1080p a 200%: ≈ 960×520 DIP úteis
        var pages = document.Descendants(Presentation + "TabItem").Where(t => t.Parent!.Attribute(Xaml + "Name")?.Value == "Pages").ToArray();
        Assert.Equal(12, pages.Count(p => p.Descendants(Presentation + "ScrollViewer").Any()));
        foreach (var header in new[] { "Resultados", "Hardware", "Estabilidade e Eventos", "Desempenho", "Rede", "Gráficos & GPU", "Correlação", "Histórico", "Monitoramento" })
        {
            var host = pages.Single(p => (string?)p.Attribute("Header") == header).Elements().Single();
            Assert.Equal("ScrollViewer", host.Name.LocalName);
            var fill = host.Elements().Single();
            Assert.Equal("Border", fill.Name.LocalName);
            Assert.True(int.Parse((string)fill.Attribute("MinHeight")!, CultureInfo.InvariantCulture) >= 520, header);
            Assert.Contains("ViewportHeight", (string)fill.Attribute("Height")!);
        }
        Assert.Contains("SystemParameters.WorkArea", Source("MainWindow.xaml.cs"));
    }
}
