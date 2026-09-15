<#
.SYNOPSIS
    Puts a locally built game profile into the running service, sealed to this machine, so a game
    the licence server does not serve yet can be played through the tunnel.

.DESCRIPTION
    A licensed installation reads ONLY the sealed profiles the licence server sent
    (%ProgramData%\GamePingBooster\profiles\<game>.sealed). profiles\<game>-vn.json beside the
    repository is ignored there, so a game that exists only in games.json cannot be tested by
    building its profile alone. This does what the app's sync does, with a local file instead of
    a server response:

      1. validate the profile against every other game's (Test-Profile.ps1)
      2. read this machine's device public key from the service
      3. seal the game's entry to it with web-service/scripts/seal-local-profile.ts - the same
         ECIES the licence server uses, and no server secret is involved
      4. push it with set-profile, exactly as the app does
      5. make the new file OLDER than every other sealed profile, and reload

    Step 5 is the one that matters. The service takes its relay list from the NEWEST sealed
    profile (ProfileMerge), and a locally built profile has no relays - the real ones are on the
    licence server, sealed where nothing here can read them. Left newest, it would empty the relay
    list for every game. Older than the rest, it adds its game and nothing else, and it stays that
    way when the app next refreshes the other games.

    The pipe serves one client at a time, so the app must be closed while this runs.

    A development tool. The file stays until `./gpb push-profile <game> remove`, or until the
    licence server serves the same game and the app's sync replaces it.

.EXAMPLE
    .\Push-LocalProfile.ps1 -Game valorant

.EXAMPLE
    .\Push-LocalProfile.ps1 -Game valorant -Remove
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Game,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'GpbGames.ps1')

$profilesDir = Join-Path $env:ProgramData 'GamePingBooster\profiles'
$logPath = Join-Path $env:ProgramData 'GamePingBooster\logs\gpb-service.log'
$entry = Get-GpbGame $root $Game
$sealedPath = Join-Path $profilesDir "$($entry.Id).sealed"

function Say($msg, $colour = 'Cyan') { Write-Host "==> $msg" -ForegroundColor $colour }
function Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

# Checked first, before anything is pushed: the timestamp in step 5 can only be set by an
# Administrator, and a push without it leaves every game with no relays.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this from an Administrator terminal - it has to change a file in $profilesDir, which only Administrators can."
}

# The tunnel state by name. It crosses the pipe as a NUMBER - TunnelState has no string converter in
# IpcJsonContext - so comparing the raw field with 'Disconnected' refused a tunnel that was
# disconnected, reporting it as "0". Names are accepted too, in case the contract ever changes.
function Get-TunnelStateName {
    param($State)
    $names = @('Disconnected', 'Connecting', 'Connected', 'Reconnecting', 'Faulted')
    $text = "$State"
    if ($text -match '^\d+$' -and [int]$text -lt $names.Count) { return $names[[int]$text] }
    return $text
}

function Read-StatusLine {
    param($Reader, [int]$TimeoutMs)
    $task = $Reader.ReadLineAsync()
    if (-not $task.Wait($TimeoutMs) -or -not $task.Result) { return $null }
    return ($task.Result | ConvertFrom-Json)
}

<#
    Opens the pipe, reads the status the service pushes on connect, optionally sends one command,
    and hands the open reader to -Then. The service has no reply that is reliably "the answer to
    this command" - set-profile answers with an ordinary status, and a heartbeat can arrive first -
    so callers judge the outcome by what changed on disk, not by the next line.
#>
function Use-ServicePipe {
    param([hashtable]$Command, [scriptblock]$Then)

    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'GamePingBooster', [System.IO.Pipes.PipeDirection]::InOut)
    try { $pipe.Connect(3000) }
    catch {
        $pipe.Dispose()
        throw ("Could not reach the service. It serves one client at a time: close the Game Ping Booster " +
               "app (the tray icon too), make sure the service is running (./gpb dev), and try again.")
    }
    try {
        $reader = New-Object System.IO.StreamReader($pipe)
        $writer = New-Object System.IO.StreamWriter($pipe)
        $writer.AutoFlush = $true
        $hello = Read-StatusLine -Reader $reader -TimeoutMs 5000
        if (-not $hello) { throw "The service accepted the connection but sent nothing." }
        if ($Command) {
            $Command['v'] = 2
            $writer.WriteLine(($Command | ConvertTo-Json -Compress))
        }
        if ($Then) { return (& $Then $hello $reader) }
        return $hello
    }
    finally { $pipe.Dispose() }
}

# The service's own "Loaded the pushed profile ... (games)" line written after $Since, or $null.
function Wait-ProfileLoadedLine {
    param([datetime]$Since, [int]$Seconds = 8)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $logPath) {
            $line = Get-Content $logPath -Tail 40 | Where-Object {
                $_ -match 'Loaded the pushed profile' -and $_.Length -ge 23 -and
                ([datetime]::ParseExact($_.Substring(0, 23), 'yyyy-MM-dd HH:mm:ss.fff', $null) -ge $Since)
            } | Select-Object -Last 1
            if ($line) { return $line }
        }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

# ------------------------------------------------------------------ remove

if ($Remove) {
    if (-not (Test-Path $sealedPath)) {
        Say "Nothing to remove - there is no $($entry.Id).sealed" 'DarkGray'
        return
    }
    $hello = Use-ServicePipe
    $state = Get-TunnelStateName $hello.state
    if ($state -ne 'Disconnected') { throw "The tunnel is $state. Disconnect first, then run this again." }

    Remove-Item $sealedPath -Force
    $since = (Get-Date).AddSeconds(-1)
    Use-ServicePipe -Command @{ verb = 'reload-profile' } | Out-Null
    Say "Removed $($entry.Id).sealed" 'Green'
    $loaded = Wait-ProfileLoadedLine -Since $since
    if ($loaded) { Write-Host "    $loaded" -ForegroundColor DarkGray }
    return
}

