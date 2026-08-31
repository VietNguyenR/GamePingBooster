using System.Net;
using System.Text.Json;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Service.Native;
using GamePingBooster.Service.Network;

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
    private readonly ulong _clientId = ClientIdentity.Load();

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
    }

    // -------------------------------------------------------------- profile

    /// <summary>
    /// Loads the profile: prefers the copy fetched from the server (when ProfileUrl is set and
    /// reachable), otherwise falls back to the local file shipped with the app.
    /// </summary>
    public async Task LoadProfileAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_config.ProfileUrl))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var json = await http.GetStringAsync(_config.ProfileUrl, ct).ConfigureAwait(false);
                var fetched = JsonSerializer.Deserialize(json, ProfileJsonContext.Default.ProfileBundle);
                if (fetched is not null)
                {
                    _profile = fetched;
                    // Keep a copy as the fallback for the next time the machine is offline.
                    var cachePath = Path.Combine(ServiceConfig.DefaultDirectory, "profile.cache.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    await File.WriteAllTextAsync(cachePath, json, ct).ConfigureAwait(false);
                    _log($"Fetched the profile from {_config.ProfileUrl} (generated {fetched.GeneratedUtc:u})");
                    return;
                }
            }
            catch (Exception ex)
            {
                _log($"Could not fetch the profile from the server ({ex.Message}) - using the local copy.");
            }
        }

        var local = Path.IsPathRooted(_config.ProfilePath)
            ? _config.ProfilePath
            : Path.Combine(AppContext.BaseDirectory, _config.ProfilePath);
        if (!File.Exists(local))
        {
            local = Path.Combine(ServiceConfig.DefaultDirectory, "profile.cache.json");
        }
        if (!File.Exists(local))
        {
            throw new FileNotFoundException($"No profile at {_config.ProfilePath} and no cached copy either.");
        }

        var localJson = await File.ReadAllTextAsync(local, ct).ConfigureAwait(false);
        _profile = JsonSerializer.Deserialize(localJson, ProfileJsonContext.Default.ProfileBundle)
                   ?? throw new InvalidOperationException($"The profile at {local} is not valid.");
        _log($"Loaded the local profile from {local}");
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

            if (_profile is null) await LoadProfileAsync(token).ConfigureAwait(false);

            _game = FindGame(gameId ?? _config.DefaultGameId);
            var psk = System.Text.Encoding.UTF8.GetBytes(_config.Psk);

            // Choose the relay before creating anything. Probing is pure UDP - no adapter, no
            // routes - so a relay that turns out to be unreachable costs nothing but a timeout.
            SetState(TunnelState.Connecting, "Measuring relays...");
            (_relay, _tunnel) = await SelectRelayAsync(relayId ?? _config.DefaultRelayId, psk, token)
                .ConfigureAwait(false);
            var endpoint = ParseEndpoint(_relay.Endpoint);
            var session = _tunnel.Session;

            SetState(TunnelState.Connecting, "Creating the virtual adapter...");
            _adapter = WintunAdapter.Create(_config.AdapterName);
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

    // ------------------------------------------------------- relay selection

    /// <summary>
    /// Picks a relay by measuring it. The handshake is a single round trip over the physical
    /// path, so it doubles as a latency probe - no adapter, no routes, nothing to undo.
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
                var client = new TunnelClient(ParseEndpoint(pinned.Endpoint), psk, _clientId, _log);
                await client.HandshakeAsync(attempts: 4, ct).ConfigureAwait(false);
                return (pinned, client);
            }
            _log($"The profile has no relay '{preferredId}' - measuring all of them instead.");
        }

        if (candidates.Count == 1)
        {
            var only = candidates[0];
            var client = new TunnelClient(ParseEndpoint(only.Endpoint), psk, _clientId, _log);
            await client.HandshakeAsync(attempts: 4, ct).ConfigureAwait(false);
            return (only, client);
        }

        var probes = new List<(RelayEntry Relay, TunnelClient Client, double Rtt)>();
        foreach (var relay in candidates)
        {
            ct.ThrowIfCancellationRequested();
            TunnelClient? client = null;
            try
            {
                client = new TunnelClient(ParseEndpoint(relay.Endpoint), psk, _clientId, _log);
                await client.HandshakeAsync(attempts: 2, ct).ConfigureAwait(false);
                probes.Add((relay, client, client.HandshakeRttMs));
                _log($"  {relay.Name} ({relay.Location}): {client.HandshakeRttMs:F0} ms");
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                _log($"  {relay.Name} ({relay.Location}): unreachable - {ex.Message}");
                client?.Dispose();
            }
        }

        if (probes.Count == 0)
        {
            throw new InvalidOperationException(
                $"None of the {candidates.Count} relays in the profile answered. Check the network, " +
                "the endpoints in the profile, and that the PSK matches.");
        }

        var best = probes.OrderBy(p => p.Rtt).First();
        foreach (var probe in probes)
        {
            if (!ReferenceEquals(probe.Client, best.Client)) probe.Client.Dispose();
        }
        _log($"Chose {best.Relay.Name} at {best.Rtt:F0} ms.");
        return (best.Relay, best.Client);
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
    private async Task ReconnectAsync(CancellationToken ct)
    {
        var adapter = _adapter;
        var routes = _routes;
        var previous = _relay;
        if (previous is null || adapter is null || routes is null) return;

        var previousIp = _tunnel?.Session.ClientIp;
        var psk = System.Text.Encoding.UTF8.GetBytes(_config.Psk);

        Abandon(_tunnel);
        _tunnel = null;

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
                    client = new TunnelClient(ParseEndpoint(relay.Endpoint), psk, _clientId, _log);
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
                    }

                    _tunnel = client;

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
        _routes.InstallGameRoutes(_adapter.InterfaceIndex, cidrs);
        _log($"Installed {cidrs.Count} routes into the virtual adapter.");
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

        _tunnel?.Dispose();
        _tunnel = null;

        if (_supervisor is not null)
        {
            _cts?.Cancel();
            try { await _supervisor.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _supervisor = null;
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

    public StatusMessage Snapshot() => new()
    {
        State = _state,
        Detail = _detail,
        Error = _error,
        RelayId = _relay?.Id,
        RelayName = _relay?.Name,
        TunnelPingMs = _tunnel?.LastRttMs,
        LossRatio = _tunnel?.LossRatio,
        GameRunning = _watcher?.IsGameRunning ?? false,
        GameName = _game?.Name,
        ActiveRoutes = _routes?.ActiveRouteCount ?? 0,
        PacketsSent = _tunnel?.PacketsSent ?? 0,
        PacketsReceived = _tunnel?.PacketsReceived ?? 0,
    };

    /// <summary>Relay list for the UI to offer to the user.</summary>
    public IReadOnlyList<RelayEntry> Relays => _profile?.Relays ?? [];

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

    public async ValueTask DisposeAsync() => await TeardownAsync().ConfigureAwait(false);
}
