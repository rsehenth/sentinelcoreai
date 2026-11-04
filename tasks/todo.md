# Execução

## Fases 0 e 1

- [x] Inspecionar pasta, Windows, Git e runtimes; obter autorização para SDK local.
- [x] Instalar SDK local verificado e criar solução modular.
- [x] Escrever testes e implementar contratos, orquestração e validade.
- [x] Implementar e testar memória Markdown e SQLite reconstruível.
- [x] Implementar coletores reais de leitura do Windows.
- [x] Implementar interface WPF funcional.
- [x] Restaurar, compilar Debug/Release e executar testes: 56 aprovados, 0 falhas.
- [x] Publicar, abrir, auditar, fechar, reabrir e inspecionar a interface: pasta e single-file.
- [x] Documentar evidências, limitações e revisar Git diff. Ver docs/VALIDATION.md.

## Fase 2 — Hardware Intelligence

- [x] Definir contratos de snapshot, coletor, item, presença, status e limitações.
- [x] Implementar HardwareRunner com sessão compartilhada, cancelamento e status por coletor.
- [x] Implementar os onze coletores: CPU, memória, placa-mãe/BIOS, GPU, monitores, armazenamento, rede, áudio,
      USB/periféricos, drivers e outros dispositivos.
- [x] Corrigir presença PnP (CR_NO_SUCH_DEVINST), memória e DisplayConfig; mapear arquitetura e sentinelas.
- [x] Implementar diferenças sem falsa remoção em categoria parcial; propriedades voláteis excluídas.
- [x] Persistir snapshots Markdown imutáveis, relatórios e índice SQLite esquema 2.
- [x] Ampliar a aba Hardware: categorias, busca, propriedades, histórico, comparação, recarga, progresso e cancelamento.
- [x] Executar testes sintéticos: 209 aprovados em Debug e Release, 0 falhas.
- [x] Validar em hardware real: 383 itens, 11 categorias, persistência, reabertura e comparação.
- [x] Publicar self-contained Windows x64, executar, fechar, reabrir e inspecionar dados duráveis.
- [x] Verificar integridade: 11 checksums, SQLite `quick_check=ok`, auditorias preservadas.
- [x] Atualizar README, ARCHITECTURE, ROADMAP, SECURITY e VALIDATION com evidências e limites.

## Fase 3.1 — Event & Reliability Intelligence

- [x] Validar a fundação: auditorias, snapshots, SQLite, Markdown e os 209 testes existentes.
- [x] Definir contratos de janela, evento bruto, relatório WER, grupo de evidência e investigação.
- [x] Implementar coletor nativo do Log de Eventos com canais fixos, XPath e limites.
- [x] Implementar coletor de metadados WER com filtragem por período e limite de arquivo.
- [x] Implementar catálogo de categorias e deduplicação conservadora por identidade.
- [x] Implementar EventInvestigationRunner com status por fonte, progresso e cancelamento.
- [x] Persistir investigações em Markdown imutável, índice SQLite esquema 3 e projeções.
- [x] Criar a aba Estabilidade e Eventos com período, histórico, busca, filtro e detalhe.
- [x] Corrigir filtragem WER pelo período e exibição de detalhe de canal nulo.
- [x] Executar testes sintéticos: 312 aprovados, 0 falhas.
- [x] Publicar self-contained, executar, investigar, fechar, reabrir e inspecionar dados duráveis.
- [x] Verificar integridade: auditorias, snapshots, SQLite `quick_check=ok`, `user_version=3`.
- [x] Atualizar VALIDATION com resultados e limitações exatas.

## Fase 3.2 — Performance Intelligence

- [x] Validar a fundação: 312 testes, auditorias, snapshots, investigações, SQLite esquema 3 e Markdown.
- [x] Definir contratos de sessão, limites, amostras, estatísticas, observações e auto-sobrecarga.
- [x] Implementar normalizadores de CPU e memória com capacidade lógica total e distinção de commit.
- [x] Implementar normalizador e identidade de processos (PID + início), término e reutilização de PID.
- [x] Implementar normalizador e mapa de identidade de armazenamento sem confundir zero I/O com latência zero.
- [x] Implementar motor de estatísticas com amostras válidas, percentis, tendência e nulos indisponíveis.
- [x] Implementar seis regras determinísticas de evidência com amostras/segundos mínimos e causalidade não avaliada.
- [x] Implementar quatro coletores Windows somente leitura e auto-sobrecarga por tick.
- [x] Implementar runner de sessão com grade absoluta, oportunidades perdidas sem rajada, limites e cancelamento.
- [x] Persistir sessões em Markdown imutável, relatórios, `PERFORMANCE.md` e SQLite esquema 4.
- [x] Ampliar StoreInspector com as três tabelas de desempenho.
- [x] Implementar a aba Desempenho: duração, progresso, cartões, quatro visões, histórico e relatório.
- [x] Corrigir exceção do Ctrl+Tab no PageHost e validar troca por clique e teclado.
- [x] Executar testes sintéticos finais em Release e builds Debug/Release sem avisos — 386/386 em 4 s; 0 avisos, 0 erros.
- [x] Validar sessões reais de 30 s e 1 min na interface, reabertura, relatório e cancelamento — sessões publicadas
  `0e555edf` (30,66 s) e `000a1878` (60,67 s, 3 oportunidades perdidas), cancelamentos `3d27d713` e `63257da5`,
  7 sessões restauradas após reabrir; relatório aberto no Explorador.
- [x] Aplicar a revisão independente somente de leitura e corrigir o defeito crítico do intervalo cumulativo em CPU
  e I/O: cada tick passa a receber o intervalo desde o tick anterior; limites repassados aos coletores; PID
  reutilizado separado de processo encerrado; contador indisponível não vira zero. 2 testes novos de runner.
- [x] Reexecutar a suíte completa após a correção — 388/388 em 5 s; builds Debug e Release sem avisos.
- [x] Publicar `0.3.0-preview.2` sem sobrescrever `0.3.0-preview.1` e validar o executável publicado — pasta
  `win-x64` com 149.960.213 B em 414 arquivos, apphost 162.304 B, SHA-256 `06A02647…1977ED`; preview.1 e 0.2.0
  preservados.
- [x] Repetir as sessões reais pós-correção no executável publicado — 30 s `42442476` (27 amostras, 30,71 s, CPU
  20,9%, 4 oportunidades perdidas) e 1 min `a31c625f` (61 amostras, 60,80 s, CPU 14,2%); medição nativa do app:
  CPU 0,44%, WS 251 MiB, privado 161,1 MiB.
- [x] Reabrir o preview.2 e visualizar as duas sessões na aba Desempenho, com histórico de 9 sessões, sem regravar
  as 7 sessões pré-correção.
- [x] Registrar no VALIDATION os 16 itens, 16 critérios, medições de auto-sobrecarga e limitações restantes —
  `docs/VALIDATION.md`; relatório da 3.1 arquivado em `docs/validation/0.3.0-preview.1.md`.

Limitações remanescentes: processos protegidos sem início/I/O, WMI com resolução de 1 s e travamentos (4,6 s
observados na sessão de 30 s), PDH não usado, primeira amostra sem CPU, zero I/O sem latência, single-file do
preview.2 não gerado, Gerenciador de Tarefas não usado como captura, amostragem nativa apenas na janela de 1 min,
UIA sem travessia do conteúdo do TabControl customizado e nota de `PERF-MEM-COMMIT-MARGIN` dizendo “amostras
insuficientes” quando a pré-condição de margem não é atingida (nenhum valor inventado). As 7 sessões pré-correção
permanecem com CPU/I/O subnotificados e não foram reescritas.

