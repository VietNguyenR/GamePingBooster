using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GamePingBooster.Core.Native;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Network;

/// <summary>
/// Owns the Windows routing table: pushes the game's IP ranges into the virtual adapter and
/// pulls them back out afterwards.
///
/// Through the IP Helper API (<see cref="IpHelper"/>), not netsh. It used to start one netsh.exe
/// per route and per setting, chosen for <c>store=active</c> - routes that live in RAM and are gone
/// after a reboot, so a hang or a BSOD leaves a clean routing table. That property is kept:
/// CreateIpForwardEntry2 writes to the active store only. What went was the cost. Each netsh process
/// took seconds as LocalSystem while the adapter was coming up, and on 2026-09-17 a connect spent
/// about 27 of its 30 seconds on them, with 64 game routes adding another 8.5 when the game opened.
/// It also retires the reason every add had its own process: netsh reported failures in the display
/// language and through one exit code, where each call here returns its own Win32 error.
///
/// Every operation logs how long it took, so a slow connect says where its time went.
/// </summary>
internal sealed class RouteManager
{
    private readonly Action<string>? _log;

    /// <summary>Route metric for everything added here - the same metric=1 netsh was given.</summary>
    private const uint Metric = 1;

    public RouteManager(Action<string>? log = null)
    {
        IpHelper.SelfCheck();
        _log = log;
    }

