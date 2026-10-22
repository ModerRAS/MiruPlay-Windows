# Third-Party Notices

MiruPlay Windows is distributed under GPL-3.0-or-later. The release package also contains third-party components governed by their own licenses.

## mpv/libmpv Windows build

- Project: mpv
- Upstream source: https://github.com/mpv-player/mpv/tree/e471496c56f0f49c133bd88b50aed62b845f1909
- Windows build source: https://github.com/shinchiro/mpv-winbuild-cmake
- License information: https://github.com/mpv-player/mpv/blob/master/Copyright

The matching development archive also supplies the in-process library:

- Asset: `mpv-dev-x86_64-20261021-git-e471496c56.7z`
- Archive SHA-256: `efc719eff3dc5954b4a5efc800632808bcec01cc9034940dd14e5eb7fe9ea633`
- Packaged file: `runtime/libmpv/libmpv-2.dll`
- DLL SHA-256: `04e4d27dc385dc18ddd93fbe69c75e635b864f3c6ec53a9122df54c2f7842f4a`

The pinned build includes FFmpeg and other libraries. Their exact configuration and corresponding source revisions are published by the Windows build project above. MiruPlay loads the matching `libmpv-2.dll` in-process. The package's `licenses` directory includes the matching mpv copyright statement and GPL/LGPL texts plus FFmpeg's license summary and GPL/LGPL texts.

## .NET and NuGet components

The self-contained package includes the Microsoft .NET runtime and the dependencies declared in `src/MiruPlay.Windows/MiruPlay.Windows.csproj`, including ASP.NET Core, Microsoft.Data.Sqlite, SQLitePCLRaw, Google.Protobuf, and gRPC for .NET.

Every release contains a `licenses` directory with:

- `NuGet-Packages.json`, generated from the restored assets file with exact package versions, license expressions, and repository URLs.
- Full Apache-2.0, MIT/.NET, and Protobuf BSD-3-Clause license texts.
- License and third-party-notice files copied from each exact restored .NET runtime and NuGet package.
- The mpv and FFmpeg copyright/license texts described above.

Those packaged files are the redistribution notices for the release; this overview does not replace them.
