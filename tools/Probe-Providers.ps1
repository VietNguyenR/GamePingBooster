<#
.SYNOPSIS
    Which cloud provider's Singapore network does THIS line reach best - and would a relay there
    beat the line's own route to the game at all?

.DESCRIPTION
    Written for a report of 2026-09-13: a VNPT/VNTT player at a flat 70 ms with nothing wrong on
    any rung, and every relay slower than playing without the booster. The trace explained it -
    the line leaves Vietnam through Hurricane Electric in HONG KONG and comes back down to
    Singapore - and it raised the question this answers: is there a provider whose Singapore
    network this line reaches WITHOUT that detour?

    Nothing in the app can answer it. The service measures our own relays, end to end, and only
    those. This measures the providers we do not have yet.

    What it does, in order:

      1  resolves each provider's public test endpoint, and first checks whether this line's DNS
         answers names that do not exist - some ISP resolvers do, with a search page, and every
         endpoint would then "resolve" to the same box in Vietnam and measure beautifully;
      2  measures every target in the same rounds, interleaved, so a bad second lands on all of
         them alike instead of on whichever was being measured at the time;
      3  traces the path to each and names the cities its routers say they are in, from their
         reverse DNS - which is how "passes through Hong Kong" is known rather than guessed;
      4  compares the best provider against the game's own region, reached directly.

    TCP connect to port 443 is the measurement for providers, not ICMP: AWS answers no echo at
    all, and one method for every provider keeps the comparison fair. A SYN is answered by the
    far kernel just like an echo, so the two agree within a millisecond. The game's region and
    our relays are measured by ICMP, because a landmark has no TCP port and a relay only UDP.

    What it cannot tell: how a relay there would forward. Leg 2 from a Singapore datacentre to
    the game's Singapore region is 1-2 ms on every relay measured so far, which is small enough
    to leave out - but it is not zero, and the margin below is sized to swallow it.

    Nothing needs Administrator. Nothing is installed, routed or sent anywhere except the probes
    themselves, to the public endpoints listed in this file. The report it writes carries no
    credential, because this tool never opens one.

.PARAMETER Relays
    Our own relays, as name=host. `.\gpb.ps1 probe` fills this in from gpb.conf; nobody should
    need to type it. Run standalone - the way a player runs it - the providers' public
    endpoints already cover the networks our relays sit in.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Probe-Providers.ps1
    On the player's machine, with the booster disconnected. Send the file it names at the end.

.EXAMPLE
    .\gpb.ps1 probe
    From the repository, with our relays included.
#>
[CmdletBinding()]
param(
    [string[]]$Relays = @()
)

$ErrorActionPreference = 'Stop'

$script:transcript = New-Object System.Collections.Generic.List[string]

function Emit($text, $colour) {
    if ($null -eq $text) { $text = '' }
    $script:transcript.Add([string]$text)
    if ($colour) { Write-Host $text -ForegroundColor $colour } else { Write-Host $text }
}

function Say($msg) { Emit "==> $msg" 'Cyan' }
function Warn($msg) { Emit "    $msg" 'Yellow' }
function Note($msg) { Emit "    $msg" 'DarkGray' }

function Invoke-Safely($block) {
    try { & $block } catch { $null }
}

# =============================================================================== what is probed

# The game's region, reached directly. This is the number to beat: it is what the player gets
# with the booster off. The address is the Azure southeastasia probe endpoint PUBG itself pings
# on UDP 8081 to choose a region - see server-selection.md for why that stands in for a gameplay
# server, which answers nothing.
$ReferenceAddress = '20.43.187.66'

# Every endpoint here was checked on 2026-09-13 to resolve, to answer on TCP 443, and to belong to
# the provider named - by the registry's own record of who holds the address, not by the name.
# A test hostname that does not exist is worse than a missing one on a line whose DNS invents
# answers: it measures a search page in Vietnam and wins.
$Providers = @(
    @{ Label = 'DigitalOcean'; Region = 'Singapore'; Host = 'sgp1.digitaloceanspaces.com' },
    @{ Label = 'Vultr'; Region = 'Singapore'; Host = 'sgp-ping.vultr.com' },
    @{ Label = 'Linode/Akamai'; Region = 'Singapore'; Host = 'speedtest.singapore.linode.com' },
    @{ Label = 'OVH'; Region = 'Singapore'; Host = 'sgp.proof.ovh.net' },
    @{ Label = 'Hetzner'; Region = 'Singapore'; Host = 'sin-speed.hetzner.com' },
    @{ Label = 'AWS'; Region = 'Singapore'; Host = 'ec2.ap-southeast-1.amazonaws.com' },
    @{ Label = 'Alibaba Cloud'; Region = 'Singapore'; Host = 'ecs.ap-southeast-1.aliyuncs.com' },
    # A control, not a candidate. On a line that detours through Hong Kong, a provider IN Hong Kong
    # is the obvious thought, and this is what tests it.
    @{ Label = 'AWS'; Region = 'Hong Kong'; Host = 'ec2.ap-east-1.amazonaws.com' }
)

