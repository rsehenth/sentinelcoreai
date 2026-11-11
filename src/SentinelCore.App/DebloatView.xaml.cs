using System.Text;
using System.Windows;
using System.Windows.Controls;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.App.Localization;

namespace SentinelCore.App;

/// <summary>Native inventory and preview, isolated from the executable synthetic flow.</summary>
public partial class DebloatView : UserControl
{
    private readonly LocalizedMessages debloatMessages = new();

    private DebloatManager? manager;
    private DebloatInventoryObserver? nativeObserver;
    private DebloatInventoryObservation? nativeObservation;
    private DebloatNativeRemovalPreview? nativePreview;
    private bool reinstallFromHistory;
    private IReadOnlyList<NativeRow> nativeRows = [];
    private bool nativeBusy;
    private IReadOnlyList<InventoryRow> inventory = [];
    private DebloatPreparedPlan? preparedPlan;
    private CancellationTokenSource? cancellation;
    private bool busy;
    private bool loaded;
    private bool updating;
    private bool initialized;
    private bool operationBlocked;
    private readonly ProductOperationCoordinator standaloneOperations = new();
    internal Func<IDisposable?>? BeginOperation { get; set; }
    internal event Action? HistoryChanged;

    internal void SetOperationBlocked(bool blocked)
    {
        operationBlocked = blocked;
        UpdateButtons();
    }

    public DebloatView()
    {
        InitializeComponent();
        updating = true;
        FilterCombo.DisplayMemberPath = "Label";
        FilterCombo.ItemsSource = new[]
        {
            "Todos", "Opcionais", "Baixo", "Médio", "Alto", "Não validados",
            "Presentes no inventário", "Ausentes no inventário", "Não verificados",
            "Protegidos", "Microsoft", "Terceiros/OEM"
        }.Select(key => new LocalizedChoice<string>(key, key)).ToArray();
        FilterCombo.SelectedIndex = 0;
        ScenarioCombo.ItemsSource = Enum.GetValues<DebloatScenario>()
            .Select(value => new LocalizedChoice<DebloatScenario>(value, () => ScenarioLabel(value))).ToArray();
        ScenarioCombo.SelectedIndex = 0;
        NativeCategory.DisplayMemberPath = NativeRisk.DisplayMemberPath = NativeScope.DisplayMemberPath = "Label";
        NativeCategory.ItemsSource = new[] { new LocalizedChoice<DebloatNativeCategory?>(null, "Todas") }
            .Concat(Enum.GetValues<DebloatNativeCategory>().Select(value => new LocalizedChoice<DebloatNativeCategory?>(value, DebloatPresentation.Category(value)))).ToArray();
        NativeRisk.ItemsSource = new[] { new LocalizedChoice<RiskLevel?>(null, "Todos") }
            .Concat(Enum.GetValues<RiskLevel>().Select(value => new LocalizedChoice<RiskLevel?>(value, () => RiskLabel(value)))).ToArray();
        NativeScope.ItemsSource = new[] { new LocalizedChoice<DebloatScope?>(null, "Todos") }
            .Concat(new[] { DebloatScope.CurrentUser, DebloatScope.OtherUser, DebloatScope.Provisioned }
                .Select(value => new LocalizedChoice<DebloatScope?>(value, DebloatPresentation.Scope(value)))).ToArray();
        NativeCategory.SelectedIndex = NativeRisk.SelectedIndex = NativeScope.SelectedIndex = 0;
        updating = false;
        initialized = true;
        UpdateButtons();
    }

    internal void RefreshLanguage()
    {
        updating = true;
        try
        {
            foreach (var combo in new[] { FilterCombo, ScenarioCombo, NativeCategory, NativeRisk, NativeScope })
                foreach (var choice in combo.Items.OfType<ILocalizedChoice>()) choice.RefreshLanguage();
            InventoryGrid.Items.Refresh();
            HistoryList.Items.Refresh();
            NativeGrid.Items.Refresh();
            ShowNativeSelection(preservePreview: true);
            if (nativePreview is { } preview) ShowNativePreview(preview, bringIntoView: false);
            ShowSelection(preservePlan: true);
            if (preparedPlan is { } plan) ShowPlan(plan);
            ShowHistorySelection(updateReinstallation: reinstallFromHistory);
            if (nativeObserver is null) debloatMessages.Set(nameof(NativeInventoryStatus), () => NativeInventoryStatus.Text = Loc.T("Leitor nativo indisponível nesta composição."));
            debloatMessages.Refresh();
        }
        finally { updating = false; }
        UpdateButtons();
    }

