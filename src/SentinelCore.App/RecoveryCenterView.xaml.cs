using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.App.Localization;

namespace SentinelCore.App;

/// <summary>Read-only history UI; the optional recovery flow accepts synthetic targets only.</summary>
public partial class RecoveryCenterView : UserControl
{
    private readonly LocalizedMessages recoveryMessages = new();

    private RecoveryCenter? center;
    private RecoveryCenterSnapshot? snapshot;
    private CancellationTokenSource? readCancellation;
    private bool initialized;
    private bool updating;
    private bool busy;
    private bool loaded;
    private DateTime loadedAt;
    private bool operationBlocked;
    private bool closeRequested;
    private readonly ProductOperationCoordinator standaloneOperations = new();
    internal Func<IDisposable?>? BeginOperation { get; set; }
    internal event Action? BecameIdle;
    internal void CancelRead()
    {
        closeRequested = true;
        readCancellation?.Cancel();
    }
    internal void SetOperationBlocked(bool blocked)
    {
        operationBlocked = blocked;
        UpdateButtons();
    }
    internal bool IsBusy => busy;
    internal event Action<Guid>? AuditRequested;
    internal void InvalidateAuditHistory() => loaded = false;
    internal event Action? TweakRecoveryAttempted;
    // Typing in the search box re-filters after a short pause instead of rebinding the grid on every key.
    private readonly System.Windows.Threading.DispatcherTimer searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public RecoveryCenterView()
    {
        InitializeComponent();
        BindEnum<RecoveryModule>(ModuleCombo, RecoveryCenterPresentation.Label);
        BindEnum<RecoveryOperationOutcome>(OutcomeCombo, RecoveryCenterPresentation.Label);
        BindEnum<RecoveryOrigin>(OriginCombo, RecoveryCenterPresentation.Label);
        BindEnum<RecoveryCapability>(CapabilityCombo, RecoveryCenterPresentation.Label);
        BindEnum<RiskLevel>(RiskCombo, RiskLabel);
        BindBoolean(AvailableCombo, "Potencialmente disponível", "Indisponível ou bloqueada");
        BindBoolean(ConflictCombo, "Com conflito", "Sem conflito registrado");
        BindBoolean(InterventionCombo, "Intervenção necessária", "Sem indicação de intervenção");
        BindText(TypeCombo, []);
        BindText(StateCombo, []);
        searchDebounce.Tick += (_, _) =>
        {
            searchDebounce.Stop();
            if (!busy && !updating) ApplyFilter((HistoryGrid.SelectedItem as HistoryRow)?.Operation);
        };
        initialized = true;
        UpdateButtons();
    }

    internal void RefreshLanguage()
    {
        updating = true;
        try
        {
            foreach (var combo in new[] { ModuleCombo, OutcomeCombo, OriginCombo, CapabilityCombo,
                         TypeCombo, StateCombo, RiskCombo, AvailableCombo, ConflictCombo, InterventionCombo })
                foreach (var choice in combo.Items.OfType<ILocalizedChoice>()) choice.RefreshLanguage();
            HistoryGrid.Items.Refresh();
            UpdateSummary();
            ShowSelection(preserveConfirmation: true);
            UpdateModeText();
            recoveryMessages.Refresh();
        }
        finally { updating = false; }
    }

    private void UpdateModeText()
    {
        if (center is null) return;
        ModeText.Text = center.SyntheticRecoveryAvailable
            ? Loc.T("HOST SINTÉTICO — recuperação apenas de alvos Synthetic")
            : center.RealRecoveryAvailable ? Loc.T("Recuperação controlada — HKCU do usuário atual")
                : Loc.T("Consulta local — execução real bloqueada");
        RecoveryPolicyText.Text = center.SyntheticRecoveryAvailable
            ? Loc.T("Fluxo sintético: o RecoveryService valida backup e estado atual antes de recuperar. Conflitos não serão sobrescritos.")
            : center.RealRecoveryAvailable
                ? Loc.T("Analise os alvos antes de confirmar. O serviço revalida o backup e preserva valores em conflito.")
                : Loc.T("Rollback desabilitado na distribuição. Os detalhes mostram a evidência registrada.");
    }

