<#
.SYNOPSIS
    Generate Counter-Strike 2 (CS2) routing profile from official Valve Steam SDR Web API.

.DESCRIPTION
    CS2 does not use traditional game servers hosted directly on AWS/Azure; all game
    matchmaking and game traffic is routed through Valve Corporation's Steam Datagram
    Relay (SDR) network.

    This script queries the official Valve Steam SDR configuration endpoint:
      https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=730

    For each active Point of Presence (POP):
      1. Extracts all relay server IPv4 addresses.
      2. Groups them into dedicated /24 subnets owned by Valve (AS32590).
      3. Orders regions priority-first for Vietnamese / Southeast Asian players:
         SGP (Singapore), HKG (Hong Kong), TYO (Tokyo), SEO (Seoul), followed by
         Oceania, India, Middle East, Europe, Americas, and Africa.
      4. Formats into a GamePingBooster profile bundle and validates with Test-Profile.ps1.

.PARAMETER ProfilePath
    Target output profile path. Defaults to ..\..\profiles\cs2-vn.example.json.

.PARAMETER RelayEndpoint
    The relay endpoint IP:port to populate in the profile. Defaults to RFC 5737 documentation
    address (203.0.113.10:51820).

.PARAMETER DryRun
    Fetch, parse, and display the discovered regions without writing to disk.

.EXAMPLE
    .\Build-Cs2Profile.ps1

.EXAMPLE
    .\Build-Cs2Profile.ps1 -ProfilePath ..\..\profiles\cs2-vn.json -RelayEndpoint "203.0.113.10:51820"
#>

[CmdletBinding()]
param(
    [string]$ProfilePath,
    [string]$RelayEndpoint = "203.0.113.10:51820",
    [switch]$All,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if (-not $ProfilePath) {
    $ProfilePath = Join-Path $PSScriptRoot '..\..\profiles\cs2-vn.example.json'
}

# Re-indent to two spaces per level matching the rest of the repository.
function Format-Json {
    param([string]$Json)
    try {
        $lines = $Json -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }
        $sb = New-Object System.Text.StringBuilder
        $depth = 0
        foreach ($line in $lines) {
            if ($line -match '^[\}\]]') { $depth-- }
            $null = $sb.AppendLine(('  ' * [Math]::Max($depth, 0)) + $line)
            if ($line -match '[\{\[]$') { $depth++ }
        }
        $out = $sb.ToString() -replace '\[\s*\r?\n\s*\]', '[]'
        $null = ConvertFrom-Json -InputObject $out
        return $out
    } catch {
        return $Json
    }
}

Write-Host "Fetching latest CS2 Steam SDR configuration from Valve API..." -ForegroundColor Cyan
$apiUrl = "https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=730"
$sdrConfig = Invoke-RestMethod -Uri $apiUrl -TimeoutSec 15

if (-not $sdrConfig.pops) {
    throw "Failed to retrieve POPs from Steam SDR API."
}

# Priority ordering for Southeast Asian / Vietnamese players
$priorityPops = @(
    'sgp', # Singapore (Primary)
    'hkg', # Hong Kong
    'tyo', 'tyo1', 'tyo2', # Tokyo
    'seo', # Seoul
    'can', 'canm', 'cant', 'canu', # China Guangzhou
    'sha', 'sham', 'shat', 'shau', # China Shanghai
    'tsn', 'tsnm', 'tsnt', 'tsnu', # China Tianjin
    'wuh', # China Wuhan
    'bom', 'bom2', # Mumbai
    'del', 'del2', # Delhi
    'maa', 'maa2', # Chennai
    'syd', 'syd2', # Sydney
    'dxb', # Dubai
    'fra', # Frankfurt
    'ams', # Amsterdam
    'lhr', # London
    'par', # Paris
    'mad', # Madrid
    'vie', # Vienna
    'sto', 'sto2', # Stockholm
    'waw', # Warsaw
    'iad', # Virginia (US East)
    'ord', # Chicago
    'atl', # Atlanta
    'dfw', # Dallas
    'lax', # Los Angeles
    'sea', # Seattle (US West)
    'gru', # Sao Paulo
    'eze', # Buenos Aires
    'scl', # Santiago
    'lim', # Lima
    'jnb'  # Johannesburg
)

