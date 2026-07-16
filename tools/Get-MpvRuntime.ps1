param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\runtime\mpv')
)

$ErrorActionPreference = 'Stop'
$version = '20260610'
$asset = 'mpv-x86_64-20260610-git-304426c.7z'
$expectedHash = 'facac536baa73c7b925771af5e39a3c9cb16b8d75b59a6e9800de89799dffca7'
$url = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/$version/$asset"
$archive = Join-Path $Destination $asset

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Invoke-WebRequest -Uri $url -OutFile $archive
$actualHash = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    Remove-Item $archive -Force
    throw "mpv archive digest mismatch: $actualHash"
}

& 7z x $archive "-o$Destination" -y | Out-Null
if ($LASTEXITCODE -ne 0) {
    Remove-Item $archive -Force -ErrorAction SilentlyContinue
    throw "7-Zip extraction failed with exit code $LASTEXITCODE."
}
Remove-Item $archive -Force

$mpvPath = Join-Path $Destination 'mpv.exe'
$d3dCompilerPath = Join-Path $Destination 'd3dcompiler_43.dll'
if (-not (Test-Path $mpvPath) -or -not (Test-Path $d3dCompilerPath)) {
    throw 'The verified archive did not contain the required mpv runtime files.'
}
$versionLine = & $mpvPath --version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($versionLine)) {
    throw 'The extracted mpv executable did not report a version.'
}

# mpv's Windows console entry point reports -1 after printing --version; do not leak that incidental code to callers.
$global:LASTEXITCODE = 0
Write-Output "sha256=$actualHash"
Write-Output $versionLine
