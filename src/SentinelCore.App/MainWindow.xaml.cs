using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows;
using SentinelCore.Persistence;
using SentinelCore.App.Localization;

namespace SentinelCore.App;

public partial class MainWindow : Window
{
    private readonly AuditRunner runner;
    private readonly HardwareRunner hardwareRunner;
    private readonly EventInvestigationRunner eventRunner;
    private readonly PerformanceSessionRunner performanceRunner;
    private readonly NetworkSessionRunner networkRunner;
    private readonly GraphicsInvestigationRunner graphicsRunner;
    private readonly EvidenceCorrelationRunner correlationRunner;
    private readonly MonitoringSessionRunner monitoringRunner;
    private readonly IAuditStore store;
    private readonly IHardwareSnapshotStore hardwareStore;
    private readonly IEventInvestigationStore eventStore;
    private readonly IPerformanceSessionStore performanceStore;
    private readonly INetworkSessionStore networkStore;
    private readonly IGraphicsInvestigationStore graphicsStore;
    private readonly ICorrelationInvestigationStore correlationStore;
    private readonly IMonitoringSessionStore monitoringStore;
    private readonly Action<Exception> logFailure;
    private readonly RecoveryCenter? recoveryCenter;
    private readonly DebloatManager? debloatManager;
    private readonly DebloatInventoryObserver? debloatObserver;
    private bool recoveryComposed;
    private bool debloatComposed;
    private readonly FreshnessEngine freshness = new();
    private readonly DispatcherTimer freshnessTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private CancellationTokenSource? cancellation;
    private IReadOnlyList<AuditRecord> history = [];
    private AuditRecord? reportAudit;
    private AuditRecord? unsaved;
    private bool busy;
    private bool closingAfterSave;
    private bool debloatClosingAfterSave;

    private void OnOpenDebloat(object sender, RoutedEventArgs e) => DebloatSection.BringIntoView();
    private IReadOnlyList<HardwareSnapshot> hardwareSnapshots = [];
    private HardwareSnapshot? selectedHardware;
    private HardwareSnapshot? unsavedHardware;
    private CancellationTokenSource? hardwareCancellation;
    private bool hardwareBusy;
    private bool hardwareSaving;
    private bool hardwareClosingAfterSave;
    private bool updatingHardware;
    private HardwareCategory? hardwareCategory;

    public MainWindow(AuditRunner runner, HardwareRunner hardwareRunner, EventInvestigationRunner eventRunner,
        PerformanceSessionRunner performanceRunner, NetworkSessionRunner networkRunner,
        GraphicsInvestigationRunner graphicsRunner, EvidenceCorrelationRunner correlationRunner,
        MonitoringSessionRunner monitoringRunner, TweakManager tweaks, RepairManager repairs,
        IAuditStore store, IHardwareSnapshotStore hardwareStore,
        IEventInvestigationStore eventStore, IPerformanceSessionStore performanceStore,
        INetworkSessionStore networkStore, IGraphicsInvestigationStore graphicsStore,
        ICorrelationInvestigationStore correlationStore, IMonitoringSessionStore monitoringStore,
        DebloatManager? debloat = null, RecoveryCenter? recovery = null, Action<Exception>? failureLogger = null,
        DebloatInventoryObserver? debloatObserver = null, ProductOperationCoordinator? operations = null)
    {
        logFailure = failureLogger ?? App.LogFailure;
        this.operations = operations ?? new ProductOperationCoordinator();
        InitializeComponent();
        LanguageRow.SelectedIndex = Localizer.Current == AppLanguage.Portuguese ? 1 : 0;
        this.runner = runner;
        FullAuditOption.IsEnabled = runner.SupportsFull;
        this.hardwareRunner = hardwareRunner;
        this.eventRunner = eventRunner;
        this.performanceRunner = performanceRunner;
        this.networkRunner = networkRunner;
        this.graphicsRunner = graphicsRunner;
        this.correlationRunner = correlationRunner;
        this.monitoringRunner = monitoringRunner;
        this.tweaks = tweaks;
        this.repairs = repairs;
        this.store = store;
        this.hardwareStore = hardwareStore;
        this.eventStore = eventStore;
        this.performanceStore = performanceStore;
        this.networkStore = networkStore;
        this.graphicsStore = graphicsStore;
        this.correlationStore = correlationStore;
        this.monitoringStore = monitoringStore;
        VersionText.Text = Loc.F($"v{SoftwareInfo.Version}  •  Local e offline");
        InitializeEvents();
        InitializePerformance();
        InitializeNetwork();
        InitializeGraphics();
        InitializeCorrelation();
        InitializeMonitoring();
        InitializeTweaks();
        InitializeRepairs();
        recoveryCenter = recovery;
        debloatManager = debloat;
        this.debloatObserver = debloatObserver;
        DebloatSection.BeginOperation = RecoverySection.BeginOperation = TryBeginProductOperation;
        DebloatSection.HistoryChanged += InvalidateMaintenanceHistory;
        RecoverySection.BecameIdle += () =>
        {
            if (!recoveryClosingAfterSave) return;
            recoveryClosingAfterSave = false;
            Dispatcher.BeginInvoke(new Action(Close));
        };
        this.operations.Changed += OnProductOperationChanged;
        if (recovery is not null) DebloatSection.OpenRecoveryRequested += () => Pages.SelectedItem = RecoveryTab;
        Pages.SelectionChanged += OnRecoveryPageSelectionChanged;
        RecoverySection.AuditRequested += OpenInterruptedAudit;
        RecoverySection.TweakRecoveryAttempted += InvalidateMaintenanceHistory;
        if (tweaks is null && debloat is not null) Pages.SelectionChanged += OnTweaksPageSelectionChanged;
        DebloatSection.BecameIdle += (_, _) =>
        {
            if (!debloatClosingAfterSave) return;
            debloatClosingAfterSave = false;
            Dispatcher.BeginInvoke(new Action(Close));
        };
        SetHardwareCategories(null);
        UpdateHardwareButtons();
        // 1320×860 DIP não cabe na área útil de 1080p a 175–200%; o mínimo declarado (960×520) continua valendo.
        Width = Math.Min(Width, SystemParameters.WorkArea.Width); Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        SizeChanged += OnShellSizeChanged;
        freshnessTimer.Tick += (_, _) => UpdateSummary();
        freshnessTimer.Start();
        Closed += (_, _) =>
        {
            freshnessTimer.Stop();
            this.operations.Changed -= OnProductOperationChanged;
        };
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Pages is null || !ReferenceEquals(e.OriginalSource, NavList) || !SystemParameters.ClientAreaAnimation)
            return;

