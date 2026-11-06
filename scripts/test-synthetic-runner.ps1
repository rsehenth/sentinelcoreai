# Equivalente PowerShell de scripts/test-synthetic-runner.py (T4B.3; o ambiente de desenvolvimento nao tem python nem pwsh).
# Executa o runner REAL contra um toolchain falso e totalmente sintetico em diretorio temporario descartavel.
param([string]$Only)
$ErrorActionPreference = 'Continue'
$repo = Split-Path $PSScriptRoot -Parent
$Safe = 'SentinelCore.Tests.FakeTests.Safe'; $Real = 'SentinelCore.Tests.FakeTests.Integration'
$mock = @'
$mode = $env:T4B_PROBE_MODE
$root = Split-Path $PSScriptRoot -Parent
$marker = Join-Path $root 'calls.txt'
if ($args[0] -eq 'build') { Add-Content $marker 'build'; foreach ($g in '-p:SentinelCoreRealTweaks=false', '-p:SentinelCoreRealRepairs=false', '-p:SentinelCoreRealDebloat=false') { if ($g -notin $args) { Add-Content $marker 'build-sem-gates' } }; if ($mode -eq 'build-fail') { exit 1 }; exit 0 }
if ('--list-tests' -in $args) {
    Add-Content $marker 'discover'
    if ('--filter' -in $args) { '    SentinelCore.Tests.FakeTests.Safe'; exit 0 }
    if ($mode -ne 'removed') { '    SentinelCore.Tests.FakeTests.Safe' }
    if ($mode -eq 'renamed') { '    SentinelCore.Tests.FakeTests.Renamed' }
    '    SentinelCore.Tests.FakeTests.Integration'
    if ($mode -eq 'extra' -or $mode -eq 'theory') { '    SentinelCore.Tests.FakeTests.Unapproved(status: 1)' }
    exit 0
}
Add-Content $marker 'execute-fake'; if ('--filter' -notin $args) { Add-Content $marker 'execute-nofilter' }
if ($mode -eq 'interrupt') { exit 130 }
$dir = $args[[array]::IndexOf($args, '--results-directory') + 1]
$case = if ($mode -eq 'bad-trx') { 'SentinelCore.Tests.FakeTests.Wrong' } else { 'SentinelCore.Tests.FakeTests.Safe' }
$name = if ($mode -eq 'multi') { 'synthetic-1.trx' } else { 'synthetic.trx' }
@"
<TestRun><Results><UnitTestResult testName="$case" /></Results><ResultSummary><Counters total="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>
"@ | Set-Content -Encoding UTF8 (Join-Path $dir $name)
exit 0
'@
$failures = 0
function Probe($name, $scenario = 'ok', [scriptblock]$mutate = $null, [bool]$mustExecute = $false, [bool]$expectPass = $false, $extra = @(), $mockText = $mock, $synthetic = @($Safe), $executions = $null, $expect = $null) {
    if ($Only -and $name -notlike $Only) { return }
    $root = Join-Path ([IO.Path]::GetTempPath()) ('runner-probe-' + [Guid]::NewGuid().ToString('N'))
    try {
        foreach ($d in 'scripts', 'tests\SentinelCore.Tests', 'src') { New-Item -ItemType Directory -Force (Join-Path $root $d) | Out-Null }
        Copy-Item (Join-Path $repo 'tests\run-synthetic.ps1') (Join-Path $root 'tests\run-synthetic.ps1')
        Set-Content -Encoding UTF8 (Join-Path $root 'scripts\dotnet.ps1') $mockText
        Set-Content (Join-Path $root 'tests\SentinelCore.Tests\SentinelCore.Tests.csproj') '<Project/>'
        Set-Content (Join-Path $root 'src\Probe.cs') '// synthetic dependency'
        Set-Content (Join-Path $root 'Directory.Build.props') '<Project/>'
        $inputs = Get-ChildItem $root -Recurse -File | ForEach-Object { [pscustomobject]@{ path = $_.FullName.Substring($root.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash } }
        $manifest = [ordered]@{ synthetic = @($synthetic); windowsIntegration = @($Real); windowsOperational = @(); regressionReady = $true; discoveredCases = @($synthetic + $Real); inputs = @($inputs) }
        $manifestPath = Join-Path $root 'tests\synthetic-audit.json'
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $manifestPath
        if ($mutate) { & $mutate $root $manifest $manifestPath }
        $env:T4B_PROBE_MODE = $scenario
        Push-Location $root
        try { $out = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\run-synthetic.ps1') @extra 2>&1; $code = $LASTEXITCODE } finally { Pop-Location }
        $marker = Join-Path $root 'calls.txt'
        $calls = if (Test-Path $marker) { @(Get-Content $marker) } else { @() }
        $executed = @($calls | Where-Object { $_ -eq 'execute-fake' }).Count
        $ok = (($code -eq 0) -eq $expectPass) -and (($executed -gt 0) -eq $mustExecute) -and ($null -eq $executions -or $executed -eq $executions)
        if ($ok -and $expect) { $ok = (($out | Out-String) -match $expect) } # a recusa precisa ocorrer pelo motivo esperado
        if ($ok -and $calls -contains 'execute-nofilter') { $ok = $false } # execucao sem --filter seria "rodar tudo"
        if ($ok -and $calls -contains 'build-sem-gates') { $ok = $false }
        if ($ok) { "PASS $name : exit=$code calls=$($calls -join ',')" } else { $script:failures++; "FAIL $name : exit=$code calls=$($calls -join ',') :: $($out | Select-Object -Last 8)" }
    } finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
}
$alter = { param($p, $c) { param($root, $m, $mp) Set-Content (Join-Path $root $p) $c }.GetNewClosure() }
Probe 'baseline' -mustExecute $true -expectPass $true
Probe 'selecao-aprovada' -mustExecute $true -expectPass $true -extra @('-Cases', $Safe) -expect 'EXECUCAO PARCIAL'
Probe 'selecao-nao-aprovada' -extra @('-Cases', $Real) -expect 'lida: SentinelCore\.Tests\.FakeTests\.Integration'
Probe 'regressao-bloqueada' -mutate { param($r, $m, $p) $m.regressionReady = $false; $m | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $p } -expect 'Regress.*bloqueada'
Probe 'hash-divergente' -mutate (& $alter 'src\Probe.cs' '// changed') -expect 'Entrada alterada ou n.*: src/Probe\.cs'
Probe 'manifesto-invalido' -mutate { param($r, $m, $p) Set-Content $p '{' } -expect 'JSON|json'
Probe 'arquivo-novo' -mutate (& $alter 'tests\SentinelCore.Tests\New.cs' '// new') -expect 'Arquivos de entrada divergentes'
Probe 'dependencia-alterada' -mutate (& $alter 'tests\SentinelCore.Tests\SentinelCore.Tests.csproj' '<Project><Changed/></Project>') -expect 'Entrada alterada ou n.*: tests/SentinelCore\.Tests/SentinelCore\.Tests\.csproj'
Probe 'configuracao-nova' -mutate (& $alter 'Directory.Build.targets' '<Project/>') -expect 'Arquivos de entrada divergentes'
Probe 'selecionado-nao-aprovado' -mutate { param($r, $m, $p) $m.synthetic = @($m.synthetic) + 'SentinelCore.Tests.FakeTests.Unknown'; $m | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $p } -expect 'Descoberta do lote com quantidade|Invent.*descoberto difere'
Probe 'integracao-na-selecao' -mutate { param($r, $m, $p) $m.synthetic = @($m.synthetic) + 'SentinelCore.Tests.FakeTests.Integration'; $m | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $p } -expect 'lida: SentinelCore\.Tests\.FakeTests\.Integration'
Probe 'operacional-na-selecao' -mutate { param($r, $m, $p) $m.windowsOperational = @('SentinelCore.Tests.FakeTests.Safe'); $m | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $p } -expect 'lida: SentinelCore\.Tests\.FakeTests\.Safe'
Probe 'metodo-removido' -scenario 'removed' -expect 'Invent.*descoberto difere'
Probe 'metodo-renomeado' -scenario 'renamed' -expect 'Invent.*descoberto difere'
Probe 'descoberta-divergente' -scenario 'extra' -expect 'Invent.*descoberto difere'
Probe 'theory-nova' -scenario 'theory' -expect 'Invent.*descoberto difere'
Probe 'falha-build' -scenario 'build-fail' -expect 'Build falhou'
Probe 'trx-inconsistente' -scenario 'bad-trx' -mustExecute $true -expect 'diferem da sele'
Probe 'interrupcao' -scenario 'interrupt' -mustExecute $true -expect 'falharam'
Probe 'arquivo-oculto' -mutate { param($r, $m, $p) $h = Join-Path $r 'tests\SentinelCore.Tests\Hidden.cs'; Set-Content $h '// hidden'; (Get-Item $h -Force).Attributes = 'Hidden' } -expect 'Arquivos de entrada divergentes'
Probe 'arquivo-removido' -mutate { param($r, $m, $p) Remove-Item (Join-Path $r 'src\Probe.cs') } -expect 'Arquivos de entrada divergentes'
# --- Multi-lote: mock que honra --filter e grava um TRX por lote (-BatchChars pequeno forca varios lotes). ---
$multi = @'
$mode = $env:T4B_PROBE_MODE
$root = Split-Path $PSScriptRoot -Parent
$marker = Join-Path $root 'calls.txt'
$all = 1..4 | ForEach-Object { "SentinelCore.Tests.FakeTests.Safe$_" }
if ($args[0] -eq 'build') { Add-Content $marker 'build'; foreach ($g in '-p:SentinelCoreRealTweaks=false', '-p:SentinelCoreRealRepairs=false', '-p:SentinelCoreRealDebloat=false') { if ($g -notin $args) { Add-Content $marker 'build-sem-gates' } }; exit 0 }
$names = $all
$f = [array]::IndexOf($args, '--filter')
if ($f -ge 0) { $names = @(($args[$f + 1] -split '\|') | ForEach-Object { $_ -replace '^FullyQualifiedName=', '' }) }
if ('--list-tests' -in $args) {
    Add-Content $marker 'discover'
    if ($f -lt 0) { $all | ForEach-Object { "    $_" }; '    SentinelCore.Tests.FakeTests.Integration'; exit 0 }
    if ($mode -eq 'empty-batch' -and 'SentinelCore.Tests.FakeTests.Safe3' -in $names) { exit 0 }
    $names | ForEach-Object { "    $_" }; exit 0
}
Add-Content $marker 'execute-fake'; if ('--filter' -notin $args) { Add-Content $marker 'execute-nofilter' }
if ($mode -eq 'batch-fail' -and 'SentinelCore.Tests.FakeTests.Safe3' -in $names) { exit 1 }
$dir = $args[[array]::IndexOf($args, '--results-directory') + 1]
$logger = $args[[array]::IndexOf($args, '--logger') + 1]
$file = ($logger -split 'LogFileName=')[1]
$results = @($names)
if ($mode -eq 'missing-result' -and 'SentinelCore.Tests.FakeTests.Safe3' -in $names) { $results = @($names | Select-Object -SkipLast 1) }
if ($mode -eq 'duplicate-across-batches' -and 'SentinelCore.Tests.FakeTests.Safe3' -in $names) { $results = @($names) + 'SentinelCore.Tests.FakeTests.Safe1' }
$total = $names.Count + $(if ($mode -eq 'total-mismatch' -and 'SentinelCore.Tests.FakeTests.Safe3' -in $names) { 1 } else { 0 })
$xml = ($results | ForEach-Object { "<UnitTestResult testName=`"$_`" />" }) -join ''
"<TestRun><Results>$xml</Results><ResultSummary><Counters total=`"$total`" passed=`"$total`" failed=`"0`" notExecuted=`"0`" /></ResultSummary></TestRun>" | Set-Content -Encoding UTF8 (Join-Path $dir $file)
exit 0
'@
$four = 1..4 | ForEach-Object { "SentinelCore.Tests.FakeTests.Safe$_" }
Probe 'multi-lote-2x2' -mockText $multi -synthetic $four -mustExecute $true -expectPass $true -extra @('-BatchChars', '120') -executions 2
Probe 'multi-lote-4x1' -mockText $multi -synthetic $four -mustExecute $true -expectPass $true -extra @('-BatchChars', '100') -executions 4
Probe 'multi-lote-unico' -mockText $multi -synthetic $four -mustExecute $true -expectPass $true -executions 1
Probe 'multi-falha-lote-intermediario' -scenario 'batch-fail' -mockText $multi -synthetic $four -mustExecute $true -extra @('-BatchChars', '100') -executions 3 -expect 'falharam \(lote 3\)'
Probe 'multi-lote-vazio-na-descoberta' -scenario 'empty-batch' -mockText $multi -synthetic $four -extra @('-BatchChars', '100') -expect 'descoberta difere|Descoberta do lote'
Probe 'multi-resultado-ausente' -scenario 'missing-result' -mockText $multi -synthetic $four -mustExecute $true -extra @('-BatchChars', '100') -expect 'quantidade de resultados divergente'
Probe 'multi-duplicidade-entre-lotes' -scenario 'duplicate-across-batches' -mockText $multi -synthetic $four -mustExecute $true -extra @('-BatchChars', '100') -expect 'quantidade de resultados divergente'
Probe 'multi-total-divergente' -scenario 'total-mismatch' -mockText $multi -synthetic $four -mustExecute $true -extra @('-BatchChars', '100') -expect 'confirma a execu'
Probe 'multi-batchchars-invalido' -mockText $multi -synthetic $four -extra @('-BatchChars', '5') -expect 'BatchChars'
"falhas: $failures"
if ($failures) { exit 1 }
