<#
.SYNOPSIS
    Runs the steps of .github/workflows/release.yml on this machine, the way GitHub Actions runs them.

.DESCRIPTION
    Called by `./gpb release` inside a clean git worktree of what is about to be tagged, before the
    tag exists. Releases on this repository are immutable, so a release that fails in CI burns its
    version for good - the only fix is the next number.

    Why it reads the workflow instead of repeating what the workflow does: the first rehearsal did
    repeat it, calling `gpb.ps1 release-profiles` in a process of its own and reading the exit code.
    The workflow called the same verb IN-PROCESS and then checked $LASTEXITCODE - which does not
    exist when no native program has run, and `$null -ne 0` is true. The verb succeeded, the step
    failed, and v0.2.7 was lost, while the rehearsal had passed. A rehearsal is only worth the time
    if it runs the same text, so this one runs the run: blocks themselves.

    How GitHub runs a `shell: pwsh` step, and so how this does: the block is written to a .ps1 with
    `$ErrorActionPreference = 'stop'` before it and
    `if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit $LASTEXITCODE }` after it, then
    started as `pwsh -command ". '<file>'"` from the repository root. Lines a step appends to
    $env:GITHUB_ENV become environment variables of every later step.

    What it does NOT run, by name - each must still exist in the workflow, so a rename is noticed:
      - Verify the tag is reachable from main  the tag does not exist yet; ./gpb release checks main itself
      - Install Inno Setup                      installs software; the local Inno Setup 6 is used
      - Create GitHub Release                   publishes
    `uses:` steps (checkout, setup-dotnet, setup-go) are the local toolchain instead.

    It is strict on purpose. A step with a key, a shell or an expression this file does not know how
    to rehearse fails the rehearsal and says what to teach it, rather than being run approximately.

.PARAMETER Tag
    The tag the release will have, e.g. v0.2.8.

.PARAMETER Repository
    owner/name on GitHub, for ${{ github.repository }}.

.PARAMETER Log
    File that receives every step's full output.

.PARAMETER Shell
    The PowerShell to run steps with. pwsh, like the workflow; anything else is for testing this file.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Log,
    [string]$Shell = 'pwsh'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$workflow = Join-Path $root '.github\workflows\release.yml'

$skipped = [ordered]@{
    'Verify the tag is reachable from main' = 'the tag does not exist yet; ./gpb release checked main against origin'
    'Install Inno Setup'                    = 'installs software; the local Inno Setup 6 is used'
    'Create GitHub Release'                 = 'publishes'
}

function Line($text, $colour = 'Gray') { Write-Host $text -ForegroundColor $colour }

# UTF-8 without a BOM whichever PowerShell runs this: 5.1's Add-Content writes the ANSI code page, and
# ./gpb reads this file back with tail.
function LogText([string]$text) {
    [System.IO.File]::AppendAllText($Log, $text, (New-Object System.Text.UTF8Encoding($false)))
}

# ------------------------------------------------------------------ read the workflow
#
# Not a YAML parser, and deliberately not general: it reads the shape this workflow has - one job,
# a steps: list of "- name:" items, run: as a literal block - and refuses anything else by name.

$lines = [System.IO.File]::ReadAllLines($workflow)

function Indent([string]$s) { return $s.Length - $s.TrimStart(' ').Length }
function IsBlankOrComment([string]$s) { $t = $s.Trim(); return ($t -eq '' -or $t.StartsWith('#')) }

# Top-level env: - "KEY: value" pairs indented under a column-0 "env:".
$workflowEnv = [ordered]@{}
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -ne 'env:') { continue }
    for ($j = $i + 1; $j -lt $lines.Count; $j++) {
        $l = $lines[$j]
        if (IsBlankOrComment $l) { continue }
        if ((Indent $l) -eq 0) { break }
        if ($l -notmatch '^\s+([A-Za-z_][A-Za-z0-9_]*):\s*(.*?)\s*(#.*)?$') { throw "release.yml env: line not understood: $l" }
        $workflowEnv[$Matches[1]] = $Matches[2].Trim('"', "'")
    }
    break
}

