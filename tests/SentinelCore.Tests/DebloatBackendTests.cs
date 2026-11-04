using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Debloat;
using SentinelCore.Infrastructure.Modifications;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class DebloatBackendTests
{
    [Fact]
    public void DocumentaryCatalogIsClosedAndRealRiskRemainsBlocked()
    {
        var catalog = DebloatCatalog.CreateDefault();
        Assert.Equal(162, catalog.All.Count);
        Assert.Equal(159, catalog.All.Count(e => e.RemovalMethod == "Appx"));
        Assert.Equal(3, catalog.All.Count(e => e.RemovalMethod == "WinGet"));
        Assert.Equal(86, catalog.All.Count(e => e.PreliminaryRisk == RiskLevel.Low));
        Assert.Equal(67, catalog.All.Count(e => e.PreliminaryRisk == RiskLevel.Medium));
        Assert.Equal(9, catalog.All.Count(e => e.PreliminaryRisk == RiskLevel.High));
        Assert.Equal(153, catalog.All.Count(e => e.RealRisk == RiskLevel.NotValidated));
        Assert.Equal(9, catalog.All.Count(e => e.RealRisk == RiskLevel.High));
        Assert.Equal(162, catalog.All.Select(e => e.Id).Distinct().Count());
        Assert.Throws<InvalidDataException>(() => new DebloatCatalog(catalog.All.Skip(1)));
        Assert.Throws<InvalidDataException>(() => new DebloatCatalog(catalog.All.Skip(1).Append(catalog.All[1])));
    }

    [Theory]
    [InlineData("--enable-real-debloat")]
    [InlineData("SentinelCoreRealDebloat=true")]
    [InlineData("")]
    public void RealGateCannotBeEnabled(string argument)
    {
        Assert.False(RealDebloatBuildGate.RealExecutionImplemented);
        Assert.False(RealDebloatBuildGate.AllowsRealExecution([argument]));
    }

    [Fact]
    public async Task SyntheticRemovalRunsThroughSharedSafetyJournalAndMetadataOnly()
    {
        using var scope = new Scope();
        var plan = await scope.Prepare();
        Assert.Single(plan.Simulation.Plan.Changes);
        Assert.Equal(OperationTargetRoots.Synthetic, plan.Simulation.Plan.Changes[0].Target.Root);
        Assert.Contains(scope.Package.PackageFullName, plan.Simulation.Plan.Changes[0].Before.Data!);
        Assert.Contains(scope.Package.UserId, plan.Simulation.Plan.Changes[0].Before.Data!);
        Assert.Contains("1.0.0.0", plan.Simulation.Plan.Changes[0].Before.Data!);
        var token = scope.Manager.Authorize(plan, plan.Confirmation);
        var result = await scope.Manager.ExecuteAsync(plan, token);
        Assert.True(result.Succeeded, result.Summary);
        Assert.True(result.Synthetic);
        Assert.Equal(DebloatInstallState.RemovedSynthetic, result.CurrentPackage!.State);
        var record = Assert.IsType<TransactionRecord>(result.Transaction.Transaction);
        Assert.True(record.Events.FindIndex(e => e.State == TransactionState.ExecutionStarted) >
            record.Events.FindIndex(e => e.State == TransactionState.Authorized));
        Assert.NotNull(record.Backup);
        Assert.DoesNotContain((await scope.Store.LoadTransactionsAsync()).Transactions, e => e.OperationId.StartsWith("DBLT-"));
        var history = Assert.Single(await scope.Manager.HistoryAsync());
        Assert.Contains("SINTÉTICO", history.State);
        Assert.Equal(scope.Package.PackageFullName, history.PackageFullName);
        Assert.Contains("Reinstalação não equivale", history.Limitations);
        Assert.True(File.Exists(history.ReportPath));
        var report = await File.ReadAllTextAsync(history.ReportPath);
        Assert.Contains("SINTÉTIC", report, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(scope.Root, "memory", "transactions", record.TransactionId.ToString("D"), "backup-01.md")));
        var reopened = scope.CreateManager();
        Assert.Single(await reopened.HistoryAsync());
    }

    [Theory]
    [InlineData(DebloatScenario.AccessDenied, BlockerCode.ExecutionFailed)]
    [InlineData(DebloatScenario.InUse, BlockerCode.ExecutionFailed)]
    [InlineData(DebloatScenario.UpdatedBeforeExecution, BlockerCode.StateChanged)]
    [InlineData(DebloatScenario.PartialFailure, BlockerCode.IndeterminateState)]
    [InlineData(DebloatScenario.Indeterminate, BlockerCode.IndeterminateState)]
    [InlineData(DebloatScenario.Unverifiable, BlockerCode.VerificationFailed)]
    [InlineData(DebloatScenario.Interrupted, BlockerCode.IndeterminateState)]
    [InlineData(DebloatScenario.Cancelled, BlockerCode.IndeterminateState)]
    [InlineData(DebloatScenario.PersistenceUnavailable, BlockerCode.JournalUnavailable)]
    public async Task FailureScenariosNeverClaimSuccessfulRemoval(DebloatScenario scenario, BlockerCode blocker)
    {
        using var scope = new Scope();
        var plan = await scope.Prepare(scenario);
        var result = await scope.Manager.ExecuteAsync(plan, scope.Manager.Authorize(plan, plan.Confirmation));
        Assert.False(result.Succeeded);
        Assert.Contains(blocker, result.Transaction.Blockers);
        Assert.True(result.Synthetic);
        if (scenario == DebloatScenario.PersistenceUnavailable)
        {
            Assert.Equal(DebloatInstallState.Installed, result.CurrentPackage!.State);
            Assert.Empty(await scope.Manager.HistoryAsync());
        }
    }

    [Theory]
    [InlineData(DebloatScenario.Protected)]
    [InlineData(DebloatScenario.DependencyDetected)]
    [InlineData(DebloatScenario.IdentityMismatch)]
    [InlineData(DebloatScenario.NotFound)]
    [InlineData((DebloatScenario)999)]
    public async Task BlockedScenariosCannotObtainPlans(DebloatScenario scenario)
    {
        using var scope = new Scope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Prepare(scenario));
        Assert.Empty((await scope.Journal.LoadTransactionsAsync()).Transactions);
    }

    [Fact]
    public async Task IdentityUpdateAfterInventoryInvalidatesAuthorization()
    {
        using var scope = new Scope();
        var plan = await scope.Prepare();
        var token = scope.Manager.Authorize(plan, plan.Confirmation);
        scope.Inventory.Set(scope.Package with { Publisher = "CN=Changed" });
        var result = await scope.Manager.ExecuteAsync(plan, token);
        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.StateChanged, result.Transaction.Blockers);
        Assert.Equal(DebloatInstallState.Installed, result.CurrentPackage!.State);
    }

    [Fact]
    public async Task ConfirmationTamperAndRealModeAreRejectedBeforeEffects()
    {
        using var scope = new Scope();
        var plan = await scope.Prepare();
        Assert.Throws<InvalidOperationException>(() => scope.Manager.Authorize(plan, "CONFIRMAR"));
        Assert.Throws<InvalidOperationException>(() => scope.Manager.Authorize(plan with { Scenario = DebloatScenario.AccessDenied }, plan.Confirmation));
        Assert.Throws<InvalidOperationException>(() => scope.Manager.Authorize(plan with { Package = plan.Package with { Version = "2.0.0.0" } }, plan.Confirmation));
        Assert.Throws<InvalidOperationException>(() => scope.Manager.Authorize(plan with { Id = Guid.NewGuid() }, plan.Confirmation));
        var token = scope.Manager.Authorize(plan, plan.Confirmation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Manager.ExecuteAsync(plan, token, DebloatExecutionMode.Real));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Manager.ExecuteAsync(plan, token, (DebloatExecutionMode)99));
        Assert.Empty((await scope.Journal.LoadTransactionsAsync()).Transactions);
        Assert.Equal(DebloatInstallState.Installed, (await scope.Inventory.ReadAsync())[0].State);
    }

    [Fact]
    public async Task TokensExpireAndCannotCrossPackagesOrSessions()
    {
        using var scope = new Scope();
        var plan = await scope.Prepare();
        var token = scope.Manager.Authorize(plan, plan.Confirmation);
        scope.Time.Now += TimeSpan.FromMinutes(3);
        var expired = await scope.Manager.ExecuteAsync(plan, token);
        Assert.Contains(BlockerCode.AuthorizationExpired, expired.Transaction.Blockers);
        Assert.False(expired.Succeeded);
        var next = await scope.Prepare();
        var crossed = await scope.Manager.ExecuteAsync(next, token);
        Assert.Contains(BlockerCode.AuthorizationMissing, crossed.Transaction.Blockers);
        Assert.Throws<InvalidOperationException>(() => scope.CreateManager().Authorize(next, next.Confirmation));
    }

    [Fact]
    public async Task AuthorizationCannotBeReused()
    {
        using var scope = new Scope();
        var plan = await scope.Prepare();
        var token = scope.Manager.Authorize(plan, plan.Confirmation);
        Assert.True((await scope.Manager.ExecuteAsync(plan, token)).Succeeded);
        var second = await scope.Manager.ExecuteAsync(plan, token);
        Assert.False(second.Succeeded);
        Assert.Contains(BlockerCode.AuthorizationReused, second.Transaction.Blockers);
        Assert.Single(await scope.Manager.HistoryAsync());
    }

    [Fact]
    public async Task MediumRiskRequiresSeparateReviewBeforeSpecificConfirmation()
    {
        using var scope = new Scope("Microsoft.WindowsNotepad");
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Prepare());
        var plan = await scope.Manager.PrepareAsync(scope.Package.CatalogId, mediumReviewConfirmed: true);
        Assert.Equal(RiskLevel.Medium, plan.Risk);
        Assert.True((await scope.Manager.ExecuteAsync(plan, scope.Manager.Authorize(plan, plan.Confirmation))).Succeeded);
    }

    [Theory]
    [InlineData("unknown-dependencies")]
    [InlineData("uninvestigated")]
    [InlineData("critical-dependency")]
    [InlineData("shared-dependency")]
    [InlineData("protected")]
    [InlineData("system")]
    [InlineData("framework")]
    [InlineData("resource")]
    [InlineData("other-user")]
    [InlineData("provisioned")]
    [InlineData("all-users")]
    [InlineData("win32")]
    [InlineData("absent")]
    [InlineData("publisher")]
    [InlineData("version")]
    [InlineData("family")]
    [InlineData("wildcard")]
    [InlineData("not-synthetic")]
    [InlineData("unknown-enum")]
    public async Task InventoryBlockersAreEnforcedInBackend(string mutation)
    {
        using var scope = new Scope();
        var p = scope.Package;
        p = mutation switch
        {
            "unknown-dependencies" => p with { DependencyStatus = DebloatDependencyStatus.Unknown },
            "uninvestigated" => p with { DependencyStatus = DebloatDependencyStatus.NotInvestigated },
            "critical-dependency" => p with { DependencyStatus = DebloatDependencyStatus.Known, Dependencies = [new("Critical", true, false)] },
            "shared-dependency" => p with { DependencyStatus = DebloatDependencyStatus.Known, Dependencies = [new("Shared", false, true)] },
            "protected" => p with { Protected = true }, "system" => p with { SystemPackage = true },
            "framework" => p with { Kind = DebloatPackageKind.Framework }, "resource" => p with { Kind = DebloatPackageKind.Resource },
            "other-user" => p with { Scope = DebloatScope.OtherUser, UserId = "OtherUser" },
            "provisioned" => p with { Scope = DebloatScope.Provisioned, Provisioned = true },
            "all-users" => p with { Scope = DebloatScope.AllUsers }, "win32" => p with { Kind = DebloatPackageKind.Win32 },
            "absent" => p with { State = DebloatInstallState.NotFound }, "publisher" => p with { Publisher = "CN=Spoofed" },
            "version" => p with { Version = "2.0.0.0" }, "family" => p with { PackageFamilyName = "Another_family" },
            "wildcard" => p with { TechnicalName = "*Clipchamp*" }, "not-synthetic" => p with { Synthetic = false },
            _ => p with { State = (DebloatInstallState)999 }
        };
        scope.Inventory.Set(p);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Prepare());
        Assert.Empty((await scope.Journal.LoadTransactionsAsync()).Transactions);
    }

    [Fact]
    public async Task DuplicatePackagesAndVersionsAreAmbiguous()
    {
        using var scope = new Scope();
        scope.Inventory.Replace([scope.Package, scope.Package with { Version = "2.0.0.0" }]);
        var all = await scope.Manager.LoadAsync();
        Assert.All(all.Where(p => p.Package.CatalogId == scope.Package.CatalogId), p => Assert.False(p.CanSimulate));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Prepare());
    }

    [Fact]
    public async Task AmbiguityAddedAfterAuthorizationBlocksExecution()
    {
        using var scope = new Scope();
        var plan = await scope.Prepare();
        var token = scope.Manager.Authorize(plan, plan.Confirmation);
        scope.Inventory.Replace([scope.Package, scope.Package with { CatalogId = "SC-APP-999", Version = "2.0.0.0",
            PackageFullName = $"{scope.Package.TechnicalName}_2.0.0.0_x64__synthetic" }]);
        var result = await scope.Manager.ExecuteAsync(plan, token);
        Assert.False(result.Succeeded);
        Assert.DoesNotContain(result.Transaction.Transaction?.Events ?? [], e => e.State == TransactionState.ExecutionStarted);
        Assert.All(await scope.Inventory.ReadAsync(), p => Assert.Equal(DebloatInstallState.Installed, p.State));
    }

    [Fact]
    public async Task ReverseDependencyAddedAfterAuthorizationBlocksExecution()
    {
        using var scope = new Scope();
        var plan = await scope.Prepare();
        var token = scope.Manager.Authorize(plan, plan.Confirmation);
        var dependent = scope.Package with { CatalogId = "SC-APP-999", TechnicalName = "Fixture.Dependent",
            PackageFamilyName = "Fixture.Dependent_synthetic", Dependencies = [new(scope.Package.PackageFamilyName, false, false)] };
        scope.Inventory.Replace([scope.Package, dependent]);
        Assert.False((await scope.Manager.ExecuteAsync(plan, token)).Succeeded);
    }

    [Fact]
    public async Task RebuiltDatabaseWarningAndHistoryComeFromTheSameSnapshot()
    {
        using var scope = new Scope();
        var plan = await scope.Prepare();
        Assert.True((await scope.Manager.ExecuteAsync(plan, scope.Manager.Authorize(plan, plan.Confirmation))).Succeeded);
        await File.WriteAllTextAsync(Path.Combine(scope.Root, "data", "sentinelcore.db"), "corrupt fixture");
        var history = await scope.Manager.LoadHistoryAsync();
        Assert.Single(history.Items);
        Assert.NotEmpty(history.Warnings);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(scope.Root, "data"), "*.corrupt*"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Package\":null,\"CatalogEntry\":null}")]
    public async Task MalformedHistoricalMetadataIsPreservedAndWarned(string metadata)
    {
        var descriptor = T1SyntheticCatalog.Create().Get("SYN-PREF-01") with { Id = "DBLT-TEST-INVALID" };
        using var scope = new SafetyScope(catalog: new OperationCatalog("debloat-fixture", [descriptor]));
        scope.Journal = scope.Store.CreateDebloatJournal(); scope.Rebuild();
        scope.Seed("Preference.Test", metadata);
        var record = (await scope.ExecuteAsync(descriptor.Id)).Transaction!;
        var catalog = DebloatCatalog.CreateDefault();
        var inventory = SyntheticDebloatInventory.Create(catalog.All);
        var manager = new DebloatManager(catalog, inventory, new SyntheticDebloatRuntimeFactory(inventory), scope.Journal, scope.Locks);
        var result = await manager.LoadHistoryAsync();
        Assert.Empty(result.Items);
        Assert.Contains(result.Warnings, w => w.Contains(record.TransactionId.ToString("D")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(scope.Root, "memory", "debloat", record.TransactionId.ToString("D"), "journal")));
    }

    [Fact]
    public void DebloatAdapterAssemblyHasNoWindowsExecutionCapabilities()
    {
        var assembly = typeof(SyntheticDebloatAdapter).Assembly;
        Assert.All(assembly.GetExportedTypes(), type => Assert.StartsWith("Synthetic", type.Name));
        var references = assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain("SentinelCore.Application", references);
        Assert.DoesNotContain("SentinelCore.Persistence", references);
        Assert.DoesNotContain("SentinelCore.Infrastructure.Windows", references);
        foreach (var path in Directory.GetFiles(TestRepository.Combine("src", "SentinelCore.Infrastructure.Debloat"), "*.cs", SearchOption.TopDirectoryOnly))
        {
            var source = File.ReadAllText(path);
            foreach (var forbidden in new[] { "Process.Start", "ProcessStartInfo", "DllImport", "LibraryImport", "Remove-AppxPackage", "Remove-ProvisionedAppxPackage", "winget uninstall", "Registry.CurrentUser" })
                Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task HighRiskEntriesRemainBlockedEvenWithSyntheticProvenance()
    {
        var catalog = DebloatCatalog.CreateDefault();
        foreach (var entry in catalog.All.Where(e => e.PreliminaryRisk == RiskLevel.High))
        {
            var package = SyntheticDebloatInventory.Package(entry) with { Protected = false, SystemPackage = false };
            var assessment = DebloatPolicy.Assess(catalog, package, DebloatFixtureIdentity.User);
            Assert.Equal(RiskLevel.High, assessment.EffectiveRisk);
            Assert.NotEqual(DebloatPresence.NotVerified, assessment.Presence);
            Assert.Equal(DebloatEligibility.ProtectedOrBlocked, assessment.Eligibility);
            Assert.False(assessment.CanSimulate);
            Assert.False(assessment.CanExecuteReal);
        }
    }

    [Fact]
    public async Task CatalogEntriesWithoutObservationAreNotVerifiedAndNeverAbsent()
    {
        using var scope = new Scope();
        var loaded = await scope.Manager.LoadAsync();
        Assert.Equal(162, loaded.Count);
        var present = loaded.Single(a => a.Package.CatalogId == scope.Package.CatalogId);
        Assert.Equal(DebloatPresence.PresentInSyntheticInventory, present.Presence);
        Assert.Equal(DebloatEligibility.SimulationAvailable, present.Eligibility);
        var catalogOnly = loaded.Where(a => a.Package.CatalogId != scope.Package.CatalogId).ToArray();
        Assert.All(catalogOnly, a => Assert.Equal(DebloatPresence.NotVerified, a.Presence));
        Assert.All(catalogOnly, a => Assert.Equal(DebloatInstallState.Unknown, a.Package.State));
        Assert.All(catalogOnly, a => Assert.False(a.CanSimulate));
        Assert.DoesNotContain(loaded, a => a.Presence == DebloatPresence.AbsentInConsultedInventory);
        Assert.All(loaded, a => Assert.False(a.CanExecuteReal));
        Assert.All(loaded.Where(a => a.CatalogEntry?.RealRisk == RiskLevel.High), a => Assert.False(a.CanSimulate));
    }

    [Fact]
    public async Task CompleteSyntheticInventorySeparatesPresentFromAbsentObservations()
    {
        using var scope = new Scope();
        var absentEntry = scope.Catalog.All.First(e => e.PreliminaryRisk == RiskLevel.Low &&
            DebloatPolicy.ValidIdentity(SyntheticDebloatInventory.Package(e)));
        var observed = scope.Catalog.All.Select(SyntheticDebloatInventory.Package)
            .Select(p => p.CatalogId == absentEntry.Id ? p with { State = DebloatInstallState.NotFound } : p).ToArray();
        var inventory = new SyntheticDebloatInventory(observed);
        var manager = new DebloatManager(scope.Catalog, inventory, new SyntheticDebloatRuntimeFactory(inventory),
            scope.Journal, new FileExclusiveLockFactory(scope.Root));
        var loaded = await manager.LoadAsync();
        Assert.Equal(162, loaded.Count);
        var absent = loaded.Single(a => a.Package.CatalogId == absentEntry.Id);
        Assert.Equal(DebloatPresence.AbsentInConsultedInventory, absent.Presence);
        Assert.False(absent.CanSimulate);
        Assert.All(loaded.Where(a => a.Package.CatalogId != absentEntry.Id),
            a => Assert.NotEqual(DebloatPresence.AbsentInConsultedInventory, a.Presence));
        Assert.Contains(loaded, a => a.Presence == DebloatPresence.PresentInSyntheticInventory);
        Assert.DoesNotContain(loaded, a => a.Presence == DebloatPresence.NotVerified);
    }

    [Fact]
    public void PresentationDistinguishesCatalogInventoryPresenceAndEligibility()
    {
        Assert.Equal("Não verificado — inventário real indisponível.", DebloatPresentation.Presence(DebloatPresence.NotVerified));
        Assert.Equal("Presente no inventário sintético.", DebloatPresentation.Presence(DebloatPresence.PresentInSyntheticInventory));
        Assert.Equal("Ausente no inventário efetivamente consultado.", DebloatPresentation.Presence(DebloatPresence.AbsentInConsultedInventory));
        Assert.Equal("Identidade divergente do catálogo.", DebloatPresentation.Presence(DebloatPresence.IdentityDivergent));
        Assert.Equal("Protegido ou bloqueado; não elegível.", DebloatPresentation.Eligibility(DebloatEligibility.ProtectedOrBlocked));
        Assert.Equal("Indisponível para simulação; execução real bloqueada.", DebloatPresentation.Eligibility(DebloatEligibility.UnavailableForSimulation));
        Assert.Equal(DebloatPresentation.CatalogOrigin, DebloatPresentation.Origin(DebloatPresence.NotVerified));
        Assert.Equal(DebloatPresentation.InventoryOrigin, DebloatPresentation.Origin(DebloatPresence.PresentInSyntheticInventory));
        Assert.NotEqual(DebloatPresentation.CatalogOrigin, DebloatPresentation.InventoryOrigin);
    }

    [Fact]
    public async Task FullSyntheticInventoryKeepsRepresentativeScopesAndFiveSimulatableFixtures()
    {
        using var scope = new Scope();
        var full = SyntheticDebloatInventory.Create(scope.Catalog.All);
        var manager = new DebloatManager(scope.Catalog, full, new SyntheticDebloatRuntimeFactory(full), scope.Journal,
            new FileExclusiveLockFactory(scope.Root));
        var inventory = await manager.LoadAsync();
        Assert.Equal(162, inventory.Count);
        Assert.Contains(inventory, p => p.Package.Scope == DebloatScope.Provisioned);
        Assert.Contains(inventory, p => p.Package.Scope == DebloatScope.OtherUser);
        Assert.Contains(inventory, p => p.Package.Kind == DebloatPackageKind.Framework);
        Assert.Contains(inventory, p => p.Presence == DebloatPresence.NotVerified);
        Assert.Contains(inventory, p => p.Presence == DebloatPresence.PresentInSyntheticInventory);
        Assert.All(inventory, p => Assert.False(p.CanExecuteReal));
        Assert.Equal(5, inventory.Count(p => p.CanSimulate));
    }

    [Fact]
    public async Task PriorIndeterminateTargetCannotBeRemovedByNewPlan()
    {
        using var scope = new Scope();
        var first = await scope.Prepare(DebloatScenario.Indeterminate);
        Assert.False((await scope.Manager.ExecuteAsync(first, scope.Manager.Authorize(first, first.Confirmation))).Succeeded);
        var second = await scope.Prepare();
        var result = await scope.Manager.ExecuteAsync(second, scope.Manager.Authorize(second, second.Confirmation));
        Assert.Contains(BlockerCode.IndeterminateState, result.Transaction.Blockers);
    }

    private sealed class Scope : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SentinelCoreDebloatTests", Guid.NewGuid().ToString("N"));
        public DebloatCatalog Catalog { get; } = DebloatCatalog.CreateDefault();
        public TestTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        public SyntheticDebloatInventory Inventory { get; }
        public DebloatPackage Package { get; }
        public MemoryStore Store { get; }
        public ITransactionJournalStore Journal { get; }
        public DebloatManager Manager { get; }
        public Scope(string name = "Clipchamp.Clipchamp")
        {
            Package = SyntheticDebloatInventory.Package(Catalog.All.Single(e => e.TechnicalName == name));
            Inventory = new SyntheticDebloatInventory([Package]);
            Store = new MemoryStore(Root); Journal = Store.CreateDebloatJournal(); Manager = CreateManager();
        }
        public DebloatManager CreateManager() => new(Catalog, Inventory, new SyntheticDebloatRuntimeFactory(Inventory),
            Journal, new FileExclusiveLockFactory(Root), Time);
        public Task<DebloatPreparedPlan> Prepare(DebloatScenario scenario = DebloatScenario.Success) =>
            Manager.PrepareAsync(Package.CatalogId, scenario);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}

internal static class DebloatTestListExtensions
{
    internal static int FindIndex<T>(this IReadOnlyList<T> list, Func<T, bool> predicate) =>
        list.Select((value, index) => (value, index)).First(pair => predicate(pair.value)).index;
}
