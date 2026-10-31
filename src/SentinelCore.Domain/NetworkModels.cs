using System.Text.Json.Serialization;

namespace SentinelCore.Domain;

public enum NetworkProtocol { Tcp, Udp }
public enum NetworkAddressFamily { IPv4, IPv6 }

/// <summary>Estados de <c>MIB_TCP_STATE</c>. <c>Unknown</c> só existe para valores fora da faixa documentada.</summary>
public enum TcpConnectionState
{
    Closed, Listen, SynSent, SynReceived, Established, FinWait1, FinWait2,
    CloseWait, Closing, LastAck, TimeWait, DeleteTcb, Unknown
}

public enum NetworkSessionPreset { Snapshot, Seconds30, Minute1 }
public enum NetworkSessionStatus { Running, Completed, Partial, Failed, Cancelled, Unsupported }
public enum NetworkSourceStatus { Running, Completed, Partial, Failed, Unsupported, Cancelled }
public enum NetworkSourceCategory { Endpoints, Statistics, PortRange, Adapters }
public enum NetworkExecutionStatus { Running, Completed, Cancelled, Failed, Unsupported }
public enum NetworkCoverageStatus { Complete, Partial, Unavailable }

/// <summary>Como o PID de um endpoint foi associado a um processo.</summary>
public enum NetworkProcessStatus { Resolved, AccessDenied, Exited, SystemProcess, Unavailable }

public enum NetworkObservationKind
{
    EndpointGrowth, ProcessConcentration, StateConcentration, DynamicPortPressure, AttemptFailures
}

/// <summary>
/// Motivo de uma regra não ter produzido observação. Distingue indisponibilidade de
/// "regra avaliada e condição não observada".
/// </summary>
public enum NetworkNoteReason
{
    NotSpecified, Unavailable, InvalidSamples, InsufficientSamples,
    PreconditionNotObserved, ConditionNotObserved, Observed
}

public sealed record NetworkLimits
{
    public int MaxTableBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxRowsPerTable { get; init; } = 65_536;
    public int MaxTableRetries { get; init; } = 3;
    public int InitialTableBytes { get; init; } = 64 * 1024;
    public int MaxSamples { get; init; } = 61;
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxSessionDuration { get; init; } = TimeSpan.FromMinutes(1);
    public int MaxPersistedEndpoints { get; init; } = 2_000;
    public int MaxTopPorts { get; init; } = 15;
    public int MaxTopProcesses { get; init; } = 15;
    public int MaxObservationPoints { get; init; } = 120;
    public static NetworkLimits Default { get; } = new();
}

public sealed record NetworkSessionRequest(
    NetworkSessionPreset Preset, TimeSpan Duration, TimeSpan SampleInterval,
    bool IncludeAddresses = false)
{
    public static NetworkSessionRequest ForPreset(NetworkSessionPreset preset, bool includeAddresses = false) => preset switch
    {
        NetworkSessionPreset.Snapshot => new(preset, TimeSpan.Zero, TimeSpan.Zero, includeAddresses),
        NetworkSessionPreset.Seconds30 => new(preset, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), includeAddresses),
        NetworkSessionPreset.Minute1 => new(preset, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), includeAddresses),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), "Presets suportados: instantâneo, 30 s e 1 min.")
    };

    public int ExpectedSamples => Preset == NetworkSessionPreset.Snapshot
        ? 1
        : Duration <= TimeSpan.Zero || SampleInterval <= TimeSpan.Zero
            ? 1
            : (int)Math.Floor(Duration / SampleInterval) + 1;
}

/// <summary>Contexto da sessão. As tabelas são lidas no máximo uma vez por amostra por coletor.</summary>
public sealed record NetworkCollectionContext(Guid SessionId, NetworkLimits? Limits = null);

/// <summary>
/// Um endpoint observado. Endereços são opcionais: o registro padrão não os persiste.
/// <c>ProcessIdentity</c> combina PID e hora de criação; PID isolado nunca é identidade histórica.
/// </summary>
public sealed record NetworkEndpoint(
    NetworkProtocol Protocol, NetworkAddressFamily Family,
    string? LocalAddress, ushort LocalPort,
    string? RemoteAddress, ushort? RemotePort,
    TcpConnectionState? TcpState,
    int Pid, string? ProcessIdentity, string? ProcessName, NetworkProcessStatus ProcessStatus,
    DateTimeOffset ObservedUtc, string Source)
{
    /// <summary>Chave de deduplicação dentro de uma amostra.</summary>
    [JsonIgnore]
    public string Key => string.Join('|',
        Protocol, Family, LocalAddress ?? "-", LocalPort.ToString(),
        RemoteAddress ?? "-", RemotePort?.ToString() ?? "-",
        TcpState?.ToString() ?? "-", Pid.ToString(), ProcessIdentity ?? "-");
}

public sealed record NetworkPortCount(ushort Port, int Endpoints, int DistinctProcesses);

