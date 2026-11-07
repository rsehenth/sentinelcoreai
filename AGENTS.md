# SentinelCore AI — instruções do projeto

## V2 — isolamento da inicialização (07/11/2026)

- Relatório: `docs/validation/2026-11-07-v2-startup-isolation.md`; matriz `2026-11-07-v2-test-matrix.csv`.
  Seleção vigente: 1.598 descobertos, **348 Synthetic**, seis WindowsIntegration e 1.244 Unclassified não executados. Gates e T0 intactos.
- `--data-root` é validado por `DataRootArgument.Resolve` (Persistence) antes de qualquer composição: inválido/duplicado/relativo/UNC/unidade/proibido/
  raiz padrão/link ou junção → falha fechada, sem log e sem diretório. `App.LogFailure` só grava depois que a raiz foi aceita (`ApplicationLog`);
  argumento inválido nunca recua para `%LOCALAPPDATA%\SentinelCoreAI`; sem argumento, a raiz padrão continua em uso. Não reintroduzir `static dataRoot = DefaultRoot` nem log antes da validação.
- `MainWindow.OnLoaded` lê só o histórico de Reparos do store; o preflight (volumes, System32, elevação) ocorre ao abrir a página Reparos ou em Atualizar.
- `RealValidation`/`GraphicsValidation` continuam apontando para a raiz padrão real e **não estão autorizados** para a primeira validação.
  Não afirmar homologação operacional nem ausência de efeitos colaterais; a V2 é preparação sintética + inspeção estática.

## U3 — consolidação funcional (06/11/2026)

- Relatório: `docs/validation/2026-11-06-u3-functional-consolidation.md`; matriz `2026-11-06-u3-test-matrix.csv`.
  Seleção vigente: 1.586 descobertos, **336 Synthetic**, seis WindowsIntegration e 1.244 Unclassified não executados.
- Sem mudança em `src/`. Host `u3` (`scripts/RecoveryBenchmark/U3Host.cs`) usa só raiz temporária e fonte de monitoramento sintética.
  `MonitoringSessionRunner` não grava o registro final: quem chama (a UI) o faz; sem ele a sessão é recuperada como interrompida.
  CheckBox do monitoramento reage a `Click`. Não afirmar homologação operacional nem prontidão para execução real.
  Versionada na revisão final U3 (commit `test: validate integrated SentinelCore journeys`, push normal); próxima fase não iniciada.

## U2 — refinamento visual e UX (06/11/2026)

- Relatório: `docs/validation/2026-11-06-u2-visual-ux-refinement.md`; matriz `2026-11-06-u2-test-matrix.csv`.
  Seleção vigente: 1.576 descobertos, **326 Synthetic**, seis WindowsIntegration e 1.244 Unclassified não executados.
  Correções finais (mínimo 960×520, busca de Eventos, rolagem das páginas): ver seção 19; revisão final/publish na seção 20.
  Baseline `f7cb4e6`; o pedido U2 FINAL REVIEW & VERSIONING autoriza commit e push normal para origin/master após revisão sem bloqueantes.
  Publish final: 421 arquivos, 152.820.973 bytes, zero violações; não executado. U3 não iniciada; sem homologação operacional.
- Apresentação apenas: sem mudança em Domain/Application/Persistence, gates, esquema 12, journals ou executores.
  Rótulos em português ficam em `UiText.cs` (App); nunca traduzir enums persistidos, códigos ou identificadores.
- Menu: `Nav.Group`/`Nav.Collapsed` (`Nav.cs`) exigem `xmlns:local` em `App.xaml`; os hosts em `scripts/RecoveryBenchmark`
  declaram o mesmo prefixo ao reconstruir os recursos. Atalhos `Ctrl+1…9` e `Ctrl+B`; abaixo de 1120 px o menu não expande.
- Avisos: integridade por origem (`SetIntegrity`) persiste até a recarga da mesma origem; `ShowWarning` é da execução atual.
  Não voltar a escrever direto em `StorageWarnings`/`WarningBanner`.
- Não usar `Tag` de botão para dados (reservada ao ícone). Listas vazias dizem "carregado(a)", nunca "inexistente".
- `run-synthetic.ps1` compara nomes acentuados lendo o manifesto em ANSI: sem console em Windows-1252 ele recusa (fail-closed).
  Atualizar hashes só dos arquivos revisados (`artifacts/validation/u2/tools/manifest.py --apply --allow …`).
