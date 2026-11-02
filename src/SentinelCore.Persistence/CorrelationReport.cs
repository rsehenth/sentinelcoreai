using System.Globalization;
using System.Text;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

/// <summary>
/// Relatório Markdown de uma investigação de correlação. Todo texto deriva das evidências normalizadas; nenhuma
/// relação é apresentada como causa e nenhuma evidência ausente é reconstruída.
/// </summary>
public static class CorrelationReport
{
    private const int TimelineRows = 300;
    private const int EvidenceRows = 300;
    private const int RelationRows = 60;

    public static string Render(CorrelationInvestigation investigation)
    {
        ArgumentNullException.ThrowIfNull(investigation);
        var text = new StringBuilder();
        var coverage = investigation.Coverage;
        var requested = Enum.GetValues<CorrelationSource>()
            .Where(source => investigation.RequestedSources.HasFlag(Flag(source))).ToArray();

        text.AppendLine("# SentinelCore AI — Correlação de evidências");
        text.AppendLine();
        text.AppendLine($"- ID: {investigation.Id:D}");
        text.AppendLine($"- Versão do software: {Safe(investigation.SoftwareVersion)}");
        text.AppendLine($"- Formato: {investigation.FormatVersion} (esquema de correlação {Safe(investigation.SchemaVersion)})");
        text.AppendLine($"- Janela: {WindowName(investigation.Window)}");
        text.AppendLine($"- Período UTC: {investigation.PeriodFromUtc:O} até {investigation.PeriodToUtc:O}");
        text.AppendLine($"- Período local: {investigation.PeriodFromUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} até " +
            $"{investigation.PeriodToUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine($"- Início UTC: {investigation.StartedUtc:O}");
        text.AppendLine($"- Conclusão UTC: {investigation.CompletedUtc:O}");
        text.AppendLine($"- Duração: {investigation.Duration.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)} s");
        text.AppendLine($"- Situação: {StatusName(investigation.Status)}");
        text.AppendLine($"- Execução: {ExecutionName(investigation.Execution)}");
        text.AppendLine($"- Cobertura: {CoverageName(coverage.Status)}");
        text.AppendLine($"- Fontes solicitadas: {string.Join(", ", requested.Select(CorrelationGuidance.SourceName))}");
        text.AppendLine($"- Fontes concluídas: {coverage.CompletedSources}/{coverage.ExpectedSources}");
        text.AppendLine($"- Inicialização do Windows: " +
            (investigation.Boot.LastBootUtc is { } boot ? boot.ToString("O") : "indisponível"));
        text.AppendLine("- Causalidade: não avaliada");
        text.AppendLine();
        text.AppendLine("> Investigação somente de leitura sobre registros históricos já persistidos. Nenhuma coleta " +
            "nova foi executada, nenhuma configuração do Windows foi alterada e nenhuma evidência original foi " +
            "reescrita. Correlação não é causalidade: proximidade temporal, identidade compartilhada ou sequência " +
            "observada não demonstram causa, defeito ou probabilidade.");
        text.AppendLine();

        text.AppendLine("## Resumo");
        text.AppendLine($"- Fontes consultadas: {investigation.Sources.Count(source => source.Status != CorrelationSourceStatus.Skipped)}");
        text.AppendLine($"- Referências de evidência encontradas: {coverage.EvidenceFound}");
        text.AppendLine($"- Ocorrências lógicas distintas: {coverage.DistinctOccurrences}");
        text.AppendLine($"- Representações consolidadas por deduplicação: {coverage.MergedReferences}");
        text.AppendLine($"- Evidências sem horário confiável: {coverage.UndatedEvidence}");
        text.AppendLine($"- Itens na linha do tempo: {investigation.Timeline.Count}");
        text.AppendLine($"- Relações identificadas: {investigation.Relations.Count(relation => relation.IsHypothesis)}");
        text.AppendLine($"- Limite interno atingido: {(coverage.LimitHit ? "sim" : "não")}");
        text.AppendLine();

        text.AppendLine("## Fontes");
        text.AppendLine();
        text.AppendLine("| Fonte | Situação | Registros lidos | Registros usados | Referências de evidência | Limite | Observação |");
        text.AppendLine("| --- | --- | ---: | ---: | ---: | --- | --- |");
        foreach (var source in investigation.Sources)
            text.AppendLine($"| {CorrelationGuidance.SourceName(source.Source)} | {SourceStatusName(source.Status)} | " +
                $"{source.RecordsRead} | {source.RecordsUsed} | {source.EvidenceCount} | " +
                $"{(source.LimitHit ? "Sim" : "Não")} | {Cell(source.Note)} |");
        if (investigation.Sources.Count == 0) text.AppendLine("| Indisponível | — | — | — | — | — | — |");
        text.AppendLine();
        foreach (var source in investigation.Sources)
            foreach (var limitation in source.Limitations)
                text.AppendLine($"- Limitação de {CorrelationGuidance.SourceName(source.Source)}: {Safe(limitation)}");
        if (investigation.Sources.Any(source => source.Limitations.Count > 0)) text.AppendLine();

        text.AppendLine("## Inicialização do Windows");
        text.AppendLine();
        text.AppendLine(Safe(investigation.Boot.Note));
        text.AppendLine();

        text.AppendLine("## Linha do tempo");
        text.AppendLine();
        if (investigation.Timeline.Count == 0)
        {
            text.AppendLine("Nenhuma evidência com horário confiável na janela. Registros sem horário confiável não " +
                "foram posicionados artificialmente e aparecem na seção seguinte.");
        }
        else
        {
            text.AppendLine("| Horário UTC | Precisão | Fonte | Tipo | Evidência | Identidade | Registro de origem |");
            text.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
            foreach (var item in investigation.Timeline.Take(TimelineRows))
                text.AppendLine($"| {item.TimestampUtc:O} | {CorrelationGuidance.PrecisionName(item.Precision)} | " +
                    $"{CorrelationGuidance.SourceName(item.Source)} | {EvidenceKindName(item.Kind)} | " +
                    $"{Cell(item.Title)} | {Cell(item.Identity)} | {Cell(item.SourceRecordId)} |");
            if (investigation.Timeline.Count > TimelineRows)
                text.AppendLine($"| … | — | — | — | {investigation.Timeline.Count - TimelineRows} item(ns) adicional(is) " +
                    "no payload estruturado | — | — |");
        }
        text.AppendLine();
        text.AppendLine("A linha do tempo apresenta apenas o instante ou o início da janela observada; evidências com " +
            "precisão aproximada ou intervalada declaram essa limitação na coluna Precisão. Nenhum horário ausente " +
            "foi inventado para permitir correlação.");
        text.AppendLine();

        var undated = investigation.Evidence.Where(item => !item.HasReliableTimestamp).ToArray();
        text.AppendLine("## Evidências sem horário confiável");
        text.AppendLine();
        if (undated.Length == 0)
            text.AppendLine("Nenhuma evidência sem horário confiável nesta investigação.");
        else
        {
            text.AppendLine("| Fonte | Tipo | Evidência | Registro de origem | Limitações |");
            text.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var item in undated.Take(EvidenceRows))
                text.AppendLine($"| {CorrelationGuidance.SourceName(item.Source)} | {EvidenceKindName(item.Kind)} | " +
                    $"{Cell(item.Title)} | {Cell(item.SourceRecordId)} | {Cell(string.Join(" ", item.Limitations))} |");
            if (undated.Length > EvidenceRows)
                text.AppendLine($"| … | — | {undated.Length - EvidenceRows} evidência(s) adicional(is) | — | — |");
        }
        text.AppendLine();

        text.AppendLine("## Relações identificadas");
        text.AppendLine();
        var relations = investigation.Relations.Where(relation => relation.IsHypothesis).ToArray();
        if (relations.Length == 0)
        {
            text.AppendLine($"{CorrelationGuidance.InsufficientEvidenceStatement}. Nenhuma associação determinística " +
                "foi identificada com as evidências disponíveis; isso não prova ausência de relação.");
        }
        foreach (var relation in relations.Take(RelationRows))
        {
            text.AppendLine();
            text.AppendLine($"### {Safe(relation.Title)}");
            text.AppendLine();
            text.AppendLine($"- Regra: {Safe(relation.RuleId)} {Safe(relation.RuleVersion)}");
            text.AppendLine($"- Tipo: {CorrelationGuidance.RelationKindName(relation.Kind)}");
            text.AppendLine($"- Severidade: {SeverityName(relation.Severity)}");
            text.AppendLine($"- Confiança: {ConfidenceName(relation.Confidence)}");
            text.AppendLine("- Causalidade: não avaliada");
            text.AppendLine($"- Janela: {relation.WindowFromUtc?.ToString("O") ?? "—"} até " +
                $"{relation.WindowToUtc?.ToString("O") ?? "—"}");
            if (relation.MaximumGapSeconds is { } gap)
                text.AppendLine($"- Diferença temporal máxima: {gap.ToString("F1", CultureInfo.InvariantCulture)} s");
            text.AppendLine($"- Evidências: {string.Join(", ", relation.EvidenceIds.Select(Safe))}");
            text.AppendLine($"- Descrição: {Safe(relation.Statement)}");
            text.AppendLine($"- Justificativa: {Safe(relation.Justification)}");
            foreach (var limitation in relation.Limitations)
                text.AppendLine($"- Limitação: {Safe(limitation)}");
        }
        if (relations.Length > RelationRows)
            text.AppendLine($"\n{relations.Length - RelationRows} relação(ões) adicional(is) no payload estruturado.");
        text.AppendLine();

        var insufficient = investigation.Relations
            .Where(relation => relation.Kind == CorrelationRelationKind.InsufficientEvidence).ToArray();
        if (insufficient.Length > 0)
        {
            text.AppendLine("## Resultado explícito de insuficiência");
            text.AppendLine();
            foreach (var relation in insufficient)
            {
                text.AppendLine($"- {Safe(relation.Statement)}");
                text.AppendLine($"  Justificativa: {Safe(relation.Justification)}");
            }
            text.AppendLine();
        }

        text.AppendLine("## Evidências consolidadas");
        text.AppendLine();
        text.AppendLine("| Identificador lógico | Fonte principal | Tipo | Evidência | Horário UTC | Precisão | Identidade | Origens |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | ---: |");
        foreach (var item in investigation.Evidence.Take(EvidenceRows))
            text.AppendLine($"| {Safe(item.Id)} | {CorrelationGuidance.SourceName(item.Source)} | " +
                $"{EvidenceKindName(item.Kind)} | {Cell(item.Title)} | " +
                $"{(item.EffectiveTimestampUtc is { } at ? at.ToString("O") : "—")} | " +
                $"{CorrelationGuidance.PrecisionName(item.Precision)} | {Cell(item.IdentityKey)} | {item.Origins.Count} |");
        if (investigation.Evidence.Count > EvidenceRows)
            text.AppendLine($"| … | — | — | — | — | — | — | {investigation.Evidence.Count - EvidenceRows} adicional(is) |");
        text.AppendLine();
        text.AppendLine("Cada identificador lógico pode reunir mais de uma origem quando o mesmo acontecimento apareceu " +
            "em módulos diferentes. Todas as origens permanecem referenciadas e nenhuma representação é descartada.");
        text.AppendLine();

        var duplicated = investigation.Evidence.Where(item => item.Origins.Count > 1).Take(20).ToArray();
        if (duplicated.Length > 0)
        {
            text.AppendLine("## Deduplicação entre módulos");
            text.AppendLine();
            foreach (var item in duplicated)
            {
                text.AppendLine($"- {Safe(item.Id)} — {Safe(item.Title)}");
                foreach (var origin in item.Origins)
                    text.AppendLine($"  - Origem: {CorrelationGuidance.SourceName(origin.Source)} · registro " +
                        $"{Safe(origin.SourceRecordId)} · referência {Safe(origin.OriginReference)}");
            }
            if (investigation.Evidence.Count(item => item.Origins.Count > 1) > 20)
                text.AppendLine("- Demais ocorrências consolidadas estão no payload estruturado.");
            text.AppendLine();
        }

        text.AppendLine("## Notas de análise");
        text.AppendLine();
        if (investigation.AnalysisNotes.Count == 0) text.AppendLine("- Nenhuma nota registrada.");
        else
        {
            text.AppendLine("| Regra | Avaliada | Motivo | Detalhe |");
            text.AppendLine("| --- | --- | --- | --- |");
            foreach (var note in investigation.AnalysisNotes)
                text.AppendLine($"| {Cell(note.RuleId)} {Cell(note.RuleVersion)} | {(note.Evaluated ? "Sim" : "Não")} | " +
                    $"{CorrelationGuidance.NoteReasonName(note.Reason)} | {Cell(note.Detail)} |");
        }
        text.AppendLine();

        text.AppendLine("## Limitações da investigação");
        text.AppendLine();
        if (investigation.Limitations.Count == 0)
            text.AppendLine("- Nenhuma limitação registrada. A ausência de limitações não amplia a cobertura.");
        else
            foreach (var limitation in investigation.Limitations) text.AppendLine($"- {Safe(limitation)}");
        text.AppendLine();

        text.AppendLine("## Escopo");
        text.AppendLine();
        text.AppendLine("Esta investigação não executa reparos, não altera o Windows, não instala ou atualiza drivers e " +
            "não sugere comandos executáveis.");
        text.AppendLine("As evidências originais permanecem inalteradas nos registros de origem; este documento é uma " +
            "leitura derivada e não substitui auditorias, inventários, investigações, sessões ou métricas.");
        return text.ToString();
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
        _ => CorrelationSources.None
    };

