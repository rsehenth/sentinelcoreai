using System.IO;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Debloat;
using SentinelCore.Infrastructure.Modifications;
using SentinelCore.Infrastructure.Repairs;
using SentinelCore.Persistence;

namespace RecoveryBenchmark;

/// <summary>
/// Generates only through the existing synthetic engines (SyntheticTargetStore, synthetic Debloat/T3C/R1 adapters),
/// so every record respects the persisted contracts. Nothing here touches Windows, the Registry or installed packages.
/// </summary>
internal static class Fixtures
{
    public static readonly Dictionary<string, int> Errors = new();
    private static readonly HashSet<string> Examples = new(StringComparer.Ordinal) { "Clipchamp.Clipchamp", "Microsoft.BingWeather",
        "Microsoft.WindowsCalculator", "Microsoft.WindowsNotepad", "Microsoft.MicrosoftSolitaireCollection", "Microsoft.Windows.Photos",
        "Microsoft.WindowsCamera", "Microsoft.YourPhone" };
    private static void Log(Exception ex)
    {
        var key = ex.GetType().Name + ": " + (ex.Message.Length > 120 ? ex.Message[..120] : ex.Message);
        Errors[key] = Errors.GetValueOrDefault(key) + 1;
    }

    public sealed record Composition(int Requested, int Tweaks, int TweakRecoveries, int Debloat, int Removal, int Repairs,
        int GenerationFailures, int SkippedBlockedTargets, int DebloatEntries, int RemovalEntries, double Seconds);

    public static async Task<Composition> GenerateAsync(string root, int count, int seed, string kinds = "tdrp")
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var random = new Random(seed);
        var store = new MemoryStore(root);
        var locks = new FileExclusiveLockFactory(root);
        var failures = 0;
        var tweaks = (int)(count * 0.40); var debloat = (int)(count * 0.20);
        var removal = (int)(count * 0.15); var repairs = count - tweaks - debloat - removal;
        int doneTweaks = 0, doneRecoveries = 0, doneDebloat = 0, doneRemoval = 0, doneRepairs = 0, skippedBlocked = 0;

