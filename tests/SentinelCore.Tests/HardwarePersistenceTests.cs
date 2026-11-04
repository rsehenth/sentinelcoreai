using Microsoft.Data.Sqlite;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class HardwarePersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SentinelCoreTests", Guid.NewGuid().ToString("N"));
    private MemoryStore NewStore() => new(root);

    private static HardwareSnapshot Sample(Guid? id = null, HardwareCollectionStatus status = HardwareCollectionStatus.Completed)
    {
        var at = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var item = HardwareSamples.Item("Processor", HardwareCategory.Cpu, "cpu", stableId: "sha256:cpu0000000000000000000000",
            properties: [new("architecture", "x64"), new("capacity_bytes", "1073741824")]);
        return new(1, id ?? Guid.NewGuid(), "0.2.0", "1", at, at.AddSeconds(2), TimeSpan.FromSeconds(2), status,
        [
            new("cpu", "1.0.0", HardwareCategory.Cpu, "Fake: tests only", at, at.AddSeconds(1), TimeSpan.FromSeconds(1),
                status, [item], status == HardwareCollectionStatus.Completed ? [] : ["Limitação sintética."])
        ]);
    }

    [Fact] public async Task FirstRunHasNoSnapshotsAndSchemaFive()
    {
        var loaded = await NewStore().LoadSnapshotsAsync();
        Assert.Empty(loaded.Snapshots); Assert.Empty(loaded.Warnings);
        Assert.Equal(12, ReadUserVersion());
    }

    [Fact] public async Task SavesReopensAndIndexesSnapshot()
    {
        var snapshot = Sample(); await NewStore().SaveSnapshotAsync(snapshot);
        var loaded = await NewStore().LoadSnapshotsAsync();
        Assert.Single(loaded.Snapshots); Assert.Equal(snapshot.Id, loaded.Snapshots[0].Id);
        Assert.True(File.Exists(NewStore().GetHardwareReportPath(snapshot.Id)));
        using var db = Open();
        using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM HardwareItems";
        Assert.Equal(1L, query.ExecuteScalar());
    }

    [Fact] public async Task RetryIsIdempotentAndDoesNotRewriteHistory()
    {
        var snapshot = Sample(); var store = NewStore(); await store.SaveSnapshotAsync(snapshot);
        var record = Directory.GetFiles(Path.Combine(root, "memory", "inventory"), "*.md").Single();
        var original = File.ReadAllText(record); var modified = File.GetLastWriteTimeUtc(record);
        await store.SaveSnapshotAsync(snapshot);
        Assert.Equal(original, File.ReadAllText(record)); Assert.Equal(modified, File.GetLastWriteTimeUtc(record));
    }

    [Fact] public async Task SameIdDifferentPayloadIsRejected()
    {
        var store = NewStore(); var snapshot = Sample(); await store.SaveSnapshotAsync(snapshot);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveSnapshotAsync(snapshot with { SoftwareVersion = "tampered" }));
        Assert.Single((await NewStore().LoadSnapshotsAsync()).Snapshots);
    }

    [Fact] public async Task SnapshotIdCannotCollideWithAuditId()
    {
        var audit = Samples.Audit(DateTimeOffset.UtcNow); var store = NewStore(); await store.SaveAsync(audit);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveSnapshotAsync(Sample(audit.Id)));
    }

    [Fact] public async Task CorruptSnapshotIsPreservedAndExcluded()
    {
        await NewStore().SaveSnapshotAsync(Sample());
        var path = Directory.GetFiles(Path.Combine(root, "memory", "inventory"), "*.md").Single();
        File.WriteAllText(path, "corrupted");
        var loaded = await NewStore().LoadSnapshotsAsync();
        Assert.Empty(loaded.Snapshots); Assert.NotEmpty(loaded.Warnings); Assert.Equal("corrupted", File.ReadAllText(path));
    }

    [Fact] public async Task MissingReportIsRecreated()
    {
        var snapshot = Sample(); var store = NewStore(); await store.SaveSnapshotAsync(snapshot);
        File.Delete(store.GetHardwareReportPath(snapshot.Id));
        await NewStore().LoadSnapshotsAsync();
        Assert.True(File.Exists(store.GetHardwareReportPath(snapshot.Id)));
    }

    [Fact] public async Task SnapshotIndexIsRebuiltAfterDatabaseLoss()
    {
        var snapshot = Sample(); await NewStore().SaveSnapshotAsync(snapshot);
        File.Delete(Path.Combine(root, "data", "sentinelcore.db"));
        Assert.Equal(snapshot.Id, (await NewStore().LoadSnapshotsAsync()).Snapshots.Single().Id);
    }

    [Fact] public async Task PartialAndCancelledSnapshotsAreRetained()
    {
        var store = NewStore();
        await store.SaveSnapshotAsync(Sample(status: HardwareCollectionStatus.Partial));
        await store.SaveSnapshotAsync(Sample(status: HardwareCollectionStatus.Cancelled));
        var loaded = await NewStore().LoadSnapshotsAsync();
        Assert.Equal(2, loaded.Snapshots.Count);
        Assert.Contains(loaded.Snapshots, s => s.Status == HardwareCollectionStatus.Partial);
        Assert.Contains(loaded.Snapshots, s => s.Status == HardwareCollectionStatus.Cancelled);
    }

    [Fact] public async Task MigrationFromSchemaOnePreservesAuditsAndAddsHardwareTables()
    {
        var audit = Samples.Audit(DateTimeOffset.UtcNow); await NewStore().SaveAsync(audit);
        using (var db = Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = "DROP TABLE EventGroups; DROP TABLE EventSources; DROP TABLE EventInvestigations; " +
                "DROP TABLE HardwareItems; DROP TABLE HardwareCollectorResults; DROP TABLE HardwareSnapshots; " +
                "PRAGMA user_version=1";
            command.ExecuteNonQuery();
        }
        var loaded = await NewStore().LoadSnapshotsAsync();
        Assert.Empty(loaded.Snapshots);
        Assert.Single((await NewStore().LoadAsync()).Audits);
        Assert.Equal(12, ReadUserVersion());
    }

    [Fact] public async Task FutureSchemaIsRejectedForSnapshots()
    {
        await NewStore().LoadAsync();
        using (var db = Open())
        {
            using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=109"; command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().LoadSnapshotsAsync());
    }

    [Fact] public async Task ItemCategoryMustMatchCollectorCategory()
    {
        var valid = Sample();
        var mismatched = valid.Results[0] with
        {
            Items = [HardwareSamples.Item("GPU", HardwareCategory.Gpu, "gpu", stableId: "sha256:gpu")]
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().SaveSnapshotAsync(valid with { Results = [mismatched] }));
    }

    [Fact] public async Task DuplicateStableIdsWithinResultAreRejected()
    {
        var valid = Sample();
        var duplicated = valid.Results[0] with
        {
            Items =
            [
                HardwareSamples.Item("A", HardwareCategory.Cpu, "cpu", stableId: "sha256:same"),
                HardwareSamples.Item("B", HardwareCategory.Cpu, "cpu", stableId: "sha256:same")
            ]
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().SaveSnapshotAsync(valid with { Results = [duplicated] }));
    }

    [Fact] public async Task HardwareRecordContainsPayloadAndChecksum()
    {
        var snapshot = Sample(); await NewStore().SaveSnapshotAsync(snapshot);
        var path = Directory.GetFiles(Path.Combine(root, "memory", "inventory"), "*.md").Single();
        var text = File.ReadAllText(path);
        Assert.Contains("<!-- sentinelcore:payload:v1 -->", text);
        Assert.Contains("<!-- sha256:", text);
        Assert.Contains("Inventário de hardware", text);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "data", "sentinelcore.db")};Pooling=False");
        connection.Open(); return connection;
    }

    private int ReadUserVersion()
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
