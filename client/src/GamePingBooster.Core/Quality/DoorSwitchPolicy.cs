namespace GamePingBooster.Core.Quality;

/// <summary>A round trip to relayd down one way in, over a window: median, 95th percentile, and how many went unanswered.</summary>
public sealed record DoorStats(double? P50, double? P95, int Sent, int Lost)
{
    public double? LossPct => Sent == 0 ? null : 100.0 * Lost / Sent;
}

/// <summary>
/// The tunnel's current way into its relay was worse than another way into the same relay for most of
/// the last thirty seconds. Whether anything is done about it is the recorder's and the engine's call.
/// </summary>
public sealed class DoorDecision
{
    public required long TickIndex { get; init; }
    public required DateTimeOffset AtUtc { get; init; }

    /// <summary>The way in use: a relay id, or an entry id.</summary>
    public required string From { get; init; }

    /// <summary>The way that was better.</summary>
    public required string To { get; init; }

    public required DoorStats FromStats { get; init; }
    public required DoorStats ToStats { get; init; }

    /// <summary>Of the quarter seconds both ways were measured, the share in which the current one was worse.</summary>
    public required double WorseShare { get; init; }

    /// <summary>How many quarter seconds both ways were measured in.</summary>
    public required int Comparable { get; init; }

    /// <summary>
    /// True when this goes back to the way an earlier decision left, because that way has recovered -
    /// judged by the return rule over <see cref="DoorSwitchPolicy.ReturnWindowTicks"/>, not by "worse".
    /// </summary>
    public bool Return { get; init; }

    /// <summary>The quarter seconds the decision was judged over.</summary>
    public int WindowTicks { get; init; } = DoorSwitchPolicy.WindowTicks;
}

/// <summary>
/// Decides when a tunnel should move to another way into the same relay - an entry, or the direct road
/// when it came in through one - while a game is running.
///
/// WHAT IT COMPARES. relayd's own round trip down the current way (the recorder's pong, B) against the
/// same round trip down each other way (a Probe), quarter second by quarter second - in a match and, since
/// 2026-09-24, in the lobby (<see cref="QualityTick.Lobby"/>): a player who sits at 100 ms in the lobby blames
/// the relay long before a match starts, and moving there drops nothing. Nothing else. That
/// pairing is the whole method, because of what it cancels out: every way crosses the same Wi-Fi and
/// the same home router before it, and reaches the same relayd and the same route to the game after it.
/// A Wi-Fi burst, a busy relay or a slow datacentre route rises on every way at once and never makes one
/// look worse than another - which is right, since moving would not fix any of them. Only a difference
/// on the stretch that is NOT shared, the player's ISP to the relay against the entry's road to it, can
/// win. That is the fault moving fixes, and the one the 2026-09-15 match had: seven minutes at 77 ms
/// and 12-18% loss on Viettel's road to sg-2, with the home router flat throughout.
///
/// WHEN. The current way worse in at least three quarters of the last thirty seconds - later than a
/// spike of two to five seconds can reach, so a blip never moves anybody. Worse means over by
/// max(10 ms, 15%) of the other way, or unanswered while the other way answered. Or loss on its own:
/// a tenth of the current way's pongs gone while the other way answered, spread over at least four of
/// the window's six five-second slices, because steady loss at normal latency is as bad for a game as
/// latency and never reaches three quarters - and a single outage of a few seconds, however total,
/// sits in one or two slices and does not count. The other way must
/// itself have been answered in nine quarter seconds in ten and lost at most two probes: moving onto a
/// way that is also struggling is how thirty seconds of lag becomes a minute of it. A way that has never
/// answered a probe is an older relayd that does not know the message, and is never a candidate.
///
/// THEN five minutes before it decides anything again, including moving back. A way that has just
/// been left because it was bad needs time to prove it is good again, and a player bounced between two
/// ways every half minute is worse off than one left on a bad one.
///
/// It also moves a player onto a way that has simply been better by that margin for thirty seconds with
/// no incident at all. That is consistent with the connect-time rule, which would have chosen the same
/// way had it measured it then.
///
/// GOING BACK. Once the tunnel is on the way a decision moved it to, the way it left is judged by a lower
/// bar: faster by <see cref="ReturnMargin"/> in three quarters of the last TWO minutes, with at most
/// <see cref="ReturnMaxLost"/> probes lost down it and its 95th percentile no higher than the current
/// way's. The "worse" margin alone cannot bring a player back, because the way moved to is usually an
/// entry - a detour, slower by construction by about as much as that margin. 2026-09-18: sg-4 lost a
/// fifth of its pongs for thirty seconds and the tunnel moved to vn-1-sg4; a minute later sg-4 was back
/// at 43 ms against 53 on the entry, clean - but the gap was 9.6 ms at the median and 10 ms or more in
/// only 40% of quarter seconds, so the player stayed on the detour for the rest of the hour. The longer
/// window is what the lower bar costs: a way that was bad must be good for four times as long as "worse"
/// asks. The five minutes still apply first. A move back is not itself returned from.
///
/// A connect that started on an entry because it was faster than the direct road (TunnelEngine.ChooseDoorAsync)
/// is the same detour, and <see cref="StartedOnDetour"/> opens the same way back - without the five minutes,
/// since no decision of this policy made it.
///
/// Pure logic, like SpikeDetector: fed settled ticks, holds no clock and no sockets.
/// </summary>
public sealed class DoorSwitchPolicy
{
    public const int WindowTicks = 30 * SpikeDetector.TicksPerSecond;
    public const int CooldownTicks = 5 * 60 * SpikeDetector.TicksPerSecond;

