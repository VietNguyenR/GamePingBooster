; Game Ping Booster installer (Inno Setup 6)
;
; Build it with:   .\gpb.ps1 installer      (or ./gpb installer from Git Bash)
; That publishes the Native AOT binaries first, because this script only packages what is
; already built and will refuse to run if they are missing.
;
; Why Inno Setup and not WiX: WiX v7 refuses to run until its Open Source Maintenance Fee EULA
; is accepted, which is a licensing commitment for a commercial product. Inno Setup is free for
; commercial use with nothing to sign. The trade is that this produces a .exe rather than an
; .msi - which costs nothing here, since nobody deploys a game utility by Group Policy.
;
; The four things this has to get right, in order of how badly they fail when wrong:
;
;   1. Stop the service BEFORE copying files. It runs as LocalSystem and holds gpb-service.exe
;      and wintun.dll open; a copy over a running service fails with a locked-file error that
;      names neither.
;   2. Register the service as LocalSystem. Administrator is not enough for Wintun - it returns
;      ERROR_ACCESS_DENIED (5) and the tunnel never comes up.
;   3. Install the Wintun driver during setup, not on the user's first Connect. Otherwise their
;      first impression is a pause and a driver notification from Windows.
;   4. Remove the service on uninstall. A leftover service that points at deleted files sits in
;      the machine forever and cannot be removed from the UI.

#define AppName        "Game Ping Booster"

; The version comes from ..\VERSION, the one place the whole product agrees on it, and
; `gpb.ps1 installer` passes it in with /DAppVersion. This used to be a hand-written "0.1.0"
; here and nothing else in the tree said anything at all, so every binary shipped as 1.0.0 - the
; MSBuild default - inside a setup .exe that called itself something different.
;
; #ifndef, not a bare #define: a #define here would overwrite whatever /D put in, which is the
; quiet way for a command-line version to be accepted and then ignored. The fallback is for
; running ISCC directly on this file, where nothing passes it.
#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

; The same version with any pre-release suffix removed. Windows version resources are four
; numbers and nothing else, so "1.0.0-beta1" cannot go in one - Inno refuses to compile. The
; suffix stays in AppVersion, which is free text and is what the user actually reads.
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.0.0"
#endif
#define AppPublisher   "Game Ping Booster"
#define ServiceName    "GamePingBooster"
#define ServiceExe     "gpb-service.exe"
#define UiExe          "GamePingBooster.exe"

; Relative to this file, which lives in installer/
#define Root           ".."
#define ServicePublish Root + "\client\src\GamePingBooster.Service\bin\Release\net9.0-windows\win-x64\publish"
#define AppPublish     Root + "\client\src\GamePingBooster.App\bin\Release\net9.0-windows\win-x64\publish"
#define UpdaterPublish Root + "\client\src\GamePingBooster.Updater\bin\Release\net9.0-windows\win-x64\publish"

