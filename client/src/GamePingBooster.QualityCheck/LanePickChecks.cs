using GamePingBooster.Core.Quality;

namespace GamePingBooster.QualityCheck;

/// <summary>
/// LanePick: which source port the tunnel should send from (2026-09-30). The numbers are the owner's line through vn-1 to
/// sg-4 that day - lanes at 45, 53 and 62 ms, fixed per port.
/// </summary>
internal static partial class Program
{
    private static LaneSample Lane(int slot, double? ms, double? worst = null, int answered = LanePick.Rounds) =>
        new(slot, ms, ms is null ? null : worst ?? ms + 0.6, new PingLoss(LanePick.Rounds, ms is null ? 0 : answered));

    private static void LanePickChecks()
    {
        var owner = LanePick.Choose(Lane(0, 62.1), [Lane(1, 53.4), Lane(2, 45.2), Lane(3, 61.8), Lane(4, 53.9)]);
        Check("On the 62 ms lane with 45 and 53 among the others - takes 45, the fastest", owner.Slot == 2, owner.Reason);

        var already = LanePick.Choose(Lane(0, 45.3), [Lane(1, 53.4), Lane(2, 45.0), Lane(3, 61.8)]);
        Check("Already on the fastest lane (45.3 against another 45.0) - stays", already.Slot is null, already.Reason);

        var flat = LanePick.Choose(Lane(0, 43.4), [Lane(1, 42.9), Lane(2, 43.1), Lane(3, 43.5)]);
        Check("A line with no lanes, every port within a millisecond - stays", flat.Slot is null, flat.Reason);

        var jitter = LanePick.Choose(Lane(0, 53.0), [Lane(1, 45.0, worst: 58.0)]);
        Check("Faster at the median, but one answer slower than the lane in use - jitter, not a lane; stays",
            jitter.Slot is null, jitter.Reason);
        Check("  ...and says so as unsettled, so the engine tries again in a minute rather than half an hour",
            jitter.Unsettled, jitter.Reason);
        Check("A line with no faster lane at all is settled - no early retry", !LanePick.Choose(Lane(0, 43.4), [Lane(1, 42.9)]).Unsettled, "");

        var vnpt = LanePick.Choose(Lane(0, 58.0), [Lane(1, 40.0, worst: 59.0), Lane(2, 41.0), Lane(3, 42.0), Lane(4, 52.0)]);
        Check("VNPT 2026-09-30: the 40 ms port jittered up to 59, clean 41 and 42 beside it - takes 41, not staying at 58",
            vnpt.Slot == 2, vnpt.Reason);

        var lossy = LanePick.Choose(Lane(0, 53.0), [Lane(1, 45.0, answered: 3), Lane(2, 49.0)]);
        Check("The fastest port lost a Probe - the clean one at 49 is taken instead", lossy.Slot == 2, lossy.Reason);

        var losingHere = LanePick.Choose(Lane(0, 45.0, answered: 2), [Lane(1, 45.5), Lane(2, 53.0)]);
        Check("The lane in use losing half its Probes, the clean one 0.5 ms slower - stays: a clean lane must not be slower",
            losingHere.Slot is null, losingHere.Reason);

        var losingHereFaster = LanePick.Choose(Lane(0, 46.0, answered: 2), [Lane(1, 45.5), Lane(2, 53.0)]);
        Check("The lane in use losing half its Probes, a clean one at 45.5 against 46 - moves", losingHereFaster.Slot == 1, losingHereFaster.Reason);

        var small = LanePick.Choose(Lane(0, 24.0), [Lane(1, 21.5)]);
        Check("2.5 ms faster at 24 ms is inside the 3 ms margin - stays", small.Slot is null, small.Reason);

        var entry = LanePick.Choose(Lane(0, 41.0), [Lane(1, 32.6), Lane(2, 24.1), Lane(3, 32.4)]);
        Check("vn-1 to sg-4 from the box itself: 41 in use, 24 found - moves to 24", entry.Slot == 2, entry.Reason);

        var old = LanePick.Choose(Lane(0, null), [Lane(1, null), Lane(2, null)]);
        Check("Nothing answers a Probe (a relayd older than the message) - stays", old.Slot is null, old.Reason);

        var unmeasured = LanePick.Choose(Lane(0, null), [Lane(1, 30.0)]);
        Check("The lane in use unmeasured - nothing to compare, stays", unmeasured.Slot is null, unmeasured.Reason);

        Check("Hunt rate: 10/s alone, 10 beside one other way, 10 beside two, 6 beside three, never under 4",
            LanePick.ProbesPerSecond(0) == 10 && LanePick.ProbesPerSecond(1) == 10 && LanePick.ProbesPerSecond(2) == 10 &&
            LanePick.ProbesPerSecond(3) == 6 && LanePick.ProbesPerSecond(9) == 4,
            string.Join(",", Enumerable.Range(0, 5).Select(LanePick.ProbesPerSecond)));
    }
}