    /// <summary>Of <see cref="WindowTicks"/>, how many must have both ways measured.</summary>
    internal const double MinComparableShare = 0.9;

    internal const double WorseShareToMove = 0.75;

    /// <summary>Current-way pongs lost while the other way answered, as a reason to move on its own.</summary>
    internal const int LossTicksToMove = WindowTicks / 10;

    /// <summary>The window in slices, and how many must hold some of that loss: steady, not one outage.</summary>
    internal const int LossSlices = 6;
    internal const int LossSlicesToMove = 4;

    /// <summary>Probes the other way may lose in the window and still count as clean.</summary>
    internal const int MaxOtherLost = 2;

    public static double Margin(double otherMs) => Math.Max(10.0, 0.15 * otherMs);

    /// <summary>How long the way a decision left must have been better before the tunnel goes back to it.</summary>
    public const int ReturnWindowTicks = 2 * 60 * SpikeDetector.TicksPerSecond;

    /// <summary>How much faster the way left must be in a quarter second to count as better for going back.</summary>
    internal const double ReturnMargin = 3.0;

    /// <summary>Probes the way left may lose in <see cref="ReturnWindowTicks"/> and still count as recovered.</summary>
    internal const int ReturnMaxLost = 4;

    private readonly Queue<QualityTick> _window = new();
    private long _lastIndex = long.MinValue;
    private long _lastDecisionIndex = long.MinValue;

    /// <summary>The way the last decision left and the one it moved to, while going back is still open.</summary>
    private string? _leftFrom;
    private string? _leftTo;

    /// <summary>Feeds one settled tick. Returns a decision at most once per <see cref="CooldownTicks"/>.</summary>
    public DoorDecision? Feed(QualityTick tick)
    {
        var continuous = _lastIndex == long.MinValue || tick.Index == _lastIndex + 1;
        _lastIndex = tick.Index;

        // A hole, no game, nothing to compare, or a different set of ways from the ticks already held:
        // none of it is evidence about this way against the others, so the window starts again.
        var comparable = (tick.Active || tick.Lobby) && tick.DoorIds is { Length: > 0 } && tick.CurrentDoor is not null;
        if (!continuous || !comparable || (_window.Count > 0 && !SamePath(_window.Peek(), tick)))
        {
            _window.Clear();
        }
        if (!comparable) return null;

        _window.Enqueue(tick);
        while (_window.Count > ReturnWindowTicks) _window.Dequeue();

        // With the tunnel anywhere but where the last decision sent it, going back is no longer open: the
        // move was never made (record mode, or it failed), or something else has moved it since.
        if (_leftTo is not null && !string.Equals(tick.CurrentDoor, _leftTo, StringComparison.OrdinalIgnoreCase))
        {
            _leftFrom = _leftTo = null;
        }

        if (_window.Count < WindowTicks) return null;
        if (_lastDecisionIndex != long.MinValue && tick.Index - _lastDecisionIndex < CooldownTicks) return null;

        var decision = Judge(tick) ?? JudgeReturn(tick);
        if (decision is not null)
        {
            _lastDecisionIndex = tick.Index;
            _window.Clear();
            _leftFrom = decision.Return ? null : decision.From;
            _leftTo = decision.Return ? null : decision.To;
        }
        return decision;
    }

    /// <summary>
    /// The tunnel was put on <paramref name="to"/> at connect because it was faster than <paramref name="from"/>,
    /// the relay's direct road. Going back to <paramref name="from"/> is then judged by the return rule, as after a
    /// move this policy made. Closed again, like that one, as soon as the tunnel is anywhere else.
    /// </summary>
    public void StartedOnDetour(string from, string to)
    {
        _leftFrom = from;
        _leftTo = to;
    }

    private static bool SamePath(QualityTick a, QualityTick b) =>
        string.Equals(a.CurrentDoor, b.CurrentDoor, StringComparison.OrdinalIgnoreCase) &&
        (ReferenceEquals(a.DoorIds, b.DoorIds) ||
         (a.DoorIds is not null && b.DoorIds is not null && a.DoorIds.SequenceEqual(b.DoorIds)));