public sealed record NetworkProcessCount(
    string? Identity, int Pid, string? Name,
    int TcpConnections, int UdpEndpoints, int ListeningPorts);

public sealed record NetworkStateCount(TcpConnectionState State, int Connections);

/// <summary>Agregado de uma amostra. Endpoints individuais não são preservados por amostra.</summary>
public sealed record NetworkSampleAggregate(
    int Index, DateTimeOffset TimestampUtc, double ElapsedSeconds, bool Warmup,
    int TcpConnections, int UdpEndpoints, int ListeningTcp, int DistinctLocalPorts,
    int EphemeralTcpConnections, int EphemeralUdpEndpoints,
    uint? TcpAttemptFailures,
    IReadOnlyList<NetworkStateCount> TcpStates,
    IReadOnlyList<NetworkPortCount> TopLocalPorts,
    IReadOnlyList<NetworkProcessCount> TopProcesses,
    int ResolvedProcesses, int UnresolvedProcesses, int DuplicateRowsIgnored,
    long CollectionDurationMilliseconds,
    IReadOnlyList<string> Limitations);

public sealed record NetworkSourceSummary(
    string CollectorId, string CollectorVersion, NetworkSourceCategory Category, string Source,
    NetworkSourceStatus Status, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc,
    TimeSpan Duration, int SamplesRead, bool LimitHit, IReadOnlyList<string> Limitations)
{
    public double? MeanReadMilliseconds { get; init; }
    public double? MaxReadMilliseconds { get; init; }
}

public sealed record NetworkPortRangeExclusion(int FromPort, int ToPort, string Source);

/// <summary>
/// Intervalo dinâmico observado. <c>IsWindowsDefault</c> indica que nenhuma configuração explícita
/// foi encontrada e que o valor é o padrão documentado do Windows, não um valor medido.
/// </summary>
public sealed record NetworkDynamicPortRange(
    NetworkProtocol Protocol, NetworkAddressFamily Family,
    int StartPort, int PortCount, string Source, bool IsWindowsDefault,
    IReadOnlyList<NetworkPortRangeExclusion> Exclusions,
    IReadOnlyList<string> Limitations)
{
    [JsonIgnore]
    public int EndPort => StartPort + Math.Max(0, PortCount) - 1;
    public bool Contains(int port) => port >= StartPort && port <= EndPort;
}

public sealed record NetworkAdapterSummary(
    string Id, string Kind, string OperationalStatus,
    bool HasIpv4, bool HasIpv6, int Ipv4AddressCount, int Ipv6AddressCount);

/// <summary>Estatísticas TCP de <c>GetTcpStatisticsEx</c>. Contadores são cumulativos desde o boot.</summary>
public sealed record NetworkTcpStatistics(
    NetworkAddressFamily Family,
    uint? RtoAlgorithm, uint RtoMin, uint RtoMax, long? MaxConnections,
    uint ActiveOpens, uint PassiveOpens, uint AttemptFails, uint EstablishedResets, uint CurrentEstablished,
    uint InSegments, uint OutSegments, uint RetransmittedSegments, uint InErrors, uint OutResets,
    uint CumulativeConnections, DateTimeOffset ObservedUtc, string Source,
    IReadOnlyList<string> Limitations);

/// <summary>Estatísticas UDP de <c>GetUdpStatisticsEx</c>.</summary>
public sealed record NetworkUdpStatistics(
    NetworkAddressFamily Family, uint InDatagrams, uint NoPorts, uint InErrors, uint OutDatagrams,
    uint Listeners, DateTimeOffset ObservedUtc, string Source, IReadOnlyList<string> Limitations);

public sealed record NetworkEvidencePoint(
    DateTimeOffset TimestampUtc, int SampleIndex, double Value, string Unit);

public sealed record NetworkObservation(
    string Id, string RuleId, string RuleVersion, NetworkObservationKind Kind,
    string Title, string Statement,
    EvidenceSeverity Severity, EvidenceConfidence Confidence, CausalityStatus Causality,
    string Metric, string Unit, double? Threshold, string? Subject, string? SubjectName,
    DateTimeOffset WindowFromUtc, DateTimeOffset WindowToUtc, TimeSpan Duration,
    int SupportingSampleCount, int EvaluatedSampleCount,
    double? ObservedMin, double? ObservedMax, double? ObservedMean, double? ObservedP95,
    string Source, IReadOnlyList<NetworkEvidencePoint> Points, IReadOnlyList<string> Limitations);

public sealed record NetworkAnalysisNote(
    string RuleId, string RuleVersion, bool Evaluated, string Detail,
    NetworkNoteReason Reason = NetworkNoteReason.NotSpecified);

/// <summary>
/// Contexto opcional derivado de uma investigação de eventos já persistida na Fase 3.1.
/// Presença de evento de transporte não confirma esgotamento e não atribui causa.
/// </summary>
public sealed record NetworkTcpipEventContext(
    Guid? InvestigationId, int AttemptFailureEvents, int EphemeralExhaustionEvents,
    DateTimeOffset? FirstObservedUtc, DateTimeOffset? LastObservedUtc, string Note);

