using SentinelCore.Application;
using SentinelCore.Domain;

namespace SentinelCore.Tests;

internal sealed class FakeSelfOverheadMeter : ISelfOverheadMeter
{
    public int Reads { get; private set; }
    public bool Disposed { get; private set; }
    public SelfOverheadSample Measure(TimeSpan elapsed, long collectionMilliseconds)
    {
        if (Disposed) throw new ObjectDisposedException(nameof(FakeSelfOverheadMeter));
        Reads++;
        return new(2.5, 4096, 8192, collectionMilliseconds);
    }
    public void Dispose() => Disposed = true;
}

/// <summary>Only delays advance synthetic UTC and monotonic time. No process or machine measurements.</summary>
internal sealed class SyntheticSessionClock : TimeProvider
{
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    public override DateTimeOffset GetUtcNow() => MonitoringSamples.Now.AddTicks(GetTimestamp());
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("One-shot fixture timers only.");
        var timer = new OneShot();
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            Interlocked.Add(ref ticks, Math.Max(0, dueTime.Ticks));
            ThreadPool.QueueUserWorkItem(_ => { if (!timer.Disposed) callback(state); });
        }
        return timer;
    }
    private sealed class OneShot : ITimer
    {
        public volatile bool Disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class SyntheticPerformanceCollector(bool fail = false) : IPerformanceCollector
{
    public string Id => "performance-cpu";
    public string Version => "1";
    public string Source => "Synthetic fixture";
    public PerformanceSourceCategory Category => PerformanceSourceCategory.Cpu;
    public IPerformanceCollectorSession StartSession(PerformanceCollectionContext context) => new Session(fail);
    private sealed class Session(bool fail) : IPerformanceCollectorSession
    {
        public Task<PerformanceCollectorTick> ReadAsync(DateTimeOffset at, TimeSpan interval, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (fail) throw new IOException("Synthetic CPU unavailable");
            return Task.FromResult(new PerformanceCollectorTick("performance-cpu", at, false,
                PerformanceSamples.Cpu(40, 25, 15), null, [], [], [], []));
        }
        public void Dispose() { }
    }
}

internal sealed class SyntheticNetworkCollector(bool fail = false) : INetworkSampleCollector
{
    public string Id => "network-endpoints";
    public string Version => "1";
    public string Source => "Synthetic fixture";
    public NetworkSourceCategory Category => NetworkSourceCategory.Endpoints;
    public Task<NetworkSampleReading> ReadAsync(NetworkCollectionContext context, DateTimeOffset at, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (fail) throw new IOException("Synthetic endpoints unavailable");
        return Task.FromResult(new NetworkSampleReading(Id, false, [new NetworkEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, null, 50000, null, 443, TcpConnectionState.Established, 123, "synthetic:123", "fixture", NetworkProcessStatus.Resolved, at, Source)], [], null, null, 0, []));
    }
}

internal sealed class SyntheticMonitoringSource(MonitoringSource kind) : IMonitoringSource
{
    public MonitoringSource Kind => kind;
    public string CollectorId => "synthetic-" + kind;
    public string CollectorVersion => "1";
    public Task<IMonitoringSourceSession> StartAsync(CancellationToken token) =>
        Task.FromResult<IMonitoringSourceSession>(new Session(kind));
    private sealed class Session(MonitoringSource kind) : IMonitoringSourceSession
    {
        private int index;
        public Task<MonitoringTick> ReadAsync(DateTimeOffset at, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var current = index++;
            return Task.FromResult(new MonitoringTick(kind, at, false,
                kind == MonitoringSource.Performance ? MonitoringSamples.PerformanceObservation(current, at) : null,
                [], kind == MonitoringSource.Network ? new MonitoringNetworkObservation(current, at, 0, 0, 0, 0, 0, 0, false, []) : null, []));
        }
        public void Dispose() { }
    }
}