    private DoorDecision? Judge(QualityTick last)
    {
        var ids = last.DoorIds!;
        DoorDecision? best = null;

        for (var slot = 0; slot < ids.Length; slot++)
        {
            int comparable = 0, worse = 0, lostWorse = 0, currentSent = 0, currentLost = 0, otherSent = 0, otherLost = 0;
            var lossSlices = 0;
            var position = -1;
            var current = new List<double>(WindowTicks);
            var other = new List<double>(WindowTicks);

            foreach (var tick in _window.Skip(_window.Count - WindowTicks))
            {
                position++;
                if (tick.RelayProcessSent)
                {
                    currentSent++;
                    if (tick.RelayProcessMs is { } c) current.Add(c);
                    else currentLost++;
                }

                var otherWasSent = tick.DoorSent is { } sent && slot < sent.Length && sent[slot];
                double? otherMs = otherWasSent && tick.DoorMs is { } ms && slot < ms.Length ? ms[slot] : null;
                if (otherWasSent)
                {
                    otherSent++;
                    if (otherMs is { } o) other.Add(o);
                    else otherLost++;
                }

                // Comparable only when both went out and the other way answered. The current way going
                // unanswered while the other answered is the loss half of "worse".
                if (!tick.RelayProcessSent || otherMs is not { } otherValue) continue;
                comparable++;
                if (tick.RelayProcessMs is not { } currentValue)
                {
                    worse++;
                    lostWorse++;
                    lossSlices |= 1 << (position * LossSlices / WindowTicks);
                }
                else if (currentValue - otherValue >= Margin(otherValue))
                {
                    worse++;
                }
            }

            if (other.Count == 0) continue;
            if (otherLost > MaxOtherLost) continue;
            if (comparable < MinComparableShare * WindowTicks) continue;

            var share = (double)worse / comparable;
            var steadyLoss = lostWorse >= LossTicksToMove &&
                             System.Numerics.BitOperations.PopCount((uint)lossSlices) >= LossSlicesToMove;
            if (share < WorseShareToMove && !steadyLoss) continue;

            var candidate = new DoorDecision
            {
                TickIndex = last.Index,
                AtUtc = last.StartUtc,
                From = last.CurrentDoor!,
                To = ids[slot],
                FromStats = Stats(current, currentSent, currentLost),
                ToStats = Stats(other, otherSent, otherLost),
                WorseShare = share,
                Comparable = comparable,
            };
            if (best is null || (candidate.ToStats.P50 ?? double.MaxValue) < (best.ToStats.P50 ?? double.MaxValue))
            {
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>
    /// Going back to the way the last decision left, once it has been the better one for two minutes - see
    /// GOING BACK above. Null until the window holds two minutes on the way that decision moved to.
    /// </summary>
    private DoorDecision? JudgeReturn(QualityTick last)
    {
        if (_leftFrom is null || _window.Count < ReturnWindowTicks) return null;
        var slot = Array.FindIndex(last.DoorIds!, id => string.Equals(id, _leftFrom, StringComparison.OrdinalIgnoreCase));
        if (slot < 0) return null;

        int comparable = 0, better = 0, currentSent = 0, currentLost = 0, otherSent = 0, otherLost = 0;
        var current = new List<double>(ReturnWindowTicks);
        var other = new List<double>(ReturnWindowTicks);
        foreach (var tick in _window)
        {
            if (tick.RelayProcessSent)
            {
                currentSent++;
                if (tick.RelayProcessMs is { } c) current.Add(c);
                else currentLost++;
            }

            var otherWasSent = tick.DoorSent is { } sent && slot < sent.Length && sent[slot];
            double? otherMs = otherWasSent && tick.DoorMs is { } ms && slot < ms.Length ? ms[slot] : null;
            if (otherWasSent)
            {
                otherSent++;
                if (otherMs is { } o) other.Add(o);
                else otherLost++;
            }

            // A pong lost on the current way while the way left answered counts toward going back, as it
            // counts toward "worse".
            if (!tick.RelayProcessSent || otherMs is not { } otherValue) continue;
            comparable++;
            if (tick.RelayProcessMs is not { } currentValue || currentValue - otherValue >= ReturnMargin) better++;
        }

        if (other.Count == 0 || otherLost > ReturnMaxLost) return null;
        if (comparable < MinComparableShare * ReturnWindowTicks) return null;
        var share = (double)better / comparable;
        if (share < WorseShareToMove) return null;

        var fromStats = Stats(current, currentSent, currentLost);
        var toStats = Stats(other, otherSent, otherLost);
        // Faster at the median is not enough while the way left still has the worse tail.
        if (fromStats.P95 is { } fromP95 && toStats.P95 is { } toP95 && toP95 > fromP95) return null;

        return new DoorDecision
        {
            TickIndex = last.Index,
            AtUtc = last.StartUtc,
            From = last.CurrentDoor!,
            To = last.DoorIds![slot],
            FromStats = fromStats,
            ToStats = toStats,
            WorseShare = share,
            Comparable = comparable,
            Return = true,
            WindowTicks = ReturnWindowTicks,
        };
    }

    /// <summary>Median, 95th percentile and loss of one way's round trips. Public: the recorder follows a decision with the same figures.</summary>
    public static DoorStats Stats(List<double> answered, int sent, int lost) => new(
        answered.Count == 0 ? null : SpikeDetector.Percentile(answered, 50),
        answered.Count == 0 ? null : SpikeDetector.Percentile(answered, 95),
        sent,
        lost);
}
