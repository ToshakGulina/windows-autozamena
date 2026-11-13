#ifndef MyAppVersion
  #define MyAppVersion "1.1.1"
#endif

#ifndef PublishDir
  #define PublishDir "..\publish\standalone"
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "AutoZamena-Setup-win-x64"
#endif

#ifndef EditionSuffix
  #define EditionSuffix ""
#endif

[Setup]
AppId={{8E9D412C-6F52-4A91-9D3E-7C508FFF0E01}
AppName=AutoZamena{#EditionSuffix}
AppVersion={#MyAppVersion}
AppVerName=AutoZamena {#MyAppVersion}{#EditionSuffix}
AppPublisher=ToshakGulina
AppPublisherURL=https://github.com/ToshakGulina/windows-autozamena
AppSupportURL=https://github.com/ToshakGulina/windows-autozamena/issues
AppUpdatesURL=https://github.com/ToshakGulina/windows-autozamena/releases
DefaultDirName={autopf}\AutoZamena
DefaultGroupName=AutoZamena
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=..\artifacts
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile=..\src\AutoZamena.App\Assets\app.ico
UninstallDisplayIcon={app}\AutoZamena.exe
UninstallDisplayName=AutoZamena {#MyAppVersion}
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.AutoStartTask=Запускать AutoZamena вместе с Windows
english.AutoStartTask=Start AutoZamena with Windows

[Tasks]
Name: "autostart"; Description: "{cm:AutoStartTask}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\AutoZamena.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\AutoZamena"; Filename: "{app}\AutoZamena.exe"; Comment: "Автозамена опечаток и переключение раскладки клавиатуры"
Name: "{autodesktop}\AutoZamena"; Filename: "{app}\AutoZamena.exe"; Tasks: desktopicon; Comment: "Автозамена опечаток и переключение раскладки клавиатуры"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AutoZamena"; ValueData: """{app}\AutoZamena.exe"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\AutoZamena.exe"; Description: "{cm:LaunchProgram,AutoZamena}"; Flags: nowait postinstall skipifsilent

[Code]
procedure StopRunningAutoZamena();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM AutoZamena.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningAutoZamena();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningAutoZamena();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AutoZamena');
  end;
end;
