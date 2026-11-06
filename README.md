# SentinelCore AI

**T4B.3 e T4B — concluídas no escopo sintético documentado (06/11/2026).** Central de Recuperação medida com 100, 1.000 e 5.000 operações sintéticas
(leitura de 1.000 operações: 25 s → 5 s; 5.000 operações: ≈ 22 s com cache quente; cancelamento < 1 ms); 10.000 operações **não** medidas
(impedimento de tempo documentado). Host WPF isolado com 35 verificações PASS, publish inspecionado (sem worker, runner, testes ou
assemblies nativos), cinco gates fechados e `RecordEnvelope.Probe` avaliado. **312 casos Synthetic aprovados em Debug e Release**; 1.244
Unclassified e 6 WindowsIntegration não executados. Revisões A/B por subagentes distintos, sem bloqueantes. **Não é homologação
operacional**: nenhuma execução real no Windows foi feita. [Relatório T4B.3](docs/validation/2026-11-05-t4b3-final-consolidation.md).

[Revisão final para versionamento](docs/validation/2026-11-06-t4b-final-versioning-review.md): matriz alinhada, bytes auditados
preservados em checkout e auxiliares restritos a fixtures temporárias próprias; sem nova medição de desempenho.

**T4B.1-S:** subconjunto de 270 casos revalidado em Debug e Release; cobertura global e validação real
continuam pendentes. [Evidências e limites](docs/validation/2026-11-05-t4b1s-isolation-validation.md).

**Cobertura crítica parcial:** identificadas leituras reais indiretas em uma rodada de testes; casos afetados
foram excluídos. [Incidente, cobertura por módulo e pendências](docs/validation/2026-11-05-t4b1-critical-coverage.md).

**Atualização T4B.1:** 146 casos Synthetic aprovados; a cobertura global permanece pendente.
[Runner e evidências](docs/validation/2026-11-05-t4b1-runner-and-coverage.md).

**T4B.1 parcial:** 48 casos auditados na seleção, seis de integração identificados e 1.462 ainda não
classificados. [Matriz e limitações](docs/validation/2026-11-05-t4b1-isolation-progress.md).

**Histórico T4B.1 — implementação então parcial.** O runner `./tests/run-synthetic.ps1 -Configuration Debug`
seleciona somente os testes explicitamente auditados; use `Release` para a outra configuração. A classificação
da suíte restante continua pendente; os benchmarks, a validação WPF e as revisões foram concluídos no escopo da T4B.3.
[Estado e limitações](docs/validation/2026-11-05-reliability-hardening-t4b-progress.md).

**T4A — Central de Recuperação, 04/11/2026.** Navegação própria com histórico unificado de Tweaks T2A/T2B,
Debloat T3, remoção controlada T3C e reparos R1; painel, pesquisa/filtros, detalhes, eventos, conflitos,
intervenção e integridade. A central da distribuição é somente leitura. Recuperação de Tweaks é exercitada
somente em host sintético pelo RecoveryService existente; aplicativos e reparos não recebem rollback equivalente.
Fontes incompletas não viram zero; origem histórica desconhecida não é presumida.

SQLite continua no esquema **12**, sem migração. T3C: **implementação concluída; execução real bloqueada e validação
prática pendente**. Validação real não iniciada. [Relatório T4A](docs/validation/2026-11-04-recovery-center-t4a.md).
Verificação T4A: **1.510/1.510 testes selecionados em Debug e Release**, 110 casos novos, builds **0 avisos/0 erros**
e **35 verificações WPF**. Quatro testes legados com consultas reais foram excluídos por filtro, sem execução.
Depois foi identificado um quinto teste com leitura do processo real entre os selecionados; a rodada T4A não comprova isolamento integral.
As contagens abaixo descrevem entregas anteriores; os resultados vigentes T4A estão no relatório.

