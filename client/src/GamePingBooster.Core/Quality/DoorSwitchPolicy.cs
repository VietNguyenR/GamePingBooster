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
}

/// <summary>
/// Decides when a tunnel should move to another way into the same relay - an entry, or the direct road
/// when it came in through one - while a game is running.
///
/// WHAT IT COMPARES. relayd's own round trip down the current way (the recorder's pong, B) against the
/// same round trip down each other way (a Probe), quarter second by quarter second. Nothing else. That
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

    internal static double Margin(double otherMs) => Math.Max(10.0, 0.15 * otherMs);

    private readonly Queue<QualityTick> _window = new();
    private long _lastIndex = long.MinValue;
    private long _lastDecisionIndex = long.MinValue;

    /// <summary>Feeds one settled tick. Returns a decision at most once per <see cref="CooldownTicks"/>.</summary>
    public DoorDecision? Feed(QualityTick tick)
    {
        var continuous = _lastIndex == long.MinValue || tick.Index == _lastIndex + 1;
        _lastIndex = tick.Index;

        // A hole, no game, nothing to compare, or a different set of ways from the ticks already held:
        // none of it is evidence about this way against the others, so the window starts again.
        var comparable = tick.Active && tick.DoorIds is { Length: > 0 } && tick.CurrentDoor is not null;
        if (!continuous || !comparable || (_window.Count > 0 && !SamePath(_window.Peek(), tick)))
        {
            _window.Clear();
        }
        if (!comparable) return null;

        _window.Enqueue(tick);
        while (_window.Count > WindowTicks) _window.Dequeue();
        if (_window.Count < WindowTicks) return null;
        if (_lastDecisionIndex != long.MinValue && tick.Index - _lastDecisionIndex < CooldownTicks) return null;

        var decision = Judge(tick);
        if (decision is not null)
        {
            _lastDecisionIndex = tick.Index;
            _window.Clear();
        }
        return decision;
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

            foreach (var tick in _window)
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

    /// <summary>Median, 95th percentile and loss of one way's round trips. Public: the recorder follows a decision with the same figures.</summary>
    public static DoorStats Stats(List<double> answered, int sent, int lost) => new(
        answered.Count == 0 ? null : SpikeDetector.Percentile(answered, 50),
        answered.Count == 0 ? null : SpikeDetector.Percentile(answered, 95),
        sent,
        lost);
}