## Fase 3.2.1 — Performance Intelligence: endurecimento

- [x] Validar a fundação: 388 testes, Markdown, SQLite esquema 4, auditorias e as 9 sessões anteriores.
- [x] Separar `PerformanceExecutionStatus` de `PerformanceCoverageStatus`, com esperadas/coletadas/perdidas,
      limites, fontes indisponíveis e limitações; status legado preservado.
- [x] Propagar execução/cobertura para envelope, relatório, `PERFORMANCE.md`, histórico e cabeçalho da UI.
- [x] Implementar migração SQLite aditiva 4→5 (`CoverageStatus`, `CoverageJson`) e reconstrução com cobertura.
- [x] Implementar `PerformanceQualityCatalog` não destrutivo para os sete IDs de `PERF-INTERVAL-CUMULATIVE`,
      com banner, marca em relatório e bloqueio de comparação.
- [x] Medir o custo por coletor e por tick; manter a cadência de 1 s com justificativa e risco residual documentado.
- [x] Corrigir `PERF-MEM-COMMIT-MARGIN` para “pré-condição não observada” e auditar as demais regras.
- [x] Revalidar intervalos, CPU, memória, processos, I/O, armazenamento, PID, nulos e limites com testes sintéticos.
- [x] Adicionar 23 testes (411 no total), incluindo cobertura parcial, interrupção, indisponibilidade, limite,
      histórico afetado/válido, imutabilidade, reconstrução, mensagens, coletores lentos e consistência.
- [x] Executar builds Debug/Release sem avisos e suíte completa — 411/411 em 5 s.
- [x] Validar sessões reais de 30 s (`181336e6`) e 1 min (`f3d332c8`) no publicado, com 0 oportunidades perdidas,
      `Completed/Partial`, auto-sobrecarga e comparação nativa independente.
- [x] Reabrir o app e conferir 16 sessões, cobertura, leituras por fonte e banner de qualidade histórica na UI.
- [x] Verificar persistência: 16 envelopes íntegros, 9/9 originais inalterados, SQLite esquema 5 com
      `quick_check=ok`.
- [x] Publicar `0.3.0-preview.3` (pasta e single-file) sem sobrescrever preview.1/preview.2.
- [x] Atualizar README, ARCHITECTURE, ROADMAP, VALIDATION e este arquivo; arquivar o VALIDATION do preview.2.
- [x] Segunda revisão independente concluída em 31/10/2026: **REPROVADO**. Dois revisores com contexto separado,
      conferência do coordenador e reproduções em harness isolado; 11 achados (2 P1 e 9 P2). Ver
      `docs/validation/2026-10-31-phase-3.2.1-quality-gate.md`. Revisão encerrada não significa fase aprovada.

Limitações remanescentes da 3.2.1: quality gate reprovado, correções pendentes; linhas duplicadas de limitação de processos por
contagem volátil; WMI de armazenamento consome ~59% do tick e pode perder amostras em máquina mais lenta;
processos protegidos sem início/I/O; primeira amostra sem CPU; zero I/O sem latência; limite de 20 processos mantém
a cobertura parcial por desenho; PDH, CPU por núcleo, GPU, temperatura e rede fora de escopo; UIA sem travessia do
conteúdo do PageHost.

## Roadmap documental e quality gate — 31/10/2026

- [x] Registrar Personalização & Debloat como futuro, opcional e exclusivo para Windows 11; T0–T4 e prioridades.
- [x] Documentar riscos, limite MIT/upstream, Safety Engine, separação de capacidades e Recovery Center.
- [x] Atualizar ROADMAP, ARCHITECTURE, SECURITY, AGENTS, README e VALIDATION sem alterar código ou executáveis atuais.
- [x] Restore locked-mode, builds Debug/Release sem avisos/erros, 411/411 testes Release e publicação isolada.
- [x] Registrar diferenças entre evidência histórica e verificações desta tarefa, incluindo índice ativo não reconfirmado.
- [x] Corrigir QG-01/02: continuidade de evidências e completude efetiva por fonte/tick.
- [x] Tratar QG-03–09: latência por direção, monotônico, cadência, seleção, baselines, múltiplos grupos e privilégio mínimo.
- [x] Corrigir QG-10/11: corrupção estrutural isolada por registro e hash original na indexação de legados.
- [x] Adicionar 22 novos testes de regressão (433 aprovados no total); preservar compatibilidade e envelopes.
- [x] Revalidar UI real, auditoria básica, inventário de hardware, investigações de eventos, sessões de desempenho de 30s e 1min, Markdown e SQLite ativo com reabertura limpa.
- [x] Publicar versão 0.3.0-preview.4 (win-x64 e win-x64-single).
- [x] Submeter a dois revisores independentes em contextos isolados e obter veredito APROVADO no Quality Gate da Fase 3.2.2.
- [x] Atualizar Matriz de Rastreabilidade, VALIDATION, ROADMAP e parecer completo do Quality Gate.

Fase 3.2.2 concluída com sucesso. Fase 3.3 apta para planejamento e desenho de arquitetura.

## Fase 3.3A — Network Diagnostics (encerrada em 01/11/2026; APROVADA COM RESSALVAS)

- [x] Definir contratos de sessão, limites, amostras, fatos, observações e cobertura de rede.
- [x] Implementar dois coletores de amostra (endpoints TCP/UDP e estatísticas de transporte) e dois de fatos
      (intervalo dinâmico e adaptadores), todos somente leitura.
- [x] Implementar associação de processos por PID + hora de criação com `PROCESS_QUERY_LIMITED_INFORMATION`.
- [x] Implementar runner com instantâneo/30 s/1 min, grade absoluta, oportunidades perdidas, execução e cobertura em
      eixos separados e endereços opcionais por autorização explícita.
- [x] Implementar estatísticas, cinco regras determinísticas e contexto informativo de eventos Tcpip 4266/4231.
- [x] Persistir em Markdown imutável com envelope/hash, relatórios, `NETWORK.md` e SQLite esquema 6.
- [x] Implementar `NetworkQualityCatalog` não destrutivo para `NET-TCP6-REMOTE-OFFSET` (coletor 1.0.0 → 1.0.1).
- [x] Implementar a aba Rede com cinco sub-abas, histórico, progresso, cancelamento e opção explícita de endereços.
- [x] Quality gate de 31/10/2026: **REPROVADO** em D1–D4; remediação corrigiu D1 (offsets 40/44), D2
      (`ToString() => Label`), D3 (IDs 4266/4231) e D4 (versão única).
- [x] Revisão do delta: **APROVADO**; encerramento formal em 01/11/2026: **APROVADA COM RESSALVAS**.
- [x] Suíte com 514 testes aprovados; builds Debug e Release sem avisos; 57/57 envelopes íntegros e 14 sessões de
      rede preservadas (SQLite `user_version=6`, `integrity_check=ok`).
- [x] Preservar histórico de reprovação, correções e ressalvas em
      `docs/validation/2026-10-31-phase-3.3a-quality-gate.md`.

Ressalvas aceitas: R1 conexão IPv6 estabelecida não observada; R2 duas sessões de procedência desconhecida
(`9cf704d4…` e `391a9da4…`) íntegras e preservadas; R3 hipóteses N2–N7 não confirmadas; R4 veredito REPROVADO
original preservado.

