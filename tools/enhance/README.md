# tools/enhance — RIFE enhancement pack pin and build notes

`rife-enhance-manifest.json` is the **source of truth** for the optional RIFE enhancement pack:

- `version` – pack version used by settings/status and by `tools/Get-EnhancementPack.ps1 -ExpectedVersion`.
- `archive.url/sha256/size` – GitHub Release asset (tag `enhance-rife-<version>`, asset `rife-<version>.7z`).
- `files[]` – every file inside the archive under `enhance/`, relative to the pack root, with SHA-256 and size.
  The app installs from this list and rejects any unexpected/missing file.
- `models[]` / `licenses[]` – metadata for diagnostics and notices.

The app embeds this file as `MiruPlay.Windows.rife-enhance-manifest.json` (see `MiruPlay.Windows.csproj`). The
full edition publishes the extracted pack at `runtime/enhance/` with an aggregate
`manifest.json` of `{ "version": "...", "files": [ { "path": "...", "sha256": "..." } ] }`
(this is the format `tools/Publish-Release.ps1` validates for the full edition).

## Rebuilding the pack archive (release engineering)

Inputs are pinned upstream portable distributions (downloaded once, verify hashes before staging):

| Input | Source | SHA-256 |
|---|---|---|
| VapourSynth portable R73 + Python 3.14 | `https://github.com/hooke007/dotfiles/releases/download/mpv_addones/py.3_14_3+vs.R73-portable.7z` | `e3e6b432cba9f1a8f69a9d00c8d3ed8b29bec615bfd04a9409b1393adb2bc9cd` |
| RIFE ncnn core + models | `https://github.com/hooke007/dotfiles/releases/download/mpv_addones/vs-k7sfunc.0_6_3.rife_std-core_models.7z` | `55d6fa699308db9dc1b403ff4c7e4d3ef074e567a0feb3f614497d2078ef59bf` |
| License texts | repository `licenses/` | — |

Staging layout before packing:

```text
staging/            # == runtime/enhance/rife
  VSScript.dll, VSPipe.exe, portable.vs, python*.dll/zip/_pth, Lib/site-packages/...
  vs-coreplugins/  vs-scripts/.keep
  vs-plugins/librife_windows_x86-64.dll
  vs-plugins/models/rife-v4.6_ensembleFalse/  vs-plugins/models/rife-v4.25-lite_ensembleFalse/
  selftest/selftest_480p.vpy
  licenses/        # VapourSynth-LGPL-2.1.txt, Python-PSF-2.0.txt, RIFE-ncnn-Vulkan-MIT.txt, ncnn-BSD-3-Clause.txt, RIFE-MIT.txt
```

Then either pack by hand or (recommended) run the reproduction helper:

```powershell
.\tools\New-RifeEnhancePack.ps1 -StagedRoot <staging> -Version 2026.11 \
    -AssetUrl https://github.com/ModerRAS/MiruPlay-Windows/releases/download/enhance-rife-2026.11/rife-2026.11.7z
```

It writes the deterministic 7z archive to `artifacts/enhance/rife-<version>.7z`, the aggregate
manifest to `<staging>/manifest.json`, and updates `tools/enhance/rife-enhance-manifest.json`.
7-Zip is pinned by the `enhancement-pack` workflow (7za 25.01, SHA-256 `26817725650583d99ca3e617a618dd75c0f71bd316b5761780b7361f5f824cad`).
Publish is CI-only: `.github/workflows/enhancement-pack.yml` rebuilds the 7z from the pinned
upstreams, verifies the SHA-256 against this manifest, and uploads it to release tag
`enhance-rife-<version>` as `rife-<version>.7z`. Do not edit the manifest SHA-256 by hand.

## Verification / consumption

- Full edition: `tools/Get-EnhancementPack.ps1` downloads the asset, verifies SHA-256, extracts
  `enhance/`, and `tools/Publish-Release.ps1 -Edition full` validates every listed file before
  publishing it under `runtime/enhance/`.
- Lite edition: never contains the enhancement pack.
- App-side install path: `%LOCALAPPDATA%/MiruPlay/enhance/rife`; the app verifies the same
  manifest per-file and then runs the VSPipe self-test.
