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
        .\gpb.ps1 capture             watch for the game and collect server addresses
        .\gpb.ps1 profile             rebuild the profile from what was captured
        .\gpb.ps1 check               is the game actually going through the relay right now
        .\gpb.ps1 logs                follow the service log
        .\gpb.ps1 status              read the tunnel's live counters
        .\gpb.ps1 test                every test on both sides
        .\gpb.ps1 publish             Native AOT build and install into ProgramData
        .\gpb.ps1 diag                collect a diagnostics bundle to send

        .\gpb.ps1 relay build         cross-compile relayd for Linux
        .\gpb.ps1 relay list          show the relays gpb.conf declares
        .\gpb.ps1 relay deploy [name] build, upload and install on a relay
        .\gpb.ps1 relay logs [name]   follow journalctl on a relay
        .\gpb.ps1 relay test          Go tests only

    Relays are declared in gpb.conf - host, port, user, key or password, one block each. Copy
    gpb.conf.example to gpb.conf and fill it in; see `.\gpb.ps1 relay setup`.

.EXAMPLE
    .\gpb.ps1 dev
    The usual starting point: everything built and running.
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Verb = 'help',
    [Parameter(Position = 1)][string]$Arg1,
    [Parameter(Position = 2)][string]$Arg2,
    [switch]$Release
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

function Get-ServiceExe {
    param([switch]$Published)
    if ($Published) { return Join-Path $programData 'bin\gpb-service.exe' }
    return Join-Path $client 'src\GamePingBooster.Service\bin\Debug\net9.0-windows\win-x64\gpb-service.exe'
}

function Get-AppExe {
    Join-Path $client 'src\GamePingBooster.App\bin\Debug\net9.0-windows\win-x64\GamePingBooster.exe'
}

function Stop-Everything {
    $stopped = @()
    foreach ($name in 'GamePingBooster', 'gpb-service') {
        $procs = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
        foreach ($p in $procs) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction Stop; $stopped += "$name($($p.Id))" } catch { }
        }
    }
    if ($stopped.Count -gt 0) { Say "Stopped: $($stopped -join ', ')" 'DarkGray' }
    return $stopped.Count
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
        Start-Process $app | Out-Null
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
        & go build -o relayd ./cmd/relayd
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

function Invoke-RelayDeploy($target) {
    $r = Resolve-Relay $target
    if (-not $r) { return }
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
        Push-Location $builder
        try { & (Join-Path $builder 'Capture-GameTraffic.ps1') } finally { Pop-Location }
    }

    'profile' {
        Push-Location $builder
        try { & (Join-Path $builder 'Build-PubgProfile.ps1') } finally { Pop-Location }
    }

    'check' {
        Push-Location $builder
        try { & (Join-Path $builder 'Test-GameRouting.ps1') } finally { Pop-Location }
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
            & go test ./...; if ($LASTEXITCODE -ne 0) { throw "go tests failed" }
        } finally { Pop-Location }

        Say "C#: build and wire-format check"
        & dotnet build (Join-Path $client 'GamePingBooster.sln') --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "C# build failed" }
        & dotnet run --project (Join-Path $client 'src\GamePingBooster.ProtocolCheck\GamePingBooster.ProtocolCheck.csproj')
        if ($LASTEXITCODE -ne 0) { throw "the C# client and the Go relay disagree on the wire format" }

        Say "Everything passed" 'Green'
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

    'relay' {
        # No null-coalescing here: PowerShell 5.1 has no ?? operator, and this repo targets 5.1.
        $sub = ''
        if ($Arg1) { $sub = $Arg1.ToLowerInvariant() }
        switch ($sub) {
            'build' { Invoke-RelayBuild }
            'deploy' { Invoke-RelayDeploy $Arg2 }
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
                try { & go test ./... } finally { Pop-Location }
            }
            default {
                Write-Host "  relay build            cross-compile relayd for Linux"
                Write-Host "  relay list             show the relays gpb.conf declares"
                Write-Host "  relay deploy [name]    build, upload and install"
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
