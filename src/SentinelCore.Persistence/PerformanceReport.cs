using System.Globalization;
using System.Text;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

public static class PerformanceReport
{
    private const int TopProcessesByCpu = 10;
    private const int TopProcessesByGrowth = 5;

    public static string Render(PerformanceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var text = new StringBuilder();
        var statistics = session.Statistics;
        var coverage = session.Coverage ?? PerformanceCoverage.FromLegacy(session);
        var quality = PerformanceQualityCatalog.Find(session.Id);

        text.AppendLine("# SentinelCore AI — Investigação de desempenho");
        text.AppendLine();
        text.AppendLine($"- ID: {session.Id:D}");
        text.AppendLine($"- Versão do software: {Safe(session.SoftwareVersion)}");
        text.AppendLine($"- Formato: {session.FormatVersion} (esquema de desempenho {Safe(session.SchemaVersion)})");
        text.AppendLine($"- Preset: {PresetName(session.Preset)}");
        text.AppendLine($"- Duração solicitada: {Seconds(session.RequestedDuration)}");
        text.AppendLine($"- Duração medida: {Seconds(session.Duration)}");
        text.AppendLine($"- Intervalo de amostragem: {Seconds(session.SampleInterval)}");
        text.AppendLine($"- Amostras: {session.Samples.Count}");
        text.AppendLine($"- Início da coleta UTC: {session.StartedUtc:O}");
        text.AppendLine($"- Conclusão UTC: {session.CompletedUtc:O}");
        text.AppendLine($"- Execução: {ExecutionName(coverage.Execution)}");
        text.AppendLine($"- Cobertura: {CoverageName(coverage.Status)}");
        text.AppendLine($"- Amostras esperadas/coletadas: {coverage.ExpectedSamples}/{coverage.CollectedSamples}");
        text.AppendLine($"- Oportunidades de amostragem perdidas: {coverage.MissedOpportunities}");
        text.AppendLine($"- Limites de cobertura: processos {(coverage.ProcessLimitReached ? "sim" : "não")}; " +
            $"armazenamento {(coverage.StorageLimitReached ? "sim" : "não")}; " +
            $"amostras {(coverage.SampleLimitReached ? "sim" : "não")}");
        text.AppendLine($"- Fontes indisponíveis: {(coverage.UnavailableCollectors.Count == 0 ? "nenhuma" :
            string.Join(", ", coverage.UnavailableCollectors.Select(Safe)))}");
        text.AppendLine("- Causalidade: não avaliada");
        text.AppendLine();

        if (quality is not null)
        {
            text.AppendLine("## Qualidade histórica");
            text.AppendLine();
            text.AppendLine($"- Defeito conhecido: {Safe(quality.DefectId)}");
            text.AppendLine($"- Motivo: {Safe(quality.Reason)}");
            text.AppendLine($"- Versão afetada: {Safe(quality.AffectedVersion)}; corrigido em {Safe(quality.FixedVersion)}");
            text.AppendLine($"- Métricas afetadas: {string.Join(", ", quality.AffectedMetrics.Select(Safe))}");
            text.AppendLine(quality.ComparisonBlocked
                ? "- Comparação quantitativa: BLOQUEADA. Os valores abaixo preservam o registro original e não devem " +
                  "ser comparados com sessões posteriores à correção."
                : "- Comparação quantitativa: permitida.");
            text.AppendLine("- O registro Markdown original não foi alterado; esta anotação é derivada em leitura.");
            text.AppendLine();
        }
        text.AppendLine("> Leitura somente. Nenhuma configuração do Windows foi alterada, nenhum processo foi encerrado e nenhuma prioridade, afinidade ou energia foi ajustada. " +
            "Observação de evidência não é diagnóstico, não confirma causa raiz e não atesta integridade ou estabilidade do sistema.");
        text.AppendLine();

