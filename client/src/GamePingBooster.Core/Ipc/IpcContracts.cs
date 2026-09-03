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
    public const int ProtocolVersion = 2;
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

    /// <summary>"connect" | "disconnect" | "status" | "reload-profile" | "set-relay" | "set-token"</summary>
    [JsonPropertyName("verb")] public string Verb { get; set; } = "status";

    // ------------------------------------------------------------------ set-relay
    //
    // The fifth verb, and the first one that carries data rather than an enum. It exists because
    // the UI runs as a normal user and cannot write %ProgramData%, where the service reads its
    // configuration from; the service owns that file and writes it on the UI's behalf.
    //
    // It is WRITE-ONLY on purpose. StatusMessage carries the relay's name and endpoint back, but
    // never the key - the pipe is open to BuiltinUsers, so anything readable here is readable by
    // any process running as the user. Sending a key in is a nuisance; letting one be read out
    // would be a credential leak.

    /// <summary>
    /// set-relay: the relay addresses, each "host:port". More than one is normal - the client
    /// probes them all and fails over between them.
    /// </summary>
    [JsonPropertyName("relayEndpoints")] public List<string>? RelayEndpoints { get; set; }

    /// <summary>set-relay: the pre-shared key. Never sent back up. Null means "keep the stored one".</summary>
    [JsonPropertyName("psk")] public string? Psk { get; set; }

    /// <summary>Relay id to use, e.g. "sg-1". Empty means let the service pick by ping.</summary>
    [JsonPropertyName("relayId")] public string? RelayId { get; set; }

    /// <summary>Id of the game to accelerate, e.g. "pubg".</summary>
    [JsonPropertyName("gameId")] public string? GameId { get; set; }

    // ------------------------------------------------------------------ set-token
    //
    // The sixth verb, and the second one carrying data. The UI signs in to the licence server,
    // is handed a 150-byte token, and pushes it down here because the service is the half that
    // presents it at handshake time and the half that can write %ProgramData%.
    //
    // WRITE-ONLY, for the same reason as the key in set-relay: the pipe is open to BuiltinUsers,
    // so anything readable over it is readable by any process running as the user. StatusMessage
    // reports whether a token exists and when it expires - never the token.
    //
    // Pushing a token is not the same as being authorised. The relay verifies the signature
    // against the licence server's public key, so the worst a hostile local process achieves is
    // making the tunnel present a token it already had.

    /// <summary>set-token: the licence token as hex, 300 characters. Null clears the stored one.</summary>
    [JsonPropertyName("token")] public string? Token { get; set; }
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

    /// <summary>
    /// The configured relay endpoints, so the settings screen can show what is set without the
    /// UI needing to read a file it has no permission to read. The key is never included.
    /// </summary>
    [JsonPropertyName("relayEndpoints")] public List<string> RelayEndpoints { get; set; } = [];

    /// <summary>False until a relay and a key have been configured. Drives the first-run prompt.</summary>
    [JsonPropertyName("configured")] public bool Configured { get; set; }

    /// <summary>
    /// Round-trip time to the relay in milliseconds; null until measured.
    ///
    /// This is the RTT to the relay over the physical path, not through the tunnel: the pinned
    /// /32 route keeps relay traffic off the virtual adapter, so keepalives never enter it. The
    /// before/after number a player actually cares about - latency to the game server with and
    /// without the tunnel - cannot be produced here, because the relay-to-server leg is only
    /// measurable from the relay and those servers do not answer probes. Use the game's own
    /// in-game ping for that, and mtr from the VPS for choosing where to put a relay.
    /// </summary>
    [JsonPropertyName("tunnelPingMs")] public double? TunnelPingMs { get; set; }

    /// <summary>Packet loss estimated from pings, 0..1.</summary>
    [JsonPropertyName("lossRatio")] public double? LossRatio { get; set; }

    /// <summary>Whether a game process is running - this drives route install/removal.</summary>
    [JsonPropertyName("gameRunning")] public bool GameRunning { get; set; }
    [JsonPropertyName("gameName")] public string? GameName { get; set; }

    /// <summary>Number of routes currently installed in the Windows routing table.</summary>
    [JsonPropertyName("activeRoutes")] public int ActiveRoutes { get; set; }

    [JsonPropertyName("packetsSent")] public long PacketsSent { get; set; }
    [JsonPropertyName("packetsReceived")] public long PacketsReceived { get; set; }

    /// <summary>
    /// Packets lost inside the client itself, not on the network.
    ///
    /// Additive on purpose, with no contract version bump: an older UI ignores the field and a
    /// newer UI reads 0 from an older service, so neither fails. The per-cause breakdown stays in
    /// the service log where it belongs - this is only the number that tells a player whether it
    /// is worth looking there at all.
    /// </summary>
    [JsonPropertyName("packetsDropped")] public long PacketsDropped { get; set; }

    /// <summary>
    /// This machine's device public key, 65 bytes as lowercase hex, or null on a service too old
    /// to have one.
    ///
    /// The PUBLIC half, and only ever that. It is the device's name, not a credential: the UI
    /// sends it to the licence server to register this machine against the signed-in account,
    /// which is why it has to be readable from a normal-user process at all. The private half
    /// stays in the service, wrapped with DPAPI, and has no representation in this contract -
    /// the pipe is open to BuiltinUsers, so anything readable here is readable by any process
    /// running as the user.
    ///
    /// Additive, so no contract version bump: an older UI ignores the field, and a newer UI
    /// reads null from an older service rather than failing.
    /// </summary>
    [JsonPropertyName("devicePublicKey")] public string? DevicePublicKey { get; set; }

    /// <summary>
    /// Whether a licence token is stored. Not whether it is VALID - only the relay knows that.
    /// </summary>
    [JsonPropertyName("hasToken")] public bool HasToken { get; set; }

    /// <summary>
    /// When the stored token expires, unix seconds, or null when there is none.
    ///
    /// Not a credential: it is a date. The UI needs it to refresh at 50% of remaining life, which
    /// is the whole reason a handshake never carries a nearly expired token.
    /// </summary>
    [JsonPropertyName("tokenExpiresAt")] public long? TokenExpiresAt { get; set; }

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
