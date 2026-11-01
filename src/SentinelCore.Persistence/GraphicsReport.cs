using System.Globalization;
using System.Text;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

/// <summary>
/// Relatório Markdown de uma investigação gráfica. Todo texto é derivado das evidências observadas: nenhum evento
/// é apresentado como causa e nenhuma métrica de GPU não coletada é inferida.
/// </summary>
public static class GraphicsReport
{
    private const int EventRows = 200;
    private const int WerRows = 120;

    public static string Render(GraphicsInvestigation investigation)
    {
        ArgumentNullException.ThrowIfNull(investigation);
        var text = new StringBuilder();
        var coverage = investigation.Coverage;

        text.AppendLine("# SentinelCore AI — Diagnóstico gráfico");
        text.AppendLine();
        text.AppendLine($"- ID: {investigation.Id:D}");
        text.AppendLine($"- Versão do software: {Safe(investigation.SoftwareVersion)}");
        text.AppendLine($"- Formato: {investigation.FormatVersion} (esquema gráfico {Safe(investigation.SchemaVersion)})");
        text.AppendLine($"- Janela: {WindowName(investigation.Window)}");
        text.AppendLine($"- Período UTC: {investigation.PeriodFromUtc:O} até {investigation.PeriodToUtc:O}");
        text.AppendLine($"- Início UTC: {investigation.StartedUtc:O}");
        text.AppendLine($"- Conclusão UTC: {investigation.CompletedUtc:O}");
        text.AppendLine($"- Duração: {investigation.Duration.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)} s");
        text.AppendLine($"- Situação: {StatusName(investigation.Status)}");
        text.AppendLine($"- Execução: {ExecutionName(coverage.Execution)}");
        text.AppendLine($"- Cobertura: {CoverageName(coverage.Status)}");
        text.AppendLine($"- Fontes concluídas: {coverage.CompletedSources}/{coverage.ExpectedSources}");
        text.AppendLine($"- Fontes indisponíveis: {(coverage.UnavailableSources.Count == 0 ? "nenhuma" :
            string.Join(", ", coverage.UnavailableSources.Select(Safe)))}");
        text.AppendLine($"- Leitura de hardware desta execução: {investigation.HardwareReadingId:D} (efêmera; não é um snapshot persistido)");
        text.AppendLine($"- Leitura de eventos desta execução: " +
            (investigation.EventReadingId?.ToString("D") ?? "não executada") +
            " (efêmera; não é uma investigação persistida)");
        text.AppendLine($"- Investigação anterior comparada: {investigation.PreviousInvestigationId?.ToString("D") ?? "nenhuma"}");
        text.AppendLine($"- Comparação de alterações: {(investigation.ChangesComparable ? "avaliada" : "não avaliada")}");
        text.AppendLine("- Causalidade: não avaliada");
        text.AppendLine();
        text.AppendLine("> Leitura somente. Nenhum driver, configuração de exibição, resolução, HDR, frequência, energia " +
            "ou arquivo do sistema foi alterado. Nenhuma elevação foi solicitada. Um evento gráfico registrado não " +
            "prova defeito físico da GPU, não identifica causa e não confirma que uma tela preta tenha sido causada " +
            "por ele. A correlação avançada permanece fora desta fase.");
        text.AppendLine();

        text.AppendLine("## Fontes");
        text.AppendLine();
        text.AppendLine("| Coletor | Versão | Categoria | Origem | Status | Registros lidos | Evidência gráfica | Limite |");
        text.AppendLine("| --- | --- | --- | --- | --- | ---: | ---: | --- |");
        foreach (var source in investigation.Sources)
            text.AppendLine($"| {Cell(source.CollectorId)} | {Cell(source.CollectorVersion)} | " +
                $"{SourceCategoryName(source.Category)} | {Cell(source.Source)} | {SourceStatusName(source.Status)} | " +
                $"{source.RecordsRead} | {source.EvidenceCount} | {(source.LimitHit ? "Sim" : "Não")} |");
        if (investigation.Sources.Count == 0) text.AppendLine("| Indisponível | — | — | — | — | — | — | — |");
        text.AppendLine();
        foreach (var source in investigation.Sources)
            foreach (var limitation in source.Limitations)
                text.AppendLine($"- Limitação de {Safe(source.CollectorId)}: {Safe(limitation)}");
        if (investigation.Sources.Any(source => source.Limitations.Count > 0)) text.AppendLine();

