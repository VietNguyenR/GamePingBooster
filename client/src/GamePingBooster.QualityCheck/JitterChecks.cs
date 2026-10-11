using GamePingBooster.Core.Quality;

namespace GamePingBooster.QualityCheck;

/// <summary>
/// Jitter: a path that swings counts slower than its median (2026-10-11). The shape is vn-2's on FPT on 2026-10-10:
/// a median of 38 ms with a tenth of its round trips at 50-58 (p50 38, p90 50), beside ways that sit within a
/// millisecond and a half of their own median.
/// </summary>
internal static partial class Program
{
    /// <summary>vn-2's swing, ten quarter seconds at a time: p50 38, p90 50, one in ten at 58.</summary>
    private static readonly double[] Vn2Swing = [37, 38, 38, 39, 38, 37, 44, 50, 58, 39];

    private static double? Swinging(int i) => Vn2Swing[i % Vn2Swing.Length];

    private static void JitterChecks()
    {
        Check("Spread is the 90th percentile less the median", Jitter.Spread(Vn2Swing) == 12, $"{Jitter.Spread(Vn2Swing)}");
        Check("  ...and nothing from fewer than six samples", Jitter.Spread([40, 41, 60, 40, 40]) is null, "a spread");

        var excess = Jitter.OverCalmest([12, 1, null, 2.5]);
        Check("Counted over the calmest path measured with it; unknown stays unknown",
            excess is [11, 0, null, 1.5], string.Join(", ", excess));
        Check("No penalty inside the 3 ms allowance", Jitter.Penalty(3) == 0 && Jitter.Penalty(null) == 0, $"{Jitter.Penalty(3)}");
        Check("  ...twice the rest past it: 11 ms over the calmest counts 16 ms", Jitter.Penalty(11) == 16, $"{Jitter.Penalty(11)}");

        // The relay list and a connect: sg-5 59 direct, vn-2-sg5 49 (the 2026-10-10 11:18 list line), vn-2 swinging 8 ms
        // more than sg-5. The median alone took the entry; swinging, it is no faster.
        DoorReading Reading(string id, double ms, double? jitter) => new(id, id == "sg-5", ms, new PingLoss(20, 20), jitter);
        var list = DoorChoice.Pick([Reading("sg-5", 59, 0), Reading("vn-2-sg5", 49, 8)]);
        Check("The list: vn-2-sg5 10 ms faster at the median but swinging 8 ms more - sg-5's own address",
            list.Door?.DoorId == "sg-5", list.Reason);
        var steadyEntry = DoorChoice.Pick([Reading("sg-5", 59, 0), Reading("vn-5-sg5", 49, 1)]);
        Check("  ...a steady entry 10 ms faster is still taken", steadyEntry.Door?.DoorId == "vn-5-sg5", steadyEntry.Reason);

        // Between matches on another tunnel's ways (WayCheck), the same numbers.
        WaySample Sample(string id, double ms, double? jitter) => new(id, ms, new PingLoss(WayCheck.Rounds, WayCheck.Rounds), jitter);
        var onVn2 = WayCheck.Choose("vn-2-sg5", [Sample("vn-2-sg5", 38, 9), Sample("sg-5", 50, 0)]);
        Check("WayCheck: on vn-2-sg5 at 38 swinging 9 ms, sg-5 steady at 50 - 50 against 50, stays",
            onVn2.MoveTo is null, onVn2.Reason);
        var toSteady = WayCheck.Choose("vn-2-sg5", [Sample("vn-2-sg5", 38, 9), Sample("vn-5-sg5", 39, 0)]);
        Check("  ...vn-5-sg5 steady at 39 - 39 against 50, past WayCheck's 10 ms, moves", toSteady.MoveTo == "vn-5-sg5", toSteady.Reason);
        var noReturn = WayCheck.Choose("vn-5-sg5", [Sample("vn-5-sg5", 45, 0), Sample("vn-2-sg5", 38, 9)], left: "vn-2-sg5");
        Check("  ...and the way left is not gone back to while it swings", noReturn.MoveTo is null, noReturn.Reason);

        JitterMemoryChecks();
        TheSwingingWayIsLeftForASteadyOne();
        TheSwingingWayIsKeptAgainstASlowerSteadyOne();
        TheSwingIsNotTradedForAHigherTail();
        WhatEveryProbedWayDoesTogetherIsNotASwing();
        TheSteadyRuleDoesNotMoveOntoASwing();
        TheWorseRuleDoesNotMoveOntoASwing();
    }

