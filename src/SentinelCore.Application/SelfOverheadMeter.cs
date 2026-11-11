using System.Diagnostics;
using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>A per-session measurement, replaceable without opening the current process in synthetic runs.</summary>
public interface ISelfOverheadMeter : IDisposable
{
    SelfOverheadSample Measure(TimeSpan elapsed, long collectionMilliseconds);
}

internal sealed class ProcessSelfOverheadMeter : ISelfOverheadMeter
{
    private readonly Process process = Process.GetCurrentProcess();
    private TimeSpan lastCpu;
    private TimeSpan lastElapsed;

    public ProcessSelfOverheadMeter() => lastCpu = process.TotalProcessorTime;

    public SelfOverheadSample Measure(TimeSpan elapsed, long collectionMilliseconds)
    {
        process.Refresh();
        var cpu = process.TotalProcessorTime;
        var wall = elapsed - lastElapsed;
        var percent = wall <= TimeSpan.Zero ? 0 : Math.Clamp(
            (cpu - lastCpu).TotalMilliseconds / (wall.TotalMilliseconds * Environment.ProcessorCount) * 100, 0, 100);
        lastCpu = cpu;
        lastElapsed = elapsed;
        return new(Math.Round(percent, 1), (ulong)Math.Max(0, process.WorkingSet64),
            (ulong)Math.Max(0, process.PrivateMemorySize64), collectionMilliseconds);
    }

    public void Dispose() => process.Dispose();
}
