using System.Globalization;
using System.Text;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

/// <summary>
/// Relatório legível de uma sessão de monitoramento e de seus checkpoints. O texto nunca converte relato do usuário
/// em evento do Windows, nunca declara causa e nunca apresenta limite ou oportunidade perdida como cobertura completa.
/// </summary>
public static class MonitoringReport
{
    private const int EventRows = 200;
    private const int PerformanceRows = 200;
    private const int NetworkRows = 200;

    public static string Render(MonitoringSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var text = new StringBuilder();
        text.AppendLine("# SentinelCore AI — Sessão de monitoramento");
        text.AppendLine();
        text.AppendLine($"- ID: {session.Id:D}");
        text.AppendLine($"- Versão do software: {Safe(session.SoftwareVersion)}");
        text.AppendLine($"- Formato: {session.FormatVersion} (esquema de monitoramento {Safe(session.SchemaVersion)})");
        text.AppendLine($"- Duração autorizada: {MonitoringGuidance.DurationName(session.Preset)} " +
            $"({session.AuthorizedDuration.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s)");
        text.AppendLine($"- Início UTC: {session.StartedUtc:O}");
        text.AppendLine($"- Conclusão UTC: {session.CompletedUtc:O}");
        text.AppendLine($"- Duração observada: {session.Duration.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)} s");
        text.AppendLine($"- Situação: {MonitoringGuidance.StatusName(session.Status)}");
        text.AppendLine($"- Cobertura: {MonitoringGuidance.CoverageName(session.CoverageStatus)}");
        text.AppendLine($"- Sessão recuperada de segmentos: {(session.Recovery.Recovered ? "sim" : "não")}");
        text.AppendLine("- Causalidade: não avaliada");
        text.AppendLine();
        text.AppendLine("> Sessão de observação autorizada, limitada e de baixo impacto. Nenhum serviço, tarefa " +
            "agendada ou processo residente é criado; nenhuma configuração do Windows é alterada. Um incidente " +
            "registrado manualmente é relato do usuário, não evento comprovado do Windows.");
        text.AppendLine();

        text.AppendLine("## Fontes");
        text.AppendLine();
        text.AppendLine("| Fonte | Situação | Cadência | Ciclos esperados | Ciclos coletados | Perdidos | Observações | Limite |");
        text.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (var source in session.Sources)
            text.AppendLine($"| {MonitoringGuidance.SourceName(source.Source)} | " +
                $"{MonitoringGuidance.SourceStatusName(source.Status)} | " +
                $"{source.Interval.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s | " +
                $"{source.ExpectedTicks} | {source.CollectedTicks} | {source.MissedOpportunities} | " +
                $"{source.ObservationCount} | {(source.LimitHit || source.BudgetLimitHit ? "Sim" : "Não")} |");
        if (session.Sources.Count == 0) text.AppendLine("| Indisponível | — | — | — | — | — | — | — |");
        text.AppendLine();
        foreach (var source in session.Sources)
            foreach (var limitation in source.Limitations)
                text.AppendLine($"- Limitação de {MonitoringGuidance.SourceName(source.Source)}: {Safe(limitation)}");
        text.AppendLine();

        text.AppendLine("## Orçamento de recursos");
        text.AppendLine();
        var budget = session.Budget;
        text.AppendLine($"- Cadência: {Safe(budget.CadenceSummary)}");
        text.AppendLine($"- Orçamento por leitura: {budget.MaxCollectorTick.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s");
        text.AppendLine($"- Ciclos: {budget.CollectedTicks} de {budget.ExpectedTicks} esperados; " +
            $"{budget.MissedOpportunities} oportunidade(s) perdida(s)");
        text.AppendLine($"- Tempo de leitura: média {budget.MeanTickMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms · " +
            $"máxima {budget.MaxTickMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms");
        text.AppendLine($"- Amostras de desempenho: {budget.SamplesCollected} · eventos observados: {budget.EventsCollected}");
        text.AppendLine($"- Armazenamento persistido: {budget.PersistedBytes} de {budget.StorageBudgetBytes} bytes");
        text.AppendLine($"- Limite de armazenamento atingido: {(budget.StorageBudgetReached ? "sim" : "não")} · " +
            $"orçamento por leitura excedido: {(budget.TickBudgetExceeded ? "sim" : "não")} · " +
            $"limite de eventos: {(budget.EventLimitReached ? "sim" : "não")}");
        foreach (var limitation in budget.Limitations) text.AppendLine($"- {Safe(limitation)}");
        text.AppendLine();

        text.AppendLine("## Segmentos confirmados");
        text.AppendLine();
        if (session.Segments.Count == 0)
            text.AppendLine("Nenhum segmento confirmado. Ausência de segmento não é ausência de acontecimento.");
        else
        {
            text.AppendLine("| Segmento | Fonte | Ordem | Intervalo UTC | Situação | Observações |");
            text.AppendLine("| --- | --- | ---: | --- | --- | ---: |");
            foreach (var segment in session.Segments)
                text.AppendLine($"| {segment.SegmentId:D} | {MonitoringGuidance.SourceName(segment.Source)} | " +
                    $"{segment.Sequence} | {segment.FromUtc:O} até {segment.ToUtc:O} | " +
                    $"{(segment.Status == MonitoringSegmentStatus.Complete ? "Completo" : "Parcial")} | " +
                    $"{segment.ObservationCount} |");
        }
        text.AppendLine();

        text.AppendLine("## Incidentes registrados manualmente");
        text.AppendLine();
        if (session.Incidents.Count == 0)
            text.AppendLine("Nenhum incidente registrado nesta sessão.");
        foreach (var incident in session.Incidents)
        {
            text.AppendLine($"### {MonitoringGuidance.CategoryName(incident.Category)} — {incident.Id:D}");
            text.AppendLine();
            text.AppendLine($"- Registro manual UTC: {incident.RecordedUtc:O}");
            text.AppendLine($"- Ocorrência informada UTC: " +
                $"{(incident.OccurredUtc is { } occurred ? occurred.ToString("O") : "não informada")}");
            text.AppendLine($"- Precisão: {MonitoringGuidance.PrecisionName(incident.Precision)}");
            text.AppendLine($"- Momento na sessão: {incident.SessionElapsedSeconds.ToString("F1", CultureInfo.InvariantCulture)} s");
            text.AppendLine($"- Descrição: {Safe(incident.Description)}");
            foreach (var evidence in incident.Evidence)
                text.AppendLine($"- Evidência disponível: {MonitoringGuidance.SourceName(evidence.Source)} · " +
                    $"{Safe(evidence.Reference)} · {Safe(evidence.Detail)}");
            foreach (var limitation in incident.Limitations) text.AppendLine($"- Limitação: {Safe(limitation)}");
            text.AppendLine();
        }

        if (session.Recovery.Recovered)
        {
            text.AppendLine("## Recuperação");
            text.AppendLine();
            text.AppendLine(Safe(session.Recovery.Detail));
            text.AppendLine($"- Segmentos recuperados: {session.Recovery.SegmentsRecovered}");
            text.AppendLine($"- Último segmento UTC: " +
                $"{(session.Recovery.LastSegmentUtc is { } last ? last.ToString("O") : "indisponível")}");
            text.AppendLine();
        }

        text.AppendLine("## Limitações da sessão");
        text.AppendLine();
        foreach (var limitation in session.Limitations) text.AppendLine($"- {Safe(limitation)}");
        text.AppendLine();
        text.AppendLine("## Escopo");
        text.AppendLine();
        text.AppendLine("Esta sessão não executa reparos, não altera o Windows, não instala ou atualiza drivers, não " +
            "captura tela, teclado, áudio ou pacotes e não envia dados para fora do computador.");
        return text.ToString();
    }