public sealed record NetworkStatistics(
    int TotalSamples, int ValidSamples,
    int PeakTcpConnections, double? MeanTcpConnections, double? TcpGrowthPercent,
    int PeakUdpEndpoints, double? MeanUdpEndpoints,
    int FinalTcpConnections, int FinalUdpEndpoints,
    int FinalDistinctLocalPorts, int FinalEphemeralEndpoints, double? FinalEphemeralSharePercent,
    int FinalListening, int FinalEstablished, int FinalTimeWait,
    IReadOnlyList<NetworkStateCount> FinalStates,
    IReadOnlyList<NetworkPortCount> FinalTopLocalPorts,
    IReadOnlyList<NetworkProcessCount> FinalTopProcesses,
    IReadOnlyList<string> Limitations);

public sealed record NetworkCoverage(
    NetworkExecutionStatus Execution,
    NetworkCoverageStatus Status,
    int ExpectedSamples, int CollectedSamples, int MissedOpportunities,
    bool SampleLimitReached, bool DetailLimitReached, bool PortRangeUnavailable,
    IReadOnlyList<string> UnavailableCollectors,
    IReadOnlyList<string> Limitations);

public sealed record NetworkSession(
    int FormatVersion, Guid Id, string SoftwareVersion, string SchemaVersion,
    NetworkSessionPreset Preset, TimeSpan RequestedDuration, TimeSpan SampleInterval,
    DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, TimeSpan Duration,
    NetworkSessionStatus Status,
    bool IncludesEndpointDetails, bool IncludesAddresses,
    IReadOnlyList<NetworkSourceSummary> Sources,
    IReadOnlyList<NetworkDynamicPortRange> PortRanges,
    IReadOnlyList<NetworkAdapterSummary> Adapters,
    NetworkTcpStatistics? TcpStatistics,
    NetworkUdpStatistics? UdpStatistics,
    NetworkStatistics Statistics,
    IReadOnlyList<NetworkObservation> Observations,
    IReadOnlyList<NetworkAnalysisNote> AnalysisNotes,
    IReadOnlyList<NetworkSampleAggregate> Samples,
    IReadOnlyList<NetworkEndpoint> EndpointDetails,
    SelfOverheadSummary SelfOverhead,
    NetworkTcpipEventContext? TcpipEvents,
    IReadOnlyList<string> Limitations,
    NetworkCoverage? Coverage = null);

public sealed record NetworkProgress(
    int SamplesCollected, int ExpectedSamples, TimeSpan Elapsed, TimeSpan Requested,
    NetworkSampleAggregate? Latest);

public sealed record NetworkStoreSnapshot(
    IReadOnlyList<NetworkSession> Sessions, IReadOnlyList<string> Warnings);

/// <summary>Leitura de uma amostra: endpoints, processos e estatísticas do mesmo instante lógico.</summary>
public sealed record NetworkSampleReading(
    string CollectorId, bool Unsupported,
    IReadOnlyList<NetworkEndpoint> Endpoints,
    IReadOnlyList<NetworkProcessCount> Processes,
    NetworkTcpStatistics? TcpStatistics,
    NetworkUdpStatistics? UdpStatistics,
    int DuplicateRowsIgnored,
    IReadOnlyList<string> Limitations)
{
    public static NetworkSampleReading Empty(string collectorId, string limitation) =>
        new(collectorId, false, [], [], null, null, 0, [limitation]);

    public static NetworkSampleReading NotSupported(string collectorId, string limitation) =>
        new(collectorId, true, [], [], null, null, 0, [limitation]);
}

/// <summary>Leitura única por sessão: intervalos dinâmicos e adaptadores.</summary>
public sealed record NetworkFactsReading(
    string CollectorId, bool Unsupported,
    IReadOnlyList<NetworkDynamicPortRange> PortRanges,
    IReadOnlyList<NetworkAdapterSummary> Adapters,
    IReadOnlyList<string> Limitations)
{
    public static NetworkFactsReading None(string collectorId) =>
        new(collectorId, false, [], [], []);
}

public interface INetworkSampleCollector
{
    string Id { get; }
    string Version { get; }
    string Source { get; }
    NetworkSourceCategory Category { get; }
    Task<NetworkSampleReading> ReadAsync(NetworkCollectionContext context,
        DateTimeOffset timestampUtc, CancellationToken cancellationToken);
}

public interface INetworkFactsCollector
{
    string Id { get; }
    string Version { get; }
    string Source { get; }
    NetworkSourceCategory Category { get; }
    Task<NetworkFactsReading> ReadAsync(NetworkCollectionContext context,
        CancellationToken cancellationToken);
}

public interface INetworkSessionStore
{
    Task<NetworkStoreSnapshot> LoadNetworkSessionsAsync(CancellationToken cancellationToken = default);
    Task<long> SaveNetworkSessionAsync(NetworkSession session, CancellationToken cancellationToken = default);
    string GetNetworkReportPath(Guid sessionId);
}
