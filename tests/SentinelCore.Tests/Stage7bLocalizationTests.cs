using System.Text.Json;
using SentinelCore.App;
using SentinelCore.App.Localization;
using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

[Collection("Localization")]
public sealed class Stage7bLocalizationTests : IDisposable
{
    [Theory]
    [InlineData("Todos", "All")]
    [InlineData("Opcionais", "Optional")]
    [InlineData("Baixo", "Low")]
    [InlineData("Médio", "Medium")]
    [InlineData("Alto", "High")]
    [InlineData("Não validados", "Not validated")]
    [InlineData("Presentes no inventário", "Present in inventory")]
    [InlineData("Ausentes no inventário", "Absent from inventory")]
    [InlineData("Não verificados", "Not verified")]
    [InlineData("Protegidos", "Protected")]
    [InlineData("Microsoft", "Microsoft")]
    [InlineData("Terceiros/OEM", "Third party/OEM")]
    public void FilterLabelsChangeWithoutChangingTheSelectedKey(string key, string english)
    {
        var selected = new LocalizedChoice<string>(key, key);
        var notifications = 0;
        selected.PropertyChanged += (_, change) => { Assert.Equal("Label", change.PropertyName); notifications++; };
        Localizer.Use(AppLanguage.Portuguese);
        Assert.Equal(key, selected.Label);
        Localizer.Use(AppLanguage.English);
        selected.RefreshLanguage();
        Assert.Equal(english, selected.Label);
        Assert.Same(key, selected.Value);
        Localizer.Use(AppLanguage.Portuguese);
        selected.RefreshLanguage();
        Assert.Equal(key, selected.Label);
        Assert.Equal(2, notifications);
        Assert.Null(System.Windows.Application.Current);
    }

    [Fact]
    public void EnumBooleanAndRecordedStateChoicesKeepValuesAcrossBothDirections()
    {
        var state = new LocalizedChoice<string>(nameof(TransactionState.ExecutionStarted),
            () => Loc.T("Execução iniciada"));
        var module = new LocalizedChoice<RecoveryModule?>(RecoveryModule.WindowsRepair,
            () => RecoveryCenterPresentation.Label(RecoveryModule.WindowsRepair));
        var conflict = new LocalizedChoice<bool?>(false, "Sem conflito registrado");
        Localizer.Use(AppLanguage.English);
        var english = state.Label;
        Assert.Equal("No conflict recorded", conflict.Label);
        Assert.NotEqual("Reparos do Windows R1", module.Label);
        Localizer.Use(AppLanguage.Portuguese);
        Assert.Equal("Execução iniciada", state.Label);
        Assert.NotEqual(english, state.Label);
        Assert.Equal(nameof(TransactionState.ExecutionStarted), state.Value);
        Assert.Equal(RecoveryModule.WindowsRepair, module.Value);
        Assert.False(conflict.Value);
    }

    [Fact]
    public void DiagnosticPresetObjectsKeepEnumAndOccurrenceOffset()
    {
        var correlation = new CorrelationPresentationRows.CorrelationPresetRow(EventWindow.Last7Days, "7 dias");
        var network = new NetworkPresentationRows.NetworkPresetRow(NetworkSessionPreset.Snapshot, "Instantâneo");
        var monitoring = new MonitoringPresentationRows.MonitoringPresetRow(MonitoringDurationPreset.Minutes15, "15 minutos");
        var occurred = new MonitoringPresentationRows.MonitoringOccurrenceRow(TimeSpan.FromMinutes(2), "≈ 2 minutos atrás");
        Localizer.Use(AppLanguage.English);
        Assert.Equal("7 days", correlation.Label);
        Assert.Equal("Snapshot", network.Label);
        Assert.Equal("15 minutes", monitoring.Label);
        Assert.Equal("≈ 2 minutes ago", occurred.Label);
        Localizer.Use(AppLanguage.Portuguese);
        Assert.Equal("7 dias", correlation.Label);
        Assert.Equal("Instantâneo", network.Label);
        Assert.Equal("15 minutos", monitoring.Label);
        Assert.Equal(EventWindow.Last7Days, correlation.Window);
        Assert.Equal(NetworkSessionPreset.Snapshot, network.Preset);
        Assert.Equal(MonitoringDurationPreset.Minutes15, monitoring.Preset);
        Assert.Equal(TimeSpan.FromMinutes(2), occurred.Offset);
    }