# ------------------------------------------------------------------ push

if (-not (Test-Path $entry.ProfilePath)) {
    throw "No profile at $($entry.ProfilePath). Build it first: ./gpb profile $($entry.Id)"
}

Say "Validating $(Split-Path $entry.ProfilePath -Leaf) against the other games"
$otherProfiles = @(Get-GpbOtherGames $root $entry.Id | ForEach-Object { $_.ProfilePath } |
                   Where-Object { $_ -and (Test-Path $_) })
& (Join-Path $root 'tools\profile-builder\Test-Profile.ps1') -Path $entry.ProfilePath -OtherProfilePaths $otherProfiles
if ($LASTEXITCODE -ne 0) { throw "The profile failed validation - nothing was pushed." }

$data = Get-Content $entry.ProfilePath -Raw | ConvertFrom-Json
$games = @($data.games | Where-Object { $_.id -eq $entry.Id })
if ($games.Count -ne 1) { throw "$($entry.ProfilePath) holds no game with id '$($entry.Id)'." }

$web = Join-Path (Split-Path $root -Parent) 'web-service'
$tsx = Join-Path $web 'node_modules\tsx\dist\cli.mjs'
$sealScript = Join-Path $web 'scripts\seal-local-profile.ts'
if (-not (Test-Path $tsx) -or -not (Test-Path $sealScript)) {
    throw "Sealing needs web-service beside this repository with its packages installed ($tsx). Run npm install there."
}
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw "Node.js is not on PATH - it runs the sealing script." }

$hello = Use-ServicePipe
$state = Get-TunnelStateName $hello.state
if ($state -ne 'Disconnected') {
    # set-profile reloads at once, and for the moment before step 5 this game's empty relay list is
    # the service's relay list. Harmless while disconnected; not something to do under a live tunnel.
    throw "The tunnel is $state. Disconnect first, then run this again."
}
if (-not $hello.licenceUrl) {
    Warn "This installation is self-hosted: it loads $(Split-Path $entry.ProfilePath -Leaf) from beside its profile"
    Warn "already, and a sealed copy is only read when that file is missing. Pushing anyway."
}
$key = $hello.devicePublicKey
if (-not $key) { throw "The service did not report a device public key - it is too old for sealed profiles." }

# Only this game, and NO relays - see step 5 in the description.
$bundle = [pscustomobject]@{
    schemaVersion = 1
    generatedUtc  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    games         = @($games)
    relays        = @()
}
$plain = Join-Path ([IO.Path]::GetTempPath()) ("gpb-push-{0}-{1}.json" -f $entry.Id, [guid]::NewGuid().ToString('N'))
try {
    # UTF-8 WITHOUT a byte-order mark: JSON.parse in the sealing script rejects one.
    [IO.File]::WriteAllText($plain, ($bundle | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding $false))
    Say "Sealing $($games[0].name) to this machine's device key"
    $hex = (& node $tsx $sealScript $key $plain | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $hex -notmatch '^[0-9a-f]+$') { throw "Sealing failed: $hex" }
}
finally {
    Remove-Item $plain -Force -ErrorAction SilentlyContinue
}

$before = if (Test-Path $sealedPath) { (Get-Item $sealedPath).LastWriteTimeUtc } else { [datetime]::MinValue }

Say "Pushing it to the service"
$refusal = Use-ServicePipe -Command @{ verb = 'set-profile'; profile = $hex } -Then {
    param($hello, $reader)
    # Success is the file being written. A refusal comes back as a status whose error differs from
    # the one the tunnel already carried when we connected.
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $sealedPath) -and (Get-Item $sealedPath).LastWriteTimeUtc -gt $before) { return $null }
        $status = Read-StatusLine -Reader $reader -TimeoutMs 1000
        if ($status -and $status.error -and $status.error -ne $hello.error) { return $status.error }
    }
    return "the service neither stored the profile nor refused it within 10 s - see ./gpb logs"
}
if ($refusal) { throw "The service refused the profile: $refusal" }

# Step 5: never the newest, so it never supplies the relay list.
$others = @(Get-ChildItem $profilesDir -Filter '*.sealed' | Where-Object { $_.FullName -ne $sealedPath } |
            Sort-Object LastWriteTimeUtc)
if ($others.Count -eq 0) {
    Warn "No other sealed profile is stored, so this one - with no relays - is the only relay list there is."
    Warn "Open the app and sign in so it fetches the licence server's profiles, then push again."
} else {
    (Get-Item $sealedPath).LastWriteTimeUtc = $others[0].LastWriteTimeUtc.AddMinutes(-1)
}

$since = (Get-Date).AddSeconds(-1)
Use-ServicePipe -Command @{ verb = 'reload-profile' } | Out-Null
$loaded = Wait-ProfileLoadedLine -Since $since

Write-Host ""
if ($loaded -and $loaded -like "*$($games[0].name)*") {
    Say "$($games[0].name) is loaded" 'Green'
    Write-Host "    $loaded" -ForegroundColor DarkGray
} else {
    Warn "Pushed, but the service log does not show $($games[0].name) in a reload yet. Check: ./gpb logs"
}

$cidrs = @($games[0].regions | ForEach-Object { $_.cidrs } | Where-Object { $_ })
Write-Host ""
Write-Host "    Routed while $(@($games[0].processNames) -join ' / ') runs: $($cidrs -join ', ')"
Write-Host "    Next: open the app, Connect, start the game, then confirm the routes exist:"
Write-Host "      Get-NetRoute -DestinationPrefix $($cidrs[0])"
Write-Host "    Undo: ./gpb push-profile $($entry.Id) remove"
