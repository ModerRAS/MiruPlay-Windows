param(
    [string]$ManifestPath = (Join-Path $PSScriptRoot 'enhance\rife-enhance-manifest.json'),
    [string]$Destination = (Join-Path $PSScriptRoot '..\runtime\enhance\rife'),
    [string]$CacheDirectory = (Join-Path $PSScriptRoot '..\.cache\enhance'),
    [string]$ArchivePath = ''
)

# CI release consumer for the RIFE enhancement pack. The pack is built/published by
# rife's enhancement-pack workflow as GitHub Release asset rife-<version>.7z under
# tag enhance-rife-<version>. This script only downloads/verifies/installs it.
$ErrorActionPreference = 'Stop'
$manifestPath = [System.IO.Path]::GetFullPath($ManifestPath)
$destination = [System.IO.Path]::GetFullPath($Destination)
$cacheDirectory = [System.IO.Path]::GetFullPath($CacheDirectory)
if (-not (Test-Path $manifestPath)) { throw "RIFE enhancement manifest is missing: $manifestPath" }
$manifest = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($manifestPath)) | ConvertFrom-Json
$version = [string]$manifest.version
$archive = $manifest.sourceArchive
if ([string]::IsNullOrWhiteSpace($version) -or $null -eq $archive) { throw 'RIFE enhancement manifest is incomplete.' }
if (([string]$archive.sha256) -notmatch '^[0-9a-fA-F]{64}$' -or [long]$archive.size -le 0) {
    throw 'RIFE enhancement manifest sourceArchive.sha256/size is invalid.'
}
$expectedHash = ([string]$archive.sha256).ToLowerInvariant()
$expectedSize = [long]$archive.size
$assetName = "rife-$version.7z"
$releaseTag = "enhance-rife-$version"

New-Item -ItemType Directory -Force -Path $cacheDirectory | Out-Null
if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    $ArchivePath = Join-Path $cacheDirectory $assetName
    $downloaded = $false
    if (Get-Command gh.exe -ErrorAction SilentlyContinue) {
        $ghToken = $env:GH_TOKEN
        $env:GH_TOKEN = $ghToken
        & gh release download $releaseTag --pattern $assetName --dir $cacheDirectory --clobber 2>&1 | Out-Host
        $downloaded = ($LASTEXITCODE -eq 0 -and (Test-Path $ArchivePath))
    }
    if (-not $downloaded -and -not [string]::IsNullOrWhiteSpace([string]$archive.url) -and
        ([string]$archive.url) -notmatch 'example\.invalid' -and
        ([string]$archive.url) -match '^https?://') {
        Invoke-WebRequest -Uri ([string]$archive.url) -OutFile $ArchivePath -TimeoutSec 600
        $downloaded = Test-Path $ArchivePath
    }
    if (-not $downloaded) {
        throw "RIFE enhancement pack asset $assetName was not found (release tag $releaseTag). Run the enhancement-pack workflow (rife) first, then rerun this release workflow. Expected sha256=$expectedHash size=$expectedSize."
    }
}
if (-not (Test-Path $ArchivePath)) { throw "RIFE enhancement archive is missing: $ArchivePath" }
$actualSize = (Get-Item $ArchivePath).Length
if ($actualSize -ne $expectedSize) { throw "RIFE enhancement archive size mismatch: $actualSize != $expectedSize" }
$actualHash = (Get-FileHash $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) { throw "RIFE enhancement archive SHA-256 mismatch: $actualHash" }
Write-Output "enhanceReleaseTag=$releaseTag"
Write-Output "enhanceAsset=$assetName"
Write-Output "enhanceArchiveSha256=$actualHash"
& (Join-Path $PSScriptRoot 'Get-EnhancementPack.ps1') -ArchivePath $ArchivePath -Destination $destination | Where-Object { $_ -match '^enhance' } | ForEach-Object { Write-Output $_ }