    [Fact]
    public void CorrelationOriginKeepsItsGuidAndRecordedReference()
    {
        var origin = new CorrelationEvidenceOrigin(CorrelationSource.Events, Guid.NewGuid().ToString("D"), "Conflitos");
        var row = new CorrelationPresentationRows.CorrelationOriginRow(origin);
        var json = JsonSerializer.Serialize(origin);
        Localizer.Use(AppLanguage.Portuguese);
        var portuguese = row.Label;
        Localizer.Use(AppLanguage.English);
        Assert.NotEqual(portuguese, row.Label);
        Assert.Contains(origin.SourceRecordId, row.Label);
        Assert.Equal(json, JsonSerializer.Serialize(origin));
        Assert.Equal("Conflitos", row.Origin.OriginReference);
    }

    [Fact]
    public void MonitoringRowsTranslateCategoryWithoutChangingIncidentTextOrIdentity()
    {
        var incident = MonitoringSamples.Incident(Guid.NewGuid(), description: "Conflitos");
        var row = new MonitoringPresentationRows.MonitoringIncidentRow(incident, null);
        var before = JsonSerializer.Serialize(incident);
        Localizer.Use(AppLanguage.Portuguese);
        var portuguese = row.Category;
        Localizer.Use(AppLanguage.English);
        Assert.NotEqual(portuguese, row.Category);
        Assert.Equal("occurrence not provided", row.Detail);
        Assert.Equal("Conflitos", row.Description);
        Assert.Equal(before, JsonSerializer.Serialize(incident));
    }

    [Fact]
    public void AuditScopeTranslatesBothConsentsAndKeepsTheirLimits()
    {
        Localizer.Use(AppLanguage.English);
        Assert.StartsWith("The basic audit reads", AuditScopeText.For(AuditKind.Basic));
        var full = AuditScopeText.For(AuditKind.Full);
        Assert.Contains("30 s", full);
        Assert.Contains("5 min", full);
        Assert.Contains("By confirming, you authorize this session", full);
        Localizer.Use(AppLanguage.Portuguese);
        Assert.StartsWith("A auditoria completa", AuditScopeText.For(AuditKind.Full));
    }

    [Fact]
    public void RecoveryRendererTranslatesOnlyLabelsAndPreservesPreviewAndHistory()
    {
        var operation = new RecoveryOperation(Guid.NewGuid(), RecoveryModule.Tweaks, "SYN-01", "TypedOperation",
            "Conflitos", MonitoringSamples.Now, nameof(TransactionState.ExecutionStarted), RecoveryOperationOutcome.Indeterminate,
            RecoveryOrigin.Synthetic, RiskLevel.Low, RecoveryCapability.Blocked, true, false, false, false,
            "Autorização: Conflitos", "Backup: Conflitos", "Verificação: Conflitos", ["Conflitos"], [], [], [], [], "fixture/report.md");
        var original = JsonSerializer.Serialize(operation);
        var seen = new List<string>();
        string Translate(string value) { seen.Add(value); return Loc.T(value); }
        Localizer.Use(AppLanguage.English);
        var english = RecoveryCenterPresentation.Details(operation, Translate);
        Assert.Contains("IDENTIFICATION", english);
        Assert.Contains("Target: Conflitos", english);
        Assert.Contains(operation.Id.ToString("D"), english);
        Assert.Contains(nameof(TransactionState.ExecutionStarted), english);
        Assert.DoesNotContain(operation.Target, seen);
        Assert.DoesNotContain(operation.OperationId, seen);
        Localizer.Use(AppLanguage.Portuguese);
        Assert.Contains("IDENTIFICAÇÃO", RecoveryCenterPresentation.Details(operation, Loc.T));
        Assert.Equal(original, JsonSerializer.Serialize(operation));
        Assert.Equal(RecoveryCenterPresentation.Details(operation), RecoveryCenterPresentation.Details(operation, Loc.T));
    }