        // Tweaks (synthetic target store only) + synthetic recovery attempts (conflict, full and partial).
        var catalog1 = T1SyntheticCatalog.Create();
        var a = new OperationTarget("Synthetic", "T4b3.A", "Value"); var b = new OperationTarget("Synthetic", "T4b3.B", "Value");
        var template = catalog1.Get("SYN-PREF-01");
        var partial = template with { Id = "SYN-T4B3-PARTIAL", Targets = [a, b],
            Changes = [new(a, new(true, "String", "after")), new(b, new(true, "String", "after"))] };
        var catalog = new OperationCatalog("t4b3-1", T1SyntheticCatalog.Operations().Append(partial).ToArray());
        var targets = new SyntheticTargetStore();
        var executor = new SyntheticOperationExecutor(targets);
        var engine = new SafetyEngine(catalog, new SyntheticSystemProfileReader(SyntheticSystemProfileReader.Default(DateTimeOffset.UtcNow)),
            targets, new SyntheticProtectionProvider(), store, executor, locks);
        var recovery = new RecoveryService(store, targets, executor, locks, catalog: catalog, syntheticOnly: true);
        for (var i = 0; i < (kinds.Contains('t') ? tweaks : 0); i++)
        {
            try
            {
                var kind = random.Next(100);
                var id = kind < 8 ? "SYN-T4B3-PARTIAL" : new[] { "SYN-PREF-01", "SYN-PREF-02" }[random.Next(2)];
                var op = catalog.Get(id);
                foreach (var t in op.Targets) targets.Set(t, new(true, "String", "before"));
                var plan = await engine.SimulateAsync(id);
                var result = await engine.ExecuteAsync(plan.Plan, engine.Authorize(plan, "t4b3-" + i));
                if (result.Transaction is null) { skippedBlocked++; continue; }
                doneTweaks++;
                if (kind < 8 && result.Transaction is { } t1)
                {
                    targets.Set(a, new(true, "String", "third-party")); // conflict -> partial recovery
                    await recovery.RollbackAsync(t1.TransactionId, new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(t1.TransactionId)));
                    doneRecoveries++;
                }
                else if (kind < 20 && result.Transaction is { } t2)
                {
                    foreach (var t in op.Targets) targets.Set(t, new(true, "String", "third-party")); // pure conflict
                    await recovery.RollbackAsync(t2.TransactionId, new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(t2.TransactionId)));
                    doneRecoveries++;
                }
                else if (kind < 35 && result.Transaction is { } t3)
                {
                    await recovery.RollbackAsync(t3.TransactionId, new RecoveryOptions(TweakManager.BuildRecoveryConfirmationText(t3.TransactionId)));
                    doneRecoveries++;
                }
            }
            catch (Exception ex) { failures++; Log(ex); }
        }

        // Debloat (T3) and T3C removal share the DBLT journal and the "Debloat.<entry>" target namespace. A non-success
        // scenario leaves that target persistently blocked (real, intended behaviour), so each entry takes at most one such
        // history per root and later operations on it are skipped (counted, never faked).
        var debloatCatalog = DebloatCatalog.CreateDefault();
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        var scenarioMix = new[] { DebloatScenario.Success, DebloatScenario.Success, DebloatScenario.Success, DebloatScenario.Success,
            DebloatScenario.Success, DebloatScenario.Success, DebloatScenario.PartialFailure, DebloatScenario.Interrupted,
            DebloatScenario.Indeterminate, DebloatScenario.Cancelled, DebloatScenario.InUse, DebloatScenario.UpdatedBeforeExecution,
            DebloatScenario.Unverifiable, DebloatScenario.AccessDenied };
        var debloatEntries = new List<DebloatCatalogEntry>();
        foreach (var candidate in debloatCatalog.All.Where(e => Examples.Contains(e.TechnicalName)))
        {
            try
            {
                var probeInventory = SyntheticDebloatInventory.Create(debloatCatalog.All);
                var probe = new DebloatManager(debloatCatalog, probeInventory, new SyntheticDebloatRuntimeFactory(probeInventory),
                    new MemoryStore(Path.Combine(Path.GetTempPath(), "t4b3-probe-" + Guid.NewGuid().ToString("N"))).CreateDebloatJournal(), locks);
                await probe.PrepareAsync(candidate.Id);
                debloatEntries.Add(candidate);
            }
            catch (Exception) { /* not an operational synthetic definition */ }
        }
        for (var i = 0; i < (kinds.Contains('d') ? debloat : 0); i++)
        {
            try
            {
                var free = debloatEntries.Where(e => !blocked.Contains(e.Id)).ToArray();
                if (free.Length == 0) { skippedBlocked++; continue; }
                var entry = free[random.Next(free.Length)];
                var scenario = random.Next(100) < 88 ? DebloatScenario.Success : scenarioMix[random.Next(scenarioMix.Length)];
                var inventory = SyntheticDebloatInventory.Create(debloatCatalog.All); // stateful: a removal consumes the fixture package
                var dm = new DebloatManager(debloatCatalog, inventory, new SyntheticDebloatRuntimeFactory(inventory), store.CreateDebloatJournal(), locks);
                var plan = await dm.PrepareAsync(entry.Id, scenario);
                var result = await dm.ExecuteAsync(plan, dm.Authorize(plan, plan.Confirmation));
                if (result.Transaction.Transaction is null) { skippedBlocked++; continue; }
                doneDebloat++;
                if (!result.Succeeded) blocked.Add(entry.Id);
            }
            catch (Exception ex) { failures++; Log(ex); }
        }

        var removalScenarios = new[] { DebloatRemovalScenario.Success, DebloatRemovalScenario.Success, DebloatRemovalScenario.Success,
            DebloatRemovalScenario.Success, DebloatRemovalScenario.Success, DebloatRemovalScenario.AccessDenied, DebloatRemovalScenario.PartialFailure,
            DebloatRemovalScenario.Interrupted, DebloatRemovalScenario.Indeterminate, DebloatRemovalScenario.Unverifiable,
            DebloatRemovalScenario.PostVerificationFailed };
        DebloatRemovalObservation Observe(DebloatCatalogEntry e)
        {
            var package = new DebloatNativePackageMetadata(e.TechnicalName, e.TechnicalName + "_fixture000000",
                e.TechnicalName + "_1.0.0.0_x64__fixture000000", DebloatFixtureIdentity.Publisher, "fixture000000", "1.0.0.0", "x64",
                DebloatFixtureIdentity.User, DebloatPackageKind.Appx, DebloatScope.CurrentUser, DebloatNativeInstallState.Available,
                false, false, false, true, false, false, "SyntheticRemovalFixture", []);
            return new DebloatRemovalObservation(true, true, 26100, true, DebloatFixtureIdentity.User,
                e.Id, DebloatCatalog.Version, DebloatRemovalHashing.Hash(debloatCatalog.All), "fixture-revision-1",
                DebloatInventoryQueryState.Completed, DebloatObservationCompleteness.Complete, [package],
                DebloatRemovalIdentityPolicy.From(package), false, false,
                new(DebloatObservationCompleteness.Complete, [], [], [], [], [], true, []), true,
                e.ReinstallInformation, true, [e.Consequence, e.DataLossWarning]);
        }
        var removalEntries = new List<DebloatCatalogEntry>();
        foreach (var candidate in debloatCatalog.All.Where(e => Examples.Contains(e.TechnicalName)))
        {
            try
            {
                var adapter = new SyntheticDebloatRemovalAdapter(Observe(candidate));
                var probe = new DebloatRemovalManager(debloatCatalog, adapter, adapter,
                    new MemoryStore(Path.Combine(Path.GetTempPath(), "t4b3-probe-" + Guid.NewGuid().ToString("N"))).CreateDebloatJournal(), locks);
                await probe.PrepareAsync();
                removalEntries.Add(candidate);
            }
            catch (Exception) { /* preflight refuses: not part of the operational synthetic subset */ }
        }
        for (var i = 0; i < (kinds.Contains('r') ? removal : 0); i++)
        {
            try
            {
                var free = removalEntries.Where(e => !blocked.Contains(e.Id)).ToArray();
                if (free.Length == 0) { skippedBlocked++; continue; }
                var entry = free[random.Next(free.Length)];
                var adapter = new SyntheticDebloatRemovalAdapter(Observe(entry));
                var manager = new DebloatRemovalManager(debloatCatalog, adapter, adapter, store.CreateDebloatJournal(), locks);
                var plan = await manager.PrepareAsync(random.Next(100) < 88 ? DebloatRemovalScenario.Success : removalScenarios[random.Next(removalScenarios.Length)]);
                var result = await manager.ExecuteAsync(plan, manager.Authorize(plan, plan.Confirmation));
                if (result.Transaction.Transaction is null) { skippedBlocked++; continue; }
                doneRemoval++;
                if (!result.Succeeded) blocked.Add(entry.Id);
            }
            catch (Exception ex) { failures++; Log(ex); }
        }

        // R1 repairs (SyntheticRepairExecutor only; no sfc/dism/chkdsk/reagentc is ever started).
        var repairCatalog = RepairCatalog.CreateDefault();
        var center = new RepairCenter(repairCatalog, new RepairPreflightEngine(repairCatalog, new FixtureProfile(), new FixtureVolumes(),
            new FixtureTools()), new SyntheticRepairExecutor(), store, () => @"C:FixtureSER");
        var ops = new[] { RepairCatalog.SfcVerifyId, RepairCatalog.DismCheckHealthId, RepairCatalog.ChkdskCheckId, RepairCatalog.WinReInfoId };
        var repairScenarios = new[] { RepairFixtureScenario.SuccessNoIssues, RepairFixtureScenario.SuccessNoIssues, RepairFixtureScenario.CorruptionFound,
            RepairFixtureScenario.RepairPartial, RepairFixtureScenario.RepairFailed, RepairFixtureScenario.AccessDenied,
            RepairFixtureScenario.RestartRequired, RepairFixtureScenario.IncompleteOutput };
        for (var i = 0; i < (kinds.Contains('p') ? repairs : 0); i++)
        {
            try
            {
                var id = ops[random.Next(ops.Length)];
                var plan = await center.PrepareAsync(id, id == RepairCatalog.ChkdskCheckId ? "C:" : null);
                var repairResult = await center.ExecuteAsync(plan, center.Authorize(plan, RepairManager.BuildConfirmationText(plan)),
                    repairScenarios[random.Next(repairScenarios.Length)], null);
                if (repairResult.Record is null) { skippedBlocked++; continue; }
                doneRepairs++;
            }
            catch (Exception ex) { failures++; Log(ex); }
        }
        return new(count, doneTweaks, doneRecoveries, doneDebloat, doneRemoval, doneRepairs, failures, skippedBlocked, debloatEntries.Count, removalEntries.Count, started.Elapsed.TotalSeconds);
    }

    /// <summary>Copies the clean fixture and damages a deterministic sample: tampered payloads and deleted evidence files.</summary>
    public static (int Tampered, int Deleted) Corrupt(string cleanRoot, string damagedRoot, int seed)
    {
        CopyDirectory(cleanRoot, damagedRoot);
        var random = new Random(seed);
        var files = Directory.EnumerateFiles(Path.Combine(damagedRoot, "memory"), "*.md", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith("RECOVERY", StringComparison.Ordinal) &&
                        !Path.GetFileName(f).StartsWith("DEBLOAT", StringComparison.Ordinal) &&
                        !Path.GetFileName(f).StartsWith("REPAIRS", StringComparison.Ordinal)).ToArray();
        int tampered = 0, deleted = 0;
        foreach (var file in files)
        {
            var roll = random.Next(1000);
            if (roll < 15) { File.WriteAllText(file, File.ReadAllText(file).Replace("\"", "'", StringComparison.Ordinal)); tampered++; }
            else if (roll < 20) { File.Delete(file); deleted++; }
        }
        return (tampered, deleted);
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}

internal sealed class FixtureProfile : IRepairSystemProfileReader
{
    public RepairSystemProfile Read() => new("Synthetic Windows 11", 22631, true, false, true,
        "T4B.3 fixture", DateTimeOffset.UtcNow, ["SINTÉTICO — sem consulta real."]);
}
internal sealed class FixtureVolumes : IRepairVolumeProvider
{
    public Task<IReadOnlyList<RepairVolume>> GetVolumesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RepairVolume>>([new("C:", "C:\\", "NTFS", "Fixed", true,
            1000, 500, true, true, "C: SINTÉTICO", ["Fixture; nenhum disco real consultado."])]);
}
internal sealed class FixtureTools : IRepairToolLocator
{
    public RepairToolLocation Resolve(RepairTool tool) => new(tool, true,
        Path.Combine("C:\\Windows\\System32", RepairToolNames.Executable(tool)), RepairToolNames.Executable(tool),
        null, ["Fixture; nenhum executável real é consultado ou iniciado."]);
}
