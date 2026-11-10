using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.App.Localization;

namespace SentinelCore.App;

public partial class MainWindow
{
    private readonly TweakManager? tweaks;
    private IReadOnlyList<TweakOperationView> tweakViews = [];
    private TaskbarSearchModeController? searchModes;
    private TaskbarSearchModeState? searchModeState;
    private SimulationResult? tweakSimulation;
    private TransactionRecord? tweakSelectedTransaction;
    private RecoveryPreview? tweakPreview;
    private bool tweaksBusy;
    private bool tweaksLoaded;

    private void InitializeTweaks()
    {
        if (tweaks is null)
        {
            TweaksModeText.Text = Loc.T("Personalização indisponível nesta composição.");
            TweaksModeDetail.Text = Loc.T("Nenhum motor de tweaks foi composto.");
            TweaksRefreshButton.IsEnabled = false;
            TweakSearchModeCombo.IsEnabled = false;
            TweakSearchModePrepareButton.IsEnabled = false;
            return;
        }

        searchModes = new TaskbarSearchModeController(tweaks);
        (TweaksModeText.Text, TweaksModeDetail.Text) = tweaks.Mode switch
        {
            TweakExecutionMode.RealControlled => (
                "Modo real controlado — HKCU do usuário atual",
                $"{tweaks.Operations.Count} operações cadastradas, sem elevação e sem reiniciar o Explorer. " +
                "Cada escrita exige plano, backup, autorização específica e verificação."),
            TweakExecutionMode.Validation => (
                "Modo de validação — adaptadores sintéticos",
                "Nenhum Registro real é lido ou alterado; o modo existe para testes automatizados."),
            _ => (
                "Modo real desabilitado — leitura e simulação",
                "A escrita está bloqueada no backend nesta distribuição. A validação real permanece pendente " +
                "e não é executada no computador de desenvolvimento.")
        };
        TweaksSubtitle.Text = tweaks.RealExecutionEnabled
            ? Loc.F($"{tweaks.Operations.Count} operações HKCU · backup obrigatório · rollback pelo estado observado.")
            : Loc.F($"{tweaks.Operations.Count} operações HKCU em modo de leitura/simulação · escrita real indisponível.");
        Pages.SelectionChanged += OnTweaksPageSelectionChanged;
        UpdateTweakButtons();
    }

