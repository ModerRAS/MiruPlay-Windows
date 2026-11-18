[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StagedRoot,
    [string]$Version = '2026.11',
    [string]$AssetUrl = '',
    [string]$OutputDirectory,
    [string]$ManifestPath,
    [string]$RepositoryRoot,
    [string]$SevenZipPath
)

$ErrorActionPreference = 'Stop'
function Resolve-AbsolutePath {
    param([string]$Path, [string]$BasePath)
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}
$basePath = (Get-Location).Path
function ConvertTo-JsonLiteral([string]$Value) {
    return $Value.Replace('\', '\\').Replace('"', '\"')
}

# Fixed serializer for the aggregate manifest embedded in the archive. Windows PowerShell 5.1
# and PowerShell 7 format ConvertTo-Json differently, which would make the 7z digest depend on
# the shell version; this reproduces the pinned 5.1 layout byte-for-byte on both.
function ConvertTo-AggregateJson([string]$Version, $Files) {
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.Append("{`r`n")
    [void]$sb.Append('    "version":  "').Append((ConvertTo-JsonLiteral $Version)).Append('",').Append("`r`n")
    [void]$sb.Append('    "files":  [').Append("`r`n")
    $first = $true
    foreach ($file in $Files) {
        if (-not $first) { [void]$sb.Append(',').Append("`r`n") }
        $first = $false
        [void]$sb.Append('                  {').Append("`r`n")
        [void]$sb.Append('                      "path":  "').Append((ConvertTo-JsonLiteral ([string]$file.path))).Append('",').Append("`r`n")
        [void]$sb.Append('                      "sha256":  "').Append((ConvertTo-JsonLiteral ([string]$file.sha256))).Append('"').Append("`r`n")
        [void]$sb.Append('                  }')
    }
    if (-not $first) { [void]$sb.Append("`r`n") }
    [void]$sb.Append('              ]').Append("`r`n").Append('}')
    return $sb.ToString()
}

$repo = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')) } else { [System.IO.Path]::GetFullPath($RepositoryRoot) }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repo 'artifacts\enhance' }
if ([string]::IsNullOrWhiteSpace($ManifestPath)) { $ManifestPath = Join-Path $repo 'tools\enhance\rife-enhance-manifest.json' }
if ([string]::IsNullOrWhiteSpace($AssetUrl)) {
    $AssetUrl = "https://github.com/ModerRAS/MiruPlay-Windows/releases/download/enhance-rife-$Version/rife-$Version.7z"
}
if ([string]::IsNullOrWhiteSpace($SevenZipPath)) {
    $candidate = @('7za', '7z') | ForEach-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
    if ($candidate) { $SevenZipPath = $candidate.Source }
}
if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) { $repo = Resolve-AbsolutePath $RepositoryRoot $basePath }
$OutputDirectory = Resolve-AbsolutePath $OutputDirectory $basePath
$ManifestPath = Resolve-AbsolutePath $ManifestPath $basePath
$SevenZipPath = Resolve-AbsolutePath $SevenZipPath $basePath
$stage = Resolve-AbsolutePath $StagedRoot $basePath

if (-not (Test-Path -LiteralPath $SevenZipPath)) {
    throw '7-Zip (7za.exe/7z.exe) is required to build the enhancement pack; pass -SevenZipPath.'
}
foreach ($required in @(
    'VSScript.dll',
    'VSPipe.exe',
    'vs-plugins\librife_windows_x86-64.dll',
    'vs-plugins\models\rife-v4.6_ensembleFalse\flownet.bin',
    'selftest\selftest_480p.vpy',
    'licenses'
)) {
    if (-not (Test-Path (Join-Path $stage $required))) { throw "staging root is missing $required" }
}

$fileEntries = @(Get-ChildItem -LiteralPath $stage -Recurse -File |
    Where-Object { $_.Name -ne 'manifest.json' } |
    ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            size = $_.Length
        }
    } | Sort-Object -Property { $_.path } -Culture 'zh-CN')
