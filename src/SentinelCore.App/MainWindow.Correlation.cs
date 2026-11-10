using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows;
using SentinelCore.Persistence;
using SentinelCore.App.Localization;

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
        if (CorrelationTweaksCheck.IsChecked == true) sources |= CorrelationSources.Tweaks;
        return sources;
    }

    private async Task ReloadCorrelationAsync(Guid? selectId = null)
    {
        var snapshot = await correlationStore.LoadCorrelationInvestigationsAsync();
        correlationInvestigations = snapshot.Investigations;
        SetIntegrity("Correlação", snapshot.Warnings);
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
            Loc.F($"Investigação de {Local(investigation.CompletedUtc)} · {CorrelationStatusName(investigation.Status)} · ") +
            Loc.F($"cobertura {CorrelationCoverageName(coverage.Status)} · ") +
            Loc.F($"{coverage.CompletedSources}/{coverage.ExpectedSources} fontes · ID {investigation.Id:D}") +
            (persisted ? string.Empty : Loc.T(" · não confirmada na memória local"));

        CorrelationSourceValue.Text =
            Loc.F($"{coverage.CompletedSources}/{coverage.ExpectedSources}");
        CorrelationSourceDetail.Text =
            Loc.F($"{coverage.PartialSources} parcial(is) · {coverage.UnavailableSources} indisponível(is) · ") +
            Loc.F($"{coverage.CancelledSources} cancelada(s)");

        CorrelationEvidenceValue.Text = coverage.DistinctOccurrences.ToString(CultureInfo.CurrentCulture);
        CorrelationEvidenceCardDetail.Text = coverage.MergedReferences == 0
            ? Loc.F($"{coverage.EvidenceFound} referência(s) de origem")
            : Loc.F($"{coverage.EvidenceFound} referência(s) · {coverage.UndatedEvidence} sem horário confiável");

        CorrelationMergedValue.Text = coverage.MergedReferences.ToString(CultureInfo.CurrentCulture);
        CorrelationMergedDetail.Text = coverage.MergedReferences == 0
            ? Loc.T("nenhuma ocorrência repetida entre módulos")
            : Loc.T("representações consolidadas em uma ocorrência lógica");

        var associations = investigation.Relations.Count(relation => relation.IsHypothesis);
        CorrelationRelationValue.Text = associations.ToString(CultureInfo.CurrentCulture);
        CorrelationRelationCardDetail.Text =
            Loc.F($"{(coverage.LimitHit ? "limite interno atingido" : "sem limite interno")} · ") +
            Loc.F($"{investigation.Timeline.Count} item(ns) na linha do tempo");

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

        OnCorrelationEvidenceSelected(CorrelationEvidenceList, null);

        CorrelationCoverageDetail.Text = BuildCorrelationCoverageText(investigation);

        UpdateCorrelationButtons();
    }

    private void ClearCorrelationView()
    {
        selectedCorrelation = null;
        CorrelationProgress.Minimum = 0;
        CorrelationProgress.Maximum = 3;
        CorrelationProgress.Value = 0;
        CorrelationInvestigationInfo.Text = Loc.T("Nenhuma investigação de correlação realizada.");
        CorrelationStatusText.Text =
            Loc.T("Selecione um período e as fontes. A correlação reutiliza somente registros históricos; nenhuma coleta ") +
            Loc.T("nova é executada e nenhuma configuração do Windows é alterada.");
        CorrelationSourceValue.Text = "—";
        CorrelationSourceDetail.Text = Loc.T("fontes consultadas");
        CorrelationEvidenceValue.Text = "—";
        CorrelationEvidenceCardDetail.Text = Loc.T("referências de evidência");
        CorrelationMergedValue.Text = "—";
        CorrelationMergedDetail.Text = Loc.T("deduplicação entre módulos");
        CorrelationRelationValue.Text = "—";
        CorrelationRelationCardDetail.Text = Loc.T("relações identificadas");
        CorrelationTimelineList.ItemsSource = null;
        updatingCorrelation = true;
        CorrelationRelationList.ItemsSource = null;
        CorrelationEvidenceList.ItemsSource = null;
        CorrelationSourceList.ItemsSource = null;
        updatingCorrelation = false;
        CorrelationRelationDetail.Text =
            Loc.T("Nenhuma relação selecionada. Uma associação temporal ou de identidade não demonstra causa.");
        CorrelationEvidenceDetail.Text =
            Loc.T("Nenhuma evidência selecionada. Cada evidência mantém a referência ao registro de origem.");
        SetCorrelationOrigins([]);
        CorrelationCoverageDetail.Text =
            Loc.T("Nenhuma cobertura consultada. Fontes indisponíveis e limites são declarados, nunca convertidos em ") +
            Loc.T("ausência de acontecimento.");
        UpdateCorrelationButtons();
    }

    private static string BuildCorrelationSummaryText(CorrelationInvestigation investigation)
    {
        var text = new StringBuilder();
        var associations = investigation.Relations.Count(relation => relation.IsHypothesis);
        text.AppendLine(associations == 0
            ? Loc.F($"{CorrelationGuidance.InsufficientEvidenceStatement}.")
            : Loc.F($"{associations} associação(ões) determinística(s) identificada(s)."));
        text.AppendLine(Loc.T("Causalidade não avaliada."));
        text.AppendLine();
        text.AppendLine(Loc.F($"Referências de evidência: {investigation.Coverage.EvidenceFound} · ocorrências distintas: ") +
            Loc.F($"{investigation.Coverage.DistinctOccurrences} · consolidadas: {investigation.Coverage.MergedReferences}"));
        text.AppendLine(Loc.F($"Linha do tempo: {investigation.Timeline.Count} item(ns) · sem horário confiável: ") +
            Loc.F($"{investigation.Coverage.UndatedEvidence}"));
        if (investigation.Boot.LastBootUtc is { } boot)
            text.AppendLine(Loc.F($"Inicialização do Windows: {Local(boot)}"));
        else
            text.AppendLine(Loc.T("Inicialização do Windows indisponível; sessões distintas não puderam ser separadas."));
        if (investigation.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Limitações"));
            foreach (var limitation in investigation.Limitations)
                text.AppendLine(Loc.F($"• {limitation}"));
        }
        return text.ToString();
    }

    private static string BuildCorrelationCoverageText(CorrelationInvestigation investigation)
    {
        var text = new StringBuilder();
        var coverage = investigation.Coverage;
        text.AppendLine(Loc.F($"Cobertura {CorrelationCoverageName(coverage.Status)} · ") +
            Loc.F($"{coverage.CompletedSources} concluída(s) · {coverage.PartialSources} parcial(is) · ") +
            Loc.F($"{coverage.UnavailableSources} indisponível(is) · {coverage.CancelledSources} cancelada(s)"));
        text.AppendLine(Loc.F($"Evidências: {coverage.DistinctOccurrences} distinta(s) de {coverage.EvidenceFound} referência(s)"));
        text.AppendLine(coverage.LimitHit
            ? Loc.T("Limite interno de processamento atingido: a investigação é parcial.")
            : Loc.T("Nenhum limite interno de processamento atingido."));
        text.AppendLine();
        foreach (var source in investigation.Sources)
        {
            text.AppendLine(Loc.F($"{CorrelationGuidance.SourceName(source.Source)}: {CorrelationSourceStatusName(source.Status)} · ") +
                Loc.F($"{source.RecordsRead} lido(s) · {source.RecordsUsed} usado(s) · {source.EvidenceCount} referência(s)"));
            if (source.Note is not null) text.AppendLine(Loc.F($"  {source.Note}"));
            foreach (var limitation in source.Limitations) text.AppendLine(Loc.F($"  Limitação: {limitation}"));
        }
        if (investigation.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Limitações da investigação"));
            foreach (var limitation in investigation.Limitations)
                text.AppendLine(Loc.F($"• {limitation}"));
        }
        return text.ToString();
    }

    private async void OnCorrelationRun(object sender, RoutedEventArgs e)
    {
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        if (correlationBusy || busy || hardwareBusy || eventBusy || performanceBusy || networkBusy || graphicsBusy)
            return;

        var sources = SelectedCorrelationSources();
        if (sources == CorrelationSources.None)
        {
            ShowWarning(Loc.T("Selecione ao menos uma fonte para correlacionar."));
            return;
        }

        if (unsavedCorrelation is not null)
        {
            correlationBusy = true;
            correlationSaving = true;
            UpdateAllBusyButtons();
            CorrelationStatusText.Text = Loc.T("Tentando salvar novamente a correlação pendente…");
            try
            {
                var pending = unsavedCorrelation;
                await correlationStore.SaveCorrelationInvestigationAsync(pending, CancellationToken.None);
                unsavedCorrelation = null;
                await ReloadCorrelationAsync(pending.Id);
                CorrelationStatusText.Text = Loc.T("Correlação pendente salva novamente.");
            }
            catch (Exception error)
            {
                ShowCorrelationFailure(Loc.T("Falha de gravação da correlação. O resultado permanece nesta janela; ") +
                    Loc.T("tente salvar novamente"), error);
            }
            finally
            {
                correlationBusy = false;
                correlationSaving = false;
                CorrelationRunButton.Content = unsavedCorrelation is null ? Loc.T("Correlacionar") : Loc.T("Tentar salvar correlação");
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
        ClearRunWarnings();
        CorrelationProgress.Minimum = 0;
        CorrelationProgress.Maximum = 3;
        CorrelationProgress.Value = 0;
        UpdateAllBusyButtons();
        var saved = false;
        try
        {
            CorrelationStatusText.Text =
                Loc.T("Correlação em andamento. Leitura somente dos registros históricos; nenhuma coleta nova é executada.");
            var progress = new Progress<CorrelationProgress>(OnCorrelationProgress);
            var request = new CorrelationRequest(SelectedCorrelationWindow(), Sources: sources);
            var investigation = await correlationRunner.RunAsync(request, progress, correlationCancellation.Token);
            unsavedCorrelation = investigation;
            ApplyCorrelation(investigation);
            correlationSaving = true;
            UpdateAllBusyButtons();
            CorrelationStatusText.Text = Loc.T("Salvando investigação e relatório na memória local…");
            await correlationStore.SaveCorrelationInvestigationAsync(investigation, CancellationToken.None);
            saved = true;
            unsavedCorrelation = null;
            await ReloadCorrelationAsync(investigation.Id);
            CorrelationStatusText.Text =
                Loc.F($"Correlação {CorrelationStatusName(investigation.Status).ToLowerInvariant()} salva. ") +
                Loc.F($"{investigation.Coverage.EvidenceFound} referência(s) de evidência, ") +
                Loc.F($"{investigation.Coverage.MergedReferences} consolidada(s), ") +
                Loc.F($"{investigation.Relations.Count(relation => relation.IsHypothesis)} relação(ões). ") +
                Loc.T("Causalidade não avaliada.");
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
            CorrelationRunButton.Content = unsavedCorrelation is null ? Loc.T("Correlacionar") : Loc.T("Tentar salvar correlação");
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
        CorrelationStatusText.Text = Loc.T("Cancelamento solicitado. Aguardando a leitura histórica em andamento…");
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
            ShowCorrelationFailure(Loc.T("Não foi possível abrir a localização do relatório de correlação"), error);
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
        CorrelationStatusText.Text = Loc.F($"{char.ToUpperInvariant(update.Phase[0])}{update.Phase[1..]} · ") +
            Loc.F($"{update.Finished}/{update.Total} · leitura somente");
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
        text.AppendLine(Loc.F($"Regra: {relation.RuleId} {relation.RuleVersion} · tipo: ") +
            Loc.F($"{CorrelationGuidance.RelationKindName(relation.Kind)}"));
        text.AppendLine(Loc.F($"Severidade: {EventCatalog.SeverityName(relation.Severity)} · ") +
            Loc.F($"confiança: {EventCatalog.ConfidenceName(relation.Confidence)} · causalidade: não avaliada"));
        text.AppendLine();
        text.AppendLine(relation.Statement);
        text.AppendLine();
        text.AppendLine(Loc.F($"Justificativa: {relation.Justification}"));
        text.AppendLine(Loc.F($"Janela: {Local(relation.WindowFromUtc)} até {Local(relation.WindowToUtc)}"));
        if (relation.MaximumGapSeconds is { } gap)
            text.AppendLine(Loc.F($"Diferença temporal máxima: {gap.ToString("F1", CultureInfo.CurrentCulture)} s"));
        if (relation.EvidenceIds.Count > 0)
        {
            text.AppendLine(Loc.T("Evidências participantes:"));
            foreach (var id in relation.EvidenceIds)
            {
                var evidence = selectedCorrelation?.Evidence.FirstOrDefault(item => item.Id == id);
                text.AppendLine(evidence is null
                    ? Loc.F($"• {id}")
                    : Loc.F($"• {evidence.Title} — {CorrelationGuidance.SourceName(evidence.Source)} · {id}"));
            }
        }
        if (relation.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Limitações"));
            foreach (var limitation in relation.Limitations) text.AppendLine(Loc.F($"• {limitation}"));
        }
        CorrelationRelationDetail.Text = text.ToString();
    }

    private void OnCorrelationEvidenceSelected(object sender, SelectionChangedEventArgs? e)
    {
        if (updatingCorrelation) return;
        if (CorrelationEvidenceList.SelectedItem is not CorrelationEvidenceRow row)
        {
            SetCorrelationOrigins([]);
            CorrelationEvidenceDetail.Text = selectedCorrelation is { } current
                ? BuildCorrelationSummaryText(current)
                : string.Empty;
            return;
        }

        var evidence = row.Evidence;
        SetCorrelationOrigins(evidence.Origins);
        var text = new StringBuilder();
        text.AppendLine(evidence.Title);
        text.AppendLine(Loc.F($"Identidade lógica: {evidence.Id}"));
        text.AppendLine(Loc.F($"Fonte: {CorrelationGuidance.SourceName(evidence.Source)} · tipo: ") +
            Loc.F($"{CorrelationEvidenceKindName(evidence.Kind)} · precisão: ") +
            Loc.F($"{CorrelationGuidance.PrecisionName(evidence.Precision)}"));
        text.AppendLine(Loc.F($"Severidade: {EventCatalog.SeverityName(evidence.Severity)} · ") +
            Loc.F($"confiança: {EventCatalog.ConfidenceName(evidence.Confidence)} · causalidade não avaliada"));
        if (evidence.Detail is not null) text.AppendLine(evidence.Detail);
        text.AppendLine();
        text.AppendLine(Loc.F($"Horário original: {Local(evidence.OriginalTimestampUtc)}"));
        text.AppendLine(Loc.F($"Horário efetivo: {Local(evidence.EffectiveTimestampUtc)}"));
        if (evidence.WindowFromUtc is { } from && evidence.WindowToUtc is { } to)
            text.AppendLine(Loc.F($"Janela observada: {Local(from)} até {Local(to)}"));
        if (evidence.IdentityKey is not null)
            text.AppendLine(Loc.F($"Identidade: {evidence.IdentityKey} ({evidence.IdentityKind})"));
        text.AppendLine(Loc.T("Origens"));
        foreach (var origin in evidence.Origins)
            text.AppendLine(Loc.F($"• {CorrelationGuidance.SourceName(origin.Source)} · registro {origin.SourceRecordId} · ") +
                Loc.F($"{origin.OriginReference}"));
        if (evidence.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Limitações"));
            foreach (var limitation in evidence.Limitations) text.AppendLine(Loc.F($"• {limitation}"));
        }
        CorrelationEvidenceDetail.Text = text.ToString();
    }

    private void SetCorrelationOrigins(IReadOnlyList<CorrelationEvidenceOrigin> origins)
    {
        var rows = origins
            .DistinctBy(origin => (origin.Source, origin.SourceRecordId))
            .Select(origin => new CorrelationOriginRow(origin))
            .ToArray();
        CorrelationOriginCombo.ItemsSource = rows;
        CorrelationOriginCombo.SelectedIndex = rows.Length > 0 ? 0 : -1;
        CorrelationOriginPanel.Visibility = rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CorrelationOriginStatus.Text = string.Empty;
    }

    private void OnCorrelationOriginChanged(object sender, SelectionChangedEventArgs e)
    {
        CorrelationOriginButton.IsEnabled = CorrelationOriginCombo.SelectedItem is CorrelationOriginRow;
        CorrelationOriginStatus.Text = string.Empty;
    }

    private async void OnOpenCorrelationOrigin(object sender, RoutedEventArgs e)
    {
        if (CorrelationOriginCombo.SelectedItem is not CorrelationOriginRow { Origin: var origin }) return;
        if (!Guid.TryParseExact(origin.SourceRecordId, "D", out var id) || id == Guid.Empty)
        {
            CorrelationOriginStatus.Text = Loc.T("Identificador do registro de origem inválido.");
            return;
        }
        if (origin.Source == CorrelationSource.Tweaks)
        {
            if (recoveryCenter is null || RecoverySection.IsBusy || tweaksBusy)
            {
                CorrelationOriginStatus.Text = Loc.T("Histórico de personalização indisponível neste momento.");
                return;
            }
            if (!recoveryComposed)
            {
                recoveryComposed = true;
                RecoverySection.Initialize(recoveryCenter, loadIfVisible: false);
            }
            var found = await RecoverySection.OpenTweakAsync(id);
            if (!ReferenceEquals(Pages.SelectedItem, CorrelationTab) ||
                CorrelationOriginCombo.SelectedItem is not CorrelationOriginRow selected || selected.Origin != origin)
                return;
            if (found)
                NavList.SelectedIndex = Pages.Items.IndexOf(RecoveryTab);
            else
                CorrelationOriginStatus.Text = Loc.T("Transação indisponível no histórico carregado.");
            return;
        }
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        CorrelationOriginStatus.Text = await OpenDiagnosticRecordAsync(origin.Source, id) ?? string.Empty;
    }

    private async Task<string?> OpenDiagnosticRecordAsync(CorrelationSource source, Guid id)
    {
        // Refresh the existing history only; navigation never starts a collector or maintenance operation.
        try
        {
            switch (source)
            {
                case CorrelationSource.Audits: await ReloadAsync(id, readOnly: true); break;
                case CorrelationSource.Hardware: await ReloadHardwareAsync(id); break;
                case CorrelationSource.Events: await ReloadEventsAsync(id); break;
                case CorrelationSource.Performance: await ReloadPerformanceAsync(id); break;
                case CorrelationSource.Network: await ReloadNetworksAsync(id); break;
                case CorrelationSource.Graphics: await ReloadGraphicsAsync(id); break;
                case CorrelationSource.Monitoring: await ReloadMonitoringAsync(id); break;
            }
        }
        catch (Exception error)
        {
            logFailure(error);
            return "Não foi possível carregar o registro de origem. A página atual foi preservada.";
        }
        return OpenDiagnosticRecord(source, id);
    }

    private string? OpenDiagnosticRecord(CorrelationSource source, Guid id)
    {
        (TabItem Page, ListBox History, object? Row)? target = source switch
        {
            CorrelationSource.Audits => (HistoryTab, HistoryList,
                HistoryList.Items.OfType<HistoryRow>().FirstOrDefault(row => row.Audit.Id == id)),
            CorrelationSource.Hardware => (HardwareTab, HardwareSnapshotList,
                HardwareSnapshotList.Items.OfType<HardwareSnapshotRow>().FirstOrDefault(row => row.Snapshot.Id == id)),
            CorrelationSource.Events => (EventsTab, EventHistoryList,
                EventHistoryList.Items.OfType<EventHistoryRow>().FirstOrDefault(row => row.Investigation.Id == id)),
            CorrelationSource.Performance => (PerformanceTab, PerformanceHistoryList,
                PerformanceHistoryList.Items.OfType<PerformanceHistoryRow>().FirstOrDefault(row => row.Session.Id == id)),
            CorrelationSource.Network => (NetworkTab, NetworkHistoryList,
                NetworkHistoryList.Items.OfType<NetworkHistoryRow>().FirstOrDefault(row => row.Session.Id == id)),
            CorrelationSource.Graphics => (GraphicsTab, GraphicsHistoryList,
                GraphicsHistoryList.Items.OfType<GraphicsHistoryRow>().FirstOrDefault(row => row.Investigation.Id == id)),
            CorrelationSource.Monitoring => (MonitoringTab, MonitoringHistoryList,
                MonitoringHistoryList.Items.OfType<MonitoringHistoryRow>().FirstOrDefault(row => row.Session.Id == id)),
            _ => null
        };

        if (target is not { } destination) return "A fonte deste registro não está disponível para navegação.";
        if (!destination.History.IsEnabled)
            return "Aguarde a operação em andamento na página de origem e tente novamente.";
        if (destination.Row is not { } record)
            return "Registro de origem indisponível no histórico carregado.";

        // Reaplica a seleção caso uma falha ao salvar tenha deixado outro resultado na tela.
        if (ReferenceEquals(destination.History.SelectedItem, record)) destination.History.SelectedItem = null;
        destination.History.SelectedItem = record;
        NavList.SelectedIndex = Pages.Items.IndexOf(destination.Page);
        destination.History.ScrollIntoView(record);
        return null;
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
        text.AppendLine(Loc.F($"{CorrelationGuidance.SourceName(source.Source)} · {CorrelationSourceStatusName(source.Status)}"));
        text.AppendLine(Loc.F($"{source.RecordsRead} registro(s) lido(s) · {source.RecordsUsed} usado(s) · ") +
            Loc.F($"{source.EvidenceCount} referência(s) de evidência"));
        if (source.Note is not null) text.AppendLine(source.Note);
        text.AppendLine(source.LimitHit ? Loc.T("Limite da fonte atingido.") : Loc.T("Nenhum limite de fonte atingido."));
        if (source.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Limitações da fonte"));
            foreach (var limitation in source.Limitations) text.AppendLine(Loc.F($"• {limitation}"));
        }
        text.AppendLine();
        text.AppendLine(Loc.T("Limitações da investigação"));
        foreach (var limitation in investigation.Limitations) text.AppendLine(Loc.F($"• {limitation}"));
        CorrelationCoverageDetail.Text = text.ToString();
    }

    private void UpdateCorrelationButtons()
    {
        var idle = !operations.IsBusy && !correlationBusy && !busy && !hardwareBusy && !eventBusy && !performanceBusy &&
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
        CorrelationTweaksCheck.IsEnabled = !correlationBusy && !correlationSaving;
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
        RunButton.IsEnabled = !operations.IsBusy && !busy && !hardwareBusy && !eventBusy && !performanceBusy && !networkBusy &&
            !graphicsBusy && !correlationBusy && !monitoringBusy;
    }

    private void ShowCorrelationFailure(string message, Exception error)
    {
        logFailure(error);
        CorrelationStatusText.Text = message + ".";
        ShowWarning(Loc.F($"{message}. {error.GetType().Name}: {error.Message}"));
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
        CorrelationEvidenceKind.TweakJournalEvent => "evento de personalização",
        _ => "inventário de dispositivo"
    };

    private sealed record CorrelationPresetRow(EventWindow Window, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record CorrelationOriginRow(CorrelationEvidenceOrigin Origin)
    {
        public string Label => $"{CorrelationGuidance.SourceName(Origin.Source)} · {Origin.SourceRecordId}";
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