**Atualização de 04/11/2026 — T3 implementada em modo sintético.** A página **Personalização & Debloat**
inclui catálogo documental e inventário sintético apresentados separadamente, busca/filtros, seleção individual,
plano, confirmação, simulação, histórico e informações de reinstalação. Nenhum aplicativo real é consultado,
removido ou reinstalado. O catálogo documental tem 162 entradas; cinco fixtures são exercitáveis, incluindo
Bloco de Notas com revisão média. Entradas do catálogo sem observação aparecem como
**“Não verificado — inventário real indisponível”**, nunca como ausentes. Execução real permanece indisponível
por gate independente e incondicional. Reinstalação não é rollback.

SQLite atual: **12** (migração aditiva 11→12). Suíte registrada na T3: **1247/1247 em Debug e Release**, builds 0/0.
Histórico Debloat separado em `memory/debloat` e `reports/debloat`, no mesmo banco. Relatório completo:
[T3 — validação e limites](docs/validation/2026-11-04-debloat-manager-t3.md). As contagens anteriores em
seções datadas descrevem suas respectivas etapas. A T4A sucede esta entrega; validação real não iniciada.

A [T0 revisada](docs/planning/TWEAKS_DEBLOAT_T0_REVALIDATION_2026-11-04.md) preserva o histórico e registra
273 fichas com riscos 49 BAIXO, 37 MÉDIO, 33 ALTO e 154 NÃO VALIDADO, além de shortlist de 13 escolhas.
Os [avisos de terceiros](THIRD-PARTY-NOTICES.md) acompanham a distribuição.

Diagnóstico profundo do Windows, baseado em evidências. Aplicativo desktop WPF, C# e .NET 10 LTS.
As fases 0 a 5 entregam auditoria básica, inventário de hardware em onze categorias, investigação de eventos e
metadados de relatórios de falha do Windows, sessões de desempenho com métricas de CPU, memória, processos e
armazenamento, diagnóstico de rede e gráfico, correlação determinística entre as evidências históricas, sessões
autorizadas de monitoramento com registro manual de incidentes, histórico local, memória Markdown e comparação entre
snapshots.
O produto funciona offline após a publicação. O diagnóstico não executa reparos, otimizações, correções ou comandos
de IA. A única escrita no Windows é a T2B: nove ajustes visuais em HKCU, com catálogo fechado, backup, autorização e
recuperação; na distribuição padrão essa escrita permanece desabilitada até a validação real. O **R1 — Windows Repair
Center** adiciona a aba de ferramentas oficiais de reparo, com catálogo fechado e execução real bloqueada; o fluxo é
demonstrado somente por fixtures sintéticas explicitamente marcadas.

## Estado

Fase 3.3A (Network Diagnostics) encerrada em 01/11/2026 como **APROVADA COM RESSALVAS**. A Fase 3.3B
(Graphics Diagnostics) foi implementada em `0.3.0-preview.6` e o quality gate independente final foi
**APROVADO COM RESSALVAS**. A Fase 3.4 (Evidence Correlation Engine) foi implementada em `0.3.0-preview.7`; o
fechamento do quality gate em 02/11/2026 manteve **APROVADA COM RESSALVAS** após corrigir o cancelamento
pós-leitura e a sobredeclaração de cobertura. A correlação herda a parcialidade dos registros de origem, e a
execução real resulta em cobertura parcial. O parecer está em [validação](docs/VALIDATION.md). Os históricos
anteriores permanecem preservados e nenhum registro foi reescrito.
Personalização & Debloat tem a T0 concluída como auditoria documental, a **T1 — Safety & Recovery Core**
implementada como infraestrutura de segurança, autorização, journal, backup e recuperação, a **T2A — First
Controlled Windows Tweaks** implementada com três operações reais em HKCU e a **T2B — Windows Tweaks Expansion**
com seis operações adicionais no mesmo catálogo fechado. A aba **Personalização** mostra os nove cartões. A execução
real está desabilitada na distribuição; a validação real foi adiada por decisão de projeto. A T3 sintética e a
T4A estão implementadas; T4B foi concluída no escopo sintético documentado. A validação operacional permanece futura.

