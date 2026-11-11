using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class NetworkSessionRunnerTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 10, 31, 15, 30, 0, TimeSpan.Zero);

    [Fact]
    public void PresetsResolveToSnapshotThirtyAndSixtySecondsAndRejectInvalidPreset()
    {
        var snapshot = NetworkSessionRequest.ForPreset(NetworkSessionPreset.Snapshot);
        Assert.Equal(TimeSpan.Zero, snapshot.Duration);
        Assert.Equal(TimeSpan.Zero, snapshot.SampleInterval);
        Assert.Equal(1, snapshot.ExpectedSamples);
        Assert.False(snapshot.IncludeAddresses);

        var seconds30 = NetworkSessionRequest.ForPreset(NetworkSessionPreset.Seconds30, includeAddresses: true);
        Assert.Equal(TimeSpan.FromSeconds(30), seconds30.Duration);
        Assert.Equal(TimeSpan.FromSeconds(1), seconds30.SampleInterval);
        Assert.Equal(31, seconds30.ExpectedSamples);
        Assert.True(seconds30.IncludeAddresses);

        var minute1 = NetworkSessionRequest.ForPreset(NetworkSessionPreset.Minute1);
        Assert.Equal(TimeSpan.FromMinutes(1), minute1.Duration);
        Assert.Equal(TimeSpan.FromSeconds(1), minute1.SampleInterval);
        Assert.Equal(61, minute1.ExpectedSamples);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NetworkSessionRequest.ForPreset((NetworkSessionPreset)999));
    }

    [Fact]
    public async Task SnapshotCollectsSingleSampleAndCompletesCoverage()
    {
        var sampleCollector = new FakeSampleCollector("network-endpoints", NetworkSourceCategory.Endpoints,
            (index, at) => CreateSampleReading("network-endpoints", at, index));
        var factsCollector = new FakeFactsCollector("network-port-range", NetworkSourceCategory.PortRange,
            () => new NetworkFactsReading(
                CollectorId: "network-port-range",
                Unsupported: false,
                PortRanges:
                [
                    new NetworkDynamicPortRange(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, 49152, 16384, "Registry", false, [], [])
                ],
                Adapters:
                [
                    new NetworkAdapterSummary("eth0", "Ethernet", "Up", true, false, 1, 0)
                ],
                Limitations: []));

        var runner = new NetworkSessionRunner([sampleCollector], [factsCollector], new InstantTimeProvider(BaseUtc), selfMeterFactory: () => new FakeSelfOverheadMeter());
        var session = await runner.RunAsync(NetworkSessionPreset.Snapshot);

        Assert.Equal(NetworkSessionPreset.Snapshot, session.Preset);
        Assert.Equal(NetworkSessionStatus.Completed, session.Status);
        Assert.Single(session.Samples);
        Assert.Single(session.PortRanges);
        Assert.Single(session.Adapters);
        Assert.NotNull(session.Coverage);
        Assert.Equal(NetworkExecutionStatus.Completed, session.Coverage!.Execution);
        Assert.Equal(NetworkCoverageStatus.Complete, session.Coverage.Status);
        Assert.Equal(1, session.Coverage.ExpectedSamples);
        Assert.Equal(1, session.Coverage.CollectedSamples);
        Assert.Equal(0, session.Coverage.MissedOpportunities);
    }

    [Fact]
    public async Task MultiSampleSessionMarksFirstSampleAsWarmupAndReportsProgress()
    {
        var clock = new InstantTimeProvider(BaseUtc);
        var sampleCollector = new FakeSampleCollector("network-endpoints", NetworkSourceCategory.Endpoints,
            (index, at) => CreateSampleReading("network-endpoints", at, index));
        var progressUpdates = new List<NetworkProgress>();
        var progress = new SynchronousProgress<NetworkProgress>(progressUpdates.Add);

        var runner = new NetworkSessionRunner([sampleCollector], clock: clock, selfMeterFactory: () => new FakeSelfOverheadMeter());
        var request = new NetworkSessionRequest(
            Preset: NetworkSessionPreset.Seconds30,
            Duration: TimeSpan.FromSeconds(30),
            SampleInterval: TimeSpan.FromSeconds(5)); // 7 expected samples

        var session = await runner.RunAsync(request, progress: progress);

        Assert.Equal(NetworkSessionStatus.Completed, session.Status);
        Assert.Equal(7, session.Samples.Count);
        Assert.True(session.Samples[0].Warmup);
        Assert.All(session.Samples.Skip(1), sample => Assert.False(sample.Warmup));
        Assert.Equal(7, progressUpdates.Count);
        Assert.Equal(7, session.Statistics.TotalSamples);
        Assert.Equal(6, session.Statistics.ValidSamples);
    }

    [Fact]
    public async Task SlowCollectorSkipsMissedOpportunitiesWithoutBursting()
    {
        var clock = new InstantTimeProvider(BaseUtc);
        var slowFirstTick = new FakeSampleCollector("network-endpoints", NetworkSourceCategory.Endpoints,
            async (index, at, ct) =>
            {
                if (index == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1150), ct);
                }
                return CreateSampleReading("network-endpoints", at, index);
            });

        var runner = new NetworkSessionRunner([slowFirstTick], clock: clock, selfMeterFactory: () => new FakeSelfOverheadMeter());
        var session = await runner.RunAsync(NetworkSessionRequest.ForPreset(NetworkSessionPreset.Seconds30));

        Assert.NotNull(session.Coverage);
        Assert.True(session.Coverage!.MissedOpportunities >= 1);
        Assert.Equal(NetworkExecutionStatus.Completed, session.Coverage.Execution);
        Assert.Equal(NetworkCoverageStatus.Partial, session.Coverage.Status);
        Assert.Equal(NetworkSessionStatus.Partial, session.Status);
        Assert.Contains(session.Limitations, item => item.Contains("oportunidade(s) de amostragem perdida(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationPreservesCollectedSamplesAndMarksSessionCancelled()
    {
        using var cts = new CancellationTokenSource();
        var clock = new InstantTimeProvider(BaseUtc);
        var collector = new FakeSampleCollector("network-endpoints", NetworkSourceCategory.Endpoints,
            (index, at) =>
            {
                if (index == 2)
                {
                    cts.Cancel();
                }
                return CreateSampleReading("network-endpoints", at, index);
            });

        var runner = new NetworkSessionRunner([collector], clock: clock, selfMeterFactory: () => new FakeSelfOverheadMeter());
        var session = await runner.RunAsync(
            new NetworkSessionRequest(NetworkSessionPreset.Seconds30, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)),
            cancellationToken: cts.Token);

        Assert.Equal(NetworkSessionStatus.Cancelled, session.Status);
        Assert.NotNull(session.Coverage);
        Assert.Equal(NetworkExecutionStatus.Cancelled, session.Coverage!.Execution);
        Assert.Equal(NetworkCoverageStatus.Partial, session.Coverage.Status);
        Assert.Equal(3, session.Samples.Count);
        Assert.Contains(session.Limitations, item => item.Contains("Sessão cancelada pelo usuário após 3 amostra(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentSessionIsRejectedWhileActive()
    {
        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var blockingCollector = new FakeSampleCollector("network-endpoints", NetworkSourceCategory.Endpoints,
            async (index, at, ct) =>
            {
                startedTcs.TrySetResult();
                await releaseTcs.Task.WaitAsync(ct);
                return CreateSampleReading("network-endpoints", at, index);
            });

        var runner = new NetworkSessionRunner([blockingCollector], clock: new InstantTimeProvider(BaseUtc), selfMeterFactory: () => new FakeSelfOverheadMeter());
        var firstRun = runner.RunAsync(NetworkSessionPreset.Snapshot);
        await startedTcs.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(NetworkSessionPreset.Snapshot));

        releaseTcs.TrySetResult();
        var completed = await firstRun;
        Assert.Equal(NetworkSessionStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task UnavailableCollectorProducesPartialCoverageAndAllFailedProducesFailedSession()
    {
        var working = new FakeSampleCollector("network-endpoints", NetworkSourceCategory.Endpoints,
            (index, at) => CreateSampleReading("network-endpoints", at, index));
        var failing = new FakeSampleCollector("network-statistics", NetworkSourceCategory.Statistics,
            (_, _) => throw new InvalidOperationException("collector failed"));

        var partialRunner = new NetworkSessionRunner([working, failing], clock: new InstantTimeProvider(BaseUtc), selfMeterFactory: () => new FakeSelfOverheadMeter());
        var partialSession = await partialRunner.RunAsync(NetworkSessionPreset.Snapshot);

        Assert.Equal(NetworkSessionStatus.Partial, partialSession.Status);
        Assert.NotNull(partialSession.Coverage);
        Assert.Equal(NetworkExecutionStatus.Completed, partialSession.Coverage!.Execution);
        Assert.Equal(NetworkCoverageStatus.Partial, partialSession.Coverage.Status);
        Assert.Contains("network-statistics", partialSession.Coverage.UnavailableCollectors);

        var failedRunner = new NetworkSessionRunner([failing], clock: new InstantTimeProvider(BaseUtc), selfMeterFactory: () => new FakeSelfOverheadMeter());
        var failedSession = await failedRunner.RunAsync(NetworkSessionPreset.Snapshot);

        Assert.Equal(NetworkSessionStatus.Failed, failedSession.Status);
        Assert.NotNull(failedSession.Coverage);
        Assert.Equal(NetworkExecutionStatus.Failed, failedSession.Coverage!.Execution);
        Assert.Equal(NetworkCoverageStatus.Unavailable, failedSession.Coverage.Status);
    }

    [Fact]
    public async Task IncludeAddressesFalseStripsAddressesWhileTruePreservesThemAndTruncatesAtLimit()
    {
        var collector = new FakeSampleCollector("network-endpoints", NetworkSourceCategory.Endpoints,
            (_, at) =>
            {
                var endpoints = Enumerable.Range(0, 5)
                    .Select(i => new NetworkEndpoint(
                        Protocol: NetworkProtocol.Tcp,
                        Family: NetworkAddressFamily.IPv4,
                        LocalAddress: "10.0.0.5",
                        LocalPort: (ushort)(50000 + i),
                        RemoteAddress: "93.184.216.34",
                        RemotePort: 443,
                        TcpState: TcpConnectionState.Established,
                        Pid: 1000,
                        ProcessIdentity: "1000:1",
                        ProcessName: "app.exe",
                        ProcessStatus: NetworkProcessStatus.Resolved,
                        ObservedUtc: at,
                        Source: "Fake"))
                    .ToArray();
                return new NetworkSampleReading("network-endpoints", false, endpoints, [], null, null, 0, []);
            });

        var limits = new NetworkLimits { MaxPersistedEndpoints = 3 };
        var runner = new NetworkSessionRunner([collector], clock: new InstantTimeProvider(BaseUtc), limits: limits, selfMeterFactory: () => new FakeSelfOverheadMeter());

        var defaultSession = await runner.RunAsync(
            NetworkSessionRequest.ForPreset(NetworkSessionPreset.Snapshot, includeAddresses: false));
        Assert.False(defaultSession.IncludesAddresses);
        Assert.Equal(3, defaultSession.EndpointDetails.Count);
        Assert.All(defaultSession.EndpointDetails, ep =>
        {
            Assert.Null(ep.LocalAddress);
            Assert.Null(ep.RemoteAddress);
        });
        Assert.True(defaultSession.Coverage!.DetailLimitReached);

        var optInSession = await runner.RunAsync(
            NetworkSessionRequest.ForPreset(NetworkSessionPreset.Snapshot, includeAddresses: true));
        Assert.True(optInSession.IncludesAddresses);
        Assert.Equal(3, optInSession.EndpointDetails.Count);
        Assert.All(optInSession.EndpointDetails, ep =>
        {
            Assert.Equal("10.0.0.5", ep.LocalAddress);
            Assert.Equal("93.184.216.34", ep.RemoteAddress);
        });
    }

    internal static EventInvestigation InvestigationWithTcpipEvents()
    {
        var baseInvestigation = Samples.Investigation();
        var group4231 = new EvidenceGroup(
            "network:tcpip-4231", EventCategory.Network, "Tcpip 4231", "Tcpip", 4231, null,
            EvidenceSeverity.PotentialImpact, EvidenceConfidence.Medium, CausalityStatus.NotAssessed,
            4, 0, 0, BaseUtc.AddMinutes(-10), BaseUtc.AddMinutes(-2), ["System"], ["System"], []);
        var group4266 = group4231 with { Id = "network:tcpip-4266", EventId = 4266, EventRecordCount = 7 };
        return baseInvestigation with { Groups = [.. baseInvestigation.Groups, group4231, group4266] };
    }

    [Fact]
    public void BuildTcpipContextExtractsEvents4231And4266WithoutClaimingCausality()
    {
        Assert.Null(NetworkSessionRunner.BuildTcpipContext(null));

        var baseInvestigation = Samples.Investigation();
        var withoutTcpip = NetworkSessionRunner.BuildTcpipContext(baseInvestigation);
        Assert.NotNull(withoutTcpip);
        Assert.Equal(0, withoutTcpip!.AttemptFailureEvents);
        Assert.Equal(0, withoutTcpip.EphemeralExhaustionEvents);

        var tcpipGroup4231 = new EvidenceGroup(
            Id: "network:tcpip-4231",
            Category: EventCategory.Network,
            Title: "Portas dinâmicas",
            Provider: "Tcpip",
            EventId: 4231,
            ProblemCode: null,
            Severity: EvidenceSeverity.PotentialImpact,
            Confidence: EvidenceConfidence.Medium,
            Causality: CausalityStatus.NotAssessed,
            EventRecordCount: 4,
            DistinctReportCount: 0,
            RePresentationCount: 0,
            FirstObservedUtc: BaseUtc.AddMinutes(-10),
            LastObservedUtc: BaseUtc.AddMinutes(-2),
            Channels: ["System"],
            Sources: ["System"],
            Limitations: []);

        var tcpipGroup4266 = tcpipGroup4231 with
        {
            Id = "network:tcpip-4266",
            EventId = 4266,
            EventRecordCount = 7
        };

        var withTcpip = NetworkSessionRunner.BuildTcpipContext(
            baseInvestigation with { Groups = [.. baseInvestigation.Groups, tcpipGroup4231, tcpipGroup4266] });

        Assert.NotNull(withTcpip);
        Assert.Equal(7, withTcpip!.AttemptFailureEvents);
        Assert.Equal(4, withTcpip.EphemeralExhaustionEvents);
        Assert.Contains("não comprova esgotamento", withTcpip.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidSessionRequestsAndDuplicateCollectorsAreRejected()
    {
        var collector = new FakeSampleCollector("network-endpoints", NetworkSourceCategory.Endpoints,
            (index, at) => CreateSampleReading("network-endpoints", at, index));

        Assert.Throws<ArgumentException>(() => new NetworkSessionRunner([], selfMeterFactory: () => new FakeSelfOverheadMeter()));
        Assert.Throws<ArgumentException>(() => new NetworkSessionRunner([collector, collector], selfMeterFactory: () => new FakeSelfOverheadMeter()));

        var runner = new NetworkSessionRunner([collector], clock: new InstantTimeProvider(BaseUtc), selfMeterFactory: () => new FakeSelfOverheadMeter());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync(new NetworkSessionRequest(NetworkSessionPreset.Snapshot, TimeSpan.FromSeconds(1), TimeSpan.Zero)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync(new NetworkSessionRequest(NetworkSessionPreset.Seconds30, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1))));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync(new NetworkSessionRequest(NetworkSessionPreset.Seconds30, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200))));
    }

    private static NetworkSampleReading CreateSampleReading(string collectorId, DateTimeOffset at, int index)
    {
        var endpoint = new NetworkEndpoint(
            Protocol: NetworkProtocol.Tcp,
            Family: NetworkAddressFamily.IPv4,
            LocalAddress: "127.0.0.1",
            LocalPort: (ushort)(50000 + index),
            RemoteAddress: "127.0.0.1",
            RemotePort: 443,
            TcpState: TcpConnectionState.Established,
            Pid: 1234,
            ProcessIdentity: "1234:1",
            ProcessName: "runner-test.exe",
            ProcessStatus: NetworkProcessStatus.Resolved,
            ObservedUtc: at,
            Source: "Fake");

        var tcpStats = new NetworkTcpStatistics(
            Family: NetworkAddressFamily.IPv4,
            RtoAlgorithm: 4,
            RtoMin: 200,
            RtoMax: 60000,
            MaxConnections: null,
            ActiveOpens: 10,
            PassiveOpens: 5,
            AttemptFails: (uint)index,
            EstablishedResets: 1,
            CurrentEstablished: 1,
            InSegments: 100,
            OutSegments: 100,
            RetransmittedSegments: 0,
            InErrors: 0,
            OutResets: 0,
            CumulativeConnections: 15,
            ObservedUtc: at,
            Source: "Fake",
            Limitations: []);

        return new NetworkSampleReading(
            CollectorId: collectorId,
            Unsupported: false,
            Endpoints: [endpoint],
            Processes: [new NetworkProcessCount("1234:1", 1234, "runner-test.exe", 1, 0, 0)],
            TcpStatistics: tcpStats,
            UdpStatistics: null,
            DuplicateRowsIgnored: 0,
            Limitations: []);
    }

    private sealed class FakeSampleCollector : INetworkSampleCollector
    {
        private readonly Func<int, DateTimeOffset, CancellationToken, Task<NetworkSampleReading>> reader;
        private int callIndex;

        public FakeSampleCollector(
            string id,
            NetworkSourceCategory category,
            Func<int, DateTimeOffset, NetworkSampleReading> syncReader)
            : this(id, category, (index, at, _) => Task.FromResult(syncReader(index, at)))
        {
        }

        public FakeSampleCollector(
            string id,
            NetworkSourceCategory category,
            Func<int, DateTimeOffset, CancellationToken, Task<NetworkSampleReading>> reader)
        {
            Id = id;
            Category = category;
            this.reader = reader;
        }

        public string Id { get; }
        public string Version => "1.0.0";
        public string Source => "FakeSampleCollector";
        public NetworkSourceCategory Category { get; }

        public Task<NetworkSampleReading> ReadAsync(
            NetworkCollectionContext context,
            DateTimeOffset timestampUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Interlocked.Increment(ref callIndex) - 1;
            return reader(current, timestampUtc, cancellationToken);
        }
    }

    private sealed class FakeFactsCollector(
        string id,
        NetworkSourceCategory category,
        Func<NetworkFactsReading> reader) : INetworkFactsCollector
    {
        public string Id { get; } = id;
        public string Version => "1.0.0";
        public string Source => "FakeFactsCollector";
        public NetworkSourceCategory Category { get; } = category;

        public Task<NetworkFactsReading> ReadAsync(
            NetworkCollectionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(reader());
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class InstantTimeProvider(DateTimeOffset startUtc) : TimeProvider
    {
        private long utcTicks = startUtc.UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            {
                Interlocked.Add(ref utcTicks, dueTime.Ticks);
            }
            var timer = new InstantTimer(callback, state);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timer.Fire();
            }
            return timer;
        }

        private sealed class InstantTimer(TimerCallback callback, object? state) : ITimer
        {
            public void Fire() => ThreadPool.QueueUserWorkItem(_ => callback(state));
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
