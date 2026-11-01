using System.ComponentModel;
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

namespace SentinelCore.App;

public partial class MainWindow : Window
{
    private readonly AuditRunner runner;
    private readonly HardwareRunner hardwareRunner;
    private readonly EventInvestigationRunner eventRunner;
    private readonly PerformanceSessionRunner performanceRunner;
    private readonly NetworkSessionRunner networkRunner;
    private readonly GraphicsInvestigationRunner graphicsRunner;
    private readonly IAuditStore store;
    private readonly IHardwareSnapshotStore hardwareStore;
    private readonly IEventInvestigationStore eventStore;
    private readonly IPerformanceSessionStore performanceStore;
    private readonly INetworkSessionStore networkStore;
    private readonly IGraphicsInvestigationStore graphicsStore;
    private readonly FreshnessEngine freshness = new();
    private readonly DispatcherTimer freshnessTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private CancellationTokenSource? cancellation;
    private IReadOnlyList<AuditRecord> history = [];
    private AuditRecord? reportAudit;
    private AuditRecord? unsaved;
    private bool busy;
    private bool closingAfterSave;
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
        GraphicsInvestigationRunner graphicsRunner, IAuditStore store, IHardwareSnapshotStore hardwareStore,
        IEventInvestigationStore eventStore, IPerformanceSessionStore performanceStore,
        INetworkSessionStore networkStore, IGraphicsInvestigationStore graphicsStore)
    {
        InitializeComponent();
        this.runner = runner;
        this.hardwareRunner = hardwareRunner;
        this.eventRunner = eventRunner;
        this.performanceRunner = performanceRunner;
        this.networkRunner = networkRunner;
        this.graphicsRunner = graphicsRunner;
        this.store = store;
        this.hardwareStore = hardwareStore;
        this.eventStore = eventStore;
        this.performanceStore = performanceStore;
        this.networkStore = networkStore;
        this.graphicsStore = graphicsStore;
        VersionText.Text = $"v{SoftwareInfo.Version}  •  Local e offline";
        InitializeEvents();
        InitializePerformance();
        InitializeNetwork();
        InitializeGraphics();
        SetHardwareCategories(null);
        UpdateHardwareButtons();
        freshnessTimer.Tick += (_, _) => UpdateSummary();
        freshnessTimer.Start();
        Closed += (_, _) => freshnessTimer.Stop();
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

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var index) &&
            index >= 0 && index < NavList.Items.Count)
        {
            NavList.SelectedIndex = index;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;

        if (e.Key == Key.Tab)
        {
            e.Handled = true;
            var activeTabs = PerformanceTabs.IsKeyboardFocusWithin
                ? PerformanceTabs
                : NetworkTabs.IsKeyboardFocusWithin
                    ? NetworkTabs
                    : GraphicsTabs.IsKeyboardFocusWithin
                        ? GraphicsTabs
                        : null;
            if (activeTabs is not null && activeTabs.Items.Count > 0)
            {
                var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
                var count = activeTabs.Items.Count;
                activeTabs.SelectedIndex = (activeTabs.SelectedIndex + step + count) % count;
            }
            return;
        }

        var index = e.Key - Key.D1;
        if (index >= 0 && index < NavList.Items.Count)
        {
            NavList.SelectedIndex = index;
            e.Handled = true;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RunButton.IsEnabled = false;
        UpdateHardwareButtons();
        try
        {
            try { await ReloadAsync(); }
            catch (Exception error) { ShowFailure("Não foi possível recuperar a memória local", error); }

            try { await ReloadHardwareAsync(); }
            catch (Exception error) { ShowHardwareFailure("Não foi possível recuperar os inventários de hardware", error); }

            try { await ReloadEventsAsync(); }
            catch (Exception error) { ShowEventFailure("Não foi possível recuperar as investigações de eventos", error); }

            try { await ReloadPerformanceAsync(); }
            catch (Exception error) { ShowPerformanceFailure("Não foi possível recuperar as sessões de desempenho", error); }

            try { await ReloadNetworksAsync(); }
            catch (Exception error) { ShowNetworkFailure("Não foi possível recuperar as sessões de rede", error); }

            try { await ReloadGraphicsAsync(); }
            catch (Exception error) { ShowGraphicsFailure("Não foi possível recuperar as investigações gráficas", error); }
        }
        finally
        {
            RunButton.IsEnabled = true;
            UpdateHardwareButtons();
        }
    }

    private async Task ReloadAsync()
    {
        var snapshot = await store.LoadAsync();
        history = snapshot.Audits;
        HistoryList.ItemsSource = history.Select(audit => new HistoryRow(audit)).ToArray();
        if (snapshot.Warnings.Count > 0) ShowWarning(string.Join("\n", snapshot.Warnings));
        UpdateSummary();
        if (history.FirstOrDefault() is { } latest)
        {
            DisplayLatest(latest);
            HistoryList.SelectedIndex = 0;
            ExecutionStatus.Text = $"{history.Count} auditoria(s) recuperada(s) da memória local. Última tentativa: {Status(latest.Status)}.";
        }
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (busy || hardwareBusy || eventBusy || performanceBusy || networkBusy || graphicsBusy) return;
        busy = true;
        RunButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ReportButton.IsEnabled = false;
        WarningBanner.Visibility = Visibility.Collapsed;
        cancellation = new CancellationTokenSource();
        AuditProgressBar.Value = 0;
        UpdateHardwareButtons();
        try
        {
            if (unsaved is not null)
            {
                CancelButton.IsEnabled = false;
                ExecutionStatus.Text = "Tentando salvar novamente a auditoria pendente…";
                await store.SaveAsync(unsaved);
                unsaved = null;
                await ReloadAsync();
                return;
            }

            ExecutionStatus.Text = "Auditoria básica em andamento. Nenhuma configuração do Windows será alterada.";
            var progress = new Progress<AuditProgress>(p =>
            {
                AuditProgressBar.Maximum = p.Total;
                AuditProgressBar.Value = p.Finished;
                ExecutionStatus.Text = $"{CollectorName(p.CollectorId)}: {Status(p.Status)} · {p.Finished}/{p.Total} categorias";
            });
            var audit = await runner.RunBasicAsync(progress, cancellation.Token);
            unsaved = audit;
            DisplayLatest(audit);
            CancelButton.IsEnabled = false;
            ExecutionStatus.Text = "Salvando resultado e atualizando memória…";
            await store.SaveAsync(audit);
            unsaved = null;
            await ReloadAsync();
            ExecutionStatus.Text = $"Auditoria {Status(audit.Status).ToLowerInvariant()}. Histórico e relatório salvos localmente.";
        }
        catch (Exception error)
        {
            ShowFailure(unsaved is not null
                ? "Falha de gravação. O resultado permanece nesta janela; tente salvar novamente"
                : "A operação não pôde ser concluída", error);
        }
        finally
        {
            busy = false;
            cancellation?.Dispose();
            cancellation = null;
            RunButton.Content = unsaved is not null ? "Tentar salvar auditoria" : "Executar auditoria básica";
            RunButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            ReportButton.IsEnabled = unsaved is null && reportAudit is not null &&
                File.Exists(store.GetReportPath(reportAudit.Id));
            UpdateHardwareButtons();
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
        CancelButton.IsEnabled = false;
        ExecutionStatus.Text = "Cancelamento solicitado. Aguardando a consulta somente de leitura em andamento…";
    }

    private void DisplayLatest(AuditRecord audit)
    {
        reportAudit = audit;
        ResultHeading.Text = $"Última auditoria · {Status(audit.Status)}";
        LastCheck.Text = $"Última verificação: {Local(audit.CompletedUtc)} · {audit.Duration.TotalSeconds:F1} s · ID {audit.Id:D}";
        CollectorList.ItemsSource = audit.Results.Select(r => new CollectorRow(r)).ToArray();
        CollectorList.SelectedIndex = audit.Results.Count > 0 ? 0 : -1;
        ReportButton.IsEnabled = File.Exists(store.GetReportPath(audit.Id));
    }

    private void UpdateSummary()
    {
        var basic = freshness.Evaluate(history, AuditKind.Basic);
        var full = freshness.Evaluate(history, AuditKind.Full);
        BasicState.Text = FreshnessText(basic.State);
        FullState.Text = FreshnessText(full.State);
        BasicDate.Text = "Último sucesso: " + (basic.LastSuccessUtc is { } last ? Local(last) : "nunca");
        if (basic.ExpiresUtc is { } expires) BasicDate.Text += "\nValidade: " + Local(expires);
        if (full.LastSuccessUtc is { } fullLast) FullDate.Text = "Último sucesso: " + Local(fullLast);
        if (basic.LatestAttemptStatus is { } attempt && attempt != DiagnosticStatus.Completed)
            BasicDate.Text += "\nÚltima tentativa: " + Status(attempt);

        BasicState.Foreground = (Brush)FindResource(basic.State is FreshnessState.Current ? "Accent" : "Warn");
        var latestWithData = history.FirstOrDefault(a => a.Results.Any(r => r.Data is not null));
        var os = latestWithData?.Results.Select(r => r.Data?.OperatingSystem).FirstOrDefault(x => x is not null);
        var ram = latestWithData?.Results.Select(r => r.Data?.Memory).FirstOrDefault(x => x is not null);
        var cpu = latestWithData?.Results.SelectMany(r => r.Data?.Processors ?? []).FirstOrDefault();
        ComputerSummary.Text = os?.Name ?? "Windows · nome indisponível";
        ComputerDetail.Text = $"{cpu?.Model ?? "CPU indisponível"}\nRAM instalada: {Units.GiB(ram?.InstalledBytes)}";
    }

    private void OnCollectorSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CollectorList.SelectedItem is CollectorRow row)
            CollectorDetails.Text = CollectorText(row.Result);
    }

    private void OnHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row) return;
        reportAudit = row.Audit;
        HistoryDetails.Text = $"ID {row.Audit.Id:D}\n{Local(row.Audit.CompletedUtc)}\n{Status(row.Audit.Status)} · versão {row.Audit.SoftwareVersion}\n\n" +
            string.Join("\n\n", row.Audit.Results.Select(CollectorText));
        ReportButton.IsEnabled = !busy && File.Exists(store.GetReportPath(row.Audit.Id));
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
            ShowFailure("Não foi possível abrir a localização do relatório", error);
        }
        finally
        {
            ReportButton.IsEnabled = !busy && reportAudit is not null && File.Exists(store.GetReportPath(reportAudit.Id));
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy || hardwareBusy || eventBusy || performanceBusy || networkBusy || graphicsBusy)
        {
            e.Cancel = true;
            if (busy)
            {
                closingAfterSave = true;
                cancellation?.Cancel();
                ExecutionStatus.Text = "Encerrando: aguarde o cancelamento e a gravação segura do histórico.";
            }

            if (hardwareBusy)
            {
                hardwareClosingAfterSave = true;
                hardwareCancellation?.Cancel();
                HardwareStatusText.Text = "Encerrando: aguarde o cancelamento e a gravação segura do inventário.";
            }

            if (eventBusy)
            {
                eventClosingAfterSave = true;
                eventCancellation?.Cancel();
                EventStatusText.Text = "Encerrando: aguarde o cancelamento e a gravação segura da investigação.";
            }

            if (performanceBusy)
            {
                performanceClosingAfterSave = true;
                performanceCancellation?.Cancel();
                PerformanceStatusText.Text = "Encerrando: aguarde o cancelamento e a gravação segura da sessão de desempenho.";
            }

            if (networkBusy)
            {
                networkClosingAfterSave = true;
                networkCancellation?.Cancel();
                NetworkStatusText.Text = "Encerrando: aguarde o cancelamento e a gravação segura da sessão de rede.";
            }

            if (graphicsBusy)
            {
                graphicsClosingAfterSave = true;
                graphicsCancellation?.Cancel();
                GraphicsStatusText.Text = "Encerrando: aguarde o cancelamento e a gravação segura da investigação gráfica.";
            }

            return;
        }

        if (unsaved is not null || unsavedHardware is not null || unsavedEvent is not null ||
            unsavedPerformance is not null || unsavedNetwork is not null || unsavedGraphics is not null)
        {
            e.Cancel = true;
            var pending = new List<string>();
            if (unsaved is not null) pending.Add("há uma auditoria sem confirmação de gravação");
            if (unsavedHardware is not null) pending.Add("há um inventário de hardware sem confirmação de gravação");
            if (unsavedEvent is not null) pending.Add("há uma investigação de eventos sem confirmação de gravação");
            if (unsavedPerformance is not null) pending.Add("há uma sessão de desempenho sem confirmação de gravação");
            if (unsavedNetwork is not null) pending.Add("há uma sessão de rede sem confirmação de gravação");
            if (unsavedGraphics is not null) pending.Add("há uma investigação gráfica sem confirmação de gravação");
            ShowWarning($"{string.Join("; ", pending)}. Use os botões de nova tentativa antes de fechar.");
        }
    }

    private async Task ReloadHardwareAsync(Guid? selectId = null)
    {
        var snapshot = await hardwareStore.LoadSnapshotsAsync();
        hardwareSnapshots = snapshot.Snapshots;
        if (snapshot.Warnings.Count > 0) ShowWarning(string.Join("\n", snapshot.Warnings));
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
        HardwareSnapshotInfo.Text = $"Snapshot de {Local(snapshot.CompletedUtc)} · {HardwareReport.StatusName(snapshot.Status)} · " +
            $"{snapshot.Duration.TotalSeconds:F1} s · ID {snapshot.Id:D}" +
            (persisted ? string.Empty : " · não confirmado na memória local");

        var totalItems = snapshot.Results.Sum(r => r.Items.Count);
        var completedCollectors = snapshot.Results.Count(r => r.Status == HardwareCollectionStatus.Completed);
        var totalLimitations = snapshot.Results.Sum(r => r.Limitations.Count);
        HardwareSummaryText.Text = $"{totalItems} item(ns) · {completedCollectors}/{snapshot.Results.Count} coletores concluídos · " +
            $"{totalLimitations} limitação(ões) de coleta";

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
        HardwareSnapshotInfo.Text = "Nenhum snapshot de hardware coletado.";
        HardwareSummaryText.Text = "Use 'Coletar hardware' para criar o primeiro inventário local.";
        HardwareListHeading.Text = "Itens";
        HardwareDetailsHeading.Text = "Visão geral";
        HardwareDetails.Text = "Nenhum inventário disponível. A coleta é somente de leitura.";
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
            HardwareListHeading.Text = "Itens";
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
            ? $"{HardwareReport.CategoryName(category)} · {items.Length}"
            : $"Todos os itens · {items.Length}";

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
            text.AppendLine("Este snapshot ainda não foi confirmado na memória local.");
        text.AppendLine($"Status: {HardwareReport.StatusName(snapshot.Status)}");
        text.AppendLine($"ID local: {snapshot.Id:D}");
        text.AppendLine($"Software: {snapshot.SoftwareVersion} · formato {snapshot.FormatVersion} · esquema {snapshot.SchemaVersion}");
        text.AppendLine($"Início: {Local(snapshot.StartedUtc)}");
        text.AppendLine($"Conclusão: {Local(snapshot.CompletedUtc)} · duração {snapshot.Duration.TotalSeconds:F2} s");
        text.AppendLine($"Itens observados: {total} · presentes {byPresence.GetValueOrDefault(DevicePresence.Present)} · " +
            $"virtuais {byPresence.GetValueOrDefault(DevicePresence.Virtual)} · desconectados {byPresence.GetValueOrDefault(DevicePresence.Disconnected)} · " +
            $"indeterminados {byPresence.GetValueOrDefault(DevicePresence.Unknown)}");
        text.AppendLine();
        text.AppendLine("Coletores");
        foreach (var result in snapshot.Results.OrderBy(r => r.Category))
        {
            text.AppendLine($"• {HardwareReport.CategoryName(result.Category)}: {HardwareReport.StatusName(result.Status)} · " +
                $"{result.Items.Count} item(ns) · {result.Duration.TotalSeconds:F2} s");
            text.AppendLine($"  Fonte: {result.Source} · coletor {result.CollectorVersion}");
            foreach (var limitation in result.Limitations)
                text.AppendLine($"  Limitação: {limitation}");
        }

        text.AppendLine();
        text.AppendLine("Interpretação");
        text.AppendLine("• Inventário somente de leitura, obtido de interfaces locais do Windows.");
        text.AppendLine("• Campos indisponíveis permanecem nulos; nenhum valor é estimado.");
        text.AppendLine("• Estado OK/Healthy é reportado pelo Windows e não garante integridade física.");
        text.AppendLine("• Categoria parcial, cancelada ou com falha não permite concluir remoção de dispositivos.");

        HardwareDetailsHeading.Text = "Visão geral";
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
                text.AppendLine("Categoria ausente neste snapshot; ausência não é conclusiva.");
            }
            else
            {
                foreach (var result in results)
                {
                    text.AppendLine($"Coletor: {HardwareReport.StatusName(result.Status)} · {result.Items.Count} item(ns)");
                    text.AppendLine($"Fonte: {result.Source}");
                    foreach (var limitation in result.Limitations)
                        text.AppendLine($"Limitação: {limitation}");
                }
            }
        }
        else
        {
            text.AppendLine("Todos os itens");
        }

        if (query.Length > 0) text.AppendLine($"Busca: \"{query}\"");
        text.AppendLine($"{count} item(ns) no filtro atual.");
        text.AppendLine(count > 0
            ? "Selecione um item para ver identidade, driver, propriedades e limitações."
            : "Nenhum item corresponde. Campos ausentes não são estimados.");

        HardwareDetailsHeading.Text = hardwareCategory is { } selectedCategory
            ? HardwareReport.CategoryName(selectedCategory)
            : "Visão do filtro";
        HardwareDetails.Text = text.ToString();
    }

    private void ShowHardwareItem(HardwareItem item)
    {
        var text = new StringBuilder();
        text.AppendLine($"{HardwareReport.CategoryName(item.Category)} · {item.Kind}");
        text.AppendLine($"Identidade técnica (hash local): {item.StableId}");
        text.AppendLine($"Presença: {PresenceName(item.Presence)}");
        text.AppendLine($"Fabricante: {Display(item.Manufacturer)}");
        text.AppendLine($"Modelo: {Display(item.Model)}");
        text.AppendLine($"Hardware ID: {Display(item.HardwareId)}");
        text.AppendLine($"Estado reportado pelo Windows: {Display(item.WindowsState)}");
        text.AppendLine($"Firmware: {Display(item.FirmwareVersion)}");
        text.AppendLine();
        text.AppendLine("Driver associado");
        if (HasDriverData(item))
        {
            text.AppendLine($"Provedor: {Display(item.DriverProvider)}");
            text.AppendLine($"Versão: {Display(item.DriverVersion)}");
            text.AppendLine($"Data (UTC): {(item.DriverDateUtc is { } d ? d.ToUniversalTime().ToString("yyyy-MM-dd") : "Indisponível")}");
            text.AppendLine($"Classe: {Display(item.DriverClass)}");
            text.AppendLine($"INF: {Display(item.DriverInfName)}");
        }
        else
        {
            text.AppendLine("Nenhum driver associado de forma verificável.");
        }

        text.AppendLine("A presença do driver não comprova uso efetivo. A data do driver não indica desatualização.");
        text.AppendLine();
        text.AppendLine("Propriedades");
        if (item.Properties.Count == 0)
        {
            text.AppendLine("• Nenhuma propriedade adicional registrada.");
        }
        else
        {
            foreach (var prop in item.Properties.OrderBy(p => p.Name, StringComparer.Ordinal))
                text.AppendLine($"• {prop.Name}: {prop.Value}{(prop.Volatile ? " (volátil, não comparado)" : "")}");
        }

        if (item.Category == HardwareCategory.Storage)
        {
            text.AppendLine();
            text.AppendLine("Estado de saúde reportado não representa garantia de integridade física.");
        }

        if (item.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Limitações");
            foreach (var limitation in item.Limitations)
                text.AppendLine($"• {limitation}");
        }

        var collectorResult = selectedHardware?.Results.FirstOrDefault(r => r.Category == item.Category);
        if (collectorResult is not null)
        {
            text.AppendLine();
            text.AppendLine($"Coletor: {collectorResult.CollectorVersion} · {HardwareReport.StatusName(collectorResult.Status)} · {Local(collectorResult.CompletedUtc)}");
            text.AppendLine($"Fonte: {collectorResult.Source}");
            if (collectorResult.Status != HardwareCollectionStatus.Completed)
                text.AppendLine("Coleta parcial ou com falha: itens ausentes nesta categoria não indicam remoção.");
        }

        HardwareDetailsHeading.Text = string.IsNullOrWhiteSpace(item.Name) ? item.Kind : item.Name;
        HardwareDetails.Text = text.ToString();
    }

    private async void OnHardwareRun(object sender, RoutedEventArgs e)
    {
        if (hardwareBusy || busy || eventBusy || performanceBusy || networkBusy || graphicsBusy) return;
        hardwareBusy = true;
        hardwareSaving = false;
        hardwareCancellation = new CancellationTokenSource();
        WarningBanner.Visibility = Visibility.Collapsed;
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
                HardwareStatusText.Text = "Tentando salvar novamente o inventário pendente…";
                var pending = unsavedHardware;
                await hardwareStore.SaveSnapshotAsync(pending);
                saved = true;
                unsavedHardware = null;
                await ReloadHardwareAsync(pending.Id);
                HardwareStatusText.Text = "Inventário pendente salvo novamente.";
                return;
            }

            HardwareStatusText.Text = "Inventário de hardware em andamento. Leitura somente, sem alterações no Windows.";
            var progress = new Progress<HardwareProgress>(p =>
            {
                if (hardwareSaving) return;
                HardwareProgressBar.Maximum = p.Total;
                HardwareProgressBar.Value = p.Finished;
                HardwareStatusText.Text = $"{HardwareCollectorName(p.CollectorId)}: {HardwareReport.StatusName(p.Status)} · {p.Finished}/{p.Total} categorias";
            });
            var snapshot = await hardwareRunner.RunAsync(progress, hardwareCancellation.Token);
            unsavedHardware = snapshot;
            ApplyHardwareSnapshot(snapshot);
            hardwareSaving = true;
            UpdateHardwareButtons();
            HardwareStatusText.Text = "Salvando inventário e atualizando a memória local…";
            await hardwareStore.SaveSnapshotAsync(snapshot);
            saved = true;
            unsavedHardware = null;
            await ReloadHardwareAsync(snapshot.Id);
            HardwareStatusText.Text = $"Inventário salvo. Status da coleta: {HardwareReport.StatusName(snapshot.Status)}. " +
                $"{snapshot.Results.Sum(r => r.Items.Count)} item(ns) em {snapshot.Results.Count} coletores.";
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
            HardwareRunButton.Content = unsavedHardware is not null ? "Tentar salvar inventário" : "Coletar hardware";
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
        HardwareStatusText.Text = "Cancelamento solicitado. Consultas em andamento podem aguardar o timeout local.";
    }

    private async void OnHardwareReload(object sender, RoutedEventArgs e)
    {
        if (hardwareBusy || busy || eventBusy || performanceBusy || networkBusy || graphicsBusy) return;
        hardwareBusy = true;
        RunButton.IsEnabled = false;
        UpdateHardwareButtons();
        HardwareStatusText.Text = "Recarregando inventários salvos da memória local…";
        try
        {
            await ReloadHardwareAsync(selectedHardware?.Id);
            HardwareStatusText.Text = "Inventários recarregados da memória local.";
        }
        catch (Exception error)
        {
            ShowHardwareFailure("Não foi possível recarregar os inventários de hardware", error);
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
            HardwareDetailsHeading.Text = "Comparação";
            HardwareDetails.Text = RenderHardwareDiff(diff, previous, current);
        }
        catch (Exception error)
        {
            ShowHardwareFailure("Não foi possível comparar os inventários", error);
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
            ShowHardwareFailure("Não foi possível abrir a localização do relatório de hardware", error);
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
        var idle = !hardwareBusy && !busy && !eventBusy && !performanceBusy && !networkBusy && !graphicsBusy;
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
        text.AppendLine("Comparação observada (anterior → atual)");
        text.AppendLine($"Anterior: {Local(previous.CompletedUtc)} · ID {previous.Id:D} · {HardwareReport.StatusName(previous.Status)}");
        text.AppendLine($"Atual: {Local(current.CompletedUtc)} · ID {current.Id:D} · {HardwareReport.StatusName(current.Status)}");
        text.AppendLine();
        if (diff.Changes.Count == 0)
        {
            text.AppendLine("Nenhuma diferença observada nas categorias comparáveis.");
        }
        else
        {
            text.AppendLine($"Mudanças observadas: {diff.Changes.Count}");
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
            text.AppendLine("Categorias sem avaliação de adição/remoção");
            foreach (var category in diff.NotComparable)
                text.AppendLine($"• {HardwareReport.CategoryName(category)}");
        }

        if (diff.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Limitações da comparação");
            foreach (var limitation in diff.Limitations)
                text.AppendLine($"• {limitation}");
        }

        text.AppendLine();
        text.AppendLine("Interpretação");
        text.AppendLine("• O relatório descreve diferenças observadas; não atribui causa, gravidade ou diagnóstico.");
        text.AppendLine("• Alteração de driver ou firmware não indica melhoria, regressão ou falha.");
        text.AppendLine("• Categoria não comparável não autoriza afirmar que um dispositivo foi removido.");
        return text.ToString();
    }

    private void ShowWarning(string text)
    {
        StorageWarnings.Text = text;
        WarningBanner.Visibility = Visibility.Visible;
    }

    private void ShowFailure(string message, Exception error)
    {
        App.LogFailure(error);
        ExecutionStatus.Text = message + ".";
        ShowWarning($"{message}. {error.GetType().Name}: {error.Message}");
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
                text.AppendLine($"{name}: {value ?? "Indisponível"}");
        }
        else
        {
            text.AppendLine("Dados indisponíveis.");
        }

        text.AppendLine($"\nFonte: {result.Source}\nColetor: {result.CollectorVersion}\nColetado: {Local(result.CompletedUtc)}\nDuração: {result.Duration.TotalSeconds:F2} s");
        if (result.Limitations.Count > 0)
            text.AppendLine("\nLimitações:\n" + string.Join("\n", result.Limitations.Select(x => "• " + x)));
        if (result.CollectorId == "storage")
            text.AppendLine("\nOK/Healthy é o estado reportado pelo Windows e não garante integridade física.");
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

    private static string Status(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Pending => "Pendente",
        DiagnosticStatus.Running => "Em andamento",
        DiagnosticStatus.Completed => "Concluída",
        DiagnosticStatus.Partial => "Parcial",
        DiagnosticStatus.Failed => "Falhou",
        DiagnosticStatus.Cancelled => "Cancelada",
        _ => status.ToString()
    };

    private static string FreshnessText(FreshnessState state) => state switch
    {
        FreshnessState.None => "Nenhuma auditoria",
        FreshnessState.Current => "Atualizada",
        FreshnessState.Expiring => "Próxima do vencimento",
        FreshnessState.Outdated => "Desatualizada",
        FreshnessState.Unsuccessful => "Parcial, falha ou cancelada",
        FreshnessState.InvalidDate => "Data futura inválida",
        _ => state.ToString()
    };

    private sealed record CollectorRow(CollectorResult Result)
    {
        public string Name => CollectorName(Result.CollectorId);
        public string Status => MainWindow.Status(Result.Status);
        public override string ToString() => $"{Name}: {Status}";
    }

    private sealed record HistoryRow(AuditRecord Audit)
    {
        public string Label => $"{Audit.CompletedUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}  ·  {Status(Audit.Status)}";
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
