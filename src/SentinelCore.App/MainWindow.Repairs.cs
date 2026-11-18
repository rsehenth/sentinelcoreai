using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows;
using SentinelCore.App.Localization;

namespace SentinelCore.App;

public partial class MainWindow
{
    private readonly RepairManager? repairs;
    private RepairCenterView? repairCenterView;
    private IReadOnlyList<RepairOperationView> repairViews = [];
    private RepairOperationView? repairSelectedView;
    private RepairPlan? repairPlan;
    private RepairRecord? repairRecord;
    private RepairRecord? repairSelectedRecord;
    private RepairHistory? repairHistory;
    private bool repairsBusy;
    private bool repairsLoaded;
    private bool repairsUpdatingHistory;
    private bool repairPersistenceFailed;
    private bool repairClosingAfterSave;
    private CancellationTokenSource? repairCancellation;
    private readonly Stopwatch repairElapsed = new();
    private string repairPhase = string.Empty;
    private bool repairCommitted;
    private readonly DispatcherTimer repairElapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly LocalizedMessages repairMessages = new();

    private void InitializeRepairs()
    {
        repairElapsedTimer.Tick += (_, _) => UpdateRepairElapsed();
        repairMessages.Set(nameof(RepairsModeText), RenderRepairsModeText);
        repairMessages.Set(nameof(RepairsModeDetail), RenderRepairsModeDetail);
        repairMessages.Set(nameof(RepairsSyntheticBanner), RenderRepairsBanner);
        repairMessages.Set(nameof(RepairsDocumentedText), () =>
            RepairsDocumentedText.Text = repairCenterView is { } view
                ? BuildDocumentedText(view.DocumentedOperations)
                : string.Empty);
        if (repairs is null)
        {
            RepairsRefreshButton.IsEnabled = false;
            RepairsAuthorizeButton.IsEnabled = false;
            return;
        }

        RepairsScenarioCombo.ItemsSource = RepairPresentation.Scenarios
            .Select(option => new RepairScenarioRow(option)).ToArray();
        RepairsScenarioCombo.SelectedIndex = 0;
        repairMessages.Set(nameof(RepairsVolumeText), () =>
            RepairsVolumeText.Text = Loc.T("Volumes ainda não lidos; a leitura ocorre ao abrir esta página ou em Atualizar."));
        if (repairs.RealExecutionEnabled)
        {
            RepairsScenarioPanel.Visibility = Visibility.Collapsed;
            RepairsSyntheticBanner.Visibility = Visibility.Collapsed;
            repairMessages.Set(nameof(RepairsAuthorizeButton), () =>
                RepairsAuthorizeButton.Content = Loc.T("Autorizar e executar"));
        }
        SetRepairsStatus(() => Loc.T("Catálogo e volumes ainda não lidos. Nenhuma ferramenta foi executada."));
        Pages.SelectionChanged += OnRepairsPageSelectionChanged;
        UpdateRepairButtons();
    }

    private void RenderRepairsModeText() =>
        RepairsModeText.Text = repairs is null
            ? Loc.T("Repair Center indisponível nesta composição.")
            : repairCenterView is { } view
                ? Loc.T(view.ModeText)
                : repairs.RealExecutionEnabled ? Loc.T("Execução pelo broker local") : Loc.T(RepairGuidance.RealExecutionUnavailable);

    private void RenderRepairsModeDetail() =>
        RepairsModeDetail.Text = repairs is null
            ? Loc.T("Nenhum catálogo de reparos foi composto.")
            : repairCenterView is { } view
                ? Loc.T(view.ModeDetail)
                : repairs.RealExecutionEnabled
                    ? Loc.T("Selecione uma operação e confirme o plano. O Windows solicita consentimento quando necessário.")
                    : Loc.T("Catálogo fechado com executáveis oficiais e argumentos fixos. ") +
                      Loc.T("A execução real não é composta nesta distribuição. Os cartões demonstram o fluxo com fixtures ") +
                      Loc.T("sintéticas explicitamente marcadas; nenhuma elevação é solicitada.");

    private void RenderRepairsBanner() =>
        RepairsSyntheticBanner.Text = Loc.T(RepairGuidance.SyntheticDisclaimer);

    private void SetRepairsStatus(Func<string> status) =>
        repairMessages.Set(nameof(RepairsStatusText), () => RepairsStatusText.Text = status());

