; Inno Setup 6.1+ script. Build: iscc /DAppVersion=1.0.0 /DSourceDir=..\publish installer\TaskbarStats.iss
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6B1F0C52-3D5E-4C8B-9A64-5B7B1F0E7A21}
AppName=TaskbarStats
AppVerName=TaskbarStats
UninstallDisplayName=TaskbarStats
AppVersion={#AppVersion}
AppPublisher=NSteven
DefaultDirName={autopf}\TaskbarStats
DefaultGroupName=TaskbarStats
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=TaskbarStats-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\TaskbarStats.exe
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "pawnio"; Description: "Download and install PawnIO driver (recommended: CPU temperature and motherboard fans)"; GroupDescription: "Sensors:"
Name: "autostart"; Description: "Start TaskbarStats with Windows (as administrator, no UAC prompt)"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TaskbarStats"; Filename: "{app}\TaskbarStats.exe"
Name: "{autodesktop}\TaskbarStats"; Filename: "{app}\TaskbarStats.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TaskbarStats.exe"; Parameters: "--autostart on"; Flags: runhidden; Tasks: autostart; StatusMsg: "Registering startup task..."
Filename: "{app}\TaskbarStats.exe"; Description: "Start TaskbarStats"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\TaskbarStats.exe"; Parameters: "--autostart off"; Flags: runhidden; RunOnceId: "RemoveStartupTask"

[Code]
const
  PawnIoUrl = 'https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe';

var
  DownloadPage: TDownloadWizardPage;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('Downloading PawnIO', 'The official PawnIO installer is being downloaded from GitHub.', nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and WizardIsTaskSelected('pawnio') then
  begin
    DownloadPage.Clear;
    DownloadPage.Add(PawnIoUrl, 'PawnIO_setup.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        // not fatal: the widget works without PawnIO, only CPU / motherboard sensors are missing
        if SuppressibleMsgBox('PawnIO could not be downloaded (' + GetExceptionMessage + ').' + #13#10 +
           'TaskbarStats will be installed without it. You can install PawnIO later from pawnio.eu.',
           mbInformation, MB_OK, IDOK) = IDOK then;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Installer: String;
begin
  if CurStep = ssPostInstall then
  begin
    Installer := ExpandConstant('{tmp}\PawnIO_setup.exe');
    if WizardIsTaskSelected('pawnio') and FileExists(Installer) then
      Exec(Installer, '-install -silent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure PawnIoUninstallPrompt;
var
  Dir, Exe: String;
  ResultCode: Integer;
begin
  if not RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO', 'InstallLocation', Dir) then Exit;
  Exe := AddBackslash(Dir) + 'uninstall.exe';
  if not FileExists(Exe) then Exit;
  if MsgBox('Also uninstall the PawnIO driver?' + #13#10 + #13#10 +
            'Other programs that read hardware sensors (for example LibreHardwareMonitor) may use it too.' + #13#10 +
            'Choose No to keep it.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    Exec(Exe, '-uninstall -silent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    PawnIoUninstallPrompt;
    if DirExists(ExpandConstant('{userappdata}\TaskbarStats')) then
      if MsgBox('Also delete your TaskbarStats settings?' + #13#10 + #13#10 +
                ExpandConstant('{userappdata}\TaskbarStats') + #13#10 + #13#10 +
                'Choose No to keep them for a later reinstall.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ExpandConstant('{userappdata}\TaskbarStats'), True, True, True);
  end;
end;
