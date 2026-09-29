using System.Diagnostics;
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
    /// only, like every move. <paramref name="why"/> goes in the log.
    /// </summary>
    private bool MoveOtherToDoor(string relayId, string doorId, bool rollback, string why)
    {
        var routes = _routes;
        var profile = _profile;
        OtherTunnel? other;
        lock (_otherTunnels) _otherTunnels.TryGetValue(relayId, out other);
        if (_state != TunnelState.Connected || routes is null || profile is null || other is null) return false;

        var target = RelayPaths.DoorsOf(profile.Relays, relayId)
            .FirstOrDefault(d => d.Id.Equals(doorId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            _log($"Entry switching: {doorId} is no longer a way into {other.Way.Name} - that tunnel stays where it is.");
            return false;
        }
        if (target.Id.Equals(other.Way.Id, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            // The way moved to is pinned already, as one of this relay's ways in (PinDoors) - a move is only ever asked
            // for between ways whose probes were answered, and those are pinned. The tunnel's own pin follows it below.
            var endpoint = ParseEndpoint(target.Endpoint);
            other.Client.MoveTo(endpoint);
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
                 "the same relay and session, so the game server sees no change, and home is not touched.");
            StatusChanged?.Invoke(Snapshot());
            return true;
        }
        catch (Exception ex)
        {
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
    /// <see cref="WayCheck.Rounds"/> rounds of Probes, each way from a socket of its own, and what
    /// <see cref="WayCheck.Choose"/> makes of them. Null when that relay's ways are not pinned (entry switching off, or a
    /// relay with one way in). Never throws; the numbers are for the region plan's pass, which is running when this is.
    /// </summary>
    private async Task<(IReadOnlyList<WaySample> Ways, WayChoice Choice)?> CheckWaysAsync(OtherTunnel other, CancellationToken ct)
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
            var samples = await ProbeWaysAsync(other.Client.SessionId, ways, WayCheck.Rounds, WayCheck.SpacingFor(ways.Count), ct).ConfigureAwait(false);
            return (samples, WayCheck.Choose(other.Way.Id, samples, other.LeftDoor));
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
        int spacingMs, CancellationToken ct)
    {
        var roundStarts = new long[rounds + 1];
        var best = new double?[ways.Count, rounds];
        var gate = new object();
        var started = 0;

        using var probes = new DoorProbes(sessionId, ways, (_, slot, sentAt, receivedAt) =>
        {
            lock (gate)
            {
                // Filed under the round it left in, the way the recorder files a probe under its quarter second.
                var round = -1;
                for (var r = started - 1; r >= 0; r--)
                {
                    if (sentAt >= roundStarts[r]) { round = r; break; }
                }
                if (round < 0 || (uint)slot >= (uint)ways.Count) return;
                var rtt = (receivedAt - sentAt) * 1000.0 / Stopwatch.Frequency;
                best[slot, round] = best[slot, round] is { } was ? Math.Min(was, rtt) : rtt;
            }
        });

        for (var r = 0; r < rounds; r++)
        {
            if (r > 0) await Task.Delay(spacingMs, ct).ConfigureAwait(false);
            lock (gate)
            {
                roundStarts[r] = Stopwatch.GetTimestamp();
                started = r + 1;
            }
            for (var slot = 0; slot < ways.Count; slot++) probes.Send(slot);
        }
        await Task.Delay(RelayLoss.BurstWaitMs, ct).ConfigureAwait(false);

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
        return samples;
    }
}
