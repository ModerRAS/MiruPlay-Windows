# libmpv runtime

Release builds bundle the matching shared libmpv library downloaded by
`tools/Get-LibMpvRuntime.ps1`:

- Asset: `mpv-dev-x86_64-20261021-git-e471496c56.7z`
- Archive SHA-256: `efc719eff3dc5954b4a5efc800632808bcec01cc9034940dd14e5eb7fe9ea633`
- `libmpv-2.dll` SHA-256: `04e4d27dc385dc18ddd93fbe69c75e635b864f3c6ec53a9122df54c2f7842f4a`
- Source: https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261021

The DLL is excluded from Git and is acquired during release packaging. It is
loaded in-process by MiruPlay. When it is unavailable or incompatible, local
files use the Windows system player with degraded controls and WebDAV playback
reports that the embedded playback backend is unavailable.
