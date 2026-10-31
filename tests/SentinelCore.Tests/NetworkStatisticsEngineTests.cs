using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows.Network;
using Xunit;

namespace SentinelCore.Tests;

public sealed class NetworkStatisticsEngineTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 10, 31, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void EmptySamplesReturnNullRatesAndZeroCountsWithLimitation()
    {
        var engine = new NetworkStatisticsEngine();

        var stats = engine.Compute([]);

        Assert.Equal(0, stats.TotalSamples);
        Assert.Equal(0, stats.ValidSamples);
        Assert.Equal(0, stats.PeakTcpConnections);
        Assert.Null(stats.MeanTcpConnections);
        Assert.Null(stats.TcpGrowthPercent);
        Assert.Equal(0, stats.PeakUdpEndpoints);
        Assert.Null(stats.MeanUdpEndpoints);
        Assert.Null(stats.FinalEphemeralSharePercent);
        Assert.Empty(stats.FinalStates);
        Assert.Empty(stats.FinalTopLocalPorts);
        Assert.Empty(stats.FinalTopProcesses);
        Assert.Contains(stats.Limitations, item => item.Contains("Nenhuma amostra coletada", StringComparison.Ordinal));
    }

    [Fact]
    public void SingleWarmupSampleUsesSnapshotValuesAndNullGrowth()
    {
        var sample = CreateSample(
            index: 0,
            warmup: true,
            tcp: 40,
            udp: 10,
            listening: 6,
            distinctPorts: 32,
            ephemeralTcp: 20,
            ephemeralUdp: 5,
            established: 30,
            timeWait: 4);

        var stats = new NetworkStatisticsEngine().Compute([sample]);

        Assert.Equal(1, stats.TotalSamples);
        Assert.Equal(1, stats.ValidSamples);
        Assert.Equal(40, stats.PeakTcpConnections);
        Assert.Equal(40d, stats.MeanTcpConnections);
        Assert.Null(stats.TcpGrowthPercent);
        Assert.Equal(10, stats.PeakUdpEndpoints);
        Assert.Equal(10d, stats.MeanUdpEndpoints);
        Assert.Equal(40, stats.FinalTcpConnections);
        Assert.Equal(10, stats.FinalUdpEndpoints);
        Assert.Equal(32, stats.FinalDistinctLocalPorts);
        Assert.Equal(25, stats.FinalEphemeralEndpoints);
        Assert.Equal(50.0, stats.FinalEphemeralSharePercent);
        Assert.Equal(6, stats.FinalListening);
        Assert.Equal(30, stats.FinalEstablished);
        Assert.Equal(4, stats.FinalTimeWait);
    }

    [Fact]
    public void MultipleSamplesExcludeWarmupFromMeansAndIncludeBaselineInGrowth()
    {
        var samples = new[]
        {
            CreateSample(index: 0, warmup: true, tcp: 20, udp: 10, listening: 4, distinctPorts: 20, ephemeralTcp: 10, ephemeralUdp: 2, established: 15, timeWait: 1),
            CreateSample(index: 1, warmup: false, tcp: 30, udp: 12, listening: 4, distinctPorts: 28, ephemeralTcp: 18, ephemeralUdp: 4, established: 22, timeWait: 4),
            CreateSample(index: 2, warmup: false, tcp: 50, udp: 18, listening: 5, distinctPorts: 42, ephemeralTcp: 34, ephemeralUdp: 6, established: 35, timeWait: 10)
        };

        var stats = new NetworkStatisticsEngine().Compute(samples);

        Assert.Equal(3, stats.TotalSamples);
        Assert.Equal(2, stats.ValidSamples);
        Assert.Equal(50, stats.PeakTcpConnections);
        // Mean of non-warmup samples: (30 + 50) / 2 = 40.0
        Assert.Equal(40.0, stats.MeanTcpConnections);
        // Growth from index 0 (20) to index 2 (50): (50 - 20) * 100 / 20 = 150.0%
        Assert.Equal(150.0, stats.TcpGrowthPercent);
        Assert.Equal(18, stats.PeakUdpEndpoints);
        // Mean of non-warmup UDP: (12 + 18) / 2 = 15.0
        Assert.Equal(15.0, stats.MeanUdpEndpoints);
        Assert.Equal(50, stats.FinalTcpConnections);
        Assert.Equal(18, stats.FinalUdpEndpoints);
        Assert.Equal(40, stats.FinalEphemeralEndpoints);
        // 40 / (50 + 18) = 58.8%
        Assert.Equal(58.8, stats.FinalEphemeralSharePercent);
        Assert.Equal(5, stats.FinalListening);
        Assert.Equal(35, stats.FinalEstablished);
        Assert.Equal(10, stats.FinalTimeWait);
    }

    [Fact]
    public void ZeroBaselineTcpConnectionsReturnsNullGrowthWithLimitation()
    {
        var samples = new[]
        {
            CreateSample(index: 0, warmup: true, tcp: 0, udp: 4, listening: 0, distinctPorts: 4, ephemeralTcp: 0, ephemeralUdp: 2, established: 0, timeWait: 0),
            CreateSample(index: 1, warmup: false, tcp: 15, udp: 4, listening: 2, distinctPorts: 14, ephemeralTcp: 10, ephemeralUdp: 2, established: 12, timeWait: 1)
        };

        var stats = new NetworkStatisticsEngine().Compute(samples);

        Assert.Null(stats.TcpGrowthPercent);
        Assert.Contains(stats.Limitations, item => item.Contains("Primeira amostra sem conexões TCP", StringComparison.Ordinal));
    }

    [Fact]
    public void FinalSampleWithZeroTotalEndpointsReturnsNullEphemeralShareAndPreservesDuplicateWarning()
    {
        var sample = CreateSample(
            index: 0,
            warmup: false,
            tcp: 0,
            udp: 0,
            listening: 0,
            distinctPorts: 0,
            ephemeralTcp: 0,
            ephemeralUdp: 0,
            established: 0,
            timeWait: 0,
            duplicateRowsIgnored: 3);

        var stats = new NetworkStatisticsEngine().Compute([sample]);

        Assert.Null(stats.FinalEphemeralSharePercent);
        Assert.Contains(stats.Limitations, item => item.Contains("3 linha(s) duplicada(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void UnlimitedConnectionsConstantEqualsUintMaxValue()
    {
        Assert.Equal(uint.MaxValue, NetworkStatisticsCollector.UnlimitedConnections);
    }

    private static NetworkSampleAggregate CreateSample(
        int index,
        bool warmup,
        int tcp,
        int udp,
        int listening,
        int distinctPorts,
        int ephemeralTcp,
        int ephemeralUdp,
        int established,
        int timeWait,
        int duplicateRowsIgnored = 0) =>
        new(
            Index: index,
            TimestampUtc: BaseUtc.AddSeconds(index),
            ElapsedSeconds: index,
            Warmup: warmup,
            TcpConnections: tcp,
            UdpEndpoints: udp,
            ListeningTcp: listening,
            DistinctLocalPorts: distinctPorts,
            EphemeralTcpConnections: ephemeralTcp,
            EphemeralUdpEndpoints: ephemeralUdp,
            TcpAttemptFailures: 0,
            TcpStates:
            [
                new NetworkStateCount(TcpConnectionState.Established, established),
                new NetworkStateCount(TcpConnectionState.Listen, listening),
                new NetworkStateCount(TcpConnectionState.TimeWait, timeWait)
            ],
            TopLocalPorts: [new NetworkPortCount(443, established, 1)],
            TopProcesses: [new NetworkProcessCount("100:1", 100, "app.exe", tcp, udp, listening)],
            ResolvedProcesses: tcp + udp,
            UnresolvedProcesses: 0,
            DuplicateRowsIgnored: duplicateRowsIgnored,
            CollectionDurationMilliseconds: 4,
            Limitations: []);
}
