[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$VapourSynthArchive,
    [Parameter(Mandatory)][string]$RifeArchive,
    [Parameter(Mandatory)][string]$SevenZipPath,
    [string]$Destination,
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
function Resolve-AbsolutePath {
    param([string]$Path, [string]$BasePath)
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}
$basePath = (Get-Location).Path
function Copy-TextFileAsLf([string]$Source, [string]$Destination) {
    # Normalize CRLF to LF at the byte level, preserving any UTF-8 BOM and the final newline.
    $bytes = [System.IO.File]::ReadAllBytes($Source)
    $output = [System.Collections.Generic.List[byte]]::new($bytes.Length)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -eq 13 -and ($i + 1) -lt $bytes.Length -and $bytes[$i + 1] -eq 10) { continue }
        $output.Add($bytes[$i])
    }
    [System.IO.File]::WriteAllBytes($Destination, $output.ToArray())
}

$repo = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')) } else { [System.IO.Path]::GetFullPath($RepositoryRoot) }
if ([string]::IsNullOrWhiteSpace($Destination)) { $Destination = Join-Path $repo 'artifacts\enhance-stage' }
$SevenZipPath = Resolve-AbsolutePath $SevenZipPath $basePath
$VapourSynthArchive = Resolve-AbsolutePath $VapourSynthArchive $basePath
$RifeArchive = Resolve-AbsolutePath $RifeArchive $basePath
$Destination = Resolve-AbsolutePath $Destination $basePath
if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) { $repo = Resolve-AbsolutePath $RepositoryRoot $basePath }
if (-not (Test-Path -LiteralPath $SevenZipPath)) { throw "7-Zip was not found: $SevenZipPath" }
if (-not (Test-Path -LiteralPath $VapourSynthArchive)) { throw "VapourSynth archive not found: $VapourSynthArchive" }
if (-not (Test-Path -LiteralPath $RifeArchive)) { throw "RIFE archive not found: $RifeArchive" }

$destination = $Destination
Remove-Item $destination -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$stageParent = Split-Path -Parent $destination
New-Item -ItemType Directory -Force -Path $stageParent | Out-Null
$temp = Join-Path $stageParent ("miruplay-rife-stage-" + [Guid]::NewGuid().ToString('N'))
$vsExtract = Join-Path $temp 'vs'
$rifeExtract = Join-Path $temp 'rife'
New-Item -ItemType Directory -Force -Path $vsExtract, $rifeExtract | Out-Null
try {
    & $SevenZipPath x $VapourSynthArchive "-o$vsExtract" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'VapourSynth archive extraction failed.' }
    & $SevenZipPath x $RifeArchive "-o$rifeExtract" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'RIFE archive extraction failed.' }

    # 1) portable VapourSynth runtime at the pack root (trim doc/sdk/pip/CLI extras).
    $excludedRootFiles = @(
        '7z.dll', '7z.exe', 'AVFS.exe', 'get-pip.py', 'LICENSE.txt', 'MANIFEST.in',
        'pfm-192-vapoursynth-win.exe', 'pythonw.exe', 'vsgenstubs.py',
        'VSScriptPython38.dll', 'VSVFW.dll', 'python.cat', 'vsrepo.py'
    )
    Get-ChildItem -LiteralPath $vsExtract -File | Where-Object { $_.Name -notin $excludedRootFiles } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination -Force }

    $sitePackages = Join-Path $destination 'Lib\site-packages'
    New-Item -ItemType Directory -Force -Path $sitePackages | Out-Null
    foreach ($item in 'vapoursynth.dll', 'vapoursynth.pyd', 'vapoursynth-73.dist-info', 'vsscript') {
        $source = Join-Path $vsExtract (Join-Path 'Lib\site-packages' $item)
        if (-not (Test-Path -LiteralPath $source)) { throw "VapourSynth portable package is missing Lib\site-packages\$item" }
        Copy-Item -LiteralPath $source -Destination $sitePackages -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path (Join-Path $destination 'vs-coreplugins') | Out-Null
    Copy-Item -LiteralPath (Join-Path $vsExtract 'vs-coreplugins\AvsCompat.dll') -Destination (Join-Path $destination 'vs-coreplugins\AvsCompat.dll') -Force

    $bomOnly = [byte[]](0xEF, 0xBB, 0xBF)
    New-Item -ItemType Directory -Force -Path (Join-Path $destination 'vs-scripts'), (Join-Path $destination 'vs-plugins') | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $destination 'vs-scripts\.keep'), $bomOnly)
    [System.IO.File]::WriteAllBytes((Join-Path $destination 'vs-plugins\.keep'), $bomOnly)

    # 2) RIFE ncnn plugin + supported models.
    Copy-Item -LiteralPath (Join-Path $rifeExtract 'librife_windows_x86-64.dll') -Destination (Join-Path $destination 'vs-plugins\librife_windows_x86-64.dll') -Force
    foreach ($model in 'rife-v4.6_ensembleFalse', 'rife-v4.25-lite_ensembleFalse') {
        Copy-Item -LiteralPath (Join-Path $rifeExtract (Join-Path 'models' $model)) -Destination (Join-Path $destination (Join-Path 'vs-plugins\models' $model)) -Recurse -Force
    }

    # 3) License texts from the repository.
    $licenses = Join-Path $destination 'licenses'
    New-Item -ItemType Directory -Force -Path $licenses | Out-Null
    foreach ($license in 'VapourSynth-LGPL-2.1.txt', 'Python-PSF-2.0.txt', 'RIFE-ncnn-Vulkan-MIT.txt', 'ncnn-BSD-3-Clause.txt', 'RIFE-MIT.txt') {
        Copy-TextFileAsLf (Join-Path $repo (Join-Path 'licenses' $license)) (Join-Path $licenses $license)
    }

    # 4) Synthetic VSPipe self-test script.
    $selftest = Join-Path $destination 'selftest'
    New-Item -ItemType Directory -Force -Path $selftest | Out-Null
    $scriptLines = @(
        'import vapoursynth as vs',
        'from vapoursynth import core',
        'clip = core.std.BlankClip(width=854, height=480, fpsnum=24, fpsden=1, length=24, format=vs.YUV420P8, color=[32,96,160])',
        'clip = core.text.FrameNum(clip)',
        'src_fmt = clip.format.id',
        'clip = core.resize.Bilinear(clip=clip, format=vs.RGBS, matrix_in_s="709")',
        'clip = core.rife.RIFE(clip=clip, model=23, factor_num=2, factor_den=1, gpu_id=0, gpu_thread=2, sc=False)',
        'clip = core.resize.Bilinear(clip=clip, format=src_fmt, matrix_s="709")',
        'clip.set_output(0)'
    )
    $script = ($scriptLines -join "`n")
    [System.IO.File]::WriteAllText((Join-Path $selftest 'selftest_480p.vpy'), $script, [System.Text.UTF8Encoding]::new($false))

    $count = (Get-ChildItem $destination -Recurse -File | Measure-Object).Count
    Write-Output "stageFiles=$count"
    Write-Output "stageRoot=$destination"
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
