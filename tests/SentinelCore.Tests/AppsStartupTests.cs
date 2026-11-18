using System.Collections;
using System.Reflection;
using System.Xml.Linq;
using SentinelCore.App;
using SentinelCore.App.Localization;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.AppsStartup;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>Módulo Aplicativos e Inicialização: somente fixtures artificiais, fakes e inspeção estática.</summary>
public sealed class AppsStartupTests
{
    private static (AppsStartupManager Manager, SyntheticStartupStore Startup, SyntheticApplicationStore Apps) Compose()
    {
        var startup = SyntheticStartupStore.CreateDefault();
        var apps = SyntheticApplicationStore.CreateDefault();
        return (new AppsStartupManager(startup, apps, startup, apps), startup, apps);
    }

    private static StartupEntry Startup(AppsStartupSnapshot snapshot, string name) => snapshot.Startup.Single(e => e.Name == name);
    private static InstalledApplicationEntry App(AppsStartupSnapshot snapshot, string name, AppsScope? scope = null) =>
        snapshot.Apps.Single(a => a.Name == name && (scope is null || a.Scope == scope));

    // ───── Gates ─────

    [Fact]
    public void BothGatesAreClosedAndOfferNoActivationArgument()
    {
        Assert.False(StartupModificationBuildGate.RealExecutionCompiled);
        Assert.True(StartupModificationBuildGate.RealExecutionImplemented);
        Assert.False(StartupModificationBuildGate.PermitsOperationalExecution);
        Assert.False(ApplicationUninstallBuildGate.RealExecutionCompiled);
        Assert.True(ApplicationUninstallBuildGate.RealExecutionImplemented);
        Assert.False(ApplicationUninstallBuildGate.PermitsOperationalExecution);
        foreach (var gate in new[] { typeof(StartupModificationBuildGate), typeof(ApplicationUninstallBuildGate) })
            Assert.DoesNotContain(gate.GetMethods(BindingFlags.Public | BindingFlags.Static),
                m => m.GetParameters().Any(p => p.ParameterType == typeof(IEnumerable<string>)));
        Assert.False(RealDebloatBuildGate.AllowsRealExecution(["--enable-real-debloat"]));
    }

    [Fact]
    public async Task OperationalItemsNeverReachAnExecutorWhileTheGatesAreClosed()
    {
        var startupExecutor = new CountingStartupExecutor(synthetic: false);
        var uninstallExecutor = new CountingUninstallExecutor(synthetic: false);
        var manager = new AppsStartupManager(new FixedStartup(OperationalStartup()), new FixedApps(OperationalApp()),
            startupExecutor, uninstallExecutor);
        var snapshot = await manager.LoadAsync(default);
        var startupPreview = manager.PreviewStartupChange(snapshot.Startup.Single(), StartupAction.Disable);
        Assert.True(startupPreview.CanExecute);
        var startup = await manager.ExecuteStartupChangeAsync(startupPreview, startupPreview.ConfirmationPhrase, default);
        var uninstallPreview = manager.PreviewUninstall(snapshot.Apps.Single());
        Assert.True(uninstallPreview.CanExecute);
        var uninstall = await manager.ExecuteUninstallAsync(uninstallPreview, uninstallPreview.ConfirmationPhrase, default);
        Assert.Equal(AppsOperationOutcome.GateClosed, startup.Outcome);
        Assert.Equal(AppsOperationOutcome.GateClosed, uninstall.Outcome);
        Assert.Equal(0, startupExecutor.Calls);
        Assert.Equal(0, uninstallExecutor.Calls);
    }

    [Fact]
    public async Task SyntheticExecutorsNeverActOnOperationalItemsAndViceVersa()
    {
        var syntheticStartup = SyntheticStartupStore.CreateDefault();
        var syntheticApps = SyntheticApplicationStore.CreateDefault();
        var manager = new AppsStartupManager(new FixedStartup(OperationalStartup()), new FixedApps(OperationalApp()), syntheticStartup, syntheticApps);
        var snapshot = await manager.LoadAsync(default);
        var preview = manager.PreviewStartupChange(snapshot.Startup.Single(), StartupAction.Disable);
        Assert.Equal(AppsOperationOutcome.GateClosed, (await manager.ExecuteStartupChangeAsync(preview, preview.ConfirmationPhrase, default)).Outcome);
        var uninstall = manager.PreviewUninstall(snapshot.Apps.Single());
        Assert.Equal(AppsOperationOutcome.GateClosed, (await manager.ExecuteUninstallAsync(uninstall, uninstall.ConfirmationPhrase, default)).Outcome);

        var real = new AppsStartupManager(syntheticStartup, syntheticApps, new CountingStartupExecutor(false), new CountingUninstallExecutor(false));
        var synthetic = await real.LoadAsync(default);
        var syntheticPreview = real.PreviewStartupChange(Startup(synthetic, "Fixture Chat Client"), StartupAction.Disable);
        Assert.Equal(AppsOperationOutcome.GateClosed, (await real.ExecuteStartupChangeAsync(syntheticPreview, syntheticPreview.ConfirmationPhrase, default)).Outcome);
    }

    // ───── Inventário de inicialização ─────

