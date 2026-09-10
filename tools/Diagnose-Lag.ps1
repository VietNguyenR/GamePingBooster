<#
.SYNOPSIS
    Finds WHERE a lag spike is, while it is happening: this PC, the home network, the ISP's last
    mile, the ISP's international transit, or the relay.

.DESCRIPTION
    Run it DURING the lag. Afterwards it has nothing to look at - the whole method is to sample
    every segment of the path at the same moment and compare them against each other.

    Why the segments are compared rather than judged one at a time: there is no absolute number
    that means "bad" at every distance. 8 ms of jitter to your own router is a catastrophe; 8 ms
    of jitter to Singapore is a Tuesday. So each rung is scored against a threshold that scales
    with its own distance, and then - this is the part that actually locates the fault - the
    verdict picks the FIRST rung that is bad and stays bad at every rung beyond it.

    That last rule is what makes the answer trustworthy. A fault propagates outward: if the line
    into your house is congested, everything past it is congested too. A rung that looks bad on
    its own while everything beyond it looks fine is not a fault, it is a router deprioritising
    pings aimed at its own control plane - which most of them do. Without this rule the tool
    would blame a random middle hop several times a week.

    The rungs:

      1  home router        the default gateway - your Wi-Fi, cable and router
      2  ISP access         first hop past your router, or the CGNAT address if there is one
      3  ISP domestic core  the last hop before the RTT jumps - i.e. still inside the country
      4  relay              the relay's public address, over the physical path
         landmark           a DIFFERENT network in the same region, as a lateral control
      5  relay process      the service's own live keepalive RTT, read over the named pipe
      6  in game            the service's measured game ping, end to end

    Rung 4 against the landmark separates "the whole international path is bad" from "this one
    provider is bad", and rung 4 against rung 5 separates "the path to the relay is bad" from
    "the relay box is bad" - ICMP to the VPS and our own UDP to relayd cross the same wire, so
    when only the second one suffers, the wire is fine and the box is not.

    Rungs 5 and 6 are READ from the running service, never measured by opening a session of our
    own. A second handshake during a match can land inside the relay's session-resume window and
    knock the live client off its own session - diagnosing a lag spike by causing a worse one.

    Nothing here needs Administrator, nothing is written to the routing table, and nothing is
    sent anywhere.

    Every run leaves two files under %LOCALAPPDATA%\GamePingBooster. The history is one compact
    line per run, and it is what makes the thresholds self-calibrating: run this once while
    things feel FINE and, after three such runs, "bad" stops meaning a generic number and starts
    meaning worse than this connection's own normal. The report is the whole run - the trace, the
    verdict, and every raw sample in the order it arrived - written to be read afterwards or sent
    to someone. `.\gpb.ps1 diag` folds the newest three into its bundle.

    Neither file can contain a credential: this tool never opens the PSK, a licence token or
    gpb.conf.

.PARAMETER Seconds
    How long to sample. The default is enough to see jitter; raise it if the lag comes in waves.

.EXAMPLE
    .\gpb.ps1 lag
    The whole point: no arguments, during the lag.

.EXAMPLE
    .\gpb.ps1 lag 60
    A longer window, for lag that comes and goes every few seconds.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][int]$Seconds = 20
)

$ErrorActionPreference = 'Stop'

# Everything printed is also kept, so the saved report is word for word what was on screen.
# Two renderings of the same run would drift, and the one you send is the one nobody checked.
$script:transcript = New-Object System.Collections.Generic.List[string]

function Emit($text, $colour) {
    if ($null -eq $text) { $text = '' }
    $script:transcript.Add([string]$text)
    if ($colour) { Write-Host $text -ForegroundColor $colour } else { Write-Host $text }
}

function Say($msg) { Emit "==> $msg" 'Cyan' }
function Warn($msg) { Emit "    $msg" 'Yellow' }
function Note($msg) { Emit "    $msg" 'DarkGray' }

# Every discovery step below is best-effort: a machine with no Wi-Fi, a VPS that drops ICMP, a
# service that is not running. None of those are errors - they are one less piece of evidence,
# and the verdict says so rather than failing.
function Invoke-Safely($block) {
    try { & $block } catch { $null }
}

if ($Seconds -lt 5) { $Seconds = 5 }
if ($Seconds -gt 300) { $Seconds = 300 }

# =============================================================================== statistics

function Get-Percentile([double[]]$values, [double]$p) {
    if ($null -eq $values -or $values.Count -eq 0) { return $null }
    $sorted = @($values | Sort-Object)
    $idx = [int][math]::Ceiling(($p / 100.0) * $sorted.Count) - 1
    if ($idx -lt 0) { $idx = 0 }
    if ($idx -ge $sorted.Count) { $idx = $sorted.Count - 1 }
    return [double]$sorted[$idx]
}

# Mean absolute difference between consecutive samples, not the standard deviation.
#
# Standard deviation measures spread around an average, which a slow steady drift produces just
# as well as a packet that arrives 90 ms late. A game only ever feels the second one: what breaks
# interpolation is the change from one packet to the next. This is the same quantity RTP calls
# interarrival jitter, for the same reason.
function Get-Jitter([double[]]$values) {
    if ($null -eq $values -or $values.Count -lt 2) { return $null }
    $sum = 0.0
    for ($i = 1; $i -lt $values.Count; $i++) {
        $sum += [math]::Abs($values[$i] - $values[$i - 1])
    }
    return $sum / ($values.Count - 1)
}

function New-Stats($samples, $sent) {
    $arr = @($samples)
    $recv = $arr.Count
    $loss = 0.0
    if ($sent -gt 0) { $loss = 100.0 * ($sent - $recv) / $sent }

    $s = [pscustomobject]@{
        Sent     = $sent
        Received = $recv
        LossPct  = $loss
        P50      = $null
        P95      = $null
        Max      = $null
        Jitter   = $null
    }
    if ($recv -gt 0) {
        $s.P50 = Get-Percentile $arr 50
        $s.P95 = Get-Percentile $arr 95
        $s.Max = ($arr | Measure-Object -Maximum).Maximum
        $s.Jitter = Get-Jitter $arr
        if ($null -eq $s.Jitter) { $s.Jitter = 0.0 }
    }
    return $s
}

