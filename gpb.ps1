<#
.SYNOPSIS
    One entry point for everything done day to day on this project.

.DESCRIPTION
    The work was spread across four long commands in four directories, each with a path nobody
    can remember, and two of them pointed at different build outputs for no reason - the service
    ran from bin\Debug while the UI ran from a Release publish. This collapses them into verbs.

    Run it from anywhere:

        .\gpb.ps1 dev                 build and start the service (as LocalSystem) plus the UI
        .\gpb.ps1 stop                stop both
        .\gpb.ps1 capture [game] [udp|tcp|all]  watch for the game and collect server addresses
                                      (udp); tcp/all also report which lobby/login connections
                                      never answered, into tcp-sessions.txt - never the profile
        .\gpb.ps1 etw [game]          PROTOTYPE, run next to capture: the same discovery through ETW,
                                      no Wireshark - reports what it found and what it cost
        .\gpb.ps1 blockcheck <label> [app]  how a blocked service is blocked on THIS line - DNS
                                      poisoning, SNI filtering or a dead address. label is the ISP
                                      and city (vnpt-hcm); app defaults to steam and comes from
                                      tools\blockcheck\targets.json. Turn every VPN off first.
        .\gpb.ps1 dns                 does the Steam name fix work here? Needs an Administrator
                                      terminal; installs nothing that it does not remove again
        .\gpb.ps1 profile [game]      rebuild that game's profile from what was captured
        .\gpb.ps1 push-profile <game> [remove]  seal that local profile into the running service,
                                      to test a game the licence server does not serve yet
        .\gpb.ps1 check               is the game actually going through the relay right now
        .\gpb.ps1 lag [seconds]       run this DURING the lag: which segment is at fault
        .\gpb.ps1 probe               which provider's Singapore network this line reaches best,
                                      and whether a relay there would beat the line's own route
        .\gpb.ps1 logs                follow the service log
        .\gpb.ps1 status              read the tunnel's live counters
        .\gpb.ps1 test                every test on both sides
        .\gpb.ps1 publish             Native AOT build and install into ProgramData
        .\gpb.ps1 diag                collect a diagnostics bundle to send
        .\gpb.ps1 installer [version] build, then package a setup .exe (needs Inno Setup 6)
        .\gpb.ps1 release-profiles    stand the committed example profiles in for missing real ones
        .\gpb.ps1 version [x.y.z]     show or set the version everything is stamped with
        .\gpb.ps1 reset               remove EVERYTHING this software installed, to test setup

        .\gpb.ps1 relay build         cross-compile relayd for Linux
        .\gpb.ps1 relay list          show the relays gpb.conf declares
        .\gpb.ps1 relay deploy [name] build, upload and install on a relay
                                      the mode comes from gpb.conf; --psk or --token asserts it
        .\gpb.ps1 relay logs [name]   follow journalctl on a relay
        .\gpb.ps1 relay test          Go tests only

        .\gpb.ps1 entry deploy [name] upload setup-entry.sh to an entry VPS; it pings every relay
                                      in gpb.conf and forwards to the nearest (runs ./gpb)
        .\gpb.ps1 entry list          show the entries gpb.conf declares

        .\gpb.ps1 release x.y.z       bump VERSION, commit, push main and the tag; GitHub Actions
                                      then builds and publishes the release. Never publish a
                                      release on the GitHub web page - see .github/workflows/release.yml

    Relays are declared in gpb.conf - host, port, user, key or password, one block each. Copy
    gpb.conf.example to gpb.conf and fill it in; see `.\gpb.ps1 relay setup`.

    Games are declared in tools\profile-builder\games.json - which process to watch, which files
    to write, which cloud regions an address may belong to. Leave the game out and the file's
    "default" is used, which is what `capture` and `profile` always did. That file is committed;
    gpb.conf is not.

.EXAMPLE
    .\gpb.ps1 dev
    The usual starting point: everything built and running.
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Verb = 'help',
    [Parameter(Position = 1)][string]$Arg1,
    [Parameter(Position = 2)][string]$Arg2,
    [switch]$Release,

    # Anything left over, verbatim - including tokens that look like switches.
    #
    # `reset` has six of its own and they have to survive the trip. Without this, PowerShell
    # tries to bind `-DryRun` as a parameter of THIS script and fails with "a parameter cannot
    # be found", which points at the wrong file entirely. ValueFromRemainingArguments collects
    # them as plain strings instead, both on a direct call and through `powershell -File`, which
    # is how ./gpb reaches here.
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$client = Join-Path $root 'client'
$relayDir = Join-Path $root 'relay'
$tools = Join-Path $root 'tools'
$builder = Join-Path $tools 'profile-builder'
$programData = Join-Path $env:ProgramData 'GamePingBooster'

# Local settings, never committed: gpb.conf declares each relay's host, port, user and key or
# password. The parser is shared with relay\deploy.ps1 rather than written twice, and follows the
# same rules as the POSIX half in ./gpb. Nothing in this repository names a real host; see
# gpb.conf.example.
. (Join-Path $root 'tools\GpbConf.ps1')

# Which game `capture` and `profile` are working on, and everything that differs between games.
# Committed, unlike gpb.conf: a game's process name and address files are project data, not a
# property of one machine. See tools\profile-builder\games.json.
. (Join-Path $root 'tools\GpbGames.ps1')

function Say($msg, $colour = 'Cyan') { Write-Host "==> $msg" -ForegroundColor $colour }
function Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

# Native AOT needs the MSVC linker, and the ILCompiler finds it through vswhere - which is not on
# PATH by default. Without this a Release publish dies with "'vswhere.exe' is not recognized".
function Add-VsWhereToPath {
    $p = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
    if ((Test-Path (Join-Path $p 'vswhere.exe')) -and ($env:PATH -notlike "*$p*")) {
        $env:PATH = "$p;$env:PATH"
    }
}

# The Native AOT build of both halves, into their own bin\Release publish folders and nowhere else.
#
# Split out of `publish` because `publish` also stops the running service and copies the build into
# ProgramData - right for a developer, wrong for `installer`. The installer only needs the build, and
# `./gpb release` rehearses the whole CI build on this machine before a tag is pushed: a rehearsal
# that stopped the booster somebody is playing through, and replaced its binaries with a build from a
# scratch checkout, would be a strange price for checking a release.
function Invoke-AotPublish {
    Add-VsWhereToPath
    Say "Native AOT publish"
    & dotnet publish (Join-Path $client 'src\GamePingBooster.Service\GamePingBooster.Service.csproj') -c Release -r win-x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
    & dotnet publish (Join-Path $client 'src\GamePingBooster.App\GamePingBooster.App.csproj') -c Release -r win-x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
}

