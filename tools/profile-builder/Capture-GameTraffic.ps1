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

.PARAMETER Protocol
    udp (default), tcp, or all.

    udp is what builds the profile, and is exactly what this script always did: gameplay and the
    datacentre probes are UDP, and only UDP ever reaches observed.txt or landmarks-observed.txt.

    tcp is for a different question - why a lobby sits on "Initializing..." - and answers it
    without touching the profile. Login, lobby, store and anti-cheat are TCP, so the default filter
    never sees them. With tcp, every destination the game opened a TCP connection to is written
    to tcp-sessions.txt with a verdict per address: connected, or BLOCKED when every SYN to it went
    unanswered. all captures both and handles each half as above.

    TCP is NEVER written to observed.txt. Everything there is widened to a /20 and routed, and TCP
    destinations are shared CDN and cloud front doors carrying everybody else's traffic too.

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
    .\Capture-GameTraffic.ps1 -Protocol tcp
    Start the game, let the lobby hang on "Initializing...", then close it or press Ctrl+C. The
    table shows which TCP destinations never answered.

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
    [switch]$KeepCapture,
    [ValidateSet('udp', 'tcp', 'all')]
    [string]$Protocol = 'udp',
    [string]$TcpOutputPath
)

$ErrorActionPreference = 'Stop'
if (-not $OutputPath) { $OutputPath = Join-Path $PSScriptRoot 'observed.txt' }
if (-not $LandmarkPath) { $LandmarkPath = Join-Path $PSScriptRoot 'landmarks-observed.txt' }
if (-not $TcpOutputPath) { $TcpOutputPath = Join-Path $PSScriptRoot 'tcp-sessions.txt' }

# Which halves this run handles. The UDP half is the profile's; the TCP half is diagnosis only and
# has its own file. Nothing below lets one feed the other.
$captureUdp = $Protocol -in @('udp', 'all')
$captureTcp = $Protocol -in @('tcp', 'all')

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
#   udp / tcp            gameplay is UDP; lobby, login and store are TCP - only when asked for
#   not port 53          DNS
#   not dst net ...      LAN, link-local, multicast, broadcast
#
# Note the dst qualifier on every net term. A bare `net 192.168.0.0/16` matches source OR
# destination, and this machine's own address is in that range - the unqualified form silently
# discards every outbound packet and captures nothing at all.
#
# The same qualifier is why only OUTBOUND packets are captured, and the TCP analysis is built
# around that: a SYN-ACK coming back is never seen, but the ACK this machine sends in reply is,
# and that is proof enough that the handshake completed.
$protocolTerm = switch ($Protocol) {
    'udp' { 'udp' }
    'tcp' { 'tcp' }
    default { '(udp or tcp)' }
}
$bpfFilter = "$protocolTerm and not port 53 " +
             'and not dst net 10.0.0.0/8 and not dst net 172.16.0.0/12 ' +
             'and not dst net 192.168.0.0/16 and not dst net 169.254.0.0/16 ' +
             'and not dst net 224.0.0.0/4 and not dst host 255.255.255.255'

# Only the headers are ever read back (ip.dst, the ports, the TCP flags, the timestamp), and the
# deepest of those - the TCP flags - ends at byte 48. Capturing 96 keeps room for a VLAN tag and
# throws the payload away in the driver: a three-hour session drops from roughly a gigabyte to
# under a hundred megabytes, and tshark reads it back in a fraction of the time. It also means no
# game content ever touches the disk, which is worth something on its own.
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

