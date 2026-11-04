using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class SafetyValidationFlowTests
{
    [Fact]
    public async Task CompleteSyntheticFlowCoversObservationBackupAuthorizationExecutionAndRollback()
    {
        using var scope = new SafetyScope();

        scope.Seed("Preference.Test", "A");
        var observed = await scope.Targets.ReadAsync(scope.Target("Preference.Test"));
        Assert.True(observed.IsKnown);
        Assert.Equal("A", observed.Value!.Data);

        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-01");
        Assert.True(simulation.CanExecute);
        Assert.False(simulation.HasEffects);

        var backup = new BackupService(scope.Store, scope.Time).Create(simulation.Plan,
            [observed], 1);
        Assert.True(backup.IsComplete);
        Assert.True(BackupService.ValidateBackup(backup, simulation.Plan).Valid);
        Assert.Equal("A", scope.Current("Preference.Test"));

        var token = scope.Engine.Authorize(simulation, "confirmacao-flow");
        var execution = await scope.Engine.ExecuteAsync(simulation.Plan, token);
        Assert.True(execution.Succeeded);
        Assert.Equal("B", scope.Current("Preference.Test"));
        Assert.True(execution.Transaction!.Verification!.Passed);

        var reopened = new MemoryStore(scope.Root);
        var persisted = await reopened.FindTransactionAsync(simulation.Plan.TransactionId);
        Assert.NotNull(persisted);
        Assert.True(persisted!.Succeeded);

        var recovery = new RecoveryService(reopened, scope.Targets, scope.Executor, scope.Locks, scope.Time);
        var preview = await recovery.PreviewAsync(simulation.Plan.TransactionId);
        Assert.True(preview.CanRecover);
        Assert.Empty(preview.Conflicts);

        var rollback = await recovery.RollbackAsync(simulation.Plan.TransactionId,
            new RecoveryOptions("confirmacao-rollback"));
        Assert.Equal(RecoveryOutcome.Completed, rollback.Outcome);
        Assert.Equal("A", scope.Current("Preference.Test"));

        var final = await reopened.FindTransactionAsync(simulation.Plan.TransactionId);
        Assert.Equal(TransactionState.RecoveryCompleted, final!.State);
        Assert.Contains("RecoveryCompleted", File.ReadAllText(
            Path.Combine(scope.Root, "memory", "RECOVERY.md")));
        Assert.True(File.Exists(reopened.GetTransactionReportPath(simulation.Plan.TransactionId)));
    }

    [Fact]
    public async Task FlowWithJournalFailureBeforeEffectDoesNotChangeTarget()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        scope.Journal = new FaultingJournal(scope.Store) { FailOn = TransactionState.Planned };
        scope.Rebuild();
        var (simulation, token) = await scope.PrepareAsync("SYN-PREF-01");

        var execution = await scope.Engine.ExecuteAsync(simulation.Plan, token);

        Assert.False(execution.Succeeded);
        Assert.Contains(BlockerCode.JournalUnavailable, execution.Blockers);
        Assert.Equal("A", scope.Current("Preference.Test"));
        Assert.Null(execution.Transaction);
    }

    [Fact]
    public async Task FlowWithCorruptedBackupBlocksRecovery()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var execution = await scope.ExecuteAsync("SYN-PREF-01");
        Assert.True(execution.Succeeded);
        var transactionId = execution.Transaction!.TransactionId;
        var backupPath = Path.Combine(scope.Root, "memory", "transactions", transactionId.ToString("D"),
            "backup-01.md");
        File.WriteAllText(backupPath, File.ReadAllText(backupPath).Replace("\"A\"", "\"X\""));

        var rollback = await scope.Recovery.RollbackAsync(transactionId,
            new RecoveryOptions("confirmacao"));

        Assert.Equal(RecoveryOutcome.NotRecoverable, rollback.Outcome);
        Assert.False(rollback.FullyRecovered);
        Assert.Equal("B", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task FlowWithUnexpectedInterruptionLeavesIndeterminateStateAndBlocksRetry()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        scope.Journal = new FaultingJournal(scope.Store)
        {
            FailOnStates = [TransactionState.VerificationPassed]
        };
        scope.Rebuild();
        var (simulation, token) = await scope.PrepareAsync("SYN-PREF-01");

        var execution = await scope.Engine.ExecuteAsync(simulation.Plan, token);

        Assert.False(execution.Succeeded);
        Assert.True(execution.Transaction!.IsIndeterminate);
        Assert.Equal("B", scope.Current("Preference.Test"));

        scope.Journal = scope.Store;
        scope.Rebuild();
        var retry = await scope.ExecuteAsync("SYN-PREF-01");

        Assert.False(retry.Succeeded);
        Assert.Contains(BlockerCode.IndeterminateState, retry.Blockers);
    }

    [Fact]
    public async Task FlowWithConcurrentAlterationDetectsConflictAndPreservesCurrentState()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var execution = await scope.ExecuteAsync("SYN-PREF-01");
        Assert.True(execution.Succeeded);

        scope.Seed("Preference.Test", "C");
        var preview = await scope.Recovery.PreviewAsync(execution.Transaction!.TransactionId);
        var rollback = await scope.Recovery.RollbackAsync(execution.Transaction.TransactionId,
            new RecoveryOptions("confirmacao"));

        Assert.Equal(RecoveryOutcome.Conflict, preview.Outcome);
        Assert.Equal(RecoveryOutcome.Conflict, rollback.Outcome);
        Assert.Equal("C", scope.Current("Preference.Test"));
    }
}