# The job's default shell must be the one this rehearses.
$defaultShell = ($lines | Where-Object { $_ -match '^\s+shell:\s*(\S+)' } | Select-Object -First 1)
if (-not $defaultShell -or $defaultShell -notmatch 'shell:\s*pwsh\s*$') {
    throw "release.yml's default shell is not pwsh ($defaultShell). This rehearsal only knows how GitHub runs pwsh steps."
}

$steps = New-Object System.Collections.Generic.List[object]
$stepsLine = -1
for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s+steps:\s*$') { $stepsLine = $i; break } }
if ($stepsLine -lt 0) { throw "No steps: in $workflow" }

$i = $stepsLine + 1
while ($i -lt $lines.Count) {
    $l = $lines[$i]
    if ($l -notmatch '^(\s*)- name:\s*(.+?)\s*$') { $i++; continue }
    $itemIndent = $Matches[1].Length
    $step = [ordered]@{ Name = $Matches[2].Trim('"', "'"); Uses = $null; Run = $null; Env = $false; Other = @() }
    $keyIndent = $itemIndent + 2
    $i++
    while ($i -lt $lines.Count) {
        $l = $lines[$i]
        if (IsBlankOrComment $l) { $i++; continue }
        $ind = Indent $l
        if ($ind -le $itemIndent) { break }
        if ($ind -ne $keyIndent -or $l -notmatch '^\s+([a-z-]+):\s*(.*)$') { throw "release.yml, step '$($step.Name)': line not understood: $l" }
        $key = $Matches[1]; $value = $Matches[2]
        $i++
        # Everything indented deeper than the key belongs to it.
        $body = New-Object System.Collections.Generic.List[string]
        while ($i -lt $lines.Count -and ($lines[$i].Trim() -eq '' -or (Indent $lines[$i]) -gt $keyIndent)) {
            $body.Add($lines[$i]); $i++
        }
        switch ($key) {
            'uses' { $step.Uses = $value.Trim() }
            'with' { }
            'env' { $step.Env = $true }
            'run' {
                if ($value.Trim() -ne '|') { throw "release.yml, step '$($step.Name)': run: must be a literal block (run: |)." }
                while ($body.Count -gt 0 -and $body[$body.Count - 1].Trim() -eq '') { $body.RemoveAt($body.Count - 1) }
                $first = $body | Where-Object { $_.Trim() -ne '' } | Select-Object -First 1
                $cut = Indent $first
                $step.Run = ($body | ForEach-Object { if ($_.Length -ge $cut) { $_.Substring($cut) } else { $_.TrimStart() } }) -join "`n"
            }
            default { $step.Other += $key }
        }
    }
    $steps.Add([pscustomobject]$step)
}

foreach ($name in $skipped.Keys) {
    if (-not ($steps | Where-Object { $_.Name -eq $name })) {
        throw "The rehearsal skips a step named '$name', and release.yml no longer has one. Update `$skipped in $PSCommandPath to match the workflow."
    }
}

# ------------------------------------------------------------------ run it

