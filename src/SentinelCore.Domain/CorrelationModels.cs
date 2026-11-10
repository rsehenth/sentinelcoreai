namespace SentinelCore.Domain;

/// <summary>
/// Fontes históricas que podem participar de uma investigação de correlação. Cada valor corresponde a um
/// armazenamento já existente; nenhum coletor novo é executado para correlacionar.
/// </summary>
public enum CorrelationSource { Audits, Hardware, Events, Performance, Network, Graphics, Monitoring, Tweaks }

public enum CorrelationSourceStatus { Completed, Partial, Unavailable, Cancelled, Skipped }

public enum CorrelationExecutionStatus { Running, Completed, Cancelled, Failed }

public enum CorrelationCoverageStatus { Complete, Partial, Unavailable }

public enum CorrelationStatus { Running, Completed, Partial, Failed, Cancelled }

/// <summary>
/// Tipo de evidência normalizada. <c>EventLog</c> e <c>WerReport</c> preservam as identidades da Fase 3.1 para
/// impedir contagem dupla entre o módulo de eventos e o diagnóstico gráfico.
/// </summary>
public enum CorrelationEvidenceKind
{
    EventLog, WerReport, PerformanceObservation, NetworkObservation,
    GraphicsObservation, HardwareChange, GraphicsChange, DeviceInventory, TweakJournalEvent
}

/// <summary>
/// Precisão temporal declarada. <c>Exact</c> vem do instante registrado pela fonte; <c>Coarse</c> é um instante de
/// coleta ou criação que pode diferir do acontecimento; <c>Interval</c> é uma janela observada; <c>Unknown</c> não
/// possui horário confiável e nunca é posicionado na linha do tempo.
/// </summary>
public enum CorrelationTimestampPrecision { Exact, Coarse, Interval, Unknown }

/// <summary>Identidade usada por uma regra de correlação. <c>None</c> significa ausência de identidade confiável.</summary>
public enum CorrelationIdentityKind { None, DeviceStableId, ProcessInstance, WerReport, EventRecord }

public enum CorrelationRelationKind
{
    TemporalAssociation, IdentityAssociation, ObservedSequence, InsufficientEvidence
}

/// <summary>Motivo de uma regra não produzir associação. Distingue ausência de evidência de condição observada.</summary>
public enum CorrelationNoteReason
{
    NotSpecified, Unavailable, NotSelected, InsufficientEvidence, PreconditionNotObserved,
    ConditionNotObserved, Observed
}

[Flags]
public enum CorrelationSources
{
    None = 0,
    Audits = 1,
    Hardware = 2,
    Events = 4,
    Performance = 8,
    Network = 16,
    Graphics = 32,
    /// <summary>Observações de sessões de monitoramento da Fase 5, limitadas e autorizadas.</summary>
    Monitoring = 64,
    // Preserve the default diagnostic set. Maintenance history is explicitly selected.
    All = Audits | Hardware | Events | Performance | Network | Graphics | Monitoring,
    Tweaks = 128,
    Supported = All | Tweaks
}

/// <summary>
/// Limites explícitos e janelas de correlação. Cada janela é específica por tipo de relação: um intervalo único
/// para todos os eventos trataria acontecimentos distintos como equivalentes.
/// </summary>
public sealed record CorrelationLimits
{
    public int MaxRecordsPerSource { get; init; } = 60;
    public int MaxEvidence { get; init; } = 2_000;
    public int MaxTimelineItems { get; init; } = 1_500;
    public int MaxRelations { get; init; } = 120;
    public int MaxPairsPerRule { get; init; } = 25;

