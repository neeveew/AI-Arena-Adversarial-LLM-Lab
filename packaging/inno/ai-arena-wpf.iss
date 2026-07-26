; Inno Setup installer for the native WPF AI Arena build.

#define MyAppName "AI Arena"
#define MyAppDisplayName "AI Arena: Adversarial LLM Lab"
#define MyAppVersion "0.4.114-beta"
#define MyAppPublisher "Dominik Fiala"
#define MyAppExeName "AI Arena.exe"
#define MyAppIconName "ai-arena-icon.ico"
#define MyReleaseDir "..\..\dist\AI Arena - 0.4.114-beta"
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
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=yes
UsePreviousAppDir=no
OutputDir=..\..\dist\installer\AI Arena - {#MyAppVersion}
OutputBaseFilename=AI Arena Setup {#MyAppVersion}
SetupIconFile=..\..\src\AIArena.Wpf\Assets\ai-arena-icon.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Full installation"
Name: "compact"; Description: "AI Arena only"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "app"; Description: "AI Arena application"; Types: full compact custom; Flags: fixed
Name: "searxng"; Description: "Local web search engine (SearXNG, AGPL-3.0)"; Types: full custom

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#MyReleaseDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "searxng\*"; Components: app
Source: "{#MyReleaseDir}\searxng\*"; DestDir: "{app}\searxng"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: searxng
Source: "{#MyReleaseDir}\searxng\LICENSE"; DestDir: "{tmp}"; DestName: "SEARXNG-LICENSE.txt"; Flags: dontcopy
Source: "..\..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "..\..\NOTICE.md"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "..\..\docs\USER_GUIDE.md"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "..\..\CONTROLPLANE.md"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "..\..\src\AIArena.Wpf\Assets\ai-arena-icon.ico"; DestDir: "{app}"; DestName: "{#MyAppIconName}"; Flags: ignoreversion; Components: app

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppIconName}"
Name: "{group}\AI Arena User Guide"; Filename: "{app}\USER_GUIDE.md"; IconFilename: "{app}\{#MyAppIconName}"
Name: "{group}\AI Arena PowerShell Control"; Filename: "{app}\CONTROLPLANE.md"; IconFilename: "{app}\{#MyAppIconName}"
Name: "{group}\Release Notes"; Filename: "{app}\changes.txt"; IconFilename: "{app}\{#MyAppIconName}"
Name: "{group}\GitHub Releases"; Filename: "{#MyReleaseUrl}"; IconFilename: "{app}\{#MyAppIconName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppIconName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\USER_GUIDE.md"; Description: "Open user guide"; Flags: shellexec postinstall skipifsilent runasoriginaluser

[UninstallDelete]
; The payload is app-owned. Remove interpreter caches or other runtime-only
; files that were not present in the install manifest.
Type: filesandordirs; Name: "{app}\searxng"

[Code]
var
  RemoveUserData: Boolean;
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

function EscapePowerShellSingleQuoted(Value: string): string;
begin
  StringChangeEx(Value, '''', '''''', True);
  Result := Value;
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
var
  DataDir: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopBundledSearxng;
    RemoveUserData := False;
    if not UninstallSilent then
    begin
      RemoveUserData :=
        MsgBox(
          'Also delete AI Arena saved sessions, settings, templates, checkpoints, exports, logs, and cache from your user profile?'#13#10#13#10 +
          'Choose No to uninstall the app but keep your data.',
          mbConfirmation,
          MB_YESNO) = IDYES;
    end;
  end;

  if (CurUninstallStep = usPostUninstall) and RemoveUserData then
  begin
    DataDir := ExpandConstant('{localappdata}\AI Arena');
    if DirExists(DataDir) then
    begin
      DelTree(DataDir, True, True, True);
    end;
  end;
end;
