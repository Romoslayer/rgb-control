; Inno Setup 6 script. Build: scripts\publish.ps1, then ISCC /DAppVersion=x.y.z installer\RgbControl.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "RGB Control"
#define ServiceName "RgbControl"
#define RepoUrl "https://github.com/Romoslayer/rgb-control"

[Setup]
AppId={{8F3C2A51-6E4B-4C7D-9A1E-2B5D7C9E4F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Romoslayer
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
DefaultDirName={autopf}\RgbControl
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\artifacts
OutputBaseFilename=RgbControl-Setup-{#AppVersion}
SetupIconFile=..\src\RgbControl.App\Assets\RgbControl.ico
UninstallDisplayIcon={app}\RgbControl.exe
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=no

[Tasks]
Name: "pawnio"; Description: "Install the PawnIO driver with winget (needed for RAM lighting)"; Check: not IsPawnIOInstalled
Name: "startup"; Description: "Start RGB Control in the notification area when I sign in"; Flags: unchecked

[InstallDelete]
; Folder layout used by the earlier install-from-source script.
Type: filesandordirs; Name: "{app}\service"
Type: filesandordirs; Name: "{app}\app"
Type: filesandordirs; Name: "{app}\cli"

[Dirs]
; The app runs without admin rights and saves lighting here; the service reads it.
Name: "{commonappdata}\RgbControl"; Permissions: users-modify; Flags: uninsneveruninstall

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\RgbControl.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\RgbControl.exe"" --tray"; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#AppName}"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\rgbctl.exe"; Parameters: "init-config"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""{app}\RgbControl.Service.exe"" start= auto DisplayName= ""{#AppName}"""; Flags: runhidden; Check: not ServiceExists
Filename: "{sys}\sc.exe"; Parameters: "config {#ServiceName} binPath= ""{app}\RgbControl.Service.exe"" start= auto"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""Applies motherboard and RAM lighting at boot and turns it off at shutdown and sleep."""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000"; Flags: runhidden
Filename: "{cmd}"; Parameters: "/c winget install --id namazso.PawnIO --exact --silent --accept-package-agreements --accept-source-agreements"; StatusMsg: "Installing PawnIO..."; Tasks: pawnio; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; StatusMsg: "Starting the RGB Control service..."; Flags: runhidden
Filename: "{app}\RgbControl.exe"; Description: "Open RGB Control"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM RgbControl.exe"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "powershell.exe"; Parameters: "-NoProfile -Command ""Stop-Service -Name {#ServiceName} -Force -ErrorAction SilentlyContinue"""; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"

[Code]
function IsPawnIOInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

function ServiceExists: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\{#ServiceName}');
end;

// Stop the running app and service so their files can be replaced on upgrade.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM RgbControl.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ServiceExists then
    Exec('powershell.exe', '-NoProfile -Command "Stop-Service -Name {#ServiceName} -Force -ErrorAction SilentlyContinue"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