    [Fact]
    public void DynamicMessagesRerenderLatestTextWithoutChangingTheCapturedContext()
    {
        var messages = new LocalizedMessages();
        var id = Guid.NewGuid();
        const string confirmation = "AUTORIZAR SYN-01 hash12";
        var model = new { Id = id, Confirmation = confirmation, Description = "Conflitos" };
        string status = "", count = "";
        Localizer.Use(AppLanguage.Portuguese);
        messages.Set("status", () => status = Loc.F($"Alvo: {model.Description} · ID {model.Id:D}"));
        messages.Set("count", () => count = Loc.F($"{3} evidência(s) · "));
        messages.Set("count", () => count = Loc.F($"{7} evidência(s) · "));
        Localizer.Use(AppLanguage.English);
        messages.Refresh();
        Assert.Equal($"Target: Conflitos · ID {id:D}", status);
        Assert.Equal("7 evidence item(s) · ", count);
        Assert.Equal(confirmation, model.Confirmation);
        Localizer.Use(AppLanguage.Portuguese);
        messages.Refresh();
        Assert.StartsWith("Alvo: Conflitos", status);
        Assert.Equal("7 evidência(s) · ", count);
    }

    [Fact]
    public void LanguageRefreshHasNoReloadOrAuthorizationAndPreservesPreparedInputs()
    {
        var shell = TestRepository.AppSource("MainWindow.Localization.cs");
        Assert.DoesNotContain("Reload", shell);
        Assert.DoesNotContain("RunAsync", shell);
        Assert.DoesNotContain("PrepareAsync", shell);
        var main = TestRepository.AppSource("MainWindow.xaml.cs");
        var handler = main[main.IndexOf("void OnLanguageChanged(", StringComparison.Ordinal)..main.IndexOf("private async Task ReloadAsync", StringComparison.Ordinal)];
        Assert.Contains("RefreshLanguagePresentation()", handler);
        Assert.DoesNotContain("ReloadHistoriesAsync", handler);
        var apps = TestRepository.AppSource("AppsStartupView.xaml.cs");
        var refresh = apps[apps.IndexOf("internal void RefreshLanguage()", StringComparison.Ordinal)..apps.IndexOf("private async void OnLoaded(", StringComparison.Ordinal)];
        Assert.DoesNotContain("RefreshAsync", refresh);
        Assert.DoesNotContain("Execute", refresh);
        Assert.DoesNotContain("ConfirmBox.Text =", refresh);
        var recovery = TestRepository.AppSource("RecoveryCenterView.xaml.cs");
        Assert.Contains("ShowSelection(preserveConfirmation: true)", recovery);
    }

    [Fact]
    public void RepairMessagesRerenderFromMemoryWithoutReloadingOrAuthorizing()
    {
        Assert.False(File.Exists(TestRepository.Combine("src", "SentinelCore.App", "MainWindow.Tweaks.cs")));
        var repairs = TestRepository.AppSource("MainWindow.Repairs.cs");
        var shell = TestRepository.AppSource("MainWindow.Localization.cs");
        foreach (var control in new[] { "RepairsModeText", "RepairsModeDetail", "RepairsSyntheticBanner", "RepairsVolumeText",
                     "RepairsDocumentedText", "RepairsUnfinishedText", "RepairsDetailsTitle", "RepairsDetailsMeta",
                     "RepairsDetailsText", "RepairsOutputText", "RepairsConfirmationHint", "RepairsStatusText" })
            Assert.Contains($"repairMessages.Set(nameof({control})", repairs);
        // The language switch re-renders messages and list rows from memory: no reload, no plan, no authorization.
        Assert.Contains("repairMessages.Refresh()", shell);
        Assert.DoesNotContain("tweakMessages", shell);
        Assert.DoesNotContain("TweaksCardsHost", shell);
        Assert.Contains("RepairsCardsHost.Items.Refresh()", shell);
        Assert.Contains("RepairsHistoryList.Items.Refresh()", shell);
        // Renderers never write into the typed confirmation boxes or their buttons.
        Assert.DoesNotContain("nameof(RepairsConfirmationText)", repairs);
        // Statuses carried into reloads are re-derived functions, so a language switch re-translates them
        // from the stored result instead of repeating the operation.
        Assert.Contains("Func<string>? status = null", repairs);
        // 7B.6: a failed reload must not replace the execution result; live output and run warnings are re-rendered.
        Assert.Contains("repairMessages.Set(nameof(RepairsOutputText), () => RepairsOutputText.Text = liveOutput", repairs);
        Assert.Contains("Func<string> Render", TestRepository.AppSource("MainWindow.xaml.cs"));
    }