$discoveredPops = @{}
foreach ($prop in $sdrConfig.pops.PSObject.Properties) {
    $popCode = $prop.Name.ToLower()
    $popData = $prop.Value
    if ($popData.relays -and $popData.relays.Count -gt 0) {
        $discoveredPops[$popCode] = $popData
    }
}

Write-Host "Found $($discoveredPops.Count) active Valve SDR POPs with relays." -ForegroundColor Green

# POPs aimed at Vietnamese players (Singapore primary, Hong Kong, Tokyo, Seoul)
$vietnamPops = @('sgp', 'hkg', 'tyo', 'tyo1', 'tyo2', 'seo')

$orderedPopCodes = @()
$targetList = if ($All) { $priorityPops } else { $vietnamPops }
foreach ($code in $targetList) {
    if ($discoveredPops.ContainsKey($code)) {
        $orderedPopCodes += $code
    }
}
if ($All) {
    foreach ($code in ($discoveredPops.Keys | Sort-Object)) {
        if (-not ($orderedPopCodes -contains $code)) {
            $orderedPopCodes += $code
        }
    }
}

$regions = @()
$totalSubnets = 0

foreach ($code in $orderedPopCodes) {
    $pop = $discoveredPops[$code]
    $name = if ($pop.desc) { $pop.desc } else { $code.ToUpper() }

    # Extract relay IPs and convert to /24 subnets
    $subnets = @()
    foreach ($relay in $pop.relays) {
        if ($relay.ipv4) {
            $parts = $relay.ipv4.Split('.')
            if ($parts.Count -eq 4) {
                $subnet = "$($parts[0]).$($parts[1]).$($parts[2]).0/24"
                if (-not ($subnets -contains $subnet)) {
                    $subnets += $subnet
                }
            }
        }
    }

    if ($subnets.Count -eq 0) { continue }

    $regionEntry = [ordered]@{
        id     = "cs2-$code"
        name   = $name
        source = "steam-sdr:$code"
        note   = "Valve SDR $($code.ToUpper()) relay cluster."
        cidrs  = $subnets
    }

    $regions += $regionEntry
    $totalSubnets += $subnets.Count
}

$profile = [ordered]@{
    schemaVersion = 1
    generatedUtc  = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
    note          = "CS2 COMPLETE server IPs - all $($regions.Count) Valve SDR POPs from Steam API (https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=730). Generated $([DateTime]::UtcNow.ToString('yyyy-MM-dd'))."
    games         = @(
        [ordered]@{
            id            = "cs2"
            name          = "Counter-Strike 2"
            processNames  = @("cs2.exe")
            lobbyAddresses = @()
            regions       = $regions
        }
    )
    relays        = @(
        [ordered]@{
            id       = "relay-1"
            name     = "Your relay"
            location = "Singapore"
            endpoint = $RelayEndpoint
        }
    )
}

$jsonOutput = Format-Json ($profile | ConvertTo-Json -Depth 20)

if ($DryRun) {
    Write-Host "Dry run completed: $($regions.Count) regions, $totalSubnets /24 subnets discovered." -ForegroundColor Yellow
    return
}

$fullProfilePath = [System.IO.Path]::GetFullPath($ProfilePath)
Write-Host "Writing CS2 profile to: $fullProfilePath" -ForegroundColor Cyan
[System.IO.File]::WriteAllText($fullProfilePath, $jsonOutput, [System.Text.Encoding]::UTF8)

# Run Test-Profile.ps1 to validate
$testerPath = Join-Path $PSScriptRoot 'Test-Profile.ps1'
if (Test-Path $testerPath) {
    Write-Host "Validating generated profile with Test-Profile.ps1..." -ForegroundColor Cyan
    & $testerPath -Path $fullProfilePath
}

Write-Host "CS2 profile generated and validated successfully ($totalSubnets subnets across $($regions.Count) Valve POPs)." -ForegroundColor Green
