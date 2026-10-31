using SentinelCore.Domain;

namespace SentinelCore.Application;

public static class EventWindows
{
    public static readonly TimeSpan Maximum = TimeSpan.FromDays(90);

    public static EventQueryWindow Resolve(EventWindow preset, DateTimeOffset nowUtc,
        DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null)
    {
        var now = nowUtc.ToUniversalTime();
        if (preset != EventWindow.Custom)
        {
            var span = preset switch
            {
                EventWindow.Last24Hours => TimeSpan.FromHours(24),
                EventWindow.Last7Days => TimeSpan.FromDays(7),
                EventWindow.Last30Days => TimeSpan.FromDays(30),
                _ => throw new ArgumentOutOfRangeException(nameof(preset))
            };
            return new(preset, now - span, now);
        }

        if (fromUtc is null || toUtc is null)
            throw new ArgumentException("Janela personalizada exige início e fim.");
        var from = fromUtc.Value.ToUniversalTime();
        var to = toUtc.Value.ToUniversalTime();
        if (to > now) to = now;
        if (from >= to) throw new ArgumentException("O início deve ser anterior ao fim da janela.");
        if (to - from > Maximum) throw new ArgumentException("A janela máxima é de 90 dias.");
        return new(EventWindow.Custom, from, to);
    }
}
