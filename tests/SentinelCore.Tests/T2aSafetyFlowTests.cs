using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class T2aSafetyFlowTests
{
    [Fact]
    public async Task ShowExtensionsReadsObservedValueThenAppliesAndVerifies()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);

        var simulation = await scope.Engine.SimulateAsync(T2aCatalog.ShowFileExtensionsId);
        var change = Assert.Single(simulation.Plan.Changes);

        Assert.True(simulation.CanExecute);
        Assert.Equal(new TargetValueState(true, RegistryValueKinds.DWord, "1"), change.Before);
        Assert.Equal(new TargetValueState(true, RegistryValueKinds.DWord, "0"), change.After);
        Assert.False(simulation.HasEffects);

        var token = scope.Engine.Authorize(simulation, TweakManager.BuildConfirmationText(simulation.Plan));
        var result = await scope.Engine.ExecuteAsync(simulation.Plan, token);

        Assert.True(result.Succeeded);
        Assert.Equal("0", scope.Access.Data(target));
        var transaction = Assert.IsType<TransactionRecord>(result.Transaction);
        Assert.True(transaction.Verification!.Passed);
        Assert.Equal("1", transaction.Backup!.Entries.Single().Value!.Data);
        Assert.Equal(["Planned", "PreflightPassed", "BackupPrepared", "BackupVerified", "Authorized",
            "ExecutionStarted", "ExecutionCompleted", "VerificationPassed"],
            transaction.Events.Select(item => item.State.ToString()));
    }

    [Fact]
    public async Task AbsentValueIsCreatedAndItsAbsenceIsPreservedInBackup()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedAbsentValue(target);

        var result = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);

        Assert.True(result.Succeeded);
        Assert.Equal("0", scope.Access.Data(target));
        var before = result.Transaction!.Plan!.Changes.Single().Before;
        Assert.False(before.Exists);
        Assert.False(result.Transaction.Backup!.Entries.Single().Value!.Exists);
    }

    [Fact]
    public async Task TaskbarAlignmentIsAppliedOnlyInHkcu()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.TaskbarAlValue);
        scope.Access.SeedDWord(target, 1);

        var result = await scope.ExecuteAsync(T2aCatalog.AlignTaskbarLeftId);

        Assert.True(result.Succeeded);
        Assert.Equal("0", scope.Access.Data(target));
        Assert.Equal(T2aCatalog.ExplorerAdvancedPath, result.Transaction!.Plan!.Changes.Single().Target.Path);
        Assert.Equal(OperationTargetRoots.CurrentUser, result.Transaction.Plan.Changes.Single().Target.Root);
    }

    [Fact]
    public async Task DarkModePreservesAndVerifiesBothValuesSeparately()
    {
        using var scope = new T2aScope();
        var apps = scope.Target(T2aCatalog.AppsUseLightThemeValue);
        var system = scope.Target(T2aCatalog.SystemUsesLightThemeValue);
        scope.Access.SeedDWord(apps, 1);
        scope.Access.SeedDWord(system, 1);

        var simulation = await scope.Engine.SimulateAsync(T2aCatalog.EnableDarkModeId);
        Assert.Equal(2, simulation.Plan.Changes.Count);
        var token = scope.Engine.Authorize(simulation, TweakManager.BuildConfirmationText(simulation.Plan));
        var result = await scope.Engine.ExecuteAsync(simulation.Plan, token);

        Assert.True(result.Succeeded);
        Assert.Equal("0", scope.Access.Data(apps));
        Assert.Equal("0", scope.Access.Data(system));
        Assert.Equal(2, result.Transaction!.Backup!.Entries.Count);
        Assert.Equal(2, result.Transaction.Verification!.Steps.Count(item => item.Verified));
    }

    [Fact]
    public async Task DarkModePartialFailureKeepsEachStepExplicit()
    {
        using var scope = new T2aScope();
        var apps = scope.Target(T2aCatalog.AppsUseLightThemeValue);
        var system = scope.Target(T2aCatalog.SystemUsesLightThemeValue);
        scope.Access.SeedDWord(apps, 1);
        scope.Access.SeedDWord(system, 1);
        scope.Access.DenyWrite(system);

        var result = await scope.ExecuteAsync(T2aCatalog.EnableDarkModeId);

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.ExecutionFailed, result.Blockers);
        Assert.Equal(TransactionState.ExecutionFailed, result.Transaction!.State);
        Assert.Equal("0", scope.Access.Data(apps));
        Assert.Equal("1", scope.Access.Data(system));
        Assert.Contains(result.Transaction.Steps, step => step.Applied);
        Assert.Contains(result.Transaction.Steps, step => !step.Applied);
    }

    [Fact]
    public async Task StateChangedAfterSimulationBlocksExecutionWithoutWrite()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var (simulation, token) = await scope.PrepareAsync(T2aCatalog.ShowFileExtensionsId);
        scope.Access.SeedDWord(target, 0);

        var result = await scope.Engine.ExecuteAsync(simulation.Plan, token);

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.StateChanged, result.Blockers);
        Assert.Equal(0, scope.Access.Writes);
        Assert.Equal(TransactionState.Blocked, result.Transaction!.State);
    }

    [Fact]
    public async Task ExpiredAuthorizationIsRejectedWithoutEffect()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var (simulation, token) = await scope.PrepareAsync(T2aCatalog.ShowFileExtensionsId);
        scope.Time.Now = scope.Time.Now.AddMinutes(3);

        var result = await scope.Engine.ExecuteAsync(simulation.Plan, token);

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.AuthorizationExpired, result.Blockers);
        Assert.Equal("1", scope.Access.Data(target));
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task ReusedAuthorizationAndPlanAreRejected()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var (simulation, token) = await scope.PrepareAsync(T2aCatalog.ShowFileExtensionsId);
        var first = await scope.Engine.ExecuteAsync(simulation.Plan, token);
        var writesAfterFirst = scope.Access.Writes;

        var second = await scope.Engine.ExecuteAsync(simulation.Plan, token);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Contains(BlockerCode.AuthorizationReused, second.Blockers);
        Assert.Equal(writesAfterFirst, scope.Access.Writes);
    }

    [Fact]
    public async Task BackupFailureBlocksBeforeAuthorizationConsumption()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var journal = new FaultingJournal(new MemoryStore(scope.Root))
        {
            FailOn = TransactionState.BackupPrepared
        };
        scope.Rebuild(journal);

        var result = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.BackupFailed, result.Blockers);
        Assert.Equal("1", scope.Access.Data(target));
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task JournalFailureBeforeEffectLeavesNoWrite()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var journal = new FaultingJournal(new MemoryStore(scope.Root))
        {
            FailOn = TransactionState.Planned
        };
        scope.Rebuild(journal);

        var result = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.JournalUnavailable, result.Blockers);
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task VerificationDivergenceIsNeverReportedAsSuccess()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        scope.Access.AfterWrite = written =>
        {
            if (written.Identity == target.Identity) scope.Access.SeedDWord(target, 1);
        };

        var result = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.VerificationFailed, result.Blockers);
        Assert.Equal(TransactionState.VerificationFailed, result.Transaction!.State);
        Assert.False(result.Transaction.Verification!.Passed);
    }

    [Fact]
    public async Task UnexpectedValueKindBlocksWithNoConversion()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedString(target, "");

        var simulation = await scope.Engine.SimulateAsync(T2aCatalog.ShowFileExtensionsId);

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.Unrecoverable, simulation.Blockers);
        Assert.Equal(RegistryValueKinds.String, simulation.Plan.Changes.Single().Before.Kind);
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task AccessDeniedIsReportedAsLimitationAndBlocker()
    {
        using var scope = new T2aScope();
        scope.Access.DenyRead(scope.Target(T2aCatalog.HideFileExtValue));

        var simulation = await scope.Engine.SimulateAsync(T2aCatalog.ShowFileExtensionsId);

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.AccessDenied, simulation.Blockers);
        Assert.Contains(simulation.Limitations, item => item.Contains("negado", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingKeyIsBlockedBecauseTheAdapterDoesNotCreateKeys()
    {
        using var scope = new T2aScope();
        scope.Access.SeedMissingKey(scope.Target(T2aCatalog.HideFileExtValue));

        var simulation = await scope.Engine.SimulateAsync(T2aCatalog.ShowFileExtensionsId);

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.Unrecoverable, simulation.Blockers);
    }

    [Fact]
    public async Task DistributionDisabledModeBlocksAllWritesInTheBackend()
    {
        using var scope = new T2aScope(realExecutionEnabled: false);
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);

        var views = await scope.Manager.LoadAsync();
        var view = views.Single(item => item.OperationId == T2aCatalog.ShowFileExtensionsId);

        Assert.All(views, item => Assert.False(item.CanExecute));
        Assert.All(views, item => Assert.False(item.RealExecutionEnabled));
        Assert.Contains(BlockerCode.PolicyConflict, view.Blockers);
        var simulation = await scope.Manager.SimulateAsync(T2aCatalog.ShowFileExtensionsId);
        Assert.Throws<InvalidOperationException>(() => scope.Manager.Authorize(simulation, "autorizo"));
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task RestrictivePolicyBlocksDarkModeOnly()
    {
        using var scope = new T2aScope();
        scope.Policy.BlockedOperationIds.Add(T2aCatalog.EnableDarkModeId);
        scope.Access.SeedDWord(scope.Target(T2aCatalog.HideFileExtValue), 1);
        scope.Access.SeedDWord(scope.Target(T2aCatalog.AppsUseLightThemeValue), 1);
        scope.Access.SeedDWord(scope.Target(T2aCatalog.SystemUsesLightThemeValue), 1);

        var dark = await scope.Engine.SimulateAsync(T2aCatalog.EnableDarkModeId);
        var extensions = await scope.Engine.SimulateAsync(T2aCatalog.ShowFileExtensionsId);

        Assert.False(dark.CanExecute);
        Assert.Contains(BlockerCode.PolicyConflict, dark.Blockers);
        Assert.True(extensions.CanExecute);
    }

    [Fact]
    public async Task RealJournalDoesNotClaimASimulatedOrSyntheticEffect()
    {
        using var scope = new T2aScope();
        scope.Access.SeedDWord(scope.Target(T2aCatalog.HideFileExtValue), 1);
        var execution = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);
        var journal = string.Join("\n", execution.Transaction!.Events.Select(item =>
            (item.Detail ?? string.Empty) + " " +
            string.Join(" ", item.Results?.SelectMany(result => result.Effects) ?? [])));

        Assert.DoesNotContain("simulado", journal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sintétic", journal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HKCU", journal);
    }

    [Fact]
    public async Task NoOpValueIsStillVerifiedButChangedIsFalse()
    {
        using var scope = new T2aScope();
        scope.Access.SeedDWord(scope.Target(T2aCatalog.HideFileExtValue), 0);

        var result = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);

        Assert.True(result.Succeeded);
        var step = Assert.Single(result.Transaction!.Steps);
        Assert.False(step.Changed);
        Assert.True(step.Applied);
        Assert.True(step.Verified);
        Assert.True(result.Transaction.Verification!.Passed);
    }

    [Fact]
    public async Task AuthorizeRefusesASelfConsistentPlanOutsideTheCatalog()
    {
        using var scope = new T2aScope();
        scope.Access.SeedDWord(scope.Target(T2aCatalog.HideFileExtValue), 1);
        var simulation = await scope.Engine.SimulateAsync(T2aCatalog.ShowFileExtensionsId);
        var plan = simulation.Plan;
        var forgedTarget = new OperationTarget(OperationTargetRoots.CurrentUser,
            T2aCatalog.ExplorerAdvancedPath, "ForaDoCatalogo");
        var changes = new[]
        {
            plan.Changes[0] with { TargetIdentity = forgedTarget.Identity, Target = forgedTarget }
        };
        var forged = plan with
        {
            Changes = changes,
            PlanHash = SafetyHashing.PlanHash(plan.PlanId, plan.TransactionId, plan.OperationId,
                plan.OperationVersion, plan.CatalogVersion, plan.Risk, plan.Scope, plan.Protection,
                plan.ObservedStateHash, changes)
        };
        var forgedSimulation = simulation with { Plan = forged };

        Assert.Throws<InvalidOperationException>(() =>
            scope.Engine.Authorize(forgedSimulation, TweakManager.BuildConfirmationText(forged)));
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public async Task PolicyProbeFailureBlocksEveryTweak()
    {
        using var scope = new T2aScope();
        scope.Policy.Throw = true;
        scope.Access.SeedDWord(scope.Target(T2aCatalog.HideFileExtValue), 1);

        var simulation = await scope.Engine.SimulateAsync(T2aCatalog.ShowFileExtensionsId);

        Assert.False(simulation.CanExecute);
        Assert.Contains(BlockerCode.PolicyConflict, simulation.Blockers);
        Assert.Equal(0, scope.Access.Writes);
    }

    [Fact]
    public void RebuildPreservesTheDisabledComposition()
    {
        using var scope = new T2aScope(realExecutionEnabled: false);

        scope.Rebuild(new MemoryStore(scope.Root));

        Assert.Equal(TweakExecutionMode.RealDisabled, scope.Manager.Mode);
        Assert.False(scope.Manager.RealExecutionEnabled);
    }

    [Fact]
    public async Task DisabledExecutorNeverTouchesAnyTarget()
    {
        var executor = new DisabledOperationExecutor();
        var target = HkcuTargetPolicy.AllowedTargets[0];
        var request = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(), T2aCatalog.ShowFileExtensionsId,
            1, ChangeKind.SetValue, target, new TargetValueState(true, RegistryValueKinds.DWord, "1"),
            new TargetValueState(true, RegistryValueKinds.DWord, "0"));

        var outcome = await executor.ApplyAsync(request);

        Assert.False(outcome.Applied);
        Assert.Contains("desabilitada", outcome.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransactionReportDescribesARealHkcuOperationWithoutSyntheticDisclaimer()
    {
        using var scope = new T2aScope();
        scope.Access.SeedDWord(scope.Target(T2aCatalog.HideFileExtValue), 1);
        var execution = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);
        var transactionId = execution.Transaction!.TransactionId;

        var report = File.ReadAllText(scope.Store.GetTransactionReportPath(transactionId));

        Assert.Contains("Operação controlada em HKCU", report);
        Assert.DoesNotContain("Nenhuma alteração real do Windows foi executada", report);
        Assert.Contains("SC-TW-EXP-05", report);
    }

    [Fact]
    public async Task HistoryContainsOnlyT2aTransactionsInNewestFirstOrder()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var first = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);
        scope.Time.Now = scope.Time.Now.AddMinutes(1);
        scope.Access.SeedDWord(target, 1);
        var second = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);

        var history = await scope.Manager.LoadHistoryAsync();

        Assert.Equal(2, history.Count);
        Assert.Equal(second.Transaction!.TransactionId, history[0].TransactionId);
        Assert.Equal(first.Transaction!.TransactionId, history[1].TransactionId);
        Assert.All(history, item => Assert.Contains(item.OperationId, T2aCatalog.OperationIds));
    }
}
