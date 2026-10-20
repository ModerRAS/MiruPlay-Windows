# Windows distribution

MiruPlay publishes Windows x64 artifacts from one self-contained `.NET 10` publish directory:

- `MiruPlay-<version>-win-x64-portable.zip`: portable application with the .NET runtime and pinned in-process libmpv runtime.
- `MiruPlay-<version>-win-x64-setup.exe`: per-user Inno Setup installer. The default directory is `%LOCALAPPDATA%\Programs\MiruPlay`; elevation is not required.
- `MiruPlay-<version>-win-x64-symbols.zip`: first-party portable PDB files.
- `release-manifest.json`: build/runtime metadata, signing state, and mpv provenance.
- `windows-update.json`: updater-compatible GitHub Release metadata and SHA-256/size for the `*-setup.exe` asset (generated when `-SkipInstaller` is not used).
- `SHA256SUMS.txt`: SHA-256 for every release artifact and manifest.

Both application formats contain `THIRD-PARTY-NOTICES.md` and a `licenses` directory. Packaging derives `licenses/NuGet-Packages.json` from the restored assets file, copies exact .NET/runtime package notices, and includes the required mpv, FFmpeg, Protobuf, Apache, and MIT license texts.

The installer removes application files but intentionally preserves user configuration, DPAPI secrets, caches, and `state.db` under `%LOCALAPPDATA%\MiruPlay`.

## Build locally

Requirements: Windows 10/11, .NET 10 SDK, 7-Zip, and Inno Setup 6.

```powershell
winget install --id JRSoftware.InnoSetup --exact --scope user
.\tools\Publish-Release.ps1 -Version 0.1.0
.\tools\Test-ReleaseArtifacts.ps1 -ReleaseDirectory .\artifacts\release
```

`Publish-Release.ps1` emits `windows-update.json` beside the downloadable assets. Its `downloadUrl` points to the tag asset for `-GitHubRepository` (default `ModerRAS/MiruPlay-Windows`); host that JSON at a stable URL and configure the same URL in WebControl. `-SkipInstaller` creates a portable-only build and therefore does not emit an update manifest.

`Publish-Release.ps1` downloads the pinned libmpv archive only when the local runtime is missing and verifies its hard-coded SHA-256 value before extraction. Use `-SkipLibMpvDownload` to require a pre-existing runtime, or `-SkipInstaller` for a portable-only developer build.

## Signing policy

Unsigned local and CI artifacts are explicit: `release-manifest.json` contains `"signing": "unsigned"`. A release is Authenticode-signed only when both GitHub Actions secrets are configured:

- `WINDOWS_SIGNING_CERT_BASE64`: base64-encoded PFX certificate.
- `WINDOWS_SIGNING_CERT_PASSWORD`: PFX password.

The release script signs `MiruPlay.exe`, `MiruPlay.dll`, and the final installer with SHA-256 and a trusted timestamp, then verifies each signature with `signtool /pa`. It never signs the third-party mpv binary. Certificate material is written only to the runner temporary directory and deleted after packaging.

A public production release should be treated as unsigned unless the manifest says `authenticode`. The portable ZIP and installer always remain independently verifiable through `SHA256SUMS.txt`.

## CI and GitHub Releases

Pull requests and branch pushes run restore, warning-as-error Release build, tests, dependency audit, and a mandatory real-mpv integration job using the pinned runtime and tracked smoke fixture. A `v<major>.<minor>.<patch>` tag or manual workflow dispatch also builds and validates release artifacts. Tag builds upload the verified files to the matching GitHub Release; manual runs keep them as Actions artifacts without creating a tag or release.

## Release history

- **v0.1.5** (2026-10-20, `Latest`): portable-only release, built locally from `2c715a6` via `Publish-Release.ps1 -Version 0.1.5 -SkipInstaller` and verified with `Test-ReleaseArtifacts.ps1 -SkipInstaller` (3 hashes, portable launch test). Assets: portable ZIP, symbols ZIP, `SHA256SUMS.txt`, `release-manifest.json`; unsigned. No installer/`windows-update.json` in this release (installer deferred: no Inno Setup on the build machine, no code-signing certificate).

`tools/Test-ReleaseArtifacts.ps1` verifies every checksum, expands and launches the portable package, silently installs and launches the installer, closes the application through its normal WPF lifecycle, and silently uninstalls it. GitHub-hosted Windows runners provide the clean-user validation lane.