    /// <summary>
    /// Evento gráfico × relatório WER. O WER registra o relatório após o evento; 5 minutos cobrem a geração
    /// normal do relatório sem transformar horas distintas em uma única ocorrência.
    /// </summary>
    public TimeSpan GraphicsWerWindow { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>Evento gráfico × falha de aplicativo. Falhas correlatas tendem a ocorrer no mesmo minuto.</summary>
    public TimeSpan GraphicsApplicationWindow { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Observação de desempenho × evento do Windows. A janela da observação é ampliada em 60 s.</summary>
    public TimeSpan PerformanceEventWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Observação de rede × evento de rede. A janela da observação é ampliada em 60 s.</summary>
    public TimeSpan NetworkEventWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Tolerância para associação por identidade: as janelas observadas precisam se sobrepor ou ficar próximas
    /// deste limite. Identidade compartilhada em instantes distantes não é uma associação desta fase.
    /// </summary>
    public TimeSpan IdentityWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Alteração observada entre inventários × eventos posteriores. O instante exato da alteração é desconhecido;
    /// o limite superior evita atribuir eventos muito distantes à mudança.
    /// </summary>
    public TimeSpan ChangeEventWindow { get; init; } = TimeSpan.FromHours(24);

    public static CorrelationLimits Default { get; } = new();

    public void Validate()
    {
        if (MaxRecordsPerSource <= 0 || MaxEvidence <= 0 || MaxTimelineItems <= 0 || MaxRelations <= 0 ||
            MaxPairsPerRule <= 0)
            throw new ArgumentOutOfRangeException(nameof(CorrelationLimits), "Limites de correlação devem ser positivos.");
        if (GraphicsWerWindow <= TimeSpan.Zero || GraphicsApplicationWindow <= TimeSpan.Zero ||
            PerformanceEventWindow < TimeSpan.Zero || NetworkEventWindow < TimeSpan.Zero ||
            IdentityWindow < TimeSpan.Zero || ChangeEventWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(CorrelationLimits), "Janelas de correlação inválidas.");
    }
}

/// <summary>Janela da investigação resolvida em UTC. A interface apresenta no fuso local.</summary>
public sealed record CorrelationRequest(
    EventWindow Window, DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null,
    CorrelationSources Sources = CorrelationSources.All, CorrelationLimits? Limits = null)
{
    public CorrelationLimits EffectiveLimits => Limits ?? CorrelationLimits.Default;
}

/// <summary>
/// Referência de inicialização do Windows, obtida de uma auditoria já persistida. É usada somente para impedir que
/// registros de sessões diferentes do Windows sejam tratados como uma sequência única.
/// </summary>
public sealed record CorrelationBootReference(
    DateTimeOffset? LastBootUtc, Guid? AuditId, DateTimeOffset? AuditCompletedUtc, string Note)
{
    public static CorrelationBootReference Unknown { get; } = new(null, null, null,
        "Horário da última inicialização do Windows indisponível nas auditorias persistidas; sessões distintas " +
        "de inicialização não puderam ser separadas.");
}

/// <summary>Origem de uma evidência normalizada. Todas as representações consolidadas permanecem rastreáveis.</summary>
public sealed record CorrelationEvidenceOrigin(
    CorrelationSource Source, string SourceRecordId, string OriginReference);

/// <summary>
/// Evidência normalizada. <see cref="Id"/> é a identidade lógica da ocorrência: duas representações da mesma
/// ocorrência em módulos diferentes compartilham o mesmo <see cref="Id"/> e são consolidadas em
/// <see cref="Origins"/>. O registro original nunca é substituído.
/// </summary>
public sealed record CorrelationEvidence(
    string Id,
    CorrelationSource Source,
    CorrelationEvidenceKind Kind,
    string SourceRecordId,
    string Title,
    string? Detail,
    string? GroupId,
    DateTimeOffset? OriginalTimestampUtc,
    DateTimeOffset? EffectiveTimestampUtc,
    CorrelationTimestampPrecision Precision,
    DateTimeOffset? WindowFromUtc,
    DateTimeOffset? WindowToUtc,
    string? IdentityKey,
    CorrelationIdentityKind IdentityKind,
    string? Subject,
    EvidenceSeverity Severity,
    EvidenceConfidence Confidence,
    IReadOnlyList<CorrelationEvidenceOrigin> Origins,
    IReadOnlyList<string> Limitations)
{
    public bool HasReliableTimestamp => EffectiveTimestampUtc is not null && Precision != CorrelationTimestampPrecision.Unknown;

    public DateTimeOffset? EndTimestampUtc => WindowToUtc ?? EffectiveTimestampUtc;
}

public sealed record CorrelationTimelineItem(
    string EvidenceId,
    DateTimeOffset TimestampUtc,
    CorrelationTimestampPrecision Precision,
    CorrelationSource Source,
    CorrelationEvidenceKind Kind,
    string Title,
    string? Identity,
    string SourceRecordId,
    EvidenceSeverity Severity,
    EvidenceConfidence Confidence);

public sealed record CorrelationRelation(
    string Id,
    string RuleId,
    string RuleVersion,
    CorrelationRelationKind Kind,
    string Title,
    string Statement,
    string Justification,
    EvidenceSeverity Severity,
    EvidenceConfidence Confidence,
    CausalityStatus Causality,
    IReadOnlyList<string> EvidenceIds,
    DateTimeOffset? WindowFromUtc,
    DateTimeOffset? WindowToUtc,
    double? MaximumGapSeconds,
    IReadOnlyList<string> Limitations)
{
    public bool IsHypothesis => Kind != CorrelationRelationKind.InsufficientEvidence;
}

public sealed record CorrelationAnalysisNote(
    string RuleId, string RuleVersion, bool Evaluated, string Detail,
    CorrelationNoteReason Reason = CorrelationNoteReason.NotSpecified);

public sealed record CorrelationSourceSummary(
    CorrelationSource Source,
    CorrelationSourceStatus Status,
    int RecordsRead,
    int RecordsUsed,
    int EvidenceCount,
    bool LimitHit,
    string? Note,
    IReadOnlyList<string> Limitations);

public sealed record CorrelationCoverage(
    CorrelationCoverageStatus Status,
    int ExpectedSources,
    int CompletedSources,
    int PartialSources,
    int UnavailableSources,
    int CancelledSources,
    int EvidenceFound,
    int DistinctOccurrences,
    int MergedReferences,
    int UndatedEvidence,
    bool LimitHit,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Investigação de correlação. Reutiliza somente registros históricos imutáveis, preserva procedência por evidência
/// e declara sempre causalidade não avaliada.
/// </summary>
public sealed record CorrelationInvestigation(
    int FormatVersion,
    Guid Id,
    string SoftwareVersion,
    string SchemaVersion,
    EventWindow Window,
    DateTimeOffset PeriodFromUtc,
    DateTimeOffset PeriodToUtc,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    TimeSpan Duration,
    CorrelationStatus Status,
    CorrelationSources RequestedSources,
    IReadOnlyList<CorrelationSourceSummary> Sources,
    CorrelationBootReference Boot,
    IReadOnlyList<CorrelationEvidence> Evidence,
    IReadOnlyList<CorrelationTimelineItem> Timeline,
    IReadOnlyList<CorrelationRelation> Relations,
    IReadOnlyList<CorrelationAnalysisNote> AnalysisNotes,
    CorrelationCoverage Coverage,
    IReadOnlyList<string> Limitations)
{
    public CorrelationExecutionStatus Execution => Status switch
    {
        CorrelationStatus.Cancelled => CorrelationExecutionStatus.Cancelled,
        CorrelationStatus.Failed => CorrelationExecutionStatus.Failed,
        CorrelationStatus.Running => CorrelationExecutionStatus.Running,
        _ => CorrelationExecutionStatus.Completed
    };
}

public sealed record CorrelationProgress(int Finished, int Total, string Phase, CorrelationSource? Source);

public sealed record CorrelationStoreSnapshot(
    IReadOnlyList<CorrelationInvestigation> Investigations, IReadOnlyList<string> Warnings);

public interface ICorrelationInvestigationStore
{
    Task<CorrelationStoreSnapshot> LoadCorrelationInvestigationsAsync(CancellationToken cancellationToken = default);
    Task<long> SaveCorrelationInvestigationAsync(CorrelationInvestigation investigation,
        CancellationToken cancellationToken = default);
    string GetCorrelationReportPath(Guid investigationId);
}

/// <summary>
/// Identificadores, versões e textos obrigatórios compartilhados entre regras, validação, relatório e interface.
/// Nenhuma regra desta fase declara causa.
/// </summary>
public static class CorrelationGuidance
{
    public const string RuleVersion = "1.0";

    public const string GraphicsWerRuleId = "CORR-TEMPORAL-GFX-WER";
    public const string GraphicsApplicationRuleId = "CORR-TEMPORAL-GFX-APP";
    public const string PerformanceEventRuleId = "CORR-TEMPORAL-PERF-EVENT";
    public const string NetworkEventRuleId = "CORR-TEMPORAL-NET-EVENT";
    public const string ProcessIdentityRuleId = "CORR-IDENTITY-PROCESS";
    public const string DeviceIdentityRuleId = "CORR-IDENTITY-DEVICE";
    public const string ChangeSequenceRuleId = "CORR-SEQUENCE-CHANGE-EVENT";
    public const string InsufficientEvidenceRuleId = "CORR-INSUFFICIENT";

    public const string InsufficientEvidenceStatement =
        "CORRELAÇÃO NÃO AVALIÁVEL COM AS EVIDÊNCIAS DISPONÍVEIS";

    public const string CausalityLimitation =
        "Correlação não é causalidade: proximidade temporal, identidade compartilhada ou sequência observada não " +
        "demonstram que uma evidência tenha causado outra e não identificam defeito de hardware, driver, " +
        "configuração ou processo. A causalidade permanece não avaliada.";

    public const string ScopeLimitation =
        "Esta correlação reutiliza somente registros históricos já persistidos: nenhuma coleta nova é executada, " +
        "nenhuma configuração do Windows é alterada e nenhuma evidência original é reescrita.";

    public const string NonRenewalLimitation =
        "A correlação não renova a validade de auditorias, inventários, investigações, sessões ou métricas de origem " +
        "e não substitui os registros originais.";

    public const string AbsenceLimitation =
        "Ausência de correlação não prova ausência de relação. Coleta incompleta, limite atingido ou horário não " +
        "confiável impedem a avaliação e permanecem declarados.";

    public const string LimitLimitation =
        "Limite interno de processamento atingido; a investigação é parcial e nada foi omitido em silêncio.";

    public const string WerDelayLimitation =
        "O horário de criação do relatório WER pode ser posterior ao acontecimento; ele é usado apenas quando " +
        "EventTime não existe e rebaixa a precisão temporal para aproximada.";

    public const string DeduplicationLimitation =
        "Representações da mesma ocorrência em módulos diferentes são consolidadas em uma única evidência lógica; " +
        "todas as origens permanecem referenciadas e nenhuma é descartada.";

    public static string SourceName(CorrelationSource source) => source switch
    {
        CorrelationSource.Audits => "Auditorias",
        CorrelationSource.Hardware => "Hardware",
        CorrelationSource.Events => "Eventos e WER",
        CorrelationSource.Performance => "Desempenho",
        CorrelationSource.Network => "Rede",
        CorrelationSource.Graphics => "Gráficos",
        CorrelationSource.Monitoring => "Monitoramento",
        CorrelationSource.Tweaks => "Personalização",
        _ => source.ToString()
    };

    public static string PrecisionName(CorrelationTimestampPrecision precision) => precision switch
    {
        CorrelationTimestampPrecision.Exact => "exata",
        CorrelationTimestampPrecision.Coarse => "aproximada",
        CorrelationTimestampPrecision.Interval => "intervalo observado",
        _ => "sem horário confiável"
    };

    public static string RelationKindName(CorrelationRelationKind kind) => kind switch
    {
        CorrelationRelationKind.TemporalAssociation => "Associação temporal",
        CorrelationRelationKind.IdentityAssociation => "Associação por identidade",
        CorrelationRelationKind.ObservedSequence => "Sequência observada",
        _ => "Informação insuficiente"
    };

    public static string NoteReasonName(CorrelationNoteReason reason) => reason switch
    {
        CorrelationNoteReason.Unavailable => "Indisponível",
        CorrelationNoteReason.NotSelected => "Fonte não selecionada",
        CorrelationNoteReason.InsufficientEvidence => "Evidência insuficiente",
        CorrelationNoteReason.PreconditionNotObserved => "Pré-condição não observada",
        CorrelationNoteReason.ConditionNotObserved => "Condição não observada",
        CorrelationNoteReason.Observed => "Observado",
        _ => "Não especificado"
    };
}
