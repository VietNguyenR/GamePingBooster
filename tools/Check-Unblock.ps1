<#
.SYNOPSIS
    Why a game or Steam will not load on a player's machine: DNS block, the unblock fix, or something else.

.DESCRIPTION
    Changes one thing, for a few minutes: it turns on Windows' DNS client log (Microsoft-Windows-DNS-Client/Operational)
    when it is off, and turns it back off once read. Nothing else on the machine is changed. Writes one text file to the
    Desktop to send back.

    It first asks for the problem to be reproduced: open PUBG (again, when the log was off - its first questions are the
    ones that matter), wait until it is stuck, press Enter. Then it reads the questions the game's own processes asked
    and the game's TCP connections - never another program's. -NoWait skips the wait and reads whatever is there.

    Run it while the game sits on the black screen or on "Initializing...", or right after:

        powershell -ExecutionPolicy Bypass -File Check-Unblock.ps1

    On a machine where scripts are disabled ("running scripts is disabled on this system") and the line above is
    refused too, run it from a PowerShell window as a script block - execution policy applies to script files only:

        & ([scriptblock]::Create((Get-Content -Raw "$env:USERPROFILE\Downloads\Check-Unblock.ps1")))

    Written 2026-10-02 from the day PUBG loaded to a black screen on VNPT and Viettel. Every check below is one
    that day needed, and each answers a different question:

      - what the ISP's own resolver says (nslookup straight to it - Resolve-DnsName obeys the NRPT and cannot)
      - what Windows says, which is what the game gets
      - what encrypted DNS says, the truth to compare against
      - the NRPT rules the app wrote, and whether anything is listening on 127.0.0.53 to answer them
        (rules with no listener make every name "No such host" - a dev service once left 17 behind)
      - whether the lobby's real edge completes a TLS handshake (SNI filtering) and its TCP 40002 connects
      - the app's own unblock lines from the service log
      - added 2026-10-03, for PUBG stuck on "Initializing..." on FPT Ha Noi with every name above answering fine:
        the names the GAME asked Windows (from the DNS client log), each one's ISP answer against encrypted DNS when
        the app does not unblock it, and the game's TCP connections - which showed 17 of them to a Tencent Cloud CDN
        node in Hebei, China, that no list here had ever named

    It contains the machine's public IP and ISP, its DNS servers, the app's log lines about DNS, and the names and
    servers the game's processes used. No password, no licence token, no pre-shared key, no other program's traffic.
#>
param([string]$OutDir = [Environment]::GetFolderPath('Desktop'), [switch]$NoWait)

$ErrorActionPreference = 'Continue'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $OutDir "gpb-unblock-check-$stamp.txt"
$lines = New-Object System.Collections.Generic.List[string]
$hints = New-Object System.Collections.Generic.List[string]

function Say([string]$text) { $lines.Add($text); Write-Host $text }
function Section([string]$title) { Say ''; Say "== $title"; }

# A certificate callback in C#: a PowerShell script block cannot run on the thread SslStream calls it from.
Add-Type -TypeDefinition @'
public static class GpbAnyCert {
    public static System.Net.Security.SslPolicyErrors Last;
    public static bool Ok(object s, System.Security.Cryptography.X509Certificates.X509Certificate c,
        System.Security.Cryptography.X509Certificates.X509Chain ch, System.Net.Security.SslPolicyErrors e) { Last = e; return true; }
}
'@

$names = @(
    @{ Name = 'prod-live-front.playbattlegrounds.com'; Why = 'PUBG lobby - black screen when this fails' },
    @{ Name = 'prod-live-cfentry.playbattlegrounds.com'; Why = 'PUBG lobby content' },
    @{ Name = 'prod-live-xenuine.playbattlegrounds.com'; Why = 'PUBG launcher API' },
    @{ Name = 'acrt-pcprod.acs.pubg.com'; Why = 'PUBG anti-cheat / telemetry' },
    @{ Name = 'zk-ga-pcprod.acs.pubg.com'; Why = 'PUBG lobby, TCP 40002' },
    @{ Name = 'accounts.pubg.com'; Why = 'PUBG accounts' },
    @{ Name = 'pubg.com'; Why = 'PUBG website' },
    @{ Name = 'steamcommunity.com'; Why = 'Steam community' },
    @{ Name = 'store.steampowered.com'; Why = 'Steam store' },
    @{ Name = 'api.steampowered.com'; Why = 'Steam sign-in' },
    @{ Name = 'cmp1-sgp1.steamserver.net'; Why = 'Steam connection server' },
    @{ Name = 'steampipe.akamaized.net'; Why = 'Steam downloads' },
    @{ Name = 'www.microsoft.com'; Why = 'control - nobody blocks it' }
)

