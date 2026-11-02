namespace SentinelCore.Domain;

/// <summary>
/// Durações autorizadas de uma sessão de monitoramento. O nome "persistente" descreve o histórico preservado,
/// não a execução: a observação existe apenas durante a sessão explicitamente autorizada.
/// </summary>
public enum MonitoringDurationPreset { Unknown, Custom, Minutes5, Minutes15, Minutes30 }

/// <summary>Estado da execução da sessão, separado da cobertura das fontes.</summary>
public enum MonitoringExecutionStatus { Running, Completed, Cancelled, Interrupted, Failed }

public enum MonitoringCoverageStatus { Complete, Partial, Unavailable }

/// <summary>
/// Fontes observadas. <c>Graphics</c> é uma visão derivada do mesmo fluxo incremental de eventos: não existe
/// leitor gráfico paralelo nem coleta de estresse.
/// </summary>
public enum MonitoringSource { Events, Performance, Network, Graphics }

public enum MonitoringSourceStatus { Running, Completed, Partial, Unavailable, Cancelled, Skipped }

public enum MonitoringSegmentStatus { Complete, Partial }

public enum MonitoringIncidentCategory { BlackScreen, Freeze, Slowness, ApplicationFailure, NetworkProblem, Other }

/// <summary>
/// Precisão da ocorrência informada pelo usuário. <c>Approximate</c> significa horário estimado; <c>Unknown</c>
/// significa que apenas o momento do registro manual é conhecido.
/// </summary>
public enum MonitoringIncidentPrecision { Approximate, Unknown }

[Flags]
public enum MonitoringSources
{
    None = 0,
    Events = 1,
    Performance = 2,
    Network = 4,
    Graphics = 8,
    All = Events | Performance | Network | Graphics
}

/// <summary>
/// Orçamento explícito de recursos. As cadências foram escolhidas pelo custo real das fontes: desempenho usa
/// somente duas chamadas nativas por tick (sem WMI, sem enumeração de processos e sem disco), eventos usam leitura
/// incremental por RecordId e rede usa as mesmas tabelas locais em cadência menor. Nenhuma auditoria completa é
/// repetida durante a sessão.
/// </summary>
public sealed record MonitoringLimits
{
    public TimeSpan MaxSessionDuration { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan PerformanceInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan EventInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan NetworkInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan SegmentDuration { get; init; } = TimeSpan.FromMinutes(1);
    public int MaxSamplesPerSource { get; init; } = 240;
    public int MaxEventsPerSession { get; init; } = 2_000;
    public int MaxEventsPerTick { get; init; } = 60;
    public int MaxIncidents { get; init; } = 50;
    public int MaxIncidentDescriptionLength { get; init; } = 280;
    public TimeSpan MaxCollectorTick { get; init; } = TimeSpan.FromSeconds(2);
    public long MaxSessionBytes { get; init; } = 3 * 1024 * 1024;
    public static MonitoringLimits Default { get; } = new();

    public void Validate()
    {
        if (MaxSessionDuration <= TimeSpan.Zero || MaxSessionDuration > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(MonitoringLimits),
                "A duração máxima autorizada é de 30 minutos.");
        if (PerformanceInterval <= TimeSpan.Zero || EventInterval <= TimeSpan.Zero ||
            NetworkInterval <= TimeSpan.Zero || SegmentDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MonitoringLimits),
                "Cadências de observação ou duração de segmento inválidas.");
        if (MaxSamplesPerSource <= 0 || MaxEventsPerSession <= 0 || MaxEventsPerTick <= 0 ||
            MaxIncidents <= 0 || MaxIncidentDescriptionLength <= 0 || MaxSessionBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MonitoringLimits),
                "Limites de amostras, eventos, incidentes ou armazenamento devem ser positivos.");
        if (MaxIncidentDescriptionLength > 280)
            throw new ArgumentOutOfRangeException(nameof(MonitoringLimits),
                "A descrição de incidente não pode exceder 280 caracteres, coerente com a validação persistente.");
        if (MaxCollectorTick <= TimeSpan.Zero || MaxCollectorTick > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(MonitoringLimits),
                "O orçamento por leitura deve ficar entre zero e 10 segundos.");
    }
}

