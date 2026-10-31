using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows;
using Xunit;

namespace SentinelCore.Tests;

public sealed class WerWindowTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EventTimeInsideWindowIsIncluded()
    {
        var report = Report(From.AddDays(2), null);

        Assert.True(WerMetadataCollector.IsWithin(report, Window(), out var known));
        Assert.True(known);
    }

    [Fact]
    public void MissingEventTimeFallsBackToCreationTime()
    {
        var report = Report(null, From.AddDays(4));

        Assert.True(WerMetadataCollector.IsWithin(report, Window(), out var known));
        Assert.True(known);
    }

    [Fact]
    public void ReportWithoutAnyTimeIsExcludedAndFlagged()
    {
        var report = Report(null, null);

        Assert.False(WerMetadataCollector.IsWithin(report, Window(), out var known));
        Assert.False(known);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public void WindowBoundariesAreInclusive(int days, bool expected)
    {
        var report = Report(From.AddDays(days), null);

        Assert.Equal(expected, WerMetadataCollector.IsWithin(report, Window(), out var known));
        Assert.True(known);
    }

    private static EventQueryWindow Window() => new(EventWindow.Custom, From, To);

    private static RawWerReport Report(DateTimeOffset? eventTime, DateTimeOffset? creationTime) =>
        new("report", true, "Sistema", "ReportArchive", "AppCrash_Contoso", "AppCrash", "1", eventTime,
            creationTime, null, null, null, null, null, null, [], [], EvidenceConfidence.High);
}