# Hong Kong to Singapore from inside a Hong Kong datacentre: 31 ms, measured on our own HK relay on
# 2026-09-06 (server-selection.md). The one number the verdict uses that is not measured on this
# machine. A Hong Kong host only helps if it is so much nearer that this second leg still fits.
$script:HongKongToSingaporeMs = 31.0

$Rounds = 20
$RoundPeriodMs = 500
$TimeoutMs = 1000

# =============================================================================== statistics

function Get-Percentile([double[]]$values, [double]$p) {
    if ($null -eq $values -or $values.Count -eq 0) { return $null }
    $sorted = @($values | Sort-Object)
    $idx = [int][math]::Ceiling(($p / 100.0) * $sorted.Count) - 1
    if ($idx -lt 0) { $idx = 0 }
    if ($idx -ge $sorted.Count) { $idx = $sorted.Count - 1 }
    return [double]$sorted[$idx]
}

function New-ProbeStats($samples, [int]$sent) {
    $arr = @($samples)
    $loss = 0.0
    if ($sent -gt 0) { $loss = 100.0 * ($sent - $arr.Count) / $sent }
    $s = [pscustomobject]@{
        Sent     = $sent
        Received = $arr.Count
        LossPct  = $loss
        P50      = $null
        P95      = $null
        Min      = $null
    }
    if ($arr.Count -gt 0) {
        $s.P50 = Get-Percentile $arr 50
        $s.P95 = Get-Percentile $arr 95
        $s.Min = [double](($arr | Measure-Object -Minimum).Minimum)
    }
    return $s
}

# =============================================================================== cities

# Where a router says it is, from its reverse DNS name, or $null when the name says nothing.
#
# Transit networks name routers after airport or city codes - e0-8.switch5.hkg1.he.net is
# Hurricane Electric in Hong Kong, which is the hop that explained the report this was written
# for. The name is split on everything that is not a letter or digit and each piece is compared
# WHOLE, trailing digits removed, so "hkg1" matches and "business" does not contain "sin".
#
# Only codes seen on real paths or unambiguous airport codes are listed. An unknown name returns
# nothing and the verdict makes no claim from it - a missing city is silence, not evidence.
$script:CityCodes = @{
    'hkg' = 'Hong Kong'; 'hkgphk' = 'Hong Kong'; 'hongkong' = 'Hong Kong'
    'sin' = 'Singapore'; 'sgp' = 'Singapore'; 'sngpsi' = 'Singapore'; 'singapore' = 'Singapore'
    'tyo' = 'Tokyo'; 'nrt' = 'Tokyo'; 'hnd' = 'Tokyo'; 'tkyojp' = 'Tokyo'; 'tokyo' = 'Tokyo'
    'osa' = 'Osaka'; 'kix' = 'Osaka'
    'icn' = 'Seoul'; 'seoul' = 'Seoul'
    'tpe' = 'Taipei'; 'taipei' = 'Taipei'
    'kul' = 'Kuala Lumpur'
    'bkk' = 'Bangkok'
    'lax' = 'Los Angeles'; 'lsanca' = 'Los Angeles'
    'sjc' = 'San Jose'; 'snjsca' = 'San Jose'
    'fra' = 'Frankfurt'; 'frnkge' = 'Frankfurt'
    'mrs' = 'Marseille'
    'syd' = 'Sydney'
    'sgn' = 'Vietnam'; 'hcm' = 'Vietnam'; 'hcmc' = 'Vietnam'; 'han' = 'Vietnam'; 'hni' = 'Vietnam'
}

