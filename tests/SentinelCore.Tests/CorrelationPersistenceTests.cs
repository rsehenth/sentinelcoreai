using System.Text;
using Microsoft.Data.Sqlite;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class CorrelationPersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SentinelCoreCorrelationTests",
        Guid.NewGuid().ToString("N"));

    private MemoryStore NewStore() => new(root);

    private static async Task<CorrelationInvestigation> NewInvestigation(CorrelationSources? sources = null,
        bool deduplicated = false)
    {
        var audits = new CorrelationSamples.FakeAuditStore();
        var events = new CorrelationSamples.FakeEventStore();
        var graphics = new CorrelationSamples.FakeGraphicsStore();
        var performance = new CorrelationSamples.FakePerformanceStore();
        var network = new CorrelationSamples.FakeNetworkStore();
        audits.Records.Add(CorrelationSamples.AuditWithBoot(CorrelationSamples.Now.AddHours(-5)));
        var tdr = GraphicsSamples.DisplayTdr(recordId: 12345, at: CorrelationSamples.Now.AddHours(-2));
        events.Records.Add(await CorrelationSamples.EventsAsync(events: [tdr]));
        if (deduplicated)
            graphics.Records.Add(await CorrelationSamples.GraphicsAsync(events: [tdr]));

        var reader = new CorrelationEvidenceReader(audits, null, events, performance, network, graphics);
        var runner = new EvidenceCorrelationRunner(reader, CorrelationSamples.Clock());
        return await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: sources ?? (deduplicated
                ? CorrelationSources.Audits | CorrelationSources.Events | CorrelationSources.Graphics
                : CorrelationSources.Audits | CorrelationSources.Events)));
    }

    [Fact]
    public async Task SaveLoadReopenPreservesEvidenceRelationsAndReport()
    {
        var investigation = await NewInvestigation();
        var store = NewStore();

        var bytes = await store.SaveCorrelationInvestigationAsync(investigation);
        var loaded = await NewStore().LoadCorrelationInvestigationsAsync();

        Assert.Empty(loaded.Warnings);
        var copy = Assert.Single(loaded.Investigations);
        Assert.Equal(investigation.Id, copy.Id);
        Assert.Equal(investigation.Status, copy.Status);
        Assert.Equal(investigation.Coverage.Status, copy.Coverage.Status);
        Assert.Equal(investigation.Evidence.Count, copy.Evidence.Count);
        Assert.Equal(investigation.Timeline.Count, copy.Timeline.Count);
        Assert.Equal(investigation.Relations.Count, copy.Relations.Count);
        Assert.Equal(investigation.AnalysisNotes.Count, copy.AnalysisNotes.Count);
        Assert.True(bytes > 0);
        Assert.True(File.Exists(store.GetCorrelationReportPath(investigation.Id)));
        Assert.True(File.Exists(Path.Combine(root, "memory", "CORRELATION.md")));
        Assert.Equal(12, ReadUserVersion());
    }

    [Fact]
    public async Task EnvelopeContainsPayloadAndMatchingSqliteChecksumAndCounts()
    {
        var investigation = await NewInvestigation();
        var store = NewStore();
        await store.SaveCorrelationInvestigationAsync(investigation);

        var recordPath = Path.Combine(root, "memory", "correlation", $"{investigation.Id:D}.md");
        var markdown = await File.ReadAllTextAsync(recordPath, Encoding.UTF8);
        Assert.Contains("<!-- sentinelcore:payload:v1 -->", markdown);
        var json = RecordEnvelope.Unwrap(markdown.Replace("\r\n", "\n"));
        var expectedHash = RecordEnvelope.Hash(json);

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT PayloadHash, EvidenceCount, TimelineCount, RelationCount, UndatedCount, MergedReferenceCount
            FROM CorrelationInvestigations WHERE Id = $id
            """;
        command.Parameters.AddWithValue("$id", investigation.Id.ToString("D"));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expectedHash, reader.GetString(0));
        Assert.Equal(investigation.Evidence.Count, reader.GetInt32(1));
        Assert.Equal(investigation.Timeline.Count, reader.GetInt32(2));
        Assert.Equal(investigation.Relations.Count, reader.GetInt32(3));
        Assert.Equal(investigation.Coverage.UndatedEvidence, reader.GetInt32(4));
        Assert.Equal(investigation.Coverage.MergedReferences, reader.GetInt32(5));
    }

    [Fact]
    public async Task RetryIsIdempotentAndDoesNotRewriteTheDurableRecord()
    {
        var investigation = await NewInvestigation();
        var store = NewStore();
        await store.SaveCorrelationInvestigationAsync(investigation);

        var record = Directory.GetFiles(Path.Combine(root, "memory", "correlation"), "*.md").Single();
        var before = await File.ReadAllTextAsync(record, Encoding.UTF8);
        var modified = File.GetLastWriteTimeUtc(record);

        await store.SaveCorrelationInvestigationAsync(investigation);

        Assert.Equal(before, await File.ReadAllTextAsync(record, Encoding.UTF8));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(record));
        Assert.Single((await NewStore().LoadCorrelationInvestigationsAsync()).Investigations);
    }

    [Fact]
    public async Task SameIdDifferentPayloadIsRejectedAndOriginalIsPreserved()
    {
        var investigation = await NewInvestigation();
        var store = NewStore();
        await store.SaveCorrelationInvestigationAsync(investigation);

        var tampered = investigation with { CompletedUtc = investigation.CompletedUtc.AddSeconds(5) };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveCorrelationInvestigationAsync(tampered));

        var persisted = Assert.Single((await NewStore().LoadCorrelationInvestigationsAsync()).Investigations);
        Assert.Equal(investigation.CompletedUtc, persisted.CompletedUtc);
    }

    [Fact]
    public async Task CorrelationIdCannotCollideWithOtherHistories()
    {
        var store = NewStore();
        var audit = Samples.Audit(CorrelationSamples.Now.AddHours(-1));
        await store.SaveAsync(audit);
        var investigation = await NewInvestigation();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveCorrelationInvestigationAsync(investigation with { Id = audit.Id }));

        await store.SaveCorrelationInvestigationAsync(investigation);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(Samples.Audit(CorrelationSamples.Now.AddHours(-1), id: investigation.Id)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveInvestigationAsync(Samples.Investigation(id: investigation.Id)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveSessionAsync(PerformanceSamples.Session() with { Id = investigation.Id }));
    }

    [Fact]
    public async Task CorruptRecordIsPreservedAndExcludedWithWarning()
    {
        var valid = await NewInvestigation();
        var corrupt = await NewInvestigation();
        var store = NewStore();
        await store.SaveCorrelationInvestigationAsync(valid);
        await store.SaveCorrelationInvestigationAsync(corrupt);

        var corruptPath = Path.Combine(root, "memory", "correlation", $"{corrupt.Id:D}.md");
        await File.WriteAllTextAsync(corruptPath, "corrupted-correlation-envelope", Encoding.UTF8);

        var loaded = await NewStore().LoadCorrelationInvestigationsAsync();
        var recovered = Assert.Single(loaded.Investigations);
        Assert.Equal(valid.Id, recovered.Id);
        Assert.NotEmpty(loaded.Warnings);
        Assert.Contains(loaded.Warnings, warning => warning.Contains(corrupt.Id.ToString("D")));
        Assert.Equal("corrupted-correlation-envelope", await File.ReadAllTextAsync(corruptPath, Encoding.UTF8));
    }

    [Fact]
    public async Task IndexIsRebuiltFromMarkdownAfterDatabaseLoss()
    {
        var investigation = await NewInvestigation();
        await NewStore().SaveCorrelationInvestigationAsync(investigation);

        File.Delete(Path.Combine(root, "data", "sentinelcore.db"));

        var loaded = await NewStore().LoadCorrelationInvestigationsAsync();
        var rebuilt = Assert.Single(loaded.Investigations);
        Assert.Equal(investigation.Id, rebuilt.Id);
        Assert.Equal(investigation.Relations.Count, rebuilt.Relations.Count);
        Assert.Equal(12, ReadUserVersion());

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM CorrelationInvestigations;
            SELECT COUNT(*) FROM CorrelationSources;
            SELECT COUNT(*) FROM CorrelationEvidence;
            SELECT COUNT(*) FROM CorrelationRelations;
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.True(reader.NextResult() && reader.Read());
        Assert.Equal((long)investigation.Sources.Count, reader.GetInt64(0));
        Assert.True(reader.NextResult() && reader.Read());
        Assert.Equal((long)investigation.Evidence.Count, reader.GetInt64(0));
        Assert.True(reader.NextResult() && reader.Read());
        Assert.Equal((long)investigation.Relations.Count, reader.GetInt64(0));
    }

    [Fact]
    public async Task MigrationFromSchemaSevenAddsCorrelationTablesAndPreservesOtherRecords()
    {
        var store = NewStore();
        await store.SaveAsync(Samples.Audit(CorrelationSamples.Now.AddHours(-1)));
        await store.SaveInvestigationAsync(Samples.Investigation());
        await store.SaveSessionAsync(PerformanceSamples.Session());

        using (var db = Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = """
                DROP TABLE CorrelationRelations;
                DROP TABLE CorrelationEvidence;
                DROP TABLE CorrelationSources;
                DROP TABLE CorrelationInvestigations;
                PRAGMA user_version=7;
                """;
            command.ExecuteNonQuery();
        }

        var correlation = await NewStore().LoadCorrelationInvestigationsAsync();
        Assert.Empty(correlation.Investigations);
        Assert.Single((await NewStore().LoadAsync()).Audits);
        Assert.Single((await NewStore().LoadInvestigationsAsync()).Investigations);
        Assert.Single((await NewStore().LoadSessionsAsync()).Sessions);
        Assert.Equal(12, ReadUserVersion());
    }

    [Fact]
    public async Task FutureSchemaElevenIsRejectedWithoutCorruptingDatabase()
    {
        await NewStore().LoadCorrelationInvestigationsAsync();
        using (var db = Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA user_version=13";
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().LoadCorrelationInvestigationsAsync());
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "data"), "*.corrupt-*"));
    }

    [Fact]
    public async Task ReportSanitizesUntrustedTextAndKeepsCausalityNotAssessed()
    {
        var investigation = await NewInvestigation();
        var hostile = investigation.Evidence[0] with
        {
            Title = "<script>alert(1)</script> `hostile`\r\nlinha"
        };
        var session = investigation with
        {
            Evidence = [hostile, .. investigation.Evidence.Skip(1)]
        };

        var store = NewStore();
        await store.SaveCorrelationInvestigationAsync(session);
        var report = await File.ReadAllTextAsync(store.GetCorrelationReportPath(session.Id), Encoding.UTF8);

        Assert.DoesNotContain("<script>", report);
        Assert.Contains("&lt;script&gt;", report);
        Assert.Contains("Causalidade: não avaliada", report);
        Assert.Contains("não renova", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("causou", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("defeito físico confirmado", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationAfterReadingPreservesEvidenceAndIsPersistable()
    {
        var tdr = GraphicsSamples.DisplayTdr(recordId: 7788, at: CorrelationSamples.Now.AddHours(-2));
        var graphics = new CorrelationSamples.FakeGraphicsStore();
        graphics.Records.Add(await CorrelationSamples.GraphicsAsync(events: [tdr]));
        using var cancellation = new CancellationTokenSource();
        graphics.CancelAfterLoad = cancellation;

        var reader = new CorrelationEvidenceReader(graphics: graphics);
        var runner = new EvidenceCorrelationRunner(reader, CorrelationSamples.Clock());
        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Graphics), cancellationToken: cancellation.Token);

        Assert.Equal(CorrelationStatus.Cancelled, investigation.Status);
        Assert.Equal(CorrelationCoverageStatus.Partial, investigation.Coverage.Status);
        Assert.NotEmpty(investigation.Evidence);
        Assert.Equal(0, investigation.Coverage.CancelledSources);
        Assert.All(investigation.Sources,
            source => Assert.NotEqual(CorrelationSourceStatus.Cancelled, source.Status));
        Assert.Contains(investigation.Limitations,
            item => item.Contains("cobertura permanece parcial", StringComparison.OrdinalIgnoreCase));

        var store = NewStore();
        await store.SaveCorrelationInvestigationAsync(investigation);
        var reloaded = await store.LoadCorrelationInvestigationsAsync();
        var copy = Assert.Single(reloaded.Investigations);
        Assert.Equal(CorrelationStatus.Cancelled, copy.Status);
        Assert.Equal(investigation.Evidence.Count, copy.Evidence.Count);
        Assert.Equal(investigation.Coverage.CancelledSources, copy.Coverage.CancelledSources);
    }

    [Fact]
    public async Task CancelledInvestigationIsPersistable()
    {
        var reader = new CorrelationEvidenceReader(events: new CorrelationSamples.FakeEventStore());
        var runner = new EvidenceCorrelationRunner(reader, CorrelationSamples.Clock());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var investigation = await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Events), cancellationToken: cancellation.Token);
        Assert.Equal(CorrelationStatus.Cancelled, investigation.Status);

        var store = NewStore();
        await store.SaveCorrelationInvestigationAsync(investigation);
        var loaded = Assert.Single((await NewStore().LoadCorrelationInvestigationsAsync()).Investigations);
        Assert.Equal(CorrelationStatus.Cancelled, loaded.Status);
        Assert.Equal(CorrelationCoverageStatus.Unavailable, loaded.Coverage.Status);
    }

    [Fact]
    public async Task ConsolidatedOriginsSurviveSaveAndReload()
    {
        var investigation = await NewInvestigation(deduplicated: true);
        var evidence = Assert.Single(investigation.Evidence, item => item.Id == "evt:System:12345");
        Assert.Equal(2, evidence.Origins.Count);

        var store = NewStore();
        await store.SaveCorrelationInvestigationAsync(investigation);
        var reloaded = Assert.Single(Assert.Single(
            (await NewStore().LoadCorrelationInvestigationsAsync()).Investigations).Evidence,
            item => item.Id == "evt:System:12345");

        Assert.Equal(2, reloaded.Origins.Count);
        Assert.Contains(reloaded.Origins, origin => origin.Source == CorrelationSource.Events);
        Assert.Contains(reloaded.Origins, origin => origin.Source == CorrelationSource.Graphics);
        Assert.Equal(2, reloaded.Origins.Select(origin => origin.SourceRecordId).Distinct().Count());
    }

    [Fact]
    public async Task ForgedCoverageCountersAreRejected()
    {
        var investigation = await NewInvestigation();
        var forged = investigation with
        {
            Coverage = investigation.Coverage with
            {
                Status = CorrelationCoverageStatus.Complete,
                MergedReferences = 3
            }
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewStore().SaveCorrelationInvestigationAsync(forged));
        Assert.Empty((await NewStore().LoadCorrelationInvestigationsAsync()).Investigations);
    }

    [Fact]
    public async Task ForgedCausalityAndMissingMandatoryLimitationsAreRejected()
    {
        var investigation = await NewInvestigation();
        var relation = investigation.Relations[0];
        var forgedRelation = investigation with
        {
            Relations = [relation with { Causality = (CausalityStatus)99 }]
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewStore().SaveCorrelationInvestigationAsync(forgedRelation));

        var missing = investigation with
        {
            Limitations = investigation.Limitations
                .Where(item => item != CorrelationGuidance.CausalityLimitation).ToArray()
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewStore().SaveCorrelationInvestigationAsync(missing));
    }

    [Fact]
    public async Task RelationReferencingUnknownEvidenceIsRejected()
    {
        var investigation = await NewInvestigation();
        var relation = investigation.Relations[0];
        var forged = investigation with
        {
            Relations = [relation with { EvidenceIds = ["evt:desconhecido:1", "evt:desconhecido:2"] }]
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewStore().SaveCorrelationInvestigationAsync(forged));
    }

    [Fact]
    public async Task SavingCorrelationDoesNotRewriteAuditOrEventRecords()
    {
        var store = NewStore();
        var audit = Samples.Audit(CorrelationSamples.Now.AddHours(-1));
        var eventInvestigation = Samples.Investigation();
        await store.SaveAsync(audit);
        await store.SaveInvestigationAsync(eventInvestigation);

        var auditPath = Path.Combine(root, "memory", "audits", "basic", $"{audit.Id:D}.md");
        var eventPath = Path.Combine(root, "memory", "events", $"{eventInvestigation.Id:D}.md");
        var auditBefore = await File.ReadAllBytesAsync(auditPath);
        var eventBefore = await File.ReadAllBytesAsync(eventPath);

        await store.SaveCorrelationInvestigationAsync(await NewInvestigation());

        Assert.Equal(auditBefore, await File.ReadAllBytesAsync(auditPath));
        Assert.Equal(eventBefore, await File.ReadAllBytesAsync(eventPath));
        Assert.Single((await NewStore().LoadInvestigationsAsync()).Investigations);
    }

    [Fact]
    public async Task ForgedSourceCountersAreRejected()
    {
        var investigation = await NewInvestigation();
        var events = Assert.Single(investigation.Sources, item => item.Source == CorrelationSource.Events);
        var forged = investigation with
        {
            Sources = investigation.Sources
                .Select(item => item.Source == CorrelationSource.Events
                    ? item with { EvidenceCount = events.EvidenceCount + 1 }
                    : item)
                .ToArray()
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewStore().SaveCorrelationInvestigationAsync(forged));
        Assert.Empty((await NewStore().LoadCorrelationInvestigationsAsync()).Investigations);
    }

    [Fact]
    public async Task RecordExceedingFourMiBIsRejectedWithoutWriting()
    {
        var investigation = await NewInvestigation();
        var evidence = investigation.Evidence[0] with
        {
            Title = new string('x', 4 * 1024 * 1024 + 1024)
        };
        var oversized = investigation with
        {
            Evidence = [evidence, .. investigation.Evidence.Skip(1)]
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewStore().SaveCorrelationInvestigationAsync(oversized));
        Assert.DoesNotContain(
            Directory.GetFiles(Path.Combine(root, "memory", "correlation"), "*.md"),
            path => path.Contains(oversized.Id.ToString("D"), StringComparison.OrdinalIgnoreCase));
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
