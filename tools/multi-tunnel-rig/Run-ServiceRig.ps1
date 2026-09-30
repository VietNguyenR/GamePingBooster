<#
.SYNOPSIS
    The elevated half of the multi-tunnel rig: runs a service build as LocalSystem against the WSL relays, then puts
    this PC back exactly as it was.

.DESCRIPTION
    Wintun needs LocalSystem, so the real service - real adapter, real routes - cannot run from a normal shell. This
    script does the parts that need it, and nothing else:

      1. stops the gpb-service that is running (and remembers that it was);
      2. moves %ProgramData%\GamePingBooster aside - config, licence token, device key, quality records - untouched;
      3. writes the rig's config.json into a fresh one;
      4. adds a default route through the WSL interface at metric 9999, so the service pins the rig's relays on it
         (RouteManager.GetRouteTo needs a gateway on the interface; at 9999 nothing else ever uses it);
      5. starts -ServiceExe as LocalSystem through psexec;
      6. waits for -StopFile to appear (the driver writes it) or -MaxMinutes to pass;
      7. ALWAYS, however it got here: stops that service, copies its logs to -RigData, removes the route, deletes the
         rig's data folder and moves the original back.

    The driver - TunnelCheck's "service" mode - runs as the normal user and talks to the service over its pipe.

.EXAMPLE
    Start-Process pwsh -Verb RunAs -ArgumentList '-File', 'tools\multi-tunnel-rig\Run-ServiceRig.ps1', `
        '-ServiceExe', 'C:\...\gpb-service.exe', '-RigData', 'C:\...\rigdata', '-WslIp', '172.21.44.232'
#>
param(
    [Parameter(Mandatory)][string]$ServiceExe,
    [Parameter(Mandatory)][string]$RigData,
    [Parameter(Mandatory)][string]$WslIp,
    [string]$StopFile = (Join-Path $RigData 'stop'),
    [int]$MaxMinutes = 40
)

$ErrorActionPreference = 'Stop'
$data = Join-Path $env:ProgramData 'GamePingBooster'
$backup = "$data.rig-backup"
$log = Join-Path $RigData 'elevated.log'
function Note($m) { "$(Get-Date -Format o) $m" | Add-Content -Path $log }

New-Item -ItemType Directory -Force -Path $RigData | Out-Null
Note "started as $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"

$routeAdded = $false
$moved = $false
$restartInstalled = $false
try {
    if (Test-Path $backup) { throw "$backup already exists - a previous rig run did not restore. Restore it by hand first." }

    # An installed service is stopped through the Service Control Manager, never killed: the installer sets it to
    # restart 5 s after a failure, and a killed one came back within seconds, took the pipe and read the rig's config -
    # so the driver tested the INSTALLED build, not -ServiceExe (found 2026-09-30: no line of the change under test in
    # the log, and the log opened before the rig's service had even been started). Started again at the end.
    $installed = Get-Service -Name 'GamePingBooster' -ErrorAction SilentlyContinue
    if ($installed -and $installed.Status -ne 'Stopped') {
        Stop-Service -Name 'GamePingBooster' -Force
        $installed.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        $restartInstalled = $true
        Note "stopped the installed GamePingBooster service through the SCM; it is started again at the end"
    }

    Get-Process gpb-service -ErrorAction SilentlyContinue | ForEach-Object {
        Note "stopping the running service (pid $($_.Id), $($_.Path))"
        $_ | Stop-Process -Force
    }
    Start-Sleep -Seconds 2

    if (Test-Path $data) {
        Rename-Item -Path $data -NewName (Split-Path $backup -Leaf)
        $moved = $true
        Note "moved $data aside to $backup"
    }
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    Copy-Item (Join-Path $RigData 'config.json') (Join-Path $data 'config.json')

    $wsl = Get-NetAdapter | Where-Object { $_.Name -like 'vEthernet (WSL*' } | Select-Object -First 1
    if (-not $wsl) { throw 'No vEthernet (WSL) adapter.' }
    New-NetRoute -DestinationPrefix '0.0.0.0/0' -InterfaceIndex $wsl.ifIndex -NextHop $WslIp -RouteMetric 9999 -PolicyStore ActiveStore | Out-Null
    $routeAdded = $true
    Note "default route via $WslIp on $($wsl.Name) ($($wsl.ifIndex)) at metric 9999"

    $psexec = (Get-Command psexec -ErrorAction SilentlyContinue).Source
    if (-not $psexec) { $psexec = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter psexec.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName }
    if (-not $psexec) { throw 'psexec not found.' }
    Start-Process $psexec -ArgumentList @('-accepteula', '-nobanner', '-s', '-d', "`"$ServiceExe`"", '--console') -WindowStyle Hidden
    Note "started $ServiceExe as LocalSystem"

    $deadline = (Get-Date).AddMinutes($MaxMinutes)
    while (-not (Test-Path $StopFile) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    Note ($(if (Test-Path $StopFile) { 'stop requested' } else { "no stop after $MaxMinutes min - restoring anyway" }))
}
catch {
    Note "FAILED: $_"
}
finally {
    Get-Process gpb-service -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $ServiceExe } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    try { Copy-Item (Join-Path $data 'logs') (Join-Path $RigData 'service-logs') -Recurse -Force -ErrorAction Stop } catch { Note "could not copy logs: $_" }
    if ($routeAdded) {
        Get-NetRoute -DestinationPrefix '0.0.0.0/0' -NextHop $WslIp -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
        Note 'route removed'
    }
    # Pins the rig service left behind if it was killed mid-run: /32s to the rig's addresses through the WSL adapter.
    Get-NetRoute -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.DestinationPrefix -like "$($WslIp.Substring(0, $WslIp.LastIndexOf('.')))*/32" -and $_.NextHop -eq $WslIp } |
        Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
    if ($moved) {
        Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
        Rename-Item -Path $backup -NewName (Split-Path $data -Leaf)
        Note "restored $data"
    }
    if ($restartInstalled) {
        try { Start-Service -Name 'GamePingBooster' -ErrorAction Stop; Note 'started the installed service again' }
        catch { Note "could not start the installed service again: $_" }
    }
    Note 'done'
}
