using System.Text.Json.Serialization;

namespace GamePingBooster.Core.Profiles;

/// <summary>
/// The profile is the data that decides which IP ranges get routed, for which game, through
/// which relay. It is fetched from a server or loaded locally (profiles/*.json) rather than hard-coded, so
/// when games change IP ranges only a JSON file needs updating - no new client release.
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
    /// Process names used to detect that the game is running. Examples: cs2.exe for CS2;
    /// TslGame.exe plus PUBG.exe / TslGame_BE.exe for PUBG.
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
    /// would pull other programs' traffic through the relay all day.
    ///
    /// Absent from a profile means no lobby routes, and an older client simply ignores the field.
    /// The licence server does not send it yet: its profile is assembled from the database, not
    /// from this file (web-service/app/lib/profile.server.ts).
    /// </summary>
    [JsonPropertyName("lobbyAddresses")] public List<string> LobbyAddresses { get; set; } = [];
}

public sealed class RegionEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>
    /// Destination ranges routed through the tunnel, in CIDR form. This list must stay NARROW -
    /// routing entire cloud provider regions would drag thousands of unrelated services through the relay.
    /// </summary>
    [JsonPropertyName("cidrs")] public List<string> Cidrs { get; set; } = [];

    /// <summary>
    /// Stable addresses that stand in for this region when its latency has to be measured.
    ///
    /// These are endpoints the game or coordinator probes to choose a datacentre.
    /// They are the right stand-in for three reasons: they are inside the region, they recur
    /// across sessions (gameplay servers do not), and they answer ICMP, so measuring them
    /// needs no cooperation from anyone.
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
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProfileBundle))]
public partial class ProfileJsonContext : JsonSerializerContext;
