<#
.SYNOPSIS
    Tests the Steam Datagram Relay rules of profile-builder\Test-Profile.ps1 with made-up profiles.

.DESCRIPTION
    Test-Profile.ps1 is the last check before a self-hosted profile ships, and it is run for real,
    not lifted: each case writes small profiles to a temporary folder and runs the script on them,
    reading its exit code.

    The cases are the SDR exception and its edges. An SDR game (landmarksRouted) plays and probes on
    the same Valve relay, so its landmarks sit inside its ranges; and two SDR games - Counter-Strike 2
    and Dota 2 - are served the identical relay list, so their ranges overlap by nature. Everything
    else must still be refused: a PUBG-style landmark inside a range, an SDR game against a non-SDR
    one in either direction, and an SDR range over another game's lobby address.

    Addresses are from TEST-NET-3 (203.0.113.0/24) and TEST-NET-2 (198.51.100.0/24), never real ones.
    No network, no service, no Administrator.

.EXAMPLE
    .\gpb.ps1 test
    Runs as part of the whole suite.
#>
$ErrorActionPreference = 'Stop'

$script = Join-Path $PSScriptRoot 'profile-builder\Test-Profile.ps1'
if (-not (Test-Path -LiteralPath $script)) { throw "not found: $script" }

$dir = Join-Path ([System.IO.Path]::GetTempPath()) "gpb-profile-rules-$PID"
New-Item -ItemType Directory -Force -Path $dir | Out-Null

function New-Profile {
    param([string]$Name, [string]$Id, [bool]$Sdr, [string[]]$Cidrs, [string[]]$Landmarks = @(), [string[]]$Lobby = @())
    $game = [ordered]@{
        id             = $Id
        name           = $Id
        processNames   = @("$Id.exe")
        lobbyAddresses = @($Lobby)
        regions        = @(@{ id = 'sgp'; name = 'Singapore'; cidrs = @($Cidrs); landmarks = @($Landmarks) })
    }
    if ($Sdr) { $game['landmarksRouted'] = $true }
    $path = Join-Path $dir "$Name.json"
    @{ schemaVersion = 1; relays = @(); games = @($game) } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}

$failures = 0
function Check {
    param([string]$Name, [string]$Path, [string[]]$Others = @(), [bool]$ShouldPass, [string]$Expect = '')
    # In this process, not a child pwsh: -File passes an array as loose words, and an empty one not at
    # all. The script's `exit` ends only the script and sets $LASTEXITCODE.
    $out = & $script -Path $Path -OtherProfilePaths $Others *>&1 | Out-String
    $passed = $LASTEXITCODE -eq 0
    $ok = ($passed -eq $ShouldPass) -and (-not $Expect -or $out -match [regex]::Escape($Expect))
    if ($ok) {
        Write-Host "  ok    $Name"
    } else {
        $script:failures++
        Write-Host "  FAIL  $Name (exit $LASTEXITCODE, wanted $(if ($ShouldPass) { 'pass' } else { 'refusal' }))" -ForegroundColor Red
        Write-Host ($out.Trim() -replace '(?m)^', '          ')
    }
}

try {
    $relays = @('203.0.113.16/32', '203.0.113.17/32')

    $cs2   = New-Profile 'cs2'   'cs2'   $true  $relays @('203.0.113.16') @('198.51.100.9')
    $dota2 = New-Profile 'dota2' 'dota2' $true  $relays @('203.0.113.16')
    $pubg  = New-Profile 'pubg'  'pubg'  $false @('203.0.113.0/28') @('203.0.113.20')

    Write-Host "Steam Datagram Relay games"
    Check "an SDR game's landmark inside its own relay range is allowed" $cs2 @() $true
    Check "two SDR games holding the same relay list pass" $dota2 @($cs2) $true
    Check "...in either direction" $cs2 @($dota2) $true

    Write-Host "everything else is still refused"
    $pubgOwnLandmark = New-Profile 'pubg-lm' 'pubg' $false @('203.0.113.0/28') @('203.0.113.5')
    Check "a non-SDR game's landmark inside its range" $pubgOwnLandmark @() $false 'CONTAINS LANDMARK'

    $pubgOverRelay = New-Profile 'pubg-over' 'pubg' $false @('203.0.113.16/28') @('203.0.113.40')
    Check "a non-SDR range over an SDR game's relays" $pubgOverRelay @($cs2) $false 'OVERLAPS'

    $sdrOverPubg = New-Profile 'dota2-over' 'dota2' $true @('203.0.113.1/32') @('203.0.113.1')
    Check "an SDR range over a non-SDR game's range" $sdrOverPubg @($pubg) $false 'OVERLAPS'

    $sdrOverLobby = New-Profile 'dota2-lobby' 'dota2' $true @('198.51.100.9/32') @('198.51.100.9')
    Check "an SDR range over another SDR game's lobby address" $sdrOverLobby @($cs2) $false 'lobby address of cs2'
} finally {
    Remove-Item -Recurse -Force -LiteralPath $dir -ErrorAction SilentlyContinue
}

Write-Host ""
if ($failures -gt 0) {
    Write-Host "$failures check(s) FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "all profile rule checks passed" -ForegroundColor Green
exit 0
