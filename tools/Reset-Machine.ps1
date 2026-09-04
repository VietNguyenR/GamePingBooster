<#
.SYNOPSIS
    Puts this machine back to the state it was in before Game Ping Booster was ever installed,
    so the installer can be tested properly without a second PC.

.DESCRIPTION
    The installer's job is to work on a machine that has never seen this software. A development
    machine is the opposite of that: it already has the service registered, the Wintun driver in
    the driver store, a device key, a licence token, a sealed profile and, quite often, a pinned
    relay route left behind by a session that did not shut down cleanly. Reinstalling over all of
    that tests almost none of the steps that matter, and the ones it does test it tests wrongly -
    "install the driver" on a machine that already has the driver is not a test of anything.

    This removes every trace, in the order that works, and then prints what is left so the result
    can be checked rather than assumed.

    It is deliberately NOT the uninstaller. The uninstaller keeps %ProgramData% on purpose, so a
    reinstall does not mean typing the key in again; this throws that away too, which is the
    whole point. Use -UseUninstaller to run the real uninstaller FIRST and then sweep up, which
    tests both halves in one go.

    WHAT IT COSTS. Deleting device.key makes this machine a NEW DEVICE to the licence server the
    next time it signs in, and that spends one of the account's device slots - the old
    registration does not come back by itself, it has to be removed from the dashboard. Every
    file that carries an identity is copied into a backup folder first and the path is printed,
    so the decision is reversible; -KeepIdentity skips the deletion entirely.

.PARAMETER Yes
    Do not ask. Intended for a second and third run once the first has been read.

.PARAMETER DryRun
    Show every action without performing any of it. Nothing is written, nothing is deleted.

.PARAMETER KeepIdentity
    Leave device.key, the licence token and the saved sign-in in place. The machine keeps its
    identity with the licence server and does not spend a device slot, at the cost of not being
    a clean machine in the one way a real new PC always is.

.PARAMETER KeepDriver
    Leave the Wintun driver in the driver store. Use this if something else on this machine uses
    Wintun - WireGuard does - and you would rather not find out the hard way.

.PARAMETER UseUninstaller
    Run the installed uninstaller first, if there is one, before sweeping up by hand. This is how
    to test that the uninstaller itself works; the sweep afterwards then reports what it missed.

.PARAMETER Force
    Remove the Wintun driver with pnputil when the supported route fails. Read the warning it
    prints before using this: pnputil removes the driver package itself, which is shared with any
    other Wintun user on the machine.

.EXAMPLE
    .\gpb.ps1 reset -DryRun
    Read what it would do before letting it do anything.

.EXAMPLE
    .\gpb.ps1 reset -UseUninstaller
    Test the uninstaller and then clean up whatever it left behind.
#>

[CmdletBinding()]
param(
    [switch]$Yes,
    [switch]$DryRun,
    [switch]$KeepIdentity,
    [switch]$KeepDriver,
    [switch]$UseUninstaller,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# tools\ lives one level under the repository root.
$root = Split-Path -Parent $PSScriptRoot

# The names are duplicated from installer\GamePingBooster.iss rather than parsed out of it. A
# parser for a .iss is a second thing to get wrong, and these five strings have not changed since
# the installer was written. If they ever do, they have to change here as well - which is why
# they are named constants at the top and not buried in the middle of a command line.
$ServiceName  = 'GamePingBooster'
$AppName      = 'Game Ping Booster'
$UiProcess    = 'GamePingBooster'
$SvcProcess   = 'gpb-service'
$InnoAppId    = '{6E7A4C21-8D3F-4B62-9E15-2C4A7F9D0B83}_is1'

function Say($msg, $colour = 'Cyan') { Write-Host "==> $msg" -ForegroundColor $colour }
function Note($msg) { Write-Host "    $msg" -ForegroundColor DarkGray }
function Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }
function Good($msg) { Write-Host "    $msg" -ForegroundColor Green }

