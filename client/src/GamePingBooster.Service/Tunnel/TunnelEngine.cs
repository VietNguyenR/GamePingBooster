using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Service.Native;
using GamePingBooster.Service.Network;
using System.Security.Cryptography;

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
internal sealed class TunnelEngine : IAsyncDisposable
{
    private readonly ServiceConfig _config;
    private readonly Action<string> _log;

    private ProfileBundle? _profile;
    private WintunAdapter? _adapter;
    private TunnelClient? _tunnel;
    private RouteManager? _routes;
    private GameProcessWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private Task? _supervisor;
    private Task? _gamePingProbe;

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
    private volatile string? _error;

    /// <summary>Raised on every state change so PipeServer can push it to the UI.</summary>
    public event Action<StatusMessage>? StatusChanged;

    public TunnelEngine(ServiceConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _device = DeviceIdentity.LoadOrCreate(log);
        _token = TokenStore.Load(log);
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

        try
        {
            Directory.CreateDirectory(ServiceConfig.DefaultDirectory);
            var tmp = SealedProfilePath + ".tmp";
            await File.WriteAllBytesAsync(tmp, envelope, ct).ConfigureAwait(false);
            File.Move(tmp, SealedProfilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            return $"Could not store the profile: {ex.Message}";
        }

        try
        {
            await LoadProfileAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Stored, but could not load it: {ex.Message}";
        }

        var cidrs = parsed.Games.Sum(g => g.Regions.Sum(r => r.Cidrs.Count));
        _log($"Profile updated from the licence server: {parsed.Games.Count} game(s), " +
             $"{cidrs} ranges, {parsed.Relays.Count} relay(s). It applies from the next connect.");
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
    /// Where the profile the licence server sent is kept.
    ///
    /// It holds the SEALED envelope, not the profile. Nothing readable is ever written: opening
    /// it needs the device key, which is itself DPAPI machine-scoped, so a copy of this file on
    /// any other machine - or in a backup, or in a support bundle - is inert. It used to be
    /// plaintext JSON with every captured range in it, which is exactly what this product is
    /// meant not to hand out.
    /// </summary>
    internal static string SealedProfilePath =>
        Path.Combine(ServiceConfig.DefaultDirectory, "profile.sealed");

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

        var sealedExists = File.Exists(SealedProfilePath);
        string source;
        string json;

        if ((licensed || !File.Exists(local)) && sealedExists)
        {
            // Opened in memory. The plaintext exists only for as long as it takes to parse.
            var envelope = await File.ReadAllBytesAsync(SealedProfilePath, ct).ConfigureAwait(false);
            var plaintext = _device.OpenSealedProfile(envelope);
            try
            {
                json = System.Text.Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
            source = licensed ? "pushed" : "cached";
        }
        else if (File.Exists(local))
        {
            json = await File.ReadAllTextAsync(local, ct).ConfigureAwait(false);
            source = "shipped";
        }
        else
        {
            throw new FileNotFoundException(
                $"No profile at {_config.ProfilePath} and nothing from the licence server either.");
        }

        _profile = JsonSerializer.Deserialize(json, ProfileJsonContext.Default.ProfileBundle)
                   ?? throw new InvalidOperationException("The profile is not valid.");
        _profileSource = source;
        var chosen = source == "shipped" ? local : SealedProfilePath;

        if (licensed && source != "pushed")
        {
            // Loud, because it is the silent failure this whole path exists to avoid: the tunnel
            // will work perfectly on ranges that may be months old.
            _log($"Loaded the {source} profile from {chosen}. A licence server is configured but " +
                 "nothing has been pushed yet - sign in so the app can fetch the current one.");
        }
        else
        {
            _log($"Loaded the {source} profile from {chosen}");
        }
        ApplySelfHostedRelay();
    }

    // -------------------------------------------------------------- connect

    public async Task ConnectAsync(string? relayId, string? gameId, CancellationToken ct)
    {
        if (_state is TunnelState.Connected or TunnelState.Connecting) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _error = null;

        try
        {
            SetState(TunnelState.Connecting, "Preparing...");

            // The licence gate, before anything is created and before a packet is sent. An
            // expired subscription is a refusal the relay would make anyway; making it here as
            // well turns a four-attempt timeout into a sentence that says what to do. See
            // LicenceRefusal for why this is not the same question as Configured.
            if (LicenceRefusal() is { } refusal)
            {
                throw new InvalidOperationException(refusal);
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

            _game = FindGame(gameId ?? _config.DefaultGameId);
            var psk = System.Text.Encoding.UTF8.GetBytes(_config.Psk);

            // Choose the relay before creating anything. Probing is pure UDP - no adapter, no
            // routes - so a relay that turns out to be unreachable costs nothing but a timeout.
            SetState(TunnelState.Connecting, "Measuring relays...");
            ResetThroughputBaseline();
            (_relay, _tunnel) = await SelectRelayAsync(relayId ?? _config.DefaultRelayId, psk, token)
                .ConfigureAwait(false);
            var endpoint = ParseEndpoint(_relay.Endpoint);
            var session = _tunnel.Session;

            SetState(TunnelState.Connecting, "Creating the virtual adapter...");
            _adapter = WintunAdapter.Create(_config.AdapterName, log: _log);
            _adapter.StartSession();
            _log($"Virtual adapter '{_config.AdapterName}' is ready, interface index {_adapter.InterfaceIndex}");

            // Pin the relay to the physical adapter BEFORE installing any route into the tunnel.
            _routes = new RouteManager();
            _routes.PinRelayRoute(endpoint.Address);

            _routes.ConfigureAdapter(_adapter.InterfaceIndex, session.ClientIp, prefixLength: 24, session.Mtu);
            _tunnel.StartPumping(_adapter, token);

            // Watch the game so routes come and go with it.
            _watcher = new GameProcessWatcher(_game.ProcessNames);
            _watcher.GameStateChanged += OnGameStateChanged;
            _watcher.Start();

            if (_config.RouteWithoutGame)
            {
                _log("routeWithoutGame = true - installing routes now without waiting for the game (debug mode).");
                InstallRoutes();
            }

            StartSupervisor(token);
            StartGamePingProbe(token);

            SetState(TunnelState.Connected,
                _watcher.IsGameRunning
                    ? $"Connected to {_relay.Name} - accelerating {_game.Name}"
                    : $"Connected to {_relay.Name} - waiting for {_game.Name} to start");
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            SetState(TunnelState.Faulted, "Connection failed");
            _log($"Connection failed: {ex}");
            await TeardownAsync().ConfigureAwait(false);
            throw;
        }
    }

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
    private string? LicenceRefusal()
    {
        if (string.IsNullOrWhiteSpace(_config.LicenceUrl)) return null;
        if (_config.RelayEndpoints.Count > 0) return null;

        var token = _token;
        if (token is null)
        {
            return "This installation connects through a licensed relay and is not signed in. " +
                   "Sign in from the menu to get a licence.";
        }

        var expiry = TokenStore.ExpiryOf(token);
        if (expiry <= DateTimeOffset.UtcNow)
        {
            return $"The licence expired {expiry.ToLocalTime():g}. Renew the subscription and " +
                   "sign in again - the relay will not accept an expired licence.";
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
    /// </summary>
    private async Task<(RelayEntry Relay, TunnelClient Tunnel)> SelectRelayAsync(
        string? preferredId, byte[] psk, CancellationToken ct)
    {
        var candidates = _profile!.Relays;
        if (candidates.Count == 0) throw new InvalidOperationException("The profile declares no relays.");

        if (preferredId is not null)
        {
            var pinned = candidates.FirstOrDefault(r => r.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase));
            if (pinned is not null)
            {
                var client = new TunnelClient(ParseEndpoint(pinned.Endpoint), AuthFor(pinned, psk), _clientId, _log);
                await client.HandshakeAsync(attempts: 4, ct).ConfigureAwait(false);
                await ReportBothLegsAsync(pinned, client, ct).ConfigureAwait(false);
                return (pinned, client);
            }
            _log($"The profile has no relay '{preferredId}' - measuring all of them instead.");
        }

        if (candidates.Count == 1)
        {
            var only = candidates[0];
            var client = new TunnelClient(ParseEndpoint(only.Endpoint), AuthFor(only, psk), _clientId, _log);
            await client.HandshakeAsync(attempts: 4, ct).ConfigureAwait(false);
            await ReportBothLegsAsync(only, client, ct).ConfigureAwait(false);
            return (only, client);
        }

        var target = await ChooseTargetRegionAsync(ct).ConfigureAwait(false);

        var probes = new List<RelayProbe>();
        foreach (var relay in candidates)
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

                probes.Add(new RelayProbe(relay, client, legOne, endToEnd));
                _log($"  {relay.Name} [{relay.Id}] ({relay.Location}): {Describe(legOne, endToEnd, target)}");
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                _log($"  {relay.Name} [{relay.Id}] ({relay.Location}): unreachable - {ex.Message}");
                client?.Dispose();
            }
        }

        if (probes.Count == 0)
        {
            throw new InvalidOperationException(
                $"None of the {candidates.Count} relays in the profile answered. Check the network, " +
                "the endpoints in the profile, and that the PSK matches.");
        }

        var best = ChooseByEndToEnd(probes, target);
        foreach (var probe in probes)
        {
            if (!ReferenceEquals(probe.Client, best.Client)) probe.Client.Dispose();
        }
        return (best.Relay, best.Client);
    }

    private sealed record RelayProbe(RelayEntry Relay, TunnelClient Client, double LegOneMs, double? EndToEndMs);

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
                 "see the design notes.");
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
    /// How long the relay may stay silent before the tunnel is presumed dead. Keepalives go out
    /// every 3 seconds, so this is five missed answers - long enough to ride out a hiccup, short
    /// enough that a player notices the reconnect rather than a dead game.
    /// </summary>
    private static readonly TimeSpan SilenceBeforeDead = TimeSpan.FromSeconds(15);

    private void StartSupervisor(CancellationToken ct) => _supervisor = Task.Run(() => SuperviseAsync(ct), ct);

    /// <summary>
    /// Watches for a tunnel that has gone quiet. Without this, a relay restart or a brief loss of
    /// connectivity leaves the UI reporting "Connected" over a tunnel that carries nothing - the
    /// worst possible failure, because it looks like the game's fault.
    /// </summary>
    private async Task SuperviseAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_state != TunnelState.Connected) continue;

