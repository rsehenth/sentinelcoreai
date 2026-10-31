using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows;
using Xunit;

namespace SentinelCore.Tests;

public sealed class EventQueryTests
{
    [Fact]
    public void XPathFiltersLevelAndUtcBounds()
    {
        var window = new EventQueryWindow(EventWindow.Custom,
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 2, 23, 59, 59, TimeSpan.Zero));
        var xpath = EventQuery.XPath(window);
        Assert.Contains("Level=1 or Level=2 or Level=3", xpath);
        Assert.Contains("2026-10-01T00:00:00.0000000Z", xpath);
        Assert.Contains("2026-10-02T23:59:59.0000000Z", xpath);
    }

    [Fact]
    public void XPathNormalizesOffsetsToUtc()
    {
        var window = new EventQueryWindow(EventWindow.Custom,
            new DateTimeOffset(2026, 10, 1, 21, 0, 0, TimeSpan.FromHours(-3)),
            new DateTimeOffset(2026, 10, 2, 21, 0, 0, TimeSpan.FromHours(-3)));
        var xpath = EventQuery.XPath(window);
        Assert.Contains("2026-10-02T00:00:00.0000000Z", xpath);
        Assert.Contains("2026-10-03T00:00:00.0000000Z", xpath);
    }

    [Theory]
    [InlineData(1, EventLevel.Critical)]
    [InlineData(2, EventLevel.Error)]
    [InlineData(3, EventLevel.Warning)]
    [InlineData(4, EventLevel.Information)]
    [InlineData(5, EventLevel.Verbose)]
    [InlineData(99, EventLevel.Unknown)]
    public void LevelMappingIsLanguageNeutral(int raw, EventLevel expected) =>
        Assert.Equal(expected, EventQuery.Level(raw));

    [Fact]
    public void LongMessageIsTruncatedWithFlag()
    {
        var value = EventQuery.Truncate(new string('x', 20), 10, out var truncated);
        Assert.Equal(10, value!.Length);
        Assert.True(truncated);
    }

    [Fact]
    public void NullMessageStaysUnavailable()
    {
        var value = EventQuery.Truncate(null, 10, out var truncated);
        Assert.Null(value);
        Assert.False(truncated);
    }
}