- DPI foi simulado (`SetRootDpi`) e acessibilidade verificada só por sondas estáticas; não afirmar conformidade WCAG.

## U1 — integração funcional e WPF (06/11/2026)

- Relatório: `docs/validation/2026-11-06-u1-functional-integration.md`; matriz sintética própria U1.
- Seleção vigente: 1.563 descobertos, **313 Synthetic**, seis WindowsIntegration e 1.244 Unclassified não executados.
  A contagem de 312 nas seções T4B abaixo é histórica. Cinco gates e T0 preservados.
- Fechamento U1: `docs/validation/2026-11-06-u1-final-versioning-review.md`. O pedido de revisão final autoriza
  commit e push normal no branch existente; a implementação anterior foi entregue sem commit/push. U2 não iniciada.
- Central é composta ao selecionar sua página; Debloat somente após a primeira carga de Tweaks, na página ainda selecionada.
  Não restaurar carga antecipada concorrente: o store.lock é fail-closed e pode recusar leituras/cargas em conflito.
- Host U1 injeta logger e armazenamento temporários; qualquer erro interno do shell deve falhar a rodada.
  Rodadas iniciais atingiram App.LogFailure e podem ter gravado application.log na pasta padrão: não afirmar isolamento integral
  dessas rodadas, não ler/apagar logs reais como limpeza. Não houve coletor ou manutenção Windows executados.
- 133.802,52 ms pertencem à **leitura crua** anterior à validação/locks. Reprodução pequena não repetiu o pico;
  causa externa continua indeterminada. Não afirmar correção do outlier, cache frio validado ou homologação operacional.

## T4B.3 — consolidação final, CONCLUÍDA no escopo sintético (06/11/2026)

- Relatório: `docs/validation/2026-11-05-t4b3-final-consolidation.md`. T4B.2 e T4B.3 concluídas no escopo sintético:
  100/1.000/5.000 operações medidas; **10.000 não medidas** (impedimento de tempo); host WPF até ≈ 1.000 linhas. Não é homologação operacional.
- Seleção vigente: 1.562 casos descobertos, **312 Synthetic**, 6 WindowsIntegration, 1.244 Unclassified (não executar).
  O runner executa a seleção em lotes (limite de 32.767 caracteres da linha de comando); mantém-se fail-closed.
  Atualizar o manifesto só com divergências revisadas; não regenerar hashes às cegas.
- `RecordEnvelope.Probe` **existe no binário de produção** (`internal`, `AsyncLocal`, nulo por padrão). Não adicionar
  `InternalsVisibleTo`, leitura de variável de ambiente/argumento/configuração nem carga dinâmica de assemblies em `src/`
  (guardado por `T4b3DistributionAndProbeTests`). O publish não contém worker, runner, testes, fixtures nem assemblies `.Native`.
- Não afirmar prova matemática/formal, durabilidade após falha do SO ou queda de energia, nem liberação imediata de handles:
  `WriteThrough`/`Flush` pedem a gravação, não a garantem; SHA-256 não autentica; locks são cooperativos.
- O executor HKCU (`HkcuRegistryOperationExecutor`) **está compilado** na distribuição; só o gate falso por construção e a composição com `DisabledOperationExecutor` impedem seu uso. Não escrever que o build "não compila a escrita real".
- Fixtures grandes: gerar em sequência, blocos de 25 operações, prioridade baixa, uma carga pesada por vez; apagar ao final.
- Ferramentas auxiliares (`scripts/RecoveryBenchmark`, `Inspect-Publish.ps1`, `test-synthetic-runner.ps1`) usam só fixtures
  temporárias; não geram fixtures grandes em paralelo sem necessidade (consomem muito o PC).
- Cinco gates fechados; nenhum executor real; T0 inalterada (153 NÃO VALIDADOS / 9 ALTO). Sem commit/push.

## T4B.1-S — subconjunto revalidado (05/11/2026)

- Seleção de 270 Synthetic revalidada em Debug/Release; seis WindowsIntegration e 1.244 Unclassified
  permanecem fora. Ver `docs/validation/2026-11-05-t4b1s-isolation-validation.md`.
- Não atualizar hashes indiscriminadamente nem executar casos não classificados. O manifesto não é sandbox;
  a validação real e o restante da T4B continuam pendentes.

## T4B.1 — incidente e cobertura crítica parcial (05/11/2026)

