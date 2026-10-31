using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class NetworkEvidenceAnalyzerTests
{
    private static readonly DateTimeOffset StartUtc = new(2026, 10, 31, 15, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<NetworkDynamicPortRange> DefaultRanges =
    [
        new NetworkDynamicPortRange(
            Protocol: NetworkProtocol.Tcp,
            Family: NetworkAddressFamily.IPv4,
            StartPort: 49152,
            PortCount: 16384,
            Source: "HKLM\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters",
            IsWindowsDefault: false,
            Exclusions: [],
            Limitations: [])
    ];

    [Fact]
    public void SustainedEndpointGrowthProducesObservationWithCausalityNotAssessed()
    {
        // NET-ENDPOINT-GROWTH default requires >= 10 contiguous samples, >= 20s span, delta >= 200, growth >= 50%.
        var samples = Enumerable.Range(0, 25)
            .Select(index => CreateSample(
                index: index,
                elapsedSeconds: index,
                tcp: 100 + index * 12, // 100 -> 388 (delta = 288 >= 200, growth = 288% >= 50%)
                udp: 10))
            .ToArray();

        var result = new NetworkEvidenceAnalyzer().Analyze(samples, DefaultRanges, requestedDurationSeconds: 30);

        var observation = Assert.Single(result.Observations, item => item.RuleId == "NET-ENDPOINT-GROWTH");
        Assert.Equal(NetworkObservationKind.EndpointGrowth, observation.Kind);
        Assert.Equal(CausalityStatus.NotAssessed, observation.Causality);
        Assert.Equal(25, observation.SupportingSampleCount);
        Assert.Equal(100, observation.ObservedMin);
        Assert.Equal(388, observation.ObservedMax);
        Assert.Contains(result.Notes, note => note.RuleId == "NET-ENDPOINT-GROWTH" && note.Reason == NetworkNoteReason.Observed);
    }

    [Fact]
    public void EndpointGrowthBelowMinimumDeltaProducesConditionNotObservedNote()
    {
        // High percentage growth (10 -> 40 = 300%), but absolute delta is 30 (< 200 minimum delta).
        var samples = Enumerable.Range(0, 25)
            .Select(index => CreateSample(
                index: index,
                elapsedSeconds: index,
                tcp: 10 + index,
                udp: 5))
            .ToArray();

        var result = new NetworkEvidenceAnalyzer().Analyze(samples, DefaultRanges, requestedDurationSeconds: 30);

        Assert.DoesNotContain(result.Observations, item => item.RuleId == "NET-ENDPOINT-GROWTH");
        var note = Assert.Single(result.Notes, item => item.RuleId == "NET-ENDPOINT-GROWTH");
        Assert.True(note.Evaluated);
        Assert.Equal(NetworkNoteReason.ConditionNotObserved, note.Reason);
    }

    [Fact]
    public void ProcessConcentrationProducesObservationAndRequiresVerifiableProcessIdentity()
    {
        // NET-PROC-CONCENTRATION requires >= 8 samples, >= 15s span, >= 200 endpoints, >= 60% share.
        var samples = Enumerable.Range(0, 20)
            .Select(index => CreateSample(
                index: index,
                elapsedSeconds: index,
                tcp: 250,
                udp: 50,
                processes:
                [
                    new NetworkProcessCount("4200:987654", 4200, "heavy-client.exe", 210, 30, 1), // 240 / 300 = 80%
                    new NetworkProcessCount("5100:123456", 5100, "other.exe", 40, 20, 0)
                ]))
            .ToArray();

        var analyzer = new NetworkEvidenceAnalyzer();
        var result = analyzer.Analyze(samples, DefaultRanges, requestedDurationSeconds: 30);

        var observation = Assert.Single(result.Observations, item => item.RuleId == "NET-PROC-CONCENTRATION");
        Assert.Equal("4200:987654", observation.Subject);
        Assert.Equal("heavy-client.exe", observation.SubjectName);
        Assert.Equal(CausalityStatus.NotAssessed, observation.Causality);

        // Without verifiable ProcessIdentity (e.g. PID only, Identity == null), rule must not attribute concentration.
        var unverifiedSamples = Enumerable.Range(0, 20)
            .Select(index => CreateSample(
                index: index,
                elapsedSeconds: index,
                tcp: 250,
                udp: 50,
                processes:
                [
                    new NetworkProcessCount(null, 4200, null, 240, 30, 0)
                ]))
            .ToArray();

        var unverifiedResult = analyzer.Analyze(unverifiedSamples, DefaultRanges, requestedDurationSeconds: 30);
        Assert.DoesNotContain(unverifiedResult.Observations, item => item.RuleId == "NET-PROC-CONCENTRATION");
        var unverifiedNote = Assert.Single(unverifiedResult.Notes, item => item.RuleId == "NET-PROC-CONCENTRATION");
        Assert.False(unverifiedNote.Evaluated);
        Assert.Equal(NetworkNoteReason.Unavailable, unverifiedNote.Reason);
    }

    [Fact]
    public void StateConcentrationObservesSustainedTimeWaitAndIgnoresNormalLowCounts()
    {
        // NET-STATE-CONCENTRATION requires >= 8 samples, >= 15s span, >= 500 connections in state, >= 50% of TCP.
        var elevatedSamples = Enumerable.Range(0, 20)
            .Select(index => CreateSample(
                index: index,
                elapsedSeconds: index,
                tcp: 800,
                udp: 20,
                states:
                [
                    new NetworkStateCount(TcpConnectionState.TimeWait, 600), // 75% of 800, >= 500
                    new NetworkStateCount(TcpConnectionState.Established, 190),
                    new NetworkStateCount(TcpConnectionState.Listen, 10)
                ]))
            .ToArray();

        var analyzer = new NetworkEvidenceAnalyzer();
        var elevatedResult = analyzer.Analyze(elevatedSamples, DefaultRanges, requestedDurationSeconds: 30);

        var observation = Assert.Single(elevatedResult.Observations, item => item.RuleId == "NET-STATE-CONCENTRATION");
        Assert.Equal("TimeWait", observation.Subject);
        Assert.Equal(CausalityStatus.NotAssessed, observation.Causality);

        // Low TIME_WAIT (e.g. 50 connections out of 60) is below the 500 minimum and must not trigger.
        var lowSamples = Enumerable.Range(0, 20)
            .Select(index => CreateSample(
                index: index,
                elapsedSeconds: index,
                tcp: 60,
                udp: 10,
                states:
                [
                    new NetworkStateCount(TcpConnectionState.TimeWait, 50),
                    new NetworkStateCount(TcpConnectionState.Established, 10)
                ]))
            .ToArray();

        var lowResult = analyzer.Analyze(lowSamples, DefaultRanges, requestedDurationSeconds: 30);
        Assert.DoesNotContain(lowResult.Observations, item => item.RuleId == "NET-STATE-CONCENTRATION");
        var lowNote = Assert.Single(lowResult.Notes, item => item.RuleId == "NET-STATE-CONCENTRATION");
        Assert.Equal(NetworkNoteReason.PreconditionNotObserved, lowNote.Reason);
    }

    [Fact]
    public void DynamicPortPressureObservesSustainedEphemeralUsageAndReportsUnavailableWhenRangesMissing()
    {
        // NET-EPHEMERAL-PRESSURE requires >= 8 samples, >= 15s span, >= 1000 ephemeral TCP, >= 60% share.
        var samples = Enumerable.Range(0, 20)
            .Select(index => CreateSample(
                index: index,
                elapsedSeconds: index,
                tcp: 1500,
                udp: 100,
                ephemeralTcp: 1200)) // 80% of 1500, >= 1000
            .ToArray();

        var analyzer = new NetworkEvidenceAnalyzer();
        var result = analyzer.Analyze(samples, DefaultRanges, requestedDurationSeconds: 30);

        var observation = Assert.Single(result.Observations, item => item.RuleId == "NET-EPHEMERAL-PRESSURE");
        Assert.Equal(NetworkObservationKind.DynamicPortPressure, observation.Kind);
        Assert.Equal(CausalityStatus.NotAssessed, observation.Causality);

        var missingRangesResult = analyzer.Analyze(samples, [], requestedDurationSeconds: 30);
        Assert.DoesNotContain(missingRangesResult.Observations, item => item.RuleId == "NET-EPHEMERAL-PRESSURE");
        var note = Assert.Single(missingRangesResult.Notes, item => item.RuleId == "NET-EPHEMERAL-PRESSURE");
        Assert.False(note.Evaluated);
        Assert.Equal(NetworkNoteReason.Unavailable, note.Reason);
    }

    [Fact]
    public void AttemptFailuresObservesSustainedDeltaAndRejectsCounterWrapOrRegression()
    {
        // NET-ATTEMPT-FAILURES requires >= 10 samples, >= 30s span, delta >= 50.
        var risingSamples = Enumerable.Range(0, 35)
            .Select(index => CreateSample(
                index: index,
                elapsedSeconds: index,
                tcp: 50,
                udp: 10,
                attemptFailures: (uint)(1000 + index * 2))) // 1000 -> 1068 (delta = 68 >= 50 over 34s)
            .ToArray();

        var analyzer = new NetworkEvidenceAnalyzer();
        var risingResult = analyzer.Analyze(risingSamples, DefaultRanges, requestedDurationSeconds: 60);

        var observation = Assert.Single(risingResult.Observations, item => item.RuleId == "NET-ATTEMPT-FAILURES");
        Assert.Equal(NetworkObservationKind.AttemptFailures, observation.Kind);
        Assert.Equal(68, observation.ObservedMax);
        Assert.Equal(CausalityStatus.NotAssessed, observation.Causality);

        // Counter regression / 32-bit wrap (end < start) must NEVER be interpreted as an increase.
        var wrappedSamples = Enumerable.Range(0, 35)
            .Select(index => CreateSample(
                index: index,
                elapsedSeconds: index,
                tcp: 50,
                udp: 10,
                attemptFailures: index < 20 ? 5000u : 10u))
            .ToArray();

        var wrappedResult = analyzer.Analyze(wrappedSamples, DefaultRanges, requestedDurationSeconds: 60);
        Assert.DoesNotContain(wrappedResult.Observations, item => item.RuleId == "NET-ATTEMPT-FAILURES");
        var wrappedNote = Assert.Single(wrappedResult.Notes, item => item.RuleId == "NET-ATTEMPT-FAILURES");
        Assert.False(wrappedNote.Evaluated);
        Assert.Equal(NetworkNoteReason.InvalidSamples, wrappedNote.Reason);
    }

    [Fact]
    public void SamplingGapBreaksContiguousRunsAndPreventsFalseSustainedObservations()
    {
        // Two 12-second segments separated by a 10-second gap (> MaxGapSeconds = 2.5s).
        // Neither segment reaches the 20-second minimum for NET-ENDPOINT-GROWTH.
        var firstSegment = Enumerable.Range(0, 12)
            .Select(index => CreateSample(index, index, tcp: 100 + index * 30, udp: 10));
        var secondSegment = Enumerable.Range(12, 12)
            .Select(index => CreateSample(index, index + 10.0, tcp: 100 + index * 30, udp: 10));
        var samples = firstSegment.Concat(secondSegment).ToArray();

        var analyzer = new NetworkEvidenceAnalyzer();
        var runs = analyzer.ContiguousRuns(samples);
        Assert.Equal(2, runs.Count);

        var result = analyzer.Analyze(samples, DefaultRanges, requestedDurationSeconds: 30);
        Assert.DoesNotContain(result.Observations, item => item.RuleId == "NET-ENDPOINT-GROWTH");
        var note = Assert.Single(result.Notes, item => item.RuleId == "NET-ENDPOINT-GROWTH");
        Assert.False(note.Evaluated);
        Assert.Equal(NetworkNoteReason.InsufficientSamples, note.Reason);
    }

    [Fact]
    public void SnapshotSingleSampleMarksRulesNotEvaluatedDueToInsufficientSamples()
    {
        var snapshot = new[] { CreateSample(index: 0, elapsedSeconds: 0, tcp: 500, udp: 50, ephemeralTcp: 450, attemptFailures: 900) };

        var result = new NetworkEvidenceAnalyzer().Analyze(snapshot, DefaultRanges, requestedDurationSeconds: 0);

        Assert.Empty(result.Observations);
        Assert.All(result.Notes, note => Assert.False(note.Evaluated));
        Assert.Contains(result.Notes, note => note.RuleId == "NET-ENDPOINT-GROWTH" && note.Reason == NetworkNoteReason.InsufficientSamples);
        Assert.Contains(result.Notes, note => note.RuleId == "NET-ATTEMPT-FAILURES" && note.Reason == NetworkNoteReason.InsufficientSamples);
    }

    private static NetworkSampleAggregate CreateSample(
        int index,
        double elapsedSeconds,
        int tcp,
        int udp,
        int ephemeralTcp = 20,
        uint? attemptFailures = 0,
        IReadOnlyList<NetworkStateCount>? states = null,
        IReadOnlyList<NetworkProcessCount>? processes = null) =>
        new(
            Index: index,
            TimestampUtc: StartUtc.AddSeconds(elapsedSeconds),
            ElapsedSeconds: elapsedSeconds,
            Warmup: index == 0,
            TcpConnections: tcp,
            UdpEndpoints: udp,
            ListeningTcp: 4,
            DistinctLocalPorts: Math.Max(1, tcp / 2),
            EphemeralTcpConnections: ephemeralTcp,
            EphemeralUdpEndpoints: 5,
            TcpAttemptFailures: attemptFailures,
            TcpStates: states ??
            [
                new NetworkStateCount(TcpConnectionState.Established, Math.Max(0, tcp - 4)),
                new NetworkStateCount(TcpConnectionState.Listen, 4)
            ],
            TopLocalPorts: [new NetworkPortCount(443, tcp, 1)],
            TopProcesses: processes ??
            [
                new NetworkProcessCount("1000:1", 1000, "default.exe", Math.Min(tcp, 30), Math.Min(udp, 5), 1)
            ],
            ResolvedProcesses: tcp + udp,
            UnresolvedProcesses: 0,
            DuplicateRowsIgnored: 0,
            CollectionDurationMilliseconds: 3,
            Limitations: []);
}