A Fase 5 (Persistent Monitoring) foi implementada em `0.3.0-preview.8`: sessões de 5, 15 ou 30 minutos, uma por vez,
somente após autorização explícita, com eventos incrementais, CPU/memória nativas, rede sem endereços e gráficos
derivados dos eventos. Cada sessão grava checkpoints imutáveis, registro final, incidentes manuais e orçamento de
recursos; uma sessão interrompida é reconhecida pelos checkpoints sem ser gravada como concluída. Não há serviço,
tarefa agendada ou observação após o fechamento. O histórico continua disponível e a correlação passou a oferecer a
fonte **Monitoramento** de forma opcional, sem duplicar eventos.

A **T1 — Safety & Recovery Core** foi implementada na sequência, ainda em `0.3.0-preview.8`: catálogo fechado com
operações sintéticas, preflight, simulação sem efeitos, backup do estado observado, autorização vinculada e de uso
único, journal imutável, execução sintética, verificação e recuperação com conflito. O schema SQLite passou a 10
com migração aditiva 9→10 e continua reconstruível dos Markdowns.

A **T2B — Windows Tweaks Expansion** levou o mesmo catálogo fechado (`t2b-hkcu-1`) a nove operações, ainda sem
elevação e somente em HKCU: mostrar arquivos e pastas ocultos (`Hidden=1`), ocultar o botão Visualizar Tarefas
(`ShowTaskViewButton=0`) e os quatro modos de pesquisa da barra de tarefas (`SearchboxTaskbarMode` 0–3, com
`SC-TW-TSK-02`, `SC-TW-TSK-03`, `SC-TW-TSK-05` e `SC-TW-TSK-04`). Os sete valores permitidos continuam validados
em duas camadas (`HkcuCatalogGuard` e `HkcuTargetPolicy`), com backup, journal, verificação e rollback pelo estado
observado. A pesquisa usa um único controle mutuamente exclusivo; selecionar um modo apenas prepara o plano. Os
cartões são agrupados em Explorador de Arquivos, Barra de tarefas e Aparência. A escrita real permanece
desabilitada na distribuição (`RealDisabled`): o executor HKCU está compilado, mas o gate falso e a composição com
`DisabledOperationExecutor` impedem seu uso. `--enable-real-tweaks`
só tem efeito em um build experimental de validação (`scripts/publish-validation.ps1`, nunca distribuído),
reservado à futura validação real. Nenhuma escrita real foi executada no computador de desenvolvimento.
T3 foi implementada em modo sintético em 04/11/2026. A T4A acrescenta a central unificada; T4B está concluída no escopo sintético documentado.

A **R1 — Windows Repair Center** foi implementada na sequência, ainda em `0.3.0-preview.8`, como módulo independente
do Tweaks Manager: catálogo fechado para SFC (`/verifyonly`, `/scannow`), DISM (`CheckHealth`, `ScanHealth`,
`RestoreHealth`), CHKDSK (`<volume>`, `<volume> /scan`) e WinRE (`/info`), com executáveis oficiais resolvidos somente
em `System32` e argumentos fixos. A aba **Reparos do Windows** separa diagnóstico de reparação, exige confirmação
digitada, registra a intenção antes do efeito, persiste intenção/registro Markdown imutáveis, relatório, projeção
`REPAIRS.md` e SQLite (migração R1 10→11; esquema atual 12). A execução disponível é exclusivamente sintética, com
fixtures marcadas; nenhum SFC, DISM, CHKDSK ou `reagentc` real foi executado, nenhuma elevação foi solicitada e
nenhuma configuração do Windows foi alterada. A validação real permanece pendente. Detalhes em
[validação R1](docs/validation/2026-11-04-windows-repair-center-r1.md).

Versão 0.3.0-preview.7 — Evidence Correlation Engine. A aba **Correlação de evidências** reutiliza somente
registros históricos já persistidos (auditorias, hardware, eventos/WER, desempenho, rede e gráficos) e produz
uma linha do tempo, relações determinísticas por proximidade temporal e por identidade técnica, deduplicação entre
módulos e cobertura explícita, que herda a parcialidade dos registros de origem. Não há coleta nova, IA, telemetria
ou alteração do Windows. Cada relação declara
regra e versão, evidências participantes, janela, justificativa e limitações; a causalidade permanece não avaliada.
Correlação não é causalidade: proximidade temporal ou identidade compartilhada não demonstram causa, e a ausência
de relação não prova ausência de problema.


