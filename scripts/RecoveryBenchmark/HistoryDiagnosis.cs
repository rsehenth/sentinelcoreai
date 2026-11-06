using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;

namespace RecoveryBenchmark;

/// <summary>Small, read-only U1 probe. No cache eviction, Windows collectors or large fixture generation.</summary>
internal static class HistoryDiagnosis
{
    private sealed class Preloaded(MemoryStore store, IReadOnlyList<RecoveryHistorySource> sources) : IRecoveryHistoryReader
    {
        public Task<IReadOnlyList<RecoveryHistorySource>> ReadRecoveryHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult(sources);
        public string GetRecoveryReportPath(RecoveryModule module, Guid id) => store.GetRecoveryReportPath(module, id);
    }

    public static async Task<int> RunAsync(string root)
    {
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        // Metadata does not prove content equality, but avoids warming all file contents before the first raw read.
        string[] Metadata() => files.OrderBy(f => f, StringComparer.Ordinal).Select(f =>
            $"{Path.GetRelativePath(root, f)}|{new FileInfo(f).Length}|{File.GetLastWriteTimeUtc(f).Ticks}").ToArray();
        var before = Metadata();
        var markdown = files.Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (markdown.Length > 1000) throw new ArgumentException("U1 diagnosis is limited to 1,000 Markdown files.");
        var raw = new List<object>();
        for (var pass = 0; pass < 3; pass++)
        {
            var total = Stopwatch.StartNew();
            var timings = new List<(string Path, double Ms)>();
            long bytes = 0;
            foreach (var file in markdown)
            {
                var watch = Stopwatch.StartNew();
                bytes += File.ReadAllBytes(file).Length;
                timings.Add((Path.GetRelativePath(root, file), watch.Elapsed.TotalMilliseconds));
            }
            raw.Add(new { pass, ms = total.Elapsed.TotalMilliseconds, bytes,
                slowest = timings.OrderByDescending(t => t.Ms).Take(5).Select(t => new { t.Path, t.Ms }).ToArray() });
        }
        var store = new MemoryStore(root);
        var timer = Stopwatch.StartNew();
        var sources = await store.ReadRecoveryHistoryAsync();
        var validationMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var snapshot = await new RecoveryCenter(new Preloaded(store, sources)).LoadAsync();
        var mappingMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        await new RecoveryCenter(new MemoryStore(root)).LoadAsync();
        var fullWarmMs = timer.Elapsed.TotalMilliseconds;

        var lockPath = Path.Combine(root, "data", "store.lock");
        double writerBlockedMs, readerBlockedMs;
        bool writerRefused;
        IReadOnlyList<RecoveryHistorySource> blocked;
        using (var held = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            timer.Restart(); writerRefused = false;
            try { await store.LoadTransactionsAsync(); }
            catch (IOException) { writerRefused = true; }
            writerBlockedMs = timer.Elapsed.TotalMilliseconds;
        }
        using (var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            timer.Restart(); blocked = await store.ReadRecoveryHistoryAsync();
            readerBlockedMs = timer.Elapsed.TotalMilliseconds;
        }
        var unchanged = before.SequenceEqual(Metadata()) && files.Length == Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;
        var safeContention = writerRefused && blocked.All(s => s.Status == RecoveryEvidenceStatus.Unavailable);
        Console.WriteLine(JsonSerializer.Serialize(new { markdownFiles = markdown.Length, raw, validationMs, mappingMs, fullWarmMs,
            operations = snapshot.Operations.Count, warnings = snapshot.Warnings.Count, snapshot.IsComplete,
            contention = new { writerRefused, writerBlockedMs, readerBlockedMs, allSourcesUnavailable = safeContention },
            metadataUnchanged = unchanged, cache = "OS cache uncontrolled; path cache scoped to each read; no persistent snapshot cache" }));
        return unchanged && safeContention ? 0 : 3;
    }
}
