using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows.Network;

/// <summary>
/// Estatísticas de transporte por família com <c>GetTcpStatisticsEx</c> e <c>GetUdpStatisticsEx</c>.
/// Os contadores são cumulativos desde o boot e têm 32 bits: uma leitura isolada não prova
/// esgotamento e um valor alto não prova defeito do Windows.
/// </summary>
internal sealed class NetworkStatisticsCollector : INetworkSampleCollector
{
    public string Id => "network-statistics";
    public string Version => "1.0.0";
    public string Source => "GetTcpStatisticsEx/GetUdpStatisticsEx (IP Helper)";
    public NetworkSourceCategory Category => NetworkSourceCategory.Statistics;

    /// <summary><c>MIB_TCPSTATS.dwMaxConn</c> usa <c>0xFFFFFFFF</c> para "sem limite".</summary>
    internal const uint UnlimitedConnections = uint.MaxValue;

    public Task<NetworkSampleReading> ReadAsync(NetworkCollectionContext context,
        DateTimeOffset timestampUtc, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var limitations = new List<string>();

        var tcp4 = ReadTcp(NativeNetwork.AfInet, NetworkAddressFamily.IPv4, timestampUtc, limitations);
        var tcp6 = ReadTcp(NativeNetwork.AfInet6, NetworkAddressFamily.IPv6, timestampUtc, limitations);
        var udp4 = ReadUdp(NativeNetwork.AfInet, NetworkAddressFamily.IPv4, timestampUtc, limitations);
        var udp6 = ReadUdp(NativeNetwork.AfInet6, NetworkAddressFamily.IPv6, timestampUtc, limitations);

        if (tcp4 is null && tcp6 is null)
            limitations.Add("Estatísticas TCP indisponíveis para IPv4 e IPv6.");
        if (udp4 is null && udp6 is null)
            limitations.Add("Estatísticas UDP indisponíveis para IPv4 e IPv6.");

        var unsupported = tcp4 is null && tcp6 is null && udp4 is null && udp6 is null;
        // O resumo informa a família com estatística disponível; a sessão preserva IPv4 quando ambos existem.
        return new NetworkSampleReading(Id, unsupported, [], [], tcp4 ?? tcp6, udp4 ?? udp6, 0,
            limitations.Distinct(StringComparer.Ordinal).ToArray());
    }, cancellationToken);

    private static NetworkTcpStatistics? ReadTcp(int family, NetworkAddressFamily addressFamily,
        DateTimeOffset at, List<string> limitations)
    {
        if (!NativeNetwork.TryReadTcpStatistics(family, out var stats))
        {
            limitations.Add($"GetTcpStatisticsEx({addressFamily}) indisponível; valores de TCP omitidos.");
            return null;
        }
        var familyLimitations = new List<string>
        {
            "Contadores cumulativos desde o boot e com 32 bits; uma leitura não representa taxa nem histórico."
        };
        long? maxConnections = stats.MaxConn == UnlimitedConnections ? null : stats.MaxConn;
        if (maxConnections is null)
            familyLimitations.Add("dwMaxConn sinaliza 'sem limite de conexões' e não é convertido em número.");
        return new NetworkTcpStatistics(addressFamily, stats.RtoAlgorithm, stats.RtoMin, stats.RtoMax,
            maxConnections, stats.ActiveOpens, stats.PassiveOpens, stats.AttemptFails, stats.EstabResets,
            stats.CurrEstab, stats.InSegs, stats.OutSegs, stats.RetransSegs, stats.InErrs, stats.OutRsts,
            stats.NumConns, at, "GetTcpStatisticsEx", familyLimitations);
    }

    private static NetworkUdpStatistics? ReadUdp(int family, NetworkAddressFamily addressFamily,
        DateTimeOffset at, List<string> limitations)
    {
        if (!NativeNetwork.TryReadUdpStatistics(family, out var stats))
        {
            limitations.Add($"GetUdpStatisticsEx({addressFamily}) indisponível; valores de UDP omitidos.");
            return null;
        }
        return new NetworkUdpStatistics(addressFamily, stats.InDatagrams, stats.NoPorts, stats.InErrors,
            stats.OutDatagrams, stats.NumAddrs, at, "GetUdpStatisticsEx",
            ["Contadores cumulativos desde o boot e com 32 bits; não representam tráfego atual."]);
    }
}
