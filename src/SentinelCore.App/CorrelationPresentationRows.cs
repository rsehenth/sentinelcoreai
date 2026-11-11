using System.Globalization;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.App.Localization;

namespace SentinelCore.App;

// Pure projections: no window, collector, service composition or historical writes.
internal static class CorrelationPresentationRows
{
    public static string CorrelationStatusName(CorrelationStatus status) => Loc.T(status switch
    {
        CorrelationStatus.Running => "Em andamento",
        CorrelationStatus.Completed => "Concluída",
        CorrelationStatus.Partial => "Concluída com cobertura parcial",
        CorrelationStatus.Failed => "Falhou",
        CorrelationStatus.Cancelled => "Cancelada",
        _ => status.ToString()
    });

    public static string CorrelationCoverageName(CorrelationCoverageStatus status) => Loc.T(status switch
    {
        CorrelationCoverageStatus.Complete => "completa",
        CorrelationCoverageStatus.Partial => "parcial",
        CorrelationCoverageStatus.Unavailable => "indisponível",
        _ => status.ToString()
    });

    public static string CorrelationSourceStatusName(CorrelationSourceStatus status) => Loc.T(status switch
    {
        CorrelationSourceStatus.Completed => "concluída",
        CorrelationSourceStatus.Partial => "parcial",
        CorrelationSourceStatus.Unavailable => "indisponível",
        CorrelationSourceStatus.Cancelled => "cancelada",
        _ => "não selecionada"
    });

    public static string CorrelationEvidenceKindName(CorrelationEvidenceKind kind) => Loc.T(kind switch
    {
        CorrelationEvidenceKind.EventLog => "evento do Windows",
        CorrelationEvidenceKind.WerReport => "relatório WER",
        CorrelationEvidenceKind.PerformanceObservation => "observação de desempenho",
        CorrelationEvidenceKind.NetworkObservation => "observação de rede",
        CorrelationEvidenceKind.GraphicsObservation => "observação gráfica",
        CorrelationEvidenceKind.HardwareChange => "alteração de hardware",
        CorrelationEvidenceKind.GraphicsChange => "alteração gráfica",
        CorrelationEvidenceKind.TweakJournalEvent => "evento de personalização",
        _ => "inventário de dispositivo"
    });

    public sealed record CorrelationPresetRow(EventWindow Window, string PortugueseLabel)
    {
        public string Label => Loc.T(PortugueseLabel);
        public override string ToString() => Label;
    }

    public sealed record CorrelationOriginRow(CorrelationEvidenceOrigin Origin)
    {
        public string Label => Loc.F($"{Loc.T(CorrelationGuidance.SourceName(Origin.Source))} · {Origin.SourceRecordId}");
        public override string ToString() => Label;
    }

    public sealed record CorrelationHistoryRow(CorrelationInvestigation Investigation)
    {
        public string DateText => Investigation.CompletedUtc.ToLocalTime()
            .ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture);
        public string SummaryText =>
            Loc.F($"{CorrelationStatusName(Investigation.Status)} · cobertura ") +
            Loc.F($"{CorrelationCoverageName(Investigation.Coverage.Status)} · ") +
            Loc.F($"{Investigation.Coverage.DistinctOccurrences} evidência(s) · ") +
            Loc.F($"{Investigation.Relations.Count(relation => relation.IsHypothesis)} relação(ões)");
    }

    public sealed record CorrelationTimelineRow(CorrelationTimelineItem Item)
    {
        public string Time => Item.TimestampUtc.ToLocalTime()
            .ToString("dd/MM HH:mm:ss", CultureInfo.CurrentCulture);
        public string Precision => Loc.T(CorrelationGuidance.PrecisionName(Item.Precision));
        public string Source => Loc.T(CorrelationGuidance.SourceName(Item.Source));
        public string Kind => CorrelationEvidenceKindName(Item.Kind);
        public string Title => Item.Title;
    }

    public sealed record CorrelationRelationRow(CorrelationRelation Relation)
    {
        public string Title => Relation.Title;
        public string Subtitle =>
            Loc.F($"{Loc.T(CorrelationGuidance.RelationKindName(Relation.Kind))} · ") +
            Loc.F($"{Loc.T(EventCatalog.SeverityName(Relation.Severity))} · confiança ") +
            Loc.F($"{Loc.T(EventCatalog.ConfidenceName(Relation.Confidence))} · causalidade não avaliada");
    }

    public sealed record CorrelationEvidenceRow(CorrelationEvidence Evidence)
    {
        public string Title => Evidence.Title;
        public string Subtitle =>
            Loc.F($"{Loc.T(CorrelationGuidance.SourceName(Evidence.Source))} · {CorrelationEvidenceKindName(Evidence.Kind)} · ") +
            Loc.F($"{Loc.T(CorrelationGuidance.PrecisionName(Evidence.Precision))} · {Evidence.Origins.Count} origem(ns)");
    }

    public sealed record CorrelationSourceRow(CorrelationSourceSummary Source)
    {
        public string SourceName => Loc.T(CorrelationGuidance.SourceName(Source.Source));
        public string Status => CorrelationSourceStatusName(Source.Status);
        public string Records => Loc.F($"{Source.RecordsRead} lido(s) · {Source.RecordsUsed} usado(s)");
        public string Limitations => Source.Limitations.Count == 0
            ? "—"
            : string.Join(" ", Source.Limitations);
    }
}
