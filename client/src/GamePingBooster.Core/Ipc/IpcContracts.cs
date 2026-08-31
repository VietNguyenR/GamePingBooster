using System.Text.Json.Serialization;

namespace GamePingBooster.Core.Ipc;

/// <summary>
/// Contract between the UI (runs as a normal user) and the Windows Service (LocalSystem).
/// Carried over a named pipe; every message is one line of JSON terminated by '\n'.
/// </summary>
public static class IpcConstants
{
    /// <summary>Pipe name. The UI opens \\.\pipe\GamePingBooster.</summary>
    public const string PipeName = "GamePingBooster";

    /// <summary>Bump on contract changes so an old UI and a new service fail loudly instead of behaving oddly.</summary>
    public const int ProtocolVersion = 1;
}

public enum TunnelState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Faulted,
}

/// <summary>A command the UI sends down to the service.</summary>
public sealed class CommandMessage
{
    [JsonPropertyName("v")] public int Version { get; set; } = IpcConstants.ProtocolVersion;

    /// <summary>"connect" | "disconnect" | "status" | "reload-profile"</summary>
    [JsonPropertyName("verb")] public string Verb { get; set; } = "status";

    /// <summary>Relay id to use, e.g. "sg-1". Empty means let the service pick by ping.</summary>
    [JsonPropertyName("relayId")] public string? RelayId { get; set; }

    /// <summary>Id of the game to accelerate, e.g. "pubg".</summary>
    [JsonPropertyName("gameId")] public string? GameId { get; set; }
}

/// <summary>State the service pushes up to the UI (on request, and on every change).</summary>
public sealed class StatusMessage
{
    [JsonPropertyName("v")] public int Version { get; set; } = IpcConstants.ProtocolVersion;

    [JsonPropertyName("state")] public TunnelState State { get; set; } = TunnelState.Disconnected;

    /// <summary>Short human-readable line shown directly in the UI.</summary>
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";

    [JsonPropertyName("relayId")] public string? RelayId { get; set; }
    [JsonPropertyName("relayName")] public string? RelayName { get; set; }

    /// <summary>Round-trip time through the tunnel in milliseconds; null until measured.</summary>
    [JsonPropertyName("tunnelPingMs")] public double? TunnelPingMs { get; set; }

    /// <summary>Direct RTT to the relay with the tunnel off, for a before/after comparison.</summary>
    [JsonPropertyName("directPingMs")] public double? DirectPingMs { get; set; }

    /// <summary>Packet loss estimated from pings, 0..1.</summary>
    [JsonPropertyName("lossRatio")] public double? LossRatio { get; set; }

    /// <summary>Whether a game process is running - this drives route install/removal.</summary>
    [JsonPropertyName("gameRunning")] public bool GameRunning { get; set; }
    [JsonPropertyName("gameName")] public string? GameName { get; set; }

    /// <summary>Number of routes currently installed in the Windows routing table.</summary>
    [JsonPropertyName("activeRoutes")] public int ActiveRoutes { get; set; }

    [JsonPropertyName("packetsSent")] public long PacketsSent { get; set; }
    [JsonPropertyName("packetsReceived")] public long PacketsReceived { get; set; }

    /// <summary>Error detail when State is Faulted.</summary>
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>
/// Source-generated JSON, required for Native AOT (the reflection-based serializer is trimmed away).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CommandMessage))]
[JsonSerializable(typeof(StatusMessage))]
public partial class IpcJsonContext : JsonSerializerContext;
