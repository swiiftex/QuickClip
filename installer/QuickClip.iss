; QuickClip installer (Inno Setup 6). Build it with scripts\package.ps1, which passes:
;   /DAppVersion=0.3.0-beta /DNumericVersion=0.3.0.0 /DDisplayVersion="0.3 beta" /DSourceDir=<app files> /DOutputDir=<folder>
; Installs per user (no admin prompt) into %LOCALAPPDATA%\Programs\QuickClip.

#ifndef AppVersion
  #error Build the installer with scripts\package.ps1
#endif

[Setup]
AppId={{8F6A4C0E-5B1D-4E3A-9C27-2D6B1F0A7E91}
AppName=QuickClip
AppVersion={#AppVersion}
AppVerName=QuickClip {#DisplayVersion}
AppPublisher=swiiftex
AppPublisherURL=https://github.com/swiiftex/QuickClip
AppSupportURL=https://github.com/swiiftex/QuickClip/issues
AppUpdatesURL=https://github.com/swiiftex/QuickClip/releases
VersionInfoVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
DefaultDirName={autopf}\QuickClip
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=QuickClip-{#AppVersion}-setup
SetupIconFile=..\src\QuickClip\Assets\QuickClip.ico
UninstallDisplayIcon={app}\QuickClip.exe
UninstallDisplayName=QuickClip
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "contextmenu"; Description: "Add ""Edit with QuickClip"" to the right-click menu of video and audio files"
Name: "startup"; Description: "Start QuickClip when I sign in (it records in the background, from the tray)"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\QuickClip"; Filename: "{app}\QuickClip.exe"; Comment: "Instant replay clipping and clip editor"
Name: "{autodesktop}\QuickClip"; Filename: "{app}\QuickClip.exe"; Tasks: desktopicon

[Registry]
; Same entry the app's own "Start with Windows" setting manages.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "QuickClip"; \
    ValueData: """{app}\QuickClip.exe"" --tray"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\QuickClip.exe"; Parameters: "--register"; Tasks: contextmenu; Flags: runhidden waituntilterminated; \
    StatusMsg: "Adding the right-click menu entry..."
Filename: "{app}\QuickClip.exe"; Description: "Open QuickClip"; Flags: nowait postinstall skipifsilent
; In-app updates run this installer silently with /LAUNCH=tray or /LAUNCH=window.
Filename: "{app}\QuickClip.exe"; Parameters: "{code:RelaunchParameters}"; Flags: nowait; Check: RelaunchAfterUpdate

[UninstallRun]
Filename: "{app}\QuickClip.exe"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitQuickClip"
Filename: "{app}\QuickClip.exe"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveMenuEntry"
Filename: "powershell.exe"; Parameters: "-NoProfile -Command ""Get-AppxPackage QuickClip.ContextMenu | Remove-AppxPackage"""; \
    Flags: runhidden waituntilterminated; RunOnceId: "RemoveMenuPackage"

[UninstallDelete]
; Leftovers from the optional Windows 11 menu package (scripts\add-win11-menu.ps1)
Type: files; Name: "{app}\QuickClipShell.dll*"
Type: dirifempty; Name: "{app}"

[Code]
function RelaunchAfterUpdate: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:LAUNCH|}') <> '');
end;

function RelaunchParameters(Param: String): String;
begin
  if ExpandConstant('{param:LAUNCH|}') = 'tray' then
    Result := '--tray'
  else
    Result := '';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  ResultCode: Integer;
begin
  // A running QuickClip (often recording in the tray) is asked to stop cleanly before its files are replaced.
  Exe := ExpandConstant('{app}\QuickClip.exe');
  if FileExists(Exe) then
  begin
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2500);
  end;
  Result := '';
end;
