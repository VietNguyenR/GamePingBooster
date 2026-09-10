<#
.SYNOPSIS
    Watch for the game, capture its traffic, and append the server addresses to observed.txt.

.DESCRIPTION
    Runs resident by default: it waits for the game process, captures while the game is open,
    analyses when the game exits, appends anything new, and goes back to waiting. Play as many
    matches as you like without touching the keyboard.

    Ctrl+C stops it, and stopping mid-match is fine: the capture is analysed on the way out
    rather than thrown away. You do not have to close the game first.

    Traffic is attributed to a process, so only what the game itself sent is considered. The
    attribution works the same way tools like cFosSpeed do it, without any driver and without
    touching the game:

      - the captured packet gives the local source port, the remote address and the time
      - the Windows UDP socket table (Get-NetUDPEndpoint, i.e. GetExtendedUdpTable) gives
        local port -> owning process
      - joining the two on local port AND time yields "this address was contacted by TslGame.exe
        while it actually held that port"

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

.PARAMETER FromFile
    Analyse a .pcapng that is already on disk instead of capturing. Used to recover a session the
    script left behind after an abnormal exit. Process attribution is not available for a saved
    file - the socket table it needs only existed while the capture was running - so every
    destination is listed and you judge them by volume, with the cloud cross-check in
    Build-PubgProfile.ps1 as the real filter.

.PARAMETER MinPackets
    Packet count above which a destination is written to observed.txt. Everything the game talked
    to is shown in the table either way, so lower this if you want to see short-lived endpoints
    recorded as well.

    Raised from 100 to 500 on 2026-09-05, from measurement rather than caution. A 54-second match
    put 2,323 packets on one address while everything else in the same capture managed 65 and 6 -
    the gap is close to three orders of magnitude, so a threshold anywhere in the middle costs
    nothing and 100 sat needlessly close to the noise.

    Why the threshold matters more than it looks: every address that lands in observed.txt is
    widened to a whole /20 by Build-PubgProfile.ps1, and a /20 of a game's *candidate* servers is
    actively harmful, not merely wasteful. Games pick a datacentre by measuring latency to
    several of them; routing some candidates through a relay and leaving the rest on the ISP path
    makes the game compare two different things and choose wrongly. A short-lived endpoint is
    usually one of those probes. Keep them out.

.EXAMPLE
    .\Capture-GameTraffic.ps1
    Leave it running, play, Ctrl+C when finished.

.EXAMPLE
    .\Capture-GameTraffic.ps1 -FromFile C:\Users\me\AppData\Local\Temp\gpb-20260901-215848.pcapng
    Analyse a capture that was left behind.
#>

[CmdletBinding()]
param(
    [string]$OutputPath,
    [string]$WatchProcess = 'TslGame',
    [switch]$Interactive,
    [string]$FromFile,
    [int]$MinPackets = 500,
    [int]$DurationMinutes = 180,
    [string]$Interface,
    [int]$ProbePort = 8081,
    [string]$LandmarkPath,
    [switch]$KeepCapture
)

$ErrorActionPreference = 'Stop'
if (-not $OutputPath) { $OutputPath = Join-Path $PSScriptRoot 'observed.txt' }
if (-not $LandmarkPath) { $LandmarkPath = Join-Path $PSScriptRoot 'landmarks-observed.txt' }

# A datacentre probe, not a game server.
#
# PUBG pings one endpoint per Azure region on UDP 8081 before a match and puts the player in
# whichever answers fastest. Those endpoints must never reach observed.txt, because everything in
# observed.txt ends up routed, and a routed probe makes the game measure one region through the
# relay and the rest over the player's own connection - it then compares the two and can pick a
# region that is worse both ways.
#
# -MinPackets used to be the only thing keeping them out, and it is not enough on its own: the
# probes hit 90 packets in a 186-second session, so a long enough evening pushes them over any
# threshold that still lets a short match through. The port is what actually identifies them, and
# it is unambiguous - probes are only ever seen on 8081, and no gameplay session has ever used it.
function Test-ProbeEndpoint {
    param($Row)
    $ports = @($Row.Ports.Keys)
    if ($ports.Count -eq 0) { return $false }
    foreach ($port in $ports) { if ([int]$port -ne $ProbePort) { return $false } }
    return $true
}

