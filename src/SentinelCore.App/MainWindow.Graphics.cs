using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows;
using SentinelCore.Persistence;

namespace SentinelCore.App;

public partial class MainWindow
{
    private IReadOnlyList<GraphicsInvestigation> graphicsInvestigations = [];
    private GraphicsInvestigation? selectedGraphics;
    private GraphicsInvestigation? unsavedGraphics;
    private CancellationTokenSource? graphicsCancellation;
    private bool graphicsBusy;
    private bool graphicsSaving;
    private bool graphicsClosingAfterSave;
    private bool updatingGraphics;

    private void InitializeGraphics()
    {
        updatingGraphics = true;
        GraphicsPresetCombo.ItemsSource = new[]
        {
            new GraphicsPresetRow(EventWindow.Last24Hours, "24 horas"),
            new GraphicsPresetRow(EventWindow.Last7Days, "7 dias"),
            new GraphicsPresetRow(EventWindow.Last30Days, "30 dias")
        };
        GraphicsPresetCombo.SelectedIndex = 1;
        updatingGraphics = false;
        ClearGraphicsView();
        UpdateGraphicsButtons();
    }

    private EventWindow SelectedGraphicsWindow() =>
        GraphicsPresetCombo.SelectedItem is GraphicsPresetRow row ? row.Window : EventWindow.Last7Days;

    private async Task ReloadGraphicsAsync(Guid? selectId = null)
    {
        var snapshot = await graphicsStore.LoadGraphicsInvestigationsAsync();
        graphicsInvestigations = snapshot.Investigations;
        SetIntegrity("Gráficos & GPU", snapshot.Warnings);
        RefreshGraphicsHistoryList();

        var target = selectId is { } id
            ? graphicsInvestigations.FirstOrDefault(item => item.Id == id)
            : unsavedGraphics
              ?? (selectedGraphics is { } current
                  ? graphicsInvestigations.FirstOrDefault(item => item.Id == current.Id)
                  : null)
              ?? graphicsInvestigations.FirstOrDefault();
        if (target is null)
        {
            ClearGraphicsView();
            return;
        }

        SelectGraphicsInHistory(target.Id);
        ApplyGraphicsInvestigation(target);
    }

    private void RefreshGraphicsHistoryList()
    {
        updatingGraphics = true;
        GraphicsHistoryList.ItemsSource = graphicsInvestigations
            .OrderByDescending(item => item.CompletedUtc)
            .Select(item => new GraphicsHistoryRow(item))
            .ToArray();
        updatingGraphics = false;
    }

    private void SelectGraphicsInHistory(Guid id)
    {
        updatingGraphics = true;
        var row = GraphicsHistoryList.Items.OfType<GraphicsHistoryRow>()
            .FirstOrDefault(item => item.Investigation.Id == id);
        GraphicsHistoryList.SelectedItem = row;
        if (row is not null) GraphicsHistoryList.ScrollIntoView(row);
        updatingGraphics = false;
    }

