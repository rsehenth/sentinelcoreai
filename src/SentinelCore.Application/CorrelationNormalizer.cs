using System.Globalization;
using SentinelCore.Domain;

namespace SentinelCore.Application;

/// <summary>Resultado da normalização: evidências consolidadas, linha do tempo e resumo por fonte.</summary>
public sealed record CorrelationNormalizationResult(
    IReadOnlyList<CorrelationEvidence> Evidence,
    IReadOnlyList<CorrelationTimelineItem> Timeline,
    CorrelationBootReference Boot,
    IReadOnlyList<CorrelationSourceSummary> Sources,
    CorrelationEvidenceInput Input,
    int EvidenceFound,
    int MergedReferences,
    int UndatedEvidence,
    bool LimitHit,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Converte registros heterogêneos em evidências normalizadas, consolida representações duplicadas da mesma
/// ocorrência e monta a linha do tempo. Nenhum registro original é alterado: cada evidência mantém as referências
/// de todas as origens que a representam.
/// </summary>
public sealed class CorrelationEvidenceNormalizer
{
    private const string DivergentTimestampLimitation =
        "Representações da mesma ocorrência divergem no horário; o horário original foi preservado e a divergência " +
        "permanece declarada.";

    public CorrelationNormalizationResult Normalize(CorrelationEvidenceInput input, EventQueryWindow window,
        CorrelationLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();

        var builder = new Builder(window, limits);
        var boot = ResolveBoot(input.Audits, window, builder);
        builder.Boot = boot;

        builder.AddHardware(input.HardwareSnapshots, cancellationToken);
        builder.AddEvents(input.EventInvestigations, cancellationToken);
        builder.AddPerformance(input.PerformanceSessions, cancellationToken);
        builder.AddNetwork(input.NetworkSessions, cancellationToken);
        builder.AddGraphics(input.GraphicsInvestigations, cancellationToken);
        builder.AddMonitoring(input.MonitoringSessions, input.MonitoringSegments, cancellationToken);
        builder.AddAudits(input.Audits);

        return builder.Build(input, boot);
    }

    private static CorrelationBootReference ResolveBoot(IReadOnlyList<AuditRecord> audits, EventQueryWindow window,
        Builder builder)
    {
        // A referência só é aceita se a auditoria for recente o bastante para a janela; uma auditoria antiga não
        // prova qual inicialização estava ativa. Reinicializações posteriores à coleta também não são detectáveis.
        var oldestAcceptable = window.FromUtc - TimeSpan.FromHours(24);
        var reference = audits
            .Where(audit => audit.CompletedUtc <= window.ToUtc && audit.CompletedUtc >= oldestAcceptable)
            .OrderByDescending(audit => audit.CompletedUtc)
            .Select(audit => new
            {
                Audit = audit,
                Boot = audit.Results.Select(result => result.Data?.OperatingSystem?.LastBootUtc)
                    .FirstOrDefault(value => value is not null)
            })
            .FirstOrDefault(item => item.Boot is not null);
        if (reference is null)
            return new CorrelationBootReference(null, null, null,
                "Referência de inicialização do Windows indisponível ou desatualizada nas auditorias persistidas; " +
                "registros de inicializações diferentes podem não estar separados.");
        var bootUtc = reference.Boot!.Value.ToUniversalTime();
        if (bootUtc > window.ToUtc.AddDays(1) || bootUtc.Year < 2000)
        {
            builder.Limitations.Add("O horário de inicialização informado pela auditoria está fora da faixa " +
                "plausível e não foi usado para separar sessões do Windows.");
            return CorrelationBootReference.Unknown;
        }

        return new CorrelationBootReference(bootUtc, reference.Audit.Id, reference.Audit.CompletedUtc,
            $"Inicialização informada pela auditoria {reference.Audit.Id:D}, coletada em " +
            $"{reference.Audit.CompletedUtc:O}. Registros anteriores a esse instante pertencem a uma sessão " +
            "anterior; reinicializações posteriores à coleta da auditoria não são detectáveis por esta referência.");
    }

    private sealed class SourceStat
    {
        public int References;
        public bool RecordUsed;
        public bool CoveredWindow;
        public readonly HashSet<string> UsedRecords = new(StringComparer.Ordinal);
    }

    private sealed class Builder
    {
        private readonly EventQueryWindow window;
        private readonly CorrelationLimits limits;
        private readonly Dictionary<string, CorrelationEvidence> byId = new(StringComparer.Ordinal);
        private readonly List<string> order = [];
        private readonly Dictionary<CorrelationSource, SourceStat> stats = [];
        private bool limitHit;
        private bool timelineLimitHit;

        public Builder(EventQueryWindow window, CorrelationLimits limits)
        {
            this.window = window;
            this.limits = limits;
            foreach (var source in Enum.GetValues<CorrelationSource>()) stats[source] = new();
        }

        public CorrelationBootReference Boot { get; set; } = CorrelationBootReference.Unknown;

        public readonly List<string> Limitations = [];
        private int mergedReferences;

        public void AddAudits(IReadOnlyList<AuditRecord> audits)
        {
            // O inventário estático não é evidência temporal. A auditoria participa apenas como referência de
            // inicialização do Windows, usada para não misturar sessões distintas.
            foreach (var audit in audits)
                if (audit.Results.Any(result => result.Data?.OperatingSystem?.LastBootUtc is not null))
                    stats[CorrelationSource.Audits].UsedRecords.Add(audit.Id.ToString("D"));
        }

        public void AddEvents(IReadOnlyList<EventInvestigation> investigations, CancellationToken cancellationToken)
        {
            foreach (var investigation in investigations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Overlaps(investigation.PeriodFromUtc, investigation.PeriodToUtc)) continue;
                stats[CorrelationSource.Events].RecordUsed = true;
                stats[CorrelationSource.Events].UsedRecords.Add(investigation.Id.ToString("D"));

                var groups = investigation.Groups
                    .GroupBy(group => group.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                foreach (var item in investigation.Events)
                {
                    var severity = groups.TryGetValue(item.GroupId, out var group)
                        ? group.Severity
                        : SeverityFor(item.Level);
                    var logicalId = item.RecordId is null
                        ? $"evt:{investigation.Id:D}:{item.Id}"
                        : item.Id;
                    var limitations = new List<string>
                    {
                        "Registro do Log de Eventos preservado na origem; evento observado não é diagnóstico."
                    };
                    if (item.RecordId is null)
                        limitations.Add("Identidade sem RecordId; a evidência não foi consolidada com outras " +
                            "investigações.");
                    limitations.AddRange(item.Limitations);
                    AddPoint(CorrelationSource.Events, CorrelationEvidenceKind.EventLog, logicalId,
                        investigation.Id, item.GroupId,
                        $"Evento {item.EventId} — {item.Provider}", Clamp(item.Message),
                        item.OccurredUtc, CorrelationTimestampPrecision.Exact,
                        logicalId, CorrelationIdentityKind.EventRecord,
                        item.Provider, severity, ConfidenceFor(item, group), limitations);
                }

                foreach (var report in investigation.Reports)
                    AddWer(CorrelationSource.Events, investigation.Id, report.GroupId, report.Id, report.ReportId,
                        report.EventType, report.EventTimeUtc, report.CreationTimeUtc, report.Application,
                        report.Module, report.Confidence, report.Limitations);
            }
        }

        public void AddGraphics(IReadOnlyList<GraphicsInvestigation> investigations,
            CancellationToken cancellationToken)
        {
            foreach (var investigation in investigations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Overlaps(investigation.PeriodFromUtc, investigation.PeriodToUtc)) continue;
                stats[CorrelationSource.Graphics].RecordUsed = true;
                stats[CorrelationSource.Graphics].UsedRecords.Add(investigation.Id.ToString("D"));

                foreach (var item in investigation.Events)
                {
                    var logicalId = item.RecordId is null
                        ? $"evt:{investigation.Id:D}:{item.Id}"
                        : item.Id;
                    var limitations = new List<string>
                    {
                        "Classificação gráfica da origem; o evento não identifica causa ou defeito físico."
                    };
                    if (item.RecordId is null)
                        limitations.Add("Identidade sem RecordId; a evidência não foi consolidada com outras " +
                            "investigações.");
                    limitations.AddRange(item.Limitations);
                    AddPoint(CorrelationSource.Graphics, CorrelationEvidenceKind.EventLog, logicalId,
                        investigation.Id, item.GroupId,
                        $"Evento gráfico {item.EventId} — {item.Provider}", Clamp(item.Message),
                        item.OccurredUtc, CorrelationTimestampPrecision.Exact,
                        logicalId, CorrelationIdentityKind.EventRecord,
                        item.Provider,
                        item.Kind == GraphicsEventKind.DisplayDriverReset
                            ? EvidenceSeverity.ObservedImpact
                            : EvidenceSeverity.PotentialImpact,
                        EvidenceConfidence.High, limitations);
                }

                foreach (var report in investigation.WerReports)
                    AddWer(CorrelationSource.Graphics, investigation.Id, report.GroupId, report.Id, report.ReportId,
                        report.EventType, report.EventTimeUtc, report.CreationTimeUtc, report.Application,
                        report.Module, report.Confidence, report.Limitations);

                foreach (var observation in investigation.Observations)
                {
                    AddInterval(CorrelationSource.Graphics, CorrelationEvidenceKind.GraphicsObservation,
                        $"gfx:{investigation.Id:D}:{observation.Id}", investigation.Id, observation.RuleId,
                        observation.Title, Clamp(observation.Statement), observation.Kind.ToString(),
                        observation.WindowFromUtc, observation.WindowToUtc, null,
                        CorrelationIdentityKind.None, null, observation.Severity, observation.Confidence,
                        [
                            "Observação determinística da investigação gráfica de origem; causalidade não avaliada.",
                            .. observation.Limitations
                        ]);
                }

                if (investigation.ChangesComparable)
                {
                    foreach (var change in investigation.Changes)
                    {
                        if (change.Type is GraphicsChangeType.ConfigurationChanged or GraphicsChangeType.StateChanged)
                            continue;
                        AddPoint(CorrelationSource.Graphics, CorrelationEvidenceKind.GraphicsChange,
                            $"gfxchg:{investigation.Id:D}:{change.Type}:{change.Subject}:{change.Field}",
                            investigation.Id, null,
                            $"Alteração gráfica observada: {ChangeName(change.Type)}",
                            Clamp($"{change.SubjectName ?? change.Subject}" +
                                (change.Field is null ? string.Empty : $" · {change.Field}: {change.Before ?? "—"} → {change.After ?? "—"}")),
                            investigation.CompletedUtc, CorrelationTimestampPrecision.Coarse,
                            change.Subject, CorrelationIdentityKind.DeviceStableId, change.SubjectName,
                            EvidenceSeverity.Informational, EvidenceConfidence.Medium,
                            [
                                "Alteração observada entre investigações gráficas; não indica causa, melhoria ou regressão.",
                                "O instante exato da alteração é desconhecido: apenas a coleta que a observou é conhecida."
                            ]);
                    }
                }

                AddDeviceInventory(CorrelationSource.Graphics, investigation.Id, investigation.CompletedUtc,
                    investigation.Adapters.Select(adapter => (adapter.StableId, adapter.Name, "gpu")),
                    "Adaptador gráfico", "gfxadapter");
                AddDeviceInventory(CorrelationSource.Graphics, investigation.Id, investigation.CompletedUtc,
                    investigation.Monitors.Select(monitor => (monitor.StableId, monitor.Name, "monitor")),
                    "Monitor", "gfxmonitor");
            }
        }

        /// <summary>
        /// Normaliza as sessões de monitoramento da Fase 5. Eventos observados reutilizam a identidade do Log de
        /// Eventos, portanto são consolidados com Eventos e Gráficos sem contagem dupla. Amostras de desempenho e
        /// contagens de rede entram como intervalos observados e alimentam as mesmas regras já existentes.
        /// Incidentes manuais não são convertidos em evidência nativa: eles servem apenas de referência temporal
        /// para uma investigação direcionada.
        /// </summary>
        public void AddMonitoring(IReadOnlyList<MonitoringSession> sessions,
            IReadOnlyList<MonitoringSegment> segments, CancellationToken cancellationToken)
        {
            foreach (var session in sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sessionSegments = segments.Where(segment => segment.SessionId == session.Id)
                    .OrderBy(segment => segment.Source)
                    .ThenBy(segment => segment.Sequence)
                    .ToArray();
                if (!Overlaps(session.StartedUtc, session.CompletedUtc) &&
                    !sessionSegments.Any(segment => Overlaps(segment.FromUtc, segment.ToUtc)))
                    continue;
                stats[CorrelationSource.Monitoring].CoveredWindow = true;
                var used = false;
                foreach (var segment in sessionSegments)
                {
                    foreach (var item in segment.Events)
                    {
                        var logicalId = item.RecordId is null
                            ? $"mon:{session.Id:D}:{segment.Id:D}:{item.Index}"
                            : $"evt:{item.Channel}:{item.RecordId}";
                        var limitations = new List<string>
                        {
                            "Evento observado durante sessão de monitoramento autorizada; o registro não é " +
                            "diagnóstico e não prova causa.",
                            "Identidade do Log de Eventos (canal e RecordId) preservada para deduplicação."
                        };
                        if (item.RecordId is null)
                            limitations.Add("Identidade sem RecordId; a evidência não foi consolidada com outras " +
                                "investigações.");
                        if (item.MessageTruncated)
                            limitations.Add("Mensagem formatada truncada na observação; os identificadores técnicos " +
                                "foram preservados.");
                        used |= AddPoint(CorrelationSource.Monitoring, CorrelationEvidenceKind.EventLog, logicalId,
                            session.Id, item.GroupId,
                            $"Evento {item.EventId} — {item.Provider}", Clamp(item.Message),
                            item.OccurredUtc, CorrelationTimestampPrecision.Exact,
                            logicalId, CorrelationIdentityKind.EventRecord, item.Provider, item.Severity,
                            item.RecordId is null ? EvidenceConfidence.Medium : EvidenceConfidence.High,
                            limitations);
                    }

                    if (segment.Performance.Count > 0)
                    {
                        var cpu = segment.Performance.Where(item => item.CpuTotalPercent is not null)
                            .Select(item => item.CpuTotalPercent!.Value).ToArray();
                        var available = segment.Performance
                            .Where(item => item.MemoryAvailablePercent is not null)
                            .Select(item => item.MemoryAvailablePercent!.Value).ToArray();
                        used |= AddInterval(CorrelationSource.Monitoring,
                            CorrelationEvidenceKind.PerformanceObservation,
                            $"monperf:{session.Id:D}:{segment.Id:D}", session.Id,
                            "monitoring:performance", "Amostras de CPU e memória (monitoramento)",
                            Clamp($"{segment.Performance.Count} amostra(s) · CPU total {Range(cpu, "%")} · " +
                                $"memória disponível {Range(available, "%")}"),
                            "cpu.total.percent",
                            segment.Performance.Min(item => item.TimestampUtc),
                            segment.Performance.Max(item => item.TimestampUtc), null,
                            CorrelationIdentityKind.None, null, EvidenceSeverity.Informational,
                            EvidenceConfidence.Low,
                            [
                                "Amostras pontuais de baixo impacto obtidas de APIs nativas; não são benchmark e " +
                                "não equivalem a problema sustentado.",
                                "O monitoramento não executa consultas WMI, enumeração de processos ou leitura de " +
                                "disco durante a sessão.",
                                .. segment.Performance.SelectMany(item => item.Limitations)
                            ]);
                    }

                    if (segment.Network.Count > 0)
                    {
                        used |= AddInterval(CorrelationSource.Monitoring,
                            CorrelationEvidenceKind.NetworkObservation,
                            $"monnet:{session.Id:D}:{segment.Id:D}", session.Id,
                            "monitoring:network", "Contagens locais de rede (monitoramento)",
                            Clamp($"{segment.Network.Count} leitura(s) · TCP máximo " +
                                $"{segment.Network.Max(item => item.TcpConnections)} · UDP máximo " +
                                $"{segment.Network.Max(item => item.UdpEndpoints)} · processos máximo " +
                                $"{segment.Network.Max(item => item.ProcessesObserved)}"),
                            "network.endpoints.tcp",
                            segment.Network.Min(item => item.TimestampUtc),
                            segment.Network.Max(item => item.TimestampUtc), null,
                            CorrelationIdentityKind.None, null, EvidenceSeverity.Informational,
                            EvidenceConfidence.Low,
                            [
                                MonitoringGuidance.NetworkPrivacyLimitation,
                                "Contagens pontuais de tabelas locais; não capturam tráfego e não provam " +
                                "indisponibilidade ou esgotamento.",
                                .. segment.Network.SelectMany(item => item.Limitations)
                            ]);
                    }
                }

                if (used)
                {
                    stats[CorrelationSource.Monitoring].RecordUsed = true;
                    stats[CorrelationSource.Monitoring].UsedRecords.Add(session.Id.ToString("D"));
                }
            }
        }

        public void AddHardware(IReadOnlyList<HardwareSnapshot> snapshots, CancellationToken cancellationToken)
        {
            var ordered = snapshots.OrderBy(snapshot => snapshot.CompletedUtc).ToArray();
            var inWindow = ordered.Where(snapshot =>
                snapshot.CompletedUtc >= window.FromUtc && snapshot.CompletedUtc <= window.ToUtc).ToArray();
            if (inWindow.Length > 0)
            {
                stats[CorrelationSource.Hardware].RecordUsed = true;
                stats[CorrelationSource.Hardware].UsedRecords.Add(inWindow[^1].Id.ToString("D"));
            }

            foreach (var pair in ordered.Zip(ordered.Skip(1), (previous, current) => (previous, current)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pair.current.CompletedUtc < window.FromUtc || pair.current.CompletedUtc > window.ToUtc)
                    continue;
                stats[CorrelationSource.Hardware].RecordUsed = true;
                stats[CorrelationSource.Hardware].UsedRecords.Add(pair.current.Id.ToString("D"));
                HardwareDiff diff;
                try
                {
                    diff = HardwareDiffEngine.Compare(pair.previous, pair.current);
                }
                catch (Exception error) when (error is InvalidDataException or ArgumentException
                    or InvalidOperationException)
                {
                    Limitations.Add($"Inventários {pair.previous.Id:D} e {pair.current.Id:D} não comparados " +
                        $"({error.GetType().Name}): {error.Message}");
                    continue;
                }

                foreach (var limitation in diff.Limitations)
                    Limitations.Add($"Comparação {pair.previous.Id:D}→{pair.current.Id:D}: {limitation}");
                if (diff.NotComparable.Count > 0)
                    Limitations.Add($"Categorias não comparáveis entre {pair.previous.Id:D} e {pair.current.Id:D}: " +
                        $"{string.Join(", ", diff.NotComparable)}. Ausência de alteração nessas categorias não foi " +
                        "avaliada.");

                foreach (var change in diff.Changes)
                {
                    if (change.Type is not (HardwareChangeType.Added or HardwareChangeType.Removed
                        or HardwareChangeType.DriverChanged or HardwareChangeType.FirmwareChanged))
                        continue;
                    AddPoint(CorrelationSource.Hardware, CorrelationEvidenceKind.HardwareChange,
                        $"hwchg:{pair.current.Id:D}:{change.Type}:{change.Category}:{change.StableId}:{change.Field}",
                        pair.current.Id, null,
                        $"Alteração de hardware observada: {ChangeName(change.Type)}",
                        Clamp($"{change.Name ?? change.StableId} · {HardwareCategoryName(change.Category)}" +
                            (change.Field is null ? string.Empty : $" · {change.Field}: {change.Before ?? "—"} → {change.After ?? "—"}")),
                        pair.current.CompletedUtc, CorrelationTimestampPrecision.Coarse,
                        change.StableId,
                        change.Type is HardwareChangeType.DriverChanged or HardwareChangeType.FirmwareChanged
                            ? CorrelationIdentityKind.DeviceStableId
                            : CorrelationIdentityKind.None,
                        change.Name, EvidenceSeverity.Informational, EvidenceConfidence.Medium,
                        [
                            "Alteração observada entre dois inventários; não indica causa, melhoria ou regressão.",
                            $"O instante exato é desconhecido: a alteração foi observada entre {pair.previous.CompletedUtc:O} " +
                            $"e {pair.current.CompletedUtc:O}."
                        ]);
                }
            }

            var inventoryCount = 0;
            foreach (var category in new[] { HardwareCategory.Gpu, HardwareCategory.Monitor })
            {
                var snapshot = ordered.LastOrDefault(candidate =>
                    candidate.CompletedUtc >= window.FromUtc && candidate.CompletedUtc <= window.ToUtc &&
                    candidate.Results.Any(result => result.Category == category && result.Items.Count > 0));
                if (snapshot is null) continue;
                stats[CorrelationSource.Hardware].RecordUsed = true;
                stats[CorrelationSource.Hardware].UsedRecords.Add(snapshot.Id.ToString("D"));
                var items = snapshot.Results
                    .Where(result => result.Category == category)
                    .SelectMany(result => result.Items)
                    .OrderBy(item => item.StableId, StringComparer.Ordinal)
                    .ToArray();
                foreach (var item in items)
                {
                    if (inventoryCount >= 20)
                    {
                        limitHit = true;
                        Limitations.Add("Limite de itens de inventário normalizados atingido; dispositivos adicionais " +
                            "permanecem apenas nos inventários originais.");
                        break;
                    }

                    AddPoint(CorrelationSource.Hardware, CorrelationEvidenceKind.DeviceInventory,
                        $"hwdev:{snapshot.Id:D}:{item.Category}:{item.StableId}", snapshot.Id, null,
                        $"Dispositivo de hardware: {item.Name ?? item.Kind}",
                        Clamp($"{HardwareCategoryName(item.Category)} · {item.Kind}" +
                            (string.IsNullOrWhiteSpace(item.HardwareId) ? string.Empty : $" · {item.HardwareId}")),
                        snapshot.CompletedUtc, CorrelationTimestampPrecision.Coarse,
                        item.StableId, CorrelationIdentityKind.DeviceStableId, item.Name,
                        EvidenceSeverity.Informational, EvidenceConfidence.High,
                        [
                            "Inventário é um retrato da coleta, não uma medição contínua.",
                            "Presença de um dispositivo não confirma uso efetivo nem integridade.",
                            "Apenas a coleta mais recente da categoria é normalizada; coletas anteriores permanecem " +
                            "nos registros originais.",
                            .. item.Limitations
                        ]);
                    inventoryCount++;
                }
            }
        }

        public void AddPerformance(IReadOnlyList<PerformanceSession> sessions, CancellationToken cancellationToken)
        {
            foreach (var session in sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Overlaps(session.StartedUtc, session.CompletedUtc)) continue;
                stats[CorrelationSource.Performance].RecordUsed = true;
                stats[CorrelationSource.Performance].UsedRecords.Add(session.Id.ToString("D"));
                foreach (var observation in session.Observations)
                {
                    var subject = string.IsNullOrWhiteSpace(observation.Subject) ? null : observation.Subject;
                    var identity = IsProcessIdentityKey(subject) ? subject : null;
                    var limitations = new List<string>
                    {
                        "Observação de desempenho medida em sessão; não é benchmark nem prova de causa.",
                        "Inventários estáticos não são medições de desempenho e não participam desta regra."
                    };
                    if (subject is not null && identity is null)
                        limitations.Add("Identidade de processo sem hora de criação verificável; o PID isolado não " +
                            "foi usado como identidade.");
                    limitations.AddRange(observation.Limitations);
                    AddInterval(CorrelationSource.Performance, CorrelationEvidenceKind.PerformanceObservation,
                        $"perf:{session.Id:D}:{observation.Id}", session.Id, observation.RuleId,
                        observation.Title, Clamp(observation.Statement), observation.Metric,
                        observation.WindowFromUtc, observation.WindowToUtc,
                        identity, identity is null ? CorrelationIdentityKind.None : CorrelationIdentityKind.ProcessInstance,
                        observation.SubjectName, observation.Severity, observation.Confidence, limitations);
                }
            }
        }

        public void AddNetwork(IReadOnlyList<NetworkSession> sessions, CancellationToken cancellationToken)
        {
            foreach (var session in sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Overlaps(session.StartedUtc, session.CompletedUtc)) continue;
                stats[CorrelationSource.Network].RecordUsed = true;
                stats[CorrelationSource.Network].UsedRecords.Add(session.Id.ToString("D"));
                foreach (var observation in session.Observations)
                {
                    var subject = string.IsNullOrWhiteSpace(observation.Subject) ? null : observation.Subject;
                    var identity = IsProcessIdentityKey(subject) ? subject : null;
                    var limitations = new List<string>
                    {
                        "Observação de rede derivada de tabelas locais; conexão observada não é causa de falha.",
                        "Ausência de conexão não prova indisponibilidade da rede."
                    };
                    if (subject is not null && identity is null)
                        limitations.Add("Identidade de processo sem hora de criação verificável; o PID isolado não " +
                            "foi usado como identidade.");
                    limitations.AddRange(observation.Limitations);
                    AddInterval(CorrelationSource.Network, CorrelationEvidenceKind.NetworkObservation,
                        $"net:{session.Id:D}:{observation.Id}", session.Id, observation.RuleId,
                        observation.Title, Clamp(observation.Statement), observation.Metric,
                        observation.WindowFromUtc, observation.WindowToUtc,
                        identity, identity is null ? CorrelationIdentityKind.None : CorrelationIdentityKind.ProcessInstance,
                        observation.SubjectName, observation.Severity, observation.Confidence, limitations);
                }
            }
        }

        private void AddWer(CorrelationSource source, Guid recordId, string? groupId, string id, string reportId,
            string? eventType, DateTimeOffset? eventTimeUtc, DateTimeOffset? creationTimeUtc, string? application,
            string? module, EvidenceConfidence confidence, IReadOnlyList<string> recordLimitations)
        {
            var limitations = new List<string>
            {
                "Metadados do WER não substituem o dump; nenhum dump foi lido, enviado ou apagado.",
                "A presença do relatório não prova falha de hardware nem identifica a causa."
            };
            limitations.AddRange(recordLimitations);

            CorrelationTimestampPrecision precision;
            DateTimeOffset? effective;
            if (eventTimeUtc is not null)
            {
                precision = CorrelationTimestampPrecision.Exact;
                effective = eventTimeUtc;
            }
            else if (creationTimeUtc is not null)
            {
                precision = CorrelationTimestampPrecision.Coarse;
                effective = creationTimeUtc;
                limitations.Add(CorrelationGuidance.WerDelayLimitation);
            }
            else
            {
                precision = CorrelationTimestampPrecision.Unknown;
                effective = null;
            }

            AddPoint(source, CorrelationEvidenceKind.WerReport, id, recordId, groupId,
                $"Relatório WER {eventType ?? "tipo indisponível"} — {reportId}",
                Clamp($"Aplicativo: {application ?? "indisponível"} · módulo: {module ?? "indisponível"}"),
                effective, precision, id, CorrelationIdentityKind.WerReport, null,
                EvidenceSeverity.PotentialImpact, confidence, limitations.ToArray());
        }

        private void AddDeviceInventory(CorrelationSource source, Guid recordId, DateTimeOffset at,
            IEnumerable<(string StableId, string? Name, string Kind)> items, string title, string prefix)
        {
            foreach (var item in items)
            {
                AddPoint(source, CorrelationEvidenceKind.DeviceInventory, $"{prefix}:{recordId:D}:{item.StableId}",
                    recordId, null, $"{title}: {item.Name ?? item.StableId}", Clamp(item.Kind),
                    at, CorrelationTimestampPrecision.Coarse, item.StableId,
                    CorrelationIdentityKind.DeviceStableId, item.Name,
                    EvidenceSeverity.Informational, EvidenceConfidence.High,
                    ["Identidade técnica do dispositivo preservada; uso efetivo e integridade não são confirmados."]);
            }
        }

        private bool AddPoint(CorrelationSource source, CorrelationEvidenceKind kind, string id, Guid recordId,
            string? groupId, string title, string? detail, DateTimeOffset? timestamp,
            CorrelationTimestampPrecision precision, string? identityKey, CorrelationIdentityKind identityKind,
            string? subject, EvidenceSeverity severity, EvidenceConfidence confidence,
            IReadOnlyList<string> limitations)
        {
            var withinWindow = Prepare(timestamp, precision, out var effective, out var finalPrecision,
                out var extra);
            var filtered = new List<string>(limitations);
            filtered.AddRange(extra);
            if (withinWindow && !LimitReached())
            {
                Add(source, new CorrelationEvidence(id, source, kind, recordId.ToString("D"), title, detail, groupId,
                    timestamp, effective, finalPrecision, null, null, identityKey, identityKind, subject, severity,
                    confidence, [new(source, recordId.ToString("D"), OriginReference(source, recordId, id))],
                    filtered.Distinct(StringComparer.Ordinal).ToArray()));
                return true;
            }

            return false;
        }

        private bool AddInterval(CorrelationSource source, CorrelationEvidenceKind kind, string id, Guid recordId,
            string? groupId, string title, string? detail, string metric, DateTimeOffset fromUtc, DateTimeOffset toUtc,
            string? identityKey, CorrelationIdentityKind identityKind, string? subject, EvidenceSeverity severity,
            EvidenceConfidence confidence, IReadOnlyList<string> limitations)
        {
            if (toUtc < fromUtc) (fromUtc, toUtc) = (toUtc, fromUtc);
            if (toUtc < window.FromUtc || fromUtc > window.ToUtc) return false;
            if (LimitReached()) return false;
            var limitationsFinal = new List<string>(limitations)
            {
                "A evidência representa uma janela observada; o horário mostrado é o início dessa janela."
            };
            if (fromUtc < window.FromUtc || toUtc > window.ToUtc)
                limitationsFinal.Add("A janela observada excede o período solicitado; o intervalo original foi " +
                    "preservado sem recorte.");
            if (toUtc - fromUtc > TimeSpan.FromHours(3))
                limitationsFinal.Add("Janela de observação longa; a precisão temporal é limitada ao intervalo.");
            Add(source, new CorrelationEvidence(id, source, kind, recordId.ToString("D"), title, detail, groupId,
                fromUtc, fromUtc, CorrelationTimestampPrecision.Interval, fromUtc, toUtc, identityKey, identityKind,
                subject, severity, confidence,
                [new(source, recordId.ToString("D"), OriginReference(source, recordId, id))],
                limitationsFinal.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).ToArray()));
            return true;
        }

        private bool Prepare(DateTimeOffset? timestamp, CorrelationTimestampPrecision precision,
            out DateTimeOffset? effective, out CorrelationTimestampPrecision finalPrecision,
            out IReadOnlyList<string> extra)
        {
            effective = null;
            finalPrecision = precision;
            var notes = new List<string>();
            if (timestamp is null)
            {
                finalPrecision = CorrelationTimestampPrecision.Unknown;
                notes.Add("Registro sem horário confiável: não foi posicionado na linha do tempo.");
                extra = notes;
                return true;
            }

            var value = timestamp.Value.ToUniversalTime();
            if (value.Year < 2000 || value > window.ToUtc.AddDays(1))
            {
                finalPrecision = CorrelationTimestampPrecision.Unknown;
                notes.Add($"Horário {value:O} está fora da faixa plausível e não foi usado como timestamp.");
                extra = notes;
                return true;
            }

            if (value < window.FromUtc || value > window.ToUtc)
            {
                extra = notes;
                return false;
            }

            effective = value;
            extra = notes;
            return true;
        }

        private bool LimitReached()
        {
            if (byId.Count < limits.MaxEvidence) return false;
            limitHit = true;
            if (!Limitations.Contains(CorrelationGuidance.LimitLimitation, StringComparer.Ordinal))
                Limitations.Add(CorrelationGuidance.LimitLimitation);
            return true;
        }

        private void Add(CorrelationSource source, CorrelationEvidence evidence)
        {
            var stat = stats[source];
            stat.References++;
            if (byId.TryGetValue(evidence.Id, out var existing))
            {
                var origins = existing.Origins.ToList();
                var originAdded = false;
                foreach (var origin in evidence.Origins)
                    if (!origins.Any(item => item.Source == origin.Source &&
                                             item.SourceRecordId == origin.SourceRecordId &&
                                             item.OriginReference == origin.OriginReference))
                    {
                        origins.Add(origin);
                        originAdded = true;
                    }

                var limitations = existing.Limitations.Concat(evidence.Limitations)
                    .Distinct(StringComparer.Ordinal).ToList();
                var precision = existing.Precision;
                var effective = existing.EffectiveTimestampUtc;
                var original = existing.OriginalTimestampUtc;
                var windowFrom = existing.WindowFromUtc;
                var windowTo = existing.WindowToUtc;
                if (precision == CorrelationTimestampPrecision.Unknown &&
                    evidence.Precision != CorrelationTimestampPrecision.Unknown)
                {
                    precision = evidence.Precision;
                    effective = evidence.EffectiveTimestampUtc;
                    original = evidence.OriginalTimestampUtc;
                    windowFrom = evidence.WindowFromUtc;
                    windowTo = evidence.WindowToUtc;
                }
                else if (precision != CorrelationTimestampPrecision.Unknown &&
                         evidence.Precision == CorrelationTimestampPrecision.Unknown)
                {
                    limitations.Add("Uma representação desta ocorrência não possuía horário confiável; o horário da " +
                        "representação com precisão foi preservado.");
                }
                else if (precision != CorrelationTimestampPrecision.Unknown &&
                         evidence.Precision != CorrelationTimestampPrecision.Unknown &&
                         effective != evidence.EffectiveTimestampUtc &&
                         !limitations.Contains(DivergentTimestampLimitation, StringComparer.Ordinal))
                {
                    limitations.Add(DivergentTimestampLimitation);
                }

                var identityKey = existing.IdentityKey;
                var identityKind = existing.IdentityKind;
                if (identityKey is null && evidence.IdentityKey is not null)
                {
                    identityKey = evidence.IdentityKey;
                    identityKind = evidence.IdentityKind;
                }
                else if (identityKey is not null && evidence.IdentityKey is not null &&
                         !string.Equals(identityKey, evidence.IdentityKey, StringComparison.Ordinal))
                {
                    limitations.Add("Representações da mesma ocorrência divergem na identidade relacionada; a " +
                        "identidade da primeira representação foi preservada.");
                }

                var groupId = existing.GroupId;
                if (groupId is not null && evidence.GroupId is not null &&
                    !string.Equals(groupId, evidence.GroupId, StringComparison.OrdinalIgnoreCase))
                    limitations.Add("Representações da mesma ocorrência divergem na classificação de grupo; a " +
                        "classificação da primeira representação foi preservada.");

                byId[evidence.Id] = existing with
                {
                    Source = existing.Source <= evidence.Source ? existing.Source : evidence.Source,
                    Origins = origins,
                    Limitations = limitations,
                    OriginalTimestampUtc = original,
                    EffectiveTimestampUtc = effective,
                    Precision = precision,
                    WindowFromUtc = windowFrom,
                    WindowToUtc = windowTo,
                    IdentityKey = identityKey,
                    IdentityKind = identityKind,
                    Severity = (EvidenceSeverity)Math.Max((int)existing.Severity, (int)evidence.Severity),
                    Confidence = (EvidenceConfidence)Math.Min((int)existing.Confidence, (int)evidence.Confidence)
                };
                if (originAdded) mergedReferences++;
                return;
            }

            byId[evidence.Id] = evidence;
            order.Add(evidence.Id);
        }

        public CorrelationNormalizationResult Build(CorrelationEvidenceInput input, CorrelationBootReference boot)
        {
            var evidence = order.Select(id => byId[id])
                .OrderBy(item => item.EffectiveTimestampUtc ?? DateTimeOffset.MaxValue)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            var evidenceFound = stats.Values.Sum(stat => stat.References);
            var merged = mergedReferences;
            if (merged > 0 && !Limitations.Contains(CorrelationGuidance.DeduplicationLimitation, StringComparer.Ordinal))
                Limitations.Add(CorrelationGuidance.DeduplicationLimitation);

            var timeline = evidence.Where(item => item.HasReliableTimestamp)
                .OrderBy(item => item.EffectiveTimestampUtc)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .Take(limits.MaxTimelineItems)
                .Select(item => new CorrelationTimelineItem(item.Id, item.EffectiveTimestampUtc!.Value,
                    item.Precision, item.Source, item.Kind, item.Title, item.IdentityKey, item.SourceRecordId,
                    item.Severity, item.Confidence))
                .ToArray();
            if (evidence.Count(item => item.HasReliableTimestamp) > timeline.Length) timelineLimitHit = true;
            if (timelineLimitHit)
            {
                limitHit = true;
                Limitations.Add("Limite de itens da linha do tempo atingido; os itens adicionais permanecem nas " +
                    "evidências normalizadas.");
            }

            var undated = evidence.Count(item => !item.HasReliableTimestamp);
            if (undated > 0)
                Limitations.Add($"{undated} evidência(s) sem horário confiável não foram posicionadas na linha do " +
                    "tempo e permanecem listadas separadamente.");

            var sources = new List<CorrelationSourceSummary>();
            foreach (var read in input.SourceReads)
            {
                var stat = stats[read.Source];
                var limitations = read.Limitations.ToList();
                var status = read.Status;
                if (status is CorrelationSourceStatus.Completed && read.RecordsRead > 0 &&
                    read.Source != CorrelationSource.Audits && !stat.RecordUsed)
                {
                    status = CorrelationSourceStatus.Partial;
                    limitations.Add(read.Source == CorrelationSource.Monitoring &&
                                    stat.CoveredWindow
                        ? "As sessões de monitoramento cobriram a janela, mas não produziram evidência aceita " +
                          "(nenhum evento ou amostra nos checkpoints); a cobertura não é completa e a ausência " +
                          "não é ausência de acontecimento."
                        : "Nenhum registro desta fonte cobriu o período solicitado; a fonte está " +
                          "desatualizada para esta janela.");
                }

                sources.Add(new CorrelationSourceSummary(read.Source, status, read.RecordsRead,
                    stat.UsedRecords.Count, stat.References, read.LimitHit, NoteFor(read.Source, read.Status),
                    limitations.Distinct(StringComparer.Ordinal).ToArray()));
            }

            return new(evidence, timeline, boot, sources, input, evidenceFound, merged, undated, limitHit,
                Limitations.Distinct(StringComparer.Ordinal).ToArray());
        }

        private bool Overlaps(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
            fromUtc <= window.ToUtc && toUtc >= window.FromUtc;

        private static string? NoteFor(CorrelationSource source, CorrelationSourceStatus status) => source switch
        {
            CorrelationSource.Audits when status == CorrelationSourceStatus.Completed =>
                "Usada somente como referência de inicialização do Windows; inventário estático não gera evidência temporal.",
            CorrelationSource.Audits => "Referência de inicialização indisponível.",
            _ => null
        };

        private static string OriginReference(CorrelationSource source, Guid recordId, string id) =>
            $"{CorrelationGuidance.SourceName(source)}:{recordId:D}:{id}";
    }

    private static bool IsProcessIdentityKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split(':');
        return parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
            long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && ticks > 0;
    }

    private static EvidenceSeverity SeverityFor(EventLevel level) => level switch
    {
        EventLevel.Critical or EventLevel.Error => EvidenceSeverity.ObservedImpact,
        EventLevel.Warning => EvidenceSeverity.PotentialImpact,
        EventLevel.Information => EvidenceSeverity.Informational,
        _ => EvidenceSeverity.Unknown
    };

    private static EvidenceConfidence ConfidenceFor(EventLogEvidence item, EvidenceGroup? group) =>
        group?.Confidence ?? (item.RecordId is null ? EvidenceConfidence.Medium : EvidenceConfidence.High);

    private static string Range(double[] values, string unit) => values.Length == 0
        ? "indisponível"
        : values.Min().ToString("0.##", CultureInfo.InvariantCulture) + "–" +
          values.Max().ToString("0.##", CultureInfo.InvariantCulture) + " " + unit;

    private static string? Clamp(string? value, int maximum = 600) =>
        value is null || value.Length <= maximum ? value : value[..maximum] + "… (texto limitado na correlação)";

    private static string HardwareCategoryName(HardwareCategory category) => category switch
    {
        HardwareCategory.Cpu => "Processador",
        HardwareCategory.Memory => "Memória",
        HardwareCategory.Board => "Placa-mãe e BIOS",
        HardwareCategory.Gpu => "Placa de vídeo",
        HardwareCategory.Monitor => "Monitores",
        HardwareCategory.Storage => "Armazenamento",
        HardwareCategory.Network => "Rede",
        HardwareCategory.Audio => "Áudio",
        HardwareCategory.Usb => "USB e periféricos",
        HardwareCategory.Driver => "Drivers",
        _ => "Outros dispositivos"
    };

    private static string ChangeName(HardwareChangeType type) => type switch
    {
        HardwareChangeType.Added => "dispositivo adicionado",
        HardwareChangeType.Removed => "dispositivo removido",
        HardwareChangeType.DriverChanged => "driver alterado",
        HardwareChangeType.FirmwareChanged => "firmware alterado",
        HardwareChangeType.CapacityChanged => "capacidade alterada",
        HardwareChangeType.ConfigurationChanged => "configuração alterada",
        _ => "estado alterado"
    };

    private static string ChangeName(GraphicsChangeType type) => type switch
    {
        GraphicsChangeType.AdapterAdded => "adaptador adicionado",
        GraphicsChangeType.AdapterRemoved => "adaptador removido",
        GraphicsChangeType.MonitorAdded => "monitor adicionado",
        GraphicsChangeType.MonitorRemoved => "monitor removido",
        GraphicsChangeType.DriverChanged => "driver alterado",
        GraphicsChangeType.ConfigurationChanged => "configuração alterada",
        _ => "estado alterado"
    };
}
