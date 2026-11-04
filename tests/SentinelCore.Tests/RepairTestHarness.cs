using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Repairs;
using SentinelCore.Persistence;

namespace SentinelCore.Tests;

internal sealed class FakeRepairProfile : IRepairSystemProfileReader
{
    public RepairSystemProfile Profile { get; set; } = new(
        "Windows 11 build 22631", 22631, true, false, true, "test",
        new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), []);

    public RepairSystemProfile Read() => Profile;
}

internal sealed class FakeRepairVolumes : IRepairVolumeProvider
{
    public List<RepairVolume> Volumes { get; } =
    [
        Volume("C:", "NTFS", true, true),
        Volume("D:", "NTFS", true, true),
        Volume("E:", "exFAT", true, false),
        Volume("N:", "NTFS", false, true)
    ];

    public Task<IReadOnlyList<RepairVolume>> GetVolumesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RepairVolume>>(Volumes.ToList());

    public static RepairVolume Volume(string id, string fileSystem, bool local, bool scan) =>
        new(id, id + "\\", fileSystem, "Fixed", true, 500L * 1024 * 1024 * 1024,
            200L * 1024 * 1024 * 1024, local, scan, $"{id} · teste · {fileSystem}",
            ["Volume sintético de teste; não representa um disco real."]);
}

internal sealed class FakeRepairToolLocator : IRepairToolLocator
{
    public Dictionary<RepairTool, RepairToolLocation> Locations { get; } = new()
    {
        [RepairTool.Sfc] = Location(RepairTool.Sfc),
        [RepairTool.Dism] = Location(RepairTool.Dism),
        [RepairTool.Chkdsk] = Location(RepairTool.Chkdsk),
        [RepairTool.Reagentc] = Location(RepairTool.Reagentc)
    };

    public RepairToolLocation Resolve(RepairTool tool) => Locations[tool];

    private static RepairToolLocation Location(RepairTool tool) =>
        new(tool, true, Path.Combine("C:\\Windows\\System32", RepairToolNames.Executable(tool)),
            RepairToolNames.Executable(tool), null, ["Caminho sintético de teste."]);
}

internal sealed class CountingRepairExecutor(IRepairExecutor inner) : IRepairExecutor
{
    public int Calls { get; private set; }

    public Task<RepairExecutionOutcome> ExecuteAsync(RepairExecutionRequest request,
        RepairExecutionOptions options, IProgress<RepairProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return inner.ExecuteAsync(request, options, progress, cancellationToken);
    }
}

internal sealed class FailingRepairStore : IRepairRecordStore
{
    private readonly IRepairRecordStore inner;
    public bool FailIntent { get; set; }
    public bool FailRecord { get; set; }
    public bool FailRecordOnce { get; set; }

    public FailingRepairStore(IRepairRecordStore inner) => this.inner = inner;

    public string GetRepairReportPath(Guid repairId) => inner.GetRepairReportPath(repairId);

    public Task SaveRepairIntentAsync(RepairIntent intent, CancellationToken cancellationToken = default) =>
        FailIntent
            ? Task.FromException(new IOException("Falha sintética ao gravar a intenção."))
            : inner.SaveRepairIntentAsync(intent, cancellationToken);

    public Task<RepairRecord> SaveRepairRecordAsync(RepairRecord record,
        CancellationToken cancellationToken = default)
    {
        if (FailRecord)
            return Task.FromException<RepairRecord>(new IOException("Falha sintética ao gravar o registro."));
        if (FailRecordOnce)
        {
            FailRecordOnce = false;
            return Task.FromException<RepairRecord>(new IOException("Falha sintética transitória."));
        }
        return inner.SaveRepairRecordAsync(record, cancellationToken);
    }

    public Task<RepairHistory> LoadRepairsAsync(CancellationToken cancellationToken = default) =>
        inner.LoadRepairsAsync(cancellationToken);

    public Task<RepairRecord?> FindRepairAsync(Guid repairId, CancellationToken cancellationToken = default) =>
        inner.FindRepairAsync(repairId, cancellationToken);
}

internal sealed class RepairScope : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "SentinelCoreR1Tests",
        Guid.NewGuid().ToString("N"));
    public TestTimeProvider Time { get; }
    public MemoryStore Store { get; }
    public RepairCatalog Catalog { get; } = RepairCatalog.CreateDefault();
    public FakeRepairProfile Profile { get; } = new();
    public FakeRepairVolumes Volumes { get; } = new();
    public FakeRepairToolLocator Tools { get; } = new();
    public CountingRepairExecutor Executor { get; }
    public RepairCenter Center { get; }
    public RepairManager Manager { get; }

    public RepairScope(IRepairRecordStore? recordStore = null)
    {
        Time = new TestTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        Store = new MemoryStore(Root);
        Executor = new CountingRepairExecutor(new SyntheticRepairExecutor(Time));
        var preflight = new RepairPreflightEngine(Catalog, Profile, Volumes, Tools, Time);
        Center = new RepairCenter(Catalog, preflight, Executor, recordStore ?? Store, Time,
            TimeSpan.FromMinutes(2));
        Manager = new RepairManager(Catalog, Center, Volumes);
    }

    public async Task<(RepairPlan Plan, RepairAuthorizationToken Token)> PrepareAsync(string operationId,
        string? volumeId = null, string? confirmation = null)
    {
        var plan = await Center.PrepareAsync(operationId, volumeId);
        if (!plan.SimulationAllowed)
            throw new InvalidOperationException("Plano bloqueado: " +
                string.Join(",", plan.Preflight.SimulationBlockers));
        var token = Center.Authorize(plan,
            confirmation ?? RepairManager.BuildConfirmationText(plan));
        return (plan, token);
    }

    public async Task<RepairExecutionResult> ExecuteAsync(string operationId, string? volumeId = null,
        RepairFixtureScenario scenario = RepairFixtureScenario.SuccessNoIssues,
        CancellationToken cancellationToken = default)
    {
        var (plan, token) = await PrepareAsync(operationId, volumeId);
        return await Center.ExecuteAsync(plan, token, scenario, null, cancellationToken);
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
