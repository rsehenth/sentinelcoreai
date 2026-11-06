using System.Text.Json;
using SentinelCore.Domain;
using SentinelCore.Persistence;

// Closed set of audited actions. Exit codes: 2 invalid use, 3 IO/contention, 4 invalid data, 5 target locked,
// 6 recovery reader reported every source unavailable.
var pauseStages = new Dictionary<string, AtomicWriteStage>
{
    ["pause-before-temp"] = AtomicWriteStage.BeforeTemporary,
    ["pause-during-temp"] = AtomicWriteStage.DuringTemporaryWrite,
    ["pause-before-publish"] = AtomicWriteStage.BeforePublish,
    ["pause-after-publish"] = AtomicWriteStage.AfterPublish
};
if (args.Length != 2 || !Guid.TryParseExact(args[0], "N", out var id) ||
    !(args[1] is "hold" or "append" or "append-conflict" or "append-hold" or "append-next" or "partial" or "read"
        or "read-recovery" or "target-a" or "target-b" || pauseStages.ContainsKey(args[1]))) return 2;
var root = Path.Combine(Path.GetTempPath(), "SentinelCoreT4B2", id.ToString("N"));
if (!Directory.Exists(root)) return 2;
try
{
    if (args[1] is "target-a" or "target-b")
    {
        Console.WriteLine("READY");
        await Console.In.ReadLineAsync();
        using var held = new FileExclusiveLockFactory(root).Acquire("Synthetic:fixture:" + args[1]);
        Console.WriteLine("HELD");
        await Console.In.ReadLineAsync();
        return 0;
    }
    if (args[1] == "hold")
    {
        Directory.CreateDirectory(Path.Combine(root, "data"));
        using var held = new FileStream(Path.Combine(root, "data", "store.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Console.WriteLine("READY");
        await Console.In.ReadLineAsync();
        return 0;
    }
    TransactionEventRecord? entry = null;
    if (args[1] is not ("read" or "read-recovery"))
    {
        var file = args[1] == "append-conflict" ? "conflict.json" :
            args[1] == "append-next" || pauseStages.ContainsKey(args[1]) ? "next.json" : "event.json";
        entry = JsonSerializer.Deserialize<TransactionEventRecord>(
            await File.ReadAllTextAsync(Path.Combine(root, file)), AuditJson.Options);
        if (entry?.OperationId != "SYN-PREF-01" || entry.Plan is null && entry.Sequence == 1 ||
            entry.Plan?.Changes.Any(c => c.Target.Root != "Synthetic") == true) return 2;
    }
    Console.WriteLine("READY");
    await Console.In.ReadLineAsync();
    var store = new MemoryStore(root);
    if (args[1] == "partial")
    {
        var journal = Path.Combine(root, "memory", "transactions", entry!.TransactionId.ToString("D"), "journal");
        if (!Directory.Exists(journal)) return 2;
        using var held = new FileStream(Path.Combine(root, "data", "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var partial = new FileStream(Path.Combine(journal, "0002-PreflightPassed.md.fixture.tmp"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        partial.Write("fixture parcial"u8);
        partial.Flush(true);
        Console.WriteLine("PARTIAL");
        await Console.In.ReadLineAsync();
        return 0;
    }
    if (args[1] == "read-recovery")
    {
        var sources = await store.ReadRecoveryHistoryAsync();
        Console.WriteLine("RECOVERY:" + string.Join(",", sources.Select(s => s.Status)));
        return sources.All(s => s.Status == RecoveryEvidenceStatus.Unavailable) ? 6 : 0;
    }
    if (pauseStages.TryGetValue(args[1], out var stage))
    {
        // Blocks once, inside the real AtomicWrite of the event itself (later projection writes pass through),
        // holding store.lock, until the controller sends a line or kills us.
        var fired = false;
        RecordEnvelope.Probe.Value = reached =>
        {
            if (reached != stage || fired) return;
            fired = true;
            Console.WriteLine("AT:" + reached);
            Console.In.ReadLine();
        };
    }
    if (entry is not null)
    {
        await store.AppendEventAsync(entry);
        if (args[1] == "append-hold")
        {
            Console.WriteLine("PERSISTED");
            await Console.In.ReadLineAsync();
        }
    }
    else await store.LoadTransactionsAsync();
    return 0;
}
catch (InvalidDataException) { return 4; }
catch (TargetLockedException) { return 5; }
catch (IOException) { return 3; }
