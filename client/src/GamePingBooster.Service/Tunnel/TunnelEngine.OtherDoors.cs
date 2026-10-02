using System.Diagnostics;
using System.Net.Sockets;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Paths;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Core.Quality;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Entry switching for the tunnels to other relays (multi-tunnel) - the same moves home's tunnel has made since
/// 2026-09-18, on the tunnel a region's matches go through.
///
/// The relay a region's tunnel is on never changes under a match: the game server sees that relay's address, and
/// another relay would drop the match. What changes is the way INTO it - the relay's direct road, or an entry in front
/// of it - exactly as for home: <see cref="TunnelClient.MoveTo"/> keeps the session, the inner address and the relay,
/// so the game server sees nothing, and the region's packets keep their tunnel in the dispatcher.
///
/// Two moments, as for home:
///
///   - during a match on the tunnel, the recorder follows it (5.8) and its switch policy for that relay judges the ways
///     quarter second by quarter second - the policy, its margins and its way back are home's, per relay;
///   - between matches, where home is judged in the lobby, a region's tunnel is idle and the recorder is on home. The
///     region plan that runs after every match (<see cref="PlanRegionsAsync"/>) measures each open tunnel's ways with
///     rounds of Probes and moves it before the next match when another way is clearly better (<see cref="WayCheck"/>).
///
/// Before 2026-09-30 a region's tunnel stayed on the way it was opened on for the whole connection: a customer's
/// Singapore tunnel sat at a high ping all evening while its entry was the better road.
/// </summary>
internal sealed partial class TunnelEngine
{
    /// <summary>
    /// Moves the tunnel to <paramref name="relayId"/> - another relay's, not home's - onto another way into it. Supervisor
    /// only, like every move. <paramref name="why"/> goes in the log. <paramref name="measured"/> is the socket the way was
    /// measured on, when the caller holds it (WayCheck); it is this method's from the call on, moved onto or closed.
    /// </summary>
    private bool MoveOtherToDoor(string relayId, string doorId, bool rollback, string why, Socket? measured = null) =>
        MoveOtherToDoor(relayId, doorId, rollback, why, measured, out _);