function Get-HopCity([string]$name) {
    if (-not $name) { return $null }
    foreach ($token in ($name.ToLowerInvariant() -split '[^a-z0-9]+')) {
        $bare = $token -replace '\d+$', ''
        if ($bare.Length -lt 3) { continue }
        if ($script:CityCodes.ContainsKey($bare)) { return $script:CityCodes[$bare] }
    }
    return $null
}

# =============================================================================== the verdict

# How much better a provider has to be before it is worth a relay. A few milliseconds is inside
# what leg 2 and the relay's own forwarding cost, and inside what a different evening would give.
function Get-Margin([double]$reference) {
    return [math]::Max(5.0, 0.1 * $reference)
}

# Key is one of: provider-wins, hk-wins, isp-route-wins, no-reference, no-provider.
function Get-ProbeVerdict($reference, $rows) {
    $lines = New-Object System.Collections.Generic.List[string]
    $usable = @($rows | Where-Object {
            $_.Kind -eq 'provider' -and -not $_.ViaTunnel -and $null -ne $_.Stats -and $null -ne $_.Stats.P50
        })
    $sg = @($usable | Where-Object { $_.Region -eq 'Singapore' } | Sort-Object { $_.Stats.P50 })
    $hk = @($usable | Where-Object { $_.Region -eq 'Hong Kong' } | Sort-Object { $_.Stats.P50 })

    if ($sg.Count -eq 0) {
        $lines.Add('No Singapore provider answered, so there is nothing to compare.')
        return [pscustomobject]@{ Key = 'no-provider'; Best = $null; Lines = $lines }
    }
    $best = $sg[0]

    if ($null -eq $reference -or $reference.ViaTunnel -or $null -eq $reference.Stats -or $null -eq $reference.Stats.P50) {
        $lines.Add(('Best Singapore provider from this line: {0} at {1:N0} ms.' -f $best.Label, $best.Stats.P50))
        $lines.Add('The game''s region could not be measured directly, so whether that beats playing')
        $lines.Add('without the booster is unknown. If the booster is connected, disconnect it and run again.')
        return [pscustomobject]@{ Key = 'no-reference'; Best = $best; Lines = $lines }
    }

    $ref = [double]$reference.Stats.P50
    $margin = Get-Margin $ref
    $lines.Add(('Direct to the game''s region, no booster: {0:N0} ms.' -f $ref))
    $lines.Add(('Best Singapore provider: {0} at {1:N0} ms.' -f $best.Label, $best.Stats.P50))

    # Naming one winner out of seven providers inside three milliseconds of each other - which is
    # exactly what the first real run did - tells the reader something the numbers do not say.
    $band = [math]::Max(2.0, 0.05 * $best.Stats.P50)
    $ties = @($sg | Select-Object -Skip 1 | Where-Object { $_.Stats.P50 - $best.Stats.P50 -le $band })
    $named = $best.Label
    if ($ties.Count -gt 0) {
        $lines.Add(('Within {0:N0} ms of it, and no different in practice: {1}.' -f `
                    $band, (@($ties | ForEach-Object { $_.Label }) -join ', ')))
        $named = 'any of these'
    }

    # Where the paths go, from the routers' own names. Counted across the Singapore providers AND
    # the direct path, because the claim being tested is about the line, not about one provider.
    $traced = @(@($sg) + @($reference) | Where-Object { @($_.Cities).Count -gt 0 })
    $abroad = @{}
    foreach ($row in $traced) {
        foreach ($city in @($row.Cities | Where-Object { $_ -ne 'Singapore' -and $_ -ne 'Vietnam' } | Select-Object -Unique)) {
            if (-not $abroad.ContainsKey($city)) { $abroad[$city] = 0 }
            $abroad[$city]++
        }
    }
    $detour = $null
    foreach ($city in $abroad.Keys) {
        if ($abroad[$city] * 2 -ge $traced.Count -and ($null -eq $detour -or $abroad[$city] -gt $abroad[$detour])) {
            $detour = $city
        }
    }

    $key = 'isp-route-wins'
    if ($ref - $best.Stats.P50 -ge $margin) {
        $key = 'provider-wins'
        $lines.Add(('A relay at {0} could save about {1:N0} ms - more than the {2:N0} ms a relay''s own' -f `
                    $named, ($ref - $best.Stats.P50), $margin))
        $lines.Add('second leg and a different evening could take back. Worth trying on this line.')
    } elseif ($hk.Count -gt 0 -and $hk[0].Stats.P50 + $script:HongKongToSingaporeMs -le $ref - $margin) {
        $key = 'hk-wins'
        $lines.Add(('No Singapore provider beats the direct route, but {0} in Hong Kong is {1:N0} ms away.' -f `
                    $hk[0].Label, $hk[0].Stats.P50))
        $lines.Add(('With about {0:N0} ms on from Hong Kong to Singapore that is {1:N0} ms - worth a relay there.' -f `
                    $script:HongKongToSingaporeMs, ($hk[0].Stats.P50 + $script:HongKongToSingaporeMs)))
    } else {
        $lines.Add(('No provider beats this line''s own route by the {0:N0} ms it would take to matter.' -f $margin))
        $lines.Add('A relay for this player, at any of these providers, would be no faster than the booster off.')
    }

    # "Worth a relay there" reads as advice to rent one. When a relay of ours already reaches the
    # same number, the useful thing to say is that.
    $ours = @($rows | Where-Object {
            $_.Kind -eq 'relay' -and -not $_.ViaTunnel -and $null -ne $_.Stats -and $null -ne $_.Stats.P50
        } | Sort-Object { $_.Stats.P50 })
    if ($ours.Count -gt 0) {
        if ($ours[0].Stats.P50 -le $best.Stats.P50 + $margin) {
            $who = $ours[0].Label.Substring(0, 1).ToUpperInvariant() + $ours[0].Label.Substring(1)
            $lines.Add(('{0} already reaches that, at {1:N0} ms.' -f $who, $ours[0].Stats.P50))
        } else {
            $lines.Add(('The best of our relays, {0}, is {1:N0} ms - {2:N0} ms behind that.' -f `
                        $ours[0].Label, $ours[0].Stats.P50, ($ours[0].Stats.P50 - $best.Stats.P50)))
        }
    }

    if ($null -ne $detour) {
        $lines.Add('')
        $lines.Add(('{0} of {1} traced paths to Singapore pass through {2} on the way.' -f `
                    $abroad[$detour], $traced.Count, $detour))
        if ($key -eq 'isp-route-wins') {
            $lines.Add('That is the line''s exit from Vietnam, and every provider behind it pays for it. What')
            $lines.Add('would help is an entry point reached without that detour - a relay inside Vietnam with')
            $lines.Add('its own route out - not a different provider in Singapore.')
        }
    }

    # The direct route on its own. One path in several is not the line's exit, so the count above
    # stays quiet about it - but when that one path is the route the game itself uses, where it
    # goes is the reason a relay helps or does not.
    $refAbroad = @($reference.Cities | Where-Object { $_ -and $_ -ne 'Singapore' -and $_ -ne 'Vietnam' } | Select-Object -Unique)
    if ($refAbroad.Count -gt 0 -and -not ($null -ne $detour -and $refAbroad -contains $detour)) {
        $lines.Add('')
        $text = 'The direct route to the game''s region passes through {0}' -f ($refAbroad -join ', ')
        if ($key -eq 'provider-wins') { $text += ' - that detour is what a relay takes out.' } else { $text += '.' }
        $lines.Add($text)
    }

    return [pscustomobject]@{ Key = $key; Best = $best; Lines = $lines }
}

# =============================================================================== measuring

function Measure-Tcp([string]$ip, [int]$port, [int]$timeoutMs) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $task = $client.ConnectAsync($ip, $port)
        $done = Invoke-Safely { $task.Wait($timeoutMs) }
        $clock.Stop()
        if ($done -and $client.Connected) { return $clock.Elapsed.TotalMilliseconds }
        return $null
    } finally {
        $client.Dispose()
    }
}