        Pages.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void OnRecoveryPageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Pages) || !ReferenceEquals(Pages.SelectedItem, RecoveryTab) ||
            recoveryComposed || recoveryCenter is not { } recovery) return;
        recoveryComposed = true;
        RecoverySection.Initialize(recovery);
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var index) &&
            index >= 0 && index < NavList.Items.Count && NavList.Items[index] is ListBoxItem { IsEnabled: true })
        {
            NavList.SelectedIndex = index;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (!NavList.IsEnabled || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;

        if (e.Key == Key.Tab)
        {
            e.Handled = true;
            var activeTabs = PerformanceTabs.IsKeyboardFocusWithin
                ? PerformanceTabs
                : NetworkTabs.IsKeyboardFocusWithin
                    ? NetworkTabs
                    : GraphicsTabs.IsKeyboardFocusWithin
                        ? GraphicsTabs
                        : CorrelationTabs.IsKeyboardFocusWithin
                            ? CorrelationTabs
                            : MonitoringTabs.IsKeyboardFocusWithin
                                ? MonitoringTabs
                                : null;
            if (activeTabs is not null && activeTabs.Items.Count > 0)
            {
                var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
                var count = activeTabs.Items.Count;
                activeTabs.SelectedIndex = (activeTabs.SelectedIndex + step + count) % count;
            }
            return;
        }

        if (e.Key == Key.B)
        {
            OnToggleSidebar(sender, e);
            e.Handled = true;
            return;
        }

        var index = NavigationShortcutIndex(e.Key);
        if (index >= 0 && index < NavList.Items.Count)
        {
            NavList.SelectedIndex = index;
            e.Handled = true;
        }
    }

    private static int NavigationShortcutIndex(Key key) =>
        key is >= Key.D1 and <= Key.D9 ? key - Key.D1 : -1;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        using var operation = TryBeginProductOperation();
        if (operation is null) return;
        NavList.IsEnabled = false;
        Pages.IsEnabled = false;
        RunButton.IsEnabled = false;
        var previousStatus = ExecutionStatus.Text;
        ExecutionStatus.Text = Loc.T("Carregando históricos locais…");
        UpdateHardwareButtons();
        try
        {
            await ReloadHistoriesAsync();
        }
        finally
        {
            NavList.IsEnabled = true;
            Pages.IsEnabled = true;
            RunButton.IsEnabled = true;
            if (ExecutionStatus.Text == Loc.T("Carregando históricos locais…")) ExecutionStatus.Text = previousStatus;
            UpdateHardwareButtons();
        }
    }

    // A troca de idioma reaplica os textos montados pelo code-behind repetindo as leituras locais.
    private async Task ReloadHistoriesAsync()
    {
        try { await ReloadAsync(); }
        catch (Exception error) { ShowFailure(Loc.T("Não foi possível recuperar a memória local"), error); }

        try { await ReloadHardwareAsync(); }
        catch (Exception error) { ShowHardwareFailure(Loc.T("Não foi possível recuperar os inventários de hardware"), error); }

        try { await ReloadEventsAsync(); }
        catch (Exception error) { ShowEventFailure(Loc.T("Não foi possível recuperar as investigações de eventos"), error); }

        try { await ReloadPerformanceAsync(); }
        catch (Exception error) { ShowPerformanceFailure(Loc.T("Não foi possível recuperar as sessões de desempenho"), error); }

        try { await ReloadNetworksAsync(); }
        catch (Exception error) { ShowNetworkFailure(Loc.T("Não foi possível recuperar as sessões de rede"), error); }

        try { await ReloadGraphicsAsync(); }
        catch (Exception error) { ShowGraphicsFailure(Loc.T("Não foi possível recuperar as investigações gráficas"), error); }

        try { await ReloadCorrelationAsync(); }
        catch (Exception error) { ShowCorrelationFailure(Loc.T("Não foi possível recuperar as investigações de correlação"), error); }

        try { await ReloadMonitoringAsync(); }
        catch (Exception error) { ShowMonitoringFailure(Loc.T("Não foi possível recuperar as sessões de monitoramento"), error); }

        // Só o histórico do store; o preflight (volumes, System32) ocorre ao abrir a página Reparos ou em Atualizar.
        try { await ReloadRepairHistoryAsync(); }
        catch (Exception error) { ShowFailure(Loc.T("Não foi possível recuperar o histórico de reparos"), error); }
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || LanguageRow.SelectedIndex < 0) return;
        var language = LanguageRow.SelectedIndex == 1 ? AppLanguage.Portuguese : AppLanguage.English;
        if (language == Localizer.Current) return;
        Localizer.Change(language);
        _ = ReloadTextsAsync();
    }

    private async Task ReloadTextsAsync()
    {
        using var operation = TryBeginProductOperation();
        if (operation is null) return;
        await ReloadHistoriesAsync();
    }

    private async Task ReloadAsync(Guid? selectId = null, bool readOnly = false)
    {
        var snapshot = readOnly && store is IAuditHistoryReader reader
            ? await reader.ReadAuditHistoryAsync() : await store.LoadAsync();
        history = snapshot.Audits;
        HistoryList.ItemsSource = history.Select(audit => new HistoryRow(audit)).ToArray();
        SetIntegrity("Auditorias", snapshot.Warnings);
        UpdateSummary();
        if (history.FirstOrDefault() is { } latest)
        {
            var target = history.FirstOrDefault(audit => audit.Id == selectId) ?? latest;
            DisplayLatest(target);
            SelectAuditHistory(target.Id);
            ExecutionStatus.Text = Loc.F($"{history.Count} auditoria(s) recuperada(s) da memória local. Última tentativa: {Status(latest.Status)}.");
        }
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        using var operation = TryBeginProductOperation();
        if (operation is null) return;
        if (busy || hardwareBusy || eventBusy || performanceBusy || networkBusy || graphicsBusy || correlationBusy || monitoringBusy)
            return;
        if (tweaksBusy || repairsBusy || DebloatSection.IsBusy || RecoverySection.IsBusy)
        {
            ExecutionStatus.Text = Loc.T("Aguarde a operação em andamento antes de iniciar a auditoria.");
            return;
        }

        var kind = ReferenceEquals(AuditKindCombo.SelectedItem, FullAuditOption) ? AuditKind.Full : AuditKind.Basic;
        if (unsaved is null)
        {
            var scope = kind == AuditKind.Full
                ? "A auditoria completa inclui inventário, eventos e WER das últimas 24 h, 30 s de desempenho, " +
                  "rede sem endereços, gráficos e uma sessão de 5 min de monitoramento local de eventos, CPU/memória, rede e gráficos. " +
                  "Ao confirmar, você autoriza essa sessão. O monitoramento termina ao final da sessão ou ao cancelar."
                : "A auditoria básica lê informações do Windows, CPU, memória, placa-mãe, gráficos, discos e volumes.";
            if (MessageBox.Show(this, scope + Loc.T("\n\nIniciar a coleta e salvar os resultados neste computador?"),
                    AuditName(kind), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
        }

        busy = true;
        SetAuditControls(false);
        ReportButton.IsEnabled = false;
        ClearRunWarnings();
        var collectionFinished = false;
        var saved = false;
        try
        {
            if (unsaved is null)
            {
                var collection = new CancellationTokenSource();
                cancellation = collection;
                CancelButton.IsEnabled = CancelAuditResultsButton.IsEnabled = true;
                AuditProgressBar.Value = 0;
                AuditProgressBar.IsIndeterminate = true;
                var rows = new ObservableCollection<CollectorRow>();
                CollectorList.ItemsSource = rows;
                CollectorDetails.Text = Loc.T("Os resultados aparecem à medida que as etapas são processadas.");
                AuditEvidenceActions.Visibility = Visibility.Collapsed;
                ResultHeading.Text = Loc.F($"{AuditName(kind)} · Em andamento");
                ExecutionStatus.Text = LastCheck.Text = Loc.T("Preparando a auditoria…");
                NavList.SelectedIndex = Pages.Items.IndexOf(AuditResultsTab);

                var progress = new Progress<AuditProgress>(update =>
                {
                    if (collectionFinished || !ReferenceEquals(cancellation, collection)) return;
                    AuditProgressBar.IsIndeterminate = false;
                    AuditProgressBar.Maximum = update.Total;
                    AuditProgressBar.Value = update.Finished;
                    if (update.IsSaving)
                    {
                        collectionFinished = true;
                        CancelButton.IsEnabled = CancelAuditResultsButton.IsEnabled = false;
                        ExecutionStatus.Text = LastCheck.Text = Loc.T("Salvando a auditoria na memória local…");
                        return;
                    }
                    if (update.Result is { } result)
                    {
                        rows.Add(new CollectorRow(result));
                        if (CollectorList.SelectedIndex < 0) CollectorList.SelectedIndex = 0;
                    }
                    var activity = update.Activity is { } label ? $" · {CollectorName(label)}" : string.Empty;
                    var message = $"{CollectorName(update.CollectorId)}{activity} · {Status(update.Status)} · " +
                        $"{update.Finished}/{update.Total} etapas processadas";
                    if (!collection.IsCancellationRequested) ExecutionStatus.Text = LastCheck.Text = message;
                });
                var token = collection.Token;
                unsaved = await Task.Run(() => runner.RunAsync(kind, monitoringAuthorized: kind == AuditKind.Full,
                    progress: progress, cancellationToken: token));
                saved = runner.PersistsAudits;
            }

            collectionFinished = true;
            var audit = unsaved;
            DisplayLatest(audit);
            CancelButton.IsEnabled = CancelAuditResultsButton.IsEnabled = false;
            AuditProgressBar.IsIndeterminate = false;
            AuditProgressBar.Maximum = audit.PlannedSteps ?? Math.Max(1, audit.Results.Count);
            AuditProgressBar.Value = audit.Results.Count;
            ExecutionStatus.Text = saved ? Loc.T("Atualizando históricos locais…") : Loc.T("Salvando resultado e atualizando memória…");
            if (!saved) await store.SaveAsync(audit);
            unsaved = null;
            saved = true;
            await ReloadAsync(audit.Id);
            await ReloadAuditEvidenceAsync(audit);
            ExecutionStatus.Text = Loc.F($"{AuditName(audit.Kind)} · {Status(audit.Status)}. Histórico e relatório salvos localmente.");
        }
        catch (Exception error)
        {
            collectionFinished = true;
            if (error is AuditPersistenceException failure)
            {
                unsaved = failure.Audit;
                DisplayLatest(failure.Audit);
            }
            else if (!saved && unsaved is null)
            {
                ResultHeading.Text = Loc.F($"{AuditName(kind)} · Falhou");
                LastCheck.Text = Loc.T("A execução não pôde ser confirmada. Consulte o aviso da auditoria.");
            }
            ShowFailure(unsaved is not null
                ? Loc.T("Falha de gravação. O resultado permanece nesta janela; tente salvar novamente")
                : saved ? Loc.T("Auditoria salva, mas a apresentação do histórico não foi atualizada")
                : Loc.T("Não foi possível iniciar ou concluir a auditoria"), error);
        }
        finally
        {
            busy = false;
            RecoverySection.InvalidateAuditHistory();
            cancellation?.Dispose();
            cancellation = null;
            RunButton.Content = unsaved is not null ? Loc.T("Tentar salvar auditoria") : Loc.T("Iniciar auditoria");
            AuditProgressBar.IsIndeterminate = false;
            CancelButton.IsEnabled = CancelAuditResultsButton.IsEnabled = false;
            ReportButton.IsEnabled = unsaved is null && reportAudit is not null &&
                File.Exists(store.GetReportPath(reportAudit.Id));
            SetAuditControls(true);
            if (closingAfterSave && unsaved is null)
            {
                closingAfterSave = false;
                Close();
            }
            else
            {
                closingAfterSave = false;
            }
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel();
        CancelButton.IsEnabled = CancelAuditResultsButton.IsEnabled = false;
        ExecutionStatus.Text = LastCheck.Text = Loc.T("Cancelamento solicitado. Aguardando a consulta em andamento…");
    }

    private void SetAuditControls(bool available)
    {
        TweaksNav.IsEnabled = RepairsNav.IsEnabled = RecoveryNav.IsEnabled = available;
        AuditKindCombo.IsEnabled = available && unsaved is null;
        OpenAuditResultsButton.IsEnabled = available && HistoryList.SelectedItem is HistoryRow;
        UpdateAllBusyButtons();
    }

    private async Task ReloadAuditEvidenceAsync(AuditRecord audit)
    {
        var unavailable = new List<string>();
        foreach (var result in audit.Results)
        {
            if (result.Evidence is not { } evidence) continue;
            try
            {
                switch (evidence.Source)
                {
                    case CorrelationSource.Hardware: await ReloadHardwareAsync(evidence.RecordId); break;
                    case CorrelationSource.Events: await ReloadEventsAsync(evidence.RecordId); break;
                    case CorrelationSource.Performance: await ReloadPerformanceAsync(evidence.RecordId); break;
                    case CorrelationSource.Network: await ReloadNetworksAsync(evidence.RecordId); break;
                    case CorrelationSource.Graphics: await ReloadGraphicsAsync(evidence.RecordId); break;
                    case CorrelationSource.Monitoring: await ReloadMonitoringAsync(evidence.RecordId); break;
                }
            }
            catch (Exception error)
            {
                logFailure(error);
                unavailable.Add(CorrelationGuidance.SourceName(evidence.Source));
            }
        }
        if (unavailable.Count > 0)
            ShowWarning(Loc.F($"Históricos de módulos não puderam ser atualizados: {string.Join(", ", unavailable)}."));
    }

    private void DisplayLatest(AuditRecord audit)
    {
        reportAudit = audit;
        ResultHeading.Text = Loc.F($"{AuditName(audit.Kind)} · {Status(audit.Status)}");
        LastCheck.Text = Loc.F($"{Local(audit.StartedUtc)} a {Local(audit.CompletedUtc)} · {audit.Duration.TotalSeconds:F1} s · ID {audit.Id:D}\n") +
            AuditSummary(audit);
        CollectorList.ItemsSource = audit.Results.Select(r => new CollectorRow(r)).ToArray();
        CollectorList.SelectedIndex = audit.Results.Count > 0 ? 0 : -1;
        ReportButton.IsEnabled = !busy && File.Exists(store.GetReportPath(audit.Id));
    }

    private void UpdateSummary()
    {
        var basic = freshness.Evaluate(history, AuditKind.Basic);
        var full = freshness.Evaluate(history, AuditKind.Full);
        BasicState.Text = FreshnessText(basic.State);
        FullState.Text = FreshnessText(full.State);
        BasicDate.Text = Loc.T("Último sucesso: ") + (basic.LastSuccessUtc is { } last ? Local(last) : Loc.T("nunca"));
        if (basic.ExpiresUtc is { } expires) BasicDate.Text += "\nValidade: " + Local(expires);
        FullDate.Text = Loc.T("Último sucesso: ") + (full.LastSuccessUtc is { } fullLast ? Local(fullLast) : Loc.T("nunca"));
        if (full.ExpiresUtc is { } fullExpires) FullDate.Text += "\nValidade: " + Local(fullExpires);
        if (full.LatestAttemptStatus is { } fullAttempt && fullAttempt != DiagnosticStatus.Completed)
            FullDate.Text += "\nÚltima tentativa: " + Status(fullAttempt);
        if (basic.LatestAttemptStatus is { } attempt && attempt != DiagnosticStatus.Completed)
            BasicDate.Text += "\nÚltima tentativa: " + Status(attempt);

        BasicState.Foreground = (Brush)FindResource(basic.State is FreshnessState.Current ? "Accent" : "Warn");
        FullState.Foreground = (Brush)FindResource(full.State is FreshnessState.Current ? "Accent" : "Warn");
        var latestWithData = history.FirstOrDefault(a => a.Results.Any(r => r.Data is not null));
        var os = latestWithData?.Results.Select(r => r.Data?.OperatingSystem).FirstOrDefault(x => x is not null);
        var ram = latestWithData?.Results.Select(r => r.Data?.Memory).FirstOrDefault(x => x is not null);
        var cpu = latestWithData?.Results.SelectMany(r => r.Data?.Processors ?? []).FirstOrDefault();
        ComputerSummary.Text = os?.Name ?? Loc.T("Windows · nome indisponível");
        ComputerDetail.Text = Loc.F($"{cpu?.Model ?? "CPU indisponível"}\nRAM instalada: {Units.GiB(ram?.InstalledBytes)}");
    }

    private void OnCollectorSelected(object sender, SelectionChangedEventArgs e)
    {
        var result = (CollectorList.SelectedItem as CollectorRow)?.Result;
        CollectorDetails.Text = result is null ? Loc.T("Nenhum resultado selecionado.") : CollectorText(result);
        AuditEvidenceActions.Visibility = result?.Evidence is null ? Visibility.Collapsed : Visibility.Visible;
        AuditEvidenceStatus.Text = string.Empty;
    }

    private void OnHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        OpenAuditResultsButton.IsEnabled = !busy && HistoryList.SelectedItem is HistoryRow;
        if (HistoryList.SelectedItem is not HistoryRow row) return;
        reportAudit = row.Audit;
        HistoryDetails.Text = Loc.F($"ID {row.Audit.Id:D}\n{Local(row.Audit.StartedUtc)} a {Local(row.Audit.CompletedUtc)}\n") +
            Loc.F($"{AuditName(row.Audit.Kind)} · {Status(row.Audit.Status)} · versão {row.Audit.SoftwareVersion}\n{AuditSummary(row.Audit)}\n\n") +
            string.Join(Loc.T("\n\n"), row.Audit.Results.Select(CollectorText));
        ReportButton.IsEnabled = !busy && File.Exists(store.GetReportPath(row.Audit.Id));
    }

    private void SelectAuditHistory(Guid id)
    {
        var row = HistoryList.Items.OfType<HistoryRow>().FirstOrDefault(item => item.Audit.Id == id);
        HistoryList.SelectedItem = row;
        if (row is not null) HistoryList.ScrollIntoView(row);
    }

    private async void OpenInterruptedAudit(Guid id)
    {
        if (busy) return;
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        try { await ReloadAsync(id, readOnly: true); }
        catch (Exception error)
        {
            ShowFailure(Loc.T("Não foi possível atualizar a origem da auditoria"), error);
            return;
        }
        if (!history.Any(audit => audit.Id == id))
        {
            ShowWarning(Loc.T("Auditoria indisponível no histórico carregado."));
            return;
        }
        NavList.SelectedIndex = Pages.Items.IndexOf(HistoryTab);
        SelectAuditHistory(id);
    }

    private void OnOpenAuditResults(object sender, RoutedEventArgs e)
    {
        if (busy || HistoryList.SelectedItem is not HistoryRow row) return;
        DisplayLatest(row.Audit);
        NavList.SelectedIndex = Pages.Items.IndexOf(AuditResultsTab);
    }

    private async void OnOpenAuditEvidence(object sender, RoutedEventArgs e)
    {
        if (CollectorList.SelectedItem is not CollectorRow { Result.Evidence: { } evidence }) return;
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        AuditEvidenceStatus.Text = await OpenDiagnosticRecordAsync(evidence.Source, evidence.RecordId) ?? string.Empty;
    }

    private async void OnOpenReport(object sender, RoutedEventArgs e)
    {
        if (reportAudit is null) return;
        try
        {
            ReportButton.IsEnabled = false;
            await WindowsShell.OpenReportLocationAsync(store.GetReportPath(reportAudit.Id));
        }
        catch (Exception error)
        {
            ShowFailure(Loc.T("Não foi possível abrir a localização do relatório"), error);
        }
        finally
        {
            ReportButton.IsEnabled = !busy && reportAudit is not null && File.Exists(store.GetReportPath(reportAudit.Id));
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (RecoverySection.IsBusy)
        {
            e.Cancel = true;
            recoveryClosingAfterSave = true;
            RecoverySection.CancelRead();
            return;
        }
        if (DebloatSection.IsBusy)
        {
            e.Cancel = true;
            debloatClosingAfterSave = true;
            DebloatSection.Cancel();
            return;
        }
        if (tweaksBusy)
        {
            e.Cancel = true;
            operationClosingAfterSave = true;
            TweakStatusText.Text = Loc.T("Encerrando: aguarde a operação de tweak ou recuperação e o journal durável.");
            return;
        }

        if (repairsBusy)
        {
            e.Cancel = true;
            operationClosingAfterSave = true;
            repairClosingAfterSave = true;
            repairCancellation?.Cancel();
            RepairsStatusText.Text = repairCommitted
                ? Loc.T("Encerrando após o resultado do broker e a gravação do registro de reparo.")
                : Loc.T("Encerrando após a gravação do registro de reparo. A interrupção real de uma ferramenta oficial não é garantida.");
            return;
        }

        if (busy || hardwareBusy || eventBusy || performanceBusy || networkBusy || graphicsBusy || correlationBusy || monitoringBusy)
        {
            e.Cancel = true;
            if (busy)
            {
                closingAfterSave = true;
                cancellation?.Cancel();
                ExecutionStatus.Text = Loc.T("Encerrando: aguarde o cancelamento e a gravação segura do histórico.");
            }

            if (hardwareBusy)
            {
                hardwareClosingAfterSave = true;
                hardwareCancellation?.Cancel();
                HardwareStatusText.Text = Loc.T("Encerrando: aguarde o cancelamento e a gravação segura do inventário.");
            }

            if (eventBusy)
            {
                eventClosingAfterSave = true;
                eventCancellation?.Cancel();
                EventStatusText.Text = Loc.T("Encerrando: aguarde o cancelamento e a gravação segura da investigação.");
            }

            if (performanceBusy)
            {
                performanceClosingAfterSave = true;
                performanceCancellation?.Cancel();
                PerformanceStatusText.Text = Loc.T("Encerrando: aguarde o cancelamento e a gravação segura da sessão de desempenho.");
            }

            if (networkBusy)
            {
                networkClosingAfterSave = true;
                networkCancellation?.Cancel();
                NetworkStatusText.Text = Loc.T("Encerrando: aguarde o cancelamento e a gravação segura da sessão de rede.");
            }

            if (graphicsBusy)
            {
                graphicsClosingAfterSave = true;
                graphicsCancellation?.Cancel();
                GraphicsStatusText.Text = Loc.T("Encerrando: aguarde o cancelamento e a gravação segura da investigação gráfica.");
            }

            if (correlationBusy)
            {
                correlationClosingAfterSave = true;
                correlationCancellation?.Cancel();
                CorrelationStatusText.Text = Loc.T("Encerrando: aguarde o cancelamento e a gravação segura da correlação.");
            }

            if (monitoringBusy)
            {
                monitoringClosingAfterSave = true;
                monitoringCancellation?.Cancel();
                MonitoringStatusText.Text = Loc.T("Encerrando: aguarde o último checkpoint e a gravação segura da sessão ") +
                    Loc.T("de monitoramento. Nenhuma observação continua após o fechamento.");
            }

            return;
        }

        if (operations.IsBusy)
        {
            e.Cancel = true;
            operationClosingAfterSave = true;
            return;
        }

        if (unsaved is not null || unsavedHardware is not null || unsavedEvent is not null ||
            unsavedPerformance is not null || unsavedNetwork is not null || unsavedGraphics is not null ||
            unsavedCorrelation is not null || unsavedMonitoring is not null || repairPersistenceFailed)
        {
            e.Cancel = true;
            var pending = new List<string>();
            if (unsaved is not null) pending.Add("há uma auditoria sem confirmação de gravação");
            if (unsavedHardware is not null) pending.Add("há um inventário de hardware sem confirmação de gravação");
            if (unsavedEvent is not null) pending.Add("há uma investigação de eventos sem confirmação de gravação");
            if (unsavedPerformance is not null) pending.Add("há uma sessão de desempenho sem confirmação de gravação");
            if (unsavedNetwork is not null) pending.Add("há uma sessão de rede sem confirmação de gravação");
            if (unsavedGraphics is not null) pending.Add("há uma investigação gráfica sem confirmação de gravação");
            if (unsavedCorrelation is not null) pending.Add("há uma correlação sem confirmação de gravação");
            if (unsavedMonitoring is not null) pending.Add("há uma sessão de monitoramento sem confirmação de gravação");
            if (repairPersistenceFailed) pending.Add("há um reparo sem confirmação de gravação; use Tentar salvar registro");
            ShowWarning(Loc.F($"{string.Join("; ", pending)}. Use os botões de nova tentativa antes de fechar."));
        }
    }

    private async Task ReloadHardwareAsync(Guid? selectId = null)
    {
        var snapshot = await hardwareStore.LoadSnapshotsAsync();
        hardwareSnapshots = snapshot.Snapshots;
        SetIntegrity("Hardware", snapshot.Warnings);
        RefreshHardwareSnapshotList();

        var target = selectId is { } id
            ? hardwareSnapshots.FirstOrDefault(s => s.Id == id)
            : unsavedHardware
              ?? (selectedHardware is { } current ? hardwareSnapshots.FirstOrDefault(s => s.Id == current.Id) : null)
              ?? hardwareSnapshots.FirstOrDefault();

        if (target is null)
        {
            ClearHardwareView();
            return;
        }

        SelectHardwareSnapshotInList(target.Id);
        ApplyHardwareSnapshot(target);
    }

    private void RefreshHardwareSnapshotList()
    {
        updatingHardware = true;
        HardwareSnapshotList.ItemsSource = hardwareSnapshots.Select(s => new HardwareSnapshotRow(s)).ToArray();
        updatingHardware = false;
    }

    private void SelectHardwareSnapshotInList(Guid id)
    {
        updatingHardware = true;
        var row = HardwareSnapshotList.Items.OfType<HardwareSnapshotRow>().FirstOrDefault(r => r.Snapshot.Id == id);
        HardwareSnapshotList.SelectedItem = row;
        if (row is not null) HardwareSnapshotList.ScrollIntoView(row);
        updatingHardware = false;
    }

    private void ApplyHardwareSnapshot(HardwareSnapshot snapshot)
    {
        selectedHardware = snapshot;
        var persisted = hardwareSnapshots.Any(s => s.Id == snapshot.Id) &&
            File.Exists(hardwareStore.GetHardwareReportPath(snapshot.Id));
        HardwareSnapshotInfo.Text = Loc.F($"Snapshot de {Local(snapshot.CompletedUtc)} · {HardwareReport.StatusName(snapshot.Status)} · ") +
            Loc.F($"{snapshot.Duration.TotalSeconds:F1} s · ID {snapshot.Id:D}") +
            (persisted ? string.Empty : Loc.T(" · não confirmado na memória local"));

        var totalItems = snapshot.Results.Sum(r => r.Items.Count);
        var completedCollectors = snapshot.Results.Count(r => r.Status == HardwareCollectionStatus.Completed);
        var totalLimitations = snapshot.Results.Sum(r => r.Limitations.Count);
        HardwareSummaryText.Text = Loc.F($"{totalItems} item(ns) · {completedCollectors}/{snapshot.Results.Count} coletores concluídos · ") +
            Loc.F($"{totalLimitations} limitação(ões) de coleta");

        updatingHardware = true;
        HardwareSearch.Text = string.Empty;
        HardwareSearchHint.Visibility = Visibility.Visible;
        updatingHardware = false;
        hardwareCategory = null;
        SetHardwareCategories(snapshot);
        RefreshHardwareList();
        UpdateHardwareButtons();
    }

    private void ClearHardwareView()
    {
        selectedHardware = null;
        hardwareCategory = null;
        updatingHardware = true;
        HardwareItemList.ItemsSource = null;
        HardwareSearch.Text = string.Empty;
        HardwareSearchHint.Visibility = Visibility.Visible;
        updatingHardware = false;
        SetHardwareCategories(null);
        HardwareSnapshotInfo.Text = Loc.T("Nenhum snapshot de hardware coletado.");
        HardwareSummaryText.Text = Loc.T("Use 'Coletar hardware' para criar o primeiro inventário local.");
        HardwareListHeading.Text = Loc.T("Itens");
        HardwareDetailsHeading.Text = Loc.T("Visão geral");
        HardwareDetails.Text = Loc.T("Nenhum inventário disponível. A coleta é somente de leitura.");
        UpdateHardwareButtons();
    }

    private void SetHardwareCategories(HardwareSnapshot? snapshot)
    {
        var rows = new List<HardwareCategoryRow>
        {
            new(null, snapshot is null ? "Geral" : $"Geral · {snapshot.Results.Sum(r => r.Items.Count)}")
        };
        foreach (var category in Enum.GetValues<HardwareCategory>())
        {
            var count = snapshot?.Results.Where(r => r.Category == category).Sum(r => r.Items.Count) ?? 0;
            rows.Add(new HardwareCategoryRow(category, $"{HardwareReport.CategoryName(category)} · {count}"));
        }

        updatingHardware = true;
        HardwareCategoryList.ItemsSource = rows;
        HardwareCategoryList.SelectedIndex = 0;
        updatingHardware = false;
        hardwareCategory = null;
        HardwareCategoryList.IsEnabled = snapshot is not null;
    }

    private void RefreshHardwareList()
    {
        if (selectedHardware is null)
        {
            updatingHardware = true;
            HardwareItemList.ItemsSource = null;
            updatingHardware = false;
            HardwareListHeading.Text = Loc.T("Itens");
            return;
        }

        var query = HardwareSearch.Text.Trim();
        var items = selectedHardware.Results
            .Where(r => hardwareCategory is null || r.Category == hardwareCategory.Value)
            .SelectMany(r => r.Items)
            .Select(item => new HardwareItemRow(item))
            .Where(row => query.Length == 0 || row.Haystack.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.Item.Category)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        updatingHardware = true;
        HardwareItemList.ItemsSource = items;
        HardwareItemList.SelectedItem = null;
        updatingHardware = false;

        HardwareListHeading.Text = hardwareCategory is { } category
            ? Loc.F($"{HardwareReport.CategoryName(category)} · {items.Length}")
            : Loc.F($"Todos os itens · {items.Length}");

        if (hardwareCategory is null && query.Length == 0)
            ShowSnapshotOverview(selectedHardware);
        else
            ShowFilterSummary(selectedHardware, query, items.Length);
    }

    private void ShowSnapshotOverview(HardwareSnapshot snapshot)
    {
        var total = snapshot.Results.Sum(r => r.Items.Count);
        var byPresence = snapshot.Results
            .SelectMany(r => r.Items)
            .GroupBy(i => i.Presence)
            .ToDictionary(g => g.Key, g => g.Count());

        var text = new StringBuilder();
        if (unsavedHardware?.Id == snapshot.Id)
            text.AppendLine(Loc.T("Este snapshot ainda não foi confirmado na memória local."));
        text.AppendLine(Loc.F($"Status: {HardwareReport.StatusName(snapshot.Status)}"));
        text.AppendLine(Loc.F($"ID local: {snapshot.Id:D}"));
        text.AppendLine(Loc.F($"Software: {snapshot.SoftwareVersion} · formato {snapshot.FormatVersion} · esquema {snapshot.SchemaVersion}"));
        text.AppendLine(Loc.F($"Início: {Local(snapshot.StartedUtc)}"));
        text.AppendLine(Loc.F($"Conclusão: {Local(snapshot.CompletedUtc)} · duração {snapshot.Duration.TotalSeconds:F2} s"));
        text.AppendLine(Loc.F($"Itens observados: {total} · presentes {byPresence.GetValueOrDefault(DevicePresence.Present)} · ") +
            Loc.F($"virtuais {byPresence.GetValueOrDefault(DevicePresence.Virtual)} · desconectados {byPresence.GetValueOrDefault(DevicePresence.Disconnected)} · ") +
            Loc.F($"indeterminados {byPresence.GetValueOrDefault(DevicePresence.Unknown)}"));
        text.AppendLine();
        text.AppendLine(Loc.T("Coletores"));
        foreach (var result in snapshot.Results.OrderBy(r => r.Category))
        {
            text.AppendLine(Loc.F($"• {HardwareReport.CategoryName(result.Category)}: {HardwareReport.StatusName(result.Status)} · ") +
                Loc.F($"{result.Items.Count} item(ns) · {result.Duration.TotalSeconds:F2} s"));
            text.AppendLine(Loc.F($"  Fonte: {result.Source} · coletor {result.CollectorVersion}"));
            foreach (var limitation in result.Limitations)
                text.AppendLine(Loc.F($"  Limitação: {limitation}"));
        }

        text.AppendLine();
        text.AppendLine(Loc.T("Interpretação"));
        text.AppendLine(Loc.T("• Inventário somente de leitura, obtido de interfaces locais do Windows."));
        text.AppendLine(Loc.T("• Campos indisponíveis permanecem nulos; nenhum valor é estimado."));
        text.AppendLine(Loc.T("• Estado OK/Healthy é reportado pelo Windows e não garante integridade física."));
        text.AppendLine(Loc.T("• Categoria parcial, cancelada ou com falha não permite concluir remoção de dispositivos."));

        HardwareDetailsHeading.Text = Loc.T("Visão geral");
        HardwareDetails.Text = text.ToString();
    }

    private void ShowFilterSummary(HardwareSnapshot snapshot, string query, int count)
    {
        var text = new StringBuilder();
        if (hardwareCategory is { } category)
        {
            text.AppendLine(HardwareReport.CategoryName(category));
            var results = snapshot.Results.Where(r => r.Category == category).ToArray();
            if (results.Length == 0)
            {
                text.AppendLine(Loc.T("Categoria ausente neste snapshot; ausência não é conclusiva."));
            }
            else
            {
                foreach (var result in results)
                {
                    text.AppendLine(Loc.F($"Coletor: {HardwareReport.StatusName(result.Status)} · {result.Items.Count} item(ns)"));
                    text.AppendLine(Loc.F($"Fonte: {result.Source}"));
                    foreach (var limitation in result.Limitations)
                        text.AppendLine(Loc.F($"Limitação: {limitation}"));
                }
            }
        }
        else
        {
            text.AppendLine(Loc.T("Todos os itens"));
        }

        if (query.Length > 0) text.AppendLine(Loc.F($"Busca: \"{query}\""));
        text.AppendLine(Loc.F($"{count} item(ns) no filtro atual."));
        text.AppendLine(count > 0
            ? Loc.T("Selecione um item para ver identidade, driver, propriedades e limitações.")
            : Loc.T("Nenhum item corresponde. Campos ausentes não são estimados."));

        HardwareDetailsHeading.Text = hardwareCategory is { } selectedCategory
            ? HardwareReport.CategoryName(selectedCategory)
            : Loc.T("Visão do filtro");
        HardwareDetails.Text = text.ToString();
    }

    private void ShowHardwareItem(HardwareItem item)
    {
        var text = new StringBuilder();
        text.AppendLine(Loc.F($"{HardwareReport.CategoryName(item.Category)} · {item.Kind}"));
        text.AppendLine(Loc.F($"Identidade técnica (hash local): {item.StableId}"));
        text.AppendLine(Loc.F($"Presença: {PresenceName(item.Presence)}"));
        text.AppendLine(Loc.F($"Fabricante: {Display(item.Manufacturer)}"));
        text.AppendLine(Loc.F($"Modelo: {Display(item.Model)}"));
        text.AppendLine(Loc.F($"Hardware ID: {Display(item.HardwareId)}"));
        text.AppendLine(Loc.F($"Estado reportado pelo Windows: {Display(item.WindowsState)}"));
        text.AppendLine(Loc.F($"Firmware: {Display(item.FirmwareVersion)}"));
        text.AppendLine();
        text.AppendLine(Loc.T("Driver associado"));
        if (HasDriverData(item))
        {
            text.AppendLine(Loc.F($"Provedor: {Display(item.DriverProvider)}"));
            text.AppendLine(Loc.F($"Versão: {Display(item.DriverVersion)}"));
            text.AppendLine(Loc.F($"Data (UTC): {(item.DriverDateUtc is { } d ? d.ToUniversalTime().ToString("yyyy-MM-dd") : "Indisponível")}"));
            text.AppendLine(Loc.F($"Classe: {Display(item.DriverClass)}"));
            text.AppendLine(Loc.F($"INF: {Display(item.DriverInfName)}"));
        }
        else
        {
            text.AppendLine(Loc.T("Nenhum driver associado de forma verificável."));
        }

        text.AppendLine(Loc.T("A presença do driver não comprova uso efetivo. A data do driver não indica desatualização."));
        text.AppendLine();
        text.AppendLine(Loc.T("Propriedades"));
        if (item.Properties.Count == 0)
        {
            text.AppendLine(Loc.T("• Nenhuma propriedade adicional registrada."));
        }
        else
        {
            foreach (var prop in item.Properties.OrderBy(p => p.Name, StringComparer.Ordinal))
                text.AppendLine(Loc.F($"• {prop.Name}: {prop.Value}{(prop.Volatile ? " (volátil, não comparado)" : "")}"));
        }

        if (item.Category == HardwareCategory.Storage)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Estado de saúde reportado não representa garantia de integridade física."));
        }

        if (item.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Limitações"));
            foreach (var limitation in item.Limitations)
                text.AppendLine(Loc.F($"• {limitation}"));
        }

        var collectorResult = selectedHardware?.Results.FirstOrDefault(r => r.Category == item.Category);
        if (collectorResult is not null)
        {
            text.AppendLine();
            text.AppendLine(Loc.F($"Coletor: {collectorResult.CollectorVersion} · {HardwareReport.StatusName(collectorResult.Status)} · {Local(collectorResult.CompletedUtc)}"));
            text.AppendLine(Loc.F($"Fonte: {collectorResult.Source}"));
            if (collectorResult.Status != HardwareCollectionStatus.Completed)
                text.AppendLine(Loc.T("Coleta parcial ou com falha: itens ausentes nesta categoria não indicam remoção."));
        }

        HardwareDetailsHeading.Text = string.IsNullOrWhiteSpace(item.Name) ? item.Kind : item.Name;
        HardwareDetails.Text = text.ToString();
    }

    private async void OnHardwareRun(object sender, RoutedEventArgs e)
    {
        using var operation = TryBeginProductOperation();
        if (operation is null) return;
        if (hardwareBusy || busy || eventBusy || performanceBusy || networkBusy || graphicsBusy || correlationBusy || monitoringBusy)
            return;
        hardwareBusy = true;
        hardwareSaving = false;
        hardwareCancellation = new CancellationTokenSource();
        ClearRunWarnings();
        HardwareProgressBar.Maximum = 1;
        HardwareProgressBar.Value = 0;
        RunButton.IsEnabled = false;
        UpdateEventButtons();
        UpdatePerformanceButtons();
        UpdateNetworkButtons();
        var saved = false;
        try
        {
            if (unsavedHardware is not null)
            {
                hardwareSaving = true;
                HardwareStatusText.Text = Loc.T("Tentando salvar novamente o inventário pendente…");
                var pending = unsavedHardware;
                await hardwareStore.SaveSnapshotAsync(pending);
                saved = true;
                unsavedHardware = null;
                await ReloadHardwareAsync(pending.Id);
                HardwareStatusText.Text = Loc.T("Inventário pendente salvo novamente.");
                return;
            }

            HardwareStatusText.Text = Loc.T("Inventário de hardware em andamento. Leitura somente, sem alterações no Windows.");
            var progress = new Progress<HardwareProgress>(p =>
            {
                if (hardwareSaving) return;
                HardwareProgressBar.Maximum = p.Total;
                HardwareProgressBar.Value = p.Finished;
                HardwareStatusText.Text = Loc.F($"{HardwareCollectorName(p.CollectorId)}: {HardwareReport.StatusName(p.Status)} · {p.Finished}/{p.Total} categorias");
            });
            var snapshot = await hardwareRunner.RunAsync(progress, hardwareCancellation.Token);
            unsavedHardware = snapshot;
            ApplyHardwareSnapshot(snapshot);
            hardwareSaving = true;
            UpdateHardwareButtons();
            HardwareStatusText.Text = Loc.T("Salvando inventário e atualizando a memória local…");
            await hardwareStore.SaveSnapshotAsync(snapshot);
            saved = true;
            unsavedHardware = null;
            await ReloadHardwareAsync(snapshot.Id);
            HardwareStatusText.Text = Loc.F($"Inventário salvo. Status da coleta: {HardwareReport.StatusName(snapshot.Status)}. ") +
                Loc.F($"{snapshot.Results.Sum(r => r.Items.Count)} item(ns) em {snapshot.Results.Count} coletores.");
        }
        catch (Exception error)
        {
            var message = unsavedHardware is not null
                ? "Falha de gravação do inventário. O resultado permanece nesta janela; tente salvar novamente"
                : saved
                    ? "O inventário foi salvo, mas a memória local não pôde ser relida. Use 'Recarregar'"
                    : "A coleta de hardware não pôde ser concluída";
            ShowHardwareFailure(message, error);
        }
        finally
        {
            hardwareBusy = false;
            hardwareSaving = false;
            hardwareCancellation?.Dispose();
            hardwareCancellation = null;
            HardwareRunButton.Content = unsavedHardware is not null ? Loc.T("Tentar salvar inventário") : Loc.T("Coletar hardware");
            RunButton.IsEnabled = !busy;
            UpdateHardwareButtons();
            if (hardwareClosingAfterSave && unsavedHardware is null)
            {
                hardwareClosingAfterSave = false;
                Close();
            }
            else
            {
                hardwareClosingAfterSave = false;
            }
        }
    }

    private void OnHardwareCancel(object sender, RoutedEventArgs e)
    {
        hardwareCancellation?.Cancel();
        HardwareCancelButton.IsEnabled = false;
        HardwareStatusText.Text = Loc.T("Cancelamento solicitado. Consultas em andamento podem aguardar o timeout local.");
    }

    private async void OnHardwareReload(object sender, RoutedEventArgs e)
    {
        using var operation = TryBeginProductOperation();
        if (operation is null) return;
        if (hardwareBusy || busy || eventBusy || performanceBusy || networkBusy || graphicsBusy || correlationBusy || monitoringBusy)
            return;
        hardwareBusy = true;
        RunButton.IsEnabled = false;
        UpdateHardwareButtons();
        HardwareStatusText.Text = Loc.T("Recarregando inventários salvos da memória local…");
        try
        {
            await ReloadHardwareAsync(selectedHardware?.Id);
            HardwareStatusText.Text = Loc.T("Inventários recarregados da memória local.");
        }
        catch (Exception error)
        {
            ShowHardwareFailure(Loc.T("Não foi possível recarregar os inventários de hardware"), error);
        }
        finally
        {
            hardwareBusy = false;
            RunButton.IsEnabled = !busy;
            UpdateHardwareButtons();
        }
    }

    private void OnHardwareSnapshotSelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingHardware) return;
        if (HardwareSnapshotList.SelectedItem is HardwareSnapshotRow row)
            ApplyHardwareSnapshot(row.Snapshot);
    }

    private void OnHardwareSearchChanged(object sender, TextChangedEventArgs e)
    {
        HardwareSearchHint.Visibility = string.IsNullOrWhiteSpace(HardwareSearch.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (updatingHardware || selectedHardware is null) return;
        RefreshHardwareList();
    }

    private void OnHardwareCategorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingHardware) return;
        if (HardwareCategoryList.SelectedItem is HardwareCategoryRow row)
        {
            hardwareCategory = row.Category;
            RefreshHardwareList();
        }
    }

    private void OnHardwareItemSelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingHardware) return;
        if (HardwareItemList.SelectedItem is HardwareItemRow row)
            ShowHardwareItem(row.Item);
    }

    private void OnHardwareCompare(object sender, RoutedEventArgs e)
    {
        if (selectedHardware is not { } current || PreviousHardwareSnapshot(current) is not { } previous) return;
        try
        {
            var diff = HardwareDiffEngine.Compare(previous, current);
            HardwareDetailsHeading.Text = Loc.T("Comparação");
            HardwareDetails.Text = RenderHardwareDiff(diff, previous, current);
        }
        catch (Exception error)
        {
            ShowHardwareFailure(Loc.T("Não foi possível comparar os inventários"), error);
        }
    }

    private async void OnOpenHardwareReport(object sender, RoutedEventArgs e)
    {
        if (selectedHardware is not { } snapshot) return;
        try
        {
            HardwareReportButton.IsEnabled = false;
            await WindowsShell.OpenReportLocationAsync(hardwareStore.GetHardwareReportPath(snapshot.Id));
        }
        catch (Exception error)
        {
            ShowHardwareFailure(Loc.T("Não foi possível abrir a localização do relatório de hardware"), error);
        }
        finally
        {
            UpdateHardwareButtons();
        }
    }

    private HardwareSnapshot? PreviousHardwareSnapshot(HardwareSnapshot current)
    {
        for (var i = 0; i < hardwareSnapshots.Count; i++)
        {
            if (hardwareSnapshots[i].Id == current.Id)
                return i + 1 < hardwareSnapshots.Count ? hardwareSnapshots[i + 1] : null;
        }

        return hardwareSnapshots.FirstOrDefault();
    }

    private void UpdateHardwareButtons()
    {
        var previous = selectedHardware is { } current ? PreviousHardwareSnapshot(current) : null;
        var idle = !operations.IsBusy && !hardwareBusy && !busy && !eventBusy && !performanceBusy && !networkBusy && !graphicsBusy &&
            !correlationBusy && !monitoringBusy;
        HardwareRunButton.IsEnabled = idle;
        HardwareReloadButton.IsEnabled = idle;
        HardwareCancelButton.IsEnabled = hardwareBusy && !hardwareSaving &&
            hardwareCancellation is { IsCancellationRequested: false };
        HardwareCompareButton.IsEnabled = idle && selectedHardware is not null && previous is not null;
        HardwareReportButton.IsEnabled = idle && selectedHardware is not null &&
            File.Exists(hardwareStore.GetHardwareReportPath(selectedHardware.Id));
        HardwareSearch.IsEnabled = selectedHardware is not null;
        HardwareCategoryList.IsEnabled = selectedHardware is not null;
        HardwareItemList.IsEnabled = selectedHardware is not null;
        HardwareSnapshotList.IsEnabled = !hardwareBusy;
        UpdateEventButtons();
        UpdatePerformanceButtons();
        UpdateNetworkButtons();
        UpdateGraphicsButtons();
    }

    private static string RenderHardwareDiff(HardwareDiff diff, HardwareSnapshot previous, HardwareSnapshot current)
    {
        var text = new StringBuilder();
        text.AppendLine(Loc.T("Comparação observada (anterior → atual)"));
        text.AppendLine(Loc.F($"Anterior: {Local(previous.CompletedUtc)} · ID {previous.Id:D} · {HardwareReport.StatusName(previous.Status)}"));
        text.AppendLine(Loc.F($"Atual: {Local(current.CompletedUtc)} · ID {current.Id:D} · {HardwareReport.StatusName(current.Status)}"));
        text.AppendLine();
        if (diff.Changes.Count == 0)
        {
            text.AppendLine(Loc.T("Nenhuma diferença observada nas categorias comparáveis."));
        }
        else
        {
            text.AppendLine(Loc.F($"Mudanças observadas: {diff.Changes.Count}"));
            foreach (var change in diff.Changes)
            {
                var subject = $"{HardwareReport.CategoryName(change.Category)} · {change.Kind}" +
                    (string.IsNullOrWhiteSpace(change.Name) ? string.Empty : $" · {change.Name}");
                var line = change.Type is HardwareChangeType.Added or HardwareChangeType.Removed
                    ? $"• {ChangeTypeName(change.Type)}: {subject}"
                    : $"• {ChangeTypeName(change.Type)}: {subject} · {change.Field ?? "campo"}: {Display(change.Before)} → {Display(change.After)}";
                text.AppendLine(line);
            }
        }

        if (diff.NotComparable.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Categorias sem avaliação de adição/remoção"));
            foreach (var category in diff.NotComparable)
                text.AppendLine(Loc.F($"• {HardwareReport.CategoryName(category)}"));
        }

        if (diff.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Limitações da comparação"));
            foreach (var limitation in diff.Limitations)
                text.AppendLine(Loc.F($"• {limitation}"));
        }

        text.AppendLine();
        text.AppendLine(Loc.T("Interpretação"));
        text.AppendLine(Loc.T("• O relatório descreve diferenças observadas; não atribui causa, gravidade ou diagnóstico."));
        text.AppendLine(Loc.T("• Alteração de driver ou firmware não indica melhoria, regressão ou falha."));
        text.AppendLine(Loc.T("• Categoria não comparável não autoriza afirmar que um dispositivo foi removido."));
        return text.ToString();
    }

    // Avisos de integridade (por origem) persistem até a próxima carga bem-sucedida da mesma origem;
    // avisos da execução atual são descartados quando uma nova execução começa. Nenhum substitui o outro.
    private readonly Dictionary<string, string[]> integrityWarnings = [];
    private readonly List<string> runWarnings = [];

    private void SetIntegrity(string source, IEnumerable<string> warnings)
    {
        var lines = warnings.ToArray();
        if (lines.Length == 0) integrityWarnings.Remove(source); else integrityWarnings[source] = lines;
        RenderWarnings();
    }

    private void ShowWarning(string text)
    {
        if (!runWarnings.Contains(text)) runWarnings.Add(text);
        RenderWarnings();
    }

    private void ClearRunWarnings()
    {
        runWarnings.Clear();
        RenderWarnings();
    }

    private void RenderWarnings()
    {
        var lines = integrityWarnings.SelectMany(p => p.Value.Select(w => $"Histórico incompleto — {p.Key}: {w}"))
            .Concat(runWarnings).ToArray();
        StorageWarnings.Text = string.Join(Loc.T("\n"), lines);
        WarningBanner.Visibility = lines.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool sidebarUserChoice;
    private const double SidebarAutoCollapseWidth = 1240, SidebarForcedCollapseWidth = 1120;

    private void OnToggleSidebar(object sender, RoutedEventArgs e)
    {
        if (ActualWidth < SidebarForcedCollapseWidth) return; // o conteúdo precisa dos 184 px; o botão fica desabilitado
        sidebarUserChoice = true;
        SetSidebarCollapsed(!Nav.GetCollapsed(NavList));
    }

    // Abaixo de 1240 px o menu recolhe sozinho; abaixo de 1120 px ele não pode expandir (as páginas de três colunas
    // não cabem). Entre os dois limites a escolha manual prevalece sobre a automática.
    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var narrow = e.NewSize.Width < SidebarForcedCollapseWidth;
        SidebarToggle.IsEnabled = !narrow;
        SidebarToggle.ToolTip = narrow ? Loc.T("Amplie a janela (1120 px ou mais) para expandir o menu") : Loc.T("Recolher ou expandir o menu (Ctrl+B)");
        if (narrow || !sidebarUserChoice) SetSidebarCollapsed(e.NewSize.Width < SidebarAutoCollapseWidth);
    }

    private void SetSidebarCollapsed(bool collapsed)
    {
        Nav.SetCollapsed(NavList, collapsed);
        SidebarColumn.Width = new GridLength(collapsed ? 64 : 248);
        BrandMark.Visibility = BrandText.Visibility = LanguageRow.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(SidebarToggle, collapsed ? 3 : 1);
        SidebarToggle.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        NavList.Margin = new Thickness(0, collapsed ? 4 : 0, 0, 0);
    }

    private void ShowFailure(string message, Exception error)
    {
        logFailure(error);
        ExecutionStatus.Text = message + ".";
        ShowWarning(Loc.F($"{message}. {error.GetType().Name}: {error.Message}"));
    }

    private void ShowHardwareFailure(string message, Exception error)
    {
        ShowFailure(message, error);
        HardwareStatusText.Text = message + ".";
    }

    private static string CollectorText(CollectorResult result)
    {
        var text = new StringBuilder($"{CollectorName(result.CollectorId)}\n{Status(result.Status)}\n\n");
        if (result.Data is { } data)
        {
            foreach (var (name, value) in MarkdownReport.Fields(data))
                text.AppendLine(Loc.F($"{name}: {value ?? "Indisponível"}"));
        }
        else if (result.Evidence is { } evidence)
        {
            text.AppendLine(evidence.Summary);
            text.AppendLine(Loc.F($"Registro de origem: {evidence.RecordId:D}"));
        }
        else
        {
            text.AppendLine(Loc.T("Dados indisponíveis."));
        }

        text.AppendLine(Loc.F($"\nFonte: {result.Source}\nColetor: {result.CollectorVersion}\nColetado: {Local(result.CompletedUtc)}\nDuração: {result.Duration.TotalSeconds:F2} s"));
        if (result.Limitations.Count > 0)
            text.AppendLine(Loc.T("\nLimitações:\n") + string.Join("\n", result.Limitations.Select(x => "• " + x)));
        if (result.CollectorId == "storage")
            text.AppendLine(Loc.T("\nOK/Healthy é o estado reportado pelo Windows e não garante integridade física."));
        return text.ToString();
    }

    private static string Local(DateTimeOffset at) => at.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz");
    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "Indisponível" : value;
    private static bool HasDriverData(HardwareItem item) =>
        !string.IsNullOrWhiteSpace(item.DriverProvider) ||
        !string.IsNullOrWhiteSpace(item.DriverVersion) ||
        item.DriverDateUtc is not null ||
        !string.IsNullOrWhiteSpace(item.DriverClass) ||
        !string.IsNullOrWhiteSpace(item.DriverInfName);

    private static string PresenceName(DevicePresence state) => state switch
    {
        DevicePresence.Present => "Presente",
        DevicePresence.Virtual => "Virtual",
        DevicePresence.Disconnected => "Desconectado",
        DevicePresence.Unknown => "Indeterminado",
        _ => state.ToString()
    };

    private static string ChangeTypeName(HardwareChangeType type) => type switch
    {
        HardwareChangeType.Added => "Adicionado",
        HardwareChangeType.Removed => "Removido",
        HardwareChangeType.DriverChanged => "Driver alterado",
        HardwareChangeType.FirmwareChanged => "Firmware alterado",
        HardwareChangeType.CapacityChanged => "Capacidade alterada",
        HardwareChangeType.ConfigurationChanged => "Configuração alterada",
        HardwareChangeType.StateChanged => "Estado alterado",
        _ => type.ToString()
    };

    private static string CollectorName(string id) => id switch
    {
        "windows" => "Windows",
        "processor" => "Processador",
        "memory" => "Memória",
        "board-bios" => "Placa-mãe e BIOS",
        "graphics" => "Placa de vídeo",
        "storage" => "Armazenamento",
        "audit-hardware" => "Inventário de hardware",
        "audit-events" => "Estabilidade e eventos",
        "audit-performance" => "Desempenho",
        "audit-network" => "Rede",
        "audit-graphics" => "Gráficos e GPU",
        "audit-monitoring" => "Monitoramento",
        _ => id
    };

    private static string HardwareCollectorName(string id) => id switch
    {
        "cpu" => "Processador",
        "memory" => "Memória",
        "board-bios" => "Placa-mãe e BIOS",
        "gpu" => "Placa de vídeo",
        "monitor" => "Monitores",
        "storage" => "Armazenamento",
        "network" => "Rede",
        "audio" => "Áudio",
        "usb-peripheral" => "USB e periféricos",
        "driver" => "Drivers",
        "other-device" => "Outros dispositivos",
        _ => id
    };

    private static string Status(DiagnosticStatus status) => UiText.Diagnostic(status);

    private static string FreshnessText(FreshnessState state) => state switch
    {
        FreshnessState.None => "Nenhuma auditoria",
        FreshnessState.Current => "Atualizada",
        FreshnessState.Expiring => "Próxima do vencimento",
        FreshnessState.Outdated => "Desatualizada",
        FreshnessState.Unsuccessful => "Sem auditoria concluída",
        FreshnessState.InvalidDate => "Data futura inválida",
        _ => state.ToString()
    };

    private static string AuditName(AuditKind kind) => kind == AuditKind.Full ? "Auditoria completa" : "Auditoria básica";

    private static string AuditSummary(AuditRecord audit)
    {
        var planned = audit.PlannedSteps ?? audit.Results.Count;
        var summary = $"{audit.Results.Count}/{planned} etapas registradas · " +
            $"{audit.Results.Count(r => r.Status == DiagnosticStatus.Completed)} concluídas · " +
            $"{audit.Results.Count(r => r.Status == DiagnosticStatus.Partial)} parciais · " +
            $"{audit.Results.Count(r => r.Status == DiagnosticStatus.Failed)} indisponíveis ou com falha · " +
            $"{audit.Results.Count(r => r.Status == DiagnosticStatus.Cancelled)} canceladas";
        return audit.InterruptionReason is { } reason ? $"{summary}\n{reason}" : summary;
    }

    private sealed record CollectorRow(CollectorResult Result)
    {
        public string Name => CollectorName(Result.CollectorId);
        public string Status => MainWindow.Status(Result.Status);
        public override string ToString() => $"{Name}: {Status}";
    }

    private sealed record HistoryRow(AuditRecord Audit)
    {
        public string Label => $"{Audit.CompletedUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss} · {AuditName(Audit.Kind)} · {Status(Audit.Status)}";
        public override string ToString() => Label;
    }

    private sealed record HardwareSnapshotRow(HardwareSnapshot Snapshot)
    {
        public string DateText => Snapshot.CompletedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
        public string SummaryText =>
            $"{HardwareReport.StatusName(Snapshot.Status)} · {Snapshot.Results.Sum(r => r.Items.Count)} item(ns) · {Snapshot.Duration.TotalSeconds:F1} s";
        public override string ToString() => DateText;
    }

    private sealed record HardwareCategoryRow(HardwareCategory? Category, string Label);

    private sealed class HardwareItemRow(HardwareItem item)
    {
        public HardwareItem Item { get; } = item;
        public string Haystack { get; } = BuildHaystack(item);
        public string Name => !string.IsNullOrWhiteSpace(Item.Name)
            ? Item.Name
            : !string.IsNullOrWhiteSpace(Item.Model)
                ? Item.Model
                : Item.Kind;
        public string Subtitle
        {
            get
            {
                var parts = new[] { Item.Manufacturer, Item.Model }
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return parts.Length > 0 ? string.Join(" · ", parts!) : Item.Kind;
            }
        }
        public string Badge
        {
            get
            {
                var parts = new List<string>
                {
                    HardwareReport.CategoryName(Item.Category),
                    Item.Kind,
                    PresenceName(Item.Presence)
                };
                if (!string.IsNullOrWhiteSpace(Item.DriverVersion)) parts.Add("driver " + Item.DriverVersion);
                if (!string.IsNullOrWhiteSpace(Item.WindowsState)) parts.Add(Item.WindowsState);
                return string.Join(" · ", parts);
            }
        }

        private static string BuildHaystack(HardwareItem item) =>
            string.Join("\n",
                new[]
                {
                    item.Name, item.Manufacturer, item.Model, item.HardwareId, item.Kind,
                    HardwareReport.CategoryName(item.Category), item.DriverProvider, item.DriverVersion,
                    item.DriverClass, item.DriverInfName, item.FirmwareVersion, item.WindowsState
                }
                .Concat(item.Properties.Select(p => p.Name + " " + p.Value))
                .Concat(item.Limitations)
                .Where(s => !string.IsNullOrWhiteSpace(s)));
    }
}
