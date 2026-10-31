using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>
/// Agrega os agregados por amostra em um resumo de sessão. Valores ausentes permanecem nulos;
/// zero não substitui indisponível, e crescimento só existe com duas amostras comparáveis.
/// </summary>
public sealed class NetworkStatisticsEngine
{
    public NetworkStatistics Compute(IReadOnlyList<NetworkSampleAggregate> samples, NetworkLimits? limits = null)
    {
        var settings = limits ?? NetworkLimits.Default;
        var limitations = new List<string>();
        if (samples.Count == 0)
        {
            limitations.Add("Nenhuma amostra coletada; resumo indisponível.");
            return new NetworkStatistics(0, 0, 0, null, null, 0, null,
                0, 0, 0, 0, null, 0, 0, 0, [], [], [], limitations);
        }

        var ordered = samples.OrderBy(sample => sample.Index).ToArray();
        var valid = ordered.Where(sample => !sample.Warmup).ToArray();
        if (valid.Length == 0) valid = ordered;

        var meanTcp = valid.Average(sample => (double)sample.TcpConnections);
        var meanUdp = valid.Average(sample => (double)sample.UdpEndpoints);
        double? growth = null;
        if (ordered.Length >= 2 && ordered[0].TcpConnections > 0)
        {
            var baseline = ordered[0].TcpConnections;
            growth = Math.Round((ordered[^1].TcpConnections - baseline) * 100d / baseline, 1);
        }
        else if (ordered.Length >= 2)
            limitations.Add("Primeira amostra sem conexões TCP; crescimento percentual não é calculável.");

        var final = ordered[^1];
        var total = final.TcpConnections + final.UdpEndpoints;
        double? ephemeralShare = total > 0
            ? Math.Round((final.EphemeralTcpConnections + final.EphemeralUdpEndpoints) * 100d / total, 1)
            : null;
        var states = final.TcpStates;
        var established = states.FirstOrDefault(state => state.State == TcpConnectionState.Established)?.Connections ?? 0;
        var timeWait = states.FirstOrDefault(state => state.State == TcpConnectionState.TimeWait)?.Connections ?? 0;

        limitations.Add($"{ordered.Length} amostra(s) agregada(s); a última amostra representa o estado final observado, "
            + "não um histórico contínuo.");
        if (final.DuplicateRowsIgnored > 0)
            limitations.Add($"{final.DuplicateRowsIgnored} linha(s) duplicada(s) foram contabilizadas uma única vez na amostra final.");

        return new NetworkStatistics(ordered.Length, valid.Length,
            ordered.Max(sample => sample.TcpConnections), Math.Round(meanTcp, 2), growth,
            ordered.Max(sample => sample.UdpEndpoints), Math.Round(meanUdp, 2),
            final.TcpConnections, final.UdpEndpoints,
            final.DistinctLocalPorts, final.EphemeralTcpConnections + final.EphemeralUdpEndpoints,
            ephemeralShare, final.ListeningTcp, established, timeWait,
            states, final.TopLocalPorts, final.TopProcesses, limitations);
    }
}
