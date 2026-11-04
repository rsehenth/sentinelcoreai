using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Debloat;
using SentinelCore.Persistence;

namespace SentinelCore.Tests;

internal sealed class DebloatRemovalTestScope : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "SentinelCoreRemovalTests", Guid.NewGuid().ToString("N"));
    public DebloatCatalog Catalog { get; } = DebloatCatalog.CreateDefault();
    public TestTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
    public SyntheticDebloatRemovalAdapter Adapter { get; }
    public MemoryStore Store { get; }
    public ITransactionJournalStore Journal { get; }
    public DebloatRemovalManager Manager { get; }

    public DebloatRemovalTestScope(string technicalName = "Clipchamp.Clipchamp")
    {
        var entry = Catalog.All.Single(e => e.TechnicalName == technicalName);
        var package = new DebloatNativePackageMetadata(entry.TechnicalName, entry.TechnicalName + "_fixture000000",
            entry.TechnicalName + "_1.0.0.0_x64__fixture000000", DebloatFixtureIdentity.Publisher,
            "fixture000000", "1.0.0.0", "x64", DebloatFixtureIdentity.User, DebloatPackageKind.Appx,
            DebloatScope.CurrentUser, DebloatNativeInstallState.Available, false,
            false, false, true, false, false, "SyntheticRemovalFixture", []);
        var observation = new DebloatRemovalObservation(true, true, 26100, true, DebloatFixtureIdentity.User,
            entry.Id, DebloatCatalog.Version, DebloatRemovalHashing.Hash(Catalog.All), "fixture-revision-1",
            DebloatInventoryQueryState.Completed, DebloatObservationCompleteness.Complete, [package],
            DebloatRemovalIdentityPolicy.From(package), false, false,
            new(DebloatObservationCompleteness.Complete, [], [], [], [], [], true, []),
            true, entry.ReinstallInformation, true, [entry.Consequence, entry.DataLossWarning]);
        Adapter = new(observation);
        Store = new(Root); Journal = Store.CreateDebloatJournal(); Manager = Create();
    }

    public DebloatRemovalManager Create(ITransactionJournalStore? journal = null,
        IDebloatRemovalObservationSource? source = null, IDebloatRemovalExecutor? executor = null) =>
        new(Catalog, source ?? Adapter, executor ?? Adapter, journal ?? Journal, new FileExclusiveLockFactory(Root), Time);
    public Task<DebloatRemovalPlan> Prepare(DebloatRemovalScenario scenario = DebloatRemovalScenario.Success) => Manager.PrepareAsync(scenario);
    public AuthorizationToken Authorize(DebloatRemovalPlan plan) => Manager.Authorize(plan, plan.Confirmation);
    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

internal sealed class RemovalJournalSpy(ITransactionJournalStore inner) : ITransactionJournalStore
{
    public TransactionState? FailBefore { get; set; }
    public Action<TransactionEventRecord>? AfterAppend { get; set; }
    public bool FailBackup { get; set; }
    public string GetTransactionReportPath(Guid id) => inner.GetTransactionReportPath(id);
    public async Task<TransactionEventRecord> AppendEventAsync(TransactionEventRecord entry, CancellationToken token = default)
    {
        if (entry.State == FailBefore) throw new IOException("Fixture: journal indisponível.");
        var stored = await inner.AppendEventAsync(entry, token);
        AfterAppend?.Invoke(stored);
        return stored;
    }
    public Task<string> WriteBackupAsync(BackupRecord backup, CancellationToken token = default) =>
        FailBackup ? throw new IOException("Fixture: snapshot indisponível.") : inner.WriteBackupAsync(backup, token);
    public Task<BackupRecord> ReadBackupAsync(string path, CancellationToken token = default) => inner.ReadBackupAsync(path, token);
    public Task<TransactionJournalSnapshot> LoadTransactionsAsync(CancellationToken token = default) => inner.LoadTransactionsAsync(token);
    public Task<TransactionRecord?> FindTransactionAsync(Guid id, CancellationToken token = default) => inner.FindTransactionAsync(id, token);
}