    private void OnRepairsPageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Pages) || !ReferenceEquals(Pages.SelectedItem, RepairsTab)) return;
        ScrollRepairsToTop();
        if (repairsLoaded || repairsBusy) return;
        _ = ReloadRepairsAsync();
    }

    private void ScrollRepairsToTop()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RepairsScroll.ScrollToTop();
            RepairsRefreshButton.Focus();
            RepairsScroll.ScrollToTop();
        }), DispatcherPriority.ContextIdle);
    }

    private async void OnRepairsRefresh(object sender, RoutedEventArgs e) =>
        await ReloadRepairsAsync(repairSelectedRecord?.Id);

    private async Task ReloadRepairsAsync(Guid? selectRecord = null, Func<string>? status = null,
        bool ignoreBusy = false)
    {
        if (repairs is null || (repairsBusy && !ignoreBusy)) return;
        using var productLease = ignoreBusy ? null : TryBeginProductOperation();
        if (!ignoreBusy && productLease is null) return;
        repairsBusy = true;
        UpdateRepairButtons();
        SetRepairsStatus(() => Loc.T("Lendo catálogo, volumes e histórico…"));
        try
        {
            repairCenterView = await repairs.LoadAsync();
            repairMessages.Set(nameof(RepairsModeText), RenderRepairsModeText);
            repairMessages.Set(nameof(RepairsModeDetail), RenderRepairsModeDetail);
            repairViews = repairCenterView.Operations;
            RepairsCardsHost.ItemsSource = RepairPresentation.GroupByCategory(repairViews)
                .Select(group => new RepairCategoryRow(group.Category,
                    group.Operations.Select(view => new RepairCardRow(view)).ToArray()))
                .ToArray();

            var previousVolume = (RepairsVolumeCombo.SelectedItem as RepairVolumeRow)?.Volume.Id;
            var volumeRows = repairCenterView.Volumes.Select(volume => new RepairVolumeRow(volume)).ToArray();
            RepairsVolumeCombo.ItemsSource = volumeRows;
            RepairsVolumeCombo.SelectedItem = volumeRows.FirstOrDefault(row => row.Volume.Id == previousVolume)
                ?? volumeRows.FirstOrDefault();
            UpdateRepairVolumeText();

            repairMessages.Set(nameof(RepairsDocumentedText), () =>
                RepairsDocumentedText.Text = repairCenterView is { } view
                    ? BuildDocumentedText(view.DocumentedOperations)
                    : string.Empty);
            await ReloadRepairHistoryAsync(selectRecord);
            repairsLoaded = true;
            SetRepairsStatus(status ?? (() =>
                Loc.T("Catálogo lido. Selecione 'Detalhes' em um cartão para preparar o plano.")));
            ScrollRepairsToTop();
        }
        catch (Exception error)
        {
            ShowFailure(Loc.T("Não foi possível ler o Repair Center"), error);
            SetRepairsStatus(() => Loc.T("Falha ao ler o catálogo; nenhuma ferramenta real foi executada."));
        }
        finally
        {
            repairsBusy = false;
            UpdateRepairButtons();
        }
    }

    private async Task ReloadRepairHistoryAsync(Guid? selectId = null, Func<string>? status = null)
    {
        if (repairs is null) return;
        repairHistory = await repairs.LoadHistoryAsync();
        var rows = new List<RepairHistoryRow>();
        rows.AddRange(repairHistory.Records.Select(record => new RepairHistoryRow(record, null)));
        rows.AddRange(repairHistory.UnfinishedIntents.Select(intent => new RepairHistoryRow(null, intent)));
        repairsUpdatingHistory = true;
        RepairsHistoryList.ItemsSource = rows;
        if (selectId is { } id)
        {
            var selected = rows.FirstOrDefault(row => row.Record?.Id == id);
            if (selected is not null)
            {
                RepairsHistoryList.SelectedItem = selected;
                RepairsHistoryList.ScrollIntoView(selected);
            }
        }
        else
        {
            RepairsHistoryList.SelectedItem = null;
            repairSelectedRecord = null;
        }
        repairsUpdatingHistory = false;

        repairMessages.Set(nameof(RepairsUnfinishedText), () => RepairsUnfinishedText.Text = repairHistory is not { } history
            ? string.Empty
            : history.UnfinishedIntents.Count == 0
                ? history.Warnings.Count > 0
                    ? Loc.T("Histórico incompleto: nenhuma intenção válida sem registro final carregada. O total não foi determinado; examine os avisos.")
                    : Loc.T("Nenhuma intenção sem registro final.")
                : Loc.F($"{history.UnfinishedIntents.Count} intenção(ões) sem registro final; tratadas como interrompidas."));
        SetIntegrity("Reparos", repairHistory.Warnings);
        if (status is not null) SetRepairsStatus(status);
        UpdateRepairButtons();
    }

    private void OnRepairVolumeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (repairsUpdatingHistory) return;
        repairPlan = null;
        repairSelectedView = null;
        repairMessages.Set(nameof(RepairsDetailsTitle), () => RepairsDetailsTitle.Text = Loc.T("Plano e confirmação"));
        repairMessages.Set(nameof(RepairsDetailsMeta), () => RepairsDetailsMeta.Text = string.Empty);
        repairMessages.Set(nameof(RepairsDetailsText), () =>
            RepairsDetailsText.Text = Loc.T("Selecione 'Detalhes' em um cartão para preparar o plano simulado."));
        repairMessages.Set(nameof(RepairsOutputText), () => RepairsOutputText.Text = string.Empty);
        RepairsConfirmationText.Text = string.Empty;
        repairMessages.Set(nameof(RepairsConfirmationHint), () =>
            RepairsConfirmationHint.Text = Loc.T("A execução exige confirmação digitada vinculada ao plano."));
        UpdateRepairVolumeText();
        UpdateRepairButtons();
    }

    private void UpdateRepairVolumeText()
    {
        if (RepairsVolumeCombo.SelectedItem is not RepairVolumeRow row)
        {
            repairMessages.Set(nameof(RepairsVolumeText), () =>
                RepairsVolumeText.Text = Loc.T("Nenhum volume local validado foi encontrado. ") +
                    Loc.T("Operações de disco permanecem indisponíveis."));
            return;
        }
        var volume = row.Volume;
        var scan = volume.SupportsChkdskScan
            ? Loc.T("verificação /scan compatível")
            : Loc.T("verificação /scan indisponível");
        repairMessages.Set(nameof(RepairsVolumeText), () => RepairsVolumeText.Text =
            Loc.F($"{volume.Id} · {volume.FileSystem} · ") +
            Loc.F($"{(volume.IsReady ? Loc.T("pronto") : Loc.T("indisponível"))} · {scan}") +
            (volume.Limitations.Count == 0 ? string.Empty : " · " + Loc.T(volume.Limitations[0])));
    }

    private async void OnRepairDetails(object sender, RoutedEventArgs e)
    {
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        if (repairs is null || repairsBusy || repairPersistenceFailed) return;
        if (sender is not FrameworkElement { DataContext: RepairCardRow { OperationId: var operationId } }) return;
        var view = repairViews.FirstOrDefault(item => item.OperationId == operationId);
        if (view is null) return;
        string? volumeId = null;
        if (string.Equals(view.Category, RepairCategories.Volumes, StringComparison.Ordinal) &&
            RepairsVolumeCombo.SelectedItem is RepairVolumeRow volumeRow)
            volumeId = volumeRow.Volume.Id;
        if (string.Equals(view.Category, RepairCategories.Volumes, StringComparison.Ordinal) &&
            volumeId is null)
        {
            SetRepairsStatus(() => Loc.T("Selecione um volume local validado antes de preparar esta operação."));
            return;
        }

        repairsBusy = true;
        UpdateRepairButtons();
        try
        {
            repairPlan = await repairs.PrepareAsync(operationId, volumeId);
            repairSelectedView = view;
            repairRecord = null;
            repairPersistenceFailed = false;
            ShowRepairPlan(view, repairPlan);
        }
        catch (Exception error)
        {
            ShowFailure(Loc.T("Não foi possível preparar o plano de reparo"), error);
            SetRepairsStatus(() => Loc.T("Preparação indisponível; nenhuma ferramenta real foi executada."));
        }
        finally
        {
            repairsBusy = false;
            UpdateRepairButtons();
        }
    }

    private void ShowRepairPlan(RepairOperationView view, RepairPlan plan)
    {
        var real = repairs?.RealExecutionEnabled == true;
        var canExecute = CanRunRepairPlan(plan);
        repairMessages.Set(nameof(RepairsDetailsTitle), () => RepairsDetailsTitle.Text = plan.DisplayName);
        repairMessages.Set(nameof(RepairsDetailsMeta), () => RepairsDetailsMeta.Text =
            Loc.F($"{plan.OperationId} v{plan.OperationVersion} · ") +
            Loc.F($"{Loc.T(RepairPresentation.KindName(plan.Kind))} · risco {Loc.T(RepairPresentation.RiskName(plan.Risk))} · ") +
            (plan.Volume is null ? Loc.T("sem volume") : Loc.T("volume ") + plan.Volume.Id));
        repairMessages.Set(nameof(RepairsDetailsText), () =>
            RepairsDetailsText.Text = BuildRepairPlanText(view, plan, real));
        RepairsConfirmationText.Text = string.Empty;
        repairMessages.Set(nameof(RepairsConfirmationHint), () => RepairsConfirmationHint.Text = canExecute
            ? Loc.T("Digite exatamente: ") + RepairManager.BuildConfirmationText(plan)
            : Loc.T("Plano bloqueado no backend: ") + string.Join(", ",
                (real ? plan.Preflight.Blockers : plan.Preflight.SimulationBlockers).Select(blocker => Loc.T(RepairPresentation.BlockerName(blocker)))));
        repairMessages.Set(nameof(RepairsOutputText), () => RepairsOutputText.Text = canExecute
            ? Loc.T("Plano preparado. A saída estará disponível depois de autorizar e iniciar.")
            : Loc.T("Plano bloqueado; nenhuma execução será iniciada."));
        SetRepairsStatus(() => !canExecute
            ? Loc.T("Plano bloqueado no backend; nenhuma ferramenta real será executada.")
            : real
                ? Loc.T("Plano preparado. Aguardando confirmação específica.")
                : Loc.T("Plano simulado preparado."));
        RepairsScenarioCombo.SelectedItem = (RepairsScenarioCombo.ItemsSource as IEnumerable<RepairScenarioRow>)
            ?.FirstOrDefault(row => row.Option.Scenario == DefaultScenario(view));
        UpdateRepairButtons();
    }

    private static RepairFixtureScenario DefaultScenario(RepairOperationView view)
    {
        if (string.Equals(view.Category, RepairCategories.Recovery, StringComparison.Ordinal))
            return RepairFixtureScenario.WinReEnabled;
        if (view.Kind == RepairOperationKind.Repair)
            return RepairFixtureScenario.SuccessRepairsCompleted;
        if (string.Equals(view.Category, RepairCategories.Image, StringComparison.Ordinal))
            return RepairFixtureScenario.CorruptionNotFound;
        return RepairFixtureScenario.SuccessNoIssues;
    }

    private async void OnRepairAuthorize(object sender, RoutedEventArgs e)
    {
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        if (repairs is null || repairsBusy || repairPlan is null || repairPersistenceFailed) return;
        var plan = repairPlan;
        if (!CanRunRepairPlan(plan))
        {
            SetRepairsStatus(() => Loc.T("Plano bloqueado no backend; nenhuma execução será aceita."));
            return;
        }
        var expected = RepairManager.BuildConfirmationText(plan);
        if (!string.Equals(RepairsConfirmationText.Text.Trim(), expected, StringComparison.Ordinal))
        {
            SetRepairsStatus(() => Loc.T("A confirmação digitada não corresponde à operação e ao plano apresentado."));
            return;
        }
        var real = repairs.RealExecutionEnabled;
        var scenario = (RepairsScenarioCombo.SelectedItem as RepairScenarioRow)?.Option.Scenario;
        if (!real && scenario is null)
        {
            SetRepairsStatus(() => Loc.T("Selecione um cenário sintético para a demonstração."));
            return;
        }

        repairsBusy = true;
        repairPersistenceFailed = false;
        repairPlan = null;
        RepairsConfirmationText.Clear();
        repairCancellation = new CancellationTokenSource();
        repairCommitted = false;
        repairElapsed.Restart();
        repairPhase = real ? "Preparando a solicitação ao broker" : "Preparando fixture sintética";
        RepairsProgress.Visibility = Visibility.Visible;
        RepairsProgress.IsIndeterminate = true;
        RepairsProgressLabel.Text = repairPhase;
        var liveOutput = new StringBuilder();
        var liveLines = 0;
        var reporting = true;
        repairElapsedTimer.Start();
        UpdateRepairButtons();
        try
        {
            var token = repairs.Authorize(plan, expected);
            var progress = new Progress<RepairProgress>(update =>
            {
                if (!reporting) return;
                repairCommitted |= update.ExecutionCommitted;
                repairPhase = update.Phase + (update.Detail is null ? string.Empty : " · " + update.Detail);
                UpdateRepairElapsed();
                if (real && update.Output is { } line && liveLines < RepairLimits.MaxOutputLines)
                {
                    var text = RepairOutputLimiter.Sanitize(line.Text);
                    if (liveOutput.Length + text.Length + 16 <= RepairLimits.MaxOutputBytes)
                    {
                        liveOutput.AppendLine(Loc.F($"[{line.Channel}] {text}"));
                        liveLines++;
                        // Registered so a language switch re-renders the live output instead of an older plan text.
                        repairMessages.Set(nameof(RepairsOutputText), () => RepairsOutputText.Text = liveOutput.ToString());
                    }
                }
                if (!real)
                    SetRepairsStatus(() => Loc.T("Execução sintética em andamento."));
            });
            var result = await repairs.ExecuteAsync(plan, token, real ? null : scenario, progress,
                repairCancellation.Token);
            reporting = false;
            if (result.Record is { } record)
            {
                repairRecord = record;
                repairPersistenceFailed = !result.Persisted;
                ShowRepairRecord(record);
            }
            await ReloadRepairHistoryAsync(result.Record?.Id, () => Loc.Text(result.Summary));
            SetRepairsStatus(() => Loc.Text(result.Summary) +
                (result.Blockers.Count == 0
                    ? string.Empty
                    : " " + Loc.T("Bloqueios:") + " " + string.Join(", ", result.Blockers.Select(blocker => Loc.T(RepairPresentation.BlockerName(blocker))))) +
                (result.Record is not null && !result.Persisted
                    ? Loc.T(" A gravação não foi concluída; use a nova tentativa.")
                    : string.Empty));
        }
        catch (OperationCanceledException)
        {
            SetRepairsStatus(() => Loc.T("Solicitação cancelada antes de iniciar."));
        }
        catch (Exception error)
        {
            ShowFailure(Loc.T("A execução de reparo não pôde ser acompanhada"), error);
            SetRepairsStatus(() => real
                ? Loc.T("Resultado indisponível. Revise a intenção e o estado do broker antes de uma nova tentativa.")
                : Loc.T("Falha na execução sintética."));
        }
        finally
        {
            reporting = false;
            InvalidateMaintenanceHistory();
            repairElapsedTimer.Stop();
            repairElapsed.Stop();
            repairsBusy = false;
            repairCancellation?.Dispose();
            repairCancellation = null;
            RepairsProgress.IsIndeterminate = false;
            RepairsProgress.Visibility = Visibility.Collapsed;
            UpdateRepairButtons();
            if (repairClosingAfterSave && !repairPersistenceFailed)
            {
                repairClosingAfterSave = false;
                Close();
            }
            else
            {
                repairClosingAfterSave = false;
            }
        }
    }

    private void OnRepairCancel(object sender, RoutedEventArgs e)
    {
        repairCancellation?.Cancel();
        RepairsCancelButton.IsEnabled = false;
        SetRepairsStatus(() => repairCommitted
            ? Loc.T("A autorização já foi enviada. O broker continuará acompanhando a ferramenta até o encerramento.")
            : Loc.T("Cancelamento solicitado. ") + Loc.T(RepairGuidance.CancellationLimitation));
    }

    private async void OnRepairRetrySave(object sender, RoutedEventArgs e)
    {
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        if (repairs is null || repairsBusy || repairRecord is null || !repairPersistenceFailed) return;
        repairsBusy = true;
        UpdateRepairButtons();
        try
        {
            repairRecord = await repairs.RetrySaveAsync(repairRecord);
            repairPersistenceFailed = false;
            InvalidateMaintenanceHistory();
            ShowRepairRecord(repairRecord);
            await ReloadRepairHistoryAsync(repairRecord.Id, () => Loc.T("Registro de reparo salvo novamente."));
        }
        catch (Exception error)
        {
            ShowFailure(Loc.T("Não foi possível salvar novamente o registro de reparo"), error);
            SetRepairsStatus(() => Loc.T("A gravação continua pendente; o resultado permanece nesta janela."));
        }
        finally
        {
            repairsBusy = false;
            UpdateRepairButtons();
        }
    }

    private void OnRepairHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (repairsUpdatingHistory || RepairsHistoryList.SelectedItem is not RepairHistoryRow row) return;
        if (row.Record is { } record)
        {
            repairSelectedRecord = record;
            ShowRepairRecord(record);
        }
        else if (row.Intent is { } intent)
        {
            repairSelectedRecord = null;
            ShowRepairIntent(intent);
        }
        UpdateRepairButtons();
    }

    private void ShowRepairIntent(RepairIntent intent)
    {
        repairMessages.Set(nameof(RepairsDetailsTitle), () => RepairsDetailsTitle.Text = intent.DisplayName);
        repairMessages.Set(nameof(RepairsDetailsMeta), () =>
            RepairsDetailsMeta.Text = Loc.F($"{intent.OperationId} v{intent.OperationVersion} · intenção sem registro final"));
        repairMessages.Set(nameof(RepairsDetailsText), () =>
            RepairsDetailsText.Text = BuildRepairIntentText(intent));
        repairMessages.Set(nameof(RepairsOutputText), () =>
            RepairsOutputText.Text = Loc.T("Nenhuma saída final registrada. A intenção é preservada e tratada como interrompida."));
        SetRepairsStatus(() => Loc.T("Intenção sem registro final; não há conclusão atribuída."));
    }

    private void ShowRepairRecord(RepairRecord record)
    {
        repairSelectedRecord = record;
        repairMessages.Set(nameof(RepairsDetailsTitle), () => RepairsDetailsTitle.Text = record.DisplayName);
        repairMessages.Set(nameof(RepairsDetailsMeta), () => RepairsDetailsMeta.Text =
            Loc.F($"{record.OperationId} v{record.OperationVersion} · ") +
            Loc.F($"{Loc.T(RepairPresentation.StateName(record.State))} · {Loc.T(RepairPresentation.ResultName(record.Result))} · ") +
            Loc.F($"confiança {Loc.T(RepairPresentation.ConfidenceName(record.Confidence))}"));
        repairMessages.Set(nameof(RepairsDetailsText), () =>
            RepairsDetailsText.Text = BuildRepairRecordText(record));
        repairMessages.Set(nameof(RepairsOutputText), () =>
            RepairsOutputText.Text = BuildRepairOutputText(record));
        SetRepairsStatus(() => record.IsSynthetic
            ? Loc.T("Resultado de fixture sintética; não representa o estado do Windows real.")
            : Loc.T("Resultado registrado; não declara sucesso absoluto."));
        UpdateRepairButtons();
    }

    private void OnRepairReport(object sender, RoutedEventArgs e)
    {
        var record = repairSelectedRecord ?? repairRecord;
        if (repairs is null || record is null) return;
        try
        {
            var path = repairs.GetReportPath(record.Id);
            if (!File.Exists(path)) return;
            _ = WindowsShell.OpenReportLocationAsync(path);
        }
        catch (Exception error)
        {
            ShowFailure(Loc.T("Não foi possível abrir o relatório de reparo"), error);
        }
    }

    private void UpdateRepairElapsed()
    {
        if (!repairsBusy) return;
        RepairsProgressLabel.Text = repairPhase + " · " + FormatElapsed(repairElapsed.Elapsed) + Loc.T(" · sem percentual confiável");
    }

    private void OnRepairConfirmationChanged(object sender, TextChangedEventArgs e) =>
        UpdateRepairButtons();

    private void UpdateRepairButtons()
    {
        if (RepairsAuthorizeButton is null) return;
        var idle = !operations.IsBusy && !repairsBusy;
        RepairsRefreshButton.IsEnabled = idle && repairs is not null;
        var expected = repairPlan is null ? string.Empty : RepairManager.BuildConfirmationText(repairPlan);
        var eligible = repairs?.RealExecutionEnabled == true
            ? repairPlan is { RealExecutionAllowed: true }
            : repairPlan is { SimulationAllowed: true } && RepairsScenarioCombo.SelectedItem is RepairScenarioRow;
        RepairsCardsHost.IsEnabled = idle && !repairPersistenceFailed;
        RepairsAuthorizeButton.IsEnabled = idle && !repairPersistenceFailed && repairs is not null && eligible &&
            string.Equals(RepairsConfirmationText.Text.Trim(), expected, StringComparison.Ordinal);
        RepairsCancelButton.IsEnabled = repairsBusy && repairCancellation is { IsCancellationRequested: false };
        RepairsRetrySaveButton.IsEnabled = idle && repairPersistenceFailed && repairRecord is not null;
        RepairsVolumeCombo.IsEnabled = idle && repairCenterView is { Volumes.Count: > 0 };
        RepairsScenarioCombo.IsEnabled = idle;
        var record = repairSelectedRecord ?? repairRecord;
        RepairsReportButton.IsEnabled = idle && repairs is not null && record is not null &&
            File.Exists(repairs.GetReportPath(record.Id));
    }

    private bool CanRunRepairPlan(RepairPlan plan) =>
        repairs?.RealExecutionEnabled == true ? plan.RealExecutionAllowed : plan.SimulationAllowed;

    private static string BuildRepairPlanText(RepairOperationView view, RepairPlan plan, bool real)
    {
        var text = new StringBuilder();
        text.AppendLine(real ? Loc.T("Execução pelo broker local") : RepairGuidance.RealExecutionUnavailable);
        text.AppendLine();
        text.AppendLine(Loc.F($"Comando oficial: {plan.Invocation.DisplayCommand}"));
        text.AppendLine(Loc.F($"Descrição: {Loc.T(view.Description)}"));
        text.AppendLine(Loc.F($"Tipo: {Loc.T(RepairPresentation.KindName(plan.Kind))} · Risco: {Loc.T(RepairPresentation.RiskName(plan.Risk))}"));
        text.AppendLine(view.RequiresElevation ? Loc.T("Privilégios exigidos: administrador.") : Loc.T("Privilégios exigidos: usuário atual."));
        text.AppendLine(Loc.F($"Processo atual: {(plan.Preflight.Profile.IsElevated ? "elevado" : "sem elevação")}"));
        text.AppendLine(Loc.F($"Hash do plano: {plan.PlanHash}"));
        text.AppendLine(Loc.F($"Volume: {(plan.Volume is null ? "não aplicável" : plan.Volume.Id + " · " + plan.Volume.FileSystem)}"));
        text.AppendLine(Loc.F($"Reinicialização: {(view.RequiresRestart ? "indicada pela definição" : "não indicada pela definição")}"));
        text.AppendLine(Loc.F($"Alteração do sistema: {(view.MayChangeSystem ? "possível; não há rollback convencional" : "nenhuma alteração de arquivos protegidos esperada")}"));
        text.AppendLine(real ? Loc.T("Cancelamento disponível até enviar a autorização ao broker.")
            : Loc.F($"Cancelamento seguro: {(view.CanCancelSafely ? "sim" : "não garantido")}"));
        text.AppendLine();
        text.AppendLine(Loc.T("Consequências"));
        foreach (var consequence in view.Consequences) text.AppendLine(Loc.F($"• {Loc.T(consequence)}"));
        text.AppendLine();
        text.AppendLine(Loc.T("Alterações possíveis"));
        foreach (var change in view.PossibleChanges) text.AppendLine(Loc.F($"• {Loc.T(change)}"));
        text.AppendLine();
        text.AppendLine(Loc.T("Preflight"));
        text.AppendLine(Loc.F($"• Simulação permitida: {(plan.Preflight.SimulationAllowed ? "sim" : "não")}"));
        text.AppendLine(Loc.T("• Broker: ") + (plan.RealExecutionAllowed ? Loc.T("prévia elegível; revalidação ao autorizar") : Loc.T("execução indisponível")));
        text.AppendLine(Loc.F($"• Windows 11: {(plan.Preflight.Profile.IsWindows11 ? "sim" : "não")} · build {plan.Preflight.Profile.Build}"));
        text.AppendLine(Loc.F($"• Executável oficial: {(plan.Preflight.Tool.Available ? plan.Preflight.Tool.Path : "indisponível")}"));
        foreach (var detail in plan.Preflight.Details) text.AppendLine(Loc.F($"• {detail}"));
        text.AppendLine();
        text.AppendLine(Loc.T("Bloqueios de simulação"));
        if (plan.Preflight.SimulationBlockers.Count == 0) text.AppendLine(Loc.T("• Nenhum."));
        else foreach (var blocker in plan.Preflight.SimulationBlockers)
            text.AppendLine(Loc.F($"• {Loc.T(RepairPresentation.BlockerName(blocker))}"));
        text.AppendLine();
        text.AppendLine(Loc.T("Limitações"));
        text.AppendLine("• " + RepairGuidance.NoRollback);
        text.AppendLine("• " + RepairGuidance.LocalOnly);
        foreach (var limitation in plan.Limitations.Distinct()) text.AppendLine(Loc.F($"• {Loc.T(limitation)}"));
        text.AppendLine();
        text.AppendLine(Loc.T("Resultado esperado"));
        text.AppendLine(Loc.T(view.ExpectedResult));
        text.AppendLine();
        text.AppendLine(Loc.T("Referência: ") + Loc.T(view.DocumentationReference));
        return text.ToString();
    }

    private static string BuildRepairRecordText(RepairRecord record)
    {
        var text = new StringBuilder();
        if (record.IsSynthetic) text.AppendLine(RepairGuidance.SyntheticDisclaimer);
        text.AppendLine(Loc.F($"Estado: {Loc.T(RepairPresentation.StateName(record.State))}"));
        text.AppendLine(Loc.F($"Resultado interpretado: {Loc.T(RepairPresentation.ResultName(record.Result))}"));
        text.AppendLine(Loc.F($"Confiança: {Loc.T(RepairPresentation.ConfidenceName(record.Confidence))}"));
        text.AppendLine(Loc.F($"Código de saída: {(record.ExitCodeText ?? "indisponível")}"));
        text.AppendLine(Loc.F($"Sucesso verificado: {(record.VerifiedSuccess ? "sim" : "não")}"));
        text.AppendLine(Loc.F($"Reinicialização indicada: {(record.RequiresRestart ? "sim" : "não")}"));
        text.AppendLine(Loc.F($"Intervenção posterior: {(record.RequiresIntervention ? "sim" : "não")}"));
        text.AppendLine(Loc.F($"Início: {record.StartedUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz}"));
        text.AppendLine(Loc.F($"Conclusão: {record.CompletedUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz}"));
        text.AppendLine(Loc.F($"Duração: {record.Duration.TotalSeconds:F2} s"));
        if (record.Native is { } native)
        {
            text.AppendLine(Loc.F($"Broker: {Loc.T(RepairPresentation.BrokerStatusName(native.Status))}"));
            text.AppendLine(Loc.F($"Consentimento UAC solicitado: {(native.ElevationRequested ? "sim" : "não")}"));
            text.AppendLine(Loc.F($"Início da ferramenta: {native.ProcessStartedUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz") ?? "indisponível"}"));
            text.AppendLine(Loc.F($"Término da ferramenta: {native.ProcessExitedUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz") ?? "indisponível"}"));
            text.AppendLine(Loc.T("Verificação independente posterior: não realizada."));
        }
        text.AppendLine();
        text.AppendLine(Loc.T("Evidências"));
        text.AppendLine(Loc.F($"• Corrupção detectada: {(record.Evidence.CorruptionDetected ? "sim" : "não")}"));
        text.AppendLine(Loc.F($"• Nenhuma corrupção detectada: {(record.Evidence.NoCorruptionDetected ? "sim" : "não")}"));
        text.AppendLine(Loc.F($"• Reparação relatada como concluída: {(record.Evidence.RepairCompleted ? "sim" : "não")}"));
        text.AppendLine(Loc.F($"• Reparação parcial: {(record.Evidence.RepairPartial ? "sim" : "não")}"));
        text.AppendLine(Loc.F($"• Fonte de reparação indisponível: {(record.Evidence.RepairSourceUnavailable ? "sim" : "não")}"));
        text.AppendLine(Loc.F($"• Acesso negado: {(record.Evidence.AccessDenied ? "sim" : "não")}"));
        text.AppendLine(Loc.F($"• WinRE habilitado: {(record.Evidence.WinReEnabled is null ? "não reconhecido" : record.Evidence.WinReEnabled.Value ? "sim" : "não")}"));
        text.AppendLine();
        text.AppendLine(Loc.T("Ações adicionais"));
        foreach (var action in record.AdditionalActions) text.AppendLine(Loc.F($"• {action}"));
        text.AppendLine();
        text.AppendLine(Loc.T("Passos"));
        foreach (var step in record.Steps.OrderBy(item => item.Sequence))
            text.AppendLine(Loc.F($"• {step.Sequence}. {Loc.T(RepairPresentation.StateName(step.State))} — ") +
                Loc.F($"{step.AtUtc.ToLocalTime():HH:mm:ss} — ") + Loc.Text(step.Detail));
        text.AppendLine();
        text.AppendLine(Loc.T("Limitações"));
        foreach (var limitation in record.Limitations.Distinct()) text.AppendLine(Loc.F($"• {Loc.T(limitation)}"));
        return text.ToString();
    }

    private static string BuildRepairOutputText(RepairRecord record)
    {
        var text = new StringBuilder();
        if (record.IsSynthetic) text.AppendLine(RepairGuidance.SyntheticDisclaimer);
        text.AppendLine(Loc.Text(record.OutputSummary));
        text.AppendLine();
        if (record.Output.Count == 0)
        {
            text.AppendLine(Loc.T("Nenhuma saída textual foi registrada."));
        }
        else
        {
            text.AppendLine(Loc.F($"Saída preservada: {record.Output.Count} linha(s), {record.OutputBytes} byte(s)") +
                (record.OutputTruncated ? Loc.T(" — limitada localmente.") : "."));
            text.AppendLine();
            foreach (var line in record.Output.OrderBy(item => item.Sequence))
                text.AppendLine((line.Channel is { } channel ? Loc.F($"[{channel}] ") : string.Empty) + line.Text);
        }
        text.AppendLine();
        text.AppendLine(Loc.T("Interpretação: ") + Loc.Text(record.ResultSummary));
        return text.ToString();
    }

    private static string BuildRepairIntentText(RepairIntent intent)
    {
        var text = new StringBuilder();
        if (intent.IsSynthetic) text.AppendLine(RepairGuidance.SyntheticDisclaimer);
        text.AppendLine(Loc.T("Intenção registrada antes do efeito, sem registro final."));
        text.AppendLine(Loc.F($"Operação: {intent.OperationId} v{intent.OperationVersion}"));
        text.AppendLine(Loc.F($"Comando oficial: {intent.InvocationDisplay}"));
        text.AppendLine(Loc.F($"Volume: {(intent.Volume is null ? "não aplicável" : intent.Volume.Id)}"));
        text.AppendLine(Loc.F($"Autorização: {intent.AuthorizationId:D}"));
        text.AppendLine(Loc.F($"Criado: {intent.CreatedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz}"));
        text.AppendLine();
        text.AppendLine(Loc.T("Estado: interrompido/indeterminado. Nenhuma conclusão é atribuída."));
        if (!intent.IsSynthetic)
            text.AppendLine(Loc.T("A ferramenta pode continuar após a desconexão. Investigue o estado antes de preparar novo plano."));
        text.AppendLine();
        text.AppendLine(Loc.T("Limitações"));
        foreach (var limitation in intent.Limitations.Distinct()) text.AppendLine(Loc.F($"• {Loc.T(limitation)}"));
        return text.ToString();
    }

    private static string BuildDocumentedText(IReadOnlyList<RepairOperationDescriptor> operations)
    {
        var text = new StringBuilder();
        text.AppendLine(Loc.T("Operações documentadas para avaliação futura; nenhuma delas é executável nesta fase."));
        text.AppendLine();
        foreach (var operation in operations)
        {
            text.AppendLine(Loc.F($"• {operation.Id} — {operation.DisplayCommand} — risco {operation.RiskName}"));
            text.AppendLine(Loc.F($"  {Loc.T(operation.Description)}"));
            text.AppendLine(Loc.F($"  Motivo: {Loc.T(operation.DocumentedOnlyReason ?? string.Empty)}"));
        }
        text.AppendLine();
        text.AppendLine(Loc.T("Também fora de escopo nesta fase: alterações de BCD, reinicialização para WinRE, ") +
            Loc.T("redefinição do Windows e agendamento automático de correções no boot."));
        return text.ToString();
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";

    private sealed record RepairCategoryRow(string CategoryKey, IReadOnlyList<RepairCardRow> Cards)
    {
        public string Category => Loc.T(CategoryKey);
    }

    private sealed record RepairCardRow(RepairOperationView View)
    {
        public string OperationId => View.OperationId;
        public string DisplayName => Loc.T(View.DisplayName);
        public string Category => Loc.T(View.Category);
        public string Description => Loc.T(View.Description);
        public string KindSummary => Loc.T("Tipo: ") + Loc.T(View.KindName);
        public string RiskSummary => Loc.T("Risco: ") + Loc.T(View.RiskName);
        public string Privileges => Loc.T(View.RequiresElevation
            ? "Privilégios exigidos: administrador"
            : "Privilégios: usuário atual");
        public string Command => Loc.T("Comando: ") + View.OfficialCommand;
        public string RestartSummary => Loc.T(View.RequiresRestart
            ? "Reinicialização: indicada"
            : "Reinicialização: não indicada");
        public string Availability => Loc.T("Disponibilidade: ") + Loc.T(View.Availability) +
            (View.SimulationAllowed
                ? string.Empty
                : " · " + string.Join(", ", View.Blockers.Select(blocker => Loc.T(RepairPresentation.BlockerName(blocker)))));
        public string ConsequencesSummary => Loc.T("Consequências: ") + string.Join(" ", View.Consequences.Select(Loc.T));
        public string LimitationsSummary => View.Limitations.Count == 0
            ? string.Empty
            : Loc.T("Limitações: ") + string.Join(" · ", View.Limitations.Select(Loc.T));
    }

    private sealed record RepairVolumeRow(RepairVolume Volume)
    {
        public string DisplayText => Volume.DisplayName + (Volume.IsReady ? string.Empty : Loc.T(" · indisponível"));
        public override string ToString() => DisplayText;
    }

    private sealed record RepairScenarioRow(RepairScenarioOption Option)
    {
        public string Label => Loc.T(Option.Label);
        public override string ToString() => Label;
    }

    private sealed record RepairHistoryRow(RepairRecord? Record, RepairIntent? Intent)
    {
        public string DateText => (Record?.CompletedUtc ?? Intent?.CreatedAtUtc ?? DateTimeOffset.MinValue)
            .ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
        public string Title => Record?.DisplayName ?? Intent?.DisplayName ?? Loc.T("Reparo");
        public string SummaryText => Record is { } record
            ? $"{Loc.T(RepairPresentation.StateName(record.State))} · {Loc.T(RepairPresentation.ResultName(record.Result))}" +
              (record.IsSynthetic ? " · fixture" : string.Empty)
            : Loc.T("Intenção sem registro final · interrompida");
    }
}
