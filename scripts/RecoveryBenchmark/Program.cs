using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SentinelCore.Persistence;

namespace RecoveryBenchmark;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { return args.FirstOrDefault() switch
        {
            "wpf" => WpfHost.Run(args.Skip(1).ToArray()),
            "u1" => U1Host.Run(args.Skip(1).ToArray()),
            _ => MainAsync(args).GetAwaiter().GetResult()
        }; }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { Console.Error.WriteLine(error.Message); return 2; }
    }

    private static async Task<int> MainAsync(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "diagnose": return await HistoryDiagnosis.RunAsync(FixturePaths.Existing(args[1]));
            case "self-check": return FixturePaths.SelfCheck();
            case "init": FixturePaths.Create(args[1]); return 0;
            case "chunk": // chunk <root> <count> <seed>: generate one valid synthetic history through the synthetic engines
            {
                var count = int.Parse(args[2]);
                if (count is < 1 or > 25) throw new ArgumentException("A chunk must contain 1–25 operations.");
                var composition = await Fixtures.GenerateAsync(FixturePaths.Create(args[1]), count, int.Parse(args[3]), args.Length > 4 ? args[4] : "tdrp");
                Console.WriteLine(JsonSerializer.Serialize(new { composition, Fixtures.Errors }));
                return 0;
            }
            case "merge": // merge <dest> <chunk...>: copy durable history trees, then reconcile projections/SQLite with the real store
            {
                var destination = FixturePaths.New(args[1]);
                // merge <dest> --parent <dir>: usa todos os "chunk-*" do diretorio (evita o limite de 32.767 caracteres da linha de comando)
                var chunkRoots = args.Length > 3 && args[2] == "--parent" ? Directory.GetDirectories(FixturePaths.Existing(args[3]), "chunk-*").OrderBy(x => x, StringComparer.Ordinal).ToArray() : args.Skip(2).ToArray();
                foreach (var chunk in chunkRoots) { FixturePaths.Existing(chunk); FixturePaths.Separate(chunk, destination); }
                FixturePaths.Create(destination);
                foreach (var chunk in chunkRoots)
                    foreach (var partition in new[] { "memory", "reports" })
                        foreach (var kind in new[] { "transactions", "debloat", "repairs" })
                        {
                            var source = Path.Combine(chunk, partition, kind);
                            if (Directory.Exists(source)) Fixtures.CopyDirectory(source, Path.Combine(destination, partition, kind));
                        }
                var store = new MemoryStore(destination);
                await store.LoadTransactionsAsync();
                await store.CreateDebloatJournal().LoadTransactionsAsync();
                await store.LoadRepairsAsync();
                Console.WriteLine("merged " + args.Length);
                return 0;
            }
            case "bench": // bench <root> <label> [iterations]
                return await Bench.RunAsync(FixturePaths.Existing(args[1]), args[2], args.Length > 3 ? int.Parse(args[3]) : 5);
            case "damage": // damage <clean> <damaged> <seed>
            {
                FixturePaths.Separate(args[1], args[2]);
                var (tampered, deleted) = Fixtures.Corrupt(FixturePaths.Existing(args[1]), FixturePaths.New(args[2]), int.Parse(args[3]));
                Console.WriteLine(JsonSerializer.Serialize(new { tampered, deleted }));
                return 0;
            }
            default: Console.Error.WriteLine("uso: chunk|merge|damage ..."); return 2;
        }
    }
}
