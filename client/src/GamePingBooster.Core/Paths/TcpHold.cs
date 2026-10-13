using System.Net;
using System.Net.Sockets;
using GamePingBooster.Core.Profiles;

namespace GamePingBooster.Core.Paths;

/// <summary>
/// When routes may change under a game that plays over TCP (<see cref="GameEntry.TcpGameplay"/>, Aniimo) - roadmap
/// step 3. A TCP connection riding the tunnel lives only as long as the routes and the inner IP under it: pull the
/// routes and its next segment leaves through the line with a source address the server has never seen, no reset
/// comes back, the program waits out its own timeouts, and the player is thrown back to the login. UDP shrugs the same
/// change off, which is why nothing before this asked.
///
/// The rule: a reconnect that would pull the routes from under connections riding the tunnel
/// (<see cref="ThroughTunnel"/>) leaves them in while the same relay is tried again - its address reservation gives
/// the same inner IP back, and those connections carry on. Only when that relay does not come back do the routes go,
/// as for every other game.
///
/// Routes going IN under a connection already open on the player's own line need nothing: Windows sends by the strong
/// host model, so a connection keeps the interface that owns its source address whatever route goes in later
/// (`TunnelCheck service tcp`, 2026-10-13). A hold for that case was built and taken out again as code that never ran;
/// the service logs and reports the host model (HostModel) so a machine where it would matter shows up in the data.
///
/// Pure logic: no routing table, no sockets.
/// </summary>
public static class TcpHold
{
    /// <summary>One established TCP connection: the address it was opened from, and the one it is connected to.</summary>
    public readonly record struct Flow(IPAddress Local, IPAddress Remote);

    /// <summary>How many connections into <paramref name="prefixes"/> ride the tunnel - opened from <paramref name="tunnelIp"/>.</summary>
    public static int ThroughTunnel(IEnumerable<Flow> live, IEnumerable<string> prefixes, IPAddress? tunnelIp)
    {
        if (tunnelIp is null) return 0;
        var nets = Parse(prefixes);
        return nets.Count == 0 ? 0 : live.Count(f => f.Local.Equals(tunnelIp) && Inside(nets, f.Remote));
    }

    /// <summary>
    /// What a reconnect keeps for <paramref name="game"/>: true when it plays over TCP and at least one of its
    /// connections rides the tunnel now (<paramref name="riding"/>, from <see cref="ThroughTunnel"/>).
    /// </summary>
    public static bool HoldsRoutesOnReconnect(GameEntry? game, int riding) => game is { TcpGameplay: true } && riding > 0;

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
