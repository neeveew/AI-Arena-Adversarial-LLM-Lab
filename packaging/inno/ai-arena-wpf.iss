; Inno Setup installer for the native WPF AI Arena build.

#define MyAppName "AI Arena"
#define MyAppVersion "0.3.40-beta"
#define MyAppPublisher "Dominik"
#define MyAppExeName "AI Arena.exe"
#define MyReleaseDir "..\..\dist\AI Arena - 0.3.40-beta"

[Setup]
AppId={{E2F12C8E-9B8C-45C3-B9A1-A8F8E1725F61}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\..\dist\installer\AI Arena - {#MyAppVersion}
OutputBaseFilename=AI Arena Setup {#MyAppVersion}
SetupIconFile=..\..\windows-wpf\src\AIArena.Wpf\Assets\ai-arena-icon.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#MyReleaseDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