## Fase 3.3B — Graphics Diagnostics (implementada em 0.3.0-preview.6; gate final verificado em 01/11/2026 — APROVADA COM RESSALVAS)

- [x] Definir contratos de investigação, fontes, cobertura, adaptadores, monitores, drivers, eventos, WER e alterações.
- [x] Implementar `GraphicsInvestigationRunner` reutilizando os coletores `gpu`/`monitor`/`driver` e o
      `EventInvestigationRunner` existente, sem inventário ou investigação de eventos paralela.
- [x] Implementar classificação conservadora de TDR Display 4101 e LiveKernelEvent 117/141/142; AppCrash não é
      promovido a instabilidade de hardware.
- [x] Implementar observações determinísticas, notas de análise, limites e cobertura parcial sem inventar métricas de
      temperatura, potência, frequência, utilização global ou VRAM real.
- [x] Implementar `GraphicsChangeEngine` para alterações entre investigações, sem atribuir causa.
- [x] Persistir Markdown imutável, `GRAPHICS.md`, relatórios derivados e SQLite esquema 7 com migração aditiva 6→7.
- [x] Adicionar a aba **Gráficos & GPU** com período, progresso, cancelamento, seis sub-abas, histórico e relatório,
      sem controles de reparo ou alteração.
- [x] Criar testes de TDR, eventos, WER, identidade, múltiplos adaptadores, drivers, monitores, indisponibilidade,
      cobertura parcial, persistência, histórico, cancelamento, alterações e regressões — 47 casos novos.
- [x] Executar builds Debug/Release sem avisos e suíte completa — 561/561 em Debug e Release.
- [x] Remediar as duas revisões iniciais REPROVADO: nenhuma remoção quando a leitura atual falha/trunca; alteração de
      driver única; LiveKernelEvent exige tipo; validação cruzada de cobertura; cancelamento com zero fontes
      persistível; `EventReadingId` nulo quando não executado; horários de observação separados; limites aplicados.
- [x] Reproduzir a validação real e a UI após a remediação — `43463872…` (comparação 0 alterações) e `8e3b0bc5…`
      na interface, 4 registros recuperados após reabrir.
- [x] Executar investigação real de 7 dias e uma investigação pela interface do publicado; persistir 9 registros.
- [x] Verificar 66/66 envelopes com SHA-256, SQLite `user_version=7`, `integrity_check=ok`,
      `foreign_key_check` vazio e 9 investigações gráficas; reabrir e recuperar o histórico.
- [x] Publicar `0.3.0-preview.6` sem sobrescrever `preview.1` a `preview.5`.
- [x] Concluir as duas revisões independentes e registrar o parecer do quality gate — ambas iniciaram em REPROVADO,
      a re-revisão do delta deu **APROVADO COM RESSALVAS**, e o gate final da 3.3B foi registrado como
      **APROVADO COM RESSALVAS** em `docs/VALIDATION.md`.

Limitações da 3.3B: cobertura real parcial porque o Log de Eventos trunca mensagens e o WER expõe metadados incompletos;
TDR/LiveKernelEvent não identificam causa; temperatura, potência, frequência, utilização global e VRAM real não são
coletadas; HDR/VRR limitados ao reportado; correlação avançada permanece na Fase 3.4.

## Verificação final do Quality Gate — Fase 3.3B (01/11/2026)

- [x] Identificar o delta posterior às revisões: `GraphicsAnalysis.cs`, `GraphicsInvestigationRunner.cs` e
      `GraphicsReport.cs` escritos às 14:45:31 (N4/N5/N6); testes às 14:39; publish às 14:46; UI às 14:42.
- [x] Restore `--locked-mode` e rebuild completo Debug/Release sobre a árvore final — 0 avisos, 0 erros.
- [x] Executar as suítes completas Debug e Release após as edições — **561/561** em cada, 0 falhas,
      0 ignorados; TRX em `artifacts/final-validation-3.3b/trx/`.
- [x] Confirmar o Graphify atualizado (3.703 nós, 10.212 arestas) e localizar runner, analisador, change engine,
      projeção, modelos, relatório, MemoryStore, EventInvestigationRunner, coletores de hardware e UI.
- [x] Republicar em pasta isolada e comparar com o publicado — 414/414 arquivos byte a byte idênticos,
      150.697.549 B, sem divergência de SHA-256; executável e cinco DLLs conferidos.
- [x] Verificar integridade histórica somente de leitura — 66/66 envelopes, esquema 7, `integrity_check=ok`,
      `foreign_key_check` vazio, 9 investigações gráficas, IDs Markdown↔SQLite coincidentes.
- [x] Abrir o executável publicado, selecionar **Gráficos & GPU** e confirmar 9 registros sem criar investigação;
      record set e relatórios inalterados; fechamento gracioso.
- [x] Submeter o estado final a dois revisores independentes — ambos **APROVADO COM RESSALVAS**, sem bloqueante;
      achados consolidados em R7–R13 de `docs/VALIDATION.md` §10.
- [x] Registrar a verificação final e corrigir a rastreabilidade de N3–N6 em `docs/VALIDATION.md` §10.
- [x] Tratar as ressalvas prioritárias R7, R8 e R9 em remediação direcionada de 01/11/2026: classificação WER
      type-first unificada; testes diretos dos seis limites e de identidade duplicada nas duas leituras; truncamento
      de mensagem visível na UI e no Markdown. Suíte **613/613** em Debug e Release (52 casos novos), builds `0/0`,
      históricos preservados. Registro em `docs/VALIDATION.md` §11 e
      `docs/validation/2026-11-01-phase-3.3b-r7-r9-remediation.md`. R10–R13 permanecem; a Fase 3.4 não foi iniciada.

## Sincronização documental — 01/11/2026

- [x] Verificar no código o comportamento de endereços e documentar coleta padrão × detalhada em SECURITY.
- [x] Representar os componentes efetivamente implementados da Fase 3.3A em ARCHITECTURE.
- [x] Corrigir referências obsoletas de esquema SQLite e de leitura de Registro.
- [x] Regenerar o índice Graphify do código atual em diretório temporário externo, sem sobrescrever o índice anterior.
- [x] Identificar componentes e dependências relevantes para Graphics Diagnostics.
- [x] Confirmar ausência de contradições entre VALIDATION, ROADMAP, ARCHITECTURE e SECURITY sobre a Fase 3.3A.

## Fase 3.4 — Evidence Correlation Engine (implementada em 0.3.0-preview.7)

- [x] Confirmar o estado real do repositório: `master` limpo no código, baseline 613/613 em Release, builds `0/0`,
  SQLite esquema 7, 66 envelopes, 9 investigações gráficas e 143 arquivos no diretório de dados real.
- [x] Consultar o índice Graphify existente (3.703 nós) para mapear contratos de armazenamento, runners, modelos de
  evidência, persistência e pontos de integração, sem reescrever o índice nem usar centralidade como justificativa.
- [x] Definir `CorrelationModels`: fontes, janela, limites por regra, referência de boot, evidência normalizada,
  origens, precisão, identidade, relação, nota, cobertura, investigação e contrato de armazenamento.
- [x] Implementar `CorrelationEvidenceReader` reutilizando `IAuditStore`, `IHardwareSnapshotStore`,
  `IEventInvestigationStore`, `IPerformanceSessionStore`, `INetworkSessionStore` e `IGraphicsInvestigationStore`,
  sem coletor novo e com leitura sequencial por causa do bloqueio do armazenamento.
