param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\runtime\enhance'),
    [string]$AssetUrl = '',
    [string]$ExpectedArchiveSha256 = '',
    [string]$ExpectedVersion = ''
)

# Enhancement pack interface for the full edition. Until rife/videofx deliverables
# are final this script supports two modes:
#   1) $AssetUrl set    -> download pinned archive, verify SHA-256, extract, verify manifest.
#   2) $AssetUrl empty  -> validate an existing pack directory (used by local full testing).
$ErrorActionPreference = 'Stop'
$destination = [System.IO.Path]::GetFullPath($Destination)
$manifestPath = Join-Path $destination 'manifest.json'

function Test-EnhanceManifest {
    param([string]$ManifestPath)
    if (-not (Test-Path $ManifestPath)) { throw "Enhancement pack manifest is missing: $ManifestPath" }
    $manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$manifest.version)) { throw 'Enhancement pack manifest must declare version.' }
    if ($ExpectedVersion -and [string]$manifest.version -ne $ExpectedVersion) {
        throw "Enhancement pack version mismatch: $($manifest.version) != $ExpectedVersion"
    }
    $files = @($manifest.files)
    if ($files.Count -eq 0) { throw 'Enhancement pack manifest must list files.' }
    $verified = 0
    foreach ($entry in $files) {
        $relative = ([string]$entry.path).Trim().Replace('/', '\')
        if ([string]::IsNullOrWhiteSpace($relative) -or [System.IO.Path]::IsPathRooted($relative) -or $relative.Contains('..')) {
            throw "Unsafe enhancement path: $relative"
        }
        $file = Join-Path $destination $relative
        if (-not (Test-Path $file)) { throw "Enhancement file missing: $relative" }
        $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne ([string]$entry.sha256).Trim().ToLowerInvariant()) { throw "Enhancement digest mismatch: $relative" }
        $verified++
    }
    Write-Output "enhanceVersion=$($manifest.version)"
    Write-Output "enhanceFiles=$verified"
}

if ([string]::IsNullOrWhiteSpace($AssetUrl)) {
    if (-not (Test-Path $destination)) {
        throw "Enhancement pack directory is missing: $destination. Provide -AssetUrl or stage runtime/enhance/manifest.json for local full testing."
    }
    Test-EnhanceManifest $manifestPath
    Write-Output "Enhancement pack OK: $destination"
    return
}

if ([string]::IsNullOrWhiteSpace($ExpectedArchiveSha256) -or $ExpectedArchiveSha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw 'ExpectedArchiveSha256 is required when downloading an enhancement pack.'
}
$temp = Join-Path ([System.IO.Path]::GetTempPath()) "miruplay-enhance-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $temp | Out-Null
$archive = Join-Path $temp 'enhance.zip'
try {
    Invoke-WebRequest -Uri $AssetUrl -OutFile $archive -TimeoutSec 300
    $actual = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $ExpectedArchiveSha256.ToLowerInvariant()) { throw "Enhancement archive digest mismatch: $actual" }
    Expand-Archive -Path $archive -DestinationPath $temp -Force
    $extracted = Join-Path $temp 'enhance'
    if (-not (Test-Path (Join-Path $extracted 'manifest.json'))) {
        $manifest = Get-ChildItem $temp -Recurse -Filter manifest.json | Select-Object -First 1
        if (-not $manifest) { throw 'Downloaded enhancement archive does not contain manifest.json.' }
        $extracted = $manifest.DirectoryName
    }
    Remove-Item $destination -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item (Join-Path $extracted '*') $destination -Recurse -Force
    Test-EnhanceManifest (Join-Path $destination 'manifest.json')
    Write-Output "archiveSha256=$actual"
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
