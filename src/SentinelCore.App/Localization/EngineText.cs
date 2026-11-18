using System.Text.RegularExpressions;

namespace SentinelCore.App.Localization;

/// <summary>
/// Traduz apenas na apresentação o texto em português gerado pelos motores com valores interpolados.
/// O registro persistido não muda; texto sem modelo permanece como gravado. Os modelos usam {0}, {1}… para os valores.
/// </summary>
internal static class EngineText
{
    // Modelos independentes de índice de Strings.Entries: acrescentar aqui nunca desloca T####.
    // Valores interpolados que são rótulos fixos dos motores; só traduzidos quando o valor inteiro coincide.
    private static readonly Dictionary<string, string> Terms = new(StringComparer.Ordinal)
    {
        ["leitura"] = "read",
        ["gravação"] = "write",
        ["indisponível"] = "unavailable",
        ["adaptador"] = "adapter",
    };

    private static readonly (string Pt, string En)[] Templates =
    [
        // Desempenho (PerformanceEvidenceAnalyzer)
        ("{0} sem leitura (indisponível)", "{0} without a reading (unavailable)"),
        ("{0} inválida(s)", "{0} invalid"),
        ("{0} sem leitura (indisponível) e {1} inválida(s)", "{0} without a reading (unavailable) and {1} invalid"),
        ("{0} amostra(s) da sessão", "{0} session sample(s)"),
        (" de {0} amostra(s)", " of {0} sample(s)"),
        ("Leitura de {0} indisponível em {1} de {2} amostra(s); regra não avaliada.", "Reading of {0} unavailable in {1} of {2} sample(s); rule not evaluated."),
        ("Nenhuma amostra válida de {0} ({1} inválida(s) de {2}); regra não avaliada.", "No valid {0} sample ({1} invalid of {2}); rule not evaluated."),
        ("Amostras válidas de {0} insuficientes ({1} de {2}); ", "Insufficient valid {0} samples ({1} of {2}); "),
        ("Nenhum intervalo com CPU total ≥ {0}% por {1} s; condição não observada em {2} amostra(s) válida(s).", "No interval with total CPU ≥ {0}% for {1} s; condition not observed in {2} valid sample(s)."),
        ("CPU total sustentada", "Sustained total CPU"),
        ("CPU total permaneceu em ou acima de {0}% por {1} s; observação não confirma causa.", "Total CPU stayed at or above {0}% for {1} s; the observation does not confirm a cause."),
        ("{0} intervalo(s) sustentado(s) observado(s) em {1} amostra(s) válida(s).", "{0} sustained interval(s) observed in {1} valid sample(s)."),
        ("Memória disponível permaneceu acima de {0}%; condição não observada em {1} amostra(s) válida(s).", "Available memory stayed above {0}%; condition not observed in {1} valid sample(s)."),
        ("Memória disponível reduzida", "Reduced available memory"),
        ("Memória disponível permaneceu em ou abaixo de {0}% por {1} s; não confirma vazamento.", "Available memory stayed at or below {0}% for {1} s; this does not confirm a leak."),
        ("{0} intervalo(s) observado(s) em {1} amostra(s) válida(s) de commit.", "{0} interval(s) observed in {1} valid commit sample(s)."),
        ("{0} intervalo(s) observado(s) em {1} amostra(s) válida(s).", "{0} interval(s) observed in {1} valid sample(s)."),
        ("Amostras com margem de commit ≤ {0} GiB insuficientes para sustentação ({1} de {2} amostras satisfazem a pré-condição; mínimo {3}). A pré-condição não foi observada.", "Samples with a commit headroom ≤ {0} GiB are insufficient to sustain the condition ({1} of {2} samples meet the precondition; minimum {3}). The precondition was not observed."),
        ("Uso de commit ≥ {0}% e margem ≤ {1} GiB não coexistiram por {2} s; condição não observada ({3} de {4} amostras válidas com margem baixa).", "Commit use ≥ {0}% and headroom ≤ {1} GiB did not coexist for {2} s; condition not observed ({3} of {4} valid samples with low headroom)."),
        ("Margem de commit reduzida", "Reduced commit headroom"),
        ("Uso de commit em ou acima de {0}% e margem em ou abaixo de {1} GiB por {2} s.", "Commit use at or above {0}% and headroom at or below {1} GiB for {2} s."),
        ("Nenhum processo acompanhado por pelo menos {0} amostras em {1} s ({2} amostra(s) de sessão).", "No process was tracked for at least {0} samples over {1} s ({2} session sample(s))."),
        ("Nenhum processo cresceu acima do percentual e dos bytes mínimos na janela.", "No process grew above the minimum percentage and bytes in the window."),
        ("Crescimento de memória privada", "Private memory growth"),
        ("Crescimento observado de {0} MiB ({1}%) em {2} s; não confirma vazamento.", "Observed growth of {0} MiB ({1}%) over {2} s; this does not confirm a leak."),
        ("{0} processo(s) com crescimento observado.", "{0} process(es) with observed growth."),
        ("Nenhuma leitura de armazenamento disponível; latência não avaliada.", "No storage reading available; latency not evaluated."),
        ("{0} leitura(s) de armazenamento inválida(s); latência não avaliada.", "{0} invalid storage reading(s); latency not evaluated."),
        ("Latência de {0} de disco elevada", "High disk {0} latency"),
        ("Latência de {0} em ou acima de {1} ms por {2} s; não confirma defeito.", "{0} latency at or above {1} ms for {2} s; this does not confirm a defect."),
        ("Latência não informada nas leituras válidas de armazenamento; condição não avaliada.", "Latency not reported in the valid storage readings; condition not evaluated."),
        ("Amostras válidas de latência insuficientes ({0} de {1}); condição não avaliada.", "Insufficient valid latency samples ({0} of {1}); condition not evaluated."),
        ("{0} instância(s) com latência sustentada.", "{0} instance(s) with sustained latency."),
        ("Latência abaixo de {0} ms nas instâncias avaliadas; condição não observada.", "Latency below {0} ms in the evaluated instances; condition not observed."),
        ("Nenhuma leitura de armazenamento disponível; picos de I/O não avaliados.", "No storage reading available; I/O peaks not evaluated."),
        ("{0} leitura(s) de armazenamento inválida(s); picos de I/O não avaliados.", "{0} invalid storage reading(s); I/O peaks not evaluated."),
        ("Pico de I/O", "I/O peak"),
        ("Pico de {0} MB/s, {1}× a mediana, por {2} s; pico não indica gargalo persistente.", "Peak of {0} MB/s, {1}× the median, for {2} s; a peak does not indicate a persistent bottleneck."),
        ("Vazão não informada nas leituras válidas de armazenamento; picos não avaliados.", "Throughput not reported in the valid storage readings; peaks not evaluated."),
        ("Amostras válidas de I/O insuficientes ({0} de {1}); picos não avaliados.", "Insufficient valid I/O samples ({0} of {1}); peaks not evaluated."),
        ("{0} pico(s) de I/O observado(s).", "{0} I/O peak(s) observed."),

        // Rede (NetworkEvidenceAnalyzer)
        ("Crescimento de conexões TCP observado", "TCP connection growth observed"),
        ("As conexões TCP observadas passaram de {0} para {1} ({2}% em {3} s). O crescimento indica alteração do estado observado; não identifica causa nem comprova esgotamento de recursos.", "Observed TCP connections went from {0} to {1} ({2}% in {3} s). The growth indicates a change in the observed state; it does not identify a cause or prove resource exhaustion."),
        ("Crescimento de contagem não distingue novas conexões legítimas de repetição ou acúmulo em TIME_WAIT.", "A count increase does not distinguish new legitimate connections from retries or TIME_WAIT accumulation."),
        ("Amostras ou duração insuficientes: são necessários {0} índices contíguos e {1} s.", "Insufficient samples or duration: {0} contiguous indices and {1} s are required."),
        ("Condição não observada na janela.", "Condition not observed in the window."),
        ("Nenhum processo com identidade verificável foi observado; concentração não avaliável.", "No process with a verifiable identity was observed; concentration cannot be evaluated."),
        ("Concentração de endpoints em um processo", "Endpoint concentration in one process"),
        ("{0} sustentou entre {1} e {2} endpoint(s) por {3} s, alcançando {4}% dos endpoints observados. A concentração é uma observação de distribuição; não caracteriza o processo nem atribui causa.", "{0} sustained between {1} and {2} endpoint(s) for {3} s, reaching {4}% of the observed endpoints. The concentration is a distribution observation; it does not characterize the process or assign a cause."),
        ("Um processo com muitas conexões pode ser legítimo (navegador, servidor local, agente de atualização).", "A process with many connections can be legitimate (browser, local server, update agent)."),
        ("Nenhum processo atingiu {0} endpoints com {1}% de participação por {2} s.", "No process reached {0} endpoints with a {1}% share for {2} s."),
        ("Concentração de conexões TCP em um estado", "TCP connection concentration in one state"),
        ("O estado {0} concentrou entre {1} e {2} conexões ({3}% do TCP) por {4} s. Concentração em um estado não é erro por si só e não comprova esgotamento.", "State {0} concentrated between {1} and {2} connections ({3}% of TCP) for {4} s. Concentration in one state is not an error by itself and does not prove exhaustion."),
        ("TIME_WAIT e CLOSE_WAIT são estados normais do protocolo; concentração apenas descreve o estado observado.", "TIME_WAIT and CLOSE_WAIT are normal protocol states; concentration only describes the observed state."),
        ("Nenhum estado TCP atingiu {0} conexões com {1}% de participação.", "No TCP state reached {0} connections with a {1}% share."),
        ("É necessário sustentar a condição por {0} amostras e {1} s.", "The condition must be sustained for {0} samples and {1} s."),
        ("Intervalo dinâmico de TCP indisponível; pressão sobre portas dinâmicas não avaliável.", "Dynamic TCP port range unavailable; dynamic port pressure cannot be evaluated."),
        ("Nenhuma janela sustentou {0} endpoints na faixa dinâmica com {1}% do TCP por {2} s.", "No window sustained {0} endpoints in the dynamic range with {1}% of TCP for {2} s."),
        ("Uso elevado de portas dinâmicas observado", "High dynamic port usage observed"),
        ("Entre {0} e {1} conexões TCP usaram portas locais dentro do intervalo dinâmico configurado ({2}–{3}, {4} portas), atingindo {5}% do TCP por {6} s. A razão entre endpoints observados e tamanho do intervalo não determina disponibilidade real: reservas, exclusões gerenciadas e tempo de permanência em TIME_WAIT também influenciam.", "Between {0} and {1} TCP connections used local ports inside the configured dynamic range ({2}–{3}, {4} ports), reaching {5}% of TCP for {6} s. The ratio between observed endpoints and range size does not determine real availability: reservations, managed exclusions and time spent in TIME_WAIT also have an influence."),
        ("Contagem elevada na faixa dinâmica não confirma esgotamento de portas nem falha de conexão.", "A high count in the dynamic range does not confirm port exhaustion or a connection failure."),
        ("Uso elevado de portas dinâmicas observado.", "High dynamic port usage observed."),
        ("Aumento de falhas de tentativa TCP", "Increase in TCP connection attempt failures"),
        ("O contador cumulativo de falhas de tentativa TCP cresceu {0} durante {1} s observados. Falhas de tentativa podem ocorrer por indisponibilidade momentânea, recusa ou timeout; o aumento não atribui causa e não confirma esgotamento de portas.", "The cumulative TCP connection attempt failure counter grew by {0} during {1} s of observation. Attempt failures can occur because of momentary unavailability, refusal or timeout; the increase does not assign a cause and does not confirm port exhaustion."),
        ("O contador de 32 bits é cumulativo desde o boot e pode sofrer wrap em máquinas muito ativas.", "The 32-bit counter is cumulative since boot and can wrap on very busy machines."),
        ("O contador de falhas de tentativa regrediu (wrap de 32 bits ou reinício do sistema); o intervalo não foi interpretado como aumento.", "The attempt failure counter went backwards (32-bit wrap or system restart); the interval was not interpreted as an increase."),
        ("São necessárias {0} amostras e {1} s para avaliar falhas de tentativa.", "{0} samples and {1} s are required to evaluate attempt failures."),

        // Gráficos & GPU (GraphicsAnalysis, GraphicsInvestigationRunner)
        ("Versão do driver de vídeo não exposta por esta interface.", "Video driver version not exposed by this interface."),
        ("AdapterRAM não reportado; a VRAM real não é inferida.", "AdapterRAM not reported; the real VRAM is not inferred."),
        ("Resolução ativa não confirmada; o valor não é estimado.", "Active resolution not confirmed; the value is not estimated."),
        ("Versão do driver não exposta por esta interface.", "Driver version not exposed by this interface."),
        ("Mensagem truncada no limite de evidência gráfica ou na coleta de origem; os identificadores técnicos foram preservados.", "Message truncated at the graphics evidence limit or at the source collection; technical identifiers were preserved."),
        ("Evento Display 4101 registra reinício do driver de vídeo; não identifica a causa do tempo limite.", "Display event 4101 records a video driver restart; it does not identify the cause of the timeout."),
        ("Metadados do WER não substituem o dump; nenhum dump foi lido, enviado ou apagado.", "WER metadata does not replace the dump; no dump was read, sent or deleted."),
        ("A presença de um relatório não prova falha de hardware nem identifica a causa.", "The presence of a report does not prove a hardware failure or identify the cause."),
        ("Alterações não avaliadas: a leitura anterior ou a atual estava indisponível, parcial ou truncada.", "Changes not evaluated: the previous or the current reading was unavailable, partial or truncated."),
        ("Nenhuma alteração observada entre as investigações.", "No change observed between the investigations."),
        ("Alteração de driver entre investigações", "Driver change between investigations"),
        ("Alteração de configuração de monitor", "Monitor configuration change"),
        ("Alteração de adaptador entre investigações", "Adapter change between investigations"),
        ("{0} alteração(ões) observada(s) entre a investigação anterior e esta. A alteração é descritiva: não indica melhoria, regressão, causa ou relação com qualquer evento.", "{0} change(s) observed between the previous investigation and this one. The change is descriptive: it does not indicate improvement, regression, a cause or a relation to any event."),
        ("Comparação entre investigações gráficas", "Comparison between graphics investigations"),
        ("Alteração observada não é causa, correção ou prova de instabilidade.", "An observed change is not a cause, a fix or proof of instability."),
        ("Alterações observadas entre investigações.", "Changes observed between investigations."),
        ("Fonte de eventos indisponível; a ausência de TDR não foi observada.", "Event source unavailable; the absence of TDR was not observed."),
        ("Nenhum evento Display 4101 observado na janela. A ausência não prova que não houve reinício do driver em outro período ou que o registro não tenha sido perdido.", "No Display 4101 event observed in the window. The absence does not prove that no driver restart occurred in another period or that the record was not lost."),
        ("Reinício do driver de vídeo registrado pelo Windows", "Video driver restart recorded by Windows"),
        ("O Windows registrou {0} evento(s) Display {1} na janela. Esse é o registro de parada e recuperação do driver de vídeo (TDR). Ele não identifica a causa do tempo limite, não prova defeito físico da GPU e não confirma que uma tela preta específica tenha sido causada por esse evento.", "Windows recorded {0} Display {1} event(s) in the window. This is the video driver stop and recovery record (TDR). It does not identify the cause of the timeout, does not prove a physical GPU defect and does not confirm that a specific black screen was caused by this event."),
        ("Log de Eventos do Windows — provedor Display", "Windows Event Log — Display provider"),
        ("Evento registrado não identifica causa, componente físico ou correlação com tela preta.", "A recorded event does not identify a cause, a physical component or a correlation with a black screen."),
        ("Fonte de metadados WER indisponível; a ausência de LiveKernelEvent não foi observada.", "WER metadata source unavailable; the absence of LiveKernelEvent was not observed."),
        ("Nenhum relatório WER LiveKernelEvent com ProblemCode {0} observado na janela. Ausência de relatório não prova ausência de falha: o WER pode não ter preservado o metadata.", "No WER LiveKernelEvent report with ProblemCode {0} observed in the window. The absence of a report does not prove the absence of a failure: WER may not have kept the metadata."),
        ("O WER preservou {0} relatório(s) LiveKernelEvent com ProblemCode {1} na janela. O código é mantido exatamente como reportado; esta fase não amplia seu significado oficial, não lê dumps e não conclui que exista defeito físico, superaquecimento, problema de VRAM ou instabilidade da GPU.", "WER kept {0} LiveKernelEvent report(s) with ProblemCode {1} in the window. The code is kept exactly as reported; this phase does not extend its official meaning, does not read dumps and does not conclude that there is a physical defect, overheating, a VRAM problem or GPU instability."),
        ("Código do LiveKernelEvent não é diagnóstico; nenhum dump foi lido.", "The LiveKernelEvent code is not a diagnosis; no dump was read."),
        ("Relatório ausente ou não preservado não prova ausência do evento.", "A missing or unpreserved report does not prove the absence of the event."),
        ("Relatórios LiveKernelEvent {0} observados na janela.", "LiveKernelEvent {0} reports observed in the window."),
        ("Nenhum adaptador gráfico enumerado; a fonte de GPU pode estar indisponível ou parcial.", "No graphics adapter enumerated; the GPU source may be unavailable or partial."),
        ("Um adaptador gráfico enumerado.", "One graphics adapter enumerated."),
        ("Múltiplos adaptadores gráficos enumerados", "Multiple graphics adapters enumerated"),
        ("{0} adaptadores gráficos foram enumerados, incluindo adaptadores virtuais ou secundários quando reportados pelo Windows. A quantidade não é um defeito e não indica qual adaptador está associado a um evento ou a uma saída de vídeo.", "{0} graphics adapters were enumerated, including virtual or secondary adapters when reported by Windows. The count is not a defect and does not indicate which adapter is associated with an event or a video output."),
        ("Inventário de GPU (WMI)", "GPU inventory (WMI)"),
        ("Inventário de GPU (WMI/ConfigMgr)", "GPU inventory (WMI/ConfigMgr)"),
        ("A enumeração não confirma uso efetivo, saúde ou integridade de cada adaptador.", "The enumeration does not confirm actual use, health or integrity of each adapter."),
        ("Múltiplos adaptadores observados.", "Multiple adapters observed."),
        ("Nenhum adaptador disponível para avaliar estado reportado.", "No adapter available to evaluate the reported state."),
        ("Nenhum código de erro de configuração ou estado de erro reportado para os adaptadores observados. Isso não atesta integridade do hardware.", "No configuration error code or error state reported for the observed adapters. This does not attest hardware integrity."),
        ("Adaptador gráfico com estado de erro reportado", "Graphics adapter with a reported error state"),
        ("{0} adaptador(es) reportaram código de erro de configuração ou estado de erro. Valores observados: ", "{0} adapter(s) reported a configuration error code or an error state. Observed values: "),
        (" — código ", " — code "),
        (", estado ", ", state "),
        (". O estado é o reportado pelo Windows; não substitui teste de hardware e não identifica a causa do erro.", ". The state is the one reported by Windows; it does not replace a hardware test and does not identify the cause of the error."),
        ("Código de erro reportado não é diagnóstico definitivo nem prova defeito físico.", "A reported error code is not a definitive diagnosis or proof of a physical defect."),
        ("Estado de erro reportado por adaptador.", "Error state reported by adapter."),
        ("Nenhum monitor ativo reportado pelo DisplayConfig. Saídas desconectadas ou não mapeadas não são inferidas como defeito.", "No active monitor reported by DisplayConfig. Disconnected or unmapped outputs are not inferred as a defect."),
        ("Topologia de exibição observada", "Observed display topology"),
        ("{0} monitor(es) ativo(s) foram reportados; {1} com resolução ativa confirmada. HDR, G-SYNC/VRR e frequência são apenas os valores reportados por DisplayConfig/WMI; esta fase não infere suporte além do reportado e não altera nenhuma configuração de exibição.", "{0} active monitor(s) were reported; {1} with a confirmed active resolution. HDR, G-SYNC/VRR and refresh rate are only the values reported by DisplayConfig/WMI; this phase does not infer support beyond what is reported and does not change any display setting."),
        ("Topologia observada no instante da coleta; monitores desconectados podem não ser enumerados.", "Topology observed at the moment of collection; disconnected monitors may not be enumerated."),
        ("Fonte de metadados WER indisponível; a ausência de relatórios não foi observada.", "WER metadata source unavailable; the absence of reports was not observed."),
        ("Nenhum relatório WER classificado como gráfico na janela. Ausência de relatório não prova ausência de falha.", "No WER report classified as graphics in the window. The absence of a report does not prove the absence of a failure."),
        ("Metadados gráficos preservados pelo Windows Error Reporting", "Graphics metadata preserved by Windows Error Reporting"),
        ("{0} relatório(s) WER com classificação gráfica foram preservados na janela. Somente metadados são lidos; nenhum dump é aberto. A presença do relatório não confirma falha de hardware, não identifica a causa e não demonstra que um aplicativo específico tenha causado a falha.", "{0} WER report(s) with a graphics classification were preserved in the window. Only metadata is read; no dump is opened. The presence of the report does not confirm a hardware failure, does not identify the cause and does not show that a specific application caused the failure."),
        ("Metadados podem faltar ou estar incompletos; ausência não prova ausência de falha.", "Metadata may be missing or incomplete; absence does not prove the absence of a failure."),
        ("Relatórios WER gráficos observados.", "Graphics WER reports observed."),
        ("Não há investigação gráfica anterior para comparar.", "There is no previous graphics investigation to compare."),
        ("Identidade gráfica duplicada; a comparação não foi executada.", "Duplicate graphics identity; the comparison was not run."),
        ("Adaptadores: leitura anterior ou atual parcial/indisponível ou truncada; adições e remoções não foram avaliadas.", "Adapters: previous or current reading partial/unavailable or truncated; additions and removals were not evaluated."),
        ("Monitores: leitura anterior ou atual parcial/indisponível ou truncada; adições e remoções não foram avaliadas.", "Monitors: previous or current reading partial/unavailable or truncated; additions and removals were not evaluated."),
        ("Drivers: leitura anterior ou atual parcial/indisponível; alterações não foram avaliadas.", "Drivers: previous or current reading partial/unavailable; changes were not evaluated."),
        ("A fonte não foi confirmada nas etapas anteriores da auditoria.", "The source was not confirmed in the previous audit steps."),
        ("A leitura de eventos e metadados WER não foi concluída nesta execução.", "The reading of events and WER metadata was not completed in this run."),
        ("Qualidade {0} {1}: {2} Relatórios com horário efetivo fora do período não foram incluídos na evidência gráfica.", "Quality {0} {1}: {2} Reports with an effective time outside the period were not included in the graphics evidence."),
        ("Investigação cancelada pelo usuário; as evidências coletadas foram preservadas.", "Investigation canceled by the user; the collected evidence was preserved."),
        ("Adaptadores gráficos indisponíveis ou parciais; a ausência de um adaptador não indica remoção.", "Graphics adapters unavailable or partial; the absence of an adapter does not indicate removal."),
        ("Monitores indisponíveis ou parciais; a ausência de um monitor não indica remoção.", "Monitors unavailable or partial; the absence of a monitor does not indicate removal."),
        ("Drivers gráficos indisponíveis ou parciais; a ausência de um driver não indica remoção.", "Graphics drivers unavailable or partial; the absence of a driver does not indicate removal."),
        ("Eventos e WER indisponíveis ou parciais; ausência de evento não prova ausência de falha.", "Events and WER unavailable or partial; the absence of an event does not prove the absence of a failure."),
        ("Limites internos de volume foram atingidos; a cobertura é parcial e nada foi omitido em silêncio.", "Internal volume limits were reached; coverage is partial and nothing was silently omitted."),
        ("Nenhuma evidência gráfica observada na janela; isso não prova que não houve falha fora da janela ou que o sistema não deixou de registrar o evento.", "No graphics evidence observed in the window; this does not prove that there was no failure outside the window or that the system did not fail to record the event."),
        ("Coleta cancelada pelo usuário.", "Collection canceled by the user."),
        ("Falha de coleta ({0}): {1}", "Collection failure ({0}): {1}"),
        ("Limite interno de itens atingido; a lista não é exaustiva.", "Internal item limit reached; the list is not exhaustive."),
        ("Limite interno de evidências gráficas atingido; a amostra não é exaustiva.", "Internal graphics evidence limit reached; the sample is not exhaustive."),

        // Limitações padrão dos modelos (Gráficos, Desempenho, qualidade de eventos, Monitoramento)
        ("Temperatura, potência, frequência, utilização global e VRAM real da GPU não são coletadas: o Windows não as expõe de forma confiável pelas interfaces genéricas usadas aqui. O valor AdapterRAM é o reportado pelo driver, sujeito a limite de 32 bits, e não substitui a VRAM física.", "Temperature, power, clock speed, overall utilization and the real GPU VRAM are not collected: Windows does not expose them reliably through the generic interfaces used here. The AdapterRAM value is the one reported by the driver, subject to a 32-bit limit, and does not replace the physical VRAM."),
        ("Evento gráfico, LiveKernelEvent ou alteração de driver não identifica causa, defeito físico ou correlação com uma tela preta. A causalidade permanece não avaliada nesta fase.", "A graphics event, LiveKernelEvent or driver change does not identify a cause, a physical defect or a correlation with a black screen. Causality remains not assessed in this phase."),
        ("Nenhuma correção, reparo, instalação, atualização ou reversão foi executada; nenhuma elevação foi solicitada.", "No fix, repair, installation, update or rollback was performed; no elevation was requested."),
        ("Mensagem truncada; o conteúdo apresentado é parcial e não substitui a mensagem original.", "Message truncated; the content shown is partial and does not replace the original message."),
        ("{0} mensagem indisponível (truncada na origem)", "{0} message unavailable (truncated at the source)"),
        ("Cobertura derivada do formato 1: oportunidades perdidas estimadas pela diferença entre amostras esperadas e coletadas; ausência de registro de limitação não amplia a cobertura.", "Coverage derived from format 1: missed opportunities estimated from the difference between expected and collected samples; the absence of a recorded limitation does not widen the coverage."),
        ("O intervalo acumulado desde o início da sessão foi usado como denominador das taxas, subnotificando CPU total, CPU por processo e I/O.", "The interval accumulated since the start of the session was used as the rate denominator, under-reporting total CPU, per-process CPU and I/O."),
        ("Horário efetivo fora do período investigado.", "Effective time outside the investigated period."),
        ("Apenas 20 de {0} relatórios afetados são listados como evidência.", "Only 20 of {0} affected reports are listed as evidence."),
        ("{0} de {1} relatórios WER distintos possuem horário efetivo fora do período investigado; as contagens de relatórios podem estar superestimadas. Os eventos do log não são invalidados por esta regra.", "{0} of {1} distinct WER reports have an effective time outside the investigated period; report counts may be overestimated. Log events are not invalidated by this rule."),
        ("Nenhum dos {0} relatórios WER distintos possui horário efetivo fora do período investigado.", "None of the {0} distinct WER reports has an effective time outside the investigated period."),
        ("{0} de {1} relatórios WER não possuem horário efetivo; a janela não pôde ser confirmada.", "{0} of {1} WER reports have no effective time; the window could not be confirmed."),
        ("A investigação não contém relatórios WER para avaliar.", "The investigation contains no WER reports to evaluate."),
        ("O monitoramento só é executado durante uma sessão explicitamente autorizada pelo usuário; nenhuma observação é iniciada automaticamente e nenhuma sessão prossegue após o encerramento.", "Monitoring runs only during a session explicitly authorized by the user; no observation starts automatically and no session continues after it ends."),
        ("Nenhum serviço, tarefa agendada, driver, processo residente ou alteração do Windows é criado; a observação termina com a sessão ou com o fechamento do aplicativo e não há coleta em segundo plano.", "No service, scheduled task, driver, resident process or Windows change is created; the observation ends with the session or when the application closes, and there is no background collection."),
        ("Incidente registrado manualmente pelo usuário: o momento do registro não é o momento comprovado do início da ocorrência e não equivale a um evento do Windows.", "Incident recorded manually by the user: the time of the record is not the proven start of the occurrence and is not equivalent to a Windows event."),
        ("O monitoramento preserva evidências e não declara causa; proximidade temporal, amostra pontual ou relato do usuário não demonstram defeito, diagnóstico ou relação causal.", "Monitoring preserves evidence and does not declare a cause; temporal proximity, a single sample or a user report do not show a defect, a diagnosis or a causal relation."),
        ("O orçamento de recursos limita a observação; oportunidades perdidas, limites e cobertura parcial são declarados e nunca omitidos em silêncio.", "The resource budget limits the observation; missed opportunities, limits and partial coverage are declared and never silently omitted."),
        ("Sessão interrompida: o registro final não existe; o estado foi derivado somente dos segmentos confirmados, não foi gravado e não equivale a conclusão normal.", "Interrupted session: the final record does not exist; the state was derived only from the confirmed segments, was not saved and is not equivalent to a normal completion."),
        ("Eventos observados pelo monitoramento mantêm a identidade do Log de Eventos (canal e RecordId) e podem ser consolidados com outras investigações sem contagem dupla.", "Events observed by monitoring keep the Event Log identity (channel and RecordId) and can be consolidated with other investigations without double counting."),
        ("A fonte de rede registra somente contagens agregadas; endereços IP, portas remotas detalhadas, tráfego e conteúdo de pacotes não são preservados por esta observação.", "The network source records aggregate counts only; IP addresses, detailed remote ports, traffic and packet contents are not preserved by this observation."),
        ("Sessão concluída pelo aplicativo; nenhum segmento ficou órfão.", "Session completed by the application; no segment was left orphaned."),

        // Limitações de sessão (Desempenho e Rede)
        ("Sessão cancelada pelo usuário após {0} amostra(s).", "Session canceled by the user after {0} sample(s)."),
        ("Limite de {0} amostras atingido durante a sessão.", "Limit of {0} samples reached during the session."),
        ("Limite de {0} amostras atingido antes do fim da janela.", "Limit of {0} samples reached before the end of the window."),
        ("{0} oportunidade(s) de amostragem perdida(s): a coleta excedeu o intervalo e as amostras seguintes mantiveram a cadência nominal, sem rajada de recuperação.", "{0} sampling opportunity(ies) missed: collection exceeded the interval and the following samples kept the nominal cadence, with no catch-up burst."),
        ("{0} oportunidade(s) de amostragem perdida(s): a coleta excedeu o intervalo e a cadência nominal foi mantida, sem rajada de recuperação.", "{0} sampling opportunity(ies) missed: collection exceeded the interval and the nominal cadence was kept, with no catch-up burst."),
        ("CPU indisponível; percentuais, fila e regras de CPU não avaliados.", "CPU unavailable; percentages, queue and CPU rules not evaluated."),
        ("Fonte {0} indisponível nesta sessão.", "Source {0} unavailable in this session."),
        ("Fonte {0}: limite interno de acompanhamento atingido; dados parciais.", "Source {0}: internal tracking limit reached; partial data."),
        ("Fonte {0} indisponível; a cobertura da sessão é parcial.", "Source {0} unavailable; session coverage is partial."),
        ("Nenhuma amostra coletada; sessão sem estatísticas.", "No sample collected; session without statistics."),
        ("Nenhuma amostra coletada; resumo indisponível.", "No sample collected; summary unavailable."),
        ("Detalhes de endpoint limitados a {0} linhas da última amostra.", "Endpoint details limited to {0} lines of the last sample."),
        ("A investigação de eventos associada não contém eventos Tcpip 4231/4266.", "The associated event investigation contains no Tcpip 4231/4266 events."),
        ("O sistema registrou eventos Tcpip 4231 e/ou 4266 na investigação associada; a contagem não identifica a causa, não comprova esgotamento na janela desta sessão nem atribui causa.", "The system recorded Tcpip 4231 and/or 4266 events in the associated investigation; the count does not identify the cause, does not prove exhaustion in this session's window and does not assign a cause."),
        ("Primeira amostra sem conexões TCP; crescimento percentual não é calculável.", "First sample without TCP connections; percentage growth cannot be calculated."),
        ("{0} amostra(s) agregada(s); a última amostra representa o estado final observado, não um histórico contínuo.", "{0} aggregated sample(s); the last sample represents the final observed state, not a continuous history."),
        ("{0} linha(s) duplicada(s) foram contabilizadas uma única vez na amostra final.", "{0} duplicate line(s) were counted only once in the final sample."),

        // Reparos (RepairResultInterpreter, RepairCenter)
        ("Cancelamento solicitado. A interrupção segura da ferramenta oficial não é garantida.", "Cancellation requested. A safe interruption of the official tool is not guaranteed."),
        ("A execução não foi concluída; o resultado permanece indeterminado.", "The run was not completed; the result remains indeterminate."),
        ("Execução interrompida ou não concluída; sucesso não é declarado.", "Run interrupted or not completed; success is not declared."),
        ("A saída foi limitada; o resultado pode estar incompleto.", "The output was limited; the result may be incomplete."),
        ("Saída truncada pelo limite local.", "Output truncated by the local limit."),
        ("A ferramenta não forneceu código de saída verificável.", "The tool did not provide a verifiable exit code."),
        ("Código de saída indisponível; resultado não avaliado.", "Exit code unavailable; result not evaluated."),
        ("Verificação independente posterior não realizada; a saída é evidência da ferramenta.", "No independent later verification was performed; the output is the tool's own evidence."),
        ("Outro reparo está ativo ou deixou um bloqueio pendente.", "Another repair is active or left a pending lock."),
        ("O broker recusou a solicitação antes de iniciar a ferramenta.", "The broker rejected the request before starting the tool."),
        ("Operação cancelada antes de iniciar a ferramenta.", "Operation canceled before starting the tool."),
        ("O Windows negou o início da ferramenta.", "Windows denied starting the tool."),
        ("Não foi possível iniciar a ferramenta.", "The tool could not be started."),
        ("Sem confirmação da conclusão; o efeito permanece indeterminado.", "No confirmation of completion; the effect remains indeterminate."),
        ("A ferramenta solicitou reinicialização para completar a operação.", "The tool requested a restart to complete the operation."),
        ("O processo terminou, mas a saída capturada é incompleta.", "The process ended, but the captured output is incomplete."),
        ("CHKDSK relatou limpeza ou limpeza omitida. O código não determina necessidade de reinicialização.", "CHKDSK reported cleanup or skipped cleanup. The code does not determine whether a restart is needed."),
        ("A verificação relatou sistema de arquivos incompatível.", "The check reported an incompatible file system."),
        ("A ferramenta relatou que não conseguiu abrir o volume.", "The tool reported that it could not open the volume."),
        ("Código de saída sem interpretação conclusiva: {0}.", "Exit code without a conclusive interpretation: {0}."),
        ("A ferramenta relatou arquivos que não puderam ser reparados.", "The tool reported files that could not be repaired."),
        ("A ferramenta relatou reparação concluída.", "The tool reported that the repair was completed."),
        ("A ferramenta relatou violações de integridade.", "The tool reported integrity violations."),
        ("Processo concluído com código zero e sem evidência conclusiva reconhecida.", "Process finished with exit code zero and no recognized conclusive evidence."),
        ("Acesso negado pela ferramenta; a execução real exigiria elevação.", "Access denied by the tool; a real run would require elevation."),
        ("A ferramenta relatou operação não suportada neste sistema.", "The tool reported an operation not supported on this system."),
        ("Ferramenta não catalogada.", "Tool not in the catalog."),
        ("A consulta ao ambiente de recuperação não foi concluída.", "The query of the recovery environment was not completed."),
        ("A ferramenta não conseguiu reparar todos os arquivos ou componentes afetados.", "The tool could not repair all affected files or components."),
        ("A ferramenta não pôde executar a operação solicitada.", "The tool could not perform the requested operation."),
        ("A ferramenta não pôde verificar o volume.", "The tool could not check the volume."),
        ("A ferramenta não relatou problemas no volume.", "The tool reported no problems on the volume."),
        ("A ferramenta relatou conclusão da tentativa de reparação da imagem.", "The tool reported that the image repair attempt finished."),
        ("A ferramenta relatou correção de erros no volume.", "The tool reported that errors on the volume were corrected."),
        ("A ferramenta relatou erros no volume durante a verificação.", "The tool reported errors on the volume during the check."),
        ("A ferramenta relatou falha genérica na operação.", "The tool reported a generic operation failure."),
        ("A ferramenta relatou limpeza ou necessidade de reinicialização; confirme o estado do volume.", "The tool reported cleanup or a need to restart; confirm the volume state."),
        ("A ferramenta relatou reparação de arquivos protegidos.", "The tool reported repair of protected files."),
        ("A ferramenta relatou reparação parcial; algumas falhas podem permanecer.", "The tool reported a partial repair; some failures may remain."),
        ("A imagem foi reportada como reparável; corrupção detectada.", "The image was reported as repairable; corruption detected."),
        ("A reparação não encontrou uma fonte de arquivos de origem disponível.", "The repair did not find an available source of original files."),
        ("A verificação não pôde ser concluída pela ferramenta.", "The tool could not complete the check."),
        ("Acesso negado ao consultar o ambiente de recuperação.", "Access denied when querying the recovery environment."),
        ("Código de saída DISM/HRESULT não catalogado: {0}.", "DISM/HRESULT exit code not in the catalog: {0}."),
        ("Código de saída SFC não catalogado: {0}.", "SFC exit code not in the catalog: {0}."),
        ("Código de saída chkdsk não catalogado: {0}.", "chkdsk exit code not in the catalog: {0}."),
        ("Código de saída zero para RestoreHealth; a conclusão não prova que toda corrupção foi corrigida.", "Exit code zero for RestoreHealth; completion does not prove that all corruption was fixed."),
        ("Código de saída zero, sem marcador textual reconhecido; a ausência de corrupção não é conclusiva.", "Exit code zero, with no recognized text marker; the absence of corruption is not conclusive."),
        ("Código de saída zero não prova que toda corrupção foi corrigida.", "Exit code zero does not prove that all corruption was fixed."),
        ("Estado do WinRE relatado pela ferramenta.", "WinRE state reported by the tool."),
        ("Informações do WinRE recuperadas; o ambiente de recuperação aparece desabilitado.", "WinRE information retrieved; the recovery environment appears disabled."),
        ("Informações do WinRE recuperadas; o ambiente de recuperação aparece habilitado.", "WinRE information retrieved; the recovery environment appears enabled."),
        ("Informações do WinRE recuperadas; o estado habilitado/desabilitado não foi reconhecido.", "WinRE information retrieved; the enabled/disabled state was not recognized."),
        ("Nenhuma corrupção do repositório de componentes foi detectada.", "No component store corruption was detected."),
        ("Nenhuma saída registrada.", "No output recorded."),
        ("Nenhuma violação de integridade foi relatada pela ferramenta.", "The tool reported no integrity violations."),
        ("Nenhuma violação de integridade foi relatada pela verificação.", "The check reported no integrity violations."),
        ("O texto contém indicação de acesso negado que diverge da classificação primária.", "The text contains an access-denied indication that differs from the primary classification."),
        ("O texto indica ausência de fonte de reparação.", "The text indicates that no repair source is available."),
        ("O texto indica conclusão com erros ou correção parcial.", "The text indicates completion with errors or a partial fix."),
        ("O texto indica sistema de arquivos incompatível.", "The text indicates an incompatible file system."),
        ("O texto indica volume indisponível para verificação.", "The text indicates a volume unavailable for checking."),
        ("Violações de integridade foram encontradas; nenhuma reparação foi solicitada.", "Integrity violations were found; no repair was requested."),
        ("A execução real do Windows Repair Center está bloqueada.", "Real execution of the Windows Repair Center is blocked."),
        ("A composição real do Windows Repair Center está bloqueada.", "The real composition of the Windows Repair Center is blocked."),
        ("A execução depende da autenticação e revalidação pelo broker.", "Execution depends on authentication and revalidation by the broker."),
        ("Plano não corresponde ao catálogo fechado: ", "Plan does not match the closed catalog: "),
        ("Plano bloqueado não pode ser autorizado.", "A blocked plan cannot be authorized."),
        ("Plano expirado; prepare e confirme uma nova operação.", "Plan expired; prepare and confirm a new operation."),
        ("Operação não autorizável pelo backend.", "Operation cannot be authorized by the backend."),
        ("Plano ou autorização ausente.", "Plan or authorization missing."),
        ("Broker real indisponível para este plano.", "Real broker unavailable for this plan."),
        ("Operação não existe mais no catálogo fechado.", "The operation no longer exists in the closed catalog."),
        ("Operação indisponível no catálogo.", "Operation unavailable in the catalog."),
        ("Risco alto ou não validado recusado no backend.", "High or unvalidated risk refused by the backend."),
        ("Hash do plano não corresponde ao conteúdo apresentado.", "The plan hash does not match the presented content."),
        ("A confirmação vinculada à autorização não corresponde ao plano.", "The confirmation tied to the authorization does not match the plan."),
        ("Execução já registrada ou pendente para este plano; a autorização não pode ser repetida.", "A run is already recorded or pending for this plan; the authorization cannot be repeated."),
        ("Histórico incompleto; revise os avisos antes de autorizar.", "Incomplete history; review the warnings before authorizing."),
        ("Histórico de reparos indisponível; a execução não foi iniciada. ", "Repair history unavailable; the run was not started. "),
        ("Preflight de execução indisponível: ", "Run preflight unavailable: "),
        ("Estado do sistema, do volume ou do executável divergiu desde a preparação.", "The state of the system, the volume or the executable has diverged since preparation."),
        ("Autorização inválida.", "Invalid authorization."),
        ("A intenção não pôde ser registrada; nenhuma execução foi iniciada. ", "The intent could not be recorded; no run was started. "),
        ("A autorização não pôde ser consumida.", "The authorization could not be consumed."),
        ("Intenção e autorização registradas antes de qualquer execução.", "Intent and authorization recorded before any run."),
        ("Preflight revalidado contra o catálogo, o volume e o executável.", "Preflight revalidated against the catalog, the volume and the executable."),
        ("Autorização {0} consumida.", "Authorization {0} consumed."),
        ("Resultado do broker indisponível.", "Broker result unavailable."),
        ("Falha do adaptador: ", "Adapter failure: "),
        ("Adaptador sintético iniciado; nenhum processo real foi executado.", "Synthetic adapter started; no real process was run."),
        ("Início da ferramenta confirmado pelo broker.", "Tool start confirmed by the broker."),
        ("Máquina de estados recusou o resultado: ", "The state machine rejected the result: "),
        (" A gravação não foi concluída: ", " The write was not completed: "),
        ("{0} Saída: {1}/{2} linha(s), {3} byte(s)", "{0} Output: {1}/{2} line(s), {3} byte(s)"),
        ("Estado: {0} · resultado: {1} · confiança: {2}", "State: {0} · result: {1} · confidence: {2}"),
        (" · código {0}", " · code {0}"),
        (" · código indisponível", " · code unavailable"),
        (" · reinicialização indicada", " · restart indicated"),
        (" · fixture sintética", " · synthetic fixture"),
    ];