function Measure-Icmp([string]$ip, [int]$timeoutMs) {
    $reply = Invoke-Safely { $script:pinger.Send($ip, $timeoutMs) }
    if ($null -ne $reply -and $reply.Status -eq 'Success') { return [double]$reply.RoundtripTime }
    return $null
}

function Measure-Target($t) {
    if ($t.Method -eq 'tcp') { return Measure-Tcp $t.Address 443 $TimeoutMs }
    return Measure-Icmp $t.Address $TimeoutMs
}

# What a router calls itself, from its PTR record - asked of DNS and nothing else, and kept only
# when it could be a public DNS name at all.
#
# [System.Net.Dns]::GetHostEntry was the first version, and on its first real run, 2026-09-13, it
# was wrong twice. An ISP router whose PTR record says "localhost" came back as THIS COMPUTER'S
# OWN NAME, because Windows answers localhost itself - in a report written to be sent to someone.
# And a Microsoft edge router came back as a-0003.a-msedge.net, when its PTR record says
# ae33-0.icr01.hkg20.ntwk.msn.net: the one name showing that the direct route to Azure went
# through Hong Kong, which was the whole question.
#
# Resolve-DnsName -DnsOnly asks the resolver only - no hosts file, no NetBIOS, no LLMNR. The
# answer is cached because the first hops are the same on every path, and an address with no
# record can take a second and a half to say so.
function Test-PublicHostName([string]$name, [string]$self) {
    if (-not $name) { return $false }
    $n = $name.TrimEnd('.').ToLowerInvariant()
    if ($n -notmatch '\.') { return $false }
    if ($n -eq 'localhost' -or $n.StartsWith('localhost.')) { return $false }
    if ($self) {
        $me = $self.ToLowerInvariant()
        if ($n -eq $me -or $n.StartsWith($me + '.')) { return $false }
    }
    return $true
}

