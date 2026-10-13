using System.Buffers.Binary;
using System.Net;
using GamePingBooster.Core.Paths;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Route timing for a game that plays over TCP (GameEntry.tcpGameplay, Aniimo) - roadmap step 3, rules in
/// <see cref="LineHold"/>: no route goes in under a connection the game has open on the player's own line, and a
/// reconnect does not pull the routes from under the ones riding the tunnel while the same relay may still come back.
/// Every other game is untouched: these all start from "the game plays over TCP".
/// </summary>
internal sealed partial class TunnelEngine
{
    /// <summary>
    /// How long a relay may stay silent before the tunnel is given up for dead while a TCP game's connections ride it.
    /// Windows abandons a connection whose segments go unanswered after five retransmissions from a 300 ms timeout -
    /// about 19 seconds - so <see cref="SilenceBeforeDead"/>'s fifteen plus a supervisor pass reconnected after those
    /// connections were already gone. Sooner costs little here: the reconnect tries the same relay with the routes left
    /// in (<see cref="ReconnectAsync"/>), and when it answers nothing has changed on this machine at all.
    /// </summary>
    private static readonly TimeSpan SilenceBeforeDeadHeldTcp = TimeSpan.FromSeconds(6);

    /// <summary>The lobby host routes of every game that plays over TCP, as InstallLobbyRoutes last installed them.</summary>
    private volatile IReadOnlyList<string> _tcpLobbyRoutes = [];

    /// <summary>The virtual adapter's address - the local address of every connection riding the tunnel.</summary>
    private IPAddress? AdapterIp()
    {
        if (_paths is not { } paths) return _tunnel?.Session.ClientIp;
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, paths.AdapterIp);
        return new IPAddress(bytes);
    }

    /// <summary>Where a TCP game's connections go: the running game's ranges when it plays over TCP, and TCP games' lobbies.</summary>
    private List<string> TcpGamePrefixes()
    {
        var prefixes = new List<string>(_tcpLobbyRoutes);
        if (_game is { TcpGameplay: true } game) prefixes.AddRange(game.Regions.SelectMany(r => r.Cidrs));
        return prefixes;
    }

    /// <summary>
    /// How many of the running TCP game's connections ride the tunnel now; 0 for any other game, and with several
    /// tunnels, whose shared adapter address cannot tell home's connections from the others'.
    /// </summary>
    private int TcpRidingTunnel()
    {
        if (_game is not { TcpGameplay: true } || _paths is not null || AdapterIp() is not { } inner) return 0;
        return LineHold.ThroughTunnel(TcpConnections.LiveFlowsOfOthers(), TcpGamePrefixes(), inner);
    }

    /// <summary>True when the relay has been silent long enough to reconnect early for a TCP game (<see cref="SilenceBeforeDeadHeldTcp"/>).</summary>
    private bool ReconnectSoonerForTcp(TimeSpan silence) => silence >= SilenceBeforeDeadHeldTcp && TcpRidingTunnel() > 0;

    /// <summary>
    /// Before routes into <paramref name="prefixes"/> go in: keeps every connection open to them on the player's own
    /// line there (RouteManager.HoldOnLine). A failure is logged and the routes go in regardless - what the player had
    /// before this existed.
    /// </summary>
    private void HoldLineConnections(IReadOnlyCollection<string> prefixes, string what)
    {
        if (_routes is not { } routes || _adapter is not { } adapter || prefixes.Count == 0) return;
        try
        {
            var open = LineHold.OnTheLine(TcpConnections.LiveFlowsOfOthers(), prefixes, AdapterIp());
            if (open.Count == 0) return;
            var held = routes.HoldOnLine(adapter.InterfaceIndex, open);
            if (held.Count == 0) return;
            _log($"{what} had {held.Count} TCP connection address(es) open on your own line before its routes went in " +
                 $"({string.Join(", ", held)}) - kept there, because a route under an open TCP connection ends it. Each " +
                 "takes the tunnel once nothing is connected to it any more.");
        }
        catch (Exception ex)
        {
            _log($"Could not keep {what}'s open TCP connections on your own line ({ex.Message}) - its routes go in anyway.");
        }
    }

    /// <summary>
    /// Supervisor pass: lets go of every address held on the line that nothing is connected to any more, and puts back
    /// the game and lobby routes skipped for them, so the game's next connection there takes the tunnel.
    /// </summary>
    private void ReleaseLineHolds()
    {
        if (_routes is not { } routes || _adapter is not { } adapter) return;
        var held = routes.LineHeld;
        if (held.Count == 0) return;
        try
        {
            var free = LineHold.Released(held, TcpConnections.LiveFlowsOfOthers(), AdapterIp());
            if (free.Count == 0) return;
            routes.ReleaseLine(free);
            _log($"Nothing is connected to {string.Join(", ", free)} on your own line any more - the next connection " +
                 "there takes the tunnel.");
            if (Volatile.Read(ref _routesInAtMs) != 0 && _game is { } game)
            {
                routes.InstallGameRoutes(adapter.InterfaceIndex, game.Regions.SelectMany(r => r.Cidrs).Distinct());
            }
            InstallLobbyRoutes();
        }
        catch (Exception ex)
        {
            _log($"Could not hand the addresses kept on your own line back to the tunnel ({ex.Message}).");
        }
    }
}
