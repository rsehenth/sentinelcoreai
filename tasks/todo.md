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