# Every mutation goes through this, so -DryRun is one decision made in one place rather than an
# if around each of thirty commands - which is how a dry run ends up deleting something.
function Step($what, [scriptblock]$action) {
    if ($DryRun) {
        Write-Host "    would: $what" -ForegroundColor DarkCyan
        return
    }
    Write-Host "    $what"
    & $action
}

function Test-Elevated {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

<#
.SYNOPSIS
    'missing', 'running', 'stopped', 'stop_pending', or 'unknown'.
.DESCRIPTION
    sc.exe exits 1060 when the service does not exist. Everything else has to come out of the
    STATE line, because sc's exit code is 0 for a service in any state at all - including one
    that is marked for deletion and still there.
#>
function Get-ServiceStatus {
    param([string]$Name)

    $out = & sc.exe query $Name
    if ($LASTEXITCODE -eq 1060) { return 'missing' }
    if ($LASTEXITCODE -ne 0) { return 'unknown' }
    foreach ($line in $out) {
        if ($line -match 'STATE\s*:\s*\d+\s+(\w+)') { return $Matches[1].ToLowerInvariant() }
    }
    return 'unknown'
}

function Wait-ServiceStatus {
    param([string]$Name, [string[]]$Until, [int]$TimeoutMs = 15000)

    $waited = 0
    while ($waited -lt $TimeoutMs) {
        if ($Until -contains (Get-ServiceStatus $Name)) { return $true }
        Start-Sleep -Milliseconds 500
        $waited = $waited + 500
    }
    return ($Until -contains (Get-ServiceStatus $Name))
}

# The relay addresses this machine could have pinned a /32 for.
#
# Two sources, because they answer different halves of the question: gpb.conf names the relays
# this machine deploys to, and the local profile names the ones it CONNECTS to, which is what a
# pin is actually made from. Anything that fails to resolve is skipped rather than fatal - the
# script must still clean up when the network is down.
function Get-KnownRelayAddress {
    $addresses = @()

    $profilePaths = @(
        (Join-Path $root 'profiles\pubg-vn.json'),
        (Join-Path $root 'profiles\pubg-vn.example.json')
    )
    foreach ($path in $profilePaths) {
        if (-not (Test-Path $path)) { continue }
        try {
            # NOT $profile: that is an automatic variable holding the path to the user's
            # PowerShell profile, and assigning to it works right up until it does not.
            $parsed = Get-Content $path -Raw | ConvertFrom-Json
            foreach ($relay in @($parsed.relays)) {
                if (-not $relay.endpoint) { continue }
                $addresses += ($relay.endpoint -split ':')[0]
            }
        } catch {
            Note "Could not read $path ($($_.Exception.Message)) - skipping it."
        }
    }

    try {
        . (Join-Path $root 'tools\GpbConf.ps1')
        foreach ($name in (Get-GpbRelayNames -RepoRoot $root)) {
            $relay = Get-GpbRelay -Name $name -RepoRoot $root
            if ($relay -and $relay.Target) { $addresses += ($relay.Target -replace '^.*@', '') }
        }
    } catch {
        Note "No gpb.conf to read relay hosts from - using the profile only."
    }

    # Names have to become addresses: a route is pinned to an address, and the profile may carry
    # either. A name that does not resolve is dropped, not guessed at.
    $resolved = @()
    foreach ($address in ($addresses | Sort-Object -Unique)) {
        if ($address -match '^\d+\.\d+\.\d+\.\d+$') {
            $resolved += $address
            continue
        }
        try {
            $hits = [System.Net.Dns]::GetHostAddresses($address)
            foreach ($hit in $hits) {
                if ($hit.AddressFamily -eq 'InterNetwork') { $resolved += $hit.IPAddressToString }
            }
        } catch {
            Note "Could not resolve '$address' - any route pinned for it must be removed by hand."
        }
    }
    return ($resolved | Sort-Object -Unique)
}

function Get-WintunAdapter {
    return @(Get-NetAdapter -ErrorAction SilentlyContinue |
        Where-Object { $_.InterfaceDescription -like '*Wintun*' -or $_.DriverDescription -like '*Wintun*' })
}

# ---------------------------------------------------------------- inventory

# A dry run only reads, so it is allowed from an ordinary shell - which is the shell somebody
# will be sitting in when they want to know what this would do. Refusing there would mean
# opening an Administrator terminal to be told nothing happens.
if (-not $DryRun -and -not (Test-Elevated)) {
    throw "This needs an Administrator terminal. It stops a LocalSystem service, removes a " +
          "network driver and deletes files under ProgramData, and a normal-user shell can do " +
          "none of those - it would half-succeed and leave the machine in a state that is " +
          "neither clean nor working. Run it with -DryRun from here to see what it would do."
}

$programData     = Join-Path $env:ProgramData 'GamePingBooster'
$programDataAlt  = Join-Path $env:ProgramData $AppName
$localAppData    = Join-Path $env:LOCALAPPDATA 'GamePingBooster'
# ProgramW6432 first: it is always the 64-bit Program Files, whereas $env:ProgramFiles
# resolves to the x86 directory when this runs under 32-bit PowerShell - and the installer is
# x64-only, so that would look at a folder setup never touches.
$programFiles    = $env:ProgramW6432
if (-not $programFiles) { $programFiles = $env:ProgramFiles }
$installDir      = Join-Path $programFiles $AppName
$startMenu       = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\$AppName"
$uninstallKey    = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$InnoAppId"
$uninstallKeyWow = "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$InnoAppId"

$desktopShortcuts = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk"),
    (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) "$AppName.lnk")
)