        text.AppendLine("## Adaptadores gráficos");
        text.AppendLine();
        text.AppendLine("| Adaptador | Fabricante | Hardware ID | Driver | Versão | Data UTC | Estado | Presença | VRAM reportada |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | ---: |");
        foreach (var adapter in investigation.Adapters)
            text.AppendLine($"| {Cell(adapter.Name)} | {Cell(adapter.Manufacturer)} | {Cell(adapter.HardwareId)} | " +
                $"{Cell(adapter.DriverProvider)} | {Cell(adapter.DriverVersion)} | " +
                $"{(adapter.DriverDateUtc is { } date ? date.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "—")} | " +
                $"{Cell(adapter.WindowsState)} | {PresenceName(adapter.Presence)} | {Bytes(adapter.ReportedAdapterRamBytes)} |");
        if (investigation.Adapters.Count == 0)
            text.AppendLine("| Indisponível | — | — | — | — | — | — | — | — |");
        text.AppendLine();
        text.AppendLine("VRAM reportada é o valor `AdapterRAM` do WMI, fornecido pelo driver e sujeito a limite de 32 bits; " +
            "não é a VRAM física medida. Temperatura, potência, frequência e utilização global não são coletadas.");
        text.AppendLine();
        foreach (var adapter in investigation.Adapters)
            foreach (var limitation in adapter.Limitations)
                text.AppendLine($"- Limitação de {Safe(adapter.Name ?? adapter.StableId)}: {Safe(limitation)}");
        if (investigation.Adapters.Any(adapter => adapter.Limitations.Count > 0)) text.AppendLine();

        text.AppendLine("## Monitores e configuração de exibição");
        text.AppendLine();
        text.AppendLine("| Monitor | Fabricante | Resolução ativa | Atualização | HDR suportado | HDR ativo | Conexão | Modos |");
        text.AppendLine("| --- | --- | --- | ---: | --- | --- | --- | ---: |");
        foreach (var monitor in investigation.Monitors)
            text.AppendLine($"| {Cell(monitor.Name)} | {Cell(monitor.Manufacturer)} | " +
                $"{(monitor.Width is { } width && monitor.Height is { } height ? $"{width}×{height}" : "Indisponível")} | " +
                $"{(monitor.RefreshHz is null ? "—" : $"{Safe(monitor.RefreshHz)} Hz")} | " +
                $"{(monitor.HdrSupported is null ? "Indisponível" : monitor.HdrSupported.Value ? "Sim" : "Não")} | " +
                $"{(monitor.HdrEnabled is null ? "Indisponível" : monitor.HdrEnabled.Value ? "Sim" : "Não")} | " +
                $"{Cell(monitor.Connector)} | {monitor.SupportedModes.Count} |");
        if (investigation.Monitors.Count == 0)
            text.AppendLine("| Indisponível | — | — | — | — | — | — | — |");
        text.AppendLine();
        text.AppendLine("Resolução, atualização e HDR são os valores reportados pelo Windows no instante da coleta. " +
            "G-SYNC/VRR não é inferido e nenhuma configuração é modificada.");
        text.AppendLine();
        foreach (var monitor in investigation.Monitors)
            foreach (var limitation in monitor.Limitations)
                text.AppendLine($"- Limitação de {Safe(monitor.Name ?? monitor.StableId)}: {Safe(limitation)}");
        if (investigation.Monitors.Any(monitor => monitor.Limitations.Count > 0)) text.AppendLine();