    public void Initialize(RecoveryCenter recoveryCenter, bool loadIfVisible = true)
    {
        ArgumentNullException.ThrowIfNull(recoveryCenter);
        if (center is not null) throw new InvalidOperationException("Central de Recuperação já composta.");
        center = recoveryCenter;
        UpdateModeText();
        UpdateButtons();
        if (loadIfVisible && IsLoaded) _ = RefreshAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (center is not null && !loaded && !busy && IsEnabled) await RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // A search typed less than 250 ms before leaving the page must not leave the grid on the previous filter.
        if (searchDebounce.IsEnabled) { searchDebounce.Stop(); if (snapshot is not null && !busy) ApplyFilter((HistoryGrid.SelectedItem as HistoryRow)?.Operation); }
        readCancellation?.Cancel();
    }
    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    public Task RefreshAsync() => RefreshAsync(null);

    internal async Task<bool> OpenTweakAsync(Guid id)
    {
        if (busy) return false;
        OnClearFilters(this, new RoutedEventArgs());
        await RefreshAsync(id);
        if (HistoryGrid.SelectedItem is not HistoryRow { Operation.Id: var selected } || selected != id)
            return false;
        HistoryGrid.ScrollIntoView(HistoryGrid.SelectedItem);
        return true;
    }

    private async Task RefreshAsync(Guid? tweakId, bool ownsOperation = false)
    {
        if (center is null || busy || closeRequested) return;
        using var productLease = ownsOperation ? null
            : BeginOperation is null ? standaloneOperations.Acquire() : BeginOperation();
        if (!ownsOperation && productLease is null) return;
        var selected = (HistoryGrid.SelectedItem as HistoryRow)?.Operation;
        busy = true;
        readCancellation = new CancellationTokenSource();
        var interrupted = false;
        recoveryMessages.Set(nameof(StatusText), () => StatusText.Text = Loc.T("Lendo as fontes duráveis de histórico…"));
        UpdateButtons();
        try
        {
            // Mapping and deduplication of large histories run off the UI thread; only the binding below uses the dispatcher.
            var token = readCancellation.Token;
            snapshot = await Task.Run(() => center.LoadAsync(token), token);
            loaded = true;
            loadedAt = DateTime.Now;
            updating = true;
            BindText(TypeCombo, snapshot.Operations.Select(o => o.Type));
            BindText(StateCombo, snapshot.Operations.Select(o => o.State), UiText.Recorded);
            updating = false;
            UpdateSummary();
            ApplyFilter(tweakId is { } id
                ? snapshot.Operations.SingleOrDefault(operation => operation.Id == id && operation.Module == RecoveryModule.Tweaks)
                : selected);
            recoveryMessages.Set(nameof(StatusText), () => StatusText.Text = snapshot.IsComplete
                ? Loc.F($"Histórico carregado às {loadedAt:HH:mm:ss} (horário local). Nenhuma operação de manutenção foi iniciada.")
                : Loc.F($"Leitura incompleta às {loadedAt:HH:mm:ss} (horário local): examine os avisos. As contagens cobrem somente os registros válidos carregados."));
        }
        catch (OperationCanceledException)
        {
            interrupted = readCancellation.IsCancellationRequested;
            InvalidateHistory(() => Loc.T("Leitura interrompida. O histórico não foi determinado."));
        }
        catch (Exception error)
        {
            InvalidateHistory(() => Loc.T("Não foi possível carregar os históricos: ") + error.Message);
        }
        finally
        {
            readCancellation.Dispose();
            readCancellation = null;
            updating = false;
            busy = false;
            UpdateButtons();
            BecameIdle?.Invoke();
            // Loaded can run before the previous read has observed Unloaded's cancellation.
            if (interrupted && IsLoaded && !closeRequested)
                _ = Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync()));
            closeRequested = false;
        }
    }

    private void InvalidateHistory(Func<string> message)
    {
        snapshot = null;
        loaded = false;
        HistoryGrid.ItemsSource = null;
        AuditInterruptionsList.ItemsSource = null;
        AuditInterruptionsPanel.Visibility = Visibility.Collapsed;
        SummaryCards.ItemsSource = null;
        SummaryText.Text = Loc.T("Históricos indisponíveis. Contagens não determinadas.");
        recoveryMessages.Set(nameof(HistorySummary), () => HistorySummary.Text = Loc.T("Leitura não concluída; não representa ausência de operações."));
        WarningsPanel.Visibility = Visibility.Visible;
        recoveryMessages.Set(nameof(WarningsText), () => WarningsText.Text = message());
        recoveryMessages.Set(nameof(StatusText), () => StatusText.Text = message());
        ShowSelection();
    }

    private void UpdateSummary()
    {
        if (snapshot is null) return;
        var summary = RecoveryCenter.Summarize(snapshot);
        var detail = Loc.T(summary.IsComplete ? "nos históricos carregados" : "só registros legíveis");
        string Count(int value) => !summary.IsComplete && value == 0 ? Loc.T("Não determinado") : value.ToString(CultureInfo.CurrentCulture);
        SummaryCards.ItemsSource = new[]
        {
            new SummaryItem("Operações registradas", Count(summary.Registered), detail),
            new SummaryItem("Concluídas", Count(summary.Completed), detail),
            new SummaryItem("Falhas", Count(summary.Failed), detail),
            new SummaryItem("Indeterminadas", Count(summary.Indeterminate), detail),
            new SummaryItem("Interrompidas", Count(snapshot.Operations.Count(operation => operation.Interrupted)), detail),
            new SummaryItem("Aplicação parcial", Count(snapshot.Operations.Count(operation => operation.PartiallyApplied)), detail),
            new SummaryItem("Conflitos", Count(summary.Conflicts), detail),
            new SummaryItem("Intervenção necessária", Count(summary.Intervention), detail),
            new SummaryItem("Recuperações realizadas",
                Count(summary.RecoveriesCompleted + summary.RecoveriesPartial + summary.RecoveriesBlocked + summary.RecoveriesFailed),
                summary.IsComplete || summary.RecoveriesCompleted + summary.RecoveriesPartial + summary.RecoveriesBlocked + summary.RecoveriesFailed > 0
                    ? Loc.F($"Integral: {summary.RecoveriesCompleted}; parcial: {summary.RecoveriesPartial}; bloqueada: {summary.RecoveriesBlocked}; falha: {summary.RecoveriesFailed}. {detail}.")
                    : Loc.T("Distribuição não determinada; fontes incompletas."))
        };
        SummaryText.Text = RecoveryCenterPresentation.Summary(snapshot, Loc.T);
        if ((snapshot.Audits ?? snapshot.InterruptedAudits) is { } audits)
        {
            AuditInterruptionsPanel.Visibility = Visibility.Visible;
            AuditInterruptionsList.ItemsSource = audits.Audits.Select(audit => new InterruptedAuditRow(audit)).ToArray();
            AuditInterruptionsSummary.Text = audits.Warnings.Count > 0
                ? string.Join(Environment.NewLine, audits.Warnings)
                : audits.Audits.Count == 0 ? Loc.T("Nenhuma auditoria encontrada nos registros carregados.")
                : Loc.F($"{audits.Audits.Count} auditoria(s) carregada(s), incluindo ") +
                  Loc.F($"{audits.Audits.Count(audit => audit.Status == DiagnosticStatus.Interrupted)} interrompida(s). Abra a origem para consultar as evidências.");
        }
        var warnings = snapshot.Sources.SelectMany(source => source.Warnings.Select(w => Loc.F($"{Loc.T(RecoveryCenterPresentation.Label(source.Module))}: {w}")))
            .Concat(snapshot.Warnings)
            .Concat((snapshot.Audits ?? snapshot.InterruptedAudits)?.Warnings ?? [])
            .Concat(snapshot.Sources.Where(s => s.Status != RecoveryEvidenceStatus.Complete)
                .Select(s => Loc.F($"{Loc.T(RecoveryCenterPresentation.Label(s.Module))}: fonte {s.Status}; contagem total indisponível.")))
            .Distinct().ToArray();
        recoveryMessages.Set(nameof(WarningsText), () => WarningsText.Text = string.Join(Environment.NewLine, warnings));
        WarningsPanel.Visibility = !snapshot.IsComplete || warnings.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenInterruptedAudit(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InterruptedAuditRow row }) AuditRequested?.Invoke(row.Audit.Id);
    }

    private sealed record InterruptedAuditRow(AuditRecord Audit)
    {
        public string Label => Loc.F($"Abrir auditoria · {Audit.CompletedUtc.ToLocalTime():dd/MM/yyyy HH:mm} · ") +
            Loc.F($"{Loc.T(Audit.Kind == AuditKind.Basic ? "Básica" : "Completa")} · {UiText.Diagnostic(Audit.Status)} · {Audit.Id:D}");
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized || updating || busy) return;
        if (ReferenceEquals(sender, SearchText)) { searchDebounce.Stop(); searchDebounce.Start(); return; }
        searchDebounce.Stop();
        ApplyFilter((HistoryGrid.SelectedItem as HistoryRow)?.Operation);
    }

    private void ApplyFilter(RecoveryOperation? selected = null)
    {
        if (snapshot is null) return;
        var from = FromDate.SelectedDate;
        var to = ToDate.SelectedDate;
        if ((from is null && !string.IsNullOrWhiteSpace(FromDate.Text)) ||
            (to is null && !string.IsNullOrWhiteSpace(ToDate.Text)) || from > to)
        {
            HistoryGrid.ItemsSource = Array.Empty<HistoryRow>();
            recoveryMessages.Set(nameof(HistorySummary), () => HistorySummary.Text = Loc.T("Datas inválidas. Informe um intervalo local válido."));
            ShowSelection();
            return;
        }
        var filter = new RecoveryCenterFilter(Search: SearchText.Text.Trim(),
            Module: Value<RecoveryModule>(ModuleCombo), Type: (TypeCombo.SelectedItem as LocalizedChoice<string?>)?.Value,
            FromUtc: from is { } start ? DateBoundaryUtc(start, false) : null,
            ToUtc: to is { } end ? DateBoundaryUtc(end, true) : null,
            State: (StateCombo.SelectedItem as LocalizedChoice<string?>)?.Value, Risk: Value<RiskLevel>(RiskCombo),
            Outcome: Value<RecoveryOperationOutcome>(OutcomeCombo), Origin: Value<RecoveryOrigin>(OriginCombo),
            Capability: Value<RecoveryCapability>(CapabilityCombo), RecoveryAvailable: Value<bool>(AvailableCombo),
            Conflict: Value<bool>(ConflictCombo), Intervention: Value<bool>(InterventionCombo));
        var rows = RecoveryCenter.Filter(snapshot, filter).Select(o => new HistoryRow(o)).ToArray();
        updating = true;
        HistoryGrid.ItemsSource = rows;
        HistoryGrid.SelectedItem = rows.FirstOrDefault(row => selected is not null &&
            row.Operation.Id == selected.Id && row.Operation.Module == selected.Module);
        updating = false;
        recoveryMessages.Set(nameof(HistorySummary), () => HistorySummary.Text = rows.Length == 0
            ? snapshot.IsComplete && snapshot.Operations.Count == 0
                ? Loc.T("Nenhuma operação registrada nas fontes carregadas.")
                : Loc.T("Nenhum registro válido carregado corresponde aos filtros; fontes inacessíveis não são contadas como vazias.")
            : Loc.F($"{rows.Length} de {snapshot.Operations.Count} registro(s) carregado(s). Horários no fuso local. Carga às {loadedAt:HH:mm:ss}."));
        ShowSelection();
    }

    private void OnClearFilters(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        searchDebounce.Stop();
        updating = true;
        SearchText.Clear();
        foreach (var combo in new[] { ModuleCombo, OutcomeCombo, OriginCombo, CapabilityCombo,
                     TypeCombo, StateCombo, RiskCombo, AvailableCombo, ConflictCombo, InterventionCombo })
            combo.SelectedIndex = 0;
        FromDate.SelectedDate = null;
        FromDate.Text = string.Empty;
        ToDate.SelectedDate = null;
        ToDate.Text = string.Empty;
        updating = false;
        ApplyFilter();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && !updating) ShowSelection();
    }

    private void ShowSelection(bool preserveConfirmation = false)
    {
        var operation = (HistoryGrid.SelectedItem as HistoryRow)?.Operation;
        if (!preserveConfirmation) ConfirmationText.Clear();
        DetailsTitle.Text = operation is null ? Loc.T("Detalhes da operação") : Loc.F($"{operation.OperationId} · {Loc.T(RecoveryCenterPresentation.Label(operation.Origin))}");
        DetailsText.Text = operation is null ? Loc.T("Selecione uma operação do histórico.") : RecoveryCenterPresentation.Details(operation, Loc.T);
        TimelineText.Text = operation is null ? Loc.T("Nenhum evento selecionado.") : RecoveryCenterPresentation.Timeline(operation, Loc.T);
        ReportPathText.Text = operation?.ReportPath ?? string.Empty;
        ConfirmationHint.Text = operation is { Preview.CanRecover: true, Capability: RecoveryCapability.PotentiallyAvailable } &&
            center?.CanAnalyze(operation) == true
            ? Loc.F($"Digite exatamente: {TweakManager.BuildRecoveryConfirmationText(operation.Id)}")
            : Loc.T("Selecione uma operação elegível e analise a recuperação.");
        UpdateButtons();
    }

    private void OnConfirmationChanged(object sender, RoutedEventArgs e)
    {
        if (initialized) UpdateButtons();
    }

    private void UpdateButtons()
    {
        var operation = (HistoryGrid.SelectedItem as HistoryRow)?.Operation;
        var supported = center?.CanAnalyze(operation) == true;
        var canRecover = supported && operation is
            { Capability: RecoveryCapability.PotentiallyAvailable, Preview.CanRecover: true };
        RefreshButton.IsEnabled = center is not null && !busy && !operationBlocked;
        HistoryGrid.IsEnabled = !busy && !operationBlocked;
        ClearFiltersButton.IsEnabled = !busy && !operationBlocked;
        AnalyzeButton.IsEnabled = !busy && !operationBlocked && supported;
        ConfirmationText.IsEnabled = !busy && !operationBlocked && canRecover;
        RecoverButton.IsEnabled = !busy && !operationBlocked && canRecover && operation is not null &&
            string.Equals(ConfirmationText.Text, TweakManager.BuildRecoveryConfirmationText(operation.Id), StringComparison.Ordinal);
    }

    private async void OnAnalyze(object sender, RoutedEventArgs e)
    {
        var operation = (HistoryGrid.SelectedItem as HistoryRow)?.Operation;
        if (busy || center is null || snapshot is null || !center.CanAnalyze(operation)) return;
        using var productLease = BeginOperation is null ? standaloneOperations.Acquire() : BeginOperation();
        if (productLease is null) return;
        busy = true;
        var pending = operation! with { Preview = null, Capability = RecoveryCapability.Blocked };
        snapshot = snapshot with { Operations = snapshot.Operations.Select(o =>
            o.Id == pending.Id && o.Module == pending.Module ? pending : o).ToArray() };
        ConfirmationText.Clear();
        UpdateSummary();
        ApplyFilter(pending);
        recoveryMessages.Set(nameof(StatusText), () => StatusText.Text = Loc.T("Analisando backup e estado atual dos alvos…"));
        UpdateButtons();
        try
        {
            var analyzed = await center.AnalyzeAsync(operation!);
            snapshot = snapshot with { Operations = snapshot.Operations.Select(o => o.Id == analyzed.Id && o.Module == analyzed.Module ? analyzed : o).ToArray() };
            UpdateSummary();
            ApplyFilter(analyzed);
            recoveryMessages.Set(nameof(StatusText), () => StatusText.Text = analyzed.Preview?.Reason ?? Loc.T("Prévia concluída. Confira os alvos e as condições de recuperação."));
        }
        catch (Exception error)
        {
            var failed = pending with { Limitations = pending.Limitations.Append(
                Loc.T("Análise atual indisponível; a prévia anterior foi invalidada: ") + error.Message).Distinct().ToArray() };
            snapshot = snapshot with { Operations = snapshot.Operations.Select(o =>
                o.Id == failed.Id && o.Module == failed.Module ? failed : o).ToArray() };
            UpdateSummary();
            ApplyFilter(failed);
            recoveryMessages.Set(nameof(StatusText), () => StatusText.Text = Loc.T("Análise indisponível; recuperação bloqueada: ") + error.Message);
        }
        finally { busy = false; closeRequested = false; UpdateButtons(); BecameIdle?.Invoke(); }
    }

    private async void OnRecover(object sender, RoutedEventArgs e)
    {
        var operation = (HistoryGrid.SelectedItem as HistoryRow)?.Operation;
        if (busy || center is null || !center.CanAnalyze(operation) || !RecoverButton.IsEnabled) return;
        using var productLease = BeginOperation is null ? standaloneOperations.Acquire() : BeginOperation();
        if (productLease is null) return;
        var confirmation = ConfirmationText.Text;
        busy = true;
        recoveryMessages.Set(nameof(StatusText), () => StatusText.Text = Loc.T("Recuperação em andamento. Aguarde a verificação dos alvos…"));
        UpdateButtons();
        Func<string> resultText;
        try
        {
            var result = await center.RecoverAsync(operation!, confirmation);
            resultText = () => Loc.F($"Recuperação: {UiText.Outcome(result.Outcome)}. Integral: {Loc.T(result.FullyRecovered ? "sim" : "não")}. {result.Error}");
        }
        catch (Exception error)
        {
            resultText = () => Loc.T("Recuperação não confirmada; consulte histórico e limitações: ") + error.Message;
        }
        finally
        {
            busy = false;
            TweakRecoveryAttempted?.Invoke();
            UpdateButtons();
            BecameIdle?.Invoke();
        }
        if (closeRequested) { closeRequested = false; return; }
        // Keep the recovery reservation until the authoritative history has also been reloaded.
        await RefreshAsync(null, ownsOperation: true);
        recoveryMessages.Set(nameof(StatusText), () => StatusText.Text = resultText());
    }

    private static void BindEnum<T>(ComboBox combo, Func<T, string> label) where T : struct, Enum
    {
        combo.DisplayMemberPath = "Label";
        combo.ItemsSource = new[] { new LocalizedChoice<T?>(null, "Todos") }
            .Concat(Enum.GetValues<T>().Select(value => new LocalizedChoice<T?>(value, () => label(value)))).ToArray();
        combo.SelectedIndex = 0;
    }

    private static void BindBoolean(ComboBox combo, string yes, string no)
    {
        combo.DisplayMemberPath = "Label";
        combo.ItemsSource = new[] { new LocalizedChoice<bool?>(null, "Todos"), new LocalizedChoice<bool?>(true, yes), new LocalizedChoice<bool?>(false, no) };
        combo.SelectedIndex = 0;
    }

    private static void BindText(ComboBox combo, IEnumerable<string> values, Func<string, string>? label = null)
    {
        combo.DisplayMemberPath = "Label";
        var selected = (combo.SelectedItem as LocalizedChoice<string?>)?.Value;
        var choices = new[] { new LocalizedChoice<string?>(null, "Todos") }.Concat(values.Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.CurrentCulture).Select(s => new LocalizedChoice<string?>(s, () => label?.Invoke(s) ?? s))).ToArray();
        combo.ItemsSource = choices;
        combo.SelectedItem = choices.FirstOrDefault(c => c.Value == selected) ?? choices[0];
    }

    private static T? Value<T>(ComboBox combo) where T : struct => (combo.SelectedItem as LocalizedChoice<T?>)?.Value;
    private static DateTimeOffset DateBoundaryUtc(DateTime date, bool endOfDay)
    {
        var boundary = endOfDay ? date.Date.AddTicks(TimeSpan.TicksPerDay - 1) : date.Date;
        return new DateTimeOffset(DateTime.SpecifyKind(boundary, DateTimeKind.Local).ToUniversalTime());
    }
    private static string RiskLabel(RiskLevel risk) => risk switch
    {
        RiskLevel.Low => "Baixo", RiskLevel.Medium => "Médio", RiskLevel.High => "Alto",
        RiskLevel.NotValidated => "Não validado", _ => "Desconhecido"
    };

    private sealed record SummaryItem(string PortugueseTitle, string Value, string PortugueseDetail)
    {
        public string Title => Loc.T(PortugueseTitle);
        public string Detail => Loc.T(PortugueseDetail);
    }
    private sealed record HistoryRow(RecoveryOperation Operation)
    {
        public string LocalDate => Operation.AtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        public string Module => Loc.T(RecoveryCenterPresentation.Label(Operation.Module));
        public string Origin => Loc.T(RecoveryCenterPresentation.Label(Operation.Origin));
        public string Outcome => Loc.T(RecoveryCenterPresentation.Label(Operation.Outcome));
        public string Capability => Loc.T(RecoveryCenterPresentation.Label(Operation.Capability));
        public string StateText => UiText.Recorded(Operation.State);
    }
}