# Git's bash, found through git itself rather than through PATH. A bare `bash` on Windows 11 is
# WSL's, which has no Y: and answers "No such file or directory" for a path that is plainly there -
# measured here, and it reads like a missing file rather than a wrong shell. Git for Windows is
# already required by ./gpb, so this is not a new dependency. Null when it cannot be found.
#
# Walks up from git.exe to the Git for Windows root - the directory holding git-bash.exe - instead
# of assuming git.exe sits exactly two levels below it. That assumption holds for Git\cmd\git.exe,
# which is what a normal PowerShell finds first, and fails for Git\mingw64\bin\git.exe, which is
# what it finds when started from Git Bash, because Git Bash puts mingw64\bin at the front of PATH.
# So this returned null for every gpb.ps1 run launched from Git Bash, and `test` silently skipped
# the deploy-mode test there (found 2026-09-11, when `release` needed the same lookup).
#
# Always bin\bash.exe, never usr\bin\bash.exe: the one in bin sets up PATH for the MSYS tools, and
# the real binary under usr, started directly from Windows, cannot find sed or awk.
function Get-GitBash {
    foreach ($git in @(Get-Command git -All -ErrorAction SilentlyContinue)) {
        $dir = Split-Path $git.Source -Parent
        for ($i = 0; $i -lt 4 -and $dir; $i++) {
            $bash = Join-Path $dir 'bin\bash.exe'
            if ((Test-Path (Join-Path $dir 'git-bash.exe')) -and (Test-Path $bash)) { return $bash }
            $dir = Split-Path $dir -Parent
        }
    }
    return $null
}

function Get-ServiceExe {
    param([switch]$Published)
    if ($Published) { return Join-Path $programData 'bin\gpb-service.exe' }
    return Join-Path $client 'src\GamePingBooster.Service\bin\Debug\net9.0-windows\win-x64\gpb-service.exe'
}

function Get-AppExe {
    Join-Path $client 'src\GamePingBooster.App\bin\Debug\net9.0-windows\win-x64\GamePingBooster.exe'
}

# Every profile the installer ships, read from the .iss itself rather than listed again here, so a
# game added there comes along everywhere without anyone having to remember to.
#
# Two callers: `installer` refuses to package when one is missing, and `dev` copies them beside the
# service binary.
function Get-ShippedProfileFiles {
    $iss = Join-Path $root 'installer\GamePingBooster.iss'
    $names = (Select-String -Path $iss -Pattern 'profiles\\([a-z0-9-]+-vn\.json)' -AllMatches).Matches |
        ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
    if (-not $names) {
        throw "Found no profiles\<game>-vn.json in $iss - has the Source line changed shape?"
    }
    $names | ForEach-Object {
        [pscustomobject]@{ Name = $_; Path = Join-Path $root "profiles\$_" }
    }
}

# The one place the product's version lives.
#
# Read by client\Directory.Build.props, which stamps all four assemblies, and passed to Inno
# Setup with /DAppVersion. Both from this file rather than each side keeping its own copy: two
# copies is how a setup .exe ends up called 0.1.0 with 1.0.0 binaries inside it, which is exactly
# what this repository shipped before the file existed.
$versionFile = Join-Path $root 'VERSION'

function Get-GpbVersion {
    if (-not (Test-Path $versionFile)) { return '0.0.0' }
    return (Get-Content $versionFile -Raw).Trim()
}

<#
.SYNOPSIS
    Writes VERSION, after checking the string is one every consumer will accept.
.DESCRIPTION
    Validated here rather than left to fail later, because "later" is three different places
    with three different error messages: MSBuild rejects a non-numeric AssemblyVersion, Inno
    puts whatever it is given straight into a filename, and Programs and Features sorts it as
    text. x.y.z with an optional -suffix is what all three handle.

    Written without a trailing newline dance: Directory.Build.props trims, and so does
    Get-GpbVersion, so a file edited by hand in any editor still works.
#>
function Set-GpbVersion {
    param([string]$Version)

    $clean = $Version.Trim().TrimStart('v')
    if ($clean -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.]+)?$') {
        throw "'$Version' is not a version. Use x.y.z, optionally with a suffix: 0.2.0, 1.0.0-beta1."
    }

    Set-Content -Path $versionFile -Value $clean -Encoding ascii -NoNewline
    return $clean
}

function Stop-Everything {
    $stopped = @()
    $stubborn = @()
    foreach ($name in 'GamePingBooster', 'gpb-service') {
        $procs = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
        foreach ($p in $procs) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction Stop; $stopped += "$name($($p.Id))" }
            catch { $stubborn += "$name($($p.Id))" }
        }
    }
    if ($stopped.Count -gt 0) { Say "Stopped: $($stopped -join ', ')" 'DarkGray' }

    # A failure here used to be swallowed, and that is expensive.
    #
    # gpb-service runs as LocalSystem, so a non-elevated shell cannot kill it: Stop-Process
    # throws, the catch ate it, and the build then quietly left the OLD service running while
    # the new UI talked to it. The symptom appears much later and somewhere else - an
    # "Unsupported verb" line buried in the service log, or a feature that simply does nothing -
    # and nothing points back at this function.
    if ($stubborn.Count -gt 0) {
        Warn "COULD NOT STOP: $($stubborn -join ', ')"
        Warn "gpb-service runs as LocalSystem and a normal shell cannot stop it. Whatever you"
        Warn "build next will NOT be what is running. Re-run this from an Administrator terminal."
    }
    return $stopped.Count
}

<#
.SYNOPSIS
    Starts the UI as the LOGGED-IN user, even when this script is running elevated.
.DESCRIPTION
    `dev` has to be run from an Administrator terminal, because psexec needs it. A plain
    Start-Process from there hands the UI the elevated token as well - and that is wrong in a way
    that hides bugs rather than causing them.

    The whole privilege split rests on the UI being unprivileged: the named pipe's ACL is opened
    to BuiltinUsers precisely so that a normal-user UI can drive a LocalSystem service, and the
    installer starts the UI with `runasoriginaluser` for the same reason. A dev loop that runs the
    UI elevated therefore never exercises the boundary that production depends on. It also makes
    the app untouchable from an ordinary shell - UIPI refuses even WM_CLOSE from a lower
    integrity level, with ERROR_ACCESS_DENIED - which is how this was noticed.

    Going through explorer.exe is the trick that does it without a token-manipulation helper:
    Explorer runs as the interactive user, so the process it launches does too. It gives back no
    process handle, hence the poll rather than a return value. If it does not appear, fall back to
    launching directly and say plainly what that means, because a UI that does not start at all is
    worse than one running at the wrong integrity level.
