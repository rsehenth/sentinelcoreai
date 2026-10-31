using System.Globalization;
using System.Text;
using SentinelCore.Domain;

namespace SentinelCore.Persistence;

/// <summary>
/// Relatório Markdown de uma sessão de rede. Todo texto é derivado dos fatos observados:
/// nenhuma conexão é descrita como causa e nenhum endereço aparece sem autorização explícita.
/// </summary>
public static class NetworkReport
{
    private const int SampleRows = 90;

    public static string Render(NetworkSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var text = new StringBuilder();
        var coverage = session.Coverage;
        var statistics = session.Statistics;

        text.AppendLine("# SentinelCore AI — Diagnóstico de rede");
        text.AppendLine();
        text.AppendLine($"- ID: {session.Id:D}");
        text.AppendLine($"- Versão do software: {Safe(session.SoftwareVersion)}");
        text.AppendLine($"- Formato: {session.FormatVersion} (esquema de rede {Safe(session.SchemaVersion)})");
        text.AppendLine($"- Preset: {PresetName(session.Preset)}");
        text.AppendLine($"- Duração solicitada: {Seconds(session.RequestedDuration)}");
        text.AppendLine($"- Duração medida: {Seconds(session.Duration)}");
        text.AppendLine($"- Intervalo de amostragem: {Seconds(session.SampleInterval)}");
        text.AppendLine($"- Amostras: {session.Samples.Count}");
        text.AppendLine($"- Início UTC: {session.StartedUtc:O}");
        text.AppendLine($"- Conclusão UTC: {session.CompletedUtc:O}");
        text.AppendLine($"- Situação: {StatusName(session.Status)}");
        text.AppendLine($"- Detalhes de endpoint preservados: {(session.IncludesEndpointDetails ? "sim" : "não")} " +
            $"({session.EndpointDetails.Count})");
        text.AppendLine($"- Endereços preservados: {(session.IncludesAddresses ? "sim" : "não")}");
        text.AppendLine($"- Causalidade: não avaliada");
        if (coverage is not null)
        {
            text.AppendLine($"- Execução: {ExecutionName(coverage.Execution)}");
            text.AppendLine($"- Cobertura: {CoverageName(coverage.Status)}");
            text.AppendLine($"- Amostras esperadas/coletadas: {coverage.ExpectedSamples}/{coverage.CollectedSamples}");
            text.AppendLine($"- Oportunidades de amostragem perdidas: {coverage.MissedOpportunities}");
            text.AppendLine($"- Limites de cobertura: amostras {(coverage.SampleLimitReached ? "sim" : "não")}; " +
                $"detalhes {(coverage.DetailLimitReached ? "sim" : "não")}; " +
                $"intervalo dinâmico indisponível {(coverage.PortRangeUnavailable ? "sim" : "não")}");
            text.AppendLine($"- Fontes indisponíveis: {(coverage.UnavailableCollectors.Count == 0 ? "nenhuma" :
                string.Join(", ", coverage.UnavailableCollectors.Select(Safe)))}");
        }
        text.AppendLine();
        text.AppendLine("> Leitura somente. Nenhuma conexão foi aberta, nenhum pacote foi capturado, " +
            "nenhuma porta foi sondada e nenhuma configuração de rede foi alterada. " +
            "As tabelas TCP/UDP são instantâneos do sistema no momento da amostra: a ausência de um endpoint " +
            "não prova que ele nunca existiu, e uma contagem elevada não prova esgotamento.");
        text.AppendLine();

        text.AppendLine("## Fontes");
        text.AppendLine();
        text.AppendLine("| Coletor | Versão | Categoria | Origem | Status | Leituras | Limite | Leitura média | Leitura máxima |");
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

