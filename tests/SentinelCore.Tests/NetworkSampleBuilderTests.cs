using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class NetworkSampleBuilderTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 31, 14, 15, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<NetworkDynamicPortRange> DefaultRanges =
    [
        new NetworkDynamicPortRange(
            Protocol: NetworkProtocol.Tcp,
            Family: NetworkAddressFamily.IPv4,
            StartPort: 49152,
            PortCount: 16384,
            Source: "WindowsDefault",
            IsWindowsDefault: true,
            Exclusions: [],
            Limitations: []),
        new NetworkDynamicPortRange(
            Protocol: NetworkProtocol.Udp,
            Family: NetworkAddressFamily.IPv4,
            StartPort: 49152,
            PortCount: 16384,
            Source: "WindowsDefault",
            IsWindowsDefault: true,
            Exclusions: [],
            Limitations: []),
        new NetworkDynamicPortRange(
            Protocol: NetworkProtocol.Tcp,
            Family: NetworkAddressFamily.IPv6,
            StartPort: 49152,
            PortCount: 16384,
            Source: "WindowsDefault",
            IsWindowsDefault: true,
            Exclusions: [],
            Limitations: [])
    ];

    [Fact]
    public void BuildSeparatesConnectionCountFromDistinctLocalPortsAndClassifiesEphemeralRanges()
    {
        var endpoints = new[]
        {
            CreateEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, 443, 51001, TcpConnectionState.Established, 100, "100:1", "server.exe"),
            CreateEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, 443, 51002, TcpConnectionState.Established, 100, "100:1", "server.exe"),
            CreateEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, 443, 0, TcpConnectionState.Listen, 100, "100:1", "server.exe"),
            CreateEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, 50000, 443, TcpConnectionState.TimeWait, 200, "200:1", "client.exe"),
            CreateEndpoint(NetworkProtocol.Udp, NetworkAddressFamily.IPv4, 5353, null, null, 300, "300:1", "mdns.exe"),
            CreateEndpoint(NetworkProtocol.Udp, NetworkAddressFamily.IPv4, 55000, null, null, 200, "200:1", "client.exe")
        };

        var processes = new[]
        {
            new NetworkProcessCount("100:1", 100, "server.exe", 3, 0, 1),
            new NetworkProcessCount("200:1", 200, "client.exe", 1, 1, 0),
            new NetworkProcessCount("300:1", 300, "mdns.exe", 0, 1, 0)
        };

        var aggregate = NetworkSampleBuilder.Build(
            index: 0,
            timestampUtc: NowUtc,
            elapsedSeconds: 0.1234,
            warmup: true,
            endpoints: endpoints,
            processes: processes,
            ranges: DefaultRanges,
            attemptFailures: 12,
            duplicateRowsIgnored: 2,
            collectionDurationMilliseconds: 9,
            limitations: ["limitação A", "limitação A", "limitação B"],
            limits: NetworkLimits.Default);

        Assert.Equal(0, aggregate.Index);
        Assert.True(aggregate.Warmup);
        Assert.Equal(0.123, aggregate.ElapsedSeconds);
        Assert.Equal(4, aggregate.TcpConnections);
        Assert.Equal(2, aggregate.UdpEndpoints);
        Assert.Equal(1, aggregate.ListeningTcp);
        // Three TCP endpoints share local port 443; distinct local ports across all 6 endpoints are 443, 50000, 5353, 55000 (4 total).
        Assert.Equal(4, aggregate.DistinctLocalPorts);
        Assert.Equal(1, aggregate.EphemeralTcpConnections);
        Assert.Equal(1, aggregate.EphemeralUdpEndpoints);
        Assert.Equal(12u, aggregate.TcpAttemptFailures);
        Assert.Equal(2, aggregate.DuplicateRowsIgnored);
        Assert.Equal(2, aggregate.Limitations.Count);

        var port443 = Assert.Single(aggregate.TopLocalPorts, port => port.Port == 443);
        Assert.Equal(3, port443.Endpoints);
        Assert.Equal(1, port443.DistinctProcesses);
    }

    [Fact]
    public void BuildGroupsAndOrdersTcpStatesAndMapsNullStateToUnknown()
    {
        var endpoints = new[]
        {
            CreateEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, 50001, 443, TcpConnectionState.Established, 10, "10:1", "a.exe"),
            CreateEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, 50002, 443, TcpConnectionState.Established, 10, "10:1", "a.exe"),
            CreateEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, 50003, 443, TcpConnectionState.TimeWait, 10, "10:1", "a.exe"),
            CreateEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, 50004, 443, null, 10, "10:1", "a.exe")
        };

        var aggregate = NetworkSampleBuilder.Build(
            index: 1,
            timestampUtc: NowUtc,
            elapsedSeconds: 1.0,
            warmup: false,
            endpoints: endpoints,
            processes: [],
            ranges: DefaultRanges,
            attemptFailures: null,
            duplicateRowsIgnored: 0,
            collectionDurationMilliseconds: 3,
            limitations: [],
            limits: NetworkLimits.Default);

        Assert.Equal(3, aggregate.TcpStates.Count);
        Assert.Equal(TcpConnectionState.Established, aggregate.TcpStates[0].State);
        Assert.Equal(2, aggregate.TcpStates[0].Connections);
        Assert.Contains(aggregate.TcpStates, state => state.State == TcpConnectionState.TimeWait && state.Connections == 1);
        Assert.Contains(aggregate.TcpStates, state => state.State == TcpConnectionState.Unknown && state.Connections == 1);
    }

    [Fact]
    public void BuildCountsResolvedAndSystemProcessesSeparatelyFromUnresolvedStatuses()
    {
        var endpoints = new[]
        {
            CreateEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, 50100, 443, TcpConnectionState.Established, 4, "4:system", "System", NetworkProcessStatus.SystemProcess),
            CreateEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, 50101, 443, TcpConnectionState.Established, 1200, "1200:99", "browser.exe", NetworkProcessStatus.Resolved),
            CreateEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, 50102, 443, TcpConnectionState.CloseWait, 2200, null, null, NetworkProcessStatus.AccessDenied),
            CreateEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, 50103, 443, TcpConnectionState.TimeWait, 3200, null, null, NetworkProcessStatus.Exited),
            CreateEndpoint(NetworkProtocol.Udp, NetworkAddressFamily.IPv4, 50104, null, null, 4200, null, null, NetworkProcessStatus.Unavailable)
        };

        var aggregate = NetworkSampleBuilder.Build(
            index: 2,
            timestampUtc: NowUtc,
            elapsedSeconds: 2.0,
            warmup: false,
            endpoints: endpoints,
            processes:
            [
                new NetworkProcessCount("1200:99", 1200, "browser.exe", 1, 0, 0),
                new NetworkProcessCount(null, 2200, null, 1, 0, 0)
            ],
            ranges: DefaultRanges,
            attemptFailures: 0,
            duplicateRowsIgnored: 0,
            collectionDurationMilliseconds: 4,
            limitations: [],
            limits: NetworkLimits.Default);

        Assert.Equal(2, aggregate.ResolvedProcesses);
        Assert.Equal(3, aggregate.UnresolvedProcesses);
        Assert.All(aggregate.TopProcesses, p => Assert.True(p.Identity is not null || p.Pid > 0));
    }

    [Fact]
    public void BuildRespectsConfiguredMaxTopPortsAndMaxTopProcesses()
    {
        var endpoints = Enumerable.Range(0, 10)
            .Select(i => CreateEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, (ushort)(40000 + i), 443, TcpConnectionState.Established, 100 + i, $"{100 + i}:1", $"proc{i}.exe"))
            .ToArray();
        var processes = Enumerable.Range(0, 10)
            .Select(i => new NetworkProcessCount($"{100 + i}:1", 100 + i, $"proc{i}.exe", 1, 0, 0))
            .ToArray();
        var customLimits = new NetworkLimits { MaxTopPorts = 3, MaxTopProcesses = 4 };

        var aggregate = NetworkSampleBuilder.Build(
            index: 0,
            timestampUtc: NowUtc,
            elapsedSeconds: 0,
            warmup: true,
            endpoints: endpoints,
            processes: processes,
            ranges: DefaultRanges,
            attemptFailures: null,
            duplicateRowsIgnored: 0,
            collectionDurationMilliseconds: 2,
            limitations: [],
            limits: customLimits);

        Assert.Equal(3, aggregate.TopLocalPorts.Count);
        Assert.Equal(4, aggregate.TopProcesses.Count);
    }

    [Fact]
    public void CountsAndDynamicPortRangeContainsMatchProtocolAndAddressFamilyBoundaries()
    {
        var range = DefaultRanges[0]; // TCP IPv4 49152..65535
        Assert.Equal(65535, range.EndPort);
        Assert.False(range.Contains(49151));
        Assert.True(range.Contains(49152));
        Assert.True(range.Contains(65535));
        Assert.False(range.Contains(65536));

        var endpoints = new[]
        {
            CreateEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv6, 50000, 443, TcpConnectionState.Established, 10, "10:1", "ipv6.exe"),
            CreateEndpoint(NetworkProtocol.Udp, NetworkAddressFamily.IPv6, 50000, null, null, 10, "10:1", "ipv6.exe") // UDP IPv6 range is not in DefaultRanges
        };

        var counts = NetworkSampleBuilder.Counts(endpoints, DefaultRanges);
        Assert.Equal(1, counts.Tcp);
        Assert.Equal(1, counts.Udp);
        Assert.Equal(0, counts.Listening);
        Assert.Equal(1, counts.DistinctPorts);
        Assert.Equal(1, counts.EphemeralTcp);
        Assert.Equal(0, counts.EphemeralUdp);
    }

    private static NetworkEndpoint CreateEndpoint(
        NetworkProtocol protocol,
        NetworkAddressFamily family,
        ushort localPort,
        ushort? remotePort,
        TcpConnectionState? state,
        int pid,
        string? identity,
        string? name,
        NetworkProcessStatus status = NetworkProcessStatus.Resolved) =>
        new(
            Protocol: protocol,
            Family: family,
            LocalAddress: family == NetworkAddressFamily.IPv6 ? "::1" : "127.0.0.1",
            LocalPort: localPort,
            RemoteAddress: remotePort.HasValue ? (family == NetworkAddressFamily.IPv6 ? "::1" : "127.0.0.1") : null,
            RemotePort: remotePort,
            TcpState: state,
            Pid: pid,
            ProcessIdentity: identity,
            ProcessName: name,
            ProcessStatus: status,
            ObservedUtc: NowUtc,
            Source: "Test");
}