$script:rdns = @{}
function Resolve-HopName([string]$ip) {
    if ($script:rdns.ContainsKey($ip)) { return $script:rdns[$ip] }
    $name = $null
    if (Get-Command Resolve-DnsName -ErrorAction SilentlyContinue) {
        $record = Invoke-Safely {
            Resolve-DnsName -Name $ip -Type PTR -DnsOnly -QuickTimeout -ErrorAction Stop |
                Where-Object { $_.Type -eq 'PTR' } | Select-Object -First 1
        }
        if ($null -ne $record -and (Test-PublicHostName $record.NameHost $env:COMPUTERNAME)) {
            $name = $record.NameHost.TrimEnd('.')
        }
    }
    $script:rdns[$ip] = $name
    return $name
}

# Raw TTLs rather than tracert.exe, whose output is localised - see Diagnose-Lag.ps1. The hop RTT
# is timed by hand because PingReply.RoundtripTime is 0 on every TtlExpired reply.
#
# Stops after four silent hops in a row. AWS answers no echo, and neither does any router past the
# ISP's own on the way to it: walked to hop 20 on 2026-09-13, Singapore and Hong Kong both, and not
# one answered. More patience there buys half a minute per provider and not a single name.
function Get-TracePath([string]$target, [int]$maxHops, [int]$timeoutMs) {
    $ping = New-Object System.Net.NetworkInformation.Ping
    $payload = New-Object byte[] 32
    $hops = @()
    $silent = 0

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
                $silent = 0
                $name = Resolve-HopName $addr
                $hops += [pscustomobject]@{
                    Ttl     = $ttl
                    Address = $addr
                    Rtt     = $best
                    Name    = $name
                    City    = Get-HopCity $name
                }
            } else {
                $silent++
                if ($silent -ge 4) { break }
            }
            if ($arrived) { break }
        }
    } finally {
        $ping.Dispose()
    }

    return $hops
}

function Test-ViaTunnel([string]$ip) {
    $route = Invoke-Safely { Find-NetRoute -RemoteIPAddress $ip -ErrorAction Stop | Select-Object -First 1 }
    if ($null -eq $route) { return $false }
    $adapter = Invoke-Safely { Get-NetAdapter -InterfaceIndex $route.InterfaceIndex -ErrorAction Stop }
    if ($null -eq $adapter) { return $false }
    return ($adapter.InterfaceDescription -like '*Wintun*')
}

function New-Target($kind, $label, $region, $hostName, $method) {
    return [pscustomobject]@{
        Kind      = $kind
        Label     = $label
        Region    = $region
        HostName  = $hostName
        Method    = $method
        Address   = $null
        Skip      = $null
        ViaTunnel = $false
        Samples   = New-Object System.Collections.Generic.List[double]
        Sent      = 0
        Stats     = $null
        Hops      = @()
        Cities    = @()
    }
}

# =============================================================================== the report