function Test-Sinkhole($addresses) {
    if (-not $addresses -or $addresses.Count -eq 0) { return $true }
    foreach ($a in $addresses) {
        if ($a -notmatch '^(127\.|0\.0\.0\.0|10\.|192\.168\.)' -and $a -ne '::1') { return $false }
    }
    return $true
}

function Resolve-Windows([string]$name) {
    try {
        $r = [Net.Dns]::GetHostAddresses($name) | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | ForEach-Object { $_.ToString() }
        return @{ Ok = $true; Addresses = @($r); Text = ($r -join ',') }
    } catch {
        $m = $_.Exception.InnerException
        if (-not $m) { $m = $_.Exception }
        return @{ Ok = $false; Addresses = @(); Text = "FAIL: $($m.Message)" }
    }
}

function Resolve-Isp([string]$name, [string]$server) {
    $text = (nslookup -type=A -timeout=3 $name $server 2>&1 | Out-String)
    if ($text -match 'Non-existent domain|NXDOMAIN') { return @{ Addresses = @(); Text = 'NXDOMAIN' } }
    if ($text -match 'timed out|No response') { return @{ Addresses = @(); Text = 'no answer' } }
    $after = ($text -split "Name:", 2)
    if ($after.Count -lt 2) { return @{ Addresses = @(); Text = 'no address' } }
    $ips = [regex]::Matches($after[1], '\b(\d{1,3}\.){3}\d{1,3}\b') | ForEach-Object { $_.Value }
    return @{ Addresses = @($ips); Text = (@($ips) -join ',') }
}

function Resolve-Doh([string]$name) {
    foreach ($u in @("https://cloudflare-dns.com/dns-query?name=$name&type=A", "https://dns.google/resolve?name=$name&type=A")) {
        try {
            $j = Invoke-RestMethod -Uri $u -Headers @{ accept = 'application/dns-json' } -TimeoutSec 6
            $ips = @($j.Answer | Where-Object { $_.type -eq 1 } | ForEach-Object { $_.data })
            if ($ips.Count -gt 0) { return @{ Addresses = $ips; Text = ($ips -join ',') } }
        } catch {}
    }
    return @{ Addresses = @(); Text = 'no answer (DoH unreachable?)' }
}

function Resolve-DohFrom([string]$name, [string]$url) {
    try {
        $j = Invoke-RestMethod -Uri "$url`?name=$name&type=A" -Headers @{ accept = 'application/dns-json' } -TimeoutSec 6
        return @($j.Answer | Where-Object { $_.type -eq 1 } | ForEach-Object { $_.data })
    } catch { return @() }
}

function Test-Tls([string]$ip, [string]$sni) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $c = New-Object Net.Sockets.TcpClient
    try {
        $iar = $c.BeginConnect($ip, 443, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne(5000)) { return "TCP 443 no answer in 5 s" }
        $c.EndConnect($iar)
        $tcp = $sw.ElapsedMilliseconds
        $s = New-Object Net.Security.SslStream($c.GetStream(), $false, [Delegate]::CreateDelegate([Net.Security.RemoteCertificateValidationCallback], [GpbAnyCert].GetMethod('Ok')))
        $c.ReceiveTimeout = 8000; $c.SendTimeout = 8000
        $s.AuthenticateAsClient($sni)
        $ok = if ([GpbAnyCert]::Last -eq 'None') { 'valid certificate' } else { "certificate problem: $([GpbAnyCert]::Last)" }
        return "TCP ${tcp} ms, TLS done $($sw.ElapsedMilliseconds) ms, $ok"
    } catch {
        $m = $_.Exception
        while ($m.InnerException) { $m = $m.InnerException }
        return "FAIL after $($sw.ElapsedMilliseconds) ms: $($m.Message)"
    } finally { $c.Close() }
}

