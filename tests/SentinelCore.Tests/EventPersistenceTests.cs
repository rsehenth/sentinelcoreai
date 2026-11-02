using Microsoft.Data.Sqlite;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class EventPersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "SentinelCoreTests", Guid.NewGuid().ToString("N"));

    private MemoryStore NewStore() => new(root);

    [Fact]
    public async Task SaveLoadAndReopenPreservesEvidenceAndReport()
    {
        var investigation = Samples.Investigation();
        var store = NewStore();
        await store.SaveInvestigationAsync(investigation);

        var loaded = await NewStore().LoadInvestigationsAsync();
        var copy = Assert.Single(loaded.Investigations);
        Assert.Equal(investigation.Id, copy.Id);
        Assert.Equal(investigation.Groups.Single(g => g.Id == "applications:crash").EventRecordCount,
            copy.Groups.Single(g => g.Id == "applications:crash").EventRecordCount);
        Assert.Equal(investigation.Reports.Single().ReportId, copy.Reports.Single().ReportId);
        Assert.True(File.Exists(store.GetEventReportPath(investigation.Id)));
        Assert.Contains("Investigação de estabilidade e eventos",
            File.ReadAllText(store.GetEventReportPath(investigation.Id)));
        Assert.Equal(9, ReadUserVersion());
    }

    [Fact]
    public async Task RetryIsIdempotentAndDoesNotRewriteHistory()
    {
        var investigation = Samples.Investigation();
        var store = NewStore();
        await store.SaveInvestigationAsync(investigation);
        var record = Directory.GetFiles(Path.Combine(root, "memory", "events"), "*.md").Single();
        var original = File.ReadAllText(record);
        var modified = File.GetLastWriteTimeUtc(record);

        await store.SaveInvestigationAsync(investigation);

        Assert.Equal(original, File.ReadAllText(record));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(record));
    }

    [Fact]
    public async Task SameIdDifferentPayloadIsRejected()
    {
        var store = NewStore();
        var investigation = Samples.Investigation();
        await store.SaveInvestigationAsync(investigation);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveInvestigationAsync(investigation with
            {
                PeriodFromUtc = investigation.PeriodFromUtc.AddMinutes(-1)
            }));
    }

    [Fact]
    public async Task InvestigationIdCannotCollideWithAuditOrHardware()
    {
        var audit = Samples.Audit(DateTimeOffset.UtcNow);
        var store = NewStore();
        await store.SaveAsync(audit);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveInvestigationAsync(Samples.Investigation(id: audit.Id)));
    }

    [Fact]
    public async Task CorruptInvestigationIsPreservedAndExcluded()
    {
        await NewStore().SaveInvestigationAsync(Samples.Investigation());
        var path = Directory.GetFiles(Path.Combine(root, "memory", "events"), "*.md").Single();
        File.WriteAllText(path, "corrupted");

        var loaded = await NewStore().LoadInvestigationsAsync();

        Assert.Empty(loaded.Investigations);
        Assert.NotEmpty(loaded.Warnings);
        Assert.Equal("corrupted", File.ReadAllText(path));
    }

    [Fact]
    public async Task EventIndexIsRebuiltAfterDatabaseLoss()
    {
        var investigation = Samples.Investigation();
        await NewStore().SaveInvestigationAsync(investigation);
        File.Delete(Path.Combine(root, "data", "sentinelcore.db"));

        var loaded = await NewStore().LoadInvestigationsAsync();

        Assert.Equal(investigation.Id, loaded.Investigations.Single().Id);
        Assert.True(File.Exists(Path.Combine(root, "memory", "EVENTS.md")));
    }

    [Fact]
    public async Task EventInvestigationDoesNotRenewAuditLastSuccess()
    {
        var store = NewStore();
        // Âncora no relógio injetado: usar DateTimeOffset.UtcNow tornava o teste dependente
        // da hora do dia (Expiring entre 00:00 e 15:00 UTC, Outdated apenas após 15:00 UTC).
        var clock = new FixedTime();
        var old = Samples.Audit(clock.Now.AddDays(-8));
        await store.SaveAsync(old);
        await store.SaveInvestigationAsync(Samples.Investigation());

        var audits = (await store.LoadAsync()).Audits;
        var freshness = new FreshnessEngine(clock).Evaluate(audits, AuditKind.Basic);

        Assert.Equal(FreshnessState.Outdated, freshness.State);
        Assert.Equal(old.Id, audits.Single().Id);
    }

    [Fact]
    public async Task InvalidGroupCountsAreRejected()
    {
        var investigation = Samples.Investigation();
        var broken = investigation with
        {
            Groups = [investigation.Groups[0] with { EventRecordCount = 99 }, investigation.Groups[1]]
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().SaveInvestigationAsync(broken));
    }

    [Fact]
    public async Task CompletedWithLimitationIsRejected()
    {
        var investigation = Samples.Investigation() with { Limitations = ["limitação sintética"] };

        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().SaveInvestigationAsync(investigation));
    }

    [Fact]
    public async Task FutureSchemaTenIsRejected()
    {
        await NewStore().LoadAsync();
        using (var db = Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA user_version=10";
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().LoadInvestigationsAsync());
    }

    [Fact]
    public async Task MigrationFromSchemaTwoPreservesAuditsAndHardware()
    {
        var audit = Samples.Audit(DateTimeOffset.UtcNow);
        var snapshot = HardwareSamples.Snapshot(HardwareSamples.Result(HardwareCategory.Cpu,
            HardwareCollectionStatus.Completed, HardwareSamples.Item("CPU", HardwareCategory.Cpu)));
        var store = NewStore();
        await store.SaveAsync(audit);
        await store.SaveSnapshotAsync(snapshot);
        using (var db = Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = "DROP TABLE EventGroups; DROP TABLE EventSources; " +
                "DROP TABLE EventInvestigations; PRAGMA user_version=2";
            command.ExecuteNonQuery();
        }

        var loaded = await NewStore().LoadInvestigationsAsync();

        Assert.Empty(loaded.Investigations);
        Assert.Single((await NewStore().LoadAsync()).Audits);
        Assert.Single((await NewStore().LoadSnapshotsAsync()).Snapshots);
        Assert.Equal(9, ReadUserVersion());
    }

    [Fact]
    public async Task MissingReportIsRecreated()
    {
        var investigation = Samples.Investigation();
        var store = NewStore();
        await store.SaveInvestigationAsync(investigation);
        File.Delete(store.GetEventReportPath(investigation.Id));

        await NewStore().LoadInvestigationsAsync();

        Assert.True(File.Exists(store.GetEventReportPath(investigation.Id)));
    }

    [Fact]
    public async Task ReportKeepsCountsSeparatedAndCausalityNotAssessed()
    {
        var investigation = Samples.Investigation();
        var store = NewStore();
        await store.SaveInvestigationAsync(investigation);

        var text = File.ReadAllText(store.GetEventReportPath(investigation.Id));

        Assert.Contains("Registros de evento: 2", text);
        Assert.Contains("Relatórios WER distintos: 1", text);
        Assert.Contains("Causalidade: não avaliada", text);
        Assert.Contains("não confirma defeito de hardware", text);
        Assert.Contains("Última ocorrência conhecida", text);
        Assert.Contains("| System | Sim | Sim | Sim | — |", text);
    }

    [Fact]
    public async Task ReportSanitizesUntrustedMarkdown()
    {
        var investigation = Samples.Investigation();
        var hostile = investigation.Events[0] with
        {
            Message = "<script>alert(1)</script> `x`\r\nlinha"
        };
        var modified = investigation with { Events = [hostile, investigation.Events[1]] };
        var store = NewStore();
        await store.SaveInvestigationAsync(modified);

        var text = File.ReadAllText(store.GetEventReportPath(modified.Id));

        Assert.DoesNotContain("<script>", text);
        Assert.Contains("&lt;script&gt;", text);
        Assert.DoesNotContain("`x`", text);
    }

    [Fact]
    public async Task EventRecordContainsPayloadAndChecksum()
    {
        await NewStore().SaveInvestigationAsync(Samples.Investigation());

        var record = Directory.GetFiles(Path.Combine(root, "memory", "events"), "*.md").Single();
        var text = File.ReadAllText(record);

        Assert.Contains("<!-- sentinelcore:payload:v1 -->", text);
        Assert.Contains("<!-- sha256:", text);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "data", "sentinelcore.db")};Pooling=False");
        connection.Open();
        return connection;
    }

    private int ReadUserVersion()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
