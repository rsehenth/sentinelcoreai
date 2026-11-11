using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

public sealed class Stage7bIntegrationTests
{
    [Fact]
    public async Task ConcurrentAcquisitionsHaveOneOwnerAndRecoverAfterFailure()
    {
        var coordinator = new ProductOperationCoordinator();
        using var start = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var attempts = new CountdownEvent(2);
        var owners = 0;
        Task Attempt() => Task.Run(() =>
        {
            start.Wait();
            IDisposable? lease = null;
            try { lease = coordinator.Acquire(); Interlocked.Increment(ref owners); }
            catch (TargetLockedException) { }
            finally { attempts.Signal(); }
            try { release.Wait(); }
            finally { lease?.Dispose(); }
        });
        var first = Attempt(); var second = Attempt();
        start.Set();
        try
        {
            Assert.True(attempts.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, owners);
            Assert.True(coordinator.IsBusy);
        }
        finally { release.Set(); await Task.WhenAll(first, second); attempts.Dispose(); }
        Assert.False(coordinator.IsBusy);
        using var next = coordinator.Acquire();
    }

    [Fact]
    public async Task CancellationKeepsReservationThroughPersistenceAndReopening()
    {
        using var scope = new SafetyScope();
        var owner = new ProductOperationCoordinator(scope.Locks);
        var other = new ProductOperationCoordinator(new FileExclusiveLockFactory(scope.Root));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        using (owner.Acquire())
        {
            var audit = await new AuditRunner([new FakeCollector("fixture")], checkpoints: scope.Store)
                .RunBasicAsync(cancellationToken: cancel.Token);
            Assert.Equal(DiagnosticStatus.Cancelled, audit.Status);
            Assert.Throws<TargetLockedException>(() => other.Acquire());
            var history = await new MemoryStore(scope.Root).ReadAuditHistoryAsync();
            Assert.Equal(audit.Id, Assert.Single(history.Audits).Id);
            Assert.Throws<TargetLockedException>(() => other.Acquire());
        }
        using var next = other.Acquire();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullAuditCancellationKeepsSixModulePlanAndOnlySavedEvidence(bool failHardwareSave)
    {
        using var scope = new SafetyScope();
        using var cancel = new CancellationTokenSource();
        IAuditDiagnosticStore store = new AuditStore(scope.Store, failHardwareSave);
        var full = new FullAuditDiagnostics(
            new HardwareRunner(GraphicsSamples.RealisticHardware()),
            new EventInvestigationRunner(GraphicsSamples.RealisticEvents()),
            new PerformanceSessionRunner([new ForbiddenPerformance()], selfMeterFactory: () => new FakeSelfOverheadMeter()), new NetworkSessionRunner([new ForbiddenNetwork()], selfMeterFactory: () => new FakeSelfOverheadMeter()),
            GraphicsSamples.Runner(), new MonitoringSessionRunner([], scope.Store), store);
        var progress = new SynchronousProgress(update =>
        {
            if (update.Result?.CollectorId == AuditEvidenceReference.CollectorIdFor(CorrelationSource.Events)) cancel.Cancel();
        });
        var audit = await new AuditRunner([new FakeCollector("fixture")], full: full, checkpoints: scope.Store)
            .RunAsync(AuditKind.Full, monitoringAuthorized: true, progress: progress, cancellationToken: cancel.Token);
        Assert.Equal(7, audit.PlannedSteps);
        Assert.Equal(7, audit.Results.Count);
        Assert.Equal(DiagnosticStatus.Cancelled, audit.Status);
        Assert.All(audit.Results, result => Assert.Equal(audit.Id, result.AuditId));
        Assert.All(audit.Results.Skip(3), result => Assert.Equal(DiagnosticStatus.Cancelled, result.Status));
        var hardware = audit.Results[1];
        if (failHardwareSave)
        {
            Assert.Null(hardware.Evidence);
            Assert.Equal(DiagnosticStatus.Failed, hardware.Status);
        }
        else Assert.Equal(Assert.Single((await scope.Store.LoadSnapshotsAsync()).Snapshots).Id, hardware.Evidence!.RecordId);
        Assert.Equal(Assert.Single((await scope.Store.LoadInvestigationsAsync()).Investigations).Id, audit.Results[2].Evidence!.RecordId);
        var reopened = Assert.Single((await new MemoryStore(scope.Root).ReadAuditHistoryAsync()).Audits);
        Assert.Equal(audit.Id, reopened.Id);
        Assert.Equal(DiagnosticStatus.Cancelled, reopened.Status);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("performance-collect")]
    [InlineData("network-save")]
    [InlineData("monitoring-checkpoint")]
    [InlineData("cancel-after-network")]
    public async Task FullAuditUsesOnlySyntheticSourcesAndPersistsEveryReachedModule(string failure)
    {
        using var scope = new SafetyScope();
        using var cancel = new CancellationTokenSource();
        var clock = new SyntheticSessionClock();
        var meters = new List<FakeSelfOverheadMeter>();
        ISelfOverheadMeter Meter() { var meter = new FakeSelfOverheadMeter(); meters.Add(meter); return meter; }
        var diagnosticStore = new AuditStore(scope.Store, false, failure);
        var full = new FullAuditDiagnostics(
            new HardwareRunner(GraphicsSamples.RealisticHardware(), clock),
            new EventInvestigationRunner(GraphicsSamples.RealisticEvents(), clock),
            new PerformanceSessionRunner([new SyntheticPerformanceCollector(failure == "performance-collect")], clock, selfMeterFactory: Meter),
            new NetworkSessionRunner([new SyntheticNetworkCollector()], clock: clock, selfMeterFactory: Meter),
            GraphicsSamples.Runner(clock),
            new MonitoringSessionRunner([new SyntheticMonitoringSource(MonitoringSource.Events),
                new SyntheticMonitoringSource(MonitoringSource.Performance), new SyntheticMonitoringSource(MonitoringSource.Network)],
                diagnosticStore, clock), diagnosticStore);
        var checkpoints = new RecordingCheckpoints(scope.Store);
        var updates = new List<AuditProgress>();
        var progress = new SynchronousProgress(update =>
        {
            updates.Add(update);
            if (failure == "cancel-after-network" && update.Result?.Evidence?.Source == CorrelationSource.Network) cancel.Cancel();
        });
        var audit = await new AuditRunner([new FakeCollector("fixture")], clock, full: full, checkpoints: checkpoints)
            .RunAsync(AuditKind.Full, monitoringAuthorized: true, progress: progress, cancellationToken: cancel.Token);
        Assert.True(checkpoints.Records.Count >= 7);
        Assert.All(checkpoints.Records, checkpoint => Assert.Equal(audit.Id, checkpoint.Id));
        Assert.Equal(audit, checkpoints.Final);
        Assert.True(checkpoints.Disposed);
        Assert.Equal(7, audit.PlannedSteps);
        Assert.Equal(7, audit.Results.Count);
        Assert.All(audit.Results, result => Assert.Equal(audit.Id, result.AuditId));
        Assert.Equal(2, meters.Count);
        Assert.All(meters, meter => { Assert.True(meter.Reads > 0); Assert.True(meter.Disposed); });
        Assert.Equal(31, Assert.Single((await scope.Store.LoadSessionsAsync()).Sessions).Samples.Count);
        var network = Assert.Single(audit.Results, r => r.CollectorId == AuditEvidenceReference.CollectorIdFor(CorrelationSource.Network));
        Assert.Equal(failure == "network-save", network.Evidence is null);
        var reopened = Assert.Single((await new MemoryStore(scope.Root).ReadAuditHistoryAsync()).Audits);
        Assert.Equal(audit.Id, reopened.Id);
        Assert.Equal(audit.Status, reopened.Status);
        Assert.Contains(updates, update => update.Result?.Evidence?.Source == CorrelationSource.Performance);
        if (failure == "cancel-after-network")
        {
            Assert.Equal(DiagnosticStatus.Cancelled, audit.Status);
            Assert.All(audit.Results.Skip(5), result => Assert.Equal(DiagnosticStatus.Cancelled, result.Status));
            Assert.Empty((await scope.Store.LoadMonitoringSessionsAsync()).Sessions);
        }
        else
        {
            var monitoring = Assert.Single((await scope.Store.LoadMonitoringSessionsAsync()).Sessions);
            Assert.Equal(failure == "monitoring-checkpoint" ? MonitoringExecutionStatus.Failed : MonitoringExecutionStatus.Completed, monitoring.Status);
            Assert.Equal(failure == "monitoring-checkpoint", monitoring.Segments.Count == 0);
            Assert.Equal(failure == "none" ? DiagnosticStatus.Completed : DiagnosticStatus.Partial, audit.Status);
        }
        Assert.All(audit.Results.Where(result => result.Evidence is not null), result =>
            Assert.NotEqual(Guid.Empty, result.Evidence!.RecordId));
    }

    private sealed class RecordingCheckpoints(MemoryStore store) : IAuditCheckpointStore
    {
        public List<AuditRecord> Records { get; } = [];
        public AuditRecord? Final { get; private set; }
        public bool Disposed { get; private set; }
        public async Task<IAuditRunLease> AcquireAuditLeaseAsync(Guid id) => new Lease(this, await store.AcquireAuditLeaseAsync(id));
        public Task<StoreSnapshot> ReadInterruptedAuditsAsync(CancellationToken token = default) => store.ReadInterruptedAuditsAsync(token);
        private sealed class Lease(RecordingCheckpoints owner, IAuditRunLease inner) : IAuditRunLease
        {
            public Task SaveCheckpointAsync(AuditRecord checkpoint) { owner.Records.Add(checkpoint); return inner.SaveCheckpointAsync(checkpoint); }
            public Task CompleteAsync(AuditRecord audit) { owner.Final = audit; return inner.CompleteAsync(audit); }
            public void Dispose() { inner.Dispose(); owner.Disposed = true; }
        }
    }

    private sealed class SynchronousProgress(Action<AuditProgress> report) : IProgress<AuditProgress>
    { public void Report(AuditProgress value) => report(value); }

    private sealed class AuditStore(MemoryStore inner, bool failHardwareSave, string? failure = null) : IAuditDiagnosticStore
    {
        public Task SaveSnapshotAsync(HardwareSnapshot value, CancellationToken token = default) =>
            failHardwareSave ? throw new IOException("fixture: hardware save failed") : inner.SaveSnapshotAsync(value, token);
        public Task SaveInvestigationAsync(EventInvestigation value, CancellationToken token = default) => inner.SaveInvestigationAsync(value, token);
        public Task<long> SaveSessionAsync(PerformanceSession value, CancellationToken token = default) => inner.SaveSessionAsync(value, token);
        public Task<long> SaveNetworkSessionAsync(NetworkSession value, CancellationToken token = default) => failure == "network-save" ? throw new IOException("Synthetic save failure") : inner.SaveNetworkSessionAsync(value, token);
        public Task<long> SaveGraphicsInvestigationAsync(GraphicsInvestigation value, CancellationToken token = default) => inner.SaveGraphicsInvestigationAsync(value, token);
        public Task<long> SaveMonitoringSessionAsync(MonitoringSession value, CancellationToken token = default) => inner.SaveMonitoringSessionAsync(value, token);
        public Task<HardwareStoreSnapshot> LoadSnapshotsAsync(CancellationToken token = default) => inner.LoadSnapshotsAsync(token);
        public Task<EventStoreSnapshot> LoadInvestigationsAsync(CancellationToken token = default) => inner.LoadInvestigationsAsync(token);
        public Task<PerformanceStoreSnapshot> LoadSessionsAsync(CancellationToken token = default) => inner.LoadSessionsAsync(token);
        public Task<NetworkStoreSnapshot> LoadNetworkSessionsAsync(CancellationToken token = default) => inner.LoadNetworkSessionsAsync(token);
        public Task<GraphicsStoreSnapshot> LoadGraphicsInvestigationsAsync(CancellationToken token = default) => inner.LoadGraphicsInvestigationsAsync(token);
        public Task<MonitoringStoreSnapshot> LoadMonitoringSessionsAsync(CancellationToken token = default) => inner.LoadMonitoringSessionsAsync(token);
        public Task<long> SaveMonitoringSegmentAsync(MonitoringSegment value, CancellationToken token = default) =>
            failure == "monitoring-checkpoint" ? throw new IOException("Synthetic checkpoint failure") : inner.SaveMonitoringSegmentAsync(value, token);
        public string GetHardwareReportPath(Guid id) => inner.GetHardwareReportPath(id);
        public string GetEventReportPath(Guid id) => inner.GetEventReportPath(id);
        public string GetPerformanceReportPath(Guid id) => inner.GetPerformanceReportPath(id);
        public string GetNetworkReportPath(Guid id) => inner.GetNetworkReportPath(id);
        public string GetGraphicsReportPath(Guid id) => inner.GetGraphicsReportPath(id);
        public string GetMonitoringReportPath(Guid id) => inner.GetMonitoringReportPath(id);
    }

    private sealed class ForbiddenPerformance : IPerformanceCollector
    {
        public string Id => "forbidden-performance";
        public string Version => "1";
        public string Source => "fixture: must not start";
        public PerformanceSourceCategory Category => PerformanceSourceCategory.Cpu;
        public IPerformanceCollectorSession StartSession(PerformanceCollectionContext context) =>
            throw new InvalidOperationException("Cancelled module must not start");
    }

    private sealed class ForbiddenNetwork : INetworkSampleCollector
    {
        public string Id => "forbidden-network";
        public string Version => "1";
        public string Source => "fixture: must not start";
        public NetworkSourceCategory Category => NetworkSourceCategory.Endpoints;
        public Task<NetworkSampleReading> ReadAsync(NetworkCollectionContext context, DateTimeOffset timestampUtc,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Cancelled module must not start");
    }
}
