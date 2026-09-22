<#
.SYNOPSIS
    Turn captured game server addresses into profiles/pubg-vn.json. One command, no arguments.

.DESCRIPTION
    Runs the whole "stage 2" pipeline from docs/pubg-ip-ranges.md end to end:

      1. Read observed.txt (produced by Capture-GameTraffic.ps1).
      2. Download and cache the official AWS and Azure range files.
      3. Match every observed address, and widen it to the published cloud prefix - but never
         wider than -MaxPrefixWidth, because AWS and Azure publish /17s and /18s that would drag
         thousands of unrelated services through the relay.
      4. Sort each prefix into the right region of the profile by the cloud region it came from,
         merge with what the profile already had, and combine adjacent halves.
      5. Write the profile (keeping a .bak) and validate it with Test-Profile.ps1.

    Addresses that belong to no Asian AWS/Azure range are NOT added. They go to
    observed-unverified.txt for you to look at. In practice they are usually voice chat
    (Unity/Vivox) or a CDN - real things the game talks to, but not the servers whose latency
    matters.

.PARAMETER ObservedIpPath
    Defaults to observed.txt next to this script.

.PARAMETER ProfilePath
    Defaults to ..\..\profiles\pubg-vn.json.

.PARAMETER MaxPrefixWidth
    Widest prefix that may be accepted, and the width the builder tries FIRST. Default 20
    (/20 = 4096 addresses).

    The builder narrows on its own: if the whole profile does not fit under -MaxTotalAddresses at
    this width, it rebuilds one bit narrower and tries again, down to -NarrowestPrefixWidth. That
    only works because every run rebuilds the CIDR lists from observed.txt instead of adding to
    what the profile already had, so the width applies to the whole profile rather than only to
    today's sightings.

    Accreting is what dead-ended this profile once already: blocks recorded weeks earlier kept
    their width forever, narrowing could not reach them, the total came to rest at exactly 131,072
    addresses, and the only move left was to raise the very limit that exists to prevent it.

    Width is a bet on what one sighting is worth - at /20 one observed server also speaks for the
    4095 addresses around it, at /21 for 2047. Narrowing costs coverage per sighting, never
    correctness: a server in the uncovered half is captured and added next session.

    The one measurement on record contradicts itself and needs redoing. It says a match landed on
    an uncovered server about twice in ten at /21, and also that ten matches at /20 missed twice,
    which would make the two widths equal and leave /20 with nothing to stand on. Treat the
    starting width as unmeasured until someone repeats it.

    Widening past /20 is never automatic. /19 is about 143,000 addresses and /18 about 225,000 -
    10% and 16% of AzureCloud.southeastasia, which is 1,383,855 addresses in the 2026-08-31
    service tags. The earlier note here called /18 "most of" that region; it is off by six times.

.PARAMETER NarrowestPrefixWidth
    Floor for the automatic narrowing. Default 24 (/24 = 256 addresses).

    Below this a block covers little more than the servers already seen, so a profile that still
    does not fit has a data problem rather than a packing problem: either observed.txt is holding
    addresses the game has stopped using, or the ceiling is genuinely too low. The builder stops
    and says which options are left, rather than narrowing towards /32 and calling it a success.

.PARAMETER ManualCidrPath
    Ranges to include that no cloud range file can confirm, one per line as '<regionId> <cidr>'.
    Defaults to manual-cidrs.txt next to this script.

    This file exists because the rebuild is destructive. A CIDR typed straight into the profile
    JSON is gone on the next run without a word, so anything looked up by hand out of
    observed-unverified.txt belongs here instead, with a comment recording what it turned out to
    be and when it was checked.

.PARAMETER AllowCoverageLoss
    Write the profile even when ranges it already had would disappear completely.

    A rebuild is only as complete as observed.txt, and that file has been reset before. When a
    range in the current profile has nothing behind it in the rebuilt set - not even a narrower
    piece of itself - the usual cause is missing history rather than a server that went away, so
    the build stops instead of quietly shrinking what the client routes.

.PARAMETER MaxTotalAddresses
    Ceiling for the whole profile. Default 131,072.

    Narrowing is done for the whole profile at once, so on its own it overshoots: 66 blocks that
    are 4,096 addresses over the ceiling at /21 all became /22, and 63,488 addresses of room were
    left unused while every server lost half its block. So after the width is found, blocks are
    widened back one at a time - busiest first, by sessions seen and then packets from
    observed.txt - for as long as the profile still fits and no widened block covers a landmark.
    Same data, same result: the order is fixed and nothing is carried over from the last build.

    The original 65,536 was picked before any data existed and turned out to be arbitrary: routes
    only exist while the game process is running, so the practical difference between 65k and 131k
    addresses of Azure southeastasia is some non-game traffic taking a detour during a match. What
    the ceiling really guards against is routing a whole cloud region by accident, and 131,072
    still does that.

.PARAMETER DryRun
    Show what would change without touching the profile.

.EXAMPLE
    .\Build-PubgProfile.ps1

.EXAMPLE
    .\Build-PubgProfile.ps1 -DryRun
#>

[CmdletBinding()]
param(
    [string]$ObservedIpPath,
    [string]$LandmarkObservedPath,
    [string]$ProfilePath,
    [string]$GameId = 'pubg',
    [int]$MinLandmarkSightings = 2,
    [int]$MaxLandmarksPerRegion = 3,
    [int]$MaxPrefixWidth = 20,
    [int]$NarrowestPrefixWidth = 24,
    [int]$MaxTotalAddresses = 131072,
    [string]$ManualCidrPath,
    [switch]$AllowCoverageLoss,
    [switch]$DryRun,
    [string[]]$AwsRegions = @('ap-southeast-1', 'ap-northeast-1', 'ap-northeast-2'),
    [string[]]$AzureRegions = @('southeastasia', 'japaneast', 'koreacentral'),

    # AWS Global Accelerator. Its anycast addresses are published only as GLOBAL /17s that every
    # accelerator customer draws from, so a match is routed as exactly its /32 - never widened.
    [switch]$GlobalAccelerator,

    # Autonomous systems the game's publisher owns outright, e.g. 6507 for Riot Direct. The
    # prefixes they announce (RIPEstat) count as published ranges.
    [int[]]$Asns = @(),

    [string]$UnverifiedPath,

    # Packets a second to one address that make it a server of this game, written into the profile
    # for the client's discovery to use instead of its built-in 500-in-30-seconds. Declared in
    # games.json and passed by ./gpb profile; left out, the field is removed from the profile and
    # the client falls back to its default. See GameEntry.DiscoveryPacketsPerSecond.
    [double]$DiscoveryPacketsPerSecond = 0,

    # Only used when -ProfilePath does not exist yet, to start the new profile.
    [string]$GameName,
    [string[]]$ProcessNames = @()
)

$ErrorActionPreference = 'Stop'

# The defaults are PUBG's files, and only PUBG gets them. They used to be filled in whatever
# -GameId was given, so a run for another game read PUBG's observed.txt and promoted PUBG's
# datacentre probes out of landmarks-observed.txt into that game's profile. Every other game names
# its own files; ./gpb profile <game> passes them from games.json.
if ($GameId -eq 'pubg') {
    if (-not $ObservedIpPath) { $ObservedIpPath = Join-Path $PSScriptRoot 'observed.txt' }
    if (-not $LandmarkObservedPath) { $LandmarkObservedPath = Join-Path $PSScriptRoot 'landmarks-observed.txt' }
    if (-not $ProfilePath) { $ProfilePath = Join-Path $PSScriptRoot '..\..\profiles\pubg-vn.json' }
    if (-not $ManualCidrPath) { $ManualCidrPath = Join-Path $PSScriptRoot 'manual-cidrs.txt' }
    if (-not $UnverifiedPath) { $UnverifiedPath = Join-Path $PSScriptRoot 'observed-unverified.txt' }
} else {
    foreach ($required in 'ObservedIpPath', 'ProfilePath', 'ManualCidrPath', 'UnverifiedPath') {
        if (-not (Get-Variable -Name $required -ValueOnly)) {
            throw "-$required is required for '$GameId' - its files are declared in games.json. Run: ./gpb profile $GameId"
        }
    }
}
$cacheDir = Join-Path $PSScriptRoot '.cache'