    private async void OnTweaksPageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Pages) || !ReferenceEquals(Pages.SelectedItem, TweaksTab)) return;
        ScrollTweaksToTop();
        if (tweaksBusy) return;
        if (!tweaksLoaded) await ReloadTweaksAsync();
        // Both modules share store.lock; finish the initial Tweaks read before starting Debloat.
        if (ReferenceEquals(Pages.SelectedItem, TweaksTab) && !debloatComposed && debloatManager is not null)
        {
            debloatComposed = true;
            DebloatSection.Initialize(debloatManager, debloatObserver);
        }
    }

    private void ScrollTweaksToTop()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TweaksScroll.ScrollToTop();
            TweaksRefreshButton.Focus();
            TweaksScroll.ScrollToTop();
        }), DispatcherPriority.ContextIdle);
    }

    private async void OnTweaksRefresh(object sender, RoutedEventArgs e) =>
        await ReloadTweaksAsync();

    private async Task ReloadTweaksAsync(Guid? selectTransaction = null, string? status = null,
        bool ignoreBusy = false)
    {
        if (tweaks is null || (tweaksBusy && !ignoreBusy)) return;
        using var productLease = ignoreBusy ? null : TryBeginProductOperation();
        if (!ignoreBusy && productLease is null) return;
        tweaksBusy = true;
        tweakSimulation = null;
        TweakConfirmationText.Text = string.Empty;
        TweakDetailsText.Text = Loc.T("Lendo o estado atual do perfil…");
        UpdateTweakButtons();
        try
        {
            tweakViews = await tweaks.LoadAsync();
            TweaksCardsHost.ItemsSource = TweakPresentation.GroupByCategory(tweakViews)
                .Select(group => new TweakCategoryRow(group.Category,
                    group.Operations.Select(view => new TweakCardRow(view)).ToArray()))
                .ToArray();
            await ReloadSearchModeAsync();
            await ReloadTweakHistoryAsync(selectTransaction);
            tweaksLoaded = true;
            TweakDetailsText.Text = Loc.T("Selecione 'Detalhes' em um cartão para ler o estado atual e simular o plano.");
            TweakStatusText.Text = status ?? Loc.T("Estado atual lido. Selecione 'Detalhes' para simular o plano.");
            ScrollTweaksToTop();
        }
        catch (Exception error)
        {
            ShowFailure(Loc.T("Não foi possível ler o estado dos tweaks"), error);
            TweakStatusText.Text = Loc.T("Falha ao ler o estado; nenhuma escrita foi executada.");
        }
        finally
        {
            tweaksBusy = false;
            UpdateTweakButtons();
        }
    }

    private async Task ReloadTweakHistoryAsync(Guid? selectId = null)
    {
        if (tweaks is null) return;
        var history = await tweaks.LoadHistorySnapshotAsync();
        SetIntegrity("Personalização", history.Warnings);
        var names = tweakViews.ToDictionary(item => item.OperationId, item => item.DisplayName);
        var rows = history.Transactions.Select(item => new TweakHistoryRow(item,
            names.GetValueOrDefault(item.OperationId, item.OperationId))).ToArray();
        TweakHistoryList.ItemsSource = rows;
        if (selectId is { } id)
        {
            var selected = rows.FirstOrDefault(item => item.Transaction.TransactionId == id);
            if (selected is not null)
            {
                TweakHistoryList.SelectedItem = selected;
                TweakHistoryList.ScrollIntoView(selected);
            }
        }
        else
        {
            TweakHistoryList.SelectedItem = null;
            tweakSelectedTransaction = null;
            tweakPreview = null;
        }
    }

    private async Task ReloadSearchModeAsync()
    {
        if (searchModes is null) return;
        try
        {
            searchModeState = await searchModes.LoadAsync();
            var options = searchModeState.Options;
            TweakSearchModeCombo.ItemsSource = options;
            TweakSearchModeCombo.SelectedItem = searchModeState.ObservedMode is { } observed
                ? options.FirstOrDefault(option => option.Mode == observed)
                : null;
            TweakSearchModeStatus.Text = Loc.T("Modo observado: ") + searchModeState.ObservedLabel +
                (searchModeState.Limitation is null ? string.Empty : " · " + searchModeState.Limitation) +
                Loc.T(" · selecionar não altera o Windows.");
        }
        catch (Exception error)
        {
            searchModeState = null;
            TweakSearchModeCombo.ItemsSource = null;
            TweakSearchModeStatus.Text = Loc.T("Modo de pesquisa indisponível: ") + error.Message;
        }
    }

    private void OnTweakSearchModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!tweaksBusy && searchModeState is not null &&
            TweakSearchModeCombo.SelectedItem is TaskbarSearchMode mode)
        {
            TweakSearchModeStatus.Text = searchModeState.IsSelected(mode.Mode)
                ? Loc.F($"Modo observado selecionado ({mode.Mode} — {mode.DisplayName}). Nenhuma alteração foi feita.")
                : Loc.F($"Seleção {mode.Mode} — {mode.DisplayName} preparada. Clique em 'Simular plano' para ") +
                  Loc.T("simular; nenhuma alteração foi feita.");
        }
        UpdateTweakButtons();
    }

    private async void OnTweakSearchModePrepare(object sender, RoutedEventArgs e)
    {
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        if (tweaks is null || searchModes is null || tweaksBusy) return;
        if (TweakSearchModeCombo.SelectedItem is not TaskbarSearchMode mode) return;
        tweaksBusy = true;
        tweakSimulation = null;
        TweakConfirmationText.Clear();
        UpdateTweakButtons();
        try
        {
            var simulation = await searchModes.PrepareAsync(mode.Mode);
            ShowTweakSimulation(simulation,
                Loc.F($"Modo {mode.Mode} — {mode.DisplayName}: plano simulado sem efeito. ") +
                Loc.T("A seleção do controle não alterou o Windows."));
        }
        catch (Exception error)
        {
            ShowFailure(Loc.T("Não foi possível preparar o modo de pesquisa"), error);
            TweakSearchModeStatus.Text = Loc.T("Plano indisponível; nenhuma alteração foi executada.");
        }
        finally
        {
            tweaksBusy = false;
            UpdateTweakButtons();
        }
    }

    private async void OnTweakDetails(object sender, RoutedEventArgs e)
    {
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        if (tweaks is null || tweaksBusy) return;
        if (sender is not FrameworkElement { DataContext: TweakCardRow { OperationId: var operationId } }) return;
        tweaksBusy = true;
        tweakSimulation = null;
        TweakConfirmationText.Clear();
        UpdateTweakButtons();
        try
        {
            var simulation = await tweaks.SimulateAsync(operationId);
            ShowTweakSimulation(simulation);
        }
        catch (Exception error)
        {
            ShowFailure(Loc.T("Não foi possível simular o tweak"), error);
            TweakStatusText.Text = Loc.T("Simulação indisponível; nenhuma escrita foi executada.");
        }
        finally
        {
            tweaksBusy = false;
            UpdateTweakButtons();
        }
    }

    private void ShowTweakSimulation(SimulationResult simulation, string? status = null)
    {
        tweakSimulation = simulation;
        tweakPreview = null;
        TweakDetailsTitle.Text = simulation.Operation.DisplayName;
        TweakDetailsMeta.Text = Loc.F($"Operação {simulation.Operation.Id} v{simulation.Operation.Version} · ") +
            Loc.F($"risco {UiText.Risk(simulation.Operation.Risk)} · {UiText.Scope(simulation.Operation.Scope)}");
        TweakDetailsText.Text = BuildTweakPlanText(simulation);
        TweakConfirmationText.Text = string.Empty;
        TweakConfirmationHint.Text = simulation.CanExecute && tweaks is { RealExecutionEnabled: true }
            ? Loc.T("Digite exatamente: ") + TweakManager.BuildConfirmationText(simulation.Plan)
            : Loc.T("Plano indisponível para autorização: ") + string.Join(", ", simulation.Blockers);
        TweakStatusText.Text = status ?? (simulation.CanExecute
            ? Loc.T("Plano simulado sem efeito. A autorização abaixo é vinculada a este plano e ao estado observado.")
            : Loc.T("Plano bloqueado no backend; nenhuma escrita será aceita."));
    }

    private async void OnTweakAuthorize(object sender, RoutedEventArgs e)
    {
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        if (tweaks is null || tweaksBusy || tweakSimulation is null) return;
        var plan = tweakSimulation.Plan;
        var expected = TweakManager.BuildConfirmationText(plan);
        if (!tweaks.RealExecutionEnabled)
        {
            TweakStatusText.Text = Loc.T("Execução real desabilitada nesta distribuição; validação real pendente.");
            return;
        }
        var typedConfirmation = TweakConfirmationText.Text.Trim();
        if (!string.Equals(typedConfirmation, expected, StringComparison.Ordinal))
        {
            TweakStatusText.Text = Loc.T("A confirmação digitada não corresponde à operação e ao plano apresentado.");
            return;
        }

        tweaksBusy = true;
        UpdateTweakButtons();
        TweakStatusText.Text = Loc.T("Preflight, backup e journal em andamento. Aguarde a verificação posterior…");
        try
        {
            var token = tweaks.Authorize(tweakSimulation, typedConfirmation);
            var result = await tweaks.ExecuteAsync(plan, token);
            var blockers = result.Blockers.Count == 0
                ? string.Empty
                : " Bloqueios: " + string.Join(", ", result.Blockers);
            var status = result.Summary + blockers;
            await ReloadTweaksAsync(result.Transaction?.TransactionId, status, ignoreBusy: true);
            TweakStatusText.Text = status;
        }
        catch (Exception error)
        {
            ShowFailure(Loc.T("A execução controlada não foi concluída"), error);
            TweakStatusText.Text = Loc.T("Falha na execução; consulte o histórico e limitações. Sucesso não é presumido.");
        }
        finally
        {
            RecoverySection.InvalidateAuditHistory();
            tweaksBusy = false;
            UpdateTweakButtons();
        }
    }

    private void OnTweakHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (TweakRecoveryText is null) return;
        tweakSelectedTransaction = (TweakHistoryList.SelectedItem as TweakHistoryRow)?.Transaction;
        tweakPreview = null;
        TweakRecoveryText.Text = tweakSelectedTransaction is { } transaction
            ? BuildTransactionText(transaction) : Loc.T("Selecione uma transação do histórico.");
        TweakRecoveryConfirmationText.Text = string.Empty;
        TweakRecoveryHint.Text = Loc.T("Analise a recuperação antes de confirmar.");
        TweakRecoveryStatusText.Text = Loc.T("Recuperação ainda não analisada.");
        UpdateTweakButtons();
    }

    private async void OnTweakPreviewRecovery(object sender, RoutedEventArgs e)
    {
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        if (tweaks is null || tweaksBusy || tweakSelectedTransaction is null) return;
        var transactionId = tweakSelectedTransaction.TransactionId;
        tweaksBusy = true;
        tweakPreview = null;
        TweakRecoveryConfirmationText.Clear();
        UpdateTweakButtons();
        try
        {
            var preview = await tweaks.PreviewRecoveryAsync(transactionId);
            tweakPreview = preview;
            TweakRecoveryText.Text = BuildRecoveryText(tweakSelectedTransaction, preview);
            TweakRecoveryConfirmationText.Text = string.Empty;
            TweakRecoveryHint.Text = preview.CanRecover
                ? Loc.T("Digite exatamente: ") + TweakManager.BuildRecoveryConfirmationText(transactionId)
                : Loc.T("Recuperação bloqueada; conflitos não são sobrescritos.");
            TweakRecoveryStatusText.Text = preview.Reason ?? UiText.Outcome(preview.Outcome);
        }
        catch (Exception error)
        {
            ShowFailure(Loc.T("Não foi possível analisar a recuperação"), error);
            TweakRecoveryStatusText.Text = Loc.T("Análise indisponível; nenhuma escrita foi executada.");
        }
        finally
        {
            tweaksBusy = false;
            UpdateTweakButtons();
        }
    }

    private async void OnTweakRollback(object sender, RoutedEventArgs e)
    {
        using var productLease = TryBeginProductOperation();
        if (productLease is null) return;
        if (tweaks is null || tweaksBusy || tweakSelectedTransaction is null || tweakPreview is null) return;
        var transactionId = tweakSelectedTransaction.TransactionId;
        var expected = TweakManager.BuildRecoveryConfirmationText(transactionId);
        if (!tweaks.RealExecutionEnabled)
        {
            TweakRecoveryStatusText.Text = Loc.T("Rollback real desabilitado nesta distribuição; a análise permanece somente leitura.");
            return;
        }
        if (!tweakPreview.CanRecover)
        {
            TweakRecoveryStatusText.Text = Loc.T("Recuperação não permitida: ") + (tweakPreview.Reason ?? Loc.T("conflito."));
            return;
        }
        var typedConfirmation = TweakRecoveryConfirmationText.Text.Trim();
        if (!string.Equals(typedConfirmation, expected, StringComparison.Ordinal))
        {
            TweakRecoveryStatusText.Text = Loc.T("A confirmação de recuperação não corresponde à transação selecionada.");
            return;
        }

        tweaksBusy = true;
        UpdateTweakButtons();
        TweakRecoveryStatusText.Text = Loc.T("Recuperação em andamento. O estado atual é reconferido antes de cada escrita…");
        try
        {
            var result = await tweaks.RollbackAsync(transactionId, new RecoveryOptions(typedConfirmation) { Preview = tweakPreview });
            var status = $"Recuperação: {UiText.Outcome(result.Outcome)} · integral: {(result.FullyRecovered ? "sim" : "não")}" +
                (result.Error is null ? string.Empty : " · " + result.Error);
            await ReloadTweaksAsync(transactionId, status, ignoreBusy: true);
            TweakRecoveryStatusText.Text = status;
        }
        catch (Exception error)
        {
            ShowFailure(Loc.T("A recuperação não foi concluída"), error);
            TweakRecoveryStatusText.Text = Loc.T("Falha de recuperação; o resultado parcial deve ser consultado no histórico.");
        }
        finally
        {
            RecoverySection.InvalidateAuditHistory();
            tweaksBusy = false;
            UpdateTweakButtons();
        }
    }

    private void OnTweakConfirmationChanged(object sender, TextChangedEventArgs e) => UpdateTweakButtons();

    private void OnTweakRecoveryConfirmationChanged(object sender, TextChangedEventArgs e) =>
        UpdateTweakButtons();

    private void UpdateTweakButtons()
    {
        if (TweakAuthorizeButton is null) return;
        var idle = !operations.IsBusy && !tweaksBusy;
        TweaksCardsHost.IsEnabled = idle;
        TweaksRefreshButton.IsEnabled = idle && tweaks is not null;
        TweakHistoryList.IsEnabled = idle;
        var authorizeText = tweakSimulation is null
            ? string.Empty
            : TweakManager.BuildConfirmationText(tweakSimulation.Plan);
        TweakAuthorizeButton.IsEnabled = idle && tweaks is { RealExecutionEnabled: true } &&
            tweakSimulation is { CanExecute: true } &&
            string.Equals(TweakConfirmationText.Text.Trim(), authorizeText, StringComparison.Ordinal);
        TweakPreviewRecoveryButton.IsEnabled = idle && tweakSelectedTransaction is not null;
        var recoveryText = tweakSelectedTransaction is null
            ? string.Empty
            : TweakManager.BuildRecoveryConfirmationText(tweakSelectedTransaction.TransactionId);
        TweakRollbackButton.IsEnabled = idle && tweaks is { RealExecutionEnabled: true } &&
            tweakPreview is { CanRecover: true } &&
            (tweakPreview.Outcome != RecoveryOutcome.AlreadyRestored || tweakSelectedTransaction?.IsIndeterminate == true) &&
            string.Equals(TweakRecoveryConfirmationText.Text.Trim(), recoveryText, StringComparison.Ordinal);
        if (TweakSearchModePrepareButton is not null)
            TweakSearchModePrepareButton.IsEnabled = idle && searchModes is not null &&
                TweakSearchModeCombo?.SelectedItem is TaskbarSearchMode;
    }

    private static string BuildTweakPlanText(SimulationResult simulation)
    {
        var operation = simulation.Operation;
        var plan = simulation.Plan;
        var text = new StringBuilder();
        text.AppendLine(operation.DisplayName);
        text.AppendLine(Loc.F($"Operação: {operation.Id} v{operation.Version} · risco {UiText.Risk(operation.Risk)} · {UiText.Scope(operation.Scope)}"));
        text.AppendLine(Loc.F($"Hash do plano: {plan.PlanHash[..Math.Min(16, plan.PlanHash.Length)]}…"));
        text.AppendLine(Loc.F($"Estado observado: {plan.CreatedAtUtc:O}"));
        text.AppendLine(Loc.F($"Compatibilidade: Windows 11 a partir do build {operation.Compatibility.MinimumBuild}") +
            (operation.Compatibility.MaximumBuild is { } maximum ? Loc.F($" até {maximum}.") : "."));
        if (operation.Dependencies.Count > 0)
            text.AppendLine(Loc.T("Dependências: ") + string.Join(", ", operation.Dependencies));
        text.AppendLine();
        text.AppendLine(Loc.T("Configuração"));
        foreach (var change in plan.Changes)
        {
            text.AppendLine(Loc.F($"• {change.Target.Identity}"));
            text.AppendLine(Loc.F($"  atual: {VerificationEngine.Describe(change.Before)}"));
            text.AppendLine(Loc.F($"  pretendido: {VerificationEngine.Describe(change.After)}"));
        }
        text.AppendLine();
        text.AppendLine(Loc.T("Consequências"));
        foreach (var consequence in operation.Consequences) text.AppendLine(Loc.F($"• {consequence}"));
        text.AppendLine();
        text.AppendLine(Loc.T("Atualização visual e reinício"));
        text.AppendLine(operation.RequiresExplorerRestart
            ? Loc.T("• A interface pode exigir reabrir ou atualizar o Explorador; o aplicativo não reinicia processos.")
            : Loc.T("• Nenhum reinício do Explorador é necessário para registrar a intenção."));
        text.AppendLine(operation.RequiresSystemRestart
            ? Loc.T("• Reinício do sistema indicado pela definição.")
            : Loc.T("• Nenhum reinício do sistema é executado."));
        text.AppendLine();
        text.AppendLine(Loc.T("Proteções"));
        foreach (var protection in simulation.Protections) text.AppendLine(Loc.F($"• {protection}"));
        text.AppendLine();
        text.AppendLine(Loc.T("Reversibilidade"));
        text.AppendLine(operation.Protection.RequiresFullTarget
            ? Loc.T("• Backup de chave inteira exigido; indisponível nesta fase.")
            : Loc.T("• Rollback restaura o estado observado, inclusive valor ausente, e detecta conflito posterior."));
        text.AppendLine();
        text.AppendLine(Loc.T("Limitações"));
        foreach (var limitation in simulation.Limitations) text.AppendLine(Loc.F($"• {limitation}"));
        if (operation.KnownRisks.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Riscos conhecidos"));
            foreach (var risk in operation.KnownRisks) text.AppendLine(Loc.F($"• {risk}"));
        }
        if (!simulation.CanExecute)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Bloqueios"));
            foreach (var blocker in simulation.Blockers) text.AppendLine(Loc.F($"• {blocker}"));
        }
        return text.ToString();
    }

    private static string BuildTransactionText(TransactionRecord transaction)
    {
        var text = new StringBuilder();
        text.AppendLine(Loc.F($"Transação: {transaction.TransactionId:D}"));
        text.AppendLine(Loc.F($"Operação: {transaction.OperationId} v{transaction.OperationVersion}"));
        text.AppendLine(Loc.F($"Estado: {UiText.State(transaction.State)} · sucesso verificado: {(transaction.Succeeded ? "sim" : "não")}") +
            Loc.F($" · indeterminado: {(transaction.IsIndeterminate ? "sim" : "não")}"));
        text.AppendLine(Loc.F($"Criado: {transaction.CreatedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz}"));
        text.AppendLine(Loc.F($"Atualizado: {transaction.UpdatedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz}"));
        if (transaction.Plan is { } plan)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Estado observado e pretendido"));
            foreach (var change in plan.Changes)
            {
                text.AppendLine(Loc.F($"• {change.Target.Identity}"));
                text.AppendLine(Loc.F($"  antes: {VerificationEngine.Describe(change.Before)}"));
                text.AppendLine(Loc.F($"  depois: {VerificationEngine.Describe(change.After)}"));
            }
        }
        text.AppendLine();
        text.AppendLine(Loc.F($"Backup: {(transaction.Backup is null ? "indisponível" : transaction.Backup.ContentHash[..16] + "…")}"));
        text.AppendLine($"Verificação: {(transaction.Verification is null ? "não executada" :
            transaction.Verification.Passed ? Loc.T("aprovada") : Loc.T("divergente"))}");
        text.AppendLine(Loc.F($"Recuperação: {(transaction.Recovery is null ? "não tentada" : UiText.Outcome(transaction.Recovery.Outcome))}"));
        if (transaction.Limitations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Limitações"));
            foreach (var limitation in transaction.Limitations) text.AppendLine(Loc.F($"• {limitation}"));
        }
        return text.ToString();
    }

    private static string BuildRecoveryText(TransactionRecord transaction, RecoveryPreview preview)
    {
        var text = new StringBuilder(BuildTransactionText(transaction));
        text.AppendLine();
        text.AppendLine(Loc.T("Prévia de recuperação"));
        text.AppendLine(Loc.F($"Resultado: {UiText.Outcome(preview.Outcome)} · recuperável: {(preview.CanRecover ? "sim" : "não")}"));
        if (!string.IsNullOrWhiteSpace(preview.Reason)) text.AppendLine(Loc.F($"Motivo: {preview.Reason}"));
        if (preview.Conflicts.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Conflitos (não sobrescritos)"));
            foreach (var conflict in preview.Conflicts)
                text.AppendLine(Loc.F($"• {conflict.TargetIdentity}: {UiText.Conflict(conflict.Kind)} — {conflict.Description}"));
        }
        if (preview.CurrentStates.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Estado atual observado"));
            foreach (var state in preview.CurrentStates)
                text.AppendLine(Loc.F($"• {state.TargetIdentity}: ") +
                    Loc.F($"{(state.Verified ? "no estado observado" : state.Error ?? string.Join("; ", state.Effects))}"));
        }
        text.AppendLine();
        text.AppendLine(Loc.T("A recuperação restaura somente o estado observado. Um estado alterado por terceiro ") +
            Loc.T("permanece como conflito e não é sobrescrito silenciosamente."));
        return text.ToString();
    }

    private sealed record TweakCategoryRow(string Category, IReadOnlyList<TweakCardRow> Cards);

    private sealed record TweakCardRow(TweakOperationView View)
    {
        public string OperationId => View.OperationId;
        public string DisplayName => View.DisplayName;
        public string Category => View.Category;
        public string Description => View.Description;
        public string CurrentSummary => "Estado atual: " + UiText.Observed(View.CurrentState);
        public string DesiredSummary => "Estado pretendido: " + UiText.Observed(View.DesiredState);
        public string RiskSummary => "Risco: " + UiText.Risk(View.Risk);
        public string Compatibility => View.Compatibility;
        public string ProtectionsSummary => "Proteções: " + string.Join(" · ", View.Protections);
        public string RecoverySummary => "Recuperação: estado observado, com detecção de conflito.";
        public string LimitationsSummary => View.Limitations.Count == 0
            ? string.Empty
            : "Limitações: " + string.Join(" · ", View.Limitations);
        public string Availability => View.Availability + (View.LoadError is null ? "" : " Leitura: " + View.LoadError);
    }

    private sealed record TweakHistoryRow(TransactionRecord Transaction, string OperationName)
    {
        public string DateText => Transaction.UpdatedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
        public string StateText => $"{UiText.State(Transaction.State)} · verificado: " +
            $"{(Transaction.Succeeded ? "sim" : "não")} · recuperação: " +
            $"{(Transaction.Recovery is null ? "não tentada" : UiText.Outcome(Transaction.Recovery.Outcome))}";
    }
}
