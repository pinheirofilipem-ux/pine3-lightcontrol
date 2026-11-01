; Pine3 Light Control — instalador Inno Setup 6
; Compilado pelo build.bat a partir de dist\app (dotnet publish).
; Instala para todo o sistema (como o Pine3 Monitor): a app corre como administrador
; por causa do SMBus das memórias e arranca por tarefa agendada, sem pedido de UAC.

#define AppName      "Pine3 Light Control"
#define AppVersion   "0.4.0"
#define AppExe       "Pine3LightControl.exe"
#define AppPublisher "magnificent3"
#define AppIdGuid    "{FAFBA8E6-77D0-43D8-B4A5-96CE9FA5F5B1}"

[Setup]
AppId={{#AppIdGuid}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=Pine3LightControl-Setup-{#AppVersion}
SetupIconFile=src\Pine3LightControl\Assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "pt"; MessagesFile: "compiler:Languages\Portuguese.isl"

[Tasks]
Name: "startup";     Description: "Start with Windows (recommended)"; GroupDescription: "Startup:"
Name: "pawnio";      Description: "Install the PawnIO driver (needed for memory RGB)"; GroupDescription: "Hardware access:"; Check: not PawnIOInstalled
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Driver PawnIO via winget (o mesmo que o Pine3 Monitor usa)
Filename: "{cmd}"; Parameters: "/c winget install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements"; Tasks: pawnio; Flags: runhidden waituntilterminated; StatusMsg: "Installing PawnIO driver (winget)..."
; Arranque com o Windows: tarefa agendada com privilégios máximos (criada pela própria app)
Filename: "{app}\{#AppExe}"; Parameters: "--register-startup"; Tasks: startup; Flags: runhidden waituntilterminated; StatusMsg: "Registering startup task..."
; runascurrentuser: a app pede requireAdministrator
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#AppName}"" /F"; Flags: runhidden; RunOnceId: "DelTask"

[Code]
function PawnIOInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

// Versões ≤ 0.3 instalavam só para o utilizador (%LOCALAPPDATA%\Programs) e
// arrancavam pela chave Run: remove essa instalação antes de instalar esta.
procedure RemovePerUserInstall();
var
  Key, Uninst: String;
  Code: Integer;
begin
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdGuid}_is1';
  if RegQueryStringValue(HKCU, Key, 'UninstallString', Uninst) then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Exec(RemoveQuotes(Uninst), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Pine3LightControl');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then RemovePerUserInstall();
end;
