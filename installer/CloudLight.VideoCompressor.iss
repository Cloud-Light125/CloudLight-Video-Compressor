#ifndef MyAppVersion
  #define MyAppVersion "1.2.0"
#endif

#ifndef MyPublishDir
  #define MyPublishDir "..\artifacts\publish\win-x64"
#endif

#define MyAppName "CloudLight Video Compressor"
#define MyAppPublisher "CloudLight"
#define MyAppExeName "CloudLight.VideoCompressor.exe"
#define MyAppMutex "CloudLightVideoCompressor-17BD4C51-B1E9-41C6-8818-11A70CE1ACC9"

[Setup]
AppId={{17BD4C51-B1E9-41C6-8818-11A70CE1ACC9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\CloudLight\CloudLight Video Compressor
DefaultGroupName={#MyAppName}
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\icon.ico
AppMutex={#MyAppMutex}
OutputDir=..\artifacts\installer
OutputBaseFilename=CloudLight-Video-Compressor-Setup-x64-{#MyAppVersion}
SetupIconFile=..\icon.ico
LicenseFile={#MyPublishDir}\ffmpeg\LICENSE.txt
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes
ChangesAssociations=no
UsePreviousAppDir=yes
UsePreviousGroup=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式:"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\icon.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\icon.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

; No [UninstallDelete] entries are used: Inno Setup removes only the files registered above and leaves
; user settings, videos, compression outputs, logs, and other user-owned data outside {app} untouched.
