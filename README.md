# SentinelCore AI

Diagnóstico profundo do Windows, baseado em evidências. Aplicativo desktop WPF, C# e .NET 10 LTS.
As fases 0 a 3.2.1 entregam auditoria básica, inventário de hardware em onze categorias, investigação de eventos e
metadados de relatórios de falha do Windows, sessões de desempenho com métricas de CPU, memória, processos e
armazenamento, histórico local, memória Markdown e comparação entre snapshots.
O produto funciona offline após a publicação. Não executa reparos, otimizações, correções ou comandos de IA.

## Estado

Fase 3.3A (Network Diagnostics) encerrada em 01/11/2026 como **APROVADA COM RESSALVAS**. A Fase 3.3B
(Graphics Diagnostics) foi implementada em `0.3.0-preview.6` e o quality gate independente final foi
**APROVADO COM RESSALVAS**. Os históricos anteriores permanecem preservados e nenhum registro foi reescrito.
Personalização & Debloat está apenas [planejado](docs/planning/TWEAKS_DEBLOAT_DISCOVERY.md), sem operações ou UI novas.

Versão 0.3.0-preview.6 — Graphics Diagnostics. A aba **Gráficos & GPU** investiga adaptadores, monitores,
drivers, eventos gráficos, TDR Display 4101 e metadados WER LiveKernelEvent 117/141/142, além de alterações
entre investigações. A leitura reutiliza os coletores existentes de GPU/monitor/driver e de eventos/WER; não há
segunda consulta WMI paralela. Temperatura, potência, frequência, utilização global e VRAM real não são
inventadas: somente eventos, WER, drivers, estado de exibição e o `AdapterRAM` reportado pelo driver são
preservados. TDR, LiveKernelEvent, mudança de driver ou falha de aplicativo não são apresentados como causa,
defeito físico, superaquecimento ou problema de VRAM. A causalidade permanece não avaliada (Fase 3.4).
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
coletores reais do Windows, somente leitura; não modifica configurações do Windows nem usa o histórico de produção
como raiz de teste.

## Gerar e executar o pacote

```powershell
.\scripts\publish.ps1
.\artifacts\publish\0.3.0-preview.6\win-x64\SentinelCoreAI.exe
```

O destino é versionado a partir de `Directory.Build.props`; o script recusa sobrescrever sem `-Force`.
Feche a instância publicada antes de gerar novamente o pacote.
Distribua **toda a pasta** `artifacts/publish/0.3.0-preview.6/win-x64`, incluindo DLLs nativas.
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
14. Consulte **Resultados**, **Hardware**, **Estabilidade e Eventos**, **Desempenho**, **Rede**, **Gráficos & GPU**
    e **Histórico**. Em Resultados, **Abrir localização do relatório** seleciona o Markdown da auditoria no Explorador.
15. Feche e reabra; a última tentativa, os snapshots, as investigações, as sessões de desempenho/rede, as
    investigações gráficas e o histórico serão recuperados.

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
- `data/sentinelcore.db`: índice SQLite, esquema 7 e projeções de auditorias, snapshots, investigações, sessões e
  investigações gráficas.
- `reports/<id>.md`: relatório legível da auditoria, reconstruível do registro histórico.
- `reports/hardware/<id>.md`: relatório legível do snapshot de hardware, reconstruível do payload.
- `reports/events/<id>.md`: relatório legível da investigação de eventos, reconstruível do payload.
- `reports/performance/<id>.md`: relatório legível da sessão de desempenho, reconstruível do payload.
- `reports/network/<id>.md`: relatório legível da sessão de rede, reconstruível do payload.
- `reports/graphics/<id>.md`: relatório legível da investigação gráfica, reconstruível do payload.
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
Gráficos reutilizam WMI `Win32_VideoController`/`Win32_PnPSignedDriver`, DisplayConfig/`WmiMonitorID`, Log de Eventos
e metadados WER. Nenhuma métrica de temperatura, potência, frequência, utilização global ou VRAM real é coletada; o
`AdapterRAM` é apenas o valor reportado pelo driver. Nenhum driver, resolução, HDR, frequência ou energia é alterado.
Os arquivos não são criptografados; a proteção depende das permissões do perfil Windows. Compartilhe relatórios conscientemente.
Histórico real nunca é apagado automaticamente. Para backup, feche o aplicativo e copie a pasta de dados inteira.
O hash detecta corrupção acidental do payload; não é uma assinatura contra adulteração pelo próprio usuário.

## Organização

Cinco projetos de produção em `src`, testes em `tests`, documentação em `docs` e scripts locais em `scripts`.
Consulte [arquitetura](docs/ARCHITECTURE.md), [segurança](docs/SECURITY.md) e [roadmap](docs/ROADMAP.md).
Repositório Git local, sem commit automático, remoto, push ou deploy.
