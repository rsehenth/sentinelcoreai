using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;

namespace RecoveryBenchmark;

/// <summary>Read-only measurement of the existing Recovery Center pipeline over an already generated synthetic root.</summary>
internal static class Bench
{
    private sealed class PreloadedReader(MemoryStore store, IReadOnlyList<RecoveryHistorySource> sources) : IRecoveryHistoryReader
    {
        public Task<IReadOnlyList<RecoveryHistorySource>> ReadRecoveryHistoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sources);
        public string GetRecoveryReportPath(RecoveryModule module, Guid id) => store.GetRecoveryReportPath(module, id);
    }

    private static double Ms(Stopwatch watch) => watch.Elapsed.TotalMilliseconds;
    private static object Stats(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        return new { first = Math.Round(values[0], 2), median = Math.Round(median, 2), min = Math.Round(sorted[0], 2),
            max = Math.Round(sorted[^1], 2), n = values.Count };
    }

    private sealed record Print(int Files, long Bytes, long Newest);
    private static Print Fingerprint(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToArray();
        return new Print(files.Length, files.Sum(f => f.Length), files.Max(f => f.LastWriteTimeUtc.Ticks));
    }

    public static async Task<int> RunAsync(string root, string label, int iterations)
    {
        var before = Fingerprint(root);
        var mdFiles = Directory.EnumerateFiles(Path.Combine(root, "memory"), "*.md", SearchOption.AllDirectories).Count() +
                      Directory.EnumerateFiles(Path.Combine(root, "reports"), "*.md", SearchOption.AllDirectories).Count();

        // I/O floor: read every durable Markdown file once with no validation or parsing.
        var io = new List<double>();
        for (var i = 0; i < iterations; i++)
        {
            var watch = Stopwatch.StartNew(); long total = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)) total += File.ReadAllBytes(file).Length;
            io.Add(Ms(watch));
        }

        var read = new List<double>(); var map = new List<double>(); var load = new List<double>(); var sort = new List<double>();
        IReadOnlyList<RecoveryHistorySource>? sources = null; RecoveryCenterSnapshot? snapshot = null;
        long allocatedLoad = 0; long retained = 0;
        for (var i = 0; i < iterations; i++)
        {
            var store = new MemoryStore(root);
            var watch = Stopwatch.StartNew();
            sources = await store.ReadRecoveryHistoryAsync();
            read.Add(Ms(watch));

            var preloaded = new RecoveryCenter(new PreloadedReader(store, sources));
            watch.Restart();
            snapshot = await preloaded.LoadAsync();
            map.Add(Ms(watch));

            var gcBefore = GC.GetTotalMemory(true);
            var allocBefore = GC.GetTotalAllocatedBytes(true);
            var full = new RecoveryCenter(new MemoryStore(root));
            watch.Restart();
            var fullSnapshot = await full.LoadAsync();
            load.Add(Ms(watch));
            allocatedLoad = GC.GetTotalAllocatedBytes(true) - allocBefore;
            retained = GC.GetTotalMemory(true) - gcBefore;
            GC.KeepAlive(fullSnapshot);

            watch.Restart();
            _ = snapshot.Operations.OrderByDescending(o => o.AtUtc).ThenBy(o => o.Module).ThenBy(o => o.Id).ToArray();
            sort.Add(Ms(watch));
        }

        // Search and filters over the already-loaded snapshot, 30 repetitions each.
        var someId = snapshot!.Operations.Count > 0 ? snapshot.Operations[snapshot.Operations.Count / 2].Id.ToString("D")[..8] : "x";
        var queries = new Dictionary<string, RecoveryCenterFilter>
        {
            ["no-filter"] = new(),
            ["search-common-word(sintético)"] = new(Search: "sintético"),
            ["search-id-prefix"] = new(Search: someId),
            ["search-no-match"] = new(Search: "zzz-sem-resultado"),
            ["module=Repair"] = new(Module: RecoveryModule.WindowsRepair),
            ["combined(module+outcome+conflict-free+search)"] = new(Search: "sintético", Module: RecoveryModule.Tweaks,
                Outcome: RecoveryOperationOutcome.Completed, Conflict: false)
        };
        var filterStats = new Dictionary<string, object>();
        foreach (var (name, filter) in queries)
        {
            var samples = new List<double>(); var hits = 0;
            for (var i = 0; i < 30; i++)
            {
                var watch = Stopwatch.StartNew();
                hits = RecoveryCenter.Filter(snapshot, filter).Count;
                samples.Add(Ms(watch));
            }
            filterStats[name] = new { hits, ms = Stats(samples) };
        }
        var summarize = new List<double>();
        for (var i = 0; i < 30; i++) { var watch = Stopwatch.StartNew(); _ = RecoveryCenter.Summarize(snapshot); summarize.Add(Ms(watch)); }

        // Cancellation behaviour: pre-cancelled token, and cancellation requested at ~25% of the median read time.
        var cancelStats = new Dictionary<string, object>();
        using (var pre = new CancellationTokenSource())
        {
            pre.Cancel();
            var watch = Stopwatch.StartNew(); var outcome = "completed";
            try { await new MemoryStore(root).ReadRecoveryHistoryAsync(pre.Token); } catch (OperationCanceledException) { outcome = "OperationCanceledException"; }
            cancelStats["pre-cancelled"] = new { outcome, ms = Math.Round(Ms(watch), 2) };
        }
        var medianRead = read.OrderBy(v => v).ElementAt(read.Count / 2);
        var late = new List<double>(); var cancelOutcomes = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            using var cts = new CancellationTokenSource();
            var task = new MemoryStore(root).ReadRecoveryHistoryAsync(cts.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, medianRead * 0.25)));
            var watch = Stopwatch.StartNew(); cts.Cancel();
            try { await task; cancelOutcomes.Add("completed-before-cancel-observed"); } catch (OperationCanceledException) { cancelOutcomes.Add("cancelled"); }
            late.Add(Ms(watch));
        }
        cancelStats["cancel-at-25%-of-read"] = new { latencyAfterCancelMs = Stats(late), outcomes = cancelOutcomes.GroupBy(o => o).ToDictionary(g => g.Key, g => g.Count()) };

        var after = Fingerprint(root);
        var process = Process.GetCurrentProcess();
        var result = new
        {
            label, root, iterations, mdFiles,
            operations = snapshot.Operations.Count,
            byModule = snapshot.Operations.GroupBy(o => o.Module.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            byOutcome = snapshot.Operations.GroupBy(o => o.Outcome.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            conflicts = snapshot.Operations.Count(o => o.HasConflict), intervention = snapshot.Operations.Count(o => o.RequiresIntervention),
            sourceStatus = snapshot.Sources.ToDictionary(s => s.Module.ToString(), s => s.Status.ToString()),
            warnings = snapshot.Warnings.Count, sourceWarnings = snapshot.Sources.Sum(s => s.Warnings.Count), isComplete = snapshot.IsComplete,
            timesMs = new
            {
                ioFloorReadAllMarkdown = Stats(io),
                readAndValidateHistory = Stats(read),
                mapDedupSortOnly = Stats(map),
                loadTotal = Stats(load),
                sortOnly = Stats(sort),
                summarize = Stats(summarize),
                firstResultsProgressive = "não suportado: LoadAsync entrega o snapshot completo; primeiro resultado = loadTotal"
            },
            filtersMs = filterStats,
            cancellation = cancelStats,
            memory = new
            {
                allocatedDuringLoadMiB = Math.Round(allocatedLoad / 1048576.0, 1),
                retainedSnapshotMiB = Math.Round(retained / 1048576.0, 1),
                peakWorkingSetMiB = Math.Round(process.PeakWorkingSet64 / 1048576.0, 1),
                peakPagedMiB = Math.Round(process.PeakPagedMemorySize64 / 1048576.0, 1)
            },
            readOnlyProof = new { before, after, unchanged = before == after }
        };
        Console.WriteLine(JsonSerializer.Serialize(result));
        return before == after ? 0 : 3;
    }
}
