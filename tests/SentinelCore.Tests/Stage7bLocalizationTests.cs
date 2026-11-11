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
    [InlineData("Todos", "All", true)]
    [InlineData("Opcionais", "Optional", true)]
    [InlineData("Baixo", "Low", true)]
    [InlineData("Médio", "Medium", false)]
    [InlineData("Alto", "High", false)]
    [InlineData("Não validados", "Not validated", false)]
    [InlineData("Presentes no inventário", "Present in inventory", true)]
    [InlineData("Ausentes no inventário", "Absent from inventory", false)]
    [InlineData("Não verificados", "Not verified", false)]
    [InlineData("Protegidos", "Protected", false)]
    [InlineData("Microsoft", "Microsoft", true)]
    [InlineData("Terceiros/OEM", "Third party/OEM", false)]
    public void FilterLabelsChangeWithoutChangingTheSelectedKey(string key, string english, bool expectedMatch)
    {
        var selected = new LocalizedChoice<string>(key, key);
        var notifications = 0;
        selected.PropertyChanged += (_, change) => { Assert.Equal("Label", change.PropertyName); notifications++; };
        Localizer.Use(AppLanguage.Portuguese);
        Assert.Equal(key, selected.Label);
        var package = new DebloatPackage("fixture", "Fixture", "Microsoft.Fixture", "family", "full",
            "publisher", "1", "x64", "synthetic-user", DebloatPackageKind.Appx, DebloatScope.CurrentUser,
            DebloatInstallState.Installed, false, false, false, DebloatDependencyStatus.KnownNone, [], true, "fixture", []);
        var assessment = new DebloatAssessment(package, null, RiskLevel.Low, DebloatPresence.PresentInSyntheticInventory,
            DebloatEligibility.SimulationAvailable, true, false, [], []);
        Assert.Equal(expectedMatch, DebloatFilters.Matches(assessment, selected.Value));
        Localizer.Use(AppLanguage.English);
        selected.RefreshLanguage();
        Assert.Equal(english, selected.Label);
        Assert.Same(key, selected.Value);
        Assert.Equal(expectedMatch, DebloatFilters.Matches(assessment, selected.Value));
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
        var debloat = TestRepository.AppSource("DebloatView.xaml.cs");
        var refresh = debloat[debloat.IndexOf("internal void RefreshLanguage()", StringComparison.Ordinal)..debloat.IndexOf("public void Initialize(", StringComparison.Ordinal)];
        Assert.Contains("ShowSelection(preservePlan: true)", refresh);
        Assert.DoesNotContain("InvalidatePlan()", refresh);
        Assert.DoesNotContain("ConfirmationText.Text =", refresh);
        Assert.DoesNotContain("MediumReviewCheck.IsChecked =", refresh);
        var recovery = TestRepository.AppSource("RecoveryCenterView.xaml.cs");
        Assert.Contains("ShowSelection(preserveConfirmation: true)", recovery);
    }

    public void Dispose() => Localizer.Use(AppLanguage.English);
}