Versão 0.3.0-preview.6 — Graphics Diagnostics. A aba **Gráficos & GPU** investiga adaptadores, monitores,
drivers, eventos gráficos, TDR Display 4101 e metadados WER LiveKernelEvent 117/141/142, além de alterações
entre investigações. A leitura reutiliza os coletores existentes de GPU/monitor/driver e de eventos/WER; não há
segunda consulta WMI paralela. Temperatura, potência, frequência, utilização global e VRAM real não são
inventadas: somente eventos, WER, drivers, estado de exibição e o `AdapterRAM` reportado pelo driver são
preservados. TDR, LiveKernelEvent, mudança de driver ou falha de aplicativo não são apresentados como causa,
defeito físico, superaquecimento ou problema de VRAM. A causalidade permanece não avaliada. A correlação da Fase 3.4
usa esses registros sem alterar a classificação gráfica.
Consulte [validação](docs/VALIDATION.md), [arquitetura](docs/ARCHITECTURE.md) e [segurança](docs/SECURITY.md).

Versão 0.3.0-preview.3 — Performance Intelligence com endurecimento. A aba **Desempenho** executa sessões de 30
segundos, 1 minuto ou 3 minutos, uma por vez, acionadas pelo usuário. Cada amostra de 1 segundo registra CPU
total/usuário/kernel, memória física e comprometida, até 20 processos acompanhados por identidade (PID + hora de
início) e até 12 instâncias de armazenamento. A sessão grava duração medida, **execução** e **cobertura** em eixos
separados, limitações, estatísticas (média, mediana, P95, P99, tendência), até seis regras determinísticas de
evidência e a auto-sobrecarga do próprio SentinelCore AI. Uma execução concluída pode ter cobertura parcial, por
exemplo pelo limite de processos, sem ser reclassificada como falha. Sessões históricas afetadas pelo defeito de
intervalo conhecido são sinalizadas e têm comparação quantitativa bloqueada, sem reescrever os registros originais.
Observação não é diagnóstico, não atribui causa e não testa integridade do hardware.

A aba **Estabilidade e Eventos** lê, por ação do usuário, os canais do Log de Eventos do Windows e os metadados dos
repositórios do Windows Error Reporting (WER). Cada investigação registra período, status por fonte, canais
consultados, duração e limitações. Eventos são identificados por canal e RecordId; relatórios WER, por ReportId
dentro da origem. Contagens limitadas ou parciais nunca são apresentadas como totais, e a conclusão é evidência
identificada com causa ainda indeterminada — não há diagnóstico automático de peça defeituosa. A qualidade de dados
da fase 3.1 (`EVT-WER-WINDOW` 1.0) é derivada e registrada em `QUALITY.md`, sem reescrever as investigações.

A aba Hardware consulta processador, memória, placa-mãe/BIOS, GPU, monitores,
armazenamento, rede, áudio, USB/periféricos, drivers e outros dispositivos por WMI local, SetupAPI/CFGMGR32 e DisplayConfig.
Cada snapshot registra status por coletor, fonte, duração e limitações; snapshots são preservados, recarregados e comparáveis.
Resultados parciais, falhas, cancelamentos e dados indisponíveis são explícitos. Consulte [validação](docs/VALIDATION.md).
Inventário não é auditoria completa; estados OK/Healthy reportados não garantem integridade física.

## Requisitos

- Windows x64 com suporte ao .NET 10 e WPF; validação nesta máquina Windows 11 Enterprise.
- Usuário comum; manifesto `asInvoker`, sem solicitação de administrador.
- Desenvolvimento: SDK .NET 10.0.403 (global.json), NuGet acessível para restore inicial.
- Execução do pacote self-contained: não exige runtime .NET instalado separadamente.

