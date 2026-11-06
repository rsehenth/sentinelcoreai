# RecoveryBenchmark — uso exclusivamente sintético

Guardas de caminho acrescentadas após as medições T4B.3: naquela revisão não houve nova medição de desempenho.
O pipeline de histórico medido não foi alterado. As sondas pequenas da U1 estão descritas abaixo e no relatório próprio.

Todos os comandos aceitam apenas árvores próprias abaixo de
`%TEMP%\SentinelCoreRecoveryBenchmark\<GUID sem hífens>`. Destinos precisam ser inéditos;
fontes precisam conter o marcador de fixture criado pelo auxiliar. Links/reparse points são recusados
nos ancestrais e nas árvores de entrada. A verificação é cooperativa; não protege contra troca hostil
de caminhos entre validação e uso. O marcador identifica fixtures, não autentica seu conteúdo.

`generate.ps1 -Count <n>` cria uma raiz nova, gera blocos de até 25 operações em sequência,
executa cada processo em prioridade `BelowNormal` e recusa códigos de saída não zero.
Não reutiliza nem apaga uma raiz existente. Mantém os blocos para inspeção; o controlador deve
limpar a raiz temporária própria ao terminar as medições.

Comandos diretos: `init <raiz nova>`, `chunk <raiz nova> <1..25> <seed> [tipos]`,
`merge <destino novo> --parent <raiz própria>`, `damage <origem própria> <destino novo> <seed>`,
`bench <fixture própria> <rótulo> [iterações]` e
`wpf <repositório> <trabalho novo> <clean> <damaged> <large-clean>`.
O diretório de trabalho WPF também deve estar na árvore temporária própria.

`self-check` exercita somente as guardas de caminho, sem gerar histórico, abrir WPF,
consultar o Windows ou executar benchmarks. Recusa raiz externa, GUID inválido, escape por `..`,
destino existente e origem sem marcador; confirma que a recusa preserva a raiz própria.

U1 acrescenta `diagnose <fixture própria>` (limite de 1.000 Markdown; três leituras cruas com tempos por
arquivo, validação, mapeamento e contenção controlada do lock) e
`u1 <repositório> <trabalho novo> <clean> <damaged>` (39 verificações WPF, 13 páginas; sem App.OnStartup,
coletores ou executores Windows). O host injeta armazenamento e logger; qualquer erro do shell falha a rodada.
As capturas usam software rendering, janelas fora da tela e dimensões mínimas de 1120 × 720 no shell.
Os cenários e a revisão anterior à execução constam na matriz U1 e nos JSON em `artifacts/validation/u1`.
O medidor não esvazia o cache do SO nem identifica causalmente antivírus/indexação; metadados inalterados
não são prova criptográfica de igualdade de conteúdo. Não extrapolar os resultados pequenos para 5.000 operações.
