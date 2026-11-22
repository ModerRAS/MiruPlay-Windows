#ifndef SourceDir
  #error SourceDir must point to the self-contained publish directory.
#endif
#ifndef AppVersion
  #define AppVersion "0.1.8"
#endif
#ifndef VersionNumeric
  #define VersionNumeric "0.1.8.0"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "MiruPlay-setup"
#endif
#ifndef MyEdition
  #define MyEdition "lite"
#endif
#ifndef MyEditionIsFull
  #define MyEditionIsFull "0"
#endif

#if MyEditionIsFull == "1"
  #define MyAppId "{{C4B2D913-7A61-4B7E-9F4D-2C8B9A5D6E71}"
  #define MyAppName "MiruPlay Full"
  #define MyDefaultDir "{localappdata}\Programs\MiruPlay Full"
#else
  #define MyAppId "{{9A0FC862-354F-4D39-958B-26DCAB4B2235}"
  #define MyAppName "MiruPlay"
  #define MyDefaultDir "{localappdata}\Programs\MiruPlay"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher=MiruPlay
AppPublisherURL=https://github.com/ModerRAS/MiruPlay-Windows
AppSupportURL=https://github.com/ModerRAS/MiruPlay-Windows/issues
DefaultDirName={#MyDefaultDir}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
CloseApplications=force
RestartApplications=no
SetupLogging=yes
UninstallDisplayIcon={app}\MiruPlay.exe
VersionInfoVersion={#VersionNumeric}
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=MiruPlay Windows installer
VersionInfoCompany=MiruPlay
VersionInfoCopyright=Copyright (C) MiruPlay contributors

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; edition.txt (written by Publish-Release.ps1) is copied by the recursive entry above; it identifies lite/full after install.

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\MiruPlay.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\MiruPlay"; Filename: "{app}\MiruPlay.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\MiruPlay.exe"; Description: "{cm:LaunchProgram,MiruPlay}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent