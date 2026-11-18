# libmpv runtime

Release builds bundle the matching shared libmpv library downloaded by
`tools/Get-LibMpvRuntime.ps1`:

- Asset: `mpv-dev-x86_64-20261111-git-bcde6b93cc.7z`
- Archive SHA-256: `273c079b11ea5575822f972756f4c9a71149a5c10130353abeb8586738938452`
- `libmpv-2.dll` SHA-256: `88504ee5560942a955405e99dfb5971368e4cf851321f36565bf2a667271db1b`
- Source: https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261111

The DLL is excluded from Git and is acquired during release packaging. It is
loaded in-process by MiruPlay. When it is unavailable or incompatible, local
files use the Windows system player with degraded controls and WebDAV playback
reports that the embedded playback backend is unavailable.
