using System.Globalization;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows;

internal static class EventQuery
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static string XPath(EventQueryWindow window) =>
        "*[System[(Level=1 or Level=2 or Level=3) and " +
        $"TimeCreated[@SystemTime>='{Format(window.FromUtc)}' and @SystemTime<='{Format(window.ToUtc)}']]]";

    public static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    public static EventLevel Level(int? level) => level switch
    {
        1 => EventLevel.Critical,
        2 => EventLevel.Error,
        3 => EventLevel.Warning,
        4 => EventLevel.Information,
        5 => EventLevel.Verbose,
        _ => EventLevel.Unknown
    };

    public static string? Truncate(string? value, int maximum, out bool truncated)
    {
        truncated = false;
        if (value is null) return null;
        if (maximum < 0) maximum = 0;
        if (value.Length <= maximum) return value;
        truncated = true;
        return value[..maximum];
    }
}
