using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using SentinelCore.RepairBroker;
using Xunit;

namespace SentinelCore.Tests;

public sealed class Stage7aIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SentinelCoreStage7a", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(20000);
    private MemoryStore Store => new(root);

    public Stage7aIntegrationTests()
    {
        var temporary = Path.GetFullPath(Path.GetTempPath());
        Assert.StartsWith(temporary, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(root));
        for (var current = new DirectoryInfo(temporary); current is not null; current = current.Parent)
            Assert.False(current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public async Task AuthoritativeAuditReadDoesNotRebuildMissingSqliteOrChangeMarkdown()
    {
        var audit = Samples.Audit(At);
        await Store.SaveAsync(audit);
        var record = Path.Combine(root, "memory", "audits", "basic", $"{audit.Id:D}.md");
        var bytes = File.ReadAllBytes(record);
        var database = Path.Combine(root, "data", "sentinelcore.db");
        File.Delete(database);
        var snapshot = await Store.ReadAuditHistoryAsync();
        Assert.Equal(audit.Id, Assert.Single(snapshot.Audits).Id);
        Assert.Empty(snapshot.Warnings);
        Assert.False(File.Exists(database));
        Assert.Equal(bytes, File.ReadAllBytes(record));
    }

    [Fact]
    public async Task LegacyAndCheckpointAuditVersionsReopenWithSchemaTwelve()
    {
        await Store.SaveAsync(Samples.Audit(At));
        var audit = VersionTwo();
        using (var lease = await Store.AcquireAuditLeaseAsync(audit.Id))
        {
            await lease.SaveCheckpointAsync(audit with { Status = DiagnosticStatus.Running, Results = [] });
            await lease.CompleteAsync(audit);
        }
        var history = await Store.ReadAuditHistoryAsync();
        Assert.Equal(new[] { 1, 2 }, history.Audits.Select(item => item.FormatVersion).Order().ToArray());
        Assert.Empty(history.Warnings);
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "data", "sentinelcore.db")};Pooling=False");
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = "PRAGMA user_version";
        Assert.Equal(12L, query.ExecuteScalar());
    }

    [Fact]
    public async Task ActiveAuditIsHiddenUntilLeaseEndsAndThenRecoveredAsInterrupted()
    {
        await Store.LoadAsync();
        var audit = VersionTwo();
        using (var lease = await Store.AcquireAuditLeaseAsync(audit.Id))
        {
            await lease.SaveCheckpointAsync(audit with { Status = DiagnosticStatus.Running, Results = [] });
            Assert.Empty((await Store.ReadAuditHistoryAsync()).Audits);
            await Assert.ThrowsAsync<IOException>(() => Store.AcquireAuditLeaseAsync(Guid.NewGuid()));
        }
        var recovered = Assert.Single((await Store.ReadAuditHistoryAsync()).Audits);
        Assert.Equal(audit.Id, recovered.Id);
        Assert.Equal(DiagnosticStatus.Interrupted, recovered.Status);
        Assert.NotNull(recovered.InterruptionReason);
    }

    [Fact]
    public async Task FinalAuditDominatesCheckpointWithoutInventingAnotherOperation()
    {
        var runner = new AuditRunner([new FakeCollector("fixture")], checkpoints: Store);
        var audit = await runner.RunBasicAsync();
        var history = await Store.ReadAuditHistoryAsync();
        Assert.Equal(audit.Id, Assert.Single(history.Audits).Id);
        Assert.Equal(DiagnosticStatus.Completed, history.Audits[0].Status);
        Assert.Empty(history.Warnings);
        Assert.Empty((await Store.ReadInterruptedAuditsAsync()).Audits);
    }

    [Fact]
    public async Task CorruptAuditIsPreservedAndMakesRecoverySummaryIncomplete()
    {
        var audit = Samples.Audit(At);
        await Store.SaveAsync(audit);
        var path = Path.Combine(root, "memory", "audits", "basic", $"{audit.Id:D}.md");
        File.WriteAllText(path, "fixture: corrupt record");
        var before = Tree();
        var snapshot = await new RecoveryCenter(Store).LoadAsync();
        Assert.Empty(snapshot.Audits!.Audits);
        Assert.NotEmpty(snapshot.Audits.Warnings);
        Assert.False(RecoveryCenter.Summarize(snapshot).IsComplete);
        Assert.Equal(before, Tree());
    }

    [Fact]
    public async Task CancellationAndUninitializedHistoryNeverCreateData()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.ReadAuditHistoryAsync(cancellation.Token));
        Assert.False(Directory.Exists(root));
        var history = await Store.ReadAuditHistoryAsync();
        Assert.Empty(history.Audits);
        Assert.NotEmpty(history.Warnings);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ProductReservationSerializesIndependentFactoriesOnSameRoot()
    {
        var first = new ProductOperationCoordinator(new FileExclusiveLockFactory(root));
        var second = new ProductOperationCoordinator(new FileExclusiveLockFactory(root));
        using (first.Acquire())
        {
            await Store.SaveAsync(Samples.Audit(At));
            await Store.ReadAuditHistoryAsync();
            Assert.Throws<TargetLockedException>(() => second.Acquire());
            Assert.False(second.IsBusy);
        }
        using var next = second.Acquire();
        Assert.True(second.IsBusy);
    }

    [Fact]
    public void FailedNotificationAndReservationDisposalDoNotStrandCoordinator()
    {
        var coordinator = new ProductOperationCoordinator(new ThrowingRelease());
        void Fail() => throw new InvalidOperationException("fixture: notification failed");
        coordinator.Changed += Fail;
        Assert.Throws<IOException>(() => coordinator.Acquire());
        Assert.False(coordinator.IsBusy);
        coordinator.Changed -= Fail;
        var lease = coordinator.Acquire();
        Assert.Throws<IOException>(() => lease.Dispose());
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public void BrokerContractsRejectNonceReplaySequenceAndUnknownJsonFields()
    {
        var nonce = new string('A', 64);
        Assert.True(BrokerProtocol.ValidNonce(nonce));
        foreach (var value in new[] { "", new string('a', 64), new string('A', 63), new string('G', 64) })
            Assert.False(BrokerProtocol.ValidNonce(value));
        var message = new BrokerMessage(BrokerMessageKind.Commit, nonce);
        BrokerProtocol.Expect(message, BrokerMessageKind.Commit, nonce);
        Assert.Throws<InvalidDataException>(() => BrokerProtocol.Expect(message, BrokerMessageKind.Ready, nonce));
        Assert.Throws<InvalidDataException>(() => BrokerProtocol.Expect(message, BrokerMessageKind.Commit, new string('B', 64)));
        var json = JsonSerializer.Serialize(message, BrokerJson.Default.BrokerMessage);
        Assert.Equal(message, JsonSerializer.Deserialize(json, BrokerJson.Default.BrokerMessage));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json[..^1] + ",\"unexpected\":1}", BrokerJson.Default.BrokerMessage));
    }

    [Fact]
    public void DisconnectedBrokerOutcomeNeverConfirmsAnEffectOrSuccessfulRepair()
    {
        var outcome = NativeOutcome.BeforeStart(RepairBrokerStatus.Indeterminate, "fixture: connection lost");
        Assert.True(outcome.Interrupted);
        Assert.Null(outcome.ExitCode);
        var interpretation = RepairResultInterpreter.Interpret(RepairCatalog.CreateDefault().Get(RepairCatalog.SfcRepairId), outcome);
        Assert.True(interpretation.RequiresIntervention);
        Assert.False(interpretation.Evidence.RepairCompleted);
    }

    private static AuditRecord VersionTwo() => Samples.Audit(At) with { FormatVersion = 2, PlannedSteps = 1 };
    private string[] Tree() => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order()
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed class ThrowingRelease : IExclusiveLockFactory
    {
        public IDisposable Acquire(string key) => new Lease();
        private sealed class Lease : IDisposable { public void Dispose() => throw new IOException("fixture: release failed"); }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
