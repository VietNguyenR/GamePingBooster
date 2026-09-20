namespace GamePingBooster.BlockCheck;

internal enum TargetKind
{
    /// <summary>Not blocked anywhere. If this one fails, the line or the tool is at fault, not the censor.</summary>
    Control,

    /// <summary>Login, store, community, friends. Tiny traffic - the part worth tunnelling if it comes to that.</summary>
    SteamControl,

    /// <summary>Game downloads. Must stay on the ISP's own path at full speed, so it is measured to prove it is clean.</summary>
    SteamContent,
}

internal sealed record Target(string Host, TargetKind Kind, string Why);

/// <summary>
/// What gets probed, and why each name is on the list.
///
/// The split between <see cref="TargetKind.SteamControl"/> and <see cref="TargetKind.SteamContent"/>
/// is the whole design question in one list. If the content names turn out to be clean - which is
/// what a player reporting "Steam works and downloads are still fast under a DNS-only fix" implies -
/// then they must never be routed through a relay, and this tool is the evidence for that decision.
/// </summary>
internal static class Targets
{
    public static readonly Target[] All =
    [
        new("www.microsoft.com", TargetKind.Control,
            "nobody blocks it; proves the line and this tool are working"),

        new("steamcommunity.com", TargetKind.SteamControl,
            "the name players actually report as blocked"),
        new("store.steampowered.com", TargetKind.SteamControl,
            "the store, in the browser and inside the client's CEF"),
        new("api.steampowered.com", TargetKind.SteamControl,
            "WebAPI - the client asks it for the CM list before it can log in"),
        new("login.steampowered.com", TargetKind.SteamControl,
            "sign-in itself"),
        new("help.steampowered.com", TargetKind.SteamControl,
            "support; same infrastructure, useful as a second sample of the same path"),

        // Three CDNs rather than three names on one CDN. The first run had two Akamai names and a
        // fourth that does not exist at all - every resolver answered NXDOMAIN, which the tool read
        // as a failure of the content path and reported downloads as unclean. A target list is an
        // assumption like any other and this one was wrong.
        new("cdn.cloudflare.steamstatic.com", TargetKind.SteamContent,
            "content delivery, Cloudflare-fronted - must stay direct"),
        new("cdn.steamstatic.com", TargetKind.SteamContent,
            "content delivery, Fastly - a second CDN, in case only one is filtered"),
        new("client-update.akamai.steamstatic.com", TargetKind.SteamContent,
            "client self-update and depot content over Akamai"),
    ];

    public static string Label(TargetKind kind) => kind switch
    {
        TargetKind.Control => "control",
        TargetKind.SteamControl => "steam control plane",
        TargetKind.SteamContent => "steam content",
        _ => kind.ToString(),
    };
}