$unpacked = [long]0
foreach ($entry in $fileEntries) { $unpacked += [long]$entry.size }

$aggregate = [ordered]@{
    version = $Version
    files = @($fileEntries | ForEach-Object { [ordered]@{ path = $_.path; sha256 = $_.sha256 } })
}
[System.IO.File]::WriteAllText(
    (Join-Path $stage 'manifest.json'),
    (ConvertTo-AggregateJson -Version $Version -Files $fileEntries),
    [System.Text.UTF8Encoding]::new($false))

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$archivePath = Join-Path $OutputDirectory "rife-$Version.7z"
Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue

# Deterministic 7z: fixed timestamps/attributes, sorted relative file list, pinned 7-Zip.
$listPath = Join-Path $OutputDirectory "rife-$Version.files.txt"
$sorted = @(Get-ChildItem -LiteralPath $stage -Recurse -File |
    ForEach-Object { $_.FullName.Substring($stage.Length + 1).Replace('\', '/') } |
    Sort-Object -Culture 'zh-CN')
[System.IO.File]::WriteAllLines($listPath, $sorted, [System.Text.UTF8Encoding]::new($false))
Push-Location $stage
try {
    & $SevenZipPath a -t7z -mx=9 -mtm=off -mtc=off -mta=off $archivePath ('@' + $listPath) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip archive creation failed (exit code $LASTEXITCODE)." }
}
finally { Pop-Location }
Remove-Item -LiteralPath $listPath -Force -ErrorAction SilentlyContinue

$zipInfo = Get-Item -LiteralPath $archivePath
$zipHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    schemaVersion = 1
    id = 'rife-vulkan'
    version = $Version
    minAppVersion = '0.2.0'
    sourceArchive = [ordered]@{
        url = $AssetUrl
        sha256 = $zipHash
        size = $zipInfo.Length
        format = '7z'
        unpackedSize = $unpacked
        fileCount = $fileEntries.Count
    }
    components = [ordered]@{
        vapoursynth = 'R73'
        python = '3.14.3'
        rife = 'rife-ncnn-vulkan r9_mod_v33'
        models = @('4.6', '4.25-lite')
    }
    models = @(
        [ordered]@{ id = '4.6'; index = 23; default = $true; path = 'vs-plugins/models/rife-v4.6_ensembleFalse' },
        [ordered]@{ id = '4.25-lite'; index = 70; default = $false; path = 'vs-plugins/models/rife-v4.25-lite_ensembleFalse' }
    )
    licenses = @(
        [ordered]@{ component = 'VapourSynth'; license = 'LGPL-2.1'; file = 'licenses/VapourSynth-LGPL-2.1.txt' },
        [ordered]@{ component = 'Python'; license = 'PSF-2.0'; file = 'licenses/Python-PSF-2.0.txt' },
        [ordered]@{ component = 'RIFE ncnn plugin (HolyWu/styler00dollar)'; license = 'MIT'; file = 'licenses/RIFE-ncnn-Vulkan-MIT.txt' },
        [ordered]@{ component = 'ncnn'; license = 'BSD-3-Clause'; file = 'licenses/ncnn-BSD-3-Clause.txt' },
        [ordered]@{ component = 'RIFE models'; license = 'MIT'; file = 'licenses/RIFE-MIT.txt' }
    )
    files = $fileEntries
}
[System.IO.File]::WriteAllText(
    $ManifestPath,
    ($manifest | ConvertTo-Json -Depth 8),
    [System.Text.UTF8Encoding]::new($false))

Write-Output "packVersion=$Version"
Write-Output "packSha256=$zipHash"
Write-Output "packBytes=$($zipInfo.Length)"
Write-Output "packFiles=$($fileEntries.Count)"
Write-Output "packUnpackedBytes=$unpacked"
Write-Output "packArchive=$archivePath"
Write-Output "manifest=$ManifestPath"
