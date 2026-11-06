param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string[]]$Cases,
    [ValidateRange(100, 24000)][int]$BatchChars = 20000
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$manifest = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'synthetic-audit.json') | ConvertFrom-Json
$project = Join-Path $PSScriptRoot 'SentinelCore.Tests\SentinelCore.Tests.csproj'
$dotnet = Join-Path $root 'scripts\dotnet.ps1'
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N')
$output = Join-Path $root "artifacts\test-results\t4b\$Configuration\$runId"

$expected = @{}
foreach ($entry in $manifest.inputs) {
    if ($expected.ContainsKey($entry.path)) { throw "Entrada duplicada no manifesto: $($entry.path)" }
    $expected[$entry.path] = $entry.sha256
}
function Assert-AuditedInputs {
    # The manifest cannot hash itself; changes to it still require human review.
    $inputs = @(
        Get-ChildItem -LiteralPath (Join-Path $root 'src'), (Join-Path $root 'tests') -File -Recurse -Force |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Name -ne 'synthetic-audit.json' -and $_.Extension -in '.cs', '.csproj', '.json', '.props', '.targets', '.ps1', '.runsettings', '.rsp' }
        Get-ChildItem -LiteralPath $root -File -Force | Where-Object { $_.Name -in 'NuGet.Config', 'global.json', 'Directory.Packages.props', 'MSBuild.rsp' -or $_.Name -like 'Directory.Build.*' }
        Get-Item -LiteralPath $dotnet
    )
    $actual = @{}
    foreach ($file in $inputs) {
        # Substring instead of [IO.Path]::GetRelativePath so the runner also works in Windows PowerShell 5.1 (pwsh absent here).
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        $actual[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    if ($actual.Count -ne $expected.Count) { throw 'Arquivos de entrada divergentes; revisão necessária.' }
    foreach ($path in $actual.Keys) {
        if (-not $expected.ContainsKey($path) -or $expected[$path] -ne $actual[$path]) {
            throw "Entrada alterada ou não classificada: $path"
        }
    }
}
Assert-AuditedInputs
if (-not $PSBoundParameters.ContainsKey('Cases') -and $manifest.regressionReady -ne $true) {
    throw 'Regressão ampla bloqueada até a revisão da seleção.'
}
$approved = @($manifest.synthetic)
$selected = if ($PSBoundParameters.ContainsKey('Cases')) { @($Cases) } else { $approved }
$integration = @($manifest.windowsIntegration)
$operational = @($manifest.windowsOperational)
if ($approved.Count -eq 0 -or @($approved | Sort-Object -Unique).Count -ne $approved.Count -or
    $selected.Count -eq 0 -or @($selected | Sort-Object -Unique).Count -ne $selected.Count) { throw 'Seleção vazia ou duplicada.' }
foreach ($name in $selected) {
    if ($name -notmatch '^SentinelCore\.Tests\.[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$' -or $name -notin $approved -or $name -in $integration -or $name -in $operational) {
        throw "Seleção inválida: $name"
    }
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
$common = @($project, '--no-restore', '-c', $Configuration, '-p:SentinelCoreRealTweaks=false', '-p:SentinelCoreRealRepairs=false', '-p:SentinelCoreRealDebloat=false')
& $dotnet build @common
if ($LASTEXITCODE -ne 0) { throw 'Build falhou.' }
Assert-AuditedInputs
# A linha de comando do Windows aceita no maximo 32.767 caracteres: a selecao e dividida em lotes de filtros positivos exatos.
$batches = New-Object System.Collections.Generic.List[object]
$current = New-Object System.Collections.Generic.List[string]; $length = 0
foreach ($name in $selected) {
    $part = "FullyQualifiedName=$name|"
    if ($length + $part.Length -gt $BatchChars -and $current.Count -gt 0) { $batches.Add($current.ToArray()); $current = New-Object System.Collections.Generic.List[string]; $length = 0 }
    $current.Add($name); $length += $part.Length
}
if ($current.Count -gt 0) { $batches.Add($current.ToArray()) }
if (($batches | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum -ne $selected.Count) { throw 'Particao em lotes difere da selecao.' }
function Get-BatchFilter($names) { ($names | ForEach-Object { "FullyQualifiedName=$_" }) -join '|' }
$all = @(& $dotnet test $project --no-build --no-restore -c $Configuration --list-tests)
if ($LASTEXITCODE -ne 0) { throw 'Descoberta falhou.' }
$discovered = @($all | Where-Object { $_ -match '^\s+SentinelCore\.Tests\.' } | ForEach-Object { $_.Trim() })
if ($discovered.Count -eq 0) { throw 'Descoberta vazia; formato não reconhecido.' }
$expectedDiscovery = @($manifest.discoveredCases)
if ($expectedDiscovery.Count -eq 0 -or
    @($expectedDiscovery | Sort-Object -Unique).Count -ne $expectedDiscovery.Count -or
    $expectedDiscovery.Count -ne $discovered.Count -or
    (Compare-Object ($expectedDiscovery | Sort-Object) ($discovered | Sort-Object))) {
    throw 'Inventário descoberto difere do inventário revisado; execução recusada.'
}
$selectedDiscovery = @()
foreach ($batch in $batches) {
    $listed = @(& $dotnet test $project --no-build --no-restore -c $Configuration --list-tests --filter (Get-BatchFilter $batch))
    if ($LASTEXITCODE -ne 0) { throw 'Verificação da seleção falhou.' }
    $batchDiscovery = @($listed | Where-Object { $_ -match '^\s+SentinelCore\.Tests\.' } | ForEach-Object { $_.Trim() })
    if ($batchDiscovery.Count -ne $batch.Count) { throw 'Descoberta do lote com quantidade divergente.' }
    if (Compare-Object ($batch | Sort-Object) ($batchDiscovery | Sort-Object)) { throw 'Seleção descoberta difere da lista aprovada.' }
    $selectedDiscovery += $batchDiscovery
}
if (Compare-Object ($selected | Sort-Object) ($selectedDiscovery | Sort-Object)) { throw 'Seleção descoberta difere da lista aprovada.' }
foreach ($name in $integration) { if ($name -notin $discovered) { throw "Teste de integração ausente: $name" } }
Assert-AuditedInputs
$selected | Set-Content -Encoding UTF8 (Join-Path $output 'selected.txt')
$discovered | Where-Object { $_ -notin $selected } | Set-Content -Encoding UTF8 (Join-Path $output 'not-executed.txt')
$results = @(); $index = 0
foreach ($batch in $batches) {
    $index++
    Assert-AuditedInputs # entradas e fontes nao podem ter mudado desde a descoberta ate este lote
    $trxName = if ($batches.Count -eq 1) { 'synthetic.trx' } else { "synthetic-$index.trx" }
    & $dotnet test $project --no-build --no-restore -c $Configuration --filter (Get-BatchFilter $batch) --logger "trx;LogFileName=$trxName" --results-directory $output
    if ($LASTEXITCODE -ne 0) { throw "Testes sintéticos falharam (lote $index)." }
    [xml]$trx = Get-Content -Raw -LiteralPath (Join-Path $output $trxName)
    $counters = $trx.TestRun.ResultSummary.Counters
    if ([int]$counters.total -ne $batch.Count -or [int]$counters.passed -ne $batch.Count -or [int]$counters.failed -ne 0 -or [int]$counters.notExecuted -ne 0) {
        throw "TRX não confirma a execução integral do lote $index."
    }
    # Where-Object { $_ }: em PowerShell 5.1 "$null | ForEach-Object" executa uma vez; sem o filtro um TRX vazio contaria como 1 resultado.
    $batchResults = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_ } | ForEach-Object { $_.testName })
    if ($batchResults.Count -ne $batch.Count) { throw "TRX do lote $index com quantidade de resultados divergente." }
    if (Compare-Object ($batch | Sort-Object) ($batchResults | Sort-Object)) { throw "Os testes registrados no TRX do lote $index diferem da seleção aprovada." }
    $results += $batchResults
}
if ($results.Count -ne $selected.Count -or (Compare-Object ($selected | Sort-Object) ($results | Sort-Object))) {
    throw 'Os testes registrados nos TRX diferem da seleção aprovada.'
}
Assert-AuditedInputs
'concluido' | Set-Content -Encoding ASCII (Join-Path $output 'completed.txt')
if ($PSBoundParameters.ContainsKey('Cases')) { Write-Host 'EXECUCAO PARCIAL (subconjunto -Cases); nao representa a regressao integral.' }
Write-Host "Aprovados: $($selected.Count). Não executados: $($discovered.Count - $selected.Count)."
