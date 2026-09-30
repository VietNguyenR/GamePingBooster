using System.Globalization;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Paths;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Core.Quality;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Lane hunting: the source port the tunnel sends from, chosen instead of drawn.
///
/// Measured 2026-09-30 (LanePick): from a VN datacentre the ISP spreads UDP to one relay over parallel links by a hash
/// of addresses and ports, so the same way in takes 24, 33 or 41 ms depending only on the port - and through an entry
/// the player's own port decides it, 45 against 62 ms to sg-4 from one Viettel line. Every socket the tunnel ever
/// opened drew a lane at random and kept it for the connection; a move between ways in drew again.
///
/// So once a tunnel has settled on a way in - after a connect, a reconnect, a move between ways or relays - the
/// supervisor tries <see cref="LanePick.Candidates"/> other sockets to the same address beside the tunnel's own, and
/// moves the tunnel onto the socket of a clearly faster lane with <see cref="TunnelClient.MoveToLane"/>: the session,
/// the relay and the address the game server sees do not change, as for a move between ways in. Then again every
/// <see cref="LaneRehuntAfter"/>, in case the ISP has rebalanced.
///
/// Home's tunnel only. Governed like entry switching, whose kind of move it is: the relay's setting in /admin/relays
/// ("on" moves, "record" only writes down what it would have done, "off" does not hunt), or <c>laneHunting</c> in
/// config.json for one machine.
/// </summary>
internal sealed partial class TunnelEngine
{
    /// <summary>After a tunnel settles on a way in, how long before its lanes are tried: past connect's own measuring.</summary>
    private static readonly TimeSpan LaneSettleFor = TimeSpan.FromSeconds(10);

    /// <summary>How often the lanes of a way already hunted are tried again.</summary>
    private static readonly TimeSpan LaneRehuntAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// After a hunt that saw faster lanes but could not trust any - the line jittered while it was measured - the next
    /// try, at most <see cref="LaneUnsettledRetries"/> times per way. On VNPT on 2026-09-30 one hunt in seven saw 40 ms
    /// lanes beside the 63 ms one in use with a 131 ms burst across all of them; waiting half an hour kept the player on 63.
    /// </summary>
    private static readonly TimeSpan LaneRetryAfter = TimeSpan.FromMinutes(1);
    private const int LaneUnsettledRetries = 3;

    // Supervisor only: the tunnel and way in the lanes were last looked at for, since when, and when last hunted.
    private TunnelClient? _laneTunnel;
    private System.Net.IPEndPoint? _laneEndpoint;
    private long _laneSinceTick;
    private long _laneHuntedTick;
    private int _laneRetries;
    private bool _laneModeAnnounced;

    private void ResetLaneHunting()
    {
        _laneTunnel = null;
        _laneEndpoint = null;
        _laneSinceTick = 0;
        _laneHuntedTick = 0;
        _laneRetries = 0;
        _laneModeAnnounced = false;
    }

    /// <summary>This connection's lane-hunting mode, and where it came from. See <see cref="ServiceConfig.LaneHunting"/>.</summary>
    private (EntrySwitchingMode Mode, string Source) LaneMode() =>
        !string.IsNullOrWhiteSpace(_config.LaneHunting)
            ? (EntrySwitching.Parse(_config.LaneHunting) ?? EntrySwitchingMode.Record, "config.json")
            : (_switching, "the relay's entry switching");

