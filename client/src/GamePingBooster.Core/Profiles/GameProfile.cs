using System.Text.Json.Serialization;

namespace GamePingBooster.Core.Profiles;

/// <summary>
/// The profile is the data that decides which IP ranges get routed, for which game, through
/// which relay. It is fetched from a server (profiles/pubg-vn.json) rather than hard-coded, so
/// when PUBG changes IP ranges only a JSON file needs updating - no new client release.
/// </summary>
public sealed class ProfileBundle
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;

    /// <summary>When the profile was generated, so the UI can show "updated N days ago".</summary>
    [JsonPropertyName("generatedUtc")] public DateTimeOffset GeneratedUtc { get; set; }

    [JsonPropertyName("games")] public List<GameEntry> Games { get; set; } = [];
    [JsonPropertyName("relays")] public List<RelayEntry> Relays { get; set; } = [];

    /// <summary>
    /// Services whose names this machine should answer over encrypted DNS because the line lies
    /// about them - UnblockApp on the server, under exactly this name.
    ///
    /// NOT per game, and kept from one bundle like <see cref="Relays"/>: a blocked store is blocked
    /// whether or not a game is running, and every game's profile carries the same list.
    ///
    /// ALWAYS present, empty meaning there is nothing to unblock - which is also what a client
    /// reads from a server that predates the field, and what a newer server sends to a machine
    /// entitled to nothing. So the feature turning off for everyone is a server-side edit, with no
    /// client release and no way for an old client to misread it.
    /// </summary>
    [JsonPropertyName("unblock")] public List<UnblockEntry> Unblock { get; set; } = [];
}

/// <summary>
/// One service the resolver claims a few names for. See GamePingBooster.Service.Dns.UnblockPolicy.
///
/// Delivered rather than compiled in, because the alternative is a client release every time an
/// ISP adds a name to its list - and the people who need the fix are running whatever build they
/// installed months ago.
/// </summary>
public sealed class UnblockEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>Shown to the player, so it is the service's own name: "Steam", not "steam".</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>
    /// Domain suffixes this service owns, without a leading dot. Matched as suffixes, so
    /// "steampowered.com" covers store, api and login under it.
    /// </summary>
    [JsonPropertyName("scope")] public List<string> Scope { get; set; } = [];

    /// <summary>
    /// Names under a claimed suffix that must NOT be claimed - the download and media paths.
    ///
    /// This is the field that keeps the feature from making things worse. Steam's content CDNs
    /// resolve to caches inside the country that connect in 10-20 ms, against 30-43 ms for the
    /// addresses a foreign resolver hands out, so for those names the ISP's answer is the better
    /// one. Every service has to declare its own: it was measured for Steam, not deduced, and
    /// nothing about it generalises.
    /// </summary>
    [JsonPropertyName("excluded")] public List<string> Excluded { get; set; } = [];

    /// <summary>
    /// A name this service is known to have lied about, used to prove the fix took effect.
    ///
    /// Per service and not one for the whole feature: a line that poisons Discord and leaves Steam
    /// alone would verify against a Steam canary, see an honest answer, and report success without
    /// having tested anything.
    /// </summary>
    [JsonPropertyName("canary")] public string Canary { get; set; } = "";
}

