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
        .\gpb.ps1 profile [game]      rebuild that game's profile from what was captured
        .\gpb.ps1 check               is the game actually going through the relay right now
        .\gpb.ps1 lag [seconds]       run this DURING the lag: which segment is at fault
        .\gpb.ps1 logs                follow the service log
        .\gpb.ps1 status              read the tunnel's live counters
        .\gpb.ps1 test                every test on both sides
        .\gpb.ps1 publish             Native AOT build and install into ProgramData
        .\gpb.ps1 diag                collect a diagnostics bundle to send
        .\gpb.ps1 installer [version] publish, then package a setup .exe (needs Inno Setup 6)
        .\gpb.ps1 version [x.y.z]     show or set the version everything is stamped with
        .\gpb.ps1 reset               remove EVERYTHING this software installed, to test setup

        .\gpb.ps1 relay build         cross-compile relayd for Linux
        .\gpb.ps1 relay list          show the relays gpb.conf declares
        .\gpb.ps1 relay deploy [name] build, upload and install on a relay
                                      the mode comes from gpb.conf; --psk or --token asserts it
        .\gpb.ps1 relay logs [name]   follow journalctl on a relay
        .\gpb.ps1 relay test          Go tests only

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

        $captureArgs = @{
            WatchProcess = $game.WatchProcess
            OutputPath   = $game.ObservedPath
        }
        if ($protocol) { $captureArgs['Protocol'] = $protocol.ToLowerInvariant() }

        # A game with no datacentre-probe port collects no landmarks. Passing the PUBG default
        # would fill its landmark file with whatever else happens to use 8081, and the builder
        # would then treat that as the set of endpoints the game picks a region from.
        if ($null -ne $game.ProbePort) {
            $captureArgs['ProbePort'] = [int]$game.ProbePort
            if ($game.LandmarkPath) { $captureArgs['LandmarkPath'] = $game.LandmarkPath }
        }

        Say "Capturing $($game.Name) - watching $($game.WatchProcess).exe"
        Write-Host "    addresses -> $($game.ObservedPath)" -ForegroundColor DarkGray
        if ($null -eq $game.ProbePort) {
            Warn "$($game.Name) declares no probe port, so no landmarks are collected."
        }

        Push-Location $builder
        try { & (Join-Path $builder 'Capture-GameTraffic.ps1') @captureArgs } finally { Pop-Location }
    }

    'profile' {
        $game = Get-GpbGame $root $Arg1

        # Refused rather than run. With no cloud regions declared, every observed address fails
        # the builder's cross-check and the run finishes by writing a profile with no ranges in
        # it - which is worse than an error, because it looks like a finished profile and would
        # be pushed as one. games.json says per game what is still missing.
        if (-not (Test-GpbGameBuildable $game)) {
            $message = "$($game.Name) has no AWS or Azure regions declared in games.json, so every " +
                "observed address would fail the cross-check and the profile would come out empty."
            if ($game.Note) { $message += "`n`n    $($game.Note)" }
            throw $message
        }

        $profileArgs = @{
            GameId         = $game.Id
            ObservedIpPath = $game.ObservedPath
            ProfilePath    = $game.ProfilePath
            ManualCidrPath = $game.ManualCidrPath
            AwsRegions     = $game.AwsRegions
            AzureRegions   = $game.AzureRegions
        }
        if ($game.LandmarkPath) { $profileArgs['LandmarkObservedPath'] = $game.LandmarkPath }

        Say "Building the $($game.Name) profile -> $($game.ProfilePath)"

        Push-Location $builder
        try { & (Join-Path $builder 'Build-PubgProfile.ps1') @profileArgs } finally { Pop-Location }
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

        # Pure logic, no network and no service: it drives the lag verdict with synthetic
        # samples, which is the only way to reach its branches. A healthy connection reaches one.
        Say "Lag diagnosis: the verdict rules"
        & (Join-Path $tools 'Test-DiagnoseLag.ps1')
        if ($LASTEXITCODE -ne 0) { throw "the lag diagnosis tests failed" }

        Say "C#: build and wire-format check"
        & dotnet build (Join-Path $client 'GamePingBooster.sln') --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "C# build failed" }
        & dotnet run --project (Join-Path $client 'src\GamePingBooster.ProtocolCheck\GamePingBooster.ProtocolCheck.csproj')
        if ($LASTEXITCODE -ne 0) { throw "the C# client and the Go relay disagree on the wire format" }

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
        Add-VsWhereToPath
        $null = Stop-Everything
        Start-Sleep -Milliseconds 500
        Say "Native AOT publish"
        & dotnet publish (Join-Path $client 'src\GamePingBooster.Service\GamePingBooster.Service.csproj') -c Release -r win-x64 --nologo
        if ($LASTEXITCODE -ne 0) { throw "publish failed" }
        & dotnet publish (Join-Path $client 'src\GamePingBooster.App\GamePingBooster.App.csproj') -c Release -r win-x64 --nologo
        if ($LASTEXITCODE -ne 0) { throw "publish failed" }

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
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe"
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
        Say "Publishing before packaging"
        & $PSCommandPath publish
        if ($LASTEXITCODE -ne 0) { throw "publish failed" }

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
        foreach ($what in $required.Keys) {
            if (-not (Test-Path $required[$what])) {
                throw "Cannot package: $what is missing at $($required[$what])"
            }
        }

        $prodProfiles = @(Get-ChildItem (Join-Path $root 'profiles') -Filter "*.json" | Where-Object { $_.Name -notlike "*.example.json" })
        if ($prodProfiles.Count -eq 0) {
            throw "Cannot package: no production profiles found in profiles/ (excluding *.example.json)"
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

    default {
        Get-Help $PSCommandPath -Detailed
    }
}
