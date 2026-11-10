param(
    [Parameter(Mandatory)][string]$ImagePath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-F]{64}$')][string]$ApprovedSha256
)
# Somente preparação estática: não publica, instala, inicia imagem, altera ACL ou abre gates.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$image = Get-Item -LiteralPath $ImagePath
if ($image.PSIsContainer -or $image.Name -cne 'SentinelCore.RepairBroker.exe') { throw 'Imagem de broker inválida.' }
if (($image.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Imagem redirecionada.' }
for ($directory = $image.Directory; $null -ne $directory; $directory = $directory.Parent) {
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Diretório redirecionado.' }
}
$stream = [IO.File]::Open($image.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))
    if ($hash -cne $ApprovedSha256) { throw 'SHA-256 diverge do artefato aprovado.' }
    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
    try {
        $stream.Position = 0
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Cabeçalho DOS inválido.' }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 64 -or $peOffset -gt $stream.Length - 264) { throw 'Cabeçalho PE fora dos limites.' }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'Exige PE AMD64.' }
        $stream.Position = $peOffset + 20
        if ($reader.ReadUInt16() -lt 240 -or ($reader.ReadUInt16() -band 0x2000) -ne 0) { throw 'Exige imagem executável PE32+.' }
        $optional = $peOffset + 24
        $stream.Position = $optional
        if ($reader.ReadUInt16() -ne 0x20B) { throw 'Exige cabeçalho PE32+.' }
        $stream.Position = $optional + 108
        if ($reader.ReadUInt32() -lt 15) { throw 'Diretórios PE incompletos.' }
        $stream.Position = $optional + 112 + 14 * 8
        if ($reader.ReadUInt64() -ne 0) { throw 'Imagem gerenciada recusada; use o perfil Native AOT.' }
    }
    finally { $reader.Dispose() }

    $outputDirectory = Join-Path $projectRoot 'artifacts/packaging/repair-broker'
    for ($directory = [IO.DirectoryInfo]::new($outputDirectory); $null -ne $directory; $directory = $directory.Parent) {
        if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Saída de empacotamento contém redirecionamento.'
        }
    }
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    $output = Join-Path $outputDirectory 'ApprovedRepairBroker.props'
    $document = "<Project><PropertyGroup><SentinelCoreApprovedRepairBrokerSha256>$hash</SentinelCoreApprovedRepairBrokerSha256></PropertyGroup></Project>"
    $destination = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($document)
        $destination.Write($bytes, 0, $bytes.Length)
        $destination.Flush($true)
    }
    finally { $destination.Dispose() }
    Write-Output "Pin preparado em $output. Imagem não executada; gates preservados."
}
finally { $stream.Dispose() }
