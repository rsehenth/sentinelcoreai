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
O diagnóstico é somente de leitura. A única exceção implementada é a T2A, que escreve quatro valores HKCU
(`HideFileExt`, `TaskbarAl`, `AppsUseLightTheme`, `SystemUsesLightTheme`) pelo Safety Engine, com catálogo
fechado, backup, autorização digitada, journal e verificação; essa escrita está desabilitada na distribuição
(`RealDisabled`) até validação em VM. São proibidos: alterar Registro fora da T2A, HKLM/HKCR/HKU, serviços,
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

## Personalização & Debloat — T1/T2A implementadas; ampliação exige autorização

- O módulo é opcional e exclusivo para Windows 11. Consultar
  `docs/planning/TWEAKS_DEBLOAT_DISCOVERY.md`, `docs/planning/TWEAKS_DEBLOAT_T2A_ARCHITECTURE.md` e
  `docs/ROADMAP.md` antes de propor qualquer ampliação.
- A T1 (Safety & Recovery Core) e a T2A (três operações reais em HKCU) estão implementadas. A T2A escreve
  somente `HideFileExt`, `TaskbarAl`, `AppsUseLightTheme` e `SystemUsesLightTheme`, todos HKCU e `REG_DWORD 0`,
  com catálogo fechado, backup, autorização digitada, journal e verificação. Não há HKLM/HKCR/HKU, criação de
  chave, script, PowerShell, `reg.exe`, `.reg`, Appx, serviço, tarefa, DISM ou elevação.
- **A execução real permanece desabilitada na distribuição (`RealDisabled`).** A separação é feita em tempo de
  compilação: o build de distribuição (padrão) não compila a escrita real e `--enable-real-tweaks` não tem efeito,
  mesmo por chamada interna. Somente builds experimentais de validação (`-p:SentinelCoreRealTweaks=true`) compilam
  o executor HKCU e aceitam o opt-in; esses builds são exclusivos para VM de validação e nunca devem ser distribuídos.
  Não executar escrita real no computador de desenvolvimento, não instalar virtualização sem autorização e não
  declarar a T2A pronta para distribuição sem validação real em VM com snapshot.
- A T2B, o Debloat e o Recovery Center completo não estão autorizados. Não adicionar operações, abas vazias,
  botões decorativos ou um segundo Safety Engine/backup/journal.
- Preservar separação entre Diagnostic Engine (leitura) e Controlled Modification Engine da T2A (operações
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