        text.AppendLine("## Intervalos dinâmicos");
        text.AppendLine();
        text.AppendLine("| Protocolo | Família | Faixa | Portas | Origem | Padrão do Windows | Reservas no Registro |");
        text.AppendLine("| --- | --- | --- | ---: | --- | --- | ---: |");
        foreach (var range in session.PortRanges)
            text.AppendLine($"| {range.Protocol} | {range.Family} | {range.StartPort}–{range.EndPort} | " +
                $"{range.PortCount} | {Cell(range.Source)} | {(range.IsWindowsDefault ? "Sim (não medido)" : "Não")} | " +
                $"{range.Exclusions.Count} |");
        if (session.PortRanges.Count == 0)
            text.AppendLine("| Indisponível | — | — | — | Nenhum intervalo observado | — | — |");
        text.AppendLine();
        foreach (var range in session.PortRanges)
            foreach (var limitation in range.Limitations)
                text.AppendLine($"- Limitação de {range.Protocol} {range.Family}: {Safe(limitation)}");
        if (session.PortRanges.Any(range => range.Limitations.Count > 0)) text.AppendLine();

        text.AppendLine("## Adaptadores");
        text.AppendLine();
        text.AppendLine("| Identificador | Tipo | Estado | IPv4 | IPv6 | Endereços IPv4 | Endereços IPv6 |");
        text.AppendLine("| --- | --- | --- | --- | --- | ---: | ---: |");
        foreach (var adapter in session.Adapters)
            text.AppendLine($"| {Cell(adapter.Id)} | {Cell(adapter.Kind)} | {Cell(adapter.OperationalStatus)} | " +
                $"{(adapter.HasIpv4 ? "Sim" : "Não")} | {(adapter.HasIpv6 ? "Sim" : "Não")} | " +
                $"{adapter.Ipv4AddressCount} | {adapter.Ipv6AddressCount} |");
        if (session.Adapters.Count == 0) text.AppendLine("| Indisponível | — | — | — | — | — | — |");
        text.AppendLine();
        text.AppendLine("Endereços, máscaras e identificadores físicos dos adaptadores não são lidos para o registro.");
        text.AppendLine();

        text.AppendLine("## Estatísticas de transporte");
        text.AppendLine();
        if (session.TcpStatistics is { } tcp)
        {
            text.AppendLine($"- TCP {tcp.Family} — conexões acumuladas: {tcp.CumulativeConnections}; " +
                $"estabelecidas no instante: {tcp.CurrentEstablished}");
            text.AppendLine($"- TCP {tcp.Family} — aberturas ativas: {tcp.ActiveOpens}; passivas: {tcp.PassiveOpens}; " +
                $"falhas de tentativa: {tcp.AttemptFails}");
            text.AppendLine($"- TCP {tcp.Family} — segmentos entrada: {tcp.InSegments}; saída: {tcp.OutSegments}; " +
                $"retransmitidos: {tcp.RetransmittedSegments}");
            text.AppendLine($"- TCP {tcp.Family} — erros de entrada: {tcp.InErrors}; reinicializações: {tcp.OutResets}; " +
                $"RTO: {tcp.RtoMin}–{tcp.RtoMax} ms");
            text.AppendLine($"- TCP {tcp.Family} — limite de conexões: " +
                $"{(tcp.MaxConnections is { } max ? max.ToString(CultureInfo.InvariantCulture) : "não informado")}");
            foreach (var limitation in tcp.Limitations) text.AppendLine($"- Limitação de TCP: {Safe(limitation)}");
        }
        else text.AppendLine("- Estatísticas TCP indisponíveis nesta sessão.");
        if (session.UdpStatistics is { } udp)
        {
            text.AppendLine($"- UDP {udp.Family} — datagramas recebidos: {udp.InDatagrams}; enviados: {udp.OutDatagrams}; " +
                $"sem porta de destino: {udp.NoPorts}; erros: {udp.InErrors}; ouvintes: {udp.Listeners}");
            foreach (var limitation in udp.Limitations) text.AppendLine($"- Limitação de UDP: {Safe(limitation)}");
        }
        else text.AppendLine("- Estatísticas UDP indisponíveis nesta sessão.");
        text.AppendLine();