# Thresholds scale with distance, because the same millisecond means different things at
# different distances. Loss does not scale: a lost packet is a lost packet at any range.
function Get-BadReason($s, $bestP50) {
    if ($null -eq $s -or $s.Received -eq 0) { return $null }

    $jitterLimit = [math]::Max(4.0, 0.25 * $s.P50)
    $spikeLimit = [math]::Max(15.0, 1.0 * $s.P50)

    $reasons = @()
    if ($s.LossPct -gt 2.0) { $reasons += ("{0:N0}% loss" -f $s.LossPct) }
    if ($s.Jitter -gt $jitterLimit) { $reasons += ("jitter {0:N1} ms" -f $s.Jitter) }
    if (($s.P95 - $s.P50) -gt $spikeLimit) { $reasons += ("spikes to {0:N0} ms" -f $s.P95) }

    # The baseline arrives only after this connection has been measured while healthy. Until then
    # the thresholds above are all there is, and they are deliberately loose - a tool that cries
    # wolf on a fine connection gets ignored on the day it is right.
    if ($null -ne $bestP50) {
        $allowed = $bestP50 + [math]::Max(5.0, 0.3 * $bestP50)
        if ($s.P50 -gt $allowed) {
            $reasons += ("{0:N0} ms against {1:N0} ms normally" -f $s.P50, $bestP50)
        }
    }

    if ($reasons.Count -eq 0) { return $null }
    return ($reasons -join ', ')
}

# Does the symptom seen at $inner survive out to $outer?
#
# Physically it has to. Every packet that reaches the outer target has already crossed the inner
# one, so jitter and loss introduced at the inner rung are still in the outer rung's numbers -
# nothing further along the path can undo them. When the outer rung is visibly CALMER than the
# inner one, the inner reading was never about the path at all: it is a router answering pings
# to its own address slowly while forwarding everything else perfectly. That is normal, it is
# what most routers do, and it is the single most common way a tool like this blames the wrong
# box.
#
# This is deliberately NOT "is the outer rung also bad by its own threshold". Those thresholds
# scale with distance, so by the time a fault at the home router reaches the relay rung it is
# well inside the relay's tolerance - which is how an earlier version printed three red rows
# under the verdict "nothing on the path is misbehaving", during a real Wi-Fi hiccup.
function Test-Inherits($inner, $outer) {
    # A mute outer rung contradicts nothing; silence is not evidence either way.
    if ($null -eq $outer -or $outer.Received -eq 0) { return $true }

    # Loss propagates strictly. One point of slack for rounding on a short window.
    if (($outer.LossPct + 1.0) -lt $inner.LossPct) { return $false }

    # Jitter accumulates along a path, so a real inner fault always leaves the outer rung at
    # least as unsettled. Below 2 ms there is nothing to carry and the ratio is only noise.
    if ($null -ne $inner.Jitter -and $inner.Jitter -ge 2.0) {
        if ($outer.Jitter -lt 0.6 * $inner.Jitter) { return $false }
    }
    return $true
}

# =============================================================================== addresses

function Get-AddrClass([string]$ip) {
    $addr = Invoke-Safely { [ipaddress]::Parse($ip) }
    if ($null -eq $addr -or $addr.AddressFamily -ne 'InterNetwork') { return 'other' }
    $b = $addr.GetAddressBytes()

    if ($b[0] -eq 10) { return 'private' }
    if ($b[0] -eq 127) { return 'private' }
    if ($b[0] -eq 192 -and $b[1] -eq 168) { return 'private' }
    if ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) { return 'private' }
    if ($b[0] -eq 169 -and $b[1] -eq 254) { return 'private' }

    # 100.64/10 is carrier-grade NAT. It is not the public internet and it is not your house
    # either - it is the ISP's access network, and in Vietnam it is extremely common. Calling it
    # "public" would put the ISP's own equipment in the wrong rung.
    if ($b[0] -eq 100 -and $b[1] -ge 64 -and $b[1] -le 127) { return 'cgnat' }

    return 'public'
}

# Traceroute done with raw TTLs rather than by parsing tracert.exe.
#
# tracert's output is localised - on a Vietnamese Windows the words change and a regex written
# against the English text finds nothing. Sending our own echoes with an increasing TTL and
# reading the TtlExpired replies gives the same hops as numbers, in any locale.
#
# The hop's RTT comes from a Stopwatch around the call and NOT from PingReply.RoundtripTime.
# That property is only populated when Status is Success; every TtlExpired reply - which is
# every intermediate hop, i.e. all of them - reports 0. Trusting it made the entire trace look
# like a flat 0 ms, so the international step was never found and the domestic rung silently
# vanished from the ladder. The measurement was fine all along; the field was empty.
function Get-TracePath([string]$target, [int]$maxHops, [int]$timeoutMs) {
    $ping = New-Object System.Net.NetworkInformation.Ping
    $payload = New-Object byte[] 32
    $hops = @()

    try {
        for ($ttl = 1; $ttl -le $maxHops; $ttl++) {
            $options = New-Object System.Net.NetworkInformation.PingOptions($ttl, $false)
            $addr = $null
            $best = $null
            $arrived = $false

            for ($attempt = 0; $attempt -lt 2; $attempt++) {
                $clock = [System.Diagnostics.Stopwatch]::StartNew()
                $reply = Invoke-Safely { $ping.Send($target, $timeoutMs, $payload, $options) }
                $clock.Stop()
                if ($null -eq $reply) { continue }
                if ($reply.Status -eq 'TtlExpired' -or $reply.Status -eq 'Success') {
                    $addr = $reply.Address.ToString()
                    $rtt = $clock.Elapsed.TotalMilliseconds
                    if ($null -eq $best -or $rtt -lt $best) { $best = $rtt }
                    if ($reply.Status -eq 'Success') { $arrived = $true }
                }
            }

            if ($null -ne $addr) {
                $hops += [pscustomobject]@{
                    Ttl     = $ttl
                    Address = $addr
                    Rtt     = $best
                    Class   = Get-AddrClass $addr
                }
            }
            if ($arrived) { break }
        }
    } finally {
        $ping.Dispose()
    }

    return $hops
}

