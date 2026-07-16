# Third-Party Notices

MiruPlay Windows is distributed under GPL-3.0-or-later. The release package also contains third-party components governed by their own licenses.

## mpv Windows build

- Project: mpv
- Upstream source: https://github.com/mpv-player/mpv/tree/304426c39
- Windows build source: https://github.com/shinchiro/mpv-winbuild-cmake
- Pinned build: `mpv-x86_64-20260610-git-304426c`
- Pinned archive SHA-256: `facac536baa73c7b925771af5e39a3c9cb16b8d75b59a6e9800de89799dffca7`
- License information: https://github.com/mpv-player/mpv/blob/master/Copyright

The pinned build includes FFmpeg and other libraries. Their exact configuration and corresponding source revisions are published by the Windows build project above. MiruPlay invokes `mpv.exe` as a separate process and does not modify it. The package's `licenses` directory includes the matching mpv copyright statement and GPL/LGPL texts plus FFmpeg's license summary and GPL/LGPL texts.

## Microsoft DirectX runtime component

`d3dcompiler_43.dll` is redistributed with the pinned mpv build under the Microsoft DirectX End User Runtime redistribution terms.

## .NET and NuGet components

The self-contained package includes the Microsoft .NET runtime and the dependencies declared in `src/MiruPlay.Windows/MiruPlay.Windows.csproj`, including ASP.NET Core, Microsoft.Data.Sqlite, SQLitePCLRaw, Google.Protobuf, and gRPC for .NET.

Every release contains a `licenses` directory with:

- `NuGet-Packages.json`, generated from the restored assets file with exact package versions, license expressions, and repository URLs.
- Full Apache-2.0, MIT/.NET, and Protobuf BSD-3-Clause license texts.
- License and third-party-notice files copied from each exact restored .NET runtime and NuGet package.
- The mpv and FFmpeg copyright/license texts described above.

Those packaged files are the redistribution notices for the release; this overview does not replace them.
