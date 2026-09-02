<#
.SYNOPSIS
    Validate a profile file before shipping it to users.

.DESCRIPTION
    Every check here maps to a concrete way of breaking a real user's network:
      - Prefix too wide       -> drags unrelated services through the relay
      - Contains the relay IP -> routing loop, total loss of connectivity
      - Overlaps private space -> breaks the user's LAN, printers, NAS
      - Too many addresses    -> bloated routing table, netsh takes tens of seconds
    Returns a non-zero exit code on any error so it can be wired into CI.

.EXAMPLE
    .\Test-Profile.ps1 -Path ..\..\profiles\pubg-vn.json
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [int]$MaxPrefixWidth = 20,
    [int]$MaxTotalAddresses = 131072
)

$ErrorActionPreference = 'Stop'

function ConvertTo-UInt32Address {
    param([string]$Address)
    $bytes = ([System.Net.IPAddress]::Parse($Address)).GetAddressBytes()
    return ([uint32]$bytes[0] -shl 24) -bor ([uint32]$bytes[1] -shl 16) -bor `
           ([uint32]$bytes[2] -shl 8)  -bor  [uint32]$bytes[3]
}

function Test-IpInCidr {
    param([uint32]$Ip, [string]$Cidr)
    $parts = $Cidr.Split('/')
    $bits = [int]$parts[1]
    if ($bits -eq 0) { return $true }
    $mask = [uint32]::MaxValue -shl (32 - $bits)
    return (($Ip -band $mask) -eq ((ConvertTo-UInt32Address $parts[0]) -band $mask))
}

$profileData = Get-Content $Path -Raw | ConvertFrom-Json
$errors = @()
$warnings = @()

# Relay addresses - no routed range may contain them.
$relayIps = @()
foreach ($relay in $profileData.relays) {
    $relayHost = $relay.endpoint.Split(':')[0]
    if ($relayHost -match '^\d{1,3}(\.\d{1,3}){3}$') { $relayIps += $relayHost }
    else { $warnings += "Relay '$($relay.id)' uses hostname '$relayHost' - prefer a literal IP so the pinned route is exact." }
}

$privateRanges = @('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '127.0.0.0/8', '169.254.0.0/16', '0.0.0.0/8')
$totalAddresses = 0
$allCidrs = @()

foreach ($game in $profileData.games) {
    foreach ($region in $game.regions) {
        foreach ($cidr in $region.cidrs) {
            $allCidrs += $cidr
            $label = "$($game.id)/$($region.id): $cidr"

            $parts = $cidr.Split('/')
            if ($parts.Count -ne 2 -or $parts[0] -notmatch '^\d{1,3}(\.\d{1,3}){3}$') {
                $errors += "$label - not a valid IPv4 CIDR"
                continue
            }

            $bits = [int]$parts[1]
            if ($bits -lt $MaxPrefixWidth) {
                $errors += "$label - wider than /$MaxPrefixWidth, will drag unrelated services through the relay"
            }
            $totalAddresses += [math]::Pow(2, 32 - $bits)

            foreach ($relayIp in $relayIps) {
                if (Test-IpInCidr (ConvertTo-UInt32Address $relayIp) $cidr) {
                    $errors += "$label - CONTAINS RELAY IP $relayIp, this would cause a routing loop"
                }
            }

            foreach ($private in $privateRanges) {
                if (Test-IpInCidr (ConvertTo-UInt32Address $parts[0]) $private) {
                    $errors += "$label - falls inside private range $private, this would break the user's LAN"
                }
            }
        }
    }

    if ($game.processNames.Count -eq 0) {
        $errors += "Game '$($game.id)' declares no processNames - routes would never be installed"
    }
}

if ($totalAddresses -gt $MaxTotalAddresses) {
    $errors += "Total $([int]$totalAddresses) addresses, over the $MaxTotalAddresses limit"
}
if ($allCidrs.Count -eq 0) {
    $warnings += "The profile has no CIDRs - the app will connect but accelerate nothing."
}

$duplicates = $allCidrs | Group-Object | Where-Object { $_.Count -gt 1 }
foreach ($d in $duplicates) { $warnings += "Duplicate CIDR: $($d.Name) appears $($d.Count) times" }

Write-Host ""
Write-Host "Profile   : $Path"
Write-Host "Prefixes  : $($allCidrs.Count)"
Write-Host "Addresses : $([int]$totalAddresses)"
Write-Host "Relays    : $($profileData.relays.Count)"
Write-Host ""

foreach ($w in $warnings) { Write-Warning $w }
foreach ($e in $errors) { Write-Host "ERROR: $e" -ForegroundColor Red }

if ($errors.Count -gt 0) {
    Write-Host ""
    Write-Host "$($errors.Count) error(s) - DO NOT ship this profile." -ForegroundColor Red
    exit 1
}

Write-Host "Profile is valid." -ForegroundColor Green
exit 0