                var tunnel = _tunnel;
                if (tunnel is null) continue;

                LogThroughput(tunnel);

                var silence = tunnel.SinceLastPong;
                if (silence < SilenceBeforeDead) continue;

                _log($"No answer from the relay for {silence.TotalSeconds:F0}s - reconnecting.");
                await ReconnectAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
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

    private void StartGamePingProbe(CancellationToken ct) =>
        _gamePingProbe = Task.Run(() => ProbeGamePingAsync(ct), ct);

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

                SetState(TunnelState.Reconnecting,
                    $"Reconnecting via {relay.Name} (attempt {round}) - traffic is on the normal path");

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

                    if (hadGameRoutes || _config.RouteWithoutGame || (_watcher?.IsGameRunning ?? false))
                    {
                        InstallRoutes();
                    }

                    client.StartPumping(adapter, ct);
                    _error = null;
                    SetState(TunnelState.Connected, $"Reconnected to {relay.Name}");
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
        foreach (var relay in _profile?.Relays ?? [])
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
                // The game can start while the tunnel is down and the reconnect loop is sweeping
                // relays. Installing routes then would push the game's packets into an adapter
                // with nothing behind it - the exact blackhole the reconnect path just undid.
                // ReconnectAsync reinstalls them itself as soon as a relay answers.
                if (_tunnel is null)
                {
                    _log($"Detected {processName}.exe, but the tunnel is down - leaving it on the normal path.");
                    return;
                }

