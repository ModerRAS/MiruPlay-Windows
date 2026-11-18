[CmdletBinding()]
param(
    # Target directory inside the repo/release tree. Defaults to <repo>\runtime\enhance\rife
    [string]$Destination,
    # Pinned manifest checked into the repo. Defaults to <repo>\tools\enhance\rife-enhance-manifest.json
    [string]$ManifestPath,
    # Optional local archive (CI cache / offline verify). When omitted the archive is downloaded.
    [string]$ArchivePath,
    # Optional archive URL override (mirror). Defaults to sourceArchive.url in the manifest.
    [string]$ArchiveUrl,
    # Reinstall even when the destination already matches.
    [switch]$Force,
    # Only verify an existing destination against the manifest (no network, no writes).
    [switch]$VerifyOnly,
    # Rebuild manifest.files/sourceArchive hashes from -ArchivePath (release engineer use).
    [switch]$GenerateManifest,
    # Optional version override for -GenerateManifest.
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$scriptRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot '..\runtime\enhance\rife'))
}
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot 'enhance\rife-enhance-manifest.json'))
}
$Destination = [System.IO.Path]::GetFullPath($Destination)
$ManifestPath = [System.IO.Path]::GetFullPath($ManifestPath)

function Get-FileSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-JsonFile([string]$Path) {
    # Explicit UTF-8: Windows PowerShell 5.1 Get-Content defaults to the ANSI code page.
    return [System.IO.File]::ReadAllText($Path, [System.Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
}

function Write-EnhanceResult([hashtable]$Fields, [string]$Status) {
    Write-Output "enhanceStatus=$Status"
    Write-Output "enhanceVersion=$($Fields.version)"
    Write-Output "enhanceManifestSha256=$($Fields.manifestSha256)"
    Write-Output "enhanceArchiveSha256=$($Fields.archiveSha256)"
    Write-Output "enhanceArchiveBytes=$($Fields.archiveBytes)"
    Write-Output "enhanceFileCount=$($Fields.fileCount)"
    Write-Output "enhanceUnpackedBytes=$($Fields.unpackedBytes)"
    Write-Output "enhancePath=$($Destination.Replace('\', '/'))"
}

function Assert-SafeRelativePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Enhancement manifest contains an empty file path.' }
    $normalized = $Path.Replace('\', '/')
    if ($normalized.StartsWith('/') -or $normalized -match '^[A-Za-z]:' -or (($normalized -split '/') -contains '..')) {
        throw "Enhancement manifest contains an unsafe path: $Path"
    }
}

function Test-EnhancementFileSet([string]$Root, $Manifest) {
    $expected = @{}
    foreach ($file in $Manifest.files) {
        Assert-SafeRelativePath ([string]$file.path)
        $expected[[string]$file.path] = $file
    }
    $actual = @(Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction Stop | Where-Object { $_.Name -ne 'manifest.json' })
    foreach ($file in $actual) {
        $relative = $file.FullName.Substring($Root.Length + 1).Replace('\', '/')
        if (-not $expected.ContainsKey($relative)) { throw "Unexpected file in enhancement directory: $relative" }
        $entry = $expected[$relative]
        if ([long]$file.Length -ne [long]$entry.size) { throw "Size mismatch for enhance file: $relative" }
        if ((Get-FileSha256 $file.FullName) -ne [string]$entry.sha256) { throw "SHA-256 mismatch for enhance file: $relative" }
        $expected.Remove($relative)
    }
    if ($expected.Count -gt 0) {
        throw "Missing enhancement file(s): $(($expected.Keys | Sort-Object) -join ', ')"
    }
    return $actual.Count
}

function Get-SevenZip {
    $command = Get-Command 7z -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $candidates = @(
        (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
        (Join-Path $programFilesX86 '7-Zip\7z.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\7-Zip\7z.exe')
    )
    $match = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $match) { throw '7-Zip (7z.exe) is required for enhancement pack extraction.' }
    return $match
}

function Assert-ArchiveEntriesSafe([string]$SevenZip, [string]$Archive) {
    $listing = & $SevenZip l -slt $Archive 2>&1
    if ($LASTEXITCODE -ne 0) { throw "7-Zip listing failed for $Archive (exit code $LASTEXITCODE)." }
    # 7z -slt prints the archive itself as the first Path entry; skip it, validate every stored entry.
    $entryPaths = @(@($listing) | Where-Object { $_ -match '^Path = (.+)$' } | ForEach-Object { $Matches[1] })
    if ($entryPaths.Count -gt 1) { $entryPaths = @($entryPaths[1..($entryPaths.Count - 1)]) } else { $entryPaths = @() }
    foreach ($entryPath in $entryPaths) {
        Assert-SafeRelativePath $entryPath
    }
}

if (-not (Test-Path -LiteralPath $ManifestPath)) { throw "Enhancement manifest was not found: $ManifestPath" }
$manifest = Read-JsonFile $ManifestPath
if ($manifest.schemaVersion -ne 1) { throw "Unsupported enhancement manifest schemaVersion: $($manifest.schemaVersion)" }
if ($manifest.id -ne 'rife-vulkan') { throw "Unexpected enhancement id: $($manifest.id)" }
if (-not $manifest.files -or @($manifest.files).Count -eq 0) { throw 'Enhancement manifest does not list any files.' }
$expectedVersion = if ([string]::IsNullOrWhiteSpace($Version)) { [string]$manifest.version } else { $Version }

if ($GenerateManifest) {
    if ([string]::IsNullOrWhiteSpace($ArchivePath)) { throw '-GenerateManifest requires -ArchivePath.' }
    $archive = (Resolve-Path -LiteralPath $ArchivePath).Path
    $sevenZip = Get-SevenZip
    Assert-ArchiveEntriesSafe $sevenZip $archive
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("miruplay-enhance-generate-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $temp | Out-Null
    try {
        & $sevenZip x $archive "-o$temp" -y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7-Zip extraction failed (exit code $LASTEXITCODE)." }
        $files = @(Get-ChildItem -LiteralPath $temp -Recurse -File | ForEach-Object {
            [ordered]@{
                path = $_.FullName.Substring($temp.Length + 1).Replace('\', '/')
                sha256 = Get-FileSha256 $_.FullName
                size = $_.Length
            }
        } | Sort-Object { $_.path })
        $unpacked = [long]0
        foreach ($entry in $files) { $unpacked += [long]$entry['size'] }
        $manifest.version = $expectedVersion
        $manifest.files = $files
        $manifest.generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        $manifest.sourceArchive.sha256 = Get-FileSha256 $archive
        $manifest.sourceArchive.size = (Get-Item -LiteralPath $archive).Length
        $manifest.sourceArchive.unpackedSize = $unpacked
        $manifest.sourceArchive.fileCount = $files.Count
        $json = $manifest | ConvertTo-Json -Depth 12
        [System.IO.File]::WriteAllText($ManifestPath, $json, [System.Text.UTF8Encoding]::new($false))
        Write-EnhanceResult ([ordered]@{
            version = $manifest.version
            manifestSha256 = Get-FileSha256 $ManifestPath
            archiveSha256 = $manifest.sourceArchive.sha256
            archiveBytes = $manifest.sourceArchive.size
            fileCount = $files.Count
            unpackedBytes = $unpacked
        }) 'manifest-generated'
        return
    }
    finally {
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$manifestHash = Get-FileSha256 $ManifestPath
$archiveSha = [string]$manifest.sourceArchive.sha256
$fileCount = @($manifest.files).Count
$unpackedBytes = [long]$manifest.sourceArchive.unpackedSize
$fields = [ordered]@{
    version = $expectedVersion
    manifestSha256 = $manifestHash
    archiveSha256 = $archiveSha
    archiveBytes = [long]$manifest.sourceArchive.size
    fileCount = $fileCount
    unpackedBytes = $unpackedBytes
}

if ($VerifyOnly) {
    if (-not (Test-Path -LiteralPath (Join-Path $Destination 'manifest.json'))) { throw "Enhancement directory is not installed: $Destination" }
    $installed = Read-JsonFile (Join-Path $Destination 'manifest.json')
    if ([string]$installed.version -ne $expectedVersion) { throw "Installed enhancement version mismatch: $($installed.version) != $expectedVersion" }
    $null = Test-EnhancementFileSet $Destination $manifest
    Write-EnhanceResult $fields 'verified'
    return
}

if (-not $Force -and (Test-Path -LiteralPath (Join-Path $Destination 'manifest.json'))) {
    try {
        $installed = Read-JsonFile (Join-Path $Destination 'manifest.json')
        if ([string]$installed.version -eq $expectedVersion) {
            $null = Test-EnhancementFileSet $Destination $manifest
            Write-EnhanceResult $fields 'already'
            return
        }
        Write-Output "enhanceNote=installed version $($installed.version) differs; reinstalling $expectedVersion"
    }
    catch {
        Write-Output "enhanceNote=existing enhancement directory is invalid; reinstalling ($($_.Exception.Message))"
    }
}

$sevenZip = Get-SevenZip
$downloaded = $false
if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    $url = if ([string]::IsNullOrWhiteSpace($ArchiveUrl)) { [string]$manifest.sourceArchive.url } else { $ArchiveUrl }
    if ([string]::IsNullOrWhiteSpace($url)) { throw 'No archive URL configured in manifest and -ArchivePath was not supplied.' }
    $ArchivePath = Join-Path ([System.IO.Path]::GetTempPath()) ("miruplay-enhance-" + $archiveSha.Substring(0, 16) + '.7z')
    if (-not (Test-Path -LiteralPath $ArchivePath) -or (Get-FileSha256 $ArchivePath) -ne $archiveSha) {
        Write-Output "enhanceNote=downloading $url"
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $url -OutFile $ArchivePath -UseBasicParsing
    }
    $downloaded = $true
}
$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
$archiveItem = Get-Item -LiteralPath $archive
if ([long]$archiveItem.Length -ne [long]$manifest.sourceArchive.size) { throw "Enhancement archive size mismatch: $($archiveItem.Length) != $($manifest.sourceArchive.size)" }
if ((Get-FileSha256 $archive) -ne $archiveSha) { throw 'Enhancement archive SHA-256 mismatch.' }
Assert-ArchiveEntriesSafe $sevenZip $archive

$parent = Split-Path -Parent $Destination
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$staging = Join-Path $parent ('.rife-staging-' + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $parent ('.rife-backup-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
$installedOk = $false
try {
    & $sevenZip x $archive "-o$staging" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip extraction failed (exit code $LASTEXITCODE)." }
    $null = Test-EnhancementFileSet $staging $manifest
    $installedManifest = $manifest | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText((Join-Path $staging 'manifest.json'), $installedManifest, [System.Text.UTF8Encoding]::new($false))

    if (Test-Path -LiteralPath $Destination) {
        Move-Item -LiteralPath $Destination -Destination $backup
    }
    Move-Item -LiteralPath $staging -Destination $Destination
    $installedOk = $true
    if (Test-Path -LiteralPath $backup) {
        Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
    }
}
catch {
    if (-not $installedOk -and (Test-Path -LiteralPath $backup)) {
        if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force -ErrorAction SilentlyContinue }
        Move-Item -LiteralPath $backup -Destination $Destination -ErrorAction SilentlyContinue
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue }
    if ($downloaded) { Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue }
}

$null = Test-EnhancementFileSet $Destination $manifest
Write-EnhanceResult $fields 'installed'
