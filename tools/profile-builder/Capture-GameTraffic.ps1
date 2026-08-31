<#
.SYNOPSIS
    Watch for the game, capture its traffic, and append the server addresses to observed.txt.

.DESCRIPTION
    Runs resident by default: it waits for the game process, captures while the game is open,
    analyses when the game exits, appends anything new, and goes back to waiting. Play as many
    matches as you like without touching the keyboard; Ctrl+C when you are done for the day.

    Traffic is attributed to a process, so only what the game itself sent is considered. The
    attribution works the same way tools like cFosSpeed do it, without any driver and without
    touching the game:

      - the captured packet gives the local source port and the remote address
      - the Windows UDP socket table (Get-NetUDPEndpoint, i.e. GetExtendedUdpTable) gives
        local port -> owning process
      - joining the two on local port yields "this address was contacted by TslGame.exe"

    The socket table is polled while capturing, because it only ever describes the present: UDP
    is connectionless, so there is no historical record to consult afterwards.

    Attribution removes the browser, Windows Update and background services. It does NOT separate
    gameplay from voice chat - both are the same process - so packet volume still matters, and
    Build-PubgProfile.ps1 still cross-checks every address against the published cloud ranges.

.PARAMETER WatchProcess
    Process to watch, without .exe. Defaults to TslGame (PUBG). The script starts capturing when
    it appears and analyses when it exits.

.PARAMETER Interactive
    Old behaviour: capture once, stop when you press Enter. Useful for a game whose process name
    you do not know yet.

.PARAMETER MinPackets
    Packet count above which a destination is written to observed.txt. Everything the game talked
    to is shown in the table either way, so lower this if you want to see short-lived endpoints
    recorded as well.

.EXAMPLE
    .\Capture-GameTraffic.ps1
    Leave it running, play, Ctrl+C when finished.

.EXAMPLE
    .\Capture-GameTraffic.ps1 -Interactive
    One capture, stopped by pressing Enter.
#>

[CmdletBinding()]
param(
    [string]$OutputPath,
    [string]$WatchProcess = 'TslGame',
    [switch]$Interactive,
    [int]$MinPackets = 100,
    [int]$DurationMinutes = 180,
    [string]$Interface,
    [switch]$KeepCapture
)

$ErrorActionPreference = 'Stop'
if (-not $OutputPath) { $OutputPath = Join-Path $PSScriptRoot 'observed.txt' }

# --------------------------------------------------------------- locate tools

$wiresharkDir = @("${env:ProgramFiles}\Wireshark", "${env:ProgramFiles(x86)}\Wireshark") |
    Where-Object { Test-Path (Join-Path $_ 'tshark.exe') } |
    Select-Object -First 1
if (-not $wiresharkDir) {
    throw "Wireshark not found. Install it from https://www.wireshark.org/ (include Npcap during setup)."
}
$tshark = Join-Path $wiresharkDir 'tshark.exe'
$dumpcap = Join-Path $wiresharkDir 'dumpcap.exe'

function Resolve-CaptureInterface {
    param([string]$Requested)
    if ($Requested) { return $Requested }

    $activeNic = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Sort-Object RouteMetric | Select-Object -First 1
    if (-not $activeNic) { throw "No default route found - is the machine online?" }

    $adapter = Get-NetAdapter -InterfaceIndex $activeNic.ifIndex
    # Match on the interface GUID, not the name. dumpcap -D prints the connection name
    # ("Wi-Fi 2"), never the hardware description, and both are renameable and localised.
    $devices = & $dumpcap -D
    $match = $devices | Where-Object { $_ -like "*$($adapter.InterfaceGuid)*" } | Select-Object -First 1
    if (-not $match) {
        Write-Host "Could not match the adapter automatically. Available interfaces:"
        $devices | ForEach-Object { Write-Host "    $_" }
        throw "Pass one explicitly, e.g. -Interface 5"
    }
    Write-Host "==> Adapter: $($adapter.Name) - $($adapter.InterfaceDescription) (dumpcap $(($match -split '\.')[0].Trim()))"
    return ($match -split '\.')[0].Trim()
}

# BPF filter, applied in the kernel so the disk never sees the rest:
#   udp                  gameplay is UDP; lobby/store/auth is TCP and irrelevant here
#   not port 53          DNS
#   not dst net ...      LAN, link-local, multicast, broadcast
#
# Note the dst qualifier on every net term. A bare `net 192.168.0.0/16` matches source OR
# destination, and this machine's own address is in that range - the unqualified form silently
# discards every outbound packet and captures nothing at all.
$bpfFilter = 'udp and not port 53 ' +
             'and not dst net 10.0.0.0/8 and not dst net 172.16.0.0/12 ' +
             'and not dst net 192.168.0.0/16 and not dst net 169.254.0.0/16 ' +
             'and not dst net 224.0.0.0/4 and not dst host 255.255.255.255'

# ------------------------------------------------------- process attribution

