#ifndef SourceDir
  #error SourceDir must point to the self-contained publish directory.
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef VersionNumeric
  #define VersionNumeric "0.1.0.0"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "MiruPlay-setup"
#endif

[Setup]
AppId={{9A0FC862-354F-4D39-958B-26DCAB4B2235}
AppName=MiruPlay
AppVersion={#AppVersion}
AppVerName=MiruPlay {#AppVersion}
AppPublisher=MiruPlay
AppPublisherURL=https://github.com/ModerRAS/MiruPlay-Windows
AppSupportURL=https://github.com/ModerRAS/MiruPlay-Windows/issues
DefaultDirName={localappdata}\Programs\MiruPlay
DefaultGroupName=MiruPlay
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

[Icons]
Name: "{group}\MiruPlay"; Filename: "{app}\MiruPlay.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\MiruPlay"; Filename: "{app}\MiruPlay.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\MiruPlay.exe"; Description: "{cm:LaunchProgram,MiruPlay}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
