# mpv runtime

Release builds bundle the pinned Windows x64 mpv build downloaded by `tools/Get-MpvRuntime.ps1`:

- Asset: `mpv-x86_64-20260610-git-304426c.7z`
- Archive SHA-256: `facac536baa73c7b925771af5e39a3c9cb16b8d75b59a6e9800de89799dffca7`
- Source: https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20260610

Binary files remain excluded from Git. `tools/Publish-Release.ps1` downloads and verifies the pinned archive when `runtime/mpv/mpv.exe` is absent, then includes only `mpv.exe` and `d3dcompiler_43.dll` in the self-contained package. See `THIRD-PARTY-NOTICES.md` for licensing and source links.
