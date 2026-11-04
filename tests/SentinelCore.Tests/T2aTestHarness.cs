using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using SentinelCore.Persistence;

namespace SentinelCore.Tests;

internal sealed class FakeHkcuRegistryAccess : IHkcuRegistryAccess
{
    private readonly Dictionary<string, StoredValue> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> missingKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> deniedReads = new(StringComparer.Ordinal);
    private readonly HashSet<string> deniedWrites = new(StringComparer.Ordinal);
    private readonly HashSet<string> failingWrites = new(StringComparer.Ordinal);

    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public int Deletes { get; private set; }
    public Action<OperationTarget>? AfterWrite { get; set; }

    public void SeedDWord(OperationTarget target, int value) =>
        values[target.Identity] = new StoredValue(RegistryValueKinds.DWord,
            value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public void SeedString(OperationTarget target, string value) =>
        values[target.Identity] = new StoredValue(RegistryValueKinds.String, value);
    public void SeedAbsentValue(OperationTarget target) => values.Remove(target.Identity);
    public void SeedMissingKey(OperationTarget target) => missingKeys.Add(target.Path);
    public void DenyRead(OperationTarget target) => deniedReads.Add(target.Identity);
    public void DenyWrite(OperationTarget target) => deniedWrites.Add(target.Identity);
    public void FeatureWriteFailure(OperationTarget target) => failingWrites.Add(target.Identity);
    public void AllowWrite(OperationTarget target)
    {
        deniedWrites.Remove(target.Identity);
        failingWrites.Remove(target.Identity);
    }

    public TargetValueState State(OperationTarget target) =>
        values.TryGetValue(target.Identity, out var stored)
            ? new TargetValueState(true, stored.Kind, stored.Data)
            : new TargetValueState(false, RegistryValueKinds.DWord, null);

    public string? Data(OperationTarget target) =>
        values.TryGetValue(target.Identity, out var stored) ? stored.Data : null;

    public HkcuValueRead Read(OperationTarget target)
    {
        Reads++;
        if (deniedReads.Contains(target.Identity)) return HkcuValueRead.Denied("Acesso negado simulado.");
        if (missingKeys.Contains(target.Path)) return HkcuValueRead.MissingKey();
        return values.TryGetValue(target.Identity, out var stored)
            ? HkcuValueRead.Ok(stored.Kind, stored.Data)
            : HkcuValueRead.MissingValue();
    }

    public HkcuWriteResult SetDWord(OperationTarget target, int value)
    {
        if (deniedWrites.Contains(target.Identity)) return HkcuWriteResult.Denied("Acesso negado simulado.");
        if (failingWrites.Contains(target.Identity)) return HkcuWriteResult.Failed("Falha de escrita simulada.");
        if (missingKeys.Contains(target.Path)) return HkcuWriteResult.Missing();
        values[target.Identity] = new StoredValue(RegistryValueKinds.DWord,
            value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Writes++;
        AfterWrite?.Invoke(target);
        return HkcuWriteResult.Ok();
    }

    public HkcuWriteResult DeleteValue(OperationTarget target)
    {
        if (deniedWrites.Contains(target.Identity)) return HkcuWriteResult.Denied("Acesso negado simulado.");
        if (failingWrites.Contains(target.Identity)) return HkcuWriteResult.Failed("Falha de escrita simulada.");
        if (missingKeys.Contains(target.Path)) return HkcuWriteResult.Missing();
        values.Remove(target.Identity);
        Deletes++;
        AfterWrite?.Invoke(target);
        return HkcuWriteResult.Ok();
    }

    private sealed record StoredValue(string Kind, string? Data);
}

internal sealed class FakeHkcuPolicyProbe : IHkcuPolicyProbe
{
    public List<string> BlockedOperationIds { get; } = [];
    public List<string> Limitations { get; } = [];
    public bool Throw { get; set; }

    public HkcuPolicyEvaluation Evaluate() => Throw
        ? throw new InvalidOperationException("Falha simulada da sonda de políticas.")
        : new(BlockedOperationIds, [], Limitations);
}

internal sealed class T2aScope : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "SentinelCoreT2aTests",
        Guid.NewGuid().ToString("N"));
    public MemoryStore Store { get; }
    public OperationCatalog Catalog { get; }
    public FakeHkcuRegistryAccess Access { get; } = new();
    public FakeHkcuPolicyProbe Policy { get; } = new();
    public HkcuRegistryTargetReader Reader { get; }
    public HkcuRegistryOperationExecutor Executor { get; }
    public FileExclusiveLockFactory Locks { get; }
    public TestTimeProvider Time { get; }
    public ISystemProfileReader Profile { get; }
    public SafetyEngine Engine { get; private set; } = null!;
    public RecoveryService Recovery { get; private set; } = null!;
    public TweakManager Manager { get; private set; } = null!;
    private readonly bool realExecutionEnabled;

    public T2aScope(bool windows11 = true, int build = 22631, bool realExecutionEnabled = true,
        OperationCatalog? catalog = null, ITransactionJournalStore? journal = null)
    {
        this.realExecutionEnabled = realExecutionEnabled;
        Time = new TestTimeProvider(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        Store = new MemoryStore(Root);
        Catalog = catalog ?? T2aCatalog.Create();
        Reader = new HkcuRegistryTargetReader(Access, Time);
        Executor = new HkcuRegistryOperationExecutor(Access);
        Locks = new FileExclusiveLockFactory(Root);
        Profile = new Windows11SystemProfileReader(Policy, realExecutionEnabled,
            T2aCatalog.OperationIds, build, Time);
        Rebuild(journal ?? Store, realExecutionEnabled);
    }

    public void Rebuild(ITransactionJournalStore journal, bool? realExecutionEnabled = null)
    {
        var real = realExecutionEnabled ?? this.realExecutionEnabled;
        Engine = new SafetyEngine(Catalog, Profile, Reader, new HkcuProtectionProvider(),
            journal, Executor, Locks, Time, TimeSpan.FromMinutes(2),
            new TweakAuthorizationConfirmationPolicy());
        Recovery = new RecoveryService(journal, Reader, Executor, Locks, Time, Catalog,
            new TweakRecoveryConfirmationPolicy());
        Manager = new TweakManager(Catalog, Engine, Recovery, journal,
            real ? TweakExecutionMode.RealControlled : TweakExecutionMode.RealDisabled);
    }

    public OperationTarget Target(string valueName) =>
        HkcuTargetPolicy.AllowedTargets.Single(item => item.ValueName == valueName);

    public string? Data(string valueName) => Access.Data(Target(valueName));

    public async Task<SimulationResult> SimulateAsync(string id) => await Engine.SimulateAsync(id);

    public async Task<(SimulationResult Simulation, AuthorizationToken Token)> PrepareAsync(string id,
        string? confirmation = null)
    {
        var simulation = await Engine.SimulateAsync(id);
        if (!simulation.CanExecute)
            throw new InvalidOperationException("Plano bloqueado: " + string.Join(",", simulation.Blockers));
        return (simulation, Engine.Authorize(simulation,
            confirmation ?? TweakManager.BuildConfirmationText(simulation.Plan)));
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
