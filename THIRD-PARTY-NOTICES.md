# Third-Party Notices

MiruPlay Windows is distributed under GPL-3.0-or-later. The release package also contains third-party components governed by their own licenses.

## mpv/libmpv Windows build

- Project: mpv
- Upstream source: https://github.com/mpv-player/mpv/tree/304426c39
- Windows build source: https://github.com/shinchiro/mpv-winbuild-cmake
- License information: https://github.com/mpv-player/mpv/blob/master/Copyright

The matching development archive also supplies the in-process library:

- Asset: `mpv-dev-x86_64-20260610-git-304426c.7z`
- Archive SHA-256: `8cbb25ea784f01afbb3f904217cab1317430a8bcfd5680fd827a866367f71cc9`
- Packaged file: `runtime/libmpv/libmpv-2.dll`
- DLL SHA-256: `5c876d79e070529128331591b48f87846fb30557f19c11280df9c6ee9b6dbafa`

The pinned build includes FFmpeg and other libraries. Their exact configuration and corresponding source revisions are published by the Windows build project above. MiruPlay loads the matching `libmpv-2.dll` in-process. The package's `licenses` directory includes the matching mpv copyright statement and GPL/LGPL texts plus FFmpeg's license summary and GPL/LGPL texts.

## .NET and NuGet components

The self-contained package includes the Microsoft .NET runtime and the dependencies declared in `src/MiruPlay.Windows/MiruPlay.Windows.csproj`, including ASP.NET Core, Microsoft.Data.Sqlite, SQLitePCLRaw, Google.Protobuf, and gRPC for .NET.

Every release contains a `licenses` directory with:

- `NuGet-Packages.json`, generated from the restored assets file with exact package versions, license expressions, and repository URLs.
- Full Apache-2.0, MIT/.NET, and Protobuf BSD-3-Clause license texts.
- License and third-party-notice files copied from each exact restored .NET runtime and NuGet package.
- The mpv and FFmpeg copyright/license texts described above.

Those packaged files are the redistribution notices for the release; this overview does not replace them.
