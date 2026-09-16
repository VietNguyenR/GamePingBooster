namespace GamePingBooster.Core.Profiles;

/// <summary>
/// Combines separately stored game profiles into the one bundle the tunnel runs on, and finds which
/// game a running process belongs to.
///
/// Each game has a profile of its own - the licence server serves and seals them one game at a time,
/// and the self-hosted build ships one file per game - but the client has no game selector: it
/// accelerates whichever game is open. So the service loads every profile it has and merges them.
/// </summary>
public static class ProfileMerge
{
    /// <summary>
    /// Merges <paramref name="bundles"/>, the first of which is the PRIMARY.
    ///
    /// The relay list is the primary's alone, not a union. Every profile names relays, and a union
    /// would keep a relay alive in the client for as long as any older profile still mentioned it -
    /// a relay removed on the server would go on being measured and failed over to. The primary is
    /// the newest profile from the licence server, or the configured file on a self-hosted install.
    ///
    /// Games are taken in order and the first copy of an id wins, for the same reason: callers pass
    /// the newest first.
    ///
    /// The primary is the newest profile that NAMES A RELAY. A profile with none - one sealed locally
    /// by push-profile to test a game, which is how VALORANT was tried on 2026-09-15 - is newer than the
    /// real ones the moment it is written, and taking its empty list would leave the service with
    /// nothing to connect to: "The profile declares no relays", on a machine whose other profiles
    /// list three.
    /// </summary>
    public static ProfileBundle Merge(IReadOnlyList<ProfileBundle> bundles)
    {
        if (bundles.Count == 0) throw new ArgumentException("There is no profile to merge.", nameof(bundles));

        var primary = bundles.FirstOrDefault(b => b.Relays.Count > 0) ?? bundles[0];
        var merged = new ProfileBundle
        {
            SchemaVersion = bundles.Max(b => b.SchemaVersion),
            GeneratedUtc = bundles.Max(b => b.GeneratedUtc),
            Relays = primary.Relays,
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in bundles)
        {
            foreach (var game in bundle.Games)
            {
                if (seen.Add(game.Id)) merged.Games.Add(game);
            }
        }
        return merged;
    }

    /// <summary>
    /// The game whose process names include <paramref name="processName"/>, or null.
    ///
    /// Compared without ".exe" and without regard to case, because profiles have always written it
    /// both ways ("TslGame.exe", "cs2") and the process list reports neither.
    /// </summary>
    public static GameEntry? FindByProcess(IEnumerable<GameEntry> games, string processName)
    {
        var wanted = StripExe(processName);
        return games.FirstOrDefault(game =>
            game.ProcessNames.Any(name => StripExe(name).Equals(wanted, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>A process name as Process.GetProcessesByName wants it: without ".exe".</summary>
    public static string StripExe(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }
}
