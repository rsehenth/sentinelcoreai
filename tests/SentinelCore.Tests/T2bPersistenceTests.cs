using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class T2bPersistenceTests
{
    [Fact]
    public async Task SearchTransactionSurvivesReopenWithBackupAndRecovery()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(scope.SearchTarget(), 3);
        var execution = await scope.ExecuteAsync(T2bCatalog.HideTaskbarSearchId);
        var transactionId = execution.Transaction!.TransactionId;
        var backupPath = Path.Combine(scope.Root, "memory", "transactions", transactionId.ToString("D"),
            "backup-01.md");
        var backupBytes = await File.ReadAllBytesAsync(backupPath);

        var reopened = new MemoryStore(scope.Root);
        var snapshot = await reopened.LoadTransactionsAsync();
        var recovery = new RecoveryService(reopened, scope.Reader, scope.Executor,
            new FileExclusiveLockFactory(scope.Root), scope.Time, scope.Catalog,
            new TweakRecoveryConfirmationPolicy());
        var preview = await recovery.PreviewAsync(transactionId);
        var rollback = await recovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.Empty(snapshot.Warnings);
        Assert.Single(snapshot.Transactions);
        Assert.Equal(TransactionState.VerificationPassed, snapshot.Transactions[0].State);
        Assert.Equal("3", snapshot.Transactions[0].Backup!.Entries.Single().Value!.Data);
        Assert.True(File.Exists(reopened.GetTransactionReportPath(transactionId)));
        Assert.True(preview.CanRecover);
        Assert.True(rollback.FullyRecovered);
        Assert.Equal("3", scope.Data(T2bCatalog.SearchboxTaskbarModeValue));
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backupPath));
    }

    [Fact]
    public async Task SqliteIndexIsReconstructedFromTheImmutableMarkdown()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(scope.Target(T2bCatalog.HiddenValue), 2);
        var execution = await scope.ExecuteAsync(T2bCatalog.ShowHiddenFilesId);
        var databasePath = Path.Combine(scope.Root, "data", "sentinelcore.db");
        Assert.True(File.Exists(databasePath));
        File.Delete(databasePath);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);

        var rebuilt = new MemoryStore(scope.Root);
        var snapshot = await rebuilt.LoadTransactionsAsync();

        Assert.Empty(snapshot.Warnings);
        var record = Assert.Single(snapshot.Transactions);
        Assert.Equal(execution.Transaction!.TransactionId, record.TransactionId);
        Assert.Equal(T2bCatalog.ShowHiddenFilesId, record.OperationId);
        Assert.Equal(TransactionState.VerificationPassed, record.State);
    }

    [Fact]
    public async Task SearchModeTransactionsRemainOrderedNewestFirstInHistory()
    {
        using var scope = new T2bScope();
        var target = scope.SearchTarget();
        scope.Access.SeedDWord(target, 2);
        var first = await scope.ExecuteAsync(T2bCatalog.HideTaskbarSearchId);
        scope.Time.Now = scope.Time.Now.AddMinutes(1);
        var second = await scope.ExecuteAsync(T2bCatalog.SearchIconOnlyId);

        var history = await scope.Manager.LoadHistoryAsync();

        Assert.Equal(2, history.Count);
        Assert.Equal(second.Transaction!.TransactionId, history[0].TransactionId);
        Assert.Equal(first.Transaction!.TransactionId, history[1].TransactionId);
        Assert.All(history, item => Assert.Contains(item.OperationId, T2bCatalog.OperationIds));
        Assert.Equal("1", scope.Data(T2bCatalog.SearchboxTaskbarModeValue));
    }

    [Fact]
    public async Task TamperedBackupOfANewOperationIsRejectedBeforeAnyWrite()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(scope.Target(T2bCatalog.ShowTaskViewButtonValue), 1);
        var execution = await scope.ExecuteAsync(T2bCatalog.HideTaskViewButtonId);
        var transactionId = execution.Transaction!.TransactionId;
        var backupPath = Path.Combine(scope.Root, "memory", "transactions", transactionId.ToString("D"),
            "backup-01.md");
        var text = await File.ReadAllTextAsync(backupPath);
        Assert.Contains("\"Data\": \"1\"", text);
        await File.WriteAllTextAsync(backupPath, text.Replace("\"Data\": \"1\"", "\"Data\": \"9\""));
        var writesBefore = scope.Access.Writes;

        var preview = await scope.Recovery.PreviewAsync(transactionId);
        var result = await scope.Recovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.False(preview.CanRecover);
        Assert.Equal(RecoveryOutcome.NotRecoverable, preview.Outcome);
        Assert.Equal(RecoveryOutcome.NotRecoverable, result.Outcome);
        Assert.Equal(writesBefore, scope.Access.Writes);
        Assert.Equal("0", scope.Data(T2bCatalog.ShowTaskViewButtonValue));
    }

    [Fact]
    public async Task TransactionReportCarriesCatalogAndVerificationLimitations()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(scope.Target(T2bCatalog.HiddenValue), 2);
        var execution = await scope.ExecuteAsync(T2bCatalog.ShowHiddenFilesId);
        var transaction = execution.Transaction!;
        var report = await File.ReadAllTextAsync(scope.Store.GetTransactionReportPath(transaction.TransactionId));

        Assert.Contains(transaction.Limitations,
            item => item.Contains("não se presume que era 2", StringComparison.Ordinal));
        Assert.Contains("não se presume que era 2", report, StringComparison.Ordinal);
        Assert.Contains("Limitações", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupWithExistingTargetButNullValueIsIncomplete()
    {
        using var scope = new T2bScope();
        scope.Access.SeedDWord(scope.Target(T2bCatalog.HiddenValue), 2);
        var simulation = await scope.Engine.SimulateAsync(T2bCatalog.ShowHiddenFilesId);
        var change = simulation.Plan.Changes.Single();
        var unrepresented = new ObservedTargetState(change.TargetIdentity, true, true, null,
            scope.Time.Now, null);
        var service = new BackupService(scope.Store, scope.Time);

        var backup = service.Create(simulation.Plan, [unrepresented], 1);

        Assert.False(backup.IsComplete);
        Assert.Contains(backup.Limitations,
            item => item.Contains("sem valor observado", StringComparison.Ordinal));
    }
}