function Test-Tcp([string]$ip, [int]$port) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $c = New-Object Net.Sockets.TcpClient
    try {
        $iar = $c.BeginConnect($ip, $port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne(5000)) { return "no answer in 5 s" }
        $c.EndConnect($iar)
        return "connected in $($sw.ElapsedMilliseconds) ms"
    } catch {
        $m = $_.Exception
        while ($m.InnerException) { $m = $m.InnerException }
        return "FAIL: $($m.Message)"
    } finally { $c.Close() }
}

Say "Game Ping Booster unblock check - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"

# ---------------------------------------------------------------- the app
Section 'The app'
$svc = Get-CimInstance Win32_Service -Filter "Name='GamePingBooster'" -ErrorAction SilentlyContinue
if ($svc) {
    $exe = $svc.PathName.Trim('"')
    $ver = (Get-Item $exe -ErrorAction SilentlyContinue).VersionInfo.ProductVersion
    Say "Service: $($svc.State), start $($svc.StartMode), version $ver"
} else {
    Say 'Service: not installed'
}
$others = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'gpb-service|GamePingBooster|TslGame|steam$' } |
    ForEach-Object { "$($_.ProcessName)($($_.Id))" }
Say "Running: $($others -join ', ')"

# ---------------------------------------------------------------- watching the game
# The game's own questions and connections, caught while it is stuck. Read for these processes only: the DNS client
# log holds every program's lookups, and a browser's are none of this file's business.
$gameRx = '^(TslGame|TslGame_BE|ExecPubg|BEService|steam)$'
$gamePids = @{}
function Note-GamePids {
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match $gameRx } |
        ForEach-Object { $gamePids[[int]$_.Id] = $_.ProcessName }
}
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$dnsLogName = 'Microsoft-Windows-DNS-Client/Operational'
$dnsLogTurnedOn = $false
$watchFrom = (Get-Date).AddMinutes(-15)
if ($isAdmin) {
    $dnsLog = Get-WinEvent -ListLog $dnsLogName -ErrorAction SilentlyContinue
    if ($dnsLog -and -not $dnsLog.IsEnabled) {
        wevtutil sl $dnsLogName /e:true 2>$null | Out-Null
        $dnsLogTurnedOn = $true
        $watchFrom = Get-Date
    }
}
Note-GamePids
if (-not $NoWait) {
    Write-Host ''
    if ($dnsLogTurnedOn) {
        Write-Host 'Da bat nhat ky DNS cua Windows (se tat lai khi xong).' -ForegroundColor Yellow
        Write-Host 'Bay gio: TAT PUBG neu dang mo, MO LAI, doi toi khi ket o "Initializing..." (hoac man den), roi bam Enter o cua so nay.' -ForegroundColor Yellow
    } else {
        Write-Host 'Mo PUBG (neu chua mo), doi toi khi ket o "Initializing..." (hoac man den), roi bam Enter o cua so nay.' -ForegroundColor Yellow
    }
    if (-not $isAdmin) {
        Write-Host '(Cua so nay khong chay quyen Administrator - khong doc duoc nhat ky DNS. Nen chay lai bang "Run as administrator".)' -ForegroundColor Red
    }
    Write-Host '(Tu tiep tuc sau 5 phut.)'
    $deadline = (Get-Date).AddMinutes(5)
    $console = $true
    while ((Get-Date) -lt $deadline) {
        Note-GamePids
        try {
            if ([Console]::KeyAvailable -and [Console]::ReadKey($true).Key -eq 'Enter') { break }
        } catch { $console = $false; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $console) { [void](Read-Host 'Bam Enter khi da ket'); Note-GamePids }

    # 2026-10-03: the first run of this check was Entered with PUBG still open from before the log went on - and a
    # black screen happens in the game's first seconds, so the section that would have shown it came back empty.
    $fresh = @(Get-Process TslGame -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $watchFrom })
    if ($dnsLogTurnedOn -and $fresh.Count -eq 0 -and $console) {
        Write-Host 'PUBG chua duoc MO LAI sau khi bat nhat ky - phan ten mien se trong. Tat PUBG, mo lai, doi toi khi ket roi bam Enter (bam Enter ngay de bo qua).' -ForegroundColor Red
        $deadline = (Get-Date).AddMinutes(5)
        while ((Get-Date) -lt $deadline) {
            Note-GamePids
            try { if ([Console]::KeyAvailable -and [Console]::ReadKey($true).Key -eq 'Enter') { break } } catch { break }
            Start-Sleep -Milliseconds 500
        }
    }
    Write-Host 'Dang kiem tra, mat khoang 1-2 phut...'
}

