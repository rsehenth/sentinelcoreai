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
    private IReadOnlyList<CorrelationInvestigation> correlationInvestigations = [];
    private CorrelationInvestigation? selectedCorrelation;
    private CorrelationInvestigation? unsavedCorrelation;
    private CancellationTokenSource? correlationCancellation;
    private bool correlationBusy;
    private bool correlationSaving;
    private bool correlationClosingAfterSave;
    private bool updatingCorrelation;

    private void InitializeCorrelation()
    {
        updatingCorrelation = true;
        CorrelationPresetCombo.ItemsSource = new[]
        {
            new CorrelationPresetRow(EventWindow.Last24Hours, "24 horas"),
            new CorrelationPresetRow(EventWindow.Last7Days, "7 dias"),
            new CorrelationPresetRow(EventWindow.Last30Days, "30 dias")
        };
        CorrelationPresetCombo.SelectedIndex = 1;
        updatingCorrelation = false;
        ClearCorrelationView();
        UpdateCorrelationButtons();
    }

    private EventWindow SelectedCorrelationWindow() =>
        CorrelationPresetCombo.SelectedItem is CorrelationPresetRow row ? row.Window : EventWindow.Last7Days;

    private CorrelationSources SelectedCorrelationSources()
    {
        var sources = CorrelationSources.None;
        if (CorrelationAuditCheck.IsChecked == true) sources |= CorrelationSources.Audits;
        if (CorrelationHardwareCheck.IsChecked == true) sources |= CorrelationSources.Hardware;
        if (CorrelationEventCheck.IsChecked == true) sources |= CorrelationSources.Events;
        if (CorrelationPerformanceCheck.IsChecked == true) sources |= CorrelationSources.Performance;
        if (CorrelationNetworkCheck.IsChecked == true) sources |= CorrelationSources.Network;
        if (CorrelationGraphicsCheck.IsChecked == true) sources |= CorrelationSources.Graphics;
        if (CorrelationMonitoringCheck.IsChecked == true) sources |= CorrelationSources.Monitoring;
        return sources;
    }

    private async Task ReloadCorrelationAsync(Guid? selectId = null)
    {
        var snapshot = await correlationStore.LoadCorrelationInvestigationsAsync();
        correlationInvestigations = snapshot.Investigations;
        if (snapshot.Warnings.Count > 0) ShowWarning(string.Join("\n", snapshot.Warnings));
        RefreshCorrelationHistoryList();

        var target = selectId is { } id
            ? correlationInvestigations.FirstOrDefault(item => item.Id == id)
            : unsavedCorrelation
              ?? (selectedCorrelation is { } current
                  ? correlationInvestigations.FirstOrDefault(item => item.Id == current.Id)
                  : null)
              ?? correlationInvestigations.FirstOrDefault();
        if (target is null)
        {
            ClearCorrelationView();
            return;
        }

        SelectCorrelationInHistory(target.Id);
        ApplyCorrelation(target);
    }

    private void RefreshCorrelationHistoryList()
    {
        updatingCorrelation = true;
        CorrelationHistoryList.ItemsSource = correlationInvestigations
            .OrderByDescending(item => item.CompletedUtc)
            .Select(item => new CorrelationHistoryRow(item))
            .ToArray();
        updatingCorrelation = false;
    }

    private void SelectCorrelationInHistory(Guid id)
    {
        updatingCorrelation = true;
        var row = CorrelationHistoryList.Items.OfType<CorrelationHistoryRow>()
            .FirstOrDefault(item => item.Investigation.Id == id);
        CorrelationHistoryList.SelectedItem = row;
        if (row is not null) CorrelationHistoryList.ScrollIntoView(row);
        updatingCorrelation = false;
    }

    private void ApplyCorrelation(CorrelationInvestigation investigation)
    {
        selectedCorrelation = investigation;
        var coverage = investigation.Coverage;
        var persisted = correlationInvestigations.Any(item => item.Id == investigation.Id) &&
            File.Exists(correlationStore.GetCorrelationReportPath(investigation.Id));

        CorrelationProgress.Minimum = 0;
        CorrelationProgress.Maximum = Math.Max(1, coverage.ExpectedSources);
        CorrelationProgress.Value = coverage.CompletedSources;

        CorrelationInvestigationInfo.Text =
            $"Investigação de {Local(investigation.CompletedUtc)} · {CorrelationStatusName(investigation.Status)} · " +
            $"cobertura {CorrelationCoverageName(coverage.Status)} · " +
            $"{coverage.CompletedSources}/{coverage.ExpectedSources} fontes · ID {investigation.Id:D}" +
            (persisted ? string.Empty : " · não confirmada na memória local");

        CorrelationSourceValue.Text =
            $"{coverage.CompletedSources}/{coverage.ExpectedSources}";
        CorrelationSourceDetail.Text =
            $"{coverage.PartialSources} parcial(is) · {coverage.UnavailableSources} indisponível(is) · " +
            $"{coverage.CancelledSources} cancelada(s)";

        CorrelationEvidenceValue.Text = coverage.DistinctOccurrences.ToString(CultureInfo.CurrentCulture);
        CorrelationEvidenceCardDetail.Text = coverage.MergedReferences == 0
            ? $"{coverage.EvidenceFound} referência(s) de origem"
            : $"{coverage.EvidenceFound} referência(s) · {coverage.UndatedEvidence} sem horário confiável";

        CorrelationMergedValue.Text = coverage.MergedReferences.ToString(CultureInfo.CurrentCulture);
        CorrelationMergedDetail.Text = coverage.MergedReferences == 0
            ? "nenhuma ocorrência repetida entre módulos"
            : "representações consolidadas em uma ocorrência lógica";

        var associations = investigation.Relations.Count(relation => relation.IsHypothesis);
        CorrelationRelationValue.Text = associations.ToString(CultureInfo.CurrentCulture);
        CorrelationRelationCardDetail.Text =
            $"{(coverage.LimitHit ? "limite interno atingido" : "sem limite interno")} · " +
            $"{investigation.Timeline.Count} item(ns) na linha do tempo";

        CorrelationTimelineList.ItemsSource = investigation.Timeline
            .Select(item => new CorrelationTimelineRow(item)).ToArray();
        CorrelationSourceList.ItemsSource = investigation.Sources
            .Select(item => new CorrelationSourceRow(item)).ToArray();

        updatingCorrelation = true;
        var relationRows = investigation.Relations.Select(item => new CorrelationRelationRow(item)).ToArray();
        CorrelationRelationList.ItemsSource = relationRows;
        CorrelationRelationList.SelectedIndex = relationRows.Length > 0 ? 0 : -1;
        var evidenceRows = investigation.Evidence.Select(item => new CorrelationEvidenceRow(item)).ToArray();
        CorrelationEvidenceList.ItemsSource = evidenceRows;
        CorrelationEvidenceList.SelectedIndex = evidenceRows.Length > 0 ? 0 : -1;
        CorrelationSourceList.SelectedIndex = investigation.Sources.Count > 0 ? 0 : -1;
        updatingCorrelation = false;

        if (relationRows.Length > 0) OnCorrelationRelationSelected(CorrelationRelationList, null!);
        else CorrelationRelationDetail.Text = BuildCorrelationSummaryText(investigation);

        if (evidenceRows.Length > 0) OnCorrelationEvidenceSelected(CorrelationEvidenceList, null!);
        else CorrelationEvidenceDetail.Text = BuildCorrelationSummaryText(investigation);

        CorrelationCoverageDetail.Text = BuildCorrelationCoverageText(investigation);

        UpdateCorrelationButtons();
    }

    private void ClearCorrelationView()
    {
        selectedCorrelation = null;
        CorrelationProgress.Minimum = 0;
        CorrelationProgress.Maximum = 3;
        CorrelationProgress.Value = 0;
        CorrelationInvestigationInfo.Text = "Nenhuma investigação de correlação realizada.";
        CorrelationStatusText.Text =
            "Selecione um período e as fontes. A correlação reutiliza somente registros históricos; nenhuma coleta " +
            "nova é executada e nenhuma configuração do Windows é alterada.";
        CorrelationSourceValue.Text = "—";
        CorrelationSourceDetail.Text = "fontes consultadas";
        CorrelationEvidenceValue.Text = "—";
        CorrelationEvidenceCardDetail.Text = "referências de evidência";
        CorrelationMergedValue.Text = "—";
        CorrelationMergedDetail.Text = "deduplicação entre módulos";
        CorrelationRelationValue.Text = "—";
        CorrelationRelationCardDetail.Text = "relações identificadas";
        CorrelationTimelineList.ItemsSource = null;
        updatingCorrelation = true;
        CorrelationRelationList.ItemsSource = null;
        CorrelationEvidenceList.ItemsSource = null;
        CorrelationSourceList.ItemsSource = null;
        updatingCorrelation = false;
        CorrelationRelationDetail.Text =
            "Nenhuma relação selecionada. Uma associação temporal ou de identidade não demonstra causa.";
        CorrelationEvidenceDetail.Text =
            "Nenhuma evidência selecionada. Cada evidência mantém a referência ao registro de origem.";
        CorrelationCoverageDetail.Text =
            "Nenhuma cobertura consultada. Fontes indisponíveis e limites são declarados, nunca convertidos em " +
            "ausência de acontecimento.";
        UpdateCorrelationButtons();
    }

    private static string BuildCorrelationSummaryText(CorrelationInvestigation investigation)
    {
        var text = new StringBuilder();
        var associations = investigation.Relations.Count(relation => relation.IsHypothesis);
        text.AppendLine(associations == 0
            ? $"{CorrelationGuidance.InsufficientEvidenceStatement}."
            : $"{associations} associação(ões) determinística(s) identificada(s).");
        text.AppendLine("Causalidade não avaliada.");
        text.AppendLine();
        text.AppendLine($"Referências de evidência: {investigation.Coverage.EvidenceFound} · ocorrências distintas: " +
            $"{investigation.Coverage.DistinctOccurrences} · consolidadas: {investigation.Coverage.MergedReferences}");
        text.AppendLine($"Linha do tempo: {investigation.Timeline.Count} item(ns) · sem horário confiável: " +
            $"{investigation.Coverage.UndatedEvidence}");
        if (investigation.Boot.LastBootUtc is { } boot)
            text.AppendLine($"Inicialização do Windows: {Local(boot)}");
        else
            text.AppendLine("Inicialização do Windows indisponível; sessões distintas não puderam ser separadas.");
        if (investigation.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Limitações");
            foreach (var limitation in investigation.Limitations)
                text.AppendLine($"• {limitation}");
        }
        return text.ToString();
    }

    private static string BuildCorrelationCoverageText(CorrelationInvestigation investigation)
    {
        var text = new StringBuilder();
        var coverage = investigation.Coverage;
        text.AppendLine($"Cobertura {CorrelationCoverageName(coverage.Status)} · " +
            $"{coverage.CompletedSources} concluída(s) · {coverage.PartialSources} parcial(is) · " +
            $"{coverage.UnavailableSources} indisponível(is) · {coverage.CancelledSources} cancelada(s)");
        text.AppendLine($"Evidências: {coverage.DistinctOccurrences} distinta(s) de {coverage.EvidenceFound} referência(s)");
        text.AppendLine(coverage.LimitHit
            ? "Limite interno de processamento atingido: a investigação é parcial."
            : "Nenhum limite interno de processamento atingido.");
        text.AppendLine();
        foreach (var source in investigation.Sources)
        {
            text.AppendLine($"{CorrelationGuidance.SourceName(source.Source)}: {CorrelationSourceStatusName(source.Status)} · " +
                $"{source.RecordsRead} lido(s) · {source.RecordsUsed} usado(s) · {source.EvidenceCount} referência(s)");
            if (source.Note is not null) text.AppendLine($"  {source.Note}");
            foreach (var limitation in source.Limitations) text.AppendLine($"  Limitação: {limitation}");
        }
        if (investigation.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Limitações da investigação");
            foreach (var limitation in investigation.Limitations)
                text.AppendLine($"• {limitation}");
        }
        return text.ToString();
    }

    private async void OnCorrelationRun(object sender, RoutedEventArgs e)
    {
        if (correlationBusy || busy || hardwareBusy || eventBusy || performanceBusy || networkBusy || graphicsBusy)
            return;

        var sources = SelectedCorrelationSources();
        if (sources == CorrelationSources.None)
        {
            ShowWarning("Selecione ao menos uma fonte para correlacionar.");
            return;
        }

        if (unsavedCorrelation is not null)
        {
            correlationBusy = true;
            correlationSaving = true;
            UpdateAllBusyButtons();
            CorrelationStatusText.Text = "Tentando salvar novamente a correlação pendente…";
            try
            {
                var pending = unsavedCorrelation;
                await correlationStore.SaveCorrelationInvestigationAsync(pending, CancellationToken.None);
                unsavedCorrelation = null;
                await ReloadCorrelationAsync(pending.Id);
                CorrelationStatusText.Text = "Correlação pendente salva novamente.";
            }
            catch (Exception error)
            {
                ShowCorrelationFailure("Falha de gravação da correlação. O resultado permanece nesta janela; " +
                    "tente salvar novamente", error);
            }
            finally
            {
                correlationBusy = false;
                correlationSaving = false;
                CorrelationRunButton.Content = unsavedCorrelation is null ? "Correlacionar" : "Tentar salvar correlação";
                UpdateAllBusyButtons();
                var closeNow = correlationClosingAfterSave && unsavedCorrelation is null;
                correlationClosingAfterSave = false;
                if (closeNow) Close();
            }
            return;
        }

        correlationBusy = true;
        correlationSaving = false;
        correlationCancellation = new CancellationTokenSource();
        WarningBanner.Visibility = Visibility.Collapsed;
        CorrelationProgress.Minimum = 0;
        CorrelationProgress.Maximum = 3;
        CorrelationProgress.Value = 0;
        UpdateAllBusyButtons();
        var saved = false;
        try
        {
            CorrelationStatusText.Text =
                "Correlação em andamento. Leitura somente dos registros históricos; nenhuma coleta nova é executada.";
            var progress = new Progress<CorrelationProgress>(OnCorrelationProgress);
            var request = new CorrelationRequest(SelectedCorrelationWindow(), Sources: sources);
            var investigation = await correlationRunner.RunAsync(request, progress, correlationCancellation.Token);
            unsavedCorrelation = investigation;
            ApplyCorrelation(investigation);
            correlationSaving = true;
            UpdateAllBusyButtons();
            CorrelationStatusText.Text = "Salvando investigação e relatório na memória local…";
            await correlationStore.SaveCorrelationInvestigationAsync(investigation, CancellationToken.None);
            saved = true;
            unsavedCorrelation = null;
            await ReloadCorrelationAsync(investigation.Id);
            CorrelationStatusText.Text =
                $"Correlação {CorrelationStatusName(investigation.Status).ToLowerInvariant()} salva. " +
                $"{investigation.Coverage.EvidenceFound} referência(s) de evidência, " +
                $"{investigation.Coverage.MergedReferences} consolidada(s), " +
                $"{investigation.Relations.Count(relation => relation.IsHypothesis)} relação(ões). " +
                "Causalidade não avaliada.";
        }
        catch (Exception error)
        {
            var message = unsavedCorrelation is not null
                ? "Falha de gravação da correlação. O resultado permanece nesta janela; tente salvar novamente"
                : saved
                    ? "A correlação foi salva, mas a memória local não pôde ser relida. Reabra o aplicativo"
                    : "A investigação de correlação não pôde ser concluída";
            ShowCorrelationFailure(message, error);
        }
        finally
        {
            correlationBusy = false;
            correlationSaving = false;
            correlationCancellation?.Dispose();
            correlationCancellation = null;
            CorrelationRunButton.Content = unsavedCorrelation is null ? "Correlacionar" : "Tentar salvar correlação";
            UpdateAllBusyButtons();
            var closeNow = correlationClosingAfterSave && unsavedCorrelation is null;
            correlationClosingAfterSave = false;
            if (closeNow) Close();
        }
    }

    private void OnCorrelationCancel(object sender, RoutedEventArgs e)
    {
        correlationCancellation?.Cancel();
        CorrelationCancelButton.IsEnabled = false;
        CorrelationStatusText.Text = "Cancelamento solicitado. Aguardando a leitura histórica em andamento…";
    }

    private async void OnCorrelationReport(object sender, RoutedEventArgs e)
    {
        if (selectedCorrelation is not { } investigation) return;
        try
        {
            CorrelationReportButton.IsEnabled = false;
            await WindowsShell.OpenReportLocationAsync(correlationStore.GetCorrelationReportPath(investigation.Id));
        }
        catch (Exception error)
        {
            ShowCorrelationFailure("Não foi possível abrir a localização do relatório de correlação", error);
        }
        finally
        {
            UpdateCorrelationButtons();
        }
    }

    private void OnCorrelationPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingCorrelation) return;
        UpdateCorrelationButtons();
    }

    private void OnCorrelationSourceChanged(object sender, RoutedEventArgs e)
    {
        if (updatingCorrelation) return;
        UpdateCorrelationButtons();
    }

    private void OnCorrelationProgress(CorrelationProgress update)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnCorrelationProgress(update));
            return;
        }

        if (correlationSaving) return;
        CorrelationProgress.Maximum = Math.Max(1, update.Total);
        CorrelationProgress.Value = update.Finished;
        CorrelationStatusText.Text = $"{char.ToUpperInvariant(update.Phase[0])}{update.Phase[1..]} · " +
            $"{update.Finished}/{update.Total} · leitura somente";
    }

    private void OnCorrelationHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingCorrelation) return;
        if (CorrelationHistoryList.SelectedItem is CorrelationHistoryRow row)
            ApplyCorrelation(row.Investigation);
    }

    private void OnCorrelationRelationSelected(object sender, SelectionChangedEventArgs? e)
    {
        if (updatingCorrelation) return;
        if (CorrelationRelationList.SelectedItem is not CorrelationRelationRow row)
        {
            CorrelationRelationDetail.Text = selectedCorrelation is { } current
                ? BuildCorrelationSummaryText(current)
                : string.Empty;
            return;
        }

        var relation = row.Relation;
        var text = new StringBuilder();
        text.AppendLine(relation.Title);
        text.AppendLine($"Regra: {relation.RuleId} {relation.RuleVersion} · tipo: " +
            $"{CorrelationGuidance.RelationKindName(relation.Kind)}");
        text.AppendLine($"Severidade: {EventCatalog.SeverityName(relation.Severity)} · " +
            $"confiança: {EventCatalog.ConfidenceName(relation.Confidence)} · causalidade: não avaliada");
        text.AppendLine();
        text.AppendLine(relation.Statement);
        text.AppendLine();
        text.AppendLine($"Justificativa: {relation.Justification}");
        text.AppendLine($"Janela: {Local(relation.WindowFromUtc)} até {Local(relation.WindowToUtc)}");
        if (relation.MaximumGapSeconds is { } gap)
            text.AppendLine($"Diferença temporal máxima: {gap.ToString("F1", CultureInfo.CurrentCulture)} s");
        if (relation.EvidenceIds.Count > 0)
        {
            text.AppendLine("Evidências participantes:");
            foreach (var id in relation.EvidenceIds)
            {
                var evidence = selectedCorrelation?.Evidence.FirstOrDefault(item => item.Id == id);
                text.AppendLine(evidence is null
                    ? $"• {id}"
                    : $"• {evidence.Title} — {CorrelationGuidance.SourceName(evidence.Source)} · {id}");
            }
        }
        if (relation.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Limitações");
            foreach (var limitation in relation.Limitations) text.AppendLine($"• {limitation}");
        }
        CorrelationRelationDetail.Text = text.ToString();
    }

    private void OnCorrelationEvidenceSelected(object sender, SelectionChangedEventArgs? e)
    {
        if (updatingCorrelation) return;
        if (CorrelationEvidenceList.SelectedItem is not CorrelationEvidenceRow row)
        {
            CorrelationEvidenceDetail.Text = selectedCorrelation is { } current
                ? BuildCorrelationSummaryText(current)
                : string.Empty;
            return;
        }

        var evidence = row.Evidence;
        var text = new StringBuilder();
        text.AppendLine(evidence.Title);
        text.AppendLine($"Identidade lógica: {evidence.Id}");
        text.AppendLine($"Fonte: {CorrelationGuidance.SourceName(evidence.Source)} · tipo: " +
            $"{CorrelationEvidenceKindName(evidence.Kind)} · precisão: " +
            $"{CorrelationGuidance.PrecisionName(evidence.Precision)}");
        text.AppendLine($"Severidade: {EventCatalog.SeverityName(evidence.Severity)} · " +
            $"confiança: {EventCatalog.ConfidenceName(evidence.Confidence)} · causalidade não avaliada");
        if (evidence.Detail is not null) text.AppendLine(evidence.Detail);
        text.AppendLine();
        text.AppendLine($"Horário original: {Local(evidence.OriginalTimestampUtc)}");
        text.AppendLine($"Horário efetivo: {Local(evidence.EffectiveTimestampUtc)}");
        if (evidence.WindowFromUtc is { } from && evidence.WindowToUtc is { } to)
            text.AppendLine($"Janela observada: {Local(from)} até {Local(to)}");
        if (evidence.IdentityKey is not null)
            text.AppendLine($"Identidade: {evidence.IdentityKey} ({evidence.IdentityKind})");
        text.AppendLine("Origens");
        foreach (var origin in evidence.Origins)
            text.AppendLine($"• {CorrelationGuidance.SourceName(origin.Source)} · registro {origin.SourceRecordId} · " +
                $"{origin.OriginReference}");
        if (evidence.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Limitações");
            foreach (var limitation in evidence.Limitations) text.AppendLine($"• {limitation}");
        }
        CorrelationEvidenceDetail.Text = text.ToString();
    }

    private void OnCorrelationSourceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingCorrelation || selectedCorrelation is not { } investigation) return;
        if (CorrelationSourceList.SelectedItem is not CorrelationSourceRow row)
        {
            CorrelationCoverageDetail.Text = BuildCorrelationCoverageText(investigation);
            return;
        }

        var text = new StringBuilder();
        var source = row.Source;
        text.AppendLine($"{CorrelationGuidance.SourceName(source.Source)} · {CorrelationSourceStatusName(source.Status)}");
        text.AppendLine($"{source.RecordsRead} registro(s) lido(s) · {source.RecordsUsed} usado(s) · " +
            $"{source.EvidenceCount} referência(s) de evidência");
        if (source.Note is not null) text.AppendLine(source.Note);
        text.AppendLine(source.LimitHit ? "Limite da fonte atingido." : "Nenhum limite de fonte atingido.");
        if (source.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Limitações da fonte");
            foreach (var limitation in source.Limitations) text.AppendLine($"• {limitation}");
        }
        text.AppendLine();
        text.AppendLine("Limitações da investigação");
        foreach (var limitation in investigation.Limitations) text.AppendLine($"• {limitation}");
        CorrelationCoverageDetail.Text = text.ToString();
    }

    private void UpdateCorrelationButtons()
    {
        var idle = !correlationBusy && !busy && !hardwareBusy && !eventBusy && !performanceBusy &&
            !networkBusy && !graphicsBusy;
        CorrelationRunButton.IsEnabled = idle && SelectedCorrelationSources() != CorrelationSources.None;
        CorrelationCancelButton.IsEnabled = correlationBusy && !correlationSaving &&
            correlationCancellation is { IsCancellationRequested: false };
        CorrelationPresetCombo.IsEnabled = !correlationBusy && !correlationSaving;
        CorrelationAuditCheck.IsEnabled = !correlationBusy && !correlationSaving;
        CorrelationHardwareCheck.IsEnabled = !correlationBusy && !correlationSaving;
        CorrelationEventCheck.IsEnabled = !correlationBusy && !correlationSaving;
        CorrelationPerformanceCheck.IsEnabled = !correlationBusy && !correlationSaving;
        CorrelationNetworkCheck.IsEnabled = !correlationBusy && !correlationSaving;
        CorrelationGraphicsCheck.IsEnabled = !correlationBusy && !correlationSaving;
        CorrelationMonitoringCheck.IsEnabled = !correlationBusy && !correlationSaving;
        CorrelationHistoryList.IsEnabled = !correlationBusy && !monitoringBusy;
        CorrelationReportButton.IsEnabled = !correlationBusy && selectedCorrelation is not null &&
            File.Exists(correlationStore.GetCorrelationReportPath(selectedCorrelation.Id));
    }

    private void UpdateAllBusyButtons()
    {
        UpdateCorrelationButtons();
        UpdateHardwareButtons();
        UpdateEventButtons();
        UpdatePerformanceButtons();
        UpdateNetworkButtons();
        UpdateGraphicsButtons();
        UpdateMonitoringButtons();
        RunButton.IsEnabled = !busy && !hardwareBusy && !eventBusy && !performanceBusy && !networkBusy &&
            !graphicsBusy && !correlationBusy && !monitoringBusy;
    }

    private void ShowCorrelationFailure(string message, Exception error)
    {
        App.LogFailure(error);
        CorrelationStatusText.Text = message + ".";
        ShowWarning($"{message}. {error.GetType().Name}: {error.Message}");
    }

    private static string CorrelationStatusName(CorrelationStatus status) => status switch
    {
        CorrelationStatus.Running => "Em andamento",
        CorrelationStatus.Completed => "Concluída",
        CorrelationStatus.Partial => "Concluída com cobertura parcial",
        CorrelationStatus.Failed => "Falhou",
        CorrelationStatus.Cancelled => "Cancelada",
        _ => status.ToString()
    };

    private static string CorrelationCoverageName(CorrelationCoverageStatus status) => status switch
    {
        CorrelationCoverageStatus.Complete => "completa",
        CorrelationCoverageStatus.Partial => "parcial",
        CorrelationCoverageStatus.Unavailable => "indisponível",
        _ => status.ToString()
    };

    private static string CorrelationSourceStatusName(CorrelationSourceStatus status) => status switch
    {
        CorrelationSourceStatus.Completed => "concluída",
        CorrelationSourceStatus.Partial => "parcial",
        CorrelationSourceStatus.Unavailable => "indisponível",
        CorrelationSourceStatus.Cancelled => "cancelada",
        _ => "não selecionada"
    };

    private static string CorrelationEvidenceKindName(CorrelationEvidenceKind kind) => kind switch
    {
        CorrelationEvidenceKind.EventLog => "evento do Windows",
        CorrelationEvidenceKind.WerReport => "relatório WER",
        CorrelationEvidenceKind.PerformanceObservation => "observação de desempenho",
        CorrelationEvidenceKind.NetworkObservation => "observação de rede",
        CorrelationEvidenceKind.GraphicsObservation => "observação gráfica",
        CorrelationEvidenceKind.HardwareChange => "alteração de hardware",
        CorrelationEvidenceKind.GraphicsChange => "alteração gráfica",
        _ => "inventário de dispositivo"
    };

    private sealed record CorrelationPresetRow(EventWindow Window, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record CorrelationHistoryRow(CorrelationInvestigation Investigation)
    {
        public string DateText => Investigation.CompletedUtc.ToLocalTime()
            .ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture);
        public string SummaryText =>
            $"{CorrelationStatusName(Investigation.Status)} · cobertura " +
            $"{CorrelationCoverageName(Investigation.Coverage.Status)} · " +
            $"{Investigation.Coverage.DistinctOccurrences} evidência(s) · " +
            $"{Investigation.Relations.Count(relation => relation.IsHypothesis)} relação(ões)";
    }

    private sealed record CorrelationTimelineRow(CorrelationTimelineItem Item)
    {
        public string Time => Item.TimestampUtc.ToLocalTime()
            .ToString("dd/MM HH:mm:ss", CultureInfo.CurrentCulture);
        public string Precision => CorrelationGuidance.PrecisionName(Item.Precision);
        public string Source => CorrelationGuidance.SourceName(Item.Source);
        public string Kind => CorrelationEvidenceKindName(Item.Kind);
        public string Title => Item.Title;
    }

    private sealed record CorrelationRelationRow(CorrelationRelation Relation)
    {
        public string Title => Relation.Title;
        public string Subtitle =>
            $"{CorrelationGuidance.RelationKindName(Relation.Kind)} · " +
            $"{EventCatalog.SeverityName(Relation.Severity)} · confiança " +
            $"{EventCatalog.ConfidenceName(Relation.Confidence)} · causalidade não avaliada";
    }

    private sealed record CorrelationEvidenceRow(CorrelationEvidence Evidence)
    {
        public string Title => Evidence.Title;
        public string Subtitle =>
            $"{CorrelationGuidance.SourceName(Evidence.Source)} · {CorrelationEvidenceKindName(Evidence.Kind)} · " +
            $"{CorrelationGuidance.PrecisionName(Evidence.Precision)} · {Evidence.Origins.Count} origem(ns)";
    }

    private sealed record CorrelationSourceRow(CorrelationSourceSummary Source)
    {
        public string SourceName => CorrelationGuidance.SourceName(Source.Source);
        public string Status => CorrelationSourceStatusName(Source.Status);
        public string Records => $"{Source.RecordsRead} lido(s) · {Source.RecordsUsed} usado(s)";
        public string Limitations => Source.Limitations.Count == 0
            ? "—"
            : string.Join(" ", Source.Limitations);
    }
}
