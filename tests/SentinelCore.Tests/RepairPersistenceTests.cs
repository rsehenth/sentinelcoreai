using Microsoft.Data.Sqlite;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class RepairPersistenceTests
{
    [Fact]
    public async Task RecordSurvivesReopenWithIntentReportProjectionAndSchemaEleven()
    {
        using var scope = new RepairScope();
        var result = await scope.ExecuteAsync(RepairCatalog.SfcRepairId,
            scenario: RepairFixtureScenario.SuccessRepairsCompleted);
        var record = Assert.IsType<RepairRecord>(result.Record);
        var directory = Path.Combine(scope.Root, "memory", "repairs", record.Id.ToString("D"));

        Assert.True(File.Exists(Path.Combine(directory, "intent.md")));
        Assert.True(File.Exists(Path.Combine(directory, "record.md")));
        Assert.True(File.Exists(scope.Store.GetRepairReportPath(record.Id)));
        Assert.True(File.Exists(Path.Combine(scope.Root, "memory", "REPAIRS.md")));
        Assert.Equal(11, ReadUserVersion(scope.Root));
        Assert.Equal(1, Scalar(scope.Root, "SELECT COUNT(*) FROM Repairs"));
        Assert.Equal(5, Scalar(scope.Root, "SELECT COUNT(*) FROM RepairSteps"));

        var reopened = new MemoryStore(scope.Root);
        var history = await reopened.LoadRepairsAsync();

        Assert.Empty(history.Warnings);
        Assert.Empty(history.UnfinishedIntents);
        var copy = Assert.Single(history.Records);
        Assert.Equal(record.Id, copy.Id);
        Assert.Equal(record.Result, copy.Result);
        Assert.Equal(record.Output.Count, copy.Output.Count);
        Assert.Equal(record.AuthorizationId, copy.AuthorizationId);
        Assert.True(copy.IsSynthetic);
        Assert.Contains("FIXTURE SINTÉTICA", await File.ReadAllTextAsync(reopened.GetRepairReportPath(record.Id)));
        Assert.Contains("não possuem rollback", await File.ReadAllTextAsync(reopened.GetRepairReportPath(record.Id)));
        Assert.Contains("Nenhuma reparação é executada automaticamente",
            await File.ReadAllTextAsync(reopened.GetRepairReportPath(record.Id)));
    }

    [Fact]
    public async Task SameRecordIsIdempotentAndDifferentContentIsRejected()
    {
        using var scope = new RepairScope();
        var result = await scope.ExecuteAsync(RepairCatalog.SfcVerifyId);
        var record = Assert.IsType<RepairRecord>(result.Record);
        var recordPath = Path.Combine(scope.Root, "memory", "repairs", record.Id.ToString("D"), "record.md");
        var bytes = await File.ReadAllBytesAsync(recordPath);

        var again = await scope.Store.SaveRepairRecordAsync(record);
        var different = record with { ResultSummary = "Resumo adulterado" };

        Assert.Equal(record.Id, again.Id);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(recordPath));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            scope.Store.SaveRepairRecordAsync(different));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(recordPath));
    }

    [Fact]
    public async Task RecordWithoutIntentIsPreservedButExcludedFromHistory()
    {
        using var scope = new RepairScope();
        var result = await scope.ExecuteAsync(RepairCatalog.SfcVerifyId);
        var record = Assert.IsType<RepairRecord>(result.Record);
        var directory = Path.Combine(scope.Root, "memory", "repairs", record.Id.ToString("D"));
        var recordPath = Path.Combine(directory, "record.md");
        File.Delete(Path.Combine(directory, "intent.md"));

        var history = await new MemoryStore(scope.Root).LoadRepairsAsync();

        Assert.Empty(history.Records);
        Assert.Contains(history.Warnings, warning => warning.Contains("sem intenção", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(recordPath));
    }

    [Fact]
    public async Task OrphanIntentIsExposedAsUnfinishedAndNeverAsCompleted()
    {
        using var seed = new RepairScope();
        var failing = new FailingRepairStore(seed.Store) { FailRecord = true };
        using var scope = new RepairScope(failing);
        var result = await scope.ExecuteAsync(RepairCatalog.SfcVerifyId);

        var history = await scope.Center.LoadHistoryAsync();

        Assert.NotNull(result.Record);
        Assert.False(result.Persisted);
        Assert.Empty(history.Records);
        var intent = Assert.Single(history.UnfinishedIntents);
        Assert.Equal(RepairCatalog.SfcVerifyId, intent.OperationId);
        Assert.True(intent.IsSynthetic);
        var second = await scope.Center.LoadHistoryAsync();
        Assert.DoesNotContain(second.Records, item => item.Id == intent.Id);
    }

    [Fact]
    public async Task CorruptRecordIsPreservedAndExcludedFromIndex()
    {
        using var scope = new RepairScope();
        var result = await scope.ExecuteAsync(RepairCatalog.SfcVerifyId);
        var record = Assert.IsType<RepairRecord>(result.Record);
        var recordPath = Path.Combine(scope.Root, "memory", "repairs", record.Id.ToString("D"), "record.md");
        await File.WriteAllTextAsync(recordPath, "registro corrompido");

        var history = await new MemoryStore(scope.Root).LoadRepairsAsync();

        Assert.Empty(history.Records);
        Assert.Contains(history.Warnings, warning => warning.Contains("inválido", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("registro corrompido", await File.ReadAllTextAsync(recordPath));
        Assert.Equal(11, ReadUserVersion(scope.Root));
    }

    [Fact]
    public async Task SqliteIndexIsRebuiltFromTheImmutableMarkdown()
    {
        using var scope = new RepairScope();
        var result = await scope.ExecuteAsync(RepairCatalog.DismCheckHealthId,
            scenario: RepairFixtureScenario.CorruptionNotFound);
        var record = Assert.IsType<RepairRecord>(result.Record);
        var database = Path.Combine(scope.Root, "data", "sentinelcore.db");
        File.Delete(database);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(database + suffix)) File.Delete(database + suffix);

        var rebuilt = new MemoryStore(scope.Root);
        var history = await rebuilt.LoadRepairsAsync();

        Assert.Empty(history.Warnings);
        var copy = Assert.Single(history.Records);
        Assert.Equal(record.Id, copy.Id);
        Assert.Equal(RepairResultClassification.NoCorruptionDetected, copy.Result);
        Assert.Equal(11, ReadUserVersion(scope.Root));
        Assert.Equal(1, Scalar(scope.Root, "SELECT COUNT(*) FROM Repairs"));
    }

    [Fact]
    public async Task SchemaTenMigratesAdditivelyAndPreservesPreviousHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "SentinelCoreR1MigrationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MemoryStore(root);
            var audit = Samples.Audit(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
            await store.SaveAsync(audit);
            using (var database = Open(root))
            {
                using var command = database.CreateCommand();
                command.CommandText = "DROP TABLE RepairSteps; DROP TABLE Repairs; PRAGMA user_version=10;";
                command.ExecuteNonQuery();
            }

            var reopened = new MemoryStore(root);
            var audits = await reopened.LoadAsync();
            var repairs = await reopened.LoadRepairsAsync();

            Assert.Single(audits.Audits);
            Assert.Empty(repairs.Records);
            Assert.Equal(11, ReadUserVersion(root));
            Assert.Equal(1, Scalar(root, "SELECT COUNT(*) FROM Audits"));
            Assert.Equal(0, Scalar(root, "SELECT COUNT(*) FROM Repairs"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static SqliteConnection Open(string root)
    {
        var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "data", "sentinelcore.db")};Pooling=False");
        connection.Open();
        return connection;
    }

    private static int ReadUserVersion(string root)
    {
        using var database = Open(root);
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int Scalar(string root, string sql)
    {
        using var database = Open(root);
        using var command = database.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