# Sample a socket table into portOwners: local port -> owner -> {First, Last} epoch seconds.
#
# The time window is what makes attribution honest. Windows hands out ephemeral ports from a pool
# of about sixteen thousand and reuses them freely, so over a three-hour session a port the game
# used early can belong to a browser later. Recording only "this port was the game's" would then
# credit the game with somebody else's traffic. Recording WHEN it was the game's lets the analysis
# reject a packet that arrived after the game let the port go.
#
# UDP and TCP keep separate tables. A local port number means nothing across protocols - UDP 50000
# and TCP 50000 are unrelated sockets, often in different processes - so one table would credit
# the game with another program's connections.
#
# For TCP the connection table also lists attempts still in SynSent, with their owning process,
# so a connection that never completes is attributed as well as one that does. That matters: the
# blocked ones are the entire reason to capture TCP.
function Update-PortOwners {
    param([hashtable]$PortOwners, [hashtable]$PidNames, [string]$OnlyProcess,
          [ValidateSet('udp', 'tcp')][string]$Kind = 'udp')
    try {
        if ($Kind -eq 'tcp') { $endpoints = Get-NetTCPConnection -ErrorAction Stop }
        else { $endpoints = Get-NetUDPEndpoint -ErrorAction Stop }
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

# Fold a capture file into "address -> packets, ports, owners", separately for UDP and TCP. Kept
# separate from the capture itself so that a file left behind by an abnormal exit can still be read
# with -FromFile.
#
# For TCP each destination also keeps its flows (local port > remote port) and, per flow, how many
# bare SYNs went out and whether this machine ever sent an ACK. Those two numbers are the verdict:
#
#   an ACK was sent        the handshake completed - nothing sends an ACK for a SYN-ACK it never got
#   several SYNs, no ACK   BLOCKED: Windows retried the SYN (measured at +1 s, +2 s, +4 s on the same
#                          local port) and nothing ever answered
#   one SYN, no ACK        inconclusive - the capture ended before a retry was due
#
# All of that is read from outbound packets alone, which is all this capture holds. The socket table
# is sampled only every two seconds and would miss short attempts; the packets miss nothing.
function Measure-CaptureFile {
    param([string]$Path, [hashtable]$UdpOwners, [hashtable]$TcpOwners)

    # Per-packet fields rather than -z conv,udp: the conversation table is fixed-width with unit
    # suffixes that change with magnitude ("0 bytes" / "1 kB"), which makes a parser fragile.
    #
    # The output is piped rather than collected into a variable. A long session is hundreds of
    # thousands of rows, and holding them all as PowerShell strings before folding them up costs
    # far more memory than the numbers they turn into.
    $udpStats = @{}
    $tcpStats = @{}
    $slack = 3.0   # one poll interval plus a little, so a packet at the edge is not lost

    & $tshark -r $Path -T fields -e frame.time_epoch -e ip.dst -e udp.srcport -e udp.dstport `
        -e tcp.srcport -e tcp.dstport -e tcp.flags |
        ForEach-Object {
            $parts = @($_ -split "`t")
            while ($parts.Count -lt 7) { $parts += '' }

            $ip = $parts[1].Trim()
            if ($ip -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { return }

            # Which half a packet belongs to is read from which port fields tshark filled in, not
            # from ip.proto: an empty field is unambiguous, and needs no knowledge of how a given
            # tshark version chooses to print a protocol number.
            if ($parts[2].Trim()) {
                $isTcp = $false; $srcPort = $parts[2].Trim(); $dstPort = $parts[3].Trim()
                $stats = $udpStats; $portOwners = $UdpOwners
            } elseif ($parts[4].Trim()) {
                $isTcp = $true; $srcPort = $parts[4].Trim(); $dstPort = $parts[5].Trim()
                $stats = $tcpStats; $portOwners = $TcpOwners
            } else {
                return
            }

            $when = 0.0
            [void][double]::TryParse($parts[0].Trim(), [ref]$when)

            if (-not $stats.ContainsKey($ip)) {
                $stats[$ip] = [pscustomobject]@{
                    Address = $ip; Packets = 0; Ports = @{}; Owners = @{}
                    First = 0.0; Last = 0.0; Flows = @{}
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

            if ($isTcp) {
                # tshark 4.x prints tcp.flags as hex (0x0002); a decimal is accepted too, in case an
                # older build prints it that way.
                $flags = 0
                $raw = $parts[6].Trim()
                if ($raw -match '^0x([0-9a-fA-F]+)$') { $flags = [Convert]::ToInt32($Matches[1], 16) }
                else { [void][int]::TryParse($raw, [ref]$flags) }

                $flowKey = "$srcPort>$dstPort"
                if (-not $entry.Flows.ContainsKey($flowKey)) {
                    $entry.Flows[$flowKey] = [pscustomobject]@{ Syns = 0; Acked = $false }
                }
                $flow = $entry.Flows[$flowKey]
                $syn = ($flags -band 0x02) -ne 0
                $ack = ($flags -band 0x10) -ne 0
                if ($syn -and -not $ack) { $flow.Syns++ }
                elseif ($ack) { $flow.Acked = $true }
            }

            if ($srcPort -and $portOwners.ContainsKey([int]$srcPort)) {
                foreach ($owner in $portOwners[[int]$srcPort].Keys) {
                    $window = $portOwners[[int]$srcPort][$owner]
                    if ($when -ge ($window.First - $slack) -and $when -le ($window.Last + $slack)) {
                        $entry.Owners[$owner] = $true
                    }
                }
            }
        }

    return [pscustomobject]@{ Udp = $udpStats; Tcp = $tcpStats }
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
    $udpOwners = @{}
    $tcpOwners = @{}
    $pidNames = @{}
    $sample = {
        if ($captureUdp) { Update-PortOwners -PortOwners $udpOwners -PidNames $pidNames -OnlyProcess $OnlyProcess -Kind udp }
        if ($captureTcp) { Update-PortOwners -PortOwners $tcpOwners -PidNames $pidNames -OnlyProcess $OnlyProcess -Kind tcp }
    }

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
    & $sample

    $started = Get-Date
    while (-not $proc.HasExited) {
        & $sample
        Read-ControlKeys
        if ($script:StopRequested) { break }
        if (& $ShouldStop) { break }
        Start-Sleep -Milliseconds 2000
    }

    # dumpcap leaving on its own means one of its autostops fired while the session was still going,
    # and the rest of the session is simply not in the file. That used to pass without a word. It
    # matters far more with TCP included: the filter cannot tell processes apart, so a Steam or
    # browser download running alongside is captured in full and can reach the file cap in minutes.
    $endedByDumpcap = $proc.HasExited -and -not $script:StopRequested

    if (-not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force
        Start-Sleep -Milliseconds 500
    }
    $script:LiveCapture = $null

    if ($endedByDumpcap) {
        Write-Warning ("dumpcap stopped by itself before the session ended - the $DurationMinutes-minute " +
                       "limit or the $([int]($maxCaptureKb / 1024)) MB file cap. Only what came before is analysed. " +
                       "If TCP was included, close downloads (Steam, browsers) while capturing: every " +
                       "program's traffic is recorded and only sorted by process afterwards.")
    }

    $elapsed = [int]((Get-Date) - $started).TotalSeconds
    if (-not (Test-Path $captureFile)) {
        Write-Warning "No capture file produced. Try running as Administrator."
        $script:LiveCaptureFile = $null
        return $null
    }

    $sizeMb = [math]::Round((Get-Item $captureFile).Length / 1MB, 1)
    Write-Host "==> $Label finished after ${elapsed}s, $sizeMb MB - analysing"

    $stats = Measure-CaptureFile -Path $captureFile -UdpOwners $udpOwners -TcpOwners $tcpOwners

    if (-not $KeepCapture) { Remove-Item $captureFile -Force -ErrorAction SilentlyContinue }
    else { Write-Host "    Raw capture kept at $captureFile" }
    $script:LiveCaptureFile = $null

    if ($stats.Udp.Count -eq 0 -and $stats.Tcp.Count -eq 0) {
        Write-Warning "The capture is empty. Wrong interface, or the game sent nothing."
        return $null
    }

    $stats | Add-Member -NotePropertyName Seconds -NotePropertyValue $elapsed
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

    foreach ($row in ($rows | Select-Object -First 500)) {
        $ports = ($row.Ports.Keys | Sort-Object { [int]$_ } | Select-Object -First 4) -join ','
        $owners = ($row.Owners.Keys | Sort-Object) -join ','
        if (-not $owners) { $owners = '?' }
        $flag = ''
        if ($probeAddresses -contains $row.Address) { $flag = '  <- datacentre probe, NOT routed' }
        elseif ($acceptedAddresses -contains $row.Address) { $flag = '  <- kept' }
        Write-Host ("    {0,-18} {1,9}  {2,-22} {3}{4}" -f $row.Address, $row.Packets, $ports, $owners, $flag)
    }
    if ($rows.Count -gt 500) {
        Write-Host ("    ... and {0} more, all counted" -f ($rows.Count - 500))
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

# One TCP destination's verdict, from its flows. See Measure-CaptureFile for what each count means.
function Get-TcpOutcome {
    param($Row)

    $ok = 0; $blocked = 0; $pending = 0; $retries = 0
    foreach ($flow in $Row.Flows.Values) {
        if ($flow.Acked) { $ok++ }
        elseif ($flow.Syns -ge 2) { $blocked++; $retries += $flow.Syns - 1 }
        elseif ($flow.Syns -eq 1) { $pending++ }
    }

    if ($blocked -gt 0 -and $ok -eq 0) { $text = "BLOCKED - $blocked attempt(s), no SYN answered" }
    elseif ($blocked -gt 0) { $text = "$ok connected, $blocked BLOCKED" }
    elseif ($ok -gt 0) { $text = "connected ($ok)" }
    elseif ($pending -gt 0) { $text = 'no answer yet - capture ended' }
    else { $text = '?' }

    return [pscustomobject]@{ Ok = $ok; Blocked = $blocked; Pending = $pending; Retries = $retries; Text = $text }
}

<#
.SYNOPSIS
    Prints the TCP half of a session and appends it to tcp-sessions.txt.

.DESCRIPTION
    Diagnosis only, and deliberately a different file with a different shape from observed.txt.
    Nothing reads tcp-sessions.txt; it exists to be looked at, or sent to someone.

    One block per session, appended, rather than one merged row per address like observed.txt.
    The question here is "what failed THIS time the lobby hung", and a merged table would blur a
    blocked attempt tonight into a successful one from last week.

    Blocked destinations are listed first. They are what this mode is for, and they are also the
    ones with the fewest packets - a SYN retried three times is four packets - so sorted by volume
    alone they would sink to the bottom under the store and the CDNs.
#>
function Write-TcpResult {
    param([hashtable]$Stats, [string]$OnlyProcess, [string]$Label, [int]$Seconds)

    Write-Host ""
    Write-Host "==> TCP - $Label" -ForegroundColor Cyan

    if (-not $Stats -or $Stats.Count -eq 0) {
        Write-Warning "No TCP in this capture."
        return
    }

    $rows = @($Stats.Values)
    if ($OnlyProcess) { $rows = @($rows | Where-Object { $_.Owners.Keys -contains $OnlyProcess }) }
    if ($rows.Count -eq 0) {
        Write-Warning "No TCP connection was attributed to $OnlyProcess."
        return
    }

    $scored = @(foreach ($row in $rows) { [pscustomobject]@{ Row = $row; Outcome = (Get-TcpOutcome $row) } })
    $scored = @($scored | Sort-Object -Property @{ Expression = { $_.Outcome.Blocked -gt 0 }; Descending = $true },
                                                @{ Expression = { $_.Row.Packets }; Descending = $true })

    # A capture read back with -FromFile has no session clock, so its length comes from the packets.
    if ($Seconds -le 0) {
        $firsts = @($Stats.Values | Where-Object { $_.First -gt 0 } | ForEach-Object { $_.First })
        $lasts = @($Stats.Values | ForEach-Object { $_.Last })
        if ($firsts.Count -gt 0) {
            $Seconds = [int][math]::Round(($lasts | Measure-Object -Maximum).Maximum - ($firsts | Measure-Object -Minimum).Minimum)
        }
    }

    # The result column is sized for its longest text, "N connected, N BLOCKED" and
    # "BLOCKED - N attempt(s), no SYN answered", with room for a two-digit N.
    $format = '    {0,-18} {1,8} {2,6}  {3,-18} {4,-44} {5}'
    Write-Host ($format -f 'Address', 'Packets', 'Secs', 'TCP ports', 'Result', 'Process')
    Write-Host ($format -f '-------', '-------', '----', '---------', '------', '-------')

    $lines = New-Object System.Collections.Generic.List[string]
    $blockedCount = 0
    $shown = 0
    foreach ($item in $scored) {
        $row = $item.Row
        $seconds = 0
        if ($row.Last -gt $row.First) { $seconds = [int][math]::Round($row.Last - $row.First) }
        $ports = ($row.Ports.Keys | Sort-Object { [int]$_ } | Select-Object -First 4) -join ','
        $owners = ($row.Owners.Keys | Sort-Object) -join ','
        if (-not $owners) { $owners = '?' }
        if ($item.Outcome.Blocked -gt 0) { $blockedCount++ }

        # Arguments via an array, for the same reason as in Merge-ObservedFile.
        $fields = @($row.Address, $row.Packets, $seconds, $ports, $item.Outcome.Text, $owners)
        $line = $format -f $fields
        $lines.Add($line.Substring(4))

        if ($shown -lt 60) {
            $colour = 'Gray'
            if ($item.Outcome.Blocked -gt 0) { $colour = 'Red' }
            Write-Host $line -ForegroundColor $colour
            $shown++
        }
    }
    if ($scored.Count -gt 60) {
        Write-Host ("    ... and {0} more, all written to {1}" -f ($scored.Count - 60), (Split-Path $TcpOutputPath -Leaf))
    }

    if (-not (Test-Path $TcpOutputPath)) {
        Set-Content -Path $TcpOutputPath -Encoding ascii -Value @(
            '# TCP destinations of the watched process, one block per capture session,',
            '# written by Capture-GameTraffic.ps1 -Protocol tcp or -Protocol all.',
            '#',
            '# FOR DIAGNOSIS ONLY. Nothing reads this file, and nothing in it may be copied into',
            '# observed.txt: everything there is widened to a /20 and routed, and these are the',
            '# lobby, login, store, CDN and anti-cheat front doors - shared addresses that carry',
            '# everybody else''s traffic as well.',
            '#',
            '# BLOCKED means every SYN the game sent to that address went unanswered - Windows',
            '# retried it on the same port and nothing ever came back. A firewall, an ISP filter or a',
            '# dead route looks exactly like this, and it is what a lobby stuck on "Initializing..."',
            '# should show when the cause is the network.')
    }
    $block = New-Object System.Collections.Generic.List[string]
    $block.Add('')
    $heading = @((Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Label, $Seconds, $scored.Count, $blockedCount)
    $block.Add('## {0}  {1}, {2} s, {3} destination(s), {4} blocked' -f $heading)
    # "# " eats two characters of the address column's padding, so the heading still lines up with
    # the rows beneath it.
    $columns = ($format -f 'address', 'packets', 'secs', 'tcp ports', 'result', 'process').Substring(4)
    $block.Add('# address' + $columns.Substring(9))
    foreach ($l in $lines) { $block.Add($l) }
    Add-Content -Path $TcpOutputPath -Value $block.ToArray() -Encoding ascii

    Write-Host ""
    if ($blockedCount -gt 0) {
        Write-Host "==> $blockedCount TCP destination(s) never answered." -ForegroundColor Red
    } else {
        Write-Host "==> Every TCP connection the game attempted was answered." -ForegroundColor Green
    }
    Write-Host "    Written to $(Split-Path $TcpOutputPath -Leaf). Diagnosis only - never copied into observed.txt."
    if ($OnlyProcess) {
        # Measured, not assumed: a curl request that connected, fetched a page and closed in under
        # a second was in the capture but not in this table, because no two-second sample of the
        # socket table ever saw it. Blocked attempts cannot slip through that gap - retrying the
        # SYN keeps them open for several seconds - but a quick success can, so say so rather than
        # let an empty-looking table read as "the game made no other connections".
        Write-Host "    A connection that opened and closed within about two seconds cannot be tied to $OnlyProcess"
        Write-Host "    and is not listed. Blocked ones always last longer than that, so none are missed."
    }
}

# Hands each half of a session to its own writer. The UDP half is the profile's and is untouched
# by -Protocol tcp; the TCP half never reaches observed.txt or landmarks-observed.txt.
function Write-CaptureResult {
    param($Stats, [string]$OnlyProcess, [string]$Label)

    if (-not $Stats) { return }
    if ($captureUdp) { $null = Write-SessionResult -Stats $Stats.Udp -OnlyProcess $OnlyProcess }
    if ($captureTcp) {
        $seconds = 0
        if ($Stats.PSObject.Properties['Seconds']) { $seconds = $Stats.Seconds }
        Write-TcpResult -Stats $Stats.Tcp -OnlyProcess $OnlyProcess -Label $Label -Seconds $seconds
    }
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
        $stats = Measure-CaptureFile -Path $FromFile -UdpOwners @{} -TcpOwners @{}
        Write-CaptureResult -Stats $stats -OnlyProcess '' -Label (Split-Path $FromFile -Leaf)
        if ($captureUdp) {
            Write-Host ""
            Write-Host "    Next: .\Build-PubgProfile.ps1"
        }
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
        Write-CaptureResult -Stats $stats -OnlyProcess '' -Label 'Capture'
        if ($captureUdp) {
            Write-Host ""
            Write-Host "    Next: .\Build-PubgProfile.ps1"
        }
        return
    }

    Write-Host ""
    Write-Host "==> Resident mode, watching for $WatchProcess.exe - capturing $($Protocol.ToUpper())" -ForegroundColor Cyan
    if ($captureTcp) {
        Write-Host "    TCP is included: close downloads (Steam, browsers) while this runs, they fill the capture."
        Write-Host "    TCP results go to $(Split-Path $TcpOutputPath -Leaf) and never into observed.txt."
    }
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
        Write-CaptureResult -Stats $stats -OnlyProcess $WatchProcess -Label "Session $sessionCount"

        if ($script:StopRequested) { break }

        Write-Host ""
        if ($captureUdp) { Write-Host "==> Waiting for $WatchProcess.exe again. Ctrl+C to stop, then run .\Build-PubgProfile.ps1" }
        else { Write-Host "==> Waiting for $WatchProcess.exe again. Ctrl+C to stop." }
        Write-Host ""

        # The process table can still list a closing process for a moment; do not re-trigger on it.
        Start-Sleep -Seconds 5
    }

    Write-Host ""
    # TCP never feeds the profile, so a TCP-only run has nothing for Build-PubgProfile.ps1 to read.
    if ($captureUdp) { Write-Host "==> Stopped after $sessionCount session(s). Next: .\Build-PubgProfile.ps1" -ForegroundColor Cyan }
    else { Write-Host "==> Stopped after $sessionCount session(s). TCP results are in $(Split-Path $TcpOutputPath -Leaf)." -ForegroundColor Cyan }
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