- O runner executou nesta continuação casos indevidamente classificados como sintéticos que consultaram
  privilégio do processo e diretório de perfil reais. Ver `docs/validation/2026-11-05-t4b1-critical-coverage.md`.
- Estado posterior à exclusão: 247 Synthetic, 21 WindowsIntegration, 1.248 Unclassified. Não presumir
  isolamento de fixtures com `Windows11SystemProfileReader` ou de `RepairOutputLimiter.Limit`.
- Os resultados e contagens abaixo são históricos; não apresentam a rodada de 217/266 como exclusivamente sintética.

## T4B.1 — isolamento parcial (05/11/2026)

- Atualização posterior: 146 casos Synthetic, seis WindowsIntegration e 1.364 Unclassified; ver
  `docs/validation/2026-11-05-t4b1-runner-and-coverage.md`. A contagem abaixo é histórica.
- 48 casos auditados Synthetic; seis WindowsIntegration; 1.462 Unclassified. Não executar os não classificados.
- Revisão de build/descoberta e testes negativos ainda incompleta; ver `docs/validation/2026-11-05-t4b1-isolation-progress.md`.

## T4B — em andamento (05/11/2026)

- T4B é parcial; a nota vigente e suas pendências estão em `docs/validation/2026-11-05-reliability-hardening-t4b-progress.md`.
- `tests/run-synthetic.ps1` seleciona positivamente apenas testes auditados; os demais permanecem fora da execução.
  Não converter a seleção em filtro negativo, nem atualizar hashes sem revisar os arquivos e dependências alterados.
- A rodada T4A também incluiu um teste que lê informações do processo atual; não descrevê-la como prova de isolamento integral.
- As afirmações abaixo de que T4B não foi iniciada são históricas. Limites de segurança e proibição de execução real permanecem.

## T4A — Central de Recuperação (04/11/2026)

- Escopo T4A autorizado: agregação somente leitura e recuperação funcional exclusivamente sintética.
  [Relatório](docs/validation/2026-11-04-recovery-center-t4a.md); [plano](docs/planning/RECOVERY_CENTER_T4A.md).
- `MemoryStore.ReadRecoveryHistoryAsync` lê as quatro fontes sem reconstruir SQLite, regravar projeções,
  criar diretórios ou modificar registros. Markdown segue autoritativo; SQLite esquema 12, nenhum novo banco.
- `RecoveryCenter` classifica/deduplica; fontes incompletas/inacessíveis não viram zero, sucesso ou autorização.
  Não aceitar versões/enums/recuperações contraditórias; preservar originais e avisos da mesma carga.
- App compõe `new RecoveryCenter(store)` sem serviço de recuperação. Host sintético pode injetar
  `RecoveryService(..., syntheticOnly: true)`, que recusa alvos não Synthetic e DBLT antes de ler/executar alvos.
  Reutilizar esse serviço T1; não criar outro rollback. Confirmação exata, prévia e revalidação permanecem obrigatórias.
- Debloat/T3C e R1 não possuem rollback equivalente. Sem reinstalação, dados pessoais, repetição de reparos,
  desbloqueio de alvo T3C, consulta de pacotes, Registro real, VM, elevação ou execução real nesta etapa.
- Preservar os cinco gates e a T0 (153 NÃO VALIDADOS/9 ALTO). Origem histórica desconhecida deve continuar desconhecida.
- T3C: implementação concluída; execução real bloqueada e validação prática pendente. T4B não iniciada/autorizada.
- Testes T4A e host WPF usam somente fixtures/raízes temporárias. Quatro testes legados com consultas Windows reais
  são excluídos da regressão desta etapa; nomes e comandos exatos constam no relatório. Sem commit/push.
- Verificação: 110 casos novos; **1.510/1.510 sintéticos Debug e Release**, builds **0/0**, host WPF **35 PASS**.
  Baseline 1.404 inclui quatro casos reais não executados; não apresentar esta rodada como execução integral de 1.514.

## T3C — remoção controlada compilável (04/11/2026)

- **T3C — MOTOR NATIVO DE REMOÇÃO IMPLEMENTADO, EXECUÇÃO REAL BLOQUEADA E NÃO VALIDADA.**
  [Relatório](docs/validation/2026-11-04-controlled-app-removal-t3c.md). T4A sucede esta entrega; T4B não iniciada.