O SDK desta sessão foi autorizado e extraído exclusivamente em `.tools/dotnet`, com SHA-512 oficial verificado.
Não houve instalação global. `scripts/dotnet.ps1` ajusta variáveis apenas no processo e usa caches dentro de `.tools`.
Esse script não baixa nem instala SDKs automaticamente. Em outra máquina, disponibilize o SDK nessa pasta
ou execute os comandos diretamente com `dotnet` e um SDK compatível já instalado.

## Compilar e testar

Execute no PowerShell, a partir da raiz:

```powershell
.\scripts\dotnet.ps1 restore SentinelCore.sln --locked-mode
.\scripts\dotnet.ps1 build SentinelCore.sln -c Debug --no-restore
.\scripts\dotnet.ps1 build SentinelCore.sln -c Release --no-restore
.\scripts\dotnet.ps1 test SentinelCore.sln -c Release --no-build --logger trx --results-directory artifacts/test-results
```

Os testes de persistência usam diretórios temporários isolados e dados fictícios. A suíte inclui um smoke test de
coletores reais do Windows, somente leitura; os testes da T2A/T2B usam uma implementação em memória de
`IHkcuRegistryAccess` e não tocam o Registro real. Os testes da R1 usam `SyntheticRepairExecutor` e não iniciam
`sfc.exe`, `dism.exe`, `chkdsk.exe` ou `reagentc.exe`. Nenhum teste modifica configurações do Windows ou usa o
histórico de produção como raiz de teste. A contagem **1243/1243** pertence à entrega T3 inicial.
Na T4A, quatro testes legados que dependem de leitura real do Windows são excluídos por filtro; a regressão restante
e todos os testes novos são sintéticos. Comandos, contagens e exclusões estão no relatório T4A.

## Gerar e executar o pacote

```powershell
.\scripts\publish.ps1
.\artifacts\publish\0.3.0-preview.8\win-x64\SentinelCoreAI.exe
```

O destino é versionado a partir de `Directory.Build.props`; o script recusa sobrescrever sem `-Force`.
Feche a instância publicada antes de gerar novamente o pacote.
Distribua **toda a pasta** `artifacts/publish/0.3.0-preview.8/win-x64`, incluindo DLLs nativas.
`publish.ps1` fixa `-p:SentinelCoreRealTweaks=false`: o gate e a composição bloqueiam a escrita real da T2B;
o executor HKCU permanece compilado no assembly distribuído.

Build experimental de validação (T2A/T2B), **nunca distribuído**, reservado à futura validação real:

```powershell
.\scripts\publish-validation.ps1
.\artifacts\publish-validation\0.3.0-preview.8\win-x64\SentinelCoreAI.exe --enable-real-tweaks
```

Somente esse build define `SENTINELCORE_REAL_TWEAKS` e aceita `--enable-real-tweaks`; a pasta recebe um marcador
`VALIDATION-BUILD.txt`. A escrita real continua exigindo plano, backup, autorização digitada e verificação.

A versão `0.3.0-preview.8` ainda não foi publicada. O último pacote gerado localmente é `0.3.0-preview.6`;
executar `publish.ps1` cria o pacote `preview.8` em `artifacts/publish/0.3.0-preview.8/win-x64`.
Sem MSI, assinatura ou instalação de serviço. Pacotes de versões anteriores não são sobrescritos.
Para desenvolvimento:

```powershell
.\scripts\dotnet.ps1 run --project src/SentinelCore.App
```

Single-file é uma publicação opcional, em diretório separado:

```powershell
.\scripts\publish.ps1 -SingleFile
.\artifacts\publish\0.3.0-preview.3\win-x64-single\SentinelCoreAI.exe
```

O modo single-file pode extrair bibliotecas para a pasta temporária do usuário. A versão de referência é a publicação em pasta,
que evita depender dessa extração em computadores com restrições. Não usar trimming com WPF.

## Uso

