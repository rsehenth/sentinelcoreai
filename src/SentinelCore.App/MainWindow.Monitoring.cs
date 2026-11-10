using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows;
using SentinelCore.Persistence;
using SentinelCore.App.Localization;

namespace SentinelCore.App;

public partial class MainWindow
{
    private IReadOnlyList<MonitoringSession> monitoringSessions = [];
    private IReadOnlyList<MonitoringSegment> monitoringSegments = [];
    private MonitoringSession? selectedMonitoring;
    private MonitoringSession? unsavedMonitoring;
    private CancellationTokenSource? monitoringCancellation;
    private bool monitoringBusy;
    private bool monitoringSaving;
    private bool monitoringClosingAfterSave;
    private bool updatingMonitoring;
    private readonly DispatcherTimer monitoringCountdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch monitoringStopwatch = new();
    private TimeSpan monitoringDuration;

    private void InitializeMonitoring()
    {
        monitoringCountdown.Tick += OnMonitoringCountdown;
        updatingMonitoring = true;
        MonitoringPresetCombo.ItemsSource = new[]
        {
            new MonitoringPresetRow(MonitoringDurationPreset.Minutes5,
                MonitoringGuidance.DurationName(MonitoringDurationPreset.Minutes5)),
            new MonitoringPresetRow(MonitoringDurationPreset.Minutes15,
                MonitoringGuidance.DurationName(MonitoringDurationPreset.Minutes15)),
            new MonitoringPresetRow(MonitoringDurationPreset.Minutes30,
                MonitoringGuidance.DurationName(MonitoringDurationPreset.Minutes30))
        };
        MonitoringPresetCombo.SelectedIndex = 0;
        MonitoringIncidentCategoryCombo.ItemsSource = Enum.GetValues<MonitoringIncidentCategory>()
            .Select(category => new MonitoringCategoryRow(category,
                MonitoringGuidance.CategoryName(category)))
            .ToArray();
        MonitoringIncidentCategoryCombo.SelectedIndex = 0;
        MonitoringIncidentOccurredCombo.ItemsSource = new[]
        {
            new MonitoringOccurrenceRow(null, "Agora (registro)"),
            new MonitoringOccurrenceRow(TimeSpan.FromMinutes(1), "≈ 1 minuto atrás"),
            new MonitoringOccurrenceRow(TimeSpan.FromMinutes(2), "≈ 2 minutos atrás"),
            new MonitoringOccurrenceRow(TimeSpan.FromMinutes(5), "≈ 5 minutos atrás"),
            new MonitoringOccurrenceRow(TimeSpan.FromMinutes(10), "≈ 10 minutos atrás")
        };
        MonitoringIncidentOccurredCombo.SelectedIndex = 0;
        MonitoringAuthorizationCheck.IsChecked = false;
        updatingMonitoring = false;
        ClearMonitoringView();
        UpdateMonitoringButtons();
    }

    private MonitoringDurationPreset SelectedMonitoringPreset() =>
        MonitoringPresetCombo.SelectedItem is MonitoringPresetRow row
            ? row.Preset
            : MonitoringDurationPreset.Minutes5;

    private MonitoringSources SelectedMonitoringSources()
    {
        var sources = MonitoringSources.None;
        if (MonitoringEventsCheck.IsChecked == true) sources |= MonitoringSources.Events;
        if (MonitoringPerformanceCheck.IsChecked == true) sources |= MonitoringSources.Performance;
        if (MonitoringNetworkCheck.IsChecked == true) sources |= MonitoringSources.Network;
        if (MonitoringGraphicsCheck.IsChecked == true) sources |= MonitoringSources.Graphics;
        return sources;
    }

