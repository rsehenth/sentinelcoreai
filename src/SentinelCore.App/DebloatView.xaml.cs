using System.Text;
using System.Windows;
using System.Windows.Controls;
using SentinelCore.Application;
using SentinelCore.Domain;

namespace SentinelCore.App;

/// <summary>Presentation of the synthetic-only T3 flow. No Windows package or process access.</summary>
public partial class DebloatView : UserControl
{
    private DebloatManager? manager;
    private IReadOnlyList<InventoryRow> inventory = [];
    private DebloatPreparedPlan? preparedPlan;
    private CancellationTokenSource? cancellation;
    private bool busy;
    private bool loaded;
    private bool updating;
    private bool initialized;

    public DebloatView()
    {
        InitializeComponent();
        updating = true;
        FilterCombo.ItemsSource = new[]
        {
            "Todos", "Opcionais", "Baixo", "Médio", "Alto", "Não validados",
            "Presentes no inventário", "Ausentes no inventário", "Não verificados",
            "Protegidos", "Microsoft", "Terceiros/OEM"
        };
        FilterCombo.SelectedIndex = 0;
        ScenarioCombo.ItemsSource = Enum.GetValues<DebloatScenario>()
            .Select(value => new ScenarioRow(value, ScenarioLabel(value))).ToArray();
        ScenarioCombo.SelectedIndex = 0;
        updating = false;
        initialized = true;
        UpdateButtons();
    }

    public void Initialize(DebloatManager debloatManager)
    {
        ArgumentNullException.ThrowIfNull(debloatManager);
        if (manager is not null) throw new InvalidOperationException("Debloat já foi composto.");
        manager = debloatManager;
        UpdateButtons();
        if (IsLoaded) _ = RefreshAsync();
    }

    public bool IsBusy => busy;
    public event EventHandler? BecameIdle;