    private static void JitterMemoryChecks()
    {
        var memory = new JitterMemory();
        var at = new DateTimeOffset(2026, 10, 10, 11, 30, 0, TimeSpan.Zero);
        memory.OnNetwork("nic|192.168.1.1");
        var (remembered, _) = memory.Observe(new Dictionary<string, double> { ["vn-2-sg5"] = 9, ["sg-5"] = 0, ["vn-5-sg5"] = 2 }, at);
        Check("Memory: only the way that swings past the allowance is remembered", remembered is ["vn-2-sg5"], string.Join(", ", remembered));
        Check("  ...and raises a calm short measurement of it", memory.Floor("vn-2-sg5", 1, at.AddMinutes(30)) == 9,
            $"{memory.Floor("vn-2-sg5", 1, at.AddMinutes(30))}");
        Check("  ...but never lowers a swing measured now", memory.Floor("vn-2-sg5", 12, at) == 12, $"{memory.Floor("vn-2-sg5", 12, at)}");
        Check("  ...for two hours", memory.ExcessOf("vn-2-sg5", at + JitterMemory.KeepFor) is null, "still remembered");

        var (_, forgotten) = memory.Observe(new Dictionary<string, double> { ["vn-2-sg5"] = 1 }, at.AddMinutes(5));
        Check("  ...forgotten as soon as it is seen steady", forgotten is ["vn-2-sg5"] && memory.ExcessOf("vn-2-sg5", at.AddMinutes(5)) is null,
            string.Join(", ", forgotten));

        memory.Observe(new Dictionary<string, double> { ["vn-2-sg5"] = 9 }, at);
        memory.OnNetwork("nic|192.168.1.1");
        Check("  ...kept on the same network", memory.ExcessOf("vn-2-sg5", at) == 9, "forgotten");
        memory.OnNetwork("nic|10.0.0.1");
        Check("  ...and forgotten on another", memory.ExcessOf("vn-2-sg5", at) is null, "still remembered");
    }

    /// <summary>On vn-2-sg5 swinging, sg-5 steady at 50 and vn-5-sg5 at 43 - the 2026-10-10 midday in one relay.</summary>
    private static void TheSwingingWayIsLeftForASteadyOne()
    {
        var w = new Ways { Current = "vn-2-sg5", Others = ["sg-5", "vn-5-sg5"] };
        w.Run(DoorSwitchPolicy.JitterWindowTicks - 1, Swinging, _ => 50, _ => 43);
        Check("Swinging at a 38 ms median: nothing moves before two minutes", w.Decisions.Count == 0, $"{w.Decisions.Count} decision(s)");
        var jitter = w.Policy.WayJitter(DoorSwitchPolicy.JitterWindowTicks - 1);
        Check("  ...by when vn-2-sg5 is seen to swing and the others not",
            jitter is not null && Jitter.Penalty(jitter["vn-2-sg5"]) > 0 && Jitter.Penalty(jitter["sg-5"]) == 0 &&
            Jitter.Penalty(jitter["vn-5-sg5"]) == 0,
            jitter is null ? "nothing" : string.Join(", ", jitter.Select(p => $"{p.Key} {p.Value:F1}")));
        w.Run(1, Swinging, _ => 50, _ => 43);
        Check("  ...then moves to the steady 43 (38 + its swing is more than 43 + 5)",
            w.Decisions is [{ Jitter: true, To: "vn-5-sg5" }],
            string.Join(", ", w.Decisions.Select(d => $"{d.To} jitter={d.Jitter} steady={d.Steady}")));
    }

    /// <summary>
    /// Against sg-5 steady at 50, what the swing costs decides: the customer's vn-2-sg5 lobby (p50 37.5, p95 48.8 -
    /// p90 about 46) is kept; the harsher swing above, one in ten at 58, is not.
    /// </summary>
    private static void TheSwingingWayIsKeptAgainstASlowerSteadyOne()
    {
        double[] mild = [37, 38, 38, 39, 38, 37, 41, 46, 52, 39];
        var kept = new Ways { Current = "vn-2-sg5", Others = ["sg-5"] };
        kept.Run(DoorSwitchPolicy.CooldownTicks, i => mild[i % mild.Length], _ => 50);
        Check("Swinging at 38 (p90 46) against a steady 50: still faster once the swing is counted - stays", kept.Decisions.Count == 0,
            string.Join(", ", kept.Decisions.Select(d => $"{d.To} jitter={d.Jitter}")));

        var left = new Ways { Current = "vn-2-sg5", Others = ["sg-5"] };
        left.Run(DoorSwitchPolicy.CooldownTicks, Swinging, _ => 50);
        Check("  ...at 38 (p90 50, one in ten at 58) it counts 55 against 50 - moves", left.Decisions is [{ Jitter: true, To: "sg-5" }],
            string.Join(", ", left.Decisions.Select(d => $"{d.To} jitter={d.Jitter}")));
    }

