using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using SentinelCore.App;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Debloat;
using SentinelCore.Infrastructure.Modifications;
using SentinelCore.Infrastructure.Repairs;
using SentinelCore.Persistence;

namespace RecoveryBenchmark;

/// <summary>
/// U3 isolated UI journeys: the real MainWindow over a MemoryStore in the owned temporary work tree, artificial history
/// and a synthetic monitoring source. Never starts App, diagnostic runners, Windows adapters or real executors.
/// </summary>
internal sealed class U3Host
{
    private readonly List<string> checks = [];
    private bool failed;
    private readonly string work;
    private U3Host(string work) => this.work = work;

    [STAThread]
    public static int Run(string[] args)
    {
        var repository = Path.GetFullPath(args[0]);
        var host = new U3Host(FixturePaths.Create(args[1]));
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var xml = XDocument.Load(Path.Combine(repository, "src/SentinelCore.App/App.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        app.Resources = (ResourceDictionary)XamlReader.Parse(new XElement(p + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:SentinelCore.App;assembly=" + typeof(MainWindow).Assembly.GetName().Name),
            xml.Root!.Element(p + "Application.Resources")!.Nodes()).ToString());
        app.DispatcherUnhandledException += (_, e) => { host.Check("dispatcher", false, e.Exception.ToString()); e.Handled = true; };
        app.Startup += async (_, _) =>
        {
            try { if (args.Length > 2 && args[2] == "repair-preflight") await host.CheckRepairPreflight(); else await host.RunAll(); }
            catch (Exception error) { host.Check("host", false, error.ToString()); }
            File.WriteAllLines(Path.Combine(host.work, "u3-checks.txt"), host.checks);
            foreach (var line in host.checks) Console.WriteLine(line);
            app.Shutdown(host.failed ? 1 : 0);
        };
        return app.Run();
    }

    private void Check(string name, bool ok, string detail = "")
    { checks.Add($"{(ok ? "PASS" : "FAIL")} {name} {detail}"); failed |= !ok; }
    private void Info(string line) => checks.Add("INFO " + line);
    private static T C<T>(FrameworkElement element, string name) where T : FrameworkElement => (T)element.FindName(name)!;
    private static async Task Idle() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    private static async Task Wait(Func<bool> done, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(15);
        if (!done()) throw new TimeoutException("Host U3: estado esperado não atingido: " + what);
    }
    // The shell reacts to Click (user toggles), not to IsChecked alone.
    private static void SetCheck(CheckBox box, bool value) { box.IsChecked = value; Click(box); }
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static string Txt(FrameworkElement e) => e switch
    { TextBlock t => t.Text, TextBox b => b.Text, ContentControl c => c.Content?.ToString() ?? "", _ => "" };
    private static int Count(FrameworkElement shell, string list) => ((ItemsControl)shell.FindName(list)!).Items.Count;
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
    private static string AllText(DependencyObject root) => string.Join("\n",
        VisualOf<TextBlock>(root).Select(t => t.Text).Concat(VisualOf<TextBox>(root).Select(t => t.Text)));
    private void Capture(FrameworkElement view, string name)
    {
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(work, name + ".png")); png.Save(stream);
    }

    // ---- composition: the same shell composition as U1/U2, with real correlation and a synthetic monitor ----
    private MainWindow Open(string root, bool syntheticMonitor = false, IRecoveryHistoryReader? reader = null, RepairCounters? counters = null)
    {
        var store = new MemoryStore(root);
        var locks = new FileExclusiveLockFactory(root);
        var catalog = T2bCatalog.Create();
        var targets = new SyntheticTargetStore();
        var executor = new DisabledOperationExecutor();
        var engine = new SafetyEngine(catalog, new SyntheticSystemProfileReader(SyntheticSystemProfileReader.Default(DateTimeOffset.UtcNow)),
            targets, new SyntheticProtectionProvider(), store, executor, locks);
        var recovery = new RecoveryService(store, targets, executor, locks, catalog: catalog, syntheticOnly: true);
        var tweaks = new TweakManager(catalog, engine, recovery, store, TweakExecutionMode.RealDisabled);
        var repairCatalog = RepairCatalog.CreateDefault();
        var center = counters is null ? RepairParts(store, repairCatalog, out _) :
            new RepairCenter(repairCatalog, new RepairPreflightEngine(repairCatalog, counters, counters, counters),
                new SyntheticRepairExecutor(), store, () => @"C:\U3-FIXTURE");
        var repairs = new RepairManager(repairCatalog, center, counters ?? (IRepairVolumeProvider)new FixtureVolumes());
        var debloatCatalog = DebloatCatalog.CreateDefault();
        var inventory = SyntheticDebloatInventory.Create(debloatCatalog.All);
        var debloat = new DebloatManager(debloatCatalog, inventory, new SyntheticDebloatRuntimeFactory(inventory), store.CreateDebloatJournal(), locks);
        var correlation = new EvidenceCorrelationRunner(new CorrelationEvidenceReader(store, store, store, store, store, store, monitoring: store));
        var monitor = syntheticMonitor
            ? new MonitoringSessionRunner([new SyntheticMonitor()], store, TimeProvider.System, FastLimits())
            : null!;
        // Null diagnostic runners are deliberate: collection actions are never invoked by this host.
        return new MainWindow(null!, null!, null!, null!, null!, null!, correlation, monitor, tweaks, repairs,
            store, store, store, store, store, store, store, store, debloat, new RecoveryCenter(reader ?? store),
            error => Check("unexpected-shell-error", false, error.ToString())) { Left = -12000, ShowInTaskbar = false };
    }
    private static RepairCenter RepairParts(IRepairRecordStore store, RepairCatalog catalog, out FixtureVolumes volumes)
    {
        volumes = new FixtureVolumes();
        return new RepairCenter(catalog, new RepairPreflightEngine(catalog, new FixtureProfile(), volumes, new FixtureTools()),
            new SyntheticRepairExecutor(), store, () => @"C:\U3-FIXTURE");
    }
    private static MonitoringLimits FastLimits() => new()
    {
        PerformanceInterval = TimeSpan.FromMilliseconds(100), EventInterval = TimeSpan.FromMilliseconds(100),
        NetworkInterval = TimeSpan.FromMilliseconds(150), SegmentDuration = TimeSpan.FromMilliseconds(250),
        MaxSamplesPerSource = 200, MaxEventsPerSession = 200, MaxEventsPerTick = 20, MaxIncidents = 5,
        MaxIncidentDescriptionLength = 40, MaxCollectorTick = TimeSpan.FromSeconds(2), MaxSessionBytes = 512 * 1024
    };
    private static async Task Ready(MainWindow shell)
    {
        shell.Show(); await Idle();
        await Wait(() => C<ListBox>(shell, "NavList").IsEnabled && C<Button>(shell, "RunButton").IsEnabled, "shell pronto");
    }

    private sealed class RepairCounters : IRepairSystemProfileReader, IRepairVolumeProvider, IRepairToolLocator
    {
        public int Profiles, Volumes, Tools;
        public RepairSystemProfile Read() { Profiles++; return new FixtureProfile().Read(); }
        public Task<IReadOnlyList<RepairVolume>> GetVolumesAsync(CancellationToken cancellationToken = default)
        { Volumes++; return new FixtureVolumes().GetVolumesAsync(cancellationToken); }
        public RepairToolLocation Resolve(RepairTool tool) { Tools++; return new FixtureTools().Resolve(tool); }
    }

    private async Task CheckRepairPreflight()
    {
        var counters = new RepairCounters();
        var shell = Open(Path.Combine(work, "repair-preflight"), counters: counters);
        await Ready(shell);
        Check("startup-no-preflight", (counters.Profiles, counters.Volumes, counters.Tools) == (0, 0, 0));
        await GoTo(shell, 11);
        Check("first-entry-preflight", counters.Profiles > 0 && counters.Volumes > 0 && counters.Tools > 0 &&
            Count(shell, "RepairsCardsHost") > 0);
        var before = (counters.Profiles, counters.Volumes, counters.Tools);
        Click(C<Button>(shell, "RepairsRefreshButton"));
        await Wait(() => C<Button>(shell, "RepairsRefreshButton").IsEnabled, "atualização de Reparos");
        Check("refresh-preflight", counters.Profiles > before.Profiles && counters.Volumes > before.Volumes && counters.Tools > before.Tools);
        await CloseShell(shell, "repair-preflight");
    }
    private static async Task GoTo(MainWindow shell, int page)
    {
        C<ListBox>(shell, "NavList").SelectedIndex = page; await Idle();
        if (page == 10)
        {
            await Wait(() => C<Button>(shell, "TweaksRefreshButton").IsEnabled, "Tweaks");
            await Wait(() => !C<DebloatView>(shell, "DebloatSection").IsBusy, "Debloat");
        }
        if (page == 11) await Wait(() => C<Button>(shell, "RepairsRefreshButton").IsEnabled, "Reparos");
        if (page == 12) await Wait(() => C<Button>(C<RecoveryCenterView>(shell, "RecoverySection"), "RefreshButton").IsEnabled, "Recuperação");
    }
    private async Task CloseShell(MainWindow shell, string name)
    {
        shell.Close(); await Wait(() => !shell.IsLoaded, "fechamento " + name); await Idle();
        Check($"{name}-closed-safely", !shell.IsLoaded);
    }

    private static async Task SeedDiagnostics(string root)
    {
        var store = new MemoryStore(root);
        var at = DateTimeOffset.UtcNow.AddHours(-2);
        var auditId = Guid.NewGuid();
        var data = new InventoryData(OperatingSystem: new OperatingSystemInfo("Windows 11", "Pro", "10.0", "26100", "x64", at.AddHours(-3)));
        var result = new CollectorResult(auditId, "u3-synthetic", "1.0.0", at, at, TimeSpan.Zero,
            DiagnosticStatus.Completed, "Fixture sintética U3", data, []);
        await store.SaveAsync(new AuditRecord(1, auditId, AuditKind.Basic, SoftwareInfo.Version, at, at, TimeSpan.Zero,
            DiagnosticStatus.Completed, [result]));
        var cpu = new HardwareItem(HardwareCategory.Cpu, "cpu", "synthetic-cpu", "CPU sintética", null, null,
            null, null, null, null, null, null, null, null, DevicePresence.Present, [], []);
        var collected = new HardwareCollectorResult("cpu", "1.0.0", HardwareCategory.Cpu, "Fixture sintética U3", at, at,
            TimeSpan.Zero, HardwareCollectionStatus.Completed, [cpu], []);
        await store.SaveSnapshotAsync(new HardwareSnapshot(1, Guid.NewGuid(), SoftwareInfo.Version,
            SoftwareInfo.SnapshotsSchemaVersion, at, at, TimeSpan.Zero, HardwareCollectionStatus.Completed, [collected]));
    }

    private static async Task SeedRepairs(string root)
    {
        var store = new MemoryStore(root);
        var center = RepairParts(store, RepairCatalog.CreateDefault(), out _);
        async Task<RepairExecutionResult> Execute(string id, RepairFixtureScenario scenario)
        {
            var plan = await center.PrepareAsync(id, null);
            var token = center.Authorize(plan, RepairManager.BuildConfirmationText(plan));
            return await center.ExecuteAsync(plan, token, scenario, null);
        }
        await Execute(RepairCatalog.SfcVerifyId, RepairFixtureScenario.SuccessNoIssues);
        await Execute(RepairCatalog.SfcRepairId, RepairFixtureScenario.RepairFailed);
        await Execute(RepairCatalog.DismCheckHealthId, RepairFixtureScenario.Interrupted);
        var orphan = await Execute(RepairCatalog.DismCheckHealthId, RepairFixtureScenario.SuccessNoIssues);
        File.Delete(Path.Combine(root, "memory", "repairs", orphan.Record!.Id.ToString("D"), "record.md"));
    }

    private async Task RunAll()
    {
        // Jornada 1 — primeira utilização (armazenamento vazio)
        var first = Path.Combine(work, "j1-empty");
        var shell = Open(first); await Ready(shell);
        var nav = C<ListBox>(shell, "NavList"); var pages = C<TabControl>(shell, "Pages");
        Check("j1-ready", Txt(C<TextBlock>(shell, "ExecutionStatus")).Length > 0, Txt(C<TextBlock>(shell, "ExecutionStatus")));
        for (var i = 0; i < nav.Items.Count; i++)
        {
            await GoTo(shell, i);
            Check($"j1-nav-{i}", pages.SelectedIndex == i &&
                ((TabItem)pages.SelectedItem).Header.ToString() == ((ListBoxItem)nav.Items[i]).Content.ToString());
        }
        foreach (var list in new[] { "HistoryList", "HardwareSnapshotList", "EventHistoryList", "PerformanceHistoryList",
                     "NetworkHistoryList", "GraphicsHistoryList", "CorrelationHistoryList", "MonitoringHistoryList", "TweakHistoryList", "RepairsHistoryList" })
            Check($"j1-empty-{list}", Count(shell, list) == 0);
        var recoveryView = C<RecoveryCenterView>(shell, "RecoverySection");
        Check("j1-recovery-empty-not-success", Count(recoveryView, "HistoryGrid") == 0 &&
            !Txt(C<TextBlock>(recoveryView, "SummaryText")).Contains("inexistente", StringComparison.OrdinalIgnoreCase),
            Txt(C<TextBlock>(recoveryView, "SummaryText")));
        await GoTo(shell, 0);
        var dashboard = AllText(shell);
        Check("j1-dashboard-no-invented-health", !dashboard.Contains("saudável", StringComparison.OrdinalIgnoreCase) &&
            !dashboard.Contains("sem problemas", StringComparison.OrdinalIgnoreCase));
        Check("j1-blocked-actions", !C<Button>(shell, "MonitoringStartButton").IsEnabled && !C<Button>(shell, "TweakAuthorizeButton").IsEnabled &&
            !C<Button>(shell, "RepairsAuthorizeButton").IsEnabled && !C<Button>(recoveryView, "RecoverButton").IsEnabled);
        Capture(shell, "j1-dashboard");
        await CloseShell(shell, "j1");
        // Fechamento imediato durante a carga inicial e reabertura na mesma raiz.
        var early = Open(first); early.Show(); early.Close(); await Task.Delay(400); await Idle();
        Check("j1-close-during-load", !early.IsLoaded);
        shell = Open(first); await Ready(shell); Check("j1-reopen-after-early-close", C<ListBox>(shell, "NavList").IsEnabled);
        await CloseShell(shell, "j1-reopen");

        // Jornada 2 — diagnóstico e histórico artificiais
        var diagnostics = Path.Combine(work, "j2-diagnostics");
        await SeedDiagnostics(diagnostics);
        shell = Open(diagnostics); await Ready(shell);
        nav = C<ListBox>(shell, "NavList"); pages = C<TabControl>(shell, "Pages");
        Check("j2-audit-history", Count(shell, "HistoryList") == 1);
        await GoTo(shell, 8);
        C<ListBox>(shell, "HistoryList").SelectedIndex = 0; await Idle();
        Check("j2-audit-details", Txt(C<TextBox>(shell, "HistoryDetails")).Length > 0);
        await GoTo(shell, 0);
        var tile = VisualOf<Button>(shell).FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Abrir Hardware");
        Check("j2-dashboard-hardware-tile", tile is not null);
        if (tile is not null) Click(tile);
        await Idle();
        Check("j2-tile-navigates-to-hardware", pages.SelectedIndex == 2);
        Check("j2-hardware-history", Count(shell, "HardwareSnapshotList") == 1);
        Check("j2-compare-needs-two-snapshots", !C<Button>(shell, "HardwareCompareButton").IsEnabled);
        Check("j2-other-sources-not-invented", Count(shell, "EventHistoryList") == 0 && Count(shell, "PerformanceHistoryList") == 0 &&
            Count(shell, "NetworkHistoryList") == 0 && Count(shell, "GraphicsHistoryList") == 0);
        nav.SelectedIndex = 0; await Idle(); Check("j2-back-to-dashboard", pages.SelectedIndex == 0);

        // Jornada 3 — investigação: correlação real sobre as evidências persistidas (sem eventos/desempenho/rede/GPU)
        foreach (var page in new[] { 3, 4, 5, 6 }) { await GoTo(shell, page); Check($"j3-page-{page}-reachable", pages.SelectedIndex == page); }
        await GoTo(shell, 7);
        Click(C<Button>(shell, "CorrelationRunButton"));
        await Wait(() => Count(shell, "CorrelationHistoryList") == 1 && C<Button>(shell, "CorrelationRunButton").IsEnabled, "correlação");
        var correlationText = AllText(C<TabControl>(shell, "CorrelationTabs")) + "\n" + Txt(C<TextBlock>(shell, "CorrelationStatusText"));
        C<ListBox>(shell, "CorrelationRelationList").SelectedIndex = 0; await Idle();
        var relation = Txt(C<TextBox>(shell, "CorrelationRelationDetail"));
        Info("correlação: " + Txt(C<TextBlock>(shell, "CorrelationStatusText")));
        Check("j3-correlation-persisted", Count(shell, "CorrelationHistoryList") == 1);
        Check("j3-status-declares-causality-not-assessed", Txt(C<TextBlock>(shell, "CorrelationStatusText")).Contains("Causalidade não avaliada", StringComparison.Ordinal));
        Check("j3-no-causality-claim", !(correlationText + relation).Contains("causa comprovada", StringComparison.OrdinalIgnoreCase) &&
            !(correlationText + relation).Contains("foi causado por", StringComparison.OrdinalIgnoreCase), relation.Length > 120 ? relation[..120] : relation);
        Check("j3-coverage-not-complete-when-sources-missing", !Txt(C<TextBox>(shell, "CorrelationCoverageDetail")).Contains("Cobertura completa", StringComparison.OrdinalIgnoreCase));
        await CloseShell(shell, "j2j3");

        // Jornada 4 — monitoramento sintético: consentimento, progresso, incidente, cancelamento, histórico e encaminhamento
        var monitoring = Path.Combine(work, "j4-monitoring");
        shell = Open(monitoring, syntheticMonitor: true); await Ready(shell);
        nav = C<ListBox>(shell, "NavList"); pages = C<TabControl>(shell, "Pages");
        await GoTo(shell, 9);
        SetCheck(C<CheckBox>(shell, "MonitoringEventsCheck"), false); SetCheck(C<CheckBox>(shell, "MonitoringNetworkCheck"), false);
        SetCheck(C<CheckBox>(shell, "MonitoringGraphicsCheck"), false); SetCheck(C<CheckBox>(shell, "MonitoringPerformanceCheck"), true);
        SetCheck(C<CheckBox>(shell, "MonitoringAuthorizationCheck"), false); await Idle();
        Check("j4-start-blocked-without-consent", !C<Button>(shell, "MonitoringStartButton").IsEnabled);
        SetCheck(C<CheckBox>(shell, "MonitoringAuthorizationCheck"), true); await Idle();
        Check("j4-start-enabled-after-consent", C<Button>(shell, "MonitoringStartButton").IsEnabled);
        Click(C<Button>(shell, "MonitoringStartButton"));
        await Wait(() => C<Button>(shell, "MonitoringStopButton").IsEnabled, "sessão em andamento");
        await Wait(() => Txt(C<TextBlock>(shell, "MonitoringSamplesValue")) is { Length: > 0 } s && s != "0" && s != "—", "amostras");
        Check("j4-progress-running", !C<Button>(shell, "MonitoringStartButton").IsEnabled && C<Button>(shell, "MonitoringStopButton").IsEnabled);
        C<TextBox>(shell, "MonitoringIncidentDescription").Text = "incidente artificial U3";
        Click(C<Button>(shell, "MonitoringIncidentButton"));
        await Wait(() => Txt(C<TextBlock>(shell, "MonitoringIncidentsValue")) == "1", "incidente");
        Check("j4-incident-recorded", true);
        Click(C<Button>(shell, "MonitoringStopButton"));
        await Wait(() => Count(shell, "MonitoringHistoryList") == 1 && C<CheckBox>(shell, "MonitoringAuthorizationCheck").IsEnabled
            && !C<Button>(shell, "MonitoringStopButton").IsEnabled, "cancelamento salvo");
        Check("j4-consent-reset-after-run", C<CheckBox>(shell, "MonitoringAuthorizationCheck").IsChecked != true);
        var saved = (await new MemoryStore(monitoring).LoadMonitoringSessionsAsync());
        var session = saved.Sessions.SingleOrDefault();
        Check("j4-persisted-cancelled-session", session is { Status: MonitoringExecutionStatus.Cancelled } && session.Incidents.Count == 1 && saved.Warnings.Count == 0,
            session is null ? "sem sessão" : $"{session.Status}/{session.CoverageStatus}; avisos={saved.Warnings.Count}");
        Info("monitoramento: " + Txt(C<TextBlock>(shell, "MonitoringStatusText")));
        C<ListBox>(shell, "MonitoringHistoryList").SelectedIndex = 0; await Idle();
        Check("j4-incident-listed", Count(shell, "MonitoringIncidentList") == 1);
        Capture(shell, "j4-monitoring");
        C<ListView>(shell, "MonitoringIncidentList").SelectedIndex = 0; await Idle();
        Check("j4-correlate-enabled-for-selected-incident", C<Button>(shell, "MonitoringCorrelateButton").IsEnabled);
        Click(C<Button>(shell, "MonitoringCorrelateButton"));
        try { await Wait(() => pages.SelectedIndex == 7, "encaminhamento para Correlação"); }
        catch (TimeoutException) { Info("encaminhamento: " + Txt(C<TextBlock>(shell, "MonitoringStatusText")) + " | " + AllText(C<Border>(shell, "WarningBanner"))); }
        Check("j4-forwards-to-correlation", pages.SelectedIndex == 7, $"página {pages.SelectedIndex}");
        await Wait(() => C<Button>(shell, "CorrelationRunButton").IsEnabled, "correlação direcionada concluída");
        Check("j4-targeted-correlation-persisted-and-causality-not-assessed", Count(shell, "CorrelationHistoryList") == 1 &&
            Txt(C<TextBlock>(shell, "MonitoringStatusText")).Contains("Causalidade não avaliada", StringComparison.Ordinal), Txt(C<TextBlock>(shell, "MonitoringStatusText")));
        // Fechamento durante a operação: a janela só fecha depois do checkpoint/registro seguro.
        await GoTo(shell, 9);
        SetCheck(C<CheckBox>(shell, "MonitoringAuthorizationCheck"), true); await Idle();
        Click(C<Button>(shell, "MonitoringStartButton"));
        try { await Wait(() => C<Button>(shell, "MonitoringStopButton").IsEnabled, "segunda sessão"); }
        catch (TimeoutException) { Check("j4-second-session-diagnostic", false, $"start={C<Button>(shell, "MonitoringStartButton").IsEnabled}; auth={C<CheckBox>(shell, "MonitoringAuthorizationCheck").IsChecked}; status={Txt(C<TextBlock>(shell, "MonitoringStatusText"))}; banner={AllText(C<Border>(shell, "WarningBanner"))}"); throw; }
        await Task.Delay(400);
        shell.Close();
        await Wait(() => !shell.IsLoaded, "fechamento durante a sessão");
        var afterClose = await new MemoryStore(monitoring).LoadMonitoringSessionsAsync();
        Check("j4-close-during-run-keeps-both-sessions", afterClose.Sessions.Count == 2 && afterClose.Sessions.All(item => item.Status != MonitoringExecutionStatus.Completed),
            string.Join(",", afterClose.Sessions.Select(item => item.Status)) + "; avisos=" + string.Join(" | ", afterClose.Warnings));

        // Jornada 5 — Personalização e Debloat: nada ultrapassa os gates
        var tweaksRoot = Path.Combine(work, "j5-tweaks-debloat");
        shell = Open(tweaksRoot); await Ready(shell);
        await GoTo(shell, 10);
        Check("j5-tweak-catalog-loaded", C<ItemsControl>(shell, "TweaksCardsHost").Items.Count > 0);
        Check("j5-real-execution-disabled-message", Txt(C<TextBlock>(shell, "TweaksModeText")).Length > 0, Txt(C<TextBlock>(shell, "TweaksModeText")));
        Check("j5-tweak-authorize-blocked", !C<Button>(shell, "TweakAuthorizeButton").IsEnabled && Count(shell, "TweakHistoryList") == 0);
        var debloatView = C<DebloatView>(shell, "DebloatSection");
        Check("j5-native-inventory-not-composed-in-fixture", !C<Button>(debloatView, "NativeRefreshButton").IsEnabled &&
            Txt(C<TextBlock>(debloatView, "NativeInventoryStatus")).Contains("indisponível nesta composição", StringComparison.Ordinal),
            Txt(C<TextBlock>(debloatView, "NativeInventoryStatus")));
        C<Expander>(debloatView, "SyntheticSection").IsExpanded = true;
        await Idle();
        var debloatText = AllText(debloatView);
        Check("j5-debloat-unverified-not-absent", debloatText.Contains("Não verificado", StringComparison.Ordinal) &&
            !debloatText.Contains("não instalado", StringComparison.OrdinalIgnoreCase));
        var grid = C<DataGrid>(debloatView, "InventoryGrid");
        if (grid.Items.Count > 0) { grid.SelectedIndex = 0; await Idle(); }
        C<TextBox>(debloatView, "ConfirmationText").Text = "CONFIRMAÇÃO ERRADA U3"; await Idle();
        Check("j5-debloat-execute-blocked-by-wrong-confirmation", !C<Button>(debloatView, "ExecuteButton").IsEnabled);
        Check("j5-debloat-history-empty-not-success", Count(debloatView, "HistoryList") == 0);
        await GoTo(shell, 12);
        Check("j5-recovery-shows-no-applied-change", Count(C<RecoveryCenterView>(shell, "RecoverySection"), "HistoryGrid") == 0);
        await CloseShell(shell, "j5");

        // Jornada 6 — reparos e recuperação (histórico completo, interrompido e indeterminado) + ciclo de vida
        var repairs = Path.Combine(work, "j6-repairs");
        await SeedRepairs(repairs);
        shell = Open(repairs); await Ready(shell);
        await GoTo(shell, 11);
        Check("j6-repair-history-three-records-plus-unfinished-intent", Count(shell, "RepairsHistoryList") == 4, $"{Count(shell, "RepairsHistoryList")} linhas");
        Check("j6-unfinished-intent-visible", Txt(C<TextBlock>(shell, "RepairsUnfinishedText")).Length > 0, Txt(C<TextBlock>(shell, "RepairsUnfinishedText")));
        await GoTo(shell, 12);
        recoveryView = C<RecoveryCenterView>(shell, "RecoverySection");
        var recoveryGrid = C<DataGrid>(recoveryView, "HistoryGrid");
        Check("j6-recovery-lists-four-operations", recoveryGrid.Items.Count == 4, $"{recoveryGrid.Items.Count}");
        var outcomes = recoveryGrid.Items.Cast<object>().Select(row => ((RecoveryOperation)row.GetType().GetProperty("Operation")!.GetValue(row)!).Outcome).ToArray();
        Check("j6-only-the-verified-run-is-completed", outcomes.Count(o => o == RecoveryOperationOutcome.Completed) == 1, string.Join(",", outcomes));
        for (var i = 0; i < recoveryGrid.Items.Count; i++)
        {
            recoveryGrid.SelectedIndex = i; await Idle();
            if (Txt(C<TextBox>(recoveryView, "DetailsText")).Length == 0 || C<Button>(recoveryView, "RecoverButton").IsEnabled)
                Check($"j6-row-{i}-details-and-no-rollback", false);
        }
        Check("j6-rows-have-details-and-no-rollback", true);
        Capture(shell, "j6-recovery");
        // Lock ocupado: indisponível, nunca zero; liberado: atualização restaura os registros.
        var refresh = C<Button>(recoveryView, "RefreshButton");
        var lockFile = Path.Combine(repairs, "data", "store.lock");
        using (var busy = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Click(refresh); await Idle(); await Wait(() => refresh.IsEnabled, "recarga com lock");
            Check("j6-busy-store-warns-and-shows-no-zero-success", C<Border>(recoveryView, "WarningsPanel").Visibility == Visibility.Visible,
                Txt(C<TextBlock>(recoveryView, "StatusText")));
        }
        Click(refresh); await Idle(); await Wait(() => refresh.IsEnabled && recoveryGrid.Items.Count == 4, "recarga após liberar o lock");
        Check("j6-refresh-after-lock-release", recoveryGrid.Items.Count == 4);
        // Dado corrompido: preservado, excluído, aviso permanece em recargas sucessivas.
        var corrupt = Path.Combine(repairs, "memory", "repairs", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(corrupt); File.WriteAllText(Path.Combine(corrupt, "intent.md"), "U3 CORRUPTED SYNTHETIC INTENT");
        for (var pass = 0; pass < 2; pass++)
        {
            Click(refresh); await Idle(); await Wait(() => refresh.IsEnabled, "recarga com dado corrompido");
            Check($"j6-corruption-warning-persists-{pass}", C<Border>(recoveryView, "WarningsPanel").Visibility == Visibility.Visible && recoveryGrid.Items.Count == 4 && File.Exists(Path.Combine(corrupt, "intent.md")));
        }
        // Aviso de integridade da origem Reparos permanece visível depois de recarga com intenção corrompida.
        await GoTo(shell, 11);
        Click(C<Button>(shell, "RepairsRefreshButton")); await Idle(); await Wait(() => C<Button>(shell, "RepairsRefreshButton").IsEnabled, "recarga de Reparos");
        var integrity = Txt(C<TextBlock>(shell, "StorageWarnings"));
        Check("j6-repairs-integrity-warning-visible", integrity.Length > 0, integrity.Length > 160 ? integrity[..160] : integrity);
        // Navegação rápida e consistente.
        nav = C<ListBox>(shell, "NavList"); pages = C<TabControl>(shell, "Pages");
        for (var round = 0; round < 3; round++)
            for (var i = 0; i < nav.Items.Count; i++) { nav.SelectedIndex = i; await Task.Yield(); }
        await Idle(); await GoTo(shell, 12);
        Check("j6-rapid-navigation-consistent", pages.SelectedIndex == nav.SelectedIndex && pages.SelectedIndex == 12);
        await CloseShell(shell, "j6");

        Check("five-gates", !RealTweakBuildGate.AllowsRealExecution([]) && !RealRepairBuildGate.AllowsRealExecution([]) &&
            !RealDebloatBuildGate.AllowsRealExecution() && !NativeDebloatInventoryGate.AllowsActivation() && !DebloatRemovalBuildGate.AllowsRealExecution());
    }

    private sealed class SyntheticMonitor : IMonitoringSource
    {
        public MonitoringSource Kind => MonitoringSource.Performance;
        public string CollectorId => "u3-synthetic-monitor";
        public string CollectorVersion => "1.0.0";
        public Task<IMonitoringSourceSession> StartAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IMonitoringSourceSession>(new Session());
        private sealed class Session : IMonitoringSourceSession
        {
            private int index;
            public async Task<MonitoringTick> ReadAsync(DateTimeOffset timestampUtc, CancellationToken cancellationToken)
            {
                await Task.Delay(20, cancellationToken);
                var sample = new MonitoringPerformanceObservation(index, timestampUtc, index++ * 0.1, 10, 4, 50,
                    16UL << 30, 8UL << 30, 8UL << 30, ["Amostra sintética U3; nenhum dado real foi observado."]);
                return new MonitoringTick(MonitoringSource.Performance, timestampUtc, false, sample, [], null, []);
            }
            public void Dispose() { }
        }
    }
}
