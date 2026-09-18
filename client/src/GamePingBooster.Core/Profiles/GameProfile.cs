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