    /// <summary>
    /// 2026-10-10 evening: the way in use steady at 43.8, and every probed way delayed together now and then - a PC that
    /// stalled. What they share is no road's swing.
    /// </summary>
    private static void WhatEveryProbedWayDoesTogetherIsNotASwing()
    {
        var w = new Ways { Current = "vn-5-sg", Others = ["sg-1", "vn-1-sg", "vn-3-sg"] };
        double Stalled(int i, double ms) => i % 10 is 7 or 8 ? ms + 25 : ms;
        w.Run(DoorSwitchPolicy.CooldownTicks, _ => 43.5, i => Stalled(i, 56.5), i => Stalled(i, 48), i => Stalled(i, 60.5));
        var jitter = w.Policy.WayJitter();
        Check("Every probed way delayed together: no way counted as swinging",
            jitter is not null && jitter.Values.All(e => Jitter.Penalty(e) == 0),
            jitter is null ? "nothing" : string.Join(", ", jitter.Select(p => $"{p.Key} {p.Value:F1}")));
        Check("  ...and nothing moves", w.Decisions.Count == 0, $"{w.Decisions.Count} decision(s)");
    }

    /// <summary>On sg-2 steady at 52; vn-2-sg2 faster at the median by 8, its tail no worse, but swinging.</summary>
    private static void TheSteadyRuleDoesNotMoveOntoASwing()
    {
        double[] swing = [43, 44, 44, 44, 45, 44, 43, 50, 51, 44];
        var w = new Ways { Current = "sg-2", Others = ["vn-2-sg2"] };
        w.Run(DoorSwitchPolicy.CooldownTicks, _ => 52, i => swing[i % swing.Length]);
        Check("8 ms faster at the median, its tail no worse, but swinging 6 ms more: the steady rule does not move onto it",
            w.Decisions.Count == 0, string.Join(", ", w.Decisions.Select(d => $"{d.To} at {d.TickIndex} steady={d.Steady}")));
    }

    /// <summary>
    /// On sg-5 steady at 50 - where the jitter rule leaves a player - with vn-2-sg5 12 ms faster in eight quarter seconds
    /// in ten: "worse" by its 10 ms bar, but no faster once its swing is counted. Without the score it bounced him back.
    /// </summary>
    private static void TheWorseRuleDoesNotMoveOntoASwing()
    {
        double[] swing = [37, 38, 38, 39, 38, 37, 38, 50, 58, 39];
        var w = new Ways { Current = "sg-5", Others = ["vn-2-sg5"] };
        w.Run(DoorSwitchPolicy.CooldownTicks, _ => 50, i => swing[i % swing.Length]);
        Check("Steady at 50, a way 12 ms faster most of the time but swinging to 58: the worse rule does not move onto it",
            w.Decisions.Count == 0, string.Join(", ", w.Decisions.Select(d => $"{d.To} at {d.TickIndex} steady={d.Steady}")));

        var lag = new Ways { Current = "sg-5", Others = ["vn-2-sg5"] };
        lag.Run(DoorSwitchPolicy.WindowTicks, _ => 90, i => swing[i % swing.Length]);
        Check("  ...but a lag on the way in use, at 90, still leaves for it", lag.Decisions is [{ To: "vn-2-sg5" }],
            $"{lag.Decisions.Count} decision(s)");
    }

    /// <summary>
    /// A way at 30 that swings to 50-56 one quarter second in five counts 64 against a steady 57 - but 57 every quarter
    /// second is no lower a tail than the swing's 56. On prod spike records 2026-10-04..11 moves like this were worse at
    /// the 95th percentile after 60-75% of the time, and 30 ms slower at the median when the swing was large.
    /// </summary>
    private static void TheSwingIsNotTradedForAHigherTail()
    {
        double[] swing = [30, 30, 30, 30, 30, 30, 30, 30, 50, 56];
        var kept = new Ways { Current = "sg-3", Others = ["vn-1-sg-3"] };
        kept.Run(DoorSwitchPolicy.CooldownTicks, i => swing[i % swing.Length], _ => 57);
        Check("Swinging at 30 (p95 56) against a steady 57: scores 64 against 57, but no lower tail - stays",
            kept.Decisions.Count == 0, string.Join(", ", kept.Decisions.Select(d => $"{d.To} jitter={d.Jitter}")));

        var left = new Ways { Current = "sg-3", Others = ["vn-1-sg-3"] };
        left.Run(DoorSwitchPolicy.CooldownTicks, i => swing[i % swing.Length], _ => 52);
        Check("  ...against a steady 52 (tail 4 ms lower) - moves", left.Decisions is [{ Jitter: true, To: "vn-1-sg-3" }],
            string.Join(", ", left.Decisions.Select(d => $"{d.To} jitter={d.Jitter}")));
    }
}
