using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows;
using SentinelCore.Persistence;
using SentinelCore.App.Localization;

namespace SentinelCore.App;

public partial class MainWindow
{
    private IReadOnlyList<EventInvestigation> eventInvestigations = [];
    private EventInvestigation? selectedEventInvestigation;
    private EventInvestigation? unsavedEvent;
    private CancellationTokenSource? eventCancellation;
    private bool eventBusy;
    private bool eventSaving;
    private bool eventClosingAfterSave;
    private bool updatingEvents;
    private EventCategory? eventCategory;

    private void InitializeEvents()
    {
        updatingEvents = true;
        EventWindowCombo.ItemsSource = new[]
        {
            new EventWindowRow(EventWindow.Last24Hours, "Últimas 24 horas"),
            new EventWindowRow(EventWindow.Last7Days, "Últimos 7 dias"),
            new EventWindowRow(EventWindow.Last30Days, "Últimos 30 dias"),
            new EventWindowRow(EventWindow.Custom, "Personalizado")
        };
        EventWindowCombo.SelectedIndex = 1;
        EventFromPicker.SelectedDate = DateTime.Today.AddDays(-7);
        EventToPicker.SelectedDate = DateTime.Today;
        EventCategoryCombo.ItemsSource = Categories();
        EventCategoryCombo.SelectedIndex = 0;
        updatingEvents = false;
        UpdateCustomDates();
        UpdateEventStorageIdentity();
        UpdateEventButtons();
    }

    private async Task ReloadEventsAsync(Guid? selectId = null)
    {
        var loaded = await eventStore.LoadInvestigationsAsync();
        eventInvestigations = loaded.Investigations;
        SetIntegrity("Eventos", loaded.Warnings);
        RefreshEventHistoryList();
        var target = selectId is { } id ? eventInvestigations.FirstOrDefault(item => item.Id == id) : null;
        target ??= unsavedEvent;
        if (target is null && selectedEventInvestigation is { } current)
            target = eventInvestigations.FirstOrDefault(item => item.Id == current.Id);
        target ??= eventInvestigations.FirstOrDefault();
        UpdateEventStorageIdentity();
        if (target is null)
        {
            ClearEventView();
            return;
        }
        SelectEventInHistory(target.Id);
        ApplyEventInvestigation(target);
    }

    private void RefreshEventHistoryList()
    {
        updatingEvents = true;
        EventHistoryList.ItemsSource = eventInvestigations
            .Select(item => new EventHistoryRow(item, EventQualityRules.Evaluate(item)))
            .ToArray();
        updatingEvents = false;
    }

    private void SelectEventInHistory(Guid id)
    {
        updatingEvents = true;
        var row = EventHistoryList.Items.OfType<EventHistoryRow>()
            .FirstOrDefault(item => item.Investigation.Id == id);
        EventHistoryList.SelectedItem = row;
        if (row is not null) EventHistoryList.ScrollIntoView(row);
        updatingEvents = false;
    }

