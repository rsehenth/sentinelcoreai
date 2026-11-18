using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.App.Localization;

namespace SentinelCore.App;

internal sealed record StartupRow(StartupEntry Entry, StartupEligibility Eligibility)
{
    public string Name => Entry.Name;
    public string Publisher => Entry.Publisher ?? Loc.T(AppsStartupText.DetPublisherUnknown);
    public string Source => Loc.T(AppsStartupText.Source(Entry.Source));
    public string Scope => Loc.T(AppsStartupText.Scope(Entry.Scope));
    public string State => Loc.T(AppsStartupText.State(Entry.State));
    public string Runtime => Loc.T(AppsStartupText.Runtime(Entry.Runtime));
    public string EligibilityText => Loc.T(AppsStartupText.Eligibility(Eligibility.Eligibility));
}

internal sealed record AppRow(InstalledApplicationEntry Entry, UninstallEligibility Eligibility)
{
    public string Name => Entry.Name;
    public string Publisher => Entry.Publisher ?? Loc.T(AppsStartupText.DetPublisherUnknown);
    public string Version => Entry.Version ?? "—";
    public string Kind => Loc.T(AppsStartupText.Kind(Entry.Kind));
    public string Scope => Loc.T(AppsStartupText.Scope(Entry.Scope));
    public string EligibilityText => Loc.T(AppsStartupText.Eligibility(Eligibility.Eligibility));
}

internal sealed record AppsHistoryRow(AppsStartupHistoryEntry Entry)
{
    public string Date => Entry.AtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string Action => Loc.T(AppsStartupText.HistoryAction(Entry.Kind, Entry.Action));
    public string Name => Entry.Name;
    public string Origin => Loc.T(Entry.Synthetic ? AppsStartupText.HistOriginDemo : AppsStartupText.HistOriginOperational);
    public string Result => Loc.T(AppsStartupText.Outcome(Entry.Outcome, Entry.Synthetic)) +
        (Entry.NativeCode is { } code ? $" ({code})" : "") +
        (Entry.RestartRequired ? " — " + Loc.T(AppsStartupText.RestartRequired) : "");
}

/// <summary>Aplicativos e Inicialização: inventário, prévia, confirmação e execução individual (hoje apenas sintética).</summary>
public partial class AppsStartupView : UserControl
{
    private AppsStartupManager? manager;
    private AppsStartupSnapshot snapshot = AppsStartupSnapshot.Empty;
    private StartupChangePreview? startupPreview;
    private UninstallPreview? uninstallPreview;
    private bool busy;
    private bool updating = true;
    private bool loaded;
    private bool failed;
    private readonly ProductOperationCoordinator standaloneOperations = new();
    internal Func<IDisposable?>? BeginOperation { get; set; }
    internal event Action? BecameIdle;

    public bool IsBusy => busy;

    public AppsStartupView()
    {
        InitializeComponent();
        StartupSourceCombo.DisplayMemberPath = StartupStateCombo.DisplayMemberPath =
            AppsKindCombo.DisplayMemberPath = AppsScopeCombo.DisplayMemberPath = "Label";
        StartupSourceCombo.ItemsSource = new[] { new LocalizedChoice<StartupSourceKind?>(null, AppsStartupText.FilterAll) }
            .Concat(Enum.GetValues<StartupSourceKind>().Select(value =>
                new LocalizedChoice<StartupSourceKind?>(value, AppsStartupText.Source(value)))).ToArray();
        StartupStateCombo.ItemsSource = new[] { new LocalizedChoice<StartupConfiguredState?>(null, AppsStartupText.FilterAll) }
            .Concat(Enum.GetValues<StartupConfiguredState>().Select(value =>
                new LocalizedChoice<StartupConfiguredState?>(value, AppsStartupText.State(value)))).ToArray();
        AppsKindCombo.ItemsSource = new[] { new LocalizedChoice<InstalledApplicationKind?>(null, AppsStartupText.FilterAll) }
            .Concat(Enum.GetValues<InstalledApplicationKind>().Select(value =>
                new LocalizedChoice<InstalledApplicationKind?>(value, AppsStartupText.Kind(value)))).ToArray();
        AppsScopeCombo.ItemsSource = new[] { new LocalizedChoice<AppsScope?>(null, AppsStartupText.FilterAll) }
            .Concat(Enum.GetValues<AppsScope>().Select(value =>
                new LocalizedChoice<AppsScope?>(value, AppsStartupText.Scope(value)))).ToArray();
        StartupSourceCombo.SelectedIndex = StartupStateCombo.SelectedIndex = AppsKindCombo.SelectedIndex = AppsScopeCombo.SelectedIndex = 0;
        updating = false;
        ApplyText();
        ApplyFilters();
    }