        text.AppendLine("## Drivers gráficos");
        text.AppendLine();
        text.AppendLine("| Driver | Provedor | Versão | Data UTC | Classe | INF | Presença | Estado |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var driver in investigation.Drivers)
            text.AppendLine($"| {Cell(driver.Name)} | {Cell(driver.Provider)} | {Cell(driver.Version)} | " +
                $"{(driver.DateUtc is { } date ? date.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "—")} | " +
                $"{Cell(driver.DeviceClass)} | {Cell(driver.InfName)} | {PresenceName(driver.Presence)} | " +
                $"{Cell(driver.WindowsState)} |");
        if (investigation.Drivers.Count == 0)
            text.AppendLine("| Indisponível | — | — | — | — | — | — | — |");
        text.AppendLine();
        text.AppendLine("A presença do driver não comprova uso efetivo. Data antiga não prova desatualização. Nada é " +
            "baixado, instalado ou atualizado.");
        text.AppendLine();

        text.AppendLine("## Eventos gráficos observados");
        text.AppendLine();
        if (investigation.Events.Count == 0)
            text.AppendLine("Nenhum evento classificado como gráfico foi observado na janela. Ausência de evento não " +
                "prova ausência de falha: o canal pode não ter registrado o incidente.");
        else
        {
            text.AppendLine("| Horário UTC | Canal | Provedor | Event ID | RecordId | Nível | Classificação | Mensagem |");
            text.AppendLine("| --- | --- | --- | ---: | ---: | --- | --- | --- |");
            foreach (var item in investigation.Events.Take(EventRows))
                text.AppendLine($"| {item.OccurredUtc:O} | {Cell(item.Channel)} | {Cell(item.Provider)} | " +
                    $"{item.EventId} | {item.RecordId?.ToString(CultureInfo.InvariantCulture) ?? "—"} | " +
                    $"{LevelName(item.Level)} | {EventKindName(item.Kind)} | {MessageCell(item)} |");
            if (investigation.Events.Count > EventRows)
                text.AppendLine($"| … | — | — | — | — | — | — | {investigation.Events.Count - EventRows} " +
                    "evento(s) adicional(is) no payload estruturado |");
        }
        text.AppendLine();
        if (investigation.Events.Any(item => item.MessageTruncated))
        {
            text.AppendLine($"Mensagens marcadas com {GraphicsMessagePresentation.TruncatedMarker} foram truncadas no " +
                "limite de evidência gráfica ou na coleta de origem. " +
                GraphicsMessagePresentation.TruncatedNotice +
                " Nada foi reconstruído; truncamento de apresentação não é ausência de evidência.");
            text.AppendLine();
        }

        var tdr = investigation.Events.Where(item => item.Kind == GraphicsEventKind.DisplayDriverReset).ToArray();
        text.AppendLine($"## TDR — eventos Display {GraphicsEvidenceClassification.DisplayDriverResetEventId}");
        text.AppendLine();
        if (tdr.Length == 0)
        {
            text.AppendLine("Nenhum evento Display 4101 observado na janela. Ausência não prova que o driver não foi " +
                "reiniciado em outro período.");
        }
        else
        {
            text.AppendLine($"Eventos observados: {tdr.Length}");
            text.AppendLine();
            text.AppendLine("| Horário UTC | Canal | RecordId | Mensagem |");
            text.AppendLine("| --- | --- | ---: | --- |");
            foreach (var item in tdr)
                text.AppendLine($"| {item.OccurredUtc:O} | {Cell(item.Channel)} | " +
                    $"{item.RecordId?.ToString(CultureInfo.InvariantCulture) ?? "—"} | {MessageCell(item)} |");
            text.AppendLine();
            text.AppendLine("Um evento Display 4101 registra que o driver de vídeo parou de responder e foi " +
                "recuperado. Ele não identifica a causa do tempo limite, não prova defeito físico e não confirma " +
                "que uma tela preta específica tenha sido causada por ele.");
        }
        text.AppendLine();

        text.AppendLine("## LiveKernelEvent 117, 141 e 142");
        text.AppendLine();
        text.AppendLine("| Código | Relatórios WER | Primeira observação UTC | Última observação UTC |");
        text.AppendLine("| ---: | ---: | --- | --- |");
        foreach (var code in GraphicsEvidenceClassification.LiveKernelProblemCodes)
        {
            var kind = GraphicsEvidenceClassification.KindForCode(code);
            var matches = investigation.WerReports.Where(item => item.Kind == kind).ToArray();
            var first = matches.Select(item => item.EventTimeUtc ?? item.CreationTimeUtc).Where(item => item is not null)
                .Min();
            var last = matches.Select(item => item.EventTimeUtc ?? item.CreationTimeUtc).Where(item => item is not null)
                .Max();
            text.AppendLine($"| {code} | {matches.Length} | {first?.ToString("O") ?? "—"} | {last?.ToString("O") ?? "—"} |");
        }
        text.AppendLine();
        text.AppendLine("Os códigos são preservados exatamente como reportados. Esta fase não amplia o significado " +
            "oficial de cada código, não lê dumps e não conclui que exista defeito físico, superaquecimento, problema " +
            "de VRAM ou instabilidade da GPU.");
        text.AppendLine();

        text.AppendLine("## Metadados do Windows Error Reporting");
        text.AppendLine();
        if (investigation.WerReports.Count == 0)
            text.AppendLine("Nenhum relatório WER com classificação gráfica na janela. Ausência não prova ausência de falha.");
        else
        {
            text.AppendLine("| Horário UTC | Tipo | Código | Classificação | Aplicativo | Módulo | Bucket | Status | Confiança |");
            text.AppendLine("| --- | --- | ---: | --- | --- | --- | --- | --- | --- |");
            foreach (var report in investigation.WerReports.Take(WerRows))
                text.AppendLine($"| {(report.EventTimeUtc ?? report.CreationTimeUtc)?.ToString("O") ?? "—"} | " +
                    $"{Cell(report.EventType)} | {Cell(report.ProblemCode)} | {WerKindName(report.Kind)} | " +
                    $"{Cell(report.Application)} | {Cell(report.Module)} | {Cell(report.BucketId)} | " +
                    $"{Cell(report.ReportStatus)} | {EventCatalogName(report.Confidence)} |");
            if (investigation.WerReports.Count > WerRows)
                text.AppendLine($"| … | — | — | — | — | — | — | — | {investigation.WerReports.Count - WerRows} " +
                    "relatório(s) adicional(is) no payload estruturado |");
        }
        text.AppendLine();
        text.AppendLine("Somente metadados são lidos. Nenhum dump é aberto, enviado, apagado ou limpo. Caminhos de " +
            "aplicativo e módulo são preservados quando o próprio relatório os expõe.");
        text.AppendLine();

        text.AppendLine("## Alterações entre investigações");
        text.AppendLine();
        if (investigation.PreviousInvestigationId is null)
            text.AppendLine("Nenhuma investigação anterior foi comparada.");
        else if (!investigation.ChangesComparable)
            text.AppendLine("A comparação não foi avaliada porque uma das leituras estava indisponível ou parcial.");
        else if (investigation.Changes.Count == 0)
            text.AppendLine("Nenhuma alteração observada nas categorias comparáveis.");
        else
        {
            text.AppendLine("| Tipo | Sujeito | Campo | Antes | Depois |");
            text.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var change in investigation.Changes)
                text.AppendLine($"| {ChangeTypeName(change.Type)} | {Cell(change.SubjectName ?? change.Subject)} | " +
                    $"{Cell(change.Field)} | {Cell(change.Before)} | {Cell(change.After)} |");
        }
        text.AppendLine();
        foreach (var limitation in investigation.ComparisonLimitations)
            text.AppendLine($"- Limitação da comparação: {Safe(limitation)}");
        if (investigation.ComparisonLimitations.Count > 0) text.AppendLine();
        text.AppendLine("Alteração observada é descritiva: não indica melhoria, regressão, causa ou relação com evento.");
        text.AppendLine();

