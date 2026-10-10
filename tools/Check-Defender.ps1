<#
.SYNOPSIS
    Does Microsoft Defender flag a published release - and if it does, exactly which file to submit.

.DESCRIPTION
    The installer is unsigned, so every release is a new hash with no reputation, and Defender's
    machine-learning verdicts (Trojan:Win32/Wacatac.B!ml and friends) land on exactly that kind of
    file. A submission for one version does nothing for the next. Submitting a file nobody flagged
    is closed as "no detection, no telemetry" and earns nothing either, so the useful move is to
    check each release the way a player's PC sees it and submit only what is actually detected.

    "The way a player's PC sees it" decides every step here:

      - The installer comes from the GitHub release, never installer\dist. The CI build is the file
        players get; a local build is a different hash and proves nothing about it.
      - It is given the Mark of the Web a browser would give it (Zone.Identifier, ZoneId=3), because
        Defender and SmartScreen treat a downloaded file differently from a local one.
      - Signatures are updated first and cloud protection is checked, because the !ml verdicts come
        from the cloud and a scan with it off is a clean result that means nothing.
      - The installed files are scanned too, if this version is installed. Defender often flags
        gpb-service.exe or GamePingBooster.exe when they run, not the setup .exe, and a submission
        of the installer does not clear them.

    Scans run with -DisableRemediation: a detection is reported, nothing is quarantined, and the
    file stays on disk to be submitted.

    Optional: with VT_API_KEY in the environment, each file's hash is looked up on VirusTotal
    (lookup only - nothing is uploaded). Without it the VirusTotal link is printed to open by hand.

.PARAMETER Version
    The release to check. Defaults to VERSION, which is what ./gpb release just published.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times slower drawing its bar
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Say($msg, $colour = 'Cyan') { Write-Host "==> $msg" -ForegroundColor $colour }
function Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }
function Note($msg) { Write-Host "    $msg" -ForegroundColor DarkGray }

if (-not $Version) { $Version = (Get-Content (Join-Path $RepoRoot 'VERSION') -Raw).Trim() }
$Version = $Version.TrimStart('v')

# The repository is read from origin rather than typed, so a fork checks its own releases.
$origin = (& git -C $RepoRoot remote get-url origin 2>$null)
if ($origin -notmatch 'github\.com[:/]([^/]+)/([^/.]+?)(\.git)?$') {
    throw "origin is '$origin', not a GitHub repository - cannot tell where the release lives."
}
$repo = "$($Matches[1])/$($Matches[2])"
$assetName = "GamePingBooster-Setup-$Version.exe"
$releaseUrl = "https://github.com/$repo/releases/tag/v$Version"
$assetUrl = "https://github.com/$repo/releases/download/v$Version/$assetName"

$mpcmd = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
if (-not (Test-Path $mpcmd)) { throw "MpCmdRun.exe not found at $mpcmd - is Microsoft Defender installed?" }

# --------------------------------------------------------------------------- Defender state

# Defender is passive on a PC with another antivirus (WinDefend stopped, Get-MpComputerStatus fails
# with 0x800106ba). That is not an error to stop on: the download and VirusTotal still say
# something, and VirusTotal's "Microsoft" engine is Defender's own verdict.
$defenderOk = $false
$cloudOn = $false
$svc = Get-Service WinDefend -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq 'Running') {
    Say "Defender: updating signatures"
    & $mpcmd -SignatureUpdate | Out-Null
    if ($LASTEXITCODE -ne 0) { Warn "Signature update exited with $LASTEXITCODE - scanning with the signatures already here." }
    try {
        $status = Get-MpComputerStatus
        $pref = Get-MpPreference
        $defenderOk = [bool]$status.AntivirusEnabled
    } catch { }
}
if ($defenderOk) {
    Note "signatures $($status.AntivirusSignatureVersion), engine $($status.AMEngineVersion), updated $($status.AntivirusSignatureLastUpdated)"
    $cloudOn = $pref.MAPSReporting -ne 0
    if (-not $cloudOn) {
        Warn "Cloud-delivered protection is OFF. The !ml detections come from the cloud, so a clean"
        Warn "result below is not evidence. Turn it on in Windows Security > Virus & threat protection."
    }
} else {
    $other = (Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntivirusProduct -ErrorAction SilentlyContinue |
        Where-Object displayName -ne 'Windows Defender' | ForEach-Object displayName) -join ', '
    Warn "Defender is not active on this PC$(if ($other) { " ($other is the antivirus here)" }), so it cannot scan."
    if ($env:VT_API_KEY) {
        Warn "Falling back to VirusTotal's Microsoft engine, which is Defender's verdict."
    } else {
        Warn "Set VT_API_KEY (free key at virustotal.com) to read Defender's verdict from VirusTotal"
        Warn "instead, or open the VirusTotal links printed below."
    }
}

# --------------------------------------------------------------------------- the files