# The domestic/international boundary, derived from the trace rather than from a hardcoded list
# of Vietnamese addresses that would rot within a year.
#
# A submarine cable is the only thing on this path that adds tens of milliseconds in a single
# hop, so the largest RTT step in the trace is where the country ends. Below 15 ms there is no
# step worth calling one, and this returns nothing - which simply means the domestic rung is
# skipped rather than invented.
#
# PRIVATE AND CGNAT HOPS COUNT. They are the ISP's own routers, and on a Vietnamese consumer
# line the entire domestic path can be numbered out of 10/8, 172.16/12 and 100.64/10 - measured
# here on Viettel: hops 2-5 are private at 3-10 ms and the first PUBLIC hop is already 44 ms,
# i.e. already across the cable. An earlier version of this looked at public hops only, on the
# theory that private meant "inside the house", and so could never find a domestic hop on this
# path at all: it picked a 43 ms hop as the in-country reference and presented it next to a
# 43 ms relay in Singapore.
#
# Hence the second condition, and note what it is measured against. The whole point of rung 3 is
# to be a clean in-country reading that is CLEARLY NEARER THAN THE RELAY - that is what makes
# "fine at home, bad abroad" a statement about the ISP rather than a coincidence. Comparing the
# candidate against the next hop instead cannot do that job: a trace that steps from Singapore to
# somewhere further still has a perfectly good-looking step in it, and the near side of it is
# 44 ms away in another country. So the ceiling is a fraction of the RELAY's own RTT, and when
# the relay never answered, the next-hop comparison is the weaker fallback.
#
# It is better to report no domestic rung than a fictional one.
function Get-InternationalStep($hops, $targetRtt) {
    $seen = @($hops | Where-Object { $null -ne $_.Rtt })
    if ($seen.Count -lt 2) { return $null }

    $bestGap = 0.0
    $bestIdx = -1
    for ($i = 1; $i -lt $seen.Count; $i++) {
        $gap = $seen[$i].Rtt - $seen[$i - 1].Rtt
        if ($gap -gt $bestGap) { $bestGap = $gap; $bestIdx = $i }
    }
    if ($bestIdx -lt 1 -or $bestGap -lt 15.0) { return $null }

    $near = $seen[$bestIdx - 1]
    $far = $seen[$bestIdx]

    $ceiling = 0.7 * $far.Rtt
    if ($null -ne $targetRtt -and $targetRtt -gt 0) {
        $ceiling = [math]::Min($ceiling, 0.6 * $targetRtt)
    }
    if ($near.Rtt -gt $ceiling) { return $null }

    return [pscustomobject]@{
        Gap      = $bestGap
        Domestic = $near
    }
}

# A handful of ordinary echoes, best of N. Unlike the TTL walk this one asks for Status=Success,
# so PingReply.RoundtripTime is populated and there is nothing to time by hand.
function Measure-Rtt([string]$ip, [int]$count) {
    $ping = New-Object System.Net.NetworkInformation.Ping
    $best = $null
    try {
        for ($i = 0; $i -lt $count; $i++) {
            $reply = Invoke-Safely { $ping.Send($ip, 1000) }
            if ($null -ne $reply -and $reply.Status -eq 'Success') {
                $rtt = [double]$reply.RoundtripTime
                if ($null -eq $best -or $rtt -lt $best) { $best = $rtt }
            }
        }
    } finally {
        $ping.Dispose()
    }
    return $best
}

function Get-InterfaceFor([string]$ip) {
    $route = Invoke-Safely { Find-NetRoute -RemoteIPAddress $ip -ErrorAction Stop | Select-Object -First 1 }
    if ($null -eq $route) { return $null }
    $adapter = Invoke-Safely { Get-NetAdapter -InterfaceIndex $route.InterfaceIndex -ErrorAction Stop }
    if ($null -eq $adapter) { return $null }
    return $adapter.Name
}

# =============================================================================== the service

# Read the live counters instead of measuring the tunnel ourselves. See the header: opening our
# own session during a match can evict the real one.
function Read-ServiceStatus([int]$timeoutMs = 1500) {
    $pipe = $null
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
            '.', 'GamePingBooster', [System.IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect($timeoutMs)
        $reader = New-Object System.IO.StreamReader($pipe)
        $writer = New-Object System.IO.StreamWriter($pipe)
        $writer.AutoFlush = $true
        $writer.WriteLine('{"v":2,"verb":"status"}')
        $task = $reader.ReadLineAsync()
        if ($task.Wait($timeoutMs) -and $task.Result) {
            return ($task.Result | ConvertFrom-Json)
        }
        return $null
    } catch {
        return $null
    } finally {
        if ($null -ne $pipe) { Invoke-Safely { $pipe.Dispose() } }
    }
}

# =============================================================================== history

# Deliberately under LOCALAPPDATA and not ProgramData: this must never need Administrator. A
# diagnostic that asks for an elevated prompt is a diagnostic nobody runs at the moment it would
# have been useful.
$historyDir = Join-Path $env:LOCALAPPDATA 'GamePingBooster'
$historyPath = Join-Path $historyDir 'lag-history.jsonl'
$reportDir = Join-Path $historyDir 'lag-reports'