    /// <summary>
    /// Runs one routing operation and logs its duration - "Routing: pinned the relay in 2 ms." - or how
    /// long it ran before failing. The failure itself is the caller's to report; this only times it.
    /// </summary>
    private void Timed(string what, Action action)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            action();
            _log?.Invoke($"Routing: {what} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
        }
        catch
        {
            _log?.Invoke($"Routing: failed after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms: {what}.");
            throw;
        }
    }

    private readonly List<string> _installedPrefixes = [];

    // The lobby's host routes, kept apart from the game routes because they live on a different
    // clock: installed when the tunnel connects and left in place when the game exits. One shared
    // list would have RemoveGameRoutes pull the lobby off the tunnel every time a match ended.
    private readonly List<string> _lobbyPrefixes = [];

    private string? _pinnedRelayPrefix;

    // The interface the pin was made through. Deleting a route REQUIRES naming its interface, and
    // the physical adapter can change between pinning and unpinning (Wi-Fi to Ethernet), so the
    // index has to be remembered rather than looked up again at delete time.
    private uint _pinnedRelayInterface;

    // Every way into the relay in use - itself and its entries - pinned the same way, so a probe
    // measuring the ways the tunnel is NOT using can never follow a game route into the tunnel.
    // Keyed by prefix, valued by the interface the pin went through. May include _pinnedRelayPrefix,
    // which stays PinRelayRoute's to install and delete.
    private readonly Dictionary<string, uint> _pinnedDoorPrefixes = [];

    // Multi-tunnel: the relays of the other tunnels, and the way into each, pinned to the physical adapter like
    // the home relay. Never holds a prefix that is _pinnedRelayPrefix or a door - those stay theirs.
    private readonly Dictionary<string, uint> _pinnedPathPrefixes = [];

    // Multi-tunnel: destinations in use on another tunnel, pinned INTO the virtual adapter while the home tunnel
    // is reconnecting and the game's ranges are out - so a match on another tunnel keeps its exit (G1).
    private readonly List<string> _pinnedStuckPrefixes = [];

    /// <summary>Number of routes currently installed.</summary>
    public int ActiveRouteCount =>
        _installedPrefixes.Count + _lobbyPrefixes.Count + (_pinnedRelayPrefix is null ? 0 : 1) +
        _pinnedDoorPrefixes.Keys.Count(p => p != _pinnedRelayPrefix);

    /// <summary>
    /// Game routes only, excluding the pinned relay route and the lobby. The reconnect path uses
    /// this to tell whether it pulled the game off the tunnel and therefore owes it a reinstall on
    /// success.
    /// </summary>
    public int ActiveGameRouteCount => _installedPrefixes.Count;

    /// <summary>Lobby host routes currently installed.</summary>
    public int ActiveLobbyRouteCount => _lobbyPrefixes.Count;

    /// <summary>
    /// Assigns the inner IP and MTU to the virtual adapter. Call this after the relay has
    /// handed out an address during the handshake.
    /// </summary>
    public void ConfigureAdapter(uint tunInterfaceIndex, IPAddress innerIp, int prefixLength, int mtu) =>
        Timed("configured the virtual adapter", () =>
        {
            // The only IPv4 address on the adapter, usable at once: duplicate address detection has
            // nothing to find inside our own tunnel.
            var error = IpHelper.SetOnlyAddress(tunInterfaceIndex, innerIp, (byte)prefixLength);
            if (error != IpHelper.NoError)
            {
                throw new InvalidOperationException(
                    $"Could not give the virtual adapter (interface {tunInterfaceIndex}) the address " +
                    $"{innerIp}/{prefixLength}: {IpHelper.Describe(error)}.");
            }

            // MTU, and dadtransmits=0 as netsh set it.
            var (mtuError, mtuAfter) = IpHelper.SetMtu(tunInterfaceIndex, (uint)mtu);
            if (mtuError != IpHelper.NoError)
            {
                throw new InvalidOperationException(
                    $"Could not set the virtual adapter's MTU to {mtu}: {IpHelper.Describe(mtuError)}.");
            }
            if (mtuAfter != (uint)mtu)
            {
                // Not fatal - the tunnel clamps what it sends - but worth knowing about.
                _log?.Invoke($"Routing: asked for MTU {mtu} on the virtual adapter, Windows reports {mtuAfter}.");
            }

            WaitForAddress(tunInterfaceIndex, innerIp);
        });

    /// <summary>
    /// Confirms the address really landed on the adapter, reading it back from the address table
    /// rather than trusting the call that set it.
    ///
    /// Windows can report success while the address is not usable yet, and the failure that follows
    /// is silent: routes install fine, packets go nowhere, and nothing logs an error. Better to
    /// fail here, loudly, than to hand the user a tunnel that looks connected and does nothing.
    ///
    /// The table, not NetworkInformation: that went through GetAdaptersAddresses, which is what cost
    /// 4 to 5 seconds here while the new adapter was settling - see GatewayCandidates.
    /// </summary>
    private static void WaitForAddress(uint tunInterfaceIndex, IPAddress expected, int timeoutMs = 5000)
    {
        var wanted = IpHelper.ToInAddr(expected);
        var deadline = Environment.TickCount64 + timeoutMs;
        do
        {
            if (IpHelper.ReadAddresses().Any(a => a.InterfaceIndex == tunInterfaceIndex && a.Address == wanted)) return;
            Thread.Sleep(50);
        } while (Environment.TickCount64 < deadline);

        throw new InvalidOperationException(
            $"The virtual adapter (interface {tunInterfaceIndex}) still does not carry {expected} " +
            $"after {timeoutMs} ms. Windows reported success, so the adapter is most likely in a bad " +
            "state - disconnect, then connect again.");
    }

    /// <summary>
    /// <see cref="GetRouteTo"/>, with a log line when it is slow. It was the lookup, not the route, that
    /// took seconds once netsh had gone; if it ever does again the log should say so by name.
    /// </summary>
    private (uint InterfaceIndex, IPAddress Gateway)? LookUpRoute(IPAddress destination)
    {
        var started = Stopwatch.GetTimestamp();
        var found = GetRouteTo(destination);
        var took = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (took >= 50) _log?.Invoke($"Routing: looking up the way out to {destination} took {took:F0} ms.");
        return found;
    }

    /// <summary>
    /// Pins a /32 route for the relay itself through the PHYSICAL network adapter.
    ///
    /// This is the single most important step of the whole mechanism: without it, if a routed
    /// range happens to contain the relay's own address, packets destined for the relay get
    /// pushed back into the tunnel - an infinite loop that takes the machine offline. Pin first,
    /// always.
    /// </summary>
    public void PinRelayRoute(IPAddress relayIp) =>
        Timed($"pinned the relay {relayIp}", () => PinRelayRouteCore(relayIp));

    private void PinRelayRouteCore(IPAddress relayIp)
    {
        var (physIndex, gateway) = LookUpRoute(relayIp)
            ?? throw new InvalidOperationException(
                "No network adapter with a default gateway was found - is the machine offline?");

        var prefix = $"{relayIp}/32";

        // Failing over to another relay pins a different address, and _pinnedRelayPrefix only
        // holds one. Overwriting it without deleting first would strand the previous /32 in the
        // routing table forever: RemoveAll can only delete the prefix it still remembers, so the
        // old entry would outlive the service.
        if (_pinnedRelayPrefix is not null && _pinnedRelayPrefix != prefix)
        {
            // Unless it is one of the doors: switching from the relay to its entry keeps the relay
            // pinned, because it is now the other way in, and probes still measure it.
            if (!_pinnedDoorPrefixes.ContainsKey(_pinnedRelayPrefix))
            {
                DeleteRoute(_pinnedRelayPrefix, _pinnedRelayInterface);
            }
            _pinnedRelayPrefix = null;
        }

        // Already pinned as a way into the relay: a move onto it. Taken over as it stands - deleting and adding
        // it again would leave the address unpinned for a moment on the move.
        if (_pinnedDoorPrefixes.TryGetValue(prefix, out var doorInterface))
        {
            _pinnedRelayPrefix = prefix;
            _pinnedRelayInterface = doorInterface;
            return;
        }

        // Always delete before adding. This route goes through the PHYSICAL adapter, so unlike
        // the game routes it does not disappear when the virtual adapter goes away - a service
        // that was killed rather than stopped cleanly leaves it behind, possibly through a gateway
        // that has since changed. Replacing it is the only way to be sure it points where it should.
        DeleteRoute(prefix, physIndex);

        AddRoute(prefix, physIndex, gateway);
        _pinnedRelayPrefix = prefix;
        _pinnedRelayInterface = physIndex;
    }

    /// <summary>
    /// Pins exactly these addresses - every way into the relay in use, the current one included - and
    /// unpins any door pinned before that is no longer one. Same mechanics as
    /// <see cref="PinRelayRoute"/>, whose own pin is never deleted from here.
    ///
    /// Why the other ways need a pin at all: the tunnel only pins the address it is using. A probe to
    /// the relay's own address while the tunnel runs through an entry would otherwise take the
    /// ordinary routing table, and if a game range happens to contain that address it goes into the
    /// tunnel - measuring a loop, and switching the player onto one.
    /// </summary>
    public void PinDoorRoutes(IReadOnlyCollection<IPAddress> addresses) =>
        Timed($"pinned {addresses.Count} way(s) into the relay", () => PinDoorRoutesCore(addresses));

    private void PinDoorRoutesCore(IReadOnlyCollection<IPAddress> addresses)
    {
        var wanted = new Dictionary<string, IPAddress>();
        foreach (var address in addresses) wanted[$"{address}/32"] = address;

        foreach (var (prefix, iface) in _pinnedDoorPrefixes.ToList())
        {
            if (wanted.ContainsKey(prefix)) continue;
            _pinnedDoorPrefixes.Remove(prefix);
            if (prefix != _pinnedRelayPrefix) DeleteRoute(prefix, iface);
        }

        foreach (var (prefix, address) in wanted)
        {
            if (_pinnedDoorPrefixes.ContainsKey(prefix)) continue;
            if (prefix == _pinnedRelayPrefix)
            {
                _pinnedDoorPrefixes[prefix] = _pinnedRelayInterface;
                continue;
            }

            var (physIndex, gateway) = LookUpRoute(address)
                ?? throw new InvalidOperationException(
                    "No network adapter with a default gateway was found - is the machine offline?");
            DeleteRoute(prefix, physIndex);
            // Recorded before the add, as the game routes are: if it fails the table may still hold it.
            _pinnedDoorPrefixes[prefix] = physIndex;
            AddRoute(prefix, physIndex, gateway);
        }
    }

    /// <summary>
    /// Pins the relays of the other tunnels - and the way into each they use - to the physical adapter, the way the
    /// home relay is pinned: a backstop, since no profile routes a relay's address. The set replaces the last one.
    /// </summary>
    public void PinPathRoutes(IReadOnlyCollection<IPAddress> addresses) =>
        Timed($"pinned {addresses.Count} other tunnel relay(s)", () =>
        {
            var wanted = new Dictionary<string, IPAddress>();
            foreach (var address in addresses) wanted[$"{address}/32"] = address;

            foreach (var (prefix, iface) in _pinnedPathPrefixes.ToList())
            {
                if (wanted.ContainsKey(prefix)) continue;
                _pinnedPathPrefixes.Remove(prefix);
                DeleteRoute(prefix, iface);
            }
            foreach (var (prefix, address) in wanted)
            {
                if (_pinnedPathPrefixes.ContainsKey(prefix) || prefix == _pinnedRelayPrefix || _pinnedDoorPrefixes.ContainsKey(prefix)) continue;
                var (physIndex, gateway) = LookUpRoute(address)
                    ?? throw new InvalidOperationException(
                        "No network adapter with a default gateway was found - is the machine offline?");
                DeleteRoute(prefix, physIndex);
                _pinnedPathPrefixes[prefix] = physIndex;
                AddRoute(prefix, physIndex, gateway);
            }
        });

    /// <summary>
    /// Pins each of <paramref name="destinations"/> into the virtual adapter as a /32, for while the game's ranges
    /// are out during a home reconnect. Same mechanics as a game route; removed by <see cref="UnpinStuckDestinations"/>.
    /// </summary>
    public void PinStuckDestinations(uint tunInterfaceIndex, IReadOnlyCollection<IPAddress> destinations)
    {
        var fresh = destinations.Select(d => $"{d}/32")
            .Where(p => !_pinnedStuckPrefixes.Contains(p) && !_installedPrefixes.Contains(p) && !_lobbyPrefixes.Contains(p))
            .Distinct().ToList();
        if (fresh.Count == 0) return;
        Timed($"pinned {fresh.Count} destination(s) in use on other tunnels", () =>
        {
            DeleteRoutes(fresh, tunInterfaceIndex);
            _pinnedStuckPrefixes.AddRange(fresh);
            foreach (var prefix in fresh) AddRoute(prefix, tunInterfaceIndex, nextHop: null);
        });
    }

    /// <summary>
    /// Takes the /32s of <see cref="PinStuckDestinations"/> out again - except one that became a game or lobby route
    /// meanwhile, which is theirs now: deleting it would take that route out.
    /// </summary>
    public void UnpinStuckDestinations(uint tunInterfaceIndex)
    {
        if (_pinnedStuckPrefixes.Count == 0) return;
        var ours = _pinnedStuckPrefixes.Where(p => !_installedPrefixes.Contains(p) && !_lobbyPrefixes.Contains(p)).ToList();
        _pinnedStuckPrefixes.Clear();
        if (ours.Count > 0) Timed($"unpinned {ours.Count} destination(s) in use on other tunnels", () => DeleteRoutes(ours, tunInterfaceIndex));
    }

    /// <summary>
    /// Installs routes for the game's CIDR list, pointing at the virtual adapter.
    /// A low metric so they win against the physical adapter's default route.
    ///
    /// The routes are <b>on-link</b> - no nexthop is given. This matters: Wintun presents an
    /// NDIS layer-3 medium with no link layer, so there is nothing to resolve a nexthop address
    /// against. Naming a gateway (even one inside the tunnel subnet) leaves Windows waiting on a
    /// neighbour entry that can never appear, and it silently drops the packets instead of
    /// handing them to the adapter. This is also how WireGuard configures its own routes.
    /// </summary>
    public void InstallGameRoutes(uint tunInterfaceIndex, IEnumerable<string> cidrs)
    {
        var started = Stopwatch.GetTimestamp();
        var fresh = new List<string>();
        foreach (var cidr in cidrs)
        {
            if (!IsValidIPv4Cidr(cidr)) continue;
            if (_installedPrefixes.Contains(cidr)) continue;

            // Already in the table as a lobby route. Adding it here would first DELETE it (see
            // below), and RemoveGameRoutes would later delete it again when the game exits -
            // taking the lobby off the tunnel with it. The lobby owns it; leave it alone.
            if (_lobbyPrefixes.Contains(cidr)) continue;

            fresh.Add(cidr);
        }
        if (fresh.Count == 0) return;

        // Same reasoning as PinRelayRoute: clear any leftover entry first. One read of the routing
        // table for the whole batch.
        DeleteRoutes(fresh, tunInterfaceIndex);

        // Record them before the adds run, not after: if one fails partway some routes are
        // already in the table, and teardown must still know to remove them.
        _installedPrefixes.AddRange(fresh);

        // Every add checked on its own: a route that silently fails to install looks exactly like
        // a relay that is down.
        try
        {
            foreach (var cidr in fresh) AddRoute(cidr, tunInterfaceIndex, nextHop: null);
            _log?.Invoke($"Routing: installed {fresh.Count} game route(s) in " +
                         $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
        }
        catch
        {
            _log?.Invoke($"Routing: failed after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms " +
                         $"installing {fresh.Count} game route(s).");
            throw;
        }
    }

    /// <summary>Removes every game route, leaving the pinned relay route in place.</summary>
    public void RemoveGameRoutes(uint tunInterfaceIndex)
    {
        if (_installedPrefixes.Count == 0) return;

        Timed($"removed {_installedPrefixes.Count} game route(s)",
            () => DeleteRoutes(_installedPrefixes, tunInterfaceIndex));
        _installedPrefixes.Clear();
    }

    /// <summary>
    /// Installs the lobby's host routes into the virtual adapter. Same mechanics as
    /// <see cref="InstallGameRoutes"/> - on-link, metric 1, delete-then-add, recorded before adding -
    /// but tracked separately so the game exiting does not remove them.
    ///
    /// Takes validated "a.b.c.d/32" prefixes from LobbyRoutes.ToHostRoutes; it does not re-judge
    /// them.
    /// </summary>
    public void InstallLobbyRoutes(uint tunInterfaceIndex, IEnumerable<string> hostRoutes)
    {
        var started = Stopwatch.GetTimestamp();
        var fresh = new List<string>();
        foreach (var prefix in hostRoutes)
        {
            if (!IsValidIPv4Cidr(prefix)) continue;
            if (_lobbyPrefixes.Contains(prefix)) continue;

            // The same /32 is already in the table as a game route (routeWithoutGame, or a profile
            // listing it in both places). Take it over without touching the table: deleting and
            // re-adding would drop the route for a moment for nothing, and leaving it on the game
            // list would have the game's exit remove it.
            if (_installedPrefixes.Remove(prefix))
            {
                _lobbyPrefixes.Add(prefix);
                continue;
            }

            fresh.Add(prefix);
        }
        if (fresh.Count == 0) return;

        DeleteRoutes(fresh, tunInterfaceIndex);

        _lobbyPrefixes.AddRange(fresh);

        try
        {
            foreach (var prefix in fresh) AddRoute(prefix, tunInterfaceIndex, nextHop: null);
            _log?.Invoke($"Routing: installed {fresh.Count} lobby route(s) in " +
                         $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
        }
        catch
        {
            _log?.Invoke($"Routing: failed after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms " +
                         $"installing {fresh.Count} lobby route(s).");
            throw;
        }
    }

    /// <summary>
    /// Puts back any game or lobby route this manager holds that is no longer in Windows' table on the virtual
    /// adapter, and returns how many it put back. The install methods skip a prefix they already hold, so a
    /// route Windows dropped on its own - an address change on the adapter is the moment that might - would
    /// otherwise stay missing while this manager believed it was in, and that game's traffic would go out
    /// over the player's own connection with nothing said.
    /// </summary>
    public int RestoreRoutes(uint tunInterfaceIndex)
    {
        var present = IpHelper.ReadRoutes()
            .Where(r => r.InterfaceIndex == tunInterfaceIndex)
            .Select(r => new IpHelper.Prefix(r.DestinationAddress, r.DestinationPrefixLength))
            .ToHashSet();

        var restored = 0;
        foreach (var prefix in _installedPrefixes.Concat(_lobbyPrefixes))
        {
            if (IpHelper.ParsePrefix(prefix) is not { } parsed || present.Contains(parsed)) continue;
            AddRoute(prefix, tunInterfaceIndex, nextHop: null);
            restored++;
        }
        if (restored > 0) _log?.Invoke($"Routing: {restored} route(s) had gone from the virtual adapter - put back.");
        return restored;
    }

    /// <summary>Removes the lobby's host routes, leaving game routes and the pinned relay route alone.</summary>
    public void RemoveLobbyRoutes(uint tunInterfaceIndex)
    {
        if (_lobbyPrefixes.Count == 0) return;

        Timed($"removed {_lobbyPrefixes.Count} lobby route(s)",
            () => DeleteRoutes(_lobbyPrefixes, tunInterfaceIndex));
        _lobbyPrefixes.Clear();
    }

    /// <summary>Removes everything this RouteManager installed. Always call this on teardown.</summary>
    public void RemoveAll(uint tunInterfaceIndex)
    {
        RemoveGameRoutes(tunInterfaceIndex);
        RemoveLobbyRoutes(tunInterfaceIndex);
        UnpinStuckDestinations(tunInterfaceIndex);
        if (_pinnedPathPrefixes.Count > 0) PinPathRoutes([]);

        Timed("removed the relay pins", () =>
        {
            if (_pinnedRelayPrefix is not null)
            {
                DeleteRoute(_pinnedRelayPrefix, _pinnedRelayInterface);
            }
            foreach (var (prefix, iface) in _pinnedDoorPrefixes)
            {
                if (prefix != _pinnedRelayPrefix) DeleteRoute(prefix, iface);
            }
        });
        _pinnedDoorPrefixes.Clear();
        _pinnedRelayPrefix = null;
    }

    /// <summary>
    /// The interface and gateway to reach <paramref name="destination"/> - Windows' own answer
    /// where it will give one, the first adapter with a gateway where it will not.
    ///
    /// Asking is the whole point. The fallback below is what this method used to be on its own,
    /// and it cannot rank adapters: NetworkInterface has no view of the routing table, so it
    /// takes whichever adapter the enumeration happens to hand over first. One adapter, always
    /// right. Two that are Up and carry a gateway - a VM host adapter, WSL, a VPN client, Wi-Fi
    /// and Ethernet both live - and it is a coin toss. Losing it pins the relay through a door
    /// that cannot reach it: the chosen relay goes silent within seconds while every other relay
    /// still answers, the supervisor fails over, pins the next one the same way, and the session
    /// rotates for ever without connecting. That is the 2026-09-12 report, and the customer's own
    /// fix was to disable a virtual adapter.
    ///
    /// Per DESTINATION rather than "the default route", because they are not always the same
    /// question and only the first one is the one being asked.
    /// </summary>
    public static (uint InterfaceIndex, IPAddress Gateway)? GetRouteTo(IPAddress destination)
    {
        var candidates = GatewayCandidates();
        if (candidates.Count == 0) return null;

        // Windows' answer wins when this machine can act on it - which means an adapter that is
        // actually up and has a gateway to name. GetBestInterfaceEx can legitimately return an
        // interface with no gateway of its own (a point-to-point link, or a destination that is
        // on-link), and a pin needs a next hop to name, so those fall through rather than
        // producing a route that cannot be installed.
        if (IpHelperInterop.BestInterfaceFor(destination) is { } best)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.InterfaceIndex == best) return candidate;
            }
        }

        return candidates[0];
    }

    /// <summary>
    /// Every interface that could carry traffic off this machine: each IPv4 default route that names a
    /// gateway, best first by the metric Windows itself ranks them by (route metric plus interface
    /// metric), one per interface.
    ///
    /// Read from the routing table, not from NetworkInterface. The two give the same answer - an
    /// adapter with a gateway is an adapter with a default route through it - but NetworkInterface asks
    /// GetAdaptersAddresses for every adapter on the machine, and as LocalSystem, seconds after a
    /// Wintun adapter was created, that call waited for it: 4 to 9 seconds per lookup on 2026-09-17,
    /// most of what was left of a connect once netsh had gone. The table read takes a tenth of a
    /// millisecond.
    ///
    /// Nothing is excluded by name. A virtual adapter is sometimes genuinely the way out - a VM host,
    /// a corporate VPN client - and ranking is <see cref="GetRouteTo"/>'s job. A tunnel is excluded by
    /// shape instead: its default route is on-link, with no gateway to name, and a pin through it would
    /// be a loop. Ours never has a default route at all.
    /// </summary>
    private static List<(uint InterfaceIndex, IPAddress Gateway)> GatewayCandidates()
    {
        var metrics = new Dictionary<uint, uint?>();
        uint? InterfaceMetric(uint index)
        {
            if (!metrics.TryGetValue(index, out var metric))
            {
                var row = IpHelper.ReadInterface(index);
                metrics[index] = metric = row is { Connected: not 0 } ? row.Value.Metric : null;
            }
            return metric;
        }

        return IpHelper.ReadRoutes()
            .Where(r => r.DestinationPrefixLength == 0 && r.DestinationAddress == 0 && r.NextHopAddress != 0)
            .Select(r => (Route: r, InterfaceMetric: InterfaceMetric(r.InterfaceIndex)))
            .Where(x => x.InterfaceMetric is not null)
            .OrderBy(x => (ulong)x.Route.Metric + x.InterfaceMetric!.Value)
            .Select(x => (x.Route.InterfaceIndex, new IPAddress(x.Route.NextHopAddress)))
            .DistinctBy(x => x.InterfaceIndex)
            .ToList();
    }

    /// <summary>
    /// Deletes every route to <paramref name="prefix"/> through that interface, whatever its next hop.
    /// The interface is required, as it was with netsh: the physical adapter can change between
    /// pinning and unpinning, and deleting a prefix off every interface would take routes that are not
    /// ours. A route that is not there is not an error.
    /// </summary>
    private static void DeleteRoute(string prefix, uint interfaceIndex) => DeleteRoutes([prefix], interfaceIndex);

    /// <summary>Deletes routes to these prefixes on one interface, with a single read of the routing table.</summary>
    private static void DeleteRoutes(IEnumerable<string> prefixes, uint interfaceIndex)
    {
        var targets = prefixes
            .Select(IpHelper.ParsePrefix)
            .OfType<IpHelper.Prefix>()
            .Select(prefix => (prefix, interfaceIndex))
            .ToList();
        IpHelper.DeleteRoutes(targets);
    }

    /// <summary>
    /// Adds one route, on-link when <paramref name="nextHop"/> is null, and throws naming it when Windows
    /// refuses. One already there counts as added: every caller has just deleted it, so one still there
    /// is the same route.
    /// </summary>
    private static void AddRoute(string prefix, uint interfaceIndex, IPAddress? nextHop)
    {
        var parsed = IpHelper.ParsePrefix(prefix)
            ?? throw new ArgumentException($"'{prefix}' is not an IPv4 prefix.", nameof(prefix));

        var error = IpHelper.AddRoute(parsed, interfaceIndex, nextHop, Metric);
        if (error is IpHelper.NoError or IpHelper.ErrorObjectAlreadyExists) return;

        throw new InvalidOperationException(
            $"Could not add the route {parsed} on interface {interfaceIndex}" +
            (nextHop is null ? "" : $" via {nextHop}") + $": {IpHelper.Describe(error)}.");
    }

    private static bool IsValidIPv4Cidr(string cidr) => IpHelper.ParsePrefix(cidr) is not null;
}