# Twice, five seconds apart: a connection that is SynSent both times, or a local port that changed, is the game retrying.
function Get-GameTcp {
    $ids = @($gamePids.Keys | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
    if ($ids.Count -eq 0) { return @() }
    return @(Get-NetTCPConnection -OwningProcess $ids -ErrorAction SilentlyContinue |
        Where-Object { $_.RemoteAddress -notin @('0.0.0.0', '::', '127.0.0.1', '::1') })
}
$tcpFirst = Get-GameTcp
Start-Sleep -Seconds 5
$tcpSecond = Get-GameTcp

# The game's questions, from the log - event 3008 is a query completed: name, type, status, results.
$gameQueries = New-Object System.Collections.Generic.List[object]
if ($isAdmin) {
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = $dnsLogName; Id = 3008; StartTime = $watchFrom } -ErrorAction SilentlyContinue)
    foreach ($e in $events) {
        if (-not $gamePids.ContainsKey([int]$e.ProcessId)) { continue }
        $d = @{}
        foreach ($field in ([xml]$e.ToXml()).Event.EventData.Data) { $d[$field.Name] = $field.'#text' }
        $gameQueries.Add([pscustomobject]@{
            Time = $e.TimeCreated; Process = $gamePids[[int]$e.ProcessId]; Name = [string]$d['QueryName']
            Type = [string]$d['QueryType']; Status = [string]$d['QueryStatus']; Results = [string]$d['QueryResults']
        })
    }
}
if ($dnsLogTurnedOn) { wevtutil sl $dnsLogName /e:false 2>$null | Out-Null }

