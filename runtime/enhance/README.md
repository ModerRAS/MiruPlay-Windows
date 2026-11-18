# runtime/enhance (full edition only)

Full-edition enhancement assets are **not** in git (large portable VapourSynth /
MVTools / RIFE / models). The lite edition never includes this directory.

Expected layout (pinned by the rife/videofx deliverables; interface stable now):

```
runtime/enhance/
  manifest.json
  vapoursynth/...        # portable VapourSynth + MVTools
  rife/...               # RIFE ncnn runtime + models
  licenses/...           # licenses for the bundled enhancement components
```

`manifest.json`:

```json
{
  "version": "2026.11",
  "files": [
    { "path": "vapoursynth/VSPipe.exe", "sha256": "<64 hex>" },
    { "path": "rife/rife-ncnn-vulkan.exe", "sha256": "<64 hex>" },
    { "path": "licenses/VapourSynth-LGPL.txt", "sha256": "<64 hex>" }
  ]
}
```

Fetch/verify with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Get-EnhancementPack.ps1
```

`Publish-Release.ps1 -Edition full` fails if this directory or manifest is missing;
`-Edition lite` asserts the published output contains no `runtime/enhance`.
