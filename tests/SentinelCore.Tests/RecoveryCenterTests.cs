using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Debloat;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class RecoveryCenterTests
{
    [Fact]
    public async Task EmptySourcesAreCompleteButMissingSourceIsUnavailable()
    {
        var reader = new RecoveryReader(); var center = new RecoveryCenter(reader);
        var empty = await center.LoadAsync();
        Assert.True(empty.IsComplete); Assert.Empty(empty.Operations);
        reader.Sources = reader.Sources.Where(s => s.Module != RecoveryModule.Tweaks).ToArray();
        var missing = await center.LoadAsync();
        Assert.False(missing.IsComplete);
        Assert.Equal(RecoveryEvidenceStatus.Unavailable, missing.Sources.Single(s => s.Module == RecoveryModule.Tweaks).Status);
        Assert.Contains("INCOMPLETO", RecoveryCenterPresentation.Summary(missing));
    }

    [Fact]
    public async Task FailedReaderNeverBecomesEmptySuccess()
    {
        var reader = new RecoveryReader { Error = new IOException("fixture leitura negada") };
        var snapshot = await new RecoveryCenter(reader).LoadAsync();
        Assert.All(snapshot.Sources, s => Assert.Equal(RecoveryEvidenceStatus.Unavailable, s.Status));
        Assert.False(RecoveryCenter.Summarize(snapshot).IsComplete); Assert.NotEmpty(snapshot.Warnings);
    }

    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RecoveryCenter(new RecoveryReader()).LoadAsync(cancel.Token));
    }

    [Fact]
    public async Task IdenticalTransactionsCountOnceDivergentDuplicatesAreExcluded()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var transaction = (await scope.ExecuteAsync("SYN-PREF-01")).Transaction!;
        var reader = new RecoveryReader(); reader.Transactions(transaction, transaction);
        var center = new RecoveryCenter(reader);
        Assert.Single((await center.LoadAsync()).Operations);
        var events = transaction.Events.Select(e => e.Sequence == 1 ? e with { Detail = "outra carga" } : e).ToArray();
        reader.Transactions(transaction, transaction with { Events = events });
        var ambiguous = await center.LoadAsync();
        Assert.Empty(ambiguous.Operations); Assert.Contains(ambiguous.Warnings, w => w.Contains("divergente"));
    }

    [Fact]
    public async Task DuplicateSourceNeverCountsTwice()
    {
        var reader = new RecoveryReader(); reader.Sources = reader.Sources.Append(reader.Sources[0]).ToArray();
        var snapshot = await new RecoveryCenter(reader).LoadAsync();
        Assert.False(snapshot.IsComplete); Assert.Contains(snapshot.Warnings, w => w.Contains("duplicada"));
    }

    [Fact]
    public async Task StaleDerivedSuccessIsRebuiltFromPersistedEvents()
    {
        using var scope = new SafetyScope(); scope.Seed("Preference.Test", "A");
        var t = (await scope.ExecuteAsync("SYN-PREF-01")).Transaction!;
        var events = t.Events.TakeWhile(e => e.State != TransactionState.ExecutionStarted).ToArray();
        var reader = new RecoveryReader(); reader.Transactions(t with { Events = events, Succeeded = true });
        var row = Assert.Single((await new RecoveryCenter(reader).LoadAsync()).Operations);
        Assert.NotEqual(RecoveryOperationOutcome.Completed, row.Outcome); Assert.False(row.EffectMayHaveOccurred);
        Assert.DoesNotContain("ExecutionStarted", RecoveryCenterPresentation.Timeline(row));
    }

    [Theory]
    [InlineData("unknown-version")]
    [InlineData("unknown-state")]
    [InlineData("duplicate-sequence")]
    [InlineData("missing-sequence")]
    [InlineData("wrong-id")]
    [InlineData("wrong-module")]
    public async Task InvalidJournalIsNotAValidOperation(string corruption)
    {
        using var scope = new SafetyScope(); scope.Seed("Preference.Test", "A");
        var t = (await scope.ExecuteAsync("SYN-PREF-01")).Transaction!;
        var e = t.Events.ToArray();
        switch (corruption)
        {
            case "unknown-version": e[0] = e[0] with { FormatVersion = 90 }; break;
            case "unknown-state": e[1] = e[1] with { State = (TransactionState)999 }; break;
            case "duplicate-sequence": e[1] = e[1] with { Sequence = 1 }; break;
            case "missing-sequence": e = e.Where(x => x.Sequence != 2).ToArray(); break;
            case "wrong-id": e[0] = e[0] with { TransactionId = Guid.NewGuid() }; break;
        }
        var reader = new RecoveryReader(); reader.Transactions(t with { Events = e },
            module: corruption == "wrong-module" ? RecoveryModule.Debloat : RecoveryModule.Tweaks);
        var snapshot = await new RecoveryCenter(reader).LoadAsync();
        Assert.Empty(snapshot.Operations); Assert.False(snapshot.IsComplete); Assert.NotEmpty(snapshot.Warnings);
    }

    [Fact]
    public async Task SyntheticRecoveryReusesOriginalServiceAndRequiresSpecificConfirmation()
    {
        using var scope = new SafetyScope(); scope.Seed("Preference.Test", "A");
        var t = (await scope.ExecuteAsync("SYN-PREF-01")).Transaction!;
        var reader = LiveReader(scope.Store);
        var service = SyntheticRecovery(scope);
        var center = new RecoveryCenter(reader, service);
        var row = Assert.Single((await center.LoadAsync()).Operations);
        Assert.Equal(RecoveryCapability.Blocked, row.Capability);
        var noPreview = await center.RecoverAsync(row, TweakManager.BuildRecoveryConfirmationText(row.Id));
        Assert.Equal(RecoveryOutcome.Blocked, noPreview.Outcome);
        var analyzed = await center.AnalyzeAsync(row);
        Assert.Equal(RecoveryCapability.PotentiallyAvailable, analyzed.Capability);
        var refused = await center.RecoverAsync(analyzed, "sim");
        Assert.Equal(RecoveryOutcome.Blocked, refused.Outcome); Assert.Equal("B", scope.Current("Preference.Test"));
        var result = await center.RecoverAsync(analyzed, TweakManager.BuildRecoveryConfirmationText(row.Id));
        Assert.True(result.FullyRecovered); Assert.Equal("A", scope.Current("Preference.Test"));
        var reopened = Assert.Single((await new RecoveryCenter(reader).LoadAsync()).Operations);
        Assert.Single(reopened.Recoveries); Assert.True(reopened.Recoveries[0].FullyRecovered);
        var summary = RecoveryCenter.Summarize(await center.LoadAsync()); Assert.Equal(1, summary.RecoveriesCompleted);
        Assert.Equal(RecoveryOutcome.Blocked, (await center.RecoverAsync(analyzed, TweakManager.BuildRecoveryConfirmationText(row.Id))).Outcome);
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("missing-backup")]
    [InlineData("invalid-backup")]
    [InlineData("unknown-current")]
    [InlineData("already-restored")]
    public async Task UnsafeOrUnnecessaryRecoveryIsBlockedByExistingPreview(string scenario)
    {
        using var scope = new SafetyScope(); scope.Seed("Preference.Test", "A");
        var t = (await scope.ExecuteAsync("SYN-PREF-01")).Transaction!;
        if (scenario == "conflict") scope.Seed("Preference.Test", "third-party");
        if (scenario == "missing-backup") File.Delete(t.BackupPath!);
        if (scenario == "invalid-backup") File.WriteAllText(t.BackupPath!, "corrompido");
        if (scenario == "already-restored") scope.Seed("Preference.Test", "A");
        var service = new RecoveryService(scope.Store,
            scenario == "unknown-current" ? new UnknownReader() : scope.Targets,
            scope.Executor, scope.Locks, scope.Time, scope.Catalog, syntheticOnly: true);
        var center = new RecoveryCenter(LiveReader(scope.Store), service);
        var row = Assert.Single((await center.LoadAsync()).Operations);
        var analyzed = await center.AnalyzeAsync(row);
        Assert.Equal(RecoveryCapability.Blocked, analyzed.Capability);
        Assert.NotNull(analyzed.Preview);
        if (scenario == "conflict" || scenario == "unknown-current") Assert.True(analyzed.HasConflict);
        if (scenario == "already-restored") Assert.Equal(RecoveryOutcome.AlreadyRestored, analyzed.Preview.Outcome);
        else Assert.False(analyzed.Preview.CanRecover);
    }

    [Fact]
    public async Task PartialRecoveryPreservesConflictAndIsNeverIntegral()
    {
        var a = new OperationTarget("Synthetic", "A", "Value");
        var b = new OperationTarget("Synthetic", "B", "Value");
        var template = T1SyntheticCatalog.Create().Get("SYN-PREF-01");
        var descriptor = template with { Id = "SYN-T4A-PARTIAL", Targets = [a, b],
            Changes = [new(a, new(true, "String", "after")), new(b, new(true, "String", "after"))] };
        using var scope = new SafetyScope(catalog: new OperationCatalog("t4a-partial-1", [descriptor]));
        scope.Targets.Set(a, new(true, "String", "before")); scope.Targets.Set(b, new(true, "String", "before"));
        await scope.ExecuteAsync(descriptor.Id); scope.Targets.Set(a, new(true, "String", "third-party"));
        var center = new RecoveryCenter(LiveReader(scope.Store), SyntheticRecovery(scope));
        var row = await center.AnalyzeAsync(Assert.Single((await center.LoadAsync()).Operations));
        Assert.True(row.Preview!.CanRecover); Assert.True(row.HasConflict);
        var result = await center.RecoverAsync(row, TweakManager.BuildRecoveryConfirmationText(row.Id));
        Assert.Equal(RecoveryOutcome.PartiallyCompleted, result.Outcome); Assert.False(result.FullyRecovered);
        Assert.Equal("third-party", scope.Targets.Read(a).Value!.Data); Assert.Equal("before", scope.Targets.Read(b).Value!.Data);
        var summary = RecoveryCenter.Summarize(await center.LoadAsync());
        Assert.Equal(1, summary.RecoveriesPartial); Assert.Equal(0, summary.RecoveriesCompleted);
    }

    [Fact]
    public async Task NoEffectDoesNotFabricateExecutionAndNeedsNoRollback()
    {
        using var scope = new SafetyScope(); scope.Seed("Preference.Test", "A");
        var t = (await scope.ExecuteAsync("SYN-PREF-01")).Transaction!;
        var beforeEffect = TransactionProjection.Build(t.Events.TakeWhile(e => e.State != TransactionState.ExecutionStarted).ToArray());
        var reader = new RecoveryReader(); reader.Transactions(beforeEffect);
        // Preview uses a journal containing only events before effect; no execution or backup write happens here.
        var journal = new FixedJournal(beforeEffect, scope.Store);
        var center = new RecoveryCenter(reader, new RecoveryService(journal, scope.Targets, scope.Executor,
            scope.Locks, scope.Time, scope.Catalog, syntheticOnly: true));
        scope.Seed("Preference.Test", "A");
        var analyzed = await center.AnalyzeAsync(Assert.Single((await center.LoadAsync()).Operations));
        Assert.False(analyzed.EffectMayHaveOccurred); Assert.Equal(RecoveryOutcome.AlreadyRestored, analyzed.Preview!.Outcome);
    }

    [Fact]
    public void CentralRefusesServiceWithoutSyntheticBoundary()
    {
        using var scope = new SafetyScope();
        Assert.Throws<ArgumentException>(() => new RecoveryCenter(new RecoveryReader(), scope.Recovery));
    }

    [Fact]
    public async Task SyntheticServiceRejectsHkcuBeforeReaderOrExecutor()
    {
        using var scope = new T2bScope();
        var t = (await scope.ExecuteAsync(T2bCatalog.ShowHiddenFilesId)).Transaction!;
        var spy = new ThrowingRealBoundary();
        var service = new RecoveryService(scope.Store, spy, spy, scope.Locks, scope.Time, syntheticOnly: true);
        Assert.False((await service.PreviewAsync(t.TransactionId)).CanRecover);
        Assert.Equal(RecoveryOutcome.NotRecoverable, (await service.RollbackAsync(t.TransactionId, new("confirmacao"))).Outcome);
        Assert.Equal(0, spy.Calls);
    }

    [Theory]
    [InlineData(DebloatRemovalScenario.Success, false)]
    [InlineData(DebloatRemovalScenario.Unverifiable, true)]
    [InlineData(DebloatRemovalScenario.PostVerificationFailed, true)]
    [InlineData(DebloatRemovalScenario.Indeterminate, true)]
    public async Task RemovalHistoriesPreserveReceiptAndNeverOfferRollback(DebloatRemovalScenario scenario, bool indeterminate)
    {
        using var scope = new DebloatRemovalTestScope();
        var plan = await scope.Prepare(scenario); await scope.Manager.ExecuteAsync(plan, scope.Authorize(plan));
        var row = Assert.Single((await new RecoveryCenter(scope.Store).LoadAsync()).Operations);
        Assert.Equal(RecoveryModule.ControlledRemoval, row.Module);
        Assert.Equal(indeterminate, row.Outcome == RecoveryOperationOutcome.Indeterminate);
        Assert.Equal(indeterminate, row.TargetPersistentlyBlocked);
        Assert.NotEqual(RecoveryCapability.PotentiallyAvailable, row.Capability);
        Assert.Contains(row.Guidance, g => g.Contains("Reinstalação não equivale a rollback"));
        Assert.Contains("metadados", RecoveryCenterPresentation.Details(row));
        Assert.Equal(row.Transaction!.Events.Count, row.Events.Count);
        Assert.False(RecoveryCenter.Summarize(await new RecoveryCenter(scope.Store).LoadAsync()).RecoveriesCompleted > 0);
    }

    [Theory]
    [InlineData(RepairCatalog.SfcVerifyId, null)]
    [InlineData(RepairCatalog.DismCheckHealthId, null)]
    [InlineData(RepairCatalog.ChkdskScanId, "C:")]
    [InlineData(RepairCatalog.WinReInfoId, null)]
    public async Task RepairHistoryHasCommandEvidenceAndNoRollback(string id, string? volume)
    {
        using var scope = new RepairScope(); await scope.ExecuteAsync(id, volume);
        var row = Assert.Single((await new RecoveryCenter(scope.Store).LoadAsync()).Operations);
        Assert.Equal(RecoveryModule.WindowsRepair, row.Module); Assert.Equal(RecoveryOrigin.Synthetic, row.Origin);
        Assert.Equal(RecoveryCapability.Unsupported, row.Capability); Assert.NotNull(row.Repair);
        var details = RecoveryCenterPresentation.Details(row);
        Assert.Contains(row.Repair.InvocationDisplay, details); Assert.Contains("Código de saída", details);
        Assert.Contains("Sem rollback", details); Assert.Equal(row.Repair.Steps.Count, row.Events.Count);
    }

    [Theory]
    [InlineData(RepairFixtureScenario.RepairPartial)]
    [InlineData(RepairFixtureScenario.IncompleteOutput)]
    [InlineData(RepairFixtureScenario.Interrupted)]
    public async Task InconclusiveRepairNeedsIntervention(RepairFixtureScenario scenario)
    {
        using var scope = new RepairScope(); await scope.ExecuteAsync(RepairCatalog.SfcRepairId, scenario: scenario);
        var row = Assert.Single((await new RecoveryCenter(scope.Store).LoadAsync()).Operations);
        Assert.Equal(RecoveryOperationOutcome.Indeterminate, row.Outcome); Assert.True(row.RequiresIntervention);
    }

    [Theory]
    [InlineData(RepairConfidence.Low, RecoveryOperationOutcome.Indeterminate)]
    [InlineData(RepairConfidence.High, RecoveryOperationOutcome.Completed)]
    public async Task DismNoCorruptionWithoutSufficientConfidenceIsNotConclusive(RepairConfidence confidence, RecoveryOperationOutcome expected)
    {
        using var scope = new RepairScope();
        var result = await scope.ExecuteAsync(RepairCatalog.DismCheckHealthId, scenario: RepairFixtureScenario.CorruptionNotFound);
        var r = result.Record! with { Confidence = confidence, State = RepairState.Completed,
            Result = RepairResultClassification.NoCorruptionDetected,
            ResultSummary = confidence == RepairConfidence.Low ? "Ausência de corrupção não é conclusiva." : "Nenhuma corrupção detectada." };
        var reader = new RecoveryReader();
        reader.Sources = reader.Sources.Select(s => s.Module == RecoveryModule.WindowsRepair ? s with { Repairs = [r] } : s).ToArray();
        var row = Assert.Single((await new RecoveryCenter(reader).LoadAsync()).Operations);
        Assert.Equal(expected, row.Outcome); Assert.Equal(confidence == RepairConfidence.Low, row.RequiresIntervention);
        Assert.Contains($"conclusão suficientemente documentada: {confidence != RepairConfidence.Low}", row.Verification);
        if (confidence == RepairConfidence.Low) Assert.DoesNotContain("sucesso documentado: True", RecoveryCenterPresentation.Details(row));
    }

    [Fact]
    public async Task DebloatSimulationHistoryIncludesIdentityDependenciesAndLimits()
    {
        using var scope = new SafetyScope();
        var catalog = DebloatCatalog.CreateDefault(); var package = SyntheticDebloatInventory.Package(catalog.All.First(e => e.TechnicalName == "Clipchamp.Clipchamp"));
        var inventory = new SyntheticDebloatInventory([package]);
        var manager = new DebloatManager(catalog, inventory, new SyntheticDebloatRuntimeFactory(inventory), scope.Store.CreateDebloatJournal(), scope.Locks, scope.Time);
        var plan = await manager.PrepareAsync(package.CatalogId, DebloatScenario.Success);
        await manager.ExecuteAsync(plan, manager.Authorize(plan, plan.Confirmation));
        var row = Assert.Single((await new RecoveryCenter(scope.Store).LoadAsync()).Operations);
        Assert.Equal(RecoveryModule.Debloat, row.Module); Assert.Equal(RecoveryOperationOutcome.Completed, row.Outcome);
        Assert.Contains(package.PackageFullName, row.Target); Assert.Equal(RecoveryCapability.Unsupported, row.Capability);
        Assert.Contains("Dependências", RecoveryCenterPresentation.Details(row));
        Assert.Contains("risco real T0", RecoveryCenterPresentation.Details(row));
    }

    [Theory]
    [InlineData("scenario")][InlineData("state")][InlineData("kind")]
    [InlineData("scope")][InlineData("dependencies")][InlineData("risk")]
    public async Task UnknownDebloatMetadataCannotBecomeCompletedEvidence(string field)
    {
        using var scope = new SafetyScope();
        var catalog = DebloatCatalog.CreateDefault();
        var package = SyntheticDebloatInventory.Package(catalog.All.First(e => e.TechnicalName == "Clipchamp.Clipchamp"));
        var inventory = new SyntheticDebloatInventory([package]);
        var manager = new DebloatManager(catalog, inventory, new SyntheticDebloatRuntimeFactory(inventory), scope.Store.CreateDebloatJournal(), scope.Locks, scope.Time);
        var prepared = await manager.PrepareAsync(package.CatalogId, DebloatScenario.Success);
        var t = (await manager.ExecuteAsync(prepared, manager.Authorize(prepared, prepared.Confirmation))).Transaction.Transaction!;
        var plan = t.Plan!; var change = plan.Changes.Single();
        var metadata = System.Text.Json.JsonSerializer.Deserialize<DebloatTargetSnapshot>(change.Before.Data!)!;
        metadata = field switch
        {
            "scenario" => metadata with { Scenario = (DebloatScenario)999 },
            "state" => metadata with { Package = metadata.Package with { State = (DebloatInstallState)999 } },
            "kind" => metadata with { Package = metadata.Package with { Kind = (DebloatPackageKind)999 } },
            "scope" => metadata with { Package = metadata.Package with { Scope = (DebloatScope)999 } },
            "dependencies" => metadata with { Package = metadata.Package with { DependencyStatus = (DebloatDependencyStatus)999 } },
            _ => metadata with { CatalogEntry = metadata.CatalogEntry with { RealRisk = (RiskLevel)999 } }
        };
        var changes = new[] { change with { Before = change.Before with { Data = System.Text.Json.JsonSerializer.Serialize(metadata) } } };
        plan = plan with { Changes = changes, PlanHash = SafetyHashing.PlanHash(plan.PlanId, plan.TransactionId,
            plan.OperationId, plan.OperationVersion, plan.CatalogVersion, plan.Risk, plan.Scope, plan.Protection, plan.ObservedStateHash, changes) };
        var events = t.Events.Select(e => e with { PlanHash = plan.PlanHash, Plan = e.Plan is null ? null : plan }).ToArray();
        t = TransactionProjection.Build(events);
        var reader = new RecoveryReader(); reader.Transactions(t, module: RecoveryModule.Debloat);
        var snapshot = await new RecoveryCenter(reader).LoadAsync();
        Assert.Empty(snapshot.Operations); Assert.False(snapshot.IsComplete); Assert.NotEmpty(snapshot.Warnings);
        var original = new DebloatManager(catalog, inventory, new SyntheticDebloatRuntimeFactory(inventory),
            new FixedJournal(t, scope.Store.CreateDebloatJournal()), scope.Locks, scope.Time);
        Assert.Empty((await original.LoadHistoryAsync()).Items);
    }

    [Fact]
    public async Task FailedAnalysisRevokesPreviouslyActionablePreview()
    {
        using var scope = new SafetyScope(); scope.Seed("Preference.Test", "A");
        var t = (await scope.ExecuteAsync("SYN-PREF-01")).Transaction!;
        var reader = LiveReader(scope.Store); var center = new RecoveryCenter(reader, SyntheticRecovery(scope));
        var row = await center.AnalyzeAsync(Assert.Single((await center.LoadAsync()).Operations));
        reader.Error = new IOException("fixture falha de leitura");
        await Assert.ThrowsAsync<InvalidOperationException>(() => center.AnalyzeAsync(row));
        reader.Error = null;
        var result = await center.RecoverAsync(row, TweakManager.BuildRecoveryConfirmationText(t.TransactionId));
        Assert.Equal(RecoveryOutcome.Blocked, result.Outcome); Assert.Equal("B", scope.Current("Preference.Test"));
    }

    [Theory]
    [InlineData("module")][InlineData("type")][InlineData("date")][InlineData("state")]
    [InlineData("risk")][InlineData("outcome")][InlineData("origin")][InlineData("capability")]
    [InlineData("available")][InlineData("unavailable")][InlineData("conflict")][InlineData("intervention")]
    [InlineData("search-id")][InlineData("search-target")][InlineData("search-consequence")]
    public void FiltersAreReadOnlyAndSearchRelevantInformation(string choice)
    {
        var target = Example();
        var other = target with { Id = Guid.NewGuid(), OperationId = "unrelated", Module = RecoveryModule.WindowsRepair,
            Type = "other", State = "Other", Risk = RiskLevel.High, Outcome = RecoveryOperationOutcome.Failed,
            Origin = RecoveryOrigin.Real, Capability = RecoveryCapability.Unsupported, HasConflict = false,
            RequiresIntervention = false, Target = "other", Consequences = [], AtUtc = target.AtUtc.AddDays(-7) };
        var snapshot = new RecoveryCenterSnapshot([target, other], [], []);
        var filter = choice switch
        {
            "module" => new RecoveryCenterFilter(Module: target.Module), "type" => new(Type: target.Type),
            "date" => new(FromUtc: target.AtUtc.AddHours(-1), ToUtc: target.AtUtc.AddHours(1)),
            "state" => new(State: target.State), "risk" => new(Risk: target.Risk),
            "outcome" => new(Outcome: target.Outcome), "origin" => new(Origin: target.Origin),
            "capability" => new(Capability: target.Capability), "available" => new(RecoveryAvailable: true),
            "unavailable" => new(RecoveryAvailable: false), "conflict" => new(Conflict: true),
            "intervention" => new(Intervention: true), "search-id" => new(Search: target.Id.ToString("D")),
            "search-target" => new(Search: "PREFERENCE.TEST"), _ => new(Search: "consequência específica")
        };
        var result = Assert.Single(RecoveryCenter.Filter(snapshot, filter));
        Assert.Same(choice == "unavailable" ? other : target, result); Assert.Equal(2, snapshot.Operations.Count);
    }

    [Fact]
    public async Task OrderedNewestFirstAndWarningsTravelWithSameLoad()
    {
        using var scope = new SafetyScope(); scope.Seed("Preference.Test", "A");
        var first = (await scope.ExecuteAsync("SYN-PREF-01")).Transaction!;
        scope.Time.Now = scope.Time.Now.AddMinutes(1); scope.Seed("Preference.Test", "A");
        var second = (await scope.ExecuteAsync("SYN-PREF-01")).Transaction!;
        var reader = new RecoveryReader(); reader.Transactions(first, second);
        reader.Sources = reader.Sources.Select(s => s.Module == RecoveryModule.Tweaks ? s with { Warnings = ["fixture aviso"] } : s).ToArray();
        var snapshot = await new RecoveryCenter(reader).LoadAsync();
        Assert.Equal(second.TransactionId, snapshot.Operations[0].Id); Assert.False(snapshot.IsComplete);
        Assert.Contains(snapshot.Warnings, w => w.Contains("fixture aviso"));
    }

    private static RecoveryService SyntheticRecovery(SafetyScope scope) => new(scope.Store, scope.Targets,
        scope.Executor, scope.Locks, scope.Time, scope.Catalog, syntheticOnly: true);
    private static RecoveryReader LiveReader(MemoryStore store) => new() { Loader = async () =>
    { var snapshot = await store.LoadTransactionsAsync(); return [new(RecoveryModule.Tweaks, RecoveryEvidenceStatus.Complete, snapshot.Transactions, [], [], [])]; } };
    private static RecoveryOperation Example() => new(Guid.NewGuid(), RecoveryModule.Tweaks, "SYN-EXAMPLE", "SetValue", "Preference.Test",
        new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), "VerificationPassed", RecoveryOperationOutcome.Completed,
        RecoveryOrigin.Synthetic, RiskLevel.Low, RecoveryCapability.PotentiallyAvailable, true, true, true, false,
        "fixture", "fixture", "fixture", ["consequência específica"], [], [], [], [], "fixture.md");

    private sealed class UnknownReader : ITargetStateReader
    {
        public Task<ObservedTargetState> ReadAsync(OperationTarget target, CancellationToken cancellationToken = default) =>
            Task.FromResult(ObservedTargetState.Unknown(target.Identity, DateTimeOffset.UtcNow, "fixture desconhecido"));
    }
    private sealed class ThrowingRealBoundary : ITargetStateReader, IOperationExecutor
    {
        public int Calls { get; private set; }
        public Task<ObservedTargetState> ReadAsync(OperationTarget target, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Leitura real proibida"); }
        public Task<ExecutionOutcome> ApplyAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Escrita real proibida"); }
    }
    private sealed class FixedJournal(TransactionRecord transaction, ITransactionJournalStore original) : ITransactionJournalStore
    {
        public string GetTransactionReportPath(Guid id) => original.GetTransactionReportPath(id);
        public Task<TransactionRecord?> FindTransactionAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<TransactionRecord?>(transaction);
        public Task<TransactionJournalSnapshot> LoadTransactionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new TransactionJournalSnapshot([transaction], []));
        public Task<BackupRecord> ReadBackupAsync(string path, CancellationToken cancellationToken = default) => original.ReadBackupAsync(path, cancellationToken);
        public Task<string> WriteBackupAsync(BackupRecord record, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<TransactionEventRecord> AppendEventAsync(TransactionEventRecord entry, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}

internal sealed class RecoveryReader : IRecoveryHistoryReader
{
    public IReadOnlyList<RecoveryHistorySource> Sources { get; set; } = Enum.GetValues<RecoveryModule>()
        .Select(m => new RecoveryHistorySource(m, RecoveryEvidenceStatus.Complete, [], [], [], [])).ToArray();
    public Exception? Error { get; set; }
    public Func<Task<IReadOnlyList<RecoveryHistorySource>>>? Loader { get; set; }
    public void Transactions(TransactionRecord first, TransactionRecord? second = null, RecoveryModule module = RecoveryModule.Tweaks) =>
        Sources = Sources.Select(s => s.Module == module ? s with { Transactions = second is null ? [first] : [first, second] } : s).ToArray();
    public async Task<IReadOnlyList<RecoveryHistorySource>> ReadRecoveryHistoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); if (Error is not null) throw Error;
        if (Loader is null) return Sources;
        var loaded = await Loader();
        return Sources.Select(s => loaded.FirstOrDefault(l => l.Module == s.Module) ?? s).ToArray();
    }
    public string GetRecoveryReportPath(RecoveryModule module, Guid id) => Path.Combine(Path.GetTempPath(), "fixture-" + id.ToString("D") + ".md");
}