    /// <summary><see cref="MoveOtherToDoor(string, string, bool, string, Socket?)"/>, saying whether it took the measured socket.</summary>
    private bool MoveOtherToDoor(string relayId, string doorId, bool rollback, string why, Socket? measured, out bool onMeasured)
    {
        onMeasured = false;
        var routes = _routes;
        var profile = _profile;
        OtherTunnel? other;
        lock (_otherTunnels) _otherTunnels.TryGetValue(relayId, out other);
        if (_state != TunnelState.Connected || routes is null || profile is null || other is null)
        {
            measured?.Dispose();
            return false;
        }

        var target = RelayPaths.DoorsOf(profile.Relays, relayId)
            .FirstOrDefault(d => d.Id.Equals(doorId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            measured?.Dispose();
            _log($"Entry switching: {doorId} is no longer a way into {other.Way.Name} - that tunnel stays where it is.");
            return false;
        }
        if (target.Id.Equals(other.Way.Id, StringComparison.OrdinalIgnoreCase))
        {
            measured?.Dispose();
            return false;
        }

        try
        {
            // The way moved to is pinned already, as one of this relay's ways in (PinDoors) - a move is only ever asked
            // for between ways whose probes were answered, and those are pinned. The tunnel's own pin follows it below.
            var endpoint = ParseEndpoint(target.Endpoint);
            var socket = measured;
            measured = null;
            onMeasured = MoveOntoDoor(other.Client, target.Id, endpoint, socket);
            var moved = other with { Way = target };
            lock (_otherTunnels)
            {
                if (_otherTunnels.TryGetValue(relayId, out var now) && ReferenceEquals(now, other)) _otherTunnels[relayId] = moved;
            }
            PinOtherTunnels(routes);

            // The in-game reading, when this tunnel carries the match, was taken down the old way.
            if (ReferenceEquals(_carrier.Current, other.Client)) ForgetDirectPing();

            if (rollback) _movedFrom.Remove(relayId);
            else _movedFrom[relayId] = (other.Way.Id, Environment.TickCount64);

            _log($"Entry switching ({why}): moved the tunnel to {other.Way.Name} [{other.Way.Id}] onto {target.Name} [{target.Id}] - " +
                 $"the same relay and session, so the game server sees no change, and home is not touched; {LaneNote(onMeasured)}.");
            StatusChanged?.Invoke(Snapshot());
            return true;
        }
        catch (Exception ex)
        {
            measured?.Dispose();
            _log($"Entry switching: could not move the tunnel to {other.Way.Name} [{other.Way.Id}] onto {target.Name} " +
                 $"({ex.Message}) - it stays where it is.");
            return false;
        }
    }

    /// <summary>
    /// <see cref="MovedBackAfterSilence"/> for the other tunnels: one moved in the last thirty seconds whose new way has
    /// gone silent for three goes back where it came from - before <see cref="SuperviseOtherTunnels"/> gives it up after
    /// fifteen and sends its regions home, which would drop a match on it.
    /// </summary>
    private void MoveOtherTunnelsBackAfterSilence()
    {
        var home = _relay is { } r ? RelayPaths.RelayIdOf(r) : null;
        foreach (var (relayId, moved) in _movedFrom.ToList())
        {
            if (relayId.Equals(home, StringComparison.OrdinalIgnoreCase)) continue;
            if (OtherTunnelTo(relayId) is not { } client || Environment.TickCount64 - moved.AtTick > MoveBackWithin.TotalMilliseconds)
            {
                _movedFrom.Remove(relayId);
                continue;
            }
            var silence = client.SinceLastHeard;
            if (silence < MoveBackSilence) continue;

            _movedFrom.Remove(relayId);
            _log($"Entry switching: the tunnel to {relayId} has heard nothing for {silence.TotalSeconds:F0} s since its move - " +
                 $"going back to {moved.From} rather than waiting for it to be given up for dead.");
            MoveOtherToDoor(relayId, moved.From, rollback: true, why: "the new way went silent");
        }
    }

    /// <summary>
    /// Every way into the relay of an open tunnel to another relay - the one in use too - measured by
    /// <see cref="WayCheck.Rounds"/> rounds of Probes, the way in use on the tunnel's own socket and each other way from a
    /// socket of its own, and what
    /// <see cref="WayCheck.Choose"/> makes of them. Null when that relay's ways are not pinned (entry switching off, or a
    /// relay with one way in). Never throws; the numbers are for the region plan's pass, which is running when this is.
    ///
    /// When the choice is a move, <c>Socket</c> is the socket the way moved to was measured on, for the move to take - the
    /// lane the choice was made on (see DoorProbes). The caller's to move onto or close; null when it could not be kept.
    /// </summary>
    private async Task<(IReadOnlyList<WaySample> Ways, WayChoice Choice, Socket? Socket)?> CheckWaysAsync(OtherTunnel other, CancellationToken ct)
    {
        var profile = _profile;
        if (profile is null || !_doorsPinnedRelays.Contains(other.RelayId) || other.Client.SessionId == 0) return null;

        var ways = new List<DoorProbes.Door>();
        foreach (var way in RelayPaths.DoorsOf(profile.Relays, other.RelayId))
        {
            if (System.Net.IPEndPoint.TryParse(way.Endpoint, out var endpoint) &&
                endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                ways.Add(new DoorProbes.Door(way.Id, endpoint));
            }
        }
        if (ways.Count < 2) return null;

        try
        {
            WayChoice? choice = null;
            var (samples, socket) = await ProbeWaysKeepingAsync(other.Client.SessionId, ways, WayCheck.Rounds,
                WayCheck.SpacingFor(ways.Count), measured => (choice = WayCheck.Choose(other.Way.Id, measured, other.LeftDoor)).MoveTo,
                ct, own: other.Client, ownWay: other.Way.Id).ConfigureAwait(false);
            return (samples, choice!, socket);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"  Could not measure the ways into {other.Way.Name} ({ex.Message}).");
            return null;
        }
    }

    /// <summary>
    /// <paramref name="rounds"/> Probes down each of <paramref name="ways"/>, all ways at once, <paramref name="spacingMs"/>
    /// apart, answered to sockets of their own: per way, the median of the answers and how many came back. A Probe is
    /// answered without moving the session (see DoorProbes), so the way the tunnel is on can be measured too, the same way.
    /// </summary>
    internal static async Task<List<WaySample>> ProbeWaysAsync(ulong sessionId, IReadOnlyList<DoorProbes.Door> ways, int rounds,
        int spacingMs, CancellationToken ct) =>
        (await ProbeWaysKeepingAsync(sessionId, ways, rounds, spacingMs, _ => null, ct).ConfigureAwait(false)).Samples;