# What the running capture owns, so the cleanup at the bottom can deal with it.
$script:LiveCapture = $null
$script:LiveCaptureFile = $null

# Ctrl+C is handled as a keypress rather than as a kill. Killing the script mid-capture used to
# lose the entire session: the analysis only ran when the GAME exited, so stopping for the day
# while still in a match threw everything away. Reading the key instead lets the script finish
# the session properly on its way out, which is what somebody who has just played for an hour
# expects to happen.
$script:StopRequested = $false
$script:EnterPressed = $false
$script:CanReadKeys = $true
try { $null = [Console]::KeyAvailable } catch { $script:CanReadKeys = $false }

function Read-ControlKeys {
    if (-not $script:CanReadKeys) { return }
    # Drain everything waiting, so a stray keypress does not sit in the buffer for an hour.
    while ([Console]::KeyAvailable) {
        $key = [Console]::ReadKey($true)
        if ($key.Key -eq [ConsoleKey]::Enter) { $script:EnterPressed = $true }
        if ($key.Key -eq [ConsoleKey]::C -and ($key.Modifiers -band [ConsoleModifiers]::Control)) {
            $script:StopRequested = $true
        }
    }
}

# --------------------------------------------------------------- locate tools

$wiresharkDir = @("${env:ProgramFiles}\Wireshark", "${env:ProgramFiles(x86)}\Wireshark") |
    Where-Object { Test-Path (Join-Path $_ 'tshark.exe') } |
    Select-Object -First 1
if (-not $wiresharkDir) {
    throw "Wireshark not found. Install it from https://www.wireshark.org/ (include Npcap during setup)."
}
$tshark = Join-Path $wiresharkDir 'tshark.exe'
$dumpcap = Join-Path $wiresharkDir 'dumpcap.exe'

# Fail now rather than after a whole match. Npcap can be installed either restricted to
# administrators or open to everyone, so the only honest test is to ask dumpcap to list the
# interfaces and see whether it can.
function Assert-CanCapture {
    $probe = & $dumpcap -D 2>&1
    if ($LASTEXITCODE -ne 0 -or -not $probe) {
        $elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
        if ($elevated) {
            $hint = "Npcap looks broken or missing - reinstall Wireshark and include Npcap."
        } else {
            $hint = "Run this window as Administrator (Npcap was installed restricted to administrators)."
        }
        throw "dumpcap cannot list interfaces. $hint"
    }
    return $probe
}