    public static string RenderSegment(MonitoringSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var text = new StringBuilder();
        text.AppendLine("# SentinelCore AI — Checkpoint de monitoramento");
        text.AppendLine();
        text.AppendLine($"- Segmento: {segment.Id:D}");
        text.AppendLine($"- Sessão: {segment.SessionId:D}");
        text.AppendLine($"- Fonte: {MonitoringGuidance.SourceName(segment.Source)}");
        text.AppendLine($"- Coletor: {Safe(segment.CollectorId)} {Safe(segment.CollectorVersion)}");
        text.AppendLine($"- Ordem: {segment.Sequence}");
        text.AppendLine($"- Intervalo UTC: {segment.FromUtc:O} até {segment.ToUtc:O}");
        text.AppendLine($"- Situação: {(segment.Status == MonitoringSegmentStatus.Complete ? "Completo" : "Parcial")}");
        text.AppendLine($"- Formato: {segment.FormatVersion}");
        text.AppendLine();

        if (segment.Performance.Count > 0)
        {
            text.AppendLine("## Desempenho");
            text.AppendLine();
            text.AppendLine("| Horário UTC | Tempo (s) | CPU total | Memória disponível | Instalada (B) | Disponível (B) |");
            text.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
            foreach (var item in segment.Performance.Take(PerformanceRows))
                text.AppendLine($"| {item.TimestampUtc:O} | " +
                    $"{item.ElapsedSeconds.ToString("F2", CultureInfo.InvariantCulture)} | {Value(item.CpuTotalPercent, "%")} | " +
                    $"{Value(item.MemoryAvailablePercent, "%")} | {Bytes(item.InstalledBytes)} | {Bytes(item.AvailableBytes)} |");
            if (segment.Performance.Count > PerformanceRows)
                text.AppendLine($"| … | — | — | — | — | {segment.Performance.Count - PerformanceRows} adicional(is) |");
            text.AppendLine();
        }

        if (segment.Events.Count > 0)
        {
            text.AppendLine("## Eventos observados");
            text.AppendLine();
            text.AppendLine("| Horário do evento UTC | Canal | Provedor | ID | RecordId | Nível | Classificação |");
            text.AppendLine("| --- | --- | --- | ---: | ---: | --- | --- |");
            foreach (var item in segment.Events.Take(EventRows))
                text.AppendLine($"| {item.OccurredUtc:O} | {Cell(item.Channel)} | {Cell(item.Provider)} | " +
                    $"{item.EventId} | {(item.RecordId is { } id ? id.ToString(CultureInfo.InvariantCulture) : "—")} | " +
                    $"{item.Level} | {Cell(item.GroupId)} |");
            if (segment.Events.Count > EventRows)
                text.AppendLine($"| … | — | — | — | — | — | {segment.Events.Count - EventRows} adicional(is) no payload |");
            text.AppendLine();
        }

        if (segment.Network.Count > 0)
        {
            text.AppendLine("## Rede (contagens agregadas, sem endereços)");
            text.AppendLine();
            text.AppendLine("| Horário UTC | TCP | UDP | Escuta TCP | Estabelecidos | Portas locais | Processos |");
            text.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (var item in segment.Network.Take(NetworkRows))
                text.AppendLine($"| {item.TimestampUtc:O} | {item.TcpConnections} | {item.UdpEndpoints} | " +
                    $"{item.ListeningTcp} | {item.EstablishedTcp} | {item.DistinctLocalPorts} | " +
                    $"{item.ProcessesObserved} |");
            if (segment.Network.Count > NetworkRows)
                text.AppendLine($"| … | — | — | — | — | — | {segment.Network.Count - NetworkRows} adicional(is) |");
            text.AppendLine();
        }

        text.AppendLine("## Limitações");
        text.AppendLine();
        if (segment.Limitations.Count == 0) text.AppendLine("- Nenhuma limitação registrada neste checkpoint.");
        else foreach (var limitation in segment.Limitations) text.AppendLine($"- {Safe(limitation)}");
        text.AppendLine();
        text.AppendLine("> Este checkpoint é imutável e nunca é reescrito. O registro final da sessão apenas o referencia.");
        return text.ToString();
    }

    private static string Value(double? value, string unit) => value is { } number
        ? number.ToString("0.##", CultureInfo.InvariantCulture) + " " + unit
        : "—";

    private static string Bytes(ulong? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "—";

    private static string Cell(string? value) => Safe(value).Replace("|", "\\|");

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "—"
        : value.Replace("\r", " ").Replace("\n", " ").Replace("`", "'")
            .Replace("<", "&lt;").Replace(">", "&gt;");
}
