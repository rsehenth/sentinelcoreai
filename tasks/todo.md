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
