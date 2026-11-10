param([Parameter(Mandatory)][string]$Path, [string]$Report)
# T4B.3 / P3: inspeção estática do diretório publicado. Não executa nenhum binário publicado.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Path).Path
$latin = [Text.Encoding]::GetEncoding(28591)
$files = Get-ChildItem -LiteralPath $root -Recurse -File

# 1) Nomes de arquivo que não podem constar da distribuição.
$forbiddenNames = 'SyntheticJournalWorker', 'SentinelCore.Tests', 'run-synthetic', 'synthetic-audit', 'xunit', 'testhost',
    'Microsoft.NET.Test', 'Microsoft.TestPlatform', 'Microsoft.VisualStudio.TestPlatform', 'Debloat.Removal.Native',
    'RecoveryBenchmark', 'RealValidation', 'StoreInspector', 'GraphicsValidation', 'SentinelCore.RepairBroker', '.trx', 'fixture'
$nameHits = foreach ($file in $files) {
    foreach ($needle in $forbiddenNames) { if ($file.Name.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) { [pscustomobject]@{ File = $file.Name; Needle = $needle } } }
}

# 2) Strings nos binários do produto (metadados .NET armazenam strings em UTF-8; UTF-16 cobre literais).
$needles = 'SyntheticJournalWorker', 'SentinelCore.Tests', 'AtomicWriteStage', 'InternalsVisibleTo', 'run-synthetic', 'synthetic-audit',
    'Removal.Native', 'Debloat.Native', 'Windows.Management.Deployment', 'RemovePackageAsync', 'AddPackageAsync',
    'ProvisionPackageForAllUsersAsync', 'DeprovisionPackageForAllUsersAsync', 'RegisterPackageAsync', 'StagePackageAsync',
    'Microsoft.NET.Test', 'xunit', 'SentinelCore.RepairBroker'
$product = $files | Where-Object { $_.Name -like 'SentinelCore*.dll' -or $_.Name -like 'SentinelCoreAI*' -and $_.Extension -in '.dll', '.exe', '.json' }
$stringHits = foreach ($file in $product) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    # Literais UTF-16 podem comecar em offset par ou impar; metadados (nomes) estao em UTF-8.
    $ascii = $latin.GetString($bytes); $utf16 = [Text.Encoding]::Unicode.GetString($bytes)
    $odd = [byte[]]::new($bytes.Length - 1); [Array]::Copy($bytes, 1, $odd, 0, $odd.Length); $utf16odd = [Text.Encoding]::Unicode.GetString($odd)
    foreach ($needle in $needles) {
        if ($ascii.Contains($needle) -or $utf16.Contains($needle) -or $utf16odd.Contains($needle)) { [pscustomobject]@{ File = $file.Name; Needle = $needle } }
    }
}

# 2b) Linha de base aceita: leitor WinRT, atributos InternalsVisibleTo e seam de teste. Qualquer outro acerto,
# assembly de produto fora da lista ou nome proibido faz o script terminar com codigo diferente de zero.
$allowedProducts = 'SentinelCore.Application.dll', 'SentinelCore.Domain.dll', 'SentinelCore.Infrastructure.Debloat.dll',
    'SentinelCore.Infrastructure.Debloat.Native.dll', 'SentinelCore.Infrastructure.Modifications.dll',
    'SentinelCore.Infrastructure.Repairs.dll', 'SentinelCore.Infrastructure.Windows.dll', 'SentinelCore.Persistence.dll', 'SentinelCoreAI.deps.json',
    'SentinelCoreAI.dll', 'SentinelCoreAI.exe', 'SentinelCoreAI.runtimeconfig.json'
$baseline = @{
    'SentinelCore.Application.dll' = 'SentinelCore.Tests', 'InternalsVisibleTo'
    # Domain: a string "Windows.Management.Deployment" e so o rotulo descritivo de DebloatInventoryContracts.cs (nome da API de origem); nao ha referencia a assembly WinRT.
    'SentinelCore.Domain.dll' = 'SentinelCore.Tests', 'InternalsVisibleTo', 'Windows.Management.Deployment'
    'SentinelCore.Infrastructure.Debloat.Native.dll' = 'Debloat.Native', 'Windows.Management.Deployment'
    'SentinelCoreAI.dll' = 'Debloat.Native'
    'SentinelCoreAI.deps.json' = 'Debloat.Native'
    'SentinelCore.Infrastructure.Windows.dll' = 'SentinelCore.Tests', 'InternalsVisibleTo'
    'SentinelCore.Persistence.dll' = 'SyntheticJournalWorker', 'SentinelCore.Tests', 'AtomicWriteStage', 'InternalsVisibleTo'
}
$violations = @()
foreach ($name in $product.Name | Where-Object { $_ -notin $allowedProducts }) { $violations += "assembly/arquivo de produto fora da lista: $name" }
foreach ($name in $allowedProducts | Where-Object { $_ -notin $product.Name }) { $violations += "arquivo de produto esperado ausente: $name" }
foreach ($hit in @($stringHits)) { if (-not $baseline.ContainsKey($hit.File) -or $hit.Needle -notin $baseline[$hit.File]) { $violations += "string inesperada em $($hit.File): $($hit.Needle)" } }
foreach ($hit in @($nameHits)) { $violations += "nome proibido: $($hit.File) ($($hit.Needle))" }

# 3) deps.json: projetos e pacotes referenciados.
$deps = Get-ChildItem -LiteralPath $root -Filter '*.deps.json' | ForEach-Object {
    $json = Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json
    [pscustomobject]@{
        File = $_.Name
        Projects = @($json.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' } | ForEach-Object { $_.Name })
        Packages = @($json.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { $_.Name } |
            Where-Object { $_ -notmatch '^(runtimepack|Microsoft\.NETCore|Microsoft\.WindowsDesktop|Microsoft\.NET\.)' })
    }
}

$result = [pscustomobject]@{
    Path = $root; FileCount = $files.Count; TotalMiB = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
    ProductAssemblies = @($product | ForEach-Object { $_.Name } | Sort-Object)
    ForbiddenFileNameHits = @($nameHits); ProductStringHits = @($stringHits); Deps = @($deps)
    SqliteNative = @($files | Where-Object { $_.Name -like '*sqlite*' } | ForEach-Object { $_.Name })
    ConfigFiles = @($files | Where-Object { $_.Extension -in '.config', '.ini', '.runsettings', '.rsp', '.props', '.targets' } | ForEach-Object { $_.Name })
    ExecutablesAndScripts = @($files | Where-Object { $_.Extension -in '.exe', '.bat', '.cmd', '.ps1', '.vbs', '.js', '.msi' } | ForEach-Object { $_.Name })
    Violations = @($violations)
}
if ($Report) { $result | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 -LiteralPath $Report }
$result | ConvertTo-Json -Depth 6
if ($violations.Count -gt 0) { Write-Error ("VIOLACOES: " + ($violations -join "; ")); exit 1 }
