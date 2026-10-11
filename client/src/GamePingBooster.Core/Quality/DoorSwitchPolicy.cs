namespace GamePingBooster.Core.Quality;

/// <summary>A round trip to relayd down one way in, over a window: median, 95th percentile, and how many went unanswered.</summary>
public sealed record DoorStats(double? P50, double? P95, int Sent, int Lost)
{
    public double? LossPct => Sent == 0 ? null : 100.0 * Lost / Sent;
}

/// <summary>
/// The tunnel's current way into its relay was worse than another way into the same relay for most of
/// the last eight seconds, or lost pongs steadily for thirty. Whether anything is done about it is the
/// recorder's and the engine's call.
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

    /// <summary>
    /// True when the other way was simply faster for thirty seconds by less than the "worse" margin - the steady
    /// rule over <see cref="DoorSwitchPolicy.SteadyWindowTicks"/> (STEADILY FASTER), not a lag on the current way.
    /// </summary>
    public bool Steady { get; init; }

    /// <summary>The quarter seconds the decision was judged over.</summary>
    public int WindowTicks { get; init; } = DoorSwitchPolicy.WindowTicks;

    /// <summary>
    /// True when the way in use swings and the other is steadier by enough to make up for any median it gives away -
    /// the jitter rule over <see cref="DoorSwitchPolicy.JitterWindowTicks"/> (SWINGING), not a lag or a faster road.
    /// </summary>
    public bool Jitter { get; init; }

    /// <summary>For a <see cref="Jitter"/> decision: how much more each way swung than the calmest way into the relay.</summary>
    public double? FromJitterMs { get; init; }
    public double? ToJitterMs { get; init; }
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
/// WHEN. The current way worse in at least three quarters of the last EIGHT seconds (<see cref="WindowTicks"/>),
/// so six seconds of it at the least - longer than a spike of two to five seconds can reach, so a blip never
/// moves anybody. Worse means over by max(10 ms, 15%) of the other way, or unanswered while the other way
/// answered. The other way must itself have been answered in nine quarter seconds in ten and lost at most a
/// tenth of its probes (<see cref="MaxOtherLostShare"/>): moving onto a way that is also struggling is how
/// seconds of lag become a minute of it.
///
/// Eight, not the thirty this began with, since 2026-10-02. The owner's own match that evening sat at 83 ms
/// on sg-2 against 46 on its entry for 58 s before it moved: thirty for the window, and twenty-eight more
/// because the entry lost three probes in a row once while the bar was "at most two in thirty seconds" -
/// which the other ways exceed in one calm ten-second stretch in eight. Replayed over 5,469 match hours of prod
/// spike records with every way's samples: eight seconds decided 6.8 s into a lag at the median, three
/// quarters of those lags were still going five seconds later, and a move to a way that then proved slower
/// for nothing came about four times in a thousand hours - most ways in sit within 3 ms of each other.
/// Six seconds doubled those wasted moves; five, more again.
///
/// Or loss on its own, over thirty seconds (<see cref="LossWindowTicks"/>): a tenth of the current way's
/// pongs gone while the other way answered, spread over at least four of the window's six five-second
/// slices, because steady loss at normal latency is as bad for a game as latency and never reaches three
/// quarters - and a single outage of a few seconds, however total, sits in one or two slices and does not
/// count. That one keeps the old, stricter bar for the other way, at most two probes lost: moving for loss
/// onto a way that loses too buys nothing. A way that has never answered a probe is an older relayd that
/// does not know the message, and is never a candidate.
///
/// THEN five minutes before it decides anything again, including moving back. A way that has just
/// been left because it was bad needs time to prove it is good again, and a player bounced between two
/// ways every half minute is worse off than one left on a bad one.
///
/// It also moves a player onto a way that has simply been better by that margin for eight seconds with
/// no incident at all. That is consistent with the connect-time rule, which would have chosen the same
/// way had it measured it then.
///
/// STEADILY FASTER. A way faster by less than that margin, but every quarter second, never moved anybody:
/// on 2026-10-07 the owner's match sat on sg-2 at 58 ms for three minutes while vn-5-sg2 answered in 49,
/// clean, with a lower 95th percentile - 9 ms, under the 10 ms bar, so it was never "worse", and the move
/// waited for the between-matches rescan (max(5 ms, 10%)). So a second bar over THIRTY seconds
/// (<see cref="SteadyWindowTicks"/>): the other way faster by <see cref="SteadyTickMargin"/> in three
/// quarters of the quarter seconds, its median faster by <see cref="SteadyMargin"/> - the rescan's margin -
/// its 95th percentile no higher than the current way's, and at most <see cref="SteadyMaxOtherLostShare"/>
/// of its probes lost. Replayed over prod spike records with every way's samples (38,882 matches, record-mode
/// relays giving what came after): it decided in 1,644 match-ways, 1,421 of which the "worse" bar never
/// reached, and afterwards the way moved to stayed faster by 7.1 ms at the median, by 3 ms or more in 90%,
/// and turned out slower in 6.6% - against 8.4% for the "worse" bar's own decisions. Sixty seconds instead of
/// thirty was no surer (6.5%); a 7 or 8 ms bar was a little surer (6.0%, 4.9%) and caught a third to a half
/// fewer. The 95th percentile check halves the moves onto a way with the worse tail. The way the last
/// decision left is not judged by it: going back has its own, longer proof (GOING BACK).
///
/// The connect-time rule keeps the 10 ms bar on purpose. Its numbers are a burst of pings: that evening
/// vn-3-sg2 measured 50 ms at connect against sg-2's 60, and 70 for the whole match after - a 5 ms bar
/// there would have started the player on the slowest way in. Thirty seconds of quarter seconds, in the
/// lobby or the match, is where a gap this small can be told from noise.
///
/// GOING BACK. Once the tunnel is on the way a decision moved it to, the way it left is judged by a lower
/// bar: faster by <see cref="ReturnMargin"/> in three quarters of the last TWO minutes, with at most
/// <see cref="ReturnMaxLost"/> probes lost down it and its 95th percentile no higher than the current
/// way's. The "worse" margin alone cannot bring a player back, because the way moved to is usually an
/// entry - a detour, slower by construction by about as much as that margin. 2026-09-18: sg-4 lost a
/// fifth of its pongs for thirty seconds and the tunnel moved to vn-1-sg4; a minute later sg-4 was back
/// at 43 ms against 53 on the entry, clean - but the gap was 9.6 ms at the median and 10 ms or more in
/// only 40% of quarter seconds, so the player stayed on the detour for the rest of the hour. The longer
/// window is what the lower bar costs: a way that was bad must be good for two minutes, where "worse" asks
/// eight seconds. The five minutes still apply first. A move back is not itself returned from.
///
/// SWINGING. None of the above leaves a way that is fast at the median and swings: through vn-2 an FPT player sat at
/// 38 ms half the time and 50-60 the rest, beside a steady 50 on sg-5's own address - never "worse" for eight seconds,
/// and the steady rule never applies to a way that is SLOWER at the median. So a last bar over TWO minutes
/// (<see cref="JitterWindowTicks"/>): each way scored as its median plus <see cref="Quality.Jitter.Penalty"/> of how much
/// more it swings than the calmest way into the relay (<see cref="WayJitter"/>), and the tunnel moves when the way in
/// use swings, the other way's score is lower by <see cref="SteadyMargin"/>, its 95th percentile is lower, and the other is clean. The same score
/// keeps the steady rule from moving a player ONTO a way that swings more than the one he is on. Two minutes because a
/// swing comes and goes in bursts of a second or two: thirty seconds can fall between them.
///
/// A connect that started on an entry because it was faster than the direct road (TunnelEngine.ChooseDoorAsync)
/// is the same detour, and <see cref="StartedOnDetour"/> opens the same way back - without the five minutes,
/// since no decision of this policy made it.
///
/// Pure logic, like SpikeDetector: fed settled ticks, holds no clock and no sockets.
/// </summary>
public sealed class DoorSwitchPolicy
{
    /// <summary>The window "worse" is judged over. See WHEN above.</summary>
    public const int WindowTicks = 8 * SpikeDetector.TicksPerSecond;

