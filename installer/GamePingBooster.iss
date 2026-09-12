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

; Wintun ships beside the service, which is what loads it. The signed DLL comes from wintun.net;
; see client/native/wintun/README.md. Listed separately because it is not a build output - it is
; downloaded into the tree by hand.
Source: "{#Root}\client\native\wintun\wintun.dll"; DestDir: "{app}"; Flags: ignoreversion

; The profiles are content: the game address ranges. It is read relative to the install
; directory. Automatically include all production profiles, excluding example/template files.
Source: "{#Root}\profiles\*.json"; DestDir: "{app}\profiles"; Excludes: "*.example.json"; Flags: ignoreversion

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
  Flags: runhidden waituntilterminated; StatusMsg: "Registering the service..."

Filename: "{sys}\sc.exe"; \
  Parameters: "description {#ServiceName} ""Routes game traffic through a relay to reduce latency."""; \
  Flags: runhidden waituntilterminated

; Restart on failure: after 5 seconds, then 5, then every 60. A tunnel service that dies once
; and stays dead leaves the player with no network path they were relying on.
Filename: "{sys}\sc.exe"; \
  Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/60000"; \
  Flags: runhidden waituntilterminated

Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; \
  Flags: runhidden waituntilterminated; StatusMsg: "Starting the service..."

; Put the Wintun driver in place now, while a progress bar is already on screen, rather than
; when the user presses Connect for the first time. This runs as SYSTEM by virtue of the
; installer being elevated; see DriverSetup.cs.
Filename: "{app}\{#ServiceExe}"; Parameters: "--install-driver"; \
  Flags: runhidden waituntilterminated skipifdoesntexist; StatusMsg: "Installing the network driver..."

; Outbound UDP for the service. Inbound is never needed - the relay only ever answers packets
; the client sent first, so there is no listening port to open.
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=out action=allow program=""{app}\{#ServiceExe}"" protocol=UDP enable=yes"; \
  Flags: runhidden waituntilterminated; StatusMsg: "Adding the firewall rule..."

; Started as the logged-in user, not as SYSTEM: the UI is deliberately unprivileged, and running
; it elevated here would be the one time in its life that it was not.
Filename: "{app}\{#UiExe}"; Description: "Start {#AppName}"; \
  Flags: nowait postinstall skipifsilent runasoriginaluser

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

function ServiceStopped(): Boolean;
var
  code: Integer;
begin
  // sc query returns 1060 when the service does not exist, which counts as stopped.
  Result := True;
  if Exec(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}', '',
          SW_HIDE, ewWaitUntilTerminated, code) then
    Result := (code = 1060);
end;

// Stops the service and waits for it to actually be gone.
//
// sc stop returns as soon as the request is accepted, not when the process has exited, and the
// process holds gpb-service.exe and wintun.dll open. Copying over it while it is still running
// fails with a locked-file error that mentions neither the service nor why. Ten seconds is far
// longer than a clean shutdown takes and still short enough not to look hung.
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
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  code, waited: Integer;
begin
  Result := '';
  NeedsRestart := False;

  // The UI holds its own files open, and it is an ordinary user process, so this is allowed to
  // fail quietly - it may simply not be running.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#UiExe}', '',
       SW_HIDE, ewWaitUntilTerminated, code);

  StopServiceAndWait();

  // Upgrades reach here with the service already registered. sc create would fail on the second
  // install, so it is removed first and recreated by the [Run] section, which also means any
  // change to the service's parameters takes effect on upgrade instead of being silently
  // ignored.
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '',
       SW_HIDE, ewWaitUntilTerminated, code);

  // A delete does not always take effect immediately. If anything still holds a handle to the
  // service - services.msc left open on it is the usual culprit - Windows marks it for deletion
  // and keeps it until that handle closes. sc create then fails with 1072, "marked for
  // deletion", and because the [Run] entries are runhidden and unchecked, setup would finish
  // looking successful while leaving no service registered at all. Better to stop here and say
  // exactly which window to close.
  waited := 0;
  while (waited < 10000) and not ServiceStopped() do
  begin
    Sleep(500);
    waited := waited + 500;
  end;
  if not ServiceStopped() then
    Result := 'The existing {#AppName} service could not be removed, so a new one cannot be ' +
              'registered. This usually means the Services window (services.msc) is open on ' +
              'it. Close that window and run setup again.';
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
  end;
end;
