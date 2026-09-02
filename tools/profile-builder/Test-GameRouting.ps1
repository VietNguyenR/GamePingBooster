<#
.SYNOPSIS
    Answer one question while you are in a match: is the game's traffic actually going through
    the relay, or straight out of your network card?

.DESCRIPTION
    Run this from a second window while a match is running.

    It works by exploiting something the architecture gives us for free: a packet that IS being
    tunnelled leaves through the virtual adapter, gets wrapped by the service, and departs as UDP
    to the relay. It never appears on the physical adapter as game traffic. So anything this
    script captures on the physical adapter AND attributes to the game is, by definition, NOT
    going through the relay.

    That makes the output a direct verdict rather than a hint:

      - game destinations listed here  = bypassing the relay, missing from the profile
      - nothing listed, tunnel counters climbing = every gameplay packet is being relayed

    It also reads the service's own packet counters over the named pipe across the same window,
    so "the tunnel is carrying nothing" and "the tunnel is carrying the wrong thing" can be told
    apart.

.PARAMETER Seconds
    How long to sample. Twenty seconds inside a live match is plenty.

.EXAMPLE
    .\Test-GameRouting.ps1
    Run it while you are actually playing, not in the lobby.
#>

[CmdletBinding()]
param(
    [int]$Seconds = 20,
    [string]$WatchProcess = 'TslGame',
    [string]$Interface,
    [string]$ProfilePath
)

$ErrorActionPreference = 'Stop'

# --------------------------------------------------------------- preflight

$procs = @(Get-Process -Name $WatchProcess -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) {
    throw "$WatchProcess.exe is not running. Start a match first - the lobby is not enough, it uses different servers."
}
Write-Host "==> Game: $WatchProcess.exe (pid $(($procs | ForEach-Object { $_.Id }) -join ', '))" -ForegroundColor Cyan

$wiresharkDir = @("${env:ProgramFiles}\Wireshark", "${env:ProgramFiles(x86)}\Wireshark") |
    Where-Object { Test-Path (Join-Path $_ 'tshark.exe') } | Select-Object -First 1
if (-not $wiresharkDir) { throw "Wireshark not found." }
$tshark = Join-Path $wiresharkDir 'tshark.exe'
$dumpcap = Join-Path $wiresharkDir 'dumpcap.exe'

$tunAdapter = Get-NetAdapter -ErrorAction SilentlyContinue |
    Where-Object { $_.InterfaceDescription -like '*GamePingBooster*' -or $_.Name -like '*Game Ping Booster*' } |
    Select-Object -First 1
if ($tunAdapter) {
    Write-Host "==> Tunnel adapter: $($tunAdapter.Name) (ifIndex $($tunAdapter.ifIndex), $($tunAdapter.Status))"
} else {
    Write-Warning "No Game Ping Booster adapter found - the booster is not connected. Everything will look like a bypass."
}

$devices = & $dumpcap -D
if (-not $Interface) {
    $activeNic = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Sort-Object RouteMetric | Select-Object -First 1
    $phys = Get-NetAdapter -InterfaceIndex $activeNic.ifIndex
    $match = $devices | Where-Object { $_ -like "*$($phys.InterfaceGuid)*" } | Select-Object -First 1
    if (-not $match) { throw "Could not match the physical adapter; pass -Interface." }
    $Interface = ($match -split '\.')[0].Trim()
    Write-Host "==> Watching physical adapter: $($phys.Name) (dumpcap $Interface)"
}

# ------------------------------------------------------- service counters

function Get-TunnelStatus {
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'GamePingBooster', [System.IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect(2000)
        $reader = New-Object System.IO.StreamReader($pipe)
        $writer = New-Object System.IO.StreamWriter($pipe)
        $writer.AutoFlush = $true
        $writer.WriteLine('{"v":2,"verb":"status"}')
        $task = $reader.ReadLineAsync()
        $line = $null
        if ($task.Wait(3000)) { $line = $task.Result }
        $reader.Dispose(); $pipe.Dispose()
        if ($line) { return ($line | ConvertFrom-Json) }
    } catch {
        # The service may not be running; that is itself an answer.
    }
    return $null
}

$before = Get-TunnelStatus
if ($before) {
    Write-Host "==> Service: $($before.detail)"
    Write-Host "    routes installed: $($before.activeRoutes), tunnel ping $($before.tunnelPingMs) ms"
} else {
    Write-Warning "Could not read the service over the named pipe - is gpb-service running?"
}

# --------------------------------------------------------------- capture

# Same filter as the capture tool: outbound UDP to public addresses only.
$bpfFilter = 'udp and not port 53 ' +
             'and not dst net 10.0.0.0/8 and not dst net 172.16.0.0/12 ' +
             'and not dst net 192.168.0.0/16 and not dst net 169.254.0.0/16 ' +
             'and not dst net 224.0.0.0/4 and not dst host 255.255.255.255'