function Get-Baselines {
    if (-not (Test-Path -LiteralPath $historyPath)) { return @{} }

    $runs = @()
    foreach ($line in (Get-Content -LiteralPath $historyPath -ErrorAction SilentlyContinue)) {
        if (-not $line.Trim()) { continue }
        $obj = Invoke-Safely { $line | ConvertFrom-Json }
        if ($null -ne $obj) { $runs += $obj }
    }

    # Three runs, because two can both have been taken during the same bad evening, and would
    # then hand the verdict a "normal" that is itself broken.
    if ($runs.Count -lt 3) { return @{} }

    $best = @{}
    foreach ($run in $runs) {
        foreach ($row in @($run.rows)) {
            if ($null -eq $row.p50) { continue }
            $key = [string]$row.key
            if (-not $best.ContainsKey($key) -or $row.p50 -lt $best[$key]) {
                $best[$key] = [double]$row.p50
            }
        }
    }
    return $best
}

function Save-Run($rows, $verdict) {
    try {
        if (-not (Test-Path -LiteralPath $historyDir)) {
            New-Item -ItemType Directory -Path $historyDir -Force | Out-Null
        }
        $record = [pscustomobject]@{
            at      = (Get-Date).ToString('o')
            verdict = $verdict
            rows    = @($rows | ForEach-Object {
                    [pscustomobject]@{
                        key    = $_.Key
                        addr   = $_.Address
                        p50    = $_.Stats.P50
                        p95    = $_.Stats.P95
                        jitter = $_.Stats.Jitter
                        loss   = $_.Stats.LossPct
                    }
                })
        }
        Add-Content -LiteralPath $historyPath -Encoding UTF8 `
            -Value ($record | ConvertTo-Json -Depth 6 -Compress)
    } catch {
        Warn "Could not write $historyPath - this run will not become a baseline."
    }
}

# The history line is a baseline: four numbers per rung, small enough that hundreds of runs stay
# cheap to parse. It is not a report, and it deliberately throws away the two things that answer
# "why" rather than "where" - the raw sample order, which is where a spike sits in the window,
# and the trace, which is how the rungs were chosen in the first place. So each run also writes a
# full report meant to be READ, by a person or by whoever they send it to.
#
# It carries no credential. This tool never opens the PSK, the licence token or gpb.conf, so
# unlike a diagnostics bundle there is nothing here to redact.
function Save-Report($targets, $hops, $step, $tunnelSamples, $gameSamples, $extra) {
    try {
        if (-not (Test-Path -LiteralPath $reportDir)) {
            New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
        }
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $path = Join-Path $reportDir "lag-$stamp.txt"

        $out = New-Object System.Collections.Generic.List[string]
        $out.Add('Game Ping Booster - lag diagnosis')
        $out.Add(('collected {0}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')))
        foreach ($k in $extra.Keys) { $out.Add(('{0,-14} {1}' -f $k, $extra[$k])) }
        $out.Add('')
        foreach ($line in $script:transcript) { $out.Add($line) }

        $out.Add('')
        $out.Add('=== how the rungs were chosen ===')
        $out.Add('')
        $out.Add(('{0,4}  {1,-18} {2,9}  {3}' -f 'ttl', 'address', 'rtt', 'class'))
        if (@($hops).Count -eq 0) {
            $out.Add('   (the trace returned nothing - every hop stayed silent)')
        }
        foreach ($h in @($hops)) {
            $out.Add(('{0,4}  {1,-18} {2,6:N1} ms  {3}' -f $h.Ttl, $h.Address, $h.Rtt, $h.Class))
        }
        if ($null -ne $step) {
            $out.Add('')
            $out.Add(('the step taken as the edge of the country: {0} at {1:N1} ms, then +{2:N1} ms' -f `
                        $step.Domestic.Address, $step.Domestic.Rtt, $step.Gap))
        } else {
            $out.Add('')
            $out.Add('no step of 15 ms or more was found, so no domestic rung was claimed')
        }

        # The order matters as much as the numbers: a spike in the first second and a spike that
        # arrives every fourth second are different faults, and no percentile can tell them apart.
        $out.Add('')
        $out.Add('=== raw samples, milliseconds, in the order they arrived ===')
        $out.Add('')
        foreach ($t in ($targets | Sort-Object Rung)) {
            $vals = if (@($t.Samples).Count -gt 0) { (@($t.Samples) -join ', ') } else { '(no reply all window)' }
            $out.Add(('{0} {1} [sent {2}]' -f $t.Key, $t.Address, $t.Sent))
            $out.Add(('  ' + $vals))
        }
        if (@($tunnelSamples).Count -gt 0) {
            $out.Add('relay-udp (keepalive RTT, read from the service)')
            $out.Add('  ' + (@($tunnelSamples) -join ', '))
        }
        if (@($gameSamples).Count -gt 0) {
            $out.Add('game (end to end, read from the service)')
            $out.Add('  ' + (@($gameSamples) -join ', '))
        }

        $out.Add('')
        $out.Add('No key, token or password is read by this tool, so none can be in this file.')

        Set-Content -LiteralPath $path -Value $out -Encoding UTF8

        # Keep the newest few. An unbounded folder of these is how a diagnostic quietly becomes
        # the thing that fills a disk.
        $old = Get-ChildItem -LiteralPath $reportDir -Filter 'lag-*.txt' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -Skip 30
        foreach ($f in @($old)) { Invoke-Safely { Remove-Item -LiteralPath $f.FullName -Force } }

        return $path
    } catch {
        Warn "Could not write the report: $_"
        return $null
    }
}

# =============================================================================== discovery

Say "Working out what to measure"

$root = Split-Path -Parent $PSScriptRoot

# --- the relay we are actually using -------------------------------------------------------
$status = Read-ServiceStatus
$relayEndpoint = $null
$regionName = $null

if ($null -ne $status) {
    $regionName = $status.gameRegionName
    if ($status.relayEndpoints -and @($status.relayEndpoints).Count -gt 0) {
        $relayEndpoint = @($status.relayEndpoints)[0]
    }
}

