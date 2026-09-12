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

    /// <summary>Path to the local profile file or directory containing profiles, used offline or when the fetch fails.</summary>
    [JsonPropertyName("profilePath")] public string ProfilePath { get; set; } = "profiles";

    /// <summary>Optional list of profile file paths to load and merge.</summary>
    [JsonPropertyName("profilePaths")] public List<string> ProfilePaths { get; set; } = [];

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

    /// <summary>Optional default game id.</summary>
    [JsonPropertyName("defaultGameId")] public string? DefaultGameId { get; set; }

    /// <summary>Virtual adapter name as shown in Network Connections.</summary>
    [JsonPropertyName("adapterName")] public string AdapterName { get; set; } = "Game Ping Booster";

    /// <summary>
    /// true = install routes immediately on connect without waiting for the game. Debug only -
    /// leaving routes in place permanently would drag unrelated traffic through the relay.
    /// </summary>
    [JsonPropertyName("routeWithoutGame")] public bool RouteWithoutGame { get; set; }

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
