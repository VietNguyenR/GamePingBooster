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
/// One difference from entry switching, towards doing less: neither says anything is
/// <see cref="RegionRoutingMode.Off"/>. A profile without the field comes from a server older than multi-tunnel,
/// and nobody decided anything for that game.
///
/// A game whose landmarks are routed on purpose (<see cref="Profiles.GameEntry.LandmarksRouted"/>, CS2) follows
/// the same order. Until 2026-10-01 it was off whatever either said, on the belief that no path to a routed
/// landmark could be measured honestly. That holds for the player's own line only: an echo through home or
/// through another relay is carried inside the tunnel and leaves from that relay, whatever this PC's routes say,
/// so the relay-to-relay comparison is as honest as for any other game. Direct is what cannot be measured, and
/// <see cref="DirectAllowed"/> keeps it out. docs/MULTI-TUNNEL.md 5.6.
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
    public static (RegionRoutingMode Mode, string Source) Resolve(string? local, string? game)
    {
        if (!string.IsNullOrWhiteSpace(local)) return (Parse(local) ?? RegionRoutingMode.Record, "config.json");
        if (Parse(game) is { } fromGame) return (fromGame, "the game's setting");
        return (RegionRoutingMode.Off, "the default");
    }

    /// <summary>
    /// Whether the planner may leave a region of this game unrouted (planner rule 5, G8). Never for a game whose
    /// landmarks are routed: a "direct" echo to one would fall into the tunnel. The measuring pass already records
    /// no direct number for such a landmark; this keeps a served regionDirect from ever mattering on top.
    /// </summary>
    public static bool DirectAllowed(Profiles.GameEntry game) => game.RegionDirect && !game.LandmarksRouted;

    /// <summary>
    /// Tunnels open at once in <see cref="RegionRoutingMode.On"/>, home included. docs/MULTI-TUNNEL.md, section 4. Four since
    /// 2026-10-02 (three before): with rule 4b a game such as Naraka wants a relay inside each of its regions - Ho Chi Minh
    /// City and Singapore - and a third region then had no room. Each one is a session on that relay.
    /// </summary>
    public const int MaxTunnels = 4;
}
