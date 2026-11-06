using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SentinelCore.Application;
using SentinelCore.Domain;

namespace SentinelCore.App;

/// <summary>Read-only history UI; the optional recovery flow accepts synthetic targets only.</summary>
public partial class RecoveryCenterView : UserControl
{
    private RecoveryCenter? center;
    private RecoveryCenterSnapshot? snapshot;
    private CancellationTokenSource? readCancellation;
    private bool initialized;
    private bool updating;
    private bool busy;
    private bool loaded;
    private DateTime loadedAt;
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

    public void Initialize(RecoveryCenter recoveryCenter)
    {
        ArgumentNullException.ThrowIfNull(recoveryCenter);
        if (center is not null) throw new InvalidOperationException("Central de Recuperação já composta.");
        center = recoveryCenter;
        ModeText.Text = center.SyntheticRecoveryAvailable
            ? "HOST SINTÉTICO — recuperação apenas de alvos Synthetic"
            : "Consulta local — execução real bloqueada";
        RecoveryPolicyText.Text = center.SyntheticRecoveryAvailable
            ? "Fluxo sintético: o RecoveryService valida backup e estado atual antes de recuperar. Conflitos não serão sobrescritos."
            : "Execução bloqueada nesta composição. A central não consulta o estado atual do Registro nem executa rollback real.";
        UpdateButtons();
        if (IsLoaded) _ = RefreshAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (center is not null && !loaded && !busy) await RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // A search typed less than 250 ms before leaving the page must not leave the grid on the previous filter.
        if (searchDebounce.IsEnabled) { searchDebounce.Stop(); if (snapshot is not null && !busy) ApplyFilter((HistoryGrid.SelectedItem as HistoryRow)?.Operation); }
        readCancellation?.Cancel();
    }
    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    public async Task RefreshAsync()
    {
        if (center is null || busy) return;
        var selected = (HistoryGrid.SelectedItem as HistoryRow)?.Operation;
        busy = true;
        readCancellation = new CancellationTokenSource();
        var interrupted = false;
        StatusText.Text = "Lendo as fontes duráveis de histórico…";
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
            ApplyFilter(selected);
            StatusText.Text = snapshot.IsComplete
                ? $"Histórico carregado às {DateTime.Now:HH:mm:ss} (horário local). Nenhuma operação de manutenção foi iniciada."
                : $"Leitura incompleta às {DateTime.Now:HH:mm:ss} (horário local): examine os avisos. As contagens cobrem somente os registros válidos carregados.";
        }
        catch (OperationCanceledException)
        {
            interrupted = readCancellation.IsCancellationRequested;
            InvalidateHistory("Leitura interrompida. O histórico não foi determinado.");
        }
        catch (Exception error)
        {
            InvalidateHistory("Não foi possível carregar os históricos: " + error.Message);
        }
        finally
        {
            readCancellation.Dispose();
            readCancellation = null;
            updating = false;
            busy = false;
            UpdateButtons();
            // Loaded can run before the previous read has observed Unloaded's cancellation.
            if (interrupted && IsLoaded) _ = RefreshAsync();
        }
    }

    private void InvalidateHistory(string message)
    {
        snapshot = null;
        loaded = false;
        HistoryGrid.ItemsSource = null;
        SummaryCards.ItemsSource = null;
        SummaryText.Text = "Históricos indisponíveis. Contagens não determinadas.";
        HistorySummary.Text = "Leitura não concluída; não representa ausência de operações.";
        WarningsPanel.Visibility = Visibility.Visible;
        WarningsText.Text = message;
        StatusText.Text = message;
        ShowSelection();
    }

    private void UpdateSummary()
    {
        if (snapshot is null) return;
        var summary = RecoveryCenter.Summarize(snapshot);
        var detail = summary.IsComplete ? "nos históricos carregados" : "só registros legíveis";
        string Count(int value) => !summary.IsComplete && value == 0 ? "Não determinado" : value.ToString(CultureInfo.CurrentCulture);
        SummaryCards.ItemsSource = new[]
        {
            new SummaryItem("Operações registradas", Count(summary.Registered), detail),
            new SummaryItem("Concluídas", Count(summary.Completed), detail),
            new SummaryItem("Falhas", Count(summary.Failed), detail),
            new SummaryItem("Indeterminadas", Count(summary.Indeterminate), detail),
            new SummaryItem("Conflitos", Count(summary.Conflicts), detail),
            new SummaryItem("Intervenção necessária", Count(summary.Intervention), detail),
            new SummaryItem("Recuperações realizadas",
                Count(summary.RecoveriesCompleted + summary.RecoveriesPartial + summary.RecoveriesBlocked + summary.RecoveriesFailed),
                summary.IsComplete || summary.RecoveriesCompleted + summary.RecoveriesPartial + summary.RecoveriesBlocked + summary.RecoveriesFailed > 0
                    ? $"Integral: {summary.RecoveriesCompleted}; parcial: {summary.RecoveriesPartial}; bloqueada: {summary.RecoveriesBlocked}; falha: {summary.RecoveriesFailed}. {detail}."
                    : "Distribuição não determinada; fontes incompletas.")
        };
        SummaryText.Text = RecoveryCenterPresentation.Summary(snapshot);
        var warnings = snapshot.Sources.SelectMany(source => source.Warnings.Select(w => $"{RecoveryCenterPresentation.Label(source.Module)}: {w}"))
            .Concat(snapshot.Warnings)
            .Concat(snapshot.Sources.Where(s => s.Status != RecoveryEvidenceStatus.Complete)
                .Select(s => $"{RecoveryCenterPresentation.Label(s.Module)}: fonte {s.Status}; contagem total indisponível."))
            .Distinct().ToArray();
        WarningsText.Text = string.Join(Environment.NewLine, warnings);
        WarningsPanel.Visibility = !snapshot.IsComplete || warnings.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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
            HistorySummary.Text = "Datas inválidas. Informe um intervalo local válido.";
            ShowSelection();
            return;
        }
        var filter = new RecoveryCenterFilter(Search: SearchText.Text.Trim(),
            Module: Value<RecoveryModule>(ModuleCombo), Type: (TypeCombo.SelectedItem as TextChoice)?.Value,
            FromUtc: from is { } start ? DateBoundaryUtc(start, false) : null,
            ToUtc: to is { } end ? DateBoundaryUtc(end, true) : null,
            State: (StateCombo.SelectedItem as TextChoice)?.Value, Risk: Value<RiskLevel>(RiskCombo),
            Outcome: Value<RecoveryOperationOutcome>(OutcomeCombo), Origin: Value<RecoveryOrigin>(OriginCombo),
            Capability: Value<RecoveryCapability>(CapabilityCombo), RecoveryAvailable: Value<bool>(AvailableCombo),
            Conflict: Value<bool>(ConflictCombo), Intervention: Value<bool>(InterventionCombo));
        var rows = RecoveryCenter.Filter(snapshot, filter).Select(o => new HistoryRow(o)).ToArray();
        updating = true;
        HistoryGrid.ItemsSource = rows;
        HistoryGrid.SelectedItem = rows.FirstOrDefault(row => selected is not null &&
            row.Operation.Id == selected.Id && row.Operation.Module == selected.Module);
        updating = false;
        HistorySummary.Text = rows.Length == 0
            ? snapshot.IsComplete && snapshot.Operations.Count == 0
                ? "Nenhuma operação registrada nas fontes carregadas."
                : "Nenhum registro válido carregado corresponde aos filtros; fontes inacessíveis não são contadas como vazias."
            : $"{rows.Length} de {snapshot.Operations.Count} registro(s) carregado(s). Horários no fuso local. Carga às {loadedAt:HH:mm:ss}.";
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

    private void ShowSelection()
    {
        var operation = (HistoryGrid.SelectedItem as HistoryRow)?.Operation;
        ConfirmationText.Clear();
        DetailsTitle.Text = operation is null ? "Detalhes da operação" : $"{operation.OperationId} · {RecoveryCenterPresentation.Label(operation.Origin)}";
        DetailsText.Text = operation is null ? "Selecione uma operação do histórico." : RecoveryCenterPresentation.Details(operation);
        TimelineText.Text = operation is null ? "Nenhum evento selecionado." : RecoveryCenterPresentation.Timeline(operation);
        ReportPathText.Text = operation?.ReportPath ?? string.Empty;
        ConfirmationHint.Text = operation is null ? "Selecione um alvo sintético suportado para analisar a recuperação."
            : $"Confirmação específica, somente no host sintético: {TweakManager.BuildRecoveryConfirmationText(operation.Id)}";
        UpdateButtons();
    }

    private void OnConfirmationChanged(object sender, RoutedEventArgs e)
    {
        if (initialized) UpdateButtons();
    }

    private bool IsSyntheticTweak(RecoveryOperation? operation) => center?.SyntheticRecoveryAvailable == true &&
        operation is { Module: RecoveryModule.Tweaks, Origin: RecoveryOrigin.Synthetic };

    private void UpdateButtons()
    {
        var operation = (HistoryGrid.SelectedItem as HistoryRow)?.Operation;
        var supported = IsSyntheticTweak(operation);
        var canRecover = supported && operation is
            { Capability: RecoveryCapability.PotentiallyAvailable, Preview.CanRecover: true };
        RefreshButton.IsEnabled = center is not null && !busy;
        HistoryGrid.IsEnabled = !busy;
        ClearFiltersButton.IsEnabled = !busy;
        AnalyzeButton.IsEnabled = !busy && supported;
        ConfirmationText.IsEnabled = !busy && canRecover;
        RecoverButton.IsEnabled = !busy && canRecover && operation is not null &&
            string.Equals(ConfirmationText.Text, TweakManager.BuildRecoveryConfirmationText(operation.Id), StringComparison.Ordinal);
    }

    private async void OnAnalyze(object sender, RoutedEventArgs e)
    {
        var operation = (HistoryGrid.SelectedItem as HistoryRow)?.Operation;
        if (busy || center is null || snapshot is null || !IsSyntheticTweak(operation)) return;
        busy = true;
        var pending = operation! with { Preview = null, Capability = RecoveryCapability.Blocked };
        snapshot = snapshot with { Operations = snapshot.Operations.Select(o =>
            o.Id == pending.Id && o.Module == pending.Module ? pending : o).ToArray() };
        ConfirmationText.Clear();
        UpdateSummary();
        ApplyFilter(pending);
        StatusText.Text = "Analisando backup e estado do alvo sintético…";
        UpdateButtons();
        try
        {
            var analyzed = await center.AnalyzeAsync(operation!);
            snapshot = snapshot with { Operations = snapshot.Operations.Select(o => o.Id == analyzed.Id && o.Module == analyzed.Module ? analyzed : o).ToArray() };
            UpdateSummary();
            ApplyFilter(analyzed);
            StatusText.Text = analyzed.Preview?.Reason ?? "Prévia sintética concluída. Consulte conflitos, limitações e condições de recuperação.";
        }
        catch (Exception error)
        {
            var failed = pending with { Limitations = pending.Limitations.Append(
                "Análise atual indisponível; a prévia anterior foi invalidada: " + error.Message).Distinct().ToArray() };
            snapshot = snapshot with { Operations = snapshot.Operations.Select(o =>
                o.Id == failed.Id && o.Module == failed.Module ? failed : o).ToArray() };
            UpdateSummary();
            ApplyFilter(failed);
            StatusText.Text = "Análise indisponível; recuperação bloqueada: " + error.Message;
        }
        finally { busy = false; UpdateButtons(); }
    }

    private async void OnRecover(object sender, RoutedEventArgs e)
    {
        var operation = (HistoryGrid.SelectedItem as HistoryRow)?.Operation;
        if (busy || center is null || !IsSyntheticTweak(operation) || !RecoverButton.IsEnabled) return;
        var confirmation = ConfirmationText.Text;
        busy = true;
        StatusText.Text = "Recuperação sintética em andamento pelo RecoveryService…";
        UpdateButtons();
        string resultText;
        try
        {
            var result = await center.RecoverAsync(operation!, confirmation);
            resultText = $"Recuperação sintética: {UiText.Outcome(result.Outcome)}. Integral: {(result.FullyRecovered ? "sim" : "não")}. {result.Error}";
        }
        catch (Exception error)
        {
            resultText = "Recuperação não confirmada; consulte histórico e limitações: " + error.Message;
        }
        finally { busy = false; UpdateButtons(); }
        await RefreshAsync();
        StatusText.Text = resultText;
    }

    private static void BindEnum<T>(ComboBox combo, Func<T, string> label) where T : struct, Enum
    {
        combo.ItemsSource = new[] { new Choice<T>(null, "Todos") }
            .Concat(Enum.GetValues<T>().Select(value => new Choice<T>(value, label(value)))).ToArray();
        combo.SelectedIndex = 0;
    }

    private static void BindBoolean(ComboBox combo, string yes, string no)
    {
        combo.ItemsSource = new[] { new Choice<bool>(null, "Todos"), new Choice<bool>(true, yes), new Choice<bool>(false, no) };
        combo.SelectedIndex = 0;
    }

    private static void BindText(ComboBox combo, IEnumerable<string> values, Func<string, string>? label = null)
    {
        var selected = (combo.SelectedItem as TextChoice)?.Value;
        var choices = new[] { new TextChoice(null, "Todos") }.Concat(values.Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.CurrentCulture).Select(s => new TextChoice(s, label?.Invoke(s) ?? s))).ToArray();
        combo.ItemsSource = choices;
        combo.SelectedItem = choices.FirstOrDefault(c => c.Value == selected) ?? choices[0];
    }

    private static T? Value<T>(ComboBox combo) where T : struct => (combo.SelectedItem as Choice<T>)?.Value;
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

    private sealed record Choice<T>(T? Value, string Label) where T : struct
    {
        public override string ToString() => Label;
    }
    private sealed record TextChoice(string? Value, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record SummaryItem(string Title, string Value, string Detail);
    private sealed record HistoryRow(RecoveryOperation Operation)
    {
        public string LocalDate => Operation.AtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        public string Module => RecoveryCenterPresentation.Label(Operation.Module);
        public string Origin => RecoveryCenterPresentation.Label(Operation.Origin);
        public string Outcome => RecoveryCenterPresentation.Label(Operation.Outcome);
        public string Capability => RecoveryCenterPresentation.Label(Operation.Capability);
        public string StateText => UiText.Recorded(Operation.State);
    }
}