$shellExe = (Get-Command $Shell -ErrorAction SilentlyContinue).Source
if (-not $shellExe) {
    throw "$Shell not found. The release workflow runs its steps in PowerShell 7, and the rehearsal has to as well - " +
          "5.1 differs in exactly the places that break releases. Install it: winget install --id Microsoft.PowerShell"
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("gpb-rehearsal-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$githubEnv = Join-Path $work 'github_env'
New-Item -ItemType File -Force -Path $githubEnv | Out-Null
$sha = (& git -C $root rev-parse HEAD).Trim()

$stepEnv = [ordered]@{}
foreach ($k in $workflowEnv.Keys) { $stepEnv[$k] = $workflowEnv[$k] }
$stepEnv['CI'] = 'true'
$stepEnv['GITHUB_ACTIONS'] = 'true'
$stepEnv['GITHUB_REF_NAME'] = $Tag
$stepEnv['GITHUB_REF'] = "refs/tags/$Tag"
$stepEnv['GITHUB_REPOSITORY'] = $Repository
$stepEnv['GITHUB_SHA'] = $sha
$stepEnv['GITHUB_WORKSPACE'] = $root
$stepEnv['GITHUB_ENV'] = $githubEnv

$failed = $false
try {
    foreach ($step in $steps) {
        $label = '    {0,-48}' -f $step.Name
        if ($skipped.Contains($step.Name)) { Line "$label skipped - $($skipped[$step.Name])" 'DarkGray'; continue }
        if ($step.Uses) { Line "$label local toolchain ($($step.Uses))" 'DarkGray'; continue }
        if (-not $step.Run) { throw "Step '$($step.Name)' has neither run: nor uses:." }
        if ($step.Other.Count -gt 0) {
            throw "Step '$($step.Name)' uses $($step.Other -join ', '), which this rehearsal does not know how to reproduce. Teach $PSCommandPath."
        }
        if ($step.Env) {
            throw "Step '$($step.Name)' sets env:, which this rehearsal does not reproduce. Teach $PSCommandPath."
        }

        $script = $step.Run.Replace('${{ github.ref_name }}', $Tag).Replace('${{ github.repository }}', $Repository)
        if ($script -match '\$\{\{[^}]*\}\}') {
            throw "Step '$($step.Name)' uses the expression $($Matches[0]), which this rehearsal cannot evaluate. Teach $PSCommandPath."
        }

        # GitHub's own wrapper for shell: pwsh.
        $file = Join-Path $work ("step-" + [guid]::NewGuid().ToString('N') + '.ps1')
        $wrapped = "`$ErrorActionPreference = 'stop'`r`n" + ($script -replace "`n", "`r`n") +
                   "`r`nif ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit `$LASTEXITCODE }`r`n"
        [System.IO.File]::WriteAllText($file, $wrapped, (New-Object System.Text.UTF8Encoding($true)))

        foreach ($k in $stepEnv.Keys) { Set-Item -Path "env:$k" -Value $stepEnv[$k] }

        Write-Host -NoNewline $label
        LogText "`r`n==== step: $($step.Name)`r`n"
        # Start-Process with files, not `& $shellExe ... *>> $Log`: redirecting a native program's
        # stderr through PowerShell's streams behaves differently in 5.1 and 7 - under 'Stop', 5.1
        # turns the first stderr line into a terminating error - and a rehearsal must not depend on
        # which PowerShell happens to run it.
        $out = Join-Path $work 'stdout.txt'
        $err = Join-Path $work 'stderr.txt'
        $proc = Start-Process -FilePath $shellExe -ArgumentList "-NoProfile -command "". '$file'""" `
            -WorkingDirectory $root -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $out -RedirectStandardError $err
        $code = $proc.ExitCode
        foreach ($f in $out, $err) { if (Test-Path $f) { LogText ([System.IO.File]::ReadAllText($f)) } }
        if ($code -ne 0) {
            Line "FAILED (exit $code)" 'Red'
            LogText "`r`n==== step '$($step.Name)' exited $code`r`n"
            $failed = $true
            break
        }
        Line 'ok' 'Green'

        # KEY=VALUE lines a step appended to GITHUB_ENV, for the steps after it. The multi-line form
        # (KEY<<DELIMITER) is refused rather than half-read.
        foreach ($envLine in [System.IO.File]::ReadAllLines($githubEnv)) {
            if ($envLine.Trim() -eq '') { continue }
            if ($envLine -match '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') { $stepEnv[$Matches[1]] = $Matches[2]; continue }
            throw "A step wrote '$envLine' to GITHUB_ENV, which this rehearsal does not understand. Teach $PSCommandPath."
        }
    }
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

if ($failed) { exit 1 }
exit 0