1. Abra o aplicativo. Na primeira execução não haverá histórico.
2. Clique em **Executar auditoria básica**. A coleta assíncrona mostra progresso por categoria.
3. Se necessário, use **Cancelar coleta**. O registro cancelado também é salvo, sem renovar validade.
4. Na aba **Hardware**, clique em **Coletar hardware**. A coleta é assíncrona, cancelável e não modifica o Windows.
5. Filtre por categoria, busque por nome/modelo/ID/propriedade e selecione um item para ver propriedades e limitações.
6. Use **Comparar anterior** para observar mudanças entre snapshots, **Recarregar** para reler a memória local e
   **Abrir pasta** para localizar o Markdown do snapshot selecionado.
7. Na aba **Estabilidade e Eventos**, escolha **24 horas**, **7 dias**, **30 dias** ou um período personalizado e
   clique em **Investigar eventos**. A leitura é somente leitura, com limite por canal, total, tempo e tamanho.
8. Selecione um grupo na lista para ver categoria, contagem de eventos, relatórios WER distintos, primeira e última
   ocorrência, severidade, confiança, amostras técnicas e limitações. Use a busca e o filtro por categoria.
9. **Abrir pasta** localiza o relatório da investigação selecionada; **Recarregar** relê o armazenamento local.
10. Na aba **Desempenho**, escolha **30 segundos**, **1 minuto** ou **3 minutos** e clique em **Iniciar sessão**.
    A coleta é somente leitura, com amostras de 1 segundo, até 300 amostras e cancelamento disponível.
11. Ao concluir, consulte **Indicadores**, **Processos**, **Armazenamento** e **Evidências**; o histórico lista as
    sessões por execução e cobertura. Selecione uma sessão e use **Abrir relatório** para localizar o Markdown.
12. **Recarregar** relê o armazenamento local. Fechar o aplicativo durante a sessão solicita cancelamento e aguarda
    gravação; a sessão cancelada também é preservada.
13. Na aba **Gráficos & GPU**, escolha **24 horas**, **7 dias** ou **30 dias** e clique em **Investigar gráficos**.
    A leitura é somente de GPU, monitores, drivers, eventos e metadados WER; não há reparo, atualização de driver,
    alteração de resolução, HDR, frequência ou energia. Consulte **Adaptadores**, **Monitores**, **Drivers**,
    **Eventos**, **WER** e **Evidências**. Selecione uma investigação no histórico e use **Abrir relatório**.
14. Na aba **Correlação de evidências**, escolha o período e as fontes e clique em **Correlacionar**. A investigação
    reutiliza somente registros já persistidos, consolida ocorrências repetidas entre módulos e apresenta **Linha do
    tempo**, **Relações**, **Evidências** e **Cobertura**. Cada relação mostra regra, versão, justificativa e
    limitações; causalidade não é declarada. **Abrir relatório** localiza o Markdown e o histórico permite reabrir
    investigações anteriores.
15. Na aba **Monitoramento**, escolha a duração (5, 15 ou 30 minutos), as fontes e marque a autorização explícita;
    clique em **Iniciar monitoramento**. Durante a sessão, use **Registrar incidente** para marcar uma ocorrência
    (categoria, descrição opcional e horário aproximado opcional). **Encerrar** interrompe a observação; **Abrir
    relatório** localiza o Markdown. A sub-aba **Orçamento e limites** mostra ciclos, oportunidades perdidas e limites.
    A sessão selecionada pode ser usada em **Correlacionar incidente (janela ±10 min)**, que reutiliza o motor da Fase 3.4.
16. Consulte **Resultados**, **Hardware**, **Estabilidade e Eventos**, **Desempenho**, **Rede**, **Gráficos & GPU**,
    **Correlação**, **Histórico** e **Monitoramento**. Em Resultados, **Abrir localização do relatório** seleciona o
    Markdown da auditoria no Explorador.
17. Feche e reabra; a última tentativa, os snapshots, as investigações, as sessões de desempenho/rede, as
    investigações gráficas, as correlações, as sessões de monitoramento e o histórico serão recuperados.

Fechar durante uma coleta solicita cancelamento e aguarda gravação. Se a gravação falhar, o resultado permanece na janela
e o botão permite tentar salvar o mesmo ID novamente. Não encerre o processo à força enquanto houver dados pendentes.
O cancelamento de WMI é cooperativo: um provedor em andamento pode atrasar sua resposta, apesar do timeout solicitado.
A leitura do Log de Eventos usa a API nativa e também pode aguardar um provedor lento além do tempo solicitado.