    /// <summary>
    /// <see cref="ProbeWaysAsync"/>, then <paramref name="keep"/> names the way, if any, whose socket is handed back open
    /// instead of closed with the rest - the way a tunnel is about to move to, which then rides the lane that was measured.
    ///
    /// With <paramref name="own"/>, the way <paramref name="ownWay"/> - the one that tunnel is on - is timed on the
    /// tunnel's own socket instead of a socket of its own: a socket's port picks the ISP's link (LanePick), so a socket of
    /// its own measured some lane of that way, not the one the tunnel rides, and the choice compared the other ways with a
    /// lane drawn at random. Falls back to a socket of its own when the tunnel's answers are taken (a lane hunt) or it is
    /// not on that way's address.
    /// </summary>
    internal static async Task<(List<WaySample> Samples, Socket? Kept)> ProbeWaysKeepingAsync(ulong sessionId,
        IReadOnlyList<DoorProbes.Door> ways, int rounds, int spacingMs, Func<IReadOnlyList<WaySample>, string?> keep,
        CancellationToken ct, TunnelClient? own = null, string? ownWay = null)
    {
        var roundStarts = new long[rounds + 1];
        var best = new double?[ways.Count, rounds];
        var gate = new object();
        var started = 0;

        // The way in use, when it is timed on the tunnel's socket: its index in ways, and the queue its answers come in.
        var ownIndex = own is null || ownWay is null
            ? -1
            : ways.ToList().FindIndex(w => w.Id.Equals(ownWay, StringComparison.OrdinalIgnoreCase) && w.Endpoint.Equals(own.Endpoint));
        var ownReplies = ownIndex >= 0 && own!.SessionId == sessionId ? own.ClaimProbeReplies() : null;
        if (ownReplies is null) ownIndex = -1;
        var ownStamps = new HashSet<long>();

        // Every other way on a socket of its own; DoorProbes' slots map back to ways by this.
        var probed = Enumerable.Range(0, ways.Count).Where(i => i != ownIndex).ToArray();

        void File(int index, long sentAt, long receivedAt)
        {
            lock (gate)
            {
                // Filed under the round it left in, the way the recorder files a probe under its quarter second.
                var round = -1;
                for (var r = started - 1; r >= 0; r--)
                {
                    if (sentAt >= roundStarts[r]) { round = r; break; }
                }
                if (round < 0 || (uint)index >= (uint)ways.Count) return;
                var rtt = (receivedAt - sentAt) * 1000.0 / Stopwatch.Frequency;
                best[index, round] = best[index, round] is { } was ? Math.Min(was, rtt) : rtt;
            }
        }

        void DrainOwn()
        {
            if (ownReplies is null) return;
            while (ownReplies.TryDequeue(out var reply))
            {
                bool ours;
                lock (gate) ours = ownStamps.Remove(reply.Stamp);
                if (ours) File(ownIndex, reply.Stamp, reply.At);
            }
        }

        try
        {
            using var probes = new DoorProbes(sessionId, probed.Select(i => ways[i]).ToList(),
                (_, slot, sentAt, receivedAt) =>
                {
                    if ((uint)slot < (uint)probed.Length) File(probed[slot], sentAt, receivedAt);
                });

            for (var r = 0; r < rounds; r++)
            {
                if (r > 0) await Task.Delay(spacingMs, ct).ConfigureAwait(false);
                DrainOwn();
                long stamp;
                lock (gate)
                {
                    stamp = Stopwatch.GetTimestamp();
                    roundStarts[r] = stamp;
                    started = r + 1;
                    if (ownIndex >= 0) ownStamps.Add(stamp);
                }
                if (ownIndex >= 0 && !own!.SendOwnProbe(stamp))
                {
                    lock (gate) ownStamps.Remove(stamp);
                }
                for (var slot = 0; slot < probed.Length; slot++) probes.Send(slot);
            }
            await Task.Delay(RelayLoss.BurstWaitMs, ct).ConfigureAwait(false);
            DrainOwn();

            var samples = new List<WaySample>(ways.Count);
            lock (gate)
            {
                for (var slot = 0; slot < ways.Count; slot++)
                {
                    var answered = new List<double>();
                    for (var r = 0; r < rounds; r++)
                    {
                        if (best[slot, r] is { } ms) answered.Add(ms);
                    }
                    samples.Add(new WaySample(ways[slot].Id,
                        DoorSwitchPolicy.Stats(answered, rounds, rounds - answered.Count).P50,
                        new PingLoss(rounds, answered.Count)));
                }
            }

            var kept = keep(samples) is { } id && ways.FirstOrDefault(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } way
                ? probes.Take(way.Id, way.Endpoint, sessionId)
                : null;
            return (samples, kept);
        }
        finally
        {
            if (ownReplies is not null) own!.ReleaseProbeReplies(ownReplies);
        }
    }
}
