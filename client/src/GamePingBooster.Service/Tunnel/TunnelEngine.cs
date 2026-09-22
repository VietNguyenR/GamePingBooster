using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Service.Discovery;
using GamePingBooster.Service.Native;
using GamePingBooster.Service.Network;
using System.Security.Cryptography;

using GamePingBooster.Service.Dns;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// The conductor for the whole client side. It wires four pieces together:
/// the virtual adapter (Wintun), the tunnel (TunnelClient), the routing table (RouteManager),
/// and game detection (GameProcessWatcher).
///
/// The startup order matters and must not be rearranged:
///   1. Create the virtual adapter
///   2. Handshake with the relay (obtain the inner IP)
///   3. Pin a /32 route for the relay through the PHYSICAL adapter  &lt;- skip this and you get a loop
///   4. Assign IP and MTU to the virtual adapter, start both pump threads
///   5. Only install the game IP routes once the game is actually running
/// </summary>
internal sealed partial class TunnelEngine : IAsyncDisposable
{
    private readonly ServiceConfig _config;
    private readonly Action<string> _log;

    private ProfileBundle? _profile;

    /// <summary>
    /// The services the delivered profile says to unblock, or the built-in list when it carries
    /// none - see UnblockPolicy.
    ///
    /// Exposed rather than acted on here: unblocking a name has nothing to do with a tunnel, and
    /// the engine is not where that decision belongs. It is only the thing that happens to hold the
    /// profile the list arrives in.
    /// </summary>
    public UnblockPolicy UnblockPolicy => UnblockPolicy.FromProfile(_profile, _log);
    private WintunAdapter? _adapter;
    private TunnelClient? _tunnel;
    private RouteManager? _routes;
    private GameProcessWatcher? _watcher;

    // Servers the running game uses that the profile lacks, found from ETW while the tunnel carries
    // none of the game's UDP and sent to the licence server - see GameDestinationRecorder and
    // DiscoveryUploader. Null when config.json switches it off. Follows the watcher, so it only
    // watches while connected.
    private readonly GameDestinationRecorder? _discovery;
    private readonly DiscoveryUploader? _discoveryUploader;

    // Which game is running here, for /admin/relays. Always there; it sends only when connection quality may be.
    private readonly PresenceReporter _presence;
    private CancellationTokenSource? _cts;
    private Task? _supervisor;
    private Task? _gamePingProbe;
    private Task? _spikeRecorder;

    /// <summary>
    /// In-game ping measured directly against the server the game chose, smoothed; negative when
    /// there is no measurement. Written by the probe loop, read by <see cref="Snapshot"/> on
    /// whichever thread asks, hence the volatile access.
    /// </summary>
    private double _directPingMs = -1;
    private long _directPingAtTick;
    private readonly ulong _clientId = ClientIdentity.Load();

    /// <summary>
    /// This machine's P-256 keypair. Loaded here rather than where it is used, because it must
    /// exist from the moment the service starts: the UI reads its public half to register the
    /// device, and that happens long before anything connects. See DeviceIdentity for why it is
    /// a different kind of thing from _clientId above.
    /// </summary>
    private readonly DeviceIdentity _device;

    /// <summary>
    /// The stored licence token, or null when this installation has never signed in.
    ///
    /// Held in a field rather than read from disk per connect because a failover reconnects
    /// without any user action, and re-reading a DPAPI blob on that path buys nothing. Replaced
    /// wholesale by SetToken when the UI pushes a new one.
    /// </summary>
    private volatile byte[]? _token;

    /// <summary>
    /// Which copy of the profile is loaded: "shipped", "pushed" or "cached".
    ///
    /// Reported in the status because the failure it describes is otherwise silent. A profile
    /// that cannot be fetched falls back and the tunnel works; the only symptom is ranges that
    /// are quietly out of date, which nobody notices until a match is not accelerated.
    /// </summary>
    private volatile string _profileSource = "none";

    private GameEntry? _game;
    private RelayEntry? _relay;
    private volatile TunnelState _state = TunnelState.Disconnected;
    private volatile string _detail = "Not connected";

    // The same line as a language key and its arguments, for a UI that is not in English. English
    // stays in _detail: it is what the log and an older UI read. See StatusText.
    private volatile StatusText _detailText = new("svc.notConnected", "Not connected");
    private volatile string? _error;

    /// <summary>Raised on every state change so PipeServer can push it to the UI.</summary>
    public event Action<StatusMessage>? StatusChanged;

    public TunnelEngine(ServiceConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _device = DeviceIdentity.LoadOrCreate(log);
        _token = TokenStore.Load(log);
        QualityOutbox.Enabled = config.ShareQuality;
        if (config.DiscoverDestinations != false)
        {
            // Sent under the connection-quality switch, and only by a licensed, signed-in installation:
            // the report is authenticated with this device's licence token and key.
            _discoveryUploader = new DiscoveryUploader(
                () => _config.LicenceUrl, () => _token, _device.Key, () => _config.ShareQuality, log);
            _discovery = new GameDestinationRecorder(
                () => _tunnel?.Destinations.UdpPackets, _discoveryUploader.WhyNotSend, _discoveryUploader.Report, log);
        }
        _presence = new PresenceReporter(() => _config.LicenceUrl, () => _token, _device.Key, () => _config.ShareQuality, log);
        if (_token is not null)
        {
            log($"Licence token loaded, expires {TokenStore.ExpiryOf(_token):u}.");
        }
    }

