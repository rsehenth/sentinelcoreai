using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using SentinelCore.App;
using SentinelCore.App.Localization;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Debloat;
using SentinelCore.Infrastructure.Modifications;
using SentinelCore.Infrastructure.Repairs;
using SentinelCore.Persistence;

namespace RecoveryBenchmark;

/// <summary>U2 isolated UI only: shell, navigation, DPI/size captures and static accessibility probes.
/// Never starts App, diagnostic runners or Windows adapters; every write goes to the owned temporary work tree.</summary>
internal sealed class U2Host
{
    private readonly List<string> checks = [];
    private bool failed;
    private readonly string repository, work, clean, damaged;
    private U2Host(string repository, string work, string clean, string damaged) { this.repository = repository; this.work = work; this.clean = clean; this.damaged = damaged; }

    [STAThread]
    public static int Run(string[] args)
    {
        var host = new U2Host(Path.GetFullPath(args[0]), FixturePaths.Create(args[1]), FixturePaths.Existing(args[2]), FixturePaths.Existing(args[3]));
        FixturePaths.Separate(host.work, host.damaged); FixturePaths.Separate(host.work, host.clean);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var xml = XDocument.Load(Path.Combine(host.repository, "src/SentinelCore.App/App.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        app.Resources = (ResourceDictionary)XamlReader.Parse(new XElement(p + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:SentinelCore.App;assembly=" + typeof(MainWindow).Assembly.GetName().Name),
            xml.Root!.Element(p + "Application.Resources")!.Nodes()).ToString());
        Localizer.Use(AppLanguage.Portuguese);
        app.DispatcherUnhandledException += (_, e) => { host.Check("dispatcher", false, e.Exception.ToString()); e.Handled = true; };
        app.Startup += async (_, _) =>
        {
            try { await host.RunAll(); }
            catch (Exception error) { host.Check("host", false, error.ToString()); }
            File.WriteAllLines(Path.Combine(host.work, "u2-checks.txt"), host.checks);
            foreach (var line in host.checks) Console.WriteLine(line);
            app.Shutdown(host.failed ? 1 : 0);
        };
        return app.Run();
    }

    private void Check(string name, bool ok, string detail = "") { checks.Add($"{(ok ? "PASS" : "FAIL")} {name} {detail}"); failed |= !ok; }
    private void Info(string line) => checks.Add("INFO " + line);
    private static T C<T>(FrameworkElement element, string name) where T : FrameworkElement => (T)element.FindName(name)!;
    private static object? Invoke(object instance, string method, params object[] args) =>
        instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static async Task Idle() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    private static async Task Wait(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(15);
        if (!done()) throw new TimeoutException("Host U2: estado esperado não atingido.");
    }

    private void Capture(FrameworkElement view, string name, double scale = 1)
    {
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(view.ActualWidth * scale), (int)(view.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(work, name + ".png")); png.Save(stream);
    }

    private async Task<MainWindow> OpenShell(string root)
    {
        var store = new MemoryStore(root);
        await store.LoadAsync();
        // Same corrupted synthetic records as U1: proves integrity warnings reach the global banner.
        var invalidRepair = Path.Combine(root, "memory", "repairs", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(invalidRepair);
        File.WriteAllText(Path.Combine(invalidRepair, "intent.md"), "U2 CORRUPTED SYNTHETIC INTENT");
        var locks = new FileExclusiveLockFactory(root);
        var catalog = T2bCatalog.Create();
        var targets = new SyntheticTargetStore();
        var executor = new DisabledOperationExecutor();
        var engine = new SafetyEngine(catalog, new SyntheticSystemProfileReader(SyntheticSystemProfileReader.Default(DateTimeOffset.UtcNow)),
            targets, new SyntheticProtectionProvider(), store, executor, locks);
        var recovery = new RecoveryService(store, targets, executor, locks, catalog: catalog, syntheticOnly: true);
        var tweaks = new TweakManager(catalog, engine, recovery, store, TweakExecutionMode.RealDisabled);
        var repairCatalog = RepairCatalog.CreateDefault();
        var volumes = new FixtureVolumes();
        var repairs = new RepairManager(repairCatalog, new RepairCenter(repairCatalog,
            new RepairPreflightEngine(repairCatalog, new FixtureProfile(), volumes, new FixtureTools()),
            new SyntheticRepairExecutor(), store, () => @"C:\U2-FIXTURE"), volumes);
        var debloatCatalog = DebloatCatalog.CreateDefault();
        var inventory = SyntheticDebloatInventory.Create(debloatCatalog.All);
        var debloat = new DebloatManager(debloatCatalog, inventory, new SyntheticDebloatRuntimeFactory(inventory), store.CreateDebloatJournal(), locks);
        var shell = new MainWindow(null!, null!, null!, null!, null!, null!, null!, null!, tweaks, repairs,
            store, store, store, store, store, store, store, store, debloat, new RecoveryCenter(store),
            error => Check("unexpected-shell-error", false, error.ToString()))
        { Left = -12000, ShowInTaskbar = false };
        shell.Show();
        await Idle();
        await Wait(() => C<ListBox>(shell, "NavList").IsEnabled && C<Button>(shell, "RunButton").IsEnabled);
        return shell;
    }

    private async Task Select(MainWindow shell, int index)
    {
        var nav = C<ListBox>(shell, "NavList");
        nav.SelectedIndex = index; await Idle();
        if (index == 10) { await Wait(() => C<Button>(shell, "TweaksRefreshButton").IsEnabled); await Wait(() => !C<DebloatView>(shell, "DebloatSection").IsBusy); }
        if (index == 11) await Wait(() => C<Button>(shell, "RepairsRefreshButton").IsEnabled);
        if (index == 12) await Wait(() => C<Button>(C<RecoveryCenterView>(shell, "RecoverySection"), "RefreshButton").IsEnabled);
        await Task.Delay(260); // lets the 220 ms page fade finish before a capture
    }

    private async Task RunAll()
    {
        var shellRoot = Path.Combine(work, "shell-data");
        var shell = await OpenShell(shellRoot);
        var nav = C<ListBox>(shell, "NavList");
        var column = (ColumnDefinition)shell.FindName("SidebarColumn");

        // ── Shell: menu, recolhimento, atalhos ──
        shell.Width = 1320; shell.Height = 860; await Task.Delay(200); await Idle();
        Check("wide-window-expands-menu", column.Width.Value == 248 && !Nav.GetCollapsed(nav));
        shell.Width = 1120; shell.Height = 720; await Task.Delay(200); await Idle();
        Check("minimum-window-auto-collapses-menu", column.Width.Value == 64 && Nav.GetCollapsed(nav), $"column={column.Width.Value}");
        Check("declared-minimum-window", shell.MinWidth == 960 && shell.MinHeight == 520, $"{shell.MinWidth}x{shell.MinHeight}");
        Invoke(shell, "OnToggleSidebar", shell, new RoutedEventArgs());
        await Idle();
        Check("toggle-expands-at-minimum", column.Width.Value == 248 && !Nav.GetCollapsed(nav));
        var navScroll = (ScrollViewer)nav.Template.FindName("Scroll", nav);
        nav.UpdateLayout();
        Check("expanded-menu-fits-without-scroll-at-1120x720", navScroll.ComputedVerticalScrollBarVisibility == Visibility.Collapsed,
            $"viewport={navScroll.ViewportHeight:F0}; extent={navScroll.ExtentHeight:F0}");
        shell.Width = 1200; await Task.Delay(200); await Idle();
        Check("manual-choice-survives-resize", column.Width.Value == 248, "entre 1120 e 1240 px a escolha manual prevalece");
        shell.Width = 1000; await Task.Delay(200); await Idle();
        var toggle = C<Button>(shell, "SidebarToggle");
        Check("narrow-window-forces-collapse", column.Width.Value == 64 && Nav.GetCollapsed(nav) && !toggle.IsEnabled);
        Invoke(shell, "OnToggleSidebar", shell, new RoutedEventArgs()); await Idle();
        Check("narrow-window-refuses-expand", column.Width.Value == 64);
        shell.Width = 1200; await Task.Delay(200); await Idle();
        Check("wider-window-reenables-toggle-and-stays-collapsed", toggle.IsEnabled && column.Width.Value == 64);
        Invoke(shell, "OnToggleSidebar", shell, new RoutedEventArgs()); await Idle();
        Check("toggle-expands-again", column.Width.Value == 248 && !Nav.GetCollapsed(nav));
        Invoke(shell, "OnToggleSidebar", shell, new RoutedEventArgs()); await Idle();
        Check("toggle-collapses", column.Width.Value == 64 && Nav.GetCollapsed(nav));
        shell.Width = 1120; await Idle();
        for (var i = 0; i < nav.Items.Count; i++)
        {
            await Select(shell, i);
            var item = (ListBoxItem)nav.Items[i];
            Check($"nav-collapsed-{i}", ((TabItem)C<TabControl>(shell, "Pages").SelectedItem).Header.ToString() == item.Content.ToString() && item.ToolTip is string tip && tip.StartsWith(item.Content.ToString()!));
        }

        // ── Avisos globais: integridade sobrevive ao início de uma nova execução ──
        var banner = C<Border>(shell, "WarningBanner"); var warningText = C<TextBlock>(shell, "StorageWarnings");
        Check("integrity-warning-visible", banner.Visibility == Visibility.Visible && warningText.Text.Contains("Histórico incompleto — Reparos"), warningText.Text);
        Invoke(shell, "ShowWarning", "Falha simulada da execução atual.");
        Check("run-warning-does-not-replace-integrity", warningText.Text.Contains("Reparos") && warningText.Text.Contains("Falha simulada"));
        Invoke(shell, "ClearRunWarnings");
        Check("new-run-keeps-integrity-drops-run-warning", warningText.Text.Contains("Reparos") && !warningText.Text.Contains("Falha simulada") && banner.Visibility == Visibility.Visible);
        Invoke(shell, "SetIntegrity", "Reparos", Array.Empty<string>());
        Check("clean-reload-clears-source", banner.Visibility == Visibility.Collapsed);
        Invoke(shell, "SetIntegrity", "Reparos", new[] { "registro simulado inválido" }); // restaura para as capturas

        // ── Capturas: tamanho mínimo, expandido e DPI simulado (raiz da janela) ──
        var scenarios = new (string Name, double W, double H, double Scale, bool? Collapsed, int[] Pages)[]
        {
            ("min-auto", 1120, 720, 1, null, Enumerable.Range(0, 13).ToArray()),
            ("min-expanded", 1120, 720, 1, false, [0, 10, 11, 12]),
            ("wide", 1320, 860, 1, null, Enumerable.Range(0, 13).ToArray()),
            ("small", 1000, 680, 1, null, Enumerable.Range(0, 13).ToArray()),
            ("dpi125", 1000, 680, 1.25, null, [0, 11, 12]),
            ("dpi150", 1000, 680, 1.5, null, [0, 10, 11, 12]),
            ("dpi175", 1000, 680, 1.75, null, [0, 12]),
            ("dpi200", 1000, 680, 2, null, [0, 12])
        };
        foreach (var s in scenarios)
        {
            VisualTreeHelper.SetRootDpi(shell, new DpiScale(s.Scale, s.Scale));
            shell.Width = s.W; shell.Height = s.H; await Task.Delay(200); await Idle();
            if (s.Collapsed is { } forced) Invoke(shell, "SetSidebarCollapsed", forced);
            else Invoke(shell, "SetSidebarCollapsed", s.W < 1240);
            foreach (var page in s.Pages)
            {
                await Select(shell, page);
                Capture(shell, $"{s.Name}-p{page:00}", s.Scale);
                if (page == 10 && s.Name is "min-auto" or "dpi150")
                {
                    Invoke(shell, "OnOpenDebloat", shell, new RoutedEventArgs()); await Task.Delay(350); await Idle();
                    Capture(shell, $"{s.Name}-p10-debloat", s.Scale);
                    C<ScrollViewer>(shell, "TweaksScroll").ScrollToTop();
                }
            }
            Check($"captured-{s.Name}", true, $"{s.Pages.Length} páginas em {s.W}x{s.H} @{s.Scale * 100:F0}%");
        }
        VisualTreeHelper.SetRootDpi(shell, new DpiScale(1, 1));

        // ── Sonda dirigida das 13 páginas: o que o DPI altera de fato é a área útil em DIP (1080p: 1920×1040 px ÷ escala).
        // Sem monitor real; a janela externa recebe o tamanho útil de cada escala. Reprova controle fora da área sem rolagem que o alcance,
        // texto de linha única cortado e lista/grade com menos de 60 DIP de altura.
        foreach (var (name, w, h) in new[] { ("200pct-960x520", 960.0, 520.0), ("175pct-1097x594", 1097.0, 594.0), ("150pct-1280x693", 1280.0, 693.0),
                     ("125pct-1536x832", 1536.0, 832.0), ("100pct-1000x680", 1000.0, 680.0), ("100pct-1120x720", 1120.0, 720.0) })
        {
            shell.Width = w; shell.Height = h; Invoke(shell, "SetSidebarCollapsed", w < 1240); await Task.Delay(200); await Idle();
            Info($"{name}: janela efetiva {shell.ActualWidth:F0}x{shell.ActualHeight:F0} (mínimo declarado {shell.MinWidth}x{shell.MinHeight})");
            for (var i = 0; i < nav.Items.Count; i++)
            {
                await Select(shell, i);
                var problems = ProbeLayout(C<TabControl>(shell, "Pages"));
                foreach (var p in problems) Info($"  {name} p{i:00} {p}");
                if (name.StartsWith("200") || name.StartsWith("175")) Capture(shell, $"area-{name}-p{i:00}");
                Check($"layout-{name}-p{i:00}", problems.Count == 0 && shell.ActualWidth == w && shell.ActualHeight == h, $"{problems.Count} problema(s)");
            }
        }
        shell.Width = 1120; shell.Height = 720; Invoke(shell, "SetSidebarCollapsed", false); await Task.Delay(200);

        // ── Central de Recuperação com histórico real de fixtures sintéticas (limpo e danificado) ──
        foreach (var (fixture, label) in new[] { (clean, "clean"), (damaged, "partial") })
            foreach (var (w, h) in new[] { (1056, 680), (808, 680) })
            {
                var view = new RecoveryCenterView { Background = (Brush)System.Windows.Application.Current.Resources["Bg"] };
                var window = new Window { Content = view, Width = w, Height = h, Left = -12000, ShowInTaskbar = false, Background = (Brush)System.Windows.Application.Current.Resources["Bg"] };
                window.Show(); view.Initialize(new RecoveryCenter(new MemoryStore(fixture)));
                await Wait(() => C<Button>(view, "RefreshButton").IsEnabled);
                var grid = C<DataGrid>(view, "HistoryGrid");
                if (label == "clean" && grid.Items.Count > 0) grid.SelectedIndex = 0;
                Capture(view, $"recovery-{label}-{w}-top");
                grid.BringIntoView(); await Idle(); await Task.Delay(120);
                C<ScrollViewer>(view, "RecoveryScroll").ScrollToVerticalOffset(C<ScrollViewer>(view, "RecoveryScroll").VerticalOffset + 40);
                Capture(view, $"recovery-{label}-{w}-grid");
                var scroller = C<ScrollViewer>(view, "RecoveryScroll");
                Check($"recovery-{label}-{w}-no-horizontal-scroll", scroller.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed);
                window.Close();
            }

        // ── Acessibilidade estática: nomes UIA, alvos, texto cortado, ordem de foco ──
        var unnamed = new List<string>(); var small = new List<string>(); var clipped = new List<string>();
        for (var i = 0; i < nav.Items.Count; i++)
        {
            await Select(shell, i);
            var page = C<TabControl>(shell, "Pages"); // o conteúdo da aba selecionada vive no TabControl, não na árvore visual do TabItem
            foreach (var element in Descendants(page).OfType<FrameworkElement>().Where(e => e.IsVisible))
            {
                var label = $"p{i}:{element.GetType().Name}:{element.Name}";
                if (element is Button or ComboBox or DatePicker or ListBox or ListView or DataGrid or CheckBox or RadioButton or Slider ||
                    element is TextBox { IsReadOnly: false } || element is TextBox { IsReadOnly: true, Focusable: true })
                {
                    if (element is ListBoxItem) continue;
                    var peer = UIElementAutomationPeer.CreatePeerForElement(element);
                    var name = peer?.GetName();
                    if (string.IsNullOrWhiteSpace(name)) unnamed.Add(label);
                    if (element is Button && (element.ActualHeight < 30 || element.ActualWidth < 30)) small.Add($"{label} {element.ActualWidth:F0}x{element.ActualHeight:F0}");
                }
                if (element is TextBlock { TextWrapping: TextWrapping.NoWrap, TextTrimming: TextTrimming.None } tb && !string.IsNullOrEmpty(tb.Text) && tb.ActualWidth > 0)
                {
                    var formatted = new FormattedText(tb.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.White, 1);
                    if (formatted.Width > tb.ActualWidth + 2) clipped.Add($"{label} \"{Trim(tb.Text)}\" texto {formatted.Width:F0} > {tb.ActualWidth:F0}");
                }
            }
        }
        Info($"controles interativos sem nome UIA: {unnamed.Count}");
        foreach (var u in unnamed.Distinct()) Info("  sem-nome " + u);
        Check("no-unnamed-interactive-controls", unnamed.Count == 0, string.Join("; ", unnamed.Take(8)));
        Info($"botões com alvo < 30 px: {small.Count}");
        foreach (var u in small) Info("  alvo-pequeno " + u);
        Info($"texto de linha única mais largo que a área: {clipped.Count}");
        foreach (var u in clipped.Distinct()) Info("  possivel-corte " + u);

        await Select(shell, 0);
        // Ordem de Tab sem teclado físico: a janela fora da tela não recebe foco real, então lista-se a ordem estática
        // (árvore visual, TabIndex padrão) dos controles focáveis visíveis; a navegação real por teclado não foi acionada.
        var order = Descendants(shell).OfType<Control>().Where(c => c.IsVisible && c.IsEnabled && c.IsTabStop && c.Focusable && (c is not ListBoxItem item || (item.IsSelected && item.Parent == nav)) && c is not ScrollViewer)
            .Select(c => c is ListBoxItem navItem ? "NavItem:" + navItem.Content : $"{c.GetType().Name}:{(string.IsNullOrEmpty(c.Name) ? AutomationProperties.GetName(c) : c.Name)}").ToList();
        Info("ordem de Tab (estática) na Visão geral: " + string.Join(" > ", order.Take(24)));
        Check("tab-order-starts-with-menu-then-page", order.FindIndex(o => o.Contains("SidebarToggle")) is >= 0 and var menu &&
            order.FindIndex(o => o.StartsWith("NavItem:")) is >= 0 and var list && menu < list && list < order.FindIndex(o => o.Contains("RunButton")));

        // Contraste dos pares de tokens (WCAG 2.x, luminância relativa).
        var res = System.Windows.Application.Current.Resources;
        double Lum(Color c) { double F(byte v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); } return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B); }
        Color Of(string key) => ((SolidColorBrush)res[key]).Color;
        double Ratio(string fg, string bg) { var a = Lum(Of(fg)); var b = Lum(Of(bg)); return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05); }
        foreach (var (fg, bg, min) in new[] { ("Text", "Bg", 4.5), ("Text", "Surface", 4.5), ("Muted", "Surface", 4.5), ("Muted", "Bg", 4.5), ("Subtle", "Surface", 4.5), ("Subtle", "Sidebar", 4.5),
                     ("Accent", "Surface", 4.5), ("Accent", "AccentSoft", 4.5), ("Warn", "WarnSoft", 4.5), ("OnAccent", "Accent", 4.5), ("Danger", "Surface", 4.5), ("Success", "Surface", 4.5), ("FieldLine", "Surface", 3.0), ("FieldLine", "Bg", 3.0), ("Accent", "Bg", 3.0) })
        {
            var ratio = Ratio(fg, bg);
            Check($"contrast-{fg}-on-{bg}", ratio >= min, $"{ratio:F2}:1 (mínimo {min:F1}:1)");
        }
        shell.Close(); await Idle();
        Check("five-gates", !RealTweakBuildGate.AllowsRealExecution([]) && !RealRepairBuildGate.AllowsRealExecution([]) &&
            !RealDebloatBuildGate.AllowsRealExecution() && !NativeDebloatInventoryGate.AllowsActivation() && !DebloatRemovalBuildGate.AllowsRealExecution());
    }

    private static List<string> ProbeLayout(TabControl pages)
    {
        var problems = new List<string>();
        var page = pages;
        pages.UpdateLayout();
        foreach (var e in Descendants(page).OfType<FrameworkElement>().Where(x => x.IsVisible && x.ActualWidth > 0 && x.ActualHeight > 0))
        {
            var label = $"{e.GetType().Name}:{(!string.IsNullOrEmpty(e.Name) ? e.Name : !string.IsNullOrEmpty(AutomationProperties.GetName(e)) ? AutomationProperties.GetName(e) : "«" + Trim(Descendants(e).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "") + "»")}";
            var templatePart = false;
            for (DependencyObject? p = VisualTreeHelper.GetParent(e); p is not null && !ReferenceEquals(p, page); p = VisualTreeHelper.GetParent(p))
                if (p is ListBoxItem or DataGridRow or ComboBox or DatePicker or DataGrid or ListView or ScrollBar or TextBox) { templatePart = true; break; }
            if (!templatePart && e is Button or ComboBox or DatePicker or CheckBox or TextBox { IsReadOnly: false } && !Reachable(e, pages))
                problems.Add("inacessível " + label);
            if (e is ListBox or ListView or DataGrid && e.ActualHeight < 60 && !templatePart) problems.Add($"lista baixa {label} {e.ActualHeight:F0} DIP");
            // Painel arranjado menor que o conteúdo pedido = o WPF recorta o excedente (layout clip), mesmo dentro da área rolável.
            if (!templatePart && e is Panel && e.Visibility == Visibility.Visible && (e.DesiredSize.Width - e.Margin.Left - e.Margin.Right > e.ActualWidth + 2 || e.DesiredSize.Height - e.Margin.Top - e.Margin.Bottom > e.ActualHeight + 2))
                problems.Add($"conteúdo recortado em {label} pedido {e.DesiredSize.Width - e.Margin.Left - e.Margin.Right:F0}x{e.DesiredSize.Height - e.Margin.Top - e.Margin.Bottom:F0} > {e.ActualWidth:F0}x{e.ActualHeight:F0}");
            // Filho que ultrapassa a largura do painel pai é recortado pelo layout (ex.: linha de caixas de seleção mais larga que a coluna).
            if (!templatePart && VisualTreeHelper.GetParent(e) is Panel { } parent and not Canvas && e is not Border { Name: "FocusRing" } and not TextBlock { TextTrimming: not TextTrimming.None } && parent.ActualWidth > 0 && e.TransformToAncestor(parent).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight)).Right > parent.ActualWidth + 1)
                problems.Add($"filho recortado {label} em {parent.GetType().Name}:{parent.Name} ({parent.ActualWidth:F0} DIP)");
            if (e is TextBlock { TextWrapping: TextWrapping.NoWrap, TextTrimming: TextTrimming.None } tb && !string.IsNullOrEmpty(tb.Text))
            {
                var formatted = new FormattedText(tb.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.White, 1);
                if (formatted.Width > tb.ActualWidth + 2) problems.Add($"texto cortado {label} \"{Trim(tb.Text)}\" {formatted.Width:F0}>{tb.ActualWidth:F0}");
            }
        }
        return problems.Distinct().ToList();
    }

    // Alcançável = dentro da área visível de cada ancestral, ou fora dela apenas por um ScrollViewer rolável naquele eixo.
    private static bool Reachable(FrameworkElement element, FrameworkElement boundary)
    {
        FrameworkElement current = element;
        for (DependencyObject? a = VisualTreeHelper.GetParent(element); a is not null; a = VisualTreeHelper.GetParent(a))
        {
            if (a is not (ScrollViewer or TabControl) || a is not FrameworkElement host) continue;
            var area = host is ScrollViewer sv ? new Size(sv.ViewportWidth, sv.ViewportHeight) : new Size(host.ActualWidth, host.ActualHeight);
            var r = current.TransformToAncestor(host).TransformBounds(new Rect(0, 0, current.ActualWidth, current.ActualHeight));
            var outX = r.Right > area.Width + 1 || r.Left < -1; var outY = r.Bottom > area.Height + 1 || r.Top < -1;
            if (host is ScrollViewer { } scroller && (outX || outY))
            {
                if ((outX && scroller.ScrollableWidth <= 0) || (outY && scroller.ScrollableHeight <= 0)) return false;
                current = scroller;
            }
            else if (outX || outY) return false;
            if (ReferenceEquals(host, boundary)) break;
        }
        return true;
    }

    private static string Trim(string text) => text.Length <= 60 ? text : text[..57] + "…";

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            for (var i = VisualTreeHelper.GetChildrenCount(current) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(current, i));
        }
    }
}
