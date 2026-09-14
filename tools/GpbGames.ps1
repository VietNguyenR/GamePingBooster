<#
.SYNOPSIS
    Reads tools\profile-builder\games.json and turns a game name into the arguments the capture
    and profile scripts need.

.DESCRIPTION
    Dot-sourced by gpb.ps1 and by Build-PubgProfile.ps1, and deliberately shaped like GpbConf.ps1
    next to it: one parser, in one place, so `./gpb capture cs2` and `./gpb profile cs2` cannot
    disagree about what cs2 is.

    Only PowerShell reads this. `capture` and `profile` are Windows-only commands that the POSIX
    ./gpb hands straight to gpb.ps1, so the JSON never has to be parsed by a shell script - which
    is why it can be JSON at all, and why the per-game settings can be lists instead of the
    flattened KEY=value that gpb.conf is stuck with.

    A game the file does not declare is an ERROR, not something to guess at. The alternative -
    falling back to PUBG's settings under another name - would capture the wrong process into the
    wrong file and look like it had worked. The same goes for every file a game writes: each game
    names its own, and two games naming the same one is refused before anything runs.
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

# One games.json entry, with every path resolved to an absolute one.
function ConvertTo-GpbGame {
    param([string]$RepoRoot, [string]$Id, $Game)

    $builder = Join-Path $RepoRoot 'tools\profile-builder'

    # Resolve against the builder folder rather than the caller's location. Both scripts are run
    # with Push-Location into that folder today, but a relative path that only works because of
    # where the caller happened to be standing is the kind of thing that breaks the first time
    # somebody calls it from anywhere else.
    function Resolve-GamePath([string]$relative) {
        if ([string]::IsNullOrWhiteSpace($relative)) { return $null }
        return [System.IO.Path]::GetFullPath((Join-Path $builder $relative))
    }

    # No defaults for these. The scripts behind them used to default to PUBG's file names, and a
    # game that left one out got PUBG's file - which is how CS2's captures could land in PUBG's
    # landmark list.
    foreach ($field in 'watchProcess', 'observedPath', 'tcpSessionsPath', 'unverifiedPath', 'manualCidrPath', 'profilePath') {
        if ([string]::IsNullOrWhiteSpace([string]$Game.$field)) {
            throw "games.json: '$Id' declares no $field. Every game names its own files - a shared or borrowed one mixes two games' addresses."
        }
    }

    # @() around a pipeline, not a helper function: a function returning an empty array hands its
    # caller $null, and @($null) is an array of one.
    return [PSCustomObject]@{
        Id                = $Id
        Name              = if ($Game.name) { $Game.name } else { $Id }
        WatchProcess      = $Game.watchProcess
        ProbePort         = $Game.probePort
        ObservedPath      = Resolve-GamePath $Game.observedPath
        LandmarkPath      = Resolve-GamePath $Game.landmarkPath
        TcpSessionsPath   = Resolve-GamePath $Game.tcpSessionsPath
        UnverifiedPath    = Resolve-GamePath $Game.unverifiedPath
        ManualCidrPath    = Resolve-GamePath $Game.manualCidrPath
        ProfilePath       = Resolve-GamePath $Game.profilePath
        AwsRegions        = @(@($Game.awsRegions) | Where-Object { $_ })
        AzureRegions      = @(@($Game.azureRegions) | Where-Object { $_ })
        GlobalAccelerator = [bool]$Game.globalAccelerator
        Asns              = @(@($Game.asns) | Where-Object { $null -ne $_ } | ForEach-Object { [int]$_ })
        Note              = $Game.note
    }
}

<#
.SYNOPSIS
    Every declared game, after checking that no two of them write the same file.

.DESCRIPTION
    Checked on every call rather than trusted to review: the files are append-only address lists,
    and the day two games share one there is no way to split it again. A path is compared across
    all of a game's fields too, so one game's unverified list cannot be another's observed list.
#>
function Get-GpbAllGames {
    param([string]$RepoRoot)

    $config = Read-GpbGames $RepoRoot
    $all = @()
    if ($config.games) {
        foreach ($entry in @($config.games.PSObject.Properties)) {
            $all += ConvertTo-GpbGame -RepoRoot $RepoRoot -Id $entry.Name -Game $entry.Value
        }
    }

    $owners = @{}
    foreach ($game in $all) {
        foreach ($field in 'ObservedPath', 'LandmarkPath', 'TcpSessionsPath', 'UnverifiedPath', 'ManualCidrPath', 'ProfilePath') {
            $path = $game.$field
            if (-not $path) { continue }
            $key = $path.ToLowerInvariant()
            if ($owners.ContainsKey($key)) {
                $first = $owners[$key]
                throw ("games.json: '$($game.Id)' $field and '$($first.Id)' $($first.Field) are the same file, $path. " +
                       "Give each its own - once two sets of addresses share a file they cannot be told apart.")
            }
            $owners[$key] = [pscustomobject]@{ Id = $game.Id; Field = $field }
        }
    }

    return [pscustomobject]@{ Config = $config; Games = $all }
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

    $everything = Get-GpbAllGames $RepoRoot
    $wanted = if ([string]::IsNullOrWhiteSpace($Name)) { $everything.Config.default } else { $Name.ToLowerInvariant() }

    if ([string]::IsNullOrWhiteSpace($wanted)) {
        throw "games.json declares no `"default`", so a game has to be named: ./gpb capture <game>"
    }

    $game = $everything.Games | Where-Object { $_.Id -eq $wanted } | Select-Object -First 1
    if (-not $game) {
        $known = (Get-GpbGameNames $RepoRoot) -join ', '
        throw "games.json does not declare '$wanted'. Known games: $known"
    }
    return $game
}

<#
.SYNOPSIS
    Every declared game except this one - whose addresses this game's profile must never cover.
#>
function Get-GpbOtherGames {
    param(
        [string]$RepoRoot,
        [string]$Id
    )
    $self = "$Id".ToLowerInvariant()
    return @((Get-GpbAllGames $RepoRoot).Games | Where-Object { $_.Id -ne $self })
}

<#
.SYNOPSIS
    True when this game has enough declared for the profile builder to produce anything honest.

.DESCRIPTION
    The builder keeps an observed address only when it falls inside a published range: an AWS or
    Azure region, AWS Global Accelerator, or a prefix one of the game's own ASNs announces. With
    none of those declared every address is unverified, and the run ends with a profile containing
    no ranges at all - which is not an empty result, it is a wrong one: it looks like a finished
    profile and would ship as such.

    So this is checked before the builder runs rather than after, and the caller refuses. See the
    cs2 note in games.json for the case this exists for.
#>
function Test-GpbGameBuildable {
    param([PSCustomObject]$Game)
    $sources = @($Game.AwsRegions).Count + @($Game.AzureRegions).Count + @($Game.Asns).Count
    if ($Game.GlobalAccelerator) { $sources++ }
    return $sources -gt 0
}