- O pedido T3C autorizou somente contratos, preflight, fluxo/testes sintéticos, apresentação e compilação do novo
  `SentinelCore.Infrastructure.Debloat.Removal.Native`. Não executar `RemovePackageAsync` nem consultas reais.
- Novo assembly Domain-only, sem referência App/testes e ausente do publish. Inventário T3B não recebe remoção.
  `DebloatRemovalBuildGate` é incondicionalmente falso na entrada e antes da chamada privada; não criar opt-in.
- Reutilizar SafetyEngine, AuthorizationService, locks, snapshot/journal DBLT/Synthetic e SQLite schema 12.
  `RemovalReceipt` opcional em eventos preserva envelopes anteriores; null é omitido. Snapshot T3C schema 1.
- Identidade completa/SID/catálogo/revisão/estado/dependências/consequências vinculados ao plano e revalidados.
  T0 permanece 153 NÃO VALIDADOS / 9 ALTO. Nenhuma confirmação ou fixture autoriza aplicativo real.
- Cancelamento só antes da entrada do executor. Após início, acompanhar sem cancelamento; progresso somente informado.
  Verificação T3C inconclusiva com possível efeito mantém bloqueio persistente do alvo, inclusive após reabertura.
- Nenhum rollback/reinstalação/recuperação automática de apps/dados. Snapshot é somente metadados.
- A página mantém ações T3. Componentes WPF T3C são somente de apresentação e foram exercitados em host sintético.
- Verificação: **1.404/1.404 Debug e Release**, 108 casos novos, builds **0/0**, 15 verificações WPF sintéticas.
  Nenhuma consulta de pacotes reais, remoção real, modificação do Windows ou elevação solicitada.

> **Estado vigente de 04/11/2026:** T3 sintética, T3B e T3C compiláveis implementadas, conforme seções próprias.
> O pedido T3C autorizou contratos, fluxo sintético e adaptador nativo separado, sem executá-lo.
> Execução real, consultas funcionais a pacotes e composição dos adaptadores nativos continuam proibidas.
> O pedido posterior autoriza T4A somente nos limites acima. T4B/validação real não autorizadas.
> Restrições anteriores sobre implementar T3C são históricas, não autorização para executá-la.

## Escopo e arquitetura

- C#, .NET 10 LTS, WPF nativo e Windows x64. Não converter para web, Electron, Tauri ou Python.
- Domain define contratos tipados; Application orquestra e aplica validade; Infrastructure.Windows coleta;
  Persistence gerencia Markdown/SQLite; App contém apresentação e composição, sem consultas WMI.
- Sem dependências circulares. Evitar abstrações especulativas e novas dependências sem necessidade demonstrada.
- Antes de alterar, ler README.md, docs/ARCHITECTURE.md, docs/SECURITY.md, docs/VALIDATION.md e tasks/todo.md.
- Preservar identidade visual escura azul/grafite/ciano e renderização por software até validação gráfica específica.

## Segurança obrigatória

**Agentes futuros não podem modificar o sistema operacional sem autorização específica do usuário.**
O diagnóstico é somente de leitura. A única exceção implementada é a T2B, que escreve sete valores HKCU
(`HideFileExt`, `TaskbarAl`, `AppsUseLightTheme`, `SystemUsesLightTheme`, `Hidden`, `ShowTaskViewButton` e
`SearchboxTaskbarMode`) pelo Safety Engine, com catálogo fechado, backup, autorização digitada, journal e
verificação; essa escrita está desabilitada na distribuição (`RealDisabled`) até a validação real. São proibidos:
alterar Registro fora da T2B, HKLM/HKCR/HKU, serviços,
processos do usuário, drivers, BIOS, firmware, energia, proteções, arquivos de sistema, reparos ou reinícios.
Não solicitar elevação para iniciar. Informação inacessível deve gerar limitação, não tentativa de contornar permissões.
Não instalar ferramentas globais ou alterar variáveis permanentes. O SDK local só foi autorizado nesta sessão.
Sem IA, APIs externas, telemetria ou execução de comandos derivados dos dados. Consultas WMI fixas e locais.
Não coletar senhas, tokens, cookies, documentos, navegação, conteúdo de projetos ou tráfego.
Escritas apenas neste repositório, build/testes, temporários necessários e diretório de dados SentinelCoreAI.

## Dados e histórico

- Não apagar auditorias reais, logs, relatórios ou banco de produção sem autorização explícita.
- Nunca sobrescrever registros em memory/audits. Mesmo ID e payload idêntico é repetição idempotente;
  mesmo ID e payload diferente deve falhar.