# Sample the UDP socket table into portOwners: local port -> set of process names.
# Called repeatedly during a capture. A port can be reused by another process later, hence a
# set rather than a single name - a port claimed by two processes over one session is ambiguous
# and we would rather see that than silently pick one.
function Update-PortOwners {
    param([hashtable]$PortOwners, [hashtable]$PidNames, [string]$OnlyProcess)
    try {
        $endpoints = Get-NetUDPEndpoint -ErrorAction Stop

        if ($OnlyProcess) {
            # We already know which process matters, so skip name resolution entirely: look up
            # its PIDs once and keep only endpoints belonging to them. This machine has ~390 UDP
            # endpoints, and resolving a name for every one of them costs about four seconds on
            # the first poll - all of it wasted when a single process is the target.
            $targetPids = @{}
            foreach ($proc in (Get-Process -Name $OnlyProcess -ErrorAction SilentlyContinue)) {
                $targetPids[[int]$proc.Id] = $true
            }
            if ($targetPids.Count -eq 0) { return }

            foreach ($endpoint in $endpoints) {
                if (-not $targetPids.ContainsKey([int]$endpoint.OwningProcess)) { continue }
                $port = [int]$endpoint.LocalPort
                if (-not $PortOwners.ContainsKey($port)) { $PortOwners[$port] = @{} }
                $PortOwners[$port][$OnlyProcess] = $true
            }
            return
        }

        foreach ($endpoint in $endpoints) {
            $owningPid = $endpoint.OwningProcess
            if (-not $owningPid) { continue }
            if (-not $PidNames.ContainsKey($owningPid)) {
                try { $PidNames[$owningPid] = (Get-Process -Id $owningPid -ErrorAction Stop).ProcessName }
                catch { $PidNames[$owningPid] = "pid-$owningPid" }
            }
            $port = [int]$endpoint.LocalPort
            if (-not $PortOwners.ContainsKey($port)) { $PortOwners[$port] = @{} }
            $PortOwners[$port][$PidNames[$owningPid]] = $true
        }
    } catch {
        # The table is a snapshot of a moving target; a failed sample is not worth reporting.
    }
}

# ------------------------------------------------------------------- capture