#>
function Start-UnelevatedUi {
    param([string]$Path)

    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($id)
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        # Not elevated: nothing to drop, and explorer would only add a layer of indirection.
        Start-Process $Path | Out-Null
        return
    }

    $before = @(Get-Process -Name 'GamePingBooster' -ErrorAction SilentlyContinue).Count
    Start-Process 'explorer.exe' -ArgumentList "`"$Path`"" | Out-Null

    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        if (@(Get-Process -Name 'GamePingBooster' -ErrorAction SilentlyContinue).Count -gt $before) {
            Say "UI started as $($env:USERNAME), not elevated - the same as after an install" 'DarkGray'
            return
        }
    }

    Warn "explorer did not start the UI. Falling back to starting it from here, which means it"
    Warn "runs ELEVATED - unlike a real installation. Fine for a quick look, but do not conclude"
    Warn "anything about the pipe's permissions from it."
    Start-Process $Path | Out-Null
}

function Invoke-Dev {
    Add-VsWhereToPath

    # Stop first: the service holds wintun.dll and the UI holds Core.dll, and a build that cannot
    # copy them fails with a locked-file error that says nothing about why.
    $null = Stop-Everything
    Start-Sleep -Milliseconds 500

    Say "Building"
    & dotnet build (Join-Path $client 'GamePingBooster.sln') --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    $svc = Get-ServiceExe
    if (-not (Test-Path $svc)) { throw "No service binary at $svc" }

    # The profiles the installer ships, copied beside that binary.
    #
    # The service reads them relative to its OWN directory, and a build output has none - so a
    # development run had no game ranges and no relay list at all, while an installed one had
    # both. On a self-hosted machine that is the whole configuration: the UI reported itself
    # unconfigured and asked for a relay and key that were already entered, and nothing said the
    # profile was what was missing. Copied rather than left to the build so the .iss stays the one
    # list of what ships.
    $profileDir = Join-Path (Split-Path $svc) 'profiles'
    $null = New-Item -ItemType Directory -Force -Path $profileDir
    foreach ($p in Get-ShippedProfileFiles) {
        if (Test-Path $p.Path) {
            Copy-Item $p.Path -Destination $profileDir -Force
        } else {
            # Expected on a fresh clone: the real profiles are gitignored. Not fatal - the rest of
            # the app is worth running without them - but it IS the reason Connect will refuse.
            Warn "No profiles\$($p.Name) in the tree. Run: .\gpb.ps1 release-profiles"
        }
    }
    Say "Profiles: $((Get-ChildItem $profileDir -Filter *.json | Measure-Object).Count) file(s) beside the service"

    # Wintun refuses to create an adapter for anything below LocalSystem - Administrator is not
    # enough - so the service has to be launched through psexec -s. This is also why the UI is
    # started separately and as a normal user: that split is the whole point of the architecture.
    if (-not (Get-Command psexec -ErrorAction SilentlyContinue)) {
        throw "psexec not found. It is needed to run the service as LocalSystem (Wintun requires it). Get it from Sysinternals and put it on PATH."
    }

    Say "Starting the service as LocalSystem"
    Start-Process psexec -ArgumentList @('-accepteula', '-s', '-i', "`"$svc`"", '--console') | Out-Null

    # Wait for the pipe rather than sleeping a guessed number of seconds.
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and -not (Test-Path '\\.\pipe\GamePingBooster')) {
        Start-Sleep -Milliseconds 300
    }
    if (Test-Path '\\.\pipe\GamePingBooster') {
        Say "Service is up, pipe listening" 'Green'
    } else {
        Warn "The pipe never appeared. Check the service window, or run: .\gpb.ps1 logs"
    }

    $app = Get-AppExe
    if (Test-Path $app) {
        Say "Starting the UI"
        Start-UnelevatedUi $app
    } else {
        Warn "No UI binary at $app"
    }

    Write-Host ""
    Write-Host "  Next:  .\gpb.ps1 capture      while you play"
    Write-Host "         .\gpb.ps1 check        during a live match"
    Write-Host "         .\gpb.ps1 logs         follow the service log"
}

function Invoke-RelayBuild {
    Say "Cross-compiling relayd for Linux"
    Push-Location $relayDir
    try {
        $env:CGO_ENABLED = '0'
        $env:GOOS = 'linux'
        # Stamped in so a relay can say which build it is. Only ever displayed - in the startup
        # log and in the status report the dashboard shows - but without it every relay reports
        # itself as "dev" and there is no telling which box is still running an old binary.
        $v = 'dev'
        $versionFile = Join-Path $root 'VERSION'
        if (Test-Path $versionFile) { $v = (Get-Content $versionFile -Raw).Trim() }
        & go build -ldflags "-X main.version=$v" -o relayd ./cmd/relayd
        if ($LASTEXITCODE -ne 0) { throw "go build failed" }
    } finally {
        Remove-Item Env:GOOS -ErrorAction SilentlyContinue
        Pop-Location
    }
    $out = Join-Path $relayDir 'relayd'
    Say "Built $out ($([math]::Round((Get-Item $out).Length / 1MB, 1)) MB)" 'Green'
}