        text.AppendLine("## Observações de evidência");
        text.AppendLine();
        if (investigation.Observations.Count == 0)
            text.AppendLine("Nenhuma observação de evidência nesta investigação. Ausência de observação não é " +
                "atestado de normalidade.");
        foreach (var observation in investigation.Observations)
        {
            text.AppendLine();
            text.AppendLine($"### {SeverityName(observation.Severity)} — {Safe(observation.Title)}");
            text.AppendLine();
            text.AppendLine($"- Regra: {Safe(observation.RuleId)} {Safe(observation.RuleVersion)}");
            text.AppendLine($"- Tipo: {ObservationKindName(observation.Kind)}");
            text.AppendLine($"- Confiança: {ConfidenceName(observation.Confidence)}");
            text.AppendLine("- Causalidade: não avaliada");
            text.AppendLine($"- Métrica: {Safe(observation.Metric)} ({Safe(observation.Unit)})");
            if (observation.ObservedValue is { } value)
                text.AppendLine($"- Valor observado: {value.ToString("0.###", CultureInfo.InvariantCulture)} " +
                    $"{Safe(observation.Unit)}");
            text.AppendLine($"- Janela solicitada UTC: {observation.WindowFromUtc:O} até {observation.WindowToUtc:O}");
            text.AppendLine($"- Primeira observação UTC: {observation.FirstObservedUtc?.ToString("O") ?? "indisponível"}");
            text.AppendLine($"- Última observação UTC: {observation.LastObservedUtc?.ToString("O") ?? "indisponível"}");
            text.AppendLine($"- Evidências de suporte: {observation.EvidenceCount}");
            text.AppendLine($"- Fonte: {Safe(observation.Source)}");
            text.AppendLine($"- Descrição: {Safe(observation.Statement)}");
            foreach (var limitation in observation.Limitations)
                text.AppendLine($"- Limitação: {Safe(limitation)}");
        }
        text.AppendLine();