public sealed class GameEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>
    /// Process names used to detect that the game is running. For PUBG that is TslGame.exe
    /// (the actual game process) plus PUBG.exe / TslGame_BE.exe (launcher, BattlEye shim).
    /// This only enumerates processes the way Task Manager does - it never opens the game,
    /// reads its memory, or touches its files.
    /// </summary>
    [JsonPropertyName("processNames")] public List<string> ProcessNames { get; set; } = [];

    /// <summary>Server regions for the game; the player picks one (or leaves it on automatic).</summary>
    [JsonPropertyName("regions")] public List<RegionEntry> Regions { get; set; } = [];

    /// <summary>
    /// Addresses of the game's lobby, routed through the tunnel from the moment it connects -
    /// NOT only while the game is running, which is the one way these differ from every range in
    /// <see cref="RegionEntry.Cidrs"/>.
    ///
    /// Why they cannot wait for the game like the rest: the lobby connection is TCP and the game
    /// opens it in its first seconds, while the game routes go in only after the process watcher
    /// (a two-second poll) has noticed it. A connection opened on the normal path and then caught
    /// by a route mid-flight keeps its original source address, and the relay drops every packet
    /// whose inner source is not the address it assigned - so a late route does not accelerate the
    /// lobby, it hangs it until the game reconnects. Gameplay never meets this: a match starts long
    /// after the routes are in.
    ///
    /// Single addresses only, as "a.b.c.d" or "a.b.c.d/32". Anything wider is refused by
    /// <see cref="LobbyRoutes"/>, because these stay routed while the game is closed and a range
    /// would pull other programs' traffic through the relay all day. For PUBG these are the static
    /// anycast pair of its AWS Global Accelerator, which belong to that accelerator alone.
    ///
    /// Absent from a profile means no lobby routes, and an older client simply ignores the field.
    /// The licence server does not send it yet: its profile is assembled from the database, not
    /// from this file (web-service/app/lib/profile.server.ts).
    /// </summary>
    [JsonPropertyName("lobbyAddresses")] public List<string> LobbyAddresses { get; set; } = [];

    /// <summary>
    /// True when this game's landmarks sit inside its own routed ranges ON PURPOSE.
    ///
    /// Counter-Strike 2 carries gameplay over Steam Datagram Relay: the game talks UDP to a Valve
    /// relay, and the latency probe it sends before a match goes to the SAME address and port the
    /// match then uses - 103.28.54.179:27050 was a 12-packet probe in one capture on 2026-09-13 and
    /// carried a 7,020-packet match in the next. A route cannot tell the two apart, so the rule that
    /// keeps PUBG's probes on the player's own connection cannot hold. What holds instead is routing
    /// every relay of every PoP the player may enter, so probe and match always take the same path -
    /// and then the relay is its own landmark: it answers ICMP, it is where the game's packets go,
    /// and it is inside a routed /32 by design.
    ///
    /// All it changes on this side is that the routed-landmark warning stays quiet for such a game.
    /// Absent means false, which is right for every profile written before the field existed.
    /// </summary>
    [JsonPropertyName("landmarksRouted")] public bool LandmarksRouted { get; set; }

    /// <summary>
    /// Packets a second to one address that make it a game server, for THIS game. Absent means the
    /// built-in default (GameDestinationRecorder.NewServerPackets, 500 in a 30 s window - 16.7 a
    /// second), which is what every profile written before this field says and what every game but
    /// World of Tanks still wants.
    ///
    /// Why it cannot be one number for every game. The default was measured on PUBG, whose match
    /// runs 40-60 packets a second, and it suits Apex (140), CS2 (57-64) and VALORANT (41-43) with
    /// room to spare. World of Tanks runs 9-16, measured through the tunnel on 2026-09-22: its
    /// busiest 30-second window in a whole match was 457 packets against a threshold of 500, so a
    /// server it plays on can never be discovered. It is the first game whose match is quieter than
    /// PUBG's lobby noise threshold, and hard-coding a lower default for everyone would let Apex's
    /// datacentre measurements - 25-60 a second, from eleven regions at once - be reported as
    /// servers, which is the mistake of 2026-09-20 that cost a wrongly routed Hong Kong range.
    ///
    /// The one thing that makes a low value safe is that it is DECLARED per game by somebody who
    /// measured that game, never inferred. The recorder clamps whatever arrives to
    /// GameDestinationRecorder.MinRatePerSecond..MaxRatePerSecond, because a profile is data from
    /// the network and a zero here would make every address the game touches a new server.
    ///
    /// Read with the game, so it survives ProfileMerge like the rest of the entry. The licence
    /// server sends it from the live Game row, not from a published profile version, so changing
    /// it reaches players on their next Connect.
    ///
    /// Use <see cref="DiscoveryRate"/> rather than this property to decide anything: what arrives
    /// here is delivered data and has not been checked yet.
    /// </summary>
    [JsonPropertyName("discoveryPacketsPerSecond")] public double? DiscoveryPacketsPerSecond { get; set; }

    /// <summary>
    /// Multi-tunnel for this game: "off", "record" or "on" - see <see cref="Paths.RegionRouting"/>, which
    /// is what decides; this is only the game's say. Absent means off, which is what every profile written
    /// before the field says. Read at connect, never mid-match. docs/MULTI-TUNNEL.md, section 7.1.
    /// </summary>
    [JsonPropertyName("regionRouting")] public string? RegionRouting { get; set; }

    /// <summary>
    /// Whether a region of this game may leave over the player's own line when that measures faster than
    /// every relay (the planner's rule 5, G8). Absent means no.
    /// </summary>
    [JsonPropertyName("regionDirect")] public bool RegionDirect { get; set; }

    /// <summary>
    /// The narrowest and widest rate a profile may declare, in packets a second.
    ///
    /// The floor is what makes a delivered number safe to act on. A profile arrives over the network,
    /// and a zero - a forgotten field, a bad edit in the dashboard, a server bug - would make every
    /// address the game touches a new server and send the player's whole destination list to the
    /// licence server. 3 a second is twice the loudest thing in any capture that was not a match
    /// (85.236.96.130, 1.5 a second, PUBG's lobby), so nothing below it has ever been worth
    /// reporting. A game genuinely quieter than that needs somebody to measure it and change this
    /// line, which is the right gate: the floor is a safety limit, and those tighten in the client,
    /// never in delivered data.
    ///
    /// The ceiling only keeps a fat-fingered value from switching discovery off silently.
    /// </summary>
    public const double MinDiscoveryRate = 3.0;

    public const double MaxDiscoveryRate = 500.0;

    /// <summary>
    /// <see cref="DiscoveryPacketsPerSecond"/> brought inside
    /// <see cref="MinDiscoveryRate"/>..<see cref="MaxDiscoveryRate"/>, or null when the game declares
    /// no rate and the recorder's own default applies.
    ///
    /// Clamped rather than refused: a value outside the bounds is a mistake somewhere upstream, and
    /// the nearest safe number goes on discovering servers while a refusal would quietly switch the
    /// game's discovery off - the failure that takes a month to notice.
    /// </summary>
    [JsonIgnore]
    public double? DiscoveryRate => DiscoveryPacketsPerSecond is { } rate && double.IsFinite(rate)
        ? Math.Clamp(rate, MinDiscoveryRate, MaxDiscoveryRate)
        : null;
}

