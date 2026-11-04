# SentinelCore AI — instruções do projeto

## T3C — remoção controlada compilável (04/11/2026)

- **T3C — MOTOR NATIVO DE REMOÇÃO IMPLEMENTADO, EXECUÇÃO REAL BLOQUEADA E NÃO VALIDADA.**
  [Relatório](docs/validation/2026-11-04-controlled-app-removal-t3c.md). T4 não iniciada.
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
> T4 não foi autorizada. Restrições anteriores sobre implementar T3C são históricas, não autorização para executá-la.

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
  compilação: o build de distribuição (padrão) não compila a escrita real e `--enable-real-tweaks` não tem efeito,
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
  A **T3B** foi implementada na sequência (ver seção própria); a **T3C** (remoção controlada) permanece bloqueada e
  não foi iniciada.

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
