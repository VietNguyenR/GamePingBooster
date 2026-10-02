using System.Net;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Service.Dns;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Host routes for names the unblock resolver sends through the tunnel - see <see cref="IUnblockRoutes"/>.
///
/// Like the lobby's routes, and kept beside them: on from the moment they are asked for (the resolver asks before it
/// answers, so before the program connects), off with the tunnel, and put back on a reconnect - a program that
/// resolved the name before the tunnel dropped still holds the address, and without its route it would go straight
/// to the line that resets it. Judged by the lobby's rules (LobbyRoutes): a /32, never a relay's address, never a
/// landmark, never a private one.
/// </summary>
internal sealed partial class TunnelEngine : IUnblockRoutes
{
    /// <summary>Every host route asked for during this connection, to put back after a reconnect.</summary>
    private readonly HashSet<string> _unblockWanted = [];

    public bool Ready =>
        _state == TunnelState.Connected && _adapter is not null && _routes is not null && _tunnel is not null;

    public bool Route(string name, IReadOnlyList<IPAddress> addresses)
    {
        var adapter = _adapter;
        var routes = _routes;
        var profile = _profile;
        if (!Ready || adapter is null || routes is null || profile is null) return false;

        var rejected = new List<LobbyRoutes.Rejection>();
        var hostRoutes = LobbyRoutes.ToHostRoutes(
            addresses.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(a => a.ToString()),
            RelayEndpointsToKeepOff(profile),
            profile.Games.SelectMany(g => g.Regions).SelectMany(r => r.Landmarks),
            rejected);
        foreach (var refusal in rejected)
        {
            _log($"Unblock: {refusal.Entry} for {name} is not routed through the tunnel - {refusal.Reason}.");
        }
        if (hostRoutes.Count == 0) return false;

        lock (_unblockWanted) _unblockWanted.UnionWith(hostRoutes);

        try
        {
            var added = routes.InstallUnblockRoutes(adapter.InterfaceIndex, hostRoutes);
            if (added > 0)
            {
                _log($"Unblock: {name} goes through the tunnel - routed {string.Join(", ", hostRoutes)}.");
            }
            return true;
        }
        catch (Exception ex)
        {
            _log($"Unblock: could not route {name} through the tunnel, so it stays on the normal path: {ex.Message}");
            return false;
        }
    }

    /// <summary>The relays and every entry in front of them, as InstallLobbyRoutes keeps them off the tunnel.</summary>
    private List<string> RelayEndpointsToKeepOff(ProfileBundle profile)
    {
        var relays = profile.Relays.Select(r => r.Endpoint)
            .Concat(RelayPaths.Expand(profile.Relays).Select(p => p.Endpoint))
            .ToList();
        if (_relay is not null) relays.Add(_relay.Endpoint);
        return relays;
    }

    /// <summary>After a reconnect, with the lobby's: every route this connection had asked for.</summary>
    private void ReinstallUnblockRoutes()
    {
        var adapter = _adapter;
        var routes = _routes;
        if (adapter is null || routes is null) return;

        List<string> wanted;
        lock (_unblockWanted) wanted = [.. _unblockWanted];
        if (wanted.Count == 0) return;

        try
        {
            var added = routes.InstallUnblockRoutes(adapter.InterfaceIndex, wanted);
            if (added > 0) _log($"Unblock: put back {added} route(s) through the tunnel after the reconnect.");
        }
        catch (Exception ex)
        {
            _log($"Unblock: could not put the tunnel routes back: {ex.Message}");
        }
    }

    /// <summary>On a disconnect: the next connection starts with none, and asks again as names are resolved.</summary>
    private void ForgetUnblockRoutes()
    {
        lock (_unblockWanted) _unblockWanted.Clear();
    }
}