    public void Initialize(DebloatManager debloatManager, DebloatInventoryObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(debloatManager);
        if (manager is not null) throw new InvalidOperationException("Debloat já foi composto.");
        manager = debloatManager;
        nativeObserver = observer;
        if (observer is null) debloatMessages.Set(nameof(NativeInventoryStatus), () => NativeInventoryStatus.Text = Loc.T("Leitor nativo indisponível nesta composição."));
        UpdateButtons();
        if (IsLoaded) _ = RefreshAsync();
    }

    public bool IsBusy => busy;
    public event EventHandler? BecameIdle;
    public event Action? OpenRecoveryRequested;

    private void OnOpenRecovery(object sender, RoutedEventArgs e) => OpenRecoveryRequested?.Invoke();

    public void Cancel() => cancellation?.Cancel();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!loaded && manager is not null && !busy) await RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Cancel();

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnNativeRefresh(object sender, RoutedEventArgs e)
    {
        if (nativeObserver is null || busy) return;
        using var productLease = BeginOperation is null ? standaloneOperations.Acquire() : BeginOperation();
        if (productLease is null) return;
        nativeBusy = true;
        BeginWork("Consultando metadados do Windows…");
        nativeObservation = null;
        nativeRows = [];
        ApplyNativeFilter();
        debloatMessages.Set(nameof(NativeSummary), () => NativeSummary.Text = Loc.T("Leitura em andamento…"));
        NativeWarnings.Visibility = Visibility.Collapsed;
        var token = cancellation!.Token;
        try
        {
            var observation = await nativeObserver.ObserveAsync(token);
            token.ThrowIfCancellationRequested();
            nativeObservation = observation;
            nativeRows = observation.Assessments.Where(item => item.Package is not null)
                .Select(item => new NativeRow(item, item.Package!))
                .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var scopes = observation.Query.AdditionalScopes.Select(scope =>
                ScopeStatus(scope.Scope, scope.State, scope.Packages.Count));
            debloatMessages.Set(nameof(NativeInventoryStatus), () => NativeInventoryStatus.Text = string.Join(Environment.NewLine,
                scopes.Prepend(ScopeStatus(DebloatScope.CurrentUser, observation.Query.State, observation.ObservedCount))));
            NativeWarnings.Text = string.Join(Environment.NewLine, observation.Warnings.Take(20));
            NativeWarnings.Visibility = Visibility.Visible;
            ApplyNativeFilter();
        }
        catch (OperationCanceledException)
        {
            debloatMessages.Set(nameof(NativeInventoryStatus), () => NativeInventoryStatus.Text = Loc.T("Consulta interrompida. Nenhuma ausência foi confirmada."));
            ApplyNativeFilter();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            debloatMessages.Set(nameof(NativeInventoryStatus), () => NativeInventoryStatus.Text = Loc.F($"Consulta indisponível (0x{error.HResult:X8}). Nenhuma ausência foi confirmada."));
            ApplyNativeFilter();
        }
        finally
        {
            nativeBusy = false;
            EndWork();
        }
    }

    private static string ScopeStatus(DebloatScope scope, DebloatInventoryQueryState state, int count) =>
        $"{Loc.T(DebloatPresentation.Scope(scope))} · {Loc.T(DebloatPresentation.QueryState(state))}" +
        (count > 0 ? Loc.F($" {count} pacote(s) observado(s).") : "");

    private void OnNativeCancel(object sender, RoutedEventArgs e)
    {
        Cancel();
        NativeCancelButton.IsEnabled = false;
        debloatMessages.Set(nameof(NativeInventoryStatus), () => NativeInventoryStatus.Text = Loc.T("Aguardando o encerramento da consulta…"));
    }

    private void OnNativeFilter(object sender, RoutedEventArgs e)
    {
        if (initialized && !updating && !busy) ApplyNativeFilter();
    }

    private void ApplyNativeFilter()
    {
        var selected = NativeGrid.SelectedItem;
        var term = NativeSearch.Text.Trim();
        var rows = nativeRows.Where(row =>
            (NativeCategory.SelectedIndex == 0 || (NativeCategory.SelectedItem as LocalizedChoice<DebloatNativeCategory?>)?.Value == row.Assessment.Category) &&
            (NativeRisk.SelectedIndex == 0 || (NativeRisk.SelectedItem as LocalizedChoice<RiskLevel?>)?.Value == row.Assessment.EffectiveRisk) &&
            (NativeScope.SelectedIndex == 0 || (NativeScope.SelectedItem as LocalizedChoice<DebloatScope?>)?.Value == row.Package.Scope) &&
            (term.Length == 0 || row.Search.Contains(term, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        NativeGrid.ItemsSource = rows;
        if (selected is NativeRow row && rows.Contains(row)) NativeGrid.SelectedItem = selected;
        debloatMessages.Set(nameof(NativeSummary), () => NativeSummary.Text = nativeObservation is null ? Loc.T("Consulte o inventário para carregar a lista.")
            : rows.Length == 0 ? Loc.T("Nenhum pacote observado corresponde aos filtros. Confira a cobertura acima.")
            : Loc.F($"{rows.Length} de {nativeRows.Count} pacote(s) observado(s)."));
        ShowNativeSelection();
    }

    private void OnNativeSelection(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && !updating) ShowNativeSelection();
    }

    private void ShowNativeSelection(bool preservePreview = false)
    {
        if (!preservePreview) { nativePreview = null; NativePreview.Visibility = Visibility.Collapsed; }
        if (NativeGrid.SelectedItem is not NativeRow row)
        {
            NativeDetails.Text = Loc.T("Selecione um pacote para consultar identidade, dependências e bloqueios.");
        }
        else
        {
            var p = row.Package;
            NativeDetails.Text = Loc.F($"{row.Name}\nNome técnico: {Available(p.TechnicalName)}\nPFN: {Available(p.PackageFamilyName)}\n") +
                Loc.F($"Package Full Name: {Available(p.PackageFullName)}\nEditor: {Available(p.Publisher)}\n") +
                Loc.F($"Versão: {Available(p.Version)} · Arquitetura: {Available(p.Architecture)}\n") +
                Loc.F($"Tipo: {row.Kind} · Assinatura: {row.Signature} · Categoria: {row.Category}\n") +
                Loc.F($"Escopo: {row.Scope} · Estado: {row.InstallState} · Provisionado: {NullableLabel(p.Provisioned)}\n") +
                Loc.F($"Dependências informadas pelo Windows: {NativeDependencyLabel(p.DependencyFullNames, p.DependencyFullNames is not null)}\n") +
                Loc.F($"Risco T0: {row.Risk} · Remoção real bloqueada\n") + string.Join(Loc.T("\n"), row.Assessment.Blockers);
        }
        UpdateButtons();
    }

    private void OnNativePreview(object sender, RoutedEventArgs e)
    {
        if (busy || nativeObservation is null || NativeGrid.SelectedItem is not NativeRow row) return;
        nativePreview = DebloatRemovalPreflightEngine.PreviewNative(nativeObservation, row.Assessment);
        ShowNativePreview(nativePreview);
    }

    private void ShowNativePreview(DebloatNativeRemovalPreview plan, bool bringIntoView = true)
    {
        NativePreview.Text = Loc.F($"Prévia individual · {Loc.T(DebloatPresentation.Scope(plan.Package.Scope))} · {plan.ObservedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}\n") +
            Loc.F($"{Available(plan.Package.PackageFullName)}\nRisco T0: {RiskLabel(plan.Preflight.RealRisk)}\n") +
            Loc.F($"Dependências informadas: {NativeDependencyLabel(plan.Dependencies, plan.Dependencies is not null)}\n") +
            Loc.F($"Dependentes observados: {NativeDependencyLabel(plan.Dependents, plan.DependenciesComplete)}\n") +
            Loc.F($"Dependências compartilhadas observadas: {NativeDependencyLabel(plan.SharedDependencies, plan.DependenciesComplete)}\n") +
            Loc.F($"Impacto observado: {plan.PotentiallyAffected.Count} identidade(s), incluindo o alvo. ") +
            (plan.DependenciesComplete ? Loc.T("O Windows pode produzir efeitos indiretos.\n") : Loc.T("O impacto total permanece indeterminado.\n")) +
            NativeDependencyLabel(plan.PotentiallyAffected, false) + Loc.T("\n\n") +
            string.Join(Loc.T("\n"), plan.Consequences) + Loc.T("\n\nBloqueios\n") +
            string.Join(Loc.T("\n"), plan.Preflight.RealBlockers) + Loc.T("\n\n") + plan.Reinstallation;
        NativePreview.Visibility = Visibility.Visible;
        if (bringIntoView) NativePreview.BringIntoView();
    }

    private static string NativeDependencyLabel(IReadOnlyList<string>? names, bool complete) => names switch
    {
        { Count: > 0 } => string.Join(", ", names.Select(Available)),
        _ when complete => Loc.T("Nenhuma na consulta estática."),
        _ => Loc.T("Indisponível ou incompleto.")
    };

    private static string Available(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Loc.T("Indisponível");
        return new string(value.Where(c => !char.IsControl(c)).Take(DebloatNativeLimits.MaximumText).ToArray());
    }

    private async Task RefreshAsync()
    {
        if (manager is null || busy) return;
        using var productLease = BeginOperation is null ? standaloneOperations.Acquire() : BeginOperation();
        if (productLease is null) return;
        BeginWork("Lendo inventário sintético e histórico local…");
        InvalidatePlan();
        try
        {
            await LoadInventoryAsync(cancellation!.Token);
            await LoadHistoryAsync(cancellation.Token);
            loaded = true;
            debloatMessages.Set(nameof(StatusText), () => StatusText.Text = Loc.T("Inventário sintético atualizado. Atualizar não restaura fixtures já removidas nesta sessão."));
        }
        catch (OperationCanceledException)
        {
            debloatMessages.Set(nameof(StatusText), () => StatusText.Text = Loc.T("Leitura cancelada. Nenhum aplicativo real foi consultado ou removido."));
        }
        catch (Exception error)
        {
            debloatMessages.Set(nameof(StatusText), () => StatusText.Text = Loc.T("Não foi possível atualizar o inventário ou histórico: ") + error.Message);
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
        var snapshot = await manager!.LoadHistoryAsync(token, includeControlledRemoval: true);
        var records = snapshot.Items;
        var warnings = snapshot.Warnings;
        token.ThrowIfCancellationRequested();
        var rows = records.OrderByDescending(item => item.AtUtc).Select(item => new HistoryRow(item)).ToArray();
        HistoryList.ItemsSource = rows;
        HistoryList.SelectedItem = rows.FirstOrDefault(row => row.Item.Id == selectedId);
        debloatMessages.Set(nameof(HistorySummary), () => HistorySummary.Text = warnings.Count > 0
            ? Loc.F($"Histórico incompleto: {rows.Length} registro(s) válido(s) carregado(s). O total não foi determinado; examine os avisos.")
            : rows.Length == 0
                ? Loc.T("Nenhuma simulação persistida. Registros futuros aparecerão aqui após a execução.")
                : Loc.F($"{rows.Length} registro(s) sintético(s). Horários exibidos no fuso local."));
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
        var filter = (FilterCombo.SelectedItem as LocalizedChoice<string>)?.Value ?? "Todos";
        var rows = inventory.Where(row => DebloatFilters.Matches(row.Assessment, filter) &&
            (search.Length == 0 || row.Search.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        updating = true;
        InventoryGrid.ItemsSource = rows;
        InventoryGrid.SelectedItem = rows.FirstOrDefault(row => row.Assessment.Package.CatalogId == selectedId);
        updating = false;
        debloatMessages.Set(nameof(InventorySummary), () => InventorySummary.Text = rows.Length == 0
            ? Loc.T("Nenhum aplicativo corresponde à busca e ao filtro. Altere os critérios.")
            : Loc.F($"{rows.Length} de {inventory.Count} entrada(s). Todos os estados e identidades abaixo são sintéticos."));
        ShowSelection();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || updating || busy) return;
        ShowSelection();
    }

    private void ShowSelection(bool preservePlan = false)
    {
        if (!preservePlan)
        {
            reinstallFromHistory = false;
            InvalidatePlan();
            updating = true;
            MediumReviewCheck.IsChecked = false;
            updating = false;
        }
        if (InventoryGrid.SelectedItem is not InventoryRow row)
        {
            DetailsText.Text = Loc.T("Selecione um aplicativo no inventário sintético.");
            ReinstallText.Text = Loc.T("Selecione um aplicativo ou registro do histórico para consultar as informações disponíveis.");
            UpdateButtons();
            return;
        }
        var assessment = row.Assessment;
        var package = assessment.Package;
        var entry = assessment.CatalogEntry;
        var text = new StringBuilder();
        text.AppendLine(Loc.T("FIXTURE SINTÉTICA — não descreve os aplicativos reais deste computador."));
        text.AppendLine(Loc.F($"Aplicativo: {package.FriendlyName}\nID de catálogo: {package.CatalogId}"));
        text.AppendLine(Loc.F($"Nome técnico: {package.TechnicalName}\nFamília: {package.PackageFamilyName}"));
        text.AppendLine(Loc.F($"Identidade completa: {package.PackageFullName}\nEditor: {package.Publisher}"));
        text.AppendLine(Loc.F($"Versão: {package.Version}\nArquitetura: {package.Architecture}\nSID sintético: {package.UserId}"));
        text.AppendLine(Loc.F($"Tipo: {package.Kind}\nEscopo: {ScopeLabel(package.Scope)}"));
        text.AppendLine(Loc.F($"Presença: {Loc.T(DebloatPresentation.Presence(assessment.Presence))}"));
        text.AppendLine(Loc.F($"Origem do registro: {Loc.T(DebloatPresentation.Origin(assessment.Presence))}"));
        text.AppendLine(Loc.F($"Elegibilidade: {Loc.T(DebloatPresentation.Eligibility(assessment.Eligibility))}"));
        text.AppendLine(Loc.F($"Estado técnico do pacote: {InstallLabel(package.State)}"));
        text.AppendLine(Loc.F($"Provisionado: {NullableLabel(package.Provisioned)}\nProtegido: {YesNo(package.Protected)}\nPacote de sistema: {YesNo(package.SystemPackage)}"));
        text.AppendLine(Loc.F($"Risco preliminar documental: {(entry is null ? "Não catalogado" : RiskLabel(entry.PreliminaryRisk))}"));
        text.AppendLine(Loc.F($"Risco efetivo sintético: {RiskLabel(assessment.EffectiveRisk)}"));
        text.AppendLine(Loc.F($"Risco de execução real: {(entry is null ? "Não validado" : RiskLabel(entry.RealRisk))}; execução real indisponível."));
        text.AppendLine(Loc.F($"Dependências: {DependencyLabel(package.DependencyStatus)}"));
        AppendItems(text, package.Dependencies.Select(value => $"{value.PackageFamilyName}; crítica: {YesNo(value.Critical)}; exigida por outro pacote: {YesNo(value.RequiredByOtherPackage)}"));
        text.AppendLine(Loc.F($"Consequência documental: {entry?.Consequence ?? "Indisponível"}"));
        text.AppendLine(Loc.F($"Dados e preferências: {entry?.DataLossWarning ?? "Recuperação não garantida"}"));
        text.AppendLine(Loc.F($"Método documental: {entry?.RemovalMethod ?? "Indisponível"}; não executável no Windows nesta fase."));
        text.AppendLine(Loc.F($"Fonte do inventário: {package.Source}\nReferência: {entry?.UpstreamSource ?? "Indisponível"}\nCommit de referência: {entry?.UpstreamCommit ?? "Indisponível"}"));
        text.AppendLine(Loc.T("Bloqueios:"));
        AppendItems(text, assessment.Blockers);
        text.AppendLine(Loc.T("Limitações:"));
        AppendItems(text, package.Limitations.Concat(assessment.Limitations).Distinct());
        DetailsText.Text = text.ToString();
        ReinstallText.Text = entry?.ReinstallInformation ?? Loc.T("Informação documental de reinstalação indisponível.");
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
        RemovalPresentation.Present(DebloatRemovalPresentation.Unavailable());
        preparedPlan = null;
        ConfirmationText.Text = string.Empty;
        debloatMessages.Set(nameof(ConfirmationHint), () => ConfirmationHint.Text = Loc.T("A confirmação exata será exibida após preparar um plano elegível."));
        PlanText.Text = Loc.T("Prepare um plano após revisar os detalhes. Alterações de aplicativo, cenário ou revisão invalidam o plano.");
        if (initialized) UpdateButtons();
    }

    private async void OnPrepare(object sender, RoutedEventArgs e)
    {
        if (manager is null || busy || InventoryGrid.SelectedItem is not InventoryRow row ||
            ScenarioCombo.SelectedItem is not LocalizedChoice<DebloatScenario> scenario) return;
        var id = row.Assessment.Package.CatalogId;
        using var productLease = BeginOperation is null ? standaloneOperations.Acquire() : BeginOperation();
        if (productLease is null) return;
        var mediumReview = MediumReviewCheck.IsChecked == true;
        InvalidatePlan();
        BeginWork("Preparando plano sintético e verificando bloqueios…");
        try
        {
            var plan = await manager.PrepareAsync(id, scenario.Value, mediumReview, cancellation!.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            preparedPlan = plan;
            ShowPlan(plan);
            debloatMessages.Set(nameof(StatusText), () => StatusText.Text = plan.Simulation.CanExecute
                ? Loc.T("Plano preparado. Revise as consequências e digite a confirmação exata para autorizar somente esta simulação.")
                : Loc.T("Plano bloqueado. Consulte os motivos; autorização indisponível."));
        }
        catch (OperationCanceledException) { debloatMessages.Set(nameof(StatusText), () => StatusText.Text = Loc.T("Preparação cancelada. Prepare um novo plano para continuar.")); }
        catch (Exception error) { debloatMessages.Set(nameof(StatusText), () => StatusText.Text = Loc.T("Plano não preparado: ") + error.Message); }
        finally { EndWork(); }
    }

    private void ShowPlan(DebloatPreparedPlan plan)
    {
        RemovalPresentation.Present(DebloatRemovalPresentation.FromLegacySynthetic(plan));
        var simulation = plan.Simulation;
        var text = new StringBuilder();
        text.AppendLine(DebloatLimits.SyntheticWarning);
        text.AppendLine(Loc.F($"Aplicativo: {plan.Package.FriendlyName}\nIdentidade: {plan.Package.PackageFullName}\nSID sintético: {plan.Package.UserId}"));
        text.AppendLine(Loc.F($"Escopo: {ScopeLabel(plan.Package.Scope)}\nCenário: {ScenarioLabel(plan.Scenario)}"));
        text.AppendLine(Loc.F($"Risco efetivo sintético: {RiskLabel(plan.Risk)}\nRevisão individual de risco médio: {YesNo(plan.MediumReviewConfirmed)}"));
        text.AppendLine(Loc.F($"Plano: {plan.Id}\nHash: {plan.PlanHash}\nElegível: {YesNo(simulation.CanExecute)}"));
        text.AppendLine(Loc.F($"Resultado esperado da fixture: {simulation.ExpectedResult}"));
        text.AppendLine(Loc.T("Consequências:")); AppendItems(text, plan.Consequences.Concat(simulation.Consequences).Distinct());
        text.AppendLine(Loc.T("Riscos:")); AppendItems(text, simulation.Risks);
        text.AppendLine(Loc.T("Dependências:")); AppendItems(text, simulation.Dependencies);
        text.AppendLine(Loc.T("Proteções do fluxo sintético:")); AppendItems(text, simulation.Protections);
        text.AppendLine(Loc.T("Bloqueios:")); AppendItems(text, simulation.Blockers.Select(value => value.ToString()));
        text.AppendLine(Loc.T("Limitações:")); AppendItems(text, plan.Limitations.Concat(simulation.Limitations).Distinct());
        text.AppendLine(DebloatLimits.RecoveryWarning);
        PlanText.Text = text.ToString();
        debloatMessages.Set(nameof(ConfirmationHint), () => ConfirmationHint.Text = simulation.CanExecute ? Loc.T("Digite exatamente: ") + plan.Confirmation : Loc.T("Plano bloqueado; confirmação indisponível."));
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
        using var productLease = BeginOperation is null ? standaloneOperations.Acquire() : BeginOperation();
        if (productLease is null) return;
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
            debloatMessages.Set(nameof(StatusText), () => StatusText.Text = Loc.F($"SIMULAÇÃO: {Loc.T(state)}. {result.Summary}"));
            PlanText.Text += Environment.NewLine + Environment.NewLine + "Resultado sintético:" + Environment.NewLine +
                state + Environment.NewLine + result.Summary + Environment.NewLine +
                string.Join(Environment.NewLine, result.Transaction.Blockers);
        }
        catch (OperationCanceledException)
        {
            debloatMessages.Set(nameof(StatusText), () => StatusText.Text = Loc.T("Simulação cancelada. O histórico identifica estados persistidos; cancelamento não equivale a sucesso."));
        }
        catch (Exception error)
        {
            debloatMessages.Set(nameof(StatusText), () => StatusText.Text = Loc.T("Simulação não concluída: ") + error.Message + Loc.T(" Consulte o histórico e seus avisos antes de preparar outro plano."));
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
                HistoryWarnings.Text = Loc.T("Não foi possível reler os registros: ") + error.Message +
                    Loc.T(" Resultado persistido não confirmado. Use Atualizar inventário sintético.");
            }
            PlanText.Text = executionDetails;
            debloatMessages.Set(nameof(ConfirmationHint), () => ConfirmationHint.Text = Loc.T("A autorização foi encerrada. Prepare um novo plano para outra simulação."));
            HistoryChanged?.Invoke();
            EndWork();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Cancel();
        CancelButton.IsEnabled = false;
        debloatMessages.Set(nameof(StatusText), () => StatusText.Text = Loc.T("Cancelamento solicitado; aguardando o registro do estado sintético…"));
    }

    private void OnHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (updating) return;
        reinstallFromHistory = true;
        ShowHistorySelection();
    }

    private void ShowHistorySelection(bool updateReinstallation = true)
    {
        if (HistoryList.SelectedItem is not HistoryRow row)
        {
            HistoryDetails.Text = Loc.T("Selecione uma simulação para consultar identidade, estado e limitações persistidas.");
            return;
        }
        var item = row.Item;
        HistoryDetails.Text = Loc.F($"REGISTRO SINTÉTICO\nID: {item.Id}\nAplicativo: {item.Name}\nIdentidade: {item.PackageFullName}\n") +
            Loc.F($"Editor: {item.Publisher}\nVersão anterior: {item.PreviousVersion}\nSID sintético: {item.UserId}\n") +
            Loc.F($"Horário local: {item.AtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz}\nEstado: {StateLabel(item.State, item.Succeeded, item.Indeterminate)}\n") +
            Loc.F($"Estado técnico: {item.State}\nLimitações: {item.Limitations}\nRelatório local: {item.ReportPath}\n") + DebloatLimits.RecoveryWarning;
        if (item.Evidence is { } evidence) HistoryDetails.Text += "\n\n" + evidence;
        if (updateReinstallation) ReinstallText.Text = item.ReinstallInformation;
    }

    private void BeginWork(string status)
    {
        busy = true;
        cancellation = new CancellationTokenSource();
        if (nativeBusy) debloatMessages.Set(nameof(NativeInventoryStatus), () => NativeInventoryStatus.Text = Loc.T(status));
        else debloatMessages.Set(nameof(StatusText), () => StatusText.Text = Loc.T(status));
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
        RefreshButton.IsEnabled = manager is not null && !busy && !operationBlocked;
        NativeRefreshButton.IsEnabled = nativeObserver is not null && !busy && !operationBlocked;
        NativeCancelButton.IsEnabled = nativeBusy && cancellation?.IsCancellationRequested == false;
        NativePreviewButton.IsEnabled = !busy && !operationBlocked && NativeGrid.SelectedItem is NativeRow;
        NativeGrid.IsEnabled = NativeSearch.IsEnabled = NativeCategory.IsEnabled =
            NativeRisk.IsEnabled = NativeScope.IsEnabled = !busy && !operationBlocked;
        OpenRecoveryButton.IsEnabled = !busy && !operationBlocked && OpenRecoveryRequested is not null;
        SearchText.IsEnabled = !busy && !operationBlocked;
        FilterCombo.IsEnabled = !busy && !operationBlocked;
        InventoryGrid.IsEnabled = !busy && !operationBlocked;
        ScenarioCombo.IsEnabled = !busy && !operationBlocked && row is not null;
        MediumReviewCheck.IsEnabled = !busy && !operationBlocked && row?.Assessment.EffectiveRisk == RiskLevel.Medium;
        PrepareButton.IsEnabled = manager is not null && !busy && !operationBlocked && row?.Assessment.CanSimulate == true && mediumReady;
        ConfirmationText.IsEnabled = !busy && !operationBlocked && preparedPlan?.Simulation.CanExecute == true;
        ExecuteButton.IsEnabled = manager is not null && !busy && !operationBlocked && preparedPlan?.Simulation.CanExecute == true &&
            string.Equals(ConfirmationText.Text, preparedPlan.Confirmation, StringComparison.Ordinal);
        CancelButton.IsEnabled = busy && cancellation?.IsCancellationRequested == false;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void AppendItems(StringBuilder text, IEnumerable<string> values)
    {
        var items = values.ToArray();
        if (items.Length == 0) text.AppendLine(Loc.T("• Nenhum informado; ausência de evidência não comprova segurança."));
        else foreach (var value in items) text.AppendLine("• " + value);
    }

    private static string YesNo(bool value) => Loc.T(value ? "Sim" : "Não");
    private static string NullableLabel(bool? value) => value is null ? Loc.T("Indisponível") : YesNo(value.Value);
    private static string RiskLabel(RiskLevel value) => Loc.T(value switch
    {
        RiskLevel.Low => "Baixo", RiskLevel.Medium => "Médio", RiskLevel.High => "Alto", _ => "Não validado"
    });
    private static string InstallLabel(DebloatInstallState value) => Loc.T(value switch
    {
        DebloatInstallState.Installed => "Instalado (fixture)",
        DebloatInstallState.NotFound => "Ausente no inventário efetivamente consultado",
        DebloatInstallState.RemovedSynthetic => "Removido (simulação)",
        _ => "Não observado ou indeterminado"
    });
    private static string ScopeLabel(DebloatScope value) => Loc.T(value switch
    {
        DebloatScope.CurrentUser => "Usuário atual (fixture)", DebloatScope.OtherUser => "Outro usuário (bloqueado)",
        DebloatScope.Provisioned => "Provisionado (bloqueado)", DebloatScope.AllUsers => "Todos os usuários (bloqueado)", _ => "Indisponível"
    });
    private static string DependencyLabel(DebloatDependencyStatus value) => Loc.T(value switch
    {
        DebloatDependencyStatus.KnownNone => "Nenhuma na fixture", DebloatDependencyStatus.Known => "Detectadas na fixture",
        DebloatDependencyStatus.NotInvestigated => "Não investigadas", _ => "Desconhecidas"
    });
    private static string ScenarioLabel(DebloatScenario value) => Loc.T(value switch
    {
        DebloatScenario.Success => "Sucesso sintético", DebloatScenario.NotFound => "Aplicativo ausente (cenário sintético)",
        DebloatScenario.AccessDenied => "Acesso negado", DebloatScenario.Protected => "Aplicativo protegido",
        DebloatScenario.DependencyDetected => "Dependência detectada", DebloatScenario.IdentityMismatch => "Identidade divergente",
        DebloatScenario.UpdatedBeforeExecution => "Atualização antes da execução", DebloatScenario.InUse => "Aplicativo em uso",
        DebloatScenario.PartialFailure => "Falha parcial", DebloatScenario.Interrupted => "Execução interrompida",
        DebloatScenario.Cancelled => "Cancelamento sintético", DebloatScenario.Indeterminate => "Estado indeterminado",
        DebloatScenario.PersistenceUnavailable => "Persistência indisponível", DebloatScenario.Unverifiable => "Resultado não verificável",
        _ => "Cenário não reconhecido (bloqueado)"
    });
    private static string StateLabel(string state, bool succeeded, bool indeterminate)
    {
        if (indeterminate) return Loc.T("Indeterminado (simulação; sem sucesso confirmado)");
        const string syntheticPrefix = "SINTÉTICO — ";
        if (state.StartsWith(syntheticPrefix, StringComparison.Ordinal)) state = state[syntheticPrefix.Length..];
        return Loc.T(state switch
        {
            nameof(TransactionState.VerificationPassed) when succeeded => "Concluído e verificado (simulação)",
            nameof(TransactionState.Cancelled) => "Cancelado (simulação)",
            nameof(TransactionState.Interrupted) => "Interrompido (simulação)",
            nameof(TransactionState.Blocked) => "Bloqueado (simulação)",
            nameof(TransactionState.ExecutionFailed) => "Falha de execução (simulação)",
            nameof(TransactionState.VerificationFailed) => "Falha de verificação (simulação)",
            nameof(TransactionState.ExecutionIndeterminate) => "Indeterminado (simulação)",
            _ => Loc.F($"Sem sucesso confirmado (simulação): {state}")
        });
    }

    private sealed record HistoryRow(DebloatHistoryItem Item)
    {
        public string Label => $"{Item.AtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss} — {Item.Name} — {StateLabel(Item.State, Item.Succeeded, Item.Indeterminate)}";
    }
    private sealed record NativeRow(DebloatNativeAssessment Assessment, DebloatNativePackageMetadata Package)
    {
        public string Name => Available(string.IsNullOrWhiteSpace(Package.DisplayName) ? Package.TechnicalName : Package.DisplayName);
        public string Category => Loc.T(DebloatPresentation.Category(Assessment.Category));
        public string Scope => Loc.T(DebloatPresentation.Scope(Package.Scope));
        public string Risk => RiskLabel(Assessment.EffectiveRisk);
        public string Eligibility => Loc.T("Bloqueada");
        public string InstallState => Loc.T(Package.InstallState switch
        {
            DebloatNativeInstallState.Available => "Disponível",
            DebloatNativeInstallState.NotAvailable => "Indisponível no Windows",
            DebloatNativeInstallState.Degraded => "Com restrição informada pelo Windows",
            _ => "Estado de instalação indisponível"
        });
        public string Kind => Loc.T(Package.Kind switch
        {
            DebloatPackageKind.Framework => "Framework",
            DebloatPackageKind.Resource => "Recurso",
            _ when Package.IsBundle => "Bundle",
            _ when Package.IsOptional => "Pacote opcional",
            DebloatPackageKind.Appx => "Appx/MSIX principal",
            _ => "Tipo indisponível"
        });
        public string Signature => Loc.T(Package.Signature switch
        {
            DebloatNativeSignature.Store => "Microsoft Store",
            DebloatNativeSignature.System => "Sistema Windows",
            DebloatNativeSignature.Developer => "Desenvolvedor",
            DebloatNativeSignature.Enterprise => "Empresa",
            DebloatNativeSignature.Unsigned => "Sem assinatura",
            _ => "Indisponível"
        });
        public string Search => string.Join(" ", Name, Package.TechnicalName, Package.PackageFamilyName,
            Package.PackageFullName, Package.Publisher, Package.Version, Signature);
    }
    private sealed record InventoryRow(DebloatAssessment Assessment)
    {
        public string Name => Assessment.Package.FriendlyName;
        public string Publisher => Assessment.Package.Publisher;
        public string Version => Assessment.Package.Version;
        public string Category => Loc.T(Assessment.CatalogEntry?.Category ?? "Não catalogado");
        public string Origin => Loc.T(DebloatPresentation.Origin(Assessment.Presence));
        public string State => Loc.T(DebloatPresentation.Presence(Assessment.Presence));
        public string Risk => RiskLabel(Assessment.EffectiveRisk);
        public string Dependencies => DependencyLabel(Assessment.Package.DependencyStatus);
        public string Eligibility => Loc.T(DebloatPresentation.Eligibility(Assessment.Eligibility)) +
            (Assessment.CanSimulate ? string.Empty : " " + string.Join("; ", Assessment.Blockers));
        public string Search => string.Join(" ", Name, Publisher, Version, Category, Assessment.Package.CatalogId,
            Assessment.Package.TechnicalName, Assessment.Package.PackageFamilyName, Assessment.Package.PackageFullName,
            Origin, State);
    }
}