        text.AppendLine("## Notas de análise");
        text.AppendLine();
        if (investigation.AnalysisNotes.Count == 0) text.AppendLine("- Nenhuma nota registrada.");
        else
        {
            text.AppendLine("| Regra | Avaliada | Motivo | Detalhe |");
            text.AppendLine("| --- | --- | --- | --- |");
            foreach (var note in investigation.AnalysisNotes)
                text.AppendLine($"| {Cell(note.RuleId)} {Cell(note.RuleVersion)} | {(note.Evaluated ? "Sim" : "Não")} | " +
                    $"{NoteReasonName(note.Reason)} | {Cell(note.Detail)} |");
        }
        text.AppendLine();

        text.AppendLine("## Limitações da investigação");
        text.AppendLine();
        if (investigation.Limitations.Count == 0)
            text.AppendLine("- Nenhuma limitação registrada. A ausência de limitações não amplia o escopo da leitura.");
        else
            foreach (var limitation in investigation.Limitations) text.AppendLine($"- {Safe(limitation)}");
        text.AppendLine();

        text.AppendLine("## Escopo");
        text.AppendLine();
        text.AppendLine("Esta investigação não renova a validade de auditorias, inventários, investigações de eventos " +
            "ou sessões de desempenho/rede.");
        text.AppendLine("Nenhuma correção, reparo, instalação, atualização ou reversão foi executada.");
        text.AppendLine("Observação gráfica não é diagnóstico, não confirma causa raiz e não atesta integridade ou " +
            "estabilidade do hardware.");
        return text.ToString();
    }

    private static string WindowName(EventWindow window) => window switch
    {
        EventWindow.Last24Hours => "24 horas",
        EventWindow.Last7Days => "7 dias",
        EventWindow.Last30Days => "30 dias",
        _ => "Personalizada"
    };

    private static string StatusName(GraphicsInvestigationStatus status) => status switch
    {
        GraphicsInvestigationStatus.Running => "Em execução",
        GraphicsInvestigationStatus.Completed => "Concluída",
        GraphicsInvestigationStatus.Partial => "Concluída com cobertura parcial",
        GraphicsInvestigationStatus.Failed => "Falhou",
        GraphicsInvestigationStatus.Cancelled => "Cancelada",
        GraphicsInvestigationStatus.Unsupported => "Não suportada",
        _ => status.ToString()
    };

    private static string ExecutionName(GraphicsExecutionStatus status) => status switch
    {
        GraphicsExecutionStatus.Running => "Em execução",
        GraphicsExecutionStatus.Completed => "Concluída",
        GraphicsExecutionStatus.Cancelled => "Cancelada",
        GraphicsExecutionStatus.Failed => "Falhou",
        GraphicsExecutionStatus.Unsupported => "Não suportada",
        _ => status.ToString()
    };

    private static string CoverageName(GraphicsCoverageStatus status) => status switch
    {
        GraphicsCoverageStatus.Complete => "Completa",
        GraphicsCoverageStatus.Partial => "Parcial",
        GraphicsCoverageStatus.Unavailable => "Indisponível",
        _ => status.ToString()
    };

    private static string SourceStatusName(GraphicsSourceStatus status) => status switch
    {
        GraphicsSourceStatus.Running => "Em execução",
        GraphicsSourceStatus.Completed => "Concluída",
        GraphicsSourceStatus.Partial => "Parcial",
        GraphicsSourceStatus.Failed => "Falhou",
        GraphicsSourceStatus.Unsupported => "Não suportada",
        GraphicsSourceStatus.Cancelled => "Cancelada",
        _ => status.ToString()
    };

    private static string SourceCategoryName(GraphicsSourceCategory category) => category switch
    {
        GraphicsSourceCategory.Adapters => "Adaptadores",
        GraphicsSourceCategory.Monitors => "Monitores",
        GraphicsSourceCategory.Drivers => "Drivers",
        GraphicsSourceCategory.Events => "Eventos",
        GraphicsSourceCategory.Wer => "WER",
        _ => category.ToString()
    };

    private static string EventKindName(GraphicsEventKind kind) => kind switch
    {
        GraphicsEventKind.DisplayDriverReset => "TDR — Display 4101",
        GraphicsEventKind.DisplayDriverOther => "Outro evento do provedor Display",
        GraphicsEventKind.GraphicsDriverVendor => "Evento de driver gráfico",
        _ => "Evento gráfico"
    };

    private static string WerKindName(GraphicsWerKind kind) => kind switch
    {
        GraphicsWerKind.LiveKernelEvent117 => "LiveKernelEvent 117",
        GraphicsWerKind.LiveKernelEvent141 => "LiveKernelEvent 141",
        GraphicsWerKind.LiveKernelEvent142 => "LiveKernelEvent 142",
        GraphicsWerKind.LiveKernelEventOther => "LiveKernelEvent (outro código)",
        _ => "Relatório gráfico"
    };

    private static string ObservationKindName(GraphicsObservationKind kind) => kind switch
    {
        GraphicsObservationKind.DisplayDriverReset => "Reinício do driver",
        GraphicsObservationKind.LiveKernelEvent => "LiveKernelEvent",
        GraphicsObservationKind.MultipleAdapters => "Múltiplos adaptadores",
        GraphicsObservationKind.AdapterState => "Estado de adaptador",
        GraphicsObservationKind.MonitorTopology => "Topologia de monitores",
        GraphicsObservationKind.AdapterChange => "Alteração de adaptador",
        GraphicsObservationKind.DriverChange => "Alteração de driver",
        GraphicsObservationKind.MonitorChange => "Alteração de monitor",
        _ => "Metadados WER"
    };

    private static string ChangeTypeName(GraphicsChangeType type) => type switch
    {
        GraphicsChangeType.AdapterAdded => "Adaptador adicionado",
        GraphicsChangeType.AdapterRemoved => "Adaptador removido",
        GraphicsChangeType.MonitorAdded => "Monitor adicionado",
        GraphicsChangeType.MonitorRemoved => "Monitor removido",
        GraphicsChangeType.DriverChanged => "Driver alterado",
        GraphicsChangeType.ConfigurationChanged => "Configuração alterada",
        _ => "Estado alterado"
    };

    private static string NoteReasonName(GraphicsNoteReason reason) => reason switch
    {
        GraphicsNoteReason.Unavailable => "Indisponível",
        GraphicsNoteReason.InsufficientEvidence => "Evidência insuficiente",
        GraphicsNoteReason.PreconditionNotObserved => "Pré-condição não observada",
        GraphicsNoteReason.ConditionNotObserved => "Condição não observada",
        GraphicsNoteReason.Observed => "Observado",
        _ => "Não especificado"
    };

    private static string LevelName(EventLevel level) => level switch
    {
        EventLevel.Critical => "Crítico",
        EventLevel.Error => "Erro",
        EventLevel.Warning => "Aviso",
        EventLevel.Information => "Informação",
        EventLevel.Verbose => "Detalhado",
        _ => "Desconhecido"
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

    private static string EventCatalogName(EvidenceConfidence confidence) => ConfidenceName(confidence);

    private static string PresenceName(DevicePresence presence) => presence switch
    {
        DevicePresence.Present => "Presente",
        DevicePresence.Virtual => "Virtual",
        DevicePresence.Disconnected => "Desconectado",
        _ => "Indeterminado"
    };

    private static string Bytes(ulong? value) =>
        value is { } bytes ? $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} B" : "Indisponível";

    private static string Cell(string? value) => Safe(value).Replace("|", "\\|");

    private static string MessageCell(GraphicsEventEvidence item) =>
        Cell(GraphicsMessagePresentation.Text(item.Message, item.MessageTruncated));

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "—"
        : value.Replace("\r", " ").Replace("\n", " ").Replace("`", "'")
            .Replace("<", "&lt;").Replace(">", "&gt;");
}
