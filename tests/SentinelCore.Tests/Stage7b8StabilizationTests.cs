using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using SentinelCore.RepairBroker;
using Xunit;

namespace SentinelCore.Tests;

public sealed class Stage7b8StabilizationTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "SentinelCore7b8Tests",
        Guid.NewGuid().ToString("N"));

    private MemoryStore NewStore() => new(root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task LoadRepairsAsyncReconstructsMissingAndStaleDerivedReports()
    {
        using var scope = new RepairScope();
        var result = await scope.ExecuteAsync(RepairCatalog.SfcVerifyId);
        var record = Assert.IsType<RepairRecord>(result.Record);
        var reportPath = Path.Combine(scope.Root, "reports", "repairs", $"{record.Id:D}.md");
        Assert.True(File.Exists(reportPath));
        var expected = File.ReadAllText(reportPath, Encoding.UTF8);

        File.Delete(reportPath);
        Assert.False(File.Exists(reportPath));

        var afterMissing = await scope.Store.LoadRepairsAsync();
        Assert.Empty(afterMissing.Warnings);
        var loadedMissing = Assert.Single(afterMissing.Records);
        Assert.Equal(record.Id, loadedMissing.Id);
        Assert.True(File.Exists(reportPath));
        Assert.Equal(expected, File.ReadAllText(reportPath, Encoding.UTF8));

        File.WriteAllText(reportPath, "corrupted derived report", Encoding.UTF8);
        var afterStale = await scope.Store.LoadRepairsAsync();
        Assert.Empty(afterStale.Warnings);
        Assert.Single(afterStale.Records);
        Assert.Equal(expected, File.ReadAllText(reportPath, Encoding.UTF8));
    }

    [Fact]
    public async Task LoadRepairsAsyncKeepsValidRecordsAndWarnsWhenReportOrProjectionFailsOrIndexDrifts()
    {
        using var scope = new RepairScope();
        var firstResult = await scope.ExecuteAsync(RepairCatalog.SfcVerifyId);
        var secondResult = await scope.ExecuteAsync(RepairCatalog.SfcVerifyId);
        var first = Assert.IsType<RepairRecord>(firstResult.Record);
        var second = Assert.IsType<RepairRecord>(secondResult.Record);

        var blockedReportPath = Path.Combine(scope.Root, "reports", "repairs", $"{first.Id:D}.md");
        File.Delete(blockedReportPath);
        Directory.CreateDirectory(blockedReportPath);

        var projectionPath = Path.Combine(scope.Root, "memory", "REPAIRS.md");
        File.Delete(projectionPath);
        Directory.CreateDirectory(projectionPath);

        var secondDir = Path.Combine(scope.Root, "memory", "repairs", $"{second.Id:D}");
        Directory.Delete(secondDir, recursive: true);

        var loaded = await scope.Store.LoadRepairsAsync();

        var remaining = Assert.Single(loaded.Records);
        Assert.Equal(first.Id, remaining.Id);
        Assert.Contains(loaded.Warnings, warning =>
            warning.Contains($"{first.Id:D}", StringComparison.Ordinal) &&
            warning.Contains("relatório derivado não pôde ser regenerado", StringComparison.Ordinal) &&
            warning.Contains("Isso não invalida", StringComparison.Ordinal));
        Assert.Contains(loaded.Warnings, warning =>
            warning.Contains("Projeção memory/REPAIRS.md", StringComparison.Ordinal) &&
            warning.Contains("Registros autoritativos preservados", StringComparison.Ordinal));
        Assert.Contains(loaded.Warnings, warning =>
            warning.Contains($"{second.Id:D}", StringComparison.Ordinal) &&
            warning.Contains("ausente nos arquivos autoritativos", StringComparison.Ordinal));

        using var db = new SqliteConnection($"Data Source={Path.Combine(scope.Root, "data", "sentinelcore.db")}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Repairs;";
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public async Task ReadRecoveryHistoryAsyncRemainsReadOnlyWhenRepairDerivedReportIsMissing()
    {
        using var scope = new RepairScope();
        var result = await scope.ExecuteAsync(RepairCatalog.SfcVerifyId);
        var record = Assert.IsType<RepairRecord>(result.Record);
        var reportPath = Path.Combine(scope.Root, "reports", "repairs", $"{record.Id:D}.md");
        File.Delete(reportPath);

        var sources = await scope.Store.ReadRecoveryHistoryAsync();
        var repairsSource = Assert.Single(sources, source => source.Module == RecoveryModule.WindowsRepair);
        var loaded = Assert.Single(repairsSource.Repairs);
        Assert.Equal(record.Id, loaded.Id);
        Assert.False(File.Exists(reportPath));
    }

    [Fact]
    public async Task LoadAuditsPreservesValidAndUnrenderizableRecordsWhileExcludingCorrupt()
    {
        var store = NewStore();
        var valid = Samples.Audit(DateTimeOffset.UtcNow.AddMinutes(-5));
        await store.SaveAsync(valid);

        var baseAudit = Samples.Audit(DateTimeOffset.UtcNow);
        var badResult = baseAudit.Results[0] with
        {
            Data = baseAudit.Results[0].Data! with { Processors = [null!] }
        };
        var unrenderizable = baseAudit with { Results = [badResult, .. baseAudit.Results.Skip(1)] };
        WriteEnvelope(
            Path.Combine(root, "memory", "audits", "basic", $"{unrenderizable.Id:D}.md"),
            "# Relatório sintético",
            unrenderizable);

        var corruptId = Guid.NewGuid();
        var corruptPath = Path.Combine(root, "memory", "audits", "basic", $"{corruptId:D}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
        File.WriteAllText(corruptPath, "corrupted envelope", Encoding.UTF8);

        var validReport = store.GetReportPath(valid.Id);
        File.Delete(validReport);

        var loaded = await NewStore().LoadAsync();

        Assert.Equal(2, loaded.Audits.Count);
        Assert.Contains(loaded.Audits, item => item.Id == valid.Id);
        Assert.Contains(loaded.Audits, item => item.Id == unrenderizable.Id);
        Assert.DoesNotContain(loaded.Audits, item => item.Id == corruptId);
        Assert.True(File.Exists(validReport));

        Assert.Contains(loaded.Warnings, warning =>
            warning.Contains($"{unrenderizable.Id:D}", StringComparison.Ordinal) &&
            warning.Contains("não pôde ser renderizado", StringComparison.Ordinal));
        Assert.Contains(loaded.Warnings, warning =>
            warning.Contains($"{corruptId:D}.md", StringComparison.Ordinal) &&
            warning.Contains("não recuperado", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadPerformanceNetworksAndHardwareMixedBatchesHandleRenderFailuresWithoutStoppingLoad()
    {
        var store = NewStore();

        var perfValid = PerformanceSamples.Session();
        await store.SaveSessionAsync(perfValid);
        var perfSeed = PerformanceSamples.Session();
        var perfBadRender = perfSeed with
        {
            Id = Guid.NewGuid(),
            Statistics = perfSeed.Statistics with { Processes = [null!] }
        };
        WriteEnvelope(
            Path.Combine(root, "memory", "performance", $"{perfBadRender.Id:D}.md"),
            "performance",
            perfBadRender);
        var perfCorruptId = Guid.NewGuid();
        File.WriteAllText(
            Path.Combine(root, "memory", "performance", $"{perfCorruptId:D}.md"),
            "corrupted",
            Encoding.UTF8);

        var perfLoaded = await store.LoadSessionsAsync();
        Assert.Equal(2, perfLoaded.Sessions.Count);
        Assert.Contains(perfLoaded.Warnings, w =>
            w.Contains($"{perfBadRender.Id:D}", StringComparison.Ordinal) && w.Contains("não pôde ser renderizado", StringComparison.Ordinal));
        Assert.Contains(perfLoaded.Warnings, w =>
            w.Contains($"{perfCorruptId:D}.md", StringComparison.Ordinal) && w.Contains("não recuperad", StringComparison.Ordinal));

        var netValid = NetworkPersistenceTests.CreateValidSession(4, includeObservation: true);
        await store.SaveNetworkSessionAsync(netValid);
        var netSeed = NetworkPersistenceTests.CreateValidSession(4, includeObservation: true);
        var netBadRender = netSeed with { Id = Guid.NewGuid(), PortRanges = null! };
        WriteEnvelope(
            Path.Combine(root, "memory", "network", $"{netBadRender.Id:D}.md"),
            "network",
            netBadRender);
        var netCorruptId = Guid.NewGuid();
        File.WriteAllText(
            Path.Combine(root, "memory", "network", $"{netCorruptId:D}.md"),
            "corrupted",
            Encoding.UTF8);

        var netLoaded = await store.LoadNetworkSessionsAsync();
        Assert.Equal(2, netLoaded.Sessions.Count);
        Assert.Contains(netLoaded.Warnings, w =>
            w.Contains($"{netBadRender.Id:D}", StringComparison.Ordinal) && w.Contains("não pôde ser renderizado", StringComparison.Ordinal));
        Assert.Contains(netLoaded.Warnings, w =>
            w.Contains($"{netCorruptId:D}.md", StringComparison.Ordinal) && w.Contains("não recuperad", StringComparison.Ordinal));

        var hwValid = HardwareSnapshotSample(withNullProperty: false);
        await store.SaveSnapshotAsync(hwValid);
        var hwBadRender = HardwareSnapshotSample(withNullProperty: true);
        WriteEnvelope(
            Path.Combine(root, "memory", "inventory", $"{hwBadRender.Id:D}.md"),
            "# Hardware sintético",
            hwBadRender);
        var hwCorruptId = Guid.NewGuid();
        File.WriteAllText(
            Path.Combine(root, "memory", "inventory", $"{hwCorruptId:D}.md"),
            "corrupted",
            Encoding.UTF8);

        var hwLoaded = await store.LoadSnapshotsAsync();
        Assert.Equal(2, hwLoaded.Snapshots.Count);
        Assert.Contains(hwLoaded.Warnings, w =>
            w.Contains($"{hwBadRender.Id:D}", StringComparison.Ordinal) && w.Contains("não pôde ser renderizado", StringComparison.Ordinal));
        Assert.Contains(hwLoaded.Warnings, w =>
            w.Contains($"{hwCorruptId:D}.md", StringComparison.Ordinal) && w.Contains("não recuperad", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadGraphicsMixedBatchHandlesRenderFailuresWithoutStoppingLoad()
    {
        var store = NewStore();
        var valid = await GraphicsInvestigationSample();
        await store.SaveGraphicsInvestigationAsync(valid);

        var blocked = await GraphicsInvestigationSample() with { Id = Guid.NewGuid() };
        await store.SaveGraphicsInvestigationAsync(blocked);
        var blockedReportPath = store.GetGraphicsReportPath(blocked.Id);
        File.Delete(blockedReportPath);
        Directory.CreateDirectory(blockedReportPath);

        var corruptId = Guid.NewGuid();
        File.WriteAllText(
            Path.Combine(root, "memory", "graphics", $"{corruptId:D}.md"),
            "corrupted",
            Encoding.UTF8);

        var loaded = await store.LoadGraphicsInvestigationsAsync();

        Assert.Equal(2, loaded.Investigations.Count);
        Assert.Contains(loaded.Investigations, item => item.Id == valid.Id);
        Assert.Contains(loaded.Investigations, item => item.Id == blocked.Id);
        Assert.Contains(loaded.Warnings, w =>
            w.Contains($"{blocked.Id:D}", StringComparison.Ordinal) &&
            w.Contains("relatório derivado não pôde ser regenerado", StringComparison.Ordinal));
        Assert.Contains(loaded.Warnings, w =>
            w.Contains($"{corruptId:D}.md", StringComparison.Ordinal) &&
            w.Contains("não recuperad", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MonitoringSessionRunnerUsesInjectedClockForTickMeasurementAndCheckpoints()
    {
        var store = NewStore();
        var clock = new SyntheticSessionClock();
        var limits = new MonitoringLimits
        {
            PerformanceInterval = TimeSpan.FromMilliseconds(100),
            EventInterval = TimeSpan.FromMilliseconds(100),
            NetworkInterval = TimeSpan.FromMilliseconds(150),
            SegmentDuration = TimeSpan.FromMilliseconds(250),
            MaxCollectorTick = TimeSpan.FromMilliseconds(500)
        };
        var events = new FakeMonitoringEventSource(index =>
            [MonitoringSamples.Tdr(3000 + index, clock.GetUtcNow())]);
        var runner = new MonitoringSessionRunner(
        [
            new EventMonitoringSource(events),
            new PerformanceMonitoringSource(new FakeMonitoringPerformanceCollector())
        ], store, clock, limits);

        var session = await runner.RunAsync(new MonitoringRequest(
            MonitoringDurationPreset.Custom,
            TimeSpan.FromMilliseconds(700),
            MonitoringSources.Events | MonitoringSources.Graphics | MonitoringSources.Performance,
            Authorized: true));
        await store.SaveMonitoringSessionAsync(session);

        var loaded = await NewStore().LoadMonitoringSessionsAsync();

        Assert.Empty(loaded.Warnings);
        var copy = Assert.Single(loaded.Sessions);
        Assert.Equal(session.Segments.Count, copy.Segments.Count);
        Assert.False(copy.Budget.TickBudgetExceeded);
        Assert.Equal(TimeSpan.FromMilliseconds(700), copy.Duration);
    }

    [Fact]
    public void RepairBrokerHandlesInvalidLeaseStateWithRefusedOutcomeAndPreservesLeaseContract()
    {
        var repoRoot = FindRepoRoot();
        var programText = File.ReadAllText(Path.Combine(repoRoot, "src", "SentinelCore.RepairBroker", "Program.cs"));
        var leaseText = File.ReadAllText(Path.Combine(repoRoot, "src", "SentinelCore.RepairBroker", "BrokerLease.cs"));
        var clientText = File.ReadAllText(Path.Combine(repoRoot, "src", "SentinelCore.RepairBroker.Client", "RepairBrokerClient.cs"));

        Assert.Contains("error is InvalidDataException or JsonException", programText, StringComparison.Ordinal);
        Assert.Contains("NativeOutcome.BeforeStart(RepairBrokerStatus.Refused, \"Estado protegido do broker inválido ou ilegível; execução recusada.\")", programText, StringComparison.Ordinal);
        Assert.Contains("error is IOException or UnauthorizedAccessException", programText, StringComparison.Ordinal);
        Assert.Contains("try { lease.ClearActive(); }", programText, StringComparison.Ordinal);

        Assert.Contains("FileShare.None", leaseText, StringComparison.Ordinal);
        Assert.DoesNotContain("Replace(", leaseText, StringComparison.Ordinal);

        Assert.Contains("RepairBrokerStatus.Indeterminate", clientText, StringComparison.Ordinal);

        var outcome = NativeOutcome.BeforeStart(RepairBrokerStatus.Refused,
            "Estado protegido do broker inválido ou ilegível; execução recusada.");
        Assert.NotNull(outcome.Native);
        Assert.Equal(RepairBrokerStatus.Refused, outcome.Native!.Status);
        Assert.False(outcome.Started);
        Assert.False(outcome.Completed);
        Assert.DoesNotContain("ProgramData", outcome.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var json = JsonSerializer.Serialize(new BrokerMessage(BrokerMessageKind.Result, "nonce-1")
        {
            Outcome = outcome
        }, AuditJson.Options);
        var roundtrip = JsonSerializer.Deserialize<BrokerMessage>(json, AuditJson.Options);
        Assert.NotNull(roundtrip?.Outcome?.Native);
        Assert.Equal(RepairBrokerStatus.Refused, roundtrip!.Outcome!.Native!.Status);
    }

    private static void WriteEnvelope<T>(string path, string kind, T payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(payload, AuditJson.Options).Replace("\r\n", "\n");
        RecordEnvelope.AtomicWrite(path, RecordEnvelope.Wrap(kind, json), overwrite: true);
    }

    private static HardwareSnapshot HardwareSnapshotSample(bool withNullProperty)
    {
        var at = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var item = HardwareSamples.Item("Processor", HardwareCategory.Cpu, "cpu",
            stableId: "sha256:cpu0000000000000000000000",
            properties: withNullProperty
                ? [null!]
                : [new("architecture", "x64"), new("capacity_bytes", "1073741824")]);
        return new(1, Guid.NewGuid(), "0.2.0", "1", at, at.AddSeconds(2), TimeSpan.FromSeconds(2),
            HardwareCollectionStatus.Completed,
            [
                new HardwareCollectorResult("cpu", "0.2.0", HardwareCategory.Cpu, "sintético",
                    at, at.AddSeconds(1), TimeSpan.FromSeconds(1), HardwareCollectionStatus.Completed, [item], [])
            ]);
    }

    private static async Task<GraphicsInvestigation> GraphicsInvestigationSample()
    {
        var runner = GraphicsSamples.Runner(events: GraphicsSamples.RealisticEvents(
            events: [GraphicsSamples.DisplayTdr()]));
        return await runner.RunAsync(new GraphicsInvestigationRequest(EventWindow.Last7Days));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SentinelCore.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("SentinelCore.sln");
    }
}