    /// <summary>Compõe o módulo; só pode ser chamado uma vez.</summary>
    public void Initialize(AppsStartupManager appsManager)
    {
        ArgumentNullException.ThrowIfNull(appsManager);
        if (manager is not null) throw new InvalidOperationException("Aplicativos e Inicialização já foi composto.");
        manager = appsManager;
        if (IsLoaded) _ = RefreshAsync();
    }

    internal void RefreshLanguage()
    {
        updating = true;
        try
        {
            foreach (var combo in new[] { StartupSourceCombo, StartupStateCombo, AppsKindCombo, AppsScopeCombo })
                foreach (var choice in combo.Items.OfType<ILocalizedChoice>()) choice.RefreshLanguage();
        }
        finally { updating = false; }
        ApplyText();
        ApplyFilters();
        if (startupPreview is { } sp) ShowStartupPreview(sp);
        if (uninstallPreview is { } up) ShowUninstallPreview(up);
        ShowStartupDetails();
        ShowAppDetails();
        ShowHistory();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!loaded && manager is not null && !busy) await RefreshAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (manager is null || busy) return;
        using var lease = BeginOperation is null ? standaloneOperations.Acquire() : BeginOperation();
        if (lease is null) return;
        SetBusy(true);
        StatusText.Text = Loc.T(AppsStartupText.Loading);
        try
        {
            snapshot = await manager.LoadAsync(CancellationToken.None);
            loaded = true; failed = false;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failed = true;
            snapshot = AppsStartupSnapshot.Empty;
        }
        finally { SetBusy(false); }
        ApplyFilters();
        ShowStartupDetails();
        ShowAppDetails();
        ShowHistory();
        ApplyText();
    }

    private void SetBusy(bool value)
    {
        busy = value;
        RefreshButton.IsEnabled = !value && manager is not null;
        InnerTabs.IsEnabled = !value;
        if (!value) BecameIdle?.Invoke();
    }

    private void ApplyText()
    {
        RefreshButton.Content = Loc.T(AppsStartupText.Refresh);
        DemoBadgeText.Text = Loc.T(AppsStartupText.DemoBadge);
        GateText.Text = Loc.T(AppsStartupText.GateClosed);
        StartupHint.Text = Loc.T(AppsStartupText.StartupTabHint);
        AppsHint.Text = Loc.T(AppsStartupText.AppsTabHint);
        OriginText.Text = Loc.T(AppsStartupText.DataIsSynthetic);
        StatusText.Text = manager is null ? Loc.T(AppsStartupText.Unavailable)
            : failed ? Loc.T(AppsStartupText.LoadFailed)
            : loaded ? string.Empty : Loc.T(AppsStartupText.NotLoaded);
        WarningText.Text = Loc.T(AppsStartupText.IncompleteWarning);
        WarningText.Visibility = loaded && !snapshot.Complete ? Visibility.Visible : Visibility.Collapsed;
        StartupTab.Header = Loc.T(AppsStartupText.StartupTabHeader);
        AppsTab.Header = Loc.T(AppsStartupText.AppsTabHeader);
        StartupSearchLabel.Text = AppsSearchLabel.Text = Loc.T(AppsStartupText.Search);
        StartupSourceLabel.Text = Loc.T(AppsStartupText.FilterSource);
        StartupStateLabel.Text = Loc.T(AppsStartupText.FilterState);
        AppsKindLabel.Text = Loc.T(AppsStartupText.FilterType);
        AppsScopeLabel.Text = Loc.T(AppsStartupText.FilterScope);
        StartupSearch.SetValue(AutomationProperties.NameProperty, Loc.T(AppsStartupText.Search));
        AppsSearch.SetValue(AutomationProperties.NameProperty, Loc.T(AppsStartupText.Search));
        StartupColName.Header = AppsColName.Header = HistColName.Header = Loc.T(AppsStartupText.ColName);
        StartupColPublisher.Header = AppsColPublisher.Header = Loc.T(AppsStartupText.ColPublisher);
        StartupColSource.Header = Loc.T(AppsStartupText.FilterSource);
        StartupColScope.Header = AppsColScope.Header = Loc.T(AppsStartupText.FilterScope);
        StartupColState.Header = Loc.T(AppsStartupText.FilterState);
        StartupColRuntime.Header = Loc.T(AppsStartupText.ColRuntime);
        StartupColEligibility.Header = AppsColEligibility.Header = Loc.T(AppsStartupText.ColEligibility);
        AppsColVersion.Header = Loc.T(AppsStartupText.ColVersion);
        AppsColKind.Header = Loc.T(AppsStartupText.FilterType);
        HistColDate.Header = Loc.T(AppsStartupText.ColDate);
        HistColAction.Header = Loc.T(AppsStartupText.ColAction);
        HistColResult.Header = Loc.T(AppsStartupText.ColResult);
        HistColOrigin.Header = Loc.T(AppsStartupText.DetOrigin);
        StartupGrid.SetValue(AutomationProperties.NameProperty, Loc.T(AppsStartupText.StartupGridName));
        AppsGrid.SetValue(AutomationProperties.NameProperty, Loc.T(AppsStartupText.AppsGridName));
        HistoryGrid.SetValue(AutomationProperties.NameProperty, Loc.T(AppsStartupText.HistoryTitle));
        foreach (var box in new[] { StartupDetails, AppsDetails })
            box.SetValue(AutomationProperties.NameProperty, Loc.T(AppsStartupText.DetailsName));
        foreach (var box in new[] { StartupPreviewBox, UninstallPreviewBox })
            box.SetValue(AutomationProperties.NameProperty, Loc.T(AppsStartupText.PreviewName));
        PreviewDisableButton.Content = Loc.T(AppsStartupText.PreviewDisable);
        PreviewEnableButton.Content = Loc.T(AppsStartupText.PreviewEnable);
        PreviewUninstallButton.Content = Loc.T(AppsStartupText.PreviewUninstall);
        StartupExecuteButton.Content = UninstallExecuteButton.Content = Loc.T(AppsStartupText.Execute);
        StartupConfirmBox.SetValue(AutomationProperties.NameProperty, Loc.T(AppsStartupText.ConfirmLabel));
        UninstallConfirmBox.SetValue(AutomationProperties.NameProperty, Loc.T(AppsStartupText.ConfirmLabel));
        HistoryTitle.Text = Loc.T(AppsStartupText.HistoryTitle);
    }

    // ───── Filtros ─────

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!updating && IsInitialized) ApplyFilters();
    }

    private void ApplyFilters()
    {
        if (StartupGrid is null) return;
        var startup = AppsStartupFilters.Filter(snapshot.Startup, StartupSearch.Text,
            (StartupSourceCombo.SelectedItem as LocalizedChoice<StartupSourceKind?>)?.Value,
            (StartupStateCombo.SelectedItem as LocalizedChoice<StartupConfiguredState?>)?.Value);
        var selectedStartup = (StartupGrid.SelectedItem as StartupRow)?.Entry.StableId;
        StartupGrid.ItemsSource = startup.Select(e => new StartupRow(e, AppsStartupPolicy.Evaluate(e))).ToArray();
        StartupGrid.SelectedItem = StartupGrid.Items.OfType<StartupRow>().FirstOrDefault(r => r.Entry.StableId == selectedStartup);
        StartupSummary.Text = Summary(snapshot.Startup.Count, startup.Count, AppsStartupText.EmptyStartup);

        var apps = AppsStartupFilters.Filter(snapshot.Apps, AppsSearch.Text,
            (AppsKindCombo.SelectedItem as LocalizedChoice<InstalledApplicationKind?>)?.Value,
            (AppsScopeCombo.SelectedItem as LocalizedChoice<AppsScope?>)?.Value);
        var selectedApp = (AppsGrid.SelectedItem as AppRow)?.Entry.StableId;
        AppsGrid.ItemsSource = apps.Select(a => new AppRow(a, AppsStartupPolicy.Evaluate(a))).ToArray();
        AppsGrid.SelectedItem = AppsGrid.Items.OfType<AppRow>().FirstOrDefault(r => r.Entry.StableId == selectedApp);
        AppsSummary.Text = Summary(snapshot.Apps.Count, apps.Count, AppsStartupText.EmptyApps);
    }

    private static string Summary(int total, int shown, string empty) =>
        total == 0 ? Loc.T(empty) : shown == 0 ? Loc.T(AppsStartupText.NoMatches) : $"{shown} / {total}";

    // ───── Inicialização ─────

    private StartupRow? SelectedStartup => StartupGrid.SelectedItem as StartupRow;

    private void OnStartupSelection(object sender, SelectionChangedEventArgs e)
    {
        startupPreview = null;
        StartupPreviewBox.Visibility = StartupConfirmPanel.Visibility = Visibility.Collapsed;
        StartupConfirmBox.Text = string.Empty;
        ShowStartupDetails();
    }

    private void ShowStartupDetails()
    {
        if (SelectedStartup is not { } row)
        {
            StartupDetails.Text = Loc.T(AppsStartupText.SelectItem);
            PreviewDisableButton.IsEnabled = PreviewEnableButton.IsEnabled = false;
            return;
        }
        var e = row.Entry;
        var text = new StringBuilder();
        Line(text, AppsStartupText.ColName, e.Name);
        Line(text, AppsStartupText.ColPublisher, row.Publisher);
        Line(text, AppsStartupText.FilterSource, row.Source);
        Line(text, AppsStartupText.DetReference, e.Reference);
        Line(text, AppsStartupText.DetScope, row.Scope);
        Line(text, AppsStartupText.DetState, row.State);
        Line(text, AppsStartupText.DetRuntime, row.Runtime);
        Line(text, AppsStartupText.DetType, e.EntryType);
        Line(text, AppsStartupText.ColEligibility, row.EligibilityText);
        Line(text, AppsStartupText.DetPrivilege, Loc.T(row.Eligibility.RequiresPrivilege ? AppsStartupText.RequiresPrivilege : AppsStartupText.NoPrivilege));
        Line(text, AppsStartupText.DetReason, Loc.T(AppsStartupText.Reason(row.Eligibility.Reason)));
        Line(text, AppsStartupText.DetId, e.StableId);
        Line(text, AppsStartupText.DetOrigin, Loc.T(e.Origin == AppsDataOrigin.Synthetic ? AppsStartupText.DetSynthetic : AppsStartupText.DetOperational));
        StartupDetails.Text = text.ToString();
        var eligible = row.Eligibility.Eligibility == ChangeEligibility.Eligible && !busy;
        PreviewDisableButton.IsEnabled = eligible && e.State == StartupConfiguredState.Enabled;
        PreviewEnableButton.IsEnabled = eligible && e.State == StartupConfiguredState.Disabled;
    }

    private void OnPreviewDisable(object sender, RoutedEventArgs e) => PreviewStartup(StartupAction.Disable);
    private void OnPreviewEnable(object sender, RoutedEventArgs e) => PreviewStartup(StartupAction.Enable);

    private void PreviewStartup(StartupAction action)
    {
        if (manager is null || SelectedStartup is not { } row) return;
        startupPreview = manager.PreviewStartupChange(row.Entry, action);
        StartupConfirmBox.Text = string.Empty;
        ShowStartupPreview(startupPreview);
    }

    private void ShowStartupPreview(StartupChangePreview preview)
    {
        var text = new StringBuilder();
        Line(text, AppsStartupText.ColAction, Loc.T(preview.Action == StartupAction.Disable ? AppsStartupText.HistoryDisable : AppsStartupText.HistoryEnable));
        Line(text, AppsStartupText.ColName, preview.Entry.Name);
        Line(text, AppsStartupText.DetReference, preview.Entry.Reference);
        Line(text, AppsStartupText.DetId, preview.Entry.StableId);
        text.AppendLine(Loc.T(AppsStartupText.PreviewNote));
        text.AppendLine(preview.CanExecute ? $"{Loc.T(AppsStartupText.ConfirmationIs)}: {preview.ConfirmationPhrase}" : Loc.T(AppsStartupText.NotExecutable));
        StartupPreviewBox.Text = text.ToString();
        StartupPreviewBox.Visibility = Visibility.Visible;
        StartupConfirmPanel.Visibility = preview.CanExecute ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnStartupExecute(object sender, RoutedEventArgs e)
    {
        if (manager is null || startupPreview is not { } preview || busy) return;
        if (MessageBox.Show(Window.GetWindow(this), Loc.T(AppsStartupText.ConfirmDialog), Loc.T(AppsStartupText.Title),
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        using var lease = BeginOperation is null ? standaloneOperations.Acquire() : BeginOperation();
        if (lease is null) return;
        SetBusy(true);
        AppsOperationOutcome outcome;
        try { outcome = (await manager.ExecuteStartupChangeAsync(preview, StartupConfirmBox.Text, CancellationToken.None)).Outcome; }
        catch (OperationCanceledException) { outcome = AppsOperationOutcome.Indeterminate; }
        finally { SetBusy(false); }
        await FinishOperationAsync(outcome, lease);
    }

    // ───── Aplicativos instalados ─────

    private AppRow? SelectedApp => AppsGrid.SelectedItem as AppRow;

    private void OnAppSelection(object sender, SelectionChangedEventArgs e)
    {
        uninstallPreview = null;
        UninstallPreviewBox.Visibility = UninstallConfirmPanel.Visibility = Visibility.Collapsed;
        UninstallConfirmBox.Text = string.Empty;
        ShowAppDetails();
    }

    private void ShowAppDetails()
    {
        if (SelectedApp is not { } row)
        {
            AppsDetails.Text = Loc.T(AppsStartupText.SelectItem);
            PreviewUninstallButton.IsEnabled = false;
            return;
        }
        var a = row.Entry;
        var text = new StringBuilder();
        Line(text, AppsStartupText.ColName, a.Name);
        Line(text, AppsStartupText.ColPublisher, row.Publisher);
        Line(text, AppsStartupText.DetVersion, row.Version);
        Line(text, AppsStartupText.DetKind, row.Kind);
        Line(text, AppsStartupText.DetRegistry, a.RegistrySource);
        Line(text, AppsStartupText.DetScope, row.Scope);
        Line(text, AppsStartupText.DetId, a.StableId);
        if (a.Location is not null) Line(text, AppsStartupText.DetLocation, a.Location);
        Line(text, AppsStartupText.ColEligibility, row.EligibilityText);
        Line(text, AppsStartupText.DetMethod, Loc.T(AppsStartupText.Method(row.Eligibility.Method)));
        Line(text, AppsStartupText.DetPrivilege, Loc.T(row.Eligibility.RequiresPrivilege ? AppsStartupText.RequiresPrivilege : AppsStartupText.NoPrivilege));
        Line(text, AppsStartupText.DetReason, Loc.T(AppsStartupText.Reason(row.Eligibility.Reason)));
        Line(text, AppsStartupText.DetOrigin, Loc.T(a.Origin == AppsDataOrigin.Synthetic ? AppsStartupText.DetSynthetic : AppsStartupText.DetOperational));
        AppsDetails.Text = text.ToString();
        PreviewUninstallButton.IsEnabled = row.Eligibility.Eligibility == ChangeEligibility.Eligible && !busy;
    }

    private void OnPreviewUninstall(object sender, RoutedEventArgs e)
    {
        if (manager is null || SelectedApp is not { } row) return;
        uninstallPreview = manager.PreviewUninstall(row.Entry);
        UninstallConfirmBox.Text = string.Empty;
        ShowUninstallPreview(uninstallPreview);
    }

    private void ShowUninstallPreview(UninstallPreview preview)
    {
        var text = new StringBuilder();
        Line(text, AppsStartupText.ColName, preview.Entry.Name);
        Line(text, AppsStartupText.DetVersion, preview.Entry.Version ?? "—");
        Line(text, AppsStartupText.DetId, preview.Entry.StableId);
        Line(text, AppsStartupText.DetMethod, Loc.T(AppsStartupText.Method(preview.Eligibility.Method)));
        text.AppendLine(Loc.T(AppsStartupText.UninstallNote));
        text.AppendLine(preview.CanExecute ? $"{Loc.T(AppsStartupText.ConfirmationIs)}: {preview.ConfirmationPhrase}" : Loc.T(AppsStartupText.NotExecutable));
        UninstallPreviewBox.Text = text.ToString();
        UninstallPreviewBox.Visibility = Visibility.Visible;
        UninstallConfirmPanel.Visibility = preview.CanExecute ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnUninstallExecute(object sender, RoutedEventArgs e)
    {
        if (manager is null || uninstallPreview is not { } preview || busy) return;
        if (MessageBox.Show(Window.GetWindow(this), Loc.T(AppsStartupText.ConfirmDialog), Loc.T(AppsStartupText.Title),
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        using var lease = BeginOperation is null ? standaloneOperations.Acquire() : BeginOperation();
        if (lease is null) return;
        SetBusy(true);
        AppsOperationOutcome outcome;
        try { outcome = (await manager.ExecuteUninstallAsync(preview, UninstallConfirmBox.Text, CancellationToken.None)).Outcome; }
        catch (OperationCanceledException) { outcome = AppsOperationOutcome.Indeterminate; }
        finally { SetBusy(false); }
        await FinishOperationAsync(outcome, lease);
    }

    // ───── Comum ─────

    private void OnConfirmChanged(object sender, TextChangedEventArgs e)
    {
        StartupExecuteButton.IsEnabled = !busy && startupPreview is { CanExecute: true } sp &&
            string.Equals(StartupConfirmBox.Text, sp.ConfirmationPhrase, StringComparison.Ordinal);
        UninstallExecuteButton.IsEnabled = !busy && uninstallPreview is { CanExecute: true } up &&
            string.Equals(UninstallConfirmBox.Text, up.ConfirmationPhrase, StringComparison.Ordinal);
    }

    /// <summary>Reconcilia o inventário após a ação confirmada pelo usuário e mostra o desfecho.</summary>
    private async Task FinishOperationAsync(AppsOperationOutcome outcome, IDisposable lease)
    {
        startupPreview = null; uninstallPreview = null;
        StartupPreviewBox.Visibility = StartupConfirmPanel.Visibility = UninstallPreviewBox.Visibility = UninstallConfirmPanel.Visibility = Visibility.Collapsed;
        StartupConfirmBox.Text = UninstallConfirmBox.Text = string.Empty;
        lease.Dispose();
        await RefreshAsync();
        StatusText.Text = Loc.T(AppsStartupText.Outcome(outcome));
    }

    private void ShowHistory()
    {
        var rows = (manager?.History ?? []).OrderByDescending(h => h.AtUtc).Select(h => new AppsHistoryRow(h)).ToArray();
        HistoryGrid.ItemsSource = rows;
        HistoryEmpty.Text = rows.Length == 0 ? Loc.T(AppsStartupText.HistoryEmpty) : string.Empty;
    }

    private static void Line(StringBuilder text, string labelKey, string value) =>
        text.Append(Loc.T(labelKey)).Append(": ").AppendLine(value);
}
