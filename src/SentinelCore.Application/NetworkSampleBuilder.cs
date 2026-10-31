using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Reduz uma leitura de endpoints em um agregado por amostra. O agregado é o que a sessão
/// preserva: contagem de conexões nunca é confundida com quantidade de portas distintas.
/// </summary>
public static class NetworkSampleBuilder
{
    public static NetworkSampleAggregate Build(
        int index, DateTimeOffset timestampUtc, double elapsedSeconds, bool warmup,
        IReadOnlyList<NetworkEndpoint> endpoints, IReadOnlyList<NetworkProcessCount> processes,
        IReadOnlyList<NetworkDynamicPortRange> ranges, uint? attemptFailures,
        int duplicateRowsIgnored, long collectionDurationMilliseconds,
        IReadOnlyList<string> limitations, NetworkLimits limits)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var tcp = endpoints.Where(endpoint => endpoint.Protocol == NetworkProtocol.Tcp).ToArray();
        var udp = endpoints.Where(endpoint => endpoint.Protocol == NetworkProtocol.Udp).ToArray();

        // Um porto local pode sustentar muitas conexões: as duas contagens são eixos diferentes.
        var distinctPorts = endpoints.Select(endpoint => endpoint.LocalPort).Distinct().Count();
        var ephemeralTcp = tcp.Count(endpoint => InRange(endpoint, NetworkProtocol.Tcp, ranges));
        var ephemeralUdp = udp.Count(endpoint => InRange(endpoint, NetworkProtocol.Udp, ranges));

        var states = tcp.GroupBy(endpoint => endpoint.TcpState ?? TcpConnectionState.Unknown)
            .Select(group => new NetworkStateCount(group.Key, group.Count()))
            .OrderByDescending(state => state.Connections).ThenBy(state => state.State)
            .ToArray();

        var topPorts = endpoints.GroupBy(endpoint => endpoint.LocalPort)
            .Select(group => new NetworkPortCount(group.Key, group.Count(),
                group.Select(endpoint => endpoint.ProcessIdentity ?? $"pid:{endpoint.Pid}")
                    .Distinct(StringComparer.Ordinal).Count()))
            .OrderByDescending(port => port.Endpoints).ThenBy(port => port.Port)
            .Take(Math.Max(1, limits.MaxTopPorts)).ToArray();

        var topProcesses = processes.Take(Math.Max(1, limits.MaxTopProcesses)).ToArray();
        var resolved = endpoints.Count(endpoint => endpoint.ProcessStatus
            is NetworkProcessStatus.Resolved or NetworkProcessStatus.SystemProcess);
        var unresolved = endpoints.Count - resolved;

        return new NetworkSampleAggregate(index, timestampUtc, Math.Round(elapsedSeconds, 3), warmup,
            tcp.Length, udp.Length, tcp.Count(endpoint => endpoint.TcpState == TcpConnectionState.Listen),
            distinctPorts, ephemeralTcp, ephemeralUdp, attemptFailures,
            states, topPorts, topProcesses, resolved, unresolved, duplicateRowsIgnored,
            collectionDurationMilliseconds,
            limitations.Distinct(StringComparer.Ordinal).Take(12).ToArray());
    }

    internal static bool InRange(NetworkEndpoint endpoint, NetworkProtocol protocol,
        IReadOnlyList<NetworkDynamicPortRange> ranges) =>
        ranges.Any(range => range.Protocol == protocol && range.Family == endpoint.Family
            && range.Contains(endpoint.LocalPort));

    public static (int Tcp, int Udp, int Listening, int DistinctPorts,
        int EphemeralTcp, int EphemeralUdp) Counts(IReadOnlyList<NetworkEndpoint> endpoints,
        IReadOnlyList<NetworkDynamicPortRange> ranges)
    {
        var tcp = endpoints.Where(endpoint => endpoint.Protocol == NetworkProtocol.Tcp).ToArray();
        var udp = endpoints.Where(endpoint => endpoint.Protocol == NetworkProtocol.Udp).ToArray();
        return (tcp.Length, udp.Length, tcp.Count(endpoint => endpoint.TcpState == TcpConnectionState.Listen),
            endpoints.Select(endpoint => endpoint.LocalPort).Distinct().Count(),
            tcp.Count(endpoint => InRange(endpoint, NetworkProtocol.Tcp, ranges)),
            udp.Count(endpoint => InRange(endpoint, NetworkProtocol.Udp, ranges)));
    }
}
