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
Nesta fase, diagnóstico é somente de leitura. São proibidos: alterar Registro, serviços, processos do usuário,
drivers, BIOS, firmware, energia, proteções, arquivos de sistema, reparos ou reinícios.
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

## Personalização & Debloat — somente planejamento

- O módulo é futuro, opcional e exclusivo para Windows 11. Consultar
  `docs/planning/TWEAKS_DEBLOAT_DISCOVERY.md` e `docs/ROADMAP.md` antes de propor sua implementação.
- Esta atualização documental não autoriza alterações do Windows, novos executores, abas ou implementações vazias.
- Priorizar diagnóstico e correlação. T0 pode ser antecipada apenas como pesquisa documental e somente leitura.
- Preservar separação entre Diagnostic Engine (leitura) e futuro Controlled Modification Engine (operações
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