    private void ApplyGraphicsInvestigation(GraphicsInvestigation investigation)
    {
        selectedGraphics = investigation;
        var coverage = investigation.Coverage;
        var persisted = graphicsInvestigations.Any(item => item.Id == investigation.Id) &&
            File.Exists(graphicsStore.GetGraphicsReportPath(investigation.Id));

        GraphicsProgress.Minimum = 0;
        GraphicsProgress.Maximum = Math.Max(1, coverage.ExpectedSources);
        GraphicsProgress.Value = coverage.CompletedSources;

        GraphicsStatusText.Text =
            $"Investigação de {Local(investigation.CompletedUtc)} · {GraphicsStatusName(investigation.Status)} · " +
            $"execução {GraphicsExecutionName(coverage.Execution)} · cobertura {GraphicsCoverageName(coverage.Status)} · " +
            $"{coverage.CompletedSources}/{coverage.ExpectedSources} fontes · ID {investigation.Id:D}" +
            (persisted ? string.Empty : " · não confirmada na memória local");

        GraphicsAdapterValue.Text = investigation.Adapters.Count.ToString(CultureInfo.CurrentCulture);
        GraphicsAdapterDetail.Text = investigation.Adapters.Count == 0
            ? "nenhum adaptador enumerado"
            : string.Join(", ", investigation.Adapters.Take(2).Select(adapter => adapter.Name ?? "adaptador"));

        GraphicsMonitorValue.Text = investigation.Monitors.Count.ToString(CultureInfo.CurrentCulture);
        var activeMonitor = investigation.Monitors.FirstOrDefault(monitor => monitor.Width is > 0 && monitor.Height is > 0);
        GraphicsMonitorDetail.Text = activeMonitor is null
            ? "nenhuma resolução ativa confirmada"
            : $"{activeMonitor.Width}×{activeMonitor.Height}" +
              (activeMonitor.RefreshHz is null ? string.Empty : $" · {activeMonitor.RefreshHz} Hz");

        GraphicsEventValue.Text = investigation.Events.Count.ToString(CultureInfo.CurrentCulture);
        GraphicsEventDetail.Text = $"{investigation.WerReports.Count} relatório(s) WER · " +
            $"{investigation.Observations.Count} observação(ões)";

        var tdr = investigation.Events.Count(item => item.Kind == GraphicsEventKind.DisplayDriverReset);
        var liveKernel = GraphicsEvidenceClassification.CountLiveKernelReports(investigation.WerReports);
        GraphicsTdrValue.Text = tdr.ToString(CultureInfo.CurrentCulture);
        GraphicsTdrDetail.Text =
            $"TDR Display {GraphicsEvidenceClassification.DisplayDriverResetEventId} · " +
            $"{liveKernel} LiveKernelEvent(s) " +
            $"{string.Join("/", GraphicsEvidenceClassification.LiveKernelProblemCodes)}";

        GraphicsAdapterList.ItemsSource = investigation.Adapters.Select(item => new GraphicsAdapterRow(item)).ToArray();
        GraphicsMonitorList.ItemsSource = investigation.Monitors.Select(item => new GraphicsMonitorRow(item)).ToArray();
        GraphicsDriverList.ItemsSource = investigation.Drivers.Select(item => new GraphicsDriverRow(item)).ToArray();
        GraphicsEventList.ItemsSource = investigation.Events.Select(item => new GraphicsEventRow(item)).ToArray();
        GraphicsWerList.ItemsSource = investigation.WerReports.Select(item => new GraphicsWerRow(item)).ToArray();

        updatingGraphics = true;
        var observationRows = investigation.Observations
            .Select(item => new GraphicsObservationRow(item)).ToArray();
        GraphicsObservationList.ItemsSource = observationRows;
        GraphicsObservationList.SelectedIndex = observationRows.Length > 0 ? 0 : -1;
        updatingGraphics = false;

        if (observationRows.Length > 0)
            OnGraphicsObservationSelected(GraphicsObservationList, null!);
        else
            GraphicsObservationDetail.Text = BuildGraphicsSummaryText(investigation);

        UpdateGraphicsButtons();
    }

    private void ClearGraphicsView()
    {
        selectedGraphics = null;
        GraphicsProgress.Minimum = 0;
        GraphicsProgress.Maximum = 1;
        GraphicsProgress.Value = 0;
        GraphicsStatusText.Text =
            "Nenhuma investigação gráfica selecionada. A coleta é somente de leitura e não altera drivers, " +
            "resolução, HDR, frequência ou energia.";
        GraphicsAdapterValue.Text = "—";
        GraphicsAdapterDetail.Text = "adaptadores enumerados";
        GraphicsMonitorValue.Text = "—";
        GraphicsMonitorDetail.Text = "monitores ativos";
        GraphicsEventValue.Text = "—";
        GraphicsEventDetail.Text = "eventos e metadados WER";
        GraphicsTdrValue.Text = "—";
        GraphicsTdrDetail.Text = "TDR e LiveKernelEvent";
        GraphicsAdapterList.ItemsSource = null;
        GraphicsMonitorList.ItemsSource = null;
        GraphicsDriverList.ItemsSource = null;
        GraphicsEventList.ItemsSource = null;
        GraphicsWerList.ItemsSource = null;
        updatingGraphics = true;
        GraphicsObservationList.ItemsSource = null;
        updatingGraphics = false;
        GraphicsObservationDetail.Text =
            "Nenhuma observação gráfica selecionada. Um evento não confirma causa, defeito físico ou correlação " +
            "com tela preta.";
        UpdateGraphicsButtons();
    }