Write-Host ""
Say "What is on this machine now"

$serviceStatus = Get-ServiceStatus $ServiceName
if ($serviceStatus -eq 'missing') {
    Note "service $ServiceName : not registered"
} else {
    $binPath = ''
    foreach ($line in (& sc.exe qc $ServiceName)) {
        if ($line -match 'BINARY_PATH_NAME\s*:\s*(.+)$') { $binPath = $Matches[1].Trim() }
    }
    Warn "service $ServiceName : $serviceStatus  ->  $binPath"
}

foreach ($name in @($UiProcess, $SvcProcess)) {
    $procs = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    if ($procs.Count -gt 0) { Warn "process $name : running (pid $($procs.Id -join ', '))" }
}

foreach ($path in @($installDir, $programData, $programDataAlt, $localAppData, $startMenu)) {
    if (Test-Path $path) { Warn "directory : $path" } else { Note "directory : $path (absent)" }
}
foreach ($path in $desktopShortcuts) {
    if (Test-Path $path) { Warn "shortcut  : $path" }
}
foreach ($key in @($uninstallKey, $uninstallKeyWow)) {
    if (Test-Path $key) { Warn "registry  : $key" }
}

$adapters = Get-WintunAdapter
foreach ($adapter in $adapters) {
    Warn "adapter   : $($adapter.Name) [$($adapter.InterfaceDescription)] ifIndex $($adapter.ifIndex), $($adapter.Status)"
}

# pnputil has no machine-readable output on Windows 10 and its labels are localised, so this
# matches the English ones. On a non-English Windows the driver simply will not be found, which
# shows up as "the driver is still in the driver store" rather than as a wrong answer.
$wintunDrivers = @()
foreach ($line in (& pnputil.exe /enum-drivers)) {
    if ($line -match '^\s*Published Name\s*:\s*(oem\d+\.inf)') { $published = $Matches[1] }
    if ($line -match '^\s*Original Name\s*:\s*wintun\.inf') { $wintunDrivers += $published }
}
foreach ($driver in $wintunDrivers) { Warn "driver    : wintun.inf published as $driver" }

$firewallRules = @(Get-NetFirewallRule -DisplayName $AppName -ErrorAction SilentlyContinue)
foreach ($rule in $firewallRules) { Warn "firewall  : $($rule.DisplayName) ($($rule.Direction))" }

