using Microsoft.Data.Sqlite;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class DebloatPersistenceTests
{
    private const string OperationId = "DBLT-TEST-01";

    private static SafetyScope Scope()
    {
        var descriptor = T1SyntheticCatalog.Create().Get("SYN-PREF-01") with { Id = OperationId };
        var scope = new SafetyScope(catalog: new OperationCatalog("debloat-test-1", [descriptor]));
        scope.Journal = scope.Store.CreateDebloatJournal();
        scope.Rebuild();
        scope.Seed("Preference.Test", "A");
        return scope;
    }

    private static async Task<TransactionRecord> ExecuteAsync(SafetyScope scope)
    {
        var result = await scope.ExecuteAsync(OperationId);
        Assert.True(result.Succeeded, result.Summary);
        return Assert.IsType<TransactionRecord>(result.Transaction);
    }

    private static string EventPath(SafetyScope scope, TransactionRecord record, int sequence,
        TransactionState state) => Path.Combine(scope.Root, "memory", "debloat",
        record.TransactionId.ToString("D"), "journal", $"{sequence:D4}-{state}.md");

    private static long Scalar(string root, string sql)
    {
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, "data", "sentinelcore.db"), Pooling = false }.ToString());
        database.Open();
        using var query = database.CreateCommand();
        query.CommandText = sql;
        return Convert.ToInt64(query.ExecuteScalar());
    }

    private static void Sql(string root, string sql)
    {
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, "data", "sentinelcore.db"), Pooling = false }.ToString());
        database.Open();
        using var query = database.CreateCommand();
        query.CommandText = sql;
        query.ExecuteNonQuery();
    }

    [Fact]
    public async Task ReopenPreservesSyntheticJournalSnapshotReportAndSeparateIndex()
    {
        using var scope = Scope();
        var record = await ExecuteAsync(scope);
        var journal = new MemoryStore(scope.Root).CreateDebloatJournal();

        var snapshot = await journal.LoadTransactionsAsync();

        Assert.Empty(snapshot.Warnings);
        var copy = Assert.Single(snapshot.Transactions);
        Assert.Equal(record.TransactionId, copy.TransactionId);
        Assert.True(copy.Succeeded);
        Assert.Equal(8, copy.Events.Count);
        Assert.NotNull(copy.Backup);
        Assert.NotNull(copy.Authorization);
        Assert.NotNull(copy.Verification);
        Assert.True(File.Exists(EventPath(scope, record, 1, TransactionState.Planned)));
        Assert.Equal(Path.Combine(scope.Root, "reports", "debloat", $"{record.TransactionId:D}.md"),
            journal.GetTransactionReportPath(record.TransactionId));
        var report = await File.ReadAllTextAsync(journal.GetTransactionReportPath(record.TransactionId));
        Assert.Contains("SYNTHETIC", report);
        Assert.Contains("não é backup dos dados do aplicativo", report);
        Assert.Contains("Não oferece rollback", report);
        Assert.DoesNotContain("operação sintética da T1", report);
        Assert.Contains("SYNTHETIC", await File.ReadAllTextAsync(record.BackupPath!));
        Assert.DoesNotContain("Completo para reversão", await File.ReadAllTextAsync(record.BackupPath!));
        Assert.Contains("memory/debloat", await File.ReadAllTextAsync(Path.Combine(scope.Root, "memory", "DEBLOAT.md")));
        Assert.Equal(12, Scalar(scope.Root, "PRAGMA user_version"));
        Assert.Equal(1, Scalar(scope.Root, "SELECT COUNT(*) FROM DebloatTransactions"));
        Assert.Equal(8, Scalar(scope.Root, "SELECT COUNT(*) FROM DebloatTransactionEvents"));
        Assert.Equal(1, Scalar(scope.Root, "SELECT COUNT(*) FROM DebloatTransactionBackups"));
        Assert.Equal(0, Scalar(scope.Root, "SELECT COUNT(*) FROM DebloatRecoveries"));
        Assert.Equal(0, Scalar(scope.Root, "SELECT COUNT(*) FROM Transactions"));
        Assert.Equal(0, Scalar(scope.Root, "SELECT COUNT(*) FROM Repairs"));
        Assert.Empty((await scope.Store.LoadTransactionsAsync()).Transactions);
        Assert.Null(await scope.Store.FindTransactionAsync(record.TransactionId));
    }

    [Fact]
    public async Task RepeatedEventsAndMetadataSnapshotsAreImmutableAndIdempotent()
    {
        using var scope = Scope();
        var record = await ExecuteAsync(scope);
        var path = EventPath(scope, record, 1, TransactionState.Planned);
        var before = await File.ReadAllBytesAsync(path);
        var backupBytes = await File.ReadAllBytesAsync(record.BackupPath!);

        await scope.Journal.AppendEventAsync(record.Events[0]);
        await scope.Journal.WriteBackupAsync(record.Backup!);
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Journal.AppendEventAsync(
            record.Events[0] with { Detail = "different" }));
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Journal.WriteBackupAsync(
            record.Backup! with { Limitations = ["different"] }));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(record.BackupPath!));
        Assert.Equal(8, Scalar(scope.Root, "SELECT COUNT(*) FROM DebloatTransactionEvents"));
    }

    [Fact]
    public async Task RebuildRestoresDebloatAndTweaksFromTheirOwnMarkdownWithoutMixing()
    {
        using var scope = Scope();
        var debloat = await ExecuteAsync(scope);
        using var tweaks = new SafetyScope();
        tweaks.Seed("Preference.Test", "A");
        var tweakResult = await tweaks.ExecuteAsync("SYN-PREF-01");
        foreach (var entry in tweakResult.Transaction!.Events) await scope.Store.AppendEventAsync(entry);
        var recoveryBefore = await File.ReadAllTextAsync(Path.Combine(scope.Root, "memory", "RECOVERY.md"));
        File.Delete(Path.Combine(scope.Root, "data", "sentinelcore.db"));

        var reopened = new MemoryStore(scope.Root);
        Assert.Single((await reopened.CreateDebloatJournal().LoadTransactionsAsync()).Transactions);
        Assert.Single((await reopened.LoadTransactionsAsync()).Transactions);

        Assert.Equal(1, Scalar(scope.Root, "SELECT COUNT(*) FROM DebloatTransactions"));
        Assert.Equal(1, Scalar(scope.Root, "SELECT COUNT(*) FROM Transactions"));
        Assert.Equal(8, Scalar(scope.Root, "SELECT COUNT(*) FROM TransactionEvents"));
        Assert.Equal(recoveryBefore, await File.ReadAllTextAsync(Path.Combine(scope.Root, "memory", "RECOVERY.md")));
        Assert.Equal(debloat.TransactionId, (await reopened.CreateDebloatJournal()
            .FindTransactionAsync(debloat.TransactionId))!.TransactionId);
    }

    [Fact]
    public async Task SchemaElevenMigrationIsAdditiveAndPreservesRepairAndTweaksTables()
    {
        using var scope = Scope();
        await scope.Store.LoadTransactionsAsync();
        Sql(scope.Root, """
            DROP TABLE DebloatRecoveries;
            DROP TABLE DebloatTransactionBackups;
            DROP TABLE DebloatTransactionEvents;
            DROP TABLE DebloatTransactions;
            CREATE TABLE MigrationSentinel (Value TEXT NOT NULL);
            INSERT INTO MigrationSentinel VALUES ('preserved');
            PRAGMA user_version = 11;
            """);

        await scope.Journal.LoadTransactionsAsync();

        Assert.Equal(12, Scalar(scope.Root, "PRAGMA user_version"));
        Assert.Equal(1, Scalar(scope.Root, "SELECT COUNT(*) FROM MigrationSentinel"));
        Assert.Equal(0, Scalar(scope.Root, "SELECT COUNT(*) FROM Transactions"));
        Assert.Equal(0, Scalar(scope.Root, "SELECT COUNT(*) FROM Repairs"));
        await ExecuteAsync(scope);
    }

    [Fact]
    public async Task FutureSchemaIsRefusedWithoutDowngrade()
    {
        using var scope = Scope();
        await scope.Journal.LoadTransactionsAsync();
        Sql(scope.Root, "PRAGMA user_version = 13");

        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Journal.LoadTransactionsAsync());

        Assert.Equal(13, Scalar(scope.Root, "PRAGMA user_version"));
    }

    [Fact]
    public async Task CorruptJournalIsPreservedAndExcludedWithoutTouchingOtherPartition()
    {
        using var scope = Scope();
        var record = await ExecuteAsync(scope);
        var path = EventPath(scope, record, 1, TransactionState.Planned);
        await File.WriteAllTextAsync(path, "corrupt synthetic record");

        var snapshot = await scope.Journal.LoadTransactionsAsync();

        Assert.Empty(snapshot.Transactions);
        Assert.NotEmpty(snapshot.Warnings);
        Assert.Equal("corrupt synthetic record", await File.ReadAllTextAsync(path));
        Assert.Equal(0, Scalar(scope.Root, "SELECT COUNT(*) FROM DebloatTransactions"));
        Assert.Empty((await scope.Store.LoadTransactionsAsync()).Transactions);
    }

    [Fact]
    public async Task CorruptSqliteIsPreservedAndDebloatIndexIsRebuilt()
    {
        using var scope = Scope();
        var record = await ExecuteAsync(scope);
        await File.WriteAllTextAsync(Path.Combine(scope.Root, "data", "sentinelcore.db"), "corrupt db");

        var snapshot = await scope.Journal.LoadTransactionsAsync();

        Assert.Equal(record.TransactionId, Assert.Single(snapshot.Transactions).TransactionId);
        Assert.NotEmpty(snapshot.Warnings);
        Assert.Single(Directory.GetFiles(Path.Combine(scope.Root, "data"), "sentinelcore.db.corrupt-*"));
        Assert.Equal(1, Scalar(scope.Root, "SELECT COUNT(*) FROM DebloatTransactions"));
    }

    [Fact]
    public async Task InterruptedTailIsNotPromotedToSuccess()
    {
        using var scope = Scope();
        var record = await ExecuteAsync(scope);
        File.Delete(EventPath(scope, record, 8, TransactionState.VerificationPassed));

        var copy = Assert.Single((await scope.Journal.LoadTransactionsAsync()).Transactions);

        Assert.Equal(TransactionState.ExecutionCompleted, copy.State);
        Assert.True(copy.IsIndeterminate);
        Assert.False(copy.Succeeded);
    }

    [Theory]
    [InlineData(TransactionState.RecoveryRequested)]
    [InlineData(TransactionState.RecoveryStarted)]
    [InlineData(TransactionState.RecoveryCompleted)]
    [InlineData(TransactionState.RecoveryFailed)]
    public async Task RecoveryEventsAreRefused(TransactionState state)
    {
        using var scope = Scope();
        var record = await ExecuteAsync(scope);

        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Journal.AppendEventAsync(
            record.Events[0] with { State = state, Sequence = 9, Plan = null }));

        Assert.Equal(8, Scalar(scope.Root, "SELECT COUNT(*) FROM DebloatTransactionEvents"));
    }

    [Fact]
    public async Task WrongOperationPartitionIsRefusedInBothDirections()
    {
        using var scope = Scope();
        var record = await ExecuteAsync(scope);
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.AppendEventAsync(record.Events[0]));
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.WriteBackupAsync(record.Backup!));
        using var tweaks = new SafetyScope();
        tweaks.Seed("Preference.Test", "A");
        var tweak = (await tweaks.ExecuteAsync("SYN-PREF-01")).Transaction!;
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Journal.AppendEventAsync(tweak.Events[0]));
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Journal.WriteBackupAsync(tweak.Backup!));
    }

    [Fact]
    public async Task DebloatRefusesHkcuPlanAndMetadataSnapshot()
    {
        using var scope = Scope();
        var record = await ExecuteAsync(scope);
        var plan = record.Plan!;
        var target = new OperationTarget("HKCU", @"Software\Example", "Value");
        var changes = plan.Changes.Select(change => change with
            { Target = target, TargetIdentity = target.Identity }).ToList();
        plan = plan with { Changes = changes, PlanHash = SafetyHashing.PlanHash(plan.PlanId,
            plan.TransactionId, plan.OperationId, plan.OperationVersion, plan.CatalogVersion,
            plan.Risk, plan.Scope, plan.Protection, plan.ObservedStateHash, changes) };

        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Journal.AppendEventAsync(
            record.Events[0] with { Plan = plan, PlanHash = plan.PlanHash }));
        var entry = record.Backup!.Entries[0] with { TargetIdentity = target.Identity };
        entry = entry with { Checksum = SafetyHashing.EntryChecksum(entry) };
        var backup = record.Backup with { Entries = [entry],
            ContentHash = SafetyHashing.BackupContentHash([entry]) };
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Journal.WriteBackupAsync(backup));
    }

    [Fact]
    public async Task MetadataSnapshotReadCannotCrossPartitionOrEscapeRoot()
    {
        using var scope = Scope();
        var record = await ExecuteAsync(scope);

        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.ReadBackupAsync(record.BackupPath!));
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Journal.ReadBackupAsync(
            Path.Combine(scope.Root, "memory", "..", "..", "outside.md")));
        Assert.Equal(record.Backup!.ContentHash,
            (await scope.Journal.ReadBackupAsync(record.BackupPath!)).ContentHash);
    }

    [Fact]
    public async Task GlobalIdCollisionsAreRefusedInBothDirectionsIncludingRepairIntent()
    {
        using var scope = Scope();
        var record = await ExecuteAsync(scope);
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.SaveAsync(
            Samples.Audit(scope.Time.Now) with { Id = record.TransactionId }));

        using var repair = new RepairScope();
        var result = await repair.ExecuteAsync(RepairCatalog.SfcVerifyId);
        var intentPath = Path.Combine(repair.Root, "memory", "repairs", $"{result.Record!.Id:D}", "intent.md");
        var intent = System.Text.Json.JsonSerializer.Deserialize<RepairIntent>(
            RecordEnvelope.Unwrap(await File.ReadAllTextAsync(intentPath)), AuditJson.Options)!;
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.SaveRepairIntentAsync(
            intent with { Id = record.TransactionId }));

        Directory.CreateDirectory(Path.Combine(scope.Root, "memory", "repairs", $"{record.TransactionId:D}"));
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Journal.AppendEventAsync(record.Events[0]));
        var snapshot = await scope.Journal.LoadTransactionsAsync();
        Assert.Empty(snapshot.Transactions);
        Assert.NotEmpty(snapshot.Warnings);
        Assert.True(File.Exists(EventPath(scope, record, 1, TransactionState.Planned)));
    }

    [Fact]
    public async Task ExistingTweaksIdCannotBeReusedByDebloat()
    {
        using var scope = Scope();
        var simulation = await scope.SimulateAsync(OperationId);
        var plan = simulation.Plan;
        Directory.CreateDirectory(Path.Combine(scope.Root, "memory", "transactions", $"{plan.TransactionId:D}"));
        var entry = new TransactionEventRecord(1, plan.TransactionId, 1, TransactionState.Planned,
            scope.Time.Now, plan.OperationId, plan.OperationVersion, plan.CatalogVersion, plan.PlanHash,
            null, null, plan, null, null, null, null, null, null, []);

        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Journal.AppendEventAsync(entry));
        Assert.False(Directory.Exists(Path.Combine(scope.Root, "memory", "debloat", $"{plan.TransactionId:D}")));
    }

    [Fact]
    public async Task ExistingDebloatIdCannotBeReusedByTweaksEvenWithValidTweaksPlan()
    {
        using var scope = Scope();
        var record = await ExecuteAsync(scope);
        var plan = record.Plan! with { OperationId = "SYN-PREF-01" };
        plan = Rehash(plan);

        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.AppendEventAsync(Planned(plan)));

        Assert.False(Directory.Exists(Path.Combine(scope.Root, "memory", "transactions", $"{plan.TransactionId:D}")));
        Assert.Single((await scope.Journal.LoadTransactionsAsync()).Transactions);
    }

    [Fact]
    public async Task ExistingRepairIntentIdCannotBeReusedByDebloatEvenBeforeRepairCompletion()
    {
        using var scope = Scope();
        using var repair = new RepairScope();
        var result = await repair.ExecuteAsync(RepairCatalog.SfcVerifyId);
        var intentPath = Path.Combine(repair.Root, "memory", "repairs", $"{result.Record!.Id:D}", "intent.md");
        var intent = System.Text.Json.JsonSerializer.Deserialize<RepairIntent>(
            RecordEnvelope.Unwrap(await File.ReadAllTextAsync(intentPath)), AuditJson.Options)!;
        await scope.Store.SaveRepairIntentAsync(intent);
        var simulation = await scope.SimulateAsync(OperationId);
        var plan = Rehash(simulation.Plan with { TransactionId = intent.Id });

        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Journal.AppendEventAsync(Planned(plan)));

        Assert.Single((await scope.Store.LoadRepairsAsync()).UnfinishedIntents);
        Assert.False(Directory.Exists(Path.Combine(scope.Root, "memory", "debloat", $"{intent.Id:D}")));
    }

    [Fact]
    public async Task RelocatedRootResolvesOnlyDebloatMetadataSnapshotWithoutChangingDurableJournal()
    {
        using var scope = Scope();
        var record = await ExecuteAsync(scope);
        var relocatedRoot = Path.Combine(scope.Root, "relocated");
        var files = Directory.GetFiles(Path.Combine(scope.Root, "memory", "debloat"), "*.md",
            SearchOption.AllDirectories);
        var firstBytes = await File.ReadAllBytesAsync(EventPath(scope, record, 1, TransactionState.Planned));
        foreach (var source in files)
        {
            var destination = Path.Combine(relocatedRoot, Path.GetRelativePath(scope.Root, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
        var relocated = new MemoryStore(relocatedRoot).CreateDebloatJournal();

        var reopened = Assert.Single((await relocated.LoadTransactionsAsync()).Transactions);
        var backup = await relocated.ReadBackupAsync(reopened.BackupPath!);

        Assert.Equal(record.Backup!.ContentHash, backup.ContentHash);
        var relocatedFirst = Path.Combine(relocatedRoot,
            Path.GetRelativePath(scope.Root, EventPath(scope, record, 1, TransactionState.Planned)));
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(relocatedFirst));
        Assert.Empty((await new MemoryStore(relocatedRoot).LoadTransactionsAsync()).Transactions);
    }

    private static OperationPlan Rehash(OperationPlan plan) => plan with
    {
        PlanHash = SafetyHashing.PlanHash(plan.PlanId, plan.TransactionId, plan.OperationId,
            plan.OperationVersion, plan.CatalogVersion, plan.Risk, plan.Scope, plan.Protection,
            plan.ObservedStateHash, plan.Changes)
    };

    private static TransactionEventRecord Planned(OperationPlan plan) =>
        new(1, plan.TransactionId, 1, TransactionState.Planned, plan.CreatedAtUtc,
            plan.OperationId, plan.OperationVersion, plan.CatalogVersion, plan.PlanHash,
            null, null, plan, null, null, null, null, null, null, []);
}