function Show-RelaySetup {
    Write-Host @"

Setting up a relay, start to finish.

1. Declare it in gpb.conf. Copy the example and edit one block - host, and whichever of user,
   port, key or password your VPS needs. The name in the middle of the key is yours to pick and
   is what you type on the command line:

    copy gpb.conf.example gpb.conf

    RELAY_SG_HOST=203.0.113.10
    RELAY_SG_USER=root
    RELAY_SG_KEY=~/.ssh/id_ed25519
    RELAY_DEFAULT=sg

   gpb.conf is gitignored. Check what got read:

    .\gpb.ps1 relay list

2. If you have no key yet, make one and install it. Once per host, and the last time you type
   that password:

    ssh-keygen -t ed25519
    type `$env:USERPROFILE\.ssh\id_ed25519.pub | ssh root@203.0.113.10 "mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys"

   A password in RELAY_<NAME>_PASSWORD works instead, and costs one prompt-free deploy, but it
   sits in plain text on your disk. gpb.conf.example says more about that trade.

3. Deploy. This builds relayd, ships it in one connection and runs install.sh on the far end:

    .\gpb.ps1 relay deploy sg          (or just .\gpb.ps1 relay deploy, for RELAY_DEFAULT)
    .\gpb.ps1 relay logs sg

   An account that is not root needs sudo for the install step, which the deploy works out on
   the far end. If sudo wants a password it uses RELAY_<NAME>_SUDO_PASSWORD, or the login
   password when that is empty, and asks you on the terminal if there is neither.

4. install.sh prints the endpoint and the PSK. They go into two different files, because they
   are two different things:

    endpoint <ip>:51820  ->  profiles\<profile>.json, as an entry in "relays"
    PSK                  ->  client\config.json, as "psk"

   Neither belongs in gpb.conf: that file is only about reaching the VPS.

"@
}

function Show-RelayList {
    $names = Get-GpbRelayNames -RepoRoot $root
    if (-not $names) {
        Warn "gpb.conf declares no relays. Copy gpb.conf.example to gpb.conf and fill in one block."
        return
    }
    $conf = Read-GpbConf (Get-GpbConfPath $root)
    $default = $conf['RELAY_DEFAULT']
    $rows = foreach ($n in $names) {
        $r = Get-GpbRelay -Name $n -RepoRoot $root
        if ($r.Key) { $auth = 'key' } elseif ($r.Password) { $auth = 'password' } else { $auth = 'ssh decides' }
        $mark = ''
        if ($n -eq $default) { $mark = '*' }
        [pscustomobject]@{
            NAME              = "$n$mark"
            SSH               = "$($r.Target):$($r.Port)"
            'CLIENT ENDPOINT' = $r.Endpoint
            AUTH              = $auth
            MODE              = $r.Mode
            # Shown because this table is how you check what actually got parsed, and a cap that
            # silently read as 0 looks exactly like a relay with no cap configured.
            'MAX CLIENTS'     = $(if ([int]$r.MaxClients -gt 0) { $r.MaxClients } else { 'no limit' })
            # A relay that reports nowhere is invisible in the dashboard, which looks exactly
            # like a relay that has died. Worth seeing here, where it is one line to fix.
            'REPORTS TO'      = $(if ($r.ReportUrl) { $r.ReportUrl } else { '-' })
        }
    }
    $rows | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host "* = RELAY_DEFAULT, used when a command is given no name."
}

function Resolve-Relay($name) {
    $r = Get-GpbRelay -Name $name -RepoRoot $root
    if ($r) { return $r }
    Warn "Which relay? Name one, or set RELAY_DEFAULT in gpb.conf."
    $known = Get-GpbRelayNames -RepoRoot $root
    if ($known) {
        Warn ""
        Warn "Declared in gpb.conf: $($known -join ', ')"
    } else {
        Show-RelaySetup
    }
    return $null
}

function Invoke-RelayDeploy($target, $extra) {
    # A mode flag typed here is CHECKED against gpb.conf, never quietly ignored.
    #
    # `relay deploy hk --psk` used to be swallowed whole - the flag reached nothing, and the
    # deploy went out in whatever mode the far end already had. Where the flag agrees with the
    # declaration it is a no-op, which is what makes it safe to type; where it disagrees the
    # deploy stops, because the mode belongs to one place. gpb.conf is where the licence key, the
    # client cap and the report URL for this relay are declared, and a deploy that contradicted
    # it would be undone by the next one that did not.
    $wantMode = $null
    foreach ($a in $extra) {
        switch ($a) {
            '--psk' { $wantMode = 'psk' }
            '--token' { $wantMode = 'token' }
            default { throw "unknown option '$a'. Usage: .\gpb.ps1 relay deploy [name] [--psk|--token]" }
        }
    }

    $r = Resolve-Relay $target
    if (-not $r) { return }

    # Before the build, not after: a mode that does not match should cost a second, not a
    # cross-compile.
    if ($wantMode -and $wantMode -ne $r.Mode) {
        $slug = $r.Name.ToUpperInvariant()
        throw ("--$wantMode was asked for, but RELAY_${slug}_MODE says $($r.Mode)." +
            "`n    The mode is declared in gpb.conf, so that this deploy and the next one agree." +
            "`n    Set RELAY_${slug}_MODE=$wantMode there (a token relay also needs RELAY_${slug}_LICENCE_KEY) and run this again.")
    }
    Push-Location $relayDir
    try {
        # deploy.ps1 resolves the name from gpb.conf itself, so it stays usable on its own.
        & (Join-Path $relayDir 'deploy.ps1') -RemoteHost $r.Name
        if ($LASTEXITCODE -ne 0) { throw "deploy failed" }
    } finally { Pop-Location }
}