# The profile is the fallback, and also where the landmarks live. It is the same file the service
# reads, so the relay found here is the relay the service would use.
$gameProfile = $null
$profilePath = $null
foreach ($candidate in @((Join-Path $root 'config.json'), (Join-Path $root 'client\config.json'))) {
    if (-not (Test-Path -LiteralPath $candidate)) { continue }
    $cfg = Invoke-Safely { Get-Content -LiteralPath $candidate -Raw | ConvertFrom-Json }
    if ($null -eq $cfg -or -not $cfg.profilePath) { continue }
    $p = $cfg.profilePath
    if (-not [System.IO.Path]::IsPathRooted($p)) { $p = Join-Path $root $p }
    if (Test-Path -LiteralPath $p) { $profilePath = $p; break }
}
if ($profilePath) {
    $gameProfile = Invoke-Safely { Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json }
}

if (-not $relayEndpoint -and $null -ne $gameProfile -and $gameProfile.relays) {
    $first = @($gameProfile.relays)[0]
    if ($first) { $relayEndpoint = $first.endpoint }
}

if (-not $relayEndpoint) {
    Warn "No relay found in the service, config.json or the profile."
    Warn "Rungs 4-6 will be missing, which is most of the point. Try .\gpb.ps1 status first."
}

$relayIp = $null
if ($relayEndpoint) { $relayIp = ($relayEndpoint -split ':')[0] }

# --- a landmark in the same region, on somebody else's network -----------------------------
$landmarkIp = $null
if ($null -ne $gameProfile -and $gameProfile.games) {
    $regions = @($gameProfile.games[0].regions)
    $chosen = $null
    if ($regionName) { $chosen = $regions | Where-Object { $_.name -eq $regionName } | Select-Object -First 1 }
    if (-not $chosen -or @($chosen.landmarks).Count -eq 0) {
        $chosen = $regions | Where-Object { @($_.landmarks).Count -gt 0 } | Select-Object -First 1
    }
    if ($chosen -and @($chosen.landmarks).Count -gt 0) { $landmarkIp = @($chosen.landmarks)[0] }
}

# --- the local end -------------------------------------------------------------------------
$defaultRoute = Invoke-Safely {
    Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop |
        Sort-Object RouteMetric, InterfaceMetric | Select-Object -First 1
}
$gatewayIp = $null
if ($null -ne $defaultRoute) { $gatewayIp = $defaultRoute.NextHop }

$adapter = $null
if ($null -ne $defaultRoute) {
    $adapter = Invoke-Safely { Get-NetAdapter -InterfaceIndex $defaultRoute.InterfaceIndex -ErrorAction Stop }
}

$onWifi = $false
$wifiDetail = $null
if ($null -ne $adapter -and $adapter.PhysicalMediaType -like '*802.11*') {
    $onWifi = $true
    # netsh output is localised, so this pulls a number out rather than matching words. Failing
    # to parse it costs a detail line, not the verdict - being ON Wi-Fi is the part that matters.
    $wlan = Invoke-Safely { netsh wlan show interfaces 2>$null | Out-String }
    if ($wlan) {
        $signal = [regex]::Match($wlan, '(\d{1,3})\s*%')
        if ($signal.Success) { $wifiDetail = "signal $($signal.Groups[1].Value)%" }
    }
}

# --- the path, traced once, to pick the ISP rungs ------------------------------------------
$traceTarget = $relayIp
if (-not $traceTarget) { $traceTarget = '1.1.1.1' }

# Measured before the trace because the trace needs it: it is the yardstick that decides whether
# a candidate domestic hop is really in the country or just nearer than the one behind it.
$traceTargetRtt = Measure-Rtt $traceTarget 3
$hops = Get-TracePath $traceTarget 12 800

$accessHop = @($hops | Where-Object { $_.Class -eq 'cgnat' }) | Select-Object -First 1
if (-not $accessHop) {
    $accessHop = @($hops | Where-Object { $_.Class -eq 'public' }) | Select-Object -First 1
}
$step = Get-InternationalStep $hops $traceTargetRtt
$coreHop = $null
if ($null -ne $step) { $coreHop = $step.Domestic }

# =============================================================================== targets

$targets = @()
function Add-Target($rung, $key, $label, $address) {
    if (-not $address) { return }
    if ($script:targets | Where-Object { $_.Address -eq $address }) { return }
    $script:targets += [pscustomobject]@{
        Rung    = $rung
        Key     = $key
        Label   = $label
        Address = $address
        Via     = Get-InterfaceFor $address
        Sent    = 0
        Samples = @()
        Stats   = $null
        Bad     = $null
        Mute    = $false
    }
}

Add-Target 1 'gateway' 'home router' $gatewayIp
if ($accessHop) { Add-Target 2 'access' 'ISP access' $accessHop.Address }
if ($coreHop) { Add-Target 3 'core' 'ISP domestic' $coreHop.Address }
Add-Target 4 'relay' 'relay' $relayIp
Add-Target 4 'landmark' 'landmark' $landmarkIp

if ($targets.Count -eq 0) { throw "Nothing to measure - no gateway, no relay, no landmark." }

# The landmark is only a lateral control while it leaves by the same card as everything else.
# Once the tunnel is up it very likely sits inside a routed game CIDR, in which case pinging it
# measures the tunnel and not the ISP - a completely different statement, and one that would
# wreck the comparison if it were read as the original.
$physical = $null
if ($null -ne $adapter) { $physical = $adapter.Name }

$landmarkRow = $targets | Where-Object { $_.Key -eq 'landmark' }
$landmarkTunnelled = ($null -ne $landmarkRow -and $physical -and $landmarkRow.Via -and $landmarkRow.Via -ne $physical)

$relayRow = $targets | Where-Object { $_.Key -eq 'relay' }
$relayTunnelled = ($null -ne $relayRow -and $physical -and $relayRow.Via -and $relayRow.Via -ne $physical)

# =============================================================================== sampling

Say "Sampling every rung at once for $Seconds seconds"
Note "All targets are pinged on the same tick. Measuring them one after another would compare"
Note "segments from different minutes, and congestion does not sit still for that long."
Emit ""