    private static string BuildGraphicsSummaryText(GraphicsInvestigation investigation)
    {
        var text = new StringBuilder();
        text.AppendLine("Nenhuma observação sustentada nesta investigação.");
        text.AppendLine("Causalidade não avaliada.");
        text.AppendLine();
        text.AppendLine($"Adaptadores: {investigation.Adapters.Count} · monitores: {investigation.Monitors.Count} · " +
            $"drivers: {investigation.Drivers.Count} · eventos: {investigation.Events.Count} · " +
            $"WER: {investigation.WerReports.Count}");
        text.AppendLine($"Execução: {GraphicsExecutionName(investigation.Coverage.Execution)} · " +
            $"cobertura: {GraphicsCoverageName(investigation.Coverage.Status)}");

        var truncatedMessages = investigation.Events.Count(item => item.MessageTruncated);
        if (truncatedMessages > 0)
            text.AppendLine($"Mensagens truncadas: {truncatedMessages} de {investigation.Events.Count}. " +
                "O texto exibido é parcial; o conteúdo ausente não foi reconstruído.");

        if (investigation.Changes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Alterações observadas");
            foreach (var change in investigation.Changes)
                text.AppendLine($"• {GraphicsChangeTypeName(change.Type)}: {change.SubjectName ?? change.Subject}" +
                    (change.Field is null ? string.Empty : $" · {change.Field}: {change.Before ?? "—"} → {change.After ?? "—"}"));
        }

        if (investigation.AnalysisNotes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Notas de análise");
            foreach (var note in investigation.AnalysisNotes)
                text.AppendLine($"• {note.RuleId}: {note.Detail}");
        }

        if (investigation.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Limitações");
            foreach (var limitation in investigation.Limitations)
                text.AppendLine($"• {limitation}");
        }

        return text.ToString();
    }

    private static string Local(DateTimeOffset? at) =>
        at is { } value ? Local(value) : "indisponível";