- Markdown histórico é fonte durável com formato versionado. SQLite é índice reconstruível.
- Mudanças de formato precisam de migração/compatibilidade e testes. Não interpretar enum desconhecido como sucesso.
- Preserve arquivos corrompidos, emita aviso e exclua da validade. Não fabricar auditorias antigas.
- Nulos significam indisponível, não zero. Datas em UTC; UI no fuso local. Duração por relógio monotônico.
- Auditorias parciais, canceladas ou com falha não renovam último sucesso. Relógio injetável nos testes.
- Não afirmar integridade de hardware ou estabilidade porque uma consulta retornou OK/Healthy.

## Verificação

- Usar scripts/dotnet.ps1; restore com lockfiles, builds Debug e Release, testes, publish self-contained.
- Testes de persistência usam raízes temporárias isoladas, nunca o diretório de produção.
- Testar falhas/cancelamentos/corrupção com fakes. UI real nunca usa fakes.
- Verificar interface real, auditoria, Markdown, SQLite e reabertura antes de declarar conclusão.
- Documentar resultados exatos e limitações; não confundir compilação com validação funcional.
- Não criar remoto, commit, push ou deploy automaticamente.

## Personalização & Debloat — T3 sintética e T3B nativa implementadas; execução real exige autorização

- O módulo é opcional e exclusivo para Windows 11. Consultar
  `docs/planning/TWEAKS_DEBLOAT_DISCOVERY.md`, `docs/planning/TWEAKS_DEBLOAT_T2A_ARCHITECTURE.md`,
  `docs/ARCHITECTURE.md` e `docs/ROADMAP.md` antes de propor qualquer ampliação.
- A T1 (Safety & Recovery Core), a T2A (três operações HKCU) e a T2B (seis operações adicionais) estão
  implementadas. O catálogo publicado é `t2b-hkcu-1` e contém nove operações: `HideFileExt=0`, `TaskbarAl=0`,
  `AppsUseLightTheme=0`, `SystemUsesLightTheme=0`, `Hidden=1`, `ShowTaskViewButton=0` e `SearchboxTaskbarMode`
  (0–3). Não há HKLM/HKCR/HKU, criação de chave, script, PowerShell, `reg.exe`, `.reg`, Appx, serviço, tarefa,
  DISM ou elevação.
- **A execução real permanece desabilitada na distribuição (`RealDisabled`).** A separação é feita em tempo de
  compilação do gate: o executor HKCU permanece compilado, mas o gate falso e `DisabledOperationExecutor` bloqueiam seu uso;
  `--enable-real-tweaks` não tem efeito no build de distribuição,
  mesmo por chamada interna. Somente builds experimentais de validação (`-p:SentinelCoreRealTweaks=true`) compilam
  o executor HKCU e aceitam o opt-in; esses builds são exclusivos para a futura validação real e nunca devem ser
  distribuídos. Não executar escrita real no computador de desenvolvimento.
- **Diretriz vigente:** durante o desenvolvimento dos módulos Tweaks, Debloat e Recovery Center, não serão
  realizados testes reais no Windows (nem VM/snapshot). Os testes automatizados são obrigatórios e usam mocks,
  fakes, adaptadores em memória e armazenamento temporário isolado. A validação real será concentrada em uma etapa
  futura única, após a conclusão dos módulos. Isso não autoriza declarar operações não validadas como seguras nem
  habilitar a execução real na distribuição.
- A execução real da T3 e o Recovery Center completo continuam não autorizados. Não adicionar operações, abas vazias,
  botões decorativos ou um segundo Safety Engine/backup/journal.
- Preservar separação entre Diagnostic Engine (leitura) e Controlled Modification Engine da T2B (operações
  tipadas, autorizadas e auditáveis); diagnóstico e IA não recebem acesso implícito ao executor.
- Risco alto e classificação não validada são bloqueados no backend, inclusive em chamadas internas.
  Baixo/médio exigem análise individual, proteção suficiente, simulação e autorização explícita.
- Safety Engine obrigatório; backup imediatamente anterior íntegro, ponto de restauração verificável quando
  exigido pela proteção da operação e journal durável. Não contornar falha de proteção ou política.
- Rollback restaura estado observado, inclusive ausência de valor, detecta mudanças posteriores, solicita
  confirmação e verifica o resultado. Reinstalação de aplicativo não promete recuperar seus dados.