$pingers = @{}
foreach ($t in $targets) { $pingers[$t.Key] = New-Object System.Net.NetworkInformation.Ping }

$tunnelPing = @()
$gamePing = @()
$gameDirect = $false
$serviceSeen = $false
$droppedFirst = $null
$droppedLast = $null

$startStats = $null
if ($null -ne $adapter) {
    $startStats = Invoke-Safely { Get-NetAdapterStatistics -Name $adapter.Name -ErrorAction Stop }
}
$startTime = Get-Date

try {
    for ($tick = 0; $tick -lt $Seconds; $tick++) {
        $tickStart = Get-Date
        $pending = @()

        foreach ($t in $targets) {
            $t.Sent++
            $pending += [pscustomobject]@{
                Target = $t
                Task   = $pingers[$t.Key].SendPingAsync($t.Address, 1000)
            }
        }

        # Read the service while the echoes are in flight - it costs nothing and keeps the tunnel
        # numbers on the same tick as the network ones.
        $live = Read-ServiceStatus 700
        if ($null -ne $live) {
            $serviceSeen = $true
            if ($null -ne $live.tunnelPingMs) { $tunnelPing += [double]$live.tunnelPingMs }
            if ($null -ne $live.gamePingMs) { $gamePing += [double]$live.gamePingMs }
            if ($live.gamePingDirect) { $gameDirect = $true }
            if ($null -ne $live.packetsDropped) {
                if ($null -eq $droppedFirst) { $droppedFirst = [long]$live.packetsDropped }
                $droppedLast = [long]$live.packetsDropped
            }
            if ($null -eq $status) { $status = $live }
        }

        foreach ($p in $pending) {
            $reply = Invoke-Safely { $p.Task.Result }
            if ($null -ne $reply -and $reply.Status -eq 'Success') {
                $p.Target.Samples += [double]$reply.RoundtripTime
            }
        }

        Write-Host "." -NoNewline -ForegroundColor DarkGray
        $elapsed = ((Get-Date) - $tickStart).TotalMilliseconds
        if ($elapsed -lt 1000) { Start-Sleep -Milliseconds ([int](1000 - $elapsed)) }
    }
} finally {
    foreach ($p in $pingers.Values) { Invoke-Safely { $p.Dispose() } }
}

Emit ""
Emit ""

$endStats = $null
if ($null -ne $adapter) {
    $endStats = Invoke-Safely { Get-NetAdapterStatistics -Name $adapter.Name -ErrorAction Stop }
}
$window = ((Get-Date) - $startTime).TotalSeconds

# =============================================================================== scoring

$baselines = Get-Baselines

foreach ($t in $targets) {
    $t.Stats = New-Stats $t.Samples $t.Sent
    $t.Mute = ($t.Stats.Received -eq 0)
    $best = $null
    if ($baselines.ContainsKey($t.Key)) { $best = $baselines[$t.Key] }
    $t.Bad = Get-BadReason $t.Stats $best
}

$tunnelStats = New-Stats $tunnelPing @($tunnelPing).Count
$gameStats = New-Stats $gamePing @($gamePing).Count

$tunnelBest = $null
if ($baselines.ContainsKey('relay-udp')) { $tunnelBest = $baselines['relay-udp'] }
$gameBest = $null
if ($baselines.ContainsKey('game')) { $gameBest = $baselines['game'] }

$tunnelBad = Get-BadReason $tunnelStats $tunnelBest
$gameBad = Get-BadReason $gameStats $gameBest

# =============================================================================== the table

$fmt = "{0,-5} {1,-26} {2,-20} {3,8} {4,8} {5,8} {6,6}  {7}"
Emit ($fmt -f 'rung', 'target', 'leaves by', 'p50', 'p95', 'jitter', 'loss', 'verdict') 'White'
Emit ("-" * 104) 'DarkGray'

function Show-Row($rung, $label, $address, $via, $s, $bad, $mute) {
    $name = $label
    if ($address) { $name = "$label $address" }
    if ($name.Length -gt 26) { $name = $name.Substring(0, 26) }
    $viaText = $via
    if (-not $viaText) { $viaText = '-' }
    if ($viaText.Length -gt 20) { $viaText = $viaText.Substring(0, 20) }

    if ($mute) {
        Emit ($fmt -f $rung, $name, $viaText, '-', '-', '-', '-', 'no reply (ignored)') 'DarkGray'
        return
    }

    $note = 'ok'
    if ($bad) { $note = $bad }
    $line = $fmt -f $rung, $name, $viaText,
        ("{0:N1}" -f $s.P50), ("{0:N1}" -f $s.P95), ("{0:N1}" -f $s.Jitter),
        ("{0:N0}%" -f $s.LossPct), $note

    $colour = 'Green'
    if ($bad) { $colour = 'Red' }
    Emit $line $colour
}

foreach ($t in ($targets | Sort-Object Rung)) {
    Show-Row $t.Rung $t.Label $t.Address $t.Via $t.Stats $t.Bad $t.Mute
}

if ($serviceSeen) {
    Show-Row 5 'relay process (UDP)' $null 'service keepalive' $tunnelStats $tunnelBad ($tunnelStats.Received -eq 0)
    $gameLabel = 'in game (estimated)'
    if ($gameDirect) { $gameLabel = 'in game (measured)' }
    Show-Row 6 $gameLabel $null 'service' $gameStats $gameBad ($gameStats.Received -eq 0)
}

Emit ""

# =============================================================================== local context