    private async void OnGraphicsRun(object sender, RoutedEventArgs e)
    {
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        if (graphicsBusy || busy || hardwareBusy || eventBusy || performanceBusy || networkBusy || correlationBusy || monitoringBusy)
            return;

        if (unsavedGraphics is not null)
        {
            graphicsBusy = true;
            graphicsSaving = true;
            UpdateGraphicsButtons();
            UpdatePerformanceButtons();
            UpdateNetworkButtons();
            UpdateHardwareButtons();
            UpdateEventButtons();
            GraphicsStatusText.Text = "Tentando salvar novamente a investigação pendente…";
            try
            {
                var pending = unsavedGraphics;
                await graphicsStore.SaveGraphicsInvestigationAsync(pending, CancellationToken.None);
                unsavedGraphics = null;
                await ReloadGraphicsAsync(pending.Id);
                GraphicsStatusText.Text = "Investigação pendente salva novamente.";
            }
            catch (Exception error)
            {
                ShowGraphicsFailure("Falha de gravação da investigação. O resultado permanece nesta janela; " +
                    "tente salvar novamente", error);
            }
            finally
            {
                graphicsBusy = false;
                graphicsSaving = false;
                GraphicsRunButton.Content = unsavedGraphics is null ? "Investigar gráficos" : "Tentar salvar investigação";
                UpdateGraphicsButtons();
                UpdatePerformanceButtons();
                UpdateNetworkButtons();
                UpdateHardwareButtons();
                UpdateEventButtons();
                var closeNow = graphicsClosingAfterSave && unsavedGraphics is null;
                graphicsClosingAfterSave = false;
                if (closeNow) Close();
            }
            return;
        }

        var previous = selectedGraphics ?? graphicsInvestigations.FirstOrDefault();
        graphicsBusy = true;
        graphicsSaving = false;
        graphicsCancellation = new CancellationTokenSource();
        GraphicsRunButton.Content = "Investigar gráficos";
        GraphicsProgress.Minimum = 0;
        GraphicsProgress.Maximum = 1;
        GraphicsProgress.Value = 0;
        ClearRunWarnings();
        UpdateGraphicsButtons();
        UpdatePerformanceButtons();
        UpdateNetworkButtons();
        UpdateHardwareButtons();
        UpdateEventButtons();
        var saved = false;
        try
        {
            GraphicsStatusText.Text = "Investigação gráfica em andamento. Leitura somente; nenhuma configuração será alterada.";
            var progress = new Progress<GraphicsProgress>(OnGraphicsProgress);
            var request = new GraphicsInvestigationRequest(SelectedGraphicsWindow());
            var investigation = await graphicsRunner.RunAsync(request, previous, progress,
                graphicsCancellation.Token);
            unsavedGraphics = investigation;
            ApplyGraphicsInvestigation(investigation);
            graphicsSaving = true;
            UpdateGraphicsButtons();
            GraphicsStatusText.Text = "Salvando investigação e relatório na memória local…";
            await graphicsStore.SaveGraphicsInvestigationAsync(investigation, CancellationToken.None);
            saved = true;
            unsavedGraphics = null;
            await ReloadGraphicsAsync(investigation.Id);
            GraphicsStatusText.Text =
                $"Investigação {GraphicsStatusName(investigation.Status).ToLowerInvariant()} salva. " +
                $"{investigation.Adapters.Count} adaptador(es), {investigation.Monitors.Count} monitor(es), " +
                $"{investigation.Events.Count} evento(s), {investigation.WerReports.Count} relatório(s) WER, " +
                $"{investigation.Observations.Count} observação(ões). Causalidade não avaliada.";
        }
        catch (Exception error)
        {
            var message = unsavedGraphics is not null
                ? "Falha de gravação da investigação. O resultado permanece nesta janela; tente salvar novamente"
                : saved
                    ? "A investigação foi salva, mas a memória local não pôde ser relida. Reabra o aplicativo"
                    : "A investigação gráfica não pôde ser concluída";
            ShowGraphicsFailure(message, error);
        }
        finally
        {
            graphicsBusy = false;
            graphicsSaving = false;
            graphicsCancellation?.Dispose();
            graphicsCancellation = null;
            GraphicsRunButton.Content = unsavedGraphics is null ? "Investigar gráficos" : "Tentar salvar investigação";
            UpdateGraphicsButtons();
            UpdatePerformanceButtons();
            UpdateNetworkButtons();
            UpdateHardwareButtons();
            UpdateEventButtons();
            var closeNow = graphicsClosingAfterSave && unsavedGraphics is null;
            graphicsClosingAfterSave = false;
            if (closeNow) Close();
        }
    }

    private void OnGraphicsCancel(object sender, RoutedEventArgs e)
    {
        graphicsCancellation?.Cancel();
        GraphicsCancelButton.IsEnabled = false;
        GraphicsStatusText.Text = "Cancelamento solicitado. Aguardando a leitura gráfica em andamento…";
    }

    private async void OnGraphicsReport(object sender, RoutedEventArgs e)
    {
        if (selectedGraphics is not { } investigation) return;
        try
        {
            GraphicsReportButton.IsEnabled = false;
            await WindowsShell.OpenReportLocationAsync(graphicsStore.GetGraphicsReportPath(investigation.Id));
        }
        catch (Exception error)
        {
            ShowGraphicsFailure("Não foi possível abrir a localização do relatório gráfico", error);
        }
        finally
        {
            UpdateGraphicsButtons();
        }
    }

