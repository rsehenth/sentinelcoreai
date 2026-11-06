using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;
using Fixture = SentinelCore.Tests.MultiprocessJournalTests.Fixture;

namespace SentinelCore.Tests;

/// <summary>
/// T4B.2 final blockers. Only artificial temp roots and in-memory synthetic adapters; no Windows query, registry,
/// package, elevation or real executor is involved. Worker processes run a closed set of audited actions.
/// </summary>
public sealed class T4b2InterruptionAndConcurrencyTests
{
    private static string JournalDirectory(string root, Guid id) =>
        Path.Combine(root, "memory", "transactions", id.ToString("D"), "journal");
    // A killed process can keep its file handles for a moment; retry instead of assuming immediate release.
    private static async Task<string> ReadWhenReleasedAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (attempt < 250) { await Task.Delay(20); }
        }
    }
    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static TransactionEventRecord Next(TransactionEventRecord first) => first with
    {
        Sequence = 2, State = TransactionState.PreflightPassed, Plan = null,
        Detail = "Preflight sintético.", AtUtc = first.AtUtc.AddSeconds(1)
    };
    private static AtomicWriteStage Parse(string stage) => Enum.Parse<AtomicWriteStage>(stage);

    private static async Task<(string Journal, string EventOne, string HashOne)> SeedFirstAsync(
        Fixture fixture, TransactionEventRecord first)
    {
        await fixture.WriteAsync(first);
        Assert.Equal(0, await fixture.RunAsync("append"));
        var journal = JournalDirectory(fixture.Root, first.TransactionId);
        var one = Directory.GetFiles(journal, "*.md").Single();
        return (journal, one, Sha(one));
    }

    [Fact]
    public void ProbeSeamIsInternalNullByDefaultAndVisibleOnlyToTestAssemblies()
    {
        var assembly = typeof(MemoryStore).Assembly;
        var visible = assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(item => item.AssemblyName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["SentinelCore.Tests", "SyntheticJournalWorker"], visible);
        Assert.False(assembly.GetType("SentinelCore.Persistence.RecordEnvelope")!.IsPublic);
        Assert.Null(RecordEnvelope.Probe.Value);
    }

    // ---- B1, component level: failures that unwind through AtomicWrite's own finally block. ----

    [Fact] public Task InjectedFailureBeforeTemporaryKeepsHistoryAndCleansTemporary() => InjectedFailureBeforePublishAsync("BeforeTemporary");
    [Fact] public Task InjectedFailureDuringTemporaryWriteKeepsHistoryAndCleansTemporary() => InjectedFailureBeforePublishAsync("DuringTemporaryWrite");
    [Fact] public Task InjectedFailureBeforePublishKeepsHistoryAndCleansTemporary() => InjectedFailureBeforePublishAsync("BeforePublish");

    private static async Task InjectedFailureBeforePublishAsync(string stage)
    {
        using var fixture = new Fixture();
        var first = await fixture.EventAsync();
        var store = new MemoryStore(fixture.Root);
        await store.AppendEventAsync(first);
        var journal = JournalDirectory(fixture.Root, first.TransactionId);
        var eventOne = Directory.GetFiles(journal, "*.md").Single();
        var hashOne = Sha(eventOne);
        var calls = 0;
        RecordEnvelope.Probe.Value = reached =>
        {
            if (reached != Parse(stage)) return;
            calls++;
            throw new IOException("Fixture: falha injetada na persistência.");
        };
        try { await Assert.ThrowsAsync<IOException>(() => store.AppendEventAsync(Next(first))); }
        finally { RecordEnvelope.Probe.Value = null; }

        Assert.Equal(1, calls); // the store never retries a possibly persisted effect by itself
        Assert.Equal([eventOne], Directory.GetFiles(journal));
        Assert.Equal(hashOne, Sha(eventOne));
        var snapshot = await new MemoryStore(fixture.Root).LoadTransactionsAsync();
        Assert.Empty(snapshot.Warnings);
        var record = Assert.Single(snapshot.Transactions);
        Assert.Single(record.Events);
        Assert.False(record.Succeeded);
        await store.AppendEventAsync(Next(first));
        Assert.Equal(2, Assert.Single((await new MemoryStore(fixture.Root).LoadTransactionsAsync()).Transactions).Events.Count);
    }

    [Fact]
    public async Task FailureAfterPublishReportsErrorButEventIsDurableAndRetryIsIdempotent()
    {
        using var fixture = new Fixture();
        var first = await fixture.EventAsync();
        var store = new MemoryStore(fixture.Root);
        await store.AppendEventAsync(first);
        var calls = 0;
        RecordEnvelope.Probe.Value = reached =>
        {
            if (reached != AtomicWriteStage.AfterPublish) return;
            calls++;
            throw new IOException("Fixture: confirmação externa perdida.");
        };
        try { await Assert.ThrowsAsync<IOException>(() => store.AppendEventAsync(Next(first))); }
        finally { RecordEnvelope.Probe.Value = null; }

        Assert.Equal(1, calls);
        var journal = JournalDirectory(fixture.Root, first.TransactionId);
        Assert.Empty(Directory.GetFiles(journal, "*.tmp"));
        var reopened = await new MemoryStore(fixture.Root).LoadTransactionsAsync();
        Assert.Empty(reopened.Warnings);
        var record = Assert.Single(reopened.Transactions);
        Assert.Equal(2, record.Events.Count);
        Assert.False(record.Succeeded);
        await store.AppendEventAsync(Next(first));
        Assert.Equal(2, Assert.Single((await new MemoryStore(fixture.Root).LoadTransactionsAsync()).Transactions).Events.Count);
    }

    // ---- B1, process level: the writer is killed while blocked inside the real AtomicWrite. ----

    [Fact] public Task WriterKilledBeforeTemporaryLeavesPriorHistoryIntact() => KilledInsideAtomicWriteAsync("BeforeTemporary", "before-temp", 0);
    [Fact] public Task WriterKilledDuringTemporaryWriteLeavesPartialTemporaryUnaccepted() => KilledInsideAtomicWriteAsync("DuringTemporaryWrite", "during-temp", 1);
    [Fact] public Task WriterKilledBeforePublishLeavesCompleteTemporaryUnaccepted() => KilledInsideAtomicWriteAsync("BeforePublish", "before-publish", 1);

    private static async Task KilledInsideAtomicWriteAsync(string stage, string mode, int temporaries)
    {
        using var fixture = new Fixture();
        var first = await fixture.EventAsync();
        var (journal, eventOne, hashOne) = await SeedFirstAsync(fixture, first);
        await fixture.WriteAsync(Next(first), "next.json");
        using var writer = await fixture.StartAsync("pause-" + mode);
        try
        {
            await writer.StandardInput.WriteLineAsync("go");
            Assert.Equal("AT:" + stage, await writer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(temporaries, Directory.GetFiles(journal, "*.tmp").Length);
            writer.Kill();
            await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { if (!writer.HasExited) { writer.Kill(); await writer.WaitForExitAsync(); } }

        var leftovers = Directory.GetFiles(journal, "*.tmp");
        Assert.Equal(temporaries, leftovers.Length); // crash: the finally block never ran, so evidence is preserved
        foreach (var leftover in leftovers)
        {
            var text = await ReadWhenReleasedAsync(leftover);
            if (stage == "DuringTemporaryWrite") Assert.Throws<InvalidDataException>(() => RecordEnvelope.Unwrap(text));
            else _ = RecordEnvelope.Unwrap(text); // complete but never published: still not an event
        }
        var snapshot = await Fixture_Reopen(fixture);
        Assert.Empty(snapshot.Warnings);
        var record = Assert.Single(snapshot.Transactions);
        Assert.Single(record.Events);
        Assert.True(record.IsIndeterminate);
        Assert.False(record.Succeeded);
        Assert.Equal(hashOne, Sha(eventOne));
        Assert.Single(Directory.GetFiles(journal, "*.md"));

        Assert.Equal(0, await fixture.RunAsync("append-next"));
        record = Assert.Single((await Fixture_Reopen(fixture)).Transactions);
        Assert.Equal(2, record.Events.Count);
        Assert.False(record.Succeeded);
        Assert.Equal(hashOne, Sha(eventOne));
        Assert.Equal(temporaries, Directory.GetFiles(journal, "*.tmp").Length);
    }

    [Fact]
    public async Task WriterKilledAfterPublishBeforeConfirmationReopensWithDurableEventAndIdempotentRetry()
    {
        using var fixture = new Fixture();
        var first = await fixture.EventAsync();
        var (journal, eventOne, hashOne) = await SeedFirstAsync(fixture, first);
        await fixture.WriteAsync(Next(first), "next.json");
        using var writer = await fixture.StartAsync("pause-after-publish");
        try
        {
            await writer.StandardInput.WriteLineAsync("go");
            Assert.Equal("AT:AfterPublish", await writer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            writer.Kill();
            await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { if (!writer.HasExited) { writer.Kill(); await writer.WaitForExitAsync(); } }

        var snapshot = await Fixture_Reopen(fixture);
        Assert.Empty(snapshot.Warnings);
        var record = Assert.Single(snapshot.Transactions);
        Assert.Equal(2, record.Events.Count);
        Assert.False(record.Succeeded);
        Assert.Empty(Directory.GetFiles(journal, "*.tmp"));
        Assert.Equal(hashOne, Sha(eventOne));
        Assert.Equal(0, await fixture.RunAsync("append-next"));
        Assert.Equal(2, Assert.Single((await Fixture_Reopen(fixture)).Transactions).Events.Count);
    }

    [Fact]
    public async Task WriterKilledDuringFirstEventLeavesNoFabricatedHistoryAndFailsClosed()
    {
        using var fixture = new Fixture();
        var first = await fixture.EventAsync();
        await fixture.WriteAsync(first, "next.json");
        using var writer = await fixture.StartAsync("pause-during-temp");
        try
        {
            await writer.StandardInput.WriteLineAsync("go");
            Assert.Equal("AT:DuringTemporaryWrite", await writer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            writer.Kill();
            await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { if (!writer.HasExited) { writer.Kill(); await writer.WaitForExitAsync(); } }

        var journal = JournalDirectory(fixture.Root, first.TransactionId);
        Assert.Empty(Directory.GetFiles(journal, "*.md"));
        Assert.Single(Directory.GetFiles(journal, "*.tmp"));
        var snapshot = await Fixture_Reopen(fixture);
        Assert.Empty(snapshot.Transactions); // a partial first event is never read as history or success
        // Deliberate fail-closed contract from the earlier journal fixes: a journal directory without valid events
        // refuses a retry of the same transaction (no effect had started; a new transaction id is required).
        Assert.Equal(4, await fixture.RunAsync("append-next"));
        Assert.Empty(Directory.GetFiles(journal, "*.md"));
    }

    // ---- B2: a second process reads while the writer is blocked inside AtomicWrite holding store.lock. ----

    [Fact] public Task ContentionDuringTemporaryWriteIsRejectedThenFullSnapshotAfterRelease() => ContentionInsideAtomicWriteAsync("DuringTemporaryWrite", "during-temp");
    [Fact] public Task ContentionBeforePublishIsRejectedThenFullSnapshotAfterRelease() => ContentionInsideAtomicWriteAsync("BeforePublish", "before-publish");
    [Fact] public Task ContentionAfterPublishIsRejectedThenFullSnapshotAfterRelease() => ContentionInsideAtomicWriteAsync("AfterPublish", "after-publish");

    private static async Task ContentionInsideAtomicWriteAsync(string stage, string mode)
    {
        using var fixture = new Fixture();
        var first = await fixture.EventAsync();
        var (journal, eventOne, hashOne) = await SeedFirstAsync(fixture, first);
        await fixture.WriteAsync(Next(first), "next.json");
        using var writer = await fixture.StartAsync("pause-" + mode);
        try
        {
            await writer.StandardInput.WriteLineAsync("go");
            Assert.Equal("AT:" + stage, await writer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            // The writer is provably blocked inside the persistence path: the readers below start after its signal
            // and the writer only proceeds when the release line is sent after they have exited.
            Assert.Equal(3, await fixture.RunAsync("read"));
            Assert.Equal(6, await fixture.RunAsync("read-recovery"));
            Assert.Equal(3, await fixture.RunAsync("append-next"));
            Assert.False(writer.HasExited);
            Assert.Equal(hashOne, Sha(eventOne));
            Assert.Equal(stage == "AfterPublish" ? 2 : 1, Directory.GetFiles(journal, "*.md").Length);
            await writer.StandardInput.WriteLineAsync("release");
            Assert.Equal(0, await Fixture.ExitAsync(writer));
        }
        finally { if (!writer.HasExited) { writer.Kill(); await writer.WaitForExitAsync(); } }

        Assert.Equal(0, await fixture.RunAsync("read-recovery"));
        var snapshot = await Fixture_Reopen(fixture);
        Assert.Empty(snapshot.Warnings);
        var record = Assert.Single(snapshot.Transactions);
        Assert.Equal(2, record.Events.Count);
        Assert.Equal(hashOne, Sha(eventOne));
        Assert.Empty(Directory.GetFiles(journal, "*.tmp"));
        Assert.Equal(2, Directory.GetFiles(journal, "*.md").Length);
    }

    private static Task<TransactionJournalSnapshot> Fixture_Reopen(Fixture fixture) =>
        MultiprocessJournalTests.ReopenAfterExitAsync(fixture.Root);

    // ---- B3: T3C attempts on one target, with explicit synchronisation. ----

    [Fact]
    public async Task AttemptsRendezvousAtLockAcquisitionAndNeitherExecutesOnUnverifiedTarget()
    {
        using var scope = new DebloatRemovalTestScope();
        var first = await scope.Prepare(DebloatRemovalScenario.Unverifiable);
        var initial = await scope.Manager.ExecuteAsync(first, scope.Authorize(first));
        Assert.True(initial.Receipt!.EffectMayHaveOccurred);
        var request = scope.Adapter.LastRequest;
        var transactions = Path.Combine(scope.Root, "memory", "debloat");
        var before = Directory.GetFiles(transactions, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, Sha);

        var locks = new RendezvousLocks(new FileExclusiveLockFactory(scope.Root));
        DebloatRemovalManager Contender() => new(scope.Catalog, scope.Adapter, scope.Adapter,
            new GatedJournal(new MemoryStore(scope.Root).CreateDebloatJournal(), () => locks.Refused.Task.WaitAsync(Limit)),
            locks, scope.Time);
        var a = Contender(); var b = Contender();
        var pa = await a.PrepareAsync(); var pb = await b.PrepareAsync();
        var ta = a.Authorize(pa, pa.Confirmation); var tb = b.Authorize(pb, pb.Confirmation);

        var results = await Task.WhenAll(Task.Run(() => a.ExecuteAsync(pa, ta)), Task.Run(() => b.ExecuteAsync(pb, tb)))
            .WaitAsync(Limit);

        Assert.Equal(2, locks.Arrived); // both attempts were inside ExecuteAsync and reached the lock together
        Assert.Single(results, r => r.Transaction.Blockers.Contains(BlockerCode.TargetLocked));
        Assert.Single(results, r => r.Transaction.Blockers.Contains(BlockerCode.IndeterminateState));
        Assert.All(results, r => { Assert.False(r.Succeeded); Assert.Null(r.Receipt); });
        Assert.Equal(1, scope.Adapter.ExecutionCount);
        Assert.Same(request, scope.Adapter.LastRequest);
        Assert.Equal(before, Directory.GetFiles(transactions, "*", SearchOption.AllDirectories).ToDictionary(path => path, Sha));

        for (var rebuild = 0; rebuild < 2; rebuild++)
        {
            if (rebuild == 1)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                File.Delete(Path.Combine(scope.Root, "data", "sentinelcore.db"));
            }
            var manager = scope.Create(new MemoryStore(scope.Root).CreateDebloatJournal());
            var history = await manager.LoadHistoryAsync();
            Assert.Empty(history.Warnings);
            Assert.True(Assert.Single(history.Items).Transaction.IsIndeterminate);
            var next = await manager.PrepareAsync();
            var retry = await manager.ExecuteAsync(next, manager.Authorize(next, next.Confirmation));
            Assert.Contains(BlockerCode.IndeterminateState, retry.Transaction.Blockers);
            Assert.Equal(1, scope.Adapter.ExecutionCount);
        }
    }

    [Fact]
    public async Task SecondAttemptIsRejectedWhileFirstHoldsTargetLockInsideExecutorAndEffectRunsOnce()
    {
        using var scope = new DebloatRemovalTestScope();
        var a = scope.Create(new MemoryStore(scope.Root).CreateDebloatJournal());
        var b = scope.Create(new MemoryStore(scope.Root).CreateDebloatJournal());
        var pa = await a.PrepareAsync(); var pb = await b.PrepareAsync();
        var ta = a.Authorize(pa, pa.Confirmation); var tb = b.Authorize(pb, pb.Confirmation);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        scope.Adapter.AfterStart = () => { started.TrySetResult(); release.Wait(Limit); };
        var firstAttempt = Task.Run(() => a.ExecuteAsync(pa, ta));
        try
        {
            await started.Task.WaitAsync(Limit); // A is inside the (synthetic) executor, after the journaled start
            var second = await b.ExecuteAsync(pb, tb).WaitAsync(Limit);
            Assert.Contains(BlockerCode.TargetLocked, second.Transaction.Blockers);
            Assert.False(second.Succeeded); Assert.Null(second.Receipt);
            Assert.Equal(1, scope.Adapter.ExecutionCount);
            Assert.Single(Directory.GetDirectories(Path.Combine(scope.Root, "memory", "debloat")));
            Assert.False(firstAttempt.IsCompleted);
        }
        finally { release.Set(); }
        var outcome = await firstAttempt.WaitAsync(Limit);
        Assert.True(outcome.Succeeded);
        Assert.Equal(1, scope.Adapter.ExecutionCount);
        var history = await scope.Create(new MemoryStore(scope.Root).CreateDebloatJournal()).LoadHistoryAsync();
        Assert.Empty(history.Warnings);
        Assert.Single(history.Items);
    }

    private static TimeSpan Limit => TimeSpan.FromSeconds(20);

    private sealed class RendezvousLocks(IExclusiveLockFactory inner) : IExclusiveLockFactory
    {
        private readonly Barrier barrier = new(2);
        private int arrived;
        public int Arrived => Volatile.Read(ref arrived);
        public TaskCompletionSource Refused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable Acquire(string key)
        {
            Interlocked.Increment(ref arrived);
            if (!barrier.SignalAndWait(Limit)) throw new TimeoutException("Fixture: tentativa concorrente não chegou.");
            try { return inner.Acquire(key); }
            catch (TargetLockedException) { Refused.TrySetResult(); throw; }
        }
    }

    /// <summary>Holds the lock winner (the only caller that reaches the journal) until the loser has been refused.</summary>
    private sealed class GatedJournal(ITransactionJournalStore inner, Func<Task> beforeFind) : ITransactionJournalStore
    {
        public string GetTransactionReportPath(Guid id) => inner.GetTransactionReportPath(id);
        public Task<TransactionEventRecord> AppendEventAsync(TransactionEventRecord entry, CancellationToken token = default) =>
            inner.AppendEventAsync(entry, token);
        public Task<string> WriteBackupAsync(BackupRecord backup, CancellationToken token = default) =>
            inner.WriteBackupAsync(backup, token);
        public Task<BackupRecord> ReadBackupAsync(string path, CancellationToken token = default) =>
            inner.ReadBackupAsync(path, token);
        public Task<TransactionJournalSnapshot> LoadTransactionsAsync(CancellationToken token = default) =>
            inner.LoadTransactionsAsync(token);
        public async Task<TransactionRecord?> FindTransactionAsync(Guid id, CancellationToken token = default)
        {
            await beforeFind();
            return await inner.FindTransactionAsync(id, token);
        }
    }
}
