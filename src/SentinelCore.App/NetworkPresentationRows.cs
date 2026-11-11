using System.Globalization;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.App.Localization;

namespace SentinelCore.App;

// Pure projections: no window, collector, service composition or historical writes.
internal static class NetworkPresentationRows
{
    public static string StatusName(NetworkSessionStatus status) => Loc.T(status switch
    {
        NetworkSessionStatus.Running => "Em andamento",
        NetworkSessionStatus.Completed => "Concluída",
        NetworkSessionStatus.Partial => "Concluída com cobertura parcial",
        NetworkSessionStatus.Cancelled => "Cancelada",
        NetworkSessionStatus.Failed => "Falhou",
        NetworkSessionStatus.Unsupported => "Não suportada",
        _ => status.ToString()
    });

    public static string CoverageName(NetworkCoverage? coverage) => Loc.T(coverage?.Status switch
    {
        NetworkCoverageStatus.Complete => "completa",
        NetworkCoverageStatus.Partial => "parcial",
        NetworkCoverageStatus.Unavailable => "indisponível",
        _ => "não informada"
    });

    public static string PresetName(NetworkSessionPreset preset) => Loc.T(preset switch
    {
        NetworkSessionPreset.Snapshot => "instantâneo",
        NetworkSessionPreset.Seconds30 => "30 segundos",
        NetworkSessionPreset.Minute1 => "1 minuto",
        _ => preset.ToString()
    });

    public static string KindName(NetworkObservationKind kind) => Loc.T(kind switch
    {
        NetworkObservationKind.EndpointGrowth => "crescimento sustentado de endpoints",
        NetworkObservationKind.ProcessConcentration => "concentração de endpoints por processo",
        NetworkObservationKind.StateConcentration => "concentração de estado TCP",
        NetworkObservationKind.DynamicPortPressure => "pressão na faixa dinâmica de portas",
        NetworkObservationKind.AttemptFailures => "falhas de tentativas TCP",
        _ => kind.ToString()
    });

    public static string TcpStateName(TcpConnectionState state) => Loc.T(state switch
    {
        TcpConnectionState.Closed => "CLOSED",
        TcpConnectionState.Listen => "LISTEN",
        TcpConnectionState.SynSent => "SYN_SENT",
        TcpConnectionState.SynReceived => "SYN_RCVD",
        TcpConnectionState.Established => "ESTABLISHED",
        TcpConnectionState.FinWait1 => "FIN_WAIT_1",
        TcpConnectionState.FinWait2 => "FIN_WAIT_2",
        TcpConnectionState.CloseWait => "CLOSE_WAIT",
        TcpConnectionState.Closing => "CLOSING",
        TcpConnectionState.LastAck => "LAST_ACK",
        TcpConnectionState.TimeWait => "TIME_WAIT",
        TcpConnectionState.DeleteTcb => "DELETE_TCB",
        _ => "DESCONHECIDO"
    });

    public static string Number(double? value) =>
        value is { } number ? number.ToString("F1", CultureInfo.CurrentCulture) : "Indisponível";

    public static string Value(double? value) =>
        value is { } number ? number.ToString("0.###", CultureInfo.CurrentCulture) : "Indisponível";

    public sealed record NetworkPresetRow(NetworkSessionPreset Preset, string PortugueseLabel)
    {
        public string Label => Loc.T(PortugueseLabel);
        public override string ToString() => Label;
    }

    public sealed record NetworkHistoryRow(NetworkSession Session)
    {
        public string DateText => Session.CompletedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture);
        public string SummaryText =>
            Loc.F($"{PresetName(Session.Preset)} · {StatusName(Session.Status)} · ") +
            Loc.F($"TCP {Session.Statistics.FinalTcpConnections} · UDP {Session.Statistics.FinalUdpEndpoints} · ") +
            Loc.F($"{Session.Observations.Count} obs.");
    }

    public sealed record NetworkPortRow(NetworkPortCount PortCount)
    {
        public string Port => PortCount.Port.ToString(CultureInfo.CurrentCulture);
        public string Endpoints => PortCount.Endpoints.ToString(CultureInfo.CurrentCulture);
        public string Processes => PortCount.DistinctProcesses.ToString(CultureInfo.CurrentCulture);
    }

    public sealed record NetworkStateRow(NetworkStateCount StateCount)
    {
        public string State => TcpStateName(StateCount.State);
        public string Connections => StateCount.Connections.ToString(CultureInfo.CurrentCulture);
    }

    public sealed record NetworkProcessRow(NetworkProcessCount Process)
    {
        public string Name =>
            Process.Name is { Length: > 0 } name
                ? Loc.F($"{name} (PID {Process.Pid})")
                : Loc.F($"PID {Process.Pid}");
        public string Counts =>
            Loc.F($"{Process.TcpConnections} / {Process.UdpEndpoints}");
        public string Detail =>
            Loc.F($"total {Process.TcpConnections + Process.UdpEndpoints} · escuta {Process.ListeningPorts}");
    }

    public sealed record NetworkRangeRow(NetworkDynamicPortRange RangeItem)
    {
        public string Protocol => RangeItem.Protocol.ToString().ToUpperInvariant();
        public string Family => RangeItem.Family.ToString();
        public string Range => Loc.F($"{RangeItem.StartPort}–{RangeItem.EndPort} ({RangeItem.PortCount})");
        public string Origin =>
            Loc.F($"{Loc.T(RangeItem.IsWindowsDefault ? "Padrão documentado do Windows" : "Configuração explícita")} · {RangeItem.Source}") +
            (RangeItem.Exclusions.Count > 0 ? Loc.F($" · {RangeItem.Exclusions.Count} exclusão(ões)") : string.Empty);
    }

    public sealed record NetworkObservationRow(NetworkObservation Observation)
    {
        public string Title => Observation.Title;
        public string Subtitle =>
            Loc.F($"{KindName(Observation.Kind)} · {Loc.T(EventCatalog.SeverityName(Observation.Severity))} · ") +
            Loc.F($"confiança {Loc.T(EventCatalog.ConfidenceName(Observation.Confidence))} · causalidade não avaliada");
    }
}