    [Fact]
    public async Task StartupInventoryClassifiesEligibilityConservatively()
    {
        var (manager, _, _) = Compose();
        var snapshot = await manager.LoadAsync(default);
        Assert.True(snapshot.Complete);
        Assert.All(snapshot.Startup, e => Assert.Equal(AppsDataOrigin.Synthetic, e.Origin));

        var chat = AppsStartupPolicy.Evaluate(Startup(snapshot, "Fixture Chat Client"));
        Assert.Equal(new StartupEligibility(ChangeEligibility.Eligible, ChangeBlockReason.None, false), chat);
        Assert.True(AppsStartupPolicy.Evaluate(Startup(snapshot, "Fixture Machine Updater")).RequiresPrivilege);
        Assert.Equal(ChangeEligibility.Eligible, AppsStartupPolicy.Evaluate(Startup(snapshot, "Fixture Packaged Widget")).Eligibility);
        Assert.Equal(ChangeBlockReason.ConservativeScheduledTask, AppsStartupPolicy.Evaluate(Startup(snapshot, "Fixture Vendor Task")).Reason);
        Assert.Equal(ChangeBlockReason.NotAnApplication, AppsStartupPolicy.Evaluate(Startup(snapshot, "Fixture RunOnce Setup")).Reason);
        Assert.Equal(ChangeBlockReason.NotAnApplication, AppsStartupPolicy.Evaluate(Startup(snapshot, "Fixture Security Agent")).Reason);
        var unknown = AppsStartupPolicy.Evaluate(Startup(snapshot, "Fixture Unknown State"));
        Assert.Equal((ChangeEligibility.Unavailable, ChangeBlockReason.UnknownState), (unknown.Eligibility, unknown.Reason));
        foreach (var source in new[] { StartupSourceKind.Service, StartupSourceKind.Driver, StartupSourceKind.WindowsInternalTask, StartupSourceKind.RegistryRunOnce })
            Assert.Equal(ChangeEligibility.ReadOnly, AppsStartupPolicy.Evaluate(Startup(snapshot, "Fixture Chat Client") with { Source = source }).Eligibility);
        Assert.Equal(ChangeEligibility.ReadOnly, AppsStartupPolicy.Evaluate(Startup(snapshot, "Fixture Chat Client") with { Source = StartupSourceKind.Other }).Eligibility);
    }

    [Fact]
    public async Task ConfiguredRunningAndNotVerifiedAreDistinctAxes()
    {
        var snapshot = await Compose().Manager.LoadAsync(default);
        var running = Startup(snapshot, "Fixture Chat Client");
        var notVerified = Startup(snapshot, "Fixture Cloud Sync");
        var disabled = Startup(snapshot, "Fixture Game Launcher");
        Assert.Equal((StartupConfiguredState.Enabled, StartupRuntimeState.Running), (running.State, running.Runtime));
        Assert.Equal((StartupConfiguredState.Enabled, StartupRuntimeState.NotVerified), (notVerified.State, notVerified.Runtime));
        Assert.Equal((StartupConfiguredState.Disabled, StartupRuntimeState.NotRunning), (disabled.State, disabled.Runtime));
    }

    // ───── Alteração individual, reversão e conflito ─────

    [Fact]
    public async Task DisableThenEnableIsReversiblePreservesDataAndNeverStopsTheProcess()
    {
        var (manager, _, _) = Compose();
        var before = Startup(await manager.LoadAsync(default), "Fixture Chat Client");
        var disable = manager.PreviewStartupChange(before, StartupAction.Disable);
        Assert.StartsWith("CONFIRMAR INICIALIZACAO DESATIVAR ", disable.ConfirmationPhrase, StringComparison.Ordinal);
        var result = await manager.ExecuteStartupChangeAsync(disable, disable.ConfirmationPhrase, default);
        Assert.Equal(AppsOperationOutcome.Succeeded, result.Outcome);
        Assert.Equal(before.Reference, result.OriginalDataPreserved);

        var disabled = Startup(await manager.LoadAsync(default), "Fixture Chat Client");
        Assert.Equal(StartupConfiguredState.Disabled, disabled.State);
        Assert.Equal(before.Reference, disabled.Reference);
        Assert.Equal(StartupRuntimeState.Running, disabled.Runtime);

        var enable = manager.PreviewStartupChange(disabled, StartupAction.Enable);
        Assert.StartsWith("CONFIRMAR INICIALIZACAO REATIVAR ", enable.ConfirmationPhrase, StringComparison.Ordinal);
        Assert.Equal(AppsOperationOutcome.Succeeded, (await manager.ExecuteStartupChangeAsync(enable, enable.ConfirmationPhrase, default)).Outcome);
        Assert.Equal(before, Startup(await manager.LoadAsync(default), "Fixture Chat Client"));
        Assert.Equal([AppsOperationOutcome.Succeeded, AppsOperationOutcome.Succeeded], manager.History.Select(h => h.Outcome));
        Assert.All(manager.History, h => Assert.True(h.Synthetic));
    }

    [Fact]
    public async Task ActionsThatDoNotApplyToTheCurrentStateCannotBePrepared()
    {
        var (manager, _, _) = Compose();
        var snapshot = await manager.LoadAsync(default);
        Assert.False(manager.PreviewStartupChange(Startup(snapshot, "Fixture Chat Client"), StartupAction.Enable).CanExecute);
        Assert.False(manager.PreviewStartupChange(Startup(snapshot, "Fixture Game Launcher"), StartupAction.Disable).CanExecute);
        Assert.False(manager.PreviewStartupChange(Startup(snapshot, "Fixture Vendor Task"), StartupAction.Disable).CanExecute);
    }

