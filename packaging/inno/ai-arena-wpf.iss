; Inno Setup installer for the native WPF AI Arena build.

; Compatibility identity: keep this value stable for the existing executable
; and upgrade lineage. Public Lite labels and the machine install path are separate.
#define MyAppName "AI Arena"
#define MyAppShortDisplayName "AI Arena - Lite"
#define MyAppDisplayName "AI Arena - Lite: Adversarial LLM Lab"
#define MyAppVersion "0.4.137-beta"
#define MyAppPublisher "Dominik Fiala"
#define MyAppExeName "AI Arena.exe"
#define MyAppIconName "ai-arena-lite-icon.ico"
#define MyPerUserMigrationSha256 "0F75A6496F52DAD96E08B86C20BF4287AB76F62B325110CE6E460EB9CFC2087E"
#define MyReleaseDir "..\..\dist\AI Arena - 0.4.137-beta"
#define MyReleaseUrl "https://github.com/neeveew/AI-Arena-Adversarial-LLM-Lab/releases"

[Setup]
AppId={{E2F12C8E-9B8C-45C3-B9A1-A8F8E1725F61}
AppName={#MyAppDisplayName}
AppVerName={#MyAppDisplayName} - {#MyAppVersion}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyReleaseUrl}
AppSupportURL={#MyReleaseUrl}
AppUpdatesURL={#MyReleaseUrl}
DefaultDirName={autopf}\AI Arena Lite
DefaultGroupName={#MyAppShortDisplayName}
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=no
UsePreviousGroup=no
OutputDir=..\..\dist\installer\AI Arena - {#MyAppVersion}
OutputBaseFilename=AI Arena Setup {#MyAppVersion}
SetupIconFile=..\..\src\AIArena.Wpf\Assets\ai-arena-icon.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppIconName}
LicenseFile=..\..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Full installation"
Name: "compact"; Description: "{#MyAppShortDisplayName} only"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "app"; Description: "{#MyAppShortDisplayName} application"; Types: full compact custom; Flags: fixed
Name: "searxng"; Description: "Local web search engine (SearXNG, AGPL-3.0)"; Types: full custom

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "migrate-ai-arena-per-user.ps1"; Flags: dontcopy noencryption
Source: "{#MyReleaseDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "searxng\*"; Components: app
Source: "{#MyReleaseDir}\searxng\*"; DestDir: "{app}\searxng"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: searxng
Source: "{#MyReleaseDir}\searxng\LICENSE"; DestDir: "{tmp}"; DestName: "SEARXNG-LICENSE.txt"; Flags: dontcopy
Source: "..\..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "..\..\NOTICE.md"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "..\..\docs\USER_GUIDE.md"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "..\..\CONTROLPLANE.md"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "..\..\src\AIArena.Wpf\Assets\ai-arena-icon.ico"; DestDir: "{app}"; DestName: "{#MyAppIconName}"; Flags: ignoreversion; Components: app

[Icons]
Name: "{group}\{#MyAppShortDisplayName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppIconName}"
Name: "{group}\{#MyAppShortDisplayName} User Guide"; Filename: "{app}\USER_GUIDE.md"; IconFilename: "{app}\{#MyAppIconName}"
Name: "{group}\{#MyAppShortDisplayName} PowerShell Control"; Filename: "{app}\CONTROLPLANE.md"; IconFilename: "{app}\{#MyAppIconName}"
Name: "{group}\Release Notes"; Filename: "{app}\changes.txt"; IconFilename: "{app}\{#MyAppIconName}"
Name: "{group}\GitHub Releases"; Filename: "{#MyReleaseUrl}"; IconFilename: "{app}\{#MyAppIconName}"
Name: "{autodesktop}\{#MyAppShortDisplayName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppIconName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppShortDisplayName}"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\USER_GUIDE.md"; Description: "Open user guide"; Flags: shellexec postinstall skipifsilent runasoriginaluser

[UninstallDelete]
; The payload is app-owned. Remove interpreter caches or other runtime-only
; files that were not present in the install manifest.
Type: filesandordirs; Name: "{app}\searxng"

[Code]
var
  SearxngLicensePage: TWizardPage;
  SearxngLicenseMemo: TNewMemo;
  SearxngLicenseAccepted: TNewCheckBox;

procedure InitializeWizard;
var
  LicenseText: AnsiString;
begin
  SearxngLicensePage :=
    CreateCustomPage(
      wpSelectComponents,
      'SearXNG AGPL-3.0 License',
      'Review and accept the SearXNG license to install the local web search engine.');

  SearxngLicenseMemo := TNewMemo.Create(SearxngLicensePage);
  SearxngLicenseMemo.Parent := SearxngLicensePage.Surface;
  SearxngLicenseMemo.Left := 0;
  SearxngLicenseMemo.Top := 0;
  SearxngLicenseMemo.Width := SearxngLicensePage.SurfaceWidth;
  SearxngLicenseMemo.Height := SearxngLicensePage.SurfaceHeight - ScaleY(32);
  SearxngLicenseMemo.ReadOnly := True;
  SearxngLicenseMemo.ScrollBars := ssVertical;
  SearxngLicenseMemo.WordWrap := True;

  ExtractTemporaryFile('SEARXNG-LICENSE.txt');
  if LoadStringFromFile(ExpandConstant('{tmp}\SEARXNG-LICENSE.txt'), LicenseText) then
  begin
    SearxngLicenseMemo.Text := LicenseText;
  end
  else
  begin
    SearxngLicenseMemo.Text :=
      'SearXNG is licensed under AGPL-3.0-or-later. The bundled LICENSE file will be installed beside the local search engine payload.';
  end;

  SearxngLicenseAccepted := TNewCheckBox.Create(SearxngLicensePage);
  SearxngLicenseAccepted.Parent := SearxngLicensePage.Surface;
  SearxngLicenseAccepted.Left := 0;
  SearxngLicenseAccepted.Top := SearxngLicensePage.SurfaceHeight - ScaleY(24);
  SearxngLicenseAccepted.Width := SearxngLicensePage.SurfaceWidth;
  SearxngLicenseAccepted.Caption := 'I accept the SearXNG AGPL-3.0 license.';
  { Interactive installs use the checkbox. Automated full installs must opt in
    explicitly with /SEARXNGLICENSE=accept; silent mode never implies consent. }
  SearxngLicenseAccepted.Checked :=
    WizardSilent and
    (Lowercase(Trim(ExpandConstant('{param:SEARXNGLICENSE|}'))) = 'accept');
end;

function EscapePowerShellSingleQuoted(Value: string): string;
begin
  StringChangeEx(Value, '''', '''''', True);
  Result := Value;
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
var
  MigrationScript: string;
  MigrationCommand: string;
  ResultCode: Integer;
begin
  Result := '';
  NeedsRestart := False;
  MigrationScript := ExpandConstant('{tmp}\migrate-ai-arena-per-user.ps1');
  ExtractTemporaryFile('migrate-ai-arena-per-user.ps1');
  if CompareText(GetSHA256OfFile(MigrationScript), '{#MyPerUserMigrationSha256}') <> 0 then
  begin
    Result :=
      'Setup could not verify its per-user migration helper. No legacy files were changed. ' +
      'Download a fresh installer and try again.';
    Exit;
  end;

  { The inline command reads the helper once, hashes those exact bytes, and
    executes the same decoded buffer. A replacement between extraction and
    launch therefore cannot be executed under the original user token. }
  MigrationCommand :=
    '$ErrorActionPreference = ''Stop''; ' +
    '$bytes = [IO.File]::ReadAllBytes(''' + EscapePowerShellSingleQuoted(MigrationScript) + '''); ' +
    '$sha = [Security.Cryptography.SHA256]::Create(); ' +
    'try { $actual = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace(''-'', '''') } finally { $sha.Dispose() }; ' +
    'if (-not $actual.Equals(''{#MyPerUserMigrationSha256}'', [StringComparison]::OrdinalIgnoreCase)) { exit 92 }; ' +
    '$text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes); ' +
    '& ([ScriptBlock]::Create($text)); exit $LASTEXITCODE';

  if not ExecAsOriginalUser(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -Command "' + MigrationCommand + '"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Result :=
      'Setup could not inspect the current user''s earlier AI Arena installation. ' +
      'No files were changed. Close Setup, uninstall the earlier per-user version while keeping its saved data, then run Setup again.';
    Exit;
  end;

  case ResultCode of
    0:
      Result := '';
    20:
      Result :=
        'An earlier AI Arena installation uses a custom or unverified location. ' +
        'For safety, Setup will not remove it automatically. Uninstall that version while keeping its saved data, then run Setup again.';
    21:
      Result :=
        'The earlier AI Arena uninstall command could not be verified. ' +
        'No legacy files were removed. Repair or uninstall that version manually, then run Setup again.';
    22:
      Result :=
        'The earlier AI Arena uninstaller is missing. ' +
        'Repair or remove that installation manually, then run Setup again.';
    23:
      Result :=
        'The earlier per-user AI Arena installation could not be removed cleanly. ' +
        'Its saved data was preserved. Finish uninstalling it, then run Setup again.';
    24:
      Result :=
        'Setup was started from an already elevated process, so it cannot safely access the original user''s earlier installation. ' +
        'Close Setup and launch the installer normally (do not use Run as administrator), or uninstall the earlier per-user version manually while keeping its saved data.';
    92:
      Result :=
        'Setup detected that its per-user migration helper changed after extraction. ' +
        'No legacy files were changed. Download a fresh installer and try again.';
  else
    Result :=
      'Setup could not migrate the earlier per-user AI Arena installation (code ' +
      IntToStr(ResultCode) + '). Its saved data was preserved. Resolve that installation, then run Setup again.';
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if (SearxngLicensePage <> nil) and (PageID = SearxngLicensePage.ID) then
  begin
    Result := not WizardIsComponentSelected('searxng');
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (SearxngLicensePage <> nil) and (CurPageID = SearxngLicensePage.ID) then
  begin
    if WizardIsComponentSelected('searxng') and not SearxngLicenseAccepted.Checked then
    begin
      MsgBox('You must accept the SearXNG AGPL-3.0 license to install the local web search engine component.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure StopBundledSearxng;
var
  ResultCode: Integer;
  Script: string;
begin
  Script :=
    '$targets = @(''' + EscapePowerShellSingleQuoted(ExpandConstant('{app}\searxng\python\pythonw.exe')) + ''',''' + EscapePowerShellSingleQuoted(ExpandConstant('{app}\searxng\python\python.exe')) + '''); ' +
    'Get-CimInstance Win32_Process | Where-Object { $targets -contains $_.ExecutablePath } | Invoke-CimMethod -MethodName Terminate | Out-Null';
  Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoLogo -NoProfile -NonInteractive -Command "' + Script + '"',
    '',
    SW_SHOWNORMAL,
    ewWaitUntilTerminated,
    ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopBundledSearxng;
  end;
end;