    [Fact]
    public async Task RemovalPresentationTranslatesLabelsWithoutTouchingPlanIdentityOrConfirmation()
    {
        var (prepared, typed) = await RemovalPlanFixtureAsync();
        Localizer.Use(AppLanguage.English);
        var unavailable = DebloatRemovalPresentation.Unavailable(Loc.T);
        Assert.StartsWith("T3C — compilable native engine", unavailable.Plan, StringComparison.Ordinal);
        Assert.StartsWith("SIMULATION — NO REAL APPLICATION", unavailable.Limitations, StringComparison.Ordinal);
        Localizer.Use(AppLanguage.Portuguese);
        var portugueseUnavailable = DebloatRemovalPresentation.Unavailable(Loc.T);
        Assert.StartsWith("T3C — motor nativo compilável", portugueseUnavailable.Plan, StringComparison.Ordinal);
        Assert.Equal(portugueseUnavailable, DebloatRemovalPresentation.Unavailable());

        Localizer.Use(AppLanguage.English);
        var english = DebloatRemovalPresentation.FromLegacySynthetic(prepared, Loc.T);
        Assert.StartsWith("T3 fixture preview: ", english.Plan, StringComparison.Ordinal);
        Assert.Contains(prepared.Package.PackageFullName, english.Plan, StringComparison.Ordinal);
        Assert.Contains(prepared.Id.ToString("D"), english.Plan, StringComparison.Ordinal);
        Assert.Contains(prepared.PlanHash, english.Plan, StringComparison.Ordinal);
        Assert.Contains("This plan is not a T3C authorization", english.Plan, StringComparison.Ordinal);
        Assert.Contains("Dependencies reported by the T3 fixture: ", english.Dependencies, StringComparison.Ordinal);
        Assert.StartsWith("Specific authorization requires", english.Authorization, StringComparison.Ordinal);
        Assert.StartsWith("SIMULATION — NO REAL APPLICATION", english.Limitations, StringComparison.Ordinal);
        Assert.Equal(english, DebloatRemovalPresentation.FromLegacySynthetic(prepared, Loc.T));
        Localizer.Use(AppLanguage.Portuguese);
        var portuguese = DebloatRemovalPresentation.FromLegacySynthetic(prepared, Loc.T);
        Assert.StartsWith("Prévia da fixture T3: ", portuguese.Plan, StringComparison.Ordinal);
        Assert.Contains("Autorização específica exige plano simulado", portuguese.Authorization, StringComparison.Ordinal);
        // Without a translate function the historical behaviour is preserved verbatim.
        Assert.Equal(portuguese, DebloatRemovalPresentation.FromLegacySynthetic(prepared));

        Localizer.Use(AppLanguage.English);
        var presentation = DebloatRemovalPresentation.From(typed, progress: new(
            typed.Id, DebloatRemovalPhase.ProgressReported, 40, "fixture"), translate: Loc.T);
        Assert.Contains("Plan: ", presentation.Plan, StringComparison.Ordinal);
        Assert.Contains(typed.Hash, presentation.Plan, StringComparison.Ordinal);
        Assert.Contains("Synthetic SID: ", presentation.Plan, StringComparison.Ordinal);
        Assert.Contains(typed.Snapshot.Observation.SelectedPackage.PackageFullName, presentation.Plan, StringComparison.Ordinal);
        Assert.Contains("Completeness: ", presentation.Dependencies, StringComparison.Ordinal);
        Assert.Contains("Real risk: ", presentation.Risks, StringComparison.Ordinal);
        Assert.StartsWith("Simulation only: ", presentation.Authorization, StringComparison.Ordinal);
        Assert.Contains(typed.Confirmation, presentation.Authorization, StringComparison.Ordinal);
        Assert.Contains("percentage reported by the provider: 40%", presentation.Progress, StringComparison.Ordinal);
        Assert.StartsWith("No execution result.", presentation.Result, StringComparison.Ordinal);
        Assert.Contains("History not loaded in this component.", presentation.History, StringComparison.Ordinal);
        Localizer.Use(AppLanguage.Portuguese);
        Assert.StartsWith("Não iniciado. Sem percentual informado.",
            DebloatRemovalPresentation.From(typed).Progress, StringComparison.Ordinal);
    }

