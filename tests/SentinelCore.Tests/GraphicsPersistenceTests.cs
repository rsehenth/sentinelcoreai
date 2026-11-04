using System.Text;
using Microsoft.Data.Sqlite;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class GraphicsPersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SentinelCoreGraphicsTests",
        Guid.NewGuid().ToString("N"));

    private MemoryStore NewStore() => new(root);

    private static async Task<GraphicsInvestigation> NewInvestigation()
    {
        var runner = GraphicsSamples.Runner(events: GraphicsSamples.RealisticEvents(
            events: [GraphicsSamples.DisplayTdr()],
            reports:
            [
                GraphicsSamples.LiveKernel("wer-117", "117"),
                GraphicsSamples.LiveKernel("wer-141", "141"),
                GraphicsSamples.LiveKernel("wer-142", "142")
            ]));
        return await runner.RunAsync(new GraphicsInvestigationRequest(EventWindow.Last7Days));
    }

    [Fact]
    public async Task SaveLoadAndReopenPreservesEvidenceAndReport()
    {
        var investigation = await NewInvestigation();
        var store = NewStore();

        var bytes = await store.SaveGraphicsInvestigationAsync(investigation);
        var loaded = await NewStore().LoadGraphicsInvestigationsAsync();

        Assert.Empty(loaded.Warnings);
        var copy = Assert.Single(loaded.Investigations);
        Assert.Equal(investigation.Id, copy.Id);
        Assert.Equal(investigation.Status, copy.Status);
        Assert.Equal(investigation.Adapters.Count, copy.Adapters.Count);
        Assert.Equal(investigation.Monitors.Count, copy.Monitors.Count);
        Assert.Equal(investigation.Events.Count, copy.Events.Count);
        Assert.Equal(investigation.WerReports.Count, copy.WerReports.Count);
        Assert.Equal(investigation.Observations.Count, copy.Observations.Count);
        Assert.True(bytes > 0);
        Assert.True(File.Exists(store.GetGraphicsReportPath(investigation.Id)));
        Assert.True(File.Exists(Path.Combine(root, "memory", "GRAPHICS.md")));
        Assert.Equal(11, ReadUserVersion());
    }

    [Fact]
    public async Task EnvelopeContainsPayloadAndMatchingSqliteChecksum()
    {
        var investigation = await NewInvestigation();
        var store = NewStore();
        await store.SaveGraphicsInvestigationAsync(investigation);

        var recordPath = Path.Combine(root, "memory", "graphics", $"{investigation.Id:D}.md");
        var markdown = await File.ReadAllTextAsync(recordPath, Encoding.UTF8);
        Assert.Contains("<!-- sentinelcore:payload:v1 -->", markdown);
        var json = RecordEnvelope.Unwrap(markdown.Replace("\r\n", "\n"));
        var expectedHash = RecordEnvelope.Hash(json);

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT PayloadHash, AdapterCount, EventCount, WerReportCount FROM GraphicsInvestigations WHERE Id = $id";
        command.Parameters.AddWithValue("$id", investigation.Id.ToString("D"));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expectedHash, reader.GetString(0));
        Assert.Equal(investigation.Adapters.Count, reader.GetInt32(1));
        Assert.Equal(investigation.Events.Count, reader.GetInt32(2));
        Assert.Equal(investigation.WerReports.Count, reader.GetInt32(3));
    }

    [Fact]
    public async Task RetryIsIdempotentAndDoesNotRewriteTheDurableRecord()
    {
        var investigation = await NewInvestigation();
        var store = NewStore();
        await store.SaveGraphicsInvestigationAsync(investigation);

        var record = Directory.GetFiles(Path.Combine(root, "memory", "graphics"), "*.md").Single();
        var before = await File.ReadAllTextAsync(record, Encoding.UTF8);
        var modified = File.GetLastWriteTimeUtc(record);

        await store.SaveGraphicsInvestigationAsync(investigation);

        Assert.Equal(before, await File.ReadAllTextAsync(record, Encoding.UTF8));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(record));
        Assert.Single((await NewStore().LoadGraphicsInvestigationsAsync()).Investigations);
    }

    [Fact]
    public async Task SameIdDifferentPayloadIsRejectedAndPreservesOriginal()
    {
        var investigation = await NewInvestigation();
        var store = NewStore();
        await store.SaveGraphicsInvestigationAsync(investigation);

        var tampered = investigation with { CompletedUtc = investigation.CompletedUtc.AddSeconds(5) };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveGraphicsInvestigationAsync(tampered));

        var persisted = Assert.Single((await NewStore().LoadGraphicsInvestigationsAsync()).Investigations);
        Assert.Equal(investigation.CompletedUtc, persisted.CompletedUtc);
    }

    [Fact]
    public async Task GraphicsIdCannotCollideWithOtherHistories()
    {
        var store = NewStore();
        var audit = Samples.Audit(GraphicsSamples.StartUtc);
        await store.SaveAsync(audit);
        var investigation = await NewInvestigation();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveGraphicsInvestigationAsync(investigation with { Id = audit.Id }));

        await store.SaveGraphicsInvestigationAsync(investigation);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(Samples.Audit(GraphicsSamples.StartUtc, id: investigation.Id)));
    }

    [Fact]
    public async Task CorruptRecordIsPreservedAndExcludedWithWarning()
    {
        var valid = await NewInvestigation();
        var corrupt = await NewInvestigation();
        var store = NewStore();
        await store.SaveGraphicsInvestigationAsync(valid);
        await store.SaveGraphicsInvestigationAsync(corrupt);

        var corruptPath = Path.Combine(root, "memory", "graphics", $"{corrupt.Id:D}.md");
        await File.WriteAllTextAsync(corruptPath, "corrupted-graphics-envelope", Encoding.UTF8);

        var loaded = await NewStore().LoadGraphicsInvestigationsAsync();
        var recovered = Assert.Single(loaded.Investigations);
        Assert.Equal(valid.Id, recovered.Id);
        Assert.NotEmpty(loaded.Warnings);
        Assert.Contains(loaded.Warnings, warning => warning.Contains(corrupt.Id.ToString("D")));
        Assert.Equal("corrupted-graphics-envelope", await File.ReadAllTextAsync(corruptPath, Encoding.UTF8));
    }

    [Fact]
    public async Task IndexIsRebuiltFromMarkdownAfterDatabaseLoss()
    {
        var investigation = await NewInvestigation();
        await NewStore().SaveGraphicsInvestigationAsync(investigation);

        File.Delete(Path.Combine(root, "data", "sentinelcore.db"));

        var loaded = await NewStore().LoadGraphicsInvestigationsAsync();
        var rebuilt = Assert.Single(loaded.Investigations);
        Assert.Equal(investigation.Id, rebuilt.Id);
        Assert.Equal(investigation.Events.Count, rebuilt.Events.Count);
        Assert.Equal(11, ReadUserVersion());

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM GraphicsInvestigations; SELECT COUNT(*) FROM GraphicsSources; SELECT COUNT(*) FROM GraphicsObservations;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.True(reader.NextResult() && reader.Read());
        Assert.Equal((long)investigation.Sources.Count, reader.GetInt64(0));
        Assert.True(reader.NextResult() && reader.Read());
        Assert.Equal((long)investigation.Observations.Count, reader.GetInt64(0));
    }

    [Fact]
    public async Task MigrationFromSchemaSixAddsGraphicsTablesAndPreservesOtherRecords()
    {
        var store = NewStore();
        await store.SaveAsync(Samples.Audit(GraphicsSamples.StartUtc));
        await store.SaveInvestigationAsync(Samples.Investigation());
        await store.SaveSessionAsync(PerformanceSamples.Session());

        using (var db = Open())
        {
            using var command = db.CreateCommand();
            command.CommandText =
                "DROP TABLE GraphicsObservations; DROP TABLE GraphicsSources; DROP TABLE GraphicsInvestigations; PRAGMA user_version=6";
            command.ExecuteNonQuery();
        }

        var graphics = await NewStore().LoadGraphicsInvestigationsAsync();
        Assert.Empty(graphics.Investigations);
        Assert.Single((await NewStore().LoadAsync()).Audits);
        Assert.Single((await NewStore().LoadInvestigationsAsync()).Investigations);
        Assert.Single((await NewStore().LoadSessionsAsync()).Sessions);
        Assert.Equal(11, ReadUserVersion());
    }

    [Fact]
    public async Task FutureSchemaElevenIsRejectedWithoutCorruptingDatabase()
    {
        await NewStore().LoadGraphicsInvestigationsAsync();
        using (var db = Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA user_version=12";
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().LoadGraphicsInvestigationsAsync());
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "data"), "*.corrupt-*"));
    }

    [Fact]
    public async Task ReportSanitizesUntrustedTextAndKeepsCausalityNotAssessed()
    {
        var investigation = await NewInvestigation();
        var hostile = investigation.Adapters[0] with { Name = "<script>alert(1)</script> `hostile`\r\nlinha" };
        var session = investigation with { Adapters = [hostile, .. investigation.Adapters.Skip(1)] };

        var store = NewStore();
        await store.SaveGraphicsInvestigationAsync(session);
        var report = await File.ReadAllTextAsync(store.GetGraphicsReportPath(session.Id), Encoding.UTF8);

        Assert.DoesNotContain("<script>", report);
        Assert.Contains("&lt;script&gt;", report);
        Assert.Contains("Causalidade: não avaliada", report);
        Assert.Contains("não renova", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("defeito físico confirmado", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SavingGraphicsDoesNotRewriteExistingEventOrAuditRecords()
    {
        var store = NewStore();
        var audit = Samples.Audit(GraphicsSamples.StartUtc);
        var eventInvestigation = Samples.Investigation();
        await store.SaveAsync(audit);
        await store.SaveInvestigationAsync(eventInvestigation);

        var auditPath = Path.Combine(root, "memory", "audits", "basic", $"{audit.Id:D}.md");
        var eventPath = Path.Combine(root, "memory", "events", $"{eventInvestigation.Id:D}.md");
        var auditBefore = await File.ReadAllBytesAsync(auditPath);
        var eventBefore = await File.ReadAllBytesAsync(eventPath);

        await store.SaveGraphicsInvestigationAsync(await NewInvestigation());

        Assert.Equal(auditBefore, await File.ReadAllBytesAsync(auditPath));
        Assert.Equal(eventBefore, await File.ReadAllBytesAsync(eventPath));
        Assert.Single((await NewStore().LoadInvestigationsAsync()).Investigations);
    }

    [Fact]
    public async Task DuplicateStableIdInAnyReadingIsRejectedBeforeWriting()
    {
        var investigation = await NewInvestigation();
        var store = NewStore();
        var duplicateAdapter = investigation with
        {
            Adapters = [.. investigation.Adapters, investigation.Adapters[0]]
        };
        var duplicateMonitor = investigation with
        {
            Monitors = [.. investigation.Monitors, investigation.Monitors[0]]
        };
        var duplicateDriver = investigation with
        {
            Drivers = [.. investigation.Drivers, investigation.Drivers[0]]
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveGraphicsInvestigationAsync(duplicateAdapter));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveGraphicsInvestigationAsync(duplicateMonitor));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveGraphicsInvestigationAsync(duplicateDriver));
        Assert.Empty((await NewStore().LoadGraphicsInvestigationsAsync()).Investigations);
    }

    [Fact]
    public async Task MessageTruncationFlagSurvivesSaveAndReload()
    {
        var investigation = await GraphicsSamples.Runner(events: GraphicsSamples.RealisticEvents(events:
            [
                GraphicsSamples.DisplayTdr(message: new string('z', 200))
            ]), limits: new GraphicsLimits { MaxMessageChars = 50 })
            .RunAsync(new GraphicsInvestigationRequest(EventWindow.Last7Days));
        var evidence = Assert.Single(investigation.Events);
        Assert.True(evidence.MessageTruncated);
        Assert.Equal(50, evidence.Message!.Length);

        var store = NewStore();
        await store.SaveGraphicsInvestigationAsync(investigation);
        var reloaded = Assert.Single(Assert.Single((await NewStore().LoadGraphicsInvestigationsAsync())
            .Investigations).Events);

        Assert.True(reloaded.MessageTruncated);
        Assert.Equal(evidence.Message, reloaded.Message);
        Assert.Contains(reloaded.Limitations, limitation =>
            limitation.Contains("Mensagem truncada", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompleteCoverageWithUnavailableAdapterFlagIsRejected()
    {
        var investigation = await NewInvestigation();
        var forged = investigation with
        {
            Coverage = investigation.Coverage with
            {
                Status = GraphicsCoverageStatus.Complete,
                AdaptersUnavailable = true
            }
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewStore().SaveGraphicsInvestigationAsync(forged));
    }

    [Fact]
    public async Task InconsistentCoverageCountersAndUnavailableSourcesAreRejected()
    {
        var investigation = await NewInvestigation();
        var wrongExpected = investigation with
        {
            Coverage = investigation.Coverage with { ExpectedSources = 1 }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewStore().SaveGraphicsInvestigationAsync(wrongExpected));

        var wrongUnavailable = investigation with
        {
            Coverage = investigation.Coverage with { UnavailableSources = ["fonte-inexistente"] }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewStore().SaveGraphicsInvestigationAsync(wrongUnavailable));
    }

    [Fact]
    public async Task RecordWithoutMandatoryScopeLimitationsIsRejected()
    {
        var investigation = await NewInvestigation();
        var forged = investigation with
        {
            Limitations = investigation.Limitations
                .Where(limitation => limitation != GraphicsEvidenceClassification.ExtraScopeLimitation)
                .ToArray()
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewStore().SaveGraphicsInvestigationAsync(forged));
    }

    [Fact]
    public async Task ForgedCausalityIsRejectedAndOriginalIsPreserved()
    {
        var investigation = await NewInvestigation();
        var observation = investigation.Observations[0];
        var forged = investigation with
        {
            Observations = [observation with { Causality = (CausalityStatus)99 }]
        };

        var store = NewStore();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveGraphicsInvestigationAsync(forged));
        Assert.Empty((await store.LoadGraphicsInvestigationsAsync()).Investigations);
    }

    [Fact]
    public async Task CancelledBeforeFirstSourceIsPersistable()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var investigation = await GraphicsSamples.Runner().RunAsync(
            new GraphicsInvestigationRequest(EventWindow.Last24Hours),
            cancellationToken: cancellation.Token);
        Assert.Equal(GraphicsInvestigationStatus.Cancelled, investigation.Status);
        Assert.Empty(investigation.Sources);

        var store = NewStore();
        await store.SaveGraphicsInvestigationAsync(investigation);
        var loaded = Assert.Single((await NewStore().LoadGraphicsInvestigationsAsync()).Investigations);
        Assert.Equal(GraphicsInvestigationStatus.Cancelled, loaded.Status);
        Assert.Equal(GraphicsCoverageStatus.Unavailable, loaded.Coverage.Status);
        Assert.Empty(loaded.Sources);
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