function Invoke-CaptureSession {
    param(
        [string]$InterfaceId,
        [scriptblock]$ShouldStop,     # called every poll; $true means wrap up
        [string]$Label,
        [string]$OnlyProcess
    )

    $captureFile = Join-Path ([System.IO.Path]::GetTempPath()) ("gpb-{0:yyyyMMdd-HHmmss}.pcapng" -f (Get-Date))
    $portOwners = @{}
    $pidNames = @{}

    $proc = Start-Process -FilePath $dumpcap -PassThru -NoNewWindow -ArgumentList @(
        '-i', $InterfaceId,
        '-f', "`"$bpfFilter`"",
        '-a', "duration:$($DurationMinutes * 60)",
        '-w', "`"$captureFile`"")

    $started = Get-Date
    while (-not $proc.HasExited) {
        Update-PortOwners -PortOwners $portOwners -PidNames $pidNames -OnlyProcess $OnlyProcess
        if (& $ShouldStop) { break }
        Start-Sleep -Milliseconds 2000
    }

    if (-not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force
        Start-Sleep -Milliseconds 500
    }

    $elapsed = [int]((Get-Date) - $started).TotalSeconds
    if (-not (Test-Path $captureFile)) {
        Write-Warning "No capture file produced. Try running as Administrator."
        return $null
    }

    $sizeMb = [math]::Round((Get-Item $captureFile).Length / 1MB, 1)
    Write-Host "==> $Label finished after ${elapsed}s, $sizeMb MB"

    # Per-packet fields rather than -z conv,udp: the conversation table is fixed-width with unit
    # suffixes that change with magnitude ("0 bytes" / "1 kB"), which makes a parser fragile.
    # A long session is a few hundred thousand rows and takes seconds to fold up here.
    $rows = & $tshark -r $captureFile -T fields -e ip.dst -e udp.srcport -e udp.dstport
    if (-not $KeepCapture) { Remove-Item $captureFile -Force -ErrorAction SilentlyContinue }
    else { Write-Host "    Raw capture kept at $captureFile" }

    if (-not $rows) {
        Write-Warning "The capture is empty. Wrong interface, or the game sent nothing."
        return $null
    }

    $stats = @{}
    foreach ($row in $rows) {
        $parts = $row -split "`t"
        if ($parts.Count -lt 3) { continue }
        $ip = $parts[0].Trim()
        if ($ip -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { continue }
        $srcPort = $parts[1].Trim()
        $dstPort = $parts[2].Trim()

        if (-not $stats.ContainsKey($ip)) {
            $stats[$ip] = [pscustomobject]@{
                Address = $ip; Packets = 0; Ports = @{}; Owners = @{}
            }
        }
        $entry = $stats[$ip]
        $entry.Packets++
        if ($dstPort) { $entry.Ports[$dstPort] = $true }
        if ($srcPort -and $portOwners.ContainsKey([int]$srcPort)) {
            foreach ($owner in $portOwners[[int]$srcPort].Keys) { $entry.Owners[$owner] = $true }
        }
    }

    return $stats
}

# -------------------------------------------------------------------- report

function Write-SessionResult {
    param([hashtable]$Stats, [string]$OnlyProcess)

    if (-not $Stats -or $Stats.Count -eq 0) { return @() }

    $rows = $Stats.Values
    if ($OnlyProcess) {
        # Keep only destinations whose local port belonged to the watched process. Unattributed
        # ones are dropped too: with the socket table sampled every two seconds, anything the
        # game held open for a whole match is attributed, so what is left is someone else's.
        $rows = @($rows | Where-Object { $_.Owners.Keys -contains $OnlyProcess })
    }
    $rows = @($rows | Sort-Object Packets -Descending)

    if ($rows.Count -eq 0) {
        Write-Warning "Nothing attributed to $OnlyProcess. Was the game actually sending UDP?"
        return @()
    }

    Write-Host ""
    Write-Host ("    {0,-18} {1,9}  {2,-22} {3}" -f 'Address', 'Packets', 'UDP ports', 'Process')
    Write-Host ("    {0,-18} {1,9}  {2,-22} {3}" -f '-------', '-------', '---------', '-------')

    $accepted = @()
    foreach ($row in ($rows | Select-Object -First 25)) {
        $ports = ($row.Ports.Keys | Sort-Object { [int]$_ } | Select-Object -First 4) -join ','
        $owners = ($row.Owners.Keys | Sort-Object) -join ','
        if (-not $owners) { $owners = '?' }
        $flag = ''
        if ($row.Packets -ge $MinPackets) { $accepted += $row.Address; $flag = '  <- kept' }
        Write-Host ("    {0,-18} {1,9}  {2,-22} {3}{4}" -f $row.Address, $row.Packets, $ports, $owners, $flag)
    }

    if ($accepted.Count -eq 0) {
        Write-Warning "Nothing reached the $MinPackets packet threshold."
        return @()
    }

    $existing = @()
    if (Test-Path $OutputPath) { $existing = Get-Content $OutputPath | ForEach-Object { $_.Trim() } }
    $new = @($accepted | Where-Object { $existing -notcontains $_ })
    if ($new.Count -gt 0) { Add-Content -Path $OutputPath -Value $new -Encoding ascii }

    $total = @(Get-Content $OutputPath | Where-Object { $_.Trim() }).Count
    Write-Host ""
    if ($new.Count -gt 0) {
        Write-Host "==> $($new.Count) new: $($new -join ', ')" -ForegroundColor Green
    } else {
        Write-Host "==> Nothing new this session - the list may be saturating." -ForegroundColor Green
    }
    Write-Host "    observed.txt now holds $total addresses"
    return $new
}

# ---------------------------------------------------------------------- main

$Interface = Resolve-CaptureInterface -Requested $Interface

if ($Interactive) {
    Write-Host ""
    Write-Host "==> Interactive capture. Start your match now, press Enter when it ends." -ForegroundColor Cyan
    $stop = {
        if ($Host.UI.RawUI.KeyAvailable) {
            $key = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
            return ($key.VirtualKeyCode -eq 13)
        }
        return $false
    }
    $stats = Invoke-CaptureSession -InterfaceId $Interface -ShouldStop $stop -Label 'Capture' -OnlyProcess ''
    $null = Write-SessionResult -Stats $stats -OnlyProcess ''
    Write-Host ""
    Write-Host "    Next: .\Build-PubgProfile.ps1"
    return
}

Write-Host ""
Write-Host "==> Resident mode, watching for $WatchProcess.exe" -ForegroundColor Cyan
Write-Host "    Play as many matches as you like. Ctrl+C to stop."
Write-Host "    Capture starts when the game opens and is analysed when it closes."
Write-Host ""

$sessionCount = 0
while ($true) {
    while (-not (Get-Process -Name $WatchProcess -ErrorAction SilentlyContinue)) {
        Start-Sleep -Seconds 2
    }

    $sessionCount++
    Write-Host "==> $WatchProcess.exe started - capturing (session $sessionCount)" -ForegroundColor Cyan

    $stop = { return -not (Get-Process -Name $WatchProcess -ErrorAction SilentlyContinue) }
    $stats = Invoke-CaptureSession -InterfaceId $Interface -ShouldStop $stop -Label "Session $sessionCount" -OnlyProcess $WatchProcess
    $null = Write-SessionResult -Stats $stats -OnlyProcess $WatchProcess

    Write-Host ""
    Write-Host "==> Waiting for $WatchProcess.exe again. Ctrl+C to stop, then run .\Build-PubgProfile.ps1"
    Write-Host ""

    # The process table can still list a closing process for a moment; do not re-trigger on it.
    Start-Sleep -Seconds 5
}
