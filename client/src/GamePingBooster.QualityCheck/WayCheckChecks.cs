using GamePingBooster.Core.Quality;

namespace GamePingBooster.QualityCheck;

/// <summary>
/// WayCheck: whether a region's tunnel, idle between matches, changes its way in before the next one (2026-09-30).
/// Same bars as the switch policy - better by DoorSwitchPolicy.Margin to leave, ReturnMargin and clean to go back - on
/// the score, so a way losing packets counts RelayLoss.PenaltyMs slower.
/// </summary>
internal static partial class Program
{
    private static WaySample Way(string id, double? ms, int answered = WayCheck.Rounds) =>
        new(id, ms, new PingLoss(WayCheck.Rounds, answered));

    private static void WayCheckChecks()
    {
        var customer = WayCheck.Choose("sg-1", [Way("sg-1", 95), Way("vn-1-sg", 52), Way("vn-2-sg", 60)]);
        Check("The customer's evening: sg-1 direct at 95 ms, vn-1-sg at 52 - moves to vn-1-sg, the best of the others",
            customer.MoveTo == "vn-1-sg" && !customer.Return, customer.Reason);

        var inside = WayCheck.Choose("sg-1", [Way("sg-1", 50), Way("vn-1-sg", 42)]);
        Check("8 ms better at 42 ms is inside the margin (max(10, 15%)) - stays", inside.MoveTo is null, inside.Reason);

        var edge = WayCheck.Choose("sg-1", [Way("sg-1", 52), Way("vn-1-sg", 42)]);
        Check("Exactly the margin (10 ms at 42) - moves, as the policy's 'worse' does at its margin", edge.MoveTo == "vn-1-sg", edge.Reason);

        var losing = WayCheck.Choose("sg-1", [Way("sg-1", 45, answered: 12), Way("vn-1-sg", 52)]);
        Check("The way in use losing a quarter of its Probes at 45 ms, a clean entry at 52 - moves to the clean one",
            losing.MoveTo == "vn-1-sg", losing.Reason);

        var lossyEntry = WayCheck.Choose("sg-1", [Way("sg-1", 60), Way("vn-1-sg", 30, answered: 12)]);
        Check("An entry at 30 ms that loses a quarter is not a better way than a clean 60 - stays",
            lossyEntry.MoveTo is null, lossyEntry.Reason);

        var dead = WayCheck.Choose("sg-1", [Way("sg-1", null, answered: 0), Way("vn-1-sg", 70)]);
        Check("The way in use answering nothing, the entry answering at 70 - moves", dead.MoveTo == "vn-1-sg", dead.Reason);

        var alone = WayCheck.Choose("sg-1", [Way("sg-1", 90), Way("vn-1-sg", null, answered: 0)]);
        Check("No other way answering - stays, whatever the way in use measures", alone.MoveTo is null, alone.Reason);

        var unmeasured = WayCheck.Choose("sg-1", [Way("vn-1-sg", 30)]);
        Check("The way in use not among the measured - stays (nothing to compare it with)", unmeasured.MoveTo is null, unmeasured.Reason);

        var back = WayCheck.Choose("vn-1-sg", [Way("sg-1", 44), Way("vn-1-sg", 50)], left: "sg-1");
        Check("On the entry, the road left earlier now 6 ms faster and clean - goes back (the return rule's 3 ms, not the margin)",
            back.MoveTo == "sg-1" && back.Return, back.Reason);

        var notYet = WayCheck.Choose("vn-1-sg", [Way("sg-1", 48), Way("vn-1-sg", 50)], left: "sg-1");
        Check("The road left only 2 ms faster - stays on the entry", notYet.MoveTo is null, notYet.Reason);

        var stillLosing = WayCheck.Choose("vn-1-sg", [Way("sg-1", 40, answered: 13), Way("vn-1-sg", 50)], left: "sg-1");
        Check("The road left faster but still losing packets - not gone back to", stillLosing.MoveTo is null, stillLosing.Reason);

        var noLeft = WayCheck.Choose("vn-1-sg", [Way("sg-1", 44), Way("vn-1-sg", 50)]);
        Check("Without a way left, 6 ms is inside the margin - stays", noLeft.MoveTo is null, noLeft.Reason);

        var oneLost = WayCheck.Choose("sg-1", [Way("sg-1", 50, answered: 15), Way("vn-1-sg", 48)]);
        Check("One Probe of sixteen lost on the way in use is noise - no penalty, stays", oneLost.MoveTo is null, oneLost.Reason);

        Check($"Probe rounds stay under relayd's 20 a second per session: 2 ways every {WayCheck.SpacingFor(2)} ms, 4 every {WayCheck.SpacingFor(4)} ms",
            Enumerable.Range(1, 8).All(w => 1000.0 * w / WayCheck.SpacingFor(w) <= WayCheck.MaxProbesPerSecond) && WayCheck.MaxProbesPerSecond < 20,
            string.Join(", ", Enumerable.Range(1, 8).Select(w => $"{w}: {1000.0 * w / WayCheck.SpacingFor(w):F1}/s")));

        // Property: never a move to a way that scores worse than the one in use, and never a move for less than the
        // return margin - over random numbers.
        var rng = new Random(20260930);
        string? broken = null;
        for (var i = 0; i < 50_000 && broken is null; i++)
        {
            var ids = new[] { "a", "b", "c" };
            var ways = ids.Select(id => Way(id, rng.Next(8) == 0 ? null : 5 + rng.NextDouble() * 150,
                rng.Next(4) == 0 ? rng.Next(WayCheck.Rounds + 1) : WayCheck.Rounds)).ToList();
            var current = ids[rng.Next(ids.Length)];
            var left = rng.Next(2) == 0 ? ids[rng.Next(ids.Length)] : null;
            var choice = WayCheck.Choose(current, ways, left);
            if (choice.MoveTo is not { } to) continue;
            var from = ways.First(w => w.Id == current);
            var dest = ways.First(w => w.Id == to);
            if (to == current) broken = $"#{i}: a move to the way in use";
            else if (dest.MedianMs is null) broken = $"#{i}: a move to a way that did not answer";
            else if (dest.Score > from.Score) broken = $"#{i}: {dest} scores worse than {from}";
            else if (from.MedianMs is { } f && dest.Score > from.Score - DoorSwitchPolicy.ReturnMargin && !(dest.Score <= from.Score && choice.Return))
                broken = $"#{i}: {dest} against {from} is less than any bar";
            else if (choice.Return && (to != left || dest.Loss.IsLossy)) broken = $"#{i}: a return to {to} (left {left}, {dest})";
        }
        Check("Property, 50,000 random ways: a move only ever goes to an answering way that scores better, by a bar",
            broken is null, broken ?? "");
    }
}