- [x] Implementar `CorrelationEvidenceNormalizer` com UTC, precisão explícita, ausência de timestamp, horários
  implausíveis, janelas de observação, identidade técnica, deduplicação e proveniência por origem.
- [x] Implementar `CorrelationEngine` com oito regras versionadas (gráfico×WER, gráfico×aplicativo, desempenho×evento,
  rede×evento, identidade de processo, identidade de dispositivo, sequência de alteração e insuficiência explícita).
- [x] Implementar `EvidenceCorrelationRunner` com janela, progresso, cancelamento, status, cobertura, limites e
  limitações obrigatórias de causalidade/escopo.
- [x] Persistir em `memory/correlation/<guid>.md` imutável, `CORRELATION.md`, `reports/correlation` e SQLite esquema 8
  com migração aditiva 7→8, validação estrita, reconstrução do índice e colisão entre históricos.
- [x] Implementar a aba **Correlação de evidências** com período, seis fontes, linha do tempo, relações, evidências,
  cobertura, histórico, cancelamento e relatório, sem controles de reparo ou alteração.
- [x] Integrar o bloqueio de operação, o encerramento com gravação segura, o Ctrl+Tab e os atalhos Ctrl+8/Ctrl+9.
- [x] Adicionar 72 testes novos: normalização, regras, limites exatos, identidade, deduplicação, cobertura,
  cancelamento, persistência, integridade, interface e compatibilidade de esquema.
- [x] Executar suíte completa Debug e Release — **685/685** em cada, 0 falhas; builds Debug e Release `0/0`.
- [x] Validar em cópia isolada de dados reais: 2.804 referências, 479 ocorrências distintas, 2.325 consolidadas,
  cobertura completa, hash de envelope igual ao SQLite, esquema 8, `integrity_check=ok` e `foreign_key_check` vazio.
- [x] Executar a correlação pela interface real (clique em **Correlacionar**) em cópia isolada, confirmar novo registro,
  histórico com dois itens e reabertura com histórico recuperado; produção permaneceu byte a byte inalterada.
- [x] Aplicar as duas revisões independentes: corrigir a versão fixa dos testes de contrato, a fan-out de botões entre
  abas, o cancelamento nas regras, a preservação de evidências no cancelamento, a referência de boot desatualizada, a
  validação de identidade de processo, a contagem de deduplicação, os limites de inventário e o endurecimento do
  `--data-root`; adicionar testes para os cenários apontados.
- [x] Submeter o estado final a duas revisões independentes — ver `docs/validation/2026-11-01-phase-3.4-evidence-correlation.md`.
- [x] Atualizar README, ARCHITECTURE, SECURITY, ROADMAP, VALIDATION, este arquivo e o relatório da fase.

Limitações da 3.4: correlação é associação limitada, não causa; depende da qualidade/atualidade dos registros;
mensagens e identidades já autorizadas aparecem no relatório derivado; evidência sem horário confiável não entra na
linha do tempo; a UIA não percorre o conteúdo do `PageHost`, então a validação visual usou captura e clique real.

## Fechamento do Quality Gate — Fase 3.4 (02/11/2026)

- [x] Revalidar o estado final: Git, versão `0.3.0-preview.7`, builds Debug/Release `0/0`, TRX novos e produção
  intocada (SQLite esquema 7, 66 envelopes, `integrity_check=ok`, `foreign_key_check` vazio).
- [x] Regenerar o Graphify em diretório temporário externo sobre `src`+`tests`: 3.748 nós, 11.290 arestas no
  `graph.json` (11.266 consideradas no agrupamento) e 192 comunidades; dependências dos oito componentes novos e dos
  armazenamentos históricos confirmadas; índice do repositório não foi alterado.
- [x] Submeter o delta a duas revisões independentes. Ambas reprovaram inicialmente: bloqueante de cancelamento
  **pós-leitura** (`Cancelled` sem fonte cancelada, rejeitado pela validação e não salvável) e sobredeclaração de
  cobertura (registros de origem `Partial` tratados como concluídos).
- [x] Corrigir o bloqueante no runner/validador, com teste de regressão que falhou antes (`InvalidDataException`) e
  passou depois; propagar o status/cobertura dos registros de origem; honrar os limites do construtor do runner;
  recusar `--data-root` sem valor.
- [x] Revalidar: **688/688** em Debug e Release, 0 falhas/ignorados; 3 testes novos; builds `0/0`.
- [x] Executar teste de escala isolado com 2.000 evidências: 2.000/2.000 distintas, 2.000 itens de linha do tempo,
  120 relações no limite, 166,7 ms, +5,0 MiB de heap gerenciado, envelope de 3.561.605 B persistido e relido;
  caminho esparso de 2.000 evidências concluiu `Complete` em 3,8 ms; cancelamento produziu `Cancelled/Unavailable`
  sem evidência fabricada; assinatura determinística idêntica e nenhuma relação/par duplicado.
- [x] Correlacionar uma cópia isolada do armazenamento real: **2/6 fontes concluídas e 4/6 parciais**, cobertura e
  status `Partial`, 2.617 referências, 448 distintas, 2.169 consolidadas, 448 itens de linha do tempo, 0 relações;
  66/66 envelopes inalterados; envelope de 1.547.826 B persistido e relido; SQLite esquema 8 na cópia.
- [x] Validar a interface final por clique real em cópia isolada: inicialização, navegação, histórico de 1 → 2
  registros, status "Concluída com cobertura parcial", reabertura com histórico recuperado e encerramento gracioso.
- [x] Submeter o delta corrigido a nova revisão independente e preservar os pareceres anteriores sem reescrever o
  histórico. Parecer final em `docs/validation/2026-11-02-phase-3.4-final-gate.md`.

**FASE 3.4 — APROVADA COM RESSALVAS.** Nenhum commit, push ou deploy foi executado.

Limitações remanescentes: a janela real validada não teve associação temporal/identidade (regras cobertas por
fixtures); a maior parte dos registros históricos é parcial, então a cobertura real é parcial; alguns campos de
validação cruzada permanecem sem checagem (baixa severidade); a busca de pares é quadrática e limitada; a captura
detalhada de cancelamento em voo não foi registrada, embora o caminho esteja coberto por testes; a UIA não expõe o
conteúdo do `PageHost`, e o clique real foi por coordenada após verificação de primeiro plano.

## Fase 5 — Persistent Monitoring (implementada em 0.3.0-preview.8)

- [x] Confirmar estado real: `master` com alterações locais preservadas, versão `0.3.0-preview.7`, schema 8, baseline
  688 testes; não executar reset, clean, commit ou push.
- [x] Inspecionar `AGENTS.md`, ARCHITECTURE, SECURITY, ROADMAP, VALIDATION e o parecer final da Fase 3.4; preservar o
  histórico de reprovações e ressalvas.
- [x] Regenerar o Graphify em diretório externo ao repositório sobre o código atual: **3.845 nós, 11.511 arestas**,
  com os índices anteriores preservados; confirmar dependências dos runners, coletores, armazenamentos e UI.
- [x] Definir os contratos de sessão, duração, fontes, orçamento, observações, segmentos, incidentes, recuperação e
  armazenamento em `MonitoringModels`.
