param([switch]$Force)
# T2A - build experimental de validacao em VM. NAO e distribuicao.
# Somente este build compila SENTINELCORE_REAL_TWEAKS e aceita --enable-real-tweaks.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$propsPath = Join-Path $projectRoot 'Directory.Build.props'
[xml]$props = Get-Content -LiteralPath $propsPath
$version = @($props.Project.PropertyGroup.Version) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) { throw "Versao ausente em $propsPath." }
$output = Join-Path $projectRoot "artifacts\publish-validation\$version\win-x64"
if (Test-Path -LiteralPath $output) {
    if (-not $Force) { throw "Destino ja existe: $output. Use -Force para substituir." }
    Remove-Item -LiteralPath $output -Recurse -Force
}
Write-Warning 'Build experimental de validacao: a execucao real da T2A sera compilada. Use apenas em VM Windows 11 com snapshot.'
$arguments = @('publish', (Join-Path $projectRoot 'src\SentinelCore.App\SentinelCore.App.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $output, '-p:PublishTrimmed=false', '-p:SentinelCoreRealTweaks=true')
& (Join-Path $PSScriptRoot 'dotnet.ps1') @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$marker = @(
    'SentinelCore AI - build experimental de validacao (T2A).',
    'Nao distribuir. Nao executar fora de uma VM Windows 11 com snapshot.',
    'Execucao real compilada; exige o argumento --enable-real-tweaks.',
    "Versao: $version"
)
Set-Content -LiteralPath (Join-Path $output 'VALIDATION-BUILD.txt') -Value $marker -Encoding UTF8
Write-Output "Build experimental publicado em $output"