        text.AppendLine("## Fontes");
        text.AppendLine();
        text.AppendLine("| Coletor | Versão | Categoria | Origem | Status | Amostras | Limite atingido | Leitura média | Leitura máxima |");
        text.AppendLine("| --- | --- | --- | --- | --- | ---: | --- | ---: | ---: |");
        foreach (var source in session.Sources)
            text.AppendLine($"| {Cell(source.CollectorId)} | {Cell(source.CollectorVersion)} | " +
                $"{CategoryName(source.Category)} | {Cell(source.Source)} | {SourceStatusName(source.Status)} | " +
                $"{source.SamplesRead} | {(source.LimitHit ? "Sim" : "Não")} | " +
                $"{Milliseconds(source.MeanReadMilliseconds)} | {Milliseconds(source.MaxReadMilliseconds)} |");
        if (session.Sources.Count == 0) text.AppendLine("| Indisponível | — | — | — | — | — | — | — | — |");
        text.AppendLine();
        foreach (var source in session.Sources)
            foreach (var limitation in source.Limitations)
                text.AppendLine($"- Limitação de {Safe(source.CollectorId)}: {Safe(limitation)}");
        if (session.Sources.Any(source => source.Limitations.Count > 0)) text.AppendLine();

        text.AppendLine("## Indicadores");
        text.AppendLine();
        text.AppendLine($"- CPU total — média: {Percent(statistics.CpuTotalPercent.Mean)}; " +
            $"P95: {Percent(statistics.CpuTotalPercent.P95)}; máxima: {Percent(statistics.CpuTotalPercent.Max)}");
        text.AppendLine($"- CPU usuário — média: {Percent(statistics.CpuUserPercent.Mean)}; " +
            $"CPU kernel — média: {Percent(statistics.CpuKernelPercent.Mean)}");
        var minimumAvailable = session.Samples
            .Where(sample => sample.Memory is { Valid: true })
            .Select(sample => (ulong?)sample.Memory!.AvailableBytes)
            .Min();
        text.AppendLine($"- Memória disponível mínima: {Bytes(minimumAvailable)} " +
            $"({Percent(statistics.MemoryAvailablePercent.Min)} da memória instalada)");
        text.AppendLine($"- Memória usada máxima: {Bytes(statistics.MemoryUsedBytes.Max)}");
        text.AppendLine($"- Percentual de commit máximo: {Percent(statistics.CommitUsagePercent.Max)}");
        text.AppendLine($"- Fila do processador — média: {Number(statistics.ProcessorQueueLength.Mean, 1)}; " +
            $"máxima: {Number(statistics.ProcessorQueueLength.Max, 1)}");
        text.AppendLine($"- Páginas por segundo — média: {Number(statistics.PagesPerSecond.Mean, 1)}");
        text.AppendLine($"- Processos acompanhados: {statistics.Processes.Count}");
        text.AppendLine($"- Encerramentos ou reatribuições de PID observados: " +
            $"{session.Samples.Sum(sample => sample.Exits.Count)}");
        text.AppendLine($"- Instâncias de armazenamento acompanhadas: {statistics.Storage.Count}");
        text.AppendLine($"- Amostras válidas de CPU: {statistics.ValidCpuSamples}/{statistics.TotalSamples}; " +
            $"de memória: {statistics.ValidMemorySamples}/{statistics.TotalSamples}");
        text.AppendLine();

        text.AppendLine("## Processos");
        text.AppendLine();
        text.AppendLine($"Seleção limitada aos {TopProcessesByCpu} maiores por CPU média e aos {TopProcessesByGrowth} maiores crescimentos de memória privada. " +
            "Valores indisponíveis não são estimados.");
        text.AppendLine();
        text.AppendLine("| Identidade | Nome | PID | CPU média | CPU máxima | Privado final | Crescimento do privado |");
        text.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: |");
        var selected = TopProcesses(statistics.Processes);
        foreach (var process in selected)
            text.AppendLine($"| {Cell(process.Identity)} | {Cell(process.Name)} | {process.Pid} | " +
                $"{Percent(process.CpuPercent.Mean)} | {Percent(process.CpuPercent.Max)} | " +
                $"{Bytes(process.PrivateBytes.Last)} | {Percent(process.PrivateBytes.GrowthPercent)} |");
        if (selected.Count == 0) text.AppendLine("| Indisponível | — | — | — | — | — | — |");
        text.AppendLine();

