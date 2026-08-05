# libmpv runtime

Release builds bundle the matching shared libmpv library downloaded by
`tools/Get-LibMpvRuntime.ps1`:

- Asset: `mpv-dev-x86_64-20260610-git-304426c.7z`
- Archive SHA-256: `8cbb25ea784f01afbb3f904217cab1317430a8bcfd5680fd827a866367f71cc9`
- `libmpv-2.dll` SHA-256: `5c876d79e070529128331591b48f87846fb30557f19c11280df9c6ee9b6dbafa`
- Source: https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20260610

The DLL is excluded from Git and is acquired during release packaging. It is
loaded in-process by MiruPlay. When it is unavailable or incompatible, local
files use the Windows system player with degraded controls and WebDAV playback
reports that the embedded playback backend is unavailable.
