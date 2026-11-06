using System.Diagnostics;
using System.Text.Json;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class MultiprocessJournalTests
{
    [Fact]
    public async Task InterruptedSyntheticTemporaryIsNotAcceptedAsJournalEvent()
    {
        using var fixture = new Fixture();
        var entry = await fixture.EventAsync();
        await fixture.WriteAsync(entry);
        Assert.Equal(0, await fixture.RunAsync("append"));
        using var worker = await fixture.StartAsync("partial");
        try
        {
            await worker.StandardInput.WriteLineAsync("go");
            Assert.Equal("PARTIAL", await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(3, await fixture.RunAsync("read"));
            worker.Kill();
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var snapshot = await ReopenAfterExitAsync(fixture.Root);
            Assert.Empty(snapshot.Warnings);
            var transaction = Assert.Single(snapshot.Transactions);
            Assert.Single(transaction.Events);
            Assert.True(transaction.IsIndeterminate);
            Assert.False(transaction.Succeeded);
            Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "memory", "transactions",
                entry.TransactionId.ToString("D"), "journal"), "*.tmp"));
        }
        finally { if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync(); } }
    }
    [Fact]
    public async Task TargetLocksExcludeSameIdentityAndPermitDifferentIdentityAcrossProcesses()
    {
        using var fixture = new Fixture();
        using var a = await fixture.StartAsync("target-a");
        using var b = await fixture.StartAsync("target-b");
        try
        {
            await a.StandardInput.WriteLineAsync("go");
            Assert.Equal("HELD", await a.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            await b.StandardInput.WriteLineAsync("go");
            Assert.Equal("HELD", await b.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(5, await fixture.RunAsync("target-a"));
            a.Kill();
            await a.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            using var again = await fixture.StartAsync("target-a");
            await again.StandardInput.WriteLineAsync("go");
            Assert.Equal("HELD", await again.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            await again.StandardInput.WriteLineAsync("release");
            Assert.Equal(0, await Fixture.ExitAsync(again));
            await b.StandardInput.WriteLineAsync("release");
            Assert.Equal(0, await Fixture.ExitAsync(b));
        }
        finally
        {
            if (!a.HasExited) { a.Kill(); await a.WaitForExitAsync(); }
            if (!b.HasExited) { b.Kill(); await b.WaitForExitAsync(); }
        }
    }
    [Fact]
    public async Task IndependentWritersPreserveIdempotencyAndRejectConflicts()
    {
        using var fixture = new Fixture();
        var entry = await fixture.EventAsync();
        await fixture.WriteAsync(entry);
        using var a = await fixture.StartAsync("append");
        using var b = await fixture.StartAsync("append");
        Assert.NotEqual(a.Id, b.Id);
        await a.StandardInput.WriteLineAsync("go");
        await b.StandardInput.WriteLineAsync("go");
        var outcomes = await Task.WhenAll(Fixture.ExitAsync(a), Fixture.ExitAsync(b));
        Assert.Contains(0, outcomes);
        Assert.All(outcomes, code => Assert.True(code is 0 or 3));
        var snapshot = await new MemoryStore(fixture.Root).LoadTransactionsAsync();
        Assert.Empty(snapshot.Warnings);
        Assert.Single(Assert.Single(snapshot.Transactions).Events);
        Assert.Equal(0, await fixture.RunAsync("append"));
        await fixture.WriteAsync(entry with { Detail = "conflito sintético" });
        Assert.Equal(4, await fixture.RunAsync("append"));
        Assert.Single(Assert.Single((await new MemoryStore(fixture.Root).LoadTransactionsAsync()).Transactions).Events);
    }

    [Fact]
    public async Task CompetingDifferentEventsCannotBothWin()
    {
        using var fixture = new Fixture();
        var entry = await fixture.EventAsync();
        await fixture.WriteAsync(entry);
        await fixture.WriteAsync(entry with { Detail = "outro evento" }, "conflict.json");
        using var a = await fixture.StartAsync("append");
        using var b = await fixture.StartAsync("append-conflict");
        await a.StandardInput.WriteLineAsync("go");
        await b.StandardInput.WriteLineAsync("go");
        var outcomes = await Task.WhenAll(Fixture.ExitAsync(a), Fixture.ExitAsync(b));
        Assert.Single(outcomes, code => code == 0);
        Assert.Single(outcomes, code => code is 3 or 4);
        var record = Assert.Single((await new MemoryStore(fixture.Root).LoadTransactionsAsync()).Transactions);
        Assert.Single(record.Events);
        var losingMode = outcomes[0] == 0 ? "append-conflict" : "append";
        Assert.Equal(4, await fixture.RunAsync(losingMode));
    }

    [Fact]
    public async Task TerminationAfterPersistedEventAllowsIdempotentReopen()
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync(await fixture.EventAsync());
        using var worker = await fixture.StartAsync("append-hold");
        try
        {
            await worker.StandardInput.WriteLineAsync("go");
            Assert.Equal("PERSISTED", await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            worker.Kill();
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var snapshot = await new MemoryStore(fixture.Root).LoadTransactionsAsync();
            Assert.Empty(snapshot.Warnings);
            var record = Assert.Single(snapshot.Transactions);
            Assert.True(record.IsIndeterminate);
            Assert.False(record.Succeeded);
            Assert.Equal(0, await fixture.RunAsync("append"));
            Assert.Single(Assert.Single((await new MemoryStore(fixture.Root).LoadTransactionsAsync()).Transactions).Events);
        }
        finally { if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task HeldLockRejectsReadersAndWritersUntilOwnerIsTerminated()
    {
        using var fixture = new Fixture();
        var entry = await fixture.EventAsync();
        await fixture.WriteAsync(entry);
        using var holder = await fixture.StartAsync("hold");
        try
        {
            Assert.Equal(3, await fixture.RunAsync("append"));
            Assert.Equal(3, await fixture.RunAsync("read"));
            using var other = new Fixture();
            Assert.Equal(0, await other.RunAsync("read"));
            holder.Kill();
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, await fixture.RunAsync("append"));
            var record = Assert.Single((await new MemoryStore(fixture.Root).LoadTransactionsAsync()).Transactions);
            Assert.True(record.IsIndeterminate);
            Assert.False(record.Succeeded);
        }
        finally
        {
            if (!holder.HasExited) { holder.Kill(); await holder.WaitForExitAsync(); }
        }
    }

    internal static async Task<TransactionJournalSnapshot> ReopenAfterExitAsync(string root)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try { return await new MemoryStore(root).LoadTransactionsAsync(); }
            catch (IOException) when (deadline.Elapsed < TimeSpan.FromSeconds(5))
            { await Task.Delay(20); }
        }
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly Guid id = Guid.NewGuid();
        public string Root { get; }
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "SentinelCoreT4B2", id.ToString("N"));
            Directory.CreateDirectory(Root);
        }
        public async Task<TransactionEventRecord> EventAsync()
        {
            using var scope = new SafetyScope();
            scope.Seed("Preference.Test", "A");
            var p = (await scope.Engine.SimulateAsync("SYN-PREF-01")).Plan;
            return new(1, p.TransactionId, 1, TransactionState.Planned, scope.Time.Now,
                p.OperationId, p.OperationVersion, p.CatalogVersion, p.PlanHash,
                null, null, p, null, null, null, null, null, null, []);
        }
        public Task WriteAsync(TransactionEventRecord entry, string name = "event.json") => File.WriteAllTextAsync(
            Path.Combine(Root, name), JsonSerializer.Serialize(entry, AuditJson.Options));
        public async Task<Process> StartAsync(string mode)
        {
            var output = new DirectoryInfo(AppContext.BaseDirectory);
            var config = output.Parent!.Name;
            var tests = output.Parent.Parent!.Parent!.Parent!.FullName;
            var executable = Path.Combine(tests, "SyntheticJournalWorker", "bin", config, "net10.0", "SyntheticJournalWorker.dll");
            var dotnet = Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")!, "dotnet.exe");
            var info = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            info.ArgumentList.Add(executable); info.ArgumentList.Add(id.ToString("N")); info.ArgumentList.Add(mode);
            var process = Process.Start(info) ?? throw new InvalidOperationException("Auxiliar não iniciado.");
            try
            {
                var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                if (ready != "READY") throw new InvalidOperationException("Auxiliar não sinalizou prontidão.");
                return process;
            }
            catch
            {
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
                process.Dispose(); throw;
            }
        }
        public static async Task<int> ExitAsync(Process process)
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); return process.ExitCode; }
            catch { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } throw; }
        }
        public async Task<int> RunAsync(string mode)
        {
            using var process = await StartAsync(mode);
            await process.StandardInput.WriteLineAsync("go");
            return await ExitAsync(process);
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
