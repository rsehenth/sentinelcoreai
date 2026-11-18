using System.Text;
using Microsoft.Data.Sqlite;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class Stage7b9ClosingTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "SentinelCore7b9Tests",
        Guid.NewGuid().ToString("N"));

    private MemoryStore NewStore() => new(root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task LoadAsyncPersistsProjectionWarningsInStorageLogWithoutDuplicationOrRecordLoss()
    {
        var store = NewStore();
        var audit = Samples.Audit(DateTimeOffset.UtcNow.AddMinutes(-5));
        await store.SaveAsync(audit);

        var systemStatePath = Path.Combine(root, "memory", "SYSTEM_STATE.md");
        var hardwarePath = Path.Combine(root, "memory", "HARDWARE.md");
        Assert.True(File.Exists(systemStatePath));
        Assert.True(File.Exists(hardwarePath));
        BlockWithDirectory(systemStatePath);
        BlockWithDirectory(hardwarePath);

        var corruptPath = Path.Combine(root, "memory", "audits", "basic", $"{Guid.NewGuid():D}.md");
        await File.WriteAllTextAsync(corruptPath, "corrupted envelope");

        var loaded = await NewStore().LoadAsync();

        var record = Assert.Single(loaded.Audits);
        Assert.Equal(audit.Id, record.Id);
        Assert.Single(loaded.Warnings, warning => warning.Contains("não recuperado", StringComparison.Ordinal));
        Assert.Equal(2, loaded.Warnings.Count(warning =>
            warning.Contains("relatório derivado não pôde ser regenerado", StringComparison.Ordinal)));
        Assert.Contains(loaded.Warnings, warning =>
            warning.Contains($"{audit.Id:D}", StringComparison.Ordinal) &&
            warning.Contains("não pôde ser regenerado", StringComparison.Ordinal));

        var log = File.ReadAllText(Path.Combine(root, "logs", "storage.log"), Encoding.UTF8);
        Assert.Equal(1, CountOccurrences(log, "não recuperado"));
        Assert.Equal(2, CountOccurrences(log, "relatório derivado não pôde ser regenerado"));
        Assert.Contains($"{audit.Id:D}", log);

        var reloaded = await NewStore().LoadAsync();
        Assert.Equal(audit.Id, Assert.Single(reloaded.Audits).Id);
        Assert.Equal(1L, IndexedAudits());
    }

    private void BlockWithDirectory(string path)
    {
        File.Delete(path);
        Directory.CreateDirectory(path);
    }

    private long IndexedAudits()
    {
        using var database = new SqliteConnection($"Data Source={Path.Combine(root, "data", "sentinelcore.db")}");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Audits;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }
}
