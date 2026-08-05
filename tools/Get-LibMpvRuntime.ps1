param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\runtime\libmpv')
)

$ErrorActionPreference = 'Stop'
$version = '20260610'
$asset = 'mpv-dev-x86_64-20260610-git-304426c.7z'
$expectedArchiveHash = '8cbb25ea784f01afbb3f904217cab1317430a8bcfd5680fd827a866367f71cc9'
$expectedLibraryHash = '5c876d79e070529128331591b48f87846fb30557f19c11280df9c6ee9b6dbafa'
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
