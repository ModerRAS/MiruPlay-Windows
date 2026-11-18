# Windows distribution

MiruPlay publishes Windows x64 artifacts in two editions from one self-contained `.NET 10` publish directory:

- **lite** (default): application + pinned in-process libmpv + SDL3 + base Anime4K Fast assets.
- **full**: lite + optional enhancement pack from `runtime/enhance/` (portable VapourSynth + MVTools, RIFE ncnn + models, optional large shaders). The pack is not in git; `tools/Get-EnhancementPack.ps1` pins its version, archive SHA-256 and file manifest. `Publish-Release.ps1 -Edition full` fails if the pack is missing; CI skips full until the pack is delivered.

Artifacts are edition-scoped:

- `MiruPlay-<version>-win-x64-<edition>-portable.zip`: portable application with the .NET runtime, pinned in-process libmpv runtime, and the optional pinned SDL3 runtime for the TV-mode gamepad backend.
- `MiruPlay-<version>-win-x64-<edition>-setup.exe`: per-user Inno Setup installer. lite uses `%LOCALAPPDATA%\Programs\MiruPlay`; full uses `%LOCALAPPDATA%\Programs\MiruPlay Full` and a distinct AppId, so both editions can coexist. `edition.txt` is written into the install directory.
- `MiruPlay-<version>-win-x64-<edition>-symbols.zip`: first-party portable PDB files.
- `release-manifest-<edition>.json`: build/runtime metadata, `edition`, `enhancements[]`, signing state, mpv provenance, and SDL3 provenance.
- `windows-update-<edition>.json`: edition-scoped update manifest; lite installs only lite updates and full only full updates.
- `windows-update.json`: updater-compatible GitHub Release metadata and SHA-256/size for the `*-setup.exe` asset (generated when `-SkipInstaller` is not used).
- `SHA256SUMS.txt`: SHA-256 for every release artifact and manifest.

Both application formats contain `THIRD-PARTY-NOTICES.md` and a `licenses` directory. Packaging derives `licenses/NuGet-Packages.json` from the restored assets file, copies exact .NET/runtime package notices, and includes the required mpv, FFmpeg, Protobuf, Apache, and MIT license texts.

The installer removes application files but intentionally preserves user configuration, DPAPI secrets, caches, and `state.db` under `%LOCALAPPDATA%\MiruPlay`.

## Build locally

Requirements: Windows 10/11, .NET 10 SDK, 7-Zip, and Inno Setup 6. The SDL3 runtime is downloaded on demand by `tools/Get-Sdl3Runtime.ps1` (or already present under `runtime/sdl3`).

```powershell
winget install --id JRSoftware.InnoSetup --exact --scope user
.\tools\Publish-Release.ps1 -Version 0.1.0
.\tools\Test-ReleaseArtifacts.ps1 -ReleaseDirectory .\artifacts\release
```

`Publish-Release.ps1` emits `windows-update.json` beside the downloadable assets. Its `downloadUrl` points to the tag asset for `-GitHubRepository` (default `ModerRAS/MiruPlay-Windows`); host that JSON at a stable URL and configure the same URL in WebControl. `-SkipInstaller` creates a portable-only build and therefore does not emit an update manifest.

`Publish-Release.ps1` downloads the pinned libmpv archive only when the local runtime is missing and verifies its hard-coded SHA-256 value before extraction. The same pattern applies to SDL3:

- Pinned runtime: **SDL 3.4.20** (2026-11-03) from `libsdl-org/SDL`, asset `SDL3-3.4.20-win32-x64.zip`.
- Archive SHA-256: `368bed8b24e44ad9ea372990814f2343fcfb9e468abb7f8f4a3bc146c7304752`
- Packaged file: `runtime/sdl3/SDL3.dll` (SHA-256 `66844f10251cc1c10619ed5d8e4e3224f564900ee6d69ba87d63de782addae40`)
- When the DLL is missing, `Publish-Release.ps1` runs `tools/Get-Sdl3Runtime.ps1` and re-verifies the digest before publishing. Use `-SkipSdl3Download` to require a pre-existing runtime; a missing file then fails the build instead of silently shipping without SDL3.
- `test-ReleaseArtifacts.ps1` requires `runtime\sdl3\SDL3.dll` in the portable ZIP, after silent installation, and verifies the portable digest against the `sdl3` section of `release-manifest.json`.

Use `-SkipLibMpvDownload` to require a pre-existing libmpv runtime, or `-SkipInstaller` for a portable-only developer build.

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

`tools/Test-ReleaseArtifacts.ps1` verifies every checksum, expands and launches the portable package, silently installs and launches the installer, closes the application through its normal WPF lifecycle, and silently uninstalls it. GitHub-hosted Windows runners provide the clean-user validation lane. The release job builds lite always and full only when `runtime/enhance/manifest.json` exists; `Test-ReleaseArtifacts.ps1 -Edition <edition>` asserts lite has no enhancement assets and full contains the VapourSynth/RIFE components plus license files and hashes.