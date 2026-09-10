<#
.SYNOPSIS
    Tests the parts of Diagnose-Lag.ps1 that decide things, using synthetic samples.

.DESCRIPTION
    `.\gpb.ps1 lag` is only worth running if its verdict is right, and its verdict cannot be
    checked by running it: a healthy connection exercises exactly one of its branches. So the
    functions that decide are lifted out of the real file BY NAME - not copied here, so this
    cannot pass against a stale duplicate - and driven with made-up numbers.

    Among them are the numbers from the two faults that actually shipped:

      - a trace whose every hop is already abroad, where the old code picked a 43 ms hop as the
        in-country reference and printed it next to a 43 ms relay in Singapore;
      - a real Wi-Fi hiccup, where three rungs were flagged and the verdict still said nothing
        was misbehaving, because the outer rungs' distance-scaled tolerance swallowed it.

    No network, no service, no Administrator, well under a second.

.EXAMPLE
    .\gpb.ps1 test
    Runs as part of the whole suite.
#>
$ErrorActionPreference = 'Stop'

$target = Join-Path $PSScriptRoot 'Diagnose-Lag.ps1'
if (-not (Test-Path -LiteralPath $target)) { throw "not found: $target" }
$src = Get-Content -LiteralPath $target -Raw

function Import-Fn([string]$name) {
    $m = [regex]::Match($src, "(?ms)^function\s+$name\b.*?^\}", 'Multiline')
    if (-not $m.Success) { throw "could not lift $name" }
    return $m.Value
}
foreach ($fn in 'Invoke-Safely', 'Get-Percentile', 'Get-Jitter', 'New-Stats', 'Get-BadReason',
    'Get-AddrClass', 'Get-InternationalStep', 'Test-Inherits') {
    Invoke-Expression (Import-Fn $fn)
}

$fail = 0
function Check($label, $got, $want) {
    $ok = ($got -eq $want)
    if (-not $ok) { $script:fail++ }
    $tag = if ($ok) { 'PASS' } else { "FAIL (got '$got', want '$want')" }
    "{0,-58} {1}" -f $label, $tag
}

# ---------------------------------------------------------------- address classes
Check 'gateway is private'        (Get-AddrClass '192.168.1.1')    'private'
Check 'viettel 10/8 is private'   (Get-AddrClass '10.0.245.22')    'private'
Check 'cgnat is cgnat'            (Get-AddrClass '100.114.128.2')  'cgnat'
Check '100.128 is NOT cgnat'      (Get-AddrClass '100.128.0.1')    'public'
Check 'relay is public'           (Get-AddrClass '139.99.73.90')   'public'

# ---------------------------------------------------------------- stats
$s = New-Stats @(10, 10, 10, 40, 10) 5
Check 'p50 of a spiky series'     $s.P50    10
Check 'p95 catches the spike'     $s.P95    40
Check 'jitter is mean |delta|'    ([math]::Round($s.Jitter, 1))  15.0
$s2 = New-Stats @(10, 10, 10, 10) 5
Check 'one lost of five = 20%'    ([math]::Round($s2.LossPct))    20

# ---------------------------------------------------------------- bad reasons
$steady = New-Stats (1..20 | ForEach-Object { 44.0 }) 20
Check 'a steady 44 ms relay is ok'  (Get-BadReason $steady $null)  $null

$steadyLan = New-Stats (1..20 | ForEach-Object { 1.0 }) 20
Check 'a steady 1 ms router is ok'  (Get-BadReason $steadyLan $null) $null

# 8 ms of jitter: fine at 44 ms, a catastrophe at 1 ms. This is the scaling rule.
$jitterFar = New-Stats @(44, 48, 44, 48, 44, 48, 44, 48, 44, 48) 10
Check 'small jitter far away is tolerated' (Get-BadReason $jitterFar $null) $null
$jitterNear = New-Stats @(1, 9, 1, 9, 1, 9, 1, 9, 1, 9) 10
Check 'same jitter at the router is flagged' ($null -ne (Get-BadReason $jitterNear $null)) $true

# Loss does not scale with distance.
$lossy = New-Stats (1..17 | ForEach-Object { 44.0 }) 20
Check 'loss is flagged at any distance' ($null -ne (Get-BadReason $lossy $null)) $true

# The baseline tightens what counts as bad.
$slow = New-Stats (1..20 | ForEach-Object { 70.0 }) 20
Check 'no baseline: a flat 70 ms passes'  (Get-BadReason $slow $null) $null
Check 'baseline 44: the same 70 ms fails' ($null -ne (Get-BadReason $slow 44.0)) $true
Check 'baseline 44: 48 ms still passes'   (Get-BadReason (New-Stats (1..20 | ForEach-Object { 48.0 }) 20) 44.0) $null

# ---------------------------------------------------------------- the boundary
function Hop($addr, $rtt) {
    [pscustomobject]@{ Address = $addr; Rtt = $rtt; Class = (Get-AddrClass $addr) }
}
# The real Viettel trace measured on this machine.
$viettel = @(
    (Hop '192.168.1.1' 2.2), (Hop '100.114.128.2' 3.9), (Hop '172.16.10.64' 5.8),
    (Hop '10.0.245.21' 10.5), (Hop '10.0.245.22' 9.9), (Hop '27.68.228.161' 44.7),
    (Hop '27.68.255.66' 44.8), (Hop '117.1.220.87' 43.8), (Hop '129.250.2.134' 43.4)
)
$step = Get-InternationalStep $viettel 44.0
Check 'real trace: boundary found'         ($null -ne $step) $true
Check 'real trace: domestic is the 9.9 ms hop' $step.Domestic.Address '10.0.245.22'

# The regression that shipped: every hop already abroad, with a real-looking step
# from Singapore to somewhere further. The relay is at 44 ms, so none of these is domestic.
$abroadOnly = @((Hop '27.68.228.161' 44.7), (Hop '117.1.220.87' 43.8), (Hop '129.250.6.65' 77.3))
$bad = Get-InternationalStep $abroadOnly 44.0
Check 'a 44 ms hop is refused as "domestic"' ($null -eq $bad) $true

# No cable on the path at all - a domestic game server, say.
$flat = @((Hop '192.168.1.1' 1.0), (Hop '100.114.128.2' 3.0), (Hop '27.68.228.161' 6.0))
Check 'no step means no domestic rung' ($null -eq (Get-InternationalStep $flat 6.0)) $true

# ---------------------------------------------------------------- the ladder rule
# Lifted verbatim from the verdict block: the culprit is the innermost bad rung whose symptom
# still shows up at every rung beyond it.
function Find-Culprit($ladder) {
    for ($i = 0; $i -lt $ladder.Count; $i++) {
        if (-not $ladder[$i].Bad) { continue }
        $survives = $true
        for ($j = $i + 1; $j -lt $ladder.Count; $j++) {
            if (-not (Test-Inherits $ladder[$i].Stats $ladder[$j].Stats)) { $survives = $false; break }
        }
        if ($survives) { return $ladder[$i] }
    }
    return $null
}
# Each pair is name + jitter; a rung is "bad" when its jitter breaks its own scaled limit.
function L($pairs) {
    $out = @()
    foreach ($p in $pairs) {
        $base = [double]$p[2]
        $jit = [double]$p[1]
        $samples = @()
        for ($k = 0; $k -lt 20; $k++) {
            if ($k % 2 -eq 0) { $samples += $base } else { $samples += ($base + 2 * $jit) }
        }
        $st = New-Stats $samples 20
        $out += [pscustomobject]@{ Key = $p[0]; Bad = (Get-BadReason $st $null); Stats = $st }
    }
    return $out
}

Check 'all clean: no culprit' `
    (Find-Culprit (L @(@('gateway', 0.2, 1), @('access', 0.3, 4), @('relay', 0.5, 44)))) $null

Check 'bad from the router out: the router' `
    (Find-Culprit (L @(@('gateway', 5, 1), @('access', 6, 4), @('relay', 10, 44)))).Key 'gateway'

# The regression this rule exists for: a real Wi-Fi hiccup measured on this machine. The relay
# rung inherits it but stays well inside its own distance-scaled tolerance, so the old
# "is the outer rung also bad" test found no culprit and printed an all-clear under three red
# rows.
Check 'wifi hiccup absorbed by scaling is still caught' `
    (Find-Culprit (L @(@('gateway', 4.7, 1), @('access', 5.7, 4), @('core', 5.0, 10), @('relay', 9.9, 44)))).Key 'gateway'

Check 'home clean, abroad bad: the relay rung' `
    (Find-Culprit (L @(@('gateway', 0.2, 1), @('access', 0.3, 4), @('core', 0.4, 10), @('relay', 25, 44)))).Key 'relay'

# The rule that stops a deprioritising router from being blamed: it looks awful, everything
# past it is serene, so the packets were never actually delayed.
Check 'a lone bad middle hop is ignored' `
    (Find-Culprit (L @(@('gateway', 0.2, 1), @('access', 30, 4), @('core', 0.4, 10), @('relay', 0.5, 44)))) $null

Check 'bad access AND everything beyond: the access' `
    (Find-Culprit (L @(@('gateway', 0.2, 1), @('access', 12, 4), @('core', 14, 10), @('relay', 20, 44)))).Key 'access'

Check 'path clean, only relayd bad: the box' `
    (Find-Culprit (L @(@('gateway', 0.2, 1), @('relay', 0.5, 44), @('relay-udp', 25, 44), @('game', 30, 70)))).Key 'relay-udp'

Check 'everything clean but the game: beyond the relay' `
    (Find-Culprit (L @(@('gateway', 0.2, 1), @('relay', 0.5, 44), @('relay-udp', 0.6, 44), @('game', 40, 70)))).Key 'game'

# Loss propagates strictly, and a rung that loses nothing downstream did not cause the loss.
$lossyInner = New-Stats (1..10 | ForEach-Object { 4.0 }) 20
$cleanOuter = New-Stats (1..20 | ForEach-Object { 44.0 }) 20
Check 'loss that vanishes downstream is an artifact' (Test-Inherits $lossyInner $cleanOuter) $false
Check 'loss that persists downstream is inherited' `
    (Test-Inherits $lossyInner (New-Stats (1..10 | ForEach-Object { 44.0 }) 20)) $true
Check 'a mute outer rung contradicts nothing' (Test-Inherits $lossyInner (New-Stats @() 20)) $true

""
if ($fail -eq 0) {
    Write-Host "Diagnose-Lag: all checks passed" -ForegroundColor Green
    exit 0
}
Write-Host "Diagnose-Lag: $fail check(s) FAILED" -ForegroundColor Red
exit 1