        text.AppendLine("## Indicadores");
        text.AppendLine();
        text.AppendLine($"- Conexões TCP — pico: {statistics.PeakTcpConnections}; " +
            $"média: {Number(statistics.MeanTcpConnections, 1)}; final: {statistics.FinalTcpConnections}");
        text.AppendLine($"- Crescimento percentual de TCP na janela: {Percent(statistics.TcpGrowthPercent)}");
        text.AppendLine($"- Endpoints UDP — pico: {statistics.PeakUdpEndpoints}; " +
            $"média: {Number(statistics.MeanUdpEndpoints, 1)}; final: {statistics.FinalUdpEndpoints}");
        text.AppendLine($"- Portas locais distintas na amostra final: {statistics.FinalDistinctLocalPorts}");
        text.AppendLine($"- Endpoints na faixa dinâmica: {statistics.FinalEphemeralEndpoints} " +
            $"({Percent(statistics.FinalEphemeralSharePercent)} dos endpoints)");
        text.AppendLine($"- TCP em escuta: {statistics.FinalListening}; estabelecidas: {statistics.FinalEstablished}; " +
            $"TIME_WAIT: {statistics.FinalTimeWait}");
        text.AppendLine($"- Amostras agregadas: {statistics.ValidSamples} válidas de {statistics.TotalSamples}");
        text.AppendLine("- Contagem de conexões e quantidade de portas são eixos distintos e não são somadas.");
        text.AppendLine();

        text.AppendLine("## Estados TCP na amostra final");
        text.AppendLine();
        text.AppendLine("| Estado | Conexões |");
        text.AppendLine("| --- | ---: |");
        foreach (var state in statistics.FinalStates)
            text.AppendLine($"| {StateName(state.State)} | {state.Connections} |");
        if (statistics.FinalStates.Count == 0) text.AppendLine("| Indisponível | — |");
        text.AppendLine();
        text.AppendLine("TIME_WAIT, CLOSE_WAIT e SYN_SENT são estados previstos pelo protocolo; " +
            "sua presença não é classificada como erro.");
        text.AppendLine();

        text.AppendLine("## Portas locais principais");
        text.AppendLine();
        text.AppendLine("| Porta | Endpoints | Processos distintos |");
        text.AppendLine("| ---: | ---: | ---: |");
        foreach (var port in statistics.FinalTopLocalPorts)
            text.AppendLine($"| {port.Port} | {port.Endpoints} | {port.DistinctProcesses} |");
        if (statistics.FinalTopLocalPorts.Count == 0) text.AppendLine("| — | — | — |");
        text.AppendLine();

        text.AppendLine("## Processos com mais endpoints");
        text.AppendLine();
        text.AppendLine("| Identidade | Nome | PID | TCP | UDP | Portas em escuta |");
        text.AppendLine("| --- | --- | ---: | ---: | ---: | ---: |");
        foreach (var process in statistics.FinalTopProcesses)
            text.AppendLine($"| {Cell(process.Identity)} | {Cell(process.Name)} | {process.Pid} | " +
                $"{process.TcpConnections} | {process.UdpEndpoints} | {process.ListeningPorts} |");
        if (statistics.FinalTopProcesses.Count == 0) text.AppendLine("| Indisponível | — | — | — | — | — |");
        text.AppendLine();
        text.AppendLine("A identidade combina PID e hora de criação; um PID isolado não identifica um processo " +
            "ao longo do tempo porque pode ser reutilizado. Nenhum caminho completo é registrado.");
        text.AppendLine();

