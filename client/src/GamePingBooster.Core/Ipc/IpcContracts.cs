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

    /// <summary>
    /// "connect" | "disconnect" | "status" | "reload-profile" | "set-relay" | "set-token" |
    /// "set-profile"
    /// </summary>
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

    /// <summary>
    /// set-relay: base URL of the licence server, or empty for a self-hosted installation.
    ///
    /// It travels with the relay settings rather than in a verb of its own because it is the
    /// same act - the settings screen writing the service's configuration - and a second verb
    /// would mean a second round trip and a second way for the two halves to disagree about
    /// what was saved. Null means "leave it as it is"; empty string means "clear it".
    /// </summary>
    [JsonPropertyName("licenceUrl")] public string? LicenceUrl { get; set; }

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

    // ------------------------------------------------------------------ set-profile
    //
    // The seventh verb. The UI fetches the profile and pushes it down; the service does not
    // fetch it itself, and the split is not arbitrary.
    //
    // The licence server authenticates the profile request with the account's refresh token.
    // That credential belongs to the PERSON, is wrapped with DPAPI at USER scope, and lives in
    // the signed-in user's own profile directory. The service runs as LocalSystem and cannot
    // read it - nor should it: pulling a user credential across that boundary to save an IPC
    // message would widen the one privilege boundary this project keeps narrow.
    //
    // Unlike the key and the token, the profile is not write-only, because it is not a secret in
    // the first place: RouteManager turns every CIDR in it into a Windows route, so anyone can
    // read the whole list back with Get-NetRoute while the tunnel is up. Sending it over a pipe
    // open to BuiltinUsers gives away nothing that is not already visible.

    /// <summary>
    /// set-profile: the SEALED profile as hex, exactly as the licence server sent it.
    ///
    /// The UI never opens it and could not: it is encrypted to the device key, which lives in
    /// the service. So the ranges do not cross this pipe in readable form, and the UI does not
    /// hold them even briefly.
    /// </summary>
    [JsonPropertyName("profile")] public string? Profile { get; set; }
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
    ///
    /// SELF-HOSTED ONLY: these are the addresses somebody typed into Settings. A licensed
    /// installation takes its relays from the pushed profile instead and leaves this EMPTY, so it
    /// is the wrong field to ask "which relay are we on" - see <see cref="RelayAddress"/>, which
    /// exists because reading this one for that gave an empty answer on every paying customer.
    /// </summary>
    [JsonPropertyName("relayEndpoints")] public List<string> RelayEndpoints { get; set; } = [];

    /// <summary>
    /// host:port of the relay this session is actually on, or null when not connected.
    ///
    /// The profile is sealed to the service and the UI cannot open it, so without this the UI can
    /// name the relay it is using but cannot address it - which is what left the lag report with
    /// no traceroute and three empty rungs.
    ///
    /// Not a secret being given away: this machine is sending packets to that address right now
    /// and The requested operation requires elevation. prints it. What the sealed profile protects is the captured IP RANGES,
    /// and those stay where they are.
    /// </summary>
    [JsonPropertyName("relayAddress")] public string? RelayAddress { get; set; }

    /// <summary>False until a relay and a key have been configured. Drives the first-run prompt.</summary>
    [JsonPropertyName("configured")] public bool Configured { get; set; }

    /// <summary>
    /// Round-trip time to the relay in milliseconds; null until measured. **Half the path.**
    ///
    /// This is the RTT to the relay over the physical path, not through the tunnel: the pinned
    /// /32 route keeps relay traffic off the virtual adapter, so keepalives never enter it.
    ///
    /// It is live - a keepalive every second - and it is the wrong number to put in front
    /// of a player on its own. A tester saw 23 ms here while his game showed 70-80, because the
    /// leg from the relay on to the game server is not in it. Show <see cref="GamePingMs"/> as
    /// the headline and keep this as the detail that explains it.
    /// </summary>
    [JsonPropertyName("tunnelPingMs")] public double? TunnelPingMs { get; set; }

    /// <summary>
    /// Latency to the game's server through the tunnel - what the game will show. Null before
    /// connecting, and while neither of the two ways of arriving at it has produced anything.
    ///
    /// It comes from one of two places, and <see cref="GamePingDirect"/> says which:
    ///
    ///   1. MEASURED. An ICMP echo through the live tunnel to the address the game is actually
    ///      playing on, once a second. That travels the whole path the game's packets travel, so
    ///      there is no arithmetic in it at all.
    ///
    ///   2. ESTIMATED. The live <see cref="TunnelPingMs"/> plus an offset for the
    ///      relay-to-datacentre leg, measured once at connect time against the region's landmark.
    ///      The split is deliberate: the part that moves is the player's own connection, while
    ///      the leg between two datacentres barely does (0.07 ms of jitter over five echoes,
    ///      2026-09-05). Used when the game server does not answer echoes, or between matches.
    ///
    /// The estimate is what shipped first, and it was wrong on real hardware in a way worth
    /// recording. Its offset came from subtracting a single handshake sample from a best-of-three
    /// echo; on 2026-09-10 those were 45 and 45, the offset came out as zero, and the app spent
    /// the session showing the relay ping under a label saying in-game ping - 42-43 ms against
    /// 46-50 ms in the game. Both legs are now measured the same way, and the direct measurement
    /// supersedes the whole calculation whenever it is available.
    ///
    /// Note the estimate is NOT the "two numbers added together" that relay selection
    /// rejects for *choosing* a relay. That rejection is about building a total from two
    /// independent measurements, which silently omits the relay's forwarding cost. Here the total
    /// was measured first and the offset derived from it.
    /// </summary>
    [JsonPropertyName("gamePingMs")] public double? GamePingMs { get; set; }

    /// <summary>
    /// True when <see cref="GamePingMs"/> was measured against the game's own server, false when
    /// it is the landmark estimate.
    ///
    /// Worth showing rather than hiding. The two are not equally trustworthy, and a player
    /// comparing our number against the one in the game is entitled to know which they are
    /// looking at - especially since the estimate is the one that has been wrong before.
    /// </summary>
    [JsonPropertyName("gamePingDirect")] public bool GamePingDirect { get; set; }

    /// <summary>Display name of the region the game will use, e.g. "Southeast Asia (Singapore)".</summary>
    [JsonPropertyName("gameRegionName")] public string? GameRegionName { get; set; }

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
    /// Drops that mean something is WRONG - the total above minus the link-local chatter Windows
    /// pushes into every adapter and the uplink filter correctly throws away.
    ///
    /// Two counters rather than one because they answer different questions and only this one may
    /// be alarmed on. The total is a diagnostic figure and is dominated by noise on a perfectly
    /// healthy machine; anything that says "packets are being lost inside this PC" has to read
    /// this instead, or it says so about everybody.
    /// </summary>
    [JsonPropertyName("packetsDroppedFaults")] public long PacketsDroppedFaults { get; set; }

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

    /// <summary>
    /// Where to sign in, from the service's configuration. Null or empty means this installation
    /// is self-hosted and there is nothing to sign in to - the UI hides the whole idea then.
    /// A URL, not a credential.
    /// </summary>
    [JsonPropertyName("licenceUrl")] public string? LicenceUrl { get; set; }

    /// <summary>
    /// Why a connection would be refused for licence reasons right now, already worded for the
    /// user, or null when it would not be.
    ///
    /// The service decides this, because the service is what acts on it: ConnectAsync throws
    /// with this same sentence. The UI only reflects it - a second copy of the rule up there
    /// would eventually disagree with the one that matters, and leave a button enabled for a
    /// connection that cannot happen.
    ///
    /// It is NOT the enforcement. The relay verifies the licence token offline against the
    /// licence server's public key and refuses an expired one by itself; nothing on this side
    /// of the pipe can be trusted, because all of it runs on the user's machine. This exists so
    /// the refusal arrives as a sentence rather than as a timeout.
    ///
    /// Additive, so no contract version bump: an older UI ignores it, and a newer UI reads null
    /// from an older service - which is what an unblocked installation reports anyway.
    /// </summary>
    [JsonPropertyName("licenceRefusal")] public string? LicenceRefusal { get; set; }

    /// <summary>
    /// Which profile the service is actually using: "shipped", "pushed", or "cached".
    ///
    /// Worth reporting because the failure this answers is silent. A profile fetch that goes
    /// wrong falls back to the local copy and carries on, so the only visible symptom of a
    /// licence server nobody can reach is that the ranges are older than they should be.
    /// </summary>
    [JsonPropertyName("profileSource")] public string? ProfileSource { get; set; }

    /// <summary>
    /// When the profile pushed by the licence server was last written, unix seconds, or null
    /// when nothing has ever been pushed.
    ///
    /// The UI needs it to decide whether fetching again is worth it, and the answer has to
    /// outlive the UI process - which is the whole point. ProfileSync used to hold that
    /// timestamp in a field, so every launch of the app started life believing it had never
    /// fetched anything and asked again immediately. Twelve launches in an hour is an ordinary
    /// afternoon on a development machine, and twelve is exactly the licence server's cap, so
    /// the app started reporting "Too many requests" to somebody who had done nothing but open
    /// it. The file on disk is the honest answer to "when did we last get one", it survives the
    /// UI, and it cannot drift from the thing it describes.
    ///
    /// Additive, so no contract version bump: an older UI ignores it, and a newer UI reads null
    /// from an older service - which means "fetch", the same as a machine that has never had a
    /// profile.
    /// </summary>
    [JsonPropertyName("profileUpdatedAt")] public long? ProfileUpdatedAt { get; set; }

    /// <summary>Error detail when State is Faulted.</summary>
    ///
    /// A property of the TUNNEL, not a reply to whatever the UI last sent. It is sticky by
    /// design - it survives until the next connect attempt, so a window opened after a failed
    /// one can still say why - which is exactly why a command must not be judged by it. See
    /// <see cref="AckVerb"/>.
    [JsonPropertyName("error")] public string? Error { get; set; }

    /// <summary>
    /// The verb this status is the direct reply to, or null on a status that was pushed for
    /// some other reason - a state change, or the once-a-second heartbeat.
    ///
    /// It exists because a screen that sends a command has no other way to recognise its own
    /// answer. Statuses arrive continuously, so "the next one" is usually a heartbeat, and
    /// <see cref="Error"/> on it is whatever the tunnel last failed with. The settings screen
    /// read both that way and reported a save as failed because a CONNECT had failed earlier -
    /// the settings were on disk the whole time.
    ///
    /// Additive, so no contract version bump: an older UI ignores it, and a newer UI reads null
    /// from an older service, which means "not a reply" and leaves it waiting rather than
    /// believing something wrong.
    /// </summary>
    [JsonPropertyName("ackVerb")] public string? AckVerb { get; set; }

    /// <summary>
    /// Why the command named by <see cref="AckVerb"/> was refused, or null when it was carried
    /// out. Meaningless on a status that is not a reply.
    ///
    /// Separate from <see cref="Error"/> because they answer different questions: this one is
    /// about the message just sent, that one is about the tunnel. Saving settings is a local
    /// act - validate the format, write the file - and it succeeds on a machine whose relay is
    /// unreachable, in a different auth mode, or refusing the key it has. Connecting is what
    /// asks the network anything.
    /// </summary>
    [JsonPropertyName("commandError")] public string? CommandError { get; set; }
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
