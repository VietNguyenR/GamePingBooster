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
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProfileBundle))]
public partial class ProfileJsonContext : JsonSerializerContext;