    /// <summary>The window steady loss is judged over: loss needs time to show it is steady and not one outage.</summary>
    public const int LossWindowTicks = 30 * SpikeDetector.TicksPerSecond;

    public const int CooldownTicks = 5 * 60 * SpikeDetector.TicksPerSecond;

    /// <summary>Of a window, how many quarter seconds must have both ways measured.</summary>
    internal const double MinComparableShare = 0.9;

    internal const double WorseShareToMove = 0.75;

    /// <summary>The share of its probes the other way may lose in <see cref="WindowTicks"/> and still count as clean.</summary>
    internal const double MaxOtherLostShare = 0.1;

    /// <summary>Current-way pongs lost while the other way answered, as a reason to move on its own.</summary>
    internal const int LossTicksToMove = LossWindowTicks / 10;

    /// <summary>The loss window in slices, and how many must hold some of that loss: steady, not one outage.</summary>
    internal const int LossSlices = 6;
    internal const int LossSlicesToMove = 4;

    /// <summary>Probes the other way may lose in <see cref="LossWindowTicks"/> and still be moved to for loss.</summary>
    internal const int MaxOtherLost = 2;

    public static double Margin(double otherMs) => Math.Max(10.0, 0.15 * otherMs);

    /// <summary>The window the steady rule is judged over. See STEADILY FASTER above.</summary>
    public const int SteadyWindowTicks = 30 * SpikeDetector.TicksPerSecond;