# games.json is read here as well as by ./gpb, for one thing: the addresses every OTHER game was
# observed on, which this game's profile must never cover. Reading it here rather than having them
# passed in means a run that bypasses ./gpb is held to the same rule.
. (Join-Path $PSScriptRoot '..\GpbGames.ps1')
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

# Which profile region each published range belongs to. Adding a new region to the game means
# adding it here and in games.json.
$regionMap = @{
    'ap-southeast-1' = 'asia-sg'; 'southeastasia' = 'asia-sg'
    'ap-east-1'      = 'asia-hk'
    'ap-northeast-1' = 'asia-jp'; 'japaneast'     = 'asia-jp'
    'ap-northeast-2' = 'asia-kr'; 'koreacentral'  = 'asia-kr'
    # Global Accelerator's ranges carry no location - the same address is announced from every
    # edge - so its /32s form a region of their own rather than being guessed into a city.
    'GLOBAL'         = 'aws-ga'
}
$regionNames = @{
    'asia-sg' = 'Southeast Asia (Singapore)'
    'asia-hk' = 'Hong Kong'
    'asia-jp' = 'Japan (Tokyo)'
    'asia-kr' = 'Korea (Seoul)'
    'aws-ga'  = 'AWS Global Accelerator (anycast)'
}
# An ASN's announcements carry no location either, so the same applies.
foreach ($asn in $Asns) {
    $regionMap["AS$asn"] = "as$asn"
    $regionNames["as$asn"] = "AS$asn network"
}

# ------------------------------------------------------------------- helpers

function ConvertTo-UInt32Address {
    param([string]$Address)
    $bytes = ([System.Net.IPAddress]::Parse($Address)).GetAddressBytes()
    return ([uint32]$bytes[0] -shl 24) -bor ([uint32]$bytes[1] -shl 16) -bor `
           ([uint32]$bytes[2] -shl 8)  -bor  [uint32]$bytes[3]
}

function ConvertFrom-UInt32Address {
    param([uint32]$Value)
    return "{0}.{1}.{2}.{3}" -f (($Value -shr 24) -band 0xff), (($Value -shr 16) -band 0xff),
                                (($Value -shr 8) -band 0xff), ($Value -band 0xff)
}

# PowerShell 5.1's ConvertTo-Json indents four spaces per level AND aligns values to the key,
# producing 20-space gutters and diffs nobody can read. Re-indent to two spaces per level.
# If anything about the reformat goes wrong, the original text is kept: a valid ugly file beats
# a pretty broken one, and this file is what the client routes traffic from.
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
        $out = $sb.ToString() -replace '\[\s*
?
\s*\]', '[]'
        $null = ConvertFrom-Json -InputObject $out    # sanity check before we hand it back
        return $out
    } catch {
        return $Json
    }
}

function Test-IpInCidr {
    param([uint32]$Ip, [string]$Cidr)
    $parts = $Cidr.Split('/')
    if ($parts.Count -ne 2) { return $false }
    if ($parts[0].Contains(':')) { return $false }   # skip IPv6
    $bits = [int]$parts[1]
    if ($bits -eq 0) { return $true }
    $mask = [uint32]::MaxValue -shl (32 - $bits)
    return (($Ip -band $mask) -eq ((ConvertTo-UInt32Address $parts[0]) -band $mask))
}

# Merge sibling prefixes into their parent, e.g. 52.139.216.0/24 + 52.139.217.0/24 -> /23.
# Lossless: both halves were actually observed, so the parent covers exactly the same addresses.
# It only merges when BOTH halves are present - widening on a hunch would be guessing about
# servers nobody has seen. Never goes wider than $MinPrefixWidth.
function Merge-AdjacentPrefixes {
    param([string[]]$Cidrs, [int]$MinPrefixWidth)

    $current = @($Cidrs | Sort-Object -Unique)
    $changed = $true
    while ($changed) {
        $changed = $false
        $out = New-Object System.Collections.ArrayList
        $consumed = @{}

        for ($i = 0; $i -lt $current.Count; $i++) {
            if ($consumed.ContainsKey($i)) { continue }
            $a = $current[$i]
            $aBits = [int]$a.Split('/')[1]
            $aNet = ConvertTo-UInt32Address $a.Split('/')[0]
            $parentBits = $aBits - 1
            $merged = $false

            if ($parentBits -ge $MinPrefixWidth) {
                $parentMask = [uint32]::MaxValue -shl (32 - $parentBits)
                for ($j = $i + 1; $j -lt $current.Count; $j++) {
                    if ($consumed.ContainsKey($j)) { continue }
                    $b = $current[$j]
                    if ([int]$b.Split('/')[1] -ne $aBits) { continue }
                    $bNet = ConvertTo-UInt32Address $b.Split('/')[0]
                    if ((($aNet -band $parentMask) -eq ($bNet -band $parentMask)) -and ($aNet -ne $bNet)) {
                        $null = $out.Add((ConvertFrom-UInt32Address ($aNet -band $parentMask)) + "/$parentBits")
                        $consumed[$j] = $true
                        $merged = $true
                        $changed = $true
                        break
                    }
                }
            }
            if (-not $merged) { $null = $out.Add($a) }
        }
        $current = @($out | Sort-Object -Unique)
    }

    # Drop anything already covered by a wider prefix in the same set. The sibling merge above
    # cannot do this: a /24 sitting inside a /20 is nobody's sibling, so it survived every pass
    # and then got counted a second time. That inflated the validator's total - it saw 66,304
    # addresses where the union was really 61,440, and failed a profile that was actually within
    # the limit - and it installed 32 routes into the adapter where 13 cover the same ground.
    $final = New-Object System.Collections.ArrayList
    foreach ($c in $current) {
        $cBits = [int]$c.Split('/')[1]
        $cNet = ConvertTo-UInt32Address $c.Split('/')[0]
        $covered = $false
        foreach ($other in $current) {
            if ($other -eq $c) { continue }
            $oBits = [int]$other.Split('/')[1]
            if ($oBits -ge $cBits) { continue }   # only a strictly wider prefix can contain this one
            $oMask = [uint32]::MaxValue -shl (32 - $oBits)
            $oNet = ConvertTo-UInt32Address $other.Split('/')[0]
            if (($cNet -band $oMask) -eq ($oNet -band $oMask)) { $covered = $true; break }
        }
        if (-not $covered) { $null = $final.Add($c) }
    }

    return @($final | Sort-Object -Unique)
}

# ------------------------------------------------------------- range sources

function Get-AwsRanges {
    $path = Join-Path $cacheDir 'aws-ip-ranges.json'
    $fresh = (Test-Path $path) -and ((Get-Date) - (Get-Item $path).LastWriteTime).TotalHours -lt 24
    if ($fresh) {
        Write-Host "==> AWS ranges from cache ($([int]((Get-Date) - (Get-Item $path).LastWriteTime).TotalHours)h old)"
    } else {
        Write-Host "==> Downloading AWS ip-ranges.json"
        Invoke-WebRequest -Uri 'https://ip-ranges.amazonaws.com/ip-ranges.json' -OutFile $path -UseBasicParsing -TimeoutSec 30
    }
    return (Get-Content $path -Raw | ConvertFrom-Json)
}

function Get-AzureRanges {
    # Microsoft publishes a new file every Monday under a folder GUID that has been stable for
    # years, but the filename carries that Monday's date and there is no "latest" alias. So walk
    # back through recent Mondays and take the first one that exists.
    $existing = Get-ChildItem (Join-Path $cacheDir 'ServiceTags_Public_*.json') -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if ($existing -and ((Get-Date) - $existing.LastWriteTime).TotalDays -lt 7) {
        Write-Host "==> Azure ranges from cache ($($existing.Name))"
        return (Get-Content $existing.FullName -Raw | ConvertFrom-Json)
    }

    $base = 'https://download.microsoft.com/download/7/1/d/71d86715-5596-4529-9b13-da13a5de5b63/ServiceTags_Public_{0}.json'
    $today = Get-Date
    for ($i = 0; $i -lt 28; $i++) {
        $day = $today.AddDays(-$i)
        if ($day.DayOfWeek -ne 'Monday') { continue }
        $stamp = $day.ToString('yyyyMMdd')
        $url = $base -f $stamp
        $target = Join-Path $cacheDir "ServiceTags_Public_$stamp.json"
        try {
            Write-Host "==> Downloading Azure Service Tags ($stamp)"
            Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing -TimeoutSec 60
            return (Get-Content $target -Raw | ConvertFrom-Json)
        } catch {
            # That Monday is not published; try the one before.
        }
    }

    if ($existing) {
        Write-Warning "Could not download a fresh Azure file; falling back to the cached $($existing.Name)."
        return (Get-Content $existing.FullName -Raw | ConvertFrom-Json)
    }
    throw "Could not obtain the Azure Service Tags file. Download it by hand from https://www.microsoft.com/en-us/download/details.aspx?id=56519 into $cacheDir"
}

function Get-AsnPrefixes {
    param([int]$Asn)

    # RIPEstat's announced-prefixes: what the ASN has announced to the global routing table over
    # the last two weeks, as seen by RIPE's route collectors. Not a list the publisher maintains
    # for anyone - Riot publishes none - but it is public, machine-readable, and cannot contain an
    # address the publisher does not route, which is the property the cross-check needs.
    $path = Join-Path $cacheDir "announced-prefixes-AS$Asn.json"
    $fresh = (Test-Path $path) -and ((Get-Date) - (Get-Item $path).LastWriteTime).TotalHours -lt 24
    if ($fresh) {
        Write-Host "==> AS$Asn prefixes from cache ($([int]((Get-Date) - (Get-Item $path).LastWriteTime).TotalHours)h old)"
    } else {
        Write-Host "==> Downloading the prefixes AS$Asn announces (RIPEstat)"
        $partial = "$path.download"
        try {
            Invoke-WebRequest -Uri "https://stat.ripe.net/data/announced-prefixes/data.json?resource=AS$Asn" `
                -OutFile $partial -UseBasicParsing -TimeoutSec 60
            Move-Item -LiteralPath $partial -Destination $path -Force
        } catch {
            Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
            if (-not (Test-Path $path)) {
                throw "Could not get the prefixes AS$Asn announces from RIPEstat: $($_.Exception.Message)"
            }
            Write-Warning "Could not refresh AS$Asn from RIPEstat; using the cached copy from $((Get-Item $path).LastWriteTime)."
        }
    }

    $data = Get-Content $path -Raw | ConvertFrom-Json
    $prefixes = @($data.data.prefixes | ForEach-Object { $_.prefix } | Where-Object { $_ -and -not $_.Contains(':') })
    if ($prefixes.Count -eq 0) {
        # An empty answer is a failed lookup, not an ASN that routes nothing. Treated as success it
        # would hold every observed address back and the coverage check would then refuse the build
        # with a message about observed.txt.
        throw "RIPEstat lists no IPv4 prefix for AS$Asn. Delete $path and try again, or check the ASN in games.json."
    }
    return $prefixes
}

