using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class SafetyExecutionTests
{
    [Fact]
    public async Task SuccessfulFlowPersistsOrderedJournalAndVerifiedResult()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-01");
        var token = scope.Engine.Authorize(simulation, "confirmacao");

        var result = await scope.Engine.ExecuteAsync(simulation.Plan, token);

        Assert.True(result.Succeeded);
        Assert.Equal("B", scope.Current("Preference.Test"));
        var transaction = Assert.IsType<TransactionRecord>(result.Transaction);
        Assert.Equal(TransactionState.VerificationPassed, transaction.State);
        Assert.True(transaction.Succeeded);
        Assert.False(transaction.IsIndeterminate);
        Assert.Equal(["Planned", "PreflightPassed", "BackupPrepared", "BackupVerified", "Authorized",
            "ExecutionStarted", "ExecutionCompleted", "VerificationPassed"],
            transaction.Events.Select(item => item.State.ToString()));
        Assert.Equal("A", transaction.Backup!.Entries.Single().Value!.Data);
        Assert.True(transaction.Verification!.Passed);
    }

    [Fact]
    public async Task JournalAndReportsAreRecoveredAfterReopen()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var result = await scope.ExecuteAsync("SYN-PREF-01");
        var transactionId = result.Transaction!.TransactionId;

        var reopened = new MemoryStore(scope.Root);
        var snapshot = await reopened.LoadTransactionsAsync();

        Assert.Empty(snapshot.Warnings);
        var record = Assert.Single(snapshot.Transactions);
        Assert.Equal(transactionId, record.TransactionId);
        Assert.True(record.Succeeded);
        Assert.True(File.Exists(reopened.GetTransactionReportPath(transactionId)));
        Assert.True(File.Exists(Path.Combine(scope.Root, "memory", "RECOVERY.md")));
        Assert.True(Directory.Exists(Path.Combine(scope.Root, "memory", "transactions",
            transactionId.ToString("D"), "journal")));
    }

    [Fact]
    public async Task InitialAbsentValueIsCreated()
    {
        using var scope = new SafetyScope();
        scope.SeedAbsent("Preference.Test");

        var result = await scope.ExecuteAsync("SYN-PREF-01");

        Assert.True(result.Succeeded);
        Assert.Equal("B", scope.Current("Preference.Test"));
        Assert.False(result.Transaction!.Plan!.Changes[0].Before.Exists);
    }

    [Fact]
    public async Task EmptyInitialValueIsReplaced()
    {
        using var scope = new SafetyScope();
        scope.SeedEmpty("Preference.Empty");

        var result = await scope.ExecuteAsync("SYN-PREF-02");

        Assert.True(result.Succeeded);
        Assert.Equal("value", scope.Current("Preference.Empty"));
    }

    [Fact]
    public async Task DeleteValueRemovesValueAndKeepsTarget()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Delete", "A");

        var result = await scope.ExecuteAsync("SYN-PREF-03");

        Assert.True(result.Succeeded);
        var observed = scope.Targets.Read(scope.Target("Preference.Delete"));
        Assert.True(observed.TargetExists);
        Assert.False(observed.Value!.Exists);
    }

    [Fact]
    public async Task DeleteKeyRemovesTarget()
    {
        using var scope = new SafetyScope();
        var key = new OperationTarget("Synthetic", "Preference.Key", null);
        scope.Targets.Set(key, new TargetValueState(true, "String", "x"));

        var result = await scope.ExecuteAsync("SYN-PREF-97");

        Assert.True(result.Succeeded);
        Assert.False(scope.Targets.Read(key).TargetExists);
    }

    [Fact]
    public async Task ExpiredAuthorizationIsRejectedWithoutEffect()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var (simulation, token) = await scope.PrepareAsync("SYN-PREF-01");
        scope.Time.Now = scope.Time.Now.AddMinutes(3);

        var result = await scope.Engine.ExecuteAsync(simulation.Plan, token);

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.AuthorizationExpired, result.Blockers);
        Assert.Equal("A", scope.Current("Preference.Test"));
        Assert.Equal(TransactionState.Blocked, result.Transaction!.State);
    }

    [Fact]
    public async Task ReusedAuthorizationIsRejected()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var (simulation, token) = await scope.PrepareAsync("SYN-PREF-01");
        Assert.True((await scope.Engine.ExecuteAsync(simulation.Plan, token)).Succeeded);

        var second = await scope.Engine.ExecuteAsync(simulation.Plan, token);

        Assert.False(second.Succeeded);
        Assert.Contains(BlockerCode.AuthorizationReused, second.Blockers);
        Assert.Equal("B", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task ChangedPlanIsRejected()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var (simulation, token) = await scope.PrepareAsync("SYN-PREF-01");
        var forged = simulation.Plan with { PlanId = Guid.NewGuid() };

        var result = await scope.Engine.ExecuteAsync(forged, token);

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.PlanMismatch, result.Blockers);
        Assert.Equal("A", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task StateChangedAfterSimulationIsRejected()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var (simulation, token) = await scope.PrepareAsync("SYN-PREF-01");
        scope.Seed("Preference.Test", "C");

        var result = await scope.Engine.ExecuteAsync(simulation.Plan, token);

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.StateChanged, result.Blockers);
        Assert.Equal("C", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task HighRiskCannotBeAuthorizedEvenWithForgedPlan()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        scope.Seed("Preference.High", "A");
        var high = await scope.Engine.SimulateAsync("SYN-PREF-90");
        Assert.False(high.CanExecute);
        Assert.Throws<InvalidOperationException>(() => scope.Engine.Authorize(high, "x"));

        var (low, token) = await scope.PrepareAsync("SYN-PREF-01");
        var forged = low.Plan with { OperationId = "SYN-PREF-90" };
        var result = await scope.Engine.ExecuteAsync(forged, token);

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.RiskBlocked, result.Blockers);
        Assert.Equal("A", scope.Current("Preference.High"));
    }

    [Fact]
    public async Task UnknownPlanOperationIsRejected()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var (simulation, token) = await scope.PrepareAsync("SYN-PREF-01");
        var forged = simulation.Plan with { OperationId = "NAO-EXISTE" };

        var result = await scope.Engine.ExecuteAsync(forged, token);

        Assert.Contains(BlockerCode.TargetOutsideCatalog, result.Blockers);
    }

    [Fact]
    public async Task CatalogVersionAndContentDivergenceAreRejected()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var (simulation, token) = await scope.PrepareAsync("SYN-PREF-01");

        var version = await scope.Engine.ExecuteAsync(
            simulation.Plan with { CatalogVersion = "catalogo-antigo" }, token);
        var content = await scope.Engine.ExecuteAsync(
            simulation.Plan with { CatalogContentHash = new string('0', 64) }, token);

        Assert.Contains(BlockerCode.PlanMismatch, version.Blockers);
        Assert.Contains(BlockerCode.PlanMismatch, content.Blockers);
        Assert.Equal("A", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task JournalFailureBeforeEffectLeavesTargetUntouched()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        scope.Journal = new FaultingJournal(scope.Store) { FailOn = TransactionState.PreflightPassed };
        scope.Rebuild();
        var (simulation, token) = await scope.PrepareAsync("SYN-PREF-01");

        var result = await scope.Engine.ExecuteAsync(simulation.Plan, token);

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.JournalUnavailable, result.Blockers);
        Assert.Equal("A", scope.Current("Preference.Test"));
        Assert.Equal(TransactionState.Interrupted, result.Transaction!.State);
        Assert.False(result.Transaction.IsIndeterminate);
    }

    [Fact]
    public async Task JournalFailureAfterEffectLeavesIndeterminateState()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        scope.Journal = new FaultingJournal(scope.Store) { FailOn = TransactionState.VerificationPassed };
        scope.Rebuild();
        var (simulation, token) = await scope.PrepareAsync("SYN-PREF-01");

        var result = await scope.Engine.ExecuteAsync(simulation.Plan, token);

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.IndeterminateState, result.Blockers);
        Assert.Equal("B", scope.Current("Preference.Test"));
        Assert.True(result.Transaction!.IsIndeterminate);
        Assert.Equal(TransactionState.Interrupted, result.Transaction.State);
    }

    [Fact]
    public async Task ForgedPlanWithConsistentHashIsRejectedByCatalogBinding()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-01");
        var changes = simulation.Plan.Changes
            .Select(change => change with { After = new TargetValueState(true, "String", "HACK") })
            .ToList();
        var forged = simulation.Plan with { Changes = changes };
        forged = forged with
        {
            PlanHash = SafetyHashing.PlanHash(forged.PlanId, forged.TransactionId, forged.OperationId,
                forged.OperationVersion, forged.CatalogVersion, forged.Risk, forged.Scope,
                forged.Protection, forged.ObservedStateHash, changes)
        };
        var forgedSimulation = simulation with { Plan = forged };

        Assert.Throws<InvalidOperationException>(() => scope.Engine.Authorize(forgedSimulation, "forjado"));
        Assert.Equal("A", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task ExecutorFailureBeforeEffectIsDeterminate()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var executor = new FaultingExecutor(scope.Executor)
        {
            Handler = _ => Task.FromResult(ExecutionOutcome.Failed("falha simulada antes do efeito"))
        };
        scope.Executor = executor;
        scope.Rebuild();

        var result = await scope.ExecuteAsync("SYN-PREF-01");

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.ExecutionFailed, result.Blockers);
        Assert.Equal("A", scope.Current("Preference.Test"));
        Assert.Equal(TransactionState.ExecutionFailed, result.Transaction!.State);
        Assert.False(result.Transaction.IsIndeterminate);
    }

    [Fact]
    public async Task IndeterminateExecutorFailureBlocksFurtherOperations()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        scope.Executor = new FaultingExecutor(scope.Executor)
        {
            Handler = _ => Task.FromResult(ExecutionOutcome.Unknown("efeito indeterminado"))
        };
        scope.Rebuild();
        var first = await scope.ExecuteAsync("SYN-PREF-01");
        Assert.True(first.Transaction!.IsIndeterminate);

        scope.Executor = new SyntheticOperationExecutor(scope.Targets);
        scope.Rebuild();
        var second = await scope.ExecuteAsync("SYN-PREF-01");

        Assert.False(second.Succeeded);
        Assert.Contains(BlockerCode.IndeterminateState, second.Blockers);
        Assert.Equal("A", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task ExceptionAfterEffectIsMarkedIndeterminate()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var inner = new SyntheticOperationExecutor(scope.Targets);
        scope.Executor = new FaultingExecutor(inner)
        {
            Handler = async request =>
            {
                await inner.ApplyAsync(request);
                throw new IOException("queda simulada após o efeito");
            }
        };
        scope.Rebuild();

        var result = await scope.ExecuteAsync("SYN-PREF-01");

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.IndeterminateState, result.Blockers);
        Assert.Equal("B", scope.Current("Preference.Test"));
        Assert.True(result.Transaction!.IsIndeterminate);
    }

    [Fact]
    public async Task VerificationMismatchIsNeverSuccess()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        scope.Executor = new FaultingExecutor(scope.Executor)
        {
            Handler = request =>
            {
                scope.Targets.Set(request.Target, new TargetValueState(true, "String", "ERRADO"));
                return Task.FromResult(ExecutionOutcome.Success());
            }
        };
        scope.Rebuild();

        var result = await scope.ExecuteAsync("SYN-PREF-01");

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.VerificationFailed, result.Blockers);
        Assert.Equal(TransactionState.VerificationFailed, result.Transaction!.State);
        Assert.False(result.Transaction.Verification!.Passed);
        Assert.Equal("ERRADO", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task MutationBetweenRevalidationAndEffectIsRejected()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var inner = new SyntheticOperationExecutor(scope.Targets);
        scope.Executor = new FaultingExecutor(inner)
        {
            Handler = request =>
            {
                scope.Targets.Set(request.Target, new TargetValueState(true, "String", "C"));
                return inner.ApplyAsync(request);
            }
        };
        scope.Rebuild();

        var result = await scope.ExecuteAsync("SYN-PREF-01");

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.ExecutionFailed, result.Blockers);
        Assert.Equal("C", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task CancellationPropagatesAndDoesNotDeclareSuccess()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        scope.Executor = new FaultingExecutor(scope.Executor)
        {
            Handler = _ => throw new OperationCanceledException()
        };
        scope.Rebuild();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ExecuteAsync("SYN-PREF-01"));
        Assert.Equal("A", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task NullPlanOrTokenIsRejected()
    {
        using var scope = new SafetyScope();

        var result = await scope.Engine.ExecuteAsync(null!, null!);

        Assert.False(result.Succeeded);
        Assert.Contains(BlockerCode.AuthorizationMissing, result.Blockers);
    }
}
