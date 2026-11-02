using System.Text;
using Microsoft.Data.Sqlite;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// Persistência da Fase 5 em raiz temporária isolada: Markdown imutável por checkpoint, registro final que apenas
/// referencia segmentos, checksum, idempotência, colisão de IDs, migração aditiva 8→9 e recuperação de sessões
/// interrompidas sem falsificar conclusão.
/// </summary>
public sealed class MonitoringPersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SentinelCoreMonitoringTests",
        Guid.NewGuid().ToString("N"));

    private MemoryStore NewStore() => new(root);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "data", "sentinelcore.db"),
                Pooling = false
            }.ToString());
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

    private static MonitoringSegment EventSegment(Guid sessionId, long recordId = 900, int sequence = 0) =>
        MonitoringSamples.Segment(sessionId, MonitoringSource.Events, sequence,
            events: [MonitoringSamples.EventObservation(MonitoringSamples.Tdr(recordId,
                MonitoringSamples.Now), 0, MonitoringSamples.Now)]);

    [Fact]
    public async Task SaveSegmentAndSessionRoundTripPreservesDataAndReport()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid();
        var segment = EventSegment(sessionId);
        var incident = MonitoringSamples.Incident(sessionId, MonitoringSamples.Now.AddSeconds(30));
        var session = MonitoringSamples.Session(sessionId, [segment], incidents: [incident]);

        var segmentBytes = await store.SaveMonitoringSegmentAsync(segment);
        var sessionBytes = await store.SaveMonitoringSessionAsync(session);

        Assert.True(segmentBytes > 0);
        Assert.True(sessionBytes > 0);
        Assert.True(File.Exists(store.GetMonitoringReportPath(sessionId)));
        Assert.True(File.Exists(Path.Combine(root, "memory", "MONITORING.md")));
        Assert.True(File.Exists(Path.Combine(root, "memory", "monitoring", $"{sessionId:D}.md")));
        Assert.True(File.Exists(Path.Combine(root, "memory", "monitoring", $"{sessionId:D}", "segments",
            $"{segment.Id:D}.md")));

        var loaded = await NewStore().LoadMonitoringSessionsAsync();
        Assert.Empty(loaded.Warnings);
        var copy = Assert.Single(loaded.Sessions);
        Assert.Equal(session.Id, copy.Id);
        Assert.Equal(MonitoringCoverageStatus.Partial, copy.CoverageStatus);
        Assert.Equal(segment.ObservationCount, copy.Segments.Single().ObservationCount);
        Assert.Equal(incident.Id, copy.Incidents.Single().Id);
        Assert.Equal(EventCategory.Graphics, loaded.Segments.Single().Events.Single().Category);
        Assert.Equal(9, ReadUserVersion());
    }

    [Fact]
    public async Task EnvelopeChecksumMatchesSqlitePayloadHashAndCounts()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid();
        var segment = EventSegment(sessionId, recordId: 901);
        await store.SaveMonitoringSegmentAsync(segment);
        await store.SaveMonitoringSessionAsync(MonitoringSamples.Session(sessionId, [segment]));

        var segmentPath = Path.Combine(root, "memory", "monitoring", $"{sessionId:D}", "segments",
            $"{segment.Id:D}.md");
        var markdown = await File.ReadAllTextAsync(segmentPath, Encoding.UTF8);
        Assert.Contains("<!-- sentinelcore:payload:v1 -->", markdown);
        var json = RecordEnvelope.Unwrap(markdown.Replace("\r\n", "\n"));
        var expected = RecordEnvelope.Hash(json);

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT PayloadHash, ObservationCount FROM MonitoringSegments WHERE SegmentId = $id
            """;
        command.Parameters.AddWithValue("$id", segment.Id.ToString("D"));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expected, reader.GetString(0));
        Assert.Equal(segment.ObservationCount, reader.GetInt32(1));

        using var incidents = db.CreateCommand();
        incidents.CommandText = "SELECT COUNT(*) FROM MonitoringSessions WHERE Recovered = 0";
        Assert.Equal(1L, Convert.ToInt64(incidents.ExecuteScalar()));
    }

    [Fact]
    public async Task RetryIsIdempotentAndDoesNotRewriteDurableRecords()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid();
        var segment = EventSegment(sessionId);
        var session = MonitoringSamples.Session(sessionId, [segment]);
        await store.SaveMonitoringSegmentAsync(segment);
        await store.SaveMonitoringSessionAsync(session);

        var segmentPath = Path.Combine(root, "memory", "monitoring", $"{sessionId:D}", "segments",
            $"{segment.Id:D}.md");
        var sessionPath = Path.Combine(root, "memory", "monitoring", $"{sessionId:D}.md");
        var segmentBefore = await File.ReadAllTextAsync(segmentPath, Encoding.UTF8);
        var sessionBefore = await File.ReadAllTextAsync(sessionPath, Encoding.UTF8);
        var segmentTime = File.GetLastWriteTimeUtc(segmentPath);
        var sessionTime = File.GetLastWriteTimeUtc(sessionPath);

        await store.SaveMonitoringSegmentAsync(segment);
        await store.SaveMonitoringSessionAsync(session);

        Assert.Equal(segmentBefore, await File.ReadAllTextAsync(segmentPath, Encoding.UTF8));
        Assert.Equal(sessionBefore, await File.ReadAllTextAsync(sessionPath, Encoding.UTF8));
        Assert.Equal(segmentTime, File.GetLastWriteTimeUtc(segmentPath));
        Assert.Equal(sessionTime, File.GetLastWriteTimeUtc(sessionPath));
    }

    [Fact]
    public async Task SameIdWithDifferentPayloadIsRejectedAndOriginalIsPreserved()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid();
        var segment = EventSegment(sessionId);
        await store.SaveMonitoringSegmentAsync(segment);
        var session = MonitoringSamples.Session(sessionId, [segment]);
        await store.SaveMonitoringSessionAsync(session);

        var tampered = session with { CompletedUtc = session.CompletedUtc.AddSeconds(5) };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveMonitoringSessionAsync(tampered));

        var loaded = await NewStore().LoadMonitoringSessionsAsync();
        Assert.Equal(session.CompletedUtc, Assert.Single(loaded.Sessions).CompletedUtc);
    }

    [Fact]
    public async Task CorruptSegmentIsPreservedAndSessionIsExcludedWithWarning()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid();
        var segment = EventSegment(sessionId);
        await store.SaveMonitoringSegmentAsync(segment);
        await store.SaveMonitoringSessionAsync(MonitoringSamples.Session(sessionId, [segment]));

        var segmentPath = Path.Combine(root, "memory", "monitoring", $"{sessionId:D}", "segments",
            $"{segment.Id:D}.md");
        var original = await File.ReadAllTextAsync(segmentPath, Encoding.UTF8);
        var corrupted = original.Replace("<!-- sha256:", "<!-- sha256:0");
        await File.WriteAllTextAsync(segmentPath, corrupted, Encoding.UTF8);

        var loaded = await NewStore().LoadMonitoringSessionsAsync();

        Assert.NotEmpty(loaded.Warnings);
        Assert.Empty(loaded.Sessions);
        Assert.True(File.Exists(segmentPath));
        Assert.Contains(corrupted, await File.ReadAllTextAsync(segmentPath, Encoding.UTF8));
    }

    [Fact]
    public async Task SessionWithoutFinalRecordIsRecoveredAsInterrupted()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid();
        await store.SaveMonitoringSegmentAsync(EventSegment(sessionId, recordId: 910));
        await store.SaveMonitoringSegmentAsync(MonitoringSamples.Segment(sessionId,
            MonitoringSource.Performance, sequence: 0,
            performance: [MonitoringSamples.PerformanceObservation(0, MonitoringSamples.Now)]));

        var loaded = await NewStore().LoadMonitoringSessionsAsync();

        var session = Assert.Single(loaded.Sessions);
        Assert.Equal(sessionId, session.Id);
        Assert.Equal(MonitoringExecutionStatus.Interrupted, session.Status);
        Assert.Equal(MonitoringCoverageStatus.Partial, session.CoverageStatus);
        Assert.True(session.IsRecovered);
        Assert.True(session.Recovery.Recovered);
        Assert.Equal(2, session.Segments.Count);
        Assert.Contains(MonitoringGuidance.RecoveryLimitation, session.Limitations);
        Assert.NotEqual(MonitoringExecutionStatus.Completed, session.Status);
        var recovery = Assert.Single(loaded.Recoveries);
        Assert.Equal(sessionId, recovery.SessionId);
        Assert.Equal(2, recovery.SegmentCount);
        Assert.False(File.Exists(Path.Combine(root, "memory", "monitoring", $"{sessionId:D}.md")));
        var projection = await File.ReadAllTextAsync(
            Path.Combine(root, "memory", "MONITORING.md"), Encoding.UTF8);
        Assert.Contains("interrompida, derivada de checkpoints", projection);
        Assert.DoesNotContain($"](monitoring/{sessionId:D}.md)", projection);
    }

    [Fact]
    public async Task MonitoringIdCannotCollideWithAnotherHistory()
    {
        var store = NewStore();
        var audit = Samples.Audit(MonitoringSamples.Now);
        await store.SaveAsync(audit);
        var segment = EventSegment(audit.Id);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveMonitoringSegmentAsync(segment));
        var session = MonitoringSamples.Session(audit.Id, [segment]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveMonitoringSessionAsync(session));
        Assert.Single((await NewStore().LoadAsync()).Audits);
    }

    [Fact]
    public async Task MigrationFromSchemaEightAddsMonitoringTablesAndPreservesOtherRecords()
    {
        var store = NewStore();
        await store.SaveAsync(Samples.Audit(MonitoringSamples.Now));
        var sessionId = Guid.NewGuid();
        var segment = EventSegment(sessionId);
        await store.SaveMonitoringSegmentAsync(segment);
        await store.SaveMonitoringSessionAsync(MonitoringSamples.Session(sessionId, [segment]));

        using (var db = Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = """
                DROP TABLE MonitoringIncidents;
                DROP TABLE MonitoringSegments;
                DROP TABLE MonitoringRecoveries;
                DROP TABLE MonitoringSessions;
                PRAGMA user_version=8;
                """;
            command.ExecuteNonQuery();
        }

        var loaded = await NewStore().LoadMonitoringSessionsAsync();
        Assert.Single(loaded.Sessions);
        Assert.Single((await NewStore().LoadAsync()).Audits);
        Assert.Equal(9, ReadUserVersion());
    }

    [Fact]
    public async Task FutureSchemaTenIsRejectedWithoutCorruptingDatabase()
    {
        await NewStore().LoadMonitoringSessionsAsync();
        using (var db = Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA user_version=10";
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().LoadMonitoringSessionsAsync());
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "data"), "*.corrupt-*"));
    }

    [Fact]
    public async Task ReportSanitizesUserDescriptionAndKeepsIncidentOrigins()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid();
        var segment = EventSegment(sessionId);
        var incident = MonitoringSamples.Incident(sessionId) with
        {
            Description = "<script>alert(1)</script> `hostile`"
        };
        var session = MonitoringSamples.Session(sessionId, [segment], incidents: [incident]);
        await store.SaveMonitoringSegmentAsync(segment);
        await store.SaveMonitoringSessionAsync(session);

        var report = await File.ReadAllTextAsync(store.GetMonitoringReportPath(sessionId), Encoding.UTF8);

        Assert.DoesNotContain("<script>", report);
        Assert.Contains("&lt;script&gt;", report);
        Assert.Contains("evento comprovado do Windows", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Causalidade: não avaliada", report);
    }

    [Fact]
    public async Task SessionsAreOrderedByCompletionAndCorruptOriginalStaysOnDisk()
    {
        var store = NewStore();
        var oldId = Guid.NewGuid();
        var oldSegment = EventSegment(oldId, recordId: 920);
        await store.SaveMonitoringSegmentAsync(oldSegment);
        var oldSession = MonitoringSamples.Session(oldId, [oldSegment]) with
        {
            StartedUtc = MonitoringSamples.Now.AddDays(-2),
            CompletedUtc = MonitoringSamples.Now.AddDays(-2).AddMinutes(5)
        };
        await store.SaveMonitoringSessionAsync(oldSession);

        var newId = Guid.NewGuid();
        var newSegment = EventSegment(newId, recordId: 921);
        await store.SaveMonitoringSegmentAsync(newSegment);
        await store.SaveMonitoringSessionAsync(MonitoringSamples.Session(newId, [newSegment]));

        var loaded = await NewStore().LoadMonitoringSessionsAsync();

        Assert.Equal([newId, oldId], loaded.Sessions.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task RunnerCheckpointsWithEventsAreAcceptedByTheRealStore()
    {
        var store = NewStore();
        var limits = new MonitoringLimits
        {
            PerformanceInterval = TimeSpan.FromMilliseconds(100),
            EventInterval = TimeSpan.FromMilliseconds(100),
            NetworkInterval = TimeSpan.FromMilliseconds(150),
            SegmentDuration = TimeSpan.FromMilliseconds(250),
            MaxCollectorTick = TimeSpan.FromSeconds(2)
        };
        var events = new FakeMonitoringEventSource(index =>
            [MonitoringSamples.Tdr(2000 + index, MonitoringSamples.Now)]);
        var runner = new MonitoringSessionRunner(
        [
            new EventMonitoringSource(events),
            new PerformanceMonitoringSource(new FakeMonitoringPerformanceCollector())
        ], store, TimeProvider.System, limits);

        var session = await runner.RunAsync(new MonitoringRequest(MonitoringDurationPreset.Custom,
            TimeSpan.FromMilliseconds(700),
            MonitoringSources.Events | MonitoringSources.Graphics | MonitoringSources.Performance,
            Authorized: true));
        await store.SaveMonitoringSessionAsync(session);

        var loaded = await NewStore().LoadMonitoringSessionsAsync();

        Assert.Empty(loaded.Warnings);
        var copy = Assert.Single(loaded.Sessions);
        Assert.Equal(session.Segments.Count, copy.Segments.Count);
        Assert.NotEmpty(loaded.Segments.SelectMany(segment => segment.Events));
        Assert.All(loaded.Segments.SelectMany(segment => segment.Events), observation =>
            Assert.True(observation.RecordId is > 0));
        Assert.NotEqual(0, copy.Budget.PersistedBytes);
    }

    [Fact]
    public async Task RecoveredSessionCannotBeDeclaredCompleted()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid();
        var segment = EventSegment(sessionId);
        await store.SaveMonitoringSegmentAsync(segment);
        var recovered = MonitoringSamples.Session(sessionId, [segment]) with
        {
            Status = MonitoringExecutionStatus.Completed,
            Recovery = new MonitoringRecoveryInfo(true, MonitoringSamples.Now, 1,
                "Sessão recuperada de checkpoints.")
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveMonitoringSessionAsync(recovered));
    }

    [Fact]
    public async Task DurationIncompatibleWithPresetIsRejected()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid();
        var segment = EventSegment(sessionId);
        await store.SaveMonitoringSegmentAsync(segment);
        var invalid = MonitoringSamples.Session(sessionId, [segment]) with
        {
            AuthorizedDuration = TimeSpan.FromMinutes(10)
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveMonitoringSessionAsync(invalid));
    }

    [Fact]
    public async Task OrphanSegmentFolderBlocksAnotherHistoryWithTheSameId()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        await store.SaveMonitoringSegmentAsync(EventSegment(id));

        var audit = Samples.Audit(MonitoringSamples.Now, id: id);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(audit));
        Assert.False(File.Exists(Path.Combine(root, "memory", "monitoring", $"{id:D}.md")));
    }

    [Fact]
    public async Task MissingReportIsRegeneratedOnLoad()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid();
        var segment = EventSegment(sessionId);
        await store.SaveMonitoringSegmentAsync(segment);
        await store.SaveMonitoringSessionAsync(MonitoringSamples.Session(sessionId, [segment]));
        File.Delete(store.GetMonitoringReportPath(sessionId));

        var loaded = await NewStore().LoadMonitoringSessionsAsync();

        Assert.Single(loaded.Sessions);
        Assert.True(File.Exists(store.GetMonitoringReportPath(sessionId)));
    }

    [Fact]
    public async Task CorruptFinalRecordIsNotPresentedAsAnInterruptedSession()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid();
        var segment = EventSegment(sessionId);
        await store.SaveMonitoringSegmentAsync(segment);
        await store.SaveMonitoringSessionAsync(MonitoringSamples.Session(sessionId, [segment]));

        var sessionPath = Path.Combine(root, "memory", "monitoring", $"{sessionId:D}.md");
        var text = await File.ReadAllTextAsync(sessionPath, Encoding.UTF8);
        await File.WriteAllTextAsync(sessionPath, text.Replace("<!-- sha256:", "<!-- sha256:0"),
            Encoding.UTF8);

        var loaded = await NewStore().LoadMonitoringSessionsAsync();

        Assert.DoesNotContain(loaded.Sessions, session => session.Id == sessionId);
        Assert.Empty(loaded.Recoveries);
        Assert.Contains(loaded.Warnings,
            warning => warning.Contains("ilegível", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(sessionPath));
    }

    [Fact]
    public async Task DurationBeyondAuthorizedToleranceIsRejected()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid();
        var segment = EventSegment(sessionId);
        await store.SaveMonitoringSegmentAsync(segment);
        var invalid = MonitoringSamples.Session(sessionId, [segment]) with
        {
            Duration = TimeSpan.FromMinutes(6)
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveMonitoringSessionAsync(invalid));
    }

    [Fact]
    public async Task CompletedSourceWithFewerCyclesThanExpectedIsRejected()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid();
        var segment = EventSegment(sessionId);
        await store.SaveMonitoringSegmentAsync(segment);
        var baseline = MonitoringSamples.Session(sessionId, [segment]);
        var invalid = baseline with
        {
            Sources = baseline.Sources.Select(source => source with
            {
                Status = MonitoringSourceStatus.Completed,
                ExpectedTicks = 10,
                CollectedTicks = 0
            }).ToArray()
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveMonitoringSessionAsync(invalid));
    }

    [Fact]
    public async Task ReferenceDivergingFromCheckpointIsRejectedBeforeWriting()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid();
        var segment = EventSegment(sessionId);
        await store.SaveMonitoringSegmentAsync(segment);
        var baseline = MonitoringSamples.Session(sessionId, [segment]);
        var invalid = baseline with
        {
            Segments = [segment.Reference with { FromUtc = segment.FromUtc.AddSeconds(-5) }]
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveMonitoringSessionAsync(invalid));
        Assert.False(File.Exists(Path.Combine(root, "memory", "monitoring", $"{sessionId:D}.md")));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
