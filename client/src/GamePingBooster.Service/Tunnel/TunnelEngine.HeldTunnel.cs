using System.Buffers.Binary;
using System.Diagnostics;
using GamePingBooster.Core.Paths;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Core.Quality;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// A move between matches that keeps the old tunnel open for the TCP connections that rode it.
///
/// Until 2026-10-19 a move closed them (<see cref="CloseWhatRodeIt"/>) and re-addressed the adapter. At 14:35 that day
/// (owner, FPT) the move came 14 s after a match ended, while the lobby was still loading, and cut the game's own Steam
/// CM connection - the only thing of the lobby's on the tunnel. The game closed its lobby connection the same second and
/// asked nothing more: smoke. No moment after a match is reliably safe (the lobby keeps opening connections for 30-40 s,
/// the next match came 21-95 s after the gap), so the move no longer cuts anything:
///
///   - the multi-tunnel dispatcher (<see cref="PathDispatcher"/>) is created on the first such move, with the adapter's
///     address fixed from then on, as region routing does - the new relay's tunnel rewrites to its own address;
///   - every destination of a connection that rode the old tunnel is stuck to it, so those connections finish there,
///     and home moves WITHOUT taking them along (<see cref="PathDispatcher.MoveHome"/>) - what opens from now on, the
///     next match too, goes through the new relay;
///   - the old tunnel stays as another tunnel (<see cref="OtherTunnel.Held"/>) and goes at the first of: nothing on it
///     in use (the other tunnels' rule), its relay silent 15 s (likewise), the next match running a minute on the new
///     relay, or ten minutes (<see cref="HeldTunnelClock"/>). Then what still rides it is closed, as the move used to.
///
/// No second move is made between matches while a tunnel is held: the next match retires it first. Nothing changes
/// for a move with no TCP connection riding, or with no room for another tunnel (<see cref="RegionRouting.MaxTunnels"/>):
/// those close what rode the old relay, as before.
/// </summary>
internal sealed partial class TunnelEngine
{
    /// <summary>True while the dispatcher exists only because a move kept the old tunnel: no region is planned on it.</summary>
    private bool PathsOnlyHold => _paths is { Table.RegionCount: 0 };

    /// <summary>The tunnels kept open by a move, still open.</summary>
    private List<OtherTunnel> HeldTunnels()
    {
        lock (_otherTunnels) return [.. _otherTunnels.Values.Where(o => o.Held is not null)];
    }

    /// <summary>
    /// The TCP connections riding <paramref name="tunnel"/>: with one tunnel, every one from its inner address; with
    /// several, whose shared adapter address cannot tell them apart, those to a destination stuck to it.
    /// </summary>
    private List<TcpConnections.Row> RidingTunnel(TunnelClient tunnel)
    {
        if (_paths is not { } paths) return TcpConnections.From(tunnel.Session.ClientIp);
        var stuck = paths.DestinationsOf(tunnel).ToHashSet();
        if (stuck.Count == 0) return [];
        return [.. TcpConnections.From(ToAddress(paths.AdapterIp)).Where(row => stuck.Contains(HostOrder(row.RemoteAddress)))];
    }

    /// <summary>A MIB_TCPROW address, network order in memory, as the dispatcher's host-order value.</summary>
    private static uint HostOrder(uint mibAddress) => BinaryPrimitives.ReadUInt32BigEndian(BitConverter.GetBytes(mibAddress));

    /// <summary>
    /// Whether a move can keep the old tunnel open for <paramref name="riding"/>: null when it can, otherwise why not,
    /// for the log - empty when nothing rode it, which needs no saying.
    /// </summary>
    private string? WhyNotHold(List<TcpConnections.Row> riding)
    {
        if (riding.Count == 0) return "";
        if (_pump is null) return "the adapter has no reader";
        if (_paths is not null && !PathsOnlyHold) return "region routing has the other tunnels";
        if (_paths?.FaultReason is { } fault) return $"the dispatcher collapsed earlier ({fault})";
        int others;
        lock (_otherTunnels) others = _otherTunnels.Count;
        if (others >= RegionRouting.MaxTunnels - 1) return $"{RegionRouting.MaxTunnels} tunnels are open already";
        return null;
    }