## Dados locais

`%LOCALAPPDATA%\SentinelCoreAI\`

Quando iniciado dentro do Codex empacotado (MSIX), o Windows pode redirecionar os dados para
`%LOCALAPPDATA%\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Local\SentinelCoreAI\`.
O botão de relatório resolve a localização física correta. Abrir fora desse contexto pode usar outro diretório lógico;
não mova ou apague históricos para tentar unificá-los. Consulte VALIDATION.md para a localização desta sessão.

- `memory/MEMORY.md`: últimas auditorias concluídas com sucesso e links para o histórico.
- `memory/SYSTEM_STATE.md`: última tentativa observada, incluindo limitações.
- `memory/HARDWARE.md`: resumo de hardware da última auditoria básica.
- `memory/EVENTS.md`: resumo das últimas investigações de eventos e WER.
- `memory/QUALITY.md`: avaliação derivada da qualidade de dados das investigações (regra `EVT-WER-WINDOW` 1.0).
- `memory/PERFORMANCE.md`: resumo das últimas sessões de desempenho, com execução e cobertura separadas.
- `memory/audits/basic/<id>.md`: registro histórico imutável, relatório e payload estruturado com SHA-256.
- `memory/inventory/<id>.md`: snapshot imutável do inventário de hardware, com payload JSON e SHA-256.
- `memory/events/<id>.md`: investigação imutável de eventos e WER, com payload JSON e SHA-256.
- `memory/performance/<id>.md`: sessão imutável de desempenho, com amostras, estatísticas e SHA-256.
- `memory/network/<id>.md`: sessão imutável de rede, com payload JSON e SHA-256.
- `memory/NETWORK.md`: resumo derivado das últimas sessões de rede.
- `memory/graphics/<id>.md`: investigação gráfica imutável, com adaptadores, monitores, drivers, eventos, WER,
  observações, alterações, limitações e SHA-256.
- `memory/GRAPHICS.md`: resumo derivado das últimas investigações gráficas.
- `memory/correlation/<id>.md`: investigação de correlação imutável, com fontes, cobertura, linha do tempo,
  evidências consolidadas com todas as origens, relações, notas e SHA-256.
- `memory/CORRELATION.md`: resumo derivado das últimas investigações de correlação.
- `memory/monitoring/<id>.md`: sessão de monitoramento imutável, com fontes, orçamento, incidentes, referências aos
  checkpoints e SHA-256.
- `memory/monitoring/<id>/segments/<segmento>.md`: checkpoint imutável de uma fonte, com observações, intervalo e SHA-256.
- `memory/MONITORING.md`: resumo derivado das últimas sessões de monitoramento.
- `memory/transactions/<id>/journal/<sequência>-<estado>.md`: evento imutável do journal de uma transação sintética,
  com plano, autorização, backup, resultados, verificação, recuperação e SHA-256.
- `memory/transactions/<id>/backup-01.md`: backup imutável do estado observado, com existência de alvo/valor, tipo,
  conteúdo, checksum e SHA-256.
- `reports/transactions/<id>.md`: relatório legível da transação, reconstruível do journal.
- `memory/RECOVERY.md`: índice derivado de transações e recuperações.
- `data/sentinelcore.db`: índice SQLite, esquema 10 e projeções de auditorias, snapshots, investigações, sessões,
  investigações gráficas, correlações, sessões de monitoramento, checkpoints, transações, eventos, backups e
  recuperações.
- `reports/<id>.md`: relatório legível da auditoria, reconstruível do registro histórico.
- `reports/hardware/<id>.md`: relatório legível do snapshot de hardware, reconstruível do payload.
- `reports/events/<id>.md`: relatório legível da investigação de eventos, reconstruível do payload.
- `reports/performance/<id>.md`: relatório legível da sessão de desempenho, reconstruível do payload.
- `reports/network/<id>.md`: relatório legível da sessão de rede, reconstruível do payload.
- `reports/graphics/<id>.md`: relatório legível da investigação gráfica, reconstruível do payload.
- `reports/correlation/<id>.md`: relatório legível da correlação, reconstruível do payload.
- `reports/monitoring/<id>.md`: relatório legível da sessão de monitoramento, reconstruível do payload.
- `logs/`: falhas de aplicação e avisos de consistência quando ocorrerem.

Investigação de eventos lê somente canais habilitados, estados de canal e metadados de WER. Mensagens formatadas e
identificadores técnicos podem conter caminhos de aplicativos ou módulos; nada é enviado, e nenhum dump, documento ou
conteúdo de navegação é lido. Relatórios WER não são apagados nem limpos. Não há envio de telemetria ou dados do produto.
Não são coletados senhas, tokens, documentos, tráfego ou conteúdo de projetos.

Desempenho usa `GetSystemTimes`, `GlobalMemoryStatusEx`, `System.Diagnostics.Process` com `GetProcessIoCounters` e WMI
formatado `PerfOS`/`PerfDisk` em leitura. O PDH não é usado nesta versão porque exige seleção/caminho de contador e
tratamento próprio de instâncias; a escolha evita outra superfície de configuração e mantém consultas fixas. O WMI
formatado tem resolução de 1 s e pode bloquear por vários segundos em alguns provedores; a sessão mantém a cadência
nominal e registra oportunidades perdidas em vez de disparar uma rajada de recuperação. Nas sessões reais do
0.3.0-preview.3, a coleta levou 585–596 ms em média e 616–651 ms no máximo, dominada por duas consultas WMI de
armazenamento; não houve oportunidade perdida. Processos protegidos ficam sem nome, hora de início ou contadores e são
contabilizados como limitação, não estimados. A coleta roda somente enquanto uma sessão está ativa; não há monitoramento
permanente, serviço nem elevação.
O monitoramento da Fase 5 também roda somente durante a sessão autorizada. CPU e memória usam `GetSystemTimes` e
`GlobalMemoryStatusEx` a cada 15 s; eventos usam leitura incremental nativa por `EventRecordID` nos níveis 1–3 a cada
15 s; rede usa as tabelas locais da Fase 3.3A a cada 30 s e persiste apenas contagens. Gráficos são uma visão derivada
dos eventos, sem leitor paralelo. O orçamento padrão admite 2 s por leitura, 240 amostras por fonte, 2.000 eventos e
3 MiB por sessão; limites e oportunidades perdidas são declarados. Nenhum serviço, tarefa agendada ou processo residente
é criado, e a observação termina com o fechamento do aplicativo.
Gráficos reutilizam WMI `Win32_VideoController`/`Win32_PnPSignedDriver`, DisplayConfig/`WmiMonitorID`, Log de Eventos
e metadados WER. Nenhuma métrica de temperatura, potência, frequência, utilização global ou VRAM real é coletada; o
`AdapterRAM` é apenas o valor reportado pelo driver. Nenhum driver, resolução, HDR, frequência ou energia é alterado.
Os arquivos não são criptografados; a proteção depende das permissões do perfil Windows. Compartilhe relatórios conscientemente.
Histórico real nunca é apagado automaticamente. Para backup, feche o aplicativo e copie a pasta de dados inteira.
O hash detecta corrupção acidental do payload; não é uma assinatura contra adulteração pelo próprio usuário.

Hosts de validação podem iniciar o aplicativo com um diretório de dados absoluto e isolado:
`SentinelCoreAI.exe --data-root D:\caminho\isolado`. Sem esse argumento, o aplicativo usa
`%LOCALAPPDATA%\SentinelCoreAI`. A opção serve para validar a interface e a persistência sem tocar no histórico real;
ela não eleva privilégios nem altera o Windows.

## Organização

Cinco projetos de produção em `src`, testes em `tests`, documentação em `docs` e scripts locais em `scripts`.
Consulte [arquitetura](docs/ARCHITECTURE.md), [segurança](docs/SECURITY.md) e [roadmap](docs/ROADMAP.md).
Repositório Git local, sem commit automático, remoto, push ou deploy.