        var physical = statistics.Storage.Where(item => item.Scope == StorageScope.Physical).ToArray();
        var volumes = statistics.Storage.Where(item => item.Scope == StorageScope.Volume).ToArray();
        text.AppendLine("## Armazenamento físico");
        text.AppendLine();
        StorageTable(text, physical);
        text.AppendLine();
        text.AppendLine("## Volumes");
        text.AppendLine();
        StorageTable(text, volumes);
        text.AppendLine();

        text.AppendLine("## Observações de evidência");
        text.AppendLine();
        if (session.Observations.Count == 0)
            text.AppendLine("Nenhuma observação de evidência nesta sessão.");
        foreach (var observation in session.Observations)
        {
            text.AppendLine();
            text.AppendLine($"### {SeverityName(observation.Severity)} — {Safe(observation.Title)}");
            text.AppendLine();
            text.AppendLine($"- Regra: {Safe(observation.RuleId)} {Safe(observation.RuleVersion)}");
            text.AppendLine($"- Tipo: {KindName(observation.Kind)}");
            text.AppendLine($"- Severidade: {SeverityName(observation.Severity)}");
            text.AppendLine($"- Confiança: {ConfidenceName(observation.Confidence)}");
            text.AppendLine("- Causalidade: não avaliada");
            text.AppendLine($"- Métrica: {Safe(observation.Metric)} ({Safe(observation.Unit)})");
            if (observation.Threshold is { } threshold)
                text.AppendLine($"- Limiar: {FormatValue(observation.Unit, threshold)}");
            if (observation.Subject is not null || observation.SubjectName is not null)
                text.AppendLine($"- Sujeito: {Cell(observation.Subject)}" +
                    (observation.SubjectName is null ? string.Empty : $" ({Safe(observation.SubjectName)})"));
            text.AppendLine($"- Janela UTC: {observation.WindowFromUtc:O} até {observation.WindowToUtc:O} ({Seconds(observation.Duration)})");
            text.AppendLine($"- Amostras: {observation.SupportingSampleCount} de suporte; {observation.EvaluatedSampleCount} avaliadas");
            text.AppendLine($"- Valores observados — mínimo: {FormatValue(observation.Unit, observation.ObservedMin)}; " +
                $"máximo: {FormatValue(observation.Unit, observation.ObservedMax)}; " +
                $"média: {FormatValue(observation.Unit, observation.ObservedMean)}; " +
                $"P95: {FormatValue(observation.Unit, observation.ObservedP95)}");
            text.AppendLine($"- Fonte: {Safe(observation.Source)}");
            text.AppendLine($"- Descrição: {Safe(observation.Statement)}");
            foreach (var limitation in observation.Limitations)
                text.AppendLine($"- Limitação: {Safe(limitation)}");
        }
        text.AppendLine();

        text.AppendLine("## Auto-sobrecarga da coleta");
        text.AppendLine();
        var self = session.SelfOverhead;
        text.AppendLine($"- Amostras de auto-sobrecarga: {self.Samples}");
        text.AppendLine($"- CPU do SentinelCore AI — média: {Percent(self.CpuPercentMean)}; máxima: {Percent(self.CpuPercentMax)}");
        text.AppendLine($"- Working set máximo: {Bytes(self.WorkingSetBytesMax)}");
        text.AppendLine($"- Memória privada máxima: {Bytes(self.PrivateBytesMax)}");
        text.AppendLine($"- Duração da coleta — média: {Milliseconds(self.CollectionDurationMeanMilliseconds)}; " +
            $"máxima: {Milliseconds(self.CollectionDurationMaxMilliseconds)}");
        text.AppendLine();

        text.AppendLine("## Limitações da sessão");
        text.AppendLine();
        if (session.Limitations.Count == 0)
            text.AppendLine("- Nenhuma limitação registrada. A ausência de limitações não amplia o escopo da leitura.");
        else
            foreach (var limitation in session.Limitations) text.AppendLine($"- {Safe(limitation)}");
        text.AppendLine();

