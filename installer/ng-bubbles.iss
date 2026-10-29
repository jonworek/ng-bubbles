#define AppVersion "0.4.23"
[Setup]
AppId={{93A36047-E2D8-4811-A103-2F769424ACF3}
AppName=NG Bubbles
AppVersion={#AppVersion}
AppPublisher=Jon Worek Technology LLC
AppPublisherURL=https://github.com/jonworek/ng-bubbles
AppSupportURL=https://github.com/jonworek/ng-bubbles/issues
DefaultDirName={localappdata}\Programs\NG Bubbles
DefaultGroupName=NG Bubbles
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=NG-Bubbles-{#AppVersion}-x64-Setup
SetupIconFile=..\assets\icon.ico
UninstallDisplayName=NG Bubbles
UninstallDisplayIcon={app}\NG Bubbles.exe
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
LicenseFile=..\LICENSE
WizardStyle=modern
[Files]
Source: "..\dist\native\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\NG Bubbles"; Filename: "{app}\NG Bubbles.exe"; AppUserModelID: "com.jonworek.ngbubbles"
Name: "{userdesktop}\NG Bubbles"; Filename: "{app}\NG Bubbles.exe"; AppUserModelID: "com.jonworek.ngbubbles"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
[Run]
Filename: "{app}\NG Bubbles.exe"; Description: "Open NG Bubbles"; Flags: nowait postinstall skipifsilent; Check: not IsInAppUpdate
Filename: "{app}\NG Bubbles.exe"; Flags: nowait; Check: IsInAppUpdate
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "NG Bubbles"; Flags: uninsdeletevalue

Root: HKCU; Subkey: "Software\Jon Worek Technology LLC\NG Bubbles\Installer"; Flags: uninsdeletekey
[Code]
const
  InstallerPreferences = 'Software\Jon Worek Technology LLC\NG Bubbles\Installer';

function OpenProcess(Access: LongWord; Inherit: Boolean; ProcessId: LongWord): LongWord;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: LongWord; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: LongWord): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function IsInAppUpdate: Boolean;
begin
  Result := ExpandConstant('{param:NGUPDATE|0}') = '1';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ProcessId: Integer;
  Handle, WaitResult: LongWord;
begin
  Result := '';
  if not IsInAppUpdate then Exit;
  ProcessId := StrToIntDef(ExpandConstant('{param:NGWAITPID|0}'), 0);
  if ProcessId <= 0 then begin
    Result := 'The update is missing the running app process. Reopen NG Bubbles and try again.';
    Exit;
  end;
  Handle := OpenProcess($00100000, False, ProcessId);
  if Handle = 0 then begin
    if DLLGetLastError <> 87 then
      Result := 'Cannot wait for NG Bubbles to close. Close the app and run the installer again.';
    Exit;
  end;
  try
    Log('Waiting for NG Bubbles process to exit before updating.');
    WaitResult := WaitForSingleObject(Handle, 60000);
    if WaitResult <> 0 then
      Result := 'NG Bubbles did not close in time. Close the app and run the installer again.';
  finally
    CloseHandle(Handle);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  LaunchOnFinish: Cardinal;
begin
  if (CurPageID = wpFinished) and not WizardSilent and not IsInAppUpdate then
    if (WizardForm.RunList.Items.Count > 0) and
       RegQueryDWordValue(HKCU, InstallerPreferences, 'LaunchOnFinish', LaunchOnFinish) then
      WizardForm.RunList.Checked[0] := LaunchOnFinish <> 0;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssDone) and not WizardSilent and not IsInAppUpdate then
    if WizardForm.RunList.Items.Count > 0 then
      RegWriteDWordValue(HKCU, InstallerPreferences, 'LaunchOnFinish', Ord(WizardForm.RunList.Checked[0]));
end;