switch ($Verb.ToLowerInvariant()) {
    'dev' { Invoke-Dev }

    'stop' {
        if ((Stop-Everything) -eq 0) { Say "Nothing was running" 'DarkGray' }
    }

    'capture' {
        # ./gpb capture [game] [udp|tcp|all], and either may be left out.
        #
        # The protocol used to be the FIRST argument, and `./gpb capture tcp` is in people's
        # fingers and in the usage text of every older checkout. So a first argument that names a
        # protocol is still read as one, rather than being looked up as a game and failing. The
        # two vocabularies cannot collide: a game called udp, tcp or all is refused below.
        $protocols = @('udp', 'tcp', 'all')
        $gameName = $Arg1
        $protocol = $Arg2
        if ($Arg1 -and $protocols -contains $Arg1.ToLowerInvariant()) {
            $gameName = $null
            $protocol = $Arg1
        }

        $game = Get-GpbGame $root $gameName
        if ($protocols -contains $game.Id) {
            throw "games.json declares a game called '$($game.Id)', which is also a protocol name. Rename it."
        }

        # Every output file is passed, never left to the script's defaults: those are PUBG's file
        # names, and a default taken for another game writes that game's addresses into PUBG's.
        $captureArgs = @{
            WatchProcess  = $game.WatchProcess
            OutputPath    = $game.ObservedPath
            TcpOutputPath = $game.TcpSessionsPath
        }
        if ($protocol) { $captureArgs['Protocol'] = $protocol.ToLowerInvariant() }

        # A game with no datacentre-probe port collects no landmarks. Leaving -ProbePort out is not
        # the same thing: the script then falls back to PUBG's 8081 and PUBG's landmark file, and
        # anything this game sends on 8081 is filed as one of PUBG's probes. 0 switches it off.
        if ($null -ne $game.ProbePort) {
            $captureArgs['ProbePort'] = [int]$game.ProbePort
            if ($game.LandmarkPath) { $captureArgs['LandmarkPath'] = $game.LandmarkPath }
        } else {
            $captureArgs['ProbePort'] = 0
        }

        Say "Capturing $($game.Name) - watching $($game.WatchProcess).exe"
        Write-Host "    addresses -> $($game.ObservedPath)" -ForegroundColor DarkGray
        if ($null -eq $game.ProbePort) {
            Warn "$($game.Name) declares no probe port, so no landmarks are collected."
        }

        Push-Location $builder
        try { & (Join-Path $builder 'Capture-GameTraffic.ps1') @captureArgs } finally { Pop-Location }
    }

    'etw' {
        # ./gpb etw [game]: server discovery from ETW instead of a packet capture. A prototype, run
        # next to `./gpb capture` in the same match so the two can be compared - see the header of
        # client\src\GamePingBooster.EtwWatch\Program.cs. It writes only its own report file, never
        # the observed or landmark lists.
        $game = Get-GpbGame $root $Arg1

        $principal = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw "ETW network events need Administrator rights. Run ./gpb etw from an Administrator terminal."
        }

        $project = Join-Path $client 'src\GamePingBooster.EtwWatch\GamePingBooster.EtwWatch.csproj'
        Say "Building gpb-etwwatch"
        & dotnet build $project --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Build failed." }

        # The exe, not `dotnet run`: Ctrl+C has to reach the tool, which reports on the way out,
        # rather than the dotnet host in front of it.
        $exe = Join-Path $client 'src\GamePingBooster.EtwWatch\bin\Debug\net9.0-windows\gpb-etwwatch.exe'

        # Named observed-etw-* so the .gitignore glob that keeps capture output out of the
        # repository covers it too: it is the same kind of data.
        $report = Join-Path $builder "observed-etw-$($game.Id).txt"

        # Every path passed, as for capture - a default would be PUBG's.
        $probePort = 0
        if ($null -ne $game.ProbePort) { $probePort = [int]$game.ProbePort }
        $etwArgs = @(
            '--process', $game.WatchProcess,
            '--game-id', $game.Id,
            '--game-name', $game.Name,
            '--probe-port', $probePort,
            '--observed', $game.ObservedPath,
            '--profile', $game.ProfilePath,
            '--report', $report
        )
        if ($game.LandmarkPath) { $etwArgs += @('--landmarks', $game.LandmarkPath) }

        Say "Watching $($game.Name) through ETW - $($game.WatchProcess).exe"
        Write-Host "    report -> $report" -ForegroundColor DarkGray
        & $exe @etwArgs
        if ($LASTEXITCODE -ne 0) { throw "gpb-etwwatch exited with $LASTEXITCODE." }
    }

    'blockcheck' {
        # ./gpb blockcheck <label> [app]: which of the three ways a service can be blocked is happening here.
        #
        # The answer decides a whole feature. If it is DNS poisoning on every line, supporting Steam
        # is a resolver scoped to a handful of names and nothing else - no relay to pay for and no
        # download traffic to carry. If any line filters on SNI or drops the address, that part needs
        # the tunnel. See the header of client\src\GamePingBooster.BlockCheck\Program.cs.
        #
        # Steam does not need to be running, and restarting it proves nothing - the tool asks the
        # questions itself. What matters is that the line is untouched: it refuses to run with a VPN
        # or a DNS proxy up, because with one up every name comes back clean.
        $project = Join-Path $client 'src\GamePingBooster.BlockCheck\GamePingBooster.BlockCheck.csproj'
        Say "Building gpb-blockcheck"
        & dotnet build $project --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Build failed." }

        $exe = Join-Path $client 'src\GamePingBooster.BlockCheck\bin\Debug\net9.0-windows\gpb-blockcheck.exe'

        # Named blockcheck-* so the .gitignore glob that keeps measurement output out of the
        # repository covers it: it records this machine's resolvers, its ISP and every address
        # those resolvers named.
        $app = if ($Arg2) { $Arg2 } else { 'steam' }
        $report = Join-Path $root ("blockcheck-$app-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".json")

        # Two positional arguments, and they answer different questions. The label says WHICH LINE
        # this ran on - the ISP and the city - and is the one thing the tool cannot work out for
        # itself; a run without it is not comparable with the next one. The app says WHICH SERVICE
        # to probe, and comes from tools\blockcheck\targets.json, so adding one needs no build.
        # e.g. ./gpb blockcheck vnpt-hcm        (steam, the default)
        #      ./gpb blockcheck 4g-viettel-hcm steam
        $checkArgs = @('--json', $report)
        if ($Arg2) { $checkArgs = @($Arg2) + $checkArgs }
        if ($Arg1) { $checkArgs = @($Arg1) + $checkArgs }
        else { Warn "No label. Pass the ISP and city - e.g. vnpt-hcm - so this run can be compared with the others." }

        & $exe @checkArgs
        if ($LASTEXITCODE -eq 3) { throw "Refused: turn Cloudflare WARP or the VPN off and run it again." }
        if ($LASTEXITCODE -ne 0) { throw "gpb-blockcheck exited with $LASTEXITCODE." }
    }

    'dns' {
        # ./gpb dns: does the Steam name fix work on this machine?
        #
        # Two halves, and only one of them can be tested without rights. The resolver - the part
        # this project wrote - answers Steam's names over DoH and relays everything else to the
        # ISP, and anyone can run it. Pointing Windows at it writes a machine-wide name resolution
        # policy, which only SYSTEM may do, and a policy Windows quietly ignores looks exactly like
        # one that works. So the service proves it at runtime and rolls back if the proof fails;
        # this runs that same sequence with the output visible.
        #
        # It removes whatever it installs, on every path including the failing ones.
        $principal = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw "The name resolution policy can only be written by SYSTEM. Run ./gpb dns from an Administrator terminal."
        }

        # psexec for the same reason `dev` needs it: SYSTEM, not Administrator.
        if (-not (Get-Command psexec -ErrorAction SilentlyContinue)) {
            throw "psexec not found. It is needed to run the check as LocalSystem. Get it from Sysinternals and put it on PATH."
        }

        Say "Building gpb-service"
        & dotnet build (Join-Path $client 'src\GamePingBooster.Service\GamePingBooster.Service.csproj') --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Build failed." }

        $svc = Get-ServiceExe
        if (-not (Test-Path $svc)) { throw "No service binary at $svc" }

        # No -i: the output belongs in this window, not in one that closes when it finishes.
        Say "Running the Steam resolver check as LocalSystem"
        & psexec -accepteula -nobanner -s $svc --dns-selftest --policy
        if ($LASTEXITCODE -ne 0) { throw "The Steam resolver check reported a failure - read the lines above." }
    }

    'profile' {
        $game = Get-GpbGame $root $Arg1

        # Refused rather than run. With no cloud regions declared, every observed address fails
        # the builder's cross-check and the run finishes by writing a profile with no ranges in
        # it - which is worse than an error, because it looks like a finished profile and would
        # be pushed as one. games.json says per game what is still missing.
        if (-not (Test-GpbGameBuildable $game)) {
            $message = "$($game.Name) declares no published ranges in games.json - no AWS or Azure " +
                "region, no Global Accelerator, no ASN - so every observed address would fail the " +
                "cross-check and the profile would come out empty."
            if ($game.Note) { $message += "`n`n    $($game.Note)" }
            throw $message
        }

        # Other games' addresses are not passed from here: the builder reads games.json itself, so
        # a run that bypasses ./gpb is kept off them just the same.
        $profileArgs = @{
            GameId            = $game.Id
            GameName          = $game.Name
            ProcessNames      = @("$($game.WatchProcess).exe")
            ObservedIpPath    = $game.ObservedPath
            ProfilePath       = $game.ProfilePath
            ManualCidrPath    = $game.ManualCidrPath
            UnverifiedPath    = $game.UnverifiedPath
            AwsRegions        = $game.AwsRegions
            AzureRegions      = $game.AzureRegions
            GlobalAccelerator = $game.GlobalAccelerator
            Asns              = $game.Asns
        }
        if ($game.LandmarkPath) { $profileArgs['LandmarkObservedPath'] = $game.LandmarkPath }
        if ($null -ne $game.DiscoveryRate) { $profileArgs['DiscoveryPacketsPerSecond'] = $game.DiscoveryRate }

        Say "Building the $($game.Name) profile -> $($game.ProfilePath)"

        Push-Location $builder
        try { & (Join-Path $builder 'Build-PubgProfile.ps1') @profileArgs } finally { Pop-Location }
    }

    'push-profile' {
        # Its own file: every step exists to avoid one specific way of breaking the other games'
        # relays, and those reasons belong next to the steps.
        if (-not $Arg1) { throw "Which game? Usage: ./gpb push-profile <game> [remove]" }
        $pushArgs = @{ Game = $Arg1 }
        if ($Arg2) {
            if ($Arg2.ToLowerInvariant() -ne 'remove') { throw "unknown option '$Arg2'. Usage: ./gpb push-profile <game> [remove]" }
            $pushArgs['Remove'] = $true
        }
        & (Join-Path $tools 'Push-LocalProfile.ps1') @pushArgs
    }

    'check' {
        Push-Location $builder
        try { & (Join-Path $builder 'Test-GameRouting.ps1') } finally { Pop-Location }
    }

    'lag' {
        # Its own file rather than a block here, for the same reason as reset: the verdict rests
        # on an argument about which rung can be trusted, and that argument has to be written
        # down next to the code that makes it or it will be quietly optimised away.
        $seconds = 20
        if ($Arg1 -and [int]::TryParse($Arg1, [ref]$null)) { $seconds = [int]$Arg1 }
        & (Join-Path $tools 'Diagnose-Lag.ps1') -Seconds $seconds
    }

    'probe' {
        # The same file a player is sent on its own, so it cannot read gpb.conf itself: the relays
        # are handed to it from here. That keeps their addresses out of the repository, which is
        # the reason gpb.conf exists.
        $relays = @()
        foreach ($n in @(Get-GpbRelayNames -RepoRoot $root)) {
            $r = Get-GpbRelay -Name $n -RepoRoot $root
            if ($r.Endpoint) { $relays += ('{0}={1}' -f $n.ToLowerInvariant(), ($r.Endpoint -split ':')[0]) }
        }
        & (Join-Path $tools 'Probe-Providers.ps1') -Relays $relays
    }

    'logs' {
        $log = Join-Path $programData 'logs\gpb-service.log'
        if (-not (Test-Path $log)) { throw "No log at $log - has the service ever run?" }
        Say "Following $log  (Ctrl+C to stop)"
        Get-Content $log -Tail 30 -Wait
    }

    'status' {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'GamePingBooster', [System.IO.Pipes.PipeDirection]::InOut)
        try { $pipe.Connect(3000) } catch { throw "Could not reach the service. Is it running? Try .\gpb.ps1 dev" }
        $reader = New-Object System.IO.StreamReader($pipe)
        $writer = New-Object System.IO.StreamWriter($pipe); $writer.AutoFlush = $true
        $writer.WriteLine('{"v":2,"verb":"status"}')
        $task = $reader.ReadLineAsync()
        if ($task.Wait(3000) -and $task.Result) {
            $s = $task.Result | ConvertFrom-Json
            "{0,-13} sent={1} recv={2} dropped={3} ping={4}ms loss={5} routes={6}" -f `
                $s.state, $s.packetsSent, $s.packetsReceived, $s.packetsDropped, `
                [math]::Round([double]$s.tunnelPingMs), $s.lossRatio, $s.activeRoutes
            "  $($s.detail)"
        } else { Warn "The service did not answer - the UI may be holding the pipe." }
        $reader.Dispose(); $pipe.Dispose()
    }

    'test' {
        Add-VsWhereToPath
        Say "Go: vet, format and tests"
        Push-Location $relayDir
        try {
            $fmt = & gofmt -l .
            if ($fmt) { throw "gofmt would change: $fmt" }
            & go vet ./...; if ($LASTEXITCODE -ne 0) { throw "go vet failed" }
            # Go's test cache does not notice that testdata/protocol-vectors.json changed: it is
            # outside the package directory, so it is not one of the inputs the cache is keyed on.
            # Measured, not assumed - a tampered vector file was reported as a cached pass while the
            # same run with -count=1 failed. Since the whole point of that file is to catch drift
            # between the Go and C# implementations, a cached pass is the exact failure it exists to
            # prevent. The full suite takes about six seconds cold, so always re-running is cheap.
            & go test -count=1 ./...; if ($LASTEXITCODE -ne 0) { throw "go tests failed" }
        } finally { Pop-Location }

        # The deploy tooling has no compiler to catch it: the mode a relay is installed in comes
        # out of a string built in two places, and getting it wrong takes a fleet offline quietly.
        # The test itself is a shell script because half of what it drives is one.
        #
        $gitBash = Get-GitBash
        if ($gitBash) {
            Say "Relay deploy: the declared mode is the mode installed"
            & $gitBash ((Join-Path $tools 'test-relay-deploy-mode.sh') -replace '\\', '/')
            if ($LASTEXITCODE -ne 0) { throw "the relay deploy mode test failed" }
        } else {
            Warn "Git's bash not found - skipped tools\test-relay-deploy-mode.sh"
        }

        # The relay an entry forwards to is sticky on purpose, so a wrong choice would stay wrong.
        if ($gitBash) {
            Say "Entry deploy: the relay choice and what goes over ssh"
            & $gitBash ((Join-Path $tools 'test-entry-deploy.sh') -replace '\\', '/')
            if ($LASTEXITCODE -ne 0) { throw "the entry deploy test failed" }
        } else {
            Warn "Git's bash not found - skipped tools\test-entry-deploy.sh"
        }

        # Pure logic, no network and no service: it drives the lag verdict with synthetic
        # samples, which is the only way to reach its branches. A healthy connection reaches one.
        Say "Lag diagnosis: the verdict rules"
        & (Join-Path $tools 'Test-DiagnoseLag.ps1')
        if ($LASTEXITCODE -ne 0) { throw "the lag diagnosis tests failed" }

        Say "Provider probe: city names and the verdict"
        & (Join-Path $tools 'Test-ProbeProviders.ps1')
        if ($LASTEXITCODE -ne 0) { throw "the provider probe tests failed" }

        Say "C#: build and wire-format check"
        & dotnet build (Join-Path $client 'GamePingBooster.sln') --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "C# build failed" }
        & dotnet run --project (Join-Path $client 'src\GamePingBooster.ProtocolCheck\GamePingBooster.ProtocolCheck.csproj')
        if ($LASTEXITCODE -ne 0) { throw "the C# client and the Go relay disagree on the wire format" }

        # Pure logic, like the lag diagnosis above: synthetic quarter seconds are the only way to
        # reach the spike verdicts, since a healthy connection only ever produces "no spike".
        Say "C#: spike detector verdicts"
        & dotnet run --project (Join-Path $client 'src\GamePingBooster.QualityCheck\GamePingBooster.QualityCheck.csproj')
        if ($LASTEXITCODE -ne 0) { throw "the spike detector checks failed" }

        # Multi-tunnel's pure core, before any of it carries a packet: the inner-address NAT checked by full
        # checksum recomputation over random packets, the region table against a brute-force scan, and the
        # planner's guarantees over random plans. See docs/MULTI-TUNNEL.md, section 10.1.
        Say "C#: multi-tunnel paths - NAT, region table, sticky destinations, planner"
        & dotnet run --project (Join-Path $client 'src\GamePingBooster.PathCheck\GamePingBooster.PathCheck.csproj')
        if ($LASTEXITCODE -ne 0) { throw "the multi-tunnel path checks failed" }

        # The packet path itself - AdapterPump and TunnelClient - driven in-process through a fake adapter
        # against a fake relay on loopback that keeps relayd's rules. No driver, no VPS, no Administrator.
        # See docs/MULTI-TUNNEL.md, section 10.2.
        Say "C#: the packet path against a fake relay"
        & dotnet run --project (Join-Path $client 'src\GamePingBooster.TunnelCheck\GamePingBooster.TunnelCheck.csproj')
        if ($LASTEXITCODE -ne 0) { throw "the packet path checks failed" }

        Say "Everything passed" 'Green'
    }

    'release' {
        # Implemented once, in ./gpb, and only reached from here. A release is git and nothing
        # Windows-specific, and two copies of a sequence that ends in pushing a tag that can never
        # be taken back would be two chances to push the wrong one.
        $gitBash = Get-GitBash
        if (-not $gitBash) { throw "Git's bash not found. Install Git for Windows, or run ./gpb release from Git Bash." }
        & $gitBash ((Join-Path $root 'gpb') -replace '\\', '/') release $Arg1
        exit $LASTEXITCODE
    }

    'publish' {
        $null = Stop-Everything
        Start-Sleep -Milliseconds 500
        Invoke-AotPublish

        $bin = Join-Path $programData 'bin'
        New-Item -ItemType Directory -Force -Path $bin | Out-Null
        $svcPub = Join-Path $client 'src\GamePingBooster.Service\bin\Release\net9.0-windows\win-x64\publish'
        $appPub = Join-Path $client 'src\GamePingBooster.App\bin\Release\net9.0-windows\win-x64\publish'
        Get-ChildItem $svcPub, $appPub -File | Where-Object { $_.Extension -ne '.pdb' } |
            ForEach-Object { Copy-Item $_.FullName $bin -Force }
        Say "Installed into $bin" 'Green'
        Warn "If the service is registered, restart it (needs Administrator):  sc.exe stop GamePingBooster; sc.exe start GamePingBooster"
    }

    'diag' {
        & (Join-Path $tools 'Collect-Diagnostics.ps1')
    }

    'version' {
        if ($Arg1) {
            $v = Set-GpbVersion $Arg1
            Say "Version set to $v" 'Green'
            Warn "Nothing is rebuilt. Run .\gpb.ps1 installer to stamp it into the binaries"
            Warn "and the setup .exe - a VERSION the build has not seen yet is just a file."
        } else {
            Write-Host (Get-GpbVersion)
        }
    }

    'reset' {
        # Its own file rather than a block here, because it is the only verb that deletes things
        # and it needs room to say why for each one.
        #
        # The switches arrive as strings in $Rest and are turned back into a splat rather than
        # forwarded as an array: passing @('-DryRun') as arguments would make PowerShell bind it
        # positionally to a script that has no positional parameters, so the flag would be
        # accepted and then silently ignored. An unknown one is refused here, by name, instead of
        # producing a parameter-binding error against a file the user did not run.
        $known = 'DryRun', 'Yes', 'KeepIdentity', 'KeepDriver', 'UseUninstaller', 'Force'
        $switches = @{}
        foreach ($a in @($Arg1, $Arg2) + @($Rest)) {
            if (-not $a) { continue }
            $match = $known | Where-Object { $_ -eq $a.TrimStart('-') }
            if (-not $match) { throw "Unknown option '$a'. reset takes: -$($known -join ' -')" }
            $switches[$match] = $true
        }
        & (Join-Path $tools 'Reset-Machine.ps1') @switches
    }

    'installer' {
        # Inno Setup, not WiX: WiX v7 refuses to run until its Open Source Maintenance Fee EULA
        # is accepted, which is a licensing commitment for a commercial product. Inno is free for
        # commercial use. See installer/GamePingBooster.iss.
        $iscc = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1

        if (-not $iscc) {
            throw "Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php - " +
                  "the default location is fine, this looks in Program Files."
        }

        # The version is set BEFORE publishing, not after, because the binaries carry it too:
        # Directory.Build.props reads VERSION at compile time. Setting it afterwards would
        # produce a setup .exe named 0.2.0 full of 0.1.0 binaries, which is the exact failure
        # this whole arrangement exists to prevent.
        if ($Arg1) {
            $version = Set-GpbVersion $Arg1
            Say "Version set to $version (written to VERSION)"
        } else {
            $version = Get-GpbVersion
            Say "Version $version - pass one to change it: .\gpb.ps1 installer 0.2.0"
        }

        # Publish first. Packaging whatever happens to be lying in the publish folder is how an
        # installer ends up shipping last week's binary, and nothing about the result would say
        # so.
        # The build only - not `publish`, which also stops the running service and installs into
        # ProgramData. See Invoke-AotPublish.
        Say "Building before packaging"
        Invoke-AotPublish

        # Refuse early and name the missing file. Inno's own error for a missing source is a
        # line number in a .iss most people will never have read.
        $required = @{
            'the service'  = Join-Path $client 'src\GamePingBooster.Service\bin\Release\net9.0-windows\win-x64\publish\gpb-service.exe'
            'the UI'       = Join-Path $client 'src\GamePingBooster.App\bin\Release\net9.0-windows\win-x64\publish\GamePingBooster.exe'
            'wintun.dll'   = Join-Path $client 'native\wintun\wintun.dll'
            # Avalonia's renderer. Native AOT leaves it beside the binary rather than inside it,
            # and an installer that shipped without it produced an app that crashed on launch
            # with a TypeInitializationException naming neither the file nor the installer.
            'libSkiaSharp' = Join-Path $client 'src\GamePingBooster.App\bin\Release\net9.0-windows\win-x64\publish\libSkiaSharp.dll'
        }
        # Every profile the installer ships, read from the .iss itself rather than listed again here,
        # so a game added there is checked here without anyone remembering to.
        foreach ($p in Get-ShippedProfileFiles) {
            $required["the profile $($p.Name)"] = $p.Path
        }
        foreach ($what in $required.Keys) {
            if (-not (Test-Path $required[$what])) {
                throw "Cannot package: $what is missing at $($required[$what])"
            }
        }

        Say "Building the installer"
        # /D overrides the #ifndef fallback in the .iss. One string, three places it has to
        # appear: the setup filename, AppVersion, and the Programs and Features entry.
        # Two defines, not one: AppVersion is free text and keeps any -suffix, while a Windows
        # version resource is four numbers and nothing else. Splitting here rather than in the
        # .iss because Inno's preprocessor has no string split worth reading.
        $numeric = ($version -split '-')[0]
        & $iscc "/DAppVersion=$version" "/DAppVersionNumeric=$numeric" (Join-Path $root 'installer\GamePingBooster.iss')
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

        $out = Join-Path $root 'installer\dist'
        Say "Installer written to $out" 'Green'
        Say "  GamePingBooster-Setup-$version.exe" 'Green'
        Warn "It is NOT code signed. Windows SmartScreen will warn every person who runs it,"
        Warn "and many will stop there. Signing needs a certificate you have to buy."
    }

    'entry' {
        # One implementation, in ./gpb. An entry deploy is ssh, a shell script and some iptables rules
        # on the far end - nothing Windows adds to - so Git's bash runs exactly the code a Linux or
        # macOS machine does, instead of a second copy here that would have to be kept in step by
        # hand the way deploy.ps1 and ./gpb are.
        $gitBash = Get-GitBash
        if (-not $gitBash) {
            throw "Git's bash not found. The entry verbs run through ./gpb - install Git for Windows, or run ./gpb entry from any shell."
        }
        $gpbArgs = @(((Join-Path $root 'gpb') -replace '\\', '/'), 'entry')
        foreach ($a in @($Arg1, $Arg2) + @($Rest)) { if ($a) { $gpbArgs += $a } }
        & $gitBash @gpbArgs
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    'relay' {
        # No null-coalescing here: PowerShell 5.1 has no ?? operator, and this repo targets 5.1.
        $sub = ''
        if ($Arg1) { $sub = $Arg1.ToLowerInvariant() }
        switch ($sub) {
            'build' { Invoke-RelayBuild }
            'deploy' { Invoke-RelayDeploy $Arg2 $Rest }
            'setup' { Show-RelaySetup }
            'list' { Show-RelayList }
            'logs' {
                # $host2, not $host: $Host is the PowerShell host object and shadowing it in a
                # script that also writes to the console is a debugging session nobody wants.
                $host2 = Resolve-Relay $Arg2
                if (-not $host2) { break }
                # The same password handling as a deploy: without it, a host that authenticates
                # by password would prompt here but not there, which reads as a broken command
                # rather than as a difference between two code paths.
                $askpass = Enable-GpbAskpass -Password $host2.Password
                try {
                    & ssh @($host2.SshArgs) 'journalctl -u relayd -f'
                } finally {
                    Disable-GpbAskpass -Helper $askpass
                }
            }
            'test' {
                Push-Location $relayDir
                try { & go test -count=1 ./... } finally { Pop-Location }
            }
            default {
                Write-Host "  relay build            cross-compile relayd for Linux"
                Write-Host "  relay list             show the relays gpb.conf declares"
                Write-Host "  relay deploy [name]    build, upload and install; the mode comes from"
                Write-Host "                         gpb.conf, and --psk or --token asserts it"
                Write-Host "  relay logs [name]      follow journalctl"
                Write-Host "  relay test             Go tests"
                Write-Host "  relay setup            first-time setup, explained"
            }
        }
    }

    'release-profiles' {
        # What a release build packages in place of the real profiles. profiles\<game>-vn.json hold
        # live address ranges and are gitignored, so a CI checkout never has them; the committed
        # <game>-vn.example.json stands in for each one that is missing.
        #
        # One implementation, called by .github/workflows/release.yml AND by ./gpb release's
        # rehearsal, so the rehearsal cannot pass on logic the real build does not run.
        #
        # The list is READ from installer\GamePingBooster.iss, never typed. It used to be typed: v0.2.3
        # was lost to a profile missing from it, and v0.2.6 to VALORANT being added to the installer
        # and not to the list. A missing profile does not fail here by itself - it fails in Inno Setup
        # as "Compile aborted" - so a game with no example fails HERE, by name.
        $iss = Join-Path $root 'installer\GamePingBooster.iss'
        $games = Select-String -Path $iss -Pattern 'profiles\\([a-z0-9-]+)-vn\.json' -AllMatches |
            ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
        if (-not $games) {
            throw "Found no profiles\<game>-vn.json in $iss - has the Source line changed shape?"
        }
        foreach ($game in $games) {
            $real = Join-Path $root "profiles\$game-vn.json"
            $example = Join-Path $root "profiles\$game-vn.example.json"
            if (Test-Path $real) {
                Say "profiles\$game-vn.json present"
                continue
            }
            if (-not (Test-Path $example)) {
                throw ("The installer ships profiles\$game-vn.json, which is gitignored, and there is no " +
                       "profiles\$game-vn.example.json committed to stand in for it. Add one - empty cidrs is fine.")
            }
            Warn "profiles\$game-vn.json not present (expected - it is gitignored). Using the example instead."
            Copy-Item $example $real
        }
    }

    default {
        Get-Help $PSCommandPath -Detailed
    }
}
