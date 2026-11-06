param([switch]$SingleFile, [switch]$Force)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$propsPath = Join-Path $projectRoot 'Directory.Build.props'
[xml]$props = Get-Content -LiteralPath $propsPath
$version = @($props.Project.PropertyGroup.Version) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) { throw "Versao ausente em $propsPath." }
$mode = if ($SingleFile) { 'win-x64-single' } else { 'win-x64' }
$output = Join-Path $projectRoot "artifacts\publish\$version\$mode"
if (Test-Path -LiteralPath $output) {
    if (-not $Force) { throw "Destino ja existe: $output. Use -Force para substituir." }
    Remove-Item -LiteralPath $output -Recurse -Force
}
# Distribuicao: uma variavel de ambiente DefineConstants nao pode introduzir simbolos de execucao real fora dos -p: abaixo.
if ($env:DefineConstants -match 'SENTINELCORE_REAL') { throw 'DefineConstants do ambiente contem simbolo de execucao real; publish recusado.' }
$arguments = @('publish', (Join-Path $projectRoot 'src\SentinelCore.App\SentinelCore.App.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $output, '-p:PublishTrimmed=false')
# Build de distribuição: a execução real da T2A não é compilada, mesmo que o ambiente tente habilitá-la.
$arguments += @('-p:SentinelCoreRealTweaks=false')
# R1: o Repair Center não compila nem compõe executor real nesta fase.
$arguments += @('-p:SentinelCoreRealRepairs=false')
$arguments += @('-p:SentinelCoreRealDebloat=false')
if ($SingleFile) { $arguments += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') }
& (Join-Path $PSScriptRoot 'dotnet.ps1') @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Output "Publicado em $output"
