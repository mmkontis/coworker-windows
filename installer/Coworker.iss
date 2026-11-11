; Coworker for Windows - Inno Setup 6.3+ script (per-user install, no admin rights, silent-update friendly).
;
; Normally compiled by installer\create_installer.ps1, which publishes the app and passes the values below:
;   iscc /DAppVersion=1.2.0 /DAppBuild=7 /DArch=x64 /DSourceDir="<publish folder>" /DOutputDir="<release folder>" Coworker.iss
;
; What it does
;   * installs the self-contained publish output to %LOCALAPPDATA%\Programs\Coworker (PrivilegesRequired=lowest, so
;     the in-app updater never needs UAC);
;   * registers the coworker-auth:// and coworker:// URL handlers for the current user (same keys the app writes itself);
;   * creates the Start menu shortcut; "launch at login" is NOT set up here, the app owns that (HKCU Run value "Coworker");
;   * update installs (silent, started by the app's updater with /VERYSILENT ...) close the old app if needed and
;     start the new build again when done, in the background;
;   * uninstall removes files, shortcuts, URL handlers and the Run value; the user's data in %LOCALAPPDATA%\Coworker
;     (settings, history cache) stays unless they say so.

#if VER < EncodeVer(6, 3, 0)
  #error Inno Setup 6.3 or newer is required (x64compatible / arm64 architecture identifiers).
#endif

#ifndef AppVersion
  #error Pass /DAppVersion=<major.minor.patch> (create_installer.ps1 reads it from Coworker.csproj).
#endif
#ifndef AppBuild
  #define AppBuild "1"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\bin\Release\net9.0-windows10.0.22621.0\win-" + Arch + "\publish"
#endif
#ifndef OutputDir
  #define OutputDir "release"
#endif

#define AppName "Coworker"
#define AppExe "Coworker.exe"
#define AppPublisher "Humanlike"
#define AppUrl "https://humanlike.co/coworker"

[Setup]
; Never change AppId: it is how Windows and the updater recognise an existing install.
AppId={{735E51F2-3854-47BA-895A-7C5A7986149C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
AppCopyright=Copyright (c) {#AppPublisher}
VersionInfoVersion={#AppVersion}.{#AppBuild}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

; Per user: no UAC prompt, and {autopf} becomes %LOCALAPPDATA%\Programs.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
UsePreviousAppDir=yes
MinVersion=10.0.17763

#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
; x64compatible also lets an ARM64 Windows 11 PC run the x64 build under emulation.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-{#Arch}
SetupIconFile=..\Assets\coworker.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
SetupLogging=yes

; Update installs: the old app is asked to close (Restart Manager) if it still runs. The updater quits the app
; itself first and the [Run] entry below starts the new build, so nothing is restarted by Restart Manager.
CloseApplications=yes
CloseApplicationsFilter={#AppExe}
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; URL handlers for the current user. coworker-auth:// completes sign-in in the browser, coworker:// is the Mac's
; second scheme. The app registers the same values on start when missing (App.EnsureProtocolHandler).
Root: HKCU; Subkey: "Software\Classes\coworker-auth"; ValueType: string; ValueName: ""; ValueData: "URL:coworker-auth"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\coworker-auth"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\coworker-auth\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"
Root: HKCU; Subkey: "Software\Classes\coworker-auth\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\coworker"; ValueType: string; ValueName: ""; ValueData: "URL:coworker"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\coworker"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\coworker\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"
Root: HKCU; Subkey: "Software\Classes\coworker\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

[Run]
; Interactive install: offer to start Coworker on the last page.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Silent install = an update started by the app itself (it quit so the files could be replaced): bring it back in
; the background. The app treats --background as "start in the tray, do not raise the main window".
Filename: "{app}\{#AppExe}"; Parameters: "--background --updated"; Flags: nowait skipifnotsilent

[UninstallRun]
; Files that are in use cannot be removed: stop the app first.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopCoworker"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { "Launch at login" is written by the app, not by this installer: remove it so nothing points at a missing exe. }
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Coworker');

    { User data stays unless asked for (never deleted by a silent uninstall). }
    DataDir := ExpandConstant('{localappdata}\Coworker');
    if DirExists(DataDir) and (not UninstallSilent) then
    begin
      if MsgBox('Also delete your Coworker settings and cached data?' + #13#10 + #13#10 + DataDir,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
