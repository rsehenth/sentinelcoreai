using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class SafetyRecoveryTests
{
    private static async Task<TransactionRecord> ExecutedAsync(SafetyScope scope,
        string operation = "SYN-PREF-01", string path = "Preference.Test", string value = "A")
    {
        scope.Seed(path, value);
        var result = await scope.ExecuteAsync(operation);
        Assert.True(result.Succeeded);
        return result.Transaction!;
    }

    private static RecoveryOptions Confirm() => new("confirmacao-recuperacao");

    [Fact]
    public async Task RollbackRestoresObservedStateAndVerifies()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        Assert.Equal("B", scope.Current("Preference.Test"));

        var preview = await scope.Recovery.PreviewAsync(transaction.TransactionId);
        Assert.True(preview.CanRecover);
        Assert.Equal(RecoveryOutcome.NotAttempted, preview.Outcome);
        Assert.Empty(preview.Conflicts);

        var result = await scope.Recovery.RollbackAsync(transaction.TransactionId, Confirm());

        Assert.Equal(RecoveryOutcome.Completed, result.Outcome);
        Assert.True(result.FullyRecovered);
        Assert.Equal("A", scope.Current("Preference.Test"));
        var reloaded = await scope.Store.FindTransactionAsync(transaction.TransactionId);
        Assert.NotNull(reloaded);
        Assert.Equal(TransactionState.RecoveryCompleted, reloaded!.State);
        Assert.Equal(RecoveryOutcome.Completed, reloaded.Recovery!.Outcome);
        Assert.False(reloaded.IsIndeterminate);
    }

    [Fact]
    public async Task RollbackWhenAlreadyRestoredIsIdempotent()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        scope.Seed("Preference.Test", "A");

        var result = await scope.Recovery.RollbackAsync(transaction.TransactionId, Confirm());

        Assert.Equal(RecoveryOutcome.AlreadyRestored, result.Outcome);
        Assert.True(result.FullyRecovered);
        Assert.DoesNotContain(result.Steps, item => item.Applied);
        Assert.Equal("A", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task ConflictWhenStateChangedAfterTransactionIsNotOverwritten()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        scope.Seed("Preference.Test", "C");

        var preview = await scope.Recovery.PreviewAsync(transaction.TransactionId);
        Assert.False(preview.CanRecover);
        Assert.Equal(RecoveryOutcome.Conflict, preview.Outcome);
        Assert.Equal(RecoveryConflictKind.StateChangedAfterTransaction, preview.Conflicts.Single().Kind);

        var result = await scope.Recovery.RollbackAsync(transaction.TransactionId, Confirm());

        Assert.Equal(RecoveryOutcome.Conflict, result.Outcome);
        Assert.False(result.FullyRecovered);
        Assert.Equal("C", scope.Current("Preference.Test"));
        var reloaded = await scope.Store.FindTransactionAsync(transaction.TransactionId);
        Assert.Equal(TransactionState.RecoveryFailed, reloaded!.State);
        Assert.False(reloaded.IsIndeterminate);
        Assert.Equal(RecoveryOutcome.Conflict, reloaded.Recovery!.Outcome);
    }

    [Fact]
    public async Task TargetMissingIsAConflict()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        scope.SeedAbsent("Preference.Test");

        var preview = await scope.Recovery.PreviewAsync(transaction.TransactionId);

        Assert.False(preview.CanRecover);
        Assert.Equal(RecoveryConflictKind.TargetMissing, preview.Conflicts.Single().Kind);
    }

    [Fact]
    public async Task KindChangedIsAConflictAndNotConverted()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        scope.SeedKind("Preference.Test", "Integer", "7");

        var preview = await scope.Recovery.PreviewAsync(transaction.TransactionId);
        var result = await scope.Recovery.RollbackAsync(transaction.TransactionId, Confirm());

        Assert.Equal(RecoveryOutcome.Conflict, result.Outcome);
        Assert.Equal(RecoveryConflictKind.KindChanged, preview.Conflicts.Single().Kind);
        var current = scope.Targets.Read(scope.Target("Preference.Test"));
        Assert.Equal("Integer", current.Value!.Kind);
        Assert.Equal("7", current.Value.Data);
    }

    [Fact]
    public async Task CorruptBackupBlocksRecovery()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        var path = Path.Combine(scope.Root, "memory", "transactions", $"{transaction.TransactionId:D}",
            "backup-01.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"A\"", "\"TAMPERED\""));

        var preview = await scope.Recovery.PreviewAsync(transaction.TransactionId);
        var result = await scope.Recovery.RollbackAsync(transaction.TransactionId, Confirm());

        Assert.Equal(RecoveryOutcome.NotRecoverable, preview.Outcome);
        Assert.Equal(RecoveryOutcome.NotRecoverable, result.Outcome);
        Assert.Equal("B", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task MissingBackupBlocksRecovery()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        File.Delete(Path.Combine(scope.Root, "memory", "transactions", $"{transaction.TransactionId:D}",
            "backup-01.md"));

        var preview = await scope.Recovery.PreviewAsync(transaction.TransactionId);

        Assert.Equal(RecoveryOutcome.NotRecoverable, preview.Outcome);
        Assert.Contains("ausente", preview.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PartialRecoveryIsPreservedAndNotReportedAsComplete()
    {
        var descriptor = SafetyTestData.TwoTargetOperation();
        using var scope = new SafetyScope(catalog: new OperationCatalog("multi-v1", [descriptor]));
        scope.Seed("Preference.MultiA", "A");
        scope.Seed("Preference.MultiB", "B");
        var execution = await scope.ExecuteAsync(descriptor.Id);
        Assert.True(execution.Succeeded);
        Assert.Equal("A2", scope.Current("Preference.MultiA"));
        Assert.Equal("B2", scope.Current("Preference.MultiB"));

        var inner = new SyntheticOperationExecutor(scope.Targets);
        var calls = 0;
        scope.Executor = new FaultingExecutor(inner)
        {
            Handler = request => Interlocked.Increment(ref calls) == 2
                ? Task.FromResult(ExecutionOutcome.Failed("falha simulada no segundo passo"))
                : inner.ApplyAsync(request)
        };
        scope.Rebuild();

        var result = await scope.Recovery.RollbackAsync(
            execution.Transaction!.TransactionId, Confirm());

        Assert.Equal(RecoveryOutcome.PartiallyCompleted, result.Outcome);
        Assert.False(result.FullyRecovered);
        Assert.True(result.EffectMayHaveOccurred);
        Assert.Equal("A", scope.Current("Preference.MultiA"));
        Assert.Equal("B2", scope.Current("Preference.MultiB"));
        var reloaded = await scope.Store.FindTransactionAsync(execution.Transaction.TransactionId);
        Assert.True(reloaded!.IsIndeterminate);
    }

    [Fact]
    public async Task InterruptedRecoveryCanBeRetriedAfterReopen()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        var sequence = transaction.Events.Max(item => item.Sequence) + 1;
        await AppendAsync(scope, transaction, sequence, TransactionState.RecoveryRequested);
        await AppendAsync(scope, transaction, sequence + 1, TransactionState.RecoveryStarted);

        var reopened = new MemoryStore(scope.Root);
        var recovery = new RecoveryService(reopened, scope.Targets, scope.Executor, scope.Locks, scope.Time);
        var preview = await recovery.PreviewAsync(transaction.TransactionId);
        Assert.True(preview.CanRecover);
        Assert.Equal(RecoveryOutcome.Interrupted, preview.Outcome);

        var result = await recovery.RollbackAsync(transaction.TransactionId, Confirm());

        Assert.Equal(RecoveryOutcome.Completed, result.Outcome);
        Assert.Equal("A", scope.Current("Preference.Test"));
        var reloaded = await reopened.FindTransactionAsync(transaction.TransactionId);
        Assert.Equal(TransactionState.RecoveryCompleted, reloaded!.State);
        Assert.False(reloaded.IsIndeterminate);
    }

    [Fact]
    public async Task RecoveryAfterReopenWorks()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        var reopened = new MemoryStore(scope.Root);
        var recovery = new RecoveryService(reopened, scope.Targets, scope.Executor, scope.Locks, scope.Time);

        var result = await recovery.RollbackAsync(transaction.TransactionId, Confirm());

        Assert.Equal(RecoveryOutcome.Completed, result.Outcome);
        Assert.Equal("A", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task BlockedTransactionWithoutEffectNeedsNoRollback()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var (simulation, token) = await scope.PrepareAsync("SYN-PREF-01");
        scope.Time.Now = scope.Time.Now.AddMinutes(3);
        var blocked = await scope.Engine.ExecuteAsync(simulation.Plan, token);
        Assert.False(blocked.Succeeded);

        var preview = await scope.Recovery.PreviewAsync(simulation.Plan.TransactionId);
        var result = await scope.Recovery.RollbackAsync(simulation.Plan.TransactionId, Confirm());

        Assert.Equal(RecoveryOutcome.AlreadyRestored, preview.Outcome);
        Assert.Equal(RecoveryOutcome.AlreadyRestored, result.Outcome);
        Assert.Equal("A", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task RecoveryWithoutConfirmationIsBlocked()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);

        var result = await scope.Recovery.RollbackAsync(transaction.TransactionId,
            new RecoveryOptions("  "));

        Assert.Equal(RecoveryOutcome.Blocked, result.Outcome);
        Assert.Contains("confirmação", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("B", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task RollbackRemovesKeyThatDidNotExistBefore()
    {
        using var scope = new SafetyScope();
        scope.SeedAbsent("Preference.Test");
        var execution = await scope.ExecuteAsync("SYN-PREF-01");
        Assert.True(execution.Succeeded);
        Assert.True(scope.Targets.Read(scope.Target("Preference.Test")).TargetExists);

        var result = await scope.Recovery.RollbackAsync(execution.Transaction!.TransactionId, Confirm());

        Assert.Equal(RecoveryOutcome.Completed, result.Outcome);
        Assert.False(scope.Targets.Read(scope.Target("Preference.Test")).TargetExists);
    }

    [Fact]
    public async Task RollbackRestoresEmptyValueExactly()
    {
        using var scope = new SafetyScope();
        scope.SeedEmpty("Preference.Empty");
        var execution = await scope.ExecuteAsync("SYN-PREF-02");
        Assert.True(execution.Succeeded);
        Assert.Equal("value", scope.Current("Preference.Empty"));

        var result = await scope.Recovery.RollbackAsync(execution.Transaction!.TransactionId, Confirm());

        Assert.Equal(RecoveryOutcome.Completed, result.Outcome);
        var value = scope.Targets.Read(scope.Target("Preference.Empty")).Value!;
        Assert.True(value.Exists);
        Assert.Equal("", value.Data);
    }

    [Fact]
    public async Task RecoveryRevalidatesBackupAtTheMomentOfUse()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        var journal = new TamperingJournal(scope.Store);
        scope.Journal = journal;
        scope.Rebuild();

        var result = await scope.Recovery.RollbackAsync(transaction.TransactionId, Confirm());

        Assert.Equal(RecoveryOutcome.NotRecoverable, result.Outcome);
        Assert.True(journal.BackupReads >= 2);
        Assert.Equal("B", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task StateChangeBetweenPreviewAndRollbackIsAConflict()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        var reads = 0;
        scope.Reader = new DelegatingTargetReader(_ => Task.FromResult(
            reads++ == 0
                ? new ObservedTargetState(scope.Key("Preference.Test"), true, true,
                    new TargetValueState(true, "String", "B"), scope.Time.Now, null)
                : new ObservedTargetState(scope.Key("Preference.Test"), true, true,
                    new TargetValueState(true, "String", "C"), scope.Time.Now, null)));
        scope.Rebuild();

        var result = await scope.Recovery.RollbackAsync(transaction.TransactionId, Confirm());

        Assert.Equal(RecoveryOutcome.Conflict, result.Outcome);
        Assert.Single(result.Conflicts);
        Assert.DoesNotContain(result.Steps, item => item.Applied);
        Assert.Equal("B", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task InterruptedBeforeEffectNeedsNoRollback()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        scope.Journal = new FaultingJournal(scope.Store) { FailOn = TransactionState.PreflightPassed };
        scope.Rebuild();
        var (simulation, token) = await scope.PrepareAsync("SYN-PREF-01");
        var execution = await scope.Engine.ExecuteAsync(simulation.Plan, token);
        Assert.False(execution.Succeeded);
        scope.Journal = scope.Store;
        scope.Rebuild();

        var preview = await scope.Recovery.PreviewAsync(simulation.Plan.TransactionId);
        var result = await scope.Recovery.RollbackAsync(simulation.Plan.TransactionId, Confirm());

        Assert.Equal(RecoveryOutcome.AlreadyRestored, preview.Outcome);
        Assert.Equal(RecoveryOutcome.AlreadyRestored, result.Outcome);
        Assert.Equal("A", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task PartialRecoveryCanBeCompletedOnRetry()
    {
        var descriptor = SafetyTestData.TwoTargetOperation();
        using var scope = new SafetyScope(catalog: new OperationCatalog("multi-v1", [descriptor]));
        scope.Seed("Preference.MultiA", "A");
        scope.Seed("Preference.MultiB", "B");
        var execution = await scope.ExecuteAsync(descriptor.Id);
        Assert.True(execution.Succeeded);
        var transactionId = execution.Transaction!.TransactionId;

        var inner = new SyntheticOperationExecutor(scope.Targets);
        var calls = 0;
        scope.Executor = new FaultingExecutor(inner)
        {
            Handler = request => Interlocked.Increment(ref calls) == 2
                ? Task.FromResult(ExecutionOutcome.Failed("falha simulada no segundo passo"))
                : inner.ApplyAsync(request)
        };
        scope.Rebuild();
        var partial = await scope.Recovery.RollbackAsync(transactionId, Confirm());
        Assert.Equal(RecoveryOutcome.PartiallyCompleted, partial.Outcome);
        var afterPartial = await scope.Store.FindTransactionAsync(transactionId);
        Assert.True(afterPartial!.IsIndeterminate);

        scope.Executor = inner;
        scope.Rebuild();
        var completed = await scope.Recovery.RollbackAsync(transactionId, Confirm());

        Assert.Equal(RecoveryOutcome.Completed, completed.Outcome);
        Assert.True(completed.FullyRecovered);
        var reloaded = await scope.Store.FindTransactionAsync(transactionId);
        Assert.False(reloaded!.IsIndeterminate);
        Assert.Equal("A", scope.Current("Preference.MultiA"));
        Assert.Equal("B", scope.Current("Preference.MultiB"));
    }

    [Fact]
    public async Task InterruptedAfterEffectRecoveryClearsIndeterminacy()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        scope.Journal = new FaultingJournal(scope.Store) { FailOn = TransactionState.VerificationPassed };
        scope.Rebuild();
        var (simulation, token) = await scope.PrepareAsync("SYN-PREF-01");
        var execution = await scope.Engine.ExecuteAsync(simulation.Plan, token);
        Assert.True(execution.Transaction!.IsIndeterminate);
        scope.Journal = scope.Store;
        scope.Rebuild();

        var completed = await scope.Recovery.RollbackAsync(simulation.Plan.TransactionId, Confirm());

        Assert.Equal(RecoveryOutcome.Completed, completed.Outcome);
        var reloaded = await scope.Store.FindTransactionAsync(simulation.Plan.TransactionId);
        Assert.False(reloaded!.IsIndeterminate);
        Assert.Equal("A", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task RollbackRechecksPostStateImmediatelyBeforeWrite()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        var external = await scope.Recovery.PreviewAsync(transaction.TransactionId);
        Assert.True(external.CanRecover);

        var reads = 0;
        scope.Reader = new DelegatingTargetReader(_ => Task.FromResult(
            reads++ == 0
                ? new ObservedTargetState(scope.Key("Preference.Test"), true, true,
                    new TargetValueState(true, "String", "B"), scope.Time.Now, null)
                : new ObservedTargetState(scope.Key("Preference.Test"), true, true,
                    new TargetValueState(true, "String", "C"), scope.Time.Now, null)));
        scope.Rebuild();

        var result = await scope.Recovery.RollbackAsync(transaction.TransactionId, Confirm());

        Assert.Equal(RecoveryOutcome.Conflict, result.Outcome);
        Assert.Single(result.Conflicts);
        Assert.DoesNotContain(result.Steps, item => item.Applied);
        Assert.Equal("B", scope.Current("Preference.Test"));
    }

    [Fact]
    public async Task RecoveryIsIndexedAndReported()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        await scope.Recovery.RollbackAsync(transaction.TransactionId, Confirm());

        var recoveryFile = Path.Combine(scope.Root, "memory", "RECOVERY.md");
        Assert.Contains("Completed", File.ReadAllText(recoveryFile));
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(scope.Root, "data", "sentinelcore.db")};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Recoveries";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    private static Task AppendAsync(SafetyScope scope, TransactionRecord transaction, int sequence,
        TransactionState state) =>
        scope.Store.AppendEventAsync(new TransactionEventRecord(1, transaction.TransactionId, sequence,
            state, scope.Time.Now, transaction.OperationId, transaction.OperationVersion,
            transaction.CatalogVersion, transaction.PlanHash, "manual", null, null, null, null,
            null, null, null, null, []));
}
