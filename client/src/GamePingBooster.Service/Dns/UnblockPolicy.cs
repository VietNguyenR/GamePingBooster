using GamePingBooster.Core.Profiles;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// One service the resolver answers for: which suffixes it claims, which names under them it must
/// not, and the name it proves itself with.
/// </summary>
internal sealed record UnblockApp(
    string Id,
    string Name,
    IReadOnlyList<string> Scope,
    IReadOnlyList<string> Excluded,
    string Canary,
    IReadOnlyList<string>? Tunnel = null,
    IReadOnlyList<string>? Proxied = null,
    IReadOnlyList<string>? Downloads = null)
{
    /// <summary>
    /// Whether the name's connections go to the profile's lobby proxies first: on the tunnel list AND on the proxied
    /// list. Only meaningful with proxies in the policy - see SplitProxy.
    /// </summary>
    public bool ViaLobbyProxy(string name) =>
        RoutesThroughTunnel(name) && (Proxied ?? []).Any(suffix => Matches(name, suffix));

    /// <summary>
    /// Whether <paramref name="name"/> is in the profile's tunnel list: claimed, and under one of its tunnel names.
    /// See ProfileUnblock.Tunnel.
    ///
    /// A listed name goes through the tunnel whenever one is up, on every line. 0.3.8 alone tried the line first for
    /// these too, and that broke PUBG's lobby on FPT in Ha Noi (2026-10-03) - see LocalResolver.AnswerAsync. Which
    /// is why the list must hold small names only: prod-live-images, listed for FPT in Ho Chi Minh City on 2026-10-02,
    /// rode the relay for Viettel and VNPT players too until it was taken off.
    /// </summary>
    public bool RoutesThroughTunnel(string name) =>
        Claims(name) && (Tunnel ?? []).Any(suffix => Matches(name, suffix));

    /// <summary>
    /// A Steam name the line cuts by name, to be carried over the line with the hello split. Download hosts too, which
    /// never go through the tunnel: on 2026-10-09 Steam's own content log showed every manifest request to
    /// cache*-sgp1.steamcontent.com (HTTPS, 443) closed at once - "No Connection", 0 bytes - because the line resets
    /// a handshake naming them, and an update could not start. A split hello is the one thing that gets past that, and
    /// the bytes after it travel the line, not the relay's cap. Not the profile's listed names (those always had the
    /// proxy), and not the connection servers (<c>steamserver.net</c>: their ports and handshakes are not the web's,
    /// and the split was never measured on them).
    ///
    /// Web names too, for a second reason: they sit on shared Fastly and Akamai addresses. A /32 routed into the
    /// tunnel for shared.steamstatic.com was also the address of fastly.cdn.steampipe.steamcontent.com, so the Steam
    /// window loaded at 60 KB/s through the relay's cap. A name sent over the line split needs no route at all.
    /// </summary>
    public bool SplitsBeforeTunnel(string name) =>
        Id == "steam" && Claims(name) && !RoutesThroughTunnel(name) && !Matches(name, "steamserver.net");

    /// <summary>
    /// Download hosts, never sent through the tunnel unless a profile names them itself. A relay caps a session at
    /// 256 KB/s each way, shared with the game's own packets, so a game update riding it would be slow AND drop the
    /// match's packets while it ran - and Steam's downloads come from caches inside Vietnam anyway (10 ms against
    /// 30-43 abroad, 2026-09-20), so a line that cut them would need a different fix, not a relay.
    /// </summary>
    internal static readonly string[] DefaultDownloadHosts =
    [
        "steamcontent.com", "steampipe.akamaized.net", "steamcdn-a.akamaihd.net", "steamusercontent.com",
        "client-update.akamai.steamstatic.com", "client-update.fastly.steamstatic.com", "client-update.steamstatic.com",
    ];

    /// <summary>
    /// A download host: its bytes never ride the relay, whatever else fails (SplitProxy resets the connection instead,
    /// and the game's client moves to another content server).
    /// </summary>
    public bool IsDownloadHost(string name) =>
        Claims(name) && !RoutesThroughTunnel(name) && DownloadHostList.Any(host => Matches(name, host));

    /// <summary>
    /// The profile's download list, or the built-in one when the profile does not carry the field (a server older than
    /// it). A delivered empty list means none: switching the rule off stays a server-side edit.
    /// </summary>
    private IReadOnlyList<string> DownloadHostList => Downloads ?? DefaultDownloadHosts;

    /// <summary>
    /// Whether the resolver may send <paramref name="name"/> through the tunnel once the line is seen cutting it by
    /// name (WorkingEdges.CutByName): listed by the profile, or claimed and not a download host.
    ///
    /// Why unlisted names too: on 2026-10-02 FPT in Ho Chi Minh City cut prod-live-cfentry and prod-live-images, which
    /// FPT in Ha Noi had let through that afternoon, and PUBG could not connect until someone added them to the
    /// profile's tunnel list by hand on the server. The line differs by city and by hour; a list kept by a person
    /// is always behind it. A listed download host is allowed: the profile named it, which is a decision.
    /// </summary>
    public bool MayTunnelWhenCut(string name) =>
        RoutesThroughTunnel(name) || (Claims(name) && !DownloadHostList.Any(host => Matches(name, host)));

    /// <summary>The namespaces the Windows policy is given. A leading dot is what NRPT expects for a suffix.</summary>
    public IReadOnlyList<string> Namespaces => [.. Scope.Select(s => "." + s)];

    /// <summary>
    /// True when this service should answer the name over encrypted DNS rather than pass it to the
    /// ISP. Expected lower-cased and without a trailing dot, as DnsWire.TryReadQuestion produces it.
    /// </summary>
    public bool Claims(string name)
    {
        foreach (var excluded in Excluded)
        {
            if (Matches(name, excluded)) return false;
        }

        foreach (var suffix in Scope)
        {
            if (Matches(name, suffix)) return true;
        }

        return false;
    }

    private static bool Matches(string name, string suffix) =>
        name.Equals(suffix, StringComparison.Ordinal) ||
        (name.Length > suffix.Length &&
         name[name.Length - suffix.Length - 1] == '.' &&
         name.EndsWith(suffix, StringComparison.Ordinal));
}