    /// <summary>
    /// Stores a licence token pushed down from the UI, replacing any previous one.
    ///
    /// WRITE-ONLY by design, and the reason is the pipe's ACL: it is open to BuiltinUsers so the
    /// normal-user UI can drive the LocalSystem service, which means anything readable over it is
    /// readable by every process running as the user. A token going in is a nuisance - the relay
    /// still verifies it, so the worst a hostile local process achieves is making the tunnel use
    /// a token it already had. A token coming back out would be a credential leak.
    ///
    /// It does NOT take effect on a live tunnel. The token is presented at handshake time, and
    /// tearing down a working session to re-present one would drop the player out of a match for
    /// no benefit - the session already in progress was authorised when it started, and the relay
    /// caps its age anyway.
    /// </summary>
    /// <summary>
    /// Stores a profile the UI fetched from the licence server, and reloads from it.
    ///
    /// Everything arriving here is untrusted: the pipe is open to BuiltinUsers, so a hostile
    /// local process can call this. It is parsed before it is written - a file that does not
    /// deserialise would leave the service unable to load a profile at all on the next start,
    /// which is a denial of service anybody could trigger.
    ///
    /// The worst a hostile caller achieves after those checks is routing their own choice of
    /// addresses through the relay from their own machine, which they could do by editing the
    /// routing table directly. This is not a new capability.
    /// </summary>
    public async Task<string?> SetProfileAsync(string envelopeHex, CancellationToken ct)
    {
        // A sealed profile is tens of kilobytes of hex. A megabyte is not one.
        const int MaxChars = 8 * 1024 * 1024;
        if (envelopeHex.Length > MaxChars) return "That profile is too large.";

        byte[] envelope;
        try
        {
            envelope = Convert.FromHexString(envelopeHex.Trim());
        }
        catch (FormatException)
        {
            return "That is not a sealed profile.";
        }

        // Opened BEFORE it is written. The envelope is authenticated, so this is the check that
        // makes the verb safe: the pipe is open to BuiltinUsers, and without it any local
        // process could drop a file the service cannot use and leave it unable to load a profile
        // at all on the next start.
        string json;
        try
        {
            var plaintext = _device.OpenSealedProfile(envelope);
            try
            {
                json = System.Text.Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (CryptographicException ex)
        {
            return ex.Message;
        }

        ProfileBundle? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(json, ProfileJsonContext.Default.ProfileBundle);
        }
        catch (JsonException ex)
        {
            return $"The sealed profile did not contain a valid one: {ex.Message}";
        }
        if (parsed is null || parsed.Games.Count == 0) return "That profile names no games.";

        // Stored per game, under the game's own id. The licence server seals one game per profile,
        // and a single shared file meant fetching Counter-Strike 2 overwrote PUBG. The id becomes a
        // file name and this pipe is open to BuiltinUsers, so it is held to a shape that can only
        // ever name a file inside the one directory.
        var game = parsed.Games[0];
        if (!IsStorableGameId(game.Id))
        {
            return $"That profile's game id cannot be stored: '{game.Id}'.";
        }

        try
        {
            Directory.CreateDirectory(SealedProfileDirectory);
            var path = SealedProfilePathFor(game.Id);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, envelope, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            return $"Could not store the profile: {ex.Message}";
        }

        RetireLegacySealedProfile();

        try
        {
            await LoadProfileAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Stored, but could not load it: {ex.Message}";
        }

        var cidrs = parsed.Games.Sum(g => g.Regions.Sum(r => r.Cidrs.Count));
        _log($"Profile for {game.Name} updated from the licence server: {cidrs} ranges, " +
             $"{parsed.Relays.Count} relay(s). It applies from the next connect.");
        StatusChanged?.Invoke(Snapshot());
        return null;
    }

    /// <summary>Forgets the stored token. Signing out, or a token the server has revoked.</summary>
    public void ClearToken()
    {
        TokenStore.Clear(_log);
        _token = null;
        StatusChanged?.Invoke(Snapshot());
    }

    public bool SetToken(ReadOnlySpan<byte> token)
    {
        if (!TokenStore.Save(token, _log)) return false;
        _token = token.ToArray();
        _log($"Licence token stored, expires {TokenStore.ExpiryOf(_token):u}. It applies from the next connect.");
        StatusChanged?.Invoke(Snapshot());
        return true;
    }

    // -------------------------------------------------------------- profile

    /// <summary>
    /// Where the profiles the licence server sent are kept: one file per game, profiles\pubg.sealed
    /// and profiles\cs2.sealed.
    ///
    /// Each holds the SEALED envelope, not the profile. Nothing readable is ever written: opening
    /// it needs the device key, which is itself DPAPI machine-scoped, so a copy of these files on
    /// any other machine - or in a backup, or in a support bundle - is inert. It used to be
    /// plaintext JSON with every captured range in it, which is exactly what this product is
    /// meant not to hand out.
    /// </summary>
    internal static string SealedProfileDirectory =>
        Path.Combine(ServiceConfig.DefaultDirectory, "profiles");

    private static string SealedProfilePathFor(string gameId) =>
        Path.Combine(SealedProfileDirectory, gameId.ToLowerInvariant() + ".sealed");

    /// <summary>
    /// The one file every game shared before profiles were stored per game. Still read - after
    /// every per-game file, so it never wins - so an upgraded installation keeps its profile until
    /// the first fetch replaces it, and deleted once every game it held has a file of its own.
    /// </summary>
    private static string LegacySealedProfilePath =>
        Path.Combine(ServiceConfig.DefaultDirectory, "profile.sealed");

    /// <summary>Every stored sealed profile, newest first, with the legacy file last.</summary>
    private static List<string> SealedProfileFiles()
    {
        var files = Directory.Exists(SealedProfileDirectory)
            ? Directory.GetFiles(SealedProfileDirectory, "*.sealed")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList()
            : [];
        if (File.Exists(LegacySealedProfilePath)) files.Add(LegacySealedProfilePath);
        return files;
    }

    /// <summary>
    /// When the OLDEST stored profile was written, or null when none is.
    ///
    /// The oldest, because the UI reads this to decide whether a fetch is due, and one game's fresh
    /// profile must not hide that another's is stale.
    /// </summary>
    private static DateTimeOffset? OldestSealedProfileWrite()
    {
        var files = SealedProfileFiles();
        if (files.Count == 0) return null;
        return new DateTimeOffset(files.Min(File.GetLastWriteTimeUtc));
    }

    /// <summary>A game id that is safe as a file name: letters, digits, '-' and '_', at most 32.</summary>
    private static bool IsStorableGameId(string id) =>
        id.Length is > 0 and <= 32 &&
        id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');

    /// <summary>Opens one sealed envelope in memory. The plaintext exists only as long as parsing takes.</summary>
    private ProfileBundle OpenSealed(byte[] envelope)
    {
        var plaintext = _device.OpenSealedProfile(envelope);
        string json;
        try
        {
            json = System.Text.Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
        return JsonSerializer.Deserialize(json, ProfileJsonContext.Default.ProfileBundle)
               ?? throw new InvalidOperationException("The sealed profile is not valid.");
    }

    /// <summary>
    /// Deletes the pre-per-game profile file once nothing in it is still needed. Failures only
    /// leave it in place, where it is read last and harms nothing.
    /// </summary>
    private void RetireLegacySealedProfile()
    {
        if (!File.Exists(LegacySealedProfilePath)) return;
        try
        {
            var legacy = OpenSealed(File.ReadAllBytes(LegacySealedProfilePath));
            if (legacy.Games.Any(g => !IsStorableGameId(g.Id) || !File.Exists(SealedProfilePathFor(g.Id)))) return;

            File.Delete(LegacySealedProfilePath);
            _log("Removed the old shared profile file - every game it held now has a file of its own.");
        }
        catch (Exception ex)
        {
            _log($"Left the old shared profile file in place: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads the profile.
    ///
    /// The service does NOT fetch it, even though ProfileUrl is stored in its configuration. It
    /// used to, with a bare HttpClient and no credential, which worked only against a server
    /// that did not ask for one. The real licence server authenticates the request with the
    /// account's refresh token - a credential belonging to the PERSON, wrapped with DPAPI at
    /// USER scope in their own profile directory. This process is LocalSystem and cannot read
    /// it, and reaching across that boundary to fetch a list of IP ranges would be a poor
    /// trade. So the UI fetches and pushes it down with set-profile.
    ///
    /// Order, and it matters:
    ///
    ///   licence server configured -> the PUSHED copy wins, because it is the current one and
    ///                                the shipped file is whatever the installer happened to
    ///                                carry. Falls back to the shipped copy when nothing has
    ///                                been pushed yet, so a fresh install still connects.
    ///   self-hosted               -> the local file, exactly as before. Nothing pushes.
    /// </summary>
    public async Task LoadProfileAsync(CancellationToken ct)
    {
        // "Licensed" is having a licence server, full stop. There is no separate profile
        // address to configure: the endpoints all hang off the one URL somebody typed, and
        // asking for a second address for the same server was needless.
        var licensed = !string.IsNullOrWhiteSpace(_config.LicenceUrl);

        // Where the installer puts it, and what the default in ServiceConfig resolves to.
        var shipped = Path.Combine(AppContext.BaseDirectory, "profiles", "pubg-vn.json");

        var local = Path.IsPathRooted(_config.ProfilePath)
            ? _config.ProfilePath
            : Path.Combine(AppContext.BaseDirectory, _config.ProfilePath);

        // A configured path that no longer exists is not a dead end. It usually means an
        // absolute path written by hand on a developer's machine, or an install that moved -
        // and in both cases the profile the installer shipped is sitting right there.
        if (!File.Exists(local) && File.Exists(shipped))
        {
            _log($"No profile at {local}; falling back to the one installed at {shipped}.");
            local = shipped;
        }

        var sealedFiles = SealedProfileFiles();
        var bundles = new List<ProfileBundle>();
        string source;
        string chosen;

        if ((licensed || !File.Exists(local)) && sealedFiles.Count > 0)
        {
            // Every game's profile, newest first, so the newest decides the relay list - see
            // ProfileMerge. One that cannot be opened is skipped rather than failing the rest: a
            // damaged Counter-Strike 2 file is no reason to stop accelerating PUBG.
            foreach (var file in sealedFiles)
            {
                try
                {
                    var envelope = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
                    bundles.Add(OpenSealed(envelope));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log($"Skipping the stored profile {Path.GetFileName(file)}: {ex.Message}");
                }
            }
            if (bundles.Count == 0)
            {
                throw new InvalidOperationException("None of the stored profiles could be opened.");
            }
            source = licensed ? "pushed" : "cached";
            chosen = SealedProfileDirectory;
        }
        else if (File.Exists(local))
        {
            // The configured file is the primary, and its relays are the ones used. Every other
            // profile beside it adds its games - one file per game is how the installer ships them -
            // but never its relays. Example files are templates, not profiles.
            bundles.Add(ParseProfile(await File.ReadAllTextAsync(local, ct).ConfigureAwait(false), local));

            var fullLocal = Path.GetFullPath(local);
            var siblings = Directory.GetFiles(Path.GetDirectoryName(fullLocal)!, "*.json")
                .Where(f => !Path.GetFullPath(f).Equals(fullLocal, StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.EndsWith(".example.json", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase);
            foreach (var sibling in siblings)
            {
                try
                {
                    bundles.Add(ParseProfile(await File.ReadAllTextAsync(sibling, ct).ConfigureAwait(false), sibling));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log($"Skipping {Path.GetFileName(sibling)} beside the profile: {ex.Message}");
                }
            }
            source = "shipped";
            chosen = local;
        }
        else
        {
            // Two sentences, because the two installations have nothing to do with each other.
            // A self-hosted machine has no licence server and never will, so telling its owner
            // that nothing came from one sends them looking for a server they deliberately do
            // not run - and the file they actually need is the one named here, beside the
            // service. The licensed wording stays as it was: there the profile really does
            // arrive from the server, and the shipped file is only the fallback.
            throw new NoProfileAvailableException(licensed
                ? $"No profile at {_config.ProfilePath} and nothing from the licence server either."
                : $"No profile at {_config.ProfilePath}, and none installed beside the service " +
                  $"at {shipped}. A self-hosted installation reads the game's address ranges " +
                  "from that file.");
        }

        _profile = ProfileMerge.Merge(bundles);
        _profileSource = source;
        var games = string.Join(", ", _profile.Games.Select(g => g.Name));

        if (licensed && source != "pushed")
        {
            // Loud, because it is the silent failure this whole path exists to avoid: the tunnel
            // will work perfectly on ranges that may be months old.
            _log($"Loaded the {source} profile from {chosen} ({games}). A licence server is configured but " +
                 "nothing has been pushed yet - sign in so the app can fetch the current one.");
        }
        else
        {
            _log($"Loaded the {source} profile from {chosen} ({games})");
        }
        ApplySelfHostedRelay();
    }

    private static ProfileBundle ParseProfile(string json, string path) =>
        JsonSerializer.Deserialize(json, ProfileJsonContext.Default.ProfileBundle)
        ?? throw new InvalidOperationException($"The profile at {path} is not valid.");

    // -------------------------------------------------------------- connect

    /// <summary>
    /// Turns automatic time on with Cloudflare and keeps the clock inside the relays' window, on
    /// every Connect. See ClockKeeper for what it changes and what it never does.
    /// </summary>
    private ClockKeeper Clock => _clockKeeper ??= new ClockKeeper(() => _config.LicenceUrl, _log);
    private ClockKeeper? _clockKeeper;

    public async Task ConnectAsync(string? relayId, string? gameId, CancellationToken ct)
    {
        if (_state is TunnelState.Connected or TunnelState.Connecting) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _error = null;

        try
        {
            SetState(TunnelState.Connecting, new StatusText("svc.preparing", "Preparing..."));
            var phases = new PhaseTimer();

            // Started first and awaited just before the first handshake, so the measurement runs
            // while the profile loads. See ClockKeeper: automatic time with Cloudflare is switched
            // on every time, whatever the clock says.
            var clockCheck = Clock.PrepareAsync(token);

            // The licence gate, before anything is created and before a packet is sent. An
            // expired subscription is a refusal the relay would make anyway; making it here as
            // well turns a four-attempt timeout into a sentence that says what to do. See
            // LicenceRefusal for why this is not the same question as Configured.
            if (LicenceRefusal() is { } refusal)
            {
                throw new InvalidOperationException(refusal.English);
            }

            // Reload every time the user connects. This used to be "load it once and keep it",
            // which meant rebuilding the profile changed nothing until the service was restarted,
            // and nothing said so: the log still reported a route count, just the old one. The
            // only clue was that the number disagreed with the file on disk.
            try
            {
                await LoadProfileAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (_profile is not null)
            {
                // A profile we cannot re-read is not a reason to refuse a connection when we
                // already have a good copy in hand.
                _log($"Could not reload the profile ({ex.Message}) - continuing with the one already loaded.");
            }

            phases.Mark("profile");
            _game = ChooseGameForConnect(gameId);
            var psk = System.Text.Encoding.UTF8.GetBytes(_config.Psk);

            // Every relay compares the handshake's timestamp with its own clock, so a clock outside
            // the window fails every one of them, silently. Correct it BEFORE the first handshake.
            if (await clockCheck.ConfigureAwait(false) is { } offset && offset.Duration() > ClockKeeper.CorrectAbove)
            {
                SetState(TunnelState.Connecting, new StatusText("svc.fixingClock", "Correcting the system clock..."));
                var after = await Clock.CorrectAsync(offset, token).ConfigureAwait(false);
                if (after is { } still && still.Duration() > GamePingBooster.Core.Protocol.GpbProtocol.HandshakeSkew)
                {
                    throw new ClockWrongException(still);
                }
            }
            phases.Mark("clock");

            // Choose the relay before creating anything. Probing is pure UDP - no adapter, no
            // routes - so a relay that turns out to be unreachable costs nothing but a timeout.
            SetState(TunnelState.Connecting, new StatusText("svc.measuring", "Measuring relays..."));
            ResetThroughputBaseline();
            _choiceAtConnect = RelayChoice;
            var preferred = string.IsNullOrWhiteSpace(relayId) ? RelayChoice : relayId;
            _chosenByHandAtConnect = preferred;
            (_relay, _tunnel) = await SelectRelayAsync(preferred, psk, token).ConfigureAwait(false);
            var endpoint = ParseEndpoint(_relay.Endpoint);
            var session = _tunnel.Session;
            phases.Mark("relays");

            SetState(TunnelState.Connecting, new StatusText("svc.creatingAdapter", "Creating the virtual adapter..."));
            _adapter = OpenAdapter();
            phases.Mark("adapter");

            // Pin the relay to the physical adapter BEFORE installing any route into the tunnel.
            _routes = new RouteManager(_log);
            _routes.PinRelayRoute(endpoint.Address);
            PinDoors();

            _routes.ConfigureAdapter(_adapter.InterfaceIndex, session.ClientIp, prefixLength: 24, session.Mtu);
            _tunnel.StartPumping(_adapter, token);

            // The lobby goes on the tunnel NOW, before the game exists, rather than with the game
            // routes below. See GameEntry.LobbyAddresses: the lobby is a TCP connection the game
            // opens in its first seconds, and one caught by a route after it opened is dropped by
            // the relay for carrying the wrong source address - a late lobby route hangs the lobby.
            InstallLobbyRoutes();
            phases.Mark("routing");

            // Watch EVERY game in the profile, so routes come and go with whichever one is opened.
            _watcher = new GameProcessWatcher(_profile!.Games.SelectMany(g => g.ProcessNames));
            _watcher.GameStateChanged += OnGameStateChanged;
            _watcher.Start();

            if (_config.RouteWithoutGame)
            {
                _log("routeWithoutGame = true - installing routes now without waiting for the game (debug mode).");
                InstallRoutes();
            }

            StartSupervisor(token);
            StartGamePingProbe(token);
            StartSpikeRecorder(token);
            phases.Mark("start");
            _log($"Connect took {phases}.");

            SetState(TunnelState.Connected,
                _watcher.IsGameRunning
                    ? new StatusText("svc.connectedAccelerating",
                        $"Connected to {_relay.Name} - accelerating {_game.Name}", _relay.Name, _game.Name)
                    : WaitingForGame(_relay.Name));
        }
        catch (Exception ex)
        {
            if (ex is ClockWrongException clock)
            {
                // Said in the detail line, in the person's language, rather than as a raw error.
                _error = null;
                SetState(TunnelState.Faulted, clock.Text);
            }
            else
            {
                _error = ex.Message;
                SetState(TunnelState.Faulted, new StatusText("svc.connectFailed", "Connection failed"));
            }
            _log($"Connection failed: {ex}");
            // The adapter goes too: it may be the reason, and the next connect should start from a new one.
            await TeardownAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Where a connect's or a disconnect's time went, for one log line: "5.1 s (profile 40 ms, relays 2.8 s,
    /// adapter 1.4 s, routing 12 ms, start 30 ms)". Each mark closes the phase since the previous one.
    /// Written because the only way to find the 27 seconds netsh cost was reading gaps between unrelated
    /// log lines.
    /// </summary>
    private sealed class PhaseTimer
    {
        private readonly long _started = System.Diagnostics.Stopwatch.GetTimestamp();
        private long _last;
        private readonly List<(string Name, TimeSpan Took)> _phases = [];

        public PhaseTimer() => _last = _started;

        public void Mark(string name)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            _phases.Add((name, System.Diagnostics.Stopwatch.GetElapsedTime(_last, now)));
            _last = now;
        }

        public override string ToString() =>
            $"{Format(System.Diagnostics.Stopwatch.GetElapsedTime(_started, _last))} (" +
            string.Join(", ", _phases.Select(p => $"{p.Name} {Format(p.Took)}")) + ")";

        private static string Format(TimeSpan t) =>
            t.TotalSeconds >= 1 ? $"{t.TotalSeconds:F1} s" : $"{t.TotalMilliseconds:F0} ms";
    }

    // ------------------------------------------------------- relay choice

    /// <summary>The relay chosen on the main window, or null for automatic. Saved as defaultRelayId.</summary>
    private string? RelayChoice => string.IsNullOrWhiteSpace(_config.DefaultRelayId) ? null : _config.DefaultRelayId;

    /// <summary>The choice this tunnel was connected with, so a change since can be told from a relay that did not answer.</summary>
    private volatile string? _choiceAtConnect;

    /// <summary>
    /// The relay this tunnel was connected to by hand - the app's choice, or one named in the connect command -
    /// or null when it was chosen automatically. Nothing ever moves such a tunnel to another relay.
    /// </summary>
    private volatile string? _chosenByHandAtConnect;

    /// <summary>Each relay's last ICMP round trip, by relay id - null when it did not answer. Read and written under its own lock.</summary>
    private readonly Dictionary<string, double?> _relayPings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ping round in progress, so a list opened twice in a second shares one round rather than starting two.</summary>
    private Task<List<RelayOption>>? _pinging;
    private readonly object _pingingGate = new();

    private const int RelayPingTimeoutMs = 1000;

    /// <summary>
    /// Pings every relay once, all at once, and returns the list with the results. For the main window's
    /// relay list, which asks when it opens and every few seconds while it stays open.
    ///
    /// ICMP over the player's own connection rather than the measurement a connect makes: that one opens a
    /// session on every relay, and doing it whenever a list is opened would leave a trail of them. An echo
    /// costs a relay nothing. Relays answer them - the spike recorder's relayWire signal is the same echo.
    /// It is only the first leg; automatic selection still compares the whole way to the game at connect.
    ///
    /// Safe with a tunnel up: the relay in use is pinned to the physical adapter, and no game range holds a
    /// relay's address (WarnAboutRoutedLandmarks and the builders see to that), so the echoes cannot fall
    /// into the tunnel.
    /// </summary>
    public Task<List<RelayOption>> PingRelaysAsync()
    {
        lock (_pingingGate)
        {
            if (_pinging is { IsCompleted: false } running) return running;
            return _pinging = PingRoundAsync();
        }
    }

    private async Task<List<RelayOption>> PingRoundAsync()
    {
        var relays = _profile?.Relays ?? [];
        var rounds = relays.Select(async relay =>
        {
            double? rtt = null;
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(ParseEndpoint(relay.Endpoint).Address, RelayPingTimeoutMs)
                    .ConfigureAwait(false);
                if (reply.Status == IPStatus.Success) rtt = reply.RoundtripTime;
            }
            catch (Exception)
            {
                // No route, a malformed endpoint, no ICMP: shown as no answer, and never a reason to fail the list.
            }
            lock (_relayPings) _relayPings[relay.Id] = rtt;
        });
        await Task.WhenAll(rounds).ConfigureAwait(false);
        return RelayOptions();
    }

    /// <summary>
    /// Every relay in the profile for the main window's choice, in profile order, with the last ping of each.
    /// The relay the tunnel is on shows the tunnel's own round trip instead: live, and the same leg.
    /// </summary>
    public List<RelayOption> RelayOptions()
    {
        var relays = _profile?.Relays ?? [];
        var current = _relay is { } relay && _tunnel?.LastRttMs is { } live
            ? (Id: RelayPaths.RelayIdOf(relay), Ms: live)
            : (Id: (string?)null, Ms: 0d);
        var game = GameForRelayList();

        lock (_relayPings)
        {
            return relays
                .Select(r => new RelayOption
                {
                    Id = r.Id,
                    Name = r.Name,
                    Location = r.Location,
                    PingMs = r.Id.Equals(current.Id, StringComparison.OrdinalIgnoreCase)
                        ? Math.Round(current.Ms)
                        : _relayPings.TryGetValue(r.Id, out var ms) && ms is { } value ? Math.Round(value) : null,
                    NotForGame = RelayPaths.Serves(r, game?.Id) ? null : game?.Name,
                })
                .ToList();
        }
    }

    /// <summary>
    /// Saves the relay to use from the next connect, or automatic for null or empty. Returns why not, or null.
    /// Only a relay the loaded profile lists can be chosen.
    /// </summary>
    public string? SetRelayChoice(string? relayId)
    {
        RelayEntry? relay = null;
        if (!string.IsNullOrWhiteSpace(relayId))
        {
            relay = _profile?.Relays.FirstOrDefault(r => r.Id.Equals(relayId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (relay is null) return $"There is no relay '{relayId.Trim()}' in the current server list.";

            // The list greys it out; this is for anything that asks over the pipe regardless.
            if (GameForRelayList() is { } game && !RelayPaths.Serves(relay, game.Id))
            {
                return $"{relay.Name} is not used for {game.Name}.";
            }
        }

        var previous = _config.DefaultRelayId;
        _config.DefaultRelayId = relay?.Id;
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            _config.DefaultRelayId = previous;
            return $"Could not save the relay choice: {ex.Message}";
        }

        _log(relay is null
            ? "Relay choice: automatic (the fastest). It applies from the next connect."
            : $"Relay choice: {relay.Name} [{relay.Id}]. It applies from the next connect.");
        StatusChanged?.Invoke(Snapshot());
        return null;
    }

    /// <summary>
    /// Why the tunnel is not on the chosen relay, when it is up and that is so; null otherwise. Worked out
    /// here, beside the state it describes, rather than in the UI from a relay id - which would be an
    /// entry's id whenever the tunnel came in through one.
    /// </summary>
    private StatusText? RelayChoiceNote()
    {
        var relay = _relay;
        if (relay is null || _state is not (TunnelState.Connected or TunnelState.Reconnecting)) return null;

        var choice = RelayChoice;
        if (!string.Equals(choice, _choiceAtConnect, StringComparison.OrdinalIgnoreCase))
        {
            return new StatusText("choiceNote.nextConnect", "Applies the next time you connect.");
        }
        if (choice is null || RelayPaths.RelayIdOf(relay).Equals(choice, StringComparison.OrdinalIgnoreCase)) return null;

        var chosen = _profile?.Relays.FirstOrDefault(r => r.Id.Equals(choice, StringComparison.OrdinalIgnoreCase));
        if (chosen is not null && _game is { } game && !RelayPaths.Serves(chosen, game.Id))
        {
            return new StatusText("choiceNote.notForGame",
                $"{chosen.Name} is not used for {game.Name} - using {relay.Name}.", chosen.Name, game.Name, relay.Name);
        }
        return chosen is null
            ? null
            : new StatusText("choiceNote.notAnswering",
                $"{chosen.Name} is not answering - using {relay.Name} instead.", chosen.Name, relay.Name);
    }

    /// <summary>
    /// Connected, with no game open yet. Two keys rather than one with a "a supported game" argument
    /// stuffed into it: that phrase is words, and words are the UI's job to translate.
    /// </summary>
    private StatusText WaitingForGame(string relayName) =>
        OnlyGameName is { } only
            ? new StatusText("svc.connectedWaiting",
                $"Connected to {relayName} - waiting for {only} to start", relayName, only)
            : new StatusText("svc.connectedWaitingAny",
                $"Connected to {relayName} - waiting for {GamesLabel} to start", relayName);

    // ------------------------------------------------------- authentication

    /// <summary>
    /// Why this installation may not connect right now, or null when it may.
    ///
    /// This is the LICENCE gate, and it is deliberately separate from Configured: an
    /// installation whose subscription has lapsed is not misconfigured, and sending that person
    /// to the settings screen - which is what an unconfigured installation does - would be the
    /// wrong destination. They need to renew and sign in again.
    ///
    /// The rule has exactly two escapes, and both are the same idea: the licence pays for the
    /// vendor's relays, so where those are not in play there is nothing for it to gate.
    ///
    ///   - no licence server configured. A self-hosted installation, which is the default and
    ///     has never had a licence to expire.
    ///   - the user's own relays are set. ApplySelfHostedRelay REPLACES the profile's list with
    ///     them, so on this connect the vendor's relays are not being used at all; the PSK is
    ///     the credential for the machines the user is paying for themselves.
    ///
    /// Anything else - a licensed installation reaching for the relays the profile lists -
    /// needs a licence token that has not run out. This check is COURTESY, not enforcement:
    /// the relay verifies the token offline against the licence server's public key and
    /// refuses an expired one with StatusCredentialExpired, and that half cannot be patched
    /// out by anybody sitting at this machine. What this buys is that the refusal arrives
    /// immediately, in words, instead of as four handshake attempts and a timeout.
    /// </summary>
    private StatusText? LicenceRefusal()
    {
        if (string.IsNullOrWhiteSpace(_config.LicenceUrl)) return null;
        if (_config.RelayEndpoints.Count > 0) return null;

        var token = _token;
        if (token is null)
        {
            return new StatusText("refusal.notSignedIn",
                "This installation connects through a licensed relay and is not signed in. " +
                "Sign in from the menu to get a licence.");
        }

        var expiry = TokenStore.ExpiryOf(token);
        if (expiry <= DateTimeOffset.UtcNow)
        {
            var when = expiry.ToLocalTime().ToString("g", CultureInfo.InvariantCulture);
            return new StatusText("refusal.expired",
                $"The licence expired {when}. Renew the subscription and " +
                "sign in again - the relay will not accept an expired licence.",
                when);
        }

        return null;
    }

    /// <summary>
    /// Decides how to authenticate to ONE relay, and says so in the log.
    ///
    /// Token mode needs three things at once: a stored token, a device key, and a public key for
    /// this particular relay. The order matters: a self-hosted endpoint has no public key and
    /// must therefore keep taking the PSK path exactly as it always has, even on a machine that
    /// has signed in and holds a perfectly good token.
    ///
    /// What it must NOT do is fall back to the PSK on a LICENSED installation. That was the
    /// original behaviour and it was wrong twice over: a relay that publishes a public key runs
    /// in token mode and never answers a PSK handshake, so the fallback could only ever produce
    /// four attempts, an eight-second wait and a message naming four possible causes; and on a
    /// machine that happens to still hold a PSK - every development machine does - it turned
    /// "your licence expired" into a connection that quietly worked, which is exactly the hole
    /// this whole path exists to close.
    /// </summary>
    private TunnelAuth AuthFor(RelayEntry relay, byte[] psk)
    {
        // No key published: a PSK endpoint. That is what a self-hoster's typed-in address is,
        // and it must keep working untouched on a machine that also holds a licence.
        if (string.IsNullOrWhiteSpace(relay.PublicKey)) return TunnelAuth.FromPsk(psk);

        var licensed = !string.IsNullOrWhiteSpace(_config.LicenceUrl);
        var token = _token;

        if (token is null)
        {
            return NotTokenMode(relay, psk, licensed,
                $"{relay.Name} authenticates with a licence and this installation holds none.",
                "Sign in from the menu.");
        }

        var expiry = TokenStore.ExpiryOf(token);
        if (expiry <= DateTimeOffset.UtcNow)
        {
            return NotTokenMode(relay, psk, licensed,
                $"The licence expired {expiry.ToLocalTime():g}.",
                "Renew the subscription and sign in again.");
        }

        try
        {
            return TunnelAuth.FromToken(token, _device.Key, relay.PublicKey!);
        }
        catch (Exception ex)
        {
            // A bad public key in the profile. Say which relay, because the profile may list
            // several and the message is otherwise unactionable.
            return NotTokenMode(relay, psk, licensed,
                $"{relay.Name}: the relay public key in the profile is not usable ({ex.Message}).",
                "The profile needs replacing; sign in again to fetch a current one.");
        }
    }

    /// <summary>
    /// What to do when a relay asked for token mode and token mode is not available.
    ///
    /// On a licensed installation this THROWS rather than returning a PSK. Both call sites -
    /// relay selection and the failover loop - already catch per relay, so the message lands
    /// against the relay it is about instead of becoming a timeout that blames the network.
    ///
    /// On a self-hosted installation it stays a fallback and a log line. Somebody who put a
    /// public key in a profile of their own making and is not using a licence server has some
    /// reason for it, and refusing to connect would take away a setup that worked.
    /// </summary>
    private TunnelAuth NotTokenMode(RelayEntry relay, byte[] psk, bool licensed, string why, string next)
    {
        if (licensed) throw new InvalidOperationException($"{why} {next}");

        _log($"{why} Using the pre-shared key for {relay.Name} instead.");
        return TunnelAuth.FromPsk(psk);
    }

    // ------------------------------------------------------- relay selection

    /// <summary>
    /// Picks a relay by measuring it, over the whole path the player's packets will take.
    ///
    /// The player's ping is two legs - player to relay, relay to game server - and until
    /// 2026-09-05 this method could only see the first. It chose on that alone, which is right
    /// only when every relay is the same distance from the game, and a tester proved it is not:
    /// 23 ms to Hong Kong beat 45 ms to Singapore, the game put him on a Singapore datacentre
    /// anyway, and he played at 70-80 ms on the relay that measured better. The second leg was
    /// the whole difference and nothing here could see it.
    ///
    /// So there are now two measurements:
    ///
    ///   1. <see cref="LandmarkProbe.RankRegionsAsync"/> over the physical path, to find which
    ///      region the game will put this player in. The game decides that by probing the same
    ///      endpoints over the same path, so measuring it the same way is not a guess.
    ///   2. an ICMP echo through each candidate tunnel to that region's landmark, which is the
    ///      end-to-end number - both legs, plus the relay's own forwarding cost.
    ///
    /// The handshake round trip is still taken and still reported, because it is the only way to
    /// show the two legs separately and it is what a player recognises. It is no longer what the
    /// choice is made on, unless the end-to-end number is unavailable - see
    /// <see cref="ChooseByEndToEnd"/> for when that happens and why it falls back wholesale.
    ///
    /// Probing is sequential on purpose. Running the probes in parallel would have them compete
    /// for the same uplink and inflate each other's numbers, which defeats the point.
    ///
    /// ENTRIES are a second round, run only when the first did not help: when no relay beats the
    /// player's own connection to the game by <see cref="RelayPaths.HelpMargin"/>. An entry is a
    /// forwarder in front of a relay - the same relay reached by another road - for lines whose route
    /// abroad is the problem. A VNTT line on 2026-09-13 left Vietnam through Hong Kong and reached
    /// Singapore in 68 ms direct and 71 through our relay, while a datacentre in Ho Chi Minh City
    /// reached that relay in 39. Players on such lines do not report it; they see no change and
    /// leave, which is why this is automatic. A line the relays already help never measures one.
    ///
    /// TWO PATHS TO ONE RELAY ARE NEVER OPEN AT ONCE. relayd keeps one session per device and
    /// answers a second handshake with that same session, moved to the new address. A client that
    /// then closed the path it did not pick with a Disconnect - which relayd accepts from the
    /// session's current address - would end the session of the path it did pick, and the tunnel
    /// would come up silent. So a relay's own probe is closed WITHOUT a Disconnect before an entry
    /// to it is measured, and reopened if the relay wins after all: the handshake resumes the same
    /// session. See <see cref="CloseProbesOf"/> and <see cref="OpenChosenAsync"/>.
    /// </summary>
    private async Task<(RelayEntry Relay, TunnelClient Tunnel)> SelectRelayAsync(
        string? preferredId, byte[] psk, CancellationToken ct)
    {
        if (_profile!.Relays.Count == 0) throw new InvalidOperationException("The profile declares no relays.");
        var relays = RelaysForGame(_game);
        var paths = RelayPaths.Expand(relays);

        // A relay the operator has taken off this game is not used for it, chosen or not. The choice stays
        // saved: it is one choice for every game, and the next game may be one this relay carries.
        if (!string.IsNullOrWhiteSpace(preferredId) &&
            _profile.Relays.Concat(RelayPaths.Expand(_profile.Relays))
                .FirstOrDefault(r => r.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase)) is { } picked &&
            !RelayPaths.Serves(picked, _game?.Id))
        {
            _log($"{picked.Name} [{picked.Id}], the relay chosen in the app, is not used for {_game?.Name} - choosing " +
                 "the fastest of the ones that are.");
            preferredId = null;
        }

        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            // A relay chosen on the main window: measured with the ways into it, exactly as the automatic
            // rule would measure it, so an entry in front of it can still win. When nothing about it
            // answers, the player gets the fastest relay rather than no connection - the status says so
            // (RelayChoiceNote), and the choice stays saved for the next connect.
            var chosen = relays.FirstOrDefault(r => r.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase));
            if (chosen is not null)
            {
                _log($"Using {chosen.Name} [{chosen.Id}], the relay chosen in the app.");
                try
                {
                    return await SelectAmongAsync([chosen], RelayPaths.Expand([chosen]), psk, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log($"{chosen.Name}, the relay chosen in the app, did not answer ({ex.Message}) - " +
                         "choosing the fastest relay instead.");
                    return await SelectAmongAsync(relays, paths, psk, ct).ConfigureAwait(false);
                }
            }

            // An entry pinned by id in config.json: the override for a player the automatic rule gets
            // wrong. Never offered in the app, and taken as it is.
            var pinned = paths.FirstOrDefault(r => r.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase));
            if (pinned is not null)
            {
                var client = new TunnelClient(ParseEndpoint(pinned.Endpoint), AuthFor(pinned, psk), _clientId, _log);
                await client.HandshakeAsync(attempts: 4, ct).ConfigureAwait(false);
                await ReportBothLegsAsync(pinned, client, ct).ConfigureAwait(false);
                return (pinned, client);
            }
            _log($"The profile has no relay '{preferredId}' - measuring all of them instead.");
        }

        return await SelectAmongAsync(relays, paths, psk, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The automatic rule, over <paramref name="candidates"/> and the entries in <paramref name="paths"/>:
    /// every relay in the profile, or the one the player chose with its entries.
    /// </summary>
    private async Task<(RelayEntry Relay, TunnelClient Tunnel)> SelectAmongAsync(
        List<RelayEntry> candidates, List<RelayEntry> paths, byte[] psk, CancellationToken ct)
    {
        if (candidates.Count == 1 && paths.Count == 0)
        {
            var only = candidates[0];
            var client = new TunnelClient(ParseEndpoint(only.Endpoint), AuthFor(only, psk), _clientId, _log);
            await client.HandshakeAsync(attempts: 4, ct).ConfigureAwait(false);
            await ReportBothLegsAsync(only, client, ct).ConfigureAwait(false);
            return (only, client);
        }

        var target = await ChooseTargetRegionAsync(ct).ConfigureAwait(false);

        var probes = new List<RelayProbe>();
        try
        {
            foreach (var relay in candidates)
            {
                if (await ProbeAsync(relay, psk, target, ct).ConfigureAwait(false) is { } probe) probes.Add(probe);
            }

            if (paths.Count > 0 && ShouldTryEntries(probes, target))
            {
                foreach (var path in paths)
                {
                    CloseProbesOf(probes, RelayPaths.RelayIdOf(path));
                    if (await ProbeAsync(path, psk, target, ct).ConfigureAwait(false) is { } probe) probes.Add(probe);
                }
            }


            // An entry that could not reach the landmark cannot be scored, and letting it in would drop
            // the whole comparison to the first leg (see ChooseByEndToEnd) - which an entry a few
            // milliseconds from the player wins by construction. Relays keep the old all-or-nothing
            // rule; an unscored entry is simply left out.
            var scored = target is null
                ? probes
                : probes.Where(p => p.Relay.ViaRelayId is null || p.EndToEndMs is not null).ToList();

            if (scored.Count == 0)
            {
                throw new InvalidOperationException(
                    $"None of the {candidates.Count} relays in the profile answered. Check the network, " +
                    "the endpoints in the profile, and that the PSK matches.");
            }

            var best = ChooseByEndToEnd(scored, target);
            var tunnel = await OpenChosenAsync(best, probes, psk, ct).ConfigureAwait(false);
            return (best.Relay, tunnel);
        }
        catch
        {
            foreach (var probe in probes)
            {
                probe.Client?.Dispose();
                probe.Client = null;
            }
            throw;
        }
    }

    /// <summary>
    /// Handshakes with one relay or entry and measures both legs through it, or logs why it could
    /// not and returns null. The probe it returns holds the tunnel open.
    /// </summary>
    private async Task<RelayProbe?> ProbeAsync(RelayEntry relay, byte[] psk, LandmarkProbe.Result? target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TunnelClient? client = null;
        try
        {
            client = new TunnelClient(ParseEndpoint(relay.Endpoint), AuthFor(relay, psk), _clientId, _log);
            await client.HandshakeAsync(attempts: 2, ct).ConfigureAwait(false);

            // Both legs measured the same way, best of three. The handshake RTT is still
            // taken and still logged, but it is one sample, and subtracting one sample from a
            // best-of-three echo is what made the second leg come out as zero - see
            // MeasureRelayRttAsync. It stays as the fallback for a relay that answers a
            // handshake but not a ping.
            var legOne = await client.MeasureRelayRttAsync(attempts: 3, ct).ConfigureAwait(false)
                         ?? client.HandshakeRttMs;

            double? endToEnd = null;
            if (target is not null)
            {
                endToEnd = await client.MeasureThroughTunnelAsync(target.Landmark, attempts: 3, ct)
                    .ConfigureAwait(false);
            }

            _log($"  {relay.Name} [{relay.Id}] ({relay.Location}): {Describe(legOne, endToEnd, target)}");
            return new RelayProbe(relay, legOne, endToEnd) { Client = client };
        }
        catch (OperationCanceledException)
        {
            client?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            _log($"  {relay.Name} [{relay.Id}] ({relay.Location}): unreachable - {ex.Message}");

            // An entry's session is its relay's session. A Disconnect from it would also end the
            // relay's own probe, closed a moment ago precisely so that it could be reopened.
            if (relay.ViaRelayId is null) client?.Dispose();
            else Abandon(client);
            return null;
        }
    }

    /// <summary>
    /// Whether the entries are worth a second round, saying why in the log when they are.
    ///
    /// Only when the relays could be judged - every one measured end to end against a region - and
    /// none beat the player's own connection by enough to matter. Or when no relay answered at all,
    /// since then there is nothing to lose. Relays that could not be judged are not a reason: an entry
    /// has no fair number to be compared with.
    /// </summary>
    private bool ShouldTryEntries(List<RelayProbe> probes, LandmarkProbe.Result? target)
    {
        if (probes.Count == 0)
        {
            _log("No relay answered directly - trying the entries in front of them.");
            return true;
        }
        if (target is null || probes.Any(p => p.EndToEndMs is null)) return false;

        var best = probes.Min(p => p.EndToEndMs!.Value);
        if (!RelayPaths.WorthTryingEntries(target.RttMs, best)) return false;

        _log($"The fastest relay is {best:F0} ms end to end to {target.RegionName}, against {target.RttMs:F0} ms " +
             "on your own connection - not enough of a difference. Trying the entries that reach the same " +
             "relays by another route.");
        return true;
    }

    /// <summary>
    /// Closes every open probe that ends at <paramref name="relayId"/>, WITHOUT a Disconnect, so the
    /// next path to that relay can be measured alone. The relay keeps the session, and a later
    /// handshake on any path to it resumes that session rather than taking a second address.
    /// </summary>
    private static void CloseProbesOf(List<RelayProbe> probes, string relayId)
    {
        foreach (var probe in probes)
        {
            if (probe.Client is null) continue;
            if (!RelayPaths.RelayIdOf(probe.Relay).Equals(relayId, StringComparison.OrdinalIgnoreCase)) continue;
            Abandon(probe.Client);
            probe.Client = null;
        }
    }

    /// <summary>
    /// Closes every probe but the winner and hands back the winner's tunnel, reopening it first if it
    /// was closed to make way for an entry to the same relay.
    ///
    /// Probes to OTHER relays go with a Disconnect, as they always have: each is a separate relayd,
    /// and its address should go back to its pool now. A probe that ends at the SAME relay as the
    /// winner goes without one - it shares the winner's session, and a Disconnect from it would end it.
    /// </summary>
    private async Task<TunnelClient> OpenChosenAsync(RelayProbe best, List<RelayProbe> probes, byte[] psk, CancellationToken ct)
    {
        var winner = RelayPaths.RelayIdOf(best.Relay);
        foreach (var probe in probes)
        {
            if (ReferenceEquals(probe, best) || probe.Client is null) continue;
            if (RelayPaths.RelayIdOf(probe.Relay).Equals(winner, StringComparison.OrdinalIgnoreCase))
            {
                Abandon(probe.Client);
            }
            else
            {
                probe.Client.Dispose();
            }
            probe.Client = null;
        }

        if (best.Client is { } open)
        {
            best.Client = null;
            return open;
        }

        _log($"Reopening {best.Relay.Name} - it was closed while an entry to the same relay was measured.");
        var client = new TunnelClient(ParseEndpoint(best.Relay.Endpoint), AuthFor(best.Relay, psk), _clientId, _log);
        try
        {
            await client.HandshakeAsync(attempts: 4, ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One path measured during selection. <see cref="Client"/> is the open tunnel, and null once it
    /// has been closed - to make way for another path to the same relay, or because it lost.
    /// </summary>
    private sealed class RelayProbe(RelayEntry relay, double legOneMs, double? endToEndMs)
    {
        public RelayEntry Relay { get; } = relay;
        public double LegOneMs { get; } = legOneMs;
        public double? EndToEndMs { get; } = endToEndMs;
        public TunnelClient? Client { get; set; }
    }

    /// <summary>
    /// What the chosen relay measured on the way to the game's datacentre, kept for the status.
    ///
    /// <c>Offset</c> is the relay-to-datacentre leg, derived by subtracting the first leg from the
    /// end-to-end measurement rather than measured on its own - so the relay's forwarding cost is
    /// inside it. Adding it to the LIVE first leg gives a live estimate of the player's in-game
    /// ping, and it is a fair one because the part that moves is the player's own connection: the
    /// leg between two datacentres jittered 0.07 ms over five echoes.
    ///
    /// The direct path - the same destination over the player's own connection - is measured too,
    /// but it is not kept here: it only ever fed a UI row that has since been removed. It still
    /// does the two jobs that matter, both at connect time: it picks the target region, and it is
    /// what RecordPath compares against to say in the log when the tunnel is not helping.
    /// </summary>
    /// <summary>
    /// <paramref name="Landmark"/> is kept as well as the offset so the probe loop has something
    /// known-answering to test itself against. Without it, a session where the in-game ping never
    /// becomes a measurement leaves two possible causes and no way to tell them apart: the game
    /// server filters ICMP, or probing through a live tunnel does not work at all.
    /// </summary>
    private sealed record PathMeasurement(string RegionName, double Offset, IPAddress Landmark);

    private volatile PathMeasurement? _path;

    /// <summary>
    /// Measures and logs both legs for a relay that was not chosen by comparison - the only one
    /// in the profile, or the one the user pinned.
    ///
    /// There is no decision to make here, so this changes nothing about what happens next. It
    /// exists because "the app says 23 ms and the game says 75" is the question this whole
    /// mechanism was built to answer, and a player who has pinned a relay is the most likely
    /// person to be asking it. Failures are swallowed: a diagnostic must never be the reason a
    /// connection does not happen.
    /// </summary>
    private async Task ReportBothLegsAsync(RelayEntry relay, TunnelClient client, CancellationToken ct)
    {
        try
        {
            _path = null;
            var target = await ChooseTargetRegionAsync(ct).ConfigureAwait(false);
            if (target is null) return;

            var legOne = await client.MeasureRelayRttAsync(attempts: 3, ct).ConfigureAwait(false)
                         ?? client.HandshakeRttMs;

            var endToEnd = await client.MeasureThroughTunnelAsync(target.Landmark, attempts: 3, ct)
                .ConfigureAwait(false);
            if (endToEnd is null)
            {
                _log($"{relay.Name} could not reach {target.RegionName} with an echo, so the second " +
                     "leg is unknown. In-game ping will be higher than the relay figure by however " +
                     "far the relay is from the game server.");
                return;
            }

            _log($"{relay.Name}: {legOne:F0} ms to the relay, {endToEnd.Value:F0} ms " +
                 $"end to end to {target.RegionName} - that second number is roughly what the game " +
                 "will show.");
            RecordPath(target, legOne, endToEnd.Value);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log($"Could not measure the path to the game region: {ex.Message}");
        }
    }

    /// <summary>
    /// Picks the winner, and says in the log which number decided it.
    ///
    /// The fallback is all or nothing on purpose. Scoring one relay on its end-to-end time and
    /// another on its handshake compares a two-leg number against a one-leg number, and the
    /// one-leg number is always smaller - so a relay that failed to answer a landmark echo would
    /// win every comparison by failing. Sorting that out relay by relay is not possible, so the
    /// moment any relay is missing an end-to-end number the whole comparison drops back to the
    /// first leg, and the log says so.
    /// </summary>
    private RelayProbe ChooseByEndToEnd(List<RelayProbe> probes, LandmarkProbe.Result? target)
    {
        if (target is not null && probes.All(p => p.EndToEndMs is not null))
        {
            var best = probes.OrderBy(p => p.EndToEndMs!.Value).First();
            _log($"Chose {best.Relay.Name} [{best.Relay.Id}] at {best.EndToEndMs!.Value:F0} ms " +
                 $"end to end to {target.RegionName} ({best.LegOneMs:F0} ms of that is the relay leg).");
            RecordPath(target, best.LegOneMs, best.EndToEndMs.Value);
            return best;
        }

        if (target is null)
        {
            _log("No region could be measured, so the relays are compared on the first leg only. " +
                 "The profile declares no landmark for any region, or none of them answered - " +
                 "and pick a region that is worse both ways.");
        }
        else
        {
            var silent = probes.Where(p => p.EndToEndMs is null).Select(p => p.Relay.Id);
            _log($"No end-to-end time through {string.Join(", ", silent)}, so every relay is " +
                 "compared on the first leg only. Whichever relay is nearest the game server " +
                 "cannot be told apart this way - if this persists, the relay is not forwarding " +
                 "ICMP and the landmark cannot be reached through it.");
        }

        _path = null;
        var fallback = probes.OrderBy(p => p.LegOneMs).First();
        _log($"Chose {fallback.Relay.Name} [{fallback.Relay.Id}] at {fallback.LegOneMs:F0} ms " +
             "(leg 1 only - see GameServerTally).");
        return fallback;
    }

    /// <summary>
    /// Keeps the chosen relay's path measurement for the status, and says plainly when the tunnel
    /// is not worth using.
    ///
    /// That last part is the point. Everything else here makes the app choose the BEST relay; it
    /// says nothing about whether the best relay is any good. A player whose ISP already has a
    /// clean path to the datacentre can be slower through every relay we own, and until now the
    /// app would have shown "Connected" and a healthy-looking relay ping while quietly costing
    /// him 19 ms. Now there is a number to compare against and the log says so.
    /// </summary>
    private void RecordPath(LandmarkProbe.Result target, double legOne, double endToEnd)
    {
        // Clamped at zero, because the two legs are NOT measured with equal rigour and the
        // difference between them is not a pure physical quantity.
        //
        // legOne is a single sample - whichever handshake attempt happened to succeed. endToEnd is
        // the BEST of three echoes. On a jittery connection an unlucky handshake against a lucky
        // echo makes the subtraction negative, and a negative offset would put "Ping in game"
        // BELOW "Ping to relay" on the screen: a number that cannot happen, since the echo travels
        // the relay leg too. This connection already measures 46/46 to Singapore, so it sits right
        // on that boundary rather than safely away from it.
        //
        // Making legOne a best-of-three too would cost three handshakes per relay, and each one is
        // a session and an address out of the relay's 253-address pool. Not worth it for a number
        // whose honest reading at this point is "the relay is effectively at the datacentre".
        var offset = endToEnd - legOne;
        if (offset < 0)
        {
            // Worth a line rather than silence: it means the first leg is jittery enough that the
            // in-game estimate is soft, which is the sort of thing to know before trusting it.
            _log($"The relay leg measured {legOne:F0} ms and the whole path {endToEnd:F0} ms, which " +
                 $"cannot be - the echo travels the relay leg too. Treating the second leg as zero. " +
                 "A single handshake sample against the best of three echoes does this on a jittery " +
                 "connection.");
            offset = 0;
        }
        _path = new PathMeasurement(target.RegionName, offset, target.Landmark);

        var saved = target.RttMs - endToEnd;
        if (saved >= 1)
        {
            _log($"Through the tunnel: {endToEnd:F0} ms to {target.RegionName}, against " +
                 $"{target.RttMs:F0} ms on your own connection - {saved:F0} ms faster.");
        }
        else
        {
            _log($"WARNING: the tunnel is NOT helping for {target.RegionName}. Through the relay " +
                 $"is {endToEnd:F0} ms; your own connection reaches the same datacentre in " +
                 $"{target.RttMs:F0} ms. Your ISP already has the better route today, and the game " +
                 "will play better with the booster off. This is worth knowing rather than hiding: " +
                 "a relay nearer the game server, or a different one, is the only thing that fixes it.");
        }
    }

    private static string Describe(double legOne, double? endToEnd, LandmarkProbe.Result? target)
    {
        if (target is null) return $"{legOne:F0} ms to the relay";
        return endToEnd is null
            ? $"{legOne:F0} ms to the relay, no answer from {target.RegionName} through it"
            : $"{legOne:F0} ms to the relay, {endToEnd.Value:F0} ms on to {target.RegionName}";
    }

    /// <summary>
    /// Which of the game's regions this player will be put in, measured over the physical path.
    ///
    /// Not read from configuration, because the player does not choose it - the game does, from
    /// its own probes over its own path, and the only way to agree with it is to measure the
    /// same thing the same way. Null when no region can be measured at all, which sends relay
    /// selection back to the first leg.
    /// </summary>
    private async Task<LandmarkProbe.Result?> ChooseTargetRegionAsync(CancellationToken ct)
    {
        var regions = _game?.Regions ?? [];
        if (regions.Count == 0 || regions.All(r => r.Landmarks.Count == 0)) return null;

        var ranked = await LandmarkProbe.RankRegionsAsync(regions, _log, ct).ConfigureAwait(false);
        if (ranked.Count == 0) return null;

        var target = ranked[0];
        _log("Game region, measured over your own connection (this is what the game measures too): " +
             string.Join(", ", ranked.Select(r => $"{r.RegionName} {r.RttMs:F0} ms")) +
             $" - so the game will use {target.RegionName}. Relays are compared on the way there.");
        return target;
    }

    // ---------------------------------------------------------- reconnection

    /// <summary>
    /// How long the relay may stay silent - no pong and no game packet - before the tunnel is presumed
    /// dead. Keepalives go out every second, so this is fifteen missed answers - long enough to ride out
    /// a hiccup, short enough that a player notices the reconnect rather than a dead game.
    /// </summary>
    private static readonly TimeSpan SilenceBeforeDead = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a tunnel may stay up with no game open before it is dropped.
    ///
    /// A relay holds a session - and an address from its pool of 253 - for every client connected
    /// to it, and keepalives stop it ever expiring on its own. An app left connected overnight, or
    /// connected at start-up by somebody who then never plays, holds a slot a player could use. An
    /// hour is long enough to cover a break between matches and short enough that a forgotten
    /// connection does not last the day.
    /// </summary>
    private static readonly TimeSpan IdleDisconnectAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// When the tunnel last became idle - connected, no game running - as Environment.TickCount64,
    /// or 0 while a game runs. Written and read only by the supervisor loop.
    /// </summary>
    private long _idleSinceTick;

    /// <summary>
    /// Whether the tunnel has had no game open for <see cref="IdleDisconnectAfter"/>.
    ///
    /// Asks the watcher on every tick rather than being told by game start and exit events. The
    /// watcher is started just before the first tick and may report a game that was already open a
    /// moment after connect; a counter set at connect would race it and could drop a player mid-match
    /// an hour later. TickCount64 keeps counting while the machine sleeps, so a laptop that wakes
    /// after a night still connected is dropped on the first tick, which is the point.
    /// </summary>
    private bool IdleForTooLong()
    {
        // Debug mode routes without a game on purpose; there is nothing idle about it.
        if (_config.RouteWithoutGame || (_watcher?.IsGameRunning ?? false))
        {
            _idleSinceTick = 0;
            return false;
        }

        var now = Environment.TickCount64;
        if (_idleSinceTick == 0)
        {
            _idleSinceTick = now;
            return false;
        }
        return now - _idleSinceTick >= IdleDisconnectAfter.TotalMilliseconds;
    }

    private void StartSupervisor(CancellationToken ct) => _supervisor = Task.Run(() => SuperviseAsync(ct), ct);

    /// <summary>
    /// Watches for a tunnel that has gone quiet. Without this, a relay restart or a brief loss of
    /// connectivity leaves the UI reporting "Connected" over a tunnel that carries nothing - the
    /// worst possible failure, because it looks like the game's fault.
    /// </summary>
    private async Task SuperviseAsync(CancellationToken ct)
    {
        // A fresh count per connect. Left over from the last session, a tick from hours ago would
        // drop this one on its first pass.
        _idleSinceTick = 0;

        // Nor does a move asked for, or made, on the last connection carry over to this one.
        _pendingDoorMove = null;
        _movedFromDoor = null;
        _matchGap = new GamePingBooster.Core.Quality.MatchGap();
        _moveFollow = null;
        _moveOffForGame = false;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_state != TunnelState.Connected) continue;

                var tunnel = _tunnel;
                if (tunnel is null) continue;

                LogThroughput(tunnel);

                if (IdleForTooLong())
                {
                    _log($"No game has been open for {IdleDisconnectAfter.TotalMinutes:F0} minutes - disconnecting, " +
                         "so this relay slot goes to somebody who is playing.");

                    // Not awaited. Disconnecting cancels this loop and waits for it to finish, which from
                    // inside the loop would wait forever.
                    _ = Task.Run(() => DisconnectAsync(new StatusText("svc.idleDisconnect",
                        $"Disconnected automatically - no game was open for {IdleDisconnectAfter.TotalMinutes:F0} minutes. " +
                        "Press Connect before you play.",
                        IdleDisconnectAfter.TotalMinutes.ToString("F0", CultureInfo.InvariantCulture))));
                    return;
                }

                var silence = tunnel.SinceLastHeard;
                if (silence < SilenceBeforeDead)
                {
                    // Here and nowhere else, so a move between ways into the relay can never run beside a
                    // reconnect: this loop is the only thing that ever replaces or moves the tunnel.
                    if (MovedBackAfterSilence(tunnel, silence)) continue;
                    if (Interlocked.Exchange(ref _pendingDoorMove, null) is { } door) MoveToDoor(tunnel, door, rollback: false);

                    // Awaited here, for the same reason: a move to another relay is a tunnel replaced.
                    if (_moveOffForGame)
                    {
                        _moveOffForGame = false;
                        await RescanBetweenMatchesAsync(tunnel, ct, forGame: true).ConfigureAwait(false);
                        continue;
                    }
                    await WatchForMatchGapAsync(tunnel, ct).ConfigureAwait(false);
                    continue;
                }
                _pendingDoorMove = null;
                _movedFromDoor = null;

                _log($"No answer from the relay for {silence.TotalSeconds:F0}s - reconnecting.");
                await ReconnectAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown. A move still being followed is written as far as it got.
            FollowRelayMove("disconnected");
        }
    }

    // -------------------------------------------------- in-game ping, measured

    /// <summary>How long a probe may take. Under the tick, so two are never in flight at once.</summary>
    private const int ProbeTimeoutMs = 800;

    /// <summary>Unanswered probes before the headline falls back to the landmark estimate.</summary>
    private const int ProbeGiveUpAfter = 5;

    /// <summary>How long to leave a silent server alone before trying it again.</summary>
    private const int ProbeRetryQuietMs = 30_000;

    /// <summary>Beyond this a reading is stale and the estimate takes the headline back.</summary>
    private static readonly TimeSpan DirectPingGoesStale = TimeSpan.FromSeconds(5);

    /// <summary>When a region's landmark last failed to answer, so a silent one is not asked every second.</summary>
    private readonly Dictionary<string, long> _regionRetryAfterTick = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan RegionRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Points the in-game estimate at the region the game is actually playing in, found from where its traffic
    /// goes.
    ///
    /// At connect the region is a prediction: the one the game would choose from its own probes, measured the same
    /// way over the player's connection. The game can disagree - a player who picks a server by hand, a queue
    /// that fills elsewhere - and the headline then read "~52 ms to Singapore" through a match played in Seoul.
    /// The busiest destination of game traffic settles it: whichever region's ranges hold that address is the
    /// region, and its landmark is measured through the live tunnel to replace the estimate's second leg.
    ///
    /// Only when a region can be told apart. An address inside no region's ranges, or a region with no landmark,
    /// leaves the estimate where it was. VALORANT is the case that cannot: Riot Direct answers every region on
    /// the same addresses, so its traffic says nothing about which server the player picked.
    /// </summary>
    private async Task FollowMatchRegionAsync(TunnelClient tunnel, IPAddress destination, CancellationToken ct)
    {
        var profile = _profile;
        if (profile is null) return;

        RegionEntry? region = null;
        foreach (var game in profile.Games)
        {
            region = game.Regions.FirstOrDefault(r => r.Cidrs.Any(c => IPNetwork.TryParse(c, out var net) && net.Contains(destination)));
            if (region is not null) break;
        }
        if (region is null || region.Landmarks.Count == 0) return;
        if (_path is { } known && known.RegionName == region.Name) return;

        var now = Environment.TickCount64;
        if (_regionRetryAfterTick.TryGetValue(region.Name, out var after) && now < after) return;

        foreach (var text in region.Landmarks)
        {
            if (!IPAddress.TryParse(text, out var landmark)) continue;

            double? best = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (await tunnel.ProbeGameServerAsync(landmark, ProbeTimeoutMs, ct).ConfigureAwait(false) is { } ms &&
                    (best is null || ms < best))
                {
                    best = ms;
                }
            }
            if (best is not { } endToEnd || tunnel.LastRttMs is not { } legOne) continue;

            // Same arithmetic as RecordPath, and the same clamp: the echo travels the relay leg too.
            var offset = Math.Max(0, endToEnd - legOne);
            var previous = _path?.RegionName;
            _path = new PathMeasurement(region.Name, offset, landmark);
            _regionRetryAfterTick.Remove(region.Name);
            _log(previous is null
                ? $"The match is in {region.Name}: {endToEnd:F0} ms there through the tunnel. The in-game estimate follows it."
                : $"The match is in {region.Name}, not {previous} as predicted at connect: {endToEnd:F0} ms there through " +
                  "the tunnel. The in-game estimate follows it.");
            return;
        }

        _regionRetryAfterTick[region.Name] = now + (long)RegionRetryAfter.TotalMilliseconds;
        _log($"The match is in {region.Name}, but its landmark did not answer through the tunnel - the in-game " +
             "estimate stays where it was for now.");
    }

    private void StartGamePingProbe(CancellationToken ct) =>
        _gamePingProbe = Task.Run(() => ProbeGamePingAsync(ct), ct);

    /// <summary>
    /// Starts the recorder that finds spikes while they happen and says where on the path each one
    /// was. See <see cref="SpikeRecorder"/>. It follows the engine through reconnects on its own, by
    /// reading <see cref="SpikeContext"/> every quarter second.
    /// </summary>
    private void StartSpikeRecorder(CancellationToken ct)
    {
        var recorder = new SpikeRecorder(SpikeContext, _log, RequestDoorMove);
        _recorder = recorder;
        _spikeRecorder = Task.Run(() => recorder.RunAsync(ct), ct);
    }

    private volatile SpikeRecorder? _recorder;

    /// <summary>A match is being recorded, so the connection-quality upload must wait.</summary>
    public bool QualityInMatch => _recorder?.InMatch ?? false;

    public bool QualitySharing => _config.ShareQuality;

    /// <summary>
    /// Turns the connection-quality upload on or off for this installation, and says why when it
    /// could not be saved. Off also empties the queue - nothing already recorded is sent afterwards.
    /// </summary>
    public string? SetQualitySharing(bool enabled)
    {
        var previous = _config.ShareQuality;
        _config.ShareQuality = enabled;
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            _config.ShareQuality = previous;
            return $"Could not save the setting: {ex.Message}";
        }

        QualityOutbox.Enabled = enabled;
        _log(enabled
            ? "Connection-quality sharing switched on: match summaries and spikes are sent between matches."
            : "Connection-quality sharing switched off: nothing more is queued, and the queue was emptied.");
        StatusChanged?.Invoke(Snapshot());
        return null;
    }

    private SpikeRecorder.Context SpikeContext()
    {
        var relay = _relay;
        var relayAddress = relay is not null && IPEndPoint.TryParse(relay.Endpoint, out var endpoint)
            ? endpoint.Address
            : null;
        var path = _path;
        var gameRunning = _config.RouteWithoutGame || (_watcher?.IsGameRunning ?? false);
        // A path through an entry is a RelayEntry whose Id is the entry's and ViaRelayId the relay's.
        // Recorded as the relay plus the entry, never as the entry alone: that split one relay's
        // spikes across as many rows as it has entries, and hid an incident on it.
        var relayId = relay is null ? null : RelayPaths.RelayIdOf(relay);
        var entryId = relay?.ViaRelayId is null ? null : relay.Id;
        var switching = _switching;
        var doors = relay is not null && _tunnel is not null && switching != EntrySwitchingMode.Off ? DoorsBeside(relay) : [];
        return new SpikeRecorder.Context(_tunnel, relayId, entryId, relay?.Name, relayAddress,
            path?.Landmark, path?.RegionName, _game?.Id, gameRunning, doors, switching == EntrySwitchingMode.On);
    }

    // ------------------------------------------------------------ moving between ways into the relay

    /// <summary>The way into the relay the switch policy asked for, until the supervisor's next pass takes it.</summary>
    private string? _pendingDoorMove;

    /// <summary>This connection's entry-switching mode, decided in <see cref="PinDoors"/>. Read by the recorder's thread.</summary>
    private volatile EntrySwitchingMode _switching = EntrySwitchingMode.Record;

    /// <summary>The way the tunnel was on before its last move, while that move is young enough to undo. Supervisor only.</summary>
    private string? _movedFromDoor;
    private long _movedAtTick;

    /// <summary>How long after a move silence on the new way sends the tunnel back, and how much silence that takes.</summary>
    private static readonly TimeSpan MoveBackWithin = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MoveBackSilence = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Undoes a move whose new way went silent. The policy only moves onto a way that answered nine probes in ten
    /// for the last thirty seconds, so this is rare - but the alternative is the supervisor's own rule, fifteen
    /// seconds of silence and then a reconnect, and a reconnect takes the game routes down and drops the match.
    /// Going back to the way the tunnel just came from keeps it.
    /// </summary>
    private bool MovedBackAfterSilence(TunnelClient tunnel, TimeSpan silence)
    {
        if (_movedFromDoor is not { } previous) return false;
        if (Environment.TickCount64 - _movedAtTick > MoveBackWithin.TotalMilliseconds)
        {
            _movedFromDoor = null;
            return false;
        }
        if (silence < MoveBackSilence) return false;

        _movedFromDoor = null;
        _log($"Entry switching: nothing has answered for {silence.TotalSeconds:F0} s since the move - going back to " +
             $"{previous} rather than waiting for the tunnel to be given up for dead.");
        MoveToDoor(tunnel, previous, rollback: true);
        return true;
    }

    /// <summary>The relay whose ways in are all pinned to the physical adapter, or null. See <see cref="PinDoors"/>.</summary>
    private volatile string? _doorsPinnedRelayId;

    // DoorsBeside's cache, touched only by the recorder's thread: one list object per relay, way and profile,
    // which is also how the recorder tells that its probes need rebuilding.
    private RelayEntry? _doorsFor;
    private object? _doorsProfile;
    private string? _doorsPinnedFor;
    private IReadOnlyList<DoorProbes.Door> _doors = [];

    /// <summary>
    /// The ways into the current relay the tunnel is NOT using - its entries, or the relay itself when the
    /// tunnel came in through an entry. Empty until they are pinned, so a probe never takes a game route.
    /// </summary>
    private IReadOnlyList<DoorProbes.Door> DoorsBeside(RelayEntry relay)
    {
        var profile = _profile;
        var pinned = _doorsPinnedRelayId;
        if (ReferenceEquals(relay, _doorsFor) && ReferenceEquals(profile, _doorsProfile) && pinned == _doorsPinnedFor)
        {
            return _doors;
        }

        var doors = new List<DoorProbes.Door>();
        var relayId = RelayPaths.RelayIdOf(relay);
        if (profile is not null && string.Equals(pinned, relayId, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var door in RelayPaths.DoorsOf(profile.Relays, relayId))
            {
                if (door.Id.Equals(relay.Id, StringComparison.OrdinalIgnoreCase)) continue;
                if (IPEndPoint.TryParse(door.Endpoint, out var endpoint) &&
                    endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    doors.Add(new DoorProbes.Door(door.Id, endpoint));
                }
            }
        }

        _doorsFor = relay;
        _doorsProfile = profile;
        _doorsPinnedFor = pinned;
        _doors = doors;
        return doors;
    }

    /// <summary>
    /// Pins every way into the relay in use to the physical adapter - the one the tunnel uses is pinned
    /// already - so a probe down another one can never follow a game route into the tunnel. Never fatal:
    /// a failure here only means the other ways go unmeasured on this connection.
    /// </summary>
    private void PinDoors()
    {
        var routes = _routes;
        var relay = _relay;
        var profile = _profile;
        _doorsPinnedRelayId = null;
        if (routes is null || relay is null || profile is null) return;

        try
        {
            var relayId = RelayPaths.RelayIdOf(relay);

            // Decided once per connection, here: this machine's config.json if it says, else the relay's
            // setting from the profile, else "record". A change made in /admin/relays reaches a player with
            // the next profile the app fetches and takes effect on the connect after it - never mid-match.
            var (mode, source) = EntrySwitching.Resolve(_config.EntrySwitching,
                profile.Relays.FirstOrDefault(r => r.Id.Equals(relayId, StringComparison.OrdinalIgnoreCase))?.EntrySwitching);
            _switching = mode;

            var doors = mode != EntrySwitchingMode.Off ? RelayPaths.DoorsOf(profile.Relays, relayId) : [];
            if (doors.Count < 2)
            {
                routes.PinDoorRoutes([]);
                return;
            }

            var addresses = doors
                .Select(d => IPEndPoint.TryParse(d.Endpoint, out var endpoint) ? endpoint.Address : null)
                .OfType<IPAddress>()
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Distinct()
                .ToList();
            routes.PinDoorRoutes(addresses);
            _doorsPinnedRelayId = relayId;

            _log($"Entry switching ({(mode == EntrySwitchingMode.On ? "on" : "record only")}, from {source}): {relay.Name} has " +
                 $"{doors.Count} ways in ({string.Join(", ", doors.Select(d => d.Id))}); the ones not in use are probed " +
                 "while a game runs.");
        }
        catch (Exception ex)
        {
            _log($"Entry switching: could not pin the ways into {relay.Name} ({ex.Message}) - they are not measured on this connection.");
        }
    }

    /// <summary>
    /// The recorder's switch policy asking for a move. Only noted here; the supervisor makes it on its next
    /// pass, the one place a tunnel is ever replaced or moved. False when it will not be made at all.
    /// </summary>
    private bool RequestDoorMove(GamePingBooster.Core.Quality.DoorDecision decision)
    {
        if (_switching != EntrySwitchingMode.On || _state != TunnelState.Connected) return false;
        Volatile.Write(ref _pendingDoorMove, decision.To);
        return true;
    }

    /// <summary>
    /// Moves the live tunnel to another way into the SAME relay, with the game running. See TunnelClient.MoveTo
    /// for why the game server sees nothing change, and why moving to another relay would drop the match -
    /// which is why the target is looked up among this relay's ways in and nowhere else.
    /// </summary>
    private void MoveToDoor(TunnelClient tunnel, string doorId, bool rollback)
    {
        var current = _relay;
        var routes = _routes;
        var profile = _profile;
        // Connected, and still this tunnel: a disconnect that got in first has already removed every route,
        // and a pin added now would outlive it.
        if (_state != TunnelState.Connected || current is null || routes is null || profile is null ||
            !ReferenceEquals(tunnel, _tunnel)) return;

        var relayId = RelayPaths.RelayIdOf(current);
        var target = RelayPaths.DoorsOf(profile.Relays, relayId)
            .FirstOrDefault(d => d.Id.Equals(doorId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            _log($"Entry switching: {doorId} is no longer a way into {current.Name} - staying where the tunnel is.");
            return;
        }
        if (target.Id.Equals(current.Id, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var endpoint = ParseEndpoint(target.Endpoint);
            routes.PinRelayRoute(endpoint.Address);
            tunnel.MoveTo(endpoint);
            _relay = target;

            // The in-game reading was taken down the old way.
            ForgetDirectPing();

            // Remembered so a way that goes silent right after the move is left again before the supervisor
            // gives the whole tunnel up - see MovedBackAfterSilence. A move back is not itself undone.
            _movedFromDoor = rollback ? null : current.Id;
            _movedAtTick = Environment.TickCount64;

            _log($"Entry switching: moved from {current.Name} [{current.Id}] to {target.Name} [{target.Id}] - the same " +
                 "relay and session, so the game server sees no change.");
            SetState(TunnelState.Connected, new StatusText("svc.movedRelay",
                $"Connected to {target.Name} - moved off {current.Name} for a better route",
                target.Name, current.Name));
        }
        catch (Exception ex)
        {
            _log($"Entry switching: could not move to {target.Name} ({ex.Message}) - staying on {current.Name}.");
        }
    }

    /// <summary>
    /// Measures the in-game ping against the server the game is actually on, once a second.
    ///
    /// Everything before this measured a stand-in. The landmark is the endpoint the game probes
    /// to pick a REGION, which is the right instrument for that job and the wrong one for this:
    /// on 2026-09-10 the game played on 172.188.74.210 and 20.198.178.182 while the ping on
    /// screen came from an echo to 20.43.187.66, taken once, before the match started, and then
    /// held for the rest of the session.
    ///
    /// An echo to the real server travels the whole path the game's packets travel, so what comes
    /// back needs no offset, no subtraction and no clamping - the three places the estimate could
    /// go wrong, and did.
    ///
    /// Whether a live match server answers ICMP is still unknown, and cannot be settled by
    /// testing addresses from a finished match: those machines are torn down with the match, so
    /// silence proves nothing. This loop settles it with real data - it logs which way it went,
    /// once per change, and falls back to the estimate when the answer is no.
    /// </summary>
    private async Task ProbeGamePingAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        IPAddress? current = null;
        var misses = 0;
        var quietUntilTick = 0L;
        var selfChecked = false;

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var tunnel = _tunnel;
                if (_state != TunnelState.Connected || tunnel is null)
                {
                    current = null;
                    misses = 0;
                    selfChecked = false;
                    ForgetDirectPing();
                    continue;
                }

                // Prove the mechanism works before there is anything to measure with it.
                //
                // The landmark answered an echo through this same tunnel a moment ago, during
                // relay selection - but through a socket this loop no longer owns, over a path
                // that reads replies a different way. If probing a LIVE tunnel is broken, every
                // session would end with an in-game ping that never became a measurement and two
                // candidate explanations: the game server filters ICMP, or this does not work.
                // One packet at connect time tells them apart, in the log, before the match.
                if (!selfChecked && _path is { } path)
                {
                    selfChecked = true;
                    var check = await tunnel.ProbeGameServerAsync(path.Landmark, ProbeTimeoutMs, ct)
                        .ConfigureAwait(false);
                    _log(check is { } ms
                        ? $"Probing through the live tunnel works - {ms:F0} ms to {path.RegionName}. " +
                          "The in-game ping will be measured against the game's own server once a match starts."
                        : "WARNING: an echo through the live tunnel to the landmark went unanswered, and that " +
                          "landmark answered during relay selection. In-game ping will stay on the estimate " +
                          "this session - this is a fault in the probe, not in the game server.");
                }

                var target = tunnel.Destinations.PrimaryDestination;
                if (target is null)
                {
                    // No game traffic this second - between matches, in a menu, or just after the
                    // 30-second log line cleared the tally it shares with us. The last reading is
                    // left alone rather than cleared: it ages out by itself, and dropping the
                    // headline to the estimate for one tick would make the number jump for no
                    // reason the player can see.
                    continue;
                }

                if (!target.Equals(current))
                {
                    // Addresses are deliberately not logged - see the tally, which masks them.
                    if (current is not null) _log("The game moved to a different server - measuring the new one.");
                    current = target;
                    misses = 0;
                    quietUntilTick = 0;
                    ForgetDirectPing();
                    await FollowMatchRegionAsync(tunnel, target, ct).ConfigureAwait(false);
                }
                else if (_path is null)
                {
                    // A failover to another relay blanks the estimate; the match in progress says where to take it again.
                    await FollowMatchRegionAsync(tunnel, target, ct).ConfigureAwait(false);
                }

                if (Environment.TickCount64 < quietUntilTick) continue;

                var rtt = await tunnel.ProbeGameServerAsync(target, ProbeTimeoutMs, ct).ConfigureAwait(false);
                if (rtt is { } measured)
                {
                    if (misses >= ProbeGiveUpAfter)
                    {
                        _log("The game server is answering echoes again - the in-game ping is measured, not estimated.");
                    }
                    else if (misses == 0 && Volatile.Read(ref _directPingMs) < 0)
                    {
                        _log($"In-game ping is now measured against the game server itself: {measured:F0} ms.");
                    }
                    misses = 0;
                    RecordDirectPing(measured);
                    continue;
                }

                misses++;
                if (misses == ProbeGiveUpAfter)
                {
                    _log($"The game server did not answer {ProbeGiveUpAfter} echoes - it filters ICMP, or this " +
                         $"one does. Falling back to the relay ping plus the measured second leg, and retrying " +
                         $"every {ProbeRetryQuietMs / 1000}s.");
                    ForgetDirectPing();
                }
                if (misses >= ProbeGiveUpAfter) quietUntilTick = Environment.TickCount64 + ProbeRetryQuietMs;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // A diagnostic must never take the tunnel down with it. The estimate keeps working.
            _log($"The in-game ping probe stopped: {ex.Message}. The displayed ping falls back to the estimate.");
        }
    }

    /// <summary>
    /// Folds one measurement into the displayed value.
    ///
    /// Lightly smoothed, half old and half new. The relay leg on this connection jitters about a
    /// millisecond, so heavy smoothing would buy nothing and cost responsiveness - and a headline
    /// that lags the game's own number is the complaint this work started from. It is enough to
    /// stop a single unlucky sample redrawing the number.
    /// </summary>
    private void RecordDirectPing(double rttMs)
    {
        var previous = Volatile.Read(ref _directPingMs);
        var smoothed = previous < 0 ? rttMs : (previous * 0.5) + (rttMs * 0.5);
        Interlocked.Exchange(ref _directPingMs, smoothed);
        Interlocked.Exchange(ref _directPingAtTick, Environment.TickCount64);
    }

    private void ForgetDirectPing()
    {
        Interlocked.Exchange(ref _directPingMs, -1);
        Interlocked.Exchange(ref _directPingAtTick, 0);
    }

    /// <summary>
    /// The measured in-game ping, or null when there is not a recent one.
    ///
    /// Staleness is checked rather than trusted: the probe loop clears the value when it gives up,
    /// but it can also simply stop getting scheduled - a reconnect, a suspended machine - and a
    /// number frozen on screen from a minute ago is worse than falling back to the estimate.
    /// </summary>
    private double? DirectGamePingMs
    {
        get
        {
            var value = Volatile.Read(ref _directPingMs);
            if (value < 0) return null;
            var at = Interlocked.Read(ref _directPingAtTick);
            if (at == 0 || Environment.TickCount64 - at > DirectPingGoesStale.TotalMilliseconds) return null;
            return value;
        }
    }

    /// <summary>
    /// Re-establishes a tunnel that has gone silent, over any relay in the profile.
    ///
    /// Two things here exist because of a failure seen on real hardware (2026-09-01: the relay's
    /// service was stopped to simulate a dead VPS).
    ///
    /// First, the game routes come out of the routing table immediately. While the tunnel is
    /// down those routes point at a virtual adapter with nothing behind it, so the game's packets
    /// are not merely slow - they are dropped on the floor. The player is worse off than if the
    /// booster had never been switched on, which is the one outcome this project must never
    /// produce. Pulling the routes hands the traffic back to the normal ISP path: higher ping,
    /// but a playable game while we sort ourselves out.
    ///
    /// Second, every relay in the profile is tried, not just the one we were on. The old code
    /// captured the relay once and hammered that single address forever, so a relay that stayed
    /// down left the client stuck permanently.
    ///
    /// The relay we were using is always tried FIRST in each round. That is what keeps a brief
    /// loss of the player's own connectivity - which takes every relay down at once - from
    /// causing a pointless switch: when the network returns, the original relay answers first and
    /// we resume on it, usually on the same inner IP.
    /// </summary>
    private long _lastThroughputTick;
    private long _lastLoggedSent;

    /// <summary>
    /// Forgets the throughput baseline, so the next line measures from zero.
    ///
    /// Called whenever the tunnel object is replaced. The counters live on the TunnelClient, so a
    /// new one starts at zero while this baseline still holds the old one's total - which is how
    /// the log ended up reporting "-3/s up over the last 30s" after a relay change. A negative
    /// rate is not a small cosmetic issue: it is the kind of thing that makes somebody distrust
    /// every other number in the file.
    /// </summary>
    private void ResetThroughputBaseline() => _lastLoggedSent = 0;

    /// <summary>
    /// Writes one throughput line every 30 seconds while traffic is moving, on the same cadence
    /// as the relay's own stats line so the two logs can be read side by side.
    ///
    /// Without this there is no way to answer the question that matters most after a session -
    /// did the game's packets actually go through the relay? - because the counters live only in
    /// memory and die with the process. A player reporting "it did not feel any different" left
    /// nothing behind to check.
    /// </summary>
    private void LogThroughput(TunnelClient tunnel)
    {
        var now = Environment.TickCount64;
        if (now - _lastThroughputTick < 30_000) return;
        _lastThroughputTick = now;

        var sent = tunnel.PacketsSent;
        if (sent == _lastLoggedSent) return;   // nothing moved; stay quiet

        var rate = 0L;
        if (_lastLoggedSent > 0 && sent > _lastLoggedSent) rate = (sent - _lastLoggedSent) / 30;
        _lastLoggedSent = sent;

        _log($"Tunnel carried {sent} packets up, {tunnel.PacketsReceived} down " +
             $"({rate}/s up over the last 30s), {_routes?.ActiveGameRouteCount ?? 0} game routes installed, " +
             $"rtt {tunnel.LastRttMs:F0} ms");

        LogGameDestinations(tunnel);
    }

    /// <summary>
    /// Writes the addresses the game is actually talking to, named with the relay carrying them.
    ///
    /// Paired with the throughput line so one log covers both halves of the question. See
    /// <see cref="GameServerTally"/>: the reason this is here is to find out whether the game
    /// server on the far end changes when the relay does.
    /// </summary>
    private void LogGameDestinations(TunnelClient tunnel)
    {
        if (!tunnel.Destinations.HasTraffic) return;

        var relay = _relay is null
            ? "an unnamed relay"
            : $"{_relay.Name} ({_relay.Location ?? "location unknown"})";

        _log(tunnel.Destinations.Format(relay));
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        var adapter = _adapter;
        var routes = _routes;
        var previous = _relay;
        if (previous is null || adapter is null || routes is null) return;

        var previousIp = _tunnel?.Session.ClientIp;
        var psk = System.Text.Encoding.UTF8.GetBytes(_config.Psk);

        // Flush before the relay changes underneath it: a destinations line naming the wrong
        // relay is worse than no line, because the whole point is comparing one against another.
        if (_tunnel is not null) LogGameDestinations(_tunnel);

        Abandon(_tunnel);
        _tunnel = null;
        ResetThroughputBaseline();

        // Fall back to the direct path before the first handshake, not after a few failures.
        // There is no such thing as a fast recovery here - the supervisor already waited 15
        // seconds of silence before calling us - so there is no quick success worth protecting
        // these routes for, and every second they stay in place is a second of no game traffic.
        var hadGameRoutes = routes.ActiveGameRouteCount > 0;
        if (hadGameRoutes)
        {
            _log("Tunnel is down - removing game routes so traffic falls back to the normal path.");
            routes.RemoveGameRoutes(adapter.InterfaceIndex);
        }

        // The lobby too, for the same reason: pointed at an adapter with no relay behind it, the
        // lobby would sit in a blackhole for as long as this loop takes, and that can be minutes.
        // No flag to remember whether to put it back - it goes back on every successful reconnect,
        // exactly as it went on at connect.
        if (routes.ActiveLobbyRouteCount > 0)
        {
            _log("Tunnel is down - removing lobby routes so the lobby falls back to the normal path.");
            routes.RemoveLobbyRoutes(adapter.InterfaceIndex);
        }

        var candidates = FailoverOrder(previous);
        var delay = TimeSpan.FromSeconds(2);

        // Which relay the pinned /32 currently points at. This is NOT the same question as
        // "are we switching relay", and conflating the two is a routing loop waiting to happen:
        // an attempt that pins relay B and then fails later on (ConfigureAdapter throwing, say)
        // leaves the pin on B, so a subsequent success on relay A must re-pin even though A is
        // the relay we originally came from.
        var pinned = previous;

        for (var round = 1; !ct.IsCancellationRequested; round++)
        {
            foreach (var relay in candidates)
            {
                if (ct.IsCancellationRequested) return;

                SetState(TunnelState.Reconnecting, new StatusText("svc.reconnecting",
                    $"Reconnecting via {relay.Name} (attempt {round}) - traffic is on the normal path",
                    relay.Name, round.ToString(CultureInfo.InvariantCulture)));

                TunnelClient? client = null;
                try
                {
                    client = new TunnelClient(ParseEndpoint(relay.Endpoint), AuthFor(relay, psk), _clientId, _log);
                    var session = await client.HandshakeAsync(attempts: 3, ct).ConfigureAwait(false);

                    // Pin the relay through the physical adapter BEFORE anything can point into
                    // the tunnel again - same rule as the initial connect, and the reason the
                    // game routes are reinstalled only after this line.
                    if (!ReferenceEquals(relay, pinned))
                    {
                        _log($"{pinned.Name} did not answer; failing over to {relay.Name}.");
                        routes.PinRelayRoute(ParseEndpoint(relay.Endpoint).Address);
                        pinned = relay;
                        _relay = relay;

                        // The second-leg offset belonged to the relay we just left, and this one
                        // may be a continent further from the game server. There is no chance to
                        // re-measure - the pump threads own the socket by the time we get here -
                        // so the in-game estimate goes blank rather than wrong. Reconnecting to
                        // the SAME relay keeps it, which is the common case: a relay that
                        // hiccuped is still exactly where it was.
                        //
                        // Blank is now much less costly than it was: the probe loop measures the
                        // real server through the new tunnel within a second, and it does not
                        // need the socket to itself to do it.
                        _path = null;
                    }

                    // Belongs to the old tunnel either way, even when the relay is the same one:
                    // the reading was taken over a session that no longer exists.
                    ForgetDirectPing();

                    _tunnel = client;
                    ResetThroughputBaseline();

                    // A failover to another relay brings other ways in with it, and leaves the old ones behind.
                    PinDoors();

                    if (previousIp is not null && session.ClientIp.Equals(previousIp))
                    {
                        // Same address, but for two very different reasons - say which, because
                        // reading "resumed" after a failover invites the conclusion that the
                        // address reservation worked across two independent relays, which is
                        // impossible: each relay has its own session table.
                        _log(ReferenceEquals(relay, previous)
                            ? $"Resumed on the same inner IP ({session.ClientIp}) - the reservation held."
                            : $"{relay.Name} happened to hand out the same inner IP ({session.ClientIp}) " +
                              "the previous relay had. Convenient - the adapter needs no change - but it is " +
                              "the two address pools coinciding, not a resumed session.");
                    }
                    else
                    {
                        _log($"Got a different inner IP ({session.ClientIp}) - reconfiguring the adapter.");
                        routes.ConfigureAdapter(adapter.InterfaceIndex, session.ClientIp, prefixLength: 24, session.Mtu);
                    }

                    InstallLobbyRoutes();

                    if (hadGameRoutes || _config.RouteWithoutGame || (_watcher?.IsGameRunning ?? false))
                    {
                        InstallRoutes();
                    }

                    client.StartPumping(adapter, ct);
                    _error = null;
                    SetState(TunnelState.Connected,
                        new StatusText("svc.reconnected", $"Reconnected to {relay.Name}", relay.Name));
                    return;
                }
                catch (OperationCanceledException)
                {
                    // Abandon here too: the socket is ours until StartPumping takes it over, and
                    // a reconnect loop that runs for hours would otherwise leak one per attempt.
                    Abandon(client);
                    _tunnel = null;
                    return;
                }
                catch (Exception ex)
                {
                    Abandon(client);
                    _tunnel = null;
                    _error = ex.Message;
                    _log($"  {relay.Name}: {ex.Message}");
                }
            }

            // Every relay failed this round. Back off before sweeping them again, but never give
            // up: the usual cause is the player's own network being down, and it comes back
            // without anyone pressing a button. Waiting here is safe now that the game is on the
            // normal path rather than pointed at a dead adapter.
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
    }

    /// <summary>
    /// Drops a tunnel that is being REPLACED rather than shut down, without telling the relay we
    /// are leaving. A Disconnect would make the relay drop this client's address reservation, and
    /// that reservation is the entire reason a reconnect can keep its inner IP and leave the
    /// routing table alone. Reconnecting used to announce its own departure and then wonder why
    /// it always came back on a different address.
    /// </summary>
    private static void Abandon(TunnelClient? client)
    {
        if (client is null) return;
        client.AnnounceDisconnect = false;
        client.Dispose();
    }

    /// <summary>
    /// Relays to try during a reconnect: the one we were on first, then the rest of the profile
    /// in order. Relays that failed are not struck off - a VPS that is rebooting comes back.
    /// </summary>
    private List<RelayEntry> FailoverOrder(RelayEntry current)
    {
        var order = new List<RelayEntry> { current };
        foreach (var relay in RelaysForGame(_game))
        {
            if (!relay.Id.Equals(current.Id, StringComparison.OrdinalIgnoreCase)) order.Add(relay);
        }
        return order;
    }

    private void OnGameStateChanged(bool running, string? processName)
    {
        try
        {
            if (running)
            {
                var detected = processName is null
                    ? null
                    : ProfileMerge.FindByProcess(_profile?.Games ?? [], processName);
                if (detected is null) return;

                // Before anything below can return early: discovery does not depend on routes or
                // on the tunnel being up, and it ignores a game it is already recording.
                _discovery?.GameStarted(detected);
                _presence.Set(detected.Id);

                if (!ReferenceEquals(detected, _game))
                {
                    SwitchGame(detected);
                }
                else if ((_routes?.ActiveGameRouteCount ?? 0) > 0)
                {
                    // The same game under another of its processes - PUBG's BattlEye shim handing
                    // over to the game itself. Its routes are already in.
                    return;
                }
                RememberLastGame(detected);

                // The game can start while the tunnel is down and the reconnect loop is sweeping
                // relays. Installing routes then would push the game's packets into an adapter
                // with nothing behind it - the exact blackhole the reconnect path just undid.
                // ReconnectAsync reinstalls them itself as soon as a relay answers.
                if (_tunnel is null)
                {
                    _log($"Detected {processName}.exe, but the tunnel is down - leaving it on the normal path.");
                    return;
                }

                _log($"Detected {processName}.exe running - installing routes for {detected.Name}.");
                InstallRoutes();
                SetState(TunnelState.Connected, new StatusText("svc.accelerating",
                    $"Accelerating {_game?.Name} through {_relay?.Name}", _game?.Name ?? "", _relay?.Name ?? ""));
            }
            else
            {
                _log("The game exited - removing routes, other traffic returns to the normal path.");
                _discovery?.GameStopped();
                _presence.Set(null);
                if (_adapter is not null) _routes?.RemoveGameRoutes(_adapter.InterfaceIndex);
                // Do not claim Connected while a reconnect is still in progress.
                if (_tunnel is not null)
                {
                    SetState(TunnelState.Connected, WaitingForGame(_relay?.Name ?? ""));
                }
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            SetState(TunnelState.Faulted, new StatusText("svc.routeFailed", "Failed to update the routing table"));
            _log($"Error while adding or removing routes: {ex}");
        }
    }

    private void InstallRoutes()
    {
        if (_adapter is null || _routes is null || _tunnel is null || _game is null) return;

        var cidrs = _game.Regions.SelectMany(r => r.Cidrs).Distinct().ToList();
        if (cidrs.Count == 0)
        {
            _log("WARNING: the profile contains no CIDRs - the tunnel is up but nothing is being routed.");
            return;
        }
        WarnAboutRoutedLandmarks(cidrs);
        _routes.InstallGameRoutes(_adapter.InterfaceIndex, cidrs);
        _log($"Installed {cidrs.Count} routes into the virtual adapter.");
    }

    /// <summary>
    /// Puts the game's lobby addresses on the tunnel. Called whenever a tunnel comes up - at
    /// connect and after every reconnect - and independent of whether the game is running.
    ///
    /// LobbyRoutes decides what is allowed; everything it refuses is logged by name and reason.
    /// A failure here never fails the connection: the lobby staying on the normal path is exactly
    /// what the player had before lobby routes existed, and losing the whole tunnel over it would
    /// trade that for no acceleration at all.
    ///
    /// Note what a reconnect to a DIFFERENT relay does to a lobby connection regardless: it leaves
    /// through a new address, which the lobby server sees as a stranger, and the game has to
    /// connect again. Nothing on this side can prevent that; it is why failover is a last resort.
    /// The move between matches (RescanBetweenMatchesAsync) accepts it: the owner has disconnected
    /// and reconnected in the PUBG lobby daily and the lobby comes back on its own.
    /// </summary>
    private void InstallLobbyRoutes()
    {
        if (_adapter is null || _routes is null || _tunnel is null || _profile is null) return;

        // Every game's, not only the one relays were measured for. Nobody chooses a game, and a lobby
        // opens in the first seconds after launch - before the watcher has said which game it is.
        var games = _profile.Games;
        var lobby = games.SelectMany(g => g.LobbyAddresses).ToList();
        if (lobby.Count == 0) return;

        // Entries too: the client pins whichever path it connects through, and an entry is one.
        var relays = _profile.Relays.Select(r => r.Endpoint)
            .Concat(RelayPaths.Expand(_profile.Relays).Select(p => p.Endpoint))
            .ToList();
        if (_relay is not null) relays.Add(_relay.Endpoint);
        var landmarks = games.SelectMany(g => g.Regions).SelectMany(r => r.Landmarks);

        var rejected = new List<LobbyRoutes.Rejection>();
        var hostRoutes = LobbyRoutes.ToHostRoutes(lobby, relays, landmarks, rejected);
        foreach (var refusal in rejected)
        {
            _log($"WARNING: lobby address '{refusal.Entry}' is not routed - {refusal.Reason}.");
        }
        if (hostRoutes.Count == 0) return;

        try
        {
            _routes.InstallLobbyRoutes(_adapter.InterfaceIndex, hostRoutes);
            _log($"Installed {hostRoutes.Count} lobby route(s) into the virtual adapter " +
                 $"({string.Join(", ", hostRoutes)}) - now, not when the game starts.");
        }
        catch (Exception ex)
        {
            _log($"Could not install the lobby routes, so the lobby stays on the normal path: {ex.Message}");
        }
    }

    /// <summary>
    /// Complains when a routed range swallows a landmark.
    ///
    /// A landmark inside the tunnel is the exact fault this whole mechanism exists to undo: the
    /// game would measure that region through the relay and every other region over the player's
    /// own connection, compare the two, and put the player wherever the arithmetic came out -
    /// which is how a tester ended up in Korea on a profile that only covered Singapore.
    ///
    /// Only a warning, because refusing to install a /20 that carries real matches would trade a
    /// bad server choice for no acceleration at all. The remedy when this does fire is the one
    /// PinRelayRoute already uses: a /32 for the landmark pointed at the physical gateway beats
    /// the /20 on longest-prefix-match. Nothing collides today, and Test-Profile.ps1 checks that
    /// stays true, so this is the backstop rather than the guard.
    /// </summary>
    private void WarnAboutRoutedLandmarks(List<string> cidrs)
    {
        // A Steam Datagram Relay game's landmarks are its own relays, routed on purpose: the game
        // probes and plays on the same relay address and port. See GameEntry.LandmarksRouted.
        if (_game!.LandmarksRouted) return;

        var ranges = new List<(uint Network, uint Mask, string Cidr)>();
        foreach (var cidr in cidrs)
        {
            var parts = cidr.Split('/');
            if (parts.Length != 2 ||
                !IPAddress.TryParse(parts[0], out var baseIp) ||
                baseIp.AddressFamily != AddressFamily.InterNetwork ||
                !int.TryParse(parts[1], out var bits) || bits is < 0 or > 32)
            {
                continue;
            }
            var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
            ranges.Add((ToUInt32(baseIp) & mask, mask, cidr));
        }

        foreach (var region in _game!.Regions)
        {
            foreach (var text in region.Landmarks)
            {
                if (!IPAddress.TryParse(text, out var landmark) ||
                    landmark.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }
                var value = ToUInt32(landmark);
                foreach (var range in ranges)
                {
                    if ((value & range.Mask) != range.Network) continue;
                    _log($"WARNING: the landmark {landmark} for region '{region.Id}' falls inside the " +
                         $"routed range {range.Cidr}. The game will measure that region through the " +
                         "relay and every other region over your own connection, and then compare " +
                         "the two. Rebuild the profile without that range, or pin the landmark to " +
                         "the physical gateway.");
                }
            }
        }
    }

    private static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    // ----------------------------------------------------------- disconnect

    /// <param name="reason">
    /// What the player reads once it is done, when the disconnect was not their own click - the
    /// idle timeout says why the tunnel went down, so nobody mistakes it for a fault.
    /// </param>
    public Task DisconnectAsync(string? reason = null) =>
        DisconnectAsync(reason is null ? null : new StatusText("", reason));

    /// <param name="reason">Why, as a language key and its English - see <see cref="StatusText"/>.</param>
    public async Task DisconnectAsync(StatusText? reason)
    {
        if (_state == TunnelState.Disconnected) return;
        SetState(TunnelState.Disconnected, new StatusText("svc.disconnecting", "Disconnecting..."));
        var phases = new PhaseTimer();
        await TeardownAsync(phases, keepAdapter: true).ConfigureAwait(false);
        _log($"Disconnect took {phases}.");
        SetState(TunnelState.Disconnected, reason ?? new StatusText("svc.notConnected", "Not connected"));
    }

    /// <summary>
    /// Tears everything down in reverse order. Must never throw. <paramref name="phases"/>, when given, is
    /// marked after each step - how a disconnect's time is split in the log.
    ///
    /// <paramref name="keepAdapter"/> ends the adapter's session and leaves the adapter itself for the next
    /// connect - see <see cref="OpenAdapter"/>. Every route has been removed by then, and an adapter with no
    /// session carries nothing.
    /// </summary>
    private async Task TeardownAsync(PhaseTimer? phases = null, bool keepAdapter = false)
    {
        // No tunnel, nothing to show beside a session on /admin/relays.
        _presence.Set(null);

        // Measured for one relay on one connect. Keeping it would have the UI reporting an
        // in-game ping for a tunnel that no longer exists, and after a failover to a relay at a
        // different distance it would be reporting the wrong one.
        _path = null;
        ForgetDirectPing();

        if (_watcher is not null)
        {
            _watcher.GameStateChanged -= OnGameStateChanged;
            _watcher.Dispose();
            _watcher = null;
        }
        // Nothing watches the game after this, so nothing would tell discovery it exited.
        _discovery?.GameStopped();
        phases?.Mark("watcher");

        try
        {
            if (_routes is not null && _adapter is not null) _routes.RemoveAll(_adapter.InterfaceIndex);
        }
        catch (Exception ex)
        {
            _log($"Error removing routes (deleting the adapter will clean up the rest): {ex.Message}");
        }
        _routes = null;
        phases?.Mark("routes");

        if (_tunnel is not null) LogGameDestinations(_tunnel);
        _tunnel?.Dispose();
        _tunnel = null;
        phases?.Mark("tunnel");

        if (_supervisor is not null)
        {
            _cts?.Cancel();
            try { await _supervisor.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _supervisor = null;
        }
        phases?.Mark("supervisor");

        if (_gamePingProbe is not null)
        {
            _cts?.Cancel();
            try { await _gamePingProbe.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _gamePingProbe = null;
        }
        phases?.Mark("game ping probe");

        // Its last act is writing the match summary, so it is awaited rather than abandoned.
        if (_spikeRecorder is not null)
        {
            _cts?.Cancel();
            try { await _spikeRecorder.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _spikeRecorder = null;
            _recorder = null;
        }
        phases?.Mark("spike recorder");

        // Deleting the adapter comes last, and it is also the safety brake: any route still
        // pointing at it disappears along with it. The session and the adapter are timed apart:
        // keeping the adapter between connects would save the second and not the first.
        _adapter?.EndSession();
        phases?.Mark("adapter session");
        if (!keepAdapter)
        {
            _adapter?.Dispose();
            _adapter = null;
            phases?.Mark("adapter removal");
        }

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }
        phases?.Mark("rest");
    }

    /// <summary>
    /// The virtual adapter for a connect, with a session open: the one kept from the last connect when there
    /// is one, a new one otherwise.
    ///
    /// KEPT BETWEEN CONNECTS, because deleting and creating it was most of what a reconnect cost. On
    /// 2026-09-17 removing it took 717 of a disconnect's 726 ms, and creating one straight after a removal
    /// took 1.3 to 1.9 s against 0.4 s on a fresh machine - Windows still tidying up the last one, and at
    /// worst refusing the name with error 2 until it had. It goes when nothing will need it soon: when the
    /// app closes (<see cref="ReleaseIdleAdapter"/>), when a connect fails, and when the service stops.
    ///
    /// A kept adapter that will not open a session - disabled in Network Connections, or gone - is replaced
    /// rather than failing the connect.
    /// </summary>
    private WintunAdapter OpenAdapter()
    {
        if (_adapter is { } kept)
        {
            try
            {
                kept.StartSession();
                _log($"Virtual adapter '{kept.Name}' reused, interface index {kept.InterfaceIndex}.");
                return kept;
            }
            catch (Exception ex)
            {
                _log($"The kept virtual adapter could not be used ({ex.Message}) - creating a new one.");
                kept.Dispose();
                _adapter = null;
            }
        }

        var adapter = WintunAdapter.Create(_config.AdapterName, log: _log);
        try
        {
            adapter.StartSession();
        }
        catch
        {
            adapter.Dispose();
            throw;
        }
        _log($"Virtual adapter '{_config.AdapterName}' is ready, interface index {adapter.InterfaceIndex}");
        return adapter;
    }

    /// <summary>
    /// Removes the kept adapter when no tunnel is using it. Called when the app disconnects from the service:
    /// with the window closed nobody is about to press Connect, and an adapter left in Network Connections
    /// all day reads as the booster still doing something to the network. A tunnel still up - the app
    /// crashed rather than closed - keeps it.
    /// </summary>
    public void ReleaseIdleAdapter()
    {
        if (_adapter is not { } adapter) return;
        if (_state is not (TunnelState.Disconnected or TunnelState.Faulted) || _tunnel is not null) return;

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _adapter = null;
        adapter.Dispose();
        _log($"Virtual adapter removed, the app has closed ({System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms).");
    }

    // -------------------------------------------------------------- status

    public StatusMessage Snapshot()
    {
        // Read once. The headline and the flag saying how it was arrived at must agree, and
        // reading the property twice inside the initializer could catch the probe going stale
        // between the two - a status that says "measured" over an estimated number.
        var direct = DirectGamePingMs;

        // Both are worked out once and read three times below - the English sentence, its language
        // key and its arguments have to describe the same moment.
        var choiceNote = RelayChoiceNote();
        var refusal = LicenceRefusal();

        return new StatusMessage
        {
            State = _state,
            Detail = _detail,
            DetailCode = _detailText.Key.Length > 0 ? _detailText.Key : null,
            DetailArgs = _detailText.Args.Count > 0 ? _detailText.Args : null,
            Error = _error,
            RelayId = _relay?.Id,
            RelayName = _relay?.Name,
            RelayAddress = _relay?.Endpoint,
            // What is CONFIGURED, not what is connected, so the settings screen can show the current
            // value before anything has been tried. The key is deliberately absent - see the
            // set-relay comment in PipeServer.
            RelayEndpoints = _config.RelayEndpoints,
            // Ready to connect: SOME credential, and somewhere to send packets.
            //
            // The relay may come from the self-hosted setting OR from the profile's own list - both
            // are normal, and treating only the first as configured disabled Connect on
            // installations that worked fine.
            //
            // The credential may be a pre-shared key OR a licence token. Requiring the key would
            // disable Connect on a licensed installation, which has no key at all and is not
            // supposed to have one.
            //
            // Relays alone is not the whole answer, though the sentence above says it should be:
            // it reads the PROFILE's list, and the self-hosted addresses only ever reach it
            // through ApplySelfHostedRelay, which does nothing when there is no profile to apply
            // them to. So a self-hosted machine with an address and a key typed in - a complete
            // configuration, and one that needs no licence server at all - reported itself
            // unconfigured, and the UI answered the only way it can: a dead Connect button and a
            // banner asking for the address and key that were already there. What is missing on
            // that machine is the profile, which is a different sentence and not this flag's to
            // say; Connect reports it, and names the file.
            Configured = (_config.HasKey || _token is not null) &&
                         (Relays.Count > 0 || _config.RelayEndpoints.Count > 0),
            TunnelPingMs = _tunnel?.LastRttMs,
            // The real thing when the game's own server answers an echo through the tunnel, and the
            // estimate when it does not.
            //
            // The estimate is the live first leg plus the second-leg offset measured at connect time,
            // so it tracks the part that actually moves - the player's own connection - without
            // re-probing the datacentre. It is still an estimate against a stand-in host, which is
            // why the measurement wins whenever there is one. Null until something has answered,
            // which is right: a number built on no measurement is not better than showing nothing.
            GamePingMs = direct ?? (_path is { } p && _tunnel?.LastRttMs is { } live ? live + p.Offset : null),
            GamePingDirect = direct is not null,
            GameRegionName = _path?.RegionName,
            LossRatio = _tunnel?.LossRatio,
            GameRunning = _watcher?.IsGameRunning ?? false,
            // The game being played, or - when none is - the only one there is. With several there is
            // no selector, so naming the one relays happened to be measured for would read as a choice
            // nobody made; the UI says how many instead of listing them all.
            GameName = (_watcher?.IsGameRunning ?? false) ? _game?.Name : OnlyGameName,
            GameCount = _profile?.Games.Count ?? 0,
            RelayChoice = RelayChoice,
            RelayChoiceNote = choiceNote?.English,
            RelayChoiceNoteCode = choiceNote?.Key,
            RelayChoiceNoteArgs = choiceNote is { Args.Count: > 0 } ? choiceNote.Args : null,
            ActiveRoutes = _routes?.ActiveRouteCount ?? 0,
            PacketsSent = _tunnel?.PacketsSent ?? 0,
            PacketsReceived = _tunnel?.PacketsReceived ?? 0,
            PacketsDropped = _tunnel?.PacketsDropped ?? 0,
            PacketsDroppedFaults = _tunnel?.PacketsDroppedFaults ?? 0,
            // The PUBLIC half only. It is not a secret - it is the device's name, and the UI has to
            // send it to the licence server to register this machine, so it has to be readable here.
            // The private half never crosses the pipe in any form; see the set-relay note about the
            // pipe being open to BuiltinUsers.
            DevicePublicKey = _device.PublicKeyHex,
            // Whether there IS a token and when it runs out - never the token itself. The UI needs
            // both to know when to sign in and when to refresh; neither is a credential.
            HasToken = _token is not null,
            TokenExpiresAt = _token is null ? null : TokenStore.ExpiryOf(_token).ToUnixTimeSeconds(),
            LicenceUrl = _config.LicenceUrl,
            // Why Connect would be refused right now, in words, or null when it would not. Computed
            // in the service rather than worked out again in the UI: the rule decides whether a
            // connection is attempted at all, and two copies of it would drift into a button that is
            // enabled for a connection that cannot happen, or disabled for one that could.
            LicenceRefusal = refusal?.English,
            LicenceRefusalCode = refusal?.Key,
            LicenceRefusalArgs = refusal is { Args.Count: > 0 } ? refusal.Args : null,
            ProfileSource = _profileSource,
            // Read from the file rather than remembered in a field, so it is right after a restart
            // and right after somebody has copied a profile in by hand. A missing file is null,
            // which the UI reads as "never" - correct on a machine that has never signed in.
            ProfileUpdatedAt = OldestSealedProfileWrite()?.ToUnixTimeSeconds(),
            QualitySharing = _config.ShareQuality,
        };
    }

    /// <summary>Relay list for the UI to offer to the user.</summary>
    public IReadOnlyList<RelayEntry> Relays => _profile?.Relays ?? [];

    /// <summary>
    /// Applies the self-hosted relay from the configuration, if there is one, by replacing the
    /// profile's relay list with it.
    ///
    /// Replacing rather than appending is deliberate: somebody running their own relay wants
    /// that relay. Falling back to a relay they do not control, because theirs was briefly
    /// unreachable, is the last thing a self-hosted setup should do - and it would do it
    /// silently, which is worse.
    ///
    /// Called after every profile load, so a fetched profile cannot quietly reintroduce the
    /// list it was told to ignore.
    /// </summary>
    private void ApplySelfHostedRelay()
    {
        if (_profile is null || _config.RelayEndpoints.Count == 0) return;

        // Each is named after its own address. A single friendly label across several relays
        // would be meaningless, and inventing "Relay 1", "Relay 2" tells the user less than the
        // address they typed - which is also what they need to see when one of them is failing.
        _profile.Relays = [.. _config.RelayEndpoints.Select((endpoint, i) => new RelayEntry
        {
            Id = $"self-{i + 1}",
            Name = endpoint,
            Location = string.Empty,
            Endpoint = endpoint,
        })];

        _log($"Using {_profile.Relays.Count} self-hosted relay(s) and ignoring the profile's list: " +
             string.Join(", ", _config.RelayEndpoints));
    }

    /// <summary>
    /// Replaces the stored relay and key, then reloads so the change takes effect without a
    /// restart. Returns an error message, or null on success.
    ///
    /// Validation happens here rather than in the UI because the UI is not a privilege boundary:
    /// the pipe is open to BuiltinUsers, so anything can send this. Rejecting a malformed
    /// endpoint here is what stops a bad value reaching the tunnel.
    /// </summary>
    public async Task<string?> SetRelayAsync(IReadOnlyList<string>? endpoints, string? psk,
        string? licenceUrl, CancellationToken ct)
    {
        psk = psk?.Trim();

        // Null means "leave it alone", empty means "clear it". The distinction matters: the
        // settings screen sends the box's contents every time, and an installation that has a
        // licence server must not lose it because somebody opened settings to change an address.
        if (licenceUrl is not null)
        {
            licenceUrl = licenceUrl.Trim();
            if (licenceUrl.Length > 0 && !IsHttpUrl(licenceUrl))
            {
                return "The licence server must be a full http:// or https:// address.";
            }
        }

        var cleaned = (endpoints ?? [])
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // An empty list is legitimate and means "use the relays the profile lists". It used to
        // be an error, which made two reasonable setups impossible to express: somebody who
        // wants to go back to the vendor's relays after trying their own, and a licensed
        // installation, whose relays only ever come from the profile.
        //
        // The key is only required when there are self-hosted endpoints to reach WITH it. A
        // licensed installation authenticates with a token and has no pre-shared key at all;
        // demanding one there would make the settings screen unusable for the exact case the
        // licensed mode exists to serve.
        var needsKey = cleaned.Count > 0;

        // A blank key means "keep the one already stored", which is what lets somebody move
        // their relay to a new address without retyping a 44-character key they no longer have
        // to hand. It is only an error when there is nothing to keep.
        var keepExisting = string.IsNullOrWhiteSpace(psk);
        if (keepExisting)
        {
            // Only an ERROR when a key is actually needed and there is none to keep. The
            // carry-across below happens either way: a blank box means "leave the key alone",
            // and letting it fall through would write an empty key over a good one - silently
            // breaking an installation whose owner only meant to change an address.
            if (needsKey && string.IsNullOrWhiteSpace(_config.Psk))
            {
                return "Enter the pre-shared key.";
            }
            psk = _config.Psk;
        }

        // Every address is checked, and the message names the one that is wrong. Reporting only
        // that "an address is invalid" when four were pasted in is not much of a report.
        foreach (var endpoint in cleaned)
        {
            var colon = endpoint.LastIndexOf(':');
            if (colon <= 0 || colon == endpoint.Length - 1)
            {
                return $"\"{endpoint}\" needs a port, for example 203.0.113.10:51820";
            }
            if (!int.TryParse(endpoint[(colon + 1)..], out var port) || port < 1 || port > 65535)
            {
                return $"\"{endpoint}\" does not end in a port between 1 and 65535.";
            }
        }
        // The relay refuses anything shorter, so catching it here saves a handshake that could
        // only ever fail, and says why. Skipped when the key was carried across rather than
        // typed: an installation with no key at all is legitimate now, and complaining that its
        // absent key is too short would be nonsense.
        if (!keepExisting && psk!.Length < 16)
        {
            return "The key is too short - it must be at least 16 characters.";
        }

        // Kept so the change can be undone if the write fails. Without this the service would
        // go on running with settings it had just told the user it could not save, and a
        // restart would silently put the old ones back - which is the worst of both, because
        // the machine behaves one way now and a different way tomorrow for no visible reason.
        var previousEndpoints = _config.RelayEndpoints;
        var previousPsk = _config.Psk;
        var previousLicenceUrl = _config.LicenceUrl;
        var previousRelayId = _config.DefaultRelayId;

        _config.RelayEndpoints = cleaned;
        _config.Psk = psk;
        if (licenceUrl is not null) _config.LicenceUrl = licenceUrl;

        // Clear the preferred relay id along with it.
        //
        // A self-hosted endpoint REPLACES the profile's relay list, so an id that referred to an
        // entry in that list now refers to nothing. Leaving it behind produces a configuration
        // file that contradicts itself - "defaultRelayId": "sg-1" sitting next to a Hong Kong
        // endpoint - and the next person to read it, including a future me, has to work out
        // which half is a lie.
        _config.DefaultRelayId = null;
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            _config.RelayEndpoints = previousEndpoints;
            _config.Psk = previousPsk;
            _config.LicenceUrl = previousLicenceUrl;
            _config.DefaultRelayId = previousRelayId;
            return $"Could not save the settings: {ex.Message}";
        }

        try
        {
            await LoadProfileAsync(ct).ConfigureAwait(false);
        }
        catch (NoProfileAvailableException)
        {
            // Not a failure, and not worth a word to the user. Before the first sign-in there is
            // no profile anywhere - nothing has been pushed and the installer ships none - so
            // every save from the settings screen ended in a red banner about a missing file the
            // user had not asked for and could not yet have. Saving settings is not what fetches
            // a profile; signing in is.
            _log("Relay settings updated (no profile to reload yet).");
            return null;
        }
        catch (Exception ex)
        {
            // The settings ARE saved at this point, so this is not a failure of the save. Say so,
            // rather than leaving the user to guess whether to type it all again.
            return $"Saved, but the profile could not be reloaded: {ex.Message}";
        }
        _log("Relay settings updated.");
        return null;
    }

    /// <summary>An absolute http or https URL. Anything else is a typo, not a scheme.</summary>
    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void SetState(TunnelState state, StatusText detail)
    {
        _state = state;
        _detail = detail.English;
        _detailText = detail;
        StatusChanged?.Invoke(Snapshot());
    }

    private GameEntry FindGame(string id) =>
        _profile!.Games.FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"The profile has no game with id '{id}'.");

    /// <summary>
    /// Which game relays are measured for at connect, before the watcher has seen anything.
    ///
    /// Relays are compared once, against one game's region, so this is the best guess there is: the
    /// game already open, else the last one seen - so somebody who plays Counter-Strike 2 is measured
    /// for it from their second session on - else the configured default, else the first game in the
    /// profile. An explicit id still wins; the UI never sends one.
    /// </summary>
    private GameEntry ChooseGameForConnect(string? requested)
    {
        var games = _profile!.Games;
        if (games.Count == 0) throw new InvalidOperationException("The profile declares no games.");
        if (!string.IsNullOrWhiteSpace(requested)) return FindGame(requested);

        var running = GameProcessWatcher.FindRunning(games.SelectMany(g => g.ProcessNames));
        if (running is not null && ProfileMerge.FindByProcess(games, running) is { } open) return open;

        foreach (var id in new[] { _config.LastGameId, _config.DefaultGameId })
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            var known = games.FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (known is not null) return known;
        }
        return games[0];
    }

    /// <summary>
    /// Moves the routes over to a game that has just been detected, from whichever game they were for.
    ///
    /// The relay stays. It was measured at connect against the other game's region, but moving to a
    /// different relay now would give the game servers a new source address - exactly what a failover
    /// costs - to fix a difference nobody has measured. The log says so, and the next connect measures
    /// for this game: see ServiceConfig.LastGameId.
    /// </summary>
    private void SwitchGame(GameEntry game)
    {
        var previous = _game;
        if (_adapter is not null && (_routes?.ActiveGameRouteCount ?? 0) > 0)
        {
            _routes!.RemoveGameRoutes(_adapter.InterfaceIndex);
        }
        _game = game;

        // Both belonged to the previous game's server. The probe loop measures the new one within a
        // second of its first packet.
        _path = null;
        ForgetDirectPing();

        if (previous is not null)
        {
            _log($"{game.Name} is running - moving the routes over from {previous.Name}. Relays were " +
                 $"measured for {previous.Name} at connect; the next connect measures them for {game.Name}.");
        }

        // A relay the operator has taken off this game is left now, not at the next connect - see
        // MoveOffRelayNotForGameAsync. The supervisor makes the move; this only asks for it.
        if (_relay is { } relay && _tunnel is not null && !RelayPaths.Serves(relay, game.Id))
        {
            _log($"{relay.Name} [{relay.Id}] is not used for {game.Name} - moving to one that is.");
            _moveOffForGame = true;
        }
    }

    /// <summary>Records the game just seen, so the next connect measures relays for it. Never fails the caller.</summary>
    private void RememberLastGame(GameEntry game)
    {
        if (game.Id.Equals(_config.LastGameId, StringComparison.OrdinalIgnoreCase)) return;

        var previous = _config.LastGameId;
        _config.LastGameId = game.Id;
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            _config.LastGameId = previous;
            _log($"Could not remember {game.Name} as the last game played: {ex.Message}");
        }
    }

    /// <summary>
    /// Every game in the loaded profile, for the "Supported games" window: running first, then the one
    /// last played, then by name. Empty without a profile.
    ///
    /// Running is looked up here rather than read from the watcher, which only exists while the tunnel
    /// is up - a player checking whether the app recognises their game has usually not connected yet.
    /// One process lookup per name, once per request; the window asks when it opens and when the game
    /// being played changes, not on a timer.
    /// </summary>
    public List<SupportedGame> SupportedGames()
    {
        var profile = _profile;
        if (profile is null) return [];

        return profile.Games
            .Select(game => new SupportedGame
            {
                Id = game.Id,
                Name = game.Name,
                ProcessNames = [.. game.ProcessNames],
                Regions = [.. game.Regions.Select(r => r.Name).Where(n => n.Length > 0)],
                Running = GameProcessWatcher.FindRunning(game.ProcessNames) is not null,
                LastPlayed = game.Id.Equals(_config.LastGameId, StringComparison.OrdinalIgnoreCase),
            })
            .OrderByDescending(g => g.Running)
            .ThenByDescending(g => g.LastPlayed)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The profile's game when it has exactly one, else null.</summary>
    private string? OnlyGameName => _profile is { Games.Count: 1 } profile ? profile.Games[0].Name : null;

    /// <summary>
    /// What "waiting for ... to start" waits for. It used to name every game joined with " / ",
    /// which stopped fitting the window once several games were supported.
    /// </summary>
    private string GamesLabel => OnlyGameName ?? (_profile is { Games.Count: > 1 } ? "a supported game" : "the game");

    private RelayEntry FindRelay(string? id)
    {
        if (_profile!.Relays.Count == 0)
            throw new InvalidOperationException("The profile declares no relays.");

        return id is null
            ? _profile.Relays[0]
            : _profile.Relays.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException($"The profile has no relay with id '{id}'.");
    }

    private static IPEndPoint ParseEndpoint(string endpoint)
    {
        if (!IPEndPoint.TryParse(endpoint, out var ep))
            throw new FormatException($"Relay endpoint '{endpoint}' is invalid; expected ip:port.");
        return ep;
    }

    public async ValueTask DisposeAsync()
    {
        await TeardownAsync().ConfigureAwait(false);
        _discovery?.Dispose();
        _discoveryUploader?.Dispose();
        _presence.Dispose();
        _device.Dispose();
    }
}


/// <summary>
/// There is no profile to load: nothing has been pushed, and no file was installed beside the
/// service either.
///
/// Its own type because one caller has to tell it apart from every other reason a load can fail.
/// A damaged file, a bad signature or an unreadable directory are faults and read as faults; this
/// one is the ordinary state of a machine that has not signed in yet, and the profile arrives
/// with the sign-in. It still derives from FileNotFoundException so the callers that only log the
/// message keep behaving exactly as before.
/// </summary>
internal sealed class NoProfileAvailableException : FileNotFoundException
{
    public NoProfileAvailableException(string message) : base(message) { }
}
