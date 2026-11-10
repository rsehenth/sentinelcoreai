using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Runs existing diagnostics and keeps their native histories as the source of evidence.</summary>
public sealed class FullAuditDiagnostics(HardwareRunner hardwareRunner, EventInvestigationRunner eventRunner,
    PerformanceSessionRunner performanceRunner, NetworkSessionRunner networkRunner,
    GraphicsInvestigationRunner graphicsRunner, MonitoringSessionRunner monitoringRunner,
    IAuditDiagnosticStore store)
{
    internal IReadOnlyList<AuditStep> CreateSteps(Guid auditId, bool monitoringAuthorized, TimeProvider clock)
    {
        HardwareSnapshot? hardware = null;
        EventInvestigation? events = null;

        AuditStep Step(CorrelationSource source, string origin, int seconds,
            Func<CancellationToken, Action<string>, Task<AuditStepReading>> read) =>
            new(AuditEvidenceReference.CollectorIdFor(source), SoftwareInfo.Version, origin, TimeSpan.FromSeconds(seconds), read);

        AuditStepReading Observed(CorrelationSource source, string origin, Guid recordId, DateTimeOffset started,
            DateTimeOffset ended, TimeSpan duration, DiagnosticStatus status, bool available, string summary,
            IEnumerable<string> limitations, Func<Task> save)
        {
            if (status == DiagnosticStatus.Partial && !available) status = DiagnosticStatus.Failed;
            else if (status == DiagnosticStatus.Failed && available) status = DiagnosticStatus.Partial;
            var notes = limitations.Where(note => !string.IsNullOrWhiteSpace(note)).Distinct().Take(8)
                .Select(note => note.Length <= 512 ? note : note[..511] + "…").ToArray();
            if (notes.Length == 0 && status != DiagnosticStatus.Completed)
                notes = ["O módulo não confirmou cobertura completa. Consulte o registro de origem."];

            return new(new CollectorResult(auditId, AuditEvidenceReference.CollectorIdFor(source), SoftwareInfo.Version,
                started, ended, duration, status, origin, null, notes)
            {
                Evidence = new(source, recordId, available ? summary : "O registro de execução explica a indisponibilidade da evidência.")
            }, available, save);
        }

        const string hardwareOrigin = "Inventário de hardware do Windows";
        const string eventOrigin = "Log de Eventos do Windows e metadados WER";
        const string performanceOrigin = "Contadores locais de desempenho do Windows";
        const string networkOrigin = "Tabelas e contadores locais de rede do Windows";
        const string graphicsOrigin = "Inventário, eventos e metadados WER desta auditoria";
        const string monitoringOrigin = "Sessão local de monitoramento do Windows";

        return
        [
            Step(CorrelationSource.Hardware, hardwareOrigin, 120, async (token, report) =>
            {
                var snapshot = await hardwareRunner.RunAsync(
                    new Activity<HardwareProgress>(p => report(p.CollectorId)), token).ConfigureAwait(false);
                return Observed(CorrelationSource.Hardware, hardwareOrigin, snapshot.Id, snapshot.StartedUtc,
                    snapshot.CompletedUtc, snapshot.Duration, Map(snapshot.Status),
                    snapshot.Results.Any(r => r.Status is HardwareCollectionStatus.Completed or HardwareCollectionStatus.Partial || r.Items.Count > 0),
                    $"{snapshot.Results.Sum(r => r.Items.Count)} itens observados no inventário.",
                    snapshot.Results.SelectMany(r => r.Limitations), async () =>
                    {
                        await store.SaveSnapshotAsync(snapshot).ConfigureAwait(false);
                        hardware = snapshot;
                    });
            }),
            Step(CorrelationSource.Events, eventOrigin, 120, async (token, report) =>
            {
                var investigation = await eventRunner.RunAsync(EventWindow.Last24Hours,
                    progress: new Activity<EventInvestigationProgress>(p => report(p.CollectorId)),
                    cancellationToken: token).ConfigureAwait(false);
                var eventLogAvailable = investigation.Events.Count > 0 || investigation.Sources.Any(s =>
                    s.CollectorId != GraphicsEvidenceClassification.WerMetadataCollectorId &&
                    s.Status is EventSourceStatus.Completed or EventSourceStatus.Partial);
                var werAvailable = investigation.Reports.Count > 0 || investigation.Sources.Any(s =>
                    s.CollectorId == GraphicsEvidenceClassification.WerMetadataCollectorId &&
                    s.Status is EventSourceStatus.Completed or EventSourceStatus.Partial);
                var eventSummary = eventLogAvailable ? $"{investigation.Events.Count} eventos observados" : "Log de Eventos indisponível";
                var werSummary = werAvailable ? $"{investigation.Reports.Count} registros WER observados" : "WER indisponível";
                return Observed(CorrelationSource.Events, eventOrigin, investigation.Id, investigation.StartedUtc,
                    investigation.CompletedUtc, investigation.Duration, Map(investigation.Status),
                    eventLogAvailable || werAvailable, $"Janela de 24 h · {eventSummary} · {werSummary}.",
                    investigation.Limitations.Concat(investigation.Sources.SelectMany(s => s.Limitations)), async () =>
                    {
                        await store.SaveInvestigationAsync(investigation).ConfigureAwait(false);
                        events = investigation;
                    });
            }),
            Step(CorrelationSource.Performance, performanceOrigin, 90, async (token, report) =>
            {
                var session = await performanceRunner.RunAsync(PerformanceSessionPreset.Seconds30, hardware,
                    new Activity<PerformanceProgress>(p => report($"CPU, memória, processos e discos · {p.SamplesCollected} amostras")),
                    token).ConfigureAwait(false);
                return Observed(CorrelationSource.Performance, performanceOrigin, session.Id, session.StartedUtc,
                    session.CompletedUtc, session.Duration, Map(session.Status),
                    session.Samples.Any(s => s.Cpu is { Valid: true } || s.Memory is not null || s.Processes.Count > 0 || s.Storage.Count > 0),
                    $"{session.Samples.Count} amostras e {session.Observations.Count} indicadores registrados.",
                    session.Limitations.Concat(session.Sources.SelectMany(s => s.Limitations)), () => store.SaveSessionAsync(session));
            }),
            Step(CorrelationSource.Network, networkOrigin, 45, async (token, report) =>
            {
                report("Tabelas de conexões, contadores e adaptadores");
                var session = await networkRunner.RunAsync(NetworkSessionPreset.Snapshot, events,
                    cancellationToken: token).ConfigureAwait(false);
                return Observed(CorrelationSource.Network, networkOrigin, session.Id, session.StartedUtc,
                    session.CompletedUtc, session.Duration, Map(session.Status),
                    session.Sources.Any(s => s.Status is NetworkSourceStatus.Completed or NetworkSourceStatus.Partial) ||
                    session.Adapters.Count > 0 || session.TcpStatistics is not null || session.UdpStatistics is not null,
                    $"{session.Samples.Count} amostras e {session.Observations.Count} indicadores registrados. Endereços omitidos.",
                    session.Limitations.Concat(session.Sources.SelectMany(s => s.Limitations)), () => store.SaveNetworkSessionAsync(session));
            }),
            Step(CorrelationSource.Graphics, graphicsOrigin, 45, async (token, report) =>
            {
                var started = clock.GetUtcNow();
                if (hardware is null)
                    return new(new(auditId, AuditEvidenceReference.CollectorIdFor(CorrelationSource.Graphics), SoftwareInfo.Version,
                        started, started, TimeSpan.Zero, DiagnosticStatus.Failed, graphicsOrigin, null,
                        ["Inventário desta auditoria indisponível. A investigação gráfica não foi iniciada."]), false);

                report("Consolidando adaptadores, monitores, drivers e eventos");
                var investigation = await graphicsRunner.RunFromAuditAsync(hardware, events, token).ConfigureAwait(false);
                var adapterSummary = investigation.Coverage.AdaptersUnavailable ? "Adaptadores indisponíveis"
                    : $"{investigation.Adapters.Count} adaptadores observados";
                return Observed(CorrelationSource.Graphics, graphicsOrigin, investigation.Id, investigation.StartedUtc,
                    investigation.CompletedUtc, investigation.Duration, Map(investigation.Status),
                    investigation.Sources.Any(s => s.Status is GraphicsSourceStatus.Completed or GraphicsSourceStatus.Partial),
                    $"{adapterSummary} · {investigation.Observations.Count} indicadores registrados.",
                    investigation.Limitations.Concat(investigation.Sources.SelectMany(s => s.Limitations)),
                    () => store.SaveGraphicsInvestigationAsync(investigation));
            }),
            Step(CorrelationSource.Monitoring, monitoringOrigin, 360, async (token, report) =>
            {
                var request = MonitoringRequest.ForPreset(MonitoringDurationPreset.Minutes5, MonitoringSources.All, monitoringAuthorized);
                var session = await monitoringRunner.RunAsync(request,
                    new Activity<MonitoringProgress>(p => report($"{p.Phase} · {p.Elapsed:mm\\:ss} decorridos")), token)
                    .ConfigureAwait(false);
                var status = session.Status switch
                {
                    MonitoringExecutionStatus.Completed when session.CoverageStatus == MonitoringCoverageStatus.Complete => DiagnosticStatus.Completed,
                    MonitoringExecutionStatus.Completed => DiagnosticStatus.Partial,
                    MonitoringExecutionStatus.Cancelled => DiagnosticStatus.Cancelled,
                    _ => DiagnosticStatus.Failed
                };
                return Observed(CorrelationSource.Monitoring, monitoringOrigin, session.Id, session.StartedUtc,
                    session.CompletedUtc, session.Duration, status, session.CoverageStatus != MonitoringCoverageStatus.Unavailable,
                    $"{session.Segments.Count} segmentos confirmados de observação.",
                    session.Sources.SelectMany(s => s.Limitations).Concat(session.Limitations), () => store.SaveMonitoringSessionAsync(session));
            })
        ];
    }

    private static DiagnosticStatus Map(Enum status) => status switch
    {
        HardwareCollectionStatus.Completed or EventInvestigationStatus.Completed or PerformanceSessionStatus.Completed or
            NetworkSessionStatus.Completed or GraphicsInvestigationStatus.Completed => DiagnosticStatus.Completed,
        HardwareCollectionStatus.Partial or EventInvestigationStatus.Partial or PerformanceSessionStatus.Partial or
            NetworkSessionStatus.Partial or GraphicsInvestigationStatus.Partial => DiagnosticStatus.Partial,
        HardwareCollectionStatus.Cancelled or EventInvestigationStatus.Cancelled or PerformanceSessionStatus.Cancelled or
            NetworkSessionStatus.Cancelled or GraphicsInvestigationStatus.Cancelled => DiagnosticStatus.Cancelled,
        _ => DiagnosticStatus.Failed
    };

    private sealed class Activity<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
