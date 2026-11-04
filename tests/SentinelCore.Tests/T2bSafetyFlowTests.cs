using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class T2bSafetyFlowTests
{
    [Fact]
    public async Task ShowHiddenFilesReadsTheObservedValueThenAppliesAndVerifies()
    {
        using var scope = new T2bScope();
        var target = scope.Target(T2bCatalog.HiddenValue);
        scope.Access.SeedDWord(target, 2);

        var simulation = await scope.Engine.SimulateAsync(T2bCatalog.ShowHiddenFilesId);
        var change = Assert.Single(simulation.Plan.Changes);

        Assert.True(simulation.CanExecute);
        Assert.Equal(new TargetValueState(true, RegistryValueKinds.DWord, "2"), change.Before);
        Assert.Equal(new TargetValueState(true, RegistryValueKinds.DWord, "1"), change.After);
        Assert.False(simulation.HasEffects);

        var token = scope.Engine.Authorize(simulation, TweakManager.BuildConfirmationText(simulation.Plan));
        var result = await scope.Engine.ExecuteAsync(simulation.Plan, token);

        Assert.True(result.Succeeded);
        Assert.Equal("1", scope.Access.Data(target));
        Assert.Equal("2", result.Transaction!.Backup!.Entries.Single().Value!.Data);
        Assert.True(result.Transaction.Verification!.Passed);
        Assert.Equal(["Planned", "PreflightPassed", "BackupPrepared", "BackupVerified", "Authorized",
            "ExecutionStarted", "ExecutionCompleted", "VerificationPassed"],
            result.Transaction.Events.Select(item => item.State.ToString()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RollbackRestoresTheObservedHiddenValueWithoutAssumingTwo(int observed)
    {
        using var scope = new T2bScope();
        var target = scope.Target(T2bCatalog.HiddenValue);
        scope.Access.SeedDWord(target, observed);
        var execution = await scope.ExecuteAsync(T2bCatalog.ShowHiddenFilesId);
        var transactionId = execution.Transaction!.TransactionId;
        Assert.True(execution.Succeeded);
        Assert.Equal("1", scope.Access.Data(target));

        var result = await scope.Recovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.True(result.FullyRecovered);
        Assert.Equal(observed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            scope.Access.Data(target));
        Assert.Equal(observed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            execution.Transaction.Backup!.Entries.Single().Value!.Data);
    }

    [Fact]
    public async Task AbsentHiddenValueIsCreatedAndItsAbsenceIsPreserved()
    {
        using var scope = new T2bScope();
        var target = scope.Target(T2bCatalog.HiddenValue);
        scope.Access.SeedAbsentValue(target);
        var execution = await scope.ExecuteAsync(T2bCatalog.ShowHiddenFilesId);
        var transactionId = execution.Transaction!.TransactionId;
        var rollback = await scope.Recovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.True(execution.Succeeded);
        Assert.False(execution.Transaction.Plan!.Changes.Single().Before.Exists);
        Assert.False(execution.Transaction.Backup!.Entries.Single().Value!.Exists);
        Assert.True(rollback.FullyRecovered);
        Assert.False(scope.Access.State(target).Exists);
    }

    [Fact]
    public async Task UnexpectedHiddenValueKindBlocksWithoutConversion()
    {
        using var scope = new T2bScope();
        scope.Access.SeedString(scope.Target(T2bCatalog.HiddenValue), "");

        var simulation = await scope.Engine.SimulateAsync(T2bCatalog.ShowHiddenFilesId);

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.Unrecoverable, simulation.Blockers);
        Assert.Equal(RegistryValueKinds.String, simulation.Plan.Changes.Single().Before.Kind);
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task MissingKeyBlocksHiddenFilesBecauseTheAdapterDoesNotCreateKeys()
    {
        using var scope = new T2bScope();
        scope.Access.SeedMissingKey(scope.Target(T2bCatalog.HiddenValue));

        var simulation = await scope.Engine.SimulateAsync(T2bCatalog.ShowHiddenFilesId);

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.Unrecoverable, simulation.Blockers);
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task TaskViewButtonIsHiddenAndVerified()
    {
        using var scope = new T2bScope();
        var target = scope.Target(T2bCatalog.ShowTaskViewButtonValue);
        scope.Access.SeedDWord(target, 1);

        var execution = await scope.ExecuteAsync(T2bCatalog.HideTaskViewButtonId);

        Assert.True(execution.Succeeded);
        Assert.Equal("0", scope.Access.Data(target));
        Assert.Equal(T2aCatalog.ExplorerAdvancedPath,
            execution.Transaction!.Plan!.Changes.Single().Target.Path);
        Assert.Equal("1", execution.Transaction.Backup!.Entries.Single().Value!.Data);
    }

    [Fact]
    public async Task TaskViewStateChangedAfterSimulationBlocksWithoutWrite()
    {
        using var scope = new T2bScope();
        var target = scope.Target(T2bCatalog.ShowTaskViewButtonValue);
        scope.Access.SeedDWord(target, 1);
        var (simulation, token) = await scope.PrepareAsync(T2bCatalog.HideTaskViewButtonId);
        scope.Access.SeedDWord(target, 0);

        var result = await scope.Engine.ExecuteAsync(simulation.Plan, token);

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.StateChanged, result.Blockers);
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task SearchModeExampleThreeToZeroAndRollbackToThree()
    {
        using var scope = new T2bScope();
        var target = scope.SearchTarget();
        scope.Access.SeedDWord(target, 3);
        var execution = await scope.ExecuteAsync(T2bCatalog.HideTaskbarSearchId);
        var transactionId = execution.Transaction!.TransactionId;

        Assert.True(execution.Succeeded);
        Assert.Equal("0", scope.Data(T2bCatalog.SearchboxTaskbarModeValue));
        Assert.Equal("3", execution.Transaction.Backup!.Entries.Single().Value!.Data);

        var rollback = await scope.Recovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.True(rollback.FullyRecovered);
        Assert.Equal("3", scope.Data(T2bCatalog.SearchboxTaskbarModeValue));
    }

    [Fact]
    public async Task SearchModesCanBeChangedSuccessivelyAndEachIsVerified()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(scope.SearchTarget(), 0);

        foreach (var mode in new[] { 1, 2, 3, 0 })
        {
            var execution = await scope.ExecuteAsync(T2bCatalog.OperationIdForSearchMode(mode));
            Assert.True(execution.Succeeded);
            Assert.Equal(mode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                scope.Data(T2bCatalog.SearchboxTaskbarModeValue));
            Assert.True(execution.Transaction!.Verification!.Passed);
        }
    }

    [Fact]
    public async Task SelectingTheObservedModeIsANoOpThatIsStillVerified()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(scope.SearchTarget(), 2);

        var execution = await scope.ExecuteAsync(T2bCatalog.SearchFullBoxId);

        Assert.True(execution.Succeeded);
        var step = Assert.Single(execution.Transaction!.Steps);
        Assert.False(step.Changed);
        Assert.True(step.Verified);
        Assert.Equal("2", scope.Data(T2bCatalog.SearchboxTaskbarModeValue));
    }

    [Fact]
    public async Task ConcurrentTransactionsOnTheSameSearchValueDetectDivergence()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(scope.SearchTarget(), 2);
        var first = await scope.ExecuteAsync(T2bCatalog.HideTaskbarSearchId);
        var second = await scope.ExecuteAsync(T2bCatalog.SearchIconOnlyId);

        var preview = await scope.Recovery.PreviewAsync(first.Transaction!.TransactionId);
        var rollback = await scope.Recovery.RollbackAsync(first.Transaction.TransactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(first.Transaction.TransactionId)));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal("1", scope.Data(T2bCatalog.SearchboxTaskbarModeValue));
        Assert.False(preview.CanRecover);
        Assert.Equal(RecoveryOutcome.Conflict, preview.Outcome);
        Assert.Contains(preview.Conflicts,
            conflict => conflict.TargetIdentity == scope.SearchTarget().Identity);
        Assert.Equal(RecoveryOutcome.Conflict, rollback.Outcome);
        Assert.False(rollback.FullyRecovered);
        Assert.Equal("1", scope.Data(T2bCatalog.SearchboxTaskbarModeValue));
    }

    [Fact]
    public async Task DistributionDisabledModeBlocksAllNineOperationsInTheBackend()
    {
        using var scope = new T2bScope(realExecutionEnabled: false);
        scope.Access.SeedDWord(scope.Target(T2bCatalog.HiddenValue), 2);
        scope.Access.SeedDWord(scope.Target(T2bCatalog.ShowTaskViewButtonValue), 1);
        scope.Access.SeedDWord(scope.SearchTarget(), 3);

        var views = await scope.Manager.LoadAsync();

        Assert.Equal(9, views.Count);
        Assert.All(views, view => Assert.False(view.CanExecute));
        Assert.All(views, view => Assert.False(view.RealExecutionEnabled));
        Assert.All(views, view => Assert.Contains(BlockerCode.PolicyConflict, view.Blockers));
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task RestrictivePolicyBlocksOnlyTheAffectedNewOperation()
    {
        using var scope = new T2bScope();
        scope.Policy.BlockedOperationIds.Add(T2bCatalog.ShowHiddenFilesId);
        scope.Access.SeedDWord(scope.Target(T2bCatalog.HiddenValue), 2);
        scope.Access.SeedDWord(scope.SearchTarget(), 2);

        var hidden = await scope.Engine.SimulateAsync(T2bCatalog.ShowHiddenFilesId);
        var search = await scope.Engine.SimulateAsync(T2bCatalog.SearchIconOnlyId);

        Assert.False(hidden.CanExecute);
        Assert.Contains(BlockerCode.PolicyConflict, hidden.Blockers);
        Assert.True(search.CanExecute);
    }

    [Fact]
    public async Task VerificationDivergenceForHiddenFilesIsNeverReportedAsSuccess()
    {
        using var scope = new T2bScope();
        var target = scope.Target(T2bCatalog.HiddenValue);
        scope.Access.SeedDWord(target, 2);
        scope.Access.AfterWrite = written =>
        {
            if (written.Identity == target.Identity) scope.Access.SeedDWord(target, 2);
        };

        var result = await scope.ExecuteAsync(T2bCatalog.ShowHiddenFilesId);

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.VerificationFailed, result.Blockers);
        Assert.Equal(TransactionState.VerificationFailed, result.Transaction!.State);
    }

    [Fact]
    public async Task JournalTextDistinguishesTheRealHkcuEffectFromASyntheticOne()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(scope.Target(T2bCatalog.HiddenValue), 2);
        var execution = await scope.ExecuteAsync(T2bCatalog.ShowHiddenFilesId);
        var journal = string.Join("\n", execution.Transaction!.Events.Select(item =>
            (item.Detail ?? string.Empty) + " " +
            string.Join(" ", item.Results?.SelectMany(result => result.Effects) ?? [])));

        Assert.DoesNotContain("simulado", journal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sintétic", journal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HKCU", journal);
    }

    [Fact]
    public async Task OutOfRangeObservedSearchModeIsPreservedAndRestored()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(scope.SearchTarget(), 9);
        var execution = await scope.ExecuteAsync(T2bCatalog.SearchIconOnlyId);
        var transactionId = execution.Transaction!.TransactionId;

        Assert.True(execution.Succeeded);
        Assert.Equal("9", execution.Transaction.Backup!.Entries.Single().Value!.Data);
        Assert.Equal("1", scope.Data(T2bCatalog.SearchboxTaskbarModeValue));

        var rollback = await scope.Recovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.True(rollback.FullyRecovered);
        Assert.Equal("9", scope.Data(T2bCatalog.SearchboxTaskbarModeValue));
    }

    [Fact]
    public async Task MaximumBuildBelowTheObservedBuildBlocksTheOperation()
    {
        var operation = T2bCatalog.Create().Get(T2bCatalog.HideTaskViewButtonId) with
        {
            Compatibility = new OperationCompatibility("22000", "22621", true)
        };
        using var scope = new T2bScope(catalog: new OperationCatalog("t2b-max", [operation]), build: 22631);

        var simulation = await scope.Engine.SimulateAsync(T2bCatalog.HideTaskViewButtonId);

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.UnsupportedWindows, simulation.Blockers);
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task JournalFailureBeforeEffectLeavesNoWriteForANewOperation()
    {
        using var scope = new T2bScope();
        var target = scope.SearchTarget();
        scope.Access.SeedDWord(target, 3);
        var journal = new FaultingJournal(new MemoryStore(scope.Root))
        {
            FailOn = TransactionState.Planned
        };
        scope.Rebuild(journal);

        var result = await scope.ExecuteAsync(T2bCatalog.HideTaskbarSearchId);

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.JournalUnavailable, result.Blockers);
        Assert.Equal(0, scope.Access.Writes);
        Assert.Equal("3", scope.Data(T2bCatalog.SearchboxTaskbarModeValue));
    }

    [Fact]
    public async Task AbsentTaskViewValueIsCreatedAndRecoveryDeletesIt()
    {
        using var scope = new T2bScope();
        var target = scope.Target(T2bCatalog.ShowTaskViewButtonValue);
        scope.Access.SeedAbsentValue(target);
        var execution = await scope.ExecuteAsync(T2bCatalog.HideTaskViewButtonId);
        var transactionId = execution.Transaction!.TransactionId;

        Assert.True(execution.Succeeded);
        Assert.Equal("0", scope.Data(T2bCatalog.ShowTaskViewButtonValue));

        var rollback = await scope.Recovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.True(rollback.FullyRecovered);
        Assert.False(scope.Access.State(target).Exists);
    }

    [Fact]
    public async Task GenericConfirmationIsRefusedByTheBackendForAValidPlan()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(scope.Target(T2bCatalog.HiddenValue), 2);
        var simulation = await scope.Engine.SimulateAsync(T2bCatalog.ShowHiddenFilesId);

        Assert.True(simulation.CanExecute);
        Assert.Throws<InvalidOperationException>(() => scope.Engine.Authorize(simulation, "autorizo"));
        Assert.Throws<InvalidOperationException>(() => scope.Engine.Authorize(simulation,
            "AUTORIZAR SC-TW-EXP-06 000000000000"));
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task AuthorizeRechecksPolicyInsteadOfTrustingTheSimulationObject()
    {
        using var scope = new T2bScope(realExecutionEnabled: false);
        scope.Access.SeedDWord(scope.Target(T2bCatalog.HiddenValue), 2);
        var simulation = await scope.Engine.SimulateAsync(T2bCatalog.ShowHiddenFilesId);
        Assert.False(simulation.CanExecute);

        var forged = simulation with { CanExecute = true, Blockers = [] };

        Assert.Throws<InvalidOperationException>(() => scope.Engine.Authorize(forged,
            TweakManager.BuildConfirmationText(simulation.Plan)));
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task AuthorizeRefusesAPlanWhoseProtectionDivergesFromTheCatalog()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(scope.SearchTarget(), 2);
        var simulation = await scope.Engine.SimulateAsync(T2bCatalog.SearchFullBoxId);
        var plan = simulation.Plan;
        var protection = new ProtectionPolicy(true, true, false);
        var forged = plan with
        {
            Protection = protection,
            PlanHash = SafetyHashing.PlanHash(plan.PlanId, plan.TransactionId, plan.OperationId,
                plan.OperationVersion, plan.CatalogVersion, plan.Risk, plan.Scope, protection,
                plan.ObservedStateHash, plan.Changes)
        };

        Assert.Throws<InvalidOperationException>(() =>
            scope.Engine.Authorize(simulation with { Plan = forged },
                TweakManager.BuildConfirmationText(forged)));
        Assert.Equal(0, scope.Access.Writes);
    }
}
