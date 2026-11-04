# SentinelCore AI — instruções do projeto

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

## Personalização & Debloat — T1/T2A/T2B implementadas; T3 exige nova autorização

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
- A T3 (Debloat) e o Recovery Center completo continuam não autorizados. Não adicionar operações, abas vazias,
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
  `reports/repairs/<id>.md`, `memory/REPAIRS.md` e SQLite esquema 11 (migração aditiva 10→11). Intenção órfã é
  interrompida, nunca concluída; o Markdown é autoritativo.
- Reparações não possuem rollback equivalente aos Tweaks. Não prometer reversão de SFC/DISM/CHKDSK, recuperação por
  ponto de restauração ou correção de todas as falhas. Não executar reparação automática após auditoria, correlação,
  monitoramento ou IA; não alterar Windows Update, Defender, firewall ou políticas.
- A validação real das quatro ferramentas permanece pendente e exige etapa isolada com autorização específica,
  proteção e fronteira privilegiada própria. Não declarar a R1 pronta para uso real sem essa validação.
