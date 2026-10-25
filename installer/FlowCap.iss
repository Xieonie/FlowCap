#define AppName "FlowCap"
#define AppVersion "1.0.0"
#define AppExe "FlowCap.exe"

[Setup]
AppId={{2F7A9C14-5E3B-4B8D-A6C1-9D04E8B37F21}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Alex
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=FlowCap-Setup-{#AppVersion}
SetupIconFile=..\SpeedLimit\flowcap.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: postinstall nowait shellexec skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "StopApp"
Filename: "schtasks.exe"; Parameters: "/Delete /TN FlowCap /F"; Flags: runhidden; RunOnceId: "RemoveAutostart"
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""FlowCap Proxy-Relay"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"

[Code]
// Laufende Instanz (läuft meist unsichtbar im Tray) beenden, sonst sind die Dateien beim Update gesperrt.
// SpeedLimit.exe ist der Name bis Version 1.0 – sonst blockieren sich alte und neue Version gegenseitig den Treiber.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/IM SpeedLimit.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('schtasks.exe', '/Delete /TN SpeedLimit /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