$work = Join-Path $env:TEMP "gpb-defendercheck\$Version"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$installer = Join-Path $work $assetName

Say "Downloading $assetName from the v$Version release"
try {
    Invoke-WebRequest -Uri $assetUrl -OutFile $installer -UseBasicParsing
} catch {
    throw "Could not download $assetUrl ($($_.Exception.Message)). Has the release workflow finished? $releaseUrl"
}
# What a browser writes on a download. Without it this is a "local" file to Defender and SmartScreen.
Set-Content -Path $installer -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3`r`nHostUrl=$assetUrl"
Note $installer

$files = @([pscustomobject]@{ Label = 'installer'; Path = $installer })

# The installed copy, only when it IS this version: scanning an older install would report on
# binaries this release does not ship.
$installDir = Join-Path $env:ProgramFiles 'Game Ping Booster'
$installedExe = Join-Path $installDir 'GamePingBooster.exe'
if (Test-Path $installedExe) {
    $installedVersion = (Get-Item $installedExe).VersionInfo.ProductVersion
    if ($installedVersion -and $installedVersion.Trim() -eq $Version) {
        Get-ChildItem $installDir -File -Include *.exe, *.dll -Recurse |
            Where-Object { $_.Name -notlike 'unins*' } |
            ForEach-Object { $files += [pscustomobject]@{ Label = 'installed'; Path = $_.FullName } }
    } else {
        Warn "Installed version is $installedVersion, not $Version - only the installer is checked."
        Warn "Install $Version from the download above and run this again to check gpb-service.exe and the app."
    }
} else {
    Warn "Game Ping Booster is not installed here - only the installer is checked."
    Warn "Install it from the download above and run this again to check gpb-service.exe and the app."
}

# --------------------------------------------------------------------------- scan

$vtKey = $env:VT_API_KEY

