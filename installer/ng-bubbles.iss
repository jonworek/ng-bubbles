#define AppVersion "0.4.5"
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
Filename: "{app}\NG Bubbles.exe"; Description: "Open NG Bubbles"; Flags: nowait postinstall skipifsilent
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "NG Bubbles"; Flags: uninsdeletevalue
