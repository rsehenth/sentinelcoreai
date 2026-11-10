using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows;

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

    private void InitializeRepairs()
    {
        repairElapsedTimer.Tick += (_, _) => UpdateRepairElapsed();
        if (repairs is null)
        {
            RepairsModeText.Text = "Repair Center indisponível nesta composição.";
            RepairsModeDetail.Text = "Nenhum catálogo de reparos foi composto.";
            RepairsRefreshButton.IsEnabled = false;
            RepairsAuthorizeButton.IsEnabled = false;
            return;
        }

        RepairsModeText.Text = RepairGuidance.RealExecutionUnavailable;
        RepairsModeDetail.Text = "Catálogo fechado com executáveis oficiais e argumentos fixos. " +
            "A execução real não é composta nesta distribuição. Os cartões demonstram o fluxo com fixtures " +
            "sintéticas explicitamente marcadas; nenhuma elevação é solicitada.";
        RepairsSyntheticBanner.Text = RepairGuidance.SyntheticDisclaimer;
        RepairsScenarioCombo.ItemsSource = RepairPresentation.Scenarios
            .Select(option => new RepairScenarioRow(option, option.Label)).ToArray();
        RepairsScenarioCombo.SelectedIndex = 0;
        if (repairs.RealExecutionEnabled)
        {
            RepairsModeText.Text = "Execução pelo broker local";
            RepairsModeDetail.Text = "Selecione uma operação e confirme o plano. O Windows solicita consentimento quando necessário.";
            RepairsScenarioPanel.Visibility = Visibility.Collapsed;
            RepairsSyntheticBanner.Visibility = Visibility.Collapsed;
            RepairsAuthorizeButton.Content = "Autorizar e executar";
        }
        RepairsVolumeText.Text = "Volumes ainda não lidos; a leitura ocorre ao abrir esta página ou em Atualizar.";
        RepairsStatusText.Text = "Catálogo e volumes ainda não lidos. Nenhuma ferramenta foi executada.";
        Pages.SelectionChanged += OnRepairsPageSelectionChanged;
        UpdateRepairButtons();
    }

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

    private async Task ReloadRepairsAsync(Guid? selectRecord = null, string? status = null,
        bool ignoreBusy = false)
    {
        if (repairs is null || (repairsBusy && !ignoreBusy)) return;
        using var productLease = ignoreBusy ? null : TryBeginProductOperation();
        if (!ignoreBusy && productLease is null) return;
        repairsBusy = true;
        UpdateRepairButtons();
        RepairsStatusText.Text = "Lendo catálogo, volumes e histórico…";
        try
        {
            repairCenterView = await repairs.LoadAsync();
            RepairsModeText.Text = repairCenterView.ModeText;
            RepairsModeDetail.Text = repairCenterView.ModeDetail;
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

            RepairsDocumentedText.Text = BuildDocumentedText(repairCenterView.DocumentedOperations);
            await ReloadRepairHistoryAsync(selectRecord);
            repairsLoaded = true;
            RepairsStatusText.Text = status ??
                "Catálogo lido. Selecione 'Detalhes' em um cartão para preparar o plano.";
            ScrollRepairsToTop();
        }
        catch (Exception error)
        {
            ShowFailure("Não foi possível ler o Repair Center", error);
            RepairsStatusText.Text = "Falha ao ler o catálogo; nenhuma ferramenta real foi executada.";
        }
        finally
        {
            repairsBusy = false;
            UpdateRepairButtons();
        }
    }

    private async Task ReloadRepairHistoryAsync(Guid? selectId = null, string? status = null)
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

        RepairsUnfinishedText.Text = repairHistory.UnfinishedIntents.Count == 0
            ? repairHistory.Warnings.Count > 0
                ? "Histórico incompleto: nenhuma intenção válida sem registro final carregada. O total não foi determinado; examine os avisos."
                : "Nenhuma intenção sem registro final."
            : $"{repairHistory.UnfinishedIntents.Count} intenção(ões) sem registro final; tratadas como interrompidas.";
        SetIntegrity("Reparos", repairHistory.Warnings);
        if (status is not null) RepairsStatusText.Text = status;
        UpdateRepairButtons();
    }

    private void OnRepairVolumeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (repairsUpdatingHistory) return;
        repairPlan = null;
        repairSelectedView = null;
        RepairsDetailsTitle.Text = "Plano e confirmação";
        RepairsDetailsMeta.Text = string.Empty;
        RepairsDetailsText.Text = "Selecione 'Detalhes' em um cartão para preparar o plano simulado.";
        RepairsConfirmationText.Text = string.Empty;
        RepairsConfirmationHint.Text = "A execução exige confirmação digitada vinculada ao plano.";
        UpdateRepairVolumeText();
        UpdateRepairButtons();
    }

    private void UpdateRepairVolumeText()
    {
        if (RepairsVolumeCombo.SelectedItem is not RepairVolumeRow row)
        {
            RepairsVolumeText.Text = "Nenhum volume local validado foi encontrado. " +
                "Operações de disco permanecem indisponíveis.";
            return;
        }
        var volume = row.Volume;
        var scan = volume.SupportsChkdskScan ? "verificação /scan compatível" : "verificação /scan indisponível";
        RepairsVolumeText.Text = $"{volume.Id} · {volume.FileSystem} · " +
            $"{(volume.IsReady ? "pronto" : "indisponível")} · {scan}" +
            (volume.Limitations.Count == 0 ? string.Empty : " · " + volume.Limitations[0]);
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
            RepairsStatusText.Text = "Selecione um volume local validado antes de preparar esta operação.";
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
            ShowFailure("Não foi possível preparar o plano de reparo", error);
            RepairsStatusText.Text = "Preparação indisponível; nenhuma ferramenta real foi executada.";
        }
        finally
        {
            repairsBusy = false;
            UpdateRepairButtons();
        }
    }

    private void ShowRepairPlan(RepairOperationView view, RepairPlan plan)
    {
        RepairsDetailsTitle.Text = plan.DisplayName;
        RepairsDetailsMeta.Text = $"{plan.OperationId} v{plan.OperationVersion} · " +
            $"{RepairPresentation.KindName(plan.Kind)} · risco {RepairPresentation.RiskName(plan.Risk)} · " +
            (plan.Volume is null ? "sem volume" : "volume " + plan.Volume.Id);
        var real = repairs?.RealExecutionEnabled == true;
        var canExecute = CanRunRepairPlan(plan);
        RepairsDetailsText.Text = BuildRepairPlanText(view, plan, real);
        RepairsConfirmationText.Text = string.Empty;
        RepairsConfirmationHint.Text = canExecute
            ? "Digite exatamente: " + RepairManager.BuildConfirmationText(plan)
            : "Plano bloqueado no backend: " + string.Join(", ",
                (real ? plan.Preflight.Blockers : plan.Preflight.SimulationBlockers).Select(RepairPresentation.BlockerName));
        RepairsScenarioCombo.SelectedItem = (RepairsScenarioCombo.ItemsSource as IEnumerable<RepairScenarioRow>)
            ?.FirstOrDefault(row => row.Option.Scenario == DefaultScenario(view));
        RepairsOutputText.Text = canExecute
            ? "Plano preparado. A saída estará disponível depois de autorizar e iniciar."
            : "Plano bloqueado; nenhuma execução será iniciada.";
        RepairsStatusText.Text = canExecute
            ? real ? "Plano preparado. Aguardando confirmação específica." : "Plano simulado preparado."
            : "Plano bloqueado no backend; nenhuma ferramenta real será executada.";
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
            RepairsStatusText.Text = "Plano bloqueado no backend; nenhuma execução será aceita.";
            return;
        }
        var expected = RepairManager.BuildConfirmationText(plan);
        if (!string.Equals(RepairsConfirmationText.Text.Trim(), expected, StringComparison.Ordinal))
        {
            RepairsStatusText.Text = "A confirmação digitada não corresponde à operação e ao plano apresentado.";
            return;
        }
        var real = repairs.RealExecutionEnabled;
        var scenario = (RepairsScenarioCombo.SelectedItem as RepairScenarioRow)?.Option.Scenario;
        if (!real && scenario is null)
        {
            RepairsStatusText.Text = "Selecione um cenário sintético para a demonstração.";
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
                        liveOutput.AppendLine($"[{line.Channel}] {text}");
                        liveLines++;
                        RepairsOutputText.Text = liveOutput.ToString();
                    }
                }
                if (!real)
                    RepairsStatusText.Text = "Execução sintética em andamento.";
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
            await ReloadRepairHistoryAsync(result.Record?.Id, result.Summary);
            var blockers = result.Blockers.Count == 0
                ? string.Empty
                : " Bloqueios: " + string.Join(", ", result.Blockers.Select(RepairPresentation.BlockerName));
            RepairsStatusText.Text = result.Summary + blockers +
                (result.Record is not null && !result.Persisted ? " A gravação não foi concluída; use a nova tentativa." : string.Empty);
        }
        catch (OperationCanceledException)
        {
            RepairsStatusText.Text = "Solicitação cancelada antes de iniciar.";
        }
        catch (Exception error)
        {
            ShowFailure("A execução de reparo não pôde ser acompanhada", error);
            RepairsStatusText.Text = real
                ? "Resultado indisponível. Revise a intenção e o estado do broker antes de uma nova tentativa."
                : "Falha na execução sintética.";
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
        RepairsStatusText.Text = repairCommitted
            ? "A autorização já foi enviada. O broker continuará acompanhando a ferramenta até o encerramento."
            : "Cancelamento solicitado. " + RepairGuidance.CancellationLimitation;
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
            await ReloadRepairHistoryAsync(repairRecord.Id, "Registro de reparo salvo novamente.");
        }
        catch (Exception error)
        {
            ShowFailure("Não foi possível salvar novamente o registro de reparo", error);
            RepairsStatusText.Text = "A gravação continua pendente; o resultado permanece nesta janela.";
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
            RepairsDetailsTitle.Text = intent.DisplayName;
            RepairsDetailsMeta.Text = $"{intent.OperationId} v{intent.OperationVersion} · intenção sem registro final";
            RepairsDetailsText.Text = BuildRepairIntentText(intent);
            RepairsOutputText.Text = "Nenhuma saída final registrada. A intenção é preservada e tratada como interrompida.";
            RepairsStatusText.Text = "Intenção sem registro final; não há conclusão atribuída.";
        }
        UpdateRepairButtons();
    }

    private void ShowRepairRecord(RepairRecord record)
    {
        repairSelectedRecord = record;
        RepairsDetailsTitle.Text = record.DisplayName;
        RepairsDetailsMeta.Text = $"{record.OperationId} v{record.OperationVersion} · " +
            $"{RepairPresentation.StateName(record.State)} · {RepairPresentation.ResultName(record.Result)} · " +
            $"confiança {RepairPresentation.ConfidenceName(record.Confidence)}";
        RepairsDetailsText.Text = BuildRepairRecordText(record);
        RepairsOutputText.Text = BuildRepairOutputText(record);
        RepairsStatusText.Text = record.IsSynthetic
            ? "Resultado de fixture sintética; não representa o estado do Windows real."
            : "Resultado registrado; não declara sucesso absoluto.";
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
            ShowFailure("Não foi possível abrir o relatório de reparo", error);
        }
    }

    private void UpdateRepairElapsed()
    {
        if (!repairsBusy) return;
        RepairsProgressLabel.Text = repairPhase + " · " + FormatElapsed(repairElapsed.Elapsed) + " · sem percentual confiável";
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
        text.AppendLine(real ? "Execução pelo broker local" : RepairGuidance.RealExecutionUnavailable);
        text.AppendLine();
        text.AppendLine($"Comando oficial: {plan.Invocation.DisplayCommand}");
        text.AppendLine($"Descrição: {view.Description}");
        text.AppendLine($"Tipo: {RepairPresentation.KindName(plan.Kind)} · Risco: {RepairPresentation.RiskName(plan.Risk)}");
        text.AppendLine(view.RequiresElevation ? "Privilégios exigidos: administrador." : "Privilégios exigidos: usuário atual.");
        text.AppendLine($"Processo atual: {(plan.Preflight.Profile.IsElevated ? "elevado" : "sem elevação")}");
        text.AppendLine($"Hash do plano: {plan.PlanHash}");
        text.AppendLine($"Volume: {(plan.Volume is null ? "não aplicável" : plan.Volume.Id + " · " + plan.Volume.FileSystem)}");
        text.AppendLine($"Reinicialização: {(view.RequiresRestart ? "indicada pela definição" : "não indicada pela definição")}");
        text.AppendLine($"Alteração do sistema: {(view.MayChangeSystem ? "possível; não há rollback convencional" : "nenhuma alteração de arquivos protegidos esperada")}");
        text.AppendLine(real ? "Cancelamento disponível até enviar a autorização ao broker."
            : $"Cancelamento seguro: {(view.CanCancelSafely ? "sim" : "não garantido")}");
        text.AppendLine();
        text.AppendLine("Consequências");
        foreach (var consequence in view.Consequences) text.AppendLine($"• {consequence}");
        text.AppendLine();
        text.AppendLine("Alterações possíveis");
        foreach (var change in view.PossibleChanges) text.AppendLine($"• {change}");
        text.AppendLine();
        text.AppendLine("Preflight");
        text.AppendLine($"• Simulação permitida: {(plan.Preflight.SimulationAllowed ? "sim" : "não")}");
        text.AppendLine("• Broker: " + (plan.RealExecutionAllowed ? "prévia elegível; revalidação ao autorizar" : "execução indisponível"));
        text.AppendLine($"• Windows 11: {(plan.Preflight.Profile.IsWindows11 ? "sim" : "não")} · build {plan.Preflight.Profile.Build}");
        text.AppendLine($"• Executável oficial: {(plan.Preflight.Tool.Available ? plan.Preflight.Tool.Path : "indisponível")}");
        foreach (var detail in plan.Preflight.Details) text.AppendLine($"• {detail}");
        text.AppendLine();
        text.AppendLine("Bloqueios de simulação");
        if (plan.Preflight.SimulationBlockers.Count == 0) text.AppendLine("• Nenhum.");
        else foreach (var blocker in plan.Preflight.SimulationBlockers)
            text.AppendLine($"• {RepairPresentation.BlockerName(blocker)}");
        text.AppendLine();
        text.AppendLine("Limitações");
        text.AppendLine("• " + RepairGuidance.NoRollback);
        text.AppendLine("• " + RepairGuidance.LocalOnly);
        foreach (var limitation in plan.Limitations.Distinct()) text.AppendLine($"• {limitation}");
        text.AppendLine();
        text.AppendLine("Resultado esperado");
        text.AppendLine(view.ExpectedResult);
        text.AppendLine();
        text.AppendLine("Referência: " + view.DocumentationReference);
        return text.ToString();
    }

    private static string BuildRepairRecordText(RepairRecord record)
    {
        var text = new StringBuilder();
        if (record.IsSynthetic) text.AppendLine(RepairGuidance.SyntheticDisclaimer);
        text.AppendLine($"Estado: {RepairPresentation.StateName(record.State)}");
        text.AppendLine($"Resultado interpretado: {RepairPresentation.ResultName(record.Result)}");
        text.AppendLine($"Confiança: {RepairPresentation.ConfidenceName(record.Confidence)}");
        text.AppendLine($"Código de saída: {(record.ExitCodeText ?? "indisponível")}");
        text.AppendLine($"Sucesso verificado: {(record.VerifiedSuccess ? "sim" : "não")}");
        text.AppendLine($"Reinicialização indicada: {(record.RequiresRestart ? "sim" : "não")}");
        text.AppendLine($"Intervenção posterior: {(record.RequiresIntervention ? "sim" : "não")}");
        text.AppendLine($"Início: {record.StartedUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz}");
        text.AppendLine($"Conclusão: {record.CompletedUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz}");
        text.AppendLine($"Duração: {record.Duration.TotalSeconds:F2} s");
        if (record.Native is { } native)
        {
            text.AppendLine($"Broker: {RepairPresentation.BrokerStatusName(native.Status)}");
            text.AppendLine($"Consentimento UAC solicitado: {(native.ElevationRequested ? "sim" : "não")}");
            text.AppendLine($"Início da ferramenta: {native.ProcessStartedUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz") ?? "indisponível"}");
            text.AppendLine($"Término da ferramenta: {native.ProcessExitedUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz") ?? "indisponível"}");
            text.AppendLine("Verificação independente posterior: não realizada.");
        }
        text.AppendLine();
        text.AppendLine("Evidências");
        text.AppendLine($"• Corrupção detectada: {(record.Evidence.CorruptionDetected ? "sim" : "não")}");
        text.AppendLine($"• Nenhuma corrupção detectada: {(record.Evidence.NoCorruptionDetected ? "sim" : "não")}");
        text.AppendLine($"• Reparação relatada como concluída: {(record.Evidence.RepairCompleted ? "sim" : "não")}");
        text.AppendLine($"• Reparação parcial: {(record.Evidence.RepairPartial ? "sim" : "não")}");
        text.AppendLine($"• Fonte de reparação indisponível: {(record.Evidence.RepairSourceUnavailable ? "sim" : "não")}");
        text.AppendLine($"• Acesso negado: {(record.Evidence.AccessDenied ? "sim" : "não")}");
        text.AppendLine($"• WinRE habilitado: {(record.Evidence.WinReEnabled is null ? "não reconhecido" : record.Evidence.WinReEnabled.Value ? "sim" : "não")}");
        text.AppendLine();
        text.AppendLine("Ações adicionais");
        foreach (var action in record.AdditionalActions) text.AppendLine($"• {action}");
        text.AppendLine();
        text.AppendLine("Passos");
        foreach (var step in record.Steps.OrderBy(item => item.Sequence))
            text.AppendLine($"• {step.Sequence}. {RepairPresentation.StateName(step.State)} — " +
                $"{step.AtUtc.ToLocalTime():HH:mm:ss} — {step.Detail}");
        text.AppendLine();
        text.AppendLine("Limitações");
        foreach (var limitation in record.Limitations.Distinct()) text.AppendLine($"• {limitation}");
        return text.ToString();
    }

    private static string BuildRepairOutputText(RepairRecord record)
    {
        var text = new StringBuilder();
        if (record.IsSynthetic) text.AppendLine(RepairGuidance.SyntheticDisclaimer);
        text.AppendLine(record.OutputSummary);
        text.AppendLine();
        if (record.Output.Count == 0)
        {
            text.AppendLine("Nenhuma saída textual foi registrada.");
        }
        else
        {
            text.AppendLine($"Saída preservada: {record.Output.Count} linha(s), {record.OutputBytes} byte(s)" +
                (record.OutputTruncated ? " — limitada localmente." : "."));
            text.AppendLine();
            foreach (var line in record.Output.OrderBy(item => item.Sequence))
                text.AppendLine((line.Channel is { } channel ? $"[{channel}] " : string.Empty) + line.Text);
        }
        text.AppendLine();
        text.AppendLine("Interpretação: " + record.ResultSummary);
        return text.ToString();
    }

    private static string BuildRepairIntentText(RepairIntent intent)
    {
        var text = new StringBuilder();
        if (intent.IsSynthetic) text.AppendLine(RepairGuidance.SyntheticDisclaimer);
        text.AppendLine("Intenção registrada antes do efeito, sem registro final.");
        text.AppendLine($"Operação: {intent.OperationId} v{intent.OperationVersion}");
        text.AppendLine($"Comando oficial: {intent.InvocationDisplay}");
        text.AppendLine($"Volume: {(intent.Volume is null ? "não aplicável" : intent.Volume.Id)}");
        text.AppendLine($"Autorização: {intent.AuthorizationId:D}");
        text.AppendLine($"Criado: {intent.CreatedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz}");
        text.AppendLine();
        text.AppendLine("Estado: interrompido/indeterminado. Nenhuma conclusão é atribuída.");
        if (!intent.IsSynthetic)
            text.AppendLine("A ferramenta pode continuar após a desconexão. Investigue o estado antes de preparar novo plano.");
        text.AppendLine();
        text.AppendLine("Limitações");
        foreach (var limitation in intent.Limitations.Distinct()) text.AppendLine($"• {limitation}");
        return text.ToString();
    }

    private static string BuildDocumentedText(IReadOnlyList<RepairOperationDescriptor> operations)
    {
        var text = new StringBuilder();
        text.AppendLine("Operações documentadas para avaliação futura; nenhuma delas é executável nesta fase.");
        text.AppendLine();
        foreach (var operation in operations)
        {
            text.AppendLine($"• {operation.Id} — {operation.DisplayCommand} — risco {operation.RiskName}");
            text.AppendLine($"  {operation.Description}");
            text.AppendLine($"  Motivo: {operation.DocumentedOnlyReason}");
        }
        text.AppendLine();
        text.AppendLine("Também fora de escopo nesta fase: alterações de BCD, reinicialização para WinRE, " +
            "redefinição do Windows e agendamento automático de correções no boot.");
        return text.ToString();
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";

    private sealed record RepairCategoryRow(string Category, IReadOnlyList<RepairCardRow> Cards);

    private sealed record RepairCardRow(RepairOperationView View)
    {
        public string OperationId => View.OperationId;
        public string DisplayName => View.DisplayName;
        public string Category => View.Category;
        public string Description => View.Description;
        public string KindSummary => "Tipo: " + View.KindName;
        public string RiskSummary => "Risco: " + View.RiskName;
        public string Privileges => View.RequiresElevation
            ? "Privilégios exigidos: administrador"
            : "Privilégios: usuário atual";
        public string Command => "Comando: " + View.OfficialCommand;
        public string RestartSummary => View.RequiresRestart
            ? "Reinicialização: indicada"
            : "Reinicialização: não indicada";
        public string Availability => "Disponibilidade: " + View.Availability +
            (View.SimulationAllowed
                ? string.Empty
                : " · " + string.Join(", ", View.Blockers.Select(RepairPresentation.BlockerName)));
        public string ConsequencesSummary => "Consequências: " + string.Join(" ", View.Consequences);
        public string LimitationsSummary => View.Limitations.Count == 0
            ? string.Empty
            : "Limitações: " + string.Join(" · ", View.Limitations);
    }

    private sealed record RepairVolumeRow(RepairVolume Volume)
    {
        public string DisplayText => Volume.DisplayName + (Volume.IsReady ? string.Empty : " · indisponível");
        public override string ToString() => DisplayText;
    }

    private sealed record RepairScenarioRow(RepairScenarioOption Option, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record RepairHistoryRow(RepairRecord? Record, RepairIntent? Intent)
    {
        public string DateText => (Record?.CompletedUtc ?? Intent?.CreatedAtUtc ?? DateTimeOffset.MinValue)
            .ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
        public string Title => Record?.DisplayName ?? Intent?.DisplayName ?? "Reparo";
        public string SummaryText => Record is { } record
            ? $"{RepairPresentation.StateName(record.State)} · {RepairPresentation.ResultName(record.Result)}" +
              (record.IsSynthetic ? " · fixture" : string.Empty)
            : "Intenção sem registro final · interrompida";
    }
}