function Resolve-CaptureInterface {
    param([string]$Requested, [string[]]$Devices)
    if ($Requested) { return $Requested }

    $activeNic = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Sort-Object RouteMetric | Select-Object -First 1
    if (-not $activeNic) { throw "No default route found - is the machine online?" }

    $adapter = Get-NetAdapter -InterfaceIndex $activeNic.ifIndex
    # Match on the interface GUID, not the name. dumpcap -D prints the connection name
    # ("Wi-Fi 2"), never the hardware description, and both are renameable and localised.
    $match = $Devices | Where-Object { $_ -like "*$($adapter.InterfaceGuid)*" } | Select-Object -First 1
    if (-not $match) {
        Write-Host "Could not match the adapter automatically. Available interfaces:"
        $Devices | ForEach-Object { Write-Host "    $_" }
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

# Only the headers are ever read back (ip.dst, the two ports, the timestamp), and those live in
# the first 42 bytes. Capturing 96 keeps room for a VLAN tag and throws the payload away in the
# driver: a three-hour session drops from roughly a gigabyte to under a hundred megabytes, and
# tshark reads it back in a fraction of the time. It also means no game content ever touches the
# disk, which is worth something on its own.
$snapLen = 96

# A second autostop, and it is not about this script working - it is about this script NOT
# working. dumpcap is a separate process, so if the host is killed outright or crashes, nothing
# stops it. Capping the file at 256 MB bounds the damage: at 96 bytes a packet that is millions
# of packets, far beyond any real session, so it never fires in normal use.
$maxCaptureKb = 262144

function Get-EpochSeconds {
    return [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() / 1000.0
}

# ------------------------------------------------------- process attribution

# Sample the UDP socket table into portOwners: local port -> owner -> {First, Last} epoch seconds.
#
# The time window is what makes attribution honest. Windows hands out ephemeral ports from a pool
# of about sixteen thousand and reuses them freely, so over a three-hour session a port the game
# used early can belong to a browser later. Recording only "this port was the game's" would then
# credit the game with somebody else's traffic. Recording WHEN it was the game's lets the analysis
# reject a packet that arrived after the game let the port go.
function Update-PortOwners {
    param([hashtable]$PortOwners, [hashtable]$PidNames, [string]$OnlyProcess)
    try {
        $endpoints = Get-NetUDPEndpoint -ErrorAction Stop
        $now = Get-EpochSeconds

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
                Add-PortSighting -PortOwners $PortOwners -Port ([int]$endpoint.LocalPort) -Owner $OnlyProcess -Now $now
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
            Add-PortSighting -PortOwners $PortOwners -Port ([int]$endpoint.LocalPort) -Owner $PidNames[$owningPid] -Now $now
        }
    } catch {
        # The table is a snapshot of a moving target; a failed sample is not worth reporting.
    }
}

function Add-PortSighting {
    param([hashtable]$PortOwners, [int]$Port, [string]$Owner, [double]$Now)
    if (-not $PortOwners.ContainsKey($Port)) { $PortOwners[$Port] = @{} }
    $owners = $PortOwners[$Port]
    if ($owners.ContainsKey($Owner)) {
        $owners[$Owner].Last = $Now
    } else {
        $owners[$Owner] = [pscustomobject]@{ First = $Now; Last = $Now }
    }
}

# ------------------------------------------------------------------ analysis

# Fold a capture file into "address -> packets, ports, owners". Kept separate from the capture
# itself so that a file left behind by an abnormal exit can still be read with -FromFile.
function Measure-CaptureFile {
    param([string]$Path, [hashtable]$PortOwners)

    # Per-packet fields rather than -z conv,udp: the conversation table is fixed-width with unit
    # suffixes that change with magnitude ("0 bytes" / "1 kB"), which makes a parser fragile.
    #
    # The output is piped rather than collected into a variable. A long session is hundreds of
    # thousands of rows, and holding them all as PowerShell strings before folding them up costs
    # far more memory than the numbers they turn into.
    $stats = @{}
    $slack = 3.0   # one poll interval plus a little, so a packet at the edge is not lost

    & $tshark -r $Path -T fields -e frame.time_epoch -e ip.dst -e udp.srcport -e udp.dstport |
        ForEach-Object {
            $parts = $_ -split "`t"
            if ($parts.Count -lt 4) { return }

            $ip = $parts[1].Trim()
            if ($ip -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { return }

            $when = 0.0
            [void][double]::TryParse($parts[0].Trim(), [ref]$when)
            $srcPort = $parts[2].Trim()
            $dstPort = $parts[3].Trim()

            if (-not $stats.ContainsKey($ip)) {
                $stats[$ip] = [pscustomobject]@{
                    Address = $ip; Packets = 0; Ports = @{}; Owners = @{}
                    First = 0.0; Last = 0.0
                }
            }
            $entry = $stats[$ip]
            $entry.Packets++
            if ($dstPort) { $entry.Ports[$dstPort] = $true }

            # First and last sighting, so the report can say how long a flow lasted. A packet
            # count on its own cannot tell a match apart from a burst: 600 packets over four
            # minutes is a heartbeat, 600 packets over eight seconds is not.
            if ($when -gt 0) {
                if ($entry.First -eq 0.0 -or $when -lt $entry.First) { $entry.First = $when }
                if ($when -gt $entry.Last) { $entry.Last = $when }
            }

            if ($srcPort -and $PortOwners.ContainsKey([int]$srcPort)) {
                foreach ($owner in $PortOwners[[int]$srcPort].Keys) {
                    $window = $PortOwners[[int]$srcPort][$owner]
                    if ($when -ge ($window.First - $slack) -and $when -le ($window.Last + $slack)) {
                        $entry.Owners[$owner] = $true
                    }
                }
            }
        }

    return $stats
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
        '-q',
        '-f', "`"$bpfFilter`"",
        '-s', $snapLen,
        '-a', "duration:$($DurationMinutes * 60)",
        '-a', "filesize:$maxCaptureKb",
        '-w', "`"$captureFile`"")

    $script:LiveCapture = $proc
    $script:LiveCaptureFile = $captureFile

    # The first sighting has to land before the first packet does, or the earliest seconds of the
    # session fall outside every ownership window and are thrown away.
    Update-PortOwners -PortOwners $portOwners -PidNames $pidNames -OnlyProcess $OnlyProcess

    $started = Get-Date
    while (-not $proc.HasExited) {
        Update-PortOwners -PortOwners $portOwners -PidNames $pidNames -OnlyProcess $OnlyProcess
        Read-ControlKeys
        if ($script:StopRequested) { break }
        if (& $ShouldStop) { break }
        Start-Sleep -Milliseconds 2000
    }

    if (-not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force
        Start-Sleep -Milliseconds 500
    }
    $script:LiveCapture = $null

    $elapsed = [int]((Get-Date) - $started).TotalSeconds
    if (-not (Test-Path $captureFile)) {
        Write-Warning "No capture file produced. Try running as Administrator."
        $script:LiveCaptureFile = $null
        return $null
    }

    $sizeMb = [math]::Round((Get-Item $captureFile).Length / 1MB, 1)
    Write-Host "==> $Label finished after ${elapsed}s, $sizeMb MB - analysing"

    $stats = Measure-CaptureFile -Path $captureFile -PortOwners $portOwners

    if (-not $KeepCapture) { Remove-Item $captureFile -Force -ErrorAction SilentlyContinue }
    else { Write-Host "    Raw capture kept at $captureFile" }
    $script:LiveCaptureFile = $null

    if ($stats.Count -eq 0) {
        Write-Warning "The capture is empty. Wrong interface, or the game sent nothing."
        return $null
    }

    return $stats
}

# -------------------------------------------------------------------- report

<#
.SYNOPSIS
    Reads observed.txt into records, tolerating both the old and the new layout.

.DESCRIPTION
    The file used to hold one bare address per line. It now holds the evidence beside it, because
    an address on its own cannot be re-judged later: when the -MinPackets threshold was raised
    from 100 to 500, there was no way to tell which of the 126 addresses already in the file would
    still qualify, and the only remedy was to throw the file away and replay every match.

    Anything after the address is optional, so a file written by the old script still parses and
    simply reports zero for the counts it never recorded.
#>
function Read-ObservedFile {
    param([string]$Path)

    if (-not (Test-Path $Path)) { return @() }

    $records = @()
    foreach ($line in Get-Content $Path) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }

        $fields = $trimmed -split '\s+'
        if ($fields[0] -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { continue }

        $packets = 0; $seconds = 0; $sightings = 1
        if ($fields.Count -gt 1) { [void][int]::TryParse($fields[1], [ref]$packets) }
        if ($fields.Count -gt 2) { [void][int]::TryParse($fields[2], [ref]$seconds) }
        if ($fields.Count -gt 3) { [void][int]::TryParse($fields[3], [ref]$sightings) }
        $ports = ''
        if ($fields.Count -gt 4) { $ports = $fields[4] }

        $records += [pscustomobject]@{
            Address = $fields[0]; Packets = $packets; Seconds = $seconds
            Sightings = $sightings; Ports = $ports
        }
    }
    return $records
}

<#
.SYNOPSIS
    Folds this session's accepted rows into observed.txt and returns the addresses that are new.

.DESCRIPTION
    One line per address, rewritten rather than appended, so an address seen in five matches is
    one row with the strongest evidence rather than five rows to reconcile by eye.

    Packets and seconds keep the HIGHEST single-session figures, not a running total. A total
    would let a heartbeat that trickles for hours out-score a real match, which is the opposite of
    what the number is for: it answers "was this ever carrying a game?", and one match is enough
    to say yes. Sightings counts the sessions separately, because an address that turns up in
    every match is better evidence than one that appeared once.
#>
function Merge-ObservedFile {
    param([string]$Path, [object[]]$Rows, [string[]]$Header)

    $existing = @{}
    $order = @()
    foreach ($record in Read-ObservedFile -Path $Path) {
        if (-not $existing.ContainsKey($record.Address)) { $order += $record.Address }
        $existing[$record.Address] = $record
    }

    $new = @()
    foreach ($row in $Rows) {
        $seconds = 0
        if ($row.Last -gt $row.First) { $seconds = [int][math]::Round($row.Last - $row.First) }
        $ports = ($row.Ports.Keys | Sort-Object { [int]$_ } | Select-Object -First 4) -join ','

        if ($existing.ContainsKey($row.Address)) {
            $record = $existing[$row.Address]
            $record.Sightings = $record.Sightings + 1
            if ($row.Packets -gt $record.Packets) { $record.Packets = $row.Packets }
            if ($seconds -gt $record.Seconds) { $record.Seconds = $seconds }
            if ($ports) { $record.Ports = $ports }
        }
        else {
            $order += $row.Address
            $existing[$row.Address] = [pscustomobject]@{
                Address = $row.Address; Packets = $row.Packets; Seconds = $seconds
                Sightings = 1; Ports = $ports
            }
            $new += $row.Address
        }
    }

    # A List[string], not @() with +=. If anything in here throws mid-array, += on the resulting
    # $null silently degrades to STRING concatenation and the whole file is written as one line -
    # which is exactly what a format-string typo did the first time this ran.
    $lines = New-Object System.Collections.Generic.List[string]
    if (-not $Header) {
        $Header = @(
            '# Destinations that passed -MinPackets, written by Capture-GameTraffic.ps1.',
            '# Only the first column is read by Build-PubgProfile.ps1; the rest is the evidence',
            '# that put the address here, so the threshold can be revisited later without',
            '# replaying every match.')
    }
    foreach ($line in $Header) { $lines.Add($line) }
    $lines.Add('# address           packets   secs  seen  udp ports')
    foreach ($address in $order) {
        $record = $existing[$address]
        # The arguments go in via an array. Writing them after -f across a line break inside
        # .Add() binds only the first one, and the format then fails on {1} with an error that
        # names neither the operator nor the line that fed it.
        $fields = @($record.Address, $record.Packets, $record.Seconds, $record.Sightings, $record.Ports)
        $lines.Add('{0,-18} {1,8} {2,6} {3,5}  {4}' -f $fields)
    }
    Set-Content -Path $Path -Value $lines.ToArray() -Encoding ascii

    return $new
}

function Write-SessionResult {
    param([hashtable]$Stats, [string]$OnlyProcess)

    if (-not $Stats -or $Stats.Count -eq 0) { return @() }

    $rows = $Stats.Values
    if ($OnlyProcess) {
        # Keep only destinations whose local port belonged to the watched process at the time the
        # packet was sent. Unattributed ones are dropped too: with the socket table sampled every
        # two seconds, anything the game held open for a whole match is attributed, so what is
        # left is someone else's.
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

    # Probes are found across EVERY row, not just the 25 printed below. The table is sorted by
    # packet count and probes sit at the bottom of it - 12 packets against a match's 4,000 - so
    # a busy session would push the quietest regions off the end of the list and they would never
    # be seen. That is the opposite of what this is for: the quiet ones are precisely the regions
    # a single capture is most likely to miss.
    $probes = @($rows | Where-Object { Test-ProbeEndpoint $_ })
    $probeAddresses = @($probes | ForEach-Object { $_.Address })

    # Decided over every row; only the first 25 are PRINTED. These used to be the same loop, so
    # an address above the threshold but ranked 26th or lower was silently dropped from
    # observed.txt - invisible, because it was also missing from the table you would check it
    # against. A real session has few destinations so it never bit, but the same mistake did bite
    # for probes, which live at the bottom of this list by definition.
    $accepted = @($rows | Where-Object {
        $probeAddresses -notcontains $_.Address -and $_.Packets -ge $MinPackets
    })
    $acceptedAddresses = @($accepted | ForEach-Object { $_.Address })

    foreach ($row in ($rows | Select-Object -First 25)) {
        $ports = ($row.Ports.Keys | Sort-Object { [int]$_ } | Select-Object -First 4) -join ','
        $owners = ($row.Owners.Keys | Sort-Object) -join ','
        if (-not $owners) { $owners = '?' }
        $flag = ''
        if ($probeAddresses -contains $row.Address) { $flag = '  <- datacentre probe, NOT routed' }
        elseif ($acceptedAddresses -contains $row.Address) { $flag = '  <- kept' }
        Write-Host ("    {0,-18} {1,9}  {2,-22} {3}{4}" -f $row.Address, $row.Packets, $ports, $owners, $flag)
    }
    if ($rows.Count -gt 25) {
        Write-Host ("    ... and {0} more, all counted" -f ($rows.Count - 25))
    }

    if ($probes.Count -gt 0) {
        $newProbes = @(Merge-ObservedFile -Path $LandmarkPath -Rows $probes -Header @(
            '# Datacentre probe endpoints seen on UDP 8081, written by Capture-GameTraffic.ps1.',
            '#',
            '# These are LANDMARKS, not game servers, and nothing here may ever be routed - the game',
            '# pings one per region to decide where to put the player, so a routed one makes it',
            '# compare a tunnelled path against direct ones.',
            '#',
            '# Nothing reads this file automatically. It accumulates across sessions because one',
            '# capture rarely sees every region - packet counts range from 90 down to 12, and the',
            '# quiet ones come and go. When a region has been seen a few times, look its address up',
            '# in the Azure Service Tags file and add it to that region''s "landmarks" in the',
            '# profile by hand. Build-PubgProfile.ps1 then checks it stays in that region.'))
        $total = @(Read-ObservedFile -Path $LandmarkPath).Count

        Write-Host ""
        Write-Host "==> $($probes.Count) datacentre probe endpoint(s) this session - these are LANDMARKS, not servers." -ForegroundColor Cyan
        foreach ($row in $probes) {
            $mark = ''
            if ($newProbes -contains $row.Address) { $mark = '  <- new' }
            Write-Host ("      {0,-18} {1,6} pkt{2}" -f $row.Address, $row.Packets, $mark)
        }
        Write-Host "    They are NOT written to observed.txt and never routed. Accumulated in"
        Write-Host "    $(Split-Path $LandmarkPath -Leaf), which now holds $total address(es)."
        Write-Host "    One capture rarely sees every region - run a few before trusting the list."
    }

    if ($accepted.Count -eq 0) {
        Write-Warning "Nothing reached the $MinPackets packet threshold."
        return @()
    }

    $new = @(Merge-ObservedFile -Path $OutputPath -Rows $accepted)

    $total = @(Read-ObservedFile -Path $OutputPath).Count
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

try {
    if ($FromFile) {
        if (-not (Test-Path $FromFile)) { throw "No such capture: $FromFile" }
        Write-Host ""
        Write-Host "==> Analysing $FromFile" -ForegroundColor Cyan
        Write-Warning ("Process attribution is not available for a saved capture: the socket table it " +
                       "needs only existed while the capture was running. Judge these by packet volume, " +
                       "and let Build-PubgProfile.ps1 do the cloud cross-check.")
        $stats = Measure-CaptureFile -Path $FromFile -PortOwners @{}
        $null = Write-SessionResult -Stats $stats -OnlyProcess ''
        Write-Host ""
        Write-Host "    Next: .\Build-PubgProfile.ps1"
        return
    }

    if ($script:CanReadKeys) { [Console]::TreatControlCAsInput = $true }

    $devices = Assert-CanCapture
    $Interface = Resolve-CaptureInterface -Requested $Interface -Devices $devices

    if ($Interactive) {
        Write-Host ""
        Write-Host "==> Interactive capture. Start your match now, press Enter when it ends." -ForegroundColor Cyan
        $stop = { return $script:EnterPressed }
        $stats = Invoke-CaptureSession -InterfaceId $Interface -ShouldStop $stop -Label 'Capture' -OnlyProcess ''
        $null = Write-SessionResult -Stats $stats -OnlyProcess ''
        Write-Host ""
        Write-Host "    Next: .\Build-PubgProfile.ps1"
        return
    }

    Write-Host ""
    Write-Host "==> Resident mode, watching for $WatchProcess.exe" -ForegroundColor Cyan
    Write-Host "    Play as many matches as you like. Ctrl+C to stop - stopping mid-match is fine,"
    Write-Host "    the capture is analysed before the script exits."
    Write-Host ""

    $sessionCount = 0
    while (-not $script:StopRequested) {
        while (-not (Get-Process -Name $WatchProcess -ErrorAction SilentlyContinue)) {
            Read-ControlKeys
            if ($script:StopRequested) { break }
            Start-Sleep -Seconds 1
        }
        if ($script:StopRequested) { break }

        $sessionCount++
        Write-Host "==> $WatchProcess.exe started - capturing (session $sessionCount)" -ForegroundColor Cyan

        $stop = { return -not (Get-Process -Name $WatchProcess -ErrorAction SilentlyContinue) }
        $stats = Invoke-CaptureSession -InterfaceId $Interface -ShouldStop $stop -Label "Session $sessionCount" -OnlyProcess $WatchProcess
        $null = Write-SessionResult -Stats $stats -OnlyProcess $WatchProcess

        if ($script:StopRequested) { break }

        Write-Host ""
        Write-Host "==> Waiting for $WatchProcess.exe again. Ctrl+C to stop, then run .\Build-PubgProfile.ps1"
        Write-Host ""

        # The process table can still list a closing process for a moment; do not re-trigger on it.
        Start-Sleep -Seconds 5
    }

    Write-Host ""
    Write-Host "==> Stopped after $sessionCount session(s). Next: .\Build-PubgProfile.ps1" -ForegroundColor Cyan
}
finally {
    if ($script:CanReadKeys) { try { [Console]::TreatControlCAsInput = $false } catch { } }

    if ($script:LiveCapture -and -not $script:LiveCapture.HasExited) {
        Stop-Process -Id $script:LiveCapture.Id -Force -ErrorAction SilentlyContinue
    }

    # Reaching here with a capture file still set means the script is ending abnormally, and that
    # file is the only copy of a session somebody spent an hour producing. An earlier version of
    # this script deleted it here for tidiness and destroyed 66,000 packets of real gameplay.
    # Keep it, and say how to read it.
    if ($script:LiveCaptureFile -and (Test-Path $script:LiveCaptureFile)) {
        Write-Host ""
        Write-Host "==> The capture was left at $script:LiveCaptureFile" -ForegroundColor Yellow
        Write-Host "    Analyse it with:  .\Capture-GameTraffic.ps1 -FromFile '$script:LiveCaptureFile'"
    }
}