    /// <summary>
    /// The swap's first half when the old tunnel is kept: the dispatcher in place (created on the first such move),
    /// what rode the old tunnel stuck to it, and the old tunnel registered and pinned as another tunnel. Packets keep
    /// leaving by the old tunnel until <see cref="StartTunnel"/> moves home. Say what it did, for the move's log line.
    /// </summary>
    private string HoldForWhatRodeIt(TunnelClient old, RelayEntry previous, RelayEntry target, List<TcpConnections.Row> riding,
        Network.RouteManager routes)
    {
        var paths = _paths;
        if (paths is null)
        {
            paths = new PathDispatcher(old.InnerIp, RegionTable.Build([]), old);
            old.Dispatcher = paths;
            _paths = paths;
            _faultReported = false;
            _pump!.SetDispatcher(paths);
            _log($"The adapter keeps {old.Session.ClientIp} for the rest of this connection, so connections that rode " +
                 $"{previous.Name} can finish there; tunnels to other relays rewrite to their own address.");
        }
        foreach (var remote in riding.Select(row => HostOrder(row.RemoteAddress)).Distinct()) paths.Stick(remote, old);

        var relayId = RelayPaths.RelayIdOf(previous);
        lock (_otherTunnels)
        {
            _otherTunnels[relayId] = new OtherTunnel(relayId, previous, old) { Held = new HeldTunnelClock(NowMs()) };
        }
        // After the new relay's pin replaced the old one's: the old relay must still leave by the physical adapter.
        PinOtherTunnels(routes);

        return $"Kept the tunnel to {previous.Name} open for the {riding.Count} TCP connection(s) that rode it (the lobby, " +
               $"Steam): they finish there, and what opens from now on - the next match too - goes through {target.Name}.";
    }

    /// <summary>
    /// One supervisor pass over the held tunnels: each goes when its clock says so (<see cref="HeldTunnelClock"/>), with
    /// what still rides it closed first, so each program opens its connection again through home at once.
    /// </summary>
    private void RetireHeldTunnels(PathDispatcher paths)
    {
        var home = _tunnel;
        long? homePackets = home?.Destinations.GamePackets;
        var changed = false;
        foreach (var held in HeldTunnels())
        {
            var why = held.Held!.Feed(NowMs(), homePackets);
            if (why is null) continue;
            // A game that plays over TCP may still be playing on the connection the move kept - its quiet read as a gap.
            // Cutting it now would be the break the hold exists to avoid; it goes once that quiet comes back.
            if (SilenceOf(held.Client) < MatchGap.QuietFor) continue;

            // Closed while their packets still leave by the held tunnel, so the resets reach the servers they were for.
            var riding = RidingTunnel(held.Client);
            var (closed, error) = riding.Count == 0 ? (0, 0u) : TcpConnections.Close(riding);
            paths.Remove(held.Client);
            Close(held, announce: true);
            changed = true;

            var heldFor = (NowMs() - held.Held.HeldAtMs) / 1000.0;
            _log($"Closed the tunnel to {held.Way.Name} [{held.Way.Id}] kept since the move, after {heldFor:F0} s - " +
                 (why == "match"
                     ? $"the next match has run {HeldTunnelClock.MatchFor.TotalSeconds:F0} s on {_relay?.Name}"
                     : $"{HeldTunnelClock.AtMost.TotalMinutes:F0} minutes without a match on {_relay?.Name}") +
                 (riding.Count == 0
                     ? ". No TCP connection was left on it."
                     : $". Closed {closed} of {riding.Count} TCP connection(s) still on it, so they are opened again through " +
                       $"{_relay?.Name}" + (error == 0 ? "." : $" (Windows refused the rest: error {error}).")));
        }
        if (changed && _routes is { } routes)
        {
            PinOtherTunnels(routes);
            PinDoors();
        }
    }
}
