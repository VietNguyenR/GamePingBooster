using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamePingBooster.Service;

/// <summary>
/// Service configuration, read from %ProgramData%\GamePingBooster\config.json.
/// The installer writes this file; the UI never edits it directly and sends commands over the
/// named pipe instead.
/// </summary>
public sealed class ServiceConfig
{
    /// <summary>URL to fetch the profile (game IP ranges plus relay list). Empty = local file only.</summary>
    [JsonPropertyName("profileUrl")] public string? ProfileUrl { get; set; }

    /// <summary>Path to the local profile file, used offline or when the fetch fails.</summary>
    [JsonPropertyName("profilePath")] public string ProfilePath { get; set; } = "profiles/pubg-vn.json";

    /// <summary>Pre-shared key; must match /etc/gpb/psk on the relay.</summary>
    [JsonPropertyName("psk")] public string Psk { get; set; } = "";

    /// <summary>
    /// Base URL of the licence server, e.g. https://licence.example.com. Empty = self-hosted
    /// only, which is the default and stays the default.
    ///
    /// It lives here rather than in a settings file of the UI's own because it is a property of
    /// the installation, not of the person sitting at it, and because there should be one place
    /// that answers "what is this client pointed at". The UI cannot read this file, so it comes
    /// back over the pipe with the status - it is a URL, not a credential.
    /// </summary>
    [JsonPropertyName("licenceUrl")] public string? LicenceUrl { get; set; }

    /// <summary>Default relay id; empty means take the first relay in the profile.</summary>
    [JsonPropertyName("defaultRelayId")] public string? DefaultRelayId { get; set; }

    /// <summary>
    /// Self-hosted relays, set from the settings screen. Each is "host:port".
    ///
    /// A LIST, not one address, because the client already measures every relay it knows and
    /// picks the fastest, and already fails over to the others when one stops answering. A
    /// single-relay setting would have quietly switched both of those off for exactly the people
    /// most likely to run more than one server.
    ///
    /// When this is non-empty it REPLACES the profile's relay list rather than adding to it.
    /// Somebody who runs their own relays wants their own, not theirs plus a list of somebody
    /// else's - silently falling back to a stranger's relay because their own were unreachable
    /// is the last thing a self-hosted setup should do. The profile still supplies the game
    /// address ranges, which is the part they cannot produce themselves.
    /// </summary>
    [JsonPropertyName("relayEndpoints")] public List<string> RelayEndpoints { get; set; } = [];

    /// <summary>
    /// True when a key is present. NOT the same as "ready to connect", which also needs a relay
    /// to reach - and a relay can come from the profile rather than from this file, which this
    /// class cannot see. TunnelEngine.Snapshot answers that question; do not try to answer it
    /// here. The first version of this property did, decided an installation whose relays came
    /// from the profile was unconfigured, and disabled the Connect button on a setup that had
    /// been working for days.
    /// </summary>
    [JsonIgnore]
    public bool HasKey => !string.IsNullOrWhiteSpace(Psk);

    /// <summary>
    /// The game relays are measured for at connect when no game is open and none has been seen
    /// yet. Only a fallback: which game is accelerated is decided by which process is running.
    /// </summary>
    [JsonPropertyName("defaultGameId")] public string DefaultGameId { get; set; } = "pubg";

    /// <summary>
    /// The last game the service saw running, written when it changes.
    ///
    /// Relays are measured once, at connect, against one game's region - usually before any game is
    /// open. Remembering the last one means somebody who plays Counter-Strike 2 gets relays chosen
    /// for Counter-Strike 2 from their second session on, without choosing anything.
    /// </summary>
    [JsonPropertyName("lastGameId")] public string? LastGameId { get; set; }

    /// <summary>Virtual adapter name as shown in Network Connections.</summary>
    [JsonPropertyName("adapterName")] public string AdapterName { get; set; } = "Game Ping Booster";

    /// <summary>
    /// true = install routes immediately on connect without waiting for the game. Debug only -
    /// leaving routes in place permanently would drag unrelated traffic through the relay.
    /// </summary>
    [JsonPropertyName("routeWithoutGame")] public bool RouteWithoutGame { get; set; }

    /// <summary>
    /// Whether the connection-quality records the spike recorder writes are also queued for upload,
    /// which the app then sends to the licence server between matches.
    ///
    /// ON unless switched off, and a config.json written before the setting existed reads as on: the
    /// owner decided on 2026-09-15 that an opt-in nobody ticks produces no data, and the players who
    /// most need a fix are the ones who never report. What makes that acceptable is what the records
    /// hold - no addresses of any kind, see QualityFile - and that the switch is in Settings.
    /// Off, nothing is queued and the queue is emptied; the local record is kept either way.
    /// </summary>
    [JsonPropertyName("shareQuality")] public bool ShareQuality { get; set; } = true;

    /// <summary>
    /// Automatic moves between the ways into the relay in use - the relay itself and the entries in front
    /// of it - while a game runs, FOR THIS MACHINE. See DoorSwitchPolicy and TunnelEngine.MoveToDoor.
    ///
    ///   "off"     the other ways are not probed at all
    ///   "record"  they are probed, and every move the policy WOULD make goes into the connection-quality
    ///             record with what both ways did in the minute after
    ///   "on"      the moves are made
    ///
    /// An override. Unset, the relay's own setting from the profile decides - set per relay in /admin/relays,
    /// which is how moves reach everybody - and without one, "record". Anything unrecognised reads as
    /// "record": a typo must neither switch the probes off nor switch moving on. See Core's EntrySwitching.
    ///
    /// UNSET BY DEFAULT, and never written back while unset. The service saves this file on its own - every
    /// time a player opens a different game - and a default of "record" written into every config.json on
    /// the first match would be indistinguishable from somebody choosing it: no later release and no
    /// server-side setting could then turn moves on for anybody. Unset behaves as "record"; only a value
    /// somebody typed pins this machine.
    /// </summary>
    [JsonPropertyName("entrySwitching")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntrySwitching { get; set; }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GamePingBooster");

    private static string FilePath => Path.Combine(DefaultDirectory, "config.json");

    /// <summary>
    /// Writes the configuration back, atomically.
    ///
    /// Via a temporary file and a replace, because the alternative is a half-written config.json
    /// if the machine loses power mid-save - and a service that cannot parse its own
    /// configuration does not start, which turns a settings change into a dead installation.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(DefaultDirectory);
        var json = JsonSerializer.Serialize(this, ServiceConfigJsonContext.Default.ServiceConfig);

        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static ServiceConfig Load()
    {
        var path = Path.Combine(DefaultDirectory, "config.json");
        if (!File.Exists(path))
        {
            // During development we run straight out of the build directory.
            path = Path.Combine(AppContext.BaseDirectory, "config.json");
        }
        if (!File.Exists(path))
        {
            // Not an error any more. A freshly installed machine has no configuration, and the
            // service has to come up anyway so the UI can connect and offer the settings screen.
            // Throwing here meant the service died on first run and the user saw nothing at all.
            return new ServiceConfig();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, ServiceConfigJsonContext.Default.ServiceConfig)
               ?? throw new InvalidOperationException($"config.json at {path} is not valid.");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServiceConfig))]
public partial class ServiceConfigJsonContext : JsonSerializerContext;