    private async Task ReloadMonitoringAsync(Guid? selectId = null)
    {
        var snapshot = await monitoringStore.LoadMonitoringSessionsAsync();
        monitoringSessions = snapshot.Sessions;
        monitoringSegments = snapshot.Segments;
        SetIntegrity("Monitoramento", snapshot.Warnings);
        RefreshMonitoringHistory();

        var target = selectId is { } id
            ? monitoringSessions.FirstOrDefault(item => item.Id == id)
            : unsavedMonitoring
              ?? (selectedMonitoring is { } current
                  ? monitoringSessions.FirstOrDefault(item => item.Id == current.Id)
                  : null)
              ?? monitoringSessions.FirstOrDefault();
        if (target is null)
        {
            ClearMonitoringView();
            return;
        }

        SelectMonitoringInHistory(target.Id);
        ApplyMonitoringSession(target);
    }

    private void RefreshMonitoringHistory()
    {
        updatingMonitoring = true;
        MonitoringHistoryList.ItemsSource = monitoringSessions
            .OrderByDescending(item => item.CompletedUtc)
            .Select(item => new MonitoringHistoryRow(item))
            .ToArray();
        updatingMonitoring = false;
    }

    private void SelectMonitoringInHistory(Guid id)
    {
        updatingMonitoring = true;
        var row = MonitoringHistoryList.Items.OfType<MonitoringHistoryRow>()
            .FirstOrDefault(item => item.Session.Id == id);
        MonitoringHistoryList.SelectedItem = row;
        if (row is not null) MonitoringHistoryList.ScrollIntoView(row);
        updatingMonitoring = false;
    }

    private void ApplyMonitoringSession(MonitoringSession session)
    {
        selectedMonitoring = session;
        var persisted = monitoringSessions.Any(item => item.Id == session.Id) &&
            File.Exists(monitoringStore.GetMonitoringReportPath(session.Id));
        var incidents = session.Incidents;

        MonitoringSessionInfo.Text =
            Loc.F($"{MonitoringGuidance.StatusName(session.Status)} · cobertura ") +
            Loc.F($"{MonitoringGuidance.CoverageName(session.CoverageStatus)} · ") +
            Loc.F($"{session.Segments.Count} checkpoint(s) · {incidents.Count} incidente(s) · ID {session.Id:D}") +
            (persisted ? string.Empty : Loc.T(" · não confirmada na memória local"));

        MonitoringElapsedText.Text = Loc.F($"{session.Duration.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s");
        MonitoringRemainingText.Text = session.Recovery.Recovered ? Loc.T("interrompida") : Loc.T("encerrada");
        MonitoringSamplesValue.Text = session.Budget.SamplesCollected.ToString(CultureInfo.CurrentCulture);
        MonitoringEventsValue.Text = session.Budget.EventsCollected.ToString(CultureInfo.CurrentCulture);
        MonitoringIncidentsValue.Text = session.Incidents.Count.ToString(CultureInfo.CurrentCulture);
        MonitoringBudgetValue.Text = session.Budget.MissedOpportunities.ToString(CultureInfo.CurrentCulture);

        MonitoringProgress.Minimum = 0;
        MonitoringProgress.Maximum = Math.Max(1, session.Budget.ExpectedTicks);
        MonitoringProgress.Value = Math.Min(session.Budget.CollectedTicks, session.Budget.ExpectedTicks);

        updatingMonitoring = true;
        var incidentRows = incidents.Select(item => new MonitoringIncidentRow(item, session)).ToArray();
        MonitoringIncidentList.ItemsSource = incidentRows;
        MonitoringIncidentList.SelectedIndex = incidentRows.Length > 0 ? 0 : -1;
        MonitoringTimelineList.ItemsSource = monitoringSegments
            .Where(segment => segment.SessionId == session.Id)
            .OrderBy(segment => segment.Source)
            .ThenBy(segment => segment.Sequence)
            .Select(segment => new MonitoringSegmentRow(segment))
            .ToArray();
        updatingMonitoring = false;

        if (incidentRows.Length > 0) ShowMonitoringIncident(incidents[0], session);
        else MonitoringIncidentDetail.Text = BuildMonitoringSummary(session);
        if (MonitoringIncidentList.SelectedItem is { } selectedIncident)
            MonitoringIncidentList.ScrollIntoView(selectedIncident);
        MonitoringBudgetDetail.Text = BuildMonitoringBudgetText(session);
        UpdateMonitoringButtons();
    }