    /// <summary>Supervisor only, beside every other move: a lane hunt on home's tunnel when one is due.</summary>
    private async Task HuntLaneIfDueAsync(TunnelClient tunnel, CancellationToken ct)
    {
        var (mode, source) = LaneMode();
        if (mode == EntrySwitchingMode.Off) return;
        if (_state != TunnelState.Connected || !ReferenceEquals(tunnel, _tunnel) || _relay is not { } relay) return;

        var now = Environment.TickCount64;
        if (!ReferenceEquals(tunnel, _laneTunnel) || !Equals(tunnel.Endpoint, _laneEndpoint))
        {
            _laneTunnel = tunnel;
            _laneEndpoint = tunnel.Endpoint;
            _laneSinceTick = now;
            _laneHuntedTick = 0;
            _laneRetries = 0;
            return;
        }
        if (now - _laneSinceTick < LaneSettleFor.TotalMilliseconds) return;
        if (_laneHuntedTick != 0 && now - _laneHuntedTick < LaneRehuntAfter.TotalMilliseconds) return;
        // A way that is not answering is the silence rules' to deal with, not a moment to measure anything.
        if (tunnel.SinceLastHeard > TimeSpan.FromSeconds(3)) return;
        _laneHuntedTick = now;

        if (!_laneModeAnnounced)
        {
            _laneModeAnnounced = true;
            _log($"Lane hunting ({(mode == EntrySwitchingMode.On ? "on" : "record only")}, from {source}): other source ports " +
                 $"into the way in use are tried {LaneSettleFor.TotalSeconds:F0} s after it settles and every " +
                 $"{LaneRehuntAfter.TotalMinutes:F0} minutes.");
        }

        // The entry-switching probes share relayd's 20 a second with the hunt: four a second for each other way in.
        var otherDoors = _profile is { } profile ? Math.Max(0, RelayPaths.DoorsOf(profile.Relays, RelayPaths.RelayIdOf(relay)).Count - 1) : 0;
        var rate = LanePick.ProbesPerSecond(otherDoors);

        LaneHunt? hunt;
        try
        {
            hunt = await tunnel.HuntLanesAsync(LanePick.Candidates, LanePick.Rounds, rate, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"Lane hunting: the hunt on {relay.Name} [{relay.Id}] failed ({ex.Message}) - staying on this lane.");
            return;
        }
        if (hunt is null) return;

        using (hunt)
        {
            var pick = LanePick.Choose(hunt.Current, hunt.Candidates);
            var answered = hunt.Candidates.Count(c => c.MedianMs is not null);
            var lanes = hunt.Candidates.Where(c => c.MedianMs is not null && c.Loss.Lost == 0)
                .Select(c => Math.Round(c.MedianMs!.Value))
                .Append(hunt.Current.MedianMs is { } cur ? Math.Round(cur) : double.NaN)
                .Where(v => !double.IsNaN(v))
                .Distinct()
                .Order()
                .ToList();
            _log($"Lane hunting on {relay.Name} [{relay.Id}]: the lane in use {hunt.Current}, {answered} of {hunt.Candidates.Count} " +
                 $"other ports answered in {hunt.Seconds:F1} s at {hunt.ProbesPerSecond}/s, round trips seen " +
                 $"{string.Join("/", lanes.Select(v => v.ToString("F0", CultureInfo.InvariantCulture)))} ms. {pick.Reason}.");

            if (pick.Unsettled && _laneRetries < LaneUnsettledRetries)
            {
                // Next pass that finds it due: LaneRetryAfter from now rather than LaneRehuntAfter.
                _laneRetries++;
                _laneHuntedTick = Environment.TickCount64 - (long)(LaneRehuntAfter - LaneRetryAfter).TotalMilliseconds;
                _log($"Lane hunting: trying again in {LaneRetryAfter.TotalMinutes:F0} min ({_laneRetries} of {LaneUnsettledRetries}).");
            }

            if (pick.Slot is not { } slot) return;
            var best = hunt.Candidates[slot - 1];
            var moves = mode == EntrySwitchingMode.On;
            var moved = false;
            DoorStats? after = null;

            if (moves && _state == TunnelState.Connected && ReferenceEquals(tunnel, _tunnel) && hunt.Take(slot) is { } socket)
            {
                try
                {
                    tunnel.MoveToLane(socket);
                    moved = true;
                    // The in-game reading was taken on the old lane.
                    ForgetDirectPing();
                }
                catch (Exception ex)
                {
                    socket.Dispose();
                    _log($"Lane hunting: the move failed ({ex.Message}) - staying on this lane.");
                }

                if (moved)
                {
                    after = await ConfirmLaneAsync(tunnel, relay, rate, ct).ConfigureAwait(false);
                }
            }
            else if (!moves)
            {
                _log("Lane hunting: record only - the tunnel stays on its lane.");
            }

            if (_recorder is { } recorder) recorder.WriteLaneMove(new LaneMoveRecord(
                    DateTimeOffset.UtcNow, relay.Id, moves, moved, pick.Reason, hunt.Seconds, hunt.ProbesPerSecond,
                    Stats(hunt.Current), Stats(best), after,
                    [hunt.Current.MedianMs, .. hunt.Candidates.Select(c => c.MedianMs)]),
                recorder.CurrentMeta(SpikeContext(home: true)));
        }
    }

    /// <summary>
    /// The lane just taken, measured again on the tunnel's own socket. A lane that answered every Probe a moment ago and
    /// answers none now is not waited on for the supervisor's fifteen seconds of silence and a reconnect, which would
    /// drop the match: the tunnel goes to a fresh socket - a lane drawn at random, as before hunting existed.
    /// </summary>
    private async Task<DoorStats?> ConfirmLaneAsync(TunnelClient tunnel, RelayEntry relay, int rate, CancellationToken ct)
    {
        try
        {
            using var check = await tunnel.HuntLanesAsync(0, LanePick.Rounds, rate, ct).ConfigureAwait(false);
            if (check is null) return null;
            if (check.Current.MedianMs is not null)
            {
                _log($"Lane hunting: moved to the faster lane on {relay.Name} [{relay.Id}] - {check.Current} on it now.");
                return Stats(check.Current);
            }

            _log($"Lane hunting: the new lane into {relay.Name} [{relay.Id}] answered nothing after the move - leaving it " +
                 "for a fresh port.");
            tunnel.MoveTo(tunnel.Endpoint);
            return Stats(check.Current);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"Lane hunting: could not measure the new lane ({ex.Message}).");
            return null;
        }
    }

    private static DoorStats Stats(LaneSample lane) => new(lane.MedianMs, lane.WorstMs, lane.Loss.Sent, lane.Loss.Lost);
}
