using System.Buffers.Binary;
using System.Net;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Paths;
using GamePingBooster.Core.Profiles;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Multi-tunnel, phase D - docs/MULTI-TUNNEL.md 5.1-5.9: a region leaves by its own relay when the plan says so.
///
/// Nothing here runs unless the game's region routing is "on" (config.json or the profile) AND a plan took at
/// least one region off home. Until then the connection is the single tunnel it has always been: no dispatcher,
/// the adapter re-addressed on a failover as before (G4).
///
/// The pieces, and who owns them:
///
///   - <see cref="PathDispatcher"/> decides per packet and keeps destinations in use on their tunnel (G1). Created
///     on the first plan that leaves home, with the adapter's address fixed from then on (5.2).
///   - the other tunnels ("secondaries"), at most <see cref="RegionRouting.MaxTunnels"/> - 1, one per relay (G5),
///     opened by <see cref="ApplyPlanAsync"/> through the way into the relay that measured fastest, and pinned.
///   - the supervisor, which is the only thread that opens, closes or replaces any of them: it calls
///     <see cref="SuperviseOtherTunnels"/> every pass - a secondary silent 15 s takes its own regions home (G6), one
///     no region needs is closed once nothing is stuck to it - and <see cref="TearDownMultiTunnel"/> when the game
///     changes.
///
/// What this phase does not do yet: the in-game ping, the spike recorder, entry switching and the status follow the
/// home tunnel only (5.8), a region planned "direct" stays home, and the plan is made once per connection and home
/// relay - there is no re-plan after a match. The log names every tunnel's traffic, so a match on another relay is
/// visible there.
/// </summary>
internal sealed partial class TunnelEngine
{
    /// <summary>A tunnel to a relay other than home. The dictionary holding these is guarded by itself.</summary>
    private sealed record OtherTunnel(string RelayId, RelayEntry Way, TunnelClient Client);

    /// <summary>The dispatcher, or null while the connection is one tunnel. Supervisor and teardown only.</summary>
    private volatile PathDispatcher? _paths;

    /// <summary>The secondaries by relay id. Locked on itself: the supervisor opens and closes, teardown disposes.</summary>
    private readonly Dictionary<string, OtherTunnel> _otherTunnels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set by <see cref="SwitchGame"/>; the supervisor tears the paths of the previous game down.</summary>
    private string? _multiTunnelResetReason;

    private bool _faultReported;
    private int _otherTunnelLogPass;

    /// <summary>Every tunnel's game UDP, home and secondaries: a match on another relay is still a match.</summary>
    private long AllGameUdpPackets(TunnelClient home)
    {
        var total = home.Destinations.UdpPackets;
        lock (_otherTunnels)
        {
            foreach (var other in _otherTunnels.Values) total += other.Client.Destinations.UdpPackets;
        }
        return total;
    }

    /// <summary>The open tunnel to <paramref name="relayId"/> other than home, or null.</summary>
    private TunnelClient? OtherTunnelTo(string relayId)
    {
        lock (_otherTunnels) return _otherTunnels.TryGetValue(relayId, out var t) ? t.Client : null;
    }