    private void ApplyEventInvestigation(EventInvestigation investigation)
    {
        selectedEventInvestigation = investigation;
        var last = investigation.Groups.Select(group => group.LastObservedUtc).Max();
        var distinctReports = investigation.Reports.Select(report => report.ReportId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        EventInvestigationInfo.Text = Loc.F($"Investigação de {Local(investigation.CompletedUtc)} · ") +
            Loc.F($"{EventCatalog.StatusName(investigation.Status)} · janela {WindowName(investigation.Window)} · ID {investigation.Id:D}");
        EventGroupCountText.Text = investigation.Groups.Count.ToString(CultureInfo.InvariantCulture);
        EventRecordCountText.Text = investigation.Events.Count.ToString(CultureInfo.InvariantCulture);
        EventReportCountText.Text = distinctReports.ToString(CultureInfo.InvariantCulture);
        EventLastSeenText.Text = last is { } at ? Local(at) : Loc.T("Indisponível");
        var quality = EventQualityRules.Evaluate(investigation);
        EventQualityText.Text = Loc.F($"Qualidade ({quality.RuleId} v{quality.RuleVersion}): {QualityStateName(quality.State)} · {quality.Summary}") +
            (quality.State == EventQualityState.Affected
                ? Loc.T(" Contagens WER não confiáveis; registros preservados. Nenhuma substituição automática.")
                : string.Empty);
        EventQualityText.Foreground = (Brush)FindResource(
            quality.State is EventQualityState.Affected or EventQualityState.Undetermined ? "Warn" : "Muted");
        if (!eventBusy)
            EventStatusText.Text = Loc.F($"Investigação {EventCatalog.StatusName(investigation.Status).ToLowerInvariant()} · ") +
                Loc.F($"causalidade não avaliada · {investigation.Sources.Count(source => source.Status == EventSourceStatus.Completed)}") +
                Loc.F($"/{investigation.Sources.Count} fontes concluídas.");
        updatingEvents = true;
        EventSearch.Text = string.Empty;
        EventSearchHint.Visibility = Visibility.Visible;
        EventCategoryCombo.SelectedIndex = 0;
        eventCategory = null;
        updatingEvents = false;
        RefreshEventGroupList();
        UpdateEventButtons();
    }

    private void ClearEventView()
    {
        selectedEventInvestigation = null;
        eventCategory = null;
        updatingEvents = true;
        EventGroupList.ItemsSource = null;
        EventSearch.Text = string.Empty;
        EventSearchHint.Visibility = Visibility.Visible;
        updatingEvents = false;
        EventInvestigationInfo.Text = Loc.T("Nenhuma investigação de eventos realizada.");
        EventGroupCountText.Text = "—";
        EventRecordCountText.Text = "—";
        EventReportCountText.Text = "—";
        EventLastSeenText.Text = "—";
        EventQualityText.Text = string.Empty;
        EventGroupHeading.Text = Loc.T("Grupos");
        EventDetailsHeading.Text = Loc.T("Detalhes");
        EventDetails.Text = Loc.T("Nenhuma investigação disponível. Use 'Investigar eventos' ou abra uma investigação salva.");
        UpdateEventButtons();
    }

    private void RefreshEventGroupList()
    {
        if (selectedEventInvestigation is not { } investigation)
        {
            updatingEvents = true;
            EventGroupList.ItemsSource = null;
            updatingEvents = false;
            EventGroupHeading.Text = Loc.T("Grupos");
            return;
        }
        var query = EventSearch.Text.Trim();
        var rows = investigation.Groups
            .Where(group => eventCategory is null || group.Category == eventCategory)
            .Select(group => new EventGroupRow(group))
            .Where(row => query.Length == 0 || row.Haystack.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => row.Group.Severity)
            .ThenByDescending(row => row.Group.LastObservedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(row => row.Group.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        updatingEvents = true;
        EventGroupList.ItemsSource = rows;
        EventGroupList.SelectedItem = rows.Length > 0 ? rows[0] : null;
        updatingEvents = false;
        EventGroupHeading.Text = eventCategory is { } category
            ? Loc.F($"{EventCatalog.CategoryName(category)} · {rows.Length}")
            : Loc.F($"Grupos · {rows.Length}");
        if (rows.Length > 0) RenderEventGroup(rows[0].Group);
        else RenderEventEmpty(investigation, query);
    }

    private void RenderEventGroup(EvidenceGroup group)
    {
        if (selectedEventInvestigation is not { } investigation) return;
        var events = investigation.Events
            .Where(item => string.Equals(item.GroupId, group.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        var reports = investigation.Reports
            .Where(item => string.Equals(item.GroupId, group.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        var text = new StringBuilder();
        text.AppendLine(Loc.F($"{group.Title} — Eventos relacionados encontrados"));
        text.AppendLine(Loc.T("Status: evidência identificada; causa ainda indeterminada."));
        text.AppendLine();
        text.AppendLine(Loc.F($"Categoria: {EventCatalog.CategoryName(group.Category)}"));
        text.AppendLine(Loc.F($"Identificador técnico: {group.Id}"));
        if (!string.IsNullOrWhiteSpace(group.Provider)) text.AppendLine(Loc.F($"Provedor: {group.Provider}"));
        if (group.EventId is { } eventId) text.AppendLine(Loc.F($"Event ID: {eventId}"));
        if (!string.IsNullOrWhiteSpace(group.ProblemCode)) text.AppendLine(Loc.F($"Código do problema: {group.ProblemCode}"));
        text.AppendLine(Loc.F($"Severidade: {EventCatalog.SeverityName(group.Severity)}"));
        text.AppendLine(Loc.F($"Confiança da evidência: {EventCatalog.ConfidenceName(group.Confidence)}"));
        text.AppendLine(Loc.F($"Causalidade: {CausalityName(group.Causality)}"));
        text.AppendLine();
        text.AppendLine(Loc.F($"Quantidade de registros de evento: {group.EventRecordCount}"));
        text.AppendLine(Loc.F($"Quantidade de relatórios distintos: {group.DistinctReportCount}"));
        text.AppendLine(Loc.F($"Reapresentações de relatório: {group.RePresentationCount}"));
        text.AppendLine(Loc.F($"Primeira ocorrência conhecida: {Format(group.FirstObservedUtc)}"));
        text.AppendLine(Loc.F($"Última ocorrência conhecida: {Format(group.LastObservedUtc)}"));
        text.AppendLine(Loc.F($"Canais: {Join(group.Channels)}"));
        text.AppendLine(Loc.F($"Fontes: {Join(group.Sources)}"));
        if (group.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Limitações do grupo"));
            foreach (var limitation in group.Limitations) text.AppendLine(Loc.F($"• {limitation}"));
        }
        if (events.Length > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.F($"Registros de evento ({Math.Min(events.Length, 50)} de {events.Length})"));
            foreach (var item in events.Take(50))
            {
                var record = item.RecordId is { } id ? $"RecordId {id}" : "sem RecordId";
                text.AppendLine(Loc.F($"• {item.Channel} · {item.Provider} · Event ID {item.EventId} · {record} · {Local(item.OccurredUtc)}"));
                text.AppendLine(Loc.F($"  Nível: {item.Level}") + (item.TaskCode is { } task ? Loc.F($" · tarefa {task}") : string.Empty) +
                    (item.ActivityId is { } activity ? Loc.F($" · atividade {activity:D}") : string.Empty));
                if (!string.IsNullOrWhiteSpace(item.Message)) text.AppendLine(Loc.F($"  Mensagem: {Excerpt(item.Message)}"));
            }
        }
        if (reports.Length > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.F($"Relatórios WER ({Math.Min(reports.Length, 50)} de {reports.Length})"));
            foreach (var item in reports.Take(50))
            {
                text.AppendLine(Loc.F($"• {item.Source}/{item.Store} · ReportId {item.ReportId} · ") +
                    Loc.F($"{item.EventType ?? "tipo indisponível"}{(item.ProblemCode is { } code ? $" {code}" : string.Empty)} · ") +
                    Loc.F($"{Format(item.EventTimeUtc ?? item.CreationTimeUtc)}"));
                if (!string.IsNullOrWhiteSpace(item.Application))
                    text.AppendLine(Loc.F($"  Aplicativo: {item.Application}") +
                        (item.Module is { } module ? Loc.F($" · módulo {module}") : string.Empty));
            }
        }
        text.AppendLine();
        text.AppendLine(Loc.T("Interpretação"));
        text.AppendLine(Loc.T("• A presença de eventos relacionados é evidência técnica, não confirmação de causa."));
        text.AppendLine(Loc.T("• Um código isolado não determina defeito de hardware."));
        text.AppendLine(Loc.T("• Contagens parciais ou limitadas não representam o total do sistema."));
        EventDetailsHeading.Text = group.Title;
        EventDetails.Text = text.ToString();
    }

    private void RenderEventEmpty(EventInvestigation investigation, string query)
    {
        var text = new StringBuilder();
        text.AppendLine(query.Length > 0
            ? Loc.F($"Nenhum grupo corresponde à busca \"{query}\".")
            : Loc.T("Nenhum evento compatível encontrado no período."));
        text.AppendLine(Loc.T("Ausência de eventos não comprova estabilidade nem ausência de problema."));
        text.AppendLine();
        text.AppendLine(Loc.F($"Status da investigação: {EventCatalog.StatusName(investigation.Status)} · causalidade não avaliada"));
        text.AppendLine(Loc.F($"Fontes: {investigation.Sources.Count(source => source.Status == EventSourceStatus.Completed)}") +
            Loc.F($"/{investigation.Sources.Count} concluídas"));
        foreach (var source in investigation.Sources)
        {
            text.AppendLine(Loc.F($"• {EventCollectorName(source.CollectorId)}: {EventCatalog.SourceStatusName(source.Status)} · ") +
                Loc.F($"{source.EventsRead} evento(s) · {source.ReportsRead} relatório(s)") +
                (source.LimitHit ? Loc.T(" · limite atingido") : string.Empty));
            foreach (var limitation in source.Limitations) text.AppendLine(Loc.F($"  Limitação: {limitation}"));
        }
        var unavailable = investigation.Channels.Where(channel => !channel.Accessible || !channel.Exists).ToArray();
        if (unavailable.Length > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Canais indisponíveis ou inacessíveis"));
            foreach (var channel in unavailable)
                text.AppendLine(Loc.F($"• {channel.Channel}: {channel.Detail ?? "indisponível"}"));
        }
        if (investigation.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Limitações da investigação"));
            foreach (var limitation in investigation.Limitations) text.AppendLine(Loc.F($"• {limitation}"));
        }
        EventDetailsHeading.Text = query.Length > 0 ? Loc.T("Busca") : Loc.T("Investigação");
        EventDetails.Text = text.ToString();
    }

    private async void OnEventRun(object sender, RoutedEventArgs e)
    {
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        if (eventBusy || busy || hardwareBusy || performanceBusy || networkBusy || graphicsBusy || correlationBusy || monitoringBusy)
            return;
        var window = (EventWindowCombo.SelectedItem as EventWindowRow)?.Window ?? EventWindow.Last7Days;
        DateTimeOffset? from = null, to = null;
        if (window == EventWindow.Custom)
        {
            if (EventFromPicker.SelectedDate is not { } fromDate || EventToPicker.SelectedDate is not { } toDate)
            {
                EventStatusText.Text = Loc.T("Selecione as duas datas do período personalizado.");
                return;
            }
            if (fromDate.Date > toDate.Date)
            {
                EventStatusText.Text = Loc.T("A data inicial não pode ser posterior à data final.");
                return;
            }
            from = LocalStart(fromDate);
            to = LocalEnd(toDate);
        }

        eventBusy = true; eventSaving = false;
        EventRunButton.IsEnabled = false; RunButton.IsEnabled = false;
        eventCancellation = new CancellationTokenSource();
        ClearRunWarnings();
        EventProgressBar.Maximum = 2; EventProgressBar.Value = 0;
        UpdateEventButtons(); UpdateHardwareButtons();
        var saved = false;
        try
        {
            if (unsavedEvent is not null)
            {
                eventSaving = true;
                var pending = unsavedEvent;
                EventStatusText.Text = Loc.T("Tentando salvar novamente a investigação pendente…");
                UpdateEventButtons();
                await eventStore.SaveInvestigationAsync(pending);
                saved = true;
                unsavedEvent = null;
                await ReloadEventsAsync(pending.Id);
                EventStatusText.Text = Loc.T("Investigação pendente salva novamente.");
                return;
            }
            EventStatusText.Text = Loc.T("Investigando canais de eventos e metadados WER. Leitura somente, sem alterações no Windows.");
            var progress = new Progress<EventInvestigationProgress>(update =>
            {
                if (eventSaving) return;
                EventProgressBar.Maximum = Math.Max(1, update.Total);
                EventProgressBar.Value = update.Finished;
                EventStatusText.Text = Loc.F($"{EventCollectorName(update.CollectorId)}: ") +
                    Loc.F($"{EventCatalog.SourceStatusName(update.Status)} · {update.Finished}/{update.Total} fontes");
            });
            var investigation = await eventRunner.RunAsync(window, from, to, progress, eventCancellation.Token);
            unsavedEvent = investigation;
            ApplyEventInvestigation(investigation);
            eventSaving = true;
            UpdateEventButtons();
            EventStatusText.Text = Loc.T("Salvando investigação e atualizando a memória local…");
            await eventStore.SaveInvestigationAsync(investigation);
            saved = true;
            unsavedEvent = null;
            await ReloadEventsAsync(investigation.Id);
            var distinctReports = investigation.Reports.Select(report => report.ReportId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            EventStatusText.Text = Loc.F($"Investigação {EventCatalog.StatusName(investigation.Status).ToLowerInvariant()}. ") +
                Loc.F($"{investigation.Groups.Count} grupo(s), {investigation.Events.Count} registro(s), ") +
                Loc.F($"{distinctReports} relatório(s) distinto(s).");
        }
        catch (Exception error)
        {
            var message = unsavedEvent is not null
                ? "Falha de gravação da investigação. O resultado permanece nesta janela; tente salvar novamente"
                : saved
                    ? "A investigação foi salva, mas a memória local não pôde ser relida. Use 'Recarregar'"
                    : "A investigação de eventos não pôde ser concluída";
            ShowEventFailure(message, error);
        }
        finally
        {
            eventBusy = false; eventSaving = false;
            eventCancellation?.Dispose(); eventCancellation = null;
            EventRunButton.Content = unsavedEvent is null ? Loc.T("Investigar eventos") : Loc.T("Tentar salvar investigação");
            RunButton.IsEnabled = true;
            UpdateEventButtons(); UpdateHardwareButtons();
            var closeNow = eventClosingAfterSave && unsavedEvent is null;
            eventClosingAfterSave = false;
            if (closeNow) Close();
        }
    }

    private void OnEventCancel(object sender, RoutedEventArgs e)
    {
        eventCancellation?.Cancel();
        EventCancelButton.IsEnabled = false;
        EventStatusText.Text = Loc.T("Cancelamento solicitado. Aguardando a leitura somente de eventos em andamento…");
    }

    private async void OnEventReload(object sender, RoutedEventArgs e)
    {
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        if (eventBusy || busy || hardwareBusy || performanceBusy || networkBusy || graphicsBusy || correlationBusy || monitoringBusy)
            return;
        eventBusy = true;
        EventReloadButton.IsEnabled = false; RunButton.IsEnabled = false;
        UpdateEventButtons(); UpdateHardwareButtons();
        EventStatusText.Text = Loc.T("Recarregando investigações salvas da memória local…");
        try
        {
            await ReloadEventsAsync(selectedEventInvestigation?.Id);
            EventStatusText.Text = Loc.T("Investigações recarregadas da memória local.");
        }
        catch (Exception error) { ShowEventFailure(Loc.T("Não foi possível recarregar as investigações de eventos"), error); }
        finally
        {
            eventBusy = false;
            RunButton.IsEnabled = true;
            UpdateEventButtons(); UpdateHardwareButtons();
        }
    }

    private void OnEventHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingEvents) return;
        if (EventHistoryList.SelectedItem is EventHistoryRow row) ApplyEventInvestigation(row.Investigation);
    }

    private void OnEventSearchChanged(object sender, TextChangedEventArgs e)
    {
        EventSearchHint.Visibility = string.IsNullOrWhiteSpace(EventSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (updatingEvents || selectedEventInvestigation is null) return;
        RefreshEventGroupList();
    }

    private void OnEventCategorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingEvents) return;
        eventCategory = (EventCategoryCombo.SelectedItem as EventCategoryRow)?.Category;
        RefreshEventGroupList();
    }

    private void OnEventGroupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingEvents) return;
        if (EventGroupList.SelectedItem is EventGroupRow row) RenderEventGroup(row.Group);
    }

    private void OnEventWindowChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingEvents) UpdateCustomDates();
    }

    private void OnEventDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!updatingEvents) UpdateEventButtons();
    }

    private async void OnOpenEventReport(object sender, RoutedEventArgs e)
    {
        if (selectedEventInvestigation is not { } investigation) return;
        try
        {
            EventReportButton.IsEnabled = false;
            await WindowsShell.OpenReportLocationAsync(eventStore.GetEventReportPath(investigation.Id));
        }
        catch (Exception error) { ShowEventFailure(Loc.T("Não foi possível abrir a localização do relatório de eventos"), error); }
        finally { UpdateEventButtons(); }
    }

    private void UpdateCustomDates()
    {
        var window = (EventWindowCombo.SelectedItem as EventWindowRow)?.Window ?? EventWindow.Last7Days;
        EventCustomDates.Visibility = window == EventWindow.Custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateEventStorageIdentity()
    {
        var directory = Path.GetDirectoryName(eventStore.GetEventReportPath(Guid.Empty));
        EventStorageIdentityText.Text = Loc.F($"Relatórios de eventos: {directory} · caminho configurado; disponibilidade não verificada.");
    }

    private void UpdateEventButtons()
    {
        var idle = !operations.IsBusy && !eventBusy && !busy && !hardwareBusy && !performanceBusy && !networkBusy && !graphicsBusy &&
            !correlationBusy && !monitoringBusy;
        EventRunButton.IsEnabled = idle;
        EventReloadButton.IsEnabled = idle;
        EventCancelButton.IsEnabled = eventBusy && !eventSaving &&
            eventCancellation is { IsCancellationRequested: false };
        EventWindowCombo.IsEnabled = !eventBusy && !eventSaving;
        EventFromPicker.IsEnabled = !eventBusy && !eventSaving;
        EventToPicker.IsEnabled = !eventBusy && !eventSaving;
        EventHistoryList.IsEnabled = !eventBusy;
        EventSearch.IsEnabled = selectedEventInvestigation is not null;
        EventCategoryCombo.IsEnabled = selectedEventInvestigation is not null;
        EventGroupList.IsEnabled = selectedEventInvestigation is not null;
        EventReportButton.IsEnabled = !eventBusy && selectedEventInvestigation is not null &&
            File.Exists(eventStore.GetEventReportPath(selectedEventInvestigation.Id));
        UpdatePerformanceButtons();
    }

    private void ShowEventFailure(string message, Exception error)
    {
        logFailure(error);
        EventStatusText.Text = message + ".";
        ShowWarning(Loc.F($"{message}. {error.GetType().Name}: {error.Message}"));
    }

    private static IReadOnlyList<EventCategoryRow> Categories()
    {
        var rows = new List<EventCategoryRow> { new(null, "Todas") };
        foreach (var category in Enum.GetValues<EventCategory>())
            rows.Add(new(category, EventCatalog.CategoryName(category)));
        return rows;
    }

    private static DateTimeOffset LocalStart(DateTime date)
    {
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static DateTimeOffset LocalEnd(DateTime date) => LocalStart(date.AddDays(1)).AddTicks(-1);

    private static string QualityStateName(EventQualityState state) => state switch
    {
        EventQualityState.NotApplicable => "não aplicável",
        EventQualityState.Verified => "verificada",
        EventQualityState.Affected => "afetada",
        EventQualityState.Undetermined => "indeterminada",
        _ => state.ToString()
    };

    private static string WindowName(EventWindow window) => window switch
    {
        EventWindow.Last24Hours => "24 horas",
        EventWindow.Last7Days => "7 dias",
        EventWindow.Last30Days => "30 dias",
        EventWindow.Custom => "personalizado",
        _ => window.ToString()
    };

    private static string EventCollectorName(string id) => id switch
    {
        "event-log" => "Log de Eventos",
        "wer-metadata" => "Windows Error Reporting",
        _ => id
    };

    private static string CausalityName(CausalityStatus causality) => causality switch
    {
        CausalityStatus.NotAssessed => "Não avaliada",
        _ => causality.ToString()
    };

    private static string Format(DateTimeOffset? value) => value is { } at ? Local(at) : "Indisponível";

    private static string Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? "Indisponível" : string.Join(", ", values);

    private static string Excerpt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Indisponível";
        var text = value.Replace("\r", " ").Replace("\n", " ");
        return text.Length <= 400 ? text : text[..400] + "…";
    }

    private sealed record EventWindowRow(EventWindow Window, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record EventCategoryRow(EventCategory? Category, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record EventHistoryRow(EventInvestigation Investigation, EventInvestigationQuality Quality)
    {
        public string DateText => Investigation.CompletedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
        public string SummaryText
        {
            get
            {
                var reports = Investigation.Reports.Select(report => report.ReportId)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                return $"{EventCatalog.StatusName(Investigation.Status)} · {Investigation.Groups.Count} grupo(s) · " +
                    $"{Investigation.Events.Count} registro(s) · {reports} relatório(s)";
            }
        }
        public EventQualityState QualityState => Quality.State;
        public string QualityText => Quality.State switch
        {
            EventQualityState.Affected =>
                $"Qualidade afetada · {Quality.ReportsOutsidePeriod}/{Quality.DistinctReports} relatórios WER fora do período",
            EventQualityState.Verified =>
                $"Qualidade verificada · {Quality.DistinctReports} relatório(s) WER no período",
            EventQualityState.Undetermined =>
                $"Qualidade indeterminada · {Quality.ReportsWithoutEffectiveTime}/{Quality.DistinctReports} sem horário efetivo",
            _ => "Qualidade não aplicável · sem relatórios WER"
        };
        public override string ToString() => DateText;
    }

    private sealed class EventGroupRow
    {
        public EventGroupRow(EvidenceGroup group)
        {
            Group = group;
            Subtitle = $"{EventCatalog.CategoryName(group.Category)} · {EventCatalog.SeverityName(group.Severity)} · " +
                $"confiança {EventCatalog.ConfidenceName(group.Confidence)}";
            Badge = $"{group.EventRecordCount} registro(s) · {group.DistinctReportCount} relatório(s) distinto(s)" +
                (group.RePresentationCount > 0 ? $" · {group.RePresentationCount} reapresentação(ões)" : string.Empty);
            Haystack = string.Join("\n", new[]
            {
                group.Title, group.Id, group.Provider, group.ProblemCode, group.EventId?.ToString(),
                group.Category.ToString(), EventCatalog.CategoryName(group.Category)
            }
            .Concat(group.Channels).Concat(group.Sources).Concat(group.Limitations)
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        public EvidenceGroup Group { get; }
        public string Subtitle { get; }
        public string Badge { get; }
        public string Haystack { get; }
        public string Title => Group.Title;
        public override string ToString() => Title;
    }
}