Say "This machine"
if ($null -ne $adapter) {
    $link = 'wired'
    if ($onWifi) { $link = 'Wi-Fi' }
    if ($wifiDetail) { $link = "$link, $wifiDetail" }
    Note "$($adapter.Name) - $link"
}
if ($null -ne $startStats -and $null -ne $endStats -and $window -gt 0) {
    $downMbps = ($endStats.ReceivedBytes - $startStats.ReceivedBytes) * 8 / $window / 1e6
    $upMbps = ($endStats.SentBytes - $startStats.SentBytes) * 8 / $window / 1e6
    Note ("this PC moved {0:N1} Mbps down / {1:N1} Mbps up while measuring" -f $downMbps, $upMbps)
    if ($downMbps -gt 20 -or $upMbps -gt 5) {
        Warn "That is not idle. Something on this PC is using the line - a download, an update,"
        Warn "a cloud sync, a stream. Stop it and run this again before reading anything below."
    }
}
if ($null -ne $droppedFirst -and $null -ne $droppedLast -and ($droppedLast - $droppedFirst) -gt 0) {
    Warn ("the service dropped {0} packets inside this PC during the window - that is not the" -f ($droppedLast - $droppedFirst))
    Warn "network. .\gpb.ps1 logs has the per-cause breakdown."
}
$gameProc = Invoke-Safely { Get-Process -Name 'TslGame' -ErrorAction Stop }
if ($null -eq $gameProc) {
    Note "TslGame.exe is not running, so the routes are not installed and rung 6 is idle."
}
if ($relayTunnelled) {
    Warn "The relay's own address is leaving by $($relayRow.Via), not $physical."
    Warn "The pinned /32 that keeps relay traffic out of the tunnel is missing - that is a"
    Warn "routing loop waiting to happen, and it makes rungs 4 and 5 measure the same thing."
}
Emit ""

# =============================================================================== verdict

# A fault propagates outward. So the culprit is the innermost rung that is bad AND stays bad all
# the way out; anything bad with clean rungs beyond it is a router deprioritising pings to
# itself, not a fault on the path. Mute targets are skipped entirely - a host that never answered
# has told us nothing, and guessing from silence is how a tool like this earns its reputation.
$ladder = @()
foreach ($t in ($targets | Where-Object { $_.Key -ne 'landmark' } | Sort-Object Rung)) {
    if ($t.Mute) { continue }
    $ladder += [pscustomobject]@{ Key = $t.Key; Bad = $t.Bad; Stats = $t.Stats }
}
if ($serviceSeen -and $tunnelStats.Received -gt 0) {
    $ladder += [pscustomobject]@{ Key = 'relay-udp'; Bad = $tunnelBad; Stats = $tunnelStats }
}
if ($serviceSeen -and $gameStats.Received -gt 0) {
    $ladder += [pscustomobject]@{ Key = 'game'; Bad = $gameBad; Stats = $gameStats }
}

$culprit = $null
$rejected = @()
for ($i = 0; $i -lt $ladder.Count; $i++) {
    if (-not $ladder[$i].Bad) { continue }
    $survives = $true
    for ($j = $i + 1; $j -lt $ladder.Count; $j++) {
        if (-not (Test-Inherits $ladder[$i].Stats $ladder[$j].Stats)) { $survives = $false; break }
    }
    if ($survives) { $culprit = $ladder[$i]; break }
    $rejected += $ladder[$i].Key
}

$landmarkUsable = ($null -ne $landmarkRow -and -not $landmarkRow.Mute -and -not $landmarkTunnelled)
$bar = "============================================================="

