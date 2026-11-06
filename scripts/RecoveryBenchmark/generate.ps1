param(
    [Parameter(Mandatory)][ValidateRange(1, 10000)][int]$Count,
    [string]$Root = (Join-Path ([IO.Path]::GetTempPath()) ('SentinelCoreRecoveryBenchmark\' + [Guid]::NewGuid().ToString('N'))),
    [ValidateRange(1, 25)][int]$ChunkSize = 25,
    [string]$Exe
)
# Guardas posteriores as medicoes T4B.3. Gera uma carga por vez, sem apagar destinos existentes.
$ErrorActionPreference = 'Stop'
$exe = if ($Exe) { $Exe } else { Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\RecoveryBenchmark.exe' }
# O proprio auxiliar valida contencao em TEMP, GUID, ausencia de reparse points e destino inedito.
& $exe init $Root
if ($LASTEXITCODE -ne 0) { throw 'Raiz temporaria recusada.' }
$Root = [IO.Path]::GetFullPath($Root)

function Invoke-LowPriority([string[]]$Arguments, [string]$Name) {
    $quoted = $Arguments | ForEach-Object { if ($_ -match '"') { throw 'Argumento invalido.' }; '"' + $_ + '"' }
    $stdout = Join-Path $Root ($Name + '.out.txt')
    $stderr = Join-Path $Root ($Name + '.err.txt')
    $process = Start-Process -FilePath $exe -ArgumentList $quoted -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    try {
        if (-not $process.HasExited) { $process.PriorityClass = 'BelowNormal' }
        $process.WaitForExit()
        $process.Refresh()
        if ($process.ExitCode -ne 0) { throw "Auxiliar falhou ($Name): $($process.ExitCode). Veja $stderr" }
    } finally { $process.Dispose() }
    Get-Content -LiteralPath $stdout
}

$chunks = [math]::Ceiling($Count / $ChunkSize)
for ($i = 0; $i -lt $chunks; $i++) {
    $size = [math]::Min($ChunkSize, $Count - $i * $ChunkSize)
    foreach ($kinds in 'tp', 'd', 'r') {
        $chunkRoot = Join-Path $Root "chunk-$i-$kinds"
        Invoke-LowPriority @('chunk', $chunkRoot, "$size", "$(5000 + $i)", $kinds) "chunk-$i-$kinds"
    }
}
Invoke-LowPriority @('merge', (Join-Path $Root 'clean'), '--parent', $Root) 'merge'
Invoke-LowPriority @('damage', (Join-Path $Root 'clean'), (Join-Path $Root 'damaged'), "$(9000 + $Count)") 'damage'
# Preserva os blocos como evidencias; a limpeza da raiz temporaria propria cabe ao controlador apos as medicoes.
Write-Host "Fixtures: $Root"