    /// <summary>
    /// Puts a plan in force: opens a tunnel to each relay it sends a region to, creates the dispatcher on the first
    /// plan that leaves home, swaps the plan in, and closes what is no longer needed once nothing is stuck to it.
    /// Returns whether the plan is in force; false leaves every region where it was.
    /// </summary>
    private async Task<bool> ApplyPlanAsync(TunnelClient home, GameEntry game, IReadOnlyList<RegionDecision> plan,
        IReadOnlyDictionary<string, Dictionary<string, string>> viaWay, ProfileBundle profile, CancellationToken ct)
    {
        var adapter = _adapter;
        var routes = _routes;
        var pump = _pump;
        if (adapter is null || routes is null || pump is null || !ReferenceEquals(home, _tunnel)) return false;

        if (_paths?.FaultReason is { } fault)
        {
            _log($"Region routing: not applied - the dispatcher collapsed earlier this connection ({fault}).");
            return false;
        }
        foreach (var d in plan.Where(d => d.Path.Kind == PathKind.Direct))
        {
            _log($"  {d.RegionId}: direct is not built into this version - it stays on home.");
        }

        var allWanted = plan.Where(d => d.Path.Kind == PathKind.Relay).Select(d => d.Path.RelayId!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var wanted = allWanted.Take(RegionRouting.MaxTunnels - 1).ToList();
        foreach (var dropped in allWanted.Skip(wanted.Count))
        {
            _log($"  {string.Join(", ", plan.Where(d => d.Path.RelayId == dropped).Select(d => d.RegionId))} -> {dropped}: not opened - " +
                 $"{RegionRouting.MaxTunnels} tunnels at most, home included, and {string.Join(" and ", wanted)} came first. Stays on home.");
        }
        if (_paths is null && wanted.Count == 0)
        {
            _log("Region routing: every region stays on home - one tunnel, as before.");
            return true;
        }

        var paths = _paths;
        if (paths is null)
        {
            var table = RegionTable.Build([.. game.Regions.Select(r => (r.Id, (IReadOnlyList<string>)r.Cidrs))]);
            if (!table.Usable)
            {
                _log($"Region routing: {game.Name}'s regions overlap, so it stays on one tunnel - " + string.Join(" ", table.Problems));
                return false;
            }
            paths = new PathDispatcher(home.InnerIp, table, home);
            home.Dispatcher = paths;
            _paths = paths;
            _faultReported = false;
            pump.SetDispatcher(paths);
            _log($"Region routing is on for {game.Name}: the adapter keeps {home.Session.ClientIp} for the rest of this " +
                 "connection, and tunnels to other relays rewrite to their own address.");
        }

        var psk = System.Text.Encoding.UTF8.GetBytes(_config.Psk);
        foreach (var relayId in wanted)
        {
            if (OtherTunnelTo(relayId) is not null) continue;
            var wayId = plan.Where(d => d.Path.RelayId == relayId)
                .Select(d => viaWay.TryGetValue(d.RegionId, out var w) && w.TryGetValue(relayId, out var id) ? id : null)
                .FirstOrDefault(id => id is not null) ?? relayId;
            var way = RelayPaths.DoorsOf(profile.Relays, relayId).FirstOrDefault(w => w.Id.Equals(wayId, StringComparison.OrdinalIgnoreCase));
            if (way is null) continue;

            var client = await HandshakeForPlanAsync(way, psk, ct).ConfigureAwait(false);
            if (client is null) continue;
            if (client.Session.Mtu < home.Session.Mtu)
            {
                _log($"  {way.Name} [{way.Id}]: MTU {client.Session.Mtu} is below the adapter's {home.Session.Mtu} - not used.");
                client.Dispose();
                continue;
            }

            try
            {
                lock (_otherTunnels) _otherTunnels[relayId] = new OtherTunnel(relayId, way, client);
                PinOtherTunnels(routes);
                client.Dispatcher = paths;
                client.StartPumping(adapter, ct);
                _log($"  Opened a tunnel to {way.Name} [{way.Id}] for {string.Join(", ", plan.Where(d => d.Path.RelayId == relayId).Select(d => d.RegionId))} " +
                     $"- inner address {client.Session.ClientIp}.");
            }
            catch (Exception ex)
            {
                _log($"  {way.Name} [{way.Id}]: could not be put to work ({ex.Message}) - its regions stay on home.");
                lock (_otherTunnels) _otherTunnels.Remove(relayId);
                paths.Remove(client);
                client.Dispose();
            }
        }

        var byRegion = new TunnelClient?[paths.Table.RegionCount];
        for (var i = 0; i < byRegion.Length; i++)
        {
            var decision = plan.FirstOrDefault(d => d.RegionId == paths.Table.RegionIdAt(i));
            if (decision?.Path.Kind == PathKind.Relay) byRegion[i] = OtherTunnelTo(decision.Path.RelayId!);
        }
        paths.SetPlan(byRegion);
        _log("Region routing in force: " + string.Join(", ", Enumerable.Range(0, byRegion.Length)
            .Select(i => $"{paths.Table.RegionIdAt(i)} -> {(byRegion[i] is { } t ? NameOf(t) : "home")}")) +
            ". Servers already in use keep the tunnel they started on.");

        SuperviseOtherTunnels();
        return true;
    }

    /// <summary>
    /// One supervisor pass over the other tunnels - see the class summary. Also reports a collapsed dispatcher once,
    /// and every 30 s what each other tunnel carried.
    /// </summary>
    private void SuperviseOtherTunnels()
    {
        var paths = _paths;
        if (paths is null) return;

        if (paths.FaultReason is { } fault && !_faultReported)
        {
            _faultReported = true;
            _log($"Region routing collapsed to the home tunnel for the rest of this connection: {fault}. Tunnels to other " +
                 "relays close as the servers in use on them go quiet.");
        }

        List<OtherTunnel> others;
        lock (_otherTunnels) others = [.. _otherTunnels.Values];
        var plan = paths.Plan;
        var changed = false;
        foreach (var other in others)
        {
            if (other.Client.SinceLastHeard >= SilenceBeforeDead)
            {
                var lost = paths.Remove(other.Client);
                _log($"{other.Way.Name} [{other.Way.Id}] has been silent {other.Client.SinceLastHeard.TotalSeconds:F0} s - its regions go " +
                     $"back to home" + (lost > 0 ? $", and {lost} server(s) in use on it are lost, as a relay dying loses a match today." : "."));
                Close(other, announce: false);
                changed = true;
                continue;
            }
            if (!plan.Any(t => ReferenceEquals(t, other.Client)) && paths.CountStuckTo(other.Client) == 0)
            {
                paths.Remove(other.Client);
                _log($"Closed the tunnel to {other.Way.Name} [{other.Way.Id}] - no region leaves by it and no server in use is on it.");
                Close(other, announce: true);
                changed = true;
            }
        }
        if (changed && _routes is { } routes) PinOtherTunnels(routes);

        if (++_otherTunnelLogPass % 6 == 0)
        {
            paths.Sweep();
            foreach (var other in others.Where(o => o.Client.Destinations.HasTraffic))
            {
                _log($"Tunnel via {other.Way.Name} [{other.Way.Id}]: {other.Client.PacketsSent} up, {other.Client.PacketsReceived} down, " +
                     $"{paths.CountStuckTo(other.Client)} server(s) in use, rtt {other.Client.LastRttMs:F0} ms.");
                _log(other.Client.Destinations.Format($"{other.Way.Name} ({other.Way.Location ?? "location unknown"})"));
            }
        }
    }

    /// <summary>
    /// Back to one tunnel: every other tunnel closed, the dispatcher off the pump, and the adapter given home's
    /// address again when a reconnect left it on another. For a game change - the previous game's matches are over
    /// - and connection teardown, where <paramref name="readdress"/> is false because the adapter is going too.
    /// </summary>
    private void TearDownMultiTunnel(string why, bool readdress)
    {
        var paths = _paths;
        List<OtherTunnel> others;
        lock (_otherTunnels)
        {
            others = [.. _otherTunnels.Values];
            _otherTunnels.Clear();
        }
        if (paths is null && others.Count == 0) return;

        _pump?.SetDispatcher(null);
        _paths = null;
        _otherPath = null;
        foreach (var other in others)
        {
            paths?.Remove(other.Client);
            other.Client.Dispose();
        }

        var home = _tunnel;
        if (home is not null) home.Dispatcher = null;
        try
        {
            if (_routes is { } routes) routes.PinPathRoutes([]);
            if (readdress && paths is not null && home is not null && home.InnerIp != paths.AdapterIp &&
                _adapter is { } adapter && _routes is { } r)
            {
                r.ConfigureAdapter(adapter.InterfaceIndex, home.Session.ClientIp, prefixLength: 24, home.Session.Mtu);
                r.RestoreRoutes(adapter.InterfaceIndex);
            }
        }
        catch (Exception ex)
        {
            _log($"Region routing: putting the single tunnel back failed part way ({ex.Message}).");
        }
        _log($"Region routing off - {why}. Back to one tunnel" + (others.Count > 0 ? $"; closed {others.Count} other." : "."));
    }

    private void Close(OtherTunnel other, bool announce)
    {
        lock (_otherTunnels)
        {
            if (_otherTunnels.TryGetValue(other.RelayId, out var current) && ReferenceEquals(current, other)) _otherTunnels.Remove(other.RelayId);
        }
        if (_otherPath is { } path && ReferenceEquals(path.Tunnel, other.Client)) _otherPath = null;
        if (announce) other.Client.Dispose();
        else Abandon(other.Client);
    }

    /// <summary>The relays of the other tunnels, and the ways into them, pinned to the physical adapter.</summary>
    private void PinOtherTunnels(Network.RouteManager routes)
    {
        List<IPAddress> addresses;
        lock (_otherTunnels) addresses = [.. _otherTunnels.Values.Select(o => o.Client.Endpoint.Address)];
        try
        {
            routes.PinPathRoutes(addresses);
        }
        catch (Exception ex)
        {
            _log($"Region routing: could not pin the other tunnels' relays ({ex.Message}). No profile routes a relay, so they still work.");
        }
    }

    private string NameOf(TunnelClient client)
    {
        lock (_otherTunnels)
        {
            return _otherTunnels.Values.FirstOrDefault(o => ReferenceEquals(o.Client, client)) is { } o ? $"{o.Way.Name} [{o.Way.Id}]" : "?";
        }
    }

    // ------------------------------------------------------------ which tunnel carries the match

    /// <summary>
    /// Which tunnel is carrying the match (5.8). A new one per connection, updated once a second by the in-game ping
    /// loop - the only reader of it that needs to act on a change - and read by the recorder and the status.
    /// </summary>
    private volatile MatchCarrier<TunnelClient> _carrier = new();

    /// <summary>
    /// The second-leg measurement of the match region through a tunnel other than home, for the estimate while the
    /// match is on it. Home's is <see cref="_path"/>, which it never overwrites: that one is home's, measured at
    /// connect, and a match on another relay says nothing about it.
    /// </summary>
    private volatile OtherPath? _otherPath;

    private sealed record OtherPath(TunnelClient Tunnel, PathMeasurement Path);

    /// <summary>The tunnel carrying the match, the way into the relay it is on, and whether it is home.</summary>
    private readonly record struct Carrying(TunnelClient? Tunnel, RelayEntry? Relay, bool IsHome);

    /// <summary>
    /// What the in-game ping, the recorder and the status follow: the carrier when it is another tunnel still open,
    /// otherwise home - which is also the answer whenever region routing is not in force.
    /// </summary>
    private Carrying CarryingNow()
    {
        var home = _tunnel;
        if (_carrier.Current is { } carrier && !ReferenceEquals(carrier, home) && OtherTunnelOf(carrier) is { } other)
        {
            return new Carrying(other.Client, other.Way, IsHome: false);
        }
        return new Carrying(home, _relay, IsHome: true);
    }

    /// <summary>
    /// One reading for <see cref="_carrier"/>: every open tunnel's game UDP. Returns the carrier. In-game ping loop only.
    /// </summary>
    private TunnelClient UpdateCarrier(TunnelClient home)
    {
        List<(TunnelClient, long)> others;
        lock (_otherTunnels) others = [.. _otherTunnels.Values.Select(o => (o.Client, o.Client.Destinations.UdpPackets))];
        return _carrier.Update(Environment.TickCount64, home, home.Destinations.UdpPackets, others);
    }

    private List<TunnelClient> OtherTunnelsNow()
    {
        lock (_otherTunnels) return [.. _otherTunnels.Values.Select(o => o.Client)];
    }

    private OtherTunnel? OtherTunnelOf(TunnelClient client)
    {
        lock (_otherTunnels) return _otherTunnels.Values.FirstOrDefault(o => ReferenceEquals(o.Client, client));
    }

    /// <summary>The second-leg measurement through <paramref name="tunnel"/>: home's own, or the other tunnel's.</summary>
    private PathMeasurement? PathFor(TunnelClient? tunnel) =>
        tunnel is null ? null
        : ReferenceEquals(tunnel, _tunnel) ? _path
        : _otherPath is { } other && ReferenceEquals(other.Tunnel, tunnel) ? other.Path
        : null;

    private void SetPathFor(TunnelClient tunnel, PathMeasurement path)
    {
        if (ReferenceEquals(tunnel, _tunnel)) _path = path;
        else _otherPath = new OtherPath(tunnel, path);
    }

    /// <summary>How the log names a tunnel: the relay and the way into it, and "home" for home.</summary>
    private string TunnelLabel(TunnelClient tunnel) =>
        ReferenceEquals(tunnel, _tunnel)
            ? _relay is { } r ? $"{r.Name} [{r.Id}] (home)" : "home"
            : NameOf(tunnel);

    /// <summary>
    /// Each region that leaves by another relay, and that relay, for the app - nothing while region routing is not
    /// in force or every region is on home.
    /// </summary>
    private List<RegionPathStatus>? RegionPathsForStatus()
    {
        var paths = _paths;
        var game = _game;
        if (paths is null || game is null) return null;

        var plan = paths.Plan;
        var list = new List<RegionPathStatus>();
        for (var i = 0; i < plan.Length; i++)
        {
            if (plan[i] is not { } tunnel || OtherTunnelOf(tunnel) is not { } other) continue;
            var regionId = paths.Table.RegionIdAt(i);
            list.Add(new RegionPathStatus
            {
                Region = game.Regions.FirstOrDefault(r => r.Id == regionId)?.Name ?? regionId,
                RelayName = other.Way.Name,
            });
        }
        return list.Count == 0 ? null : list;
    }

    /// <summary>Game UDP each tunnel had carried at the last pass. Supervisor only.</summary>
    private readonly Dictionary<TunnelClient, long> _udpAtLastPass = new(ReferenceEqualityComparer.Instance);

    /// <summary>The match server last named in the log, and the tunnel it was on. Supervisor only.</summary>
    private (TunnelClient? Tunnel, IPAddress? Server) _matchAnnounced;

    /// <summary>The region table of the game being played, for naming a server's region without a dispatcher.</summary>
    private (string GameId, RegionTable Table)? _namingTable;

    /// <summary>A tunnel carrying at least this many game packets a second is carrying a match - 9-150 by game.</summary>
    private const double MatchPacketsPerSecond = 5;

    /// <summary>
    /// One supervisor pass: when a tunnel starts carrying a match to a server it was not carrying before, says in one
    /// line which region the server is in and which tunnel carries it - and, with region routing in force, whether
    /// that is the plan. No address: the log is shared with support, and the region says what matters.
    /// </summary>
    private void AnnounceMatchServer(TunnelClient home)
    {
        var game = _game;
        if (game is null) return;

        List<(TunnelClient Tunnel, string Name, bool IsHome)> tunnels = [(home, _relay is { } r ? $"{r.Name} [{r.Id}]" : "home", true)];
        lock (_otherTunnels) tunnels.AddRange(_otherTunnels.Values.Select(o => (o.Client, $"{o.Way.Name} [{o.Way.Id}]", false)));

        (TunnelClient Tunnel, string Name, bool IsHome)? busiest = null;
        double busiestRate = 0;
        foreach (var t in tunnels)
        {
            var now = t.Tunnel.Destinations.UdpPackets;
            if (_udpAtLastPass.TryGetValue(t.Tunnel, out var before))
            {
                var rate = (now - before) / 5.0;
                if (rate >= MatchPacketsPerSecond && rate > busiestRate)
                {
                    busiest = t;
                    busiestRate = rate;
                }
            }
            _udpAtLastPass[t.Tunnel] = now;
        }
        foreach (var gone in _udpAtLastPass.Keys.Where(k => !tunnels.Any(t => ReferenceEquals(t.Tunnel, k))).ToList()) _udpAtLastPass.Remove(gone);

        if (busiest is not { } carrying || carrying.Tunnel.Destinations.PrimaryDestination is not { } server) return;
        if (ReferenceEquals(_matchAnnounced.Tunnel, carrying.Tunnel) && server.Equals(_matchAnnounced.Server)) return;
        _matchAnnounced = (carrying.Tunnel, server);

        var paths = _paths;
        var table = paths?.Table;
        if (table is null)
        {
            if (_namingTable is not { } cached || cached.GameId != game.Id)
            {
                _namingTable = (game.Id, RegionTable.Build([.. game.Regions.Select(x => (x.Id, (IReadOnlyList<string>)x.Cidrs))]));
            }
            table = _namingTable!.Value.Table;
        }

        var index = table.Find(RegionTable.ToUInt32(server));
        var regionId = index < 0 ? null : table.RegionIdAt(index);
        var regionName = regionId is null ? null : game.Regions.FirstOrDefault(x => x.Id == regionId)?.Name;
        var where = regionId is null ? "outside every region of the profile" : $"in {regionId} ({regionName})";

        var planned = "";
        if (paths is not null && index >= 0)
        {
            var plannedTunnel = paths.Plan[index];
            var plannedName = plannedTunnel is null ? "home" : NameOf(plannedTunnel);
            var matches = plannedTunnel is null ? carrying.IsHome : ReferenceEquals(plannedTunnel, carrying.Tunnel);
            planned = matches
                ? " - as planned."
                : $" - the plan says {plannedName}; the server was already in use on this tunnel when the plan was made, and keeps it.";
        }
        _log($"Match server {where}, {busiestRate:F0} packets/s - carried by {carrying.Name}" +
             (carrying.IsHome ? " (home)" : "") + (planned == "" ? "." : planned));
    }

    private static IPAddress ToAddress(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
