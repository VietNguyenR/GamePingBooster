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
    Widest prefix that may be accepted. Default 20 (/20 = 4096 addresses).

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
    [string]$ProfilePath,
    [string]$GameId = 'pubg',
    [int]$MaxPrefixWidth = 20,
    [switch]$DryRun,
    [string[]]$AwsRegions = @('ap-southeast-1', 'ap-northeast-1', 'ap-northeast-2'),
    [string[]]$AzureRegions = @('southeastasia', 'japaneast', 'koreacentral')
)

$ErrorActionPreference = 'Stop'

if (-not $ObservedIpPath) { $ObservedIpPath = Join-Path $PSScriptRoot 'observed.txt' }
if (-not $ProfilePath) { $ProfilePath = Join-Path $PSScriptRoot '..\..\profiles\pubg-vn.json' }
$cacheDir = Join-Path $PSScriptRoot '.cache'
$unverifiedPath = Join-Path $PSScriptRoot 'observed-unverified.txt'

# Which profile region each cloud region belongs to. Adding a new region to the game means
# adding it here and in the -AwsRegions / -AzureRegions defaults above.
$regionMap = @{
    'ap-southeast-1' = 'asia-sg'; 'southeastasia' = 'asia-sg'
    'ap-northeast-1' = 'asia-jp'; 'japaneast'     = 'asia-jp'
    'ap-northeast-2' = 'asia-kr'; 'koreacentral'  = 'asia-kr'
}
$regionNames = @{
    'asia-sg' = 'Southeast Asia (Singapore)'
    'asia-jp' = 'Japan (Tokyo)'
    'asia-kr' = 'Korea (Seoul)'
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
        $out = $sb.ToString() -replace '\[\s*?
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
    return $current
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

# ------------------------------------------------------------ observed input

if (-not (Test-Path $ObservedIpPath)) {
    throw "No $ObservedIpPath yet. Run .\Capture-GameTraffic.ps1 while playing a few matches first."
}
New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null

Write-Host "==> Reading $ObservedIpPath"
$observed = Get-Content $ObservedIpPath |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -match '^\d{1,3}(\.\d{1,3}){3}$' } |
    Sort-Object -Unique

if ($observed.Count -eq 0) { throw "No valid IPv4 addresses in $ObservedIpPath" }

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

# ------------------------------------------------------------------ matching

Write-Host ""
Write-Host "==> Matching" -ForegroundColor Cyan

$byRegion = @{}      # profile region id -> list of CIDRs
$sourcesByRegion = @{}
$unverified = @()

foreach ($ip in $observed) {
    $value = ConvertTo-UInt32Address $ip
    $best = $null; $bestBits = -1; $source = $null; $cloudRegion = $null

    foreach ($p in $awsPrefixes) {
        if (Test-IpInCidr $value $p.ip_prefix) {
            $bits = [int]$p.ip_prefix.Split('/')[1]
            if ($bits -gt $bestBits) {
                $best = $p.ip_prefix; $bestBits = $bits
                $source = "aws:$($p.region)"; $cloudRegion = $p.region
            }
        }
    }
    foreach ($p in $azurePrefixes) {
        if (Test-IpInCidr $value $p.Prefix) {
            $bits = [int]$p.Prefix.Split('/')[1]
            if ($bits -gt $bestBits) {
                $best = $p.Prefix; $bestBits = $bits
                $source = "azure:$($p.Region)"; $cloudRegion = $p.Region
            }
        }
    }

    if (-not $best) {
        Write-Host ("    {0,-18} not in any Asian cloud range - held back" -f $ip) -ForegroundColor DarkYellow
        $unverified += $ip
        continue
    }

    $regionId = $regionMap[$cloudRegion]
    $fallback = (ConvertFrom-UInt32Address ($value -band 0xFFFFFF00)) + '/24'
    if ($bestBits -lt $MaxPrefixWidth) {
        $chosen = $fallback
        Write-Host ("    {0,-18} {1,-22} published as {2}, using {3}" -f $ip, $source, $best, $chosen)
    } else {
        $chosen = $best
        Write-Host ("    {0,-18} {1,-22} {2}" -f $ip, $source, $chosen)
    }

    if (-not $byRegion.ContainsKey($regionId)) { $byRegion[$regionId] = @(); $sourcesByRegion[$regionId] = @() }
    $byRegion[$regionId] += $chosen
    $sourcesByRegion[$regionId] += $source
}

if ($unverified.Count -gt 0) {
    Set-Content -Path $unverifiedPath -Value ($unverified | Sort-Object -Unique) -Encoding ascii
}

# -------------------------------------------------------------- write profile

Write-Host ""
Write-Host "==> Updating $ProfilePath" -ForegroundColor Cyan

$profileData = Get-Content $ProfilePath -Raw | ConvertFrom-Json
$game = $profileData.games | Where-Object { $_.id -eq $GameId }
if (-not $game) { throw "The profile has no game with id '$GameId'." }

$totalNew = 0
foreach ($regionId in ($byRegion.Keys | Sort-Object)) {
    $region = $game.regions | Where-Object { $_.id -eq $regionId }
    if (-not $region) {
        $region = [pscustomobject]@{
            id = $regionId; name = $regionNames[$regionId]; source = ''; note = ''; cidrs = @()
        }
        $game.regions += $region
    }

    $before = @($region.cidrs)
    # Union with what the profile already had, then re-aggregate over the whole set: a prefix
    # added today may be the missing half of one recorded weeks ago.
    $union = Merge-AdjacentPrefixes -Cidrs (@($before) + @($byRegion[$regionId])) -MinPrefixWidth $MaxPrefixWidth
    $union = @($union | Sort-Object)

    $added = @($union | Where-Object { $before -notcontains $_ })
    $totalNew += $added.Count

    $region.cidrs = $union
    $region.source = (($sourcesByRegion[$regionId] | Sort-Object -Unique) -join ', ')
    $region.note = "Auto-generated by Build-PubgProfile.ps1 on $((Get-Date).ToString('yyyy-MM-dd')) " +
                   "from $($observed.Count) observed addresses. Cloud providers publish these inside much " +
                   "wider blocks, so anything wider than /$MaxPrefixWidth is narrowed to the observed /24. " +
                   "Keep capturing until several sessions in a row add nothing new."

    $addedText = ''
    if ($added.Count -gt 0) { $addedText = " (+$($added.Count) new: $($added -join ', '))" }
    Write-Host "    $regionId : $($union.Count) prefixes$addedText"
}

$profileData.generatedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

if ($unverified.Count -gt 0) {
    Write-Host ""
    Write-Host "    $($unverified.Count) addresses held back, written to observed-unverified.txt:" -ForegroundColor DarkYellow
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

Copy-Item $ProfilePath "$ProfilePath.bak" -Force
$json = Format-Json ($profileData | ConvertTo-Json -Depth 10)
Set-Content -Path $ProfilePath -Value $json -Encoding ascii
Write-Host "    Written (previous version kept as $(Split-Path $ProfilePath -Leaf).bak)"

# ----------------------------------------------------------------- validate

Write-Host ""
Write-Host "==> Validating" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'Test-Profile.ps1') -Path $ProfilePath -MaxPrefixWidth $MaxPrefixWidth
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Warning "Validation failed. The previous profile is at $ProfilePath.bak - restore it if needed."
    exit 1
}
