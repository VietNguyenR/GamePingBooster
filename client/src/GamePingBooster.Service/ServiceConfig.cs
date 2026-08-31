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

    /// <summary>Default relay id; empty means take the first relay in the profile.</summary>
    [JsonPropertyName("defaultRelayId")] public string? DefaultRelayId { get; set; }

    /// <summary>Default game id.</summary>
    [JsonPropertyName("defaultGameId")] public string DefaultGameId { get; set; } = "pubg";

    /// <summary>Virtual adapter name as shown in Network Connections.</summary>
    [JsonPropertyName("adapterName")] public string AdapterName { get; set; } = "Game Ping Booster";

    /// <summary>
    /// true = install routes immediately on connect without waiting for the game. Debug only -
    /// leaving routes in place permanently would drag unrelated traffic through the relay.
    /// </summary>
    [JsonPropertyName("routeWithoutGame")] public bool RouteWithoutGame { get; set; }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GamePingBooster");

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
            throw new FileNotFoundException(
                $"config.json not found. Create it at {Path.Combine(DefaultDirectory, "config.json")} " +
                "(see client/config.example.json).");
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, ServiceConfigJsonContext.Default.ServiceConfig)
               ?? throw new InvalidOperationException($"config.json at {path} is not valid.");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServiceConfig))]
public partial class ServiceConfigJsonContext : JsonSerializerContext;
