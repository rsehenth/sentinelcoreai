# SentinelCore AI — Fases 0 e 1

## Decisões

Aplicativo WPF nativo net10.0-windows, x64. Domain não conhece WPF, WMI ou SQLite.
Application orquestra coletores independentes e validade via TimeProvider.
Infrastructure.Windows consulta propriedades WMI explicitamente permitidas, sem comandos de reparo.
Persistence grava primeiro um Markdown imutável com payload JSON estruturado e hash SHA-256.
SQLite é índice transacional reconstruível desses registros. Relatórios e resumos são projeções recuperáveis.
Todas as datas persistidas são UTC; a apresentação usa fuso local. Dados desconhecidos permanecem nulos.
Renderização por software reduz a dependência inicial de drivers gráficos.

## Ordem e aceite

1. Inspeção e SDK: pasta vazia confirmada; SDK local autorizado pelo usuário; conferir hash oficial.
2. Contratos e testes: resultados tipados, status separados, cancelamento, unidades e datas determinísticas.
3. Persistência: testes isolados de criação, reabertura, imutabilidade e reconciliação.
4. Coleta Windows: seis categorias reais, duração, fonte, limitações e consultas limitadas.
5. WPF: execução assíncrona, progresso, cancelamento, inventário, histórico e acesso ao relatório.
6. Entrega: restore, Debug, Release, testes, publish self-contained, validação real e documentação.

## Riscos

- WMI pode falhar ou omitir propriedades: cada categoria preserva limitações e status parcial.
- WMI síncrono não tem cancelamento imediato garantido: consultas com timeout e cancelamento entre consultas/linhas.
- Queda entre arquivo e SQLite: registro imutável precede índice; inicialização reconcilia.
- Arquivo histórico corrompido: preservar original, avisar e excluir da validade, nunca mascarar como sucesso.
- SDK ausente no sistema: baixar somente para .tools após autorização, sem instalação global.

## Direção visual

Fundo #0D1522, superfícies #142237, linhas #2B405A, texto #E6EDF5, ciano #63D8EE.
Segoe UI, hierarquia por tamanho e peso, navegação por abas. Resultados legíveis em texto selecionável.
Sem animações ou afirmações de saúde geral. Revisar tela real antes da entrega.
