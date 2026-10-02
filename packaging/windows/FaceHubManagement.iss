#define MyAppName "FaceHub Management"
#define MyAppVersion "1.0.4"
#define MyAppPublisher "FaceHub"
#define MyAppExeName "FaceHubManagement.Desktop.exe"
#define AgentServiceName "FaceHubManagementAgent"

[Setup]
AppId={{A4D56F3B-7549-41B6-A283-24632B1BC4F7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\FaceHub Management
DefaultGroupName=FaceHub Management
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir=..\..\artifacts\installer
OutputBaseFilename=FaceHubManagement-{#MyAppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\..\src\FaceHubManagement.Desktop\Assets\favicon.ico
UninstallDisplayIcon={app}\desktop\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\..\artifacts\windows\desktop\*"; DestDir: "{app}\desktop"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\artifacts\windows\agent\*"; DestDir: "{app}\agent"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
; Source: "..\..\artifacts\windows\smoketest\*"; DestDir: "{app}\smoketest"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\config\agentsettings.json"; DestDir: "{commonappdata}\FaceHub Management"; DestName: "agentsettings.json"; Flags: onlyifdoesntexist
Source: "install-agent.ps1"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "uninstall-agent.ps1"; DestDir: "{app}\packaging"; Flags: ignoreversion

[Dirs]
Name: "{commonappdata}\FaceHub Management\logs"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Icons]
Name: "{group}\FaceHub Management"; Filename: "{app}\desktop\{#MyAppExeName}"; WorkingDir: "{app}\desktop"
Name: "{autodesktop}\FaceHub Management"; Filename: "{app}\desktop\{#MyAppExeName}"; WorkingDir: "{app}\desktop"; Tasks: desktopicon

[Run]
Filename: "{app}\desktop\{#MyAppExeName}"; Description: "Launch FaceHub Management"; Flags: nowait postinstall skipifsilent

[Code]
function RunPowerShellScript(const ScriptPath, Arguments: String; var ResultCode: Integer): Boolean;
var
  PowerShellPath: String;
  Params: String;
begin
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Params := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
            ScriptPath + '" ' + Arguments;

  Result := Exec(
    PowerShellPath,
    Params,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode
  );
end;

procedure InstallAgentService();
var
  ResultCode: Integer;
  ScriptPath: String;
  AgentPath: String;
  Arguments: String;
begin
  ScriptPath := ExpandConstant('{tmp}\install-agent.ps1');
  AgentPath := ExpandConstant('{app}\agent\FaceHubManagement.Agent.exe');
  Arguments := '-AgentPath "' + AgentPath + '"';

  if not RunPowerShellScript(ScriptPath, Arguments, ResultCode) then
  begin
    RaiseException('Unable to start PowerShell while installing the FaceHub Management Agent.');
  end;

  if ResultCode <> 0 then
  begin
    RaiseException(
      'FaceHub Management Agent installation failed. PowerShell exit code: ' +
      IntToStr(ResultCode)
    );
  end;
end;

procedure RemoveAgentService();
var
  ResultCode: Integer;
  ScriptPath: String;
begin
  ScriptPath := ExpandConstant('{app}\packaging\uninstall-agent.ps1');

  if not FileExists(ScriptPath) then
    Exit;

  if not RunPowerShellScript(ScriptPath, '', ResultCode) then
  begin
    RaiseException('Unable to start PowerShell while removing the FaceHub Management Agent.');
  end;

  if ResultCode <> 0 then
  begin
    RaiseException(
      'FaceHub Management Agent could not be removed. PowerShell exit code: ' +
      IntToStr(ResultCode) +
      '. The application files were left in place so the service is not orphaned.'
    );
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    InstallAgentService();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveAgentService();
  end;
end;