- [x] Implementar o adaptador nativo de CPU/memória (`monitoring-performance`, sem WMI por ciclo) e o leitor
  incremental do Log de Eventos por `EventRecordID` (níveis 1–3), reutilizando `EventRecordMapper` e `EventCatalog`.
- [x] Implementar a fonte de rede por contagens agregadas com os coletores da Fase 3.3A e a fonte gráfica derivada dos
  eventos, sem leitor paralelo e sem endereços IP persistidos.
- [x] Implementar `MonitoringSessionRunner`: autorização obrigatória, uma sessão por vez, grade de cadências, orçamento
  por leitura, oportunidades perdidas sem rajada, limites, cancelamento, incidentes e relógio monotônico.
- [x] Implementar checkpoints imutáveis por fonte, registro final que apenas os referencia, orçamento de armazenamento
  e recuperação de sessões interrompidas sem gravar `Completed`.
- [x] Implementar persistência Markdown/SQLite esquema 9 com migração aditiva 8→9, validação estrita, idempotência,
  colisão entre históricos, projeções `MONITORING.md` e `reports/monitoring`, reconstrução e testes de corrupção.
- [x] Integrar ao Evidence Correlation Engine de forma aditiva: fonte `Monitoring` opcional, mesma identidade
  `evt:{canal}:{RecordId}`, amostras como intervalos, regras existentes reutilizadas e incidente como referência
  temporal, nunca como evidência nativa.
- [x] Implementar a aba **Monitoramento** com duração, fontes, autorização, tempo restante, contadores, incidentes,
  linha do tempo, orçamento, histórico, relatório e correlação direcionada; integrar fechamento, cancelamento, Ctrl+Tab
  e o bloqueio global de operações.
- [x] Criar testes de sessão, orçamento, tempo, incidentes, eventos, persistência, recuperação, integração e regressões;
  atualizar as expectativas de esquema 9 e a guarda de ComboBox sem enfraquecer os testes anteriores.
- [x] Executar builds Debug/Release `0/0` e suítes completas — **760/760 em Debug e Release**, 0 falhas e 0 ignorados,
  incluindo a remediação dos deltas das duas revisões independentes.
- [x] Executar uma sessão real curta e controlada em armazenamento temporário isolado, com as fontes reais, medindo
  CPU/memória do processo, ciclos, oportunidades perdidas, custo por leitura, volume persistido e integridade.
- [x] Submeter o estado final a duas revisões independentes (monitoramento/desempenho e persistência/segurança) e
  corrigir os bloqueantes/high demonstrados; registrar ressalvas remanescentes.
- [x] Atualizar README, ARCHITECTURE, SECURITY, ROADMAP, VALIDATION, este arquivo e o relatório da fase; preservar os
  pareceres anteriores e não executar commit, push ou deploy.

Limitações da Fase 5: o monitoramento não permanece ativo fora da sessão; não há serviço, tarefa agendada ou coleta
em segundo plano; WER não é lido em tempo real; a fonte de eventos observa apenas níveis 1–3 e os quatro canais fixos;
a fonte de rede não persiste endereços; uma amostra ou um clique de incidente não comprovam causa ou início do
problema; a recuperação deriva estado e não reconstrói incidentes mantidos apenas em memória; a UIA não expõe o
conteúdo do `PageHost`.

## T0 — Win11Debloat Discovery & Risk Audit (concluída em 02/11/2026; somente leitura)

- [x] Confirmar estado real do repositório: `master` limpo, `HEAD` `7630220`, versão `0.3.0-preview.8`, schema 9.
- [x] Ler AGENTS, ARCHITECTURE, SECURITY, ROADMAP, VALIDATION, TWEAKS_DEBLOAT_DISCOVERY, todo e README.
- [x] Verificar o commit upstream `5d88bfe9ef2be5a8a643166a6400eb78a30c46ec` por `git ls-remote` e clone isolado;
      confirmar que era o topo de `master` e que não havia commits posteriores.
