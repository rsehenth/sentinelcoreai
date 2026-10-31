using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class EventWindowTests
{
    [Theory]
    [InlineData(EventWindow.Last24Hours, 1)]
    [InlineData(EventWindow.Last7Days, 7)]
    [InlineData(EventWindow.Last30Days, 30)]
    public void PresetsEndAtNowAndStartBefore(EventWindow preset, int days)
    {
        var now = new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);
        var window = EventWindows.Resolve(preset, now);
        Assert.Equal(now, window.ToUtc);
        Assert.Equal(now.AddDays(-days), window.FromUtc);
        Assert.Equal(preset, window.Preset);
    }

    [Fact]
    public void CustomRequiresOrderedBoundsAndIsNormalizedToUtc()
    {
        var now = new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);
        var from = new DateTimeOffset(2026, 10, 29, 23, 0, 0, TimeSpan.FromHours(-3));
        var to = new DateTimeOffset(2026, 10, 30, 13, 0, 0, TimeSpan.FromHours(-3));
        var window = EventWindows.Resolve(EventWindow.Custom, now, from, to);
        Assert.Equal(TimeSpan.Zero, window.FromUtc.Offset);
        Assert.Equal(TimeSpan.Zero, window.ToUtc.Offset);
        Assert.True(window.FromUtc < window.ToUtc);
    }

    [Fact]
    public void CustomWithoutBoundsIsRejected() =>
        Assert.Throws<ArgumentException>(() => EventWindows.Resolve(EventWindow.Custom, DateTimeOffset.UtcNow));

    [Fact]
    public void InvertedCustomWindowIsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() =>
            EventWindows.Resolve(EventWindow.Custom, now, now.AddHours(-1), now.AddHours(-2)));
    }

    [Fact]
    public void WindowLongerThanNinetyDaysIsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => EventWindows.Resolve(EventWindow.Custom, now,
            now.AddDays(-91), now));
    }

    [Fact]
    public void FutureEndIsClampedToNow()
    {
        var now = DateTimeOffset.UtcNow;
        var window = EventWindows.Resolve(EventWindow.Custom, now, now.AddHours(-1), now.AddHours(1));
        Assert.Equal(now, window.ToUtc);
    }
}
