namespace GamePingBooster.Core.Paths;

/// <summary>What multi-tunnel does for one game on one connection. See docs/MULTI-TUNNEL.md, sections 8-9.</summary>
public enum RegionRoutingMode
{
    /// <summary>One tunnel carries every region, and nothing is measured for it - the client before multi-tunnel.</summary>
    Off,

    /// <summary>Every region is measured in the lobby and the plan is written down; no second tunnel is opened.</summary>
    Record,

    /// <summary>Each region leaves by its planned path.</summary>
    On,
}

/// <summary>
/// Where a connection's region-routing mode comes from, in the order entry switching uses: a value typed
/// into config.json wins - that is how one machine tries a mode before everybody - then the game's own
/// setting from the profile.
///
/// Two differences from entry switching, both towards doing less:
///
///   - Neither says anything: <see cref="RegionRoutingMode.Off"/>. A profile without the field comes from a
///     server older than multi-tunnel, and nobody decided anything for that game.
///   - A game whose landmarks are routed on purpose (<see cref="Profiles.GameEntry.LandmarksRouted"/>, CS2) is
///     off whatever either says. Its landmarks are inside its own routes, so neither the player's own line nor
///     another relay can be measured to them honestly, and a plan made from those numbers would be noise.
///
/// A value in config.json that means nothing - a typo - is "record": whoever typed something meant to decide
/// this machine, and a typo must neither switch measuring off nor switch routing on.
/// </summary>
public static class RegionRouting
{
    public static RegionRoutingMode? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" => RegionRoutingMode.Off,
        "record" => RegionRoutingMode.Record,
        "on" => RegionRoutingMode.On,
        _ => null,
    };

    /// <summary>The mode for a connection, and where it came from - for the log line that says so.</summary>
    public static (RegionRoutingMode Mode, string Source) Resolve(string? local, string? game, bool landmarksRouted)
    {
        if (landmarksRouted) return (RegionRoutingMode.Off, "the game's landmarks are routed");
        if (!string.IsNullOrWhiteSpace(local)) return (Parse(local) ?? RegionRoutingMode.Record, "config.json");
        if (Parse(game) is { } fromGame) return (fromGame, "the game's setting");
        return (RegionRoutingMode.Off, "the default");
    }

    /// <summary>Tunnels open at once in <see cref="RegionRoutingMode.On"/>, home included. docs/MULTI-TUNNEL.md, section 4.</summary>
    public const int MaxTunnels = 3;
}
