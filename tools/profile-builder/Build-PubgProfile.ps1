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

    Both this and MaxTotalAddresses were set from measurement rather than caution. Azure publishes
    southeastasia in /16s and /17s, so the cap decides how much one sighting is worth: at /21 a
    match landed on an uncovered server about twice in ten, at /20 the same set of observations
    covers roughly twice the ground. Ten matches at /20 missed twice, which is the number to beat
    by capturing more, not by widening further.

    Going wider than /20 is where the cost starts to be real - /19 pulls in 143,000 addresses and
    /18 over 225,000, which is most of Azure southeastasia. Narrow instead (21, 22) if the profile
    ever needs to fit a smaller ceiling; that costs coverage per sighting, never correctness.

.PARAMETER MaxTotalAddresses
    Ceiling for the whole profile. Default 131,072.

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
    [int]$MaxTotalAddresses = 131072,
    [switch]$DryRun,
    [string[]]$AwsRegions = @('ap-southeast-1', 'ap-northeast-1', 'ap-northeast-2'),
    [string[]]$AzureRegions = @('southeastasia', 'japaneast', 'koreacentral')
)

$ErrorActionPreference = 'Stop'

if (-not $ObservedIpPath) { $ObservedIpPath = Join-Path $PSScriptRoot 'observed.txt' }
if (-not $LandmarkObservedPath) { $LandmarkObservedPath = Join-Path $PSScriptRoot 'landmarks-observed.txt' }
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
    # When the published prefix is wider than we allow, keep the /MaxPrefixWidth BLOCK that
    # contains this address. The obvious alternative - collapse to the observed /24 - is what this
    # used to do, and it threw away 99.6% of a /16: Azure publishes southeastasia as /16s and
    # /17s, so every single /24 needed its own separate sighting and the profile could never
    # saturate no matter how long anyone played. Clamping keeps the cap meaningful and bounded
    # while letting one observation speak for the block it landed in.
    # Decimal with an L suffix, not 0xFFFFFFFF: PowerShell 5.1 parses that hex literal as int32
    # BEFORE any cast, so it arrives as -1 and every mask built from it comes out negative.
    $hostBits = 32 - $MaxPrefixWidth
    $capMask = [uint32](4294967295L -band (-bnot ((1L -shl $hostBits) - 1L)))
    $fallback = (ConvertFrom-UInt32Address ([uint32]($value -band $capMask))) + "/$MaxPrefixWidth"
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
# warning. See the design notes.
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
                           "shipping - see the design notes.")
        }
    }

    if ($bad -eq 0) {
        Write-Host "    all $($Landmarks.Count) landmark(s) still sit in the region they claim"
    }
}

# -------------------------------------------------------------- write profile

Write-Host ""
Write-Host "==> Updating $ProfilePath" -ForegroundColor Cyan

$profileData = Get-Content $ProfilePath -Raw | ConvertFrom-Json
$game = $profileData.games | Where-Object { $_.id -eq $GameId }
if (-not $game) { throw "The profile has no game with id '$GameId'." }

# Every landmark the game's regions declare, as raw addresses. A prefix that covers one of
# these must never enter the profile: the landmarks are how the GAME chooses its datacentre, and
# routing some of them while leaving the rest on the player's own connection makes it compare two
# different paths and pick a region that is worse both ways. That is not a hypothetical - it is
# how 20.43.176.0/20 got in, why a tester was sent to Korea, and the reason this check exists.
# See HANDOFF section 6a and the design notes.
Add-ObservedLandmarks -Game $game -Azure $azure -Path $LandmarkObservedPath

$landmarks = @()
foreach ($r in $game.regions) {
    foreach ($lm in @($r.landmarks)) {
        if ($lm) { $landmarks += [pscustomobject]@{ Address = $lm; Value = (ConvertTo-UInt32Address $lm); Region = $r.id } }
    }
}
if ($landmarks.Count -eq 0) {
    Write-Warning ("The profile declares no landmarks, so nothing stops a prefix from swallowing " +
                   "the game's own datacentre probes. See the design notes.")
} else {
    Test-LandmarkRegions -Landmarks $landmarks -Azure $azure
}

$totalNew = 0
foreach ($regionId in ($byRegion.Keys | Sort-Object)) {
    $region = $game.regions | Where-Object { $_.id -eq $regionId }
    if (-not $region) {
        $region = [pscustomobject]@{
            id = $regionId; name = $regionNames[$regionId]; source = ''; note = ''; cidrs = @(); landmarks = @()
        }
        $game.regions += $region
    }
    if ($null -eq $region.PSObject.Properties['landmarks']) {
        $region | Add-Member -NotePropertyName landmarks -NotePropertyValue @()
    }

    $before = @($region.cidrs)
    # Union with what the profile already had, then re-aggregate over the whole set: a prefix
    # added today may be the missing half of one recorded weeks ago.
    $union = Merge-AdjacentPrefixes -Cidrs (@($before) + @($byRegion[$regionId])) -MinPrefixWidth $MaxPrefixWidth
    $union = @($union | Sort-Object)

    # Drop anything that covers a landmark, before it can be counted as "new" and reported as
    # progress. Loud, and it does not stop the run: the rest of the capture is still good, and a
    # collision means the address behind that prefix needs looking at, not that the tool failed.
    $swallowed = @()
    foreach ($cidr in $union) {
        foreach ($lm in $landmarks) {
            if (Test-IpInCidr -Ip $lm.Value -Cidr $cidr) {
                $swallowed += [pscustomobject]@{ Cidr = $cidr; Landmark = $lm.Address; Region = $lm.Region }
            }
        }
    }
    if ($swallowed.Count -gt 0) {
        foreach ($hit in $swallowed) {
            Write-Warning ("$($hit.Cidr) covers $($hit.Landmark), the datacentre probe for " +
                           "'$($hit.Region)'. LEFT OUT. Routing it would make the game measure that " +
                           "region through the relay and every other region over the player's own " +
                           "connection - see the design notes.")
        }
        $excluded = @($swallowed.Cidr | Sort-Object -Unique)
        $union = @($union | Where-Object { $excluded -notcontains $_ })
    }

    $added = @($union | Where-Object { $before -notcontains $_ })
    $totalNew += $added.Count

    $region.cidrs = $union
    $region.source = (($sourcesByRegion[$regionId] | Sort-Object -Unique) -join ', ')
    $region.note = "Auto-generated by Build-PubgProfile.ps1 on $((Get-Date).ToString('yyyy-MM-dd')) " +
                   "from $($observed.Count) observed addresses. Cloud providers publish these inside much " +
                   "wider blocks, so anything wider than /$MaxPrefixWidth is clamped to the /$MaxPrefixWidth block around the observed address. " +
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
& (Join-Path $PSScriptRoot 'Test-Profile.ps1') -Path $ProfilePath -MaxPrefixWidth $MaxPrefixWidth -MaxTotalAddresses $MaxTotalAddresses
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
    Write-Host "    Narrow the blocks and try again, e.g. -MaxPrefixWidth 21, or raise -MaxTotalAddresses deliberately."
    exit 1
}
