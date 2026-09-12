<#
.SYNOPSIS
    Reads tools\profile-builder\games.json and turns a game name into the arguments the capture
    and profile scripts need.

.DESCRIPTION
    Dot-sourced by gpb.ps1, and deliberately shaped like GpbConf.ps1 next to it: one parser, in
    one place, so `./gpb capture cs2` and `./gpb profile cs2` cannot disagree about what cs2 is.

    Only PowerShell reads this. `capture` and `profile` are Windows-only commands that the POSIX
    ./gpb hands straight to gpb.ps1, so the JSON never has to be parsed by a shell script - which
    is why it can be JSON at all, and why the per-game settings can be lists instead of the
    flattened KEY=value that gpb.conf is stuck with.

    A game the file does not declare is an ERROR, not something to guess at. The alternative -
    falling back to PUBG's settings under another name - would capture the wrong process into the
    wrong file and look like it had worked.
#>

function Get-GpbGamesPath {
    param([string]$RepoRoot)
    return (Join-Path $RepoRoot 'tools\profile-builder\games.json')
}

function Read-GpbGames {
    param([string]$RepoRoot)

    $path = Get-GpbGamesPath $RepoRoot
    if (-not (Test-Path -LiteralPath $path)) {
        throw "No games.json at $path - it declares every game ./gpb capture and ./gpb profile know about."
    }

    try {
        return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
    }
    catch {
        # Naming the file matters: the parse error on its own says "invalid JSON primitive" and
        # nothing about where, and this is a file people edit by hand to add a game.
        throw "games.json at $path is not valid JSON: $($_.Exception.Message)"
    }
}

function Get-GpbGameNames {
    param([string]$RepoRoot)
    $games = (Read-GpbGames $RepoRoot).games
    if (-not $games) { return @() }
    return @($games.PSObject.Properties.Name | Sort-Object)
}

<#
.SYNOPSIS
    The settings for one game, with every path resolved to an absolute one.

.PARAMETER Name
    The game to look up. Empty falls back to the file's "default", which is what makes
    `./gpb capture` with no argument keep doing what it always did.
#>
function Get-GpbGame {
    param(
        [string]$RepoRoot,
        [string]$Name
    )

    $config = Read-GpbGames $RepoRoot
    $wanted = if ([string]::IsNullOrWhiteSpace($Name)) { $config.default } else { $Name.ToLowerInvariant() }

    if ([string]::IsNullOrWhiteSpace($wanted)) {
        throw "games.json declares no `"default`", so a game has to be named: ./gpb capture <game>"
    }

    $entry = $config.games.PSObject.Properties | Where-Object { $_.Name -eq $wanted } | Select-Object -First 1
    if (-not $entry) {
        $known = (Get-GpbGameNames $RepoRoot) -join ', '
        throw "games.json does not declare '$wanted'. Known games: $known"
    }

    $builder = Join-Path $RepoRoot 'tools\profile-builder'
    $game = $entry.Value

    # Resolve against the builder folder rather than the caller's location. Both scripts are run
    # with Push-Location into that folder today, but a relative path that only works because of
    # where the caller happened to be standing is the kind of thing that breaks the first time
    # somebody calls it from anywhere else.
    function Resolve-GamePath([string]$relative) {
        if ([string]::IsNullOrWhiteSpace($relative)) { return $null }
        return [System.IO.Path]::GetFullPath((Join-Path $builder $relative))
    }

    return [PSCustomObject]@{
        Id             = $wanted
        Name           = if ($game.name) { $game.name } else { $wanted }
        WatchProcess   = $game.watchProcess
        ProbePort      = $game.probePort
        ObservedPath   = Resolve-GamePath $game.observedPath
        LandmarkPath   = Resolve-GamePath $game.landmarkPath
        ManualCidrPath = Resolve-GamePath $game.manualCidrPath
        ProfilePath    = Resolve-GamePath $game.profilePath
        AwsRegions     = @($game.awsRegions)
        AzureRegions   = @($game.azureRegions)
        Note           = $game.note
    }
}

<#
.SYNOPSIS
    True when this game has enough declared for the profile builder to produce anything honest.

.DESCRIPTION
    The builder keeps an observed address only when it falls inside a published range of one of
    the declared cloud regions. With both lists empty every address is unverified, and the run
    ends with a profile containing no ranges at all - which is not an empty result, it is a
    wrong one: it looks like a finished profile and would ship as such.

    So this is checked before the builder runs rather than after, and the caller refuses. See the
    cs2 note in games.json for the case this exists for.
#>
function Test-GpbGameBuildable {
    param([PSCustomObject]$Game)
    return (@($Game.AwsRegions).Count + @($Game.AzureRegions).Count) -gt 0
}