    /// <summary>How much faster the other way must be in a quarter second to count toward the steady rule.</summary>
    public const double SteadyTickMargin = 5.0;

    /// <summary>How much faster the other way's median must be over the steady window: the between-matches rescan's margin.</summary>
    public static double SteadyMargin(double otherMs) => Math.Max(5.0, 0.1 * otherMs);

    /// <summary>The share of its probes the other way may lose over the steady window.</summary>
    internal const double SteadyMaxOtherLostShare = 0.05;

    /// <summary>How long the way a decision left must have been better before the tunnel goes back to it.</summary>
    public const int ReturnWindowTicks = 2 * 60 * SpikeDetector.TicksPerSecond;

    /// <summary>How much faster the way left must be in a quarter second to count as better for going back.</summary>
    public const double ReturnMargin = 3.0;

    /// <summary>Probes the way left may lose in <see cref="ReturnWindowTicks"/> and still count as recovered.</summary>
    internal const int ReturnMaxLost = 4;

    /// <summary>The window the jitter rule and <see cref="WayJitter"/> are judged over. See SWINGING above.</summary>
    public const int JitterWindowTicks = 2 * 60 * SpikeDetector.TicksPerSecond;

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
        while (_window.Count > Math.Max(ReturnWindowTicks, JitterWindowTicks)) _window.Dequeue();

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
        string.Equals(a.CurrentDoor, b.CurrentDoor, StringComparison.OrdinalIgnoreCase) && a.CurrentLane == b.CurrentLane &&
        (ReferenceEquals(a.DoorIds, b.DoorIds) ||
         (a.DoorIds is not null && b.DoorIds is not null && a.DoorIds.SequenceEqual(b.DoorIds)));

    private DoorDecision? Judge(QualityTick last)
    {
        var ids = last.DoorIds!;
        DoorDecision? best = null;
        IReadOnlyDictionary<string, double>? jitter = null;

        for (var slot = 0; slot < ids.Length; slot++)
        {
            // "Worse" over the last eight seconds, against a way that lost at most a tenth of its probes - or, for
            // the way the last decision left, at most two in thirty seconds: it was left for being bad, and eight
            // seconds faster is not it proving itself clean again (GOING BACK).
            var fast = Count(slot, WindowTicks);
            var clean = IsLeft(ids[slot])
                ? Count(slot, Math.Min(_window.Count, LossWindowTicks)).OtherLost <= MaxOtherLost
                : fast.OtherLost <= MaxOtherLostShare * fast.OtherSent;
            var candidate = fast.Other.Count > 0 && clean &&
                            fast.Comparable >= MinComparableShare * WindowTicks && fast.Share >= WorseShareToMove
                ? Decide(last, slot, fast, WindowTicks)
                : null;

            // Not onto a way no better once its swing is counted: "worse" by 10 ms against a way 12 ms faster at the
            // median is no lag when that way swings 16 ms' worth - it is the way the jitter rule just left (SWINGING).
            // A real lag on the way in use lifts its own median and swing, and still leaves.
            if (candidate is not null &&
                Score(candidate.ToStats, candidate.To, jitter ??= WayJitter(Math.Min(_window.Count, JitterWindowTicks))) >=
                Score(candidate.FromStats, candidate.From, jitter))
            {
                candidate = null;
            }

            // Or steady loss over the last thirty, against a way that lost at most two.
            if (candidate is null && _window.Count >= LossWindowTicks)
            {
                var loss = Count(slot, LossWindowTicks);
                var steadyLoss = loss.LostWorse >= LossTicksToMove &&
                                 System.Numerics.BitOperations.PopCount((uint)loss.LossSlices) >= LossSlicesToMove;
                if (loss.Other.Count > 0 && loss.OtherLost <= MaxOtherLost &&
                    loss.Comparable >= MinComparableShare * LossWindowTicks && steadyLoss)
                {
                    candidate = Decide(last, slot, loss, LossWindowTicks);
                }
            }

            // Or simply faster, by less than "worse" asks but for thirty seconds (STEADILY FASTER) - never back to the
            // way the last decision left, which has the return rule.
            if (candidate is null && !IsLeft(ids[slot]) && _window.Count >= SteadyWindowTicks)
            {
                candidate = JudgeSteady(last, slot, jitter ??= WayJitter(Math.Min(_window.Count, JitterWindowTicks)));
            }

            // Or steadier, for two minutes, by more than the median it gives away (SWINGING) - not back to the way left.
            if (candidate is null && !IsLeft(ids[slot]) && _window.Count >= JitterWindowTicks)
            {
                candidate = JudgeJitter(last, slot, jitter ??= WayJitter(Math.Min(_window.Count, JitterWindowTicks)));
            }

            if (candidate is null) continue;
            if (best is null || Score(candidate.ToStats, candidate.To, jitter) < Score(best.ToStats, best.To, jitter))
            {
                best = candidate;
            }
        }
        return best;
    }

