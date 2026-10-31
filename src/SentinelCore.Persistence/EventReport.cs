using System.Text;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

public static class EventReport
{
    private const int EvidenceSamplePerGroup = 50;

    public static string Render(EventInvestigation investigation)
    {
        ArgumentNullException.ThrowIfNull(investigation);
        var text = new StringBuilder();
        var eventRecords = investigation.Events.Count;
        var distinctReports = investigation.Reports.Select(r => r.ReportId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var rePresentations = investigation.Reports.Count - distinctReports;
        var last = investigation.Groups.Select(g => g.LastObservedUtc).Max();

        text.AppendLine("# SentinelCore AI — Investigação de estabilidade e eventos");
        text.AppendLine();
        text.AppendLine($"- ID: {investigation.Id:D}");
        text.AppendLine($"- Versão do software: {Safe(investigation.SoftwareVersion)}");
        text.AppendLine($"- Formato: {investigation.FormatVersion} (esquema de eventos {Safe(investigation.SchemaVersion)})");
        text.AppendLine($"- Janela: {WindowName(investigation.Window)}");
        text.AppendLine($"- Período UTC: {investigation.PeriodFromUtc:O} até {investigation.PeriodToUtc:O}");
        text.AppendLine($"- Período local: {investigation.PeriodFromUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} até {investigation.PeriodToUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine($"- Início da coleta UTC: {investigation.StartedUtc:O}");
        text.AppendLine($"- Conclusão UTC: {investigation.CompletedUtc:O}");
        text.AppendLine($"- Duração: {investigation.Duration.TotalSeconds:F2} s");
        text.AppendLine($"- Status da investigação: {StatusName(investigation.Status)}");
        text.AppendLine("- Causalidade: não avaliada");
        text.AppendLine();
        text.AppendLine("> Leitura somente. Nenhum evento foi apagado, nenhuma configuração do Windows foi alterada e nenhum relatório foi enviado. " +
            "Evento observado, ocorrência agrupada, evidência, hipótese e causa confirmada são categorias distintas. " +
            "Este relatório não confirma defeito de hardware nem causa raiz.");
        text.AppendLine();
        text.AppendLine("## Resumo");
        text.AppendLine($"- Grupos de evidência: {investigation.Groups.Count}");
        text.AppendLine($"- Registros de evento: {eventRecords}");
        text.AppendLine($"- Relatórios WER distintos: {distinctReports}");
        text.AppendLine($"- Reapresentações de relatório: {rePresentations}");
        text.AppendLine($"- Última ocorrência conhecida: {Format(last)}");
        text.AppendLine();

        text.AppendLine("## Fontes");
        text.AppendLine();
        text.AppendLine("| Coletor | Versão | Status | Eventos lidos | Relatórios lidos | Limite atingido |");
        text.AppendLine("| --- | --- | --- | ---: | ---: | --- |");
        foreach (var source in investigation.Sources)
            text.AppendLine($"| {Safe(source.CollectorId)} | {Safe(source.CollectorVersion)} | " +
                $"{SourceStatusName(source.Status)} | {source.EventsRead} | {source.ReportsRead} | " +
                $"{(source.LimitHit ? "Sim" : "Não")} |");
        if (investigation.Sources.Count == 0) text.AppendLine("| Indisponível | — | — | — | — | — |");
        text.AppendLine();

        text.AppendLine("## Canais consultados");
        text.AppendLine();
        text.AppendLine("| Canal | Existe | Habilitado | Acessível | Detalhe |");
        text.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var channel in investigation.Channels)
            text.AppendLine($"| {Safe(channel.Channel)} | {(channel.Exists ? "Sim" : "Não")} | " +
                $"{(channel.Enabled ? "Sim" : "Não")} | {(channel.Accessible ? "Sim" : "Não")} | " +
                $"{(channel.Detail is null ? "—" : Safe(channel.Detail))} |");

        if (investigation.Channels.Count == 0) text.AppendLine("| Indisponível | — | — | — | — |");
        text.AppendLine();

        text.AppendLine("## Limitações da investigação");
        text.AppendLine();
        if (investigation.Limitations.Count == 0)
            text.AppendLine("- Nenhuma limitação registrada. A ausência de limitações não amplia o escopo da leitura.");
        else
            foreach (var limitation in investigation.Limitations) text.AppendLine($"- {Safe(limitation)}");
        text.AppendLine();

        text.AppendLine("## Grupos de evidência");
        foreach (var group in investigation.Groups)
        {
            text.AppendLine();
            text.AppendLine($"### {Safe(group.Title)}");
            text.AppendLine();
            text.AppendLine($"- Categoria: {CategoryName(group.Category)}");
            text.AppendLine($"- Identificador técnico: {Safe(group.Id)}");
            if (group.Provider is not null) text.AppendLine($"- Provedor: {Safe(group.Provider)}");
            if (group.EventId is { } eventId) text.AppendLine($"- Event ID: {eventId}");
            if (group.ProblemCode is not null) text.AppendLine($"- Código do problema: {Safe(group.ProblemCode)}");
            text.AppendLine($"- Severidade: {SeverityName(group.Severity)}");
            text.AppendLine($"- Confiança: {ConfidenceName(group.Confidence)}");
            text.AppendLine($"- Causalidade: {CausalityName(group.Causality)}");
            text.AppendLine($"- Registros de evento: {group.EventRecordCount}");
            text.AppendLine($"- Relatórios distintos: {group.DistinctReportCount}");
            text.AppendLine($"- Reapresentações: {group.RePresentationCount}");
            text.AppendLine($"- Primeira ocorrência: {Format(group.FirstObservedUtc)}");
            text.AppendLine($"- Última ocorrência: {Format(group.LastObservedUtc)}");
            text.AppendLine($"- Canais: {Join(group.Channels)}");
            text.AppendLine($"- Fontes: {Join(group.Sources)}");
            foreach (var limitation in group.Limitations) text.AppendLine($"- Limitação: {Safe(limitation)}");

            var events = investigation.Events.Where(e => string.Equals(e.GroupId, group.Id, StringComparison.OrdinalIgnoreCase))
                .Take(EvidenceSamplePerGroup).ToArray();
            var reports = investigation.Reports.Where(r => string.Equals(r.GroupId, group.Id, StringComparison.OrdinalIgnoreCase))
                .Take(EvidenceSamplePerGroup).ToArray();
            if (events.Length > 0)
            {
                text.AppendLine();
                text.AppendLine("#### Registros de evento");
                text.AppendLine();
                text.AppendLine("| Identificador | Canal | Provedor | Event ID | Nível | Horário UTC | Mensagem |");
                text.AppendLine("| --- | --- | --- | ---: | --- | --- | --- |");
                foreach (var item in events)
                    text.AppendLine($"| {Safe(item.Id)} | {Safe(item.Channel)} | {Safe(item.Provider)} | " +
                        $"{item.EventId} | {item.Level} | {item.OccurredUtc:O} | {Excerpt(item.Message)} |");
            }
            if (reports.Length > 0)
            {
                text.AppendLine();
                text.AppendLine("#### Relatórios WER");
                text.AppendLine();
                text.AppendLine("| Identificador | Origem | Tipo | Código | Horário UTC | Aplicativo | Módulo |");
                text.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
                foreach (var item in reports)
                    text.AppendLine($"| {Safe(item.ReportId)} | {Safe(item.Source)}/{Safe(item.Store)} | " +
                        $"{Safe(item.EventType)} | {Safe(item.ProblemCode)} | " +
                        $"{Format(item.EventTimeUtc ?? item.CreationTimeUtc)} | {Safe(item.Application)} | {Safe(item.Module)} |");
            }
            var total = investigation.Events.Count(e => string.Equals(e.GroupId, group.Id, StringComparison.OrdinalIgnoreCase)) +
                investigation.Reports.Count(r => string.Equals(r.GroupId, group.Id, StringComparison.OrdinalIgnoreCase));
            if (total > events.Length + reports.Length)
                text.AppendLine($"\nAmostra limitada a {EvidenceSamplePerGroup} registros e {EvidenceSamplePerGroup} relatórios por grupo; " +
                    $"o total permanece em {group.EventRecordCount} registro(s) e {group.DistinctReportCount} relatório(s) distinto(s).");
        }
        return text.ToString();
    }

    private static string WindowName(EventWindow window) => window switch
    {
        EventWindow.Last24Hours => "Últimas 24 horas",
        EventWindow.Last7Days => "Últimos 7 dias",
        EventWindow.Last30Days => "Últimos 30 dias",
        EventWindow.Custom => "Personalizado",
        _ => window.ToString()
    };

    private static string StatusName(EventInvestigationStatus status) => status switch
    {
        EventInvestigationStatus.Running => "Em execução",
        EventInvestigationStatus.Completed => "Concluída",
        EventInvestigationStatus.Partial => "Parcial",
        EventInvestigationStatus.Failed => "Falhou",
        EventInvestigationStatus.Cancelled => "Cancelada",
        EventInvestigationStatus.Unsupported => "Não suportada",
        _ => status.ToString()
    };

    private static string SourceStatusName(EventSourceStatus status) => status switch
    {
        EventSourceStatus.Running => "Em execução",
        EventSourceStatus.Completed => "Concluída",
        EventSourceStatus.Partial => "Parcial",
        EventSourceStatus.Failed => "Falhou",
        EventSourceStatus.Unsupported => "Não suportada",
        EventSourceStatus.Cancelled => "Cancelada",
        _ => status.ToString()
    };

    private static string CategoryName(EventCategory category) => category switch
    {
        EventCategory.Graphics => "Gráficos",
        EventCategory.Power => "Energia",
        EventCategory.Storage => "Armazenamento",
        EventCategory.Network => "Rede",
        EventCategory.Applications => "Aplicativos",
        EventCategory.Services => "Serviços",
        _ => "Outros"
    };

    private static string SeverityName(EvidenceSeverity severity) => severity switch
    {
        EvidenceSeverity.Unknown => "Desconhecida",
        EvidenceSeverity.Informational => "Informativa",
        EvidenceSeverity.PotentialImpact => "Impacto potencial observado",
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

    private static string CausalityName(CausalityStatus causality) => causality switch
    {
        CausalityStatus.NotAssessed => "Não avaliada",
        _ => causality.ToString()
    };

    private static string Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? "Indisponível" : Safe(string.Join(", ", values));

    private static string Format(DateTimeOffset? value) =>
        value is { } at ? at.ToString("O") : "Indisponível";

    private static string Excerpt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Indisponível";
        var safe = Safe(value);
        return safe.Length <= 200 ? safe : safe[..200] + "…";
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "Indisponível"
        : value.Replace("\r", " ").Replace("\n", " ").Replace("`", "'").Replace("<", "&lt;").Replace(">", "&gt;");
}