    private static string WindowName(EventWindow window) => window switch
    {
        EventWindow.Last24Hours => "24 horas",
        EventWindow.Last7Days => "7 dias",
        EventWindow.Last30Days => "30 dias",
        _ => "Personalizada"
    };

    private static string StatusName(CorrelationStatus status) => status switch
    {
        CorrelationStatus.Running => "Em execução",
        CorrelationStatus.Completed => "Concluída",
        CorrelationStatus.Partial => "Concluída com cobertura parcial",
        CorrelationStatus.Failed => "Falhou",
        CorrelationStatus.Cancelled => "Cancelada",
        _ => status.ToString()
    };

    private static string ExecutionName(CorrelationExecutionStatus status) => status switch
    {
        CorrelationExecutionStatus.Running => "Em execução",
        CorrelationExecutionStatus.Completed => "Concluída",
        CorrelationExecutionStatus.Cancelled => "Cancelada",
        CorrelationExecutionStatus.Failed => "Falhou",
        _ => status.ToString()
    };

    private static string CoverageName(CorrelationCoverageStatus status) => status switch
    {
        CorrelationCoverageStatus.Complete => "Completa",
        CorrelationCoverageStatus.Partial => "Parcial",
        CorrelationCoverageStatus.Unavailable => "Indisponível",
        _ => status.ToString()
    };