    internal static IReadOnlyList<(string Pt, string En)> Catalog => Templates;

    private static readonly Lazy<(Regex Pattern, string English)[]> compiled = new(Compile);

    /// <summary>Traduz o texto inteiro ou os trechos que coincidem com modelos conhecidos; o restante permanece como gravado.</summary>
    public static string Translate(string portuguese)
    {
        if (string.IsNullOrEmpty(portuguese)) return portuguese;
        var exact = Strings.English(portuguese);
        if (!ReferenceEquals(exact, portuguese) && exact != portuguese) return exact;
        var text = portuguese;
        foreach (var (pattern, english) in compiled.Value)
        {
            if (text.Length == 0) break;
            text = pattern.Replace(text, match => Fill(english, match));
        }
        return text;
    }

    private static string Fill(string english, Match match) =>
        Regex.Replace(english, @"\{(\d+)\}", hole =>
            match.Groups["g" + hole.Groups[1].Value] is { Success: true } group ? Value(group.Value) : hole.Value);

    private static string Value(string value)
    {
        if (Terms.TryGetValue(value, out var term)) return term;
        var exact = Strings.English(value);
        return exact;
    }

    private static (Regex Pattern, string English)[] Compile() => Templates
        .OrderByDescending(template => template.Pt.Length)
        .Select(template => (Build(template.Pt), template.En))
        .ToArray();

    // Valor no início: uma palavra; no meio: até o próximo trecho literal; no fim: até o fim da linha.
    private static Regex Build(string portuguese)
    {
        var parts = Regex.Split(portuguese, @"(\{\d+\})");
        var pattern = new System.Text.StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) continue;
            if (Regex.Match(part, @"^\{(\d+)\}$") is { Success: true } hole)
            {
                var name = "g" + hole.Groups[1].Value;
                var atEnd = parts.Skip(i + 1).All(rest => rest.Length == 0);
                var atStart = pattern.Length == 0;
                pattern.Append(atStart ? $"(?<{name}>\\S{{1,60}})" : atEnd ? $"(?<{name}>[^\\r\\n]+)" : $"(?<{name}>[^\\r\\n]{{1,160}}?)");
            }
            else pattern.Append(Regex.Escape(part));
        }
        return new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