# VirusTotal's report for one file, uploading it first if VirusTotal has never seen it - a new
# release always starts unknown. Every file checked here is already public (the installer is on
# the GitHub release, and the installed files are inside it), so uploading discloses nothing.
# The free API allows 4 requests a minute, hence the 20 s poll. $null when there is no report.
function Get-VtReport($path, $sha) {
    $headers = @{ 'x-apikey' = $vtKey }
    $url = "https://www.virustotal.com/api/v3/files/$sha"
    try {
        return Invoke-RestMethod -Uri $url -Headers $headers -UseBasicParsing
    } catch {
        $status = $_.Exception.Response.StatusCode.value__
        if ($status -ne 404) { Warn "VirusTotal lookup failed ($status): $($_.Exception.Message)"; return $null }
    }
    if ((Get-Item $path).Length -gt 32MB) { Warn "$(Split-Path $path -Leaf) is over 32 MB, too big for the free upload - upload it by hand."; return $null }

    Note "VirusTotal has not seen $(Split-Path $path -Leaf) - uploading it"
    # curl.exe, not Invoke-RestMethod: Windows PowerShell 5.1 has no multipart form upload.
    $json = & "$env:SystemRoot\System32\curl.exe" -s -X POST 'https://www.virustotal.com/api/v3/files' `
        -H "x-apikey: $vtKey" -F "file=@$path" | Out-String
    try { $analysisId = ($json | ConvertFrom-Json).data.id } catch { $analysisId = $null }
    if (-not $analysisId) { Warn "Upload failed: $json"; return $null }

    $deadline = (Get-Date).AddMinutes(10)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 20
        try {
            $a = Invoke-RestMethod -Uri "https://www.virustotal.com/api/v3/analyses/$analysisId" -Headers $headers -UseBasicParsing
            if ($a.data.attributes.status -eq 'completed') {
                return Invoke-RestMethod -Uri $url -Headers $headers -UseBasicParsing
            }
            Note "  still analysing ($($a.data.attributes.status))"
        } catch {
            Note "  waiting ($($_.Exception.Message))"
        }
    }
    Warn "VirusTotal had not finished after 10 minutes - run this again later."
    return $null
}
$results = @()
Say "Checking $($files.Count) file(s)"
foreach ($f in $files) {
    $threat = $null
    $verdict = 'not scanned'
    if ($defenderOk) {
        $out = & $mpcmd -Scan -ScanType 3 -File $f.Path -DisableRemediation 2>&1 | Out-String
        $code = $LASTEXITCODE
        if ($out -match 'Threat\s*:\s*(\S+)') { $threat = $Matches[1] }
        $verdict = switch ($code) {
            0 { 'clean' }
            2 { 'DETECTED' }
            default { "scan failed ($code)" }
        }
    }
    $sha = (Get-FileHash $f.Path -Algorithm SHA256).Hash.ToLowerInvariant()

    $vt = ''
    if ($vtKey) {
        $r = Get-VtReport $f.Path $sha
        if ($r) {
            $stats = $r.data.attributes.last_analysis_stats
            $ms = $r.data.attributes.last_analysis_results.Microsoft
            $msText = if ($ms -and $ms.result) { $ms.result } else { 'clean' }
            # A cached verdict: VirusTotal does not rescan on a lookup, so the date says how old it is.
            $when = [DateTimeOffset]::FromUnixTimeSeconds([long]$r.data.attributes.last_analysis_date).ToLocalTime().ToString('yyyy-MM-dd HH:mm')
            $flaggers = @($r.data.attributes.last_analysis_results.PSObject.Properties |
                Where-Object { $_.Value.category -eq 'malicious' } | ForEach-Object Name) -join ', '
            $vt = "VT $($stats.malicious)/$($stats.malicious + $stats.undetected + $stats.harmless) flag$(if ($flaggers) { " ($flaggers)" }), Microsoft: $msText, analysed $when"
            # Without a local Defender, VirusTotal's Microsoft engine IS the verdict.
            if (-not $defenderOk -and $ms) {
                if ($ms.category -eq 'malicious') { $verdict = 'DETECTED'; $threat = $ms.result }
                elseif ($ms.category -eq 'undetected') { $verdict = 'clean (VT)' }
            }
        } else {
            $vt = 'VT: no result (see above)'
        }
    }

    $colour = switch -Wildcard ($verdict) { 'clean*' { 'Green' } 'not scanned' { 'Yellow' } default { 'Red' } }
    $name = Split-Path $f.Path -Leaf
    Write-Host ("    {0,-10} {1,-32} {2}{3}" -f $f.Label, $name, $verdict, $(if ($threat) { "  $threat" } else { '' })) -ForegroundColor $colour
    if ($vt) { Note "               $vt" }

    $results += [pscustomobject]@{ Label = $f.Label; Name = $name; Path = $f.Path; Verdict = $verdict; Threat = $threat; Sha256 = $sha }
}

# Real-time detections from running the installer or the app, which a scan after the fact can miss
# if the file was already quarantined. Reading the history can need Administrator; skipped if so.
try {
    $recent = Get-MpThreatDetection | Where-Object {
        $_.InitialDetectionTime -gt (Get-Date).AddDays(-2) -and
        ($_.Resources -match 'GamePingBooster|gpb-service|Game Ping Booster|gpb-defendercheck')
    }
    foreach ($d in $recent) {
        $t = (Get-MpThreat -ThreatID $d.ThreatID -ErrorAction SilentlyContinue).ThreatName
        Warn "Defender history: $t on $($d.Resources -join ', ') at $($d.InitialDetectionTime)"
    }
} catch {
    Note "Defender's detection history needs Administrator - skipped."
}

# --------------------------------------------------------------------------- verdict

Write-Host ''
Note "VirusTotal (every engine's verdict):"
foreach ($r in $results | Where-Object { $_.Label -eq 'installer' -or $_.Name -in 'gpb-service.exe', 'GamePingBooster.exe' }) {
    Note "  $($r.Name): https://www.virustotal.com/gui/file/$($r.Sha256)"
}
Write-Host ''

$detected = @($results | Where-Object { $_.Verdict -eq 'DETECTED' })
$failed = @($results | Where-Object { $_.Verdict -like 'scan failed*' })

if ($detected.Count -gt 0) {
    Say "Submit these to Microsoft - each file on its own, not just the installer:" 'Red'
    foreach ($d in $detected) { Write-Host "      $($d.Path)   ($($d.Threat))" -ForegroundColor Red }
    Write-Host ''
    Write-Host '    https://www.microsoft.com/en-us/wdsi/filesubmission' -ForegroundColor White
    Write-Host '      Submit as:   Software developer'
    Write-Host '      Product:     Microsoft Defender Antivirus (Windows 10/11)'
    Write-Host '      Opinion:     Incorrect detection'
    Write-Host "      Detection:   $(($detected | ForEach-Object Threat | Sort-Object -Unique) -join ', ')"
    Write-Host "      Note:        Open-source game network tool, unsigned. Release: $releaseUrl"
    Write-Host "                   Source: https://github.com/$repo"
    Write-Host ''
    Note "The files stay in place (scanned with -DisableRemediation). Re-run this after Microsoft"
    Note "answers to confirm the new signatures clear them."
    exit 1
}
if ($failed.Count -gt 0) {
    Say "Some scans failed - see above. Nothing to submit yet." 'Yellow'
    exit 2
}
if (@($results | Where-Object { $_.Verdict -eq 'not scanned' }).Count -gt 0) {
    Say "No Defender verdict for some files - check the VirusTotal links above (Microsoft row)." 'Yellow'
    exit 2
}
if ($cloudOn -or -not $defenderOk) {
    Say "Microsoft flags nothing in v$Version - nothing to submit." 'Green'
} else {
    Say "Defender flags nothing, but cloud protection was off - not conclusive." 'Yellow'
}
exit 0