    private void ClearMonitoringView()
    {
        selectedMonitoring = null;
        MonitoringSessionInfo.Text = Loc.T("Nenhuma sessão de monitoramento realizada.");
        MonitoringStatusText.Text =
            Loc.T("Monitoramento desativado. A observação só começa após seleção das fontes, duração e autorização ") +
            Loc.T("explícita; fechar o aplicativo encerra a sessão.");
        MonitoringProgress.Minimum = 0;
        MonitoringProgress.Maximum = 1;
        MonitoringProgress.Value = 0;
        MonitoringElapsedText.Text = "—";
        MonitoringRemainingText.Text = "—";
        MonitoringSamplesValue.Text = "—";
        MonitoringEventsValue.Text = "—";
        MonitoringIncidentsValue.Text = "—";
        MonitoringBudgetValue.Text = "—";
        updatingMonitoring = true;
        MonitoringIncidentList.ItemsSource = null;
        MonitoringTimelineList.ItemsSource = null;
        updatingMonitoring = false;
        MonitoringIncidentDetail.Text =
            Loc.T("Nenhum incidente selecionado. Um incidente é relato do usuário; o momento do registro não é o ") +
            Loc.T("momento comprovado do início do problema.");
        MonitoringBudgetDetail.Text = Loc.T("Nenhuma sessão selecionada.");
        UpdateMonitoringButtons();
    }

    private static string BuildMonitoringSummary(MonitoringSession session)
    {
        var text = new StringBuilder();
        text.AppendLine(Loc.F($"Sessão {MonitoringGuidance.StatusName(session.Status).ToLowerInvariant()} · ") +
            Loc.F($"cobertura {MonitoringGuidance.CoverageName(session.CoverageStatus)}"));
        text.AppendLine(Loc.F($"Duração autorizada: {session.AuthorizedDuration.TotalMinutes:0} min · ") +
            Loc.F($"observada: {session.Duration.TotalSeconds:F1} s"));
        text.AppendLine(session.Recovery.Recovered
            ? Loc.T("Recuperada de checkpoints: ") + session.Recovery.Detail
            : Loc.T("Sessão concluída pelo aplicativo."));
        text.AppendLine();
        text.AppendLine(Loc.T("Fontes"));
        foreach (var source in session.Sources)
            text.AppendLine(Loc.F($"• {MonitoringGuidance.SourceName(source.Source)}: ") +
                Loc.F($"{MonitoringGuidance.SourceStatusName(source.Status)} · {source.CollectedTicks}/") +
                Loc.F($"{source.ExpectedTicks} ciclos · {source.ObservationCount} observação(ões)"));
        if (session.Incidents.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Os incidentes registrados manualmente não são eventos do Windows e não comprovam ") +
                Loc.T("o instante de início do problema."));
        }

