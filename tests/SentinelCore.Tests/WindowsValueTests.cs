using SentinelCore.Infrastructure.Windows;
using Xunit;

namespace SentinelCore.Tests;

public sealed class WindowsValueTests
{
    [Fact] public void AllSixCollectorsAreDistinctAndVersioned()
    {
        var collectors = WindowsCollectors.CreateBasic(); Assert.Equal(6, collectors.Count);
        Assert.Equal(6, collectors.Select(c => c.Id).Distinct().Count());
        Assert.All(collectors, c => { Assert.NotEmpty(c.Version); Assert.Contains("WMI", c.Source); });
    }
    [Fact] public void MissingWmiValueStaysNullAndRecordsLimitation()
    {
        var read = new WmiRead(CancellationToken.None); Assert.Null(read.UInt64(new(), "Capacity")); Assert.NotEmpty(read.Limitations);
    }
    [Fact] public void InvalidWmiValueStaysNull()
    {
        var read = new WmiRead(CancellationToken.None); Assert.Null(read.UInt64(new() { ["Capacity"] = "invalid" }, "Capacity"));
        Assert.NotEmpty(read.Limitations);
    }
    [Fact] public void ZeroIsPreservedWhenActuallyReported()
    {
        var read = new WmiRead(CancellationToken.None); Assert.Equal(0UL, read.UInt64(new() { ["FreeSpace"] = 0UL }, "FreeSpace")); Assert.Empty(read.Limitations);
    }
    [Fact] public void EmptyStringIsUnavailable()
    {
        var read = new WmiRead(CancellationToken.None); Assert.Null(read.Text(new() { ["Name"] = "  " }, "Name")); Assert.NotEmpty(read.Limitations);
    }
    [Fact] public void InvalidFirmwareDateIsUnavailable()
    {
        var read = new WmiRead(CancellationToken.None); Assert.Null(read.Date(new() { ["ReleaseDate"] = "bad" }, "ReleaseDate")); Assert.NotEmpty(read.Limitations);
    }
    [Fact] public void WmiDateKeepsCorrectUtcInstant()
    {
        var read = new WmiRead(CancellationToken.None);
        var at = read.Date(new() { ["Boot"] = "20261030120000.000000-180" }, "Boot");
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.Zero), at);
    }
    [Fact] public void CoreCountOverflowIsUnavailable()
    {
        var read = new WmiRead(CancellationToken.None); Assert.Null(read.Int32(new() { ["Cores"] = ulong.MaxValue }, "Cores")); Assert.NotEmpty(read.Limitations);
    }
    [Fact] public void CancelledQueryDoesNotTouchWindows()
    {
        using var token = new CancellationTokenSource(); token.Cancel(); var read = new WmiRead(token.Token);
        Assert.Throws<OperationCanceledException>(() => read.Query("SELECT Name FROM Win32_Processor"));
    }
    [Fact] public void ReportLocationResolvesExistingPhysicalFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "SentinelCoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var report = Path.Combine(root, "report.md");
        try
        {
            File.WriteAllText(report, "test"); var physical = WindowsShell.ResolveExistingFilePath(report);
            Assert.True(File.Exists(physical)); Assert.Equal("report.md", Path.GetFileName(physical));
            Assert.Equal("test", File.ReadAllText(physical));
        }
        finally { Directory.Delete(root, true); }
    }
}