# ---------------------------------------------------------------- the line
Section 'The line'
$route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric | Select-Object -First 1
$ispDns = @()
if ($route) {
    $ispDns = @((Get-DnsClientServerAddress -InterfaceIndex $route.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses)
    Say "Default route: $($route.InterfaceAlias) via $($route.NextHop)"
    Say "DNS servers on it: $($ispDns -join ', ')"
    $v6 = @((Get-DnsClientServerAddress -InterfaceIndex $route.ifIndex -AddressFamily IPv6 -ErrorAction SilentlyContinue).ServerAddresses |
        Where-Object { $_ -notmatch '^fec0:0:0:ffff' })
    Say "IPv6 DNS servers on it: $(if ($v6) { $v6 -join ', ' } else { 'none' })"
    if ($v6) {
        $hints.Add("Windows also asks $($v6 -join ', ') over IPv6 - usually the modem, passing on the ISP's answer - even with another resolver set for IPv4.")
    }
}
try {
    $info = Invoke-RestMethod -Uri 'https://ipinfo.io/json' -TimeoutSec 6
    Say "Public IP: $($info.ip) - $($info.org) - $($info.city)"
} catch { Say 'Public IP: could not ask ipinfo.io' }
$up = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' } | ForEach-Object { "$($_.Name) [$($_.InterfaceDescription)]" }
Say "Adapters up: $($up -join '; ')"
# The app's own tunnel adapter is "GamePingBooster Tunnel" - not a VPN for this purpose.
if ($up | Where-Object { $_ -notmatch 'GamePingBooster|Game Ping Booster' -and $_ -match 'WARP|WireGuard|OpenVPN|TAP|Tun|VPN|Radmin|ZeroTier|Hamachi' }) {
    $hints.Add('A VPN-type adapter is up. Turn VPNs off while testing - with one up, every name comes back clean or goes elsewhere.')
}
Say "WinHTTP proxy: $((netsh winhttp show proxy | Out-String).Trim() -replace '\s+', ' ')"
$hosts = Get-Content "$env:windir\System32\drivers\etc\hosts" -ErrorAction SilentlyContinue | Where-Object { $_ -match '^\s*[^#\s]' }
Say "hosts file entries: $(if ($hosts) { $hosts -join ' | ' } else { 'none' })"
if ($hosts -match 'pubg|playbattlegrounds|steam') { $hints.Add('The hosts file names PUBG or Steam - it overrides everything else. Remove those lines.') }

# ---------------------------------------------------------------- NRPT
Section 'Name resolution policy (NRPT) and the local resolver'
$rules = @(Get-DnsClientNrptPolicy -ErrorAction SilentlyContinue)
foreach ($r in $rules) { Say "  $($r.Namespace) -> $($r.NameServers -join ',')" }
if ($rules.Count -eq 0) { Say '  no rules' }
$listener = Get-NetUDPEndpoint -LocalAddress 127.0.0.53 -ErrorAction SilentlyContinue | Select-Object -First 1
if ($listener) {
    $owner = (Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue).ProcessName
    Say "127.0.0.53:53 is answered by $owner ($($listener.OwningProcess))"
} else {
    Say '127.0.0.53:53: NOTHING is listening'
}
if ($svc -and $svc.State -ne 'Running' -and -not $listener) {
    $hints.Add('The GamePingBooster service is not running and nothing answers on 127.0.0.53. Open the app, or restart the PC.')
}
$ours = @($rules | Where-Object { $_.NameServers -contains '127.0.0.53' })
if ($ours.Count -gt 0 -and -not $listener) {
    $hints.Add("$($ours.Count) unblock rule(s) point at 127.0.0.53 and nothing is listening there - every name they cover fails. Restart the GamePingBooster service (or the PC); it removes rules left by a previous run.")
}
if (-not ($rules | Where-Object { $_.Namespace -eq '.playbattlegrounds.com' })) {
    Say 'No rule for .playbattlegrounds.com - this machine is not unblocking PUBG (old app, profile not loaded yet, or unblocking off).'
}

# ---------------------------------------------------------------- loopback DNS
# 2026-10-02, FPT: the service's resolver never received its own test query in 10 s, on 0.3.7 and on the new build
# alike, while the same code answers in under 300 ms elsewhere. Something on that machine swallows UDP to port 53
# on loopback - a security suite's DNS filter, a network optimiser, another DNS proxy. These say which.
Section 'Port 53 on this machine'
Get-NetUDPEndpoint -LocalPort 53 -ErrorAction SilentlyContinue | ForEach-Object {
    $p = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
    Say "  udp $($_.LocalAddress):53 - $($p.ProcessName) ($($_.OwningProcess)) $($p.Path)"
}
$av = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue | ForEach-Object { $_.displayName })
Say "Security software: $(if ($av) { $av -join ', ' } else { 'none reported' })"
$suspects = Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -match 'kaspersky|avp|eset|ekrn|bitdefender|vsserv|bdagent|avast|avg|norton|mcafee|sophos|adguard|dnscrypt|acrylic|nextdns|cfos|netlimiter|killer|dragon|lagofast|gearup|exitlag|wtfast|noping|outfox|haste|warp'
} | Select-Object -ExpandProperty ProcessName -Unique
Say "Network or security processes running: $(if ($suspects) { $suspects -join ', ' } else { 'none recognised' })"

function Test-LoopbackUdp([int]$port) {
    $listener = $null; $sender = $null
    try {
        $listener = New-Object Net.Sockets.UdpClient (New-Object Net.IPEndPoint ([Net.IPAddress]::Parse('127.0.0.57'), $port))
        $listener.Client.ReceiveTimeout = 2000
        $sender = New-Object Net.Sockets.UdpClient
        $sw = [Diagnostics.Stopwatch]::StartNew()
        [void]$sender.Send([byte[]](1, 2, 3, 4), 4, '127.0.0.57', $port)
        $from = New-Object Net.IPEndPoint ([Net.IPAddress]::Any, 0)
        [void]$listener.Receive([ref]$from)
        return "arrived in $($sw.ElapsedMilliseconds) ms"
    } catch {
        $m = $_.Exception
        while ($m.InnerException) { $m = $m.InnerException }
        return "NOT RECEIVED: $($m.Message)"
    } finally {
        if ($listener) { $listener.Close() }
        if ($sender) { $sender.Close() }
    }
}
$u53 = Test-LoopbackUdp 53
$uCtl = Test-LoopbackUdp 53530
Say "UDP to 127.0.0.57:53   : $u53"
Say "UDP to 127.0.0.57:53530: $uCtl  (control - 5353 is mDNS and taken)"
if ($u53 -like 'NOT*' -and $uCtl -like 'arrived*') {
    $hints.Add('A datagram to port 53 on loopback never arrives while one to another port does - something on this machine intercepts DNS (see the security software and processes above). That is why the unblock resolver cannot answer.')
}

