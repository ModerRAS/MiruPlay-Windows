# Third-Party Notices

MiruPlay Windows is distributed under GPL-3.0-or-later. The release package also contains third-party components governed by their own licenses.

## mpv/libmpv Windows build

- Project: mpv
- Upstream source: https://github.com/mpv-player/mpv/tree/bcde6b93cca4d128d84e1a60d1a6db2cd81c889e
- Windows build source: https://github.com/shinchiro/mpv-winbuild-cmake
- License information: https://github.com/mpv-player/mpv/blob/master/Copyright

The matching development archive also supplies the in-process library:

- Asset: `mpv-dev-x86_64-20261111-git-bcde6b93cc.7z`
- Archive SHA-256: `273c079b11ea5575822f972756f4c9a71149a5c10130353abeb8586738938452`
- Packaged file: `runtime/libmpv/libmpv-2.dll`
- DLL SHA-256: `88504ee5560942a955405e99dfb5971368e4cf851321f36565bf2a667271db1b`

The pinned build includes FFmpeg and other libraries. Their exact configuration and corresponding source revisions are published by the Windows build project above. MiruPlay loads the matching `libmpv-2.dll` in-process. The package's `licenses` directory includes the matching mpv copyright statement and GPL/LGPL texts plus FFmpeg's license summary and GPL/LGPL texts.

## RIFE frame interpolation enhancement pack (optional, full edition / user install)

The optional RIFE enhancement pack is not part of the lite edition; the full edition bundles it or the
Windows client downloads it from the project's GitHub Release asset.

- Pack version: `2026.11`; archive `rife-2026.11.7z`
- Archive SHA-256: `6d7b73986787273402db1ebc5d946cef63bcc209e28112cdd0c40499f79e609f` (31,240,946 B)
- Layout: `runtime/enhance/rife/{manifest.json,vs-plugins/,licenses/,selftest/}` (installed copy under `%LOCALAPPDATA%\MiruPlay\enhance\rife`)
- Components and licenses:
  - VapourSynth R73 (Python binding and VSScript/VSPipe binaries) - LGPL-2.1. Upstream: https://github.com/vapoursynth/vapoursynth. The DLLs are separate dynamically linked files; license text in `licenses/VapourSynth-LGPL-2.1.txt`.
  - Python 3.14.3 runtime - PSF-2.0. Upstream: https://www.python.org. License text in `licenses/Python-PSF-2.0.txt`.
  - RIFE ncnn Vulkan VapourSynth plugin (`librife_windows_x86-64.dll`, r9_mod_v33) - MIT (Copyright 2021-2022 HolyWu). Upstream: https://github.com/styler00dollar/VapourSynth-RIFE-ncnn-Vulkan. License text in `licenses/RIFE-ncnn-Vulkan-MIT.txt`.
  - ncnn (statically linked into the plugin) - BSD-3-Clause (Tencent). Upstream: https://github.com/Tencent/ncnn. License text in `licenses/ncnn-BSD-3-Clause.txt`.
  - RIFE model weights (rife-v4.6 / rife-v4.25-lite, converted by styler00dollar) - MIT (Megvii RIFE). Upstream: https://github.com/hzwer/ECCV2022-RIFE. License text in `licenses/RIFE-MIT.txt`.
- The pack's own `manifest.json` pins every file with SHA-256; the Windows client verifies the archive hash and each file before installing and rejects unexpected or missing files.

## SDL3 (gamepad backend)

- Project: SDL (Simple DirectMedia Layer)
- Upstream: https://github.com/libsdl-org/SDL
- Release: SDL 3.4.20 (2026-11-03), asset `SDL3-3.4.20-win32-x64.zip`
- Archive SHA-256: `368bed8b24e44ad9ea372990814f2343fcfb9e468abb7f8f4a3bc146c7304752`
- Packaged file: `runtime/sdl3/SDL3.dll`
- DLL SHA-256: `66844f10251cc1c10619ed5d8e4e3224f564900ee6d69ba87d63de782addae40`
- License: zlib (see `licenses/SDL3-zlib.txt`)

MiruPlay loads SDL3 dynamically for the optional TV-mode gamepad backend
(DualSense/DualShock/Switch Pro/8BitDo and Xbox controllers). When the DLL is
absent, MiruPlay falls back to the Windows XInput backend.

## Anime4K (optional video shaders)

- Project: Anime4K
- Upstream: https://github.com/bloc97/Anime4K
- Release: v4.0.1, asset `Anime4K_v4.0.zip`
- Archive SHA-256: `139cd282086457c5adc79caf7b75b8b825091d71c9b54958c18745fea62d7ed7`
- Packaged files: nine shader files under `runtime/shaders/anime4k/` (see
  `runtime/shaders/README.md`; each file is verified by SHA-256 in
  `tools/Get-Anime4KShaders.ps1`)
- License: MIT (see `licenses/Anime4K-MIT.txt`)

MiruPlay loads these GLSL shaders through libmpv's `--glsl-shaders` user-shader
support when the user enables an Anime4K preset. The presets are optional and are
skipped for HDR content; when the packaged shader files are missing playback falls
back to the built-in scaler.

## .NET and NuGet components

The self-contained package includes the Microsoft .NET runtime and the dependencies declared in `src/MiruPlay.Windows/MiruPlay.Windows.csproj`, including ASP.NET Core, Microsoft.Data.Sqlite, SQLitePCLRaw, Google.Protobuf, and gRPC for .NET.

Every release contains a `licenses` directory with:

- `NuGet-Packages.json`, generated from the restored assets file with exact package versions, license expressions, and repository URLs.
- Full Apache-2.0, MIT/.NET, and Protobuf BSD-3-Clause license texts.
- License and third-party-notice files copied from each exact restored .NET runtime and NuGet package.
- The mpv and FFmpeg copyright/license texts described above.

Those packaged files are the redistribution notices for the release; this overview does not replace them.
