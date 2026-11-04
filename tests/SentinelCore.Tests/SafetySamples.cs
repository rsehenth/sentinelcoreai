using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using SentinelCore.Persistence;

namespace SentinelCore.Tests;

internal sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class SafetyScope : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "SentinelCoreT1Tests",
        Guid.NewGuid().ToString("N"));
    public MemoryStore Store { get; }
    public OperationCatalog Catalog { get; }
    public SyntheticTargetStore Targets { get; } = new();
    public SyntheticSystemProfileReader Profile { get; }
    public SyntheticProtectionProvider Protection { get; } = new(true);
    public FileExclusiveLockFactory Locks { get; }
    public TestTimeProvider Time { get; }
    public ITransactionJournalStore Journal { get; set; }
    public ITargetStateReader Reader { get; set; }
    public IOperationExecutor Executor { get; set; }
    public SafetyEngine Engine { get; private set; } = null!;
    public RecoveryService Recovery { get; private set; } = null!;

    public SafetyScope(bool windows11 = true, int build = 22631, OperationCatalog? catalog = null,
        DateTimeOffset? now = null)
    {
        Time = new TestTimeProvider(now ?? new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        Store = new MemoryStore(Root);
        Catalog = catalog ?? T1SyntheticCatalog.Create();
        Profile = new SyntheticSystemProfileReader(
            SyntheticSystemProfileReader.Default(Time.Now, windows11, build));
        Locks = new FileExclusiveLockFactory(Root);
        Journal = Store;
        Reader = Targets;
        Executor = new SyntheticOperationExecutor(Targets);
        Rebuild();
    }

    public void Rebuild()
    {
        Engine = new SafetyEngine(Catalog, Profile, Reader, Protection, Journal, Executor, Locks,
            Time, TimeSpan.FromMinutes(2));
        Recovery = new RecoveryService(Journal, Reader, Executor, Locks, Time);
    }

    public OperationTarget Target(string path) => new("Synthetic", path, "Value");
    public string Key(string path) => Target(path).Identity;

    public void Seed(string path, string data) =>
        Targets.Set(Target(path), new TargetValueState(true, "String", data));

    public void SeedEmpty(string path) =>
        Targets.Set(Target(path), new TargetValueState(true, "String", ""));

    public void SeedKind(string path, string kind, string data) =>
        Targets.Set(Target(path), new TargetValueState(true, kind, data));

    public void SeedTargetWithoutValue(string path) => Targets.SetTargetWithoutValue(Target(path));

    public void SeedAbsent(string path) => Targets.Remove(Target(path));

    public string Current(string path) =>
        Targets.Read(Target(path)).Value?.Data ?? "<ausente>";

    public async Task<SimulationResult> SimulateAsync(string id) => await Engine.SimulateAsync(id);

    public async Task<(SimulationResult Simulation, AuthorizationToken Token)> PrepareAsync(string id,
        string confirmation = "confirmacao-teste")
    {
        var simulation = await Engine.SimulateAsync(id);
        if (!simulation.CanExecute)
            throw new InvalidOperationException("Plano bloqueado: " + string.Join(",", simulation.Blockers));
        return (simulation, Engine.Authorize(simulation, confirmation));
    }

    public async Task<TransactionExecutionResult> ExecuteAsync(string id,
        CancellationToken cancellationToken = default)
    {
        var (simulation, token) = await PrepareAsync(id);
        return await Engine.ExecuteAsync(simulation.Plan, token, cancellationToken);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class FaultingJournal(ITransactionJournalStore inner) : ITransactionJournalStore
{
    public TransactionState? FailOn { get; set; }
    public HashSet<TransactionState> FailOnStates { get; set; } = [];
    public int Appends { get; private set; }

    public string GetTransactionReportPath(Guid transactionId) => inner.GetTransactionReportPath(transactionId);

    public Task<TransactionEventRecord> AppendEventAsync(TransactionEventRecord entry,
        CancellationToken cancellationToken = default)
    {
        Appends++;
        if (FailOn == entry.State || FailOnStates.Contains(entry.State))
            throw new IOException($"Falha de journal simulada em {entry.State}.");
        return inner.AppendEventAsync(entry, cancellationToken);
    }

    public Task<string> WriteBackupAsync(BackupRecord backup,
        CancellationToken cancellationToken = default) => inner.WriteBackupAsync(backup, cancellationToken);

    public Task<BackupRecord> ReadBackupAsync(string path,
        CancellationToken cancellationToken = default) => inner.ReadBackupAsync(path, cancellationToken);

    public Task<TransactionJournalSnapshot> LoadTransactionsAsync(
        CancellationToken cancellationToken = default) => inner.LoadTransactionsAsync(cancellationToken);

    public Task<TransactionRecord?> FindTransactionAsync(Guid transactionId,
        CancellationToken cancellationToken = default) => inner.FindTransactionAsync(transactionId, cancellationToken);
}

internal sealed class FaultingExecutor(IOperationExecutor inner) : IOperationExecutor
{
    public Func<ExecutionRequest, Task<ExecutionOutcome>>? Handler { get; set; }
    public int Calls { get; private set; }

    public Task<ExecutionOutcome> ApplyAsync(ExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Handler is null ? inner.ApplyAsync(request, cancellationToken) : Handler(request);
    }
}

internal sealed class TamperingJournal(ITransactionJournalStore inner) : ITransactionJournalStore
{
    public int BackupReads { get; private set; }

    public string GetTransactionReportPath(Guid transactionId) => inner.GetTransactionReportPath(transactionId);

    public Task<TransactionEventRecord> AppendEventAsync(TransactionEventRecord entry,
        CancellationToken cancellationToken = default) => inner.AppendEventAsync(entry, cancellationToken);

    public Task<string> WriteBackupAsync(BackupRecord backup,
        CancellationToken cancellationToken = default) => inner.WriteBackupAsync(backup, cancellationToken);

    public Task<BackupRecord> ReadBackupAsync(string path,
        CancellationToken cancellationToken = default)
    {
        BackupReads++;
        if (BackupReads == 2 && File.Exists(path))
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"A\"", "\"TAMPERED\""));
        return inner.ReadBackupAsync(path, cancellationToken);
    }

    public Task<TransactionJournalSnapshot> LoadTransactionsAsync(
        CancellationToken cancellationToken = default) => inner.LoadTransactionsAsync(cancellationToken);

    public Task<TransactionRecord?> FindTransactionAsync(Guid transactionId,
        CancellationToken cancellationToken = default) => inner.FindTransactionAsync(transactionId, cancellationToken);
}

