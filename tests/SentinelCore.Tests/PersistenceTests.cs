using Microsoft.Data.Sqlite;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SentinelCoreTests", Guid.NewGuid().ToString("N"));
    private MemoryStore NewStore() => new(root);
    [Fact] public async Task FirstRunHasNoInventedHistory()
    {
        var snapshot = await NewStore().LoadAsync(); Assert.Empty(snapshot.Audits); Assert.Empty(snapshot.Warnings);
    }
    [Fact] public async Task CreatesMemoryAndDatabase()
    {
        await NewStore().LoadAsync();
        Assert.True(File.Exists(Path.Combine(root, "memory", "MEMORY.md")));
        Assert.True(File.Exists(Path.Combine(root, "data", "sentinelcore.db")));
    }
    [Fact] public async Task SavesAndReopensRealSqliteIndex()
    {
        var audit = Samples.Audit(DateTimeOffset.UtcNow); await NewStore().SaveAsync(audit);
        var snapshot = await NewStore().LoadAsync(); Assert.Single(snapshot.Audits); Assert.Equal(audit.Id, snapshot.Audits[0].Id);
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "data", "sentinelcore.db")};Pooling=False"); db.Open();
        using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM CollectorResults";
        Assert.Equal(1L, query.ExecuteScalar());
    }
    [Fact] public async Task RetryDoesNotDuplicateOrRewriteHistory()
    {
        var audit = Samples.Audit(DateTimeOffset.UtcNow); var store = NewStore(); await store.SaveAsync(audit);
        var record = Directory.GetFiles(Path.Combine(root, "memory", "audits", "basic"), "*.md").Single();
        var original = File.ReadAllText(record); var modifiedAt = File.GetLastWriteTimeUtc(record);
        await store.SaveAsync(audit);
        Assert.Single((await store.LoadAsync()).Audits); Assert.Equal(original, File.ReadAllText(record));
        Assert.Equal(modifiedAt, File.GetLastWriteTimeUtc(record));
    }
    [Fact] public async Task SameIdDifferentPayloadIsRejected()
    {
        var store = NewStore(); var audit = Samples.Audit(DateTimeOffset.UtcNow); await store.SaveAsync(audit);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(audit with { SoftwareVersion = "tampered" }));
    }
    [Fact] public async Task TwoAuditsKeepBothHistories()
    {
        var store = NewStore(); await store.SaveAsync(Samples.Audit(DateTimeOffset.UtcNow.AddDays(-1)));
        await store.SaveAsync(Samples.Audit(DateTimeOffset.UtcNow)); Assert.Equal(2, (await NewStore().LoadAsync()).Audits.Count);
    }
    [Fact] public async Task SameIdAcrossKindsIsRejected()
    {
        var store = NewStore(); var audit = Samples.Audit(DateTimeOffset.UtcNow); await store.SaveAsync(audit);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(audit with { Kind = AuditKind.Full }));
        Assert.Single((await NewStore().LoadAsync()).Audits);
    }
    [Fact] public async Task PayloadTamperingIsDetected()
    {
        await NewStore().SaveAsync(Samples.Audit(DateTimeOffset.UtcNow));
        var path = Directory.GetFiles(Path.Combine(root, "memory", "audits", "basic"), "*.md").Single();
        File.WriteAllText(path, File.ReadAllText(path).Replace("17179869184", "17179869185"));
        var snapshot = await NewStore().LoadAsync(); Assert.Empty(snapshot.Audits); Assert.NotEmpty(snapshot.Warnings);
    }
    [Fact] public async Task CompletedWithoutCollectorDataIsRejected()
    {
        var audit = Samples.Audit(DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().SaveAsync(audit with { Results = [audit.Results[0] with { Data = null }] }));
    }
    [Fact] public async Task NullSoftwareVersionIsRejectedAtBoundary()
    {
        var audit = Samples.Audit(DateTimeOffset.UtcNow) with { SoftwareVersion = null! };
        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().SaveAsync(audit));
    }
    [Fact] public async Task OversizedRecordCannotBeAcknowledgedAsSaved()
    {
        var audit = Samples.Audit(DateTimeOffset.UtcNow) with { SoftwareVersion = new string('x', 4 * 1024 * 1024) };
        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().SaveAsync(audit));
        Assert.Empty((await NewStore().LoadAsync()).Audits);
    }
    [Fact] public async Task FutureDatabaseSchemaIsPreservedWithoutDowngrade()
    {
        await NewStore().LoadAsync();
        using (var db = new SqliteConnection($"Data Source={Path.Combine(root, "data", "sentinelcore.db")};Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=109"; command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().LoadAsync());
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "data"), "*.corrupt-*"));
    }
    [Fact] public async Task ReportWriteFailureRetainsRecordAndAllowsRetry()
    {
        var store = NewStore(); await store.LoadAsync(); var audit = Samples.Audit(DateTimeOffset.UtcNow);
        Directory.CreateDirectory(store.GetReportPath(audit.Id));
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(audit));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "memory", "audits", "basic"), "*.md"));
        Directory.Delete(store.GetReportPath(audit.Id)); await store.SaveAsync(audit);
        Assert.Single((await store.LoadAsync()).Audits); Assert.True(File.Exists(store.GetReportPath(audit.Id)));
    }
    [Theory] [InlineData(DiagnosticStatus.Cancelled)] [InlineData(DiagnosticStatus.Partial)] [InlineData(DiagnosticStatus.Failed)]
    public async Task UnsuccessfulAuditIsRetained(DiagnosticStatus status)
    {
        await NewStore().SaveAsync(Samples.Audit(DateTimeOffset.UtcNow, status));
        Assert.Equal(status, (await NewStore().LoadAsync()).Audits.Single().Status);
    }
    [Fact] public async Task RebuildsIndexAfterDatabaseLoss()
    {
        var audit = Samples.Audit(DateTimeOffset.UtcNow); await NewStore().SaveAsync(audit);
        File.Delete(Path.Combine(root, "data", "sentinelcore.db"));
        Assert.Equal(audit.Id, (await NewStore().LoadAsync()).Audits.Single().Id);
    }
    [Fact] public async Task ReconcilesCommitGapFromMarkdown()
    {
        var store = NewStore(); await store.SaveAsync(Samples.Audit(DateTimeOffset.UtcNow));
        using (var db = new SqliteConnection($"Data Source={Path.Combine(root, "data", "sentinelcore.db")};Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "DELETE FROM CollectorResults; DELETE FROM Audits"; command.ExecuteNonQuery();
        }
        Assert.Single((await NewStore().LoadAsync()).Audits);
    }
    [Fact] public async Task CorruptMarkdownIsPreservedAndExcluded()
    {
        await NewStore().SaveAsync(Samples.Audit(DateTimeOffset.UtcNow));
        var path = Directory.GetFiles(Path.Combine(root, "memory", "audits", "basic"), "*.md").Single();
        File.WriteAllText(path, "corrupted"); var snapshot = await NewStore().LoadAsync();
        Assert.Empty(snapshot.Audits); Assert.NotEmpty(snapshot.Warnings); Assert.Equal("corrupted", File.ReadAllText(path));
    }
    [Fact] public async Task MissingMarkdownIsReportedAndDoesNotRemainValid()
    {
        await NewStore().SaveAsync(Samples.Audit(DateTimeOffset.UtcNow));
        File.Delete(Directory.GetFiles(Path.Combine(root, "memory", "audits", "basic"), "*.md").Single());
        var snapshot = await NewStore().LoadAsync(); Assert.Empty(snapshot.Audits); Assert.NotEmpty(snapshot.Warnings);
    }
    [Fact] public async Task MissingReportIsRecreated()
    {
        var store = NewStore(); var audit = Samples.Audit(DateTimeOffset.UtcNow); await store.SaveAsync(audit);
        File.Delete(store.GetReportPath(audit.Id)); await NewStore().LoadAsync(); Assert.True(File.Exists(store.GetReportPath(audit.Id)));
    }
    [Fact] public async Task CorruptDatabaseIsPreservedAndRebuilt()
    {
        await NewStore().SaveAsync(Samples.Audit(DateTimeOffset.UtcNow));
        File.WriteAllText(Path.Combine(root, "data", "sentinelcore.db"), "not SQLite");
        var snapshot = await NewStore().LoadAsync(); Assert.Single(snapshot.Audits); Assert.NotEmpty(snapshot.Warnings);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "data"), "*.corrupt-*"));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
