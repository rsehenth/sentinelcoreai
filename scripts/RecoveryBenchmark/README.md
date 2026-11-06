# RecoveryBenchmark — uso exclusivamente sintético

Guardas de caminho acrescentadas após as medições T4B.3: **não houve nova medição de desempenho**.
O pipeline de histórico medido não foi alterado.

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
