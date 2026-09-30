namespace GamePingBooster.Core.Quality;

/// <summary>
/// One lane into the relay - one source port - measured by a round of Probes: the median and the slowest answer,
/// and what went unanswered. <see cref="Slot"/> is 0 for the socket the tunnel is on, 1.. for the ones tried beside it.
/// </summary>
public sealed record LaneSample(int Slot, double? MedianMs, double? WorstMs, PingLoss Loss)
{
    public override string ToString() => MedianMs is { } ms
        ? $"{ms:F0} ms{(Loss.Lost > 0 ? $" ({Loss})" : "")}"
        : "no answer";
}

/// <summary>
/// What <see cref="LanePick.Choose"/> decided: the slot to move to, or null, and the line for the log. <paramref name="Unsettled"/>
/// is set when faster lanes were seen but none cleanly - the line jittered while it was measured - so a hunt soon after
/// is worth more than waiting for the next one.
/// </summary>
public sealed record LanePickResult(int? Slot, string Reason, bool Unsettled = false);

/// <summary>
/// Which source port the tunnel should send from, when several reach the same relay by the same way in.
///
/// Measured 2026-09-30: from a VN datacentre, UDP to one relay takes one of two to four fixed round trips depending
/// on the source port - 24, 33 or 41 ms from vn-1 to every Singapore relay; through vn-1 from a Viettel home line,
/// 45, 53 or 62 ms to sg-4 - and each port keeps its time run after run. It is the ISP spreading flows over parallel
/// links by a hash of the addresses and ports (ECMP), so a port is a ticket to one link. The client's port was
/// whatever Windows handed out, and a player kept the link it drew for the whole connection.
///
/// So after a connect or a move the engine tries <see cref="Candidates"/> more sockets to the same address, a round of
/// Probes each beside a round on the tunnel's own socket, and this picks. A candidate must answer every Probe, be
/// faster by <see cref="Margin"/>, and have its SLOWEST answer under the current lane's median - a lane is a link, and
/// a link's round trips sit within a millisecond of each other, so a candidate that only looks faster by jitter fails
/// that. A lane in use that drops half its Probes is left for a clean one that is not slower.
/// Pure: numbers in, a decision out.
/// </summary>
public static class LanePick
{
    /// <summary>
    /// Sockets tried beside the tunnel's. In the 2026-09-30 test the fast lane took 2 ports in 16; sixteen tries find
    /// a lane that common 88 times in 100, and one taking a third of the ports practically always.
    /// </summary>
    public const int Candidates = 16;

    /// <summary>Probes per lane. A link's own spread is well under a millisecond, so four tell lanes apart.</summary>
    public const int Rounds = 4;

    /// <summary>
    /// Probes a second the hunt may send, all lanes together, before the entry-switching probes are counted: relayd
    /// answers 20 a second per session and drops the rest silently. See <see cref="ProbesPerSecond"/>.
    /// </summary>
    public const int MaxProbesPerSecond = 10;

    /// <summary>
    /// The hunt's rate beside <paramref name="otherDoors"/> ways in probed four times a second by entry switching
    /// (SpikeRecorder), leaving two a second of the 20 spare; never under four, so a hunt always finishes.
    /// </summary>
    public static int ProbesPerSecond(int otherDoors) => Math.Clamp(18 - 4 * Math.Max(0, otherDoors), 4, MaxProbesPerSecond);

    /// <summary>How much faster another lane must be: 3 ms, or 5% of a long path. The lanes measured differ by 8-17 ms.</summary>
    public static double Margin(double currentMs) => Math.Max(3.0, 0.05 * currentMs);

    public static LanePickResult Choose(LaneSample current, IReadOnlyList<LaneSample> candidates)
    {
        var answered = candidates.Where(c => c.MedianMs is not null).ToList();
        if (current.MedianMs is null && answered.Count == 0)
        {
            return new LanePickResult(null, "no lane answered a Probe - the relay is older than the message, or the way is down; staying");
        }
        if (current.MedianMs is not { } here)
        {
            return new LanePickResult(null, "the lane in use answered no Probe, so there is nothing to compare with - staying");
        }

        var clean = answered.Where(c => c.Loss.Lost == 0 && c.Loss.Sent > 0).ToList();
        if (clean.Count == 0) return new LanePickResult(null, $"no other lane answered every Probe - staying on {current}");

        // The fastest of the lanes that pass BOTH bars, not the fastest lane tried: on 2026-09-30 (VNPT) the 40 ms port
        // had one answer at 58 and failed the jitter bar, and the tunnel stayed at 58 ms beside clean 41 and 42 ms ones.
        var margin = Margin(here);
        var faster = clean
            .Where(c => c.MedianMs!.Value <= here - margin && c.WorstMs is { } worst && worst < here)
            .MinBy(c => c.MedianMs!.Value);
        if (faster is not null)
        {
            return new LanePickResult(faster.Slot, $"a lane at {faster} against {current} - faster by {here - faster.MedianMs!.Value:F0} ms");
        }

        var best = clean.MinBy(c => c.MedianMs!.Value)!;
        var bestMs = best.MedianMs!.Value;
        if (current.Loss.Sent > 0 && current.Loss.Lost * 2 >= current.Loss.Sent && bestMs <= here)
        {
            return new LanePickResult(best.Slot, $"the lane in use is losing Probes ({current}); a clean one at {best}");
        }

        return bestMs <= here - margin
            ? new LanePickResult(null, $"the lanes faster than {current} by {margin:F0} ms each had an answer as slow as it (jitter, not a faster link) - staying", Unsettled: true)
            : new LanePickResult(null, $"the fastest other lane, {best}, is not faster than {current} by {margin:F0} ms - staying");
    }
}