if ($null -eq $culprit) {
    $verdictText = 'clean'
    Emit $bar 'White'
    Emit " VERDICT: nothing on the path is misbehaving right now" 'Green'
    Emit $bar 'White'
    Emit ""
    if ($rejected.Count -gt 0) {
        Note ("Flagged and then dismissed: {0}." -f ($rejected -join ', '))
        Note "Each of those looked unsettled, but the rungs BEYOND them were calmer - and a fault"
        Note "cannot get quieter further down the same path. That is a router answering pings to"
        Note "its own address slowly while forwarding traffic perfectly, which is normal."
    } else {
        Note "Every rung that answered looks normal for its distance."
    }
    Note "Either the lag has passed, or it is not latency - a frame-time stall, a shader hitch"
    Note "and a network spike all feel the same from the chair. Run this the moment it happens"
    Note "next time; either way this run is now part of the baseline."
} else {
    $verdictText = $culprit.Key
    switch ($culprit.Key) {
        'gateway' {
            Emit $bar 'White'
            Emit " VERDICT: your own machine or your home network" 'Red'
            Emit $bar 'White'
            Emit ""
            Warn "The first hop - your own router - is already bad: $($culprit.Bad)"
            Warn "Nothing beyond your front door can fix this, and no relay anywhere can either."
            Emit ""
            if ($onWifi) {
                Note "You are on Wi-Fi, which is the single most likely cause. Try a cable once,"
                Note "even just to rule it out - it takes two minutes and settles the question."
            } else {
                Note "Check the cable and the router. A router that has been up for weeks with a"
                Note "full NAT table produces exactly this."
            }
            Note "Then check what else in the house is on the line. This tool only sees the"
            Note "traffic of THIS PC - a phone or a TV saturating the uplink is invisible to it"
            Note "and looks identical from here. The router's own traffic page will show it."
        }
        'access' {
            Emit $bar 'White'
            Emit " VERDICT: your ISP's last mile - the line into your house" 'Red'
            Emit $bar 'White'
            Emit ""
            Warn "Your router is clean; the first ISP hop past it is not: $($culprit.Bad)"
            Emit ""
            Note "This is the segment between your house and the ISP's local equipment. A relay"
            Note "closer to you would NOT help: every packet still crosses this exact wire before"
            Note "it can reach any relay at all."
            Note "This one is worth reporting to the ISP, and these numbers are what to report."
        }
        'core' {
            Emit $bar 'White'
            Emit " VERDICT: your ISP's domestic network" 'Red'
            Emit $bar 'White'
            Emit ""
            Warn "The line into your house is fine; the ISP's own domestic backbone is not:"
            Warn "$($culprit.Bad)"
            Emit ""
            Note "A relay inside the country sits on the far side of this, so chaining through"
            Note "one would not avoid it. Only the ISP can fix this segment."
        }
        'relay' {
            if ($landmarkUsable -and $landmarkRow.Bad) {
                Emit $bar 'White'
                Emit " VERDICT: your ISP's international transit" 'Red'
                Emit $bar 'White'
                Emit ""
                Warn "Everything inside the country is clean. The relay is bad ($($culprit.Bad))"
                Warn "and so is an unrelated network in the same region ($($landmarkRow.Bad))."
                Warn "Two different destinations abroad, both bad, everything at home fine: the"
                Warn "common factor is the way your ISP leaves the country."
                Emit ""
                Note "This is the one case where an entry relay inside the country is worth"
                Note "testing - it turns the congested consumer international leg into a"
                Note "datacentre one. Expect it to help the tail, not the median: p50 stays about"
                Note "the same and p95 is what should improve. Test it before paying for a year."
            } elseif ($landmarkUsable) {
                Emit $bar 'White'
                Emit " VERDICT: the relay's network, not your ISP" 'Red'
                Emit $bar 'White'
                Emit ""
                Warn "The relay is bad ($($culprit.Bad)) but a different network in the same"
                Warn "region is clean, over the same international path from the same machine."
                Emit ""
                Note "Your ISP's route out of the country is working. The problem is specific to"
                Note "this relay's provider or its transit. Another relay in the same city on a"
                Note "different provider is the fix - and an entry relay at home would not be."
            } else {
                Emit $bar 'White'
                Emit " VERDICT: somewhere abroad - past the last domestic hop" 'Red'
                Emit $bar 'White'
                Emit ""
                Warn "Everything inside the country is clean, the relay is not: $($culprit.Bad)"
                Emit ""
                Note "There was no usable landmark this run, so this cannot yet separate 'your"
                Note "ISP's international transit' from 'this relay's provider'. Add a landmark"
                Note "to the region in the profile and run it again - that single comparison is"
                Note "the difference between renting an entry relay and just moving the exit."
            }
        }
        'relay-udp' {
            Emit $bar 'White'
            Emit " VERDICT: the relay box itself" 'Red'
            Emit $bar 'White'
            Emit ""
            Warn "ICMP to the relay's address is clean, but relayd's own answers are not:"
            Warn "$($culprit.Bad)"
            Warn "Both cross the identical wire, so the wire is fine and the box is not."
            Emit ""
            Note "Steal time on a shared vCPU, a noisy neighbour, or relayd itself. Look at"
            Note ".\gpb.ps1 relay logs and at the VPS's own CPU steal figure."
        }
        'game' {
            Emit $bar 'White'
            Emit " VERDICT: beyond the relay" 'Red'
            Emit $bar 'White'
            Emit ""
            Warn "Everything up to and including the relay is clean; the end-to-end game ping is"
            Warn "not: $($culprit.Bad)"
            Emit ""
            Note "That leaves the relay-to-datacentre leg, or the game server. Neither is on your"
            Note "side of the path, and moving your own entry point cannot change either."
            if (-not $gameDirect) {
                Note "Note this number was ESTIMATED, not measured - the game server was not"
                Note "answering echoes. Treat it as weaker evidence than the rungs above it."
            }
        }
        default {
            Emit $bar 'White'
            Emit " VERDICT: $($culprit.Key)" 'Red'
            Emit $bar 'White'
        }
    }
}

Emit ""
if ($landmarkTunnelled) {
    Note "The landmark left by $($landmarkRow.Via), so it measured the tunnel rather than the"
    Note "plain ISP path and was not used as a control. That is expected while a match is"
    Note "running: the landmark sits inside a routed game range."
}
if (-not $serviceSeen) {
    Note "The service did not answer, so rungs 5 and 6 are missing - the relay box and the"
    Note "end-to-end ping could not be separated. Start it with .\gpb.ps1 dev, or the UI may be"
    Note "holding the pipe."
}
if ($baselines.Count -eq 0) {
    Note "No baseline yet. Run this once while everything feels FINE - after three good runs the"
    Note "thresholds stop being generic and become this connection's own normal."
}

$rows = @($targets)
if ($serviceSeen) {
    $rows += [pscustomobject]@{ Key = 'relay-udp'; Stats = $tunnelStats }
    $rows += [pscustomobject]@{ Key = 'game'; Stats = $gameStats }
}
Save-Run $rows $verdictText

$machine = ''
if ($null -ne $adapter) { $machine = $adapter.Name }
if ($onWifi) { $machine = "$machine (Wi-Fi" + $(if ($wifiDetail) { ", $wifiDetail" } else { '' }) + ')' }
elseif ($machine) { $machine = "$machine (wired)" }

$versionFile = Join-Path $root 'VERSION'
$version = 'unknown'
if (Test-Path -LiteralPath $versionFile) { $version = (Get-Content -LiteralPath $versionFile -Raw).Trim() }

$throughput = 'not measured'
if ($null -ne $downMbps) { $throughput = "{0:N1} Mbps down / {1:N1} Mbps up" -f $downMbps, $upMbps }

$extra = [ordered]@{
    'version'    = $version
    'machine'    = "$env:COMPUTERNAME, $([Environment]::OSVersion.Version)"
    'adapter'    = $machine
    'this pc'    = $throughput
    'window'     = "$Seconds s"
    'relay'      = $(if ($relayEndpoint) { $relayEndpoint } else { 'none configured' })
    'region'     = $(if ($regionName) { $regionName } else { 'unknown' })
    'game'       = $(if ($null -ne $gameProc) { 'TslGame.exe running' } else { 'not running' })
    'service'    = $(if ($serviceSeen) { 'answered' } else { 'did not answer' })
    'verdict'    = $verdictText
}

$reportPath = Save-Report $targets $hops $step $tunnelPing $gamePing $extra

Emit ""
if ($reportPath) {
    Say "Saved"
    Note "Report  $reportPath"
    Note "        the full run, with the trace and every raw sample - this is the one to send."
    Note "History $historyPath"
} else {
    Note "History: $historyPath"
}