$captureFile = Join-Path ([System.IO.Path]::GetTempPath()) ("gpb-routecheck-{0:HHmmss}.pcapng" -f (Get-Date))
$targetPids = @{}
foreach ($p in $procs) { $targetPids[[int]$p.Id] = $true }
$gamePorts = @{}

Write-Host ""
Write-Host "==> Sampling $Seconds seconds. Stay in the match." -ForegroundColor Cyan

$proc = Start-Process -FilePath $dumpcap -PassThru -NoNewWindow -ArgumentList @(
    '-i', $Interface, '-q', '-f', "`"$bpfFilter`"", '-s', '96',
    '-a', "duration:$Seconds", '-w', "`"$captureFile`"")

$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline -and -not $proc.HasExited) {
    try {
        foreach ($e in (Get-NetUDPEndpoint -ErrorAction Stop)) {
            if ($targetPids.ContainsKey([int]$e.OwningProcess)) { $gamePorts[[int]$e.LocalPort] = $true }
        }
    } catch { }
    Start-Sleep -Milliseconds 1000
}
if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; Start-Sleep -Milliseconds 500 }

$after = Get-TunnelStatus

if (-not (Test-Path $captureFile)) { throw "No capture produced. Run as Administrator." }

# ---------------------------------------------------------------- analyse

$stats = @{}
& $tshark -r $captureFile -T fields -e ip.dst -e udp.srcport -e udp.dstport | ForEach-Object {
    $parts = $_ -split "`t"
    if ($parts.Count -lt 3) { return }
    $ip = $parts[0].Trim()
    if ($ip -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { return }
    $srcPort = $parts[1].Trim()
    if (-not $srcPort -or -not $gamePorts.ContainsKey([int]$srcPort)) { return }

    $key = "$ip|$($parts[2].Trim())"
    if (-not $stats.ContainsKey($key)) {
        $stats[$key] = [pscustomobject]@{ Address = $ip; Port = $parts[2].Trim(); Packets = 0 }
    }
    $stats[$key].Packets++
}
Remove-Item $captureFile -Force -ErrorAction SilentlyContinue

# ----------------------------------------------------------------- verdict

$tunnelSent = 0
$tunnelRecv = 0
if ($before -and $after) {
    $tunnelSent = $after.packetsSent - $before.packetsSent
    $tunnelRecv = $after.packetsReceived - $before.packetsReceived
}

Write-Host ""
Write-Host "==> Tunnel carried $tunnelSent packets out and $tunnelRecv back during the sample." -ForegroundColor Cyan

$rows = @($stats.Values | Sort-Object Packets -Descending)
if ($rows.Count -eq 0) {
    Write-Host ""
    if ($tunnelSent -gt 0) {
        Write-Host "==> No game traffic left the physical adapter directly, and the tunnel was busy." -ForegroundColor Green
        Write-Host "    Every gameplay packet is going through the relay. This is what working looks like."
    } else {
        Write-Warning "No game traffic on the physical adapter AND an idle tunnel. The game may not have been sending - were you actually in a live match?"
    }
    return
}

Write-Host ""
Write-Host "    These went STRAIGHT OUT, bypassing the relay:" -ForegroundColor Yellow
Write-Host ("    {0,-18} {1,-8} {2,9}  {3}" -f 'Address', 'Port', 'Packets', 'Windows routes it via')
Write-Host ("    {0,-18} {1,-8} {2,9}  {3}" -f '-------', '----', '-------', '---------------------')

$bypassed = 0
$suggest = @()
foreach ($row in ($rows | Select-Object -First 20)) {
    $via = 'unknown'
    try {
        $r = Find-NetRoute -RemoteIPAddress $row.Address -ErrorAction Stop | Select-Object -First 1
        $nic = Get-NetAdapter -InterfaceIndex $r.InterfaceIndex -ErrorAction SilentlyContinue
        if ($nic) { $via = "$($nic.Name) ($($r.InterfaceIndex))" } else { $via = "ifIndex $($r.InterfaceIndex)" }
    } catch { }
    $bypassed += $row.Packets
    if ($suggest -notcontains $row.Address) { $suggest += $row.Address }
    Write-Host ("    {0,-18} {1,-8} {2,9}  {3}" -f $row.Address, $row.Port, $row.Packets, $via)
}

Write-Host ""
Write-Host "==> $bypassed game packets bypassed the relay in $Seconds seconds." -ForegroundColor Yellow
Write-Host "    The profile does not cover these addresses, which is why the ping did not change."
Write-Host ""
Write-Host "    Add them and rebuild:"
Write-Host "      Add-Content .\observed.txt -Value @('$($suggest -join "','")')"
Write-Host "      .\Build-PubgProfile.ps1"
Write-Host ""
Write-Host "    Port 20522 is PUBG gameplay. A high-volume flow on that port is the one that matters;"
Write-Host "    the rest is lobby, telemetry or voice chat and does not need relaying."