    private void OnGraphicsPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingGraphics) return;
        UpdateGraphicsButtons();
    }

    private void OnGraphicsProgress(GraphicsProgress update)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnGraphicsProgress(update));
            return;
        }

        if (graphicsSaving) return;
        GraphicsProgress.Maximum = Math.Max(1, update.Total);
        GraphicsProgress.Value = update.Finished;
        GraphicsStatusText.Text = $"{update.Phase}: {update.SourceId} — {GraphicsSourceStatusName(update.Status)} · " +
            $"{update.Finished}/{update.Total} fonte(s) · leitura somente";
    }

    private void OnGraphicsHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingGraphics) return;
        if (GraphicsHistoryList.SelectedItem is GraphicsHistoryRow row)
            ApplyGraphicsInvestigation(row.Investigation);
    }

    private void OnGraphicsObservationSelected(object sender, SelectionChangedEventArgs? e)
    {
        if (updatingGraphics) return;
        if (GraphicsObservationList.SelectedItem is not GraphicsObservationRow row)
        {
            GraphicsObservationDetail.Text = selectedGraphics is { } current
                ? BuildGraphicsSummaryText(current)
                : string.Empty;
            return;
        }

        var observation = row.Observation;
        var text = new StringBuilder();
        text.AppendLine(observation.Title);
        text.AppendLine($"Regra: {observation.RuleId} {observation.RuleVersion} · tipo: " +
            $"{GraphicsObservationKindName(observation.Kind)}");
        text.AppendLine($"Severidade: {EventCatalog.SeverityName(observation.Severity)} · " +
            $"confiança: {EventCatalog.ConfidenceName(observation.Confidence)} · causalidade: não avaliada");
        text.AppendLine();
        text.AppendLine(observation.Statement);
        text.AppendLine();
        text.AppendLine($"Métrica: {observation.Metric}" +
            (observation.ObservedValue is { } value
                ? $" · valor: {value.ToString("0.###", CultureInfo.CurrentCulture)} {observation.Unit}"
                : string.Empty));
        text.AppendLine($"Janela solicitada: {Local(observation.WindowFromUtc)} até {Local(observation.WindowToUtc)}");
        text.AppendLine($"Primeira observação: {Local(observation.FirstObservedUtc)}");
        text.AppendLine($"Última observação: {Local(observation.LastObservedUtc)}");
        text.AppendLine($"Evidências de suporte: {observation.EvidenceCount}");
        text.AppendLine($"Fonte: {observation.Source}");
        if (observation.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Limitações");
            foreach (var limitation in observation.Limitations)
                text.AppendLine($"• {limitation}");
        }

        GraphicsObservationDetail.Text = text.ToString();
    }

    private void UpdateGraphicsButtons()
    {
        var idle = !operations.IsBusy && !graphicsBusy && !busy && !hardwareBusy && !eventBusy && !performanceBusy && !networkBusy &&
            !correlationBusy && !monitoringBusy;
        GraphicsRunButton.IsEnabled = idle;
        GraphicsCancelButton.IsEnabled = graphicsBusy && !graphicsSaving &&
            graphicsCancellation is { IsCancellationRequested: false };
        GraphicsPresetCombo.IsEnabled = !graphicsBusy && !graphicsSaving;
        GraphicsHistoryList.IsEnabled = !graphicsBusy;
        GraphicsReportButton.IsEnabled = !graphicsBusy && selectedGraphics is not null &&
            File.Exists(graphicsStore.GetGraphicsReportPath(selectedGraphics.Id));
        UpdateCorrelationButtons();
    }

    private void ShowGraphicsFailure(string message, Exception error)
    {
        logFailure(error);
        GraphicsStatusText.Text = message + ".";
        ShowWarning($"{message}. {error.GetType().Name}: {error.Message}");
    }

    private static string GraphicsStatusName(GraphicsInvestigationStatus status) => status switch
    {
        GraphicsInvestigationStatus.Running => "Em andamento",
        GraphicsInvestigationStatus.Completed => "Concluída",
        GraphicsInvestigationStatus.Partial => "Concluída com cobertura parcial",
        GraphicsInvestigationStatus.Cancelled => "Cancelada",
        GraphicsInvestigationStatus.Failed => "Falhou",
        GraphicsInvestigationStatus.Unsupported => "Não suportada",
        _ => status.ToString()
    };

    private static string GraphicsExecutionName(GraphicsExecutionStatus status) => status switch
    {
        GraphicsExecutionStatus.Running => "em andamento",
        GraphicsExecutionStatus.Completed => "concluída",
        GraphicsExecutionStatus.Cancelled => "cancelada",
        GraphicsExecutionStatus.Failed => "falhou",
        GraphicsExecutionStatus.Unsupported => "não suportada",
        _ => status.ToString()
    };

    private static string GraphicsCoverageName(GraphicsCoverageStatus status) => status switch
    {
        GraphicsCoverageStatus.Complete => "completa",
        GraphicsCoverageStatus.Partial => "parcial",
        GraphicsCoverageStatus.Unavailable => "indisponível",
        _ => status.ToString()
    };

    private static string GraphicsSourceStatusName(GraphicsSourceStatus status) => status switch
    {
        GraphicsSourceStatus.Running => "em andamento",
        GraphicsSourceStatus.Completed => "concluída",
        GraphicsSourceStatus.Partial => "parcial",
        GraphicsSourceStatus.Failed => "falhou",
        GraphicsSourceStatus.Unsupported => "não suportada",
        GraphicsSourceStatus.Cancelled => "cancelada",
        _ => status.ToString()
    };

    private static string GraphicsEventKindName(GraphicsEventKind kind) => kind switch
    {
        GraphicsEventKind.DisplayDriverReset =>
            $"TDR — Display {GraphicsEvidenceClassification.DisplayDriverResetEventId} registrado",
        GraphicsEventKind.DisplayDriverOther => "Outro evento do provedor Display",
        GraphicsEventKind.GraphicsDriverVendor => "Evento de driver gráfico",
        _ => "Evento gráfico"
    };

    private static string GraphicsWerKindName(GraphicsWerKind kind) => kind switch
    {
        GraphicsWerKind.LiveKernelEvent117 =>
            $"LiveKernelEvent {GraphicsEvidenceClassification.LiveKernelEvent117}",
        GraphicsWerKind.LiveKernelEvent141 =>
            $"LiveKernelEvent {GraphicsEvidenceClassification.LiveKernelEvent141}",
        GraphicsWerKind.LiveKernelEvent142 =>
            $"LiveKernelEvent {GraphicsEvidenceClassification.LiveKernelEvent142}",
        GraphicsWerKind.LiveKernelEventOther => "LiveKernelEvent (outro código)",
        _ => "Relatório gráfico"
    };

    private static string GraphicsObservationKindName(GraphicsObservationKind kind) => kind switch
    {
        GraphicsObservationKind.DisplayDriverReset => "reinício do driver",
        GraphicsObservationKind.LiveKernelEvent => "LiveKernelEvent",
        GraphicsObservationKind.MultipleAdapters => "múltiplos adaptadores",
        GraphicsObservationKind.AdapterState => "estado de adaptador",
        GraphicsObservationKind.MonitorTopology => "topologia de monitores",
        GraphicsObservationKind.AdapterChange => "alteração de adaptador",
        GraphicsObservationKind.DriverChange => "alteração de driver",
        GraphicsObservationKind.MonitorChange => "alteração de monitor",
        _ => "metadados WER"
    };

    private static string GraphicsChangeTypeName(GraphicsChangeType type) => type switch
    {
        GraphicsChangeType.AdapterAdded => "Adaptador adicionado",
        GraphicsChangeType.AdapterRemoved => "Adaptador removido",
        GraphicsChangeType.MonitorAdded => "Monitor adicionado",
        GraphicsChangeType.MonitorRemoved => "Monitor removido",
        GraphicsChangeType.DriverChanged => "Driver alterado",
        GraphicsChangeType.ConfigurationChanged => "Configuração alterada",
        _ => "Estado alterado"
    };

    private sealed record GraphicsPresetRow(EventWindow Window, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record GraphicsHistoryRow(GraphicsInvestigation Investigation)
    {
        public string DateText => Investigation.CompletedUtc.ToLocalTime()
            .ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture);
        public string SummaryText =>
            $"{GraphicsStatusName(Investigation.Status)} · cobertura " +
            $"{GraphicsCoverageName(Investigation.Coverage.Status)} · {Investigation.Adapters.Count} adaptador(es) · " +
            $"{Investigation.Events.Count} evento(s) · {Investigation.Observations.Count} obs.";
    }

    private sealed record GraphicsAdapterRow(GraphicsAdapter Adapter)
    {
        public string Name => Adapter.Name ?? "Adaptador";
        public string Provider => Adapter.DriverProvider ?? "Indisponível";
        public string Version => Adapter.DriverVersion ?? "—";
        public string State => Adapter.WindowsState ?? "Indisponível";
    }

    private sealed record GraphicsMonitorRow(GraphicsMonitor Monitor)
    {
        public string Name => Monitor.Name ?? "Monitor";
        public string Resolution => Monitor.Width is { } width && Monitor.Height is { } height
            ? $"{width}×{height}"
            : "Indisponível";
        public string Refresh => Monitor.RefreshHz is null ? "—" : $"{Monitor.RefreshHz} Hz";
        public string Detail =>
            $"{Monitor.Connector ?? "conexão indisponível"} · HDR " +
            (Monitor.HdrEnabled is null ? "indisponível" : Monitor.HdrEnabled.Value ? "ativo" : "inativo");
    }

    private sealed record GraphicsDriverRow(GraphicsDriver Driver)
    {
        public string Name => Driver.Name ?? "Driver";
        public string Provider => Driver.Provider ?? "Indisponível";
        public string Version => Driver.Version ?? "—";
        public string Detail =>
            $"{(Driver.DateUtc is { } date ? date.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture) : "data indisponível")} · " +
            $"{Driver.InfName ?? "INF indisponível"}";
    }

    private sealed record GraphicsEventRow(GraphicsEventEvidence Evidence)
    {
        public string Time => Evidence.OccurredUtc.ToLocalTime()
            .ToString("dd/MM HH:mm:ss", CultureInfo.CurrentCulture);
        public string Provider => Evidence.Provider;
        public string EventId => Evidence.EventId.ToString(CultureInfo.CurrentCulture);
        public string Classification => GraphicsEventKindName(Evidence.Kind);
        public string Message => GraphicsMessagePresentation.Text(Evidence.Message, Evidence.MessageTruncated);
    }

    private sealed record GraphicsWerRow(GraphicsWerEvidence Evidence)
    {
        public string Time => (Evidence.EventTimeUtc ?? Evidence.CreationTimeUtc)?.ToLocalTime()
            .ToString("dd/MM HH:mm:ss", CultureInfo.CurrentCulture) ?? "—";
        public string Type => Evidence.EventType ?? "Indisponível";
        public string Code => Evidence.ProblemCode ?? "—";
        public string Detail => $"{GraphicsWerKindName(Evidence.Kind)} · {Evidence.Application ?? "aplicativo indisponível"}";
    }

    private sealed record GraphicsObservationRow(GraphicsObservation Observation)
    {
        public string Title => Observation.Title;
        public string Subtitle =>
            $"{GraphicsObservationKindName(Observation.Kind)} · {EventCatalog.SeverityName(Observation.Severity)} · " +
            $"confiança {EventCatalog.ConfidenceName(Observation.Confidence)} · causalidade não avaliada";
    }
}
