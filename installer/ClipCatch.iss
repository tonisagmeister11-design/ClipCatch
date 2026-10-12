; ClipCatch installer (Inno Setup 6).
; Installs for the current user only, into %LOCALAPPDATA%\Programs\ClipCatch:
; no administrator rights, no UAC prompt, no PIN.
;
; Build: iscc /DAppVersion=1.0.0 installer\ClipCatch.iss   (expects the app in dist\app)

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{9E5C3A57-6B1D-4C8E-9F2A-1C7D2B8E4F10}
AppName=ClipCatch
AppVersion={#AppVersion}
AppVerName=ClipCatch {#AppVersion}
AppPublisher=ClipCatch
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\ClipCatch
DefaultGroupName=ClipCatch
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=ClipCatch-Setup-{#AppVersion}
SetupIconFile=..\src\ClipCatch\Assets\clipcatch.ico
UninstallDisplayIcon={app}\ClipCatch.exe
UninstallDisplayName=ClipCatch
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ClipCatch"; Filename: "{app}\ClipCatch.exe"
Name: "{autodesktop}\ClipCatch"; Filename: "{app}\ClipCatch.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ClipCatch.exe"; Description: "{cm:LaunchProgram,ClipCatch}"; Flags: nowait postinstall skipifsilent

[Code]
// .NET Framework 4.8 is part of Windows 10 (1903+) and 11. Older systems get a clear hint.
function IsDotNet48Installed: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release)
    and (Release >= 528040);
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not IsDotNet48Installed then
  begin
    if MsgBox('ClipCatch braucht .NET Framework 4.8, das auf diesem Windows fehlt.' #13#10 #13#10 +
              'Bitte zuerst Windows aktualisieren oder .NET Framework 4.8 von microsoft.com installieren.' #13#10 #13#10 +
              'Trotzdem fortfahren?', mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;
