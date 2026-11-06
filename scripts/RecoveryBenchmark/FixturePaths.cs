using System.IO;

namespace RecoveryBenchmark;

// Guard added after the recorded measurements; it does not change the measured history pipeline.
internal static class FixturePaths
{
    internal const string Marker = ".sentinelcore-recovery-benchmark";
    private const string Ownership = "SentinelCore RecoveryBenchmark synthetic fixture v1";
    internal static string Parent => Path.Combine(Path.GetTempPath(), "SentinelCoreRecoveryBenchmark");

    internal static string Validate(string path)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.GetFullPath(Parent) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(full[prefix.Length..].Split(Path.DirectorySeparatorChar)[0], "N", out _))
            throw new IOException("Use only a fresh fixture below " + Parent + "\\<GUID-N>.");
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            CheckLink(current);
        return full;
    }

    private static void CheckLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse point refused: " + path);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    internal static string New(string path)
    {
        var full = Validate(path);
        if (Path.Exists(full)) throw new IOException("Existing destination refused: " + full);
        return full;
    }

    internal static string Create(string path)
    {
        var full = New(path);
        Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, Marker), Ownership);
        return full;
    }

    internal static void Separate(string source, string destination)
    {
        var left = Validate(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var right = Validate(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (left.StartsWith(right, StringComparison.OrdinalIgnoreCase) || right.StartsWith(left, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Overlapping source and destination refused.");
    }

    internal static string Existing(string path)
    {
        var full = Validate(path);
        CheckTree(full);
        if (File.ReadAllText(Path.Combine(full, Marker)) != Ownership)
            throw new IOException("Unowned fixture refused: " + full);
        return full;
    }

    private static void CheckTree(string directory)
    {
        CheckLink(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            CheckLink(entry);
            if (Directory.Exists(entry)) CheckTree(entry);
        }
    }

    internal static int SelfCheck()
    {
        var root = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        var checks = 0;
        void Refused(Action action)
        {
            try { action(); }
            catch (IOException) { checks++; return; }
            throw new InvalidOperationException("Expected unsafe fixture path refusal.");
        }
        Refused(() => New(Path.GetTempPath()));
        Refused(() => New(Path.Combine(Parent, "not-a-guid", "fixture")));
        Refused(() => New(Path.Combine(root, "..", "..", "escape")));
        Refused(() => Separate(root, Path.Combine(root, "child")));
        Refused(() => Separate(root, root));
        Create(root);
        try
        {
            Refused(() => Create(root));
            Existing(root);
            var unowned = Path.Combine(root, "unowned");
            Directory.CreateDirectory(unowned);
            Refused(() => Existing(unowned));
            if (File.ReadAllText(Path.Combine(root, Marker)) != Ownership)
                throw new InvalidOperationException("Refusal altered ownership evidence.");
            Console.WriteLine($"PASS fixture guards: {checks} refusals; owned root unchanged.");
            return 0;
        }
        finally
        {
            // Only this invocation's newly-created, owned tree can reach cleanup.
            Existing(root);
            Directory.Delete(root, true);
        }
    }
}