    private static async Task<(DebloatPreparedPlan Prepared, DebloatRemovalPlan Typed)> RemovalPlanFixtureAsync()
    {
        // Simulation reuse: the T1 synthetic catalog only; no journal, executor or Windows surface is touched.
        using var safety = new SafetyScope();
        var simulation = await safety.SimulateAsync("SYN-PREF-01");
        var identity = new DebloatRemovalIdentity("Fixture.App", "Fixture.App_1.0.0.0_x64__synthetic",
            "FixtureFamily", "CN=Fixture Publisher", "publisher-id", "1.0.0.0", "x64", "S-1-SYNTHETIC");
        var dependencies = new DebloatRemovalDependencyEvidence(DebloatObservationCompleteness.Complete,
            [], [], [], [], [], true, []);
        var observation = new DebloatRemovalObservation(true, true, 22631, false, "S-1-SYNTHETIC",
            "t3c-localization-1", "1", "catalog-hash", "inventory-revision",
            DebloatInventoryQueryState.Completed, DebloatObservationCompleteness.Complete, [], identity,
            false, false, dependencies, true, "fixture reinstall information", true, ["Fixture consequence"]);
        var entry = new DebloatCatalogEntry("t3c-localization-1", "Fixture.App", "Fixture", "Fixture",
            "fixture", RiskLevel.Low, RiskLevel.NotValidated, "Fixture consequence",
            "fixture reinstall information", "Fixture data loss warning", "fixture://source", "fixture-commit");
        var snapshot = new DebloatRemovalSnapshot(1, observation, entry, DebloatRemovalScenario.Success, false, false);
        var package = new DebloatPackage("t3c-localization-1", "Fixture", "Fixture.App", "FixtureFamily",
            "Fixture.App_1.0.0.0_x64__synthetic", "CN=Fixture Publisher", "1.0.0.0", "x64", "S-1-SYNTHETIC",
            DebloatPackageKind.Appx, DebloatScope.CurrentUser, DebloatInstallState.Installed, false, false, false,
            DebloatDependencyStatus.KnownNone, [], true, "fixture", []);
        var prepared = new DebloatPreparedPlan(Guid.NewGuid(), package, entry, RiskLevel.Low,
            DebloatScenario.Success, false, simulation, "fixture-plan-hash",
            "AUTORIZAR FIXTURE hash12", ["Fixture consequence"], ["Fixture limitation"]);
        var typed = new DebloatRemovalPlan(Guid.NewGuid(), "DBLT-LOC-FIXTURE", snapshot,
            new DebloatRemovalPreflight(true, false, RiskLevel.NotValidated, [], ["Real execution blocked"]),
            simulation, "typed-plan-hash", "AUTORIZAR FIXTURE hash12");
        return (prepared, typed);
    }