/// <summary>
/// Every service this machine currently unblocks, and where the list came from.
///
/// It comes from the profile the licence server delivers. That is the whole point of this type:
/// the list used to be an array compiled into the service, so a newly blocked name meant a client
/// release - and the people who need the fix are running whatever build they installed months ago.
/// Now adding a service, or a name to one, is a row in a database and reaches every machine on its
/// next profile fetch.
///
/// <see cref="Builtin"/> is the fallback and not the source of truth, and since 2026-10-02 only a
/// self-hosted installation - one with no licence server at all - gets it. A licensed one gets
/// exactly what its profile says, nothing when it has none: the list is part of what a licence buys,
/// and a compiled-in Steam list meant installing the app, never signing in, and having the Steam fix
/// anyway. A profile that DOES carry a list replaces it entirely, including with an empty one, so
/// switching the feature off for everybody stays a server-side edit.
/// </summary>
internal sealed record UnblockPolicy(IReadOnlyList<UnblockApp> Apps, string Source,
    IReadOnlyList<LobbyProxyEntry>? LobbyProxies = null)
{
    public bool IsEmpty => Apps.Count == 0;

    /// <summary>
    /// Steam, as measured on VNPT on 2026-09-20 and 2026-09-22.
    ///
    /// The exclusions are the part to read twice. media.steampowered.com sits under a claimed
    /// suffix and is content: it resolves to an in-country cache that connects in 10 ms, so
    /// claiming it would replace the fastest answer with a slower one and call that a fix.
    /// </summary>
    public static UnblockPolicy Builtin { get; } = new(
    [
        new UnblockApp(
            "steam",
            "Steam",
            ["steamcommunity.com", "steampowered.com", "steamgames.com", "steam-chat.com", "valvesoftware.com"],
            ["media.steampowered.com", "steamcontent.com", "steamstatic.com"],
            "steamcommunity.com"),
    ], "built in");

    public static UnblockPolicy Empty { get; } = new([], "none");

    /// <summary>
    /// Reads the list out of a delivered profile, dropping anything unusable rather than failing.
    ///
    /// A malformed row must not take the feature down for the rows that are fine: this data is
    /// edited on a website by a person, and the failure mode of strictness here is every player
    /// losing the fix because somebody left a field blank.
    /// </summary>
    /// <param name="allowBuiltin">True only for a self-hosted installation - see the type's summary.</param>
    public static UnblockPolicy FromProfile(ProfileBundle? profile, Action<string> log, bool allowBuiltin)
    {
        if (profile is null || profile.Unblock.Count == 0) return allowBuiltin ? Builtin : Empty;

        var apps = new List<UnblockApp>();

        foreach (var entry in profile.Unblock)
        {
            var scope = Clean(entry.Scope);
            if (scope.Count == 0)
            {
                log($"Unblock: '{entry.Id}' claims no names - ignoring it.");
                continue;
            }

            var canary = Normalise(entry.Canary);
            if (canary.Length == 0)
            {
                // Refused rather than defaulted to the first scoped name. The canary is what proves
                // the fix works on this line, and one picked by this code would be a name nobody
                // checked is actually poisoned - a verification that always passes.
                log($"Unblock: '{entry.Id}' declares no canary - ignoring it, because it could not be verified.");
                continue;
            }

            apps.Add(new UnblockApp(
                entry.Id.Trim(),
                string.IsNullOrWhiteSpace(entry.Name) ? entry.Id.Trim() : entry.Name.Trim(),
                scope,
                Clean(entry.Excluded),
                canary,
                Clean(entry.Tunnel),
                Clean(entry.Proxied),
                entry.Downloads is null ? null : Clean(entry.Downloads)));
        }

        return apps.Count == 0
            ? Empty
            : new UnblockPolicy(apps, $"the profile ({apps.Count} service(s))",
                [.. profile.LobbyProxies.Where(p => !string.IsNullOrWhiteSpace(p.Host) && p.Port is > 0 and < 65536)]);
    }

    /// <summary>Which service claims this name, or null. First match wins; the lists should not overlap.</summary>
    public UnblockApp? ClaimedBy(string name)
    {
        foreach (var app in Apps)
        {
            if (app.Claims(name)) return app;
        }

        return null;
    }

    public IReadOnlyList<string> AllNamespaces => [.. Apps.SelectMany(a => a.Namespaces).Distinct()];

    public string Describe() => Apps.Count == 0 ? "nothing" : string.Join(", ", Apps.Select(a => a.Name));

    private static List<string> Clean(IEnumerable<string> names) =>
        [.. names.Select(Normalise).Where(n => n.Length > 0).Distinct()];

    /// <summary>
    /// Lower-cased, trimmed, and with a leading dot or trailing dot removed.
    ///
    /// Both are things a person types into a form - ".steamcommunity.com" is how the Windows policy
    /// writes a suffix, so it is the natural thing to paste - and either would stop the name ever
    /// matching, silently.
    /// </summary>
    private static string Normalise(string? name)
    {
        var text = (name ?? "").Trim().Trim('.').ToLowerInvariant();
        return text;
    }
}
