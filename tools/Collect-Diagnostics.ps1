<#
.SYNOPSIS
    Collects everything needed to diagnose a client-side problem into one text file.

.DESCRIPTION
    Run this right after something goes wrong, then send the file that it writes.

    The service log alone is not enough. Three of the failures found in this project so far were
    only explainable by combining it with something else: a route that was silently never deleted
    (needed the routing table), a service that died without unwinding (needed the Windows event
    log, because our own log stops at the moment of a hard crash), and an adapter that existed
    with the wrong interface index (needed the adapter list). This gathers all of it at once, so
    a bug report does not turn into three rounds of "can you also send...".

    The pre-shared key is REDACTED. Never send it to anyone, including whoever is helping you.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Collect-Diagnostics.ps1
#>
param(
    [string]$OutDir = "$env:USERPROFILE\Desktop",
    [int]$EventLogDays = 2
)

$ErrorActionPreference = "Continue"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $OutDir "gpb-diagnostics-$stamp.txt"
$root = Join-Path $env:ProgramData "GamePingBooster"

function Section($title) {
    "" | Out-File -FilePath $out -Append -Encoding utf8
    "=============================================================" | Out-File -FilePath $out -Append -Encoding utf8
    "== $title" | Out-File -FilePath $out -Append -Encoding utf8
    "=============================================================" | Out-File -FilePath $out -Append -Encoding utf8
}

function Emit($value) {
    if ($null -eq $value) { $value = "(nothing)" }
    $value | Out-String -Width 200 | Out-File -FilePath $out -Append -Encoding utf8
}

"Game Ping Booster diagnostics" | Out-File -FilePath $out -Encoding utf8
"collected $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')" | Out-File -FilePath $out -Append -Encoding utf8

Section "Machine"
Emit ([PSCustomObject]@{
    OS        = (Get-CimInstance Win32_OperatingSystem).Caption
    Version   = [Environment]::OSVersion.Version.ToString()
    Machine   = $env:COMPUTERNAME
    Elevated  = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
})

Section "Service state"
Emit (Get-Service -Name GamePingBooster -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType)
Emit (sc.exe qc GamePingBooster 2>&1)
$svc = Get-CimInstance Win32_Service -Filter "Name='GamePingBooster'" -ErrorAction SilentlyContinue
if ($svc) {
    Emit ([PSCustomObject]@{ RunsAs = $svc.StartName; PID = $svc.ProcessId; Path = $svc.PathName })
    if ($svc.ProcessId -gt 0) {
        Emit (Get-Process -Id $svc.ProcessId -ErrorAction SilentlyContinue |
            Select-Object Id, ProcessName, StartTime,
                @{n = 'PrivateMB'; e = { [math]::Round($_.PrivateMemorySize64 / 1MB, 1) } },
                @{n = 'Threads'; e = { $_.Threads.Count } },
                @{n = 'Handles'; e = { $_.HandleCount } })
    }
}

Section "Binaries"
Emit (Get-ChildItem (Join-Path $root "bin") -ErrorAction SilentlyContinue |
    Select-Object Name, Length, LastWriteTime,
        @{n = 'SHA256'; e = { (Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0, 16) } })

Section "Configuration (pre-shared key redacted)"
$cfgPath = Join-Path $root "config.json"
if (Test-Path $cfgPath) {
    $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
    $safe = New-Object PSObject
    foreach ($p in $cfg.PSObject.Properties) {
        $v = $p.Value
        if ($p.Name -eq "psk") {
            if ($null -eq $v) { $v = "(absent)" } else { $v = "REDACTED (" + $v.Length + " chars)" }
        }
        Add-Member -InputObject $safe -MemberType NoteProperty -Name $p.Name -Value $v
    }
    Emit $safe
    Emit "profile in use: $($cfg.profilePath)"
    if ($cfg.profilePath -and (Test-Path $cfg.profilePath)) {
        Emit (Get-Content $cfg.profilePath -Raw)
    }
} else {
    Emit "no config.json at $cfgPath"
}

Section "Network adapters"
Emit (Get-NetAdapter -ErrorAction SilentlyContinue |
    Select-Object ifIndex, Name, InterfaceDescription, Status, LinkSpeed | Sort-Object ifIndex)
Emit (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Select-Object ifIndex, InterfaceAlias, IPAddress, PrefixLength | Sort-Object ifIndex)

Section "Routing table (default routes, tunnel routes, relay pins)"
Emit (Get-NetRoute -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.DestinationPrefix -eq "0.0.0.0/0" -or $_.RouteMetric -le 5 } |
    Select-Object DestinationPrefix, ifIndex, NextHop, RouteMetric, Store |
    Sort-Object ifIndex, DestinationPrefix)

Section "Windows event log - service crashes and errors"
# Our own log stops at the instant of a hard crash. The Service Control Manager records that the
# process died even when nothing in the service got the chance to write a line.
$since = (Get-Date).AddDays(-$EventLogDays)
Emit (Get-WinEvent -FilterHashtable @{ LogName = 'System'; StartTime = $since; ProviderName = 'Service Control Manager' } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -like "*GamePingBooster*" -or $_.Message -like "*Game Ping Booster*" } |
    Select-Object TimeCreated, Id, LevelDisplayName, Message)
Emit (Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -like "*gpb-service*" -or $_.Message -like "*GamePingBooster*" -or $_.Message -like "*Game Ping Booster*" } |
    Select-Object TimeCreated, Id, ProviderName, LevelDisplayName, Message)

Section "Service log files present"
Emit (Get-ChildItem (Join-Path $root "logs") -ErrorAction SilentlyContinue |
    Select-Object Name, Length, LastWriteTime)

# Oldest rotation first so the whole thing reads forwards in time.
$logDir = Join-Path $root "logs"
$rotations = @()
for ($i = 5; $i -ge 1; $i--) {
    $p = Join-Path $logDir "gpb-service.$i.log"
    if (Test-Path $p) { $rotations += $p }
}
$current = Join-Path $logDir "gpb-service.log"
if (Test-Path $current) { $rotations += $current }

foreach ($f in $rotations) {
    Section "LOG: $(Split-Path $f -Leaf)"
    Get-Content $f | Out-File -FilePath $out -Append -Encoding utf8
}

Write-Host ""
Write-Host "Wrote $out"
Write-Host "Size: $([math]::Round((Get-Item $out).Length / 1KB, 1)) KB"
Write-Host ""
Write-Host "The pre-shared key is redacted in this file. Check it yourself before sending:"
Write-Host "  Select-String -Path '$out' -Pattern 'psk' -SimpleMatch"
