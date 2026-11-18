# runtime/sdl3

Place the pinned SDL3 runtime here (ignored by git except this README):

- Version: SDL 3.4.20 (2026-11-03)
- Release asset: `SDL3-3.4.20-win32-x64.zip`
- Download: https://github.com/libsdl-org/SDL/releases/download/release-3.4.20/SDL3-3.4.20-win32-x64.zip
- Archive SHA-256: `368bed8b24e44ad9ea372990814f2343fcfb9e468abb7f8f4a3bc146c7304752`
- File: `SDL3.dll` (2,848,768 bytes)
- DLL SHA-256: `66844f10251cc1c10619ed5d8e4e3224f564900ee6d69ba87d63de782addae40`
- License: zlib (see `licenses/SDL3-zlib.txt`)

Regenerate/fetch with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Get-Sdl3Runtime.ps1
```

The app loads `runtime/sdl3/SDL3.dll` for the SDL3 gamepad backend when present;
without it the XInput backend is used and TV mode still works for Xbox pads.