- Win11Debloat é referência MIT: fixar commit, preservar avisos e auditar operação por operação na T0.
  Não executar scripts terceiros nem importar ajustes ou classificações upstream indiscriminadamente.
- Revisões documentais preservam código, testes, lockfiles, executáveis publicados e dados reais. Builds de
  verificação usam saída isolada. Não confundir revisão concluída com aprovação ou correções implementadas.

## R1 — Windows Repair Center — implementado; execução real desabilitada

- O módulo é independente do Tweaks Manager e cobre SFC, DISM, CHKDSK e WinRE em catálogo fechado
  (`r1-windows-repair-1`). Oito operações são simuláveis; as operações avançadas (`/f`, `/r`, `/x`, `/spotfix`,
  verificação offline, boot, `reagentc /enable|/disable|/boottore`, BCD e redefinição) são somente documentais.
- **Nenhuma ferramenta real pode ser executada nesta fase.** Não iniciar `sfc.exe`, `dism.exe`, `chkdsk.exe` ou
  `reagentc.exe`, nem por diagnóstico ou consulta. Não solicitar elevação, não criar broker/serviço privilegiado e
  não usar `cmd.exe`, PowerShell, script, `.reg` ou caminho configurável.
- O executor disponível é `SyntheticRepairExecutor`; toda saída é fixture marcada. O `RepairCenter` recusa modo real.
  O gate é independente do Tweaks (`SentinelCoreRealRepairs=false`, `RealExecutionImplemented=false`).
- Executáveis oficiais são resolvidos somente em `System32`, sem `PATH`; o volume de `chkdsk` vem de lista validada.
  Toda execução exige confirmação `AUTORIZAR REPARO <id> <hash12>` e autorização de uso único.
- Persistência: intenção antes do efeito, `memory/repairs/<id>/intent.md` e `record.md` imutáveis,
  `reports/repairs/<id>.md`, `memory/REPAIRS.md` e SQLite (migração R1 10→11; esquema atual 12). Intenção órfã é
  interrompida, nunca concluída; o Markdown é autoritativo.
- Reparações não possuem rollback equivalente aos Tweaks. Não prometer reversão de SFC/DISM/CHKDSK, recuperação por
  ponto de restauração ou correção de todas as falhas. Não executar reparação automática após auditoria, correlação,
  monitoramento ou IA; não alterar Windows Update, Defender, firewall ou políticas.
- A validação real das quatro ferramentas permanece pendente e exige etapa isolada com autorização específica,
  proteção e fronteira privilegiada própria. Não declarar a R1 pronta para uso real sem essa validação.

## T3 — Debloat Manager — implementado exclusivamente em modo sintético

- O pedido de 04/11/2026 autorizou a implementação T3 sintética. Essa autorização não permite inventário
  funcional de pacotes reais, remoção, reinstalação, downloads de apps, provisionamento, outros usuários, WinGet,
  PowerShell de aplicativos, VM, snapshot, elevação ou executores experimentais. Não iniciar T4 automaticamente.
- Consultar `docs/validation/2026-11-04-debloat-manager-t3.md`, os pareceres A/B e
  `docs/planning/TWEAKS_DEBLOAT_T0_REVALIDATION_2026-11-04.md`. A T0 schema 1 permanece histórica; o schema 2
  bloqueia 153 apps NÃO VALIDADOS e nove ALTO para remoção real. Os riscos preliminares 86/67/9 não autorizam nada.
- Catálogo embarcado `t3-debloat-synthetic-1`: 162 entradas (159 Appx/3 WinGet); somente cinco definições de
  fixture compõem o subconjunto operacional sintético. Bloco de Notas exige revisão média adicional. Não aceitar
  wildcard, correspondência parcial, identidade incompleta, dependência desconhecida ou pacote protegido/framework/recurso.
- `SentinelCore.Infrastructure.Debloat` referencia apenas Domain e contém somente adaptadores sintéticos.
  O executor nativo compilável autorizado posteriormente fica em assembly T3C separado; remover apps continua proibido.
  `RealDebloatBuildGate` recusa execução real incondicionalmente;
  `SentinelCoreRealDebloat=false` é independente de Tweaks e Repairs e não possui opt-in operacional.
- Reutilizar o Safety Engine existente com snapshot tipado da identidade/catálogo/cenário, token expirável de uso
  único e confirmação específica. Não criar segundo motor de segurança, banco ou recuperação automática.
