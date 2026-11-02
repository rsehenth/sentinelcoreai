using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Resultado da leitura de uma fonte histórica. Nenhuma coleta nova é executada nesta fase.</summary>
public sealed record CorrelationSourceRead(
    CorrelationSource Source,
    CorrelationSourceStatus Status,
    int RecordsRead,
    bool LimitHit,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Conjunto de registros históricos lidos dos armazenamentos existentes. É a entrada única do normalizador:
/// as regras nunca consultam disco, WMI ou o Log de Eventos diretamente.
/// </summary>
public sealed record CorrelationEvidenceInput(
    IReadOnlyList<AuditRecord> Audits,
    IReadOnlyList<HardwareSnapshot> HardwareSnapshots,
    IReadOnlyList<EventInvestigation> EventInvestigations,
    IReadOnlyList<PerformanceSession> PerformanceSessions,
    IReadOnlyList<NetworkSession> NetworkSessions,
    IReadOnlyList<GraphicsInvestigation> GraphicsInvestigations,
    IReadOnlyList<CorrelationSourceRead> SourceReads)
{
    /// <summary>
    /// Sessões de monitoramento (Fase 5) recuperadas de checkpoints imutáveis. A propriedade é aditiva para não
    /// alterar as chamadas existentes da Fase 3.4.
    /// </summary>
    public IReadOnlyList<MonitoringSession> MonitoringSessions { get; init; } = [];

    public IReadOnlyList<MonitoringSegment> MonitoringSegments { get; init; } = [];

    public static CorrelationEvidenceInput Empty(IReadOnlyList<CorrelationSourceRead>? reads = null) =>
        new([], [], [], [], [], [], reads ?? []);
}

/// <summary>
/// Recupera registros históricos já persistidos dos módulos existentes. Reutiliza as interfaces de armazenamento
/// das fases anteriores em vez de criar coletores paralelos; registros ausentes, indisponíveis ou limitados são
/// declarados como limitação, nunca como ausência de acontecimento.
/// </summary>
public sealed class CorrelationEvidenceReader
{
    private readonly IAuditStore? audits;
    private readonly IHardwareSnapshotStore? hardware;
    private readonly IEventInvestigationStore? events;
    private readonly IPerformanceSessionStore? performance;
    private readonly INetworkSessionStore? network;
    private readonly IGraphicsInvestigationStore? graphics;
    private readonly IMonitoringSessionStore? monitoring;
    private readonly CorrelationLimits limits;

    public CorrelationEvidenceReader(IAuditStore? audits = null, IHardwareSnapshotStore? hardware = null,
        IEventInvestigationStore? events = null, IPerformanceSessionStore? performance = null,
        INetworkSessionStore? network = null, IGraphicsInvestigationStore? graphics = null,
        CorrelationLimits? limits = null, IMonitoringSessionStore? monitoring = null)
    {
        this.audits = audits;
        this.hardware = hardware;
        this.events = events;
        this.performance = performance;
        this.network = network;
        this.graphics = graphics;
        this.monitoring = monitoring;
        this.limits = limits ?? CorrelationLimits.Default;
        this.limits.Validate();
    }

    public async Task<CorrelationEvidenceInput> ReadAsync(CorrelationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reads = new List<CorrelationSourceRead>();
        var auditRecords = new List<AuditRecord>();
        var hardwareRecords = new List<HardwareSnapshot>();
        var eventRecords = new List<EventInvestigation>();
        var performanceRecords = new List<PerformanceSession>();
        var networkRecords = new List<NetworkSession>();
        var graphicsRecords = new List<GraphicsInvestigation>();
        var monitoringRecords = new List<MonitoringSession>();
        var monitoringSegments = new List<MonitoringSegment>();

        foreach (var source in Enum.GetValues<CorrelationSource>())
        {
            if (!request.Sources.HasFlag(Flag(source)))
            {
                reads.Add(new(source, CorrelationSourceStatus.Skipped, 0, false,
                    ["Fonte não selecionada nesta investigação."]));
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                switch (source)
                {
                    case CorrelationSource.Audits:
                    {
                        if (audits is null) { reads.Add(Missing(source, "auditorias")); break; }
                        var snapshot = await audits.LoadAsync(cancellationToken).ConfigureAwait(false);
                        var (taken, hit) = TakeRecent(snapshot.Audits, record => record.CompletedUtc);
                        auditRecords.AddRange(taken);
                        reads.Add(Read(source, taken.Count, hit, snapshot.Warnings,
                            taken.Count(record => record.Status != DiagnosticStatus.Completed)));
                        break;
                    }
                    case CorrelationSource.Hardware:
                    {
                        if (hardware is null) { reads.Add(Missing(source, "inventários de hardware")); break; }
                        var snapshot = await hardware.LoadSnapshotsAsync(cancellationToken).ConfigureAwait(false);
                        var (taken, hit) = TakeRecent(snapshot.Snapshots, record => record.CompletedUtc);
                        hardwareRecords.AddRange(taken);
                        reads.Add(Read(source, taken.Count, hit, snapshot.Warnings,
                            taken.Count(record => record.Status != HardwareCollectionStatus.Completed)));
                        break;
                    }
                    case CorrelationSource.Events:
                    {
                        if (events is null) { reads.Add(Missing(source, "investigações de eventos")); break; }
                        var snapshot = await events.LoadInvestigationsAsync(cancellationToken).ConfigureAwait(false);
                        var (taken, hit) = TakeRecent(snapshot.Investigations, record => record.CompletedUtc);
                        eventRecords.AddRange(taken);
                        reads.Add(Read(source, taken.Count, hit, snapshot.Warnings,
                            taken.Count(record => record.Status != EventInvestigationStatus.Completed)));
                        break;
                    }
                    case CorrelationSource.Performance:
                    {
                        if (performance is null) { reads.Add(Missing(source, "sessões de desempenho")); break; }
                        var snapshot = await performance.LoadSessionsAsync(cancellationToken).ConfigureAwait(false);
                        var (taken, hit) = TakeRecent(snapshot.Sessions, record => record.CompletedUtc);
                        performanceRecords.AddRange(taken);
                        reads.Add(Read(source, taken.Count, hit, snapshot.Warnings,
                            taken.Count(record => record.Status != PerformanceSessionStatus.Completed ||
                                (record.Coverage?.Status ?? PerformanceCoverageStatus.Complete) !=
                                PerformanceCoverageStatus.Complete)));
                        break;
                    }
                    case CorrelationSource.Network:
                    {
                        if (network is null) { reads.Add(Missing(source, "sessões de rede")); break; }
                        var snapshot = await network.LoadNetworkSessionsAsync(cancellationToken).ConfigureAwait(false);
                        var (taken, hit) = TakeRecent(snapshot.Sessions, record => record.CompletedUtc);
                        networkRecords.AddRange(taken);
                        reads.Add(Read(source, taken.Count, hit, snapshot.Warnings,
                            taken.Count(record => record.Status != NetworkSessionStatus.Completed ||
                                (record.Coverage?.Status ?? NetworkCoverageStatus.Complete) !=
                                NetworkCoverageStatus.Complete)));
                        break;
                    }
                    case CorrelationSource.Graphics:
                    {
                        if (graphics is null) { reads.Add(Missing(source, "investigações gráficas")); break; }
                        var snapshot = await graphics.LoadGraphicsInvestigationsAsync(cancellationToken)
                            .ConfigureAwait(false);
                        var (taken, hit) = TakeRecent(snapshot.Investigations, record => record.CompletedUtc);
                        graphicsRecords.AddRange(taken);
                        reads.Add(Read(source, taken.Count, hit, snapshot.Warnings,
                            taken.Count(record => record.Status != GraphicsInvestigationStatus.Completed ||
                                record.Coverage.Status != GraphicsCoverageStatus.Complete)));
                        break;
                    }
                    default:
                        if (source == CorrelationSource.Monitoring)
                        {
                            if (monitoring is null) { reads.Add(Missing(source, "sessões de monitoramento")); break; }
                            var snapshot = await monitoring.LoadMonitoringSessionsAsync(cancellationToken)
                                .ConfigureAwait(false);
                            var (taken, hit) = TakeRecent(snapshot.Sessions, record => record.CompletedUtc);
                            var selectedIds = taken.Select(record => record.Id).ToHashSet();
                            monitoringRecords.AddRange(taken);
                            monitoringSegments.AddRange(snapshot.Segments
                                .Where(segment => selectedIds.Contains(segment.SessionId)));
                            reads.Add(Read(source, taken.Count, hit, snapshot.Warnings,
                                taken.Count(record => record.Status != MonitoringExecutionStatus.Completed ||
                                    record.CoverageStatus != MonitoringCoverageStatus.Complete)));
                            break;
                        }
                        throw new ArgumentOutOfRangeException(nameof(source));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                reads.Add(new(source, CorrelationSourceStatus.Unavailable, 0, false,
                    [$"Fonte indisponível ({error.GetType().Name}): {error.Message}"]));
            }
        }

        return new(auditRecords, hardwareRecords, eventRecords, performanceRecords, networkRecords,
            graphicsRecords, reads)
        {
            MonitoringSessions = monitoringRecords,
            MonitoringSegments = monitoringSegments
        };
    }

    private (IReadOnlyList<T> Records, bool LimitHit) TakeRecent<T>(IReadOnlyList<T> records, Func<T, DateTimeOffset> key)
    {
        var ordered = records.OrderByDescending(key).ToArray();
        var hit = ordered.Length > limits.MaxRecordsPerSource;
        return (ordered.Take(limits.MaxRecordsPerSource).ToArray(), hit);
    }

    private static CorrelationSourceRead Missing(CorrelationSource source, string name) =>
        new(source, CorrelationSourceStatus.Unavailable, 0, false,
            [$"Armazenamento de {name} não disponível nesta composição."]);

    private static CorrelationSourceRead Read(CorrelationSource source, int count, bool limitHit,
        IReadOnlyList<string> warnings, int incompleteRecords = 0)
    {
        var limitations = new List<string>();
        if (limitHit)
            limitations.Add($"Apenas os {count} registros mais recentes foram lidos; o histórico anterior não " +
                "participou desta investigação.");
        limitations.AddRange(warnings);
        if (incompleteRecords > 0)
            limitations.Add($"{incompleteRecords} registro(s) de origem declararam coleta parcial, cancelada, " +
                "com falha ou indisponível; a cobertura desta fonte herda essa limitação. Os detalhes permanecem " +
                "nos registros originais.");
        var status = limitHit || warnings.Count > 0 || incompleteRecords > 0
            ? CorrelationSourceStatus.Partial
            : CorrelationSourceStatus.Completed;
        if (count == 0)
        {
            status = CorrelationSourceStatus.Partial;
            limitations.Add("Nenhum registro histórico persistido nesta fonte; ela não pôde cobrir o período " +
                "solicitado. Ausência de histórico não é ausência de acontecimento.");
        }
        return new(source, status, count, limitHit, limitations);
    }

    private static CorrelationSources Flag(CorrelationSource source) => source switch
    {
        CorrelationSource.Audits => CorrelationSources.Audits,
        CorrelationSource.Hardware => CorrelationSources.Hardware,
        CorrelationSource.Events => CorrelationSources.Events,
        CorrelationSource.Performance => CorrelationSources.Performance,
        CorrelationSource.Network => CorrelationSources.Network,
        CorrelationSource.Graphics => CorrelationSources.Graphics,
        CorrelationSource.Monitoring => CorrelationSources.Monitoring,
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };
}
