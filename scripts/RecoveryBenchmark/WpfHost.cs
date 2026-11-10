using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using SentinelCore.App;
using SentinelCore.App.Localization;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using SentinelCore.Persistence;

namespace RecoveryBenchmark;

/// <summary>
/// P2: exercises the real RecoveryCenterView in an isolated WPF host. Only synthetic roots, no App startup,
/// no collectors, no Windows queries. Every scenario records PASS/FAIL plus the measured UI behaviour.
/// </summary>
internal sealed class WpfHost
{
    private readonly string repository, work, mediumClean, mediumDamaged, largeClean;
    private readonly List<string> lines = [];
    private bool failed;
    private Window window = null!;

    private WpfHost(string repository, string work, string mediumClean, string mediumDamaged, string largeClean)
    { this.repository = repository; this.work = work; this.mediumClean = mediumClean; this.mediumDamaged = mediumDamaged; this.largeClean = largeClean; }

    [STAThread]
    public static int Run(string[] args)
    {
        var host = new WpfHost(Path.GetFullPath(args[0]), FixturePaths.New(args[1]),
            FixturePaths.Existing(args[2]), FixturePaths.Existing(args[3]), FixturePaths.Existing(args[4]));
        foreach (var source in new[] { host.mediumClean, host.mediumDamaged, host.largeClean })
            FixturePaths.Separate(source, host.work);
        FixturePaths.Create(host.work);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var xml = XDocument.Load(Path.Combine(host.repository, "src", "SentinelCore.App", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:SentinelCore.App;assembly=" + typeof(MainWindow).Assembly.GetName().Name),
            xml.Root!.Element(presentation + "Application.Resources")!.Nodes());
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        Localizer.Use(AppLanguage.Portuguese);
        var unhandled = 0;
        app.DispatcherUnhandledException += (_, e) => { unhandled++; host.lines.Add("UNHANDLED " + e.Exception); e.Handled = true; };
        app.Startup += async (_, _) =>
        {
            try { await host.RunAll(); }
            catch (Exception error) { host.Fail("runner", error.ToString()); }
            host.lines.Add($"unhandled dispatcher exceptions: {unhandled}");
            if (unhandled > 0) host.failed = true;
            Directory.CreateDirectory(host.work);
            File.WriteAllLines(Path.Combine(host.work, "wpf-checks.txt"), host.lines);
            foreach (var line in host.lines) Console.WriteLine(line);
            app.Shutdown(host.failed ? 1 : 0);
        };
        return app.Run();
    }

    private void Pass(string name, string detail = "") => lines.Add($"PASS {name}{(detail.Length > 0 ? " — " + detail : "")}");
    private void Fail(string name, string detail) { failed = true; lines.Add($"FAIL {name} — {detail}"); }
    private void Check(string name, bool condition, string detail = "")
    { if (condition) Pass(name, detail); else Fail(name, detail.Length > 0 ? detail : "condição falsa"); }
    private async Task Scenario(string name, Func<Task> body)
    {
        var watch = Stopwatch.StartNew();
        try { await body(); lines.Add($"  [{name}] concluído em {watch.ElapsedMilliseconds} ms"); }
        catch (Exception error) { Fail(name, error.GetType().Name + ": " + error.Message); }
    }

    private static T Control<T>(FrameworkElement view, string name) where T : FrameworkElement =>
        (T)(view.FindName(name) ?? throw new InvalidOperationException("Controle ausente: " + name));
    private static RecoveryOperation Op(object row) => (RecoveryOperation)row.GetType().GetProperty("Operation")!.GetValue(row)!;
    private static async Task Idle() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    private static async Task Until(Func<bool> condition, int seconds = 60)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < limit) await Task.Delay(25);
        if (!condition()) throw new TimeoutException("Condição não atingida no tempo limite.");
    }

    private (RecoveryCenterView View, DataGrid Grid) Open(RecoveryCenter center)
    {
        var view = new RecoveryCenterView { Background = (Brush)System.Windows.Application.Current.Resources["Bg"] };
        window = new Window
        {
            Width = 1100, Height = 900, Left = -12000, Top = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            Background = (Brush)System.Windows.Application.Current.Resources["Bg"], Content = view
        };
        window.Show();
        view.Initialize(center);
        return (view, Control<DataGrid>(view, "HistoryGrid"));
    }

    private sealed class StallMeter
    {
        private readonly DispatcherTimer timer = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(15) };
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private double last; public double MaxGapMs { get; private set; }
        public StallMeter() { timer.Tick += (_, _) => { var now = watch.Elapsed.TotalMilliseconds; MaxGapMs = Math.Max(MaxGapMs, now - last); last = now; }; }
        public void Start() { last = watch.Elapsed.TotalMilliseconds; MaxGapMs = 0; timer.Start(); }
        public double Stop() { timer.Stop(); return Math.Round(MaxGapMs, 0); }
    }

    private static string Scratch(string source, string target)
    {
        FixturePaths.Existing(source);
        FixturePaths.New(target);
        FixturePaths.Separate(source, target);
        Fixtures.CopyDirectory(source, target);
        return target;
    }

    private async Task RunAll()
    {
        Directory.CreateDirectory(work);
        var mediumRoot = Scratch(mediumClean, Path.Combine(work, "medium"));
        var damagedRoot = Scratch(mediumDamaged, Path.Combine(work, "damaged"));

        await Scenario("01 histórico vazio", async () =>
        {
            var uninitialized = Path.Combine(work, "uninitialized");
            var (view, grid) = Open(new RecoveryCenter(new MemoryStore(uninitialized)));
            await Until(() => Control<TextBlock>(view, "StatusText").Text.Length > 0 && grid.Items.Count == 0 && Control<Button>(view, "RefreshButton").IsEnabled);
            var summary = Control<TextBlock>(view, "HistorySummary").Text;
            Check("01a raiz não inicializada não vira 'nenhuma operação registrada'", !summary.Contains("Nenhuma operação registrada"), summary);
            Check("01b contagens 'Não determinado' em fonte indisponível",
                Control<ItemsControl>(view, "SummaryCards").Items.Cast<object>().Any(i => i.ToString()!.Contains("Não determinado")));
            Check("01c nenhuma pasta criada pela leitura", !Directory.Exists(uninitialized));
            var initialized = Path.Combine(work, "initialized-empty");
            var store = new MemoryStore(initialized);
            await store.LoadTransactionsAsync(); await store.CreateDebloatJournal().LoadTransactionsAsync(); await store.LoadRepairsAsync();
            await view.RefreshAsync(); window.Content = null; window.Close();
            var (view2, grid2) = Open(new RecoveryCenter(new MemoryStore(initialized)));
            await Until(() => Control<TextBlock>(view2, "HistorySummary").Text.Length > 0 && Control<Button>(view2, "RefreshButton").IsEnabled);
            Check("01d raiz inicializada e vazia informa ausência de operações", grid2.Items.Count == 0 &&
                Control<TextBlock>(view2, "HistorySummary").Text.Contains("Nenhuma operação registrada"), Control<TextBlock>(view2, "HistorySummary").Text);
            window.Close();
        });

        await Scenario("02 histórico parcialmente disponível", async () =>
        {
            var partialRoot = Scratch(mediumClean, Path.Combine(work, "partial"));
            File.Delete(Path.Combine(partialRoot, "memory", "REPAIRS.md"));
            var (view, grid) = Open(new RecoveryCenter(new MemoryStore(partialRoot)));
            await Until(() => grid.Items.Count > 0);
            Check("02a fonte parcial mostra aviso e resumo INCOMPLETO",
                Control<Border>(view, "WarningsPanel").Visibility == Visibility.Visible && Control<TextBlock>(view, "SummaryText").Text.Contains("INCOMPLETO"),
                Control<TextBlock>(view, "SummaryText").Text);
            window.Close();
        });

        await Scenario("03 histórico corrompido", async () =>
        {
            var (view, grid) = Open(new RecoveryCenter(new MemoryStore(damagedRoot)));
            await Until(() => grid.Items.Count > 0);
            var warnings = Control<TextBox>(view, "WarningsText").Text;
            Check("03a registros adulterados/ausentes geram avisos preservados e exclusão da validade",
                warnings.Length > 0 && Control<Border>(view, "WarningsPanel").Visibility == Visibility.Visible);
            Check("03b nenhuma contagem completa apresentada", Control<TextBlock>(view, "SummaryText").Text.Contains("INCOMPLETO"));
            window.Close();
        });

        await Scenario("04 histórico volumoso", async () =>
        {
            var center = new RecoveryCenter(new MemoryStore(largeClean));
            var meter = new StallMeter(); var watch = Stopwatch.StartNew(); meter.Start();
            var (view, grid) = Open(center); // Initialize starts the first read
            await Until(() => Control<Button>(view, "RefreshButton").IsEnabled && grid.Items.Count > 0, 900);
            var initial = watch.ElapsedMilliseconds; var initialStall = meter.Stop();
            lines.Add($"  volumoso, carga inicial: {grid.Items.Count} linhas em {initial} ms, maior travamento da UI {initialStall} ms");
            await Idle();
            watch.Restart(); meter.Start();
            await view.RefreshAsync();
            var refresh = watch.ElapsedMilliseconds; var stall = meter.Stop();
            await Idle();
            var realized = Visual(grid).OfType<DataGridRow>().Count();
            lines.Add($"  volumoso: {grid.Items.Count} linhas, atualização {refresh} ms, maior travamento da UI {stall} ms, linhas realizadas {realized}");
            Check("04a grade carregada com virtualização de linhas", grid.Items.Count > 500 && realized < 60, $"itens={grid.Items.Count}, realizadas={realized}");
            Check("04b status final consistente", Control<TextBlock>(view, "StatusText").Text.Length > 0);

            var search = Control<TextBox>(view, "SearchText");
            var all = grid.Items.Count;
            var typing = new List<double>(); meter.Start();
            foreach (var prefix in new[] { "s", "sf", "sfc" })
            {
                var w = Stopwatch.StartNew(); search.Text = prefix; typing.Add(w.Elapsed.TotalMilliseconds);
                await Task.Delay(40); // digitação humana rápida: o debounce deve absorver as teclas intermediárias
            }
            var applyWatch = Stopwatch.StartNew();
            await Until(() => grid.Items.Count != all, 30);
            var typingStall = meter.Stop();
            lines.Add($"  pesquisa digitada: custo do manipulador por tecla (ms) {string.Join("/", typing.Select(t => Math.Round(t, 1)))}; filtro aplicado {applyWatch.ElapsedMilliseconds} ms após a última tecla; maior travamento {typingStall} ms");
            Check("05a pesquisa textual aplicada à grade após o debounce", grid.Items.Count > 0 && grid.Items.Count < all, $"{grid.Items.Count} de {all} resultados");
            search.Text = "zzz-inexistente";
            await Until(() => grid.Items.Count == 0, 30);
            Check("05b pesquisa sem resultado informa que nada corresponde e não que não há histórico",
                grid.Items.Count == 0 && !Control<TextBlock>(view, "HistorySummary").Text.Contains("Nenhuma operação registrada"));
            Control<Button>(view, "ClearFiltersButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(600);
            Check("05c limpar filtros cancela o debounce pendente e restaura a lista", grid.Items.Count == all, $"{grid.Items.Count} de {all}");
            var combo = Control<ComboBox>(view, "ModuleCombo");
            var combined = Stopwatch.StartNew();
            combo.SelectedIndex = 1; Control<ComboBox>(view, "OutcomeCombo").SelectedIndex = 1; search.Text = "sintético";
            await Task.Delay(700);
            lines.Add($"  filtros combinados (módulo+resultado+texto) aplicados em {combined.ElapsedMilliseconds} ms (inclui 700 ms de espera), {grid.Items.Count} resultados");
            Check("06a filtros combinados reduzem de forma consistente",
                grid.Items.Cast<object>().All(r => Op(r).Module == RecoveryModule.Tweaks && Op(r).Outcome == RecoveryOperationOutcome.Completed));
            Control<Button>(view, "ClearFiltersButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("06b limpar filtros restaura a lista completa", grid.Items.Count > 500);
            window.Close();
        });

        await Scenario("07 seleção rápida", async () =>
        {
            var (view, grid) = Open(new RecoveryCenter(new MemoryStore(mediumRoot)));
            await Until(() => grid.Items.Count > 20);
            var pure = Stopwatch.StartNew();
            foreach (var row in grid.Items.Cast<object>().Take(50)) { _ = RecoveryCenterPresentation.Details(Op(row)); _ = RecoveryCenterPresentation.Timeline(Op(row)); }
            lines.Add($"  custo puro de Details+Timeline: {pure.Elapsed.TotalMilliseconds / 50:F2} ms por operação (sem UI)");
            await Task.Delay(1000);
            var sizes = grid.Items.Cast<object>().Take(50).Select(r => RecoveryCenterPresentation.Details(Op(r)).Length).ToArray();
            lines.Add($"  tamanho do texto de detalhes: mediana {sizes.OrderBy(s => s).ElementAt(25)} caracteres, máximo {sizes.Max()}");
            var detailsBox = Control<TextBox>(view, "DetailsText"); var lastText = RecoveryCenterPresentation.Details(Op(grid.Items[0]));
            var setOnly = Stopwatch.StartNew();
            for (var i = 0; i < 40; i++) detailsBox.Text = lastText + i;
            lines.Add($"  40 atribuições diretas de DetailsText.Text: {setOnly.ElapsedMilliseconds / 40.0:F1} ms cada");
            setOnly.Restart();
            for (var i = 0; i < 40; i++) { detailsBox.Text = lastText + i; view.UpdateLayout(); }
            lines.Add($"  40 atribuições + UpdateLayout: {setOnly.ElapsedMilliseconds / 40.0:F1} ms cada");
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 400; i++) grid.SelectedIndex = (i * 7) % grid.Items.Count;
            lines.Add($"  400 trocas de seleção: {watch.ElapsedMilliseconds} ms ({watch.ElapsedMilliseconds / 400.0:F1} ms/troca; SEM layout nem renderização entre as trocas)");
            var layoutWatch = Stopwatch.StartNew();
            for (var i = 0; i < 40; i++) { grid.SelectedIndex = (i * 11) % grid.Items.Count; view.UpdateLayout(); }
            lines.Add($"  40 trocas de seleção COM UpdateLayout síncrono: {layoutWatch.ElapsedMilliseconds / 40.0:F1} ms/troca (janela fora da tela, renderização por software)");
            var selected = Op(grid.SelectedItem);
            Check("07a detalhes correspondem à última seleção após 400 trocas",
                Control<TextBox>(view, "DetailsText").Text.Contains(selected.OperationId) && Control<TextBox>(view, "TimelineText").Text.Length > 0,
                $"{watch.ElapsedMilliseconds} ms");
            window.Close();
        });

        await Scenario("08 troca de seleção durante carregamento", async () =>
        {
            var (view, grid) = Open(new RecoveryCenter(new MemoryStore(mediumRoot)));
            await Until(() => grid.Items.Count > 20);
            grid.SelectedIndex = 3; var chosen = Op(grid.SelectedItem);
            var refresh = view.RefreshAsync();
            Check("08a grade e atualização desabilitadas durante a leitura", !grid.IsEnabled && !Control<Button>(view, "RefreshButton").IsEnabled);
            grid.SelectedIndex = 9; // programmatic change while busy
            await refresh; await Idle();
            var shown = (grid.SelectedItem is null) ? null : Op(grid.SelectedItem);
            var details = Control<TextBox>(view, "DetailsText").Text;
            Check("08b detalhes coerentes com a linha selecionada após a leitura",
                shown is null ? details.Contains("Selecione") : details.Contains(shown.OperationId), shown?.OperationId ?? "sem seleção");
            window.Close();
        });

        await Scenario("09-11 cancelamento, fechamento e reabertura", async () =>
        {
            var center = new RecoveryCenter(new MemoryStore(largeClean));
            var (view, grid) = Open(center);
            await Until(() => grid.Items.Count > 500 || Control<TextBlock>(view, "StatusText").Text.StartsWith("Histórico") || Control<TextBlock>(view, "StatusText").Text.StartsWith("Leitura"));
            var refresh = view.RefreshAsync(); await Task.Delay(40);
            window.Content = null; // detach: Unloaded cancels the in-flight read
            await refresh; await Idle();
            var status = Control<TextBlock>(view, "StatusText").Text;
            Check("09a cancelamento por saída da página não vira sucesso nem histórico vazio",
                status.Contains("interrompida") && grid.Items.Count == 0 && Control<TextBlock>(view, "HistorySummary").Text.Contains("não representa ausência"), status);
            Check("09b controles voltam ao estado utilizável", Control<Button>(view, "RefreshButton").IsEnabled && grid.IsEnabled);
            window.Content = view; await Idle();
            await Until(() => grid.Items.Count > 500 || Control<TextBlock>(view, "StatusText").Text.StartsWith("Leitura incompleta"));
            Check("10a reabertura da mesma página recarrega o histórico", grid.Items.Count > 500, $"{grid.Items.Count} linhas");
            var closing = view.RefreshAsync(); await Task.Delay(40); window.Content = null; window.Close();
            await closing;
            Check("10b fechamento da janela durante leitura sem exceção", true);
            var (view2, grid2) = Open(new RecoveryCenter(new MemoryStore(largeClean)));
            await Until(() => grid2.Items.Count > 500);
            Check("11 nova janela após fechamento carrega normalmente", grid2.Items.Count > 500);
            window.Close();
        });

        await Scenario("12 erro durante a leitura", async () =>
        {
            foreach (Exception error in new Exception[] { new IOException("falha de E/S sintética"), new InvalidOperationException("falha inesperada sintética") })
            {
                var (view, grid) = Open(new RecoveryCenter(new ThrowingReader(error)));
                await Until(() => Control<TextBlock>(view, "StatusText").Text.Length > 0 && Control<Button>(view, "RefreshButton").IsEnabled);
                Check($"12 {error.GetType().Name}: fontes indisponíveis, nada de sucesso/zero",
                    grid.Items.Count == 0 && Control<Border>(view, "WarningsPanel").Visibility == Visibility.Visible &&
                    !Control<TextBlock>(view, "HistorySummary").Text.Contains("Nenhuma operação registrada"),
                    Control<TextBlock>(view, "StatusText").Text);
                Check("12 mensagem compreensível mantém causa", Control<TextBox>(view, "WarningsText").Text.Contains("sintética"));
                window.Close();
            }
        });

        await Scenario("13-14 estado indeterminado e recuperação indisponível", async () =>
        {
            var (view, grid) = Open(new RecoveryCenter(new MemoryStore(mediumRoot)));
            await Until(() => grid.Items.Count > 20);
            var indeterminate = grid.Items.Cast<object>().FirstOrDefault(r => Op(r).Outcome == RecoveryOperationOutcome.Indeterminate);
            Check("13a fixture contém estado indeterminado", indeterminate is not null);
            grid.SelectedItem = indeterminate;
            Check("13b indeterminado exige intervenção e não é apresentado como concluído",
                Op(indeterminate!).RequiresIntervention && Control<TextBox>(view, "DetailsText").Text.Length > 0 &&
                Op(indeterminate!).Outcome != RecoveryOperationOutcome.Completed);
            var tweak = grid.Items.Cast<object>().First(r => Op(r).Module == RecoveryModule.Tweaks);
            grid.SelectedItem = tweak;
            Check("14a composição de produção: análise/recuperação desabilitadas até para Tweaks sintéticos",
                !Control<Button>(view, "AnalyzeButton").IsEnabled && !Control<Button>(view, "RecoverButton").IsEnabled &&
                !Control<TextBox>(view, "ConfirmationText").IsEnabled);
            foreach (var row in grid.Items.Cast<object>().Where(r => Op(r).Module != RecoveryModule.Tweaks).Take(10))
            { grid.SelectedItem = row; if (Control<Button>(view, "AnalyzeButton").IsEnabled || Control<Button>(view, "RecoverButton").IsEnabled) throw new InvalidOperationException("ação habilitada para módulo não suportado"); }
            Check("14b módulos sem rollback nunca habilitam ações", true);
            window.Close();
        });

        await Scenario("15 prévia desatualizada e 16 histórico atualizado entre consultas", async () =>
        {
            var root = Path.Combine(work, "synthetic-recovery");
            FixturePaths.Create(root);
            var store = new MemoryStore(root);
            var catalog = T1SyntheticCatalog.Create();
            var targets = new SyntheticTargetStore(); var executor = new SyntheticOperationExecutor(targets);
            var locks = new FileExclusiveLockFactory(root);
            var engine = new SafetyEngine(catalog, new SyntheticSystemProfileReader(SyntheticSystemProfileReader.Default(DateTimeOffset.UtcNow)),
                targets, new SyntheticProtectionProvider(), store, executor, locks);
            var target = catalog.Get("SYN-PREF-01").Targets.Single();
            async Task<Guid> Seed(string seedId)
            {
                targets.Set(target, new(true, "String", "A"));
                var plan = await engine.SimulateAsync("SYN-PREF-01");
                return (await engine.ExecuteAsync(plan.Plan, engine.Authorize(plan, seedId))).Transaction!.TransactionId;
            }
            var first = await Seed("host-1");
            var recovery = new RecoveryService(store, targets, executor, locks, catalog: catalog, syntheticOnly: true);
            var (view, grid) = Open(new RecoveryCenter(new MemoryStore(root), recovery));
            await Until(() => grid.Items.Count == 1);
            grid.SelectedIndex = 0;
            Control<Button>(view, "AnalyzeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Control<TextBox>(view, "ConfirmationText").IsEnabled);
            Control<TextBox>(view, "ConfirmationText").Text = TweakManager.BuildRecoveryConfirmationText(first);
            Check("15a prévia válida habilita a confirmação específica sintética", Control<Button>(view, "RecoverButton").IsEnabled);
            await Seed("host-2"); // history changed between two queries (16)
            await view.RefreshAsync(); await Idle();
            Check("16a segunda consulta enxerga a operação nova", grid.Items.Count == 2, $"{grid.Items.Count} linhas");
            Check("15b atualização invalida a prévia: confirmação e recuperação desabilitadas",
                !Control<Button>(view, "RecoverButton").IsEnabled && !Control<TextBox>(view, "ConfirmationText").IsEnabled &&
                Control<TextBox>(view, "ConfirmationText").Text.Length == 0);
            Check("16b horário/estado da carga visível ao usuário", Control<TextBlock>(view, "StatusText").Text.Contains("carregado", StringComparison.OrdinalIgnoreCase), Control<TextBlock>(view, "StatusText").Text);
            window.Close();
        });

        Check("gates de build permanecem fechados no host",
            !RealTweakBuildGate.AllowsRealExecution([]) && !RealRepairBuildGate.AllowsRealExecution([]) &&
            !RealDebloatBuildGate.AllowsRealExecution() && !NativeDebloatInventoryGate.AllowsActivation() &&
            !DebloatRemovalBuildGate.AllowsRealExecution());
    }

    private static IEnumerable<T> VisualOf<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in VisualOf<T>(child)) yield return nested;
        }
    }
    private static IEnumerable<DependencyObject> Visual(DependencyObject root) => VisualOf<DependencyObject>(root);

    private sealed class ThrowingReader(Exception error) : IRecoveryHistoryReader
    {
        public Task<IReadOnlyList<RecoveryHistorySource>> ReadRecoveryHistoryAsync(CancellationToken cancellationToken = default) =>
            throw error;
        public string GetRecoveryReportPath(RecoveryModule module, Guid id) => "n/d";
    }
}
