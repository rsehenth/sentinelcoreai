using Microsoft.Data.Sqlite;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class PerformancePersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SentinelCoreTests",
        Guid.NewGuid().ToString("N"));
    private MemoryStore NewStore() => new(root);

    [Fact]
    public async Task SaveLoadAndReopenPreservesSamplesStatisticsAndObservations()
    {
        var session = PerformanceSamples.Session();
        var bytes = await NewStore().SaveSessionAsync(session);
        var loaded = await NewStore().LoadSessionsAsync();
        var copy = Assert.Single(loaded.Sessions);
        Assert.Equal(session.Id, copy.Id);
        Assert.Equal(session.Samples.Count, copy.Samples.Count);
        Assert.Equal(session.Statistics.CpuTotalPercent.Mean, copy.Statistics.CpuTotalPercent.Mean);
        Assert.Equal(session.Observations.Count, copy.Observations.Count);
        Assert.True(bytes > 0);
        Assert.True(new FileInfo(NewStore().GetPerformanceReportPath(session.Id)).Length > 0);
        Assert.Null(copy.Coverage);
        Assert.Equal(PerformanceExecutionStatus.Completed,
            PerformanceCoverage.FromLegacy(copy).Execution);
        Assert.Equal(11, ReadUserVersion());
    }

    [Fact]
    public async Task RetryIsIdempotentAndDoesNotRewriteHistory()
    {
        var session = PerformanceSamples.Session();
        var store = NewStore();
        await store.SaveSessionAsync(session);
        var record = Directory.GetFiles(Path.Combine(root, "memory", "performance"), "*.md").Single();
        var before = File.ReadAllText(record);
        var modified = File.GetLastWriteTimeUtc(record);
        await store.SaveSessionAsync(session);
        Assert.Equal(before, File.ReadAllText(record));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(record));
    }

    [Fact]
    public async Task SameIdDifferentPayloadIsRejected()
    {
        var session = PerformanceSamples.Session();
        var store = NewStore();
        await store.SaveSessionAsync(session);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveSessionAsync(session with { CompletedUtc = session.CompletedUtc.AddSeconds(1) }));
    }

    [Fact]
    public async Task SessionIdCannotCollideWithOtherHistories()
    {
        var store = NewStore();
        var audit = Samples.Audit(DateTimeOffset.UtcNow);
        await store.SaveAsync(audit);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveSessionAsync(PerformanceSamples.Session() with { Id = audit.Id }));
    }

    [Fact]
    public async Task AuditCannotCollideWithPerformanceSession()
    {
        var store = NewStore();
        var session = PerformanceSamples.Session();
        await store.SaveSessionAsync(session);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(Samples.Audit(DateTimeOffset.UtcNow, id: session.Id)));
    }

    [Fact]
    public async Task CorruptSessionIsPreservedAndExcluded()
    {
        await NewStore().SaveSessionAsync(PerformanceSamples.Session());
        var path = Directory.GetFiles(Path.Combine(root, "memory", "performance"), "*.md").Single();
        File.WriteAllText(path, "corrupted");
        var loaded = await NewStore().LoadSessionsAsync();
        Assert.Empty(loaded.Sessions);
        Assert.NotEmpty(loaded.Warnings);
        Assert.Equal("corrupted", File.ReadAllText(path));
    }

    [Fact]
    public async Task IndexIsRebuiltAfterDatabaseLoss()
    {
        var session = PerformanceSamples.Session();
        await NewStore().SaveSessionAsync(session);
        File.Delete(Path.Combine(root, "data", "sentinelcore.db"));
        var loaded = await NewStore().LoadSessionsAsync();
        Assert.Equal(session.Id, loaded.Sessions.Single().Id);
        Assert.True(File.Exists(Path.Combine(root, "memory", "PERFORMANCE.md")));
    }

    [Fact]
    public async Task PerformanceDoesNotRenewAuditLastSuccess()
    {
        var store = NewStore();
        // Âncora no relógio injetado: DateTimeOffset.UtcNow tornava o resultado dependente da hora do dia.
        var clock = new FixedTime();
        var old = Samples.Audit(clock.Now.AddDays(-8));
        await store.SaveAsync(old);
        await store.SaveSessionAsync(PerformanceSamples.Session());
        var freshness = new FreshnessEngine(clock).Evaluate((await store.LoadAsync()).Audits,
            AuditKind.Basic);
        Assert.Equal(FreshnessState.Outdated, freshness.State);
    }

    [Fact]
    public async Task MigrationFromSchemaThreePreservesEverything()
    {
        var store = NewStore();
        await store.SaveAsync(Samples.Audit(DateTimeOffset.UtcNow));
        await store.SaveInvestigationAsync(Samples.Investigation());
        using (var db = Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = "DROP TABLE PerformanceObservations; DROP TABLE PerformanceSources; " +
                "DROP TABLE PerformanceSessions; PRAGMA user_version=3";
            command.ExecuteNonQuery();
        }
        var sessions = await NewStore().LoadSessionsAsync();
        Assert.Empty(sessions.Sessions);
        Assert.Single((await NewStore().LoadAsync()).Audits);
        Assert.Single((await NewStore().LoadInvestigationsAsync()).Investigations);
        Assert.Equal(11, ReadUserVersion());
    }

    [Fact]
    public async Task FutureSchemaElevenIsRejected()
    {
        await NewStore().LoadSessionsAsync();
        using (var db = Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA user_version=12";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().LoadSessionsAsync());
    }

    [Fact]
    public async Task CompletedSessionMayCarryExpectedRestrictions()
    {
        var session = PerformanceSamples.Session() with
        {
            Status = PerformanceSessionStatus.Completed,
            Limitations = ["12 processos protegidos não acessíveis; CPU, memória e armazenamento permanecem válidos."]
        };
        await NewStore().SaveSessionAsync(session);
        var copy = Assert.Single((await NewStore().LoadSessionsAsync()).Sessions);
        Assert.Equal(PerformanceSessionStatus.Completed, copy.Status);
        Assert.NotEmpty(copy.Limitations);
    }

    [Fact]
    public async Task VersionTwoSessionRoundTripsCoverageWithoutChangingExecutionStatus()
    {
        var coverage = new PerformanceCoverage(PerformanceExecutionStatus.Completed,
            PerformanceCoverageStatus.Partial, 31, 31, 0, ProcessLimitReached: true,
            StorageLimitReached: false, SampleLimitReached: false, [],
            ["Limite de 20 processos acompanhados atingido."]);
        var session = PerformanceSamples.Session() with { FormatVersion = 2, Coverage = coverage };
        await NewStore().SaveSessionAsync(session);
        var copy = Assert.Single((await NewStore().LoadSessionsAsync()).Sessions);
        Assert.Equal(PerformanceSessionStatus.Completed, copy.Status);
        Assert.NotNull(copy.Coverage);
        Assert.Equal(coverage.Execution, copy.Coverage!.Execution);
        Assert.Equal(coverage.Status, copy.Coverage.Status);
        Assert.Equal(coverage.ExpectedSamples, copy.Coverage.ExpectedSamples);
        Assert.Equal(coverage.CollectedSamples, copy.Coverage.CollectedSamples);
        Assert.True(copy.Coverage.ProcessLimitReached);
        Assert.Equal(coverage.Limitations, copy.Coverage.Limitations);
        var text = File.ReadAllText(NewStore().GetPerformanceReportPath(session.Id));
        Assert.Contains("Execução: Concluída", text);
        Assert.Contains("Cobertura: Parcial", text);
        Assert.Contains("processos sim", text);
    }

    [Fact]
    public async Task IndexIsRebuiltWithCoverageAfterDatabaseLoss()
    {
        var coverage = new PerformanceCoverage(PerformanceExecutionStatus.Cancelled,
            PerformanceCoverageStatus.Partial, 31, 8, 23, false, false, false, [],
            ["Sessão cancelada pelo usuário após 8 amostra(s)."]);
        var session = PerformanceSamples.Session(samples: 8) with
        {
            FormatVersion = 2,
            Status = PerformanceSessionStatus.Cancelled,
            Coverage = coverage
        };
        await NewStore().SaveSessionAsync(session);
        File.Delete(Path.Combine(root, "data", "sentinelcore.db"));
        var copy = Assert.Single((await NewStore().LoadSessionsAsync()).Sessions);
        Assert.Equal(coverage.Execution, copy.Coverage!.Execution);
        Assert.Equal(coverage.CollectedSamples, copy.Coverage.CollectedSamples);
        Assert.Equal(11, ReadUserVersion());
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT ExecutionStatus, CoverageStatus FROM PerformanceSessions";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Cancelled", reader.GetString(0));
        Assert.Equal("Partial", reader.GetString(1));
    }

    [Fact]
    public async Task InconsistentCoverageIsRejected()
    {
        var coverage = new PerformanceCoverage(PerformanceExecutionStatus.Completed,
            PerformanceCoverageStatus.Complete, 31, 7, 0, false, false, false, [], []);
        var session = PerformanceSamples.Session() with { FormatVersion = 2, Coverage = coverage };
        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().SaveSessionAsync(session));
    }

    [Fact]
    public async Task KnownDefectIsAnnotatedWithoutRewritingTheImmutableRecord()
    {
        var affected = Guid.Parse("d413216f-e257-42c3-b08f-970a816e3efe");
        var session = PerformanceSamples.Session() with { Id = affected };
        var store = NewStore();
        await store.SaveSessionAsync(session);
        var path = Path.Combine(root, "memory", "performance", $"{affected:D}.md");
        var before = File.ReadAllText(path);
        var hash = RecordEnvelope.Unwrap(before);
        var loaded = Assert.Single((await store.LoadSessionsAsync()).Sessions);
        Assert.Null(loaded.Coverage);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(hash, RecordEnvelope.Unwrap(File.ReadAllText(path)));
        var annotation = Assert.IsType<PerformanceQualityAnnotation>(
            PerformanceQualityCatalog.Find(loaded.Id));
        Assert.True(annotation.ComparisonBlocked);
        var text = File.ReadAllText(store.GetPerformanceReportPath(loaded.Id));
        Assert.Contains("Qualidade histórica", text);
        Assert.Contains(annotation.DefectId, text);
        Assert.Contains("BLOQUEADA", text);
    }

    [Fact]
    public void QualityCatalogAnnotatesOnlyTheSevenKnownSessions()
    {
        Assert.NotNull(PerformanceQualityCatalog.Find(
            Guid.Parse("414b5cc7-3b10-46fe-9626-f161f45afa9b")));
        Assert.NotNull(PerformanceQualityCatalog.Find(
            Guid.Parse("63257da5-d49b-47f3-b0f9-da354135b1c9")));
        Assert.Null(PerformanceQualityCatalog.Find(
            Guid.Parse("42442476-a2d2-400f-ae2f-082f30c6152b")));
        Assert.Null(PerformanceQualityCatalog.Find(
            Guid.Parse("a31c625f-3514-435d-a838-329c8787ac49")));
        Assert.Null(PerformanceQualityCatalog.Find(Guid.NewGuid()));
    }

    [Fact]
    public async Task MissingReportIsRecreated()
    {
        var session = PerformanceSamples.Session();
        var store = NewStore();
        await store.SaveSessionAsync(session);
        File.Delete(store.GetPerformanceReportPath(session.Id));
        await NewStore().LoadSessionsAsync();
        Assert.True(File.Exists(store.GetPerformanceReportPath(session.Id)));
    }

    [Fact]
    public async Task MaximumSessionFitsBelowFourMebibytes()
    {
        var session = PerformanceSamples.MaximalSession();
        var bytes = await NewStore().SaveSessionAsync(session);
        Assert.InRange(bytes, 1, 4 * 1024 * 1024);
    }

    [Fact]
    public async Task ReportSanitizesUntrustedTextAndKeepsCausalityUnassessed()
    {
        var session = PerformanceSamples.Session() with
        {
            Observations = [PerformanceSamples.Session().Observations.First() with
            {
                SubjectName = "<script>alert(1)</script> `x`\r\nlinha"
            }]
        };
        var store = NewStore();
        await store.SaveSessionAsync(session);
        var text = File.ReadAllText(store.GetPerformanceReportPath(session.Id));
        Assert.DoesNotContain("<script>", text);
        Assert.Contains("&lt;script&gt;", text);
        Assert.Contains("Causalidade: não avaliada", text);
        Assert.Contains("não renova", text, StringComparison.OrdinalIgnoreCase);
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
