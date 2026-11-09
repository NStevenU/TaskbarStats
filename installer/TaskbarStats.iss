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
  // Pinned official release (the same file winget installs). Update the URLs and the checksum together when a new PawnIO ships.
  // The API address is tried first: github.com/.../releases/download answers HEAD (Inno's own downloader) and, at times,
  // plain GET requests too with "504 Gateway Time-out", while the API address keeps working.
  PawnIoApiUrl = 'https://api.github.com/repos/namazso/PawnIO.Setup/releases/assets/374375077';
  PawnIoUrl = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe';
  PawnIoSha256 = '1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032';

// One download attempt through curl.exe (ships with Windows 10 1803+ and 11). Err describes the failure.
function CurlGet(const Url, Headers, Dest: String; var Err: String): Boolean;
var
  Curl, Log, Code: String;
  Raw: AnsiString;
  ResultCode: Integer;
begin
  Result := False;
  Curl := ExpandConstant('{sys}\curl.exe');
  Log := ExpandConstant('{tmp}\curl_status.txt');
  if not FileExists(Curl) then
  begin
    Err := 'curl.exe was not found';
    Exit;
  end;
  DeleteFile(Dest);
  DeleteFile(Log);
  if not Exec(ExpandConstant('{cmd}'),
      '/c ""' + Curl + '" -sSL --retry 3 --retry-delay 2 --connect-timeout 20 --max-time 180 ' + Headers +
      ' -o "' + Dest + '" -w "%{http_code}" "' + Url + '" > "' + Log + '" 2>&1"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Err := 'curl.exe could not be started';
    Exit;
  end;
  Code := '';
  if LoadStringFromFile(Log, Raw) then Code := Trim(String(Raw));
  if (ResultCode <> 0) or (Code <> '200') or not FileExists(Dest) then
  begin
    if Code = '' then Code := 'curl error ' + IntToStr(ResultCode);
    Err := 'HTTP ' + Code;
    DeleteFile(Dest);
    Exit;
  end;
  if CompareText(GetSHA256OfFile(Dest), PawnIoSha256) <> 0 then
  begin
    Err := 'the downloaded file did not match the expected checksum';
    DeleteFile(Dest);
    Exit;
  end;
  Result := True;
end;

function DownloadPawnIo(const Dest: String; var Err: String): Boolean;
var
  Err2: String;
begin
  Result := CurlGet(PawnIoApiUrl, '-H "Accept: application/octet-stream"', Dest, Err);
  if not Result then
  begin
    Result := CurlGet(PawnIoUrl, '', Dest, Err2);
    if not Result then Err := Err + ' / ' + Err2;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Installer, Err: String;
begin
  if (CurStep = ssInstall) and WizardIsTaskSelected('pawnio') then
  begin
    WizardForm.StatusLabel.Caption := 'Downloading the PawnIO driver installer...';
    WizardForm.Refresh;
    if not DownloadPawnIo(ExpandConstant('{tmp}\PawnIO_setup.exe'), Err) then
      // not fatal: the widget works without PawnIO, only CPU / motherboard sensors are missing
      SuppressibleMsgBox('PawnIO could not be downloaded (' + Err + ').' + #13#10 +
        'TaskbarStats will be installed without it. You can install PawnIO later from pawnio.eu.',
        mbInformation, MB_OK, IDOK);
  end;

  if CurStep = ssPostInstall then
  begin
    Installer := ExpandConstant('{tmp}\PawnIO_setup.exe');
    if WizardIsTaskSelected('pawnio') and FileExists(Installer) then
    begin
      WizardForm.StatusLabel.Caption := 'Installing the PawnIO driver...';
      WizardForm.Refresh;
      Exec(Installer, '-install -silent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
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
