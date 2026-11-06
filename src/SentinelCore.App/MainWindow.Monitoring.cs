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
            $"{MonitoringGuidance.StatusName(session.Status)} · cobertura " +
            $"{MonitoringGuidance.CoverageName(session.CoverageStatus)} · " +
            $"{session.Segments.Count} checkpoint(s) · {incidents.Count} incidente(s) · ID {session.Id:D}" +
            (persisted ? string.Empty : " · não confirmada na memória local");

        MonitoringElapsedText.Text = $"{session.Duration.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s";
        MonitoringRemainingText.Text = session.Recovery.Recovered ? "interrompida" : "encerrada";
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
        MonitoringSessionInfo.Text = "Nenhuma sessão de monitoramento realizada.";
        MonitoringStatusText.Text =
            "Monitoramento desativado. A observação só começa após seleção das fontes, duração e autorização " +
            "explícita; fechar o aplicativo encerra a sessão.";
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
            "Nenhum incidente selecionado. Um incidente é relato do usuário; o momento do registro não é o " +
            "momento comprovado do início do problema.";
        MonitoringBudgetDetail.Text = "Nenhuma sessão selecionada.";
        UpdateMonitoringButtons();
    }

    private static string BuildMonitoringSummary(MonitoringSession session)
    {
        var text = new StringBuilder();
        text.AppendLine($"Sessão {MonitoringGuidance.StatusName(session.Status).ToLowerInvariant()} · " +
            $"cobertura {MonitoringGuidance.CoverageName(session.CoverageStatus)}");
        text.AppendLine($"Duração autorizada: {session.AuthorizedDuration.TotalMinutes:0} min · " +
            $"observada: {session.Duration.TotalSeconds:F1} s");
        text.AppendLine(session.Recovery.Recovered
            ? "Recuperada de checkpoints: " + session.Recovery.Detail
            : "Sessão concluída pelo aplicativo.");
        text.AppendLine();
        text.AppendLine("Fontes");
        foreach (var source in session.Sources)
            text.AppendLine($"• {MonitoringGuidance.SourceName(source.Source)}: " +
                $"{MonitoringGuidance.SourceStatusName(source.Status)} · {source.CollectedTicks}/" +
                $"{source.ExpectedTicks} ciclos · {source.ObservationCount} observação(ões)");
        if (session.Incidents.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Os incidentes registrados manualmente não são eventos do Windows e não comprovam " +
                "o instante de início do problema.");
        }

        text.AppendLine();
        text.AppendLine("Limitações");
        foreach (var limitation in session.Limitations) text.AppendLine($"• {limitation}");
        return text.ToString();
    }

    private static string BuildMonitoringBudgetText(MonitoringSession session)
    {
        var budget = session.Budget;
        var text = new StringBuilder();
        text.AppendLine("Orçamento de recursos");
        text.AppendLine($"• Cadência: {budget.CadenceSummary}");
        text.AppendLine($"• Orçamento por leitura: {budget.MaxCollectorTick.TotalSeconds:0.#} s");
        text.AppendLine($"• Ciclos: {budget.CollectedTicks}/{budget.ExpectedTicks} · " +
            $"{budget.MissedOpportunities} oportunidade(s) perdida(s)");
        text.AppendLine($"• Leitura: média {budget.MeanTickMilliseconds:F1} ms · máxima {budget.MaxTickMilliseconds:F1} ms");
        text.AppendLine($"• Amostras: {budget.SamplesCollected} · eventos: {budget.EventsCollected}");
        text.AppendLine($"• Persistido: {budget.PersistedBytes} de {budget.StorageBudgetBytes} bytes");
        text.AppendLine($"• Limite de armazenamento: {(budget.StorageBudgetReached ? "atingido" : "não atingido")} · " +
            $"orçamento por leitura excedido: {(budget.TickBudgetExceeded ? "sim" : "não")}");
        text.AppendLine();
        text.AppendLine("Checkpoints");
        if (session.Segments.Count == 0) text.AppendLine("• Nenhum checkpoint confirmado.");
        foreach (var segment in session.Segments)
            text.AppendLine($"• {MonitoringGuidance.SourceName(segment.Source)} #{segment.Sequence} · " +
                $"{segment.FromUtc.ToLocalTime():dd/MM HH:mm:ss}–{segment.ToUtc.ToLocalTime():HH:mm:ss} · " +
                $"{segment.ObservationCount} observação(ões)");
        text.AppendLine();
        text.AppendLine("Orçamento e limites são declarados; nada é omitido em silêncio.");
        return text.ToString();
    }

    private void ShowMonitoringIncident(MonitoringIncident incident, MonitoringSession? session)
    {
        var text = new StringBuilder();
        text.AppendLine(MonitoringGuidance.CategoryName(incident.Category));
        text.AppendLine($"Registro manual: {Local(incident.RecordedUtc)}");
        text.AppendLine($"Ocorrência informada: " +
            $"{(incident.OccurredUtc is { } occurred ? Local(occurred) : "não informada")}");
        text.AppendLine($"Precisão: {MonitoringGuidance.PrecisionName(incident.Precision)}");
        text.AppendLine($"Momento na sessão: {incident.SessionElapsedSeconds:F1} s");
        if (incident.Description is not null) text.AppendLine($"Descrição: {incident.Description}");
        text.AppendLine();
        text.AppendLine("Referência do usuário — não é evento do Windows e não comprova o início do problema.");
        if (incident.Evidence.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Evidências disponíveis até o registro");
            foreach (var evidence in incident.Evidence)
                text.AppendLine($"• {MonitoringGuidance.SourceName(evidence.Source)}: {evidence.Reference} — " +
                    evidence.Detail);
        }

        text.AppendLine();
        text.AppendLine("Limitações");
        foreach (var limitation in incident.Limitations) text.AppendLine($"• {limitation}");
        if (session?.Recovery.Recovered == true)
            text.AppendLine("• Sessão recuperada: incidentes em memória não puderam ser reconstituídos.");
        MonitoringIncidentDetail.Text = text.ToString();
    }

    private async void OnMonitoringStart(object sender, RoutedEventArgs e)
    {
        if (monitoringBusy || busy || hardwareBusy || eventBusy || performanceBusy || networkBusy || graphicsBusy ||
            correlationBusy)
            return;

        if (unsavedMonitoring is not null)
        {
            monitoringBusy = true;
            monitoringSaving = true;
            UpdateAllBusyButtons();
            MonitoringStatusText.Text = "Tentando salvar novamente a sessão pendente…";
            try
            {
                var pending = unsavedMonitoring;
                await monitoringStore.SaveMonitoringSessionAsync(pending, CancellationToken.None);
                unsavedMonitoring = null;
                await ReloadMonitoringAsync(pending.Id);
                MonitoringStatusText.Text = "Sessão pendente salva novamente.";
            }
            catch (Exception error)
            {
                ShowMonitoringFailure("Falha de gravação da sessão. O resultado permanece nesta janela; tente " +
                    "salvar novamente", error);
            }
            finally
            {
                monitoringBusy = false;
                monitoringSaving = false;
                MonitoringStartButton.Content = unsavedMonitoring is null
                    ? "Iniciar monitoramento"
                    : "Tentar salvar sessão";
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
            ShowWarning("Selecione ao menos uma fonte para observar.");
            return;
        }

        if (MonitoringAuthorizationCheck.IsChecked != true)
        {
            ShowWarning("O monitoramento exige autorização explícita. Marque a confirmação antes de iniciar.");
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
            MonitoringStatusText.Text = "Sessão de monitoramento em andamento. Observação de baixo impacto; " +
                "nenhuma configuração do Windows é alterada.";
            var progress = new Progress<MonitoringProgress>(OnMonitoringProgress);
            var session = await monitoringRunner.RunAsync(request, progress, monitoringCancellation.Token);
            unsavedMonitoring = session;
            ApplyMonitoringSession(session);
            monitoringSaving = true;
            UpdateAllBusyButtons();
            MonitoringStatusText.Text = "Salvando sessão e relatório na memória local…";
            await monitoringStore.SaveMonitoringSessionAsync(session, CancellationToken.None);
            saved = true;
            unsavedMonitoring = null;
            await ReloadMonitoringAsync(session.Id);
            MonitoringStatusText.Text =
                $"Sessão {MonitoringGuidance.StatusName(session.Status).ToLowerInvariant()} salva com cobertura " +
                $"{MonitoringGuidance.CoverageName(session.CoverageStatus)}. " +
                $"{session.Incidents.Count} incidente(s), {session.Segments.Count} checkpoint(s). " +
                "Causalidade não avaliada.";
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
                ? "Iniciar monitoramento"
                : "Tentar salvar sessão";
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
        MonitoringStatusText.Text = "Encerramento solicitado. Aguardando o último checkpoint e a gravação segura.";
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
        MonitoringStatusText.Text = $"{char.ToUpperInvariant(update.Phase[0])}{update.Phase[1..]} · " +
            $"{update.Samples} amostra(s) · {update.Events} evento(s) · {update.Incidents} incidente(s) · " +
            (update.BudgetWarning ? "orçamento excedido em um ciclo" : "dentro do orçamento");
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
        MonitoringElapsedText.Text = $"{elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture)}";
        MonitoringRemainingText.Text = $"{remaining.ToString(@"mm\:ss", CultureInfo.InvariantCulture)}";
    }

    private void OnMonitoringIncident(object sender, RoutedEventArgs e)
    {
        if (!monitoringBusy || monitoringSaving || monitoringCancellation is null)
        {
            ShowWarning("Registre um incidente somente durante uma sessão de monitoramento ativa.");
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
                ShowWarning("O incidente não foi registrado: a sessão não está ativa ou o limite foi atingido.");
                return;
            }

            MonitoringIncidentDescription.Text = string.Empty;
            RefreshLiveIncidents();
            MonitoringIncidentList.SelectedItem = MonitoringIncidentList.Items.OfType<MonitoringIncidentRow>()
                .LastOrDefault(item => item.Incident.Id == incident.Id);
            MonitoringStatusText.Text =
                $"Incidente registrado: {MonitoringGuidance.CategoryName(incident.Category)}. " +
                "Registro manual do usuário; o horário do clique não comprova o início do problema.";
        }
        catch (Exception error)
        {
            ShowMonitoringFailure("O incidente não pôde ser registrado", error);
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
            ShowMonitoringFailure("Não foi possível abrir a localização do relatório de monitoramento", error);
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
        if (monitoringBusy || correlationBusy || busy || hardwareBusy || eventBusy || performanceBusy ||
            networkBusy || graphicsBusy)
            return;
        if (MonitoringIncidentList.SelectedItem is not MonitoringIncidentRow row)
        {
            ShowWarning("Selecione um incidente registrado para solicitar a correlação direcionada.");
            return;
        }

        var reference = row.Incident.OccurredUtc ?? row.Incident.RecordedUtc;
        var from = reference - TimeSpan.FromMinutes(10);
        var to = reference + TimeSpan.FromMinutes(10);
        if (to > DateTimeOffset.UtcNow) to = DateTimeOffset.UtcNow;
        if (from >= to)
        {
            ShowWarning("A janela do incidente é inválida; nenhuma correlação foi executada.");
            return;
        }

        correlationBusy = true;
        correlationSaving = false;
        correlationCancellation = new CancellationTokenSource();
        UpdateAllBusyButtons();
        try
        {
            MonitoringStatusText.Text = "Correlação direcionada em andamento sobre os registros históricos. " +
                "Nenhuma coleta nova é executada.";
            var request = new CorrelationRequest(EventWindow.Custom, from, to, CorrelationSources.All);
            var investigation = await correlationRunner.RunAsync(request, null, correlationCancellation.Token);
            unsavedCorrelation = investigation;
            correlationSaving = true;
            UpdateAllBusyButtons();
            await correlationStore.SaveCorrelationInvestigationAsync(investigation, CancellationToken.None);
            unsavedCorrelation = null;
            await ReloadCorrelationAsync(investigation.Id);
            MonitoringStatusText.Text =
                $"Correlação direcionada salva: {investigation.Coverage.DistinctOccurrences} evidência(s), " +
                $"{investigation.Relations.Count(relation => relation.IsHypothesis)} relação(ões). " +
                "Causalidade não avaliada.";
            NavList.SelectedIndex = 7;
        }
        catch (Exception error)
        {
            ShowMonitoringFailure("A correlação direcionada não pôde ser concluída", error);
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
        var idle = !monitoringBusy && !busy && !hardwareBusy && !eventBusy && !performanceBusy &&
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
        ShowWarning($"{message}. {error.GetType().Name}: {error.Message}");
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