public sealed class RegionEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>
    /// Destination ranges routed through the tunnel, in CIDR form. This list must stay NARROW -
    /// see docs/pubg-ip-ranges.md: routing all of AWS ap-southeast-1 would drag thousands of
    /// unrelated services through the relay.
    /// </summary>
    [JsonPropertyName("cidrs")] public List<string> Cidrs { get; set; } = [];

    /// <summary>
    /// Stable addresses that stand in for this region when its latency has to be measured.
    ///
    /// For PUBG these are the endpoints the game itself probes on UDP 8081 to choose a
    /// datacentre. They are the right stand-in for three reasons:
    /// they are inside the region, they recur across sessions (gameplay servers do not), and
    /// they answer ICMP, so measuring them needs no cooperation from anyone.
    ///
    /// Deliberately NOT covered by <see cref="Cidrs"/>. A landmark that went through the tunnel
    /// would make the game measure some regions through the relay and the rest over the player's
    /// own connection, which is comparing two different things - the mistake that put a tester on
    /// a relay 30 ms further from his game server than the alternative.
    ///
    /// Empty means this region cannot be measured, and relay comparison falls back to the first
    /// leg alone. Nothing breaks; the choice is just less informed.
    /// </summary>
    [JsonPropertyName("landmarks")] public List<string> Landmarks { get; set; } = [];

    /// <summary>Where the ranges came from (aws:ap-southeast-1 / azure:southeastasia / capture), for auditing.</summary>
    [JsonPropertyName("source")] public string? Source { get; set; }

    /// <summary>Free-form note, e.g. "confirmed by capture on 2026-08-20".</summary>
    [JsonPropertyName("note")] public string? Note { get; set; }
}

