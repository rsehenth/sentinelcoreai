using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class T2aRecoveryTests
{
    [Fact]
    public async Task RollbackRestoresTheObservedExistingValue()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var execution = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);
        var transactionId = execution.Transaction!.TransactionId;

        var preview = await scope.Recovery.PreviewAsync(transactionId);
        var result = await scope.Recovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.True(preview.CanRecover);
        Assert.Equal(RecoveryOutcome.Completed, result.Outcome);
        Assert.True(result.FullyRecovered);
        Assert.Equal("1", scope.Access.Data(target));
    }

    [Fact]
    public async Task RollbackRestoresTheOriginalAbsenceOfTheValue()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedAbsentValue(target);
        var execution = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);
        var transactionId = execution.Transaction!.TransactionId;

        var result = await scope.Recovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.True(result.FullyRecovered);
        Assert.False(scope.Access.State(target).Exists);
        Assert.Equal(1, scope.Access.Deletes);
    }

    [Fact]
    public async Task DarkModeRollbackRestoresBothValues()
    {
        using var scope = new T2aScope();
        var apps = scope.Target(T2aCatalog.AppsUseLightThemeValue);
        var system = scope.Target(T2aCatalog.SystemUsesLightThemeValue);
        scope.Access.SeedDWord(apps, 1);
        scope.Access.SeedDWord(system, 1);
        var execution = await scope.ExecuteAsync(T2aCatalog.EnableDarkModeId);
        var transactionId = execution.Transaction!.TransactionId;

        var result = await scope.Recovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.True(result.FullyRecovered);
        Assert.Equal("1", scope.Access.Data(apps));
        Assert.Equal("1", scope.Access.Data(system));
    }

    [Fact]
    public async Task RecoveryOfAPartialDarkModeStaysExplicitAndCanComplete()
    {
        using var scope = new T2aScope();
        var apps = scope.Target(T2aCatalog.AppsUseLightThemeValue);
        var system = scope.Target(T2aCatalog.SystemUsesLightThemeValue);
        scope.Access.SeedDWord(apps, 1);
        scope.Access.SeedDWord(system, 1);
        scope.Access.DenyWrite(system);
        var execution = await scope.ExecuteAsync(T2aCatalog.EnableDarkModeId);
        var transactionId = execution.Transaction!.TransactionId;
        scope.Access.AllowWrite(system);

        var preview = await scope.Recovery.PreviewAsync(transactionId);
        var result = await scope.Recovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.False(execution.Succeeded);
        Assert.True(preview.CanRecover);
        Assert.Equal(RecoveryOutcome.Completed, result.Outcome);
        Assert.True(result.FullyRecovered);
        Assert.Equal("1", scope.Access.Data(apps));
        Assert.Equal("1", scope.Access.Data(system));
    }

    [Fact]
    public async Task ThirdStateAfterTransactionIsAConflictAndIsNotOverwritten()
    {
        using var scope = new T2aScope();
        var apps = scope.Target(T2aCatalog.AppsUseLightThemeValue);
        var system = scope.Target(T2aCatalog.SystemUsesLightThemeValue);
        scope.Access.SeedDWord(apps, 1);
        scope.Access.SeedDWord(system, 1);
        var execution = await scope.ExecuteAsync(T2aCatalog.EnableDarkModeId);
        var transactionId = execution.Transaction!.TransactionId;
        scope.Access.SeedDWord(system, 2);
        var writesBefore = scope.Access.Writes;

        var preview = await scope.Recovery.PreviewAsync(transactionId);
        var result = await scope.Recovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.True(preview.CanRecover);
        Assert.Equal(RecoveryOutcome.Conflict, preview.Outcome);
        Assert.Contains(preview.Conflicts, conflict => conflict.TargetIdentity == system.Identity);
        Assert.Equal(RecoveryOutcome.PartiallyCompleted, result.Outcome);
        Assert.False(result.FullyRecovered);
        Assert.Contains(result.Conflicts, conflict => conflict.TargetIdentity == system.Identity);
        Assert.Equal("1", scope.Access.Data(apps));
        Assert.Equal("2", scope.Access.Data(system));
        Assert.True(scope.Access.Writes > writesBefore);
    }

    [Fact]
    public async Task TamperedBackupIsRejectedBeforeAnyWrite()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var execution = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);
        var transactionId = execution.Transaction!.TransactionId;
        var backupPath = Path.Combine(scope.Root, "memory", "transactions", transactionId.ToString("D"),
            "backup-01.md");
        var text = File.ReadAllText(backupPath);
        Assert.Contains("\"Data\": \"1\"", text);
        File.WriteAllText(backupPath, text.Replace("\"Data\": \"1\"", "\"Data\": \"9\""));
        var writesBefore = scope.Access.Writes;

        var preview = await scope.Recovery.PreviewAsync(transactionId);
        var result = await scope.Recovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.False(preview.CanRecover);
        Assert.Equal(RecoveryOutcome.NotRecoverable, preview.Outcome);
        Assert.Equal(RecoveryOutcome.NotRecoverable, result.Outcome);
        Assert.Equal(writesBefore, scope.Access.Writes);
    }

    [Fact]
    public async Task HistoryAndRecoverySurviveApplicationReopen()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var execution = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);
        var transactionId = execution.Transaction!.TransactionId;

        var reopenedStore = new MemoryStore(scope.Root);
        var reopenedRecovery = new RecoveryService(reopenedStore, scope.Reader, scope.Executor,
            new FileExclusiveLockFactory(scope.Root), scope.Time);
        var snapshot = await reopenedStore.LoadTransactionsAsync();
        var preview = await reopenedRecovery.PreviewAsync(transactionId);
        var result = await reopenedRecovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.Empty(snapshot.Warnings);
        Assert.Single(snapshot.Transactions);
        Assert.True(preview.CanRecover);
        Assert.True(result.FullyRecovered);
        Assert.Equal("1", scope.Access.Data(target));
    }

    [Fact]
    public async Task RepeatedRollbackIsIdempotent()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var execution = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);
        var transactionId = execution.Transaction!.TransactionId;
        var confirmation = TweakManager.BuildRecoveryConfirmationText(transactionId);

        var first = await scope.Recovery.RollbackAsync(transactionId, new RecoveryOptions(confirmation));
        var second = await scope.Recovery.RollbackAsync(transactionId, new RecoveryOptions(confirmation));

        Assert.True(first.FullyRecovered);
        Assert.Equal(RecoveryOutcome.AlreadyRestored, second.Outcome);
        Assert.True(second.FullyRecovered);
        Assert.Equal("1", scope.Access.Data(target));
    }

    [Fact]
    public async Task RecoveryRefusesAPlanNotPresentInTheCatalog()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var execution = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);
        var transactionId = execution.Transaction!.TransactionId;
        var writesBefore = scope.Access.Writes;
        var foreign = new RecoveryService(scope.Store, scope.Reader, scope.Executor, scope.Locks,
            scope.Time, T1SyntheticCatalog.Create());

        var preview = await foreign.PreviewAsync(transactionId);
        var result = await foreign.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.False(preview.CanRecover);
        Assert.Equal(RecoveryOutcome.NotRecoverable, preview.Outcome);
        Assert.Equal(RecoveryOutcome.NotRecoverable, result.Outcome);
        Assert.Equal("0", scope.Access.Data(target));
        Assert.Equal(writesBefore, scope.Access.Writes);
    }

    [Fact]
    public async Task RecoveryRefusesAPlanWhoseDesiredValueDivergesFromTheCatalog()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var execution = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);
        var transactionId = execution.Transaction!.TransactionId;
        var operation = T2aCatalog.Create().Get(T2aCatalog.ShowFileExtensionsId) with
        {
            Changes =
            [
                new PlannedTarget(scope.Target(T2aCatalog.HideFileExtValue),
                    new TargetValueState(true, RegistryValueKinds.DWord, "1"))
            ]
        };
        var altered = new OperationCatalog(T2aCatalog.CatalogVersion, [operation]);
        var recovery = new RecoveryService(scope.Store, scope.Reader, scope.Executor, scope.Locks,
            scope.Time, altered);
        var writesBefore = scope.Access.Writes;

        var preview = await recovery.PreviewAsync(transactionId);

        Assert.False(preview.CanRecover);
        Assert.Equal(RecoveryOutcome.NotRecoverable, preview.Outcome);
        Assert.Contains("catálogo", preview.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(writesBefore, scope.Access.Writes);
    }

    [Fact]
    public async Task MixedDarkModeRollbackRestoresAValueAndTheOriginalAbsence()
    {
        using var scope = new T2aScope();
        var apps = scope.Target(T2aCatalog.AppsUseLightThemeValue);
        var system = scope.Target(T2aCatalog.SystemUsesLightThemeValue);
        scope.Access.SeedDWord(apps, 1);
        scope.Access.SeedAbsentValue(system);
        var execution = await scope.ExecuteAsync(T2aCatalog.EnableDarkModeId);
        var transactionId = execution.Transaction!.TransactionId;

        var result = await scope.Recovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.True(result.FullyRecovered);
        Assert.Equal("1", scope.Access.Data(apps));
        Assert.False(scope.Access.State(system).Exists);
    }

    [Fact]
    public async Task RollbackFailureOnSecondValueIsPartialAndNotFullRecovery()
    {
        using var scope = new T2aScope();
        var apps = scope.Target(T2aCatalog.AppsUseLightThemeValue);
        var system = scope.Target(T2aCatalog.SystemUsesLightThemeValue);
        scope.Access.SeedDWord(apps, 1);
        scope.Access.SeedDWord(system, 1);
        var execution = await scope.ExecuteAsync(T2aCatalog.EnableDarkModeId);
        var transactionId = execution.Transaction!.TransactionId;
        scope.Access.DenyWrite(system);

        var result = await scope.Recovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

        Assert.Equal(RecoveryOutcome.PartiallyCompleted, result.Outcome);
        Assert.False(result.FullyRecovered);
        Assert.Equal("1", scope.Access.Data(apps));
        Assert.Equal("0", scope.Access.Data(system));
        Assert.Contains(result.Steps, step => step.Applied);
    }

    [Fact]
    public async Task DuplicateBackupEntriesAreRejectedAsCorrupt()
    {
        using var scope = new T2aScope();
        var apps = scope.Target(T2aCatalog.AppsUseLightThemeValue);
        var system = scope.Target(T2aCatalog.SystemUsesLightThemeValue);
        scope.Access.SeedDWord(apps, 1);
        scope.Access.SeedDWord(system, 1);
        var simulation = await scope.Engine.SimulateAsync(T2aCatalog.EnableDarkModeId);
        var plan = simulation.Plan;
        var change = plan.Changes[0];
        var entry = new BackupEntry(change.TargetIdentity, change.BeforeTargetExists, change.Before, "");
        entry = entry with { Checksum = SafetyHashing.EntryChecksum(entry) };
        var entries = new[] { entry, entry };
        var backup = new BackupRecord(Guid.NewGuid(), plan.TransactionId, plan.PlanId, plan.OperationId,
            plan.OperationVersion, plan.PlanHash, 1, scope.Time.Now, entries,
            SafetyHashing.BackupContentHash(entries), true, []);

        var validation = BackupService.ValidateBackup(backup, plan);

        Assert.False(validation.Valid);
        Assert.Equal(BackupIntegrity.Corrupt, validation.Integrity);
    }

    [Fact]
    public async Task NoEffectTransactionWithObservedStateIsConfirmedByReading()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var journal = new FaultingJournal(new MemoryStore(scope.Root))
        {
            FailOn = TransactionState.BackupPrepared
        };
        scope.Rebuild(journal);
        var execution = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);
        var transactionId = execution.Transaction!.TransactionId;

        var preview = await scope.Recovery.PreviewAsync(transactionId);

        Assert.False(execution.Succeeded);
        Assert.Equal(RecoveryOutcome.AlreadyRestored, preview.Outcome);
        Assert.True(preview.CanRecover);
        var step = Assert.Single(preview.CurrentStates);
        Assert.True(step.Verified);
    }

    [Fact]
    public async Task NoEffectTransactionConflictsWhenTheStateChangedExternally()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var (simulation, token) = await scope.PrepareAsync(T2aCatalog.ShowFileExtensionsId);
        scope.Access.SeedDWord(target, 0);
        var execution = await scope.Engine.ExecuteAsync(simulation.Plan, token);
        var transactionId = execution.Transaction!.TransactionId;

        var preview = await scope.Recovery.PreviewAsync(transactionId);

        Assert.False(execution.Succeeded);
        Assert.Equal(RecoveryOutcome.Conflict, preview.Outcome);
        Assert.False(preview.CanRecover);
        Assert.NotEmpty(preview.CurrentStates);
        Assert.NotEmpty(preview.Conflicts);
        Assert.Equal("0", scope.Access.Data(target));
    }

    [Fact]
    public async Task RecoveryWithACorrectlyFormattedButWrongPhraseIsBlocked()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var execution = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);
        var transactionId = execution.Transaction!.TransactionId;

        var result = await scope.Recovery.RollbackAsync(transactionId,
            new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(Guid.NewGuid())));

        Assert.Equal(RecoveryOutcome.Blocked, result.Outcome);
        Assert.Equal("0", scope.Access.Data(target));
    }

    [Fact]
    public async Task BackupPreservesThePerEntryObservationTime()
    {
        using var scope = new T2aScope();
        scope.Access.SeedDWord(scope.Target(T2aCatalog.AppsUseLightThemeValue), 1);
        scope.Access.SeedDWord(scope.Target(T2aCatalog.SystemUsesLightThemeValue), 1);
        var execution = await scope.ExecuteAsync(T2aCatalog.EnableDarkModeId);

        Assert.All(execution.Transaction!.Backup!.Entries, entry =>
            Assert.NotNull(entry.ObservedAtUtc));
    }

    [Fact]
    public async Task RecoverySurvivesADataRootRelocation()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var execution = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);
        var transactionId = execution.Transaction!.TransactionId;
        var movedRoot = Path.Combine(Path.GetTempPath(), "SentinelCoreT2aMoved",
            Guid.NewGuid().ToString("N"));
        CopyDirectory(scope.Root, movedRoot);
        try
        {
            var movedStore = new MemoryStore(movedRoot);
            var movedRecovery = new RecoveryService(movedStore, scope.Reader, scope.Executor,
                new FileExclusiveLockFactory(movedRoot), scope.Time, scope.Catalog,
                new TweakRecoveryConfirmationPolicy());

            var preview = await movedRecovery.PreviewAsync(transactionId);
            var result = await movedRecovery.RollbackAsync(transactionId,
                new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(transactionId)));

            Assert.True(preview.CanRecover);
            Assert.True(result.FullyRecovered);
            Assert.Equal("1", scope.Access.Data(target));
        }
        finally
        {
            try { Directory.Delete(movedRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task RecoveryWithoutConfirmationIsBlocked()
    {
        using var scope = new T2aScope();
        var target = scope.Target(T2aCatalog.HideFileExtValue);
        scope.Access.SeedDWord(target, 1);
        var execution = await scope.ExecuteAsync(T2aCatalog.ShowFileExtensionsId);
        var transactionId = execution.Transaction!.TransactionId;
        var writesBefore = scope.Access.Writes;

        var result = await scope.Recovery.RollbackAsync(transactionId, new RecoveryOptions(" "));

        Assert.Equal(RecoveryOutcome.Blocked, result.Outcome);
        Assert.Equal(writesBefore, scope.Access.Writes);
        Assert.Equal("0", scope.Access.Data(target));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, destination, StringComparison.OrdinalIgnoreCase),
                overwrite: true);
    }
}
