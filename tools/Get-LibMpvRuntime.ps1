param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\runtime\libmpv')
)

$ErrorActionPreference = 'Stop'
$version = '20261021'
$asset = 'mpv-dev-x86_64-20261021-git-e471496c56.7z'
$expectedArchiveHash = 'efc719eff3dc5954b4a5efc800632808bcec01cc9034940dd14e5eb7fe9ea633'
$expectedLibraryHash = '04e4d27dc385dc18ddd93fbe69c75e635b864f3c6ec53a9122df54c2f7842f4a'
$url = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/$version/$asset"
$destination = [System.IO.Path]::GetFullPath($Destination)
$archive = Join-Path $destination $asset
$extractDirectory = Join-Path $destination '.extract-libmpv'
$libraryPath = Join-Path $destination 'libmpv-2.dll'

New-Item -ItemType Directory -Force -Path $destination | Out-Null
Remove-Item $extractDirectory -Recurse -Force -ErrorAction SilentlyContinue
Invoke-WebRequest -Uri $url -OutFile $archive
$actualArchiveHash = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
if ($actualArchiveHash -ne $expectedArchiveHash) {
    Remove-Item $archive -Force -ErrorAction SilentlyContinue
    throw "libmpv archive digest mismatch: $actualArchiveHash"
}

New-Item -ItemType Directory -Force -Path $extractDirectory | Out-Null
& 7z e $archive 'libmpv-2.dll' "-o$extractDirectory" -y | Out-Null
if ($LASTEXITCODE -ne 0) {
    Remove-Item $archive, $extractDirectory -Recurse -Force -ErrorAction SilentlyContinue
    throw "7-Zip extraction failed with exit code $LASTEXITCODE."
}
$extractedLibrary = Join-Path $extractDirectory 'libmpv-2.dll'
if (-not (Test-Path $extractedLibrary)) {
    Remove-Item $archive, $extractDirectory -Recurse -Force -ErrorAction SilentlyContinue
    throw 'The verified archive did not contain libmpv-2.dll.'
}
$actualLibraryHash = (Get-FileHash -Algorithm SHA256 $extractedLibrary).Hash.ToLowerInvariant()
if ($actualLibraryHash -ne $expectedLibraryHash) {
    Remove-Item $archive, $extractDirectory -Recurse -Force -ErrorAction SilentlyContinue
    throw "libmpv DLL digest mismatch: $actualLibraryHash"
}

Move-Item $extractedLibrary $libraryPath -Force
Remove-Item $archive, $extractDirectory -Recurse -Force -ErrorAction SilentlyContinue
Write-Output "archiveSha256=$actualArchiveHash"
Write-Output "libmpvSha256=$actualLibraryHash"