public sealed class RelayEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>In "ip:port" form, e.g. "203.0.113.10:51820".</summary>
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";

    /// <summary>Display location for the UI, e.g. "Singapore".</summary>
    [JsonPropertyName("location")] public string? Location { get; set; }

    /// <summary>
    /// The relay's own public key, 65 bytes as hex. Null for a self-hosted relay.
    ///
    /// Only token mode needs it, and token mode cannot work without it. Under PSK both ends hold
    /// the same secret, so a forged answer is impossible by construction; with per-client tokens
    /// there is no shared secret left, so the relay signs its answer with a key of its own and
    /// this is how the client learns which key to expect. Without it a client would accept any
    /// well-formed reply from anywhere - see TryParseHandshakeRespToken.
    ///
    /// Absent means "this relay speaks PSK", which is exactly right for the endpoints a
    /// self-hoster types into the settings screen: those are turned into entries with no key,
    /// and they must keep working untouched.
    /// </summary>
    [JsonPropertyName("publicKey")] public string? PublicKey { get; set; }

    /// <summary>
    /// Forwarders in front of this relay: the same relay reached by another road, for lines whose
    /// own route to it is the problem. See <see cref="RelayPaths"/>, which turns each into a path the
    /// tunnel measures like a relay, and TunnelEngine.SelectRelayAsync for when it bothers.
    ///
    /// Absent from older profiles and from self-hosted relays, which is an empty list. A client older
    /// than the field ignores it and measures exactly the relays it always did.
    /// </summary>
    [JsonPropertyName("entries")] public List<RelayEntryPoint> Entries { get; set; } = [];

    /// <summary>
    /// "off", "record" or "on": what entry switching does on a connection to this relay, as the operator set
    /// it in /admin/relays. Absent from older profiles and self-hosted relays, which is "record" - see
    /// <see cref="EntrySwitching"/>, which also says why config.json can override it.
    /// </summary>
    [JsonPropertyName("entrySwitching")] public string? EntrySwitching { get; set; }

    /// <summary>
    /// The games this relay may carry, by game id, as set in /admin/relays - empty for every game. PUBG's
    /// ranges are Singapore's alone, so a Hong Kong relay only adds a leg to it: the operator takes PUBG
    /// off that relay and the client never measures, chooses, fails over to or offers it for PUBG.
    ///
    /// Absent from older profiles and from self-hosted relays, which is every game. A client older than the
    /// field ignores it. See <see cref="RelayPaths.Serves"/>.
    /// </summary>
    [JsonPropertyName("games")] public List<string> Games { get; set; } = [];

    /// <summary>
    /// Games this relay may carry ONE REGION of through a second tunnel, and nothing else: region routing
    /// (docs/MULTI-TUNNEL.md) may measure it and open a tunnel to it for a region, but it is never home, never
    /// failed over to and never offered in the relay list for these games - <see cref="Games"/> decides all of
    /// that. For a relay that is fast to some of a game's regions and wrong for the rest: Hong Kong for Delta
    /// Force, whose Ho Chi Minh City matches must not leave through Hong Kong. A game listed in both is simply
    /// carried. Absent from older profiles; a client older than the field ignores it. See
    /// <see cref="RelayPaths.SecondaryOnlyFor"/>.
    /// </summary>
    [JsonPropertyName("secondaryGames")] public List<string> SecondaryGames { get; set; } = [];

    /// <summary>
    /// Set only on a path <see cref="RelayPaths.Expand"/> made from an entry: the id of the relay
    /// behind it. Never read from a profile and never written to one.
    /// </summary>
    [JsonIgnore] public string? ViaRelayId { get; set; }
}

/// <summary>
/// One forwarder in front of a relay, as the licence server serves it. It has no key and no name of
/// its own - a forwarder signs nothing, so the client checks the relay's signature through it.
/// </summary>
public sealed class RelayEntryPoint
{
    /// <summary>Unique across relays and entries alike: the log names paths by it, and defaultRelayId can pin one.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>Where the forwarder is, for people, e.g. "Vietnam (vHost HCM)".</summary>
    [JsonPropertyName("location")] public string? Location { get; set; }

    /// <summary>In "ip:port" form, like a relay's.</summary>
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProfileBundle))]
public partial class ProfileJsonContext : JsonSerializerContext;
