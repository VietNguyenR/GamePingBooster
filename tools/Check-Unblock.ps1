<#
.SYNOPSIS
    Why a game or Steam will not load on a player's machine: DNS block, the unblock fix, or something else.

.DESCRIPTION
    Read-only. Changes nothing on the machine. Writes one text file to the Desktop to send back.

    Run it while the game sits on the black screen, or right after:

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

    It contains the machine's public IP and ISP, its DNS servers and the app's log lines about DNS. No password,
    no licence token, no pre-shared key.
#>
param([string]$OutDir = [Environment]::GetFolderPath('Desktop'))

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