    [Fact]
    public void DiagnosticPageRowWiringTranslatesWithoutRebuildingOperationalState()
    {
        // Dashboard/Resultados/Hardware: rows, badges, freshness, categories and integrity warnings.
        var shell = TestRepository.AppSource("MainWindow.xaml.cs");
        Assert.Contains("FreshnessText(FreshnessState state) => Loc.T(state switch", shell, StringComparison.Ordinal);
        Assert.Contains("Loc.T(kind == AuditKind.Full ? \"Auditoria completa\" : \"Auditoria básica\")", shell, StringComparison.Ordinal);
        Assert.Contains("CollectorName(string id) => Loc.T(id switch", shell, StringComparison.Ordinal);
        Assert.Contains("HardwareCollectorName(string id) => Loc.T(id switch", shell, StringComparison.Ordinal);
        Assert.Contains("PresenceName(DevicePresence state) => Loc.T(state switch", shell, StringComparison.Ordinal);
        Assert.Contains("ChangeTypeName(HardwareChangeType type) => Loc.T(type switch", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("pending.Add(\"há ", shell, StringComparison.Ordinal);
        Assert.Contains("Loc.T(p.Key)}: {w}", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("? \"Falha de gravação do inventário", shell, StringComparison.Ordinal);
        // The language switch re-renders integrity warnings from memory without touching stores.
        var localization = TestRepository.AppSource("MainWindow.Localization.cs");
        Assert.Contains("RenderWarnings();", localization, StringComparison.Ordinal);
        Assert.DoesNotContain("ReloadHistoriesAsync", localization, StringComparison.Ordinal);
        // Events: combo rows keep PortugueseLabel and re-translate; history/quality/group rows use Loc.F.
        var events = TestRepository.AppSource("MainWindow.Events.cs");
        Assert.Contains("record EventWindowRow(EventWindow Window, string PortugueseLabel)", events, StringComparison.Ordinal);
        Assert.Contains("record EventCategoryRow(EventCategory? Category, string PortugueseLabel)", events, StringComparison.Ordinal);
        Assert.Contains("Label => Loc.T(PortugueseLabel)", events, StringComparison.Ordinal);
        Assert.Contains("QualityText => Quality.State switch", events, StringComparison.Ordinal);
        Assert.Contains("Loc.F($\"Qualidade afetada · ", events, StringComparison.Ordinal);
        Assert.Contains("Loc.T(EventCatalog.StatusName", events, StringComparison.Ordinal);
        Assert.Contains("Loc.T(EventCatalog.CategoryName", events, StringComparison.Ordinal);
        Assert.Contains("WindowName(EventWindow window) => Loc.T(window switch", events, StringComparison.Ordinal);
        // Performance: metric names, status/coverage/preset names and history rows translate.
        var performance = TestRepository.AppSource("MainWindow.Performance.cs");
        Assert.Contains("record PerformancePresetRow(PerformanceSessionPreset Preset, string PortugueseLabel)", performance, StringComparison.Ordinal);
        Assert.Contains("ExecutionName(PerformanceExecutionStatus execution) => Loc.T(execution switch", performance, StringComparison.Ordinal);
        Assert.Contains("CoverageName(PerformanceCoverageStatus status) => Loc.T(status switch", performance, StringComparison.Ordinal);
        Assert.Contains("PresetName(PerformanceSessionPreset preset) => Loc.T(preset switch", performance, StringComparison.Ordinal);
        Assert.Contains("Loc.T(\"CPU total\")", performance, StringComparison.Ordinal);
        Assert.Contains("Loc.F($\"{ExecutionName(coverage.Execution)} · cobertura {CoverageName(coverage.Status)} · \")", performance, StringComparison.Ordinal);
        // Graphics: same pattern for statuses, rows and preset combo.
        var graphics = TestRepository.AppSource("MainWindow.Graphics.cs");
        Assert.Contains("record GraphicsPresetRow(EventWindow Window, string PortugueseLabel)", graphics, StringComparison.Ordinal);
        Assert.Contains("GraphicsStatusName(GraphicsInvestigationStatus status) => Loc.T(status switch", graphics, StringComparison.Ordinal);
        Assert.Contains("GraphicsObservationKindName(GraphicsObservationKind kind) => Loc.T(kind switch", graphics, StringComparison.Ordinal);
        Assert.Contains("Loc.F($\"{GraphicsStatusName(Investigation.Status)} · cobertura \")", graphics, StringComparison.Ordinal);
        // The Debloat views were removed; the App no longer touches the legacy removal presentation.
        Assert.DoesNotContain(Directory.EnumerateFiles(TestRepository.Combine("src", "SentinelCore.App"), "*.cs"),
            file => File.ReadAllText(file).Contains("DebloatRemovalPresentation", StringComparison.Ordinal));
    }

    public void Dispose() => Localizer.Use(AppLanguage.English);
}