<#
.SYNOPSIS
    Every address another game is known to use, and every other game's profile file.

.DESCRIPTION
    Read from the other games' own evidence: their observed and landmark files, and the single
    addresses in their profiles - landmarks, lobby addresses and /32 ranges. This game's profile
    may not cover any of them.

    Deliberately addresses, not the other profiles' ranges. A range is a bet made from addresses,
    and the two games' bets can legitimately land on the same cloud block; the addresses are what
    each game was actually seen talking to. Narrowing away from those on both sides is enough for
    their ranges never to overlap, and Test-Profile.ps1 checks the ranges as well.
#>
function Get-OtherGameEvidence {
    $owners = @{}
    $profiles = @()

    foreach ($other in @(Get-GpbOtherGames -RepoRoot $repoRoot -Id $GameId)) {
        foreach ($file in @($other.ObservedPath, $other.LandmarkPath)) {
            if (-not $file -or -not (Test-Path -LiteralPath $file)) { continue }
            $leaf = Split-Path $file -Leaf
            foreach ($line in (Get-Content -LiteralPath $file)) {
                $first = ($line.Trim() -split '\s+')[0]
                if ($first -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { continue }
                if (-not $owners.ContainsKey($first)) { $owners[$first] = "$($other.Id) ($leaf)" }
            }
        }

        if (-not $other.ProfilePath -or -not (Test-Path -LiteralPath $other.ProfilePath)) { continue }
        $profiles += $other.ProfilePath
        $leaf = Split-Path $other.ProfilePath -Leaf
        $data = Get-Content -LiteralPath $other.ProfilePath -Raw | ConvertFrom-Json
        foreach ($g in @($data.games)) {
            if (-not $g -or $g.id -eq $GameId) { continue }
            $singles = @($g.lobbyAddresses)
            foreach ($r in @($g.regions)) {
                if (-not $r) { continue }
                $singles += @($r.landmarks)
                $singles += @($r.cidrs | Where-Object { "$_" -match '/32$' })
            }
            foreach ($s in $singles) {
                if (-not $s) { continue }
                $address = ("$s".Trim() -split '/')[0]
                if ($address -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { continue }
                if (-not $owners.ContainsKey($address)) { $owners[$address] = "$($g.id) ($leaf)" }
            }
        }
    }

    $list = @(foreach ($address in $owners.Keys) {
        [pscustomobject]@{ Address = $address; Value = (ConvertTo-UInt32Address $address); Owner = $owners[$address] }
    })
    return @{ Addresses = $list; Owners = $owners; Profiles = $profiles }
}

# The first address of another game inside the /$Bits block around $Value, or $null.
function Find-ForeignInBlock {
    param([uint32]$Value, [int]$Bits)
    $mask = [uint32](4294967295L -band (-bnot ((1L -shl (32 - $Bits)) - 1L)))
    $network = $Value -band $mask
    foreach ($f in $foreign.Addresses) {
        if (($f.Value -band $mask) -eq $network) { return $f }
    }
    return $null
}

# ------------------------------------------------------------ observed input

if (-not (Test-Path $ObservedIpPath)) {
    throw "No $ObservedIpPath yet. Run .\Capture-GameTraffic.ps1 while playing a few matches first."
}
New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null

Write-Host "==> Reading $ObservedIpPath"
# Only the first column. observed.txt now carries the evidence that put each address there -
# packets, duration, how many sessions - so the line is no longer just an address. Splitting and
# taking field zero reads both layouts, and a comment line fails the IPv4 test on its own.
$observed = Get-Content $ObservedIpPath |
    ForEach-Object { ($_.Trim() -split '\s+')[0] } |
    Where-Object { $_ -match '^\d{1,3}(\.\d{1,3}){3}$' } |
    Sort-Object -Unique

if ($observed.Count -eq 0) { throw "No valid IPv4 addresses in $ObservedIpPath" }

# The evidence behind each address - sessions seen and packets, columns 4 and 2 - which decides
# which blocks are widened back first when there is room under -MaxTotalAddresses. A line with
# only an address counts as one session and no packets. The busiest line wins for a repeated one.
$evidence = @{}
foreach ($line in (Get-Content $ObservedIpPath)) {
    $fields = $line.Trim() -split '\s+'
    if ($fields.Count -lt 1 -or $fields[0] -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { continue }
    $packets = 0L; $sightings = 1
    if ($fields.Count -gt 1) { [void][long]::TryParse($fields[1], [ref]$packets) }
    if ($fields.Count -gt 3) { [void][int]::TryParse($fields[3], [ref]$sightings) }
    $prev = $evidence[$fields[0]]
    if ($null -eq $prev -or $sightings -gt $prev.Sightings -or ($sightings -eq $prev.Sightings -and $packets -gt $prev.Packets)) {
        $evidence[$fields[0]] = [pscustomobject]@{ Sightings = $sightings; Packets = $packets }
    }
}

# Private space must never be routed - drop it before it can reach the profile.
$observed = @($observed | Where-Object {
    $v = ConvertTo-UInt32Address $_
    -not ((Test-IpInCidr $v '10.0.0.0/8') -or (Test-IpInCidr $v '172.16.0.0/12') -or
          (Test-IpInCidr $v '192.168.0.0/16') -or (Test-IpInCidr $v '127.0.0.0/8') -or
          (Test-IpInCidr $v '169.254.0.0/16'))
})
Write-Host "    $($observed.Count) public addresses"

# ------------------------------------------------------------------- sources

$aws = Get-AwsRanges
$awsPrefixes = @($aws.prefixes | Where-Object { $_.region -in $AwsRegions -and $_.service -eq 'EC2' })
Write-Host "    $($awsPrefixes.Count) AWS EC2 prefixes in $($AwsRegions -join ', ')"

$azure = Get-AzureRanges
$azurePrefixes = @()
foreach ($region in $AzureRegions) {
    $tag = $azure.values | Where-Object { $_.name -eq "AzureCloud.$region" }
    if (-not $tag) { continue }
    foreach ($cidr in $tag.properties.addressPrefixes) {
        if ($cidr.Contains(':')) { continue }
        $azurePrefixes += [pscustomobject]@{ Prefix = $cidr; Region = $region }
    }
}
Write-Host "    $($azurePrefixes.Count) Azure prefixes in $($AzureRegions -join ', ')"

$gaPrefixes = @()
if ($GlobalAccelerator) {
    $gaPrefixes = @($aws.prefixes | Where-Object { $_.service -eq 'GLOBALACCELERATOR' })
    Write-Host "    $($gaPrefixes.Count) AWS Global Accelerator prefixes - matches are routed one address at a time"
}

$asnPrefixes = @()
foreach ($asn in $Asns) {
    $announced = @(Get-AsnPrefixes -Asn $asn)
    foreach ($cidr in $announced) { $asnPrefixes += [pscustomobject]@{ Prefix = $cidr; Asn = $asn } }
    Write-Host "    $($announced.Count) IPv4 prefixes announced by AS$asn"
}

$foreign = Get-OtherGameEvidence
Write-Host "    $($foreign.Addresses.Count) addresses belong to other games in games.json - never covered by this profile"

# ------------------------------------------------------------------ matching
#
# Which published cloud prefix an address falls inside does not depend on the width the profile
# ends up using, so it is resolved once, here. Turning a match into an actual profile prefix -
# the clamp - is Get-ClampedPrefixes below, and that runs again for every width the builder tries.

Write-Host ""
Write-Host "==> Matching" -ForegroundColor Cyan

$matched = @()
$unverified = @()

foreach ($ip in $observed) {
    $value = ConvertTo-UInt32Address $ip

    # Seen by this game AND another. No range can be drawn around it without covering the other
    # game's server, and nothing here says whose it really is - usually something both games talk
    # to, like a voice or telemetry service, which is not worth routing anyway.
    if ($foreign.Owners.ContainsKey($ip)) {
        Write-Host ("    {0,-18} also observed for {1} - held back" -f $ip, $foreign.Owners[$ip]) -ForegroundColor DarkYellow
        $unverified += $ip
        continue
    }

    $best = $null; $bestBits = -1; $source = $null; $cloudRegion = $null; $anycast = $false

    foreach ($p in $awsPrefixes) {
        if (Test-IpInCidr $value $p.ip_prefix) {
            $bits = [int]$p.ip_prefix.Split('/')[1]
            if ($bits -gt $bestBits) {
                $best = $p.ip_prefix; $bestBits = $bits
                $source = "aws:$($p.region)"; $cloudRegion = $p.region; $anycast = $false
            }
        }
    }
    foreach ($p in $azurePrefixes) {
        if (Test-IpInCidr $value $p.Prefix) {
            $bits = [int]$p.Prefix.Split('/')[1]
            if ($bits -gt $bestBits) {
                $best = $p.Prefix; $bestBits = $bits
                $source = "azure:$($p.Region)"; $cloudRegion = $p.Region; $anycast = $false
            }
        }
    }
    foreach ($p in $gaPrefixes) {
        if (Test-IpInCidr $value $p.ip_prefix) {
            $bits = [int]$p.ip_prefix.Split('/')[1]
            if ($bits -gt $bestBits) {
                $best = $p.ip_prefix; $bestBits = $bits
                $source = 'aws:globalaccelerator'; $cloudRegion = 'GLOBAL'; $anycast = $true
            }
        }
    }
    foreach ($p in $asnPrefixes) {
        if (Test-IpInCidr $value $p.Prefix) {
            $bits = [int]$p.Prefix.Split('/')[1]
            if ($bits -gt $bestBits) {
                $best = $p.Prefix; $bestBits = $bits
                $source = "as$($p.Asn)"; $cloudRegion = "AS$($p.Asn)"; $anycast = $false
            }
        }
    }

    if (-not $best) {
        Write-Host ("    {0,-18} in no published range declared for this game - held back" -f $ip) -ForegroundColor DarkYellow
        $unverified += $ip
        continue
    }

    $matched += [pscustomobject]@{
        Address = $ip; Value = $value; Published = $best; PublishedBits = $bestBits
        Source = $source; RegionId = $regionMap[$cloudRegion]; Anycast = $anycast
    }
}
Write-Host "    $($matched.Count) matched in a published range, $($unverified.Count) held back"

function Get-BlockKey {
    param([uint32]$Value, [int]$Bits)
    $mask = [uint32](4294967295L -band (-bnot ((1L -shl (32 - $Bits)) - 1L)))
    return (ConvertFrom-UInt32Address ([uint32]($Value -band $mask))) + "/$Bits"
}

function Get-ClampedPrefixes {
    # $Wider: blocks, as "network/bits" keys, that the fill-back step widened past $Width. The widest
    # one holding an address wins.
    param([int]$Width, [hashtable]$Wider = @{})

    # When the published prefix is wider than we allow, keep the /Width BLOCK that contains this
    # address. The obvious alternative - collapse to the observed /24 - is what this used to do,
    # and it threw away 99.6% of a /16: Azure publishes southeastasia as /16s and /17s, so every
    # single /24 needed its own separate sighting and the profile could never saturate no matter
    # how long anyone played. Clamping keeps the cap meaningful and bounded while letting one
    # observation speak for the block it landed in.
    # Decimal with an L suffix, not 0xFFFFFFFF: PowerShell 5.1 parses that hex literal as int32
    # BEFORE any cast, so it arrives as -1 and every mask built from it comes out negative.
    $hostBits = 32 - $Width
    $capMask = [uint32](4294967295L -band (-bnot ((1L -shl $hostBits) - 1L)))

    $byRegion = @{}
    $sources = @{}
    $lines = @()

    foreach ($m in $matched) {
        $why = @()
        if ($m.Anycast) {
            # One accelerator's static address, drawn from a pool every Global Accelerator customer
            # shares - PUBG's lobby is one of them. Its neighbours are somebody else's.
            $bits = 32
            $why += "anycast, published as $($m.Published)"
        } elseif ($m.PublishedBits -lt $Width) {
            $bits = $Width
            $why += "published as $($m.Published)"
            for ($level = [math]::Max($m.PublishedBits, $MaxPrefixWidth); $level -lt $Width; $level++) {
                if ($Wider.ContainsKey((Get-BlockKey -Value $m.Value -Bits $level))) {
                    $bits = $level
                    $why += "widened back to /$level - there was room under the address limit"
                    break
                }
            }
        } else {
            $bits = $m.PublishedBits
        }

        # Never over another game's address. Narrow one bit at a time until the block holds none;
        # at /32 the block is this address alone, and an address both games use never got this far.
        $start = $bits
        $firstHit = Find-ForeignInBlock -Value $m.Value -Bits $bits
        $hit = $firstHit
        while ($hit -and $bits -lt 32) {
            $bits++
            $hit = Find-ForeignInBlock -Value $m.Value -Bits $bits
        }
        if ($bits -gt $start) {
            $why += "narrowed from /$start, which holds $($firstHit.Address) of $($firstHit.Owner)"
        }

        $mask = [uint32](4294967295L -band (-bnot ((1L -shl (32 - $bits)) - 1L)))
        $chosen = (ConvertFrom-UInt32Address ([uint32]($m.Value -band $mask))) + "/$bits"
        $suffix = ''
        if ($why.Count -gt 0) { $suffix = '  (' + ($why -join '; ') + ')' }
        $fields = @($m.Address, $m.Source, $chosen, $suffix)
        $lines += ("    {0,-18} {1,-22} {2}{3}" -f $fields)

        if (-not $byRegion.ContainsKey($m.RegionId)) { $byRegion[$m.RegionId] = @(); $sources[$m.RegionId] = @() }
        $byRegion[$m.RegionId] += $chosen
        $sources[$m.RegionId] += $m.Source
    }

    return @{ ByRegion = $byRegion; Sources = $sources; Lines = $lines }
}

function Read-ManualCidrs {
    param([string]$Path)

    # Ranges no cloud range file can vouch for, kept outside the profile because the profile is
    # regenerated from observed.txt on every run and would drop them without a word.
    #   asia-sg  85.236.96.0/20   # checked 2026-09-09, <whose network it turned out to be>
    $out = @{}
    if (-not (Test-Path $Path)) { return $out }

    foreach ($line in Get-Content $Path) {
        $text = ($line -split '#')[0].Trim()
        if (-not $text) { continue }
        $fields = $text -split '\s+'
        if ($fields.Count -lt 2) {
            throw "$Path : '$line' - expected '<regionId> <cidr>', for example 'asia-sg 85.236.96.0/20'"
        }
        if ($fields[1] -notmatch '^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$') {
            throw "$Path : '$($fields[1])' is not an IPv4 CIDR"
        }
        if (-not $out.ContainsKey($fields[0])) { $out[$fields[0]] = @() }
        $out[$fields[0]] += $fields[1]
    }
    return $out
}

function Test-CidrsOverlap {
    param([string]$A, [string]$B)
    $aBits = [int]$A.Split('/')[1]
    $bBits = [int]$B.Split('/')[1]
    $bits = [math]::Min($aBits, $bBits)
    if ($bits -eq 0) { return $true }
    $mask = [uint32]::MaxValue -shl (32 - $bits)
    return (((ConvertTo-UInt32Address $A.Split('/')[0]) -band $mask) -eq
            ((ConvertTo-UInt32Address $B.Split('/')[0]) -band $mask))
}

function New-ProfileAtWidth {
    param([object]$Source, [int]$Width, [hashtable]$Manual, [object[]]$Landmarks, [hashtable]$Wider = @{})

    # Work on a copy. The width search builds the whole profile several times over, and a rejected
    # attempt must not leave its prefixes behind in the object the next attempt starts from.
    $data = $Source | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $g = $data.games | Where-Object { $_.id -eq $GameId }

    $clamped = Get-ClampedPrefixes -Width $Width -Wider $Wider
    $byRegion = $clamped.ByRegion
    $sources = $clamped.Sources

    $previous = @{}; $current = @{}; $added = @{}; $warnings = @(); $newCount = 0
    $regionIds = @(@($g.regions | ForEach-Object { $_.id }) + @($byRegion.Keys) + @($Manual.Keys) |
                   Sort-Object -Unique)

    foreach ($regionId in $regionIds) {
        $region = $g.regions | Where-Object { $_.id -eq $regionId }
        if (-not $region) {
            $region = [pscustomobject]@{
                id = $regionId; name = $regionNames[$regionId]; source = ''; note = ''; cidrs = @(); landmarks = @()
            }
            $g.regions += $region
        }
        if ($null -eq $region.PSObject.Properties['landmarks']) {
            $region | Add-Member -NotePropertyName landmarks -NotePropertyValue @()
        }

        $previous[$regionId] = @($region.cidrs)

        # The rebuild proper: today's matches plus anything hand-added, and nothing at all carried
        # over from the file. Adjacent halves are still merged, which is what keeps the route
        # count down once the blocks get narrower.
        $fresh = @(@($byRegion[$regionId]) + @($Manual[$regionId]) | Where-Object { $_ })
        $union = @()
        if ($fresh.Count -gt 0) {
            $union = @(Merge-AdjacentPrefixes -Cidrs $fresh -MinPrefixWidth $Width | Sort-Object)
        }

        # Drop anything that covers a landmark. Loud, and it does not stop the run: the rest of
        # the capture is still good, and a collision means the address behind that prefix needs
        # looking at, not that the tool failed.
        $swallowed = @()
        foreach ($cidr in $union) {
            foreach ($lm in $Landmarks) {
                if (Test-IpInCidr -Ip $lm.Value -Cidr $cidr) {
                    $swallowed += [pscustomobject]@{ Cidr = $cidr; Landmark = $lm.Address; Region = $lm.Region }
                }
            }
        }
        if ($swallowed.Count -gt 0) {
            foreach ($hit in $swallowed) {
                $warnings += ("$($hit.Cidr) covers $($hit.Landmark), the datacentre probe for " +
                              "'$($hit.Region)'. LEFT OUT. Routing it would make the game measure that " +
                              "region through the relay and every other region over the player's own " +
                              "connection, then compare the two.")
            }
            $excluded = @($swallowed.Cidr | Sort-Object -Unique)
            $union = @($union | Where-Object { $excluded -notcontains $_ })
        }

        $added[$regionId] = @($union | Where-Object { $previous[$regionId] -notcontains $_ })
        $newCount += $added[$regionId].Count
        $current[$regionId] = $union
        $region.cidrs = $union

        # A region with nothing observed keeps the note it was given by hand - asia-jp's says
        # "only fill this in after capturing in Japan", and replacing that with a generated line
        # about zero addresses would throw away an instruction for a list that is meant to be
        # empty.
        if ($union.Count -gt 0) {
            $region.source = (($sources[$regionId] | Sort-Object -Unique) -join ', ')
            $region.note = "Auto-generated by Build-PubgProfile.ps1 on $((Get-Date).ToString('yyyy-MM-dd')) " +
                           "from $($observed.Count) observed addresses, rebuilt from scratch on every run. " +
                           "Cloud providers publish these inside much wider blocks, so anything wider than " +
                           "/$Width is clamped to the /$Width block around the observed address" +
                           $(if ($Wider.Count -gt 0) { ", and the busiest blocks are widened back while the profile still fits under the address limit. " } else { ". " }) +
                           "Keep capturing until several sessions in a row add nothing new."
        }
    }

    $total = 0; $prefixes = 0
    foreach ($r in $g.regions) {
        foreach ($c in @($r.cidrs)) {
            $prefixes++
            $total += [math]::Pow(2, 32 - [int]$c.Split('/')[1])
        }
    }

    return @{
        Data = $data; Width = $Width; Total = [int]$total; Prefixes = $prefixes; Widened = $Wider.Count
        Previous = $previous; Current = $current; Added = $added
        Warnings = $warnings; Lines = $clamped.Lines; NewCount = $newCount
    }
}

if ($unverified.Count -gt 0) {
    Set-Content -Path $UnverifiedPath -Value ($unverified | Sort-Object -Unique) -Encoding ascii
}

# Are the declared landmarks still where the profile says they are?
#
# A landmark is a bet that one address stands for one datacentre, and the bet is only as good as
# the day it was made. Three things can happen to it, and they are NOT equally visible:
#
#   - it stops answering ICMP        -> the client measures nothing for that region and falls back
#                                       to comparing relays on the first leg. Logged, safe.
#   - it is reassigned inside the
#     same Azure region              -> still correct; a different machine in the same building.
#   - it is reassigned to ANOTHER
#     region                         -> the client cheerfully measures the wrong continent and
#                                       picks a relay optimised for it. SILENT, and the only one
#                                       of the three that is actually dangerous.
#
# That third case is checkable, and this is the one place with the data to check it: Microsoft
# publishes which region owns every address, and this script has already downloaded that file to
# do its real job. So it costs one pass over 11,000 prefixes to turn a silent wrong answer into a
# warning.
# Which Azure region owns each of these addresses, as a hashtable address -> {Region, Cidr, Bits}.
#
# One pass over the whole file rather than one per address, because it is 11,000 prefixes and
# there are two callers. Longest prefix wins: Azure publishes overlapping blocks and the narrowest
# is the one that names the real owner.
function Resolve-AzureRegions {
    param($Azure, [string[]]$Addresses)

    $values = @{}
    foreach ($a in $Addresses) { $values[$a] = (ConvertTo-UInt32Address $a) }

    $found = @{}
    foreach ($value in $Azure.values) {
        # "AzureCloud.southeastasia" only. "AzureCloud" itself is every region at once, and
        # "AzureCloud.southeastasia.Storage" is a service inside one - neither answers "who owns
        # this address".
        if ($value.name -notlike 'AzureCloud.*') { continue }
        if (($value.name.ToCharArray() | Where-Object { $_ -eq '.' }).Count -ne 1) { continue }
        $cloudRegion = $value.name.Substring('AzureCloud.'.Length)

        foreach ($cidr in $value.properties.addressPrefixes) {
            if ($cidr.Contains(':')) { continue }
            $bits = [int]$cidr.Split('/')[1]
            foreach ($address in $Addresses) {
                if (-not (Test-IpInCidr -Ip $values[$address] -Cidr $cidr)) { continue }
                $prev = $found[$address]
                if ($null -eq $prev -or $bits -gt $prev.Bits) {
                    $found[$address] = [pscustomobject]@{ Region = $cloudRegion; Cidr = $cidr; Bits = $bits }
                }
            }
        }
    }
    return $found
}

# Fold landmarks-observed.txt into the profile, for the regions this profile actually covers.
#
# Capture finds probe endpoints; this is what promotes one into the profile, and it is the step
# that used to be done by hand with the Service Tags file open in another window.
#
# It is deliberately more cautious than the equivalent for gameplay prefixes, because the two
# failure modes are not comparable. A wrong CIDR routes some traffic it should not have; a wrong
# landmark decides which datacentre EVERYTHING is measured against, and then picks a relay for it.
# So four rules, and anything failing one is reported rather than added:
#
#   1. Azure must still publish the address, in a region $regionMap knows. A VN capture sees
#      brazilsouth and centralus probes every match - they are real, and they belong to regions
#      this profile does not carry.
#   2. It must have been seen in at least -MinLandmarkSightings separate sessions. Counts in one
#      session ranged 90 packets down to 12, so a single sighting is thin evidence.
#   3. It must not fall inside a range this profile already routes. Adding it would make the
#      prefix check drop that range on the next line - possibly a range carrying real matches -
#      and the person running this would never see why.
#   4. At most -MaxLandmarksPerRegion per region. The client takes the best answer; a fourth
#      address is another ping at connect for nothing.
function Add-ObservedLandmarks {
    param($Game, $Azure, [string]$Path)

    Write-Host ""
    Write-Host "==> Landmarks from $(Split-Path $Path -Leaf)" -ForegroundColor Cyan

    if (-not (Test-Path $Path)) {
        Write-Host "    No such file yet - Capture-GameTraffic.ps1 writes it. Nothing to add."
        return
    }

    # address, packets, secs, sightings, ports. Comment lines fail the IPv4 test on their own.
    $candidates = @()
    foreach ($line in (Get-Content $Path)) {
        $fields = $line.Trim() -split '\s+'
        if ($fields.Count -lt 1 -or $fields[0] -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { continue }
        $sightings = 1
        if ($fields.Count -ge 4 -and $fields[3] -match '^\d+$') { $sightings = [int]$fields[3] }
        $candidates += [pscustomobject]@{ Address = $fields[0]; Sightings = $sightings }
    }

    if ($candidates.Count -eq 0) {
        Write-Host "    Nothing in it yet. Capture a few matches - the probes are on UDP 8081."
        return
    }

    # ForEach-Object, NOT $candidates.Address. Object[] has a real member called Address, so
    # member enumeration over an array of these silently resolves to that PSMethod instead of the
    # property - one element, of the wrong type, and the failure surfaces three frames away as
    # "An invalid IP address was specified". See the trap note in HANDOFF section 12.
    $found = Resolve-AzureRegions -Azure $Azure -Addresses @($candidates | ForEach-Object { $_.Address })

    # Every region already declared, so "already there" is answered without re-reading per row.
    $declared = @{}
    foreach ($r in $Game.regions) {
        if ($null -eq $r.PSObject.Properties['landmarks']) {
            $r | Add-Member -NotePropertyName landmarks -NotePropertyValue @()
        }
        foreach ($lm in @($r.landmarks)) { if ($lm) { $declared[$lm] = $r.id } }
    }

    $added = 0
    foreach ($candidate in ($candidates | Sort-Object -Property @{E='Sightings';D=$true}, Address)) {
        $address = $candidate.Address
        $label = "    {0,-16}" -f $address

        if ($declared.ContainsKey($address)) {
            Write-Host "$label already declared under '$($declared[$address])'"
            continue
        }

        $hit = $found[$address]
        if ($null -eq $hit) {
            Write-Host "$label skipped - in no Azure region; look it up by hand" -ForegroundColor DarkYellow
            continue
        }

        $regionId = $regionMap[$hit.Region]
        if (-not $regionId) {
            Write-Host "$label skipped - $($hit.Region) is not a region this profile covers"
            continue
        }

        $region = $Game.regions | Where-Object { $_.id -eq $regionId }
        if (-not $region) {
            Write-Host "$label skipped - $($hit.Region) maps to '$regionId', which this profile has no entry for"
            continue
        }

        if ($candidate.Sightings -lt $MinLandmarkSightings) {
            Write-Host ("$label held back - $($hit.Region), seen {0}x, needs {1}. Capture again." -f
                        $candidate.Sightings, $MinLandmarkSightings)
            continue
        }

        $clash = $null
        foreach ($cidr in @($region.cidrs)) {
            if (Test-IpInCidr -Ip (ConvertTo-UInt32Address $address) -Cidr $cidr) { $clash = $cidr; break }
        }
        if ($clash) {
            Write-Warning ("$address is inside $clash, which this profile routes. NOT added - doing " +
                           "so would drop that range from the profile on the next step, and it may be " +
                           "carrying matches. Decide by hand which of the two is wrong.")
            continue
        }

        if (@($region.landmarks).Count -ge $MaxLandmarksPerRegion) {
            Write-Host ("$label skipped - '$regionId' already has $MaxLandmarksPerRegion landmark(s)")
            continue
        }

        $region.landmarks = @(@($region.landmarks) + $address | Where-Object { $_ })
        $declared[$address] = $regionId
        $added++
        Write-Host "$label $($hit.Region) -> $regionId  ADDED (seen $($candidate.Sightings)x)" -ForegroundColor Green
    }

    if ($added -eq 0) {
        Write-Host "    Nothing new to add."
    } else {
        Write-Host "    $added landmark(s) added. They are checked below like any other."
    }
}

function Test-LandmarkRegions {
    param($Landmarks, $Azure)

    Write-Host ""
    Write-Host "==> Checking landmarks against the Azure region they claim" -ForegroundColor Cyan

    # ForEach-Object rather than $Landmarks.Address - see the note in Add-ObservedLandmarks.
    $found = Resolve-AzureRegions -Azure $Azure -Addresses @($Landmarks | ForEach-Object { $_.Address })

    $bad = 0
    foreach ($lm in $Landmarks) {
        $hit = $found[$lm.Address]
        if ($null -eq $hit) {
            $bad++
            Write-Warning ("$($lm.Address) (declared as '$($lm.Region)') is in no Azure region at " +
                           "all. Either it is not an Azure address any more, or the Service Tags " +
                           "file is stale. Re-capture a match and look at what the game probes on " +
                           "UDP 8081 now.")
            continue
        }

        $expected = $regionMap[$hit.Region]
        if ($expected -eq $lm.Region) {
            Write-Host "    $($lm.Address.PadRight(16)) $($hit.Region)  ok"
        } else {
            $bad++
            $whose = if ($expected) { "which this profile calls '$expected'" } else { "which this profile does not cover" }
            Write-Warning ("$($lm.Address) is declared under '$($lm.Region)' but Azure publishes it " +
                           "in $($hit.Region) ($($hit.Cidr)), $whose. The client would measure the " +
                           "wrong datacentre and choose a relay for it. Fix the profile before " +
                           "shipping.")
        }
    }

    if ($bad -eq 0) {
        Write-Host "    all $($Landmarks.Count) landmark(s) still sit in the region they claim"
    }
}

# -------------------------------------------------------------- write profile

Write-Host ""
Write-Host "==> Updating $ProfilePath" -ForegroundColor Cyan

$newProfile = -not (Test-Path $ProfilePath)
if ($newProfile) {
    # The first build of a game starts its profile rather than asking for one to be written by
    # hand. Everything in it but the process names is regenerated below anyway, and the relays
    # stay empty: the client takes relays from its primary profile, never from a game's own.
    if (@($ProcessNames).Count -eq 0) {
        throw "No profile at $ProfilePath, and no -ProcessNames to start one with. Run: ./gpb profile $GameId"
    }
    $displayName = if ($GameName) { $GameName } else { $GameId }
    Write-Host "    No profile yet - starting one for $displayName"
    $profileData = [pscustomobject]@{
        schemaVersion = 1
        generatedUtc  = ''
        games         = @([pscustomobject]@{
            id             = $GameId
            name           = $displayName
            processNames   = @($ProcessNames)
            lobbyAddresses = @()
            regions        = @()
        })
        relays        = @()
    }
} else {
    $profileData = Get-Content $ProfilePath -Raw | ConvertFrom-Json
}
$game = $profileData.games | Where-Object { $_.id -eq $GameId }
if (-not $game) { throw "The profile has no game with id '$GameId'." }

# Every landmark the game's regions declare, as raw addresses. A prefix that covers one of
# these must never enter the profile: the landmarks are how the GAME chooses its datacentre, and
# routing some of them while leaving the rest on the player's own connection makes it compare two
# different paths and pick a region that is worse both ways. That is not a hypothetical - it is
# how 20.43.176.0/20 got in, why a tester was sent to Korea, and the reason this check exists.
# See HANDOFF section 6a.
if ($LandmarkObservedPath) {
    Add-ObservedLandmarks -Game $game -Azure $azure -Path $LandmarkObservedPath
}

$landmarks = @()
foreach ($r in $game.regions) {
    foreach ($lm in @($r.landmarks)) {
        if ($lm) { $landmarks += [pscustomobject]@{ Address = $lm; Value = (ConvertTo-UInt32Address $lm); Region = $r.id } }
    }
}
if ($landmarks.Count -eq 0) {
    Write-Warning ("The profile declares no landmarks, so nothing stops a prefix from swallowing " +
                   "the game's own datacentre probes.")
} else {
    Test-LandmarkRegions -Landmarks $landmarks -Azure $azure
}

$manualCidrs = Read-ManualCidrs -Path $ManualCidrPath
$manualCount = 0
foreach ($k in $manualCidrs.Keys) { $manualCount += @($manualCidrs[$k]).Count }
if ($manualCount -gt 0) {
    Write-Host "    $manualCount hand-added prefix(es) from $(Split-Path $ManualCidrPath -Leaf)"
}

# A hand-added range gets no narrowing - it is exactly what somebody typed - so one that covers
# another game's address stops the build instead.
foreach ($regionId in $manualCidrs.Keys) {
    foreach ($cidr in @($manualCidrs[$regionId])) {
        foreach ($f in $foreign.Addresses) {
            if (Test-IpInCidr -Ip $f.Value -Cidr $cidr) {
                throw ("$(Split-Path $ManualCidrPath -Leaf): '$regionId $cidr' covers $($f.Address), which " +
                       "belongs to $($f.Owner). One game's ranges must never carry another game's " +
                       "servers - narrow the entry or remove it.")
            }
        }
    }
}

# Rebuild rather than accrete, and narrow until the whole profile fits.
#
# Every run regenerates the CIDR lists from observed.txt, so -MaxPrefixWidth applies to the entire
# profile instead of only to today's sightings. Accreting is what dead-ended this profile at
# exactly 131,072 addresses: blocks recorded weeks earlier kept their width forever, so narrowing
# could not shrink them and the only move left was to raise the limit that exists to stop exactly
# that. The cost of rebuilding is that hand-written prefixes do not survive - they go in
# manual-cidrs.txt, which is read back in above.
$attempt = $null
for ($width = $MaxPrefixWidth; $width -le $NarrowestPrefixWidth; $width++) {
    $attempt = New-ProfileAtWidth -Source $profileData -Width $width -Manual $manualCidrs -Landmarks $landmarks
    if ($attempt.Total -le $MaxTotalAddresses) { break }
    Write-Host ("    /{0}: {1} prefixes, {2} addresses - over the {3} limit, narrowing to /{4}" -f
                $width, $attempt.Prefixes, $attempt.Total, $MaxTotalAddresses, ($width + 1)) -ForegroundColor DarkYellow
    $attempt = $null
}

if (-not $attempt) {
    Write-Host ""
    Write-Warning "Even at /$NarrowestPrefixWidth the profile does not fit under $MaxTotalAddresses addresses."
    Write-Host "    Narrowing further is not worth doing - a /$NarrowestPrefixWidth already covers little beyond the"
    Write-Host "    servers actually seen, so this is a data problem rather than a packing one. Either age out"
    Write-Host "    addresses that have not appeared for several sessions (observed.txt keeps a sighting count"
    Write-Host "    per address), or raise -MaxTotalAddresses deliberately, knowing that number is what keeps a"
    Write-Host "    whole cloud region out of the routing table."
    exit 1
}

# Fill back up to the ceiling. The search above narrows every block together, which leaves room
# unused - often most of a width's worth. Widen blocks one step at a time, busiest first, while the
# whole profile still fits and no widened block swallows a landmark (the build leaves such a block
# out entirely, which would lose more than it gains). Each candidate is checked by building the
# profile with it, so merging, foreign-address narrowing and the landmark rule all apply exactly as
# they do to everything else.
if ($attempt.Width -gt $MaxPrefixWidth) {
    $wider = @{}
    $baseWarnings = @($attempt.Warnings).Count
    $widened = 0
    for ($level = $attempt.Width - 1; $level -ge $MaxPrefixWidth; $level--) {
        $blocks = @{}
        foreach ($m in $matched) {
            if ($m.Anycast -or $m.PublishedBits -gt $level) { continue }
            $key = Get-BlockKey -Value $m.Value -Bits $level
            if (-not $blocks.ContainsKey($key)) {
                $blocks[$key] = [pscustomobject]@{ Key = $key; Network = (ConvertTo-UInt32Address $key.Split('/')[0]); Sightings = 0; Packets = 0L }
            }
            $e = $evidence[$m.Address]
            if ($e) { $blocks[$key].Sightings += $e.Sightings; $blocks[$key].Packets += $e.Packets }
        }

        $ordered = $blocks.Values | Sort-Object -Property @{E = 'Sightings'; D = $true }, @{E = 'Packets'; D = $true }, @{E = 'Network'; D = $false }
        foreach ($block in $ordered) {
            if ($wider.ContainsKey($block.Key)) { continue }
            $room = $MaxTotalAddresses - $attempt.Total
            if ($room -le 0) { break }

            # What widening can add at most, without building: the block, less what the profile already
            # routes inside it. Anything that cannot fit is skipped without the cost of a build.
            $size = [math]::Pow(2, 32 - $level)
            $inside = 0
            foreach ($regionId in $attempt.Current.Keys) {
                foreach ($c in @($attempt.Current[$regionId])) {
                    $cBits = [int]$c.Split('/')[1]
                    if ($cBits -lt $level) { continue }
                    if ((Get-BlockKey -Value (ConvertTo-UInt32Address $c.Split('/')[0]) -Bits $level) -eq $block.Key) {
                        $inside += [math]::Pow(2, 32 - $cBits)
                    }
                }
            }
            if ($size - $inside -gt $room) { continue }

            $wider[$block.Key] = $true
            $trial = New-ProfileAtWidth -Source $profileData -Width $attempt.Width -Manual $manualCidrs -Landmarks $landmarks -Wider $wider
            if ($trial.Total -le $MaxTotalAddresses -and @($trial.Warnings).Count -le $baseWarnings) {
                $attempt = $trial
                $widened++
            } else {
                $wider.Remove($block.Key)
            }
        }
    }
    if ($widened -gt 0) {
        Write-Host ("    widened {0} block(s) back while it still fit: {1} addresses of the {2} limit" -f
                    $widened, $attempt.Total, $MaxTotalAddresses) -ForegroundColor DarkCyan
    }
}

$profileData = $attempt.Data
$game = $profileData.games | Where-Object { $_.id -eq $GameId }

Write-Host ""
Write-Host "==> Building at /$($attempt.Width)" -ForegroundColor Cyan
foreach ($line in $attempt.Lines) { Write-Host $line }
foreach ($w in $attempt.Warnings) { Write-Warning $w }

# A rebuild is only as complete as observed.txt, and that file has been reset before - on
# 2026-09-05, with the history archived beside it as observed.txt.bak-20260905. So before writing,
# check that every range the profile already had still has something behind it. A range with no
# overlap at all in the rebuilt set means the observations that produced it are missing, not that
# the servers went away, and writing that out would quietly shrink what the client routes.
$lostGround = @()
foreach ($regionId in $attempt.Previous.Keys) {
    foreach ($old in @($attempt.Previous[$regionId])) {
        $survives = $false
        foreach ($new in @($attempt.Current[$regionId])) {
            if (Test-CidrsOverlap -A $old -B $new) { $survives = $true; break }
        }
        if (-not $survives) { $lostGround += "$regionId : $old" }
    }
}
if ($lostGround.Count -gt 0) {
    Write-Host ""
    Write-Warning "$($lostGround.Count) range(s) in the current profile have nothing behind them in $(Split-Path $ObservedIpPath -Leaf):"
    foreach ($l in ($lostGround | Sort-Object)) { Write-Host "      $l" }
    if (-not $AllowCoverageLoss) {
        Write-Host "    Nothing was written. This usually means observed.txt is missing history rather than"
        Write-Host "    those servers being gone - look for an observed.txt.bak-* worth merging back in first."
        Write-Host "    Re-run with -AllowCoverageLoss once you are satisfied they really should go."
        exit 1
    }
    Write-Host "    -AllowCoverageLoss was given, so they are being dropped." -ForegroundColor Yellow
}

$totalNew = $attempt.NewCount
foreach ($regionId in ($attempt.Current.Keys | Sort-Object)) {
    $union = @($attempt.Current[$regionId])
    $added = @($attempt.Added[$regionId])
    $addedText = ''
    if ($added.Count -gt 0) { $addedText = " (+$($added.Count) new: $($added -join ', '))" }
    Write-Host "    $regionId : $($union.Count) prefixes$addedText"
}

$profileData.generatedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

# Written from games.json on every run, and REMOVED when the game declares none - the same rule the
# regions follow. A rate that stayed behind in the file after being taken out of games.json would be
# a threshold nobody could find the reason for, which is how a quiet game gets quietly un-discovered.
if ($DiscoveryPacketsPerSecond -gt 0) {
    $game | Add-Member -NotePropertyName discoveryPacketsPerSecond -NotePropertyValue $DiscoveryPacketsPerSecond -Force
    Write-Host "    Discovery: a server of this game is $DiscoveryPacketsPerSecond packets/s (declared in games.json)"
} elseif ($game.PSObject.Properties.Name -contains 'discoveryPacketsPerSecond') {
    $game.PSObject.Properties.Remove('discoveryPacketsPerSecond')
    Write-Host "    Discovery: no rate in games.json - removed the one in the profile, the client's default applies"
}

if ($unverified.Count -gt 0) {
    Write-Host ""
    Write-Host "    $($unverified.Count) addresses held back, written to $(Split-Path $UnverifiedPath -Leaf):" -ForegroundColor DarkYellow
    $unverified | Sort-Object -Unique | ForEach-Object { Write-Host "      $_" }
    Write-Host "    These are usually voice chat or a CDN. Look them up before adding any by hand."
}

if ($DryRun) {
    Write-Host ""
    Write-Host "==> -DryRun: the profile was NOT written." -ForegroundColor Yellow
    return
}

if ($totalNew -eq 0) {
    Write-Host ""
    Write-Host "==> Nothing new - the profile already covers every observed address." -ForegroundColor Green
    Write-Host "    That is the signal you are looking for: a few sessions in a row like this means the list has saturated."
}

if (-not $newProfile) { Copy-Item $ProfilePath "$ProfilePath.bak" -Force }
$json = Format-Json ($profileData | ConvertTo-Json -Depth 10)
Set-Content -Path $ProfilePath -Value $json -Encoding ascii
if ($newProfile) { Write-Host "    Written - a new profile" }
else { Write-Host "    Written (previous version kept as $(Split-Path $ProfilePath -Leaf).bak)" }

# ----------------------------------------------------------------- validate

Write-Host ""
Write-Host "==> Validating" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'Test-Profile.ps1') -Path $ProfilePath -MaxPrefixWidth $MaxPrefixWidth `
    -MaxTotalAddresses $MaxTotalAddresses -OtherProfilePaths $foreign.Profiles
if ($LASTEXITCODE -ne 0 -and $newProfile) {
    # Nothing to restore, and a rejected first profile left in place would be loaded by the
    # service - which does not run the validator - and read as the baseline by the next build.
    Remove-Item $ProfilePath -Force
    Write-Host ""
    Write-Warning "Validation failed - the new profile was removed, nothing was changed."
    exit 1
}
if ($LASTEXITCODE -ne 0) {
    # Put the good profile back rather than telling the operator to do it.
    #
    # Leaving the rejected file in place was actively harmful in three ways: the service loads
    # this path and does not run the validator, so an over-limit profile would be used for real;
    # the next build read the rejected file as its baseline, which made "+N new" and "nothing new"
    # report nonsense; and a second failed run overwrote the .bak with the first failure, so the
    # last good profile was destroyed by the very mechanism meant to preserve it.
    Copy-Item "$ProfilePath.bak" $ProfilePath -Force
    Write-Host ""
    Write-Warning "Validation failed - the previous profile has been restored, nothing was changed."
    Write-Host "    The address total is the builder's job now, so a failure here is one of the other"
    Write-Host "    checks: a range in manual-cidrs.txt wider than /$MaxPrefixWidth, one that covers a landmark,"
    Write-Host "    contains the relay IP, or overlaps private space. The error above says which."
    exit 1
}