        text.AppendLine();
        text.AppendLine(Loc.T("Limitações"));
        foreach (var limitation in session.Limitations) text.AppendLine(Loc.F($"• {limitation}"));
        return text.ToString();
    }

    private static string BuildMonitoringBudgetText(MonitoringSession session)
    {
        var budget = session.Budget;
        var text = new StringBuilder();
        text.AppendLine(Loc.T("Orçamento de recursos"));
        text.AppendLine(Loc.F($"• Cadência: {budget.CadenceSummary}"));
        text.AppendLine(Loc.F($"• Orçamento por leitura: {budget.MaxCollectorTick.TotalSeconds:0.#} s"));
        text.AppendLine(Loc.F($"• Ciclos: {budget.CollectedTicks}/{budget.ExpectedTicks} · ") +
            Loc.F($"{budget.MissedOpportunities} oportunidade(s) perdida(s)"));
        text.AppendLine(Loc.F($"• Leitura: média {budget.MeanTickMilliseconds:F1} ms · máxima {budget.MaxTickMilliseconds:F1} ms"));
        text.AppendLine(Loc.F($"• Amostras: {budget.SamplesCollected} · eventos: {budget.EventsCollected}"));
        text.AppendLine(Loc.F($"• Persistido: {budget.PersistedBytes} de {budget.StorageBudgetBytes} bytes"));
        text.AppendLine(Loc.F($"• Limite de armazenamento: {(budget.StorageBudgetReached ? "atingido" : "não atingido")} · ") +
            Loc.F($"orçamento por leitura excedido: {(budget.TickBudgetExceeded ? "sim" : "não")}"));
        text.AppendLine();
        text.AppendLine(Loc.T("Checkpoints"));
        if (session.Segments.Count == 0) text.AppendLine(Loc.T("• Nenhum checkpoint confirmado."));
        foreach (var segment in session.Segments)
            text.AppendLine(Loc.F($"• {MonitoringGuidance.SourceName(segment.Source)} #{segment.Sequence} · ") +
                Loc.F($"{segment.FromUtc.ToLocalTime():dd/MM HH:mm:ss}–{segment.ToUtc.ToLocalTime():HH:mm:ss} · ") +
                Loc.F($"{segment.ObservationCount} observação(ões)"));
        text.AppendLine();
        text.AppendLine(Loc.T("Orçamento e limites são declarados; nada é omitido em silêncio."));
        return text.ToString();
    }

    private void ShowMonitoringIncident(MonitoringIncident incident, MonitoringSession? session)
    {
        var text = new StringBuilder();
        text.AppendLine(MonitoringGuidance.CategoryName(incident.Category));
        text.AppendLine(Loc.F($"Registro manual: {Local(incident.RecordedUtc)}"));
        text.AppendLine(Loc.F($"Ocorrência informada: ") +
            Loc.F($"{(incident.OccurredUtc is { } occurred ? Local(occurred) : "não informada")}"));
        text.AppendLine(Loc.F($"Precisão: {MonitoringGuidance.PrecisionName(incident.Precision)}"));
        text.AppendLine(Loc.F($"Momento na sessão: {incident.SessionElapsedSeconds:F1} s"));
        if (incident.Description is not null) text.AppendLine(Loc.F($"Descrição: {incident.Description}"));
        text.AppendLine();
        text.AppendLine(Loc.T("Referência do usuário — não é evento do Windows e não comprova o início do problema."));
        if (incident.Evidence.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Evidências disponíveis até o registro"));
            foreach (var evidence in incident.Evidence)
                text.AppendLine(Loc.F($"• {MonitoringGuidance.SourceName(evidence.Source)}: {evidence.Reference} — ") +
                    evidence.Detail);
        }

        text.AppendLine();
        text.AppendLine(Loc.T("Limitações"));
        foreach (var limitation in incident.Limitations) text.AppendLine(Loc.F($"• {limitation}"));
        if (session?.Recovery.Recovered == true)
            text.AppendLine(Loc.T("• Sessão recuperada: incidentes em memória não puderam ser reconstituídos."));
        MonitoringIncidentDetail.Text = text.ToString();
    }

    private async void OnMonitoringStart(object sender, RoutedEventArgs e)
    {
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        if (monitoringBusy || busy || hardwareBusy || eventBusy || performanceBusy || networkBusy || graphicsBusy ||
            correlationBusy)
            return;

        if (unsavedMonitoring is not null)
        {
            monitoringBusy = true;
            monitoringSaving = true;
            UpdateAllBusyButtons();
            MonitoringStatusText.Text = Loc.T("Tentando salvar novamente a sessão pendente…");
            try
            {
                var pending = unsavedMonitoring;
                await monitoringStore.SaveMonitoringSessionAsync(pending, CancellationToken.None);
                unsavedMonitoring = null;
                await ReloadMonitoringAsync(pending.Id);
                MonitoringStatusText.Text = Loc.T("Sessão pendente salva novamente.");
            }
            catch (Exception error)
            {
                ShowMonitoringFailure(Loc.T("Falha de gravação da sessão. O resultado permanece nesta janela; tente ") +
                    Loc.T("salvar novamente"), error);
            }
            finally
            {
                monitoringBusy = false;
                monitoringSaving = false;
                MonitoringStartButton.Content = unsavedMonitoring is null
                    ? Loc.T("Iniciar monitoramento")
                    : Loc.T("Tentar salvar sessão");
                UpdateAllBusyButtons();
                var closeNow = monitoringClosingAfterSave && unsavedMonitoring is null;
                monitoringClosingAfterSave = false;
                if (closeNow) Close();
            }
            return;
        }

        var sources = SelectedMonitoringSources();
        if (sources == MonitoringSources.None)
        {
            ShowWarning(Loc.T("Selecione ao menos uma fonte para observar."));
            return;
        }

        if (MonitoringAuthorizationCheck.IsChecked != true)
        {
            ShowWarning(Loc.T("O monitoramento exige autorização explícita. Marque a confirmação antes de iniciar."));
            return;
        }

        var preset = SelectedMonitoringPreset();
        var request = MonitoringRequest.ForPreset(preset, sources, authorized: true);
        monitoringBusy = true;
        monitoringSaving = false;
        monitoringCancellation = new CancellationTokenSource();
        ClearRunWarnings();
        monitoringDuration = request.Duration;
        monitoringStopwatch.Restart();
        MonitoringProgress.Minimum = 0;
        MonitoringProgress.Maximum = Math.Max(1, request.Duration.TotalSeconds);
        MonitoringProgress.Value = 0;
        UpdateAllBusyButtons();
        monitoringCountdown.Start();
        UpdateMonitoringCountdown();
        var saved = false;
        try
        {
            MonitoringStatusText.Text = Loc.T("Sessão de monitoramento em andamento. Observação de baixo impacto; ") +
                Loc.T("nenhuma configuração do Windows é alterada.");
            var progress = new Progress<MonitoringProgress>(OnMonitoringProgress);
            var session = await monitoringRunner.RunAsync(request, progress, monitoringCancellation.Token);
            unsavedMonitoring = session;
            ApplyMonitoringSession(session);
            monitoringSaving = true;
            UpdateAllBusyButtons();
            MonitoringStatusText.Text = Loc.T("Salvando sessão e relatório na memória local…");
            await monitoringStore.SaveMonitoringSessionAsync(session, CancellationToken.None);
            saved = true;
            unsavedMonitoring = null;
            await ReloadMonitoringAsync(session.Id);
            MonitoringStatusText.Text =
                Loc.F($"Sessão {MonitoringGuidance.StatusName(session.Status).ToLowerInvariant()} salva com cobertura ") +
                Loc.F($"{MonitoringGuidance.CoverageName(session.CoverageStatus)}. ") +
                Loc.F($"{session.Incidents.Count} incidente(s), {session.Segments.Count} checkpoint(s). ") +
                Loc.T("Causalidade não avaliada.");
        }
        catch (Exception error)
        {
            var message = unsavedMonitoring is not null
                ? "Falha de gravação da sessão. O resultado permanece nesta janela; tente salvar novamente"
                : saved
                    ? "A sessão foi salva, mas a memória local não pôde ser relida. Reabra o aplicativo"
                    : "A sessão de monitoramento não pôde ser concluída";
            ShowMonitoringFailure(message, error);
        }
        finally
        {
            monitoringCountdown.Stop();
            monitoringStopwatch.Stop();
            monitoringBusy = false;
            monitoringSaving = false;
            MonitoringAuthorizationCheck.IsChecked = false;
            monitoringCancellation?.Dispose();
            monitoringCancellation = null;
            MonitoringStartButton.Content = unsavedMonitoring is null
                ? Loc.T("Iniciar monitoramento")
                : Loc.T("Tentar salvar sessão");
            UpdateAllBusyButtons();
            var closeNow = monitoringClosingAfterSave && unsavedMonitoring is null;
            monitoringClosingAfterSave = false;
            if (closeNow) Close();
        }
    }

    private void OnMonitoringStop(object sender, RoutedEventArgs e)
    {
        monitoringCancellation?.Cancel();
        MonitoringStopButton.IsEnabled = false;
        MonitoringStatusText.Text = Loc.T("Encerramento solicitado. Aguardando o último checkpoint e a gravação segura.");
    }

    private void OnMonitoringProgress(MonitoringProgress update)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnMonitoringProgress(update));
            return;
        }

        if (monitoringSaving) return;
        MonitoringProgress.Value = Math.Min(update.Elapsed.TotalSeconds,
            Math.Max(1, MonitoringProgress.Maximum));
        MonitoringStatusText.Text = Loc.F($"{char.ToUpperInvariant(update.Phase[0])}{update.Phase[1..]} · ") +
            Loc.F($"{update.Samples} amostra(s) · {update.Events} evento(s) · {update.Incidents} incidente(s) · ") +
            (update.BudgetWarning ? Loc.T("orçamento excedido em um ciclo") : Loc.T("dentro do orçamento"));
        MonitoringSamplesValue.Text = update.Samples.ToString(CultureInfo.CurrentCulture);
        MonitoringEventsValue.Text = update.Events.ToString(CultureInfo.CurrentCulture);
        MonitoringIncidentsValue.Text = update.Incidents.ToString(CultureInfo.CurrentCulture);
        RefreshLiveIncidents();
    }

    private void RefreshLiveIncidents()
    {
        var live = monitoringRunner.Incidents;
        updatingMonitoring = true;
        var rows = live.Select(item => new MonitoringIncidentRow(item, null)).ToArray();
        MonitoringIncidentList.ItemsSource = rows;
        if (rows.Length > 0 && MonitoringIncidentList.SelectedIndex < 0)
        {
            MonitoringIncidentList.SelectedIndex = rows.Length - 1;
            MonitoringIncidentList.ScrollIntoView(rows[^1]);
        }
        updatingMonitoring = false;
        MonitoringIncidentsValue.Text = live.Count.ToString(CultureInfo.CurrentCulture);
    }

    private void OnMonitoringCountdown(object? sender, EventArgs e) => UpdateMonitoringCountdown();

    private void UpdateMonitoringCountdown()
    {
        if (!monitoringBusy || monitoringSaving) return;
        var elapsed = monitoringStopwatch.Elapsed;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var remaining = monitoringDuration - elapsed;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        MonitoringElapsedText.Text = Loc.F($"{elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture)}");
        MonitoringRemainingText.Text = Loc.F($"{remaining.ToString(@"mm\:ss", CultureInfo.InvariantCulture)}");
    }

    private void OnMonitoringIncident(object sender, RoutedEventArgs e)
    {
        if (!monitoringBusy || monitoringSaving || monitoringCancellation is null)
        {
            ShowWarning(Loc.T("Registre um incidente somente durante uma sessão de monitoramento ativa."));
            return;
        }

        var category = MonitoringIncidentCategoryCombo.SelectedItem is MonitoringCategoryRow categoryRow
            ? categoryRow.Category
            : MonitoringIncidentCategory.Other;
        DateTimeOffset? occurred = MonitoringIncidentOccurredCombo.SelectedItem is MonitoringOccurrenceRow occurrence
            ? occurrence.Offset is { } offset ? DateTimeOffset.UtcNow - offset : null
            : null;
        var request = new MonitoringIncidentRequest(category, MonitoringIncidentDescription.Text, occurred);
        try
        {
            if (!monitoringRunner.TryRecordIncident(request, out var incident) || incident is null)
            {
                ShowWarning(Loc.T("O incidente não foi registrado: a sessão não está ativa ou o limite foi atingido."));
                return;
            }

            MonitoringIncidentDescription.Text = string.Empty;
            RefreshLiveIncidents();
            MonitoringIncidentList.SelectedItem = MonitoringIncidentList.Items.OfType<MonitoringIncidentRow>()
                .LastOrDefault(item => item.Incident.Id == incident.Id);
            MonitoringStatusText.Text =
                Loc.F($"Incidente registrado: {MonitoringGuidance.CategoryName(incident.Category)}. ") +
                Loc.T("Registro manual do usuário; o horário do clique não comprova o início do problema.");
        }
        catch (Exception error)
        {
            ShowMonitoringFailure(Loc.T("O incidente não pôde ser registrado"), error);
        }
    }

    private void OnMonitoringIncidentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingMonitoring) return;
        if (MonitoringIncidentList.SelectedItem is not MonitoringIncidentRow row) return;
        ShowMonitoringIncident(row.Incident,
            row.Session ?? (monitoringRunner.IsRunning ? null : selectedMonitoring));
        UpdateMonitoringButtons();
    }

    private void OnMonitoringHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingMonitoring) return;
        if (MonitoringHistoryList.SelectedItem is MonitoringHistoryRow row)
            ApplyMonitoringSession(row.Session);
    }

    private void OnMonitoringPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingMonitoring) return;
        UpdateMonitoringButtons();
    }

    private void OnMonitoringSourceChanged(object sender, RoutedEventArgs e)
    {
        if (updatingMonitoring) return;
        UpdateMonitoringButtons();
    }

    private void OnMonitoringAuthorizationChanged(object sender, RoutedEventArgs e)
    {
        if (updatingMonitoring) return;
        UpdateMonitoringButtons();
    }

    private async void OnMonitoringReport(object sender, RoutedEventArgs e)
    {
        if (selectedMonitoring is not { } session) return;
        try
        {
            MonitoringReportButton.IsEnabled = false;
            await WindowsShell.OpenReportLocationAsync(monitoringStore.GetMonitoringReportPath(session.Id));
        }
        catch (Exception error)
        {
            ShowMonitoringFailure(Loc.T("Não foi possível abrir a localização do relatório de monitoramento"), error);
        }
        finally
        {
            UpdateMonitoringButtons();
        }
    }

    /// <summary>
    /// Investigação direcionada: usa a janela do incidente selecionado e reutiliza o motor de correlação existente.
    /// O incidente é referência temporal do usuário, não uma evidência nativa, e a causalidade permanece não avaliada.
    /// </summary>
    private async void OnMonitoringCorrelate(object sender, RoutedEventArgs e)
    {
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        if (monitoringBusy || correlationBusy || busy || hardwareBusy || eventBusy || performanceBusy ||
            networkBusy || graphicsBusy)
            return;
        if (MonitoringIncidentList.SelectedItem is not MonitoringIncidentRow row)
        {
            ShowWarning(Loc.T("Selecione um incidente registrado para solicitar a correlação direcionada."));
            return;
        }

        var reference = row.Incident.OccurredUtc ?? row.Incident.RecordedUtc;
        var from = reference - TimeSpan.FromMinutes(10);
        var to = reference + TimeSpan.FromMinutes(10);
        if (to > DateTimeOffset.UtcNow) to = DateTimeOffset.UtcNow;
        if (from >= to)
        {
            ShowWarning(Loc.T("A janela do incidente é inválida; nenhuma correlação foi executada."));
            return;
        }

        correlationBusy = true;
        correlationSaving = false;
        correlationCancellation = new CancellationTokenSource();
        UpdateAllBusyButtons();
        try
        {
            MonitoringStatusText.Text = Loc.T("Correlação direcionada em andamento sobre os registros históricos. ") +
                Loc.T("Nenhuma coleta nova é executada.");
            var request = new CorrelationRequest(EventWindow.Custom, from, to, CorrelationSources.All);
            var investigation = await correlationRunner.RunAsync(request, null, correlationCancellation.Token);
            unsavedCorrelation = investigation;
            correlationSaving = true;
            UpdateAllBusyButtons();
            await correlationStore.SaveCorrelationInvestigationAsync(investigation, CancellationToken.None);
            unsavedCorrelation = null;
            await ReloadCorrelationAsync(investigation.Id);
            MonitoringStatusText.Text =
                Loc.F($"Correlação direcionada salva: {investigation.Coverage.DistinctOccurrences} evidência(s), ") +
                Loc.F($"{investigation.Relations.Count(relation => relation.IsHypothesis)} relação(ões). ") +
                Loc.T("Causalidade não avaliada.");
            NavList.SelectedIndex = 7;
        }
        catch (Exception error)
        {
            ShowMonitoringFailure(Loc.T("A correlação direcionada não pôde ser concluída"), error);
        }
        finally
        {
            correlationBusy = false;
            correlationSaving = false;
            correlationCancellation?.Dispose();
            correlationCancellation = null;
            UpdateAllBusyButtons();
        }
    }

    private void UpdateMonitoringButtons()
    {
        var idle = !operations.IsBusy && !monitoringBusy && !busy && !hardwareBusy && !eventBusy && !performanceBusy &&
            !networkBusy && !graphicsBusy && !correlationBusy;
        var authorized = MonitoringAuthorizationCheck.IsChecked == true &&
            SelectedMonitoringSources() != MonitoringSources.None;
        MonitoringStartButton.IsEnabled = idle && (unsavedMonitoring is not null || authorized);
        MonitoringStopButton.IsEnabled = monitoringBusy && !monitoringSaving &&
            monitoringCancellation is { IsCancellationRequested: false };
        MonitoringIncidentButton.IsEnabled = monitoringBusy && !monitoringSaving;
        MonitoringAuthorizationCheck.IsEnabled = !monitoringBusy;
        MonitoringPresetCombo.IsEnabled = !monitoringBusy;
        MonitoringEventsCheck.IsEnabled = !monitoringBusy;
        MonitoringPerformanceCheck.IsEnabled = !monitoringBusy;
        MonitoringNetworkCheck.IsEnabled = !monitoringBusy;
        MonitoringGraphicsCheck.IsEnabled = !monitoringBusy;
        MonitoringHistoryList.IsEnabled = !monitoringBusy;
        MonitoringReportButton.IsEnabled = !monitoringBusy && selectedMonitoring is not null &&
            File.Exists(monitoringStore.GetMonitoringReportPath(selectedMonitoring.Id));
        MonitoringCorrelateButton.IsEnabled = idle && MonitoringIncidentList.SelectedItem is MonitoringIncidentRow;
    }

    private void ShowMonitoringFailure(string message, Exception error)
    {
        logFailure(error);
        MonitoringStatusText.Text = message + ".";
        ShowWarning(Loc.F($"{message}. {error.GetType().Name}: {error.Message}"));
    }

    private sealed record MonitoringPresetRow(MonitoringDurationPreset Preset, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record MonitoringCategoryRow(MonitoringIncidentCategory Category, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record MonitoringOccurrenceRow(TimeSpan? Offset, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record MonitoringHistoryRow(MonitoringSession Session)
    {
        public string DateText => Session.CompletedUtc.ToLocalTime()
            .ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture);
        public string SummaryText =>
            $"{MonitoringGuidance.StatusName(Session.Status)} · cobertura " +
            $"{MonitoringGuidance.CoverageName(Session.CoverageStatus)} · " +
            $"{Session.Incidents.Count} incidente(s) · {Session.Segments.Count} checkpoint(s)" +
            (Session.Recovery.Recovered ? " · recuperada" : string.Empty);
    }

    private sealed record MonitoringIncidentRow(MonitoringIncident Incident, MonitoringSession? Session)
    {
        public string Time => Incident.RecordedUtc.ToLocalTime()
            .ToString("dd/MM HH:mm:ss", CultureInfo.CurrentCulture);
        public string Category => MonitoringGuidance.CategoryName(Incident.Category);
        public string Detail => Incident.OccurredUtc is { } occurred
            ? $"ocorrência ≈ {occurred.ToLocalTime():HH:mm:ss}"
            : "ocorrência não informada";
        public string Description => string.IsNullOrWhiteSpace(Incident.Description)
            ? "—"
            : Incident.Description;
    }

    private sealed record MonitoringSegmentRow(MonitoringSegment Segment)
    {
        public string Source => MonitoringGuidance.SourceName(Segment.Source);
        public string Sequence => Segment.Sequence.ToString(CultureInfo.CurrentCulture);
        public string Range => $"{Segment.FromUtc.ToLocalTime():dd/MM HH:mm:ss}–" +
            $"{Segment.ToUtc.ToLocalTime():HH:mm:ss}";
        public string Observations => Segment.ObservationCount.ToString(CultureInfo.CurrentCulture);
    }
}
