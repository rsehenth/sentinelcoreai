using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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

/// <summary>U1 isolated UI only: never starts App, diagnostic runners or Windows adapters.</summary>
internal sealed class U1Host
{
    private readonly List<string> checks = [];
    private bool failed;
    private readonly string repository, work, clean, damaged;
    private U1Host(string repository, string work, string clean, string damaged)
    { this.repository = repository; this.work = work; this.clean = clean; this.damaged = damaged; }

    [STAThread]
    public static int Run(string[] args)
    {
        var host = new U1Host(Path.GetFullPath(args[0]), FixturePaths.Create(args[1]),
            FixturePaths.Existing(args[2]), FixturePaths.Existing(args[3]));
        FixturePaths.Separate(host.work, host.clean); FixturePaths.Separate(host.work, host.damaged);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var xml = XDocument.Load(Path.Combine(host.repository, "src/SentinelCore.App/App.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        app.Resources = (ResourceDictionary)XamlReader.Parse(new XElement(p + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:SentinelCore.App;assembly=" + typeof(MainWindow).Assembly.GetName().Name),
            xml.Root!.Element(p + "Application.Resources")!.Nodes()).ToString());
        app.DispatcherUnhandledException += (_, e) => { host.Check("dispatcher", false, e.Exception.ToString()); e.Handled = true; };
        app.Startup += async (_, _) =>
        {
            try { await host.RunAll(); }
            catch (Exception error) { host.Check("host", false, error.ToString()); }
            File.WriteAllLines(Path.Combine(host.work, "u1-checks.txt"), host.checks);
            foreach (var line in host.checks) Console.WriteLine(line);
            app.Shutdown(host.failed ? 1 : 0);
        };
        return app.Run();
    }
    private void Check(string name, bool ok, string detail = "")
    { checks.Add($"{(ok ? "PASS" : "FAIL")} {name} {detail}"); failed |= !ok; }
    private static T C<T>(FrameworkElement element, string name) where T : FrameworkElement => (T)element.FindName(name)!;
    private static object? Invoke(object instance, string method, params object[] args) =>
        instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static async Task Idle() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    private static async Task Wait(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(15);
        if (!done()) throw new TimeoutException("Host U1: estado esperado não atingido.");
    }
    private void Capture(FrameworkElement view, string name)
    {
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(work, name + ".png")); png.Save(stream);
    }
    private static Window WindowFor(object content) => new()
    { Content = content, Width = 1120, Height = 800, Left = -12000, ShowInTaskbar = false,
        Background = (Brush)System.Windows.Application.Current.Resources["Bg"] };

    private async Task RunAll()
    {
        // All writes below belong to this new, marked temporary work tree.
        var shellRoot = Path.Combine(work, "shell-data");
        var store = new MemoryStore(shellRoot);
        await store.LoadAsync();
        var invalidRepair = Path.Combine(shellRoot, "memory", "repairs", Guid.NewGuid().ToString("D"));
        var invalidDebloat = Path.Combine(shellRoot, "memory", "debloat", Guid.NewGuid().ToString("D"), "journal");
        Directory.CreateDirectory(invalidRepair); Directory.CreateDirectory(invalidDebloat);
        File.WriteAllText(Path.Combine(invalidRepair, "intent.md"), "U1 CORRUPTED SYNTHETIC INTENT");
        File.WriteAllText(Path.Combine(invalidDebloat, "0001-Planned.md"), "U1 CORRUPTED SYNTHETIC JOURNAL");
        var locks = new FileExclusiveLockFactory(shellRoot);
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
            new SyntheticRepairExecutor(), store, () => @"C:\U1-FIXTURE"), volumes);
        var debloatCatalog = DebloatCatalog.CreateDefault();
        var inventory = SyntheticDebloatInventory.Create(debloatCatalog.All);
        var debloat = new DebloatManager(debloatCatalog, inventory, new SyntheticDebloatRuntimeFactory(inventory), store.CreateDebloatJournal(), locks);
        var startupReader = new RecordingReader(store);
        // Null runners are deliberate: collection actions are never invoked by this host.
        var shell = new MainWindow(null!, null!, null!, null!, null!, null!, null!, null!, tweaks, repairs,
            store, store, store, store, store, store, store, store, debloat, new RecoveryCenter(startupReader),
            error => Check("unexpected-shell-error", false, error.ToString()))
        { Left = -12000, ShowInTaskbar = false };
        var loadedEvents = 0;
        shell.Loaded += (_, _) => loadedEvents++;
        shell.Show();
        await Idle();
        await Wait(() => C<ListBox>(shell, "NavList").IsEnabled && C<Button>(shell, "RunButton").IsEnabled);
        Check("startup-history-complete", !failed, C<TextBlock>(shell, "ExecutionStatus").Text);
        Check("startup-recovery-deferred", startupReader.Calls == 0 && loadedEvents == 1,
            $"recovery reads={startupReader.Calls}; Window.Loaded={loadedEvents}");
        var nav = C<ListBox>(shell, "NavList"); var pages = C<TabControl>(shell, "Pages");
        var shortcut = typeof(MainWindow).GetMethod("NavigationShortcutIndex", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var key in new[] { Key.A, Key.B, Key.C, Key.D, Key.D0, Key.F1, Key.NumPad1 })
            Check($"shortcut-preserves-{key}", (int)shortcut.Invoke(null, [key])! == -1);
        Check("shortcut-numbers", (int)shortcut.Invoke(null, [Key.D1])! == 0 && (int)shortcut.Invoke(null, [Key.D9])! == 8);
        for (var i = 0; i < nav.Items.Count; i++)
        {
            nav.SelectedIndex = i; await Idle();
            if (i == 10)
            {
                await Wait(() => C<Button>(shell, "TweaksRefreshButton").IsEnabled);
                var debloatView = C<DebloatView>(shell, "DebloatSection");
                await Wait(() => !debloatView.IsBusy);
            }
            if (i == 11) await Wait(() => C<Button>(shell, "RepairsRefreshButton").IsEnabled);
            if (i == 12) await Wait(() => C<Button>(C<RecoveryCenterView>(shell, "RecoverySection"), "RefreshButton").IsEnabled);
            Check($"nav-{i}", pages.SelectedIndex == i &&
                ((TabItem)pages.SelectedItem).Header.ToString() == ((ListBoxItem)nav.Items[i]).Content.ToString());
        }
        await Wait(() => C<Button>(shell, "RepairsRefreshButton").IsEnabled && C<Button>(shell, "TweaksRefreshButton").IsEnabled);
        Check("event-path-injected", C<TextBlock>(shell, "EventStorageIdentityText").Text.Contains(shellRoot));
        var debloatSection = C<DebloatView>(shell, "DebloatSection");
        Check("debloat-partial-not-empty", C<TextBlock>(debloatSection, "HistorySummary").Text.Contains("incompleto") &&
            !string.IsNullOrEmpty(C<TextBlock>(debloatSection, "HistoryWarnings").Text));
        Check("repair-partial-not-empty", C<TextBlock>(shell, "RepairsUnfinishedText").Text.Contains("incompleto"));
        Check("monitoring-requires-consent", !C<Button>(shell, "MonitoringStartButton").IsEnabled);
        Check("real-tweak-disabled", !C<Button>(shell, "TweakAuthorizeButton").IsEnabled);
        // Raised SelectionChanged follows the same bubbling route as a nested combo/grid change.
        foreach (var (page, child, scroll, handler) in new[]
        {
            (10, "TweakSearchModeCombo", "TweaksScroll", "OnTweaksPageSelectionChanged"),
            (11, "RepairsScenarioCombo", "RepairsScroll", "OnRepairsPageSelectionChanged")
        })
        {
            nav.SelectedIndex = page; await Idle();
            var viewer = C<ScrollViewer>(shell, scroll); viewer.ScrollToVerticalOffset(250); await Idle();
            var before = viewer.VerticalOffset;
            Invoke(shell, handler, pages, new SelectionChangedEventArgs(Selector.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>())
                { Source = C<ComboBox>(shell, child) });
            await Idle();
            Check($"nested-selection-{page}", Math.Abs(viewer.VerticalOffset - before) < 1, $"offset {before:F0} -> {viewer.VerticalOffset:F0}");
        }
        shell.Width = 1120; shell.Height = 720; nav.SelectedIndex = 0; await Task.Delay(300); Capture(shell, "dashboard-min");
        nav.SelectedIndex = 10; await Task.Delay(300); Capture(shell, "tweaks-min");
        nav.SelectedIndex = 11; await Task.Delay(300); Capture(shell, "repairs-min");
        nav.SelectedIndex = 12; await Task.Delay(300); Capture(shell, "recovery-min");
        await Wait(() => C<Button>(C<RecoveryCenterView>(shell, "RecoverySection"), "RefreshButton").IsEnabled);
        shell.Close(); await Idle();

        var sources = await new MemoryStore(clean).ReadRecoveryHistoryAsync();
        var delayed = new DelayedReader(sources);
        var view = RecoveryView();
        var window = WindowFor(view); window.Show(); view.Initialize(new RecoveryCenter(delayed));
        await Wait(() => delayed.Calls == 1);
        window.Content = null; await Idle(); window.Content = view; await Idle();
        delayed.Release.TrySetResult();
        await Task.Delay(500); await Idle();
        Check("rapid-return-reloads", delayed.Calls == 2 && C<DataGrid>(view, "HistoryGrid").Items.Count > 0,
            $"reads={delayed.Calls}; {C<TextBlock>(view, "StatusText").Text}");
        window.Close();

        var cancelled = new CancelledReader();
        view = RecoveryView(); window = WindowFor(view); window.Show(); view.Initialize(new RecoveryCenter(cancelled));
        await Task.Delay(150); await Idle();
        Check("unexpected-cancellation-not-retried", cancelled.Calls == 1 && C<Button>(view, "RefreshButton").IsEnabled,
            $"reads={cancelled.Calls}");
        window.Close();

        foreach (var fixture in new[] { clean, damaged })
        {
            view = RecoveryView(); window = WindowFor(view); window.Show();
            view.Initialize(new RecoveryCenter(new MemoryStore(fixture)));
            await Wait(() => C<Button>(view, "RefreshButton").IsEnabled);
            var grid = C<DataGrid>(view, "HistoryGrid");
            Check(fixture == clean ? "clean-load" : "damaged-partial", fixture == clean
                ? grid.Items.Count > 0 : C<Border>(view, "WarningsPanel").Visibility == Visibility.Visible);
            if (fixture == clean && grid.Items.Count > 1)
            {
                var type = grid.Items[0].GetType();
                var operation = (RecoveryOperation)type.GetProperty("Operation")!.GetValue(grid.Items[0])!;
                object Row(DateTimeOffset date) => Activator.CreateInstance(type, operation with { Id = Guid.NewGuid(), AtUtc = date })!;
                var early = Row(new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero));
                var late = Row(new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero));
                grid.ItemsSource = new[] { late, early };
                grid.Items.SortDescriptions.Add(new SortDescription(grid.Columns[0].SortMemberPath, ListSortDirection.Ascending));
                Check("date-chronological", ReferenceEquals(grid.Items[0], early), grid.Columns[0].SortMemberPath);
                await view.RefreshAsync();
                C<TextBox>(view, "SearchText").Text = "u1-no-match"; await Task.Delay(350);
                Check("search-empty-filter", grid.Items.Count == 0 && C<TextBlock>(view, "HistorySummary").Text.Contains("filtros"));
                C<Button>(view, "ClearFiltersButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("clear-filter", grid.Items.Count > 0);
                grid.SelectedIndex = 0;
                Check("history-detail", C<TextBox>(view, "DetailsText").Text.Length > 0 && !C<Button>(view, "RecoverButton").IsEnabled);
            }
            Capture(view, fixture == clean ? "recovery-clean" : "recovery-partial");
            C<DataGrid>(view, "HistoryGrid").BringIntoView(); await Idle();
            Capture(view, fixture == clean ? "recovery-clean-history" : "recovery-partial-history");
            window.Close();
        }
        Check("five-gates", !RealTweakBuildGate.AllowsRealExecution([]) && !RealRepairBuildGate.AllowsRealExecution([]) &&
            !RealDebloatBuildGate.AllowsRealExecution() && !NativeDebloatInventoryGate.AllowsActivation() && !DebloatRemovalBuildGate.AllowsRealExecution());
    }
    private static RecoveryCenterView RecoveryView() => new()
    { Background = (Brush)System.Windows.Application.Current.Resources["Bg"] };
    private sealed class RecordingReader(MemoryStore store) : IRecoveryHistoryReader
    {
        public int Calls;
        public Task<IReadOnlyList<RecoveryHistorySource>> ReadRecoveryHistoryAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return store.ReadRecoveryHistoryAsync(cancellationToken); }
        public string GetRecoveryReportPath(RecoveryModule module, Guid id) => store.GetRecoveryReportPath(module, id);
    }
    private sealed class CancelledReader : IRecoveryHistoryReader
    {
        public int Calls;
        public Task<IReadOnlyList<RecoveryHistorySource>> ReadRecoveryHistoryAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); throw new OperationCanceledException("U1 fixture: provider cancellation without caller request"); }
        public string GetRecoveryReportPath(RecoveryModule module, Guid id) => "U1-FIXTURE.md";
    }
    private sealed class DelayedReader(IReadOnlyList<RecoveryHistorySource> sources) : IRecoveryHistoryReader
    {
        public int Calls;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<RecoveryHistorySource>> ReadRecoveryHistoryAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref Calls) == 1) await Release.Task;
            cancellationToken.ThrowIfCancellationRequested(); return sources;
        }
        public string GetRecoveryReportPath(RecoveryModule module, Guid id) => "U1-FIXTURE.md";
    }
}