- Persistência SQLite esquema **12**, migração aditiva 11→12; partição fixa `CreateDebloatJournal()` em
  `memory/debloat/<id>/journal`, snapshot de metadados, `reports/debloat` e `memory/DEBLOAT.md`. Prefixo DBLT e
  raiz Synthetic obrigatórios. Preservar journal, histórico Tweaks/R1/diagnóstico e arquivos corrompidos.
- Snapshot de metadados não é backup de dados de app. Não expor RecoveryService/rollback/reinstalação como
  recuperação completa. UI mostra origem sintética, risco preliminar/efetivo/real e avisos da mesma carga histórica.
- Presença e elegibilidade são eixos explícitos: `DebloatPresence` (`NotVerified`, `PresentInSyntheticInventory`,
  `AbsentInConsultedInventory`, `IdentityDivergent`, `Indeterminate`) e `DebloatEligibility`
  (`SimulationAvailable`, `UnavailableForSimulation`, `ProtectedOrBlocked`). Entrada do catálogo sem observação
  aparece como **“Não verificado — inventário real indisponível”** e nunca como ausente ou não instalada. A UI separa
  **Catálogo de aplicativos** de **Inventário de aplicativos**.
- Verificação registrada da T3: 1247/1247 Debug e Release, builds 0/0 e 19 verificações WPF em host exclusivamente
  sintético. Validação real permanece pendente e não é necessária para prosseguir no desenvolvimento sintético.
  A **T3B** foi implementada na sequência (ver seção própria); a **T3C** também foi implementada posteriormente,
  com execução real bloqueada e validação prática pendente. A T4A segue a seção vigente acima.

## T3B — Inventário nativo Appx/MSIX — implementado; execução e validação reais pendentes

- O pedido de 04/11/2026 autorizou a implementação do inventário nativo **sem consultar pacotes reais**. Essa
  autorização cobre contratos, correspondência com o catálogo, adaptador WinRT compilável e testes sintéticos; **não**
  autoriza executar a enumeração, compor o adaptador na distribuição, remover/reinstalar/provisionar ou abrir T3C/T4.
- `SentinelCore.Infrastructure.Debloat.Native` (`net10.0-windows10.0.22621.0`, `SupportedOSPlatformVersion
  10.0.22000.0`) referencia apenas Domain. Usa `PackageManager.FindPackagesForUser(string.Empty)` (usuário atual),
  sem outro SID, elevação, PowerShell, WinGet, processo externo, `FindPackages()` irrestrito ou API de remoção,
  registro, deploy ou provisionamento. Não adicionar essas capacidades sem autorização específica.
- App e testes **não referenciam** o assembly nativo; a solução apenas o compila. Nenhum argumento ativa o adaptador.
  `NativeDebloatInventoryGate` é sempre falso. Preservar integralmente os três gates reais.
- Contratos: `IDebloatNativeInventoryReader` somente leitura, `DebloatInventoryQueryResult`, estados
  `NotExecuted`/`Completed`/`CompletedNoResults`/`Partial`/`AccessDenied`/`Interrupted`/`Failed`/`Unavailable`,
  completude `NotObserved`/`Complete`/`Partial`/`Unavailable` e metadados Appx/MSIX tipados.
- Ausência só é confirmada por consulta completa e sem erro fatal; consulta não executada, negada, interrompida,
  parcial, com falha ou indisponível nunca confirma ausência nem reduz risco. Risco real T0 preservado
  (9 ALTO / 153 NÃO VALIDADO). `DebloatNativeAssessment` não pode ser entregue ao `DebloatManager`.
- Correspondência somente por nome técnico exato; o catálogo não possui PFN/publisher/versão/arquitetura por entrada.
  Duplicidade, múltiplas versões, identidade divergente, framework/resource/opcional/bundle e fora do catálogo ficam
  bloqueados. Aplicativo fora do catálogo aparece como “Aplicativo identificado — fora do catálogo de remoção.”
- UI: painel somente leitura “Inventário nativo Appx/MSIX — não habilitado”; sem botão de consulta ou ação real.
- Verificação: 1296/1296 Debug e Release (49 casos novos sobre 1247), builds 0/0. Nenhuma consulta real, remoção ou
  elevação. Validação real pendente. Relatório: `docs/validation/2026-11-04-debloat-native-inventory-t3b.md`.