[Setup]
AppId={{6E7A4C21-8D3F-4B62-9E15-2C4A7F9D0B83}
AppName={#AppName}
AppVersion={#AppVersion}
; So the setup .exe itself reports a version in its file properties. Without it a folder of
; these is distinguishable only by filename, and a renamed one by nothing at all.
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductVersion={#AppVersionNumeric}
VersionInfoProductTextVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#UiExe}
OutputDir={#Root}\installer\dist
OutputBaseFilename=GamePingBooster-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Code signing, only when gpb.ps1 passes /DSignInstaller together with /Sgpbsign=<signtool line>
; (see Get-SigningSetup there). Inno then signs this setup .exe and the uninstaller it writes into
; {app}; the two program .exes were already signed in the publish folder before packaging.
#ifdef SignInstaller
SignTool=gpbsign
SignedUninstaller=yes
#endif

; The service must be registered as LocalSystem and the driver installed, so this cannot run
; unelevated. Asking up front is better than failing halfway through.
PrivilegesRequired=admin

; Wintun and the tunnel are 64-bit only.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; The WHOLE publish output of each project, not a hand-written list of executables.
;
; The first version named GamePingBooster.exe and gpb-service.exe explicitly, and produced an
; installer that put a working .exe on a clean machine where it crashed instantly. Native AOT
; still leaves native satellites beside the binary - libSkiaSharp.dll is Avalonia's renderer,
; libHarfBuzzSharp.dll shapes text, av_libglesv2.dll is ANGLE - and none of them were copied.
; The crash was a TypeInitializationException inside SkiaPlatform.Initialize, which names
; neither a missing file nor the installer, and it could not be reproduced on the build machine
; because all three DLLs were already sitting in the publish folder there.
;
; Globbing is not laziness, it is the fix: a dependency added later comes along on its own
; instead of going missing and failing at run time on somebody else's PC.
Source: "{#ServicePublish}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.zip"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#AppPublish}\*";     DestDir: "{app}"; Excludes: "*.pdb,*.zip"; Flags: ignoreversion recursesubdirs createallsubdirs

; The updater window (0.5.0): one AOT exe with no satellites. The app runs a copy of it from %TEMP%, so this file
; is never in use when setup replaces it.
Source: "{#UpdaterPublish}\gpb-updater.exe"; DestDir: "{app}"; Flags: ignoreversion

; Wintun ships beside the service, which is what loads it. The signed DLL comes from wintun.net;
; see client/native/wintun/README.md. Listed separately because it is not a build output - it is
; downloaded into the tree by hand.
Source: "{#Root}\client\native\wintun\wintun.dll"; DestDir: "{app}"; Flags: ignoreversion

; The profile is content: the game's address ranges. It is read relative to the install
; directory, which is why ServiceConfig's profilePath default is a relative path and why nothing
; here writes an absolute one - an absolute path only works on the machine it was written on.
; One file per game. The service loads every *.json beside the configured one, so a game is added
; by shipping its file here and nothing else.
Source: "{#Root}\profiles\pubg-vn.json"; DestDir: "{app}\profiles"; Flags: ignoreversion
Source: "{#Root}\profiles\cs2-vn.json"; DestDir: "{app}\profiles"; Flags: ignoreversion
Source: "{#Root}\profiles\valorant-vn.json"; DestDir: "{app}\profiles"; Flags: ignoreversion

[Dirs]
; The service writes its configuration and logs here, as LocalSystem. Nothing is placed in it at
; install time: there is no key to write, and the user sets the relay from the app's settings
; screen once it starts.
;
; GamePingBooster, NOT {#AppName}. The display name has spaces in it and the data directory does
; not - ServiceConfig.DefaultDirectory is Combine(CommonApplicationData, "GamePingBooster"). This
; used to say {#AppName}, so setup created an empty "Game Ping Booster" folder that nothing ever
; opened, while the folder the service actually uses was created at run time by whichever code
; path got there first. Nothing broke, which is why it survived; it just meant the installer was
; not doing the one thing this section is here for.
Name: "{commonappdata}\GamePingBooster"
Name: "{commonappdata}\GamePingBooster\logs"

[Icons]
Name: "{group}\{#AppName}";            Filename: "{app}\{#UiExe}"
Name: "{autodesktop}\{#AppName}";      Filename: "{app}\{#UiExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Run]
; Order matters. The service is created and started first so that the driver step, which needs
; LocalSystem, runs against an installation that is already complete.
Filename: "{sys}\sc.exe"; \
  Parameters: "create {#ServiceName} binPath= ""{app}\{#ServiceExe}"" start= auto obj= LocalSystem DisplayName= ""{#AppName}"""; \
  Flags: runhidden waituntilterminated; StatusMsg: "Registering the service..."; \
  BeforeInstall: WriteProgress('registering-service')

; On an upgrade the service already exists and the create above fails harmlessly (1073). The same
; parameters are written with config instead, so a change to them still takes effect on upgrade.
; Upgrades used to delete the service and create it again; see PrepareToInstall for why they no longer do.
Filename: "{sys}\sc.exe"; \
  Parameters: "config {#ServiceName} binPath= ""{app}\{#ServiceExe}"" start= auto obj= LocalSystem DisplayName= ""{#AppName}"""; \
  Flags: runhidden waituntilterminated

Filename: "{sys}\sc.exe"; \
  Parameters: "description {#ServiceName} ""Routes game traffic through a relay to reduce latency."""; \
  Flags: runhidden waituntilterminated

; Restart on failure: after 5 seconds, then 5, then every 60. A tunnel service that dies once
; and stays dead leaves the player with no network path they were relying on.
Filename: "{sys}\sc.exe"; \
  Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/60000"; \
  Flags: runhidden waituntilterminated

Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; \
  Flags: runhidden waituntilterminated; StatusMsg: "Starting the service..."; \
  BeforeInstall: WriteProgress('starting-service')

; Put the Wintun driver in place now, while a progress bar is already on screen, rather than
; when the user presses Connect for the first time. This runs as SYSTEM by virtue of the
; installer being elevated; see DriverSetup.cs.
Filename: "{app}\{#ServiceExe}"; Parameters: "--install-driver"; \
  Flags: runhidden waituntilterminated skipifdoesntexist; StatusMsg: "Installing the network driver..."; \
  BeforeInstall: WriteProgress('installing-driver')

; Outbound UDP for the service. Inbound is never needed - the relay only ever answers packets
; the client sent first, so there is no listening port to open.
;
; Deleted first: "add" adds another rule of the same name every time, and with the service installing
; every release by itself (0.5.0) a machine would collect one per release.
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; \
  Flags: runhidden waituntilterminated; BeforeInstall: WriteProgress('firewall')

Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=out action=allow program=""{app}\{#ServiceExe}"" protocol=UDP enable=yes"; \
  Flags: runhidden waituntilterminated; StatusMsg: "Adding the firewall rule..."

; Started as the logged-in user, not as SYSTEM: the UI is deliberately unprivileged, and running
; it elevated here would be the one time in its life that it was not.
Filename: "{app}\{#UiExe}"; Description: "Start {#AppName}"; \
  Flags: nowait postinstall skipifsilent runasoriginaluser

; The manual in-app update runs setup with /SILENT /RELAUNCH=1, and the entry above is skipped in silent
; mode - so without this the app would close for the update and never come back. Keyed on its own
; switch rather than on silent mode, so a silent install somebody scripts by hand still starts
; nothing. runasoriginaluser works because the app starts setup unelevated and setup raises the
; UAC prompt itself; see UpdateInstaller.
Filename: "{app}\{#UiExe}"; \
  Flags: nowait runasoriginaluser; Check: ShouldRelaunch

; The SERVICE's automatic update (0.5.0, AutoUpdater) runs setup as SYSTEM with /VERYSILENT /AUTOUPDATE=1
; and no /RELAUNCH: "original user" would be SYSTEM, on a desktop nobody sees. The updater window
; (gpb-updater.exe, running as the user) starts the app once the new service is up; the new service does
; it when no updater window watched the update.

[UninstallRun]
; Exact reverse of the install, and every step tolerates already being done - an uninstall that
; fails leaves the user stuck with software they have asked to remove.
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewall"

Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; \
  Flags: runhidden waituntilterminated; RunOnceId: "StopService"

; Before the service binary is deleted, and before the service itself is removed: this needs the
; executable that is about to go away.
Filename: "{app}\{#ServiceExe}"; Parameters: "--remove-driver"; \
  Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveDriver"

Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; \
  Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

[Code]
// True when the in-app updater started this setup and wants the app back afterwards.
function ShouldRelaunch(): Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

// ---------------------------------------------------------------- progress for the updater window
//
// During the service's automatic update (/AUTOUPDATE=1) setup says what it is doing in the same file the
// service wrote the download into: %ProgramData%\GamePingBooster\update\status\progress.txt, which SYSTEM
// writes and Users read. gpb-updater.exe shows it step by step. The phase names are UpdateProgress's
// (client/src/GamePingBooster.Core/Updates/UpdateProgress.cs) - keep the two in step. Nothing here can fail
// the install: a line not written is a line not shown.

var
  ProgressFinished: Boolean;
  LastCopyPercent: Integer;

function IsAutoUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:AUTOUPDATE|0}') = '1';
end;

procedure WriteProgressLines(Lines: String);
var
  Folder: String;
begin
  if not IsAutoUpdate() then
    Exit;
  Folder := ExpandConstant('{commonappdata}\GamePingBooster\update\status');
  // The service made the folder (readable by Users) before it started setup; never made here with other rights.
  if not DirExists(Folder) then
    Exit;
  SaveStringToFile(Folder + '\progress.txt',
    'phase=' + Lines + #13#10 + 'version={#AppVersion}' + #13#10, False);
end;

procedure WriteProgress(Phase: String);
begin
  WriteProgressLines(Phase);
end;

// Called by Inno as files are copied. Written at each whole percent, not each call: it fires per chunk.
procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
var
  Percent: Integer;
begin
  if MaxProgress <= 0 then
    Exit;
  Percent := (CurProgress * 100) div MaxProgress;
  if Percent = LastCopyPercent then
    Exit;
  LastCopyPercent := Percent;
  WriteProgressLines('copying' + #13#10 + 'done=' + IntToStr(Percent) + #13#10 + 'total=100');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    LastCopyPercent := -1;
    WriteProgressLines('copying' + #13#10 + 'done=0' + #13#10 + 'total=100');
  end;
  if CurStep = ssDone then
  begin
    ProgressFinished := True;
    WriteProgress('setup-done');
  end;
end;

// Always last. Without ssDone first, setup gave up somewhere: say so, so the window does not wait out its timeout.
procedure DeinitializeSetup();
begin
  if not ProgressFinished then
    WriteProgress('setup-ended');
end;


// The service's state, asked of the Service Control Manager directly.
//
// This used to be "sc query exited 1060", which is true only when the service does NOT EXIST - so it
// meant "gone", never "stopped". That held while upgrades deleted the service and waited for it to
// vanish, and it was wrong everywhere else: StopServiceAndWait always sat out its full ten seconds,
// and once upgrades stopped deleting the service (0.5.0) every install over an existing one was
// refused with "did not stop" although it had. sc's printed STATE cannot be read from here, so the
// state comes from the API that sc itself reads.
type
  TServiceStatus = record
    dwServiceType: Cardinal;
    dwCurrentState: Cardinal;
    dwControlsAccepted: Cardinal;
    dwWin32ExitCode: Cardinal;
    dwServiceSpecificExitCode: Cardinal;
    dwCheckPoint: Cardinal;
    dwWaitHint: Cardinal;
  end;

function OpenSCManager(lpMachineName, lpDatabaseName: String; dwDesiredAccess: Cardinal): THandle;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenService(hSCManager: THandle; lpServiceName: String; dwDesiredAccess: Cardinal): THandle;
  external 'OpenServiceW@advapi32.dll stdcall';
function QueryServiceStatus(hService: THandle; var ServiceStatus: TServiceStatus): Boolean;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function CloseServiceHandle(hSCObject: THandle): Boolean;
  external 'CloseServiceHandle@advapi32.dll stdcall';

// True when the service is stopped or does not exist. False while it runs, starts or stops - and
// when its state cannot be read at all, so a copy is never attempted over a service in an unknown state.
function ServiceStopped(): Boolean;
var
  manager, service: THandle;
  status: TServiceStatus;
begin
  Result := False;
  // SC_MANAGER_CONNECT; '' is the local machine, 'ServicesActive' is SERVICES_ACTIVE_DATABASE.
  manager := OpenSCManager('', 'ServicesActive', $0001);
  if manager = 0 then
    Exit;
  try
    // SERVICE_QUERY_STATUS. No handle means no such service (1060), which counts as stopped.
    service := OpenService(manager, '{#ServiceName}', $0004);
    if service = 0 then
    begin
      Result := (DLLGetLastError() = 1060);
      Exit;
    end;
    try
      // SERVICE_STOPPED = 1.
      if QueryServiceStatus(service, status) then
        Result := (status.dwCurrentState = 1);
    finally
      CloseServiceHandle(service);
    end;
  finally
    CloseServiceHandle(manager);
  end;
end;

// Stops the service and waits for it to report STOPPED.
//
// sc stop returns as soon as the request is accepted, not when the process has exited, and the
// process holds gpb-service.exe and wintun.dll open. Copying over it while it is still running
// fails with a locked-file error that mentions neither the service nor why. Ten seconds is far
// longer than a clean shutdown takes and still short enough not to look hung.
//
// STOPPED is reported when the engine has finished tearing down, a moment BEFORE the process exits
// and lets go of its files - hence the second's grace after it.
procedure StopServiceAndWait();
var
  code, waited: Integer;
begin
  if ServiceStopped() then
    Exit;

  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '',
       SW_HIDE, ewWaitUntilTerminated, code);

  waited := 0;
  while (waited < 10000) and not ServiceStopped() do
  begin
    Sleep(500);
    waited := waited + 500;
  end;
  if ServiceStopped() then
    Sleep(1000);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  code: Integer;
begin
  Result := '';
  NeedsRestart := False;

  // The UI holds its own files open, and it is an ordinary user process, so this is allowed to
  // fail quietly - it may simply not be running.
  WriteProgress('closing-app');
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#UiExe}', '',
       SW_HIDE, ewWaitUntilTerminated, code);

  WriteProgress('stopping-service');
  StopServiceAndWait();

  // Upgrades used to delete the service here and let [Run] create it again. They no longer do. A
  // delete does not always take effect at once - anything holding a handle to the service,
  // services.msc left open on it being the usual culprit, keeps it "marked for deletion" (1072) -
  // and when that happened during the service's own silent update (0.5.0) the machine was left with
  // NO service: nothing to install the next release, nothing to retry, no window to say why. [Run]
  // now creates the service when it is missing and rewrites its parameters with sc config when it
  // is not, which is all the delete was for.
  //
  // What must still hold is that it has STOPPED: copying over a running gpb-service.exe fails on a
  // locked file part-way through. Refused here, before anything is copied - the automatic update then
  // starts the old service again (AutoUpdater.SetupCommand) and tries later.
  if not ServiceStopped() then
  begin
    Result := 'The {#AppName} service did not stop, so its files cannot be replaced. ' +
              'Restart the computer and run setup again.';
    ProgressFinished := True;
    WriteProgressLines('setup-failed' + #13#10 + 'reason=' + Result);
  end;
end;

// "Start with Windows" (StartupStore in the app) is a value in each person's HKCU, which this elevated
// uninstaller does not see as its own when somebody else typed the admin password. So every profile
// that is loaded - everybody signed in - is cleaned through HKEY_USERS. A profile that is not loaded
// keeps a value pointing at a missing exe, which Windows skips without a word.
procedure RemoveStartWithWindows();
var
  users: TArrayOfString;
  i: Integer;
begin
  if RegGetSubkeyNames(HKEY_USERS, '', users) then
    for i := 0 to GetArrayLength(users) - 1 do
    begin
      RegDeleteValue(HKEY_USERS, users[i] + '\Software\Microsoft\Windows\CurrentVersion\Run', 'GamePingBooster');
      RegDeleteValue(HKEY_USERS, users[i] + '\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'GamePingBooster');
    end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  code: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#UiExe}', '',
         SW_HIDE, ewWaitUntilTerminated, code);
    StopServiceAndWait();
    RemoveStartWithWindows();
  end;
end;