    [Fact]
    public async Task WrongConfirmationOrIneligibleItemIsBlockedWithoutAnyChange()
    {
        var (manager, startup, _) = Compose();
        var snapshot = await manager.LoadAsync(default);
        var preview = manager.PreviewStartupChange(Startup(snapshot, "Fixture Chat Client"), StartupAction.Disable);
        foreach (var typed in new[] { "", "confirmar", preview.ConfirmationPhrase.ToLowerInvariant(), preview.ConfirmationPhrase + " " })
            Assert.Equal(AppsOperationOutcome.Blocked, (await manager.ExecuteStartupChangeAsync(preview, typed, default)).Outcome);
        var ineligible = manager.PreviewStartupChange(Startup(snapshot, "Fixture Vendor Task"), StartupAction.Disable);
        Assert.Equal(AppsOperationOutcome.Blocked, (await manager.ExecuteStartupChangeAsync(ineligible, ineligible.ConfirmationPhrase, default)).Outcome);
        Assert.Equal(snapshot.Startup, (await startup.ReadAsync(default)).Entries);
    }

    [Fact]
    public async Task ConcurrentChangeAfterThePreviewIsDetectedAndNothingIsOverwritten()
    {
        var (manager, startup, _) = Compose();
        var entry = Startup(await manager.LoadAsync(default), "Fixture Cloud Sync");
        var preview = manager.PreviewStartupChange(entry, StartupAction.Disable);
        startup.ChangeExternally(entry.StableId, StartupConfiguredState.Disabled);
        var result = await manager.ExecuteStartupChangeAsync(preview, preview.ConfirmationPhrase, default);
        Assert.Equal(AppsOperationOutcome.Conflict, result.Outcome);
        Assert.Equal(StartupConfiguredState.Disabled, Startup(await manager.LoadAsync(default), "Fixture Cloud Sync").State);
    }

    [Theory]
    [InlineData(AppsOperationOutcome.Failed)]
    [InlineData(AppsOperationOutcome.Indeterminate)]
    public async Task FailedAndIndeterminateResultsAreReportedAndRecordedNeverAsSuccess(AppsOperationOutcome forced)
    {
        var (manager, startup, _) = Compose();
        var entry = Startup(await manager.LoadAsync(default), "Fixture Chat Client");
        startup.InjectNextOutcome(entry.StableId, forced);
        var preview = manager.PreviewStartupChange(entry, StartupAction.Disable);
        Assert.Equal(forced, (await manager.ExecuteStartupChangeAsync(preview, preview.ConfirmationPhrase, default)).Outcome);
        Assert.Equal(forced, Assert.Single(manager.History).Outcome);
        Assert.Equal(StartupConfiguredState.Enabled, Startup(await manager.LoadAsync(default), "Fixture Chat Client").State);
    }

    [Fact]
    public async Task ExecutorExceptionBecomesIndeterminateAndTheManagerStaysUsable()
    {
        var apps = SyntheticApplicationStore.CreateDefault();
        var startup = SyntheticStartupStore.CreateDefault();
        var manager = new AppsStartupManager(startup, apps, new ThrowingStartupExecutor(), apps);
        var entry = Startup(await manager.LoadAsync(default), "Fixture Chat Client");
        var preview = manager.PreviewStartupChange(entry, StartupAction.Disable);
        Assert.Equal(AppsOperationOutcome.Indeterminate, (await manager.ExecuteStartupChangeAsync(preview, preview.ConfirmationPhrase, default)).Outcome);
        Assert.Equal(AppsOperationOutcome.Indeterminate, Assert.Single(manager.History).Outcome);
        Assert.Equal(AppsOperationOutcome.Indeterminate, (await manager.ExecuteStartupChangeAsync(preview, preview.ConfirmationPhrase, default)).Outcome);
    }

    // ───── Aplicativos instalados ─────

