namespace SentinelCore.Application;

/// <summary>Monotonic session cadence uses the same injected clock as its delays.</summary>
internal sealed class SessionTimer(TimeProvider clock)
{
    private readonly long started = clock.GetTimestamp();
    private TimeSpan? stopped;
    public TimeSpan Elapsed => stopped ?? clock.GetElapsedTime(started);
    public void Stop() => stopped ??= Elapsed;
}