/// <summary>
/// Requisição de uma sessão. <see cref="Authorized"/> precisa ser verdadeiro: o aplicativo nunca inicia
/// monitoramento automaticamente e não há valor padrão que autorize a observação.
/// </summary>
public sealed record MonitoringRequest(
    MonitoringDurationPreset Preset, TimeSpan Duration, MonitoringSources Sources, bool Authorized = false)
{
    public static MonitoringRequest ForPreset(MonitoringDurationPreset preset, MonitoringSources sources,
        bool authorized) => new(preset, DurationFor(preset), sources, authorized);

    public static TimeSpan DurationFor(MonitoringDurationPreset preset) => preset switch
    {
        MonitoringDurationPreset.Minutes5 => TimeSpan.FromMinutes(5),
        MonitoringDurationPreset.Minutes15 => TimeSpan.FromMinutes(15),
        MonitoringDurationPreset.Minutes30 => TimeSpan.FromMinutes(30),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), "Durações suportadas: 5, 15 e 30 minutos.")
    };

    public int ExpectedTicks(TimeSpan interval)
    {
        if (Duration <= TimeSpan.Zero || interval <= TimeSpan.Zero) return 0;
        var exact = Duration.Ticks / interval.Ticks;
        return (int)(Duration.Ticks % interval.Ticks == 0 ? exact : exact + 1);
    }
}