function Save-Report($targets, $verdict) {
    $dir = Invoke-Safely { [Environment]::GetFolderPath('Desktop') }
    if (-not $dir -or -not (Test-Path -LiteralPath $dir)) { $dir = Join-Path $env:LOCALAPPDATA 'GamePingBooster' }
    try {
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $path = Join-Path $dir ('gpb-providers-{0}.txt' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))

        $out = New-Object System.Collections.Generic.List[string]
        $out.Add('Game Ping Booster - provider probe')
        $out.Add(('collected {0}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')))
        $out.Add(('verdict   {0}' -f $verdict.Key))
        $out.Add('')
        foreach ($line in $script:transcript) { $out.Add($line) }

        # The paths are the half that says WHY. A number alone cannot tell a provider that is far
        # from one that is near but reached the long way round.
        $out.Add('')
        $out.Add('=== paths, with what each router calls itself ===')
        foreach ($t in $targets) {
            $out.Add('')
            $out.Add(('{0} {1} ({2}, {3})' -f $t.Label, $t.Region, $t.HostName, $t.Address))
            if ($t.Skip) { $out.Add("   not traced: $($t.Skip)"); continue }
            if (@($t.Hops).Count -eq 0) { $out.Add('   (every hop stayed silent)'); continue }
            foreach ($h in @($t.Hops)) {
                $out.Add(('{0,4}  {1,-16} {2,6:N1} ms  {3}{4}' -f $h.Ttl, $h.Address, $h.Rtt, $h.Name,
                        $(if ($h.City) { "  [$($h.City)]" } else { '' })))
            }
        }

        $out.Add('')
        $out.Add('=== raw samples, milliseconds, in the order they were taken ===')
        foreach ($t in $targets) {
            $vals = if ($t.Samples.Count -gt 0) { ($t.Samples | ForEach-Object { '{0:N1}' -f $_ }) -join ', ' } else { '(none)' }
            $out.Add(('{0} {1} [{2}, sent {3}]' -f $t.Label, $t.Region, $t.Method, $t.Sent))
            $out.Add('  ' + $vals)
        }

        $out.Add('')
        $out.Add('No key, token or password is read by this tool, so none can be in this file.')
        Set-Content -LiteralPath $path -Value $out -Encoding UTF8
        return $path
    } catch {
        Warn "Could not write the report: $_"
        return $null
    }
}

# =============================================================================== the run

Say 'Which provider this line reaches best'
Note 'About a minute. Disconnect the booster first - the direct route is half the comparison.'
Emit ''

$targets = New-Object System.Collections.Generic.List[object]
$targets.Add((New-Target 'reference' 'Game region, direct' 'Singapore' $ReferenceAddress 'icmp'))
foreach ($p in $Providers) { $targets.Add((New-Target 'provider' $p.Label $p.Region $p.Host 'tcp')) }
foreach ($r in $Relays) {
    $parts = $r -split '=', 2
    if ($parts.Count -eq 2 -and $parts[1]) { $targets.Add((New-Target 'relay' "our relay $($parts[0])" '' $parts[1] 'icmp')) }
}

# ------------------------------------------------------------------ names

