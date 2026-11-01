using System.Buffers.Binary;
using System.Net;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Windows.Network;
using Xunit;

namespace SentinelCore.Tests;

public sealed class NetworkEndpointCollectorTests
{
    private static readonly DateTimeOffset ObservedUtc = new(2026, 10, 31, 15, 45, 0, TimeSpan.Zero);

    [Fact]
    public void ReadTcp4ParsesBigEndianPortsNetworkOrderAddressesLittleEndianStateAndPid()
    {
        // 1 row of MIB_TCPROW_OWNER_PID: 4-byte count + 24-byte row = 28 bytes
        var buffer = new byte[28];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), 1);
        var row = buffer.AsSpan(4, 24);

        // State = 5 (Established) little-endian
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(0, 4), 5);
        // Local IP = 192.168.1.50
        row[4] = 192; row[5] = 168; row[6] = 1; row[7] = 50;
        // Local Port = 80 (0x00, 0x50 in network byte order in the first two bytes)
        row[8] = 0x00; row[9] = 0x50; row[10] = 0x00; row[11] = 0x00;
        // Remote IP = 93.184.216.34
        row[12] = 93; row[13] = 184; row[14] = 216; row[15] = 34;
        // Remote Port = 443 (0x01, 0xBB in network byte order)
        row[16] = 0x01; row[17] = 0xBB; row[18] = 0x00; row[19] = 0x00;
        // Owning PID = 4321 little-endian
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(20, 4), 4321);

        var parsed = Assert.Single(NativeNetwork.ReadTcp4(buffer, buffer.Length));
        Assert.Equal(5u, parsed.State);
        Assert.Equal(TcpConnectionState.Established, NativeNetwork.MapTcpState(parsed.State));
        Assert.Equal(IPAddress.Parse("192.168.1.50"), parsed.Local);
        Assert.Equal((ushort)80, parsed.LocalPort);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), parsed.Remote);
        Assert.Equal((ushort)443, parsed.RemotePort);
        Assert.Equal(4321u, parsed.Pid);
    }

    // O parsing de MIB_TCP6ROW_OWNER_PID é protegido por NativeTcp6LayoutTests, cuja fixture segue o layout
    // oficial da Microsoft. A versão anterior deste teste escrevia a fixture nos mesmos offsets do parser
    // (escopo remoto @36, porta remota @40) e por isso não detectava o defeito D1.

    [Fact]
    public void ReadTcp4AndUdpReadersRejectCountsThatExceedTheValidLength()
    {
        var tcp4 = new byte[4 + 24];
        BinaryPrimitives.WriteInt32LittleEndian(tcp4, 2);
        Assert.Throws<ArgumentException>(() => NativeNetwork.ReadTcp4(tcp4, tcp4.Length).ToArray());

        var udp4 = new byte[4 + 12];
        BinaryPrimitives.WriteInt32LittleEndian(udp4, 2);
        Assert.Throws<ArgumentException>(() => NativeNetwork.ReadUdp4(udp4, udp4.Length).ToArray());

        var udp6 = new byte[4 + 28];
        BinaryPrimitives.WriteInt32LittleEndian(udp6, 2);
        Assert.Throws<ArgumentException>(() => NativeNetwork.ReadUdp6(udp6, udp6.Length).ToArray());
    }

    [Fact]
    public void ReadUdp4AndUdp6ParseLocalAddressBigEndianPortAndPid()
    {
        // UDP4: 4-byte count + 12-byte row = 16 bytes
        var udp4Buffer = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(udp4Buffer.AsSpan(0, 4), 1);
        var row4 = udp4Buffer.AsSpan(4, 12);
        row4[0] = 127; row4[1] = 0; row4[2] = 0; row4[3] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(row4.Slice(4, 2), 5353);
        BinaryPrimitives.WriteUInt32LittleEndian(row4.Slice(8, 4), 1100);

        var udp4 = Assert.Single(NativeNetwork.ReadUdp4(udp4Buffer, udp4Buffer.Length));
        Assert.Equal(IPAddress.Loopback, udp4.Local);
        Assert.Equal((ushort)5353, udp4.LocalPort);
        Assert.Equal(1100u, udp4.Pid);

        // UDP6: 4-byte count + 28-byte row = 32 bytes
        var udp6Buffer = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(udp6Buffer.AsSpan(0, 4), 1);
        var row6 = udp6Buffer.AsSpan(4, 28);
        IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(row6.Slice(0, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(row6.Slice(16, 4), 0);
        BinaryPrimitives.WriteUInt16BigEndian(row6.Slice(20, 2), 546);
        BinaryPrimitives.WriteUInt32LittleEndian(row6.Slice(24, 4), 2200);

        var udp6 = Assert.Single(NativeNetwork.ReadUdp6(udp6Buffer, udp6Buffer.Length));
        Assert.Equal(IPAddress.IPv6Loopback, udp6.Local);
        Assert.Equal((ushort)546, udp6.LocalPort);
        Assert.Equal(2200u, udp6.Pid);
    }

    [Fact]
    public void MapTcpStateMapsAllTwelveDocumentedValuesAndOutOfRangeToUnknown()
    {
        Assert.Equal(TcpConnectionState.Closed, NativeNetwork.MapTcpState(1));
        Assert.Equal(TcpConnectionState.Listen, NativeNetwork.MapTcpState(2));
        Assert.Equal(TcpConnectionState.SynSent, NativeNetwork.MapTcpState(3));
        Assert.Equal(TcpConnectionState.SynReceived, NativeNetwork.MapTcpState(4));
        Assert.Equal(TcpConnectionState.Established, NativeNetwork.MapTcpState(5));
        Assert.Equal(TcpConnectionState.FinWait1, NativeNetwork.MapTcpState(6));
        Assert.Equal(TcpConnectionState.FinWait2, NativeNetwork.MapTcpState(7));
        Assert.Equal(TcpConnectionState.CloseWait, NativeNetwork.MapTcpState(8));
        Assert.Equal(TcpConnectionState.Closing, NativeNetwork.MapTcpState(9));
        Assert.Equal(TcpConnectionState.LastAck, NativeNetwork.MapTcpState(10));
        Assert.Equal(TcpConnectionState.TimeWait, NativeNetwork.MapTcpState(11));
        Assert.Equal(TcpConnectionState.DeleteTcb, NativeNetwork.MapTcpState(12));
        Assert.Equal(TcpConnectionState.Unknown, NativeNetwork.MapTcpState(0));
        Assert.Equal(TcpConnectionState.Unknown, NativeNetwork.MapTcpState(999));
    }

    [Fact]
    public void SummarizeGroupsByProcessIdentityOrPidAndCountsDistinctListeningPorts()
    {
        var endpoints = new[]
        {
            new NetworkEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, "0.0.0.0", 443, "0.0.0.0", 0,
                TcpConnectionState.Listen, 500, "500:1", "web.exe", NetworkProcessStatus.Resolved, ObservedUtc, "Test"),
            new NetworkEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv6, "::", 443, "::", 0,
                TcpConnectionState.Listen, 500, "500:1", "web.exe", NetworkProcessStatus.Resolved, ObservedUtc, "Test"),
            new NetworkEndpoint(NetworkProtocol.Tcp, NetworkAddressFamily.IPv4, "10.0.0.2", 443, "10.0.0.9", 51000,
                TcpConnectionState.Established, 500, "500:1", "web.exe", NetworkProcessStatus.Resolved, ObservedUtc, "Test"),
            new NetworkEndpoint(NetworkProtocol.Udp, NetworkAddressFamily.IPv4, "127.0.0.1", 9000, null, null,
                null, 700, null, null, NetworkProcessStatus.AccessDenied, ObservedUtc, "Test")
        };

        var summary = NetworkEndpointCollector.Summarize(endpoints);
        Assert.Equal(2, summary.Count);

        var resolved = summary[0];
        Assert.Equal("500:1", resolved.Identity);
        Assert.Equal(500, resolved.Pid);
        Assert.Equal("web.exe", resolved.Name);
        Assert.Equal(3, resolved.TcpConnections);
        Assert.Equal(0, resolved.UdpEndpoints);
        // Port 443 listening on both IPv4 and IPv6 counts as 1 distinct listening port for that process.
        Assert.Equal(1, resolved.ListeningPorts);

        var denied = summary[1];
        Assert.Null(denied.Identity);
        Assert.Equal(700, denied.Pid);
        Assert.Equal(0, denied.TcpConnections);
        Assert.Equal(1, denied.UdpEndpoints);
    }

    [Fact]
    public async Task DynamicPortRangeReaderDistinguishesExplicitConfigurationFromWindowsDefaultAndRejectsInvalidRanges()
    {
        var explicitSource = new FakePortRangeSource(
            ipv4: new PortRangeConfiguration(
                StartPort: 32768,
                PortCount: 16384,
                Reserved: [new NetworkPortRangeExclusion(33000, 33010, "ReservedPorts (Registro)")],
                Limitations: []),
            ipv6: new PortRangeConfiguration(
                StartPort: null,
                PortCount: null,
                Reserved: [],
                Limitations: []));

        var reader = new DynamicPortRangeReader(explicitSource);
        var reading = await reader.ReadAsync(new NetworkCollectionContext(Guid.NewGuid()), CancellationToken.None);

        Assert.False(reading.Unsupported);
        Assert.Equal(4, reading.PortRanges.Count);

        var tcpV4 = Assert.Single(reading.PortRanges, r => r.Protocol == NetworkProtocol.Tcp && r.Family == NetworkAddressFamily.IPv4);
        Assert.Equal(32768, tcpV4.StartPort);
        Assert.Equal(16384, tcpV4.PortCount);
        Assert.False(tcpV4.IsWindowsDefault);
        Assert.Single(tcpV4.Exclusions);

        var tcpV6 = Assert.Single(reading.PortRanges, r => r.Protocol == NetworkProtocol.Tcp && r.Family == NetworkAddressFamily.IPv6);
        Assert.Equal(DynamicPortRangeReader.WindowsDefaultStart, tcpV6.StartPort);
        Assert.Equal(DynamicPortRangeReader.WindowsDefaultCount, tcpV6.PortCount);
        Assert.True(tcpV6.IsWindowsDefault);
        Assert.Contains(tcpV6.Limitations, item => item.Contains("padrão documentado do Windows", StringComparison.Ordinal));

        var invalidSource = new FakePortRangeSource(
            ipv4: new PortRangeConfiguration(60000, 10000, [], []), // 60000 + 10000 - 1 > 65535
            ipv6: new PortRangeConfiguration(0, 100, [], []));
        var invalidReading = await new DynamicPortRangeReader(invalidSource)
            .ReadAsync(new NetworkCollectionContext(Guid.NewGuid()), CancellationToken.None);

        Assert.True(invalidReading.Unsupported);
        Assert.Empty(invalidReading.PortRanges);
        Assert.Contains(invalidReading.Limitations, item => item.Contains("inválido", StringComparison.Ordinal));
    }

    [Fact]
    public void TryParseReservedParsesSinglePortsAndRangesAndRejectsMalformedEntries()
    {
        Assert.True(RegistryDynamicPortRangeSource.TryParseReserved("50000-50050", out var range));
        Assert.Equal(50000, range.FromPort);
        Assert.Equal(50050, range.ToPort);

        Assert.True(RegistryDynamicPortRangeSource.TryParseReserved("54321", out var single));
        Assert.Equal(54321, single.FromPort);
        Assert.Equal(54321, single.ToPort);

        Assert.False(RegistryDynamicPortRangeSource.TryParseReserved("", out _));
        Assert.False(RegistryDynamicPortRangeSource.TryParseReserved("50100-50050", out _));
        Assert.False(RegistryDynamicPortRangeSource.TryParseReserved("0-100", out _));
        Assert.False(RegistryDynamicPortRangeSource.TryParseReserved("50000-70000", out _));
    }

    [Fact]
    public void ProcessResolverResolvesSystemIdleSystemAndCurrentProcessWithoutFullPath()
    {
        var resolver = new NetworkProcessResolver();
        resolver.BeginTick();

        var idle = resolver.Resolve(0);
        Assert.Equal("0:system", idle.Identity);
        Assert.Equal(NetworkProcessStatus.SystemProcess, idle.Status);

        var system = resolver.Resolve(4);
        Assert.Equal("4:system", system.Identity);
        Assert.Equal(NetworkProcessStatus.SystemProcess, system.Status);

        var self = resolver.Resolve(Environment.ProcessId);
        Assert.Equal(NetworkProcessStatus.Resolved, self.Status);
        Assert.NotNull(self.Identity);
        Assert.NotNull(self.Name);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, self.Name!);

        resolver.EndTick();
    }

    private sealed class FakePortRangeSource(
        PortRangeConfiguration ipv4,
        PortRangeConfiguration ipv6) : IDynamicPortRangeSource
    {
        public PortRangeConfiguration Read(NetworkAddressFamily family) =>
            family == NetworkAddressFamily.IPv6 ? ipv6 : ipv4;
    }
}