    private bool IsLeft(string id) => _leftFrom is not null && string.Equals(id, _leftFrom, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The steady rule for the way in <paramref name="slot"/> over the last <see cref="SteadyWindowTicks"/>: faster by
    /// <see cref="SteadyTickMargin"/> in three quarters of the quarter seconds, by <see cref="SteadyMargin"/> at the
    /// median, no worse at the 95th percentile, and clean. Null when any of it does not hold.
    /// </summary>
    private DoorDecision? JudgeSteady(QualityTick last, int slot, IReadOnlyDictionary<string, double>? jitter)
    {
        var t = Count(slot, SteadyWindowTicks, static _ => SteadyTickMargin);
        if (t.Other.Count == 0 || t.Current.Count == 0) return null;
        if (t.Comparable < MinComparableShare * SteadyWindowTicks || t.Share < WorseShareToMove) return null;
        if (t.OtherLost > SteadyMaxOtherLostShare * t.OtherSent) return null;

        var from = Stats(t.Current, t.CurrentSent, t.CurrentLost);
        var to = Stats(t.Other, t.OtherSent, t.OtherLost);
        if (from.P50 is not { } fromP50 || to.P50 is not { } toP50 || fromP50 - toP50 < SteadyMargin(toP50)) return null;
        if (from.P95 is { } fromP95 && to.P95 is { } toP95 && toP95 > fromP95) return null;

        // Never onto a way that swings more than this one - over as much of the two minutes as the window holds.
        if (Quality.Jitter.Penalty(ExcessOf(jitter, last.DoorIds![slot])) > Quality.Jitter.Penalty(ExcessOf(jitter, last.CurrentDoor!)))
        {
            return null;
        }

        return Decide(last, slot, t, SteadyWindowTicks, steady: true);
    }

    private static double? ExcessOf(IReadOnlyDictionary<string, double>? jitter, string way) =>
        jitter is not null && jitter.TryGetValue(way, out var e) ? e : null;

    /// <summary>A way's median over a decision's window plus its jitter penalty; the worst there is with no median.</summary>
    private static double Score(DoorStats stats, string way, IReadOnlyDictionary<string, double>? jitter) =>
        stats.P50 is { } p50 ? p50 + Quality.Jitter.Penalty(ExcessOf(jitter, way)) : double.MaxValue;

    /// <summary>
    /// The jitter rule for the way in <paramref name="slot"/> over the last <see cref="JitterWindowTicks"/>: the way in use
    /// swings, the other's median plus its jitter penalty is lower than the way in use's by <see cref="SteadyMargin"/>,
    /// its 95th percentile is lower, and the other lost at most <see cref="SteadyMaxOtherLostShare"/> of its probes. Null when any of it does not hold.
    /// </summary>
    private DoorDecision? JudgeJitter(QualityTick last, int slot, IReadOnlyDictionary<string, double>? jitter)
    {
        var fromJitter = ExcessOf(jitter, last.CurrentDoor!);
        var fromPenalty = Quality.Jitter.Penalty(fromJitter);
        if (fromPenalty <= 0) return null;

        var t = Count(slot, JitterWindowTicks);
        if (t.Other.Count == 0 || t.Current.Count == 0) return null;
        if (t.Comparable < MinComparableShare * JitterWindowTicks) return null;
        if (t.OtherLost > SteadyMaxOtherLostShare * t.OtherSent) return null;

        var from = Stats(t.Current, t.CurrentSent, t.CurrentLost);
        var to = Stats(t.Other, t.OtherSent, t.OtherLost);
        if (from.P50 is not { } fromP50 || to.P50 is not { } toP50) return null;
        var toJitter = ExcessOf(jitter, last.DoorIds![slot]);
        var fromScore = fromP50 + fromPenalty;
        var toScore = toP50 + Quality.Jitter.Penalty(toJitter);
        if (fromScore - toScore < SteadyMargin(toScore)) return null;

        // A move for the swing must leave the tail lower, as the steady and return rules ask. The penalty alone has no
        // ceiling: a way swinging 25 ms over the calmest counts 44 ms slower and would trade for a road 30-40 ms slower
        // at the median. Replayed over prod spike records 2026-10-04..11 that move turned out worse at the 95th
        // percentile 16% of the time, 42% when leaving a way other than vn-2; with this check 9% and 24%.
        if (from.P95 is { } fromP95 && to.P95 is { } toP95 && toP95 >= fromP95) return null;

        return Decide(last, slot, t, JitterWindowTicks, jitter: (fromJitter, toJitter));
    }

    /// <summary>The current way against the way in <paramref name="slot"/>, over the last <paramref name="ticks"/> of the window.</summary>
    private sealed class Tally
    {
        public int Comparable, Worse, LostWorse, CurrentSent, CurrentLost, OtherSent, OtherLost, LossSlices;
        public readonly List<double> Current = [];
        public readonly List<double> Other = [];
        public double Share => Comparable == 0 ? 0 : (double)Worse / Comparable;
    }

    /// <param name="margin">What "worse" means in one quarter second, from the other way's round trip: <see cref="Margin"/> unless said.</param>
    private Tally Count(int slot, int ticks, Func<double, double>? margin = null)
    {
        margin ??= Margin;
        var t = new Tally();
        var position = -1;
        foreach (var tick in _window.Skip(_window.Count - ticks))
        {
            position++;
            if (tick.RelayProcessSent)
            {
                t.CurrentSent++;
                if (tick.RelayProcessMs is { } c) t.Current.Add(c);
                else t.CurrentLost++;
            }

            var otherWasSent = tick.DoorSent is { } sent && slot < sent.Length && sent[slot];
            double? otherMs = otherWasSent && tick.DoorMs is { } ms && slot < ms.Length ? ms[slot] : null;
            if (otherWasSent)
            {
                t.OtherSent++;
                if (otherMs is { } o) t.Other.Add(o);
                else t.OtherLost++;
            }

            // Comparable only when both went out and the other way answered. The current way going
            // unanswered while the other answered is the loss half of "worse".
            if (!tick.RelayProcessSent || otherMs is not { } otherValue) continue;
            t.Comparable++;
            if (tick.RelayProcessMs is not { } currentValue)
            {
                t.Worse++;
                t.LostWorse++;
                t.LossSlices |= 1 << (position * LossSlices / ticks);
            }
            else if (currentValue - otherValue >= margin(otherValue))
            {
                t.Worse++;
            }
        }
        return t;
    }

    private static DoorDecision Decide(QualityTick last, int slot, Tally t, int windowTicks, bool steady = false,
        (double? From, double? To)? jitter = null) => new()
    {
        Steady = steady,
        Jitter = jitter is not null,
        FromJitterMs = jitter?.From,
        ToJitterMs = jitter?.To,
        TickIndex = last.Index,
        AtUtc = last.StartUtc,
        From = last.CurrentDoor!,
        To = last.DoorIds![slot],
        FromStats = Stats(t.Current, t.CurrentSent, t.CurrentLost),
        ToStats = Stats(t.Other, t.OtherSent, t.OtherLost),
        WorseShare = t.Share,
        Comparable = t.Comparable,
        WindowTicks = windowTicks,
    };

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

    /// <summary>
    /// How much more each way into the relay - the one in use included - swung than the calmest of them over the last
    /// <paramref name="ticks"/> quarter seconds (<see cref="JitterWindowTicks"/> unless said), by way id; null until the window
    /// holds that long on one path. A way answered in fewer than half the quarter seconds is left out: too little to call
    /// it steady or not.
    ///
    /// What every probed way does in the same quarter second is taken out first: each tick, the smallest rise over its
    /// own median among the probed ways (two or more of them answering). A PC that stalls, or a Wi-Fi burst, delays the
    /// probes together - on 2026-10-10 a customer's evening showed every probed way at p95 64-84 against a steady 43.8 on
    /// the way in use - and what they share is not any one road's. It is taken off the way in use too: a Wi-Fi burst
    /// delays its pong as much, and a stall that delayed only the probes leaves the pong early instead, below its median,
    /// which a spread up to the 90th percentile does not count. Then each way is counted over the calmest
    /// (<see cref="Quality.Jitter.OverCalmest"/>).
    /// </summary>
    public IReadOnlyDictionary<string, double>? WayJitter(int ticks = JitterWindowTicks)
    {
        if (ticks <= 0 || _window.Count < ticks) return null;
        var window = _window.Skip(_window.Count - ticks).ToList();
        var last = window[^1];
        var ids = last.DoorIds!;
        var current = last.CurrentDoor!;

        // Each way's median over the window: the probed ways by slot, the way in use by its pongs.
        var medians = new double?[ids.Length];
        for (var slot = 0; slot < ids.Length; slot++)
        {
            var values = window.Select(k => ProbeOf(k, slot)).OfType<double>().ToList();
            medians[slot] = values.Count >= window.Count / 2 ? SpikeDetector.Percentile(values, 50) : null;
        }
        var currentValues = window.Select(k => k.RelayProcessSent ? k.RelayProcessMs : null).OfType<double>().ToList();
        double? currentMedian = currentValues.Count >= window.Count / 2 ? SpikeDetector.Percentile(currentValues, 50) : null;

        var residuals = ids.Select(_ => new List<double>(window.Count)).ToArray();
        var currentResiduals = new List<double>(window.Count);
        var rises = new double?[ids.Length];
        foreach (var tick in window)
        {
            double? shared = null;
            var answering = 0;
            for (var slot = 0; slot < ids.Length; slot++)
            {
                rises[slot] = medians[slot] is { } median && ProbeOf(tick, slot) is { } ms ? ms - median : null;
                if (rises[slot] is not { } rise) continue;
                shared = shared is { } s ? Math.Min(s, rise) : rise;
                answering++;
            }
            var common = answering >= 2 && shared is { } c ? Math.Max(0, c) : 0;
            for (var slot = 0; slot < ids.Length; slot++)
            {
                if (rises[slot] is { } rise) residuals[slot].Add(rise - common);
            }
            if (currentMedian is { } cm && tick.RelayProcessSent && tick.RelayProcessMs is { } cms) currentResiduals.Add(cms - cm - common);
        }

        var names = new List<string>();
        var spreads = new List<double?>();
        for (var slot = 0; slot < ids.Length; slot++)
        {
            if (medians[slot] is null) continue;
            names.Add(ids[slot]);
            spreads.Add(Quality.Jitter.Spread(residuals[slot]));
        }
        if (currentMedian is not null)
        {
            names.Add(current);
            spreads.Add(Quality.Jitter.Spread(currentResiduals));
        }
        if (names.Count < 2) return null;

        var excess = Quality.Jitter.OverCalmest(spreads);
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            if (excess[i] is { } e) result[names[i]] = e;
        }
        return result;
    }

    private static double? ProbeOf(QualityTick tick, int slot) =>
        tick.DoorSent is { } sent && slot < sent.Length && sent[slot] && tick.DoorMs is { } ms && slot < ms.Length ? ms[slot] : null;

    /// <summary>Median, 95th percentile and loss of one way's round trips. Public: the recorder follows a decision with the same figures.</summary>
    public static DoorStats Stats(List<double> answered, int sent, int lost) => new(
        answered.Count == 0 ? null : SpikeDetector.Percentile(answered, 50),
        answered.Count == 0 ? null : SpikeDetector.Percentile(answered, 95),
        sent,
        lost);
}