# A name made up on the spot cannot exist. If it resolves, this line's DNS answers everything, and
# whatever address it hands out is the one that must never be measured as a provider.
$poison = @{}
$made = 'gpb-nx-{0}.com' -f ([guid]::NewGuid().ToString('N'))
foreach ($a in @(Invoke-Safely { [System.Net.Dns]::GetHostAddresses($made) })) {
    if ($null -ne $a) { $poison[$a.ToString()] = $true }
}
if ($poison.Count -gt 0) {
    Warn ('This line''s DNS answers names that do not exist (with {0}). Any provider resolving there is skipped.' -f `
            (@($poison.Keys) -join ', '))
}

foreach ($t in $targets) {
    $ip = $null
    if (Invoke-Safely { [ipaddress]::Parse($t.HostName) }) {
        $ip = $t.HostName
    } else {
        $ip = @(Invoke-Safely { [System.Net.Dns]::GetHostAddresses($t.HostName) } |
                Where-Object { $null -ne $_ -and $_.AddressFamily -eq 'InterNetwork' } |
                ForEach-Object { $_.ToString() }) | Select-Object -First 1
    }
    if (-not $ip) { $t.Skip = 'the name did not resolve to an IPv4 address'; continue }
    if ($poison.ContainsKey($ip)) { $t.Skip = "the name resolved to $ip, which is this line's DNS inventing an answer"; continue }
    $t.Address = $ip
    $t.ViaTunnel = Test-ViaTunnel $ip
}

if (@($targets | Where-Object { $_.ViaTunnel }).Count -gt 0) {
    Warn 'The booster is carrying some of these right now. Those rows are shown but not compared -'
    Warn 'disconnect the booster and run again for a verdict on them.'
}

# ------------------------------------------------------------------ rounds

$script:pinger = New-Object System.Net.NetworkInformation.Ping
try {
    $live = @($targets | Where-Object { -not $_.Skip })

    # One probe each that is not counted. The first TCP connect in a fresh PowerShell pays for
    # loading and compiling the socket code - measured at 46 ms against a 25 ms path - and would
    # otherwise sit in every provider's numbers as a spike that never happened.
    foreach ($t in $live) { $null = Measure-Target $t }

    for ($round = 1; $round -le $Rounds; $round++) {
        Write-Progress -Activity 'Measuring' -Status "round $round of $Rounds" -PercentComplete (100 * $round / $Rounds)
        $clock = [System.Diagnostics.Stopwatch]::StartNew()

        foreach ($t in $live) {
            if ($t.Skip) { continue }
            $t.Sent++
            $rtt = Measure-Target $t
            if ($null -ne $rtt) { $t.Samples.Add([double]$rtt) }
        }

        # Something that answered nothing five times is not going to start, and every timeout it
        # costs is a second added to every remaining round. Dropped, and said.
        if ($round -eq 5) {
            foreach ($t in $live) {
                if (-not $t.Skip -and $t.Samples.Count -eq 0) {
                    $t.Skip = 'no answer to the first five attempts'
                    Warn "$($t.Label) $($t.Region) answered nothing - dropped."
                }
            }
        }

        $left = $RoundPeriodMs - $clock.ElapsedMilliseconds
        if ($left -gt 0) { Start-Sleep -Milliseconds $left }
    }
    Write-Progress -Activity 'Measuring' -Completed
} finally {
    $script:pinger.Dispose()
}

foreach ($t in $targets) { $t.Stats = New-ProbeStats $t.Samples $t.Sent }

# ------------------------------------------------------------------ paths

$i = 0
foreach ($t in $targets) {
    $i++
    if ($t.Skip -or $t.ViaTunnel) { continue }
    Write-Progress -Activity 'Tracing paths' -Status "$($t.Label) $($t.Region)" -PercentComplete (100 * $i / $targets.Count)
    $t.Hops = @(Get-TracePath $t.Address 20 800)
    $t.Cities = @($t.Hops | Where-Object { $_.City } | ForEach-Object { $_.City })
}
Write-Progress -Activity 'Tracing paths' -Completed

# ------------------------------------------------------------------ the table

Emit ''
Emit ('{0,-22} {1,-10} {2,-4} {3,7} {4,7} {5,6}  {6}' -f 'target', 'region', 'via', 'p50', 'p95', 'loss', 'passes through')
$order = @($targets | Sort-Object @{ Expression = { if ($null -eq $_.Stats.P50) { [double]::MaxValue } else { $_.Stats.P50 } } })
foreach ($t in $order) {
    if ($t.Skip) {
        Emit ('{0,-22} {1,-10} {2,-4} {3}' -f $t.Label, $t.Region, $t.Method, "skipped: $($t.Skip)") 'DarkGray'
        continue
    }
    $through = @($t.Cities | Where-Object { $_ -ne 'Vietnam' } | Select-Object -Unique) -join ' > '
    if (-not $through) { $through = '-' }
    if ($t.ViaTunnel) { $through = '(through the booster - not compared)' }
    $colour = $null
    if ($t.Kind -eq 'reference') { $colour = 'White' }
    Emit ('{0,-22} {1,-10} {2,-4} {3,7:N1} {4,7:N1} {5,5:N0}%  {6}' -f `
            $t.Label, $t.Region, $t.Method, $t.Stats.P50, $t.Stats.P95, $t.Stats.LossPct, $through) $colour
}

$reference = $targets | Where-Object { $_.Kind -eq 'reference' } | Select-Object -First 1
$verdict = Get-ProbeVerdict $reference $targets

Emit ''
$tone = 'Yellow'
if ($verdict.Key -eq 'provider-wins' -or $verdict.Key -eq 'hk-wins') { $tone = 'Green' }
foreach ($line in $verdict.Lines) { Emit "    $line" $tone }

$saved = Save-Report $targets $verdict
Emit ''
if ($saved) { Say "Saved to $saved - send this file." }
