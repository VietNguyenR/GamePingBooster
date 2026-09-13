<#
.SYNOPSIS
    Tests the parts of Probe-Providers.ps1 that decide things, with made-up numbers.

.DESCRIPTION
    The probe's verdict cannot be checked by running it: one line reaches one branch. So the
    deciding functions are lifted out of the real file BY NAME - not copied, so this cannot pass
    against a stale duplicate - and driven with synthetic rows.

    One of the cases is the report the tool was written for: a VNTT line at 68 ms direct, every
    Singapore provider around 70, and the path passing through Hurricane Electric in Hong Kong.

    No network, no service, no Administrator.

.EXAMPLE
    .\gpb.ps1 test
    Runs as part of the whole suite.
#>
$ErrorActionPreference = 'Stop'

$target = Join-Path $PSScriptRoot 'Probe-Providers.ps1'
if (-not (Test-Path -LiteralPath $target)) { throw "not found: $target" }
$src = Get-Content -LiteralPath $target -Raw

function Import-Fn([string]$name) {
    $m = [regex]::Match($src, "(?ms)^function\s+$name\b.*?^\}", 'Multiline')
    if (-not $m.Success) { throw "could not lift $name" }
    return $m.Value
}
foreach ($fn in 'Get-Percentile', 'New-ProbeStats', 'Get-HopCity', 'Test-PublicHostName', 'Get-Margin', 'Get-ProbeVerdict') {
    Invoke-Expression (Import-Fn $fn)
}

# The two tables the functions read, lifted the same way so a code added there is tested here.
$cities = [regex]::Match($src, '(?ms)^\$script:CityCodes = @\{.*?^\}')
if (-not $cities.Success) { throw 'could not lift CityCodes' }
Invoke-Expression $cities.Value
$leg = [regex]::Match($src, '(?m)^\$script:HongKongToSingaporeMs = .*$')
if (-not $leg.Success) { throw 'could not lift HongKongToSingaporeMs' }
Invoke-Expression $leg.Value

$fail = 0
function Check($label, $got, $want) {
    $ok = ($got -eq $want)
    if (-not $ok) { $script:fail++ }
    $tag = if ($ok) { 'PASS' } else { "FAIL (got '$got', want '$want')" }
    "{0,-62} {1}" -f $label, $tag
}

function Row($label, $region, $p50, $cities, [switch]$Tunnel) {
    $samples = @()
    if ($null -ne $p50) { $samples = 1..20 | ForEach-Object { [double]$p50 } }
    [pscustomobject]@{
        Kind      = 'provider'
        Label     = $label
        Region    = $region
        ViaTunnel = [bool]$Tunnel
        Stats     = New-ProbeStats $samples 20
        Cities    = @($cities)
    }
}

function Relay($label, $p50) {
    $r = Row $label '' $p50 @()
    $r.Kind = 'relay'
    return $r
}

function Ref($p50, $cities, [switch]$Tunnel) {
    $r = Row 'Game region, direct' 'Singapore' $p50 $cities -Tunnel:$Tunnel
    $r.Kind = 'reference'
    return $r
}

# ---------------------------------------------------------------- cities
Check 'HE Hong Kong, the hop from the report'   (Get-HopCity 'e0-8.switch5.hkg1.he.net')          'Hong Kong'
Check 'NTT Singapore naming'                     (Get-HopCity 'ae-1.r20.sngpsi07.sg.bb.gin.ntt.net') 'Singapore'
Check 'a code inside a word is not a code'       (Get-HopCity 'business.example.com')              $null
Check 'two-letter pieces are ignored'            (Get-HopCity 'ge-0.hk.example.net')               $null
Check 'no name, no city'                         (Get-HopCity $null)                               $null
Check 'Microsoft edge in Hong Kong'              (Get-HopCity 'ae33-0.icr01.hkg20.ntwk.msn.net')   'Hong Kong'

# ---------------------------------------------------------------- names that may be kept
# A made-up computer name, deliberately: the point is that the real one never lands anywhere.
Check 'a real router name is kept'               (Test-PublicHostName 'ae33-0.icr01.hkg20.ntwk.msn.net' 'GAMINGPC') $true
Check 'localhost is never a router'              (Test-PublicHostName 'localhost' 'GAMINGPC')      $false
# The bare word is already caught for having no dot. This is the case the localhost rule is for.
Check 'nor is localhost with a domain on it'     (Test-PublicHostName 'localhost.localdomain' 'GAMINGPC') $false
Check 'a single label is not a public name'      (Test-PublicHostName 'SOMEBOX' 'GAMINGPC')        $false
Check 'this computer''s own name is dropped'     (Test-PublicHostName 'gamingpc.lan' 'GAMINGPC')   $false
Check 'nothing is not a name'                    (Test-PublicHostName $null 'GAMINGPC')            $false

# ---------------------------------------------------------------- stats
$s = New-ProbeStats @(70, 71, 70, 90, 70) 6
Check 'p50'                                      $s.P50                                            70
Check 'p95 catches the slow one'                 $s.P95                                            90
Check 'loss counts what never came back'         ([math]::Round($s.LossPct, 1))                    16.7
Check 'nothing received leaves p50 empty'        (New-ProbeStats @() 20).P50                       $null

# ---------------------------------------------------------------- margin
Check 'margin has a floor'                       (Get-Margin 20)                                   5
Check 'margin scales with distance'              (Get-Margin 80)                                   8