    public void Cancel() => cancellation?.Cancel();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!loaded && manager is not null && !busy) await RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Cancel();

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (manager is null || busy) return;
        BeginWork("Lendo inventário sintético e histórico local…");
        InvalidatePlan();
        try
        {
            await LoadInventoryAsync(cancellation!.Token);
            await LoadHistoryAsync(cancellation.Token);
            loaded = true;
            StatusText.Text = "Inventário sintético atualizado. Atualizar não restaura fixtures já removidas nesta sessão.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Leitura cancelada. Nenhum aplicativo real foi consultado ou removido.";
        }
        catch (Exception error)
        {
            StatusText.Text = "Não foi possível atualizar o inventário ou histórico: " + error.Message;
        }
        finally { EndWork(); }
    }

    private async Task LoadInventoryAsync(CancellationToken token)
    {
        var selectedId = (InventoryGrid.SelectedItem as InventoryRow)?.Assessment.Package.CatalogId;
        var assessments = await manager!.LoadAsync(token);
        token.ThrowIfCancellationRequested();
        inventory = assessments.Select(value => new InventoryRow(value)).ToArray();
        ApplyFilter(selectedId);
    }

    private async Task LoadHistoryAsync(CancellationToken token, Guid? selectedId = null)
    {
        selectedId ??= (HistoryList.SelectedItem as HistoryRow)?.Item.Id;
        var snapshot = await manager!.LoadHistoryAsync(token);
        var records = snapshot.Items;
        var warnings = snapshot.Warnings;
        token.ThrowIfCancellationRequested();
        var rows = records.OrderByDescending(item => item.AtUtc).Select(item => new HistoryRow(item)).ToArray();
        HistoryList.ItemsSource = rows;
        HistoryList.SelectedItem = rows.FirstOrDefault(row => row.Item.Id == selectedId);
        HistorySummary.Text = rows.Length == 0
            ? "Nenhuma simulação persistida. Registros futuros aparecerão aqui após a execução."
            : $"{rows.Length} registro(s) sintético(s). Horários exibidos no fuso local.";
        HistoryWarnings.Text = string.Join(Environment.NewLine, warnings);
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized || updating || busy) return;
        ApplyFilter((InventoryGrid.SelectedItem as InventoryRow)?.Assessment.Package.CatalogId);
    }

    private void ApplyFilter(string? selectedId)
    {
        var search = SearchText.Text.Trim();
        var filter = FilterCombo.SelectedItem as string ?? "Todos";
        var rows = inventory.Where(row => MatchesFilter(row, filter) &&
            (search.Length == 0 || row.Search.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        updating = true;
        InventoryGrid.ItemsSource = rows;
        InventoryGrid.SelectedItem = rows.FirstOrDefault(row => row.Assessment.Package.CatalogId == selectedId);
        updating = false;
        InventorySummary.Text = rows.Length == 0
            ? "Nenhum aplicativo corresponde à busca e ao filtro. Altere os critérios."
            : $"{rows.Length} de {inventory.Count} entrada(s). Todos os estados e identidades abaixo são sintéticos.";
        ShowSelection();
    }

    private static bool MatchesFilter(InventoryRow row, string filter)
    {
        var assessment = row.Assessment;
        var package = assessment.Package;
        return filter switch
        {
            "Opcionais" => assessment.CanSimulate,
            "Baixo" => assessment.EffectiveRisk == RiskLevel.Low,
            "Médio" => assessment.EffectiveRisk == RiskLevel.Medium,
            "Alto" => assessment.EffectiveRisk == RiskLevel.High,
            "Não validados" => assessment.EffectiveRisk == RiskLevel.NotValidated,
            "Presentes no inventário" => assessment.Presence == DebloatPresence.PresentInSyntheticInventory,
            "Ausentes no inventário" => assessment.Presence == DebloatPresence.AbsentInConsultedInventory,
            "Não verificados" => assessment.Presence == DebloatPresence.NotVerified,
            "Protegidos" => assessment.Eligibility == DebloatEligibility.ProtectedOrBlocked,
            "Microsoft" => IsMicrosoft(assessment.CatalogEntry?.TechnicalName ?? package.TechnicalName),
            "Terceiros/OEM" => !IsMicrosoft(assessment.CatalogEntry?.TechnicalName ?? package.TechnicalName),
            _ => true
        };
    }

    private static bool IsMicrosoft(string technicalName) =>
        technicalName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
        technicalName.StartsWith("MicrosoftCorporationII.", StringComparison.OrdinalIgnoreCase) ||
        technicalName.Equals("Clipchamp.Clipchamp", StringComparison.OrdinalIgnoreCase);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || updating || busy) return;
        ShowSelection();
    }

    private void ShowSelection()
    {
        InvalidatePlan();
        updating = true;
        MediumReviewCheck.IsChecked = false;
        updating = false;
        if (InventoryGrid.SelectedItem is not InventoryRow row)
        {
            DetailsText.Text = "Selecione um aplicativo no inventário sintético.";
            ReinstallText.Text = "Selecione um aplicativo ou registro do histórico para consultar as informações disponíveis.";
            UpdateButtons();
            return;
        }
        var assessment = row.Assessment;
        var package = assessment.Package;
        var entry = assessment.CatalogEntry;
        var text = new StringBuilder();
        text.AppendLine("FIXTURE SINTÉTICA — não descreve os aplicativos reais deste computador.");
        text.AppendLine($"Aplicativo: {package.FriendlyName}\nID de catálogo: {package.CatalogId}");
        text.AppendLine($"Nome técnico: {package.TechnicalName}\nFamília: {package.PackageFamilyName}");
        text.AppendLine($"Identidade completa: {package.PackageFullName}\nEditor: {package.Publisher}");
        text.AppendLine($"Versão: {package.Version}\nArquitetura: {package.Architecture}\nSID sintético: {package.UserId}");
        text.AppendLine($"Tipo: {package.Kind}\nEscopo: {ScopeLabel(package.Scope)}");
        text.AppendLine($"Presença: {DebloatPresentation.Presence(assessment.Presence)}");
        text.AppendLine($"Origem do registro: {DebloatPresentation.Origin(assessment.Presence)}");
        text.AppendLine($"Elegibilidade: {DebloatPresentation.Eligibility(assessment.Eligibility)}");
        text.AppendLine($"Estado técnico do pacote: {InstallLabel(package.State)}");
        text.AppendLine($"Provisionado: {NullableLabel(package.Provisioned)}\nProtegido: {YesNo(package.Protected)}\nPacote de sistema: {YesNo(package.SystemPackage)}");
        text.AppendLine($"Risco preliminar documental: {(entry is null ? "Não catalogado" : RiskLabel(entry.PreliminaryRisk))}");
        text.AppendLine($"Risco efetivo sintético: {RiskLabel(assessment.EffectiveRisk)}");
        text.AppendLine($"Risco de execução real: {(entry is null ? "Não validado" : RiskLabel(entry.RealRisk))}; execução real indisponível.");
        text.AppendLine($"Dependências: {DependencyLabel(package.DependencyStatus)}");
        AppendItems(text, package.Dependencies.Select(value => $"{value.PackageFamilyName}; crítica: {YesNo(value.Critical)}; exigida por outro pacote: {YesNo(value.RequiredByOtherPackage)}"));
        text.AppendLine($"Consequência documental: {entry?.Consequence ?? "Indisponível"}");
        text.AppendLine($"Dados e preferências: {entry?.DataLossWarning ?? "Recuperação não garantida"}");
        text.AppendLine($"Método documental: {entry?.RemovalMethod ?? "Indisponível"}; não executável no Windows nesta fase.");
        text.AppendLine($"Fonte do inventário: {package.Source}\nReferência: {entry?.UpstreamSource ?? "Indisponível"}\nCommit de referência: {entry?.UpstreamCommit ?? "Indisponível"}");
        text.AppendLine("Bloqueios:");
        AppendItems(text, assessment.Blockers);
        text.AppendLine("Limitações:");
        AppendItems(text, package.Limitations.Concat(assessment.Limitations).Distinct());
        DetailsText.Text = text.ToString();
        ReinstallText.Text = entry?.ReinstallInformation ?? "Informação documental de reinstalação indisponível.";
        UpdateButtons();
    }

    private void OnPlanInputChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized || updating || busy) return;
        InvalidatePlan();
        UpdateButtons();
    }

    private void InvalidatePlan()
    {
        preparedPlan = null;
        ConfirmationText.Text = string.Empty;
        ConfirmationHint.Text = "A confirmação exata será exibida após preparar um plano elegível.";
        PlanText.Text = "Prepare um plano após revisar os detalhes. Alterações de aplicativo, cenário ou revisão invalidam o plano.";
        if (initialized) UpdateButtons();
    }

    private async void OnPrepare(object sender, RoutedEventArgs e)
    {
        if (manager is null || busy || InventoryGrid.SelectedItem is not InventoryRow row ||
            ScenarioCombo.SelectedItem is not ScenarioRow scenario) return;
        var id = row.Assessment.Package.CatalogId;
        var mediumReview = MediumReviewCheck.IsChecked == true;
        InvalidatePlan();
        BeginWork("Preparando plano sintético e verificando bloqueios…");
        try
        {
            var plan = await manager.PrepareAsync(id, scenario.Value, mediumReview, cancellation!.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            preparedPlan = plan;
            ShowPlan(plan);
            StatusText.Text = plan.Simulation.CanExecute
                ? "Plano preparado. Revise as consequências e digite a confirmação exata para autorizar somente esta simulação."
                : "Plano bloqueado. Consulte os motivos; autorização indisponível.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Preparação cancelada. Prepare um novo plano para continuar."; }
        catch (Exception error) { StatusText.Text = "Plano não preparado: " + error.Message; }
        finally { EndWork(); }
    }

    private void ShowPlan(DebloatPreparedPlan plan)
    {
        var simulation = plan.Simulation;
        var text = new StringBuilder();
        text.AppendLine(DebloatLimits.SyntheticWarning);
        text.AppendLine($"Aplicativo: {plan.Package.FriendlyName}\nIdentidade: {plan.Package.PackageFullName}\nSID sintético: {plan.Package.UserId}");
        text.AppendLine($"Escopo: {ScopeLabel(plan.Package.Scope)}\nCenário: {ScenarioLabel(plan.Scenario)}");
        text.AppendLine($"Risco efetivo sintético: {RiskLabel(plan.Risk)}\nRevisão individual de risco médio: {YesNo(plan.MediumReviewConfirmed)}");
        text.AppendLine($"Plano: {plan.Id}\nHash: {plan.PlanHash}\nElegível: {YesNo(simulation.CanExecute)}");
        text.AppendLine($"Resultado esperado da fixture: {simulation.ExpectedResult}");
        text.AppendLine("Consequências:"); AppendItems(text, plan.Consequences.Concat(simulation.Consequences).Distinct());
        text.AppendLine("Riscos:"); AppendItems(text, simulation.Risks);
        text.AppendLine("Dependências:"); AppendItems(text, simulation.Dependencies);
        text.AppendLine("Proteções do fluxo sintético:"); AppendItems(text, simulation.Protections);
        text.AppendLine("Bloqueios:"); AppendItems(text, simulation.Blockers.Select(value => value.ToString()));
        text.AppendLine("Limitações:"); AppendItems(text, plan.Limitations.Concat(simulation.Limitations).Distinct());
        text.AppendLine(DebloatLimits.RecoveryWarning);
        PlanText.Text = text.ToString();
        ConfirmationHint.Text = simulation.CanExecute ? "Digite exatamente: " + plan.Confirmation : "Plano bloqueado; confirmação indisponível.";
    }

    private void OnConfirmationChanged(object sender, TextChangedEventArgs e)
    {
        if (initialized) UpdateButtons();
    }

    private async void OnExecute(object sender, RoutedEventArgs e)
    {
        if (manager is null || busy || preparedPlan is not { Simulation.CanExecute: true } plan ||
            !string.Equals(ConfirmationText.Text, plan.Confirmation, StringComparison.Ordinal)) return;
        var confirmation = ConfirmationText.Text;
        preparedPlan = null;
        ConfirmationText.Text = string.Empty;
        BeginWork("Executando somente a simulação autorizada…");
        Guid? historyId = null;
        try
        {
            var authorization = manager.Authorize(plan, confirmation);
            var result = await manager.ExecuteAsync(plan, authorization, DebloatExecutionMode.Synthetic, cancellation!.Token);
            historyId = result.Transaction.Transaction?.TransactionId;
            var transaction = result.Transaction.Transaction;
            var state = transaction is null ? "Resultado sem registro final" : StateLabel(transaction.State.ToString(), transaction.Succeeded, transaction.IsIndeterminate);
            StatusText.Text = $"SIMULAÇÃO: {state}. {result.Summary}";
            PlanText.Text += Environment.NewLine + Environment.NewLine + "Resultado sintético:" + Environment.NewLine +
                state + Environment.NewLine + result.Summary + Environment.NewLine +
                string.Join(Environment.NewLine, result.Transaction.Blockers);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Simulação cancelada. O histórico identifica estados persistidos; cancelamento não equivale a sucesso.";
        }
        catch (Exception error)
        {
            StatusText.Text = "Simulação não concluída: " + error.Message + " Consulte o histórico e seus avisos antes de preparar outro plano.";
        }
        finally
        {
            var executionDetails = PlanText.Text;
            try
            {
                // Reconcile persistence even after cancellation; never infer success from a caught exception.
                await LoadInventoryAsync(CancellationToken.None);
                await LoadHistoryAsync(CancellationToken.None, historyId);
            }
            catch (Exception error)
            {
                HistoryWarnings.Text = "Não foi possível reler os registros: " + error.Message +
                    " Resultado persistido não confirmado. Use Atualizar inventário sintético.";
            }
            PlanText.Text = executionDetails;
            ConfirmationHint.Text = "A autorização foi encerrada. Prepare um novo plano para outra simulação.";
            EndWork();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancelamento solicitado; aguardando o registro do estado sintético…";
    }

    private void OnHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row)
        {
            HistoryDetails.Text = "Selecione uma simulação para consultar identidade, estado e limitações persistidas.";
            return;
        }
        var item = row.Item;
        HistoryDetails.Text = $"REGISTRO SINTÉTICO\nID: {item.Id}\nAplicativo: {item.Name}\nIdentidade: {item.PackageFullName}\n" +
            $"Editor: {item.Publisher}\nVersão anterior: {item.PreviousVersion}\nSID sintético: {item.UserId}\n" +
            $"Horário local: {item.AtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz}\nEstado: {StateLabel(item.State, item.Succeeded, item.Indeterminate)}\n" +
            $"Estado técnico: {item.State}\nLimitações: {item.Limitations}\nRelatório local: {item.ReportPath}\n" + DebloatLimits.RecoveryWarning;
        ReinstallText.Text = item.ReinstallInformation;
    }

    private void BeginWork(string status)
    {
        busy = true;
        cancellation = new CancellationTokenSource();
        StatusText.Text = status;
        UpdateButtons();
    }

    private void EndWork()
    {
        cancellation?.Dispose();
        cancellation = null;
        busy = false;
        UpdateButtons();
        BecameIdle?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateButtons()
    {
        if (!initialized) return;
        var row = InventoryGrid.SelectedItem as InventoryRow;
        var mediumReady = row?.Assessment.EffectiveRisk != RiskLevel.Medium || MediumReviewCheck.IsChecked == true;
        RefreshButton.IsEnabled = manager is not null && !busy;
        SearchText.IsEnabled = !busy;
        FilterCombo.IsEnabled = !busy;
        InventoryGrid.IsEnabled = !busy;
        ScenarioCombo.IsEnabled = !busy && row is not null;
        MediumReviewCheck.IsEnabled = !busy && row?.Assessment.EffectiveRisk == RiskLevel.Medium;
        PrepareButton.IsEnabled = manager is not null && !busy && row?.Assessment.CanSimulate == true && mediumReady;
        ConfirmationText.IsEnabled = !busy && preparedPlan?.Simulation.CanExecute == true;
        ExecuteButton.IsEnabled = manager is not null && !busy && preparedPlan?.Simulation.CanExecute == true &&
            string.Equals(ConfirmationText.Text, preparedPlan.Confirmation, StringComparison.Ordinal);
        CancelButton.IsEnabled = busy && cancellation?.IsCancellationRequested == false;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void AppendItems(StringBuilder text, IEnumerable<string> values)
    {
        var items = values.ToArray();
        if (items.Length == 0) text.AppendLine("• Nenhum informado; ausência de evidência não comprova segurança.");
        else foreach (var value in items) text.AppendLine("• " + value);
    }

    private static string YesNo(bool value) => value ? "Sim" : "Não";
    private static string NullableLabel(bool? value) => value is null ? "Indisponível" : YesNo(value.Value);
    private static string RiskLabel(RiskLevel value) => value switch
    {
        RiskLevel.Low => "Baixo", RiskLevel.Medium => "Médio", RiskLevel.High => "Alto", _ => "Não validado"
    };
    private static string InstallLabel(DebloatInstallState value) => value switch
    {
        DebloatInstallState.Installed => "Instalado (fixture)",
        DebloatInstallState.NotFound => "Ausente no inventário efetivamente consultado",
        DebloatInstallState.RemovedSynthetic => "Removido (simulação)",
        _ => "Não observado ou indeterminado"
    };
    private static string ScopeLabel(DebloatScope value) => value switch
    {
        DebloatScope.CurrentUser => "Usuário atual (fixture)", DebloatScope.OtherUser => "Outro usuário (bloqueado)",
        DebloatScope.Provisioned => "Provisionado (bloqueado)", DebloatScope.AllUsers => "Todos os usuários (bloqueado)", _ => "Indisponível"
    };
    private static string DependencyLabel(DebloatDependencyStatus value) => value switch
    {
        DebloatDependencyStatus.KnownNone => "Nenhuma na fixture", DebloatDependencyStatus.Known => "Detectadas na fixture",
        DebloatDependencyStatus.NotInvestigated => "Não investigadas", _ => "Desconhecidas"
    };
    private static string ScenarioLabel(DebloatScenario value) => value switch
    {
        DebloatScenario.Success => "Sucesso sintético", DebloatScenario.NotFound => "Aplicativo ausente (cenário sintético)",
        DebloatScenario.AccessDenied => "Acesso negado", DebloatScenario.Protected => "Aplicativo protegido",
        DebloatScenario.DependencyDetected => "Dependência detectada", DebloatScenario.IdentityMismatch => "Identidade divergente",
        DebloatScenario.UpdatedBeforeExecution => "Atualização antes da execução", DebloatScenario.InUse => "Aplicativo em uso",
        DebloatScenario.PartialFailure => "Falha parcial", DebloatScenario.Interrupted => "Execução interrompida",
        DebloatScenario.Cancelled => "Cancelamento sintético", DebloatScenario.Indeterminate => "Estado indeterminado",
        DebloatScenario.PersistenceUnavailable => "Persistência indisponível", DebloatScenario.Unverifiable => "Resultado não verificável",
        _ => "Cenário não reconhecido (bloqueado)"
    };
    private static string StateLabel(string state, bool succeeded, bool indeterminate)
    {
        if (indeterminate) return "Indeterminado (simulação; sem sucesso confirmado)";
        const string syntheticPrefix = "SINTÉTICO — ";
        if (state.StartsWith(syntheticPrefix, StringComparison.Ordinal)) state = state[syntheticPrefix.Length..];
        return state switch
        {
            nameof(TransactionState.VerificationPassed) when succeeded => "Concluído e verificado (simulação)",
            nameof(TransactionState.Cancelled) => "Cancelado (simulação)",
            nameof(TransactionState.Interrupted) => "Interrompido (simulação)",
            nameof(TransactionState.Blocked) => "Bloqueado (simulação)",
            nameof(TransactionState.ExecutionFailed) => "Falha de execução (simulação)",
            nameof(TransactionState.VerificationFailed) => "Falha de verificação (simulação)",
            nameof(TransactionState.ExecutionIndeterminate) => "Indeterminado (simulação)",
            _ => "Sem sucesso confirmado (simulação): " + state
        };
    }

    private sealed record ScenarioRow(DebloatScenario Value, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record HistoryRow(DebloatHistoryItem Item)
    {
        public string Label => $"{Item.AtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss} — {Item.Name} — {StateLabel(Item.State, Item.Succeeded, Item.Indeterminate)}";
    }
    private sealed record InventoryRow(DebloatAssessment Assessment)
    {
        public string Name => Assessment.Package.FriendlyName;
        public string Publisher => Assessment.Package.Publisher;
        public string Version => Assessment.Package.Version;
        public string Category => Assessment.CatalogEntry?.Category ?? "Não catalogado";
        public string Origin => DebloatPresentation.Origin(Assessment.Presence);
        public string State => DebloatPresentation.Presence(Assessment.Presence);
        public string Risk => RiskLabel(Assessment.EffectiveRisk);
        public string Dependencies => DependencyLabel(Assessment.Package.DependencyStatus);
        public string Eligibility => DebloatPresentation.Eligibility(Assessment.Eligibility) +
            (Assessment.CanSimulate ? string.Empty : " " + string.Join("; ", Assessment.Blockers));
        public string Search => string.Join(" ", Name, Publisher, Version, Category, Assessment.Package.CatalogId,
            Assessment.Package.TechnicalName, Assessment.Package.PackageFamilyName, Assessment.Package.PackageFullName,
            Origin, State);
    }
}