/// <summary>Amostra de desempenho de baixo impacto. Somente CPU e memória físicas nativas são observadas.</summary>
public sealed record MonitoringPerformanceObservation(
    int Index, DateTimeOffset TimestampUtc, double ElapsedSeconds,
    double? CpuTotalPercent, int? LogicalProcessors,
    double? MemoryAvailablePercent, ulong? InstalledBytes, ulong? AvailableBytes, ulong? UsedBytes,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Evento lido incrementalmente do Log de Eventos. A identidade <c>evt:{canal}:{RecordId}</c> é a mesma das
/// Fases 3.1/3.4, portanto o mesmo acontecimento não é contado duas vezes na correlação.
/// </summary>
public sealed record MonitoringEventObservation(
    int Index, DateTimeOffset TimestampUtc, string Channel, string Provider, int EventId,
    long? RecordId, DateTimeOffset OccurredUtc, EventLevel Level,
    string GroupId, EventCategory Category, EvidenceSeverity Severity,
    string? Message, bool MessageTruncated);

/// <summary>
/// Resumo de uma leitura das tabelas locais de rede. Nenhum endereço IP é preservado nesta fase; apenas
/// contagens agregadas do instante observado.
/// </summary>
public sealed record MonitoringNetworkObservation(
    int Index, DateTimeOffset TimestampUtc,
    int TcpConnections, int UdpEndpoints, int ListeningTcp, int EstablishedTcp,
    int DistinctLocalPorts, int ProcessesObserved, bool Incomplete,
    IReadOnlyList<string> Limitations);

/// <summary>Resultado de um ciclo de leitura de uma fonte. Listas vazias significam "sem novidade", não falha.</summary>
public sealed record MonitoringTick(
    MonitoringSource Source, DateTimeOffset TimestampUtc, bool Unavailable,
    MonitoringPerformanceObservation? Performance,
    IReadOnlyList<MonitoringEventObservation> Events,
    MonitoringNetworkObservation? Network,
    IReadOnlyList<string> Limitations)
{
    public static MonitoringTick Empty(MonitoringSource source, DateTimeOffset at) =>
        new(source, at, false, null, [], null, []);

    public static MonitoringTick NotAvailable(MonitoringSource source, DateTimeOffset at, string limitation) =>
        new(source, at, true, null, [], null, [limitation]);

    public int ObservationCount => (Performance is null ? 0 : 1) + Events.Count + (Network is null ? 0 : 1);

    /// <summary>Truncamento declarado pelo leitor (ex.: limite de eventos por ciclo). Não é silencioso.</summary>
    public bool LimitHit { get; init; }
}

public sealed record MonitoringSourceSummary(
    MonitoringSource Source, string CollectorId, string CollectorVersion,
    MonitoringSourceStatus Status, TimeSpan Interval,
    int ExpectedTicks, int CollectedTicks, int MissedOpportunities, int TruncatedTicks,
    int ObservationCount, bool LimitHit, bool BudgetLimitHit,
    IReadOnlyList<string> Limitations);

public sealed record MonitoringSegmentReference(
    Guid SegmentId, MonitoringSource Source, int Sequence,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, MonitoringSegmentStatus Status,
    int ObservationCount);

/// <summary>
/// Checkpoint imutável de uma fonte. Cada segmento é gravado uma única vez e nunca é reescrito; o registro final
/// da sessão apenas referencia os segmentos confirmados.
/// </summary>
public sealed record MonitoringSegment(
    int FormatVersion, Guid Id, Guid SessionId, int Sequence, MonitoringSource Source,
    string CollectorId, string CollectorVersion,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, MonitoringSegmentStatus Status,
    IReadOnlyList<MonitoringPerformanceObservation> Performance,
    IReadOnlyList<MonitoringEventObservation> Events,
    IReadOnlyList<MonitoringNetworkObservation> Network,
    IReadOnlyList<string> Limitations)
{
    public int ObservationCount => Performance.Count + Events.Count + Network.Count;

    public MonitoringSegmentReference Reference => new(Id, Source, Sequence, FromUtc, ToUtc, Status,
        ObservationCount);
}

public sealed record MonitoringEvidenceReference(MonitoringSource Source, string Reference, string Detail);

/// <summary>
/// Incidente registrado manualmente. <see cref="RecordedUtc"/> é o instante do clique; <see cref="OccurredUtc"/>
/// existe apenas quando o usuário informou um horário aproximado. O registro não é convertido em evento do Windows.
/// </summary>
public sealed record MonitoringIncident(
    Guid Id, Guid SessionId, DateTimeOffset RecordedUtc, DateTimeOffset? OccurredUtc,
    MonitoringIncidentPrecision Precision, MonitoringIncidentCategory Category,
    string? Description, double SessionElapsedSeconds,
    IReadOnlyList<MonitoringEvidenceReference> Evidence, IReadOnlyList<string> Limitations);

public sealed record MonitoringIncidentRequest(
    MonitoringIncidentCategory Category, string? Description, DateTimeOffset? OccurredUtc);

public sealed record MonitoringBudgetReport(
    TimeSpan AuthorizedDuration, TimeSpan MaxCollectorTick,
    int ExpectedTicks, int CollectedTicks, int MissedOpportunities,
    double MeanTickMilliseconds, double MaxTickMilliseconds,
    int SamplesCollected, int EventsCollected, long PersistedBytes, long StorageBudgetBytes,
    bool StorageBudgetReached, bool TickBudgetExceeded, bool SampleLimitReached, bool EventLimitReached,
    string CadenceSummary, IReadOnlyList<string> Limitations);

/// <summary>
/// Informação de recuperação de uma sessão cujo registro final está ausente. A sessão derivada é somente de
/// leitura: ela não é gravada e nunca recebe o estado <c>Completed</c>.
/// </summary>
public sealed record MonitoringRecoveryInfo(
    bool Recovered, DateTimeOffset? LastSegmentUtc, int SegmentsRecovered, string Detail)
{
    public static MonitoringRecoveryInfo None { get; } = new(false, null, 0,
        "Sessão concluída pelo aplicativo; nenhum segmento ficou órfão.");
}

public sealed record MonitoringSession(
    int FormatVersion, Guid Id, string SoftwareVersion, string SchemaVersion,
    MonitoringDurationPreset Preset, TimeSpan AuthorizedDuration,
    DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, TimeSpan Duration,
    MonitoringExecutionStatus Status, MonitoringCoverageStatus CoverageStatus,
    MonitoringSources RequestedSources,
    IReadOnlyList<MonitoringSourceSummary> Sources,
    IReadOnlyList<MonitoringSegmentReference> Segments,
    IReadOnlyList<MonitoringIncident> Incidents,
    MonitoringBudgetReport Budget,
    MonitoringRecoveryInfo Recovery,
    IReadOnlyList<string> Limitations)
{
    public bool IsRecovered => Recovery.Recovered ||
        Status == MonitoringExecutionStatus.Interrupted;
}

/// <summary>Sessão interrompida detectada na leitura, sem registro final gravado.</summary>
public sealed record MonitoringRecovery(
    Guid SessionId, DateTimeOffset DetectedUtc, DateTimeOffset FirstSegmentUtc,
    DateTimeOffset LastSegmentUtc, int SegmentCount,
    int PerformanceObservations, int EventObservations, int NetworkObservations, string Detail);

public sealed record MonitoringProgress(
    TimeSpan Elapsed, TimeSpan Remaining, int Samples, int Events, int Incidents,
    bool BudgetWarning, string Phase);

public sealed record MonitoringStoreSnapshot(
    IReadOnlyList<MonitoringSession> Sessions,
    IReadOnlyList<MonitoringSegment> Segments,
    IReadOnlyList<MonitoringRecovery> Recoveries,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Leitura incremental do Log de Eventos. A implementação real mantém a marca d'água de RecordId por canal; a
/// primeira leitura apenas posiciona a marca d'água e não recupera histórico anterior à sessão.
/// </summary>
public sealed record MonitoringEventPoll(
    DateTimeOffset TimestampUtc, IReadOnlyList<RawEventRecord> Records,
    bool LimitHit, IReadOnlyList<string> Limitations);

public interface IMonitoringEventSource
{
    string Id { get; }
    string Version { get; }
    string Source { get; }
    IMonitoringEventProbe Start();
}

public interface IMonitoringEventProbe : IDisposable
{
    Task<MonitoringEventPoll> PollAsync(CancellationToken cancellationToken);
}

public interface IMonitoringSessionStore
{
    Task<MonitoringStoreSnapshot> LoadMonitoringSessionsAsync(CancellationToken cancellationToken = default);
    Task<long> SaveMonitoringSegmentAsync(MonitoringSegment segment, CancellationToken cancellationToken = default);
    Task<long> SaveMonitoringSessionAsync(MonitoringSession session, CancellationToken cancellationToken = default);
    string GetMonitoringReportPath(Guid sessionId);
}

/// <summary>
/// Textos obrigatórios, nomes e limites compartilhados entre runner, validação, relatório e interface. Nenhum
/// componente desta fase declara causa ou inicia observação sem autorização.
/// </summary>
public static class MonitoringGuidance
{
    public const string AuthorizationLimitation =
        "O monitoramento só é executado durante uma sessão explicitamente autorizada pelo usuário; nenhuma " +
        "observação é iniciada automaticamente e nenhuma sessão prossegue após o encerramento.";

    public const string ScopeLimitation =
        "Nenhum serviço, tarefa agendada, driver, processo residente ou alteração do Windows é criado; a observação " +
        "termina com a sessão ou com o fechamento do aplicativo e não há coleta em segundo plano.";

    public const string IncidentLimitation =
        "Incidente registrado manualmente pelo usuário: o momento do registro não é o momento comprovado do início " +
        "da ocorrência e não equivale a um evento do Windows.";

    public const string CausalityLimitation =
        "O monitoramento preserva evidências e não declara causa; proximidade temporal, amostra pontual ou relato do " +
        "usuário não demonstram defeito, diagnóstico ou relação causal.";

    public const string BudgetLimitation =
        "O orçamento de recursos limita a observação; oportunidades perdidas, limites e cobertura parcial são " +
        "declarados e nunca omitidos em silêncio.";

    public const string RecoveryLimitation =
        "Sessão interrompida: o registro final não existe; o estado foi derivado somente dos segmentos confirmados, " +
        "não foi gravado e não equivale a conclusão normal.";

    public const string DeduplicationLimitation =
        "Eventos observados pelo monitoramento mantêm a identidade do Log de Eventos (canal e RecordId) e podem ser " +
        "consolidados com outras investigações sem contagem dupla.";

    public const string NetworkPrivacyLimitation =
        "A fonte de rede registra somente contagens agregadas; endereços IP, portas remotas detalhadas, tráfego e " +
        "conteúdo de pacotes não são preservados por esta observação.";

    public static string SourceName(MonitoringSource source) => source switch
    {
        MonitoringSource.Events => "Eventos do Windows",
        MonitoringSource.Performance => "Desempenho",
        MonitoringSource.Network => "Rede",
        MonitoringSource.Graphics => "Gráficos",
        _ => source.ToString()
    };

    public static string SourceStatusName(MonitoringSourceStatus status) => status switch
    {
        MonitoringSourceStatus.Running => "em execução",
        MonitoringSourceStatus.Completed => "concluída",
        MonitoringSourceStatus.Partial => "parcial",
        MonitoringSourceStatus.Unavailable => "indisponível",
        MonitoringSourceStatus.Cancelled => "cancelada",
        _ => "não selecionada"
    };

    public static string StatusName(MonitoringExecutionStatus status) => status switch
    {
        MonitoringExecutionStatus.Running => "Em execução",
        MonitoringExecutionStatus.Completed => "Concluída",
        MonitoringExecutionStatus.Cancelled => "Cancelada",
        MonitoringExecutionStatus.Interrupted => "Interrompida",
        MonitoringExecutionStatus.Failed => "Falhou",
        _ => status.ToString()
    };

    public static string CoverageName(MonitoringCoverageStatus status) => status switch
    {
        MonitoringCoverageStatus.Complete => "completa",
        MonitoringCoverageStatus.Partial => "parcial",
        MonitoringCoverageStatus.Unavailable => "indisponível",
        _ => status.ToString()
    };

    public static string PrecisionName(MonitoringIncidentPrecision precision) => precision switch
    {
        MonitoringIncidentPrecision.Approximate => "horário aproximado informado pelo usuário",
        _ => "somente o momento do registro é conhecido"
    };

    public static string CategoryName(MonitoringIncidentCategory category) => category switch
    {
        MonitoringIncidentCategory.BlackScreen => "Tela preta",
        MonitoringIncidentCategory.Freeze => "Travamento",
        MonitoringIncidentCategory.Slowness => "Lentidão",
        MonitoringIncidentCategory.ApplicationFailure => "Falha de aplicativo",
        MonitoringIncidentCategory.NetworkProblem => "Problema de rede",
        _ => "Outro"
    };

    public static string DurationName(MonitoringDurationPreset preset) => preset switch
    {
        MonitoringDurationPreset.Minutes5 => "5 minutos",
        MonitoringDurationPreset.Minutes15 => "15 minutos",
        MonitoringDurationPreset.Minutes30 => "30 minutos",
        MonitoringDurationPreset.Custom => "personalizada (teste interno)",
        _ => "desconhecida"
    };

    public static MonitoringSources Flag(MonitoringSource source) => source switch
    {
        MonitoringSource.Events => MonitoringSources.Events,
        MonitoringSource.Performance => MonitoringSources.Performance,
        MonitoringSource.Network => MonitoringSources.Network,
        MonitoringSource.Graphics => MonitoringSources.Graphics,
        _ => MonitoringSources.None
    };

    public static TimeSpan Interval(MonitoringSource source, MonitoringLimits limits) => source switch
    {
        MonitoringSource.Events or MonitoringSource.Graphics => limits.EventInterval,
        MonitoringSource.Performance => limits.PerformanceInterval,
        MonitoringSource.Network => limits.NetworkInterval,
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };
}
