using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Modifications;
using SentinelCore.Persistence;

namespace SentinelCore.Tests;

internal sealed class T2bScope : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "SentinelCoreT2bTests",
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
    public TaskbarSearchModeController SearchModes { get; private set; } = null!;
    private readonly bool realExecutionEnabled;

    public T2bScope(bool windows11 = true, int build = 22631, bool realExecutionEnabled = true,
        OperationCatalog? catalog = null, ITransactionJournalStore? journal = null)
    {
        this.realExecutionEnabled = realExecutionEnabled;
        Time = new TestTimeProvider(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        Store = new MemoryStore(Root);
        Catalog = catalog ?? T2bCatalog.Create();
        Reader = new HkcuRegistryTargetReader(Access, Time);
        Executor = new HkcuRegistryOperationExecutor(Access);
        Locks = new FileExclusiveLockFactory(Root);
        Profile = new Windows11SystemProfileReader(Policy, realExecutionEnabled,
            T2bCatalog.OperationIds, build, Time);
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
        SearchModes = new TaskbarSearchModeController(Manager);
    }

    public OperationTarget Target(string valueName) =>
        HkcuTargetPolicy.AllowedTargets.Single(item => item.ValueName == valueName);

    public OperationTarget SearchTarget() => Target(T2bCatalog.SearchboxTaskbarModeValue);

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
