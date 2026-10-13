using System.Net;
using System.Net.Sockets;
using GamePingBooster.Core.Profiles;

namespace GamePingBooster.Core.Paths;

/// <summary>
/// When routes may change under a game that plays over TCP (<see cref="GameEntry.TcpGameplay"/>, Aniimo) - roadmap
/// step 3. A TCP connection belongs to the way out it was opened on. Put a route under it and its next segment leaves
/// through the other way with a source address the server has never seen: no reset comes back, the program waits out
/// its own timeouts, and the player is thrown back to the login. UDP shrugs the same change off, which is why nothing
/// before this asked.
///
/// Two rules, both fed the machine's established connections (local address, remote address):
///
///   1. Routes going IN: a connection the game opened on the player's own line before its routes went in - Aniimo's
///      gateway, opened at launch, before the watcher has seen the process - stays on the line. Its address is pinned
///      to the way it is on (<see cref="OnTheLine"/>) and let go once nothing is connected to it any more
///      (<see cref="Released"/>); the game's next connection there takes the tunnel.
///   2. Routes going OUT: a reconnect that would pull the routes from under connections riding the tunnel
///      (<see cref="ThroughTunnel"/>) leaves them in while the same relay is tried again - its address reservation gives
///      the same inner IP back, and those connections carry on. Only when that relay does not come back do the routes
///      go, as for every other game.
///
/// Pure logic: no routing table, no sockets.
/// </summary>
public static class LineHold
{
    /// <summary>One established TCP connection: the address it was opened from, and the one it is connected to.</summary>
    public readonly record struct Flow(IPAddress Local, IPAddress Remote);

    /// <summary>
    /// The remote address of every connection inside <paramref name="prefixes"/> that is NOT riding the tunnel - opened
    /// from any address but <paramref name="tunnelIp"/> - each once. These are what a route into the tunnel would cut.
    /// </summary>
    public static List<IPAddress> OnTheLine(IEnumerable<Flow> live, IEnumerable<string> prefixes, IPAddress? tunnelIp)
    {
        var nets = Parse(prefixes);
        if (nets.Count == 0) return [];
        return [.. live.Where(f => !Rides(f, tunnelIp) && Inside(nets, f.Remote)).Select(f => f.Remote).Distinct()];
    }

    /// <summary>How many connections into <paramref name="prefixes"/> ride the tunnel - opened from <paramref name="tunnelIp"/>.</summary>
    public static int ThroughTunnel(IEnumerable<Flow> live, IEnumerable<string> prefixes, IPAddress? tunnelIp)
    {
        if (tunnelIp is null) return 0;
        var nets = Parse(prefixes);
        return nets.Count == 0 ? 0 : live.Count(f => Rides(f, tunnelIp) && Inside(nets, f.Remote));
    }

    /// <summary>
    /// The addresses of <paramref name="held"/> that no connection on the line goes to any more - free to take the
    /// tunnel. A connection to one through the tunnel does not keep it held: it is already where the routes send it.
    /// </summary>
    public static List<IPAddress> Released(IEnumerable<IPAddress> held, IEnumerable<Flow> live, IPAddress? tunnelIp)
    {
        var still = live.Where(f => !Rides(f, tunnelIp)).Select(f => f.Remote).ToHashSet();
        return [.. held.Where(a => !still.Contains(a))];
    }

    /// <summary>
    /// What a reconnect keeps for <paramref name="game"/>: true when it plays over TCP and at least one of its
    /// connections rides the tunnel now (<paramref name="riding"/>, from <see cref="ThroughTunnel"/>).
    /// </summary>
    public static bool HoldsRoutesOnReconnect(GameEntry? game, int riding) => game is { TcpGameplay: true } && riding > 0;

    private static bool Rides(Flow flow, IPAddress? tunnelIp) => tunnelIp is not null && flow.Local.Equals(tunnelIp);

    private static bool Inside(List<IPNetwork> nets, IPAddress address)
    {
        foreach (var net in nets)
        {
            if (net.Contains(address)) return true;
        }
        return false;
    }

    private static List<IPNetwork> Parse(IEnumerable<string> prefixes) =>
        [.. prefixes.Select(p => IPNetwork.TryParse(p, out var net) ? net : (IPNetwork?)null)
            .OfType<IPNetwork>()
            .Where(n => n.BaseAddress.AddressFamily == AddressFamily.InterNetwork)];
}