# ---------------------------------------------------------------- names
Section 'Names: ISP resolver / Windows (what the game gets) / encrypted DNS'
$isp = if ($ispDns.Count -gt 0) { $ispDns[0] } else { $null }
$results = @{}
foreach ($n in $names) {
    $name = $n.Name
    $ispAnswer = if ($isp) { Resolve-Isp $name $isp } else { @{ Addresses = @(); Text = '(no ISP resolver)' } }
    $win = Resolve-Windows $name
    $doh = Resolve-Doh $name
    $results[$name] = @{ Isp = $ispAnswer; Win = $win; Doh = $doh }
    Say "$name  ($($n.Why))"
    Say "    ISP $isp : $($ispAnswer.Text)"
    Say "    Windows      : $($win.Text)"
    Say "    encrypted DNS: $($doh.Text)"
    if ($isp -and (Test-Sinkhole $ispAnswer.Addresses) -and $doh.Addresses.Count -gt 0) {
        Say '    -> the ISP lies about this name'
    }
    if ((Test-Sinkhole $win.Addresses) -and $doh.Addresses.Count -gt 0) {
        Say '    -> WINDOWS GETS NO USABLE ADDRESS'
    }
}
$front = $results['prod-live-front.playbattlegrounds.com']
if ($front -and (Test-Sinkhole $front.Win.Addresses)) {
    $hints.Add('The PUBG lobby name gives Windows no usable address - that is the black screen. See the NRPT section for why.')
}
if ((Test-Sinkhole $results['www.microsoft.com'].Win.Addresses)) {
    $hints.Add('Even www.microsoft.com does not resolve - DNS on this machine is broken in general, not blocked.')
}

# ---------------------------------------------------------------- what the game asked
# Every name the game's processes looked up while it was reproduced. One the app unblocks is answered by it; one it
# does not goes to the ISP's resolver as it always did - and is asked again here straight from the ISP and from
# encrypted DNS, because a name the ISP lies about and nobody listed is exactly what this script could not see before.
function Test-Claimed([string]$name) {
    foreach ($r in $rules) {
        $ns = [string]$r.Namespace
        if ($ns.StartsWith('.') -and ($name -eq $ns.Substring(1) -or $name.EndsWith($ns))) { return $true }
    }
    return $false
}
$statusText = @{ '0' = 'ok'; '9003' = 'NXDOMAIN'; '9501' = 'no record'; '1460' = 'timed out'; '9002' = 'server failure'; '87' = 'no answer logged' }
$ipName = @{}
$procs = @($gamePids.Values | Sort-Object -Unique)
Section "What the game asked Windows ($(if ($procs) { $procs -join ', ' } else { 'no game process seen' }))"
if (-not $isAdmin) {
    Say '  not read - the DNS client log needs PowerShell run as Administrator'
} elseif ($gameQueries.Count -eq 0) {
    Say "  no lookups by the game in the DNS client log since $($watchFrom.ToString('HH:mm:ss')) - was PUBG opened after the log was turned on?"
} else {
    foreach ($q in $gameQueries) {
        foreach ($m in [regex]::Matches($q.Results, '(\d{1,3}\.){3}\d{1,3}')) { if (-not $ipName.ContainsKey($m.Value)) { $ipName[$m.Value] = $q.Name } }
    }
    $asked = 0
    foreach ($g in ($gameQueries | Where-Object { $_.Type -in @('1', '28') } | Group-Object Name | Sort-Object Name)) {
        # Windows answers both families in one go and logs the outcome on one of them - measured: the AAAA event
        # carried the addresses (as ::ffff:a.b.c.d) and the NXDOMAIN, the A event only status 87 and nothing else.
        $useful = @($g.Group | Where-Object { $_.Status -ne '87' } | Sort-Object Time)
        $last = if ($useful.Count -gt 0) { $useful[-1] } else { @($g.Group | Sort-Object Time)[-1] }
        $ips = @([regex]::Matches($last.Results, '(\d{1,3}\.){3}\d{1,3}') | ForEach-Object { $_.Value } | Select-Object -Unique)
        $alias = [regex]::Match($last.Results, 'type:\s*5\s+([^;]+)').Groups[1].Value
        $st = if ($statusText.ContainsKey($last.Status)) { $statusText[$last.Status] } else { "status $($last.Status)" }
        $got = if ($ips.Count -gt 0) { $ips -join ',' } else { $st }
        $claimed = Test-Claimed $g.Name
        Say "  $($g.Name)  [$($last.Process), asked $($g.Count)x, $(if ($claimed) { 'unblocked by the app' } else { 'NOT unblocked' })] -> $got$(if ($alias) { "  (via $alias)" })"
        if ($claimed -or $asked -ge 40) { continue }
        $asked++
        $ia = if ($isp) { Resolve-Isp $g.Name $isp } else { $null }
        $da = Resolve-Doh $g.Name
        if ($ia) { Say "      ISP $isp : $($ia.Text)   encrypted DNS: $($da.Text)" }
        if ($ia -and (Test-Sinkhole $ia.Addresses) -and $da.Addresses.Count -gt 0) {
            Say '      -> the ISP lies about this name, and the app does not unblock it'
            $hints.Add("The game asked $($g.Name), which the ISP answers with $($ia.Text) and the app does not unblock - add its suffix to the PUBG unblock row.")
        } elseif ((Test-Sinkhole $ips) -and $da.Addresses.Count -gt 0) {
            Say "      -> Windows gave the game nothing usable ($got) while encrypted DNS has an address"
        }
    }
}

