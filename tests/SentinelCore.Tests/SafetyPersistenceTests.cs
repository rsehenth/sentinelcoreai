using System.Text.Json;
using Microsoft.Data.Sqlite;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class SafetyPersistenceTests
{
    private static string EventPath(SafetyScope scope, Guid transactionId, int sequence,
        TransactionState state) => Path.Combine(scope.Root, "memory", "transactions",
        transactionId.ToString("D"), "journal", $"{sequence:D4}-{state}.md");

    private static async Task<TransactionRecord> ExecutedAsync(SafetyScope scope)
    {
        scope.Seed("Preference.Test", "A");
        var result = await scope.ExecuteAsync("SYN-PREF-01");
        Assert.True(result.Succeeded);
        return result.Transaction!;
    }

    [Fact]
    public async Task JournalRoundTripsPlanBackupAndVerification()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);

        var snapshot = await new MemoryStore(scope.Root).LoadTransactionsAsync();

        Assert.Empty(snapshot.Warnings);
        var record = Assert.Single(snapshot.Transactions);
        Assert.Equal(transaction.TransactionId, record.TransactionId);
        Assert.NotNull(record.Plan);
        Assert.NotNull(record.Backup);
        Assert.NotNull(record.Verification);
        Assert.Equal(8, record.Events.Count);
        Assert.Equal(8, record.Events.Select(item => item.Sequence).Distinct().Count());
        Assert.True(File.Exists(new MemoryStore(scope.Root).GetTransactionReportPath(record.TransactionId)));
        Assert.True(File.Exists(Path.Combine(scope.Root, "memory", "RECOVERY.md")));
    }

    [Fact]
    public async Task SameEventAppendIsIdempotent()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        var first = transaction.Events[0];
        var directory = Path.Combine(scope.Root, "memory", "transactions",
            transaction.TransactionId.ToString("D"), "journal");
        var before = Directory.GetFiles(directory).Length;

        var again = await scope.Store.AppendEventAsync(first);

        Assert.Equal(first.Sequence, again.Sequence);
        Assert.Equal(before, Directory.GetFiles(directory).Length);
    }

    [Fact]
    public async Task SameSequenceWithDifferentContentIsRejected()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        var first = transaction.Events[0];
        var forged = first with { PlanHash = new string('0', 64) };

        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.AppendEventAsync(forged));
    }

    [Fact]
    public async Task OutOfOrderSequenceIsRejected()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        var forged = new TransactionEventRecord(1, transaction.TransactionId, 99,
            TransactionState.VerificationPassed, scope.Time.Now, transaction.OperationId,
            transaction.OperationVersion, transaction.CatalogVersion, transaction.PlanHash,
            null, null, null, null, null, null, null, null, null, []);

        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.AppendEventAsync(forged));
    }

    [Fact]
    public async Task FirstEventMustBePlanned()
    {
        using var scope = new SafetyScope();
        var id = Guid.NewGuid();
        var first = new TransactionEventRecord(1, id, 1, TransactionState.PreflightPassed,
            scope.Time.Now, "SYN-PREF-01", 1, T1SyntheticCatalog.CatalogVersion, new string('A', 64),
            null, null, null, null, null, null, null, null, null, []);

        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.AppendEventAsync(first));
    }

    [Fact]
    public async Task CorruptEventIsPreservedAndTransactionExcluded()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        var path = EventPath(scope, transaction.TransactionId, 1, TransactionState.Planned);
        File.WriteAllText(path, "conteúdo corrompido");

        var snapshot = await scope.Store.LoadTransactionsAsync();

        Assert.NotEmpty(snapshot.Warnings);
        Assert.Empty(snapshot.Transactions);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task UnknownEnumStateIsNotTreatedAsSuccess()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        var path = EventPath(scope, transaction.TransactionId, 8, TransactionState.VerificationPassed);
        var text = File.ReadAllText(path);
        var json = RecordEnvelope.Unwrap(text)
            .Replace("\"State\": \"VerificationPassed\"", "\"State\": \"EstadoInexistente\"");
        File.WriteAllText(path, RecordEnvelope.Wrap("evento adulterado", json));

        var snapshot = await scope.Store.LoadTransactionsAsync();

        Assert.NotEmpty(snapshot.Warnings);
        Assert.Empty(snapshot.Transactions);
    }

    [Fact]
    public async Task SequenceGapIsExcludedFromValidity()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        File.Delete(EventPath(scope, transaction.TransactionId, 2, TransactionState.PreflightPassed));

        var snapshot = await scope.Store.LoadTransactionsAsync();

        Assert.NotEmpty(snapshot.Warnings);
        Assert.Empty(snapshot.Transactions);
        Assert.True(Directory.GetFiles(Path.Combine(scope.Root, "memory", "transactions",
            transaction.TransactionId.ToString("D"), "journal")).Length >= 7);
    }

    [Fact]
    public async Task TransactionIdCollidingWithAuditIsRejected()
    {
        using var scope = new SafetyScope();
        var audit = Samples.Audit(scope.Time.Now);
        var id = audit.Id;
        await scope.Store.SaveAsync(audit);
        var entry = new TransactionEventRecord(1, id, 1, TransactionState.Planned,
            scope.Time.Now, "SYN-PREF-01", 1, T1SyntheticCatalog.CatalogVersion, new string('A', 64),
            null, null, null, null, null, null, null, null, null, []);

        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.AppendEventAsync(entry));
    }

    [Fact]
    public async Task EnvelopeChecksumMatchesSqlitePayloadHash()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        var path = EventPath(scope, transaction.TransactionId, 1, TransactionState.Planned);
        var json = RecordEnvelope.Unwrap(File.ReadAllText(path).Replace("\r\n", "\n"));
        var hash = RecordEnvelope.Hash(json);

        using var db = new SqliteConnection(
            $"Data Source={Path.Combine(scope.Root, "data", "sentinelcore.db")};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT PayloadHash FROM TransactionEvents WHERE Sequence=1";
        Assert.Equal(hash, command.ExecuteScalar()?.ToString());
    }

    [Fact]
    public async Task SqliteIndexIsRebuiltFromMarkdownAfterDeletion()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        File.Delete(Path.Combine(scope.Root, "data", "sentinelcore.db"));

        var snapshot = await new MemoryStore(scope.Root).LoadTransactionsAsync();

        Assert.Empty(snapshot.Warnings);
        Assert.Equal(transaction.TransactionId, Assert.Single(snapshot.Transactions).TransactionId);
        Assert.True(File.Exists(Path.Combine(scope.Root, "data", "sentinelcore.db")));
    }

    [Fact]
    public async Task SchemaTenMigrationPreservesPreviousHistory()
    {
        using var scope = new SafetyScope();
        var audit = Samples.Audit(scope.Time.Now);
        await scope.Store.SaveAsync(audit);
        using (var db = new SqliteConnection(
                   $"Data Source={Path.Combine(scope.Root, "data", "sentinelcore.db")};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                DROP TABLE Recoveries;
                DROP TABLE TransactionBackups;
                DROP TABLE TransactionEvents;
                DROP TABLE Transactions;
                PRAGMA user_version=9;
                """;
            command.ExecuteNonQuery();
        }

        var reopened = new MemoryStore(scope.Root);
        var snapshot = await reopened.LoadAsync();
        var journal = await reopened.LoadTransactionsAsync();

        Assert.Single(snapshot.Audits);
        Assert.Empty(journal.Transactions);
        Assert.Equal(12, ReadUserVersion(scope.Root));
        using var check = new SqliteConnection(
            $"Data Source={Path.Combine(scope.Root, "data", "sentinelcore.db")};Pooling=False");
        check.Open();
        using var command2 = check.CreateCommand();
        command2.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Transactions'";
        Assert.Equal(1L, command2.ExecuteScalar());
    }

    [Fact]
    public async Task BackupAndEventFilesAreReadOnlyArtifacts()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        var eventFile = EventPath(scope, transaction.TransactionId, 1, TransactionState.Planned);
        var backupFile = Path.Combine(scope.Root, "memory", "transactions",
            transaction.TransactionId.ToString("D"), "backup-01.md");
        var eventText = File.ReadAllText(eventFile);
        var backupText = File.ReadAllText(backupFile);

        await scope.Store.LoadTransactionsAsync();
        await scope.Store.FindTransactionAsync(transaction.TransactionId);

        Assert.Equal(eventText, File.ReadAllText(eventFile));
        Assert.Equal(backupText, File.ReadAllText(backupFile));
    }

    [Fact]
    public async Task TamperedPlanContentWithStaleHashIsRejected()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        var path = EventPath(scope, transaction.TransactionId, 1, TransactionState.Planned);
        var json = RecordEnvelope.Unwrap(File.ReadAllText(path).Replace("\r\n", "\n"));
        var entry = JsonSerializer.Deserialize<TransactionEventRecord>(json, AuditJson.Options)!;
        var plan = entry.Plan!;
        var changes = plan.Changes.Select((change, index) => index == 0
            ? change with { After = new TargetValueState(true, "String", "HACK") }
            : change).ToList();
        var forged = entry with { Plan = plan with { Changes = changes } };
        var forgedJson = JsonSerializer.Serialize(forged, AuditJson.Options).Replace("\r\n", "\n");
        File.WriteAllText(path, RecordEnvelope.Wrap(TransactionReport.RenderEvent(forged), forgedJson));

        var snapshot = await scope.Store.LoadTransactionsAsync();

        Assert.NotEmpty(snapshot.Warnings);
        Assert.Empty(snapshot.Transactions);
    }

    [Fact]
    public async Task AuditCannotReuseTransactionId()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        var audit = Samples.Audit(scope.Time.Now);
        var results = audit.Results.Select(item => item with { AuditId = transaction.TransactionId }).ToList();
        var forged = audit with { Id = transaction.TransactionId, Results = results };

        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.SaveAsync(forged));
    }

    [Fact]
    public async Task PersistedAuthorizationIsRecoverable()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);

        var reloaded = await scope.Store.FindTransactionAsync(transaction.TransactionId);

        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded!.Authorization);
        Assert.Equal("confirmacao-teste", reloaded.Authorization!.UserConfirmationId);
        Assert.Equal(transaction.TransactionId, reloaded.Authorization.TransactionId);
        Assert.True(reloaded.Authorization!.Consumed);
        Assert.Contains("Confirmação:", File.ReadAllText(
            scope.Store.GetTransactionReportPath(transaction.TransactionId)));
    }

    [Fact]
    public async Task TruncatedTrailingJournalIsIndeterminateAndBlocksRetry()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        foreach (var (sequence, state) in new[]
                 {
                     (6, TransactionState.ExecutionStarted),
                     (7, TransactionState.ExecutionCompleted),
                     (8, TransactionState.VerificationPassed)
                 })
            File.Delete(EventPath(scope, transaction.TransactionId, sequence, state));

        var snapshot = await scope.Store.LoadTransactionsAsync();
        var truncated = Assert.Single(snapshot.Transactions);
        Assert.Equal(TransactionState.Authorized, truncated.State);
        Assert.True(truncated.IsIndeterminate);

        var retry = await scope.ExecuteAsync("SYN-PREF-01");
        Assert.False(retry.Succeeded);
        Assert.Contains(BlockerCode.IndeterminateState, retry.Blockers);
    }

    [Fact]
    public async Task InvalidStateTransitionIsExcludedFromValidity()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A");
        var simulation = await scope.Engine.SimulateAsync("SYN-PREF-01");
        var plan = simulation.Plan;
        var planned = new TransactionEventRecord(1, plan.TransactionId, 1, TransactionState.Planned,
            scope.Time.Now, plan.OperationId, plan.OperationVersion, plan.CatalogVersion, plan.PlanHash,
            null, null, plan, null, null, null, null, null, null, []);
        await scope.Store.AppendEventAsync(planned);
        var verification = new VerificationResult(true, plan.ObservedStateHash, [], []);
        var invalid = new TransactionEventRecord(1, plan.TransactionId, 2,
            TransactionState.VerificationPassed, scope.Time.Now, plan.OperationId, plan.OperationVersion,
            plan.CatalogVersion, plan.PlanHash, null, null, null, null, null, null, null, verification,
            null, []);

        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.AppendEventAsync(invalid));
        var snapshot = await scope.Store.LoadTransactionsAsync();

        Assert.Empty(snapshot.Warnings);
        var record = Assert.Single(snapshot.Transactions);
        Assert.Equal(TransactionState.Planned, record.State);
        Assert.True(record.IsIndeterminate);
        Assert.False(File.Exists(EventPath(scope, plan.TransactionId, 2,
            TransactionState.VerificationPassed)));
    }

    [Fact]
    public async Task ReportSanitizesEnvelopeMarkersInObservedData()
    {
        using var scope = new SafetyScope();
        scope.Seed("Preference.Test", "A<!-- sentinelcore:payload:v1 --><!-- sha256:```");
        var execution = await scope.ExecuteAsync("SYN-PREF-01");
        Assert.True(execution.Succeeded);
        var transactionId = execution.Transaction!.TransactionId;
        var eventPath = EventPath(scope, transactionId, 1, TransactionState.Planned);
        var eventText = File.ReadAllText(eventPath);
        var backupPath = Path.Combine(scope.Root, "memory", "transactions", transactionId.ToString("D"),
            "backup-01.md");
        var backupText = File.ReadAllText(backupPath);
        var reportText = File.ReadAllText(scope.Store.GetTransactionReportPath(transactionId));
        var eventBody = eventText[..eventText.IndexOf(RecordEnvelope.PayloadMarker, StringComparison.Ordinal)];
        var backupBody = backupText[..backupText.IndexOf(RecordEnvelope.PayloadMarker, StringComparison.Ordinal)];

        Assert.DoesNotContain("<!-- sentinelcore:payload:v1 -->", eventBody);
        Assert.DoesNotContain("<!-- sha256:", backupBody);
        Assert.DoesNotContain("<!-- sentinelcore:payload:v1 -->", reportText);
        Assert.NotEmpty(RecordEnvelope.Unwrap(eventText.Replace("\r\n", "\n")));
        Assert.NotEmpty(RecordEnvelope.Unwrap(backupText.Replace("\r\n", "\n")));
    }

    [Fact]
    public async Task ExecutionCompletedTailTruncationIsIndeterminate()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        File.Delete(EventPath(scope, transaction.TransactionId, 8,
            TransactionState.VerificationPassed));

        var snapshot = await scope.Store.LoadTransactionsAsync();
        var truncated = Assert.Single(snapshot.Transactions);

        Assert.Equal(TransactionState.ExecutionCompleted, truncated.State);
        Assert.True(truncated.IsIndeterminate);
    }

    [Fact]
    public async Task RecoveryOutcomeIsIndexedInSqlite()
    {
        using var scope = new SafetyScope();
        var transaction = await ExecutedAsync(scope);
        await scope.Recovery.RollbackAsync(transaction.TransactionId,
            new RecoveryOptions("confirmacao"));

        using var db = new SqliteConnection(
            $"Data Source={Path.Combine(scope.Root, "data", "sentinelcore.db")};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Outcome, FullyRecovered FROM Recoveries";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Completed", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    private static int ReadUserVersion(string root)
    {
        using var db = new SqliteConnection(
            $"Data Source={Path.Combine(root, "data", "sentinelcore.db")};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