$relayAddresses = Get-KnownRelayAddress
$pinnedRoutes = @()
foreach ($address in $relayAddresses) {
    $pinnedRoutes += @(Get-NetRoute -DestinationPrefix "$address/32" -ErrorAction SilentlyContinue)
}
foreach ($route in $pinnedRoutes) {
    Warn "route     : $($route.DestinationPrefix) via $($route.NextHop) on ifIndex $($route.ifIndex)"
}

# ---------------------------------------------------------------- confirm

Write-Host ""
if ($DryRun) {
    Say "DRY RUN - nothing below will actually happen" 'Yellow'
} elseif (-not $Yes) {
    Say "This will remove all of the above" 'Yellow'
    if (-not $KeepIdentity) {
        Warn "device.key goes too, so this machine becomes a NEW DEVICE to the licence server"
        Warn "and the next sign-in spends one of the account's device slots. The old device"
        Warn "stays registered until it is removed from the dashboard. Use -KeepIdentity to"
        Warn "keep it, or accept the cost - a copy is saved either way."
    }
    Write-Host ""
    $answer = Read-Host "Type 'reset' to continue"
    if ($answer -ne 'reset') {
        Say "Nothing was changed." 'DarkGray'
        return
    }
}

# ---------------------------------------------------------------- back up

