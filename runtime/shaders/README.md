# Anime4K shader runtime

`runtime/shaders/anime4k/` holds the official Anime4K v4.0.1 mpv shader files used by
the MiruPlay quality presets. Like the libmpv/SDL3 runtimes these files are not
committed to Git; `tools/Get-Anime4KShaders.ps1` downloads the pinned release,
verifies the archive SHA-256 and every extracted file, and copies the nine fast-preset
files here. Release packaging validates the same hashes and bundles the files.

- Release: `Anime4K_v4.0.zip` from https://github.com/bloc97/Anime4K/releases/tag/v4.0.1
- Archive SHA-256: `139cd282086457c5adc79caf7b75b8b825091d71c9b54958c18745fea62d7ed7`
- License: MIT (see `licenses/Anime4K-MIT.txt`); each shader file also embeds the MIT text
- Version: 4.0.1

Files (9, fast/low-end GPU chains):

```text
Anime4K_Clamp_Highlights.glsl
Anime4K_Restore_CNN_M.glsl
Anime4K_Restore_CNN_S.glsl
Anime4K_Restore_CNN_Soft_M.glsl
Anime4K_Upscale_CNN_x2_M.glsl
Anime4K_Upscale_CNN_x2_S.glsl
Anime4K_Upscale_Denoise_CNN_x2_M.glsl
Anime4K_AutoDownscalePre_x2.glsl
Anime4K_AutoDownscalePre_x4.glsl
```

When the files are missing (for example a manually built package), MiruPlay keeps
playing and falls back to the built-in scaler; the settings page and diagnostics
report the degraded reason. The shader presets only apply to SDR content; HDR and
HDR-passthrough sessions deliberately skip them.