# ---------------------------------------------------------------- the report of 2026-09-13
$hk = @('Hong Kong', 'Singapore')
$rows = @(
    (Ref 68 $hk),
    (Row 'DigitalOcean' 'Singapore' 70 $hk),
    (Row 'Vultr' 'Singapore' 72 $hk),
    (Row 'OVH' 'Singapore' 81 $hk),
    (Row 'AWS' 'Hong Kong' 42 @('Hong Kong'))
)
$v = Get-ProbeVerdict $rows[0] $rows
Check 'the VNTT line: nothing beats its own route'   $v.Key                                        'isp-route-wins'
Check 'the VNTT line: the detour is named'           ([bool]($v.Lines -match 'through Hong Kong')) $true
Check 'the VNTT line: best is still reported'        $v.Best.Label                                 'DigitalOcean'

# ---------------------------------------------------------------- the owner's line, same day
# Every Singapore provider inside three milliseconds, our relays among them, and the direct route to
# Azure handed to Microsoft in Hong Kong - which a lookup through GetHostEntry had hidden.
$rows = @(
    (Ref 66 @('Hong Kong')),
    (Row 'Linode/Akamai' 'Singapore' 42.5 @('Singapore')),
    (Row 'Alibaba Cloud' 'Singapore' 42.8 @()),
    (Row 'OVH' 'Singapore' 45.4 @('Singapore')),
    (Relay 'our relay sg2' 43.0)
)
$v = Get-ProbeVerdict $rows[0] $rows
Check 'the owner''s line: a relay wins'          $v.Key                                            'provider-wins'
Check 'near-equal providers are named as equal'  ([bool]($v.Lines -match 'no different in practice: Alibaba Cloud')) $true
Check 'three ms behind is outside the tie'       ([bool]($v.Lines -match 'practice:.*OVH'))        $false
Check 'a tie is not advertised as one winner'    ([bool]($v.Lines -match 'A relay at any of these')) $true
Check 'our relay already there is said'          ([bool]($v.Lines -match 'our relay sg2 already reaches')) $true
Check 'the direct route''s detour is named'      ([bool]($v.Lines -match 'direct route .*Hong Kong')) $true

$rows = @((Ref 68 @()), (Row 'Vultr' 'Singapore' 35 @()), (Relay 'our relay sg' 60))
Check 'a relay of ours well behind is said too' `
    ([bool]((Get-ProbeVerdict $rows[0] $rows).Lines -match 'best of our relays, our relay sg, is 60 ms - 25 ms behind')) $true

$rows = @((Ref 68 @()), (Row 'Vultr' 'Singapore' 35 @()))
Check 'no detour known, none claimed'            ([bool]((Get-ProbeVerdict $rows[0] $rows).Lines -match 'passes through')) $false

# ---------------------------------------------------------------- the other branches
$rows = @((Ref 68 @()), (Row 'Vultr' 'Singapore' 35 @()), (Row 'OVH' 'Singapore' 60 @()))
Check 'a provider clearly nearer wins'           (Get-ProbeVerdict $rows[0] $rows).Key             'provider-wins'

$rows = @((Ref 68 @()), (Row 'Vultr' 'Singapore' 64 @()))
Check '4 ms better is inside the margin'         (Get-ProbeVerdict $rows[0] $rows).Key             'isp-route-wins'

$rows = @((Ref 90 @()), (Row 'Vultr' 'Singapore' 88 @()), (Row 'AWS' 'Hong Kong' 30 @()))
Check 'Hong Kong near enough to pay the second leg' (Get-ProbeVerdict $rows[0] $rows).Key          'hk-wins'

$rows = @((Ref 68 @()), (Row 'Vultr' 'Singapore' 66 @()), (Row 'AWS' 'Hong Kong' 42 @()))
Check 'Hong Kong at 42 + 31 does not'            (Get-ProbeVerdict $rows[0] $rows).Key             'isp-route-wins'

$rows = @((Ref 68 @() -Tunnel), (Row 'Vultr' 'Singapore' 35 @()))
Check 'a direct route through the booster is not a reference' (Get-ProbeVerdict $rows[0] $rows).Key 'no-reference'

$rows = @((Ref 68 @()), (Row 'Vultr' 'Singapore' 35 @() -Tunnel), (Row 'OVH' 'Singapore' 70 @()))
Check 'a provider through the booster cannot win'  (Get-ProbeVerdict $rows[0] $rows).Best.Label    'OVH'

$rows = @((Ref 68 @()), (Row 'Vultr' 'Singapore' $null @()))
Check 'nobody answered'                          (Get-ProbeVerdict $rows[0] $rows).Key             'no-provider'

# One path through Tokyo out of four is a provider's own routing, not the line's exit.
$rows = @(
    (Ref 68 @('Singapore')),
    (Row 'A' 'Singapore' 70 @('Singapore')),
    (Row 'B' 'Singapore' 71 @('Singapore')),
    (Row 'C' 'Singapore' 72 @('Tokyo', 'Singapore'))
)
Check 'a city on one path in four is not the line''s detour' `
    ([bool]((Get-ProbeVerdict $rows[0] $rows).Lines -match 'pass through')) $false

""
if ($fail -eq 0) {
    Write-Host "Probe-Providers: all checks passed" -ForegroundColor Green
    exit 0
}
Write-Host "Probe-Providers: $fail check(s) FAILED" -ForegroundColor Red
exit 1