    [Fact]
    public async Task InstalledIdentitiesSeparateSimilarNamesScopesAndProductCodes()
    {
        var snapshot = await Compose().Manager.LoadAsync(default);
        var suites = snapshot.Apps.Where(a => a.Name == "Fixture Office Suite").ToArray();
        Assert.Equal(2, suites.Length);
        Assert.NotEqual(suites[0].StableId, suites[1].StableId);
        Assert.Equal(snapshot.Apps.Count, snapshot.Apps.Select(a => a.StableId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.NotEqual(AppsIdentity.Application(InstalledApplicationKind.Msi, AppsScope.Machine, "{A}"),
            AppsIdentity.Application(InstalledApplicationKind.Msi, AppsScope.CurrentUser, "{A}"));
        Assert.NotEqual(AppsIdentity.Startup(StartupSourceKind.RegistryRun, AppsScope.Machine, "x"),
            AppsIdentity.Startup(StartupSourceKind.StartupFolder, AppsScope.Machine, "x"));
    }

    [Fact]
    public async Task DuplicateIdentitiesAreCollapsedWithAWarningAndMarkTheInventoryIncomplete()
    {
        var entry = OperationalApp();
        var manager = new AppsStartupManager(new FixedStartup(OperationalStartup()), new FixedApps(entry, entry), new CountingStartupExecutor(false), new CountingUninstallExecutor(false));
        var snapshot = await manager.LoadAsync(default);
        Assert.Single(snapshot.Apps);
        Assert.False(snapshot.Complete);
        Assert.Contains("duplicates:1", snapshot.Warnings);
    }

    [Fact]
    public async Task UninstallEligibilityRequiresATypedReliableMechanism()
    {
        var snapshot = await Compose().Manager.LoadAsync(default);
        var msi = AppsStartupPolicy.Evaluate(App(snapshot, "Fixture Office Suite", AppsScope.Machine));
        Assert.Equal((ChangeEligibility.Eligible, UninstallMethod.MsiProductCode, true), (msi.Eligibility, msi.Method, msi.RequiresPrivilege));
        var msix = AppsStartupPolicy.Evaluate(App(snapshot, "Fixture Chat Client"));
        Assert.Equal((ChangeEligibility.Eligible, UninstallMethod.MsixPackageFullName), (msix.Eligibility, msix.Method));
        Assert.Equal(ChangeBlockReason.NoReliableUninstallMechanism, AppsStartupPolicy.Evaluate(App(snapshot, "Fixture Cloud Sync")).Reason);
        Assert.Equal(ChangeBlockReason.NoReliableUninstallMechanism, AppsStartupPolicy.Evaluate(App(snapshot, "Fixture Game")).Reason);
        Assert.Equal(ChangeBlockReason.SystemOrFramework, AppsStartupPolicy.Evaluate(App(snapshot, "Fixture Runtime Framework")).Reason);
        Assert.Equal(ChangeBlockReason.AllUsersNotSupported, AppsStartupPolicy.Evaluate(App(snapshot, "Fixture All Users Package")).Reason);
        var badGuid = App(snapshot, "Fixture Office Suite", AppsScope.Machine) with { ProductCode = "not-a-guid" };
        Assert.Equal(ChangeBlockReason.IdentityIncomplete, AppsStartupPolicy.Evaluate(badGuid).Reason);
        var noPackage = App(snapshot, "Fixture Chat Client") with { PackageFullName = " " };
        Assert.Equal(ChangeBlockReason.IdentityIncomplete, AppsStartupPolicy.Evaluate(noPackage).Reason);
    }

    [Fact]
    public async Task SyntheticUninstallRemovesOnlyTheConfirmedApplicationAndIsRecorded()
    {
        var (manager, _, _) = Compose();
        var before = await manager.LoadAsync(default);
        var target = App(before, "Fixture Chat Client");
        var preview = manager.PreviewUninstall(target);
        Assert.StartsWith("CONFIRMAR DESINSTALACAO ", preview.ConfirmationPhrase, StringComparison.Ordinal);
        Assert.Equal(AppsOperationOutcome.Blocked, (await manager.ExecuteUninstallAsync(preview, "CONFIRMAR", default)).Outcome);
        Assert.Equal(AppsOperationOutcome.Succeeded, (await manager.ExecuteUninstallAsync(preview, preview.ConfirmationPhrase, default)).Outcome);
        var after = await manager.LoadAsync(default);
        Assert.Equal(before.Apps.Where(a => a.StableId != target.StableId), after.Apps);
        Assert.Equal(before.Startup, after.Startup);
        Assert.Equal([AppsOperationOutcome.Blocked, AppsOperationOutcome.Succeeded], manager.History.Select(h => h.Outcome));
    }

    [Fact]
    public async Task UnavailableApplicationsCannotBePreparedAndConcurrentChangeIsDetected()
    {
        var (manager, _, apps) = Compose();
        var snapshot = await manager.LoadAsync(default);
        Assert.False(manager.PreviewUninstall(App(snapshot, "Fixture Cloud Sync")).CanExecute);
        Assert.False(manager.PreviewUninstall(App(snapshot, "Fixture Runtime Framework")).CanExecute);
        var target = App(snapshot, "Fixture Chat Client");
        var preview = manager.PreviewUninstall(target);
        apps.ChangeExternally(target.StableId, "9.9.9");
        Assert.Equal(AppsOperationOutcome.Conflict, (await manager.ExecuteUninstallAsync(preview, preview.ConfirmationPhrase, default)).Outcome);
        apps.InjectNextOutcome(target.StableId, AppsOperationOutcome.Indeterminate);
        var again = manager.PreviewUninstall(App(await manager.LoadAsync(default), "Fixture Chat Client"));
        Assert.Equal(AppsOperationOutcome.Indeterminate, (await manager.ExecuteUninstallAsync(again, again.ConfirmationPhrase, default)).Outcome);
    }

    // ───── Filtros ─────

    [Fact]
    public async Task SearchAndFiltersCombine()
    {
        var snapshot = await Compose().Manager.LoadAsync(default);
        Assert.Single(AppsStartupFilters.Filter(snapshot.Startup, "game", null, null));
        Assert.Equal(snapshot.Startup.Count, AppsStartupFilters.Filter(snapshot.Startup, "  ", null, null).Count);
        Assert.All(AppsStartupFilters.Filter(snapshot.Startup, null, StartupSourceKind.RegistryRun, StartupConfiguredState.Enabled),
            e => Assert.Equal((StartupSourceKind.RegistryRun, StartupConfiguredState.Enabled), (e.Source, e.State)));
        Assert.Empty(AppsStartupFilters.Filter(snapshot.Startup, "no-such-app", null, null));
        Assert.Equal(2, AppsStartupFilters.Filter(snapshot.Apps, "office", null, null).Count);
        Assert.Single(AppsStartupFilters.Filter(snapshot.Apps, "office", InstalledApplicationKind.Msi, AppsScope.CurrentUser));
        Assert.All(AppsStartupFilters.Filter(snapshot.Apps, null, InstalledApplicationKind.Msix, null), a => Assert.Equal(InstalledApplicationKind.Msix, a.Kind));
    }

    // ───── Sem lote / sem comando arbitrário ─────

    [Fact]
    public void ContractsAcceptASingleTargetAndHaveNoBatchEntryPoint()
    {
        var methods = typeof(AppsStartupManager).GetMethods().Concat(typeof(IStartupChangeExecutor).GetMethods())
            .Concat(typeof(IApplicationUninstallExecutor).GetMethods())
            .Where(m => m.Name.StartsWith("Execute", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, methods.Length);
        foreach (var method in methods)
            Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(p.ParameterType));
        foreach (var request in new[] { typeof(StartupChangeRequest), typeof(UninstallRequest), typeof(StartupChangePreview), typeof(UninstallPreview) })
            Assert.DoesNotContain(request.GetProperties(), p => p.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(p.PropertyType));
    }

    // ───── Provedores operacionais (somente leitura, sobre uma fonte falsa) ─────

    [Fact]
    public async Task RunKeyProviderReadsRunValuesAndStartupApprovedStateWithoutAnyWrite()
    {
        var source = new FakeRegistry();
        source.Values[(AppsScope.CurrentUser, RunKeyStartupProvider.RunPath)] = new Dictionary<string, object?>
            { ["A"] = @"C:\a.exe", ["B"] = @"C:\b.exe", ["C"] = @"C:\c.exe", ["Bad"] = 5 };
        source.Values[(AppsScope.CurrentUser, RunKeyStartupProvider.ApprovedPath)] = new Dictionary<string, object?>
            { ["B"] = new byte[] { 3, 0, 0, 0 }, ["C"] = new byte[] { 2, 0, 0, 0 } };
        source.Values[(AppsScope.Machine, RunKeyStartupProvider.RunPath)] = new Dictionary<string, object?> { ["M"] = @"C:\m.exe" };
        var result = await new RunKeyStartupProvider(source).ReadAsync(default);
        Assert.Equal(StartupConfiguredState.Enabled, result.Entries.Single(e => e.Name == "A" && e.Scope == AppsScope.CurrentUser).State);
        Assert.Equal(StartupConfiguredState.Disabled, result.Entries.Single(e => e.Name == "B").State);
        Assert.Equal(StartupConfiguredState.Enabled, result.Entries.Single(e => e.Name == "C").State);
        Assert.Equal(StartupConfiguredState.Unknown, result.Entries.Single(e => e.Name == "M").State); // sem StartupApproved legível
        Assert.All(result.Entries, e => Assert.Equal((AppsDataOrigin.Operational, StartupRuntimeState.NotVerified), (e.Origin, e.Runtime)));
        Assert.DoesNotContain(result.Entries, e => e.Name == "Bad");
        Assert.Contains("malformed:Bad", result.Warnings);
        Assert.False(result.Complete);
        Assert.Equal(source.Reads.Distinct().Count(), source.Reads.Count); // somente as quatro leituras previstas, cada uma uma vez
    }

    [Fact]
    public async Task InaccessibleRegistryIsAWarningNeverAnEmptyCompleteInventory()
    {
        var run = await new RunKeyStartupProvider(new FakeRegistry()).ReadAsync(default);
        Assert.False(run.Complete);
        Assert.Empty(run.Entries);
        Assert.Equal(2, run.Warnings.Count);
        var apps = await new UninstallRegistryApplicationProvider(new FakeRegistry()).ReadAsync(default);
        Assert.False(apps.Complete);
        Assert.Equal(3, apps.Warnings.Count);
    }

    [Fact]
    public async Task UninstallProviderRecognizesMsiOnlyByGuidKeyAndNeverReadsUninstallString()
    {
        var msi = "{33333333-3333-3333-3333-333333333333}";
        var source = new FakeRegistry();
        var values = new TrackingValues(new Dictionary<string, object?>
            { ["DisplayName"] = "Product M", ["WindowsInstaller"] = 1, ["UninstallString"] = "cmd /c calc.exe", ["QuietUninstallString"] = "x" });
        source.Keys[(AppsScope.Machine, UninstallRegistryApplicationProvider.UninstallPath)] =
        [
            new(msi, values),
            new("PlainWin32", new Dictionary<string, object?> { ["DisplayName"] = "Plain", ["UninstallString"] = "x" }),
            new("Hidden", new Dictionary<string, object?> { ["DisplayName"] = "Hidden", ["SystemComponent"] = 1 }),
            new("{44444444-4444-4444-4444-444444444444}", new Dictionary<string, object?> { ["DisplayName"] = "No installer flag" }),
            new("NoName", new Dictionary<string, object?>())
        ];
        source.Keys[(AppsScope.CurrentUser, UninstallRegistryApplicationProvider.UninstallPath)] = [];
        source.Keys[(AppsScope.Machine, UninstallRegistryApplicationProvider.UninstallPathWow)] =
            [new("PlainWin32", new Dictionary<string, object?> { ["DisplayName"] = "Plain 32-bit" })];
        var result = await new UninstallRegistryApplicationProvider(source).ReadAsync(default);
        Assert.True(result.Complete == false && result.Warnings.Contains("no-display-name:NoName"));
        var product = result.Entries.Single(a => a.Name == "Product M");
        Assert.Equal((InstalledApplicationKind.Msi, msi), (product.Kind, product.ProductCode));
        Assert.All(result.Entries.Where(a => a.Name != "Product M"), a => Assert.Null(a.ProductCode));
        Assert.True(result.Entries.Single(a => a.Name == "Hidden").IsSystemOrFramework);
        Assert.Equal(InstalledApplicationKind.Win32Registry, result.Entries.Single(a => a.Name == "No installer flag").Kind);
        Assert.NotEqual(result.Entries.Single(a => a.Name == "Plain").StableId, result.Entries.Single(a => a.Name == "Plain 32-bit").StableId);
        Assert.DoesNotContain("UninstallString", values.Requested);
        Assert.DoesNotContain("QuietUninstallString", values.Requested);
        Assert.All(result.Entries, a => Assert.Equal(AppsDataOrigin.Operational, a.Origin));
        // Contexto máquina não é suportado pelo executor individual; identidade MSI permanece disponível na leitura.
        Assert.Equal(ChangeEligibility.ReadOnly, AppsStartupPolicy.Evaluate(product).Eligibility);
        Assert.Equal(UninstallMethod.None, AppsStartupPolicy.Evaluate(product).Method);
        Assert.Equal(UninstallMethod.None, AppsStartupPolicy.Evaluate(result.Entries.Single(a => a.Name == "Plain")).Method);
    }

    // ───── Composição, navegação e contratos estáticos ─────

    [Fact]
    public void DistributionComposesOnlySyntheticImplementationsAndOperationalExecutorsStayGated()
    {
        var project = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.Infrastructure.AppsStartup", "SentinelCore.Infrastructure.AppsStartup.csproj"));
        Assert.Contains("SentinelCore.Domain", project, StringComparison.Ordinal);
        Assert.DoesNotContain("SentinelCore.Application", project, StringComparison.Ordinal);
        var implementers = typeof(SyntheticStartupStore).Assembly.GetTypes()
            .Where(t => !t.IsInterface && (typeof(IStartupChangeExecutor).IsAssignableFrom(t) || typeof(IApplicationUninstallExecutor).IsAssignableFrom(t)))
            .Select(t => t.Name).Order().ToArray();
        Assert.Equal(["SyntheticApplicationStore", "SyntheticStartupStore"], implementers);
        Assert.True(new SyntheticStartupStore([]).IsSynthetic && new SyntheticApplicationStore([]).IsSynthetic);
        var app = TestRepository.AppSource("App.xaml.cs");
        Assert.Contains("SyntheticStartupStore.CreateDefault()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("RunKeyStartupProvider", app, StringComparison.Ordinal);
        Assert.DoesNotContain("UninstallRegistryApplicationProvider", app, StringComparison.Ordinal);
        foreach (var executor in new[] { "RunStartupChangeExecutor", "MsiApplicationUninstallExecutor", "MsixApplicationUninstallExecutor",
                     "WindowsRunStartupAccess", "WindowsMsiUninstallApi", "WindowsMsixUninstallApi", "WindowsAppsSecretProtector" })
            Assert.DoesNotContain("new " + executor, app, StringComparison.Ordinal);
        Assert.Contains("operationStore: store", app, StringComparison.Ordinal);
        foreach (var file in new[] { "AppsStartupView.xaml.cs", "AppsStartupText.cs", "MainWindow.AppsStartup.cs" })
            foreach (var forbidden in new[] { "Process.Start", "msiexec", "winget", "Remove-AppxPackage", "Win32_Product", "Registry.", "System.Management",
                         "PackageManager", "ShellExecute", "powershell" })
                Assert.DoesNotContain(forbidden, TestRepository.AppSource(file), StringComparison.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(TestRepository.Combine("src", "SentinelCore.Infrastructure.AppsStartup"), "*.cs"))
        {
            foreach (var forbidden in new[] { "Process.Start", "System.Management", "File.Delete", "Directory.Delete" })
                Assert.DoesNotContain(forbidden, File.ReadAllText(file), StringComparison.Ordinal);
            if (Path.GetFileName(file) is not ("WindowsAppsRegistrySource.cs" or "WindowsRunStartupAccess.cs"))
                Assert.DoesNotContain("Microsoft.Win32", File.ReadAllText(file), StringComparison.Ordinal);
        }
        foreach (var native in new[] { "WindowsRunStartupAccess.cs", "WindowsMsiUninstallApi.cs" })
        {
            var source = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.Infrastructure.AppsStartup", native));
            Assert.Contains("!AppsOperationalExecutionGate.PermitsExecution", source, StringComparison.Ordinal);
            Assert.DoesNotContain("GetCommandLineArgs", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NavigationReplacesDebloatWithTheNewModuleAndTheTwoTabs()
    {
        var shell = TestRepository.AppSource("MainWindow.xaml");
        Assert.DoesNotContain("Debloat", shell, StringComparison.OrdinalIgnoreCase);
        // Personalização saiu da experiência ativa: nenhum menu ou página com esses nomes.
        Assert.DoesNotContain("x:Name=\"TweaksNav\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"TweaksTab\"", shell, StringComparison.Ordinal);
        Assert.True(shell.IndexOf("x:Name=\"AppsStartupNav\"", StringComparison.Ordinal) < shell.IndexOf("x:Name=\"RepairsNav\"", StringComparison.Ordinal));
        Assert.True(shell.IndexOf("x:Name=\"AppsStartupTab\"", StringComparison.Ordinal) < shell.IndexOf("x:Name=\"RepairsTab\"", StringComparison.Ordinal));
        var document = XDocument.Parse(shell);
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var nav = document.Descendants(wpf + "ListBox").Single(e => (string?)e.Attribute(xaml + "Name") == "NavList").Elements(wpf + "ListBoxItem").ToList();
        var pages = document.Descendants(wpf + "TabControl").Single(e => (string?)e.Attribute(xaml + "Name") == "Pages").Elements(wpf + "TabItem").ToList();
        Assert.Equal(nav.Count, pages.Count);
        var navNames = nav.Select(e => (string?)e.Attribute(xaml + "Name")).Where(n => n is not null).Select(n => n![..^3]).ToArray();
        var pageNames = pages.Select(e => (string?)e.Attribute(xaml + "Name")).Where(n => n is not null).Select(n => n![..^3]).ToArray();
        Assert.Equal(navNames, pageNames.Where(navNames.Contains));
        // Redesign: o novo módulo é alcançado pelo menu lateral; a Visão geral não tem mais grade de atalhos.
        Assert.DoesNotContain(document.Descendants(wpf + "Button"), b => (string?)b.Attribute("Click") == "OnNavigate");
    }

    [Fact]
    public void ViewExposesTwoTabsAndStartsWithEveryExecutionControlDisabledOrHidden()
    {
        var document = XDocument.Parse(TestRepository.AppSource("AppsStartupView.xaml"));
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        string? Name(XElement e) => (string?)e.Attribute(xaml + "Name");
        Assert.Equal(["StartupTab", "AppsTab"], document.Descendants(wpf + "TabItem").Select(Name));
        foreach (var button in new[] { "StartupExecuteButton", "UninstallExecuteButton", "PreviewDisableButton", "PreviewEnableButton", "PreviewUninstallButton" })
            Assert.Equal("False", (string?)document.Descendants(wpf + "Button").Single(b => Name(b) == button).Attribute("IsEnabled"));
        foreach (var panel in new[] { "StartupConfirmPanel", "UninstallConfirmPanel" })
            Assert.Equal("Collapsed", (string?)document.Descendants(wpf + "WrapPanel").Single(p => Name(p) == panel).Attribute("Visibility"));
        foreach (var box in new[] { "StartupConfirmBox", "UninstallConfirmBox" })
            Assert.Null(document.Descendants(wpf + "TextBox").Single(t => Name(t) == box).Attribute("IsReadOnly"));
        foreach (var search in new[] { "StartupSearch", "AppsSearch" })
            Assert.Contains(document.Descendants(wpf + "TextBox"), t => Name(t) == search);
        foreach (var filter in new[] { "StartupSourceCombo", "StartupStateCombo", "AppsKindCombo", "AppsScopeCombo" })
            Assert.Contains(document.Descendants(wpf + "ComboBox"), c => Name(c) == filter);
        Assert.Contains(document.Descendants(wpf + "TextBlock"), t => Name(t) == "GateText");
        var code = TestRepository.AppSource("AppsStartupView.xaml.cs");
        Assert.Contains("MessageBoxButton.YesNo", code, StringComparison.Ordinal);
        Assert.Contains("MessageBoxResult.No", code, StringComparison.Ordinal);
        Assert.Contains("string.Equals(StartupConfirmBox.Text, sp.ConfirmationPhrase, StringComparison.Ordinal)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void PageIsComposedOnlyAfterItIsSelectedAndNothingIsReadAtStartup()
    {
        var code = TestRepository.AppSource("MainWindow.AppsStartup.cs");
        Assert.Contains("ReferenceEquals(Pages.SelectedItem, AppsStartupTab)", code, StringComparison.Ordinal);
        Assert.Contains("AppsStartupSection.Initialize(manager)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAsync", TestRepository.AppSource("MainWindow.xaml.cs"), StringComparison.Ordinal);
        var view = TestRepository.AppSource("AppsStartupView.xaml.cs");
        Assert.Contains("if (!loaded && manager is not null && !busy) await RefreshAsync();", view, StringComparison.Ordinal);
    }

    [Fact]
    public void NewTextsAreCatalogedInBothLanguagesAndExistingIndicesAreStable()
    {
        Localizer.Use(AppLanguage.English);
        try
        {
            var all = Strings.Entries.Select(e => e.Pt).ToArray();
            foreach (var (pt, en) in AppsStartupText.Entries)
            {
                Assert.Contains(pt, all);
                Assert.False(string.IsNullOrWhiteSpace(en), pt);
                Assert.Equal(Strings.Entries[Array.FindIndex(Strings.Entries, e => e.Pt == pt)].En, Strings.English(pt));
            }
            // Os índices T### de MainWindow.xaml para o módulo novo são fixos: a tabela base não pode ser reordenada à frente deles.
            Assert.Equal(["T2050", "T2051", "T2052", "T2053", "T2054"], new[] { AppsStartupText.Title, AppsStartupText.NavTooltip,
                AppsStartupText.Subtitle, AppsStartupText.OpenTitle, AppsStartupText.OpenPersonalization }.Select(Strings.Key));
            var title = AppsStartupText.Title;
            Assert.Equal(title, Strings.Entries[int.Parse(Strings.Key(title)[1..])].Pt);
            Assert.Equal("Apps and Startup", Strings.English(title));
            Assert.Equal("Personalização", Strings.Entries[354].Pt);
            // Todo rótulo de enum usado pela interface está catalogado.
            var labels = Enum.GetValues<StartupSourceKind>().Select(AppsStartupText.Source)
                .Concat(Enum.GetValues<AppsScope>().Select(AppsStartupText.Scope))
                .Concat(Enum.GetValues<StartupConfiguredState>().Select(AppsStartupText.State))
                .Concat(Enum.GetValues<StartupRuntimeState>().Select(AppsStartupText.Runtime))
                .Concat(Enum.GetValues<ChangeEligibility>().Select(AppsStartupText.Eligibility))
                .Concat(Enum.GetValues<InstalledApplicationKind>().Select(AppsStartupText.Kind))
                .Concat(Enum.GetValues<UninstallMethod>().Select(AppsStartupText.Method))
                .Concat(Enum.GetValues<ChangeBlockReason>().Select(AppsStartupText.Reason))
                .Concat(Enum.GetValues<AppsOperationOutcome>().Select(AppsStartupText.Outcome));
            Assert.All(labels, label => Assert.Contains(label, all));
        }
        finally { Localizer.Use(AppLanguage.English); }
    }

    // ───── Compatibilidade com o legado Debloat ─────

    [Fact]
    public void LegacyDebloatContractsHistoryAndGatesRemainAvailableButUnreachableFromTheApp()
    {
        Assert.NotNull(typeof(SentinelCore.Persistence.MemoryStore).GetMethod("CreateDebloatJournal"));
        Assert.NotNull(typeof(DebloatManager));
        Assert.NotNull(typeof(DebloatRemovalPresentation));
        Assert.True(Enum.IsDefined(DebloatInventoryOrigin.Synthetic));
        Assert.False(RealDebloatBuildGate.AllowsRealExecution(["--enable-real-debloat"]));
        Assert.False(NativeDebloatInventoryGate.Operational);
        var app = TestRepository.AppSource("App.xaml.cs");
        Assert.DoesNotContain("DebloatManager", app, StringComparison.Ordinal);
        var project = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.App", "SentinelCore.App.csproj"));
        Assert.DoesNotContain("Debloat", project, StringComparison.Ordinal);
        // O Centro de Recuperação continua lendo o histórico Debloat persistido.
        Assert.Contains("Debloat", File.ReadAllText(TestRepository.Combine("src", "SentinelCore.Application", "RecoveryCenter.cs")), StringComparison.Ordinal);
    }

    // ───── Fakes ─────

    private static StartupEntry OperationalStartup() =>
        new(AppsIdentity.Startup(StartupSourceKind.RegistryRun, AppsScope.CurrentUser,
                @"CurrentUser|Software\Microsoft\Windows\CurrentVersion\Run|Registry64|Real"), "Real", null,
            StartupSourceKind.RegistryRun, @"C:\real.exe", AppsScope.CurrentUser, StartupConfiguredState.Enabled,
            StartupRuntimeState.NotVerified, "Run", AppsDataOrigin.Operational)
            { RegistryIdentity = new("CurrentUser", RunKeyStartupProvider.RunPath, "Registry64", "Real", "String") };

    private static InstalledApplicationEntry OperationalApp() =>
        new(AppsIdentity.Application(InstalledApplicationKind.Msi, AppsScope.CurrentUser, "{55555555-5555-5555-5555-555555555555}"),
            "Real App", null, "1", InstalledApplicationKind.Msi, "Uninstall", AppsScope.CurrentUser, null,
            "{55555555-5555-5555-5555-555555555555}", null, false, AppsDataOrigin.Operational);

    private sealed class FixedStartup(params StartupEntry[] entries) : IStartupInventoryProvider
    {
        public Task<StartupInventoryResult> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StartupInventoryResult(entries, [], true));
    }

    private sealed class FixedApps(params InstalledApplicationEntry[] entries) : IInstalledApplicationProvider
    {
        public Task<InstalledApplicationInventoryResult> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new InstalledApplicationInventoryResult(entries, [], true));
    }

    private sealed class CountingStartupExecutor(bool synthetic) : IStartupChangeExecutor
    {
        public int Calls { get; private set; }
        public bool IsSynthetic => synthetic;
        public Task<StartupChangeResult> ExecuteAsync(StartupChangeRequest request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new StartupChangeResult(AppsOperationOutcome.Succeeded, "x", null)); }
    }

    private sealed class CountingUninstallExecutor(bool synthetic) : IApplicationUninstallExecutor
    {
        public int Calls { get; private set; }
        public bool IsSynthetic => synthetic;
        public Task<UninstallResult> ExecuteAsync(UninstallRequest request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new UninstallResult(AppsOperationOutcome.Succeeded, "x")); }
    }

    private sealed class ThrowingStartupExecutor : IStartupChangeExecutor
    {
        public bool IsSynthetic => true;
        public Task<StartupChangeResult> ExecuteAsync(StartupChangeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("fixture failure");
    }

    private sealed class TrackingValues(Dictionary<string, object?> inner) : IReadOnlyDictionary<string, object?>
    {
        public List<string> Requested { get; } = [];
        public object? this[string key] { get { Requested.Add(key); return inner[key]; } }
        public IEnumerable<string> Keys => inner.Keys;
        public IEnumerable<object?> Values => inner.Values;
        public int Count => inner.Count;
        public bool ContainsKey(string key) { Requested.Add(key); return inner.ContainsKey(key); }
        public bool TryGetValue(string key, out object? value) { Requested.Add(key); return inner.TryGetValue(key, out value); }
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class FakeRegistry : IAppsRegistrySource
    {
        public Dictionary<(AppsScope, string), IReadOnlyDictionary<string, object?>> Values { get; } = [];
        public Dictionary<(AppsScope, string), IReadOnlyList<RegistryKeySnapshot>> Keys { get; } = [];
        public List<(AppsScope, string)> Reads { get; } = [];

        public IReadOnlyList<RegistryKeySnapshot>? ReadSubKeys(AppsScope scope, string path)
        { Reads.Add((scope, path)); return Keys.GetValueOrDefault((scope, path)); }

        public IReadOnlyDictionary<string, object?>? ReadValues(AppsScope scope, string path)
        { Reads.Add((scope, path)); return Values.GetValueOrDefault((scope, path)); }
    }
}
