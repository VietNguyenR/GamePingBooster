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
            _relay = FindRelay(relayId ?? _config.DefaultRelayId);
            var endpoint = ParseEndpoint(_relay.Endpoint);

            SetState(TunnelState.Connecting, "Creating the virtual adapter...");
            _adapter = WintunAdapter.Create(_config.AdapterName);
            _adapter.StartSession();
            _log($"Virtual adapter '{_config.AdapterName}' is ready, interface index {_adapter.InterfaceIndex}");

            SetState(TunnelState.Connecting, $"Connecting to {_relay.Name}...");
            var psk = System.Text.Encoding.UTF8.GetBytes(_config.Psk);
            _tunnel = new TunnelClient(endpoint, psk, _log);
            var session = await _tunnel.HandshakeAsync(attempts: 4, token).ConfigureAwait(false);

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

    private void OnGameStateChanged(bool running, string? processName)
    {
        try
        {
            if (running)
            {
                _log($"Detected {processName}.exe running - installing routes.");
                InstallRoutes();
                SetState(TunnelState.Connected, $"Accelerating {_game?.Name} through {_relay?.Name}");
            }
            else
            {
                _log("The game exited - removing routes, other traffic returns to the normal path.");
                if (_adapter is not null) _routes?.RemoveGameRoutes(_adapter.InterfaceIndex);
                SetState(TunnelState.Connected, $"Connected to {_relay?.Name} - waiting for {_game?.Name} to start");
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