        text.AppendLine("## Observações de evidência");
        text.AppendLine();
        if (session.Observations.Count == 0)
            text.AppendLine("Nenhuma observação de evidência nesta sessão. Ausência de observação não é atestado de normalidade.");
        foreach (var observation in session.Observations)
        {
            text.AppendLine();
            text.AppendLine($"### {SeverityName(observation.Severity)} — {Safe(observation.Title)}");
            text.AppendLine();
            text.AppendLine($"- Regra: {Safe(observation.RuleId)} {Safe(observation.RuleVersion)}");
            text.AppendLine($"- Tipo: {KindName(observation.Kind)}");
            text.AppendLine($"- Confiança: {ConfidenceName(observation.Confidence)}");
            text.AppendLine("- Causalidade: não avaliada");
            text.AppendLine($"- Métrica: {Safe(observation.Metric)} ({Safe(observation.Unit)})");
            if (observation.Threshold is { } threshold)
                text.AppendLine($"- Limiar: {threshold.ToString("0.##", CultureInfo.InvariantCulture)} " +
                    $"{Safe(observation.Unit)}");
            if (observation.Subject is not null || observation.SubjectName is not null)
                text.AppendLine($"- Sujeito: {Cell(observation.Subject)}" +
                    (observation.SubjectName is null ? string.Empty : $" ({Safe(observation.SubjectName)})"));
            text.AppendLine($"- Janela UTC: {observation.WindowFromUtc:O} até {observation.WindowToUtc:O} " +
                $"({Seconds(observation.Duration)})");
            text.AppendLine($"- Amostras: {observation.SupportingSampleCount} de suporte; " +
                $"{observation.EvaluatedSampleCount} avaliadas");
            text.AppendLine($"- Valores observados — mínimo: {Value(observation.ObservedMin)}; " +
                $"máximo: {Value(observation.ObservedMax)}; média: {Value(observation.ObservedMean)}; " +
                $"P95: {Value(observation.ObservedP95)}");
            text.AppendLine($"- Fonte: {Safe(observation.Source)}");
            text.AppendLine($"- Descrição: {Safe(observation.Statement)}");
            foreach (var limitation in observation.Limitations)
                text.AppendLine($"- Limitação: {Safe(limitation)}");
        }
        text.AppendLine();

        if (session.TcpipEvents is { } events)
        {
            text.AppendLine("## Contexto de eventos Tcpip");
            text.AppendLine();
            text.AppendLine($"- Investigação de origem: {events.InvestigationId?.ToString("D") ?? "não informada"}");
            text.AppendLine($"- Eventos 4266 (limite de tentativas de conexão): {events.AttemptFailureEvents}");
            text.AppendLine($"- Eventos 4231 (falha ao alocar porta dinâmica): {events.EphemeralExhaustionEvents}");
            text.AppendLine($"- Primeira observação UTC: {events.FirstObservedUtc?.ToString("O") ?? "indisponível"}");
            text.AppendLine($"- Última observação UTC: {events.LastObservedUtc?.ToString("O") ?? "indisponível"}");
            text.AppendLine($"- Nota: {Safe(events.Note)}");
            text.AppendLine();
            text.AppendLine("O contexto é informativo: eventos de transporte não renovam a validade da sessão " +
                "nem confirmam esgotamento na janela observada.");
            text.AppendLine();
        }