- [x] Ler licença MIT, README, estrutura de scripts, XAML, testes, CI e política de segurança do upstream.
- [x] Inventariar 104 features (90 de registro, 14 ações), 10 `UiGroups`, 162 aplicativos e 2 presets.
- [x] Extrair 463 operações de registro (246 de aplicação, 217 de undo) com hive, chave, valor, tipo e linha.
- [x] Analisar backup, restauração, ponto de restauração, detecção de estado e privilégios do upstream.
- [x] Consultar problemas públicos relevantes (#830, #822, #794/#837, #862, #875, #806, #805/#868, #847).
- [x] Classificar 273 operações de forma independente: 138 baixo, 102 médio, 32 alto, 1 não validado.
- [x] Bloquear 38 operações (29 de sistema + 9 aplicativos), incluindo Edge, Store, BingSearch, dependências Xbox,
      Widgets, WebExperience, `store.db`, serviço, tarefas agendadas, DISM, lotes e `start2.bin` multi-usuário.
- [x] Produzir relatório T0, catálogo auditável, matriz de riscos e arquitetura T1.
- [x] Definir shortlist T2 de 15 operações HKCU, sem elevação, e onda 2.
- [x] Fundamentar o escopo inicial do Debloat Manager e as condições que impedem remoção.
- [x] Definir critérios de aceite para iniciar a T1 e os 15 cenários de falha com proteções exigidas.
- [x] Atualizar ROADMAP, DISCOVERY, ARCHITECTURE, SECURITY e este arquivo; IA registrada como última capacidade.
- [x] Validar JSON, unicidade de IDs, campos obrigatórios, contagens e referências de linha.
- [x] Submeter o inventário e a classificação a revisão independente e registrar achados.
- [x] Confirmar que nenhuma configuração do Windows foi alterada e que nenhum script do upstream foi executado.

Limitações da T0: análise estática, sem teste funcional em VM; semântica de `UserPreferencesMask` e
`StickyKeys\Flags` não documentada; aplicativos classificados por família; efeitos de políticas de navegador
variam por versão; ajustes multi-monitor dependem de hardware; suíte de testes do produto não executada por
não haver alteração de código.

## T1 — Safety & Recovery Core (implementada em 0.3.0-preview.8)

- [x] Confirmar estado real: `master` com alterações locais preservadas, versão `0.3.0-preview.8`, schema 9,
  baseline **760/760**; não executar reset, clean, commit ou push.
- [x] Ler AGENTS, ARCHITECTURE, SECURITY, ROADMAP, VALIDATION, T0, arquitetura T1, revisão T0, catálogo
  `docs/planning/tweaks-debloat/` e `tasks/todo.md`; confirmar que os arquivos existem.
- [x] Consultar o índice Graphify existente (3.703 nós, commit `e579b3b3`, anterior ao HEAD `7630220`) para
  mapear Domain, Application, Infrastructure.Windows, Persistence, MemoryStore, RecordEnvelope e SQLite.
- [x] Definir contratos versionados de operação, alvo, estado observado, risco, compatibilidade, plano, simulação,
  autorização, backup, execução, verificação, transação, recuperação, conflito e limitação.
- [x] Implementar catálogo fechado validado e `T1SyntheticCatalog` com 14 operações de teste.
- [x] Implementar `SafetyEngine`, preflight, simulação sem efeitos, autorização vinculada/uso único, backup,
  revalidação, execução sintética, verificação e recuperação independente.
- [x] Implementar `SentinelCore.Infrastructure.Modifications` isolado, apenas com adaptadores `Synthetic*`, e
  testes de arquitetura provando a fronteira.
- [x] Implementar journal imutável event-sourced, backup imutável, relatórios, `RECOVERY.md`, SQLite esquema 10
  com migração aditiva 9→10 e reconstrução a partir dos Markdowns.
- [x] Implementar locks por alvo, lock de store existente, bloqueio de estado indeterminado, conflito de
  recuperação e ausência de sobrescrita forçada.
- [x] Adicionar 142 testes: catálogo, preflight, autorização, backup, execução, recuperação, persistência,
  concorrência, arquitetura, apresentação e fluxo sintético completo. Baseline anterior preservado.
- [x] Executar builds Debug e Release `0/0` e suítes completas **902/902** em ambas as configurações.
- [x] Submeter a duas revisões independentes (segurança e persistência/recuperação); ambas **APROVADA COM
  RESSALVAS**; remediar plano↔catálogo, reconferência de rollback, revalidação de backup no uso, truncagem/
  transições do journal, autorização persistida, hash v2 e raiz do adaptador sintético.
- [x] Executar revisão delta focada; corrigir D-01 (escape de texto livre no journal/envelope) e D-02
  (indeterminação não limpa após recuperação integral); adicionar regressões de confirmação injetada, sanitização,
  truncagem de cauda `ExecutionCompleted`, retomada parcial e reconferência de pós-estado no laço.
- [x] Atualizar ARCHITECTURE, SECURITY, ROADMAP, VALIDATION, README e o parecer da T1; preservar T0/T1 planning
  e não alterar o catálogo documental para fazê-lo passar em testes.
- [x] Confirmar que nenhuma configuração do Windows foi alterada e que nenhum executor real foi registrado.
- [x] Não executar commit, push ou deploy; não iniciar T2.

Limitações da T1: validação exclusivamente sintética; sem leitor/executor real de Registro ou VM; hash não
autentica contra o dono do perfil; locks cooperativos; confirmação de recuperação ainda é string vinculada ao
fluxo; journal sem marcador externo de fechamento; reconciliação completa do índice por evento; sem interface de
Tweaks/Debloat/Recovery Center. T2–T4 não iniciadas.

## T2A — First Controlled Windows Tweaks (implementada em 0.3.0-preview.8; VALIDAÇÃO REAL PENDENTE)

- [x] Confirmar o estado real: `master` com alterações locais preservadas, versão `0.3.0-preview.8`, schema 10,
  baseline **902/902** em Debug e Release; não executar reset, clean, commit ou push.
- [x] Ler AGENTS, ARCHITECTURE, SECURITY, ROADMAP, VALIDATION, T0, arquitetura T1, parecer T1, catálogo
  `docs/planning/tweaks-debloat/` e `tasks/todo.md`.
- [x] Mapear Diagnostic Engine, Safety Engine, catálogo, preflight, backup, autorização, recuperação, journal,
  `Infrastructure.Modifications`, `App` e `MainWindow`; preservar a separação de capacidades.
- [x] Estender o domínio com raízes permitidas (`Synthetic`, `HKCU`), validação estrutural HKCU, tipos de valor e
  limitações do perfil; nenhuma permissão de escrita foi entregue ao diagnóstico.
- [x] Implementar `T2aCatalog` fechado (`t2a-hkcu-1`) com as três operações e os quatro valores autorizados.
- [x] Implementar `T2aTargetGuard` no Safety Engine e `HkcuTargetPolicy` independente no adaptador nativo;
  teste garante que as listas coincidem e que alvo HKCU fora delas é bloqueado.
- [x] Implementar `HkcuRegistryAccess`, reader, executor, sonda de política, perfil Windows 11 e proteção HKCU.
  Somente `Registry.CurrentUser`; sem HKLM/HKCR/HKU, `CreateSubKey`, `DeleteSubKey`, `RegistryHive`, script,
  PowerShell, `reg.exe`, `.reg`, DISM, Appx, serviço, tarefa ou elevação.
- [x] Implementar `TweakManager`, modos `Validation`/`RealControlled`/`RealDisabled`, confirmação de execução
  digitada e confirmação de recuperação vinculada à transação.
- [x] Integrar ao fluxo T1: preflight, estado real, plano/hash, simulação, autorização, revalidação, backup,
  journal, execução, verificação e recuperação. O modo escuro tem dois alvos e não é atômico.
- [x] Reutilizar o Recovery Core: valor existente, ausência original, recuperação parcial de múltiplos valores,
  conflito sem sobrescrita, backup adulterado/duplicado, reabertura, relocação da raiz e rollback repetido.
- [x] Implementar a aba **Personalização** com três cartões, plano, confirmação, histórico e Recovery Center
  inicial; carregamento da aba só lê/simula, não executa alteração.
- [x] Adicionar **68 testes** (970 no total) para catálogo, allowlist, leitura, execução, autorização,
  backup/journal/verificação, falha parcial, recuperação, conflito, persistência, relocação, política,
  composição desabilitada e arquitetura.
- [x] Executar builds Debug/Release `0/0` e suítes completas **970/970 em Debug e Release**.
- [x] Submeter a duas revisões independentes (segurança e persistência/recuperação); ambas **APROVADO COM
  RESSALVAS**, sem bloqueante. Corrigir os achados Médios/Altos e cobrir com testes:
  revalidação de recuperação contra o catálogo, texto/`Changed` do journal real, autorização de plano fora do
  catálogo, política fail-closed, recuperação parcial de múltiplos valores, transação sem efeito lida, resultado
  derivado dos passos, duplicidade de backup, confirmação vinculada, transição antes da gravação e instante de
  observação por entrada. Ver `docs/validation/2026-11-02-tweaks-debloat-t2a-quality-gate.md`.
- [x] Validar a interface real em diretório isolado: aba Personalização, três cartões com estado do HKCU, plano
  simulado por clique real em **Detalhes**; nenhuma transação foi registrada e `memory/transactions` permaneceu
  vazio.
- [x] Confirmar que **nenhuma escrita real foi executada** no computador de desenvolvimento e que a capacidade
  real permanece indisponível na distribuição (`RealDisabled`; `--enable-real-tweaks` é opt-in de validação).
- [x] Atualizar ARCHITECTURE, SECURITY, ROADMAP, VALIDATION, README, AGENTS, este arquivo, a arquitetura T2A e o
  parecer do quality gate; preservar T0/T1 e não alterar documentos históricos.
- [x] Não executar commit, push ou deploy; não iniciar T2B, T3 ou T4.

**Veredito:** **T2A IMPLEMENTADA — VALIDAÇÃO REAL PENDENTE.** A etapa não é declarada pronta para distribuição
até a execução real e a recuperação serem validadas em VM Windows 11 isolada, com snapshot e autorização
específica. Riscos residuais: janela TOCTOU no Registro; efeito visual pode exigir reabrir/atualizar o Explorer;
hash sem chave não autentica contra o dono do perfil; caminho absoluto antigo do backup permanece no payload
(a leitura remapeia a raiz); UIA não percorre o conteúdo do `PageHost`, então a validação visual usou captura e
clique real.

### Fechamento técnico — separação de builds (02/11/2026)

- [x] Verificar o parâmetro `--enable-real-tweaks` e confirmar que o executável de distribuição conseguia compor
  a escrita real apenas pela linha de comando.
- [x] Estabelecer barreira de compilação: `SentinelCoreRealTweaks=false` por padrão; somente
  `-p:SentinelCoreRealTweaks=true` define `SENTINELCORE_REAL_TWEAKS`. No build de distribuição
  `RealTweakBuildGate.AllowsRealExecution` retorna `false` sem interpretar argumentos.
- [x] Manter a infraestrutura de execução/recuperação para a validação em VM; criar `publish-validation.ps1`
  (build experimental, diretório separado, marcador `VALIDATION-BUILD.txt`) e fixar `false` em `publish.ps1`.
- [x] Corrigir o repasse de argumentos do wrapper `scripts/dotnet.ps1` para que `-p:` chegue ao MSBuild.
- [x] Adicionar 10 testes direcionados de bloqueio de distribuição; suíte **980/980** em Debug e Release,
  builds `0/0`.
- [x] Verificar `DefineConstants` com e sem a propriedade e validar os dois scripts de publicação.
- [x] Nenhuma escrita real no Registro e nenhum tweak executado; publicação apenas compilada, não iniciada.
- [x] Não executar commit, push ou deploy; não iniciar T2B, T3 ou T4.
- [x] Registrar formalmente que a T2A continua **aguardando validação real em VM Windows 11 com snapshot**
  ([relatório](docs/validation/2026-11-02-tweaks-debloat-t2a-build-separation.md)).

## T2B — Windows Tweaks Expansion (implementada; VALIDAÇÃO REAL PENDENTE)

- [x] Confirmar o estado real: `master` com as alterações locais preservadas, versão `0.3.0-preview.8`, schema 10,
  baseline **980/980** em Debug e Release; não executar reset, clean, commit ou push.
- [x] Ler AGENTS, ARCHITECTURE, SECURITY, ROADMAP, VALIDATION, T0, arquitetura T1, parecer T2A, catálogo
  `docs/planning/tweaks-debloat/` e `tasks/todo.md`; confirmar caminhos e compatibilidade no catálogo auditado.
- [x] Expandir o catálogo fechado para `t2b-hkcu-1`, incorporando as três operações da T2A e as seis novas, sem
  catálogo executável paralelo.
- [x] Implementar `SC-TW-EXP-06` (`Hidden=1`), `SC-TW-TSK-06` (`ShowTaskViewButton=0`) e os quatro modos de
  `SearchboxTaskbarMode` (`SC-TW-TSK-02=0`, `SC-TW-TSK-03=1`, `SC-TW-TSK-05=2`, `SC-TW-TSK-04=3`).
- [x] Estender `HkcuTargetPolicy` para as sete identidades exatas e substituir o guard da T2A por
  `HkcuCatalogGuard`, derivado do catálogo expandido.
- [x] Implementar `TaskbarSearchModeController` com quatro opções mutuamente exclusivas; selecionar apenas
  prepara o plano e nenhum valor é presumido.
- [x] Implementar `TweakPresentation` para agrupar os cartões em Explorador de Arquivos, Barra de tarefas e
  Aparência, sem lógica de negócio no code-behind.
- [x] Integrar as novas operações ao fluxo T1/T2A: preflight, estado observado, plano/hash, simulação, backup,
  autorização, revalidação, journal, execução, verificação, histórico e recuperação.
- [x] Ampliar a aba **Personalização** com os nove cartões agrupados, o controle único de pesquisa e o painel de
  plano/histórico/recuperação; nenhum botão decorativo.
- [x] Preservar o build gate: `T2bCatalog` na composição, `RealDisabled` por padrão, nenhuma alteração em
  `RealTweakBuildGate`, nenhum build experimental executado.
- [x] Adicionar **88 testes** sintéticos (1068 no total) cobrindo catálogo, allowlist, leitura, execução, modos de
  pesquisa, conflitos sucessivos, persistência, reconstrução SQLite, apresentação, distribuição e regressões.
- [x] Executar builds Debug/Release `0/0` e suítes completas **1068/1068** em Debug e Release.
- [x] Submeter o estado a duas revisões independentes (segurança e persistência/recuperação), exclusivamente
  estáticas/sintéticas, e registrar os pareceres.
- [x] Confirmar que **nenhuma escrita real foi executada**, nenhuma VM foi criada e nenhum build experimental foi
  iniciado; a capacidade real permanece indisponível na distribuição.
- [x] Atualizar ARCHITECTURE, SECURITY, ROADMAP, VALIDATION, AGENTS, README, este arquivo e o relatório da T2B.
- [x] Não executar commit, push ou deploy; não iniciar T3 ou T4.

**Veredito:** **T2B — IMPLEMENTAÇÃO CONCLUÍDA.** A validação real dos novos valores, do efeito visual e da
recuperação permanece pendente por decisão de projeto e será concentrada na etapa futura única de Tweaks,
Debloat e Recovery Center. A T3 não foi iniciada.

## R1 — Windows Repair Center (implementada; VALIDAÇÃO REAL PENDENTE)

- [x] Confirmar o estado real: `master` com as alterações locais preservadas, versão `0.3.0-preview.8`, schema 10,
  baseline **1068/1068** em Debug e Release; não executar reset, clean, commit ou push.
- [x] Ler AGENTS, ARCHITECTURE, SECURITY, ROADMAP, VALIDATION, T2B, `tasks/todo.md` e a documentação T1/T2A/T2B.
- [x] Mapear Diagnostic Engine, Safety Engine, catálogo, preflight, autorização, persistência, Recovery Center e UI;
  preservar a separação de capacidades e não reutilizar o rollback de Tweaks para reparos.
- [x] Criar contratos tipados no Domain para ferramenta, operação, volume, invocação, perfil, preflight, plano,
  autorização, progresso, resultado, evidência, registro, intenção, histórico e hash.
- [x] Implementar o catálogo fechado `r1-windows-repair-1` com oito operações oficiais e nove operações avançadas
  somente documentais/indisponíveis.
- [x] Implementar allowlist exata de argumentos, volume validado `A:`–`Z:` e recusa de operação avançada disponível,
  risco alto/não validado, volume indevido e comando/caminho livre.
- [x] Implementar `RepairPreflightEngine`, `RepairInvocationBuilder`, `RepairAuthorizationService`,
  `RepairResultInterpreter`, `RepairStateMachine`, `RepairCenter` e `RepairPresentation`.
- [x] Separar simulação de execução real; simulação exige Windows 11, ferramenta disponível e volume válido; execução
  real é bloqueada por gate independente e ausência de broker de elevação.
- [x] Implementar `SentinelCore.Infrastructure.Repairs` isolado, com perfil, volumes somente leitura, política de
  caminho oficial em `System32` e executor exclusivamente sintético. Nenhum processo é iniciado.
- [x] Implementar fixtures sintéticas para sucesso, erro, acesso negado, sistema incompatível, corrupção encontrada/não
  encontrada, reparação parcial, reinicialização, fonte ausente, volume indisponível, sistema de arquivos incompatível,
  saída incompleta, interrupção, cancelamento e WinRE habilitado/desabilitado.
- [x] Implementar intenção antes do efeito, autorização de uso único `AUTORIZAR REPARO <id> <hash12>`, consumo,
  revalidação de plano/estado/volume e bloqueio de reutilização.
- [x] Implementar persistência `memory/repairs/<id>/intent.md`/`record.md`, `reports/repairs/<id>.md`,
  `memory/REPAIRS.md` e SQLite esquema 11 com migração aditiva 10→11.
- [x] Implementar a aba **Reparos do Windows** com quatro categorias, volume validado, plano, confirmação, cenário
  sintético, barra indeterminada, tempo decorrido, saída, histórico, intenções pendentes e relatório.
- [x] Preservar o build gate: `SentinelCoreRealRepairs=false`, `RealExecutionImplemented=false`, `RepairCenter` recusa
  modo real, `publish.ps1` fixa o gate em falso e nenhum executor real é composto.
- [x] Adicionar **97 casos de teste** (1165 no total) cobrindo catálogo, argumentos, volumes, caminho oficial,
  preflight, autorização, plano adulterado, reutilização, volume alterado, estados, interpretação, saída/limites,
  persistência, imutabilidade, intenção órfã, corrupção, reconstrução SQLite, migração, arquitetura, gate e UI.
- [x] Executar builds Debug/Release `0/0`, restore locked-mode e suítes **1165/1165 em Debug e 1165/1165 em Release**.
- [x] Validar a UI em diretório isolado por captura: aba, banner de simulação, aviso de fixture, volume C: NTFS e
  cartões de SFC/DISM; nenhuma fixture executada nessa abertura e fechamento gracioso.
- [x] Confirmar que **nenhum SFC, DISM, CHKDSK ou REAGENTC real foi executado**, nenhuma elevação foi solicitada,
  nenhuma VM foi criada e nenhuma configuração do Windows foi alterada.
- [x] Atualizar ARCHITECTURE, SECURITY, ROADMAP, VALIDATION, README, AGENTS, este arquivo e o relatório da R1.
- [x] Não executar commit, push ou deploy; não iniciar T3, T4 ou validação real automaticamente.

**Veredito:** **R1 IMPLEMENTADA — VALIDAÇÃO REAL PENDENTE.** A aba, o catálogo, os contratos, o preflight, a
autorização, a máquina de estados, o executor sintético, a interpretação, a persistência, o histórico, a UI e o gate
de distribuição estão implementados e testados. A execução real das quatro ferramentas permanece indisponível e exige
uma etapa futura isolada com autorização específica, proteção e fronteira privilegiada própria.

## T0 — revisão documental de 04/11/2026

- [x] Preservar T0/T1/T2A/T2B/R1 existentes; baseline encontrada `2ccc446`, árvore inicialmente limpa, schema 11.
- [x] Reconfirmar upstream `5d88bfe9ef2be5a8a643166a6400eb78a30c46ec`, MIT e ausência de commit posterior em master.
- [x] Reconferir 479 operações de Registro e cobertura dos 104 FeatureIds; Apps é seletor sem efeito autônomo.
- [x] Produzir catálogo schema 2 com 273 fichas completas, matriz, 175 hashes e verificador somente leitura.
- [x] Corrigir riscos: 49 BAIXO, 37 MÉDIO, 33 ALTO, 154 NÃO VALIDADO; 192 bloqueadas no planejamento.
- [x] Corrigir shortlist para 13 escolhas; separar variantes Registro de efeitos compostos upstream.
- [x] Aplicar revisão independente direcionada; preservar fichas/pareceres anteriores como histórico.
- [x] Validar 14.024 verificações estruturais, fontes e hashes, sem rodar suíte funcional para mudança documental.
- [x] Reafirmar IA como última grande capacidade, sem execução real, commit ou push.

**Veredito T0:** **APROVADA COM RESSALVAS.** Referência vigente:
[suplemento](docs/planning/TWEAKS_DEBLOAT_T0_REVALIDATION_2026-11-04.md). Os números 463/217 e 138/102/32/1
do checklist original são registros anteriores às correções históricas; não são contagens vigentes.
O pedido posterior autoriza T3 exclusivamente sintética; não autoriza consultas funcionais a pacotes reais ou T4.

## T3 — Debloat Manager (04/11/2026; exclusivamente sintético)

- [x] Confirmar baseline Debug 1165/1165, HEAD `2ccc446`, preservar trabalho T0 desta sessão.
- [x] Ler docs/relatórios e mapear engine/journal/composição com Graphify e fontes.
- [x] Integrar catálogo documental fechado: 162 entradas, 159 Appx/3 WinGet, riscos preliminares e reais distintos.
- [x] Implementar inventário tipado/sintético e identidade exata, escopos/dependências, bloqueios e revisão média.
- [x] Reutilizar Safety Engine e autorização, snapshot/hash, intenção antes do efeito e verificação em memória.
- [x] Isolar adaptadores sintéticos em assembly Domain-only, preservando barreiras de Tweaks/diagnóstico.
- [x] Implementar cenários determinísticos, estados parciais/interrompidos/indeterminados e falhas de persistência.
- [x] Reutilizar journal em partição DBLT/Synthetic, mesmo SQLite, migração aditiva 11→12 e relatórios separados.
- [x] Entregar WPF na página existente, filtros/pesquisa/seleção/plano/confirmação/cancelamento/histórico e reinstalação informativa.
- [x] Fixar gate real independente falso sem argumento/configuração/variável capaz de habilitar execução.
- [x] Adicionar 78 casos; 1243/1243 Debug e Release; builds 0/0; restore locked normal e single-file.
- [x] Validar controle WPF real em host/raiz isolados, com somente fixtures: 19 verificações PASS e capturas.
- [x] Executar revisões A segurança e B persistência, corrigir três achados médios e revalidar delta.
- [x] Publicar self-contained em saída isolada com os três gates falsos, sem iniciar executável publicado.
- [x] Atualizar documentação e MIT; nenhum dado de produção ou publicação anterior substituído.
- [x] Não consultar pacotes reais, remover, reinstalar, elevar, criar VM/snapshot, iniciar T4, commit ou push.

**T3 — IMPLEMENTAÇÃO CONCLUÍDA EM MODO SINTÉTICO.** [Relatório](docs/validation/2026-11-04-debloat-manager-t3.md).
Validação real é pendência futura e não bloqueia a conclusão desta implementação sintética.

## T3 — correção semântica de presença e versionamento (04/11/2026)

- [x] Preservar as entregas T0 revisada e T3; nenhum reset, clean, stash ou rebase.
- [x] Substituir “não encontrado” de entradas sem observação por **Não verificado — inventário real indisponível**.
- [x] Adicionar `DebloatPresence` e `DebloatEligibility` ao Domain, com rótulos em `DebloatPresentation`.
- [x] `LoadAsync` deixou de sintetizar `NotFound`; entradas do catálogo usam `Unknown` sem observação.
- [x] Separar visualmente **Catálogo de aplicativos** e **Inventário de aplicativos**, com origem e presença por linha.
- [x] Ajustar filtros para presentes, ausentes no inventário consultado e não verificados.
- [x] Preservar as cinco fixtures sintéticas e não promover risco para tornar aplicativo elegível.
- [x] Adicionar 4 casos de regressão e 1 contrato XAML; **1247/1247 Debug e Release**, builds 0/0.
- [x] Confirmar `RealDebloatBuildGate` falso e incondicional; `RealTweakBuildGate`/`RealRepairBuildGate` preservados.
- [x] Nenhuma consulta a pacotes reais, remoção, reinstalação, elevação, VM, snapshot, T3B, T3C ou T4.

**T3 IMPLEMENTADA EM MODO SINTÉTICO.** Oferece catálogo, simulação, interface, autorização, histórico, persistência
e proteções. Não oferece inventário nativo, remoção real, reinstalação real, recuperação de dados removidos nem
validação operacional no Windows. **T3B** (inventário nativo somente leitura) e **T3C** (remoção controlada, mantida
bloqueada) não foram iniciadas. A capacidade real não está pronta.