internal sealed class DelegatingTargetReader(Func<OperationTarget, Task<ObservedTargetState>> handler)
    : ITargetStateReader
{
    public Task<ObservedTargetState> ReadAsync(OperationTarget target,
        CancellationToken cancellationToken = default) => handler(target);
}

internal sealed class SlowExecutor(IOperationExecutor inner, TimeSpan delay) : IOperationExecutor
{
    public async Task<ExecutionOutcome> ApplyAsync(ExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(delay, cancellationToken);
        return await inner.ApplyAsync(request, cancellationToken);
    }
}

internal static class SafetyTestData
{
    public static OperationDescriptor TwoTargetOperation(string id = "SYN-PREF-MULTI",
        RiskLevel risk = RiskLevel.Low)
    {
        var first = new OperationTarget("Synthetic", "Preference.MultiA", "Value");
        var second = new OperationTarget("Synthetic", "Preference.MultiB", "Value");
        return new OperationDescriptor(id, 1, "Preference", "Sintética múltipla",
            OperationScope.CurrentUser, ChangeKind.SetValue, risk, OperationAvailability.Enabled,
            new OperationCompatibility("22000", null, true), [first, second],
            [new PlannedTarget(first, new TargetValueState(true, "String", "A2")),
             new PlannedTarget(second, new TargetValueState(true, "String", "B2"))],
            new ProtectionPolicy(true, false, false), [], ["Duas alterações sintéticas."], [],
            false, false, ["Operação sintética de teste."], "test");
    }

    public static OperationCatalog MultiTargetCatalog() =>
        new("t1-multi-1", [TwoTargetOperation()]);
}