# ---------------------------------------------------------------- what the game is connected to
Section 'What the game is connected to (TCP, two looks 5 s apart)'
$tcpAll = @($tcpFirst) + @($tcpSecond)
if ($tcpAll.Count -eq 0) {
    Say '  no TCP connections from the game (not running?)'
} else {
    $owners = @{}
    $remotes = @($tcpAll | ForEach-Object { $_.RemoteAddress } | Select-Object -Unique)
    foreach ($ip in ($remotes | Select-Object -First 15)) {
        try {
            $o = Invoke-RestMethod -Uri "https://ipinfo.io/$ip/json" -TimeoutSec 5
            $owners[$ip] = "$($o.org), $($o.city) $($o.country)"
        } catch { $owners[$ip] = '?' }
    }
    foreach ($g in ($tcpAll | Group-Object RemoteAddress, RemotePort | Sort-Object Name)) {
        $ip = $g.Group[0].RemoteAddress
        $port = $g.Group[0].RemotePort
        $one = @($tcpFirst | Where-Object { $_.RemoteAddress -eq $ip -and $_.RemotePort -eq $port })
        $two = @($tcpSecond | Where-Object { $_.RemoteAddress -eq $ip -and $_.RemotePort -eq $port })
        $states = { param($set) ($set | Group-Object State | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', ' }
        $newPorts = @($two | Where-Object { $_.LocalPort -notin @($one | ForEach-Object { $_.LocalPort }) }).Count
        $who = if ($ipName.ContainsKey($ip)) { $ipName[$ip] } else { '?' }
        Say "  ${ip}:$port  first: $(& $states $one)  then: $(& $states $two)$(if ($newPorts) { "  ($newPorts new)" })"
        Say "      name: $who   owner: $($owners[$ip])"
        if (@($one | Where-Object State -eq 'SynSent').Count -gt 0 -and @($two | Where-Object State -eq 'SynSent').Count -gt 0) {
            $hints.Add("The game keeps trying ${ip}:$port ($who) and gets no answer - SynSent on both looks.")
        }
        if ($owners[$ip] -match ' CN$' -and $two.Count -ge 3) {
            $hints.Add("The game holds $($two.Count) connections to ${ip}:$port ($who, $($owners[$ip])) - a server in China, slow from Vietnam; a CDN that maps this line there would explain a long Initializing.")
        }
    }
}

# ---------------------------------------------------------------- the lobby's road
Section 'PUBG lobby: does the real edge answer?'
$frontIps = @()
if ($front) { $frontIps = @($front.Win.Addresses | Where-Object { -not (Test-Sinkhole @($_)) }) + @($front.Doh.Addresses) | Select-Object -Unique -First 3 }
foreach ($ip in $frontIps) { Say "  $ip SNI prod-live-front : $(Test-Tls $ip 'prod-live-front.playbattlegrounds.com')" }
$ga = $results['zk-ga-pcprod.acs.pubg.com']
$gaIps = @()
if ($ga) { $gaIps = @($ga.Win.Addresses | Where-Object { -not (Test-Sinkhole @($_)) }) + @($ga.Doh.Addresses) | Select-Object -Unique -First 2 }
foreach ($ip in $gaIps) { Say "  $ip TCP 40002 (lobby): $(Test-Tcp $ip 40002)" }

# ---------------------------------------------------------------- SNI filtering, edge by edge
# A reset or a stall with the real name, while the control name completes on the SAME address, is filtering by
# name (SNI). Both failing is the address. On VNPT 2026-09-22 Steam was filtered per address AND name: some edges
# reset, others did not - so every resolver's edges are tried, and Google's (which passes the line's subnet and
# often names an in-country cache) separately from Cloudflare's.
Section 'Edges: real name vs control name on the same address'
$edgeNames = @('prod-live-front.playbattlegrounds.com', 'prod-live-xenuine.playbattlegrounds.com', 'acrt-pcprod.acs.pubg.com',
    'api.steampowered.com', 'store.steampowered.com', 'steampipe.akamaized.net')
$known = @{ 'prod-live-front.playbattlegrounds.com' = @('184.84.205.206', '23.66.150.216', '23.77.20.34') }
foreach ($name in $edgeNames) {
    Say $name
    $sets = [ordered]@{
        'cloudflare' = @(Resolve-DohFrom $name 'https://cloudflare-dns.com/dns-query' | Select-Object -First 2)
        'google'     = @(Resolve-DohFrom $name 'https://dns.google/resolve' | Select-Object -First 2)
        'isp'        = @($results[$name].Isp.Addresses | Where-Object { -not (Test-Sinkhole @($_)) } | Select-Object -First 2)
        'known'      = @($known[$name])
    }
    $done = @{}
    foreach ($from in $sets.Keys) {
        foreach ($ip in $sets[$from]) {
            if (-not $ip -or $done.ContainsKey($ip)) { continue }
            $done[$ip] = $true
            Say "    $ip ($from)"
            Say "        real    : $(Test-Tls $ip $name)"
            Say "        control : $(Test-Tls $ip 'www.microsoft.com')"
        }
    }
}

# ---------------------------------------------------------------- the app's own account
Section "The service log's unblock lines (last 60)"
$log = Join-Path $env:ProgramData 'GamePingBooster\logs\gpb-service.log'
if (Test-Path $log) {
    Get-Content $log -Tail 4000 -ErrorAction SilentlyContinue |
        Where-Object { $_ -match 'nblock|NRPT|poison|resolver|encrypted|Loaded the pushed profile|Profiles updated' } |
        Select-Object -Last 60 | ForEach-Object { Say "  $_" }
    # 2026-10-03: a black screen with every name above fine was the client moving relay while the lobby loaded -
    # connected for another game, PUBG started, home moved and the adapter was re-addressed under the lobby's flows.
    Section 'Relay moves and game switches (last 400 log lines)'
    $moves = @(Get-Content $log -Tail 400 -ErrorAction SilentlyContinue |
        Where-Object { $_ -match 'is not used for|\bMoved from|moving the routes over|Detected .* running|configured the virtual adapter|Reconnect' })
    if ($moves.Count -eq 0) { Say '  none' } else { $moves | Select-Object -Last 25 | ForEach-Object { Say "  $_" } }
    if ($moves -match 'is not used for') {
        $hints.Add('The app moved relay when the game started (the relay it connected with does not carry this game) - that cuts the lobby''s connections while it loads. Pick the game in the app before connecting, then reconnect.')
    }
    Section 'The service log, last 120 lines as written'
    Get-Content $log -Tail 120 -ErrorAction SilentlyContinue | ForEach-Object { Say "  $_" }
} else {
    Say "  no log at $log (or no permission - run PowerShell as Administrator)"
}

# ---------------------------------------------------------------- verdict
Section 'What this points at'
if ($hints.Count -eq 0) { Say '  Nothing obvious - send this file as it is.' }
foreach ($h in $hints) { Say "  - $h" }

$lines | Out-File -FilePath $out -Encoding utf8
Write-Host ''
Write-Host "Saved: $out" -ForegroundColor Green
Write-Host 'Send this file back.' -ForegroundColor Green