# Taken before anything is stopped, because the files are readable now and a half-torn-down
# machine is a bad place to discover that one of them mattered. Only the four that carry
# something which cannot be regenerated: an identity, a credential, or typed-in settings.
$backupDir = ''
if (-not $DryRun) {
    $backupDir = Join-Path $root (".reset-backup\" + (Get-Date -Format 'yyyy-MM-dd_HHmmss'))
    $sources = @(
        (Join-Path $programData 'device.key'),
        (Join-Path $programData 'token'),
        (Join-Path $programData 'config.json'),
        (Join-Path $localAppData 'refresh')
    )
    $copied = 0
    foreach ($source in $sources) {
        if (-not (Test-Path $source)) { continue }
        if ($copied -eq 0) { New-Item -ItemType Directory -Force -Path $backupDir | Out-Null }
        Copy-Item $source $backupDir -Force
        $copied = $copied + 1
    }
    if ($copied -gt 0) {
        Write-Host ""
        Say "Backed up $copied file(s)"
        Good $backupDir
        Note "device.key and token are DPAPI machine scope: they can be copied back HERE and"
        Note "will work, and are inert anywhere else. 'refresh' is user scope, same idea."
    }
}

# ---------------------------------------------------------------- uninstaller

if ($UseUninstaller) {
    Write-Host ""
    Say "Running the installed uninstaller"
    $uninstaller = Join-Path $installDir 'unins000.exe'
    if (Test-Path $uninstaller) {
        Step "run $uninstaller /VERYSILENT /NORESTART" {
            $p = Start-Process $uninstaller -ArgumentList '/VERYSILENT', '/NORESTART' -Wait -PassThru
            Note "exit code $($p.ExitCode)"
        }
        # Inno's uninstaller detaches a copy of itself to delete its own directory, so the
        # directory can still be there for a second or two after the process returns.
        if (-not $DryRun) { Start-Sleep -Seconds 3 }
    } else {
        Note "No uninstaller at $uninstaller - nothing installed by setup, or already gone."
    }
}

# ---------------------------------------------------------------- processes

Write-Host ""
Say "Stopping processes"
$anyProcess = $false
foreach ($name in @($UiProcess, $SvcProcess)) {
    $procs = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    foreach ($proc in $procs) {
        $anyProcess = $true
        Step "kill $name (pid $($proc.Id))" { Stop-Process -Id $proc.Id -Force }
    }
}
if (-not $anyProcess) { Note "Nothing was running." }

# ---------------------------------------------------------------- service

Write-Host ""
Say "Removing the service"
$status = Get-ServiceStatus $ServiceName
if ($status -eq 'missing') {
    Note "$ServiceName is not registered."
} else {
    if ($status -ne 'stopped') {
        Step "sc.exe stop $ServiceName" {
            & sc.exe stop $ServiceName | Out-Null
            if (-not (Wait-ServiceStatus $ServiceName @('stopped', 'missing'))) {
                Warn "It did not report stopped within 15 seconds. Deleting anyway."
            }
        }
    }
    Step "sc.exe delete $ServiceName" {
        & sc.exe delete $ServiceName | Out-Null

        # A delete does not always take effect at once. Anything still holding a handle to the
        # service - services.msc left open on it is the usual culprit - leaves it marked for
        # deletion, and the NEXT installer run then fails with 1072 while otherwise looking
        # successful. That failure is worth catching here, where it is one sentence, rather than
        # in the middle of a setup wizard.
        if (-not (Wait-ServiceStatus $ServiceName @('missing'))) {
            Warn "$ServiceName is still registered - Windows has it marked for deletion."
            Warn "Close services.msc (and any Event Viewer or Task Manager services tab) and"
            Warn "run this again, or reboot. Installing over this state fails with error 1072."
        }
    }
}

# ---------------------------------------------------------------- firewall

Write-Host ""
Say "Removing the firewall rule"
if ($firewallRules.Count -eq 0) {
    Note "No rule named '$AppName'."
} else {
    foreach ($rule in $firewallRules) {
        Step "remove firewall rule '$($rule.DisplayName)' ($($rule.Direction))" {
            Remove-NetFirewallRule -Name $rule.Name -ErrorAction SilentlyContinue
        }
    }
}

# ---------------------------------------------------------------- routes

Write-Host ""
Say "Removing leftover routes"

# Handled here, so the stray scan below does not report them back. It is keyed on what this
# run DEALT WITH rather than on what is left in the routing table, because on a dry run nothing
# has been removed and the two answers differ.
$handledPrefixes = @()

# Routes on a Wintun interface first: they belong to us by construction, and they are the ones
# that blackhole traffic if the adapter goes and they do not.
foreach ($adapter in $adapters) {
    $routes = @(Get-NetRoute -InterfaceIndex $adapter.ifIndex -ErrorAction SilentlyContinue |
        Where-Object { $_.AddressFamily -eq 'IPv4' })
    foreach ($route in $routes) {
        $handledPrefixes += $route.DestinationPrefix
        Step "delete $($route.DestinationPrefix) on ifIndex $($route.ifIndex) [$($adapter.Name)]" {
            Remove-NetRoute -InputObject $route -Confirm:$false -ErrorAction SilentlyContinue
        }
    }
}

# Then the pinned /32 for a relay, which lives on the PHYSICAL adapter and therefore cannot be
# swept up by interface. Only addresses this repository can name are touched; anything else is
# reported and left alone, because a /32 on the physical adapter might be somebody's VPN.
if ($pinnedRoutes.Count -eq 0) {
    Note "No pinned relay route for: $($relayAddresses -join ', ')"
} else {
    foreach ($route in $pinnedRoutes) {
        $handledPrefixes += $route.DestinationPrefix
        Step "delete $($route.DestinationPrefix) via $($route.NextHop) on ifIndex $($route.ifIndex)" {
            Remove-NetRoute -InputObject $route -Confirm:$false -ErrorAction SilentlyContinue
        }
    }
}

# Anything else that looks like a pin: a single host, reached through a gateway rather than
# on-link. It is reported and never touched, because that shape is also what a VPN, a corporate
# route or a hand-made entry looks like, and this script has no way to tell them apart.
$strays = @(Get-NetRoute -ErrorAction SilentlyContinue | Where-Object {
    $_.AddressFamily -eq 'IPv4' -and $_.DestinationPrefix -like '*/32' -and
    $_.NextHop -ne '0.0.0.0' -and $_.DestinationPrefix -notlike '255.*' -and
    $handledPrefixes -notcontains $_.DestinationPrefix
})
foreach ($stray in $strays) {
    Warn "left alone: $($stray.DestinationPrefix) via $($stray.NextHop) on ifIndex $($stray.ifIndex)"
    Note "  If that is one of ours, remove it with:"
    Note "  netsh interface ipv4 delete route $($stray.DestinationPrefix) interface=$($stray.ifIndex)"
}

# ---------------------------------------------------------------- adapter and driver

Write-Host ""
Say "Removing the virtual adapter and the Wintun driver"

if ($KeepDriver) {
    Note "-KeepDriver: leaving the driver and any adapter in place."
} else {
    foreach ($adapter in $adapters) {
        Step "remove adapter '$($adapter.Name)' ($($adapter.PnPDeviceID))" {
            & pnputil.exe /remove-device $adapter.PnPDeviceID | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Warn "pnputil could not remove it (exit $LASTEXITCODE). The driver removal below"
                Warn "will fail while an adapter still exists; a reboot clears it."
            }
        }
    }

    # The supported route first. WintunDeleteDriver refuses while any adapter still uses the
    # driver, which is exactly the safety wanted here - and it needs LocalSystem, not merely
    # Administrator, the same rule that applies to creating an adapter.
    $serviceExe = @(
        (Join-Path $installDir 'gpb-service.exe'),
        (Join-Path $programData 'bin\gpb-service.exe'),
        (Join-Path $root 'client\src\GamePingBooster.Service\bin\Debug\net9.0-windows\win-x64\gpb-service.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    $removed = $false
    if ($serviceExe) {
        $psexec = Get-Command psexec -ErrorAction SilentlyContinue
        Step "$serviceExe --remove-driver" {
            if ($psexec) {
                $p = Start-Process psexec -ArgumentList @('-accepteula', '-s', "`"$serviceExe`"", '--remove-driver') -Wait -PassThru -WindowStyle Hidden
                Note "exit code $($p.ExitCode)"
            } else {
                Warn "psexec is not on PATH, so this runs as Administrator rather than LocalSystem."
                Warn "Wintun may refuse. Get psexec from Sysinternals if it does."
                $p = Start-Process $serviceExe -ArgumentList '--remove-driver' -Wait -PassThru -WindowStyle Hidden
                Note "exit code $($p.ExitCode)"
            }
        }
        $removed = $true
    } else {
        Note "No gpb-service.exe anywhere to call --remove-driver with."
    }

    # Did it work? --remove-driver deliberately always exits 0, because an uninstall must never
    # fail over a leftover driver, so the exit code says nothing and the driver store has to be
    # asked again.
    if (-not $DryRun) {
        $stillThere = @()
        foreach ($line in (& pnputil.exe /enum-drivers)) {
            if ($line -match '^\s*Published Name\s*:\s*(oem\d+\.inf)') { $published = $Matches[1] }
            if ($line -match '^\s*Original Name\s*:\s*wintun\.inf') { $stillThere += $published }
        }

        if ($stillThere.Count -eq 0) {
            Good "The Wintun driver is out of the driver store."
        } elseif ($Force) {
            Warn "Forcing removal with pnputil. This removes the driver PACKAGE, which is shared"
            Warn "with anything else on this machine that uses Wintun - WireGuard, most likely."
            foreach ($driver in $stillThere) {
                Step "pnputil /delete-driver $driver /uninstall /force" {
                    & pnputil.exe /delete-driver $driver /uninstall /force | Out-Null
                    Note "exit code $LASTEXITCODE"
                }
            }
        } else {
            Warn "The Wintun driver is still in the driver store as: $($stillThere -join ', ')"
            if (-not $removed) { Warn "Nothing tried to remove it - there was no service binary." }
            Warn "The installer will simply reuse it, so setup still works; what you will not be"
            Warn "testing is the driver installation step. Re-run with -Force to remove it, and"
            Warn "read what -Force prints first if anything else here uses Wintun."
        }
    }
}

# ---------------------------------------------------------------- files

Write-Host ""
Say "Deleting files"

$toDelete = @($installDir, $programDataAlt, $startMenu) + $desktopShortcuts

if ($KeepIdentity) {
    # Everything under ProgramData except the three files that carry an identity or a credential.
    # Named individually rather than "delete the folder then put them back": a restore that runs
    # after a failed delete is a way to lose a device key, and there is no reason to take that
    # risk for a folder with eight entries in it.
    Note "-KeepIdentity: device.key, token and the saved sign-in stay."
    if (Test-Path $programData) {
        $keep = @('device.key', 'token', 'refresh')
        foreach ($item in (Get-ChildItem $programData -Force)) {
            if ($keep -contains $item.Name) { continue }
            Step "delete $($item.FullName)" { Remove-Item $item.FullName -Recurse -Force }
        }
    }
} else {
    $toDelete += $programData
    $toDelete += $localAppData
}

foreach ($path in $toDelete) {
    if (-not (Test-Path $path)) { continue }
    Step "delete $path" { Remove-Item $path -Recurse -Force -ErrorAction Stop }
}

# ---------------------------------------------------------------- registry

Write-Host ""
Say "Removing the uninstall entry"
$anyKey = $false
foreach ($key in @($uninstallKey, $uninstallKeyWow)) {
    if (-not (Test-Path $key)) { continue }
    $anyKey = $true
    # Only when the installation itself is gone. A key removed from under a working install is
    # how software becomes impossible to uninstall from Settings, which is worse than the mess
    # this script exists to clean up.
    if (Test-Path $installDir) {
        Warn "$key left in place: $installDir still exists, so setup is still installed."
        Warn "Removing the key now would make it unremovable from Settings."
        continue
    }
    Step "delete $key" { Remove-Item $key -Recurse -Force }
}
if (-not $anyKey) { Note "No uninstall entry registered." }

# ---------------------------------------------------------------- result

Write-Host ""
if ($DryRun) {
    Say "Dry run finished. Nothing was changed." 'Yellow'
    Note "Run it again from an Administrator terminal, without -DryRun, to do it."
    Write-Host ""
    return
}

Say "What is left"

$leftovers = 0

$status = Get-ServiceStatus $ServiceName
if ($status -ne 'missing') { Warn "service $ServiceName : $status"; $leftovers = $leftovers + 1 }

foreach ($path in @($installDir, $programDataAlt, $startMenu) + $desktopShortcuts) {
    if (Test-Path $path) { Warn "still there: $path"; $leftovers = $leftovers + 1 }
}
if (-not $KeepIdentity) {
    foreach ($path in @($programData, $localAppData)) {
        if (Test-Path $path) { Warn "still there: $path"; $leftovers = $leftovers + 1 }
    }
}
foreach ($key in @($uninstallKey, $uninstallKeyWow)) {
    if (Test-Path $key) { Warn "still there: $key"; $leftovers = $leftovers + 1 }
}
foreach ($adapter in (Get-WintunAdapter)) {
    Warn "still there: adapter $($adapter.Name)"; $leftovers = $leftovers + 1
}
foreach ($rule in @(Get-NetFirewallRule -DisplayName $AppName -ErrorAction SilentlyContinue)) {
    Warn "still there: firewall rule $($rule.DisplayName)"; $leftovers = $leftovers + 1
}

Write-Host ""
if ($leftovers -eq 0) {
    Say "Clean. This machine now looks like one that has never had it installed." 'Green'
    if ($backupDir -and (Test-Path $backupDir)) { Note "Backup: $backupDir" }
    Write-Host ""
    Note "Next:  .\gpb.ps1 installer          build the setup .exe"
    Note "       installer\dist\GamePingBooster-Setup-0.1.0.exe"
    Note ""
    Note "The Wintun driver is the one thing a real clean machine and this one can still"
    Note "differ on. Check the line above before concluding the driver step was tested."
} else {
    Say "$leftovers item(s) survived - see the warnings above." 'Yellow'
}
Write-Host ""