    private static string SourceStatusName(CorrelationSourceStatus status) => status switch
    {
        CorrelationSourceStatus.Completed => "Concluída",
        CorrelationSourceStatus.Partial => "Parcial",
        CorrelationSourceStatus.Unavailable => "Indisponível",
        CorrelationSourceStatus.Cancelled => "Cancelada",
        _ => "Não selecionada"
    };

    private static string EvidenceKindName(CorrelationEvidenceKind kind) => kind switch
    {
        CorrelationEvidenceKind.EventLog => "Evento do Windows",
        CorrelationEvidenceKind.WerReport => "Relatório WER",
        CorrelationEvidenceKind.PerformanceObservation => "Observação de desempenho",
        CorrelationEvidenceKind.NetworkObservation => "Observação de rede",
        CorrelationEvidenceKind.GraphicsObservation => "Observação gráfica",
        CorrelationEvidenceKind.HardwareChange => "Alteração de hardware",
        CorrelationEvidenceKind.GraphicsChange => "Alteração gráfica",
        _ => "Inventário de dispositivo"
    };

    private static string SeverityName(EvidenceSeverity severity) => severity switch
    {
        EvidenceSeverity.Unknown => "Impacto desconhecido",
        EvidenceSeverity.Informational => "Informativo",
        EvidenceSeverity.PotentialImpact => "Impacto potencial",
        EvidenceSeverity.ObservedImpact => "Impacto observado",
        _ => severity.ToString()
    };

    private static string ConfidenceName(EvidenceConfidence confidence) => confidence switch
    {
        EvidenceConfidence.Low => "Baixa",
        EvidenceConfidence.Medium => "Média",
        EvidenceConfidence.High => "Alta",
        _ => confidence.ToString()
    };

    private static string Cell(string? value) => Safe(value).Replace("|", "\\|");

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "—"
        : value.Replace("\r", " ").Replace("\n", " ").Replace("`", "'")
            .Replace("<", "&lt;").Replace(">", "&gt;");
}