                _log($"Detected {processName}.exe running - installing routes.");
                InstallRoutes();
                SetState(TunnelState.Connected, $"Accelerating {_game?.Name} through {_relay?.Name}");
            }
            else
            {
                _log("The game exited - removing routes, other traffic returns to the normal path.");
                if (_adapter is not null) _routes?.RemoveGameRoutes(_adapter.InterfaceIndex);
                // Do not claim Connected while a reconnect is still in progress.
                if (_tunnel is not null)
                {
                    SetState(TunnelState.Connected, $"Connected to {_relay?.Name} - waiting for {_game?.Name} to start");
                }
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            SetState(TunnelState.Faulted, "Failed to update the routing table");
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

    public async Task DisconnectAsync()
    {
        if (_state == TunnelState.Disconnected) return;
        SetState(TunnelState.Disconnected, "Disconnecting...");
        await TeardownAsync().ConfigureAwait(false);
        SetState(TunnelState.Disconnected, "Not connected");
    }

    /// <summary>Tears everything down in reverse order. Must never throw.</summary>
    private async Task TeardownAsync()
    {
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

        try
        {
            if (_routes is not null && _adapter is not null) _routes.RemoveAll(_adapter.InterfaceIndex);
        }
        catch (Exception ex)
        {
            _log($"Error removing routes (deleting the adapter will clean up the rest): {ex.Message}");
        }
        _routes = null;

        if (_tunnel is not null) LogGameDestinations(_tunnel);
        _tunnel?.Dispose();
        _tunnel = null;

        if (_supervisor is not null)
        {
            _cts?.Cancel();
            try { await _supervisor.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _supervisor = null;
        }

        if (_gamePingProbe is not null)
        {
            _cts?.Cancel();
            try { await _gamePingProbe.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _gamePingProbe = null;
        }

        // Deleting the adapter comes last, and it is also the safety brake: any route still
        // pointing at it disappears along with it.
        _adapter?.Dispose();
        _adapter = null;

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }
    }

    // -------------------------------------------------------------- status

    public StatusMessage Snapshot()
    {
        // Read once. The headline and the flag saying how it was arrived at must agree, and
        // reading the property twice inside the initializer could catch the probe going stale
        // between the two - a status that says "measured" over an estimated number.
        var direct = DirectGamePingMs;

        return new StatusMessage
        {
            State = _state,
            Detail = _detail,
            Error = _error,
            RelayId = _relay?.Id,
            RelayName = _relay?.Name,
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
            Configured = (_config.HasKey || _token is not null) && Relays.Count > 0,
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
            GameName = _game?.Name,
            ActiveRoutes = _routes?.ActiveRouteCount ?? 0,
            PacketsSent = _tunnel?.PacketsSent ?? 0,
            PacketsReceived = _tunnel?.PacketsReceived ?? 0,
            PacketsDropped = _tunnel?.PacketsDropped ?? 0,
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
            LicenceRefusal = LicenceRefusal(),
            ProfileSource = _profileSource,
            // Read from the file rather than remembered in a field, so it is right after a restart
            // and right after somebody has copied a profile in by hand. A missing file is null,
            // which the UI reads as "never" - correct on a machine that has never signed in.
            ProfileUpdatedAt = File.Exists(SealedProfilePath)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(SealedProfilePath)).ToUnixTimeSeconds()
                : null,
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

    private void SetState(TunnelState state, string detail)
    {
        _state = state;
        _detail = detail;
        StatusChanged?.Invoke(Snapshot());
    }

    private GameEntry FindGame(string id) =>
        _profile!.Games.FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"The profile has no game with id '{id}'.");

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
        _device.Dispose();
    }
}