        text.AppendLine("## Notas de análise");
        text.AppendLine();
        if (session.AnalysisNotes.Count == 0)
            text.AppendLine("- Nenhuma nota registrada.");
        else
        {
            text.AppendLine("| Regra | Avaliada | Motivo | Detalhe |");
            text.AppendLine("| --- | --- | --- | --- |");
            foreach (var note in session.AnalysisNotes)
                text.AppendLine($"| {Cell(note.RuleId)} {Cell(note.RuleVersion)} | {(note.Evaluated ? "Sim" : "Não")} | " +
                    $"{NoteReasonName(note.Reason)} | {Cell(note.Detail)} |");
        }
        text.AppendLine();

        text.AppendLine("## Escopo");
        text.AppendLine();
        text.AppendLine("Esta sessão não renova a validade de auditorias. Observação não é diagnóstico.");
        text.AppendLine("Nenhum processo foi encerrado ou teve prioridade alterada; a coleta é somente de leitura.");
        return text.ToString();
    }

    private static void StorageTable(StringBuilder text, IReadOnlyList<StorageStatistics> items)
    {
        text.AppendLine("| Identificador | Instância | Disco | Letra | Leitura | Escrita | Latência leitura | Latência escrita | Fila | Atividade |");
        text.AppendLine("| --- | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var item in items)
            text.AppendLine($"| {Cell(item.Id)} | {Cell(item.InstanceName)} | " +
                $"{(item.DiskNumber is { } number ? number.ToString(CultureInfo.InvariantCulture) : "—")} | " +
                $"{(item.VolumeLetter is null ? "—" : Cell(item.VolumeLetter))} | " +
                $"{Rate(item.ReadBytesPerSecond.Mean)} | {Rate(item.WriteBytesPerSecond.Mean)} | " +
                $"{Latency(item.ReadLatencyMs.Mean)} | {Latency(item.WriteLatencyMs.Mean)} | " +
                $"{Number(item.QueueLength.Mean, 2)} | {Percent(item.PercentActiveTime.Mean)} |");
        if (items.Count == 0) text.AppendLine("| Indisponível | — | — | — | — | — | — | — | — | — |");
    }

    private static IReadOnlyList<ProcessStatistics> TopProcesses(IReadOnlyList<ProcessStatistics> processes)
    {
        var byCpu = processes.Where(process => process.CpuPercent.Mean.HasValue)
            .OrderByDescending(process => process.CpuPercent.Mean)
            .ThenBy(process => process.Pid)
            .Take(TopProcessesByCpu);
        var byGrowth = processes.Where(process => process.PrivateBytes.GrowthPercent.HasValue)
            .OrderByDescending(process => process.PrivateBytes.GrowthPercent)
            .ThenBy(process => process.Pid)
            .Take(TopProcessesByGrowth);
        return byCpu.Concat(byGrowth).DistinctBy(process => process.Identity).ToArray();
    }

    private static string PresetName(PerformanceSessionPreset preset) => preset switch
    {
        PerformanceSessionPreset.Seconds30 => "30 segundos",
        PerformanceSessionPreset.Minute1 => "1 minuto",
        PerformanceSessionPreset.Minutes3 => "3 minutos",
        PerformanceSessionPreset.Custom => "Personalizado",
        _ => preset.ToString()
    };

    private static string ExecutionName(PerformanceExecutionStatus status) => status switch
    {
        PerformanceExecutionStatus.Running => "Em execução",
        PerformanceExecutionStatus.Completed => "Concluída",
        PerformanceExecutionStatus.Cancelled => "Cancelada",
        PerformanceExecutionStatus.Failed => "Falhou",
        PerformanceExecutionStatus.Unsupported => "Não suportada",
        _ => status.ToString()
    };

    private static string CoverageName(PerformanceCoverageStatus status) => status switch
    {
        PerformanceCoverageStatus.Complete => "Completa",
        PerformanceCoverageStatus.Partial => "Parcial",
        PerformanceCoverageStatus.Unavailable => "Indisponível",
        _ => status.ToString()
    };

    private static string NoteReasonName(PerformanceNoteReason reason) => reason switch
    {
        PerformanceNoteReason.Unavailable => "Indisponível",
        PerformanceNoteReason.InvalidSamples => "Amostras inválidas",
        PerformanceNoteReason.InsufficientSamples => "Amostras insuficientes",
        PerformanceNoteReason.PreconditionNotObserved => "Pré-condição não observada",
        PerformanceNoteReason.ConditionNotObserved => "Condição não observada",
        PerformanceNoteReason.Observed => "Condição observada",
        _ => "Não especificado"
    };

    private static string SourceStatusName(PerformanceSourceStatus status) => status switch
    {
        PerformanceSourceStatus.Running => "Em execução",
        PerformanceSourceStatus.Completed => "Concluída",
        PerformanceSourceStatus.Partial => "Parcial",
        PerformanceSourceStatus.Failed => "Falhou",
        PerformanceSourceStatus.Unsupported => "Não suportada",
        PerformanceSourceStatus.Cancelled => "Cancelada",
        _ => status.ToString()
    };

    private static string CategoryName(PerformanceSourceCategory category) => category switch
    {
        PerformanceSourceCategory.Cpu => "CPU",
        PerformanceSourceCategory.Memory => "Memória",
        PerformanceSourceCategory.Processes => "Processos",
        PerformanceSourceCategory.Storage => "Armazenamento",
        _ => category.ToString()
    };

    private static string KindName(PerformanceObservationKind kind) => kind switch
    {
        PerformanceObservationKind.SustainedCpu => "CPU sustentada",
        PerformanceObservationKind.LowAvailableMemory => "Memória disponível baixa",
        PerformanceObservationKind.CommitMargin => "Margem de commit",
        PerformanceObservationKind.PrivateMemoryGrowth => "Crescimento de memória privada",
        PerformanceObservationKind.DiskLatency => "Latência de disco",
        PerformanceObservationKind.IoPeak => "Pico de atividade de E/S",
        _ => kind.ToString()
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

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + " s";

    private static string Milliseconds(double? value) => value is { } number
        ? number.ToString("F2", CultureInfo.InvariantCulture) + " ms" : "Indisponível";

    private static string Percent(double? value) => value is { } number
        ? number.ToString("F1", CultureInfo.InvariantCulture) + "%" : "Indisponível";

    private static string Number(double? value, int decimals) => value is { } number
        ? number.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
        : "Indisponível";

    private static string Bytes(double? value) => value is { } number
        ? number >= 1073741824d ? (number / 1073741824d).ToString("N2", CultureInfo.InvariantCulture) + " GiB"
        : number >= 1048576d ? (number / 1048576d).ToString("N1", CultureInfo.InvariantCulture) + " MiB"
        : number >= 1024d ? (number / 1024d).ToString("N1", CultureInfo.InvariantCulture) + " KiB"
        : number.ToString("N0", CultureInfo.InvariantCulture) + " B"
        : "Indisponível";

    private static string Bytes(ulong? value) => value is { } number ? Bytes((double)number) : "Indisponível";

    private static string Rate(double? value) => value is { } number ? Bytes(number) + "/s" : "Indisponível";

    private static string Latency(double? value) => value is { } number
        ? number.ToString("F2", CultureInfo.InvariantCulture) + " ms" : "Indisponível";

    private static string FormatValue(string unit, double? value)
    {
        if (value is not { } number) return "Indisponível";
        return unit switch
        {
            "bytes" => Bytes(number),
            "bytes/s" => Rate(number),
            "ms" => Latency(number),
            "%" => Percent(number),
            _ => number.ToString("F2", CultureInfo.InvariantCulture) + " " + Safe(unit)
        };
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "Indisponível"
        : value.Replace("\r", " ").Replace("\n", " ").Replace("`", "'").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Cell(string? value) => Safe(value).Replace("|", "&#124;", StringComparison.Ordinal);
}