        text.AppendLine("## Amostras");
        text.AppendLine();
        text.AppendLine("| Índice | Decorrido | TCP | UDP | Escuta | Portas | Dinâmicos | Coleta |");
        text.AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var sample in session.Samples.Take(SampleRows))
            text.AppendLine($"| {sample.Index} | {sample.ElapsedSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s | " +
                $"{sample.TcpConnections} | {sample.UdpEndpoints} | {sample.ListeningTcp} | " +
                $"{sample.DistinctLocalPorts} | {sample.EphemeralTcpConnections + sample.EphemeralUdpEndpoints} | " +
                $"{sample.CollectionDurationMilliseconds} ms |");
        if (session.Samples.Count == 0) text.AppendLine("| — | — | — | — | — | — | — | — |");
        else if (session.Samples.Count > SampleRows)
            text.AppendLine($"| … | {session.Samples.Count - SampleRows} amostra(s) adicional(is) no registro estruturado | " +
                "| | | | | |");
        text.AppendLine();

        text.AppendLine("## Detalhes de endpoint");
        text.AppendLine();
        if (!session.IncludesEndpointDetails || session.EndpointDetails.Count == 0)
            text.AppendLine("Nenhum detalhe de endpoint preservado nesta sessão.");
        else
        {
            text.AppendLine("Detalhes limitados à última amostra coletada e truncados por limite de volume. " +
                "Sem autorização explícita, endereços local e remoto não são preservados.");
            text.AppendLine();
            text.AppendLine("| Protocolo | Família | Porta local | Endereço local | Porta remota | Endereço remoto | " +
                "Estado | PID | Processo | Situação |");
            text.AppendLine("| --- | --- | ---: | --- | ---: | --- | --- | ---: | --- | --- |");
            foreach (var endpoint in session.EndpointDetails)
                text.AppendLine($"| {endpoint.Protocol} | {endpoint.Family} | {endpoint.LocalPort} | " +
                    $"{Address(endpoint.LocalAddress)} | " +
                    $"{(endpoint.RemotePort is { } remotePort ? remotePort.ToString(CultureInfo.InvariantCulture) : "—")} | " +
                    $"{Address(endpoint.RemoteAddress)} | " +
                    $"{(endpoint.TcpState is { } state ? StateName(state) : "—")} | {endpoint.Pid} | " +
                    $"{Cell(endpoint.ProcessName)} | {ProcessStatusName(endpoint.ProcessStatus)} |");
        }
        text.AppendLine();

        text.AppendLine("## Auto-sobrecarga da coleta");
        text.AppendLine();
        text.AppendLine($"- Leituras de auto-sobrecarga: {session.SelfOverhead.Samples}");
        text.AppendLine($"- CPU do SentinelCore AI — média: {Percent(session.SelfOverhead.CpuPercentMean)}; " +
            $"máxima: {Percent(session.SelfOverhead.CpuPercentMax)}");
        text.AppendLine($"- Working set máximo: {Bytes(session.SelfOverhead.WorkingSetBytesMax)}");
        text.AppendLine($"- Memória privada máxima: {Bytes(session.SelfOverhead.PrivateBytesMax)}");
        text.AppendLine($"- Duração da coleta — média: " +
            $"{Milliseconds(session.SelfOverhead.CollectionDurationMeanMilliseconds)}; " +
            $"máxima: {Milliseconds(session.SelfOverhead.CollectionDurationMaxMilliseconds)}");
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
        if (session.AnalysisNotes.Count == 0) text.AppendLine("- Nenhuma nota registrada.");
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
        text.AppendLine("Esta sessão não renova a validade de auditorias, inventários ou investigações.");
        text.AppendLine("Nenhuma correção foi aplicada, nenhum serviço ou driver foi reiniciado e nenhuma " +
            "configuração de rede foi alterada.");
        text.AppendLine("Observação de rede não é diagnóstico, não confirma causa raiz e não atesta integridade " +
            "ou estabilidade do sistema.");
        return text.ToString();
    }

    private static string PresetName(NetworkSessionPreset preset) => preset switch
    {
        NetworkSessionPreset.Snapshot => "Instantâneo",
        NetworkSessionPreset.Seconds30 => "30 segundos",
        NetworkSessionPreset.Minute1 => "1 minuto",
        _ => preset.ToString()
    };

    private static string StatusName(NetworkSessionStatus status) => status switch
    {
        NetworkSessionStatus.Running => "Em execução",
        NetworkSessionStatus.Completed => "Concluída",
        NetworkSessionStatus.Partial => "Concluída com cobertura parcial",
        NetworkSessionStatus.Failed => "Falhou",
        NetworkSessionStatus.Cancelled => "Cancelada",
        NetworkSessionStatus.Unsupported => "Não suportada",
        _ => status.ToString()
    };

    private static string ExecutionName(NetworkExecutionStatus status) => status switch
    {
        NetworkExecutionStatus.Running => "Em execução",
        NetworkExecutionStatus.Completed => "Concluída",
        NetworkExecutionStatus.Cancelled => "Cancelada",
        NetworkExecutionStatus.Failed => "Falhou",
        NetworkExecutionStatus.Unsupported => "Não suportada",
        _ => status.ToString()
    };

    private static string CoverageName(NetworkCoverageStatus status) => status switch
    {
        NetworkCoverageStatus.Complete => "Completa",
        NetworkCoverageStatus.Partial => "Parcial",
        NetworkCoverageStatus.Unavailable => "Indisponível",
        _ => status.ToString()
    };

    private static string SourceStatusName(NetworkSourceStatus status) => status switch
    {
        NetworkSourceStatus.Running => "Em execução",
        NetworkSourceStatus.Completed => "Concluída",
        NetworkSourceStatus.Partial => "Parcial",
        NetworkSourceStatus.Failed => "Falhou",
        NetworkSourceStatus.Unsupported => "Não suportada",
        NetworkSourceStatus.Cancelled => "Cancelada",
        _ => status.ToString()
    };

    private static string CategoryName(NetworkSourceCategory category) => category switch
    {
        NetworkSourceCategory.Endpoints => "Endpoints",
        NetworkSourceCategory.Statistics => "Estatísticas",
        NetworkSourceCategory.PortRange => "Intervalo dinâmico",
        NetworkSourceCategory.Adapters => "Adaptadores",
        _ => category.ToString()
    };

    private static string KindName(NetworkObservationKind kind) => kind switch
    {
        NetworkObservationKind.EndpointGrowth => "Crescimento de endpoints",
        NetworkObservationKind.ProcessConcentration => "Concentração por processo",
        NetworkObservationKind.StateConcentration => "Concentração por estado",
        NetworkObservationKind.DynamicPortPressure => "Uso de portas dinâmicas",
        NetworkObservationKind.AttemptFailures => "Falhas de tentativa TCP",
        _ => kind.ToString()
    };

    private static string StateName(TcpConnectionState state) => state switch
    {
        TcpConnectionState.Closed => "CLOSED",
        TcpConnectionState.Listen => "LISTEN",
        TcpConnectionState.SynSent => "SYN_SENT",
        TcpConnectionState.SynReceived => "SYN_RCVD",
        TcpConnectionState.Established => "ESTABLISHED",
        TcpConnectionState.FinWait1 => "FIN_WAIT_1",
        TcpConnectionState.FinWait2 => "FIN_WAIT_2",
        TcpConnectionState.CloseWait => "CLOSE_WAIT",
        TcpConnectionState.Closing => "CLOSING",
        TcpConnectionState.LastAck => "LAST_ACK",
        TcpConnectionState.TimeWait => "TIME_WAIT",
        TcpConnectionState.DeleteTcb => "DELETE_TCB",
        _ => "DESCONHECIDO"
    };

    private static string ProcessStatusName(NetworkProcessStatus status) => status switch
    {
        NetworkProcessStatus.Resolved => "Resolvido",
        NetworkProcessStatus.AccessDenied => "Acesso negado",
        NetworkProcessStatus.Exited => "Processo encerrado",
        NetworkProcessStatus.SystemProcess => "Processo do sistema",
        _ => "Indisponível"
    };

    private static string NoteReasonName(NetworkNoteReason reason) => reason switch
    {
        NetworkNoteReason.Unavailable => "Indisponível",
        NetworkNoteReason.InvalidSamples => "Amostras inválidas",
        NetworkNoteReason.InsufficientSamples => "Amostras insuficientes",
        NetworkNoteReason.PreconditionNotObserved => "Pré-condição não observada",
        NetworkNoteReason.ConditionNotObserved => "Condição não observada",
        NetworkNoteReason.Observed => "Condição observada",
        _ => "Não especificado"
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

    private static string Value(double? value) => value is { } number
        ? number.ToString("0.###", CultureInfo.InvariantCulture) : "Indisponível";

    private static string Bytes(ulong? value) => value is { } number
        ? number >= 1073741824UL ? (number / 1073741824d).ToString("N2", CultureInfo.InvariantCulture) + " GiB"
        : number >= 1048576UL ? (number / 1048576d).ToString("N1", CultureInfo.InvariantCulture) + " MiB"
        : number >= 1024UL ? (number / 1024d).ToString("N1", CultureInfo.InvariantCulture) + " KiB"
        : number.ToString("N0", CultureInfo.InvariantCulture) + " B"
        : "Indisponível";

    private static string Address(string? value) => string.IsNullOrWhiteSpace(value) ? "não preservado" : Safe(value);

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "Indisponível"
        : value.Replace("\r", " ").Replace("\n", " ").Replace("`", "'").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Cell(string? value) => Safe(value).Replace("|", "&#124;", StringComparison.Ordinal);
}
