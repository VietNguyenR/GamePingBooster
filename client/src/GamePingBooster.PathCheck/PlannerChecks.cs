using GamePingBooster.Core.Paths;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Core.Quality;

namespace GamePingBooster.PathCheck;

internal static partial class Program
{
    private static readonly string[] Relays = ["vn-1", "vn-2", "hk-2", "sg-1", "sg-4"];
    private const string HomeRelay = "vn-2";

    private static PlannerOptions Options(bool allowDirect = false, int maxTunnels = 3, string? target = null) =>
        new(allowDirect, maxTunnels, Relays, target);

    private static RegionMeasurement Region(string id, double? home, double? direct = null, bool landmark = true,
        params (string Relay, double Ms)[] via) =>
        new(id, landmark, home, via.ToDictionary(v => v.Relay, v => v.Ms), direct);

    private static RegionPath PathOf(List<RegionDecision> plan, string region) => plan.Single(d => d.RegionId == region).Path;

    private static void PlannerChecks()
    {
        NoLandmarkFollowsHome();
        NoAnswerThroughHomeFollowsHome();
        AFasterRelayByTheMarginIsChosen();
        AFasterRelayInsideTheMarginIsNot();
        TheHomeRelayIsNeverACandidateOfItsOwn();
        DirectIsNeverChosenWhenNotAllowed();
        DirectBeatsHomeAndTheBestRelay();
        DirectInsideTheMarginOfARelayIsNot();
        OneTunnelMeansNoOtherRelay();
        OverTheCapTheBiggestSavingsStay();
        OverTheCapTheRelaysAlreadyOpenStay();
        OverTheCapABetterSetReplacesTheOpenOneByTheMargin();
        OverTheCapTheTargetRegionCountsDouble();
        TheTargetRegionDoesNotEvictABigSaving();
        ApexFromDaNang();
        APathInUseStaysUnlessClearlyBeaten();
        APathSlowerThanHomeIsLeftWhateverTheMargin();
        DeltaForceFromHanoi();
        AHomeLosingPacketsIsLeftForACleanRelay();
        AHomeLosingPacketsIsLeftForACleanRelayMuchSlower();
        ALossyRelayNeverBeatsACleanHomeOnSpeed();
        WhenEveryPathLosesTheFastestStillWins();
        APathInUseThatStartsLosingIsLeft();
        ALossyPathInUseIsLeftForAHomeThatIsNot();
        PlansHoldForEveryRandomInput();
        PlansDoNotFlapOnNoise();
    }

    private static void NoLandmarkFollowsHome()
    {
        var plan = RegionPlanner.Plan(HomeRelay, [Region("sg", 40, 5, landmark: false, ("sg-1", 2))], Options(allowDirect: true));
        Check("G3: a region without a landmark follows home, whatever else was measured", PathOf(plan, "sg") == RegionPath.HomePath,
            $"got {PathOf(plan, "sg")}");
    }

    private static void NoAnswerThroughHomeFollowsHome()
    {
        var plan = RegionPlanner.Plan(HomeRelay, [Region("hk", null, 10, true, ("hk-2", 30))], Options(allowDirect: true));
        Check("G3: no number through home means nothing to compare with - follows home", PathOf(plan, "hk") == RegionPath.HomePath,
            $"got {PathOf(plan, "hk")}");
    }

    private static void AFasterRelayByTheMarginIsChosen()
    {
        var plan = RegionPlanner.Plan(HomeRelay, [Region("hk", 34, via: ("hk-2", 28))], Options());
        Check("A relay 6 ms faster than home at 34 ms (margin 5): chosen", PathOf(plan, "hk") == RegionPath.Via("hk-2"),
            $"got {PathOf(plan, "hk")}");
    }

    private static void AFasterRelayInsideTheMarginIsNot()
    {
        var plan = RegionPlanner.Plan(HomeRelay, [Region("jp", 82, via: ("hk-2", 74.5))], Options());
        Check("A relay 7.5 ms faster than home at 82 ms (margin 8.2): not worth leaving home", PathOf(plan, "jp") == RegionPath.HomePath,
            $"got {PathOf(plan, "jp")}");
    }

    private static void TheHomeRelayIsNeverACandidateOfItsOwn()
    {
        var plan = RegionPlanner.Plan(HomeRelay, [Region("hk", 40, via: (HomeRelay, 10))], Options());
        Check("The home relay's own number in the candidates is ignored - it is home", PathOf(plan, "hk") == RegionPath.HomePath,
            $"got {PathOf(plan, "hk")}");
    }

    private static void DirectIsNeverChosenWhenNotAllowed()
    {
        var plan = RegionPlanner.Plan(HomeRelay, [Region("hcm", 34, direct: 20)], Options(allowDirect: false));
        Check("G8: direct 14 ms faster, but the game does not allow it - home", PathOf(plan, "hcm") == RegionPath.HomePath,
            $"got {PathOf(plan, "hcm")}");
    }

    private static void DirectBeatsHomeAndTheBestRelay()
    {
        var plan = RegionPlanner.Plan(HomeRelay, [Region("hcm", 34, direct: 20, true, ("vn-1", 31))], Options(allowDirect: true));
        Check("G8: direct 20 ms against home 34 and vn-1 31: direct", PathOf(plan, "hcm") == RegionPath.DirectPath,
            $"got {PathOf(plan, "hcm")}");
    }

    private static void DirectInsideTheMarginOfARelayIsNot()
    {
        // vn-1 at 24 is chosen over home at 34; direct at 21 does not beat it by 5.
        var plan = RegionPlanner.Plan(HomeRelay, [Region("hcm", 34, direct: 21, true, ("vn-1", 24))], Options(allowDirect: true));
        Check("G8: direct 21 against vn-1 24 - not by the margin, so vn-1", PathOf(plan, "hcm") == RegionPath.Via("vn-1"),
            $"got {PathOf(plan, "hcm")}");
    }

    private static void OneTunnelMeansNoOtherRelay()
    {
        var plan = RegionPlanner.Plan(HomeRelay, [Region("hk", 60, via: ("hk-2", 30)), Region("sg", 60, via: ("sg-1", 30))], Options(maxTunnels: 1));
        Check("MaxTunnels 1: every region on home", plan.All(d => d.Path == RegionPath.HomePath),
            string.Join(", ", plan.Select(d => $"{d.RegionId}={d.Path}")));
    }

    private static void OverTheCapTheBiggestSavingsStay()
    {
        var plan = RegionPlanner.Plan(HomeRelay,
        [
            Region("hk", 60, via: [("hk-2", 30), ("sg-1", 50)]),     // hk-2 saves 30
            Region("sg", 60, via: [("sg-1", 45), ("hk-2", 70)]),     // sg-1 saves 15
            Region("jkt", 60, via: [("sg-4", 40), ("sg-1", 42)]),    // sg-4 saves 20, sg-1 would save 18
        ], Options(maxTunnels: 3));
        // By set: {hk-2, sg-1} saves 30 + 15 + 18 = 63, {hk-2, sg-4} 30 + 0 + 20 = 50, {sg-1, sg-4} 10 + 15 + 20 = 45.
        // Relay by relay (the rule before 2026-09-29) kept hk-2 and sg-4 and sent sg home - 13 ms less in all.
        Check("Over the cap: the set saving most - hk-2 for hk, sg-1 for sg and jkt (63 ms against 50 relay by relay)",
            PathOf(plan, "hk") == RegionPath.Via("hk-2") && PathOf(plan, "sg") == RegionPath.Via("sg-1") && PathOf(plan, "jkt") == RegionPath.Via("sg-1"),
            string.Join(", ", plan.Select(d => $"{d.RegionId}={d.Path}")));
    }

    private static void OverTheCapTheRelaysAlreadyOpenStay()
    {
        var previous = new Dictionary<string, RegionPath> { ["sg"] = RegionPath.Via("sg-1") };
        var plan = RegionPlanner.Plan(HomeRelay,
        [
            Region("hk", 60, via: [("hk-2", 30)]),
            Region("sg", 60, via: [("sg-1", 45)]),
            Region("jkt", 60, via: [("sg-4", 42)]),
        ], Options(maxTunnels: 3), previous);
        // {hk-2, sg-1} is open and saves 45; {hk-2, sg-4} would move sg home (-15) and jkt to sg-4 (+18): 3 ms more,
        // against 11 ms of margin for the two regions it moves.
        Check("Over the cap: the set already open stays when another saves more by less than its moves' margins (48 vs 45)",
            PathOf(plan, "sg") == RegionPath.Via("sg-1") && PathOf(plan, "hk") == RegionPath.Via("hk-2") && PathOf(plan, "jkt") == RegionPath.HomePath,
            string.Join(", ", plan.Select(d => $"{d.RegionId}={d.Path}")));
    }

    private static void OverTheCapABetterSetReplacesTheOpenOneByTheMargin()
    {
        var previous = new Dictionary<string, RegionPath> { ["sg"] = RegionPath.Via("sg-1") };
        var plan = RegionPlanner.Plan(HomeRelay,
        [
            Region("hk", 60, via: [("hk-2", 30)]),
            Region("sg", 60, via: [("sg-1", 55)]),
            Region("jkt", 60, via: [("sg-4", 35)]),
        ], Options(maxTunnels: 3), previous);
        // Open {hk-2, sg-1} saves 35; {hk-2, sg-4} saves 55 - 20 ms more against 11 ms of margin for sg and jkt.
        // Before 2026-09-29 sg-1 stayed whatever sg-4 saved.
        Check("Over the cap: a set saving 20 ms more than the open one replaces it (sg-1 closes, jkt gets sg-4)",
            PathOf(plan, "hk") == RegionPath.Via("hk-2") && PathOf(plan, "jkt") == RegionPath.Via("sg-4") && PathOf(plan, "sg") == RegionPath.HomePath,
            string.Join(", ", plan.Select(d => $"{d.RegionId}={d.Path}")));
    }

    private static void OverTheCapTheTargetRegionCountsDouble()
    {
        var regions = new List<RegionMeasurement>
        {
            Region("hk", 60, via: [("hk-2", 30)]),                   // saves 30
            Region("jp", 100, via: [("hk-2", 90), ("vn-1", 75)]),   // vn-1 saves 25
            Region("sg", 50, via: [("sg-1", 40)]),                   // saves 10
        };
        var plain = RegionPlanner.Plan(HomeRelay, regions, Options(maxTunnels: 3));
        var target = RegionPlanner.Plan(HomeRelay, regions, Options(maxTunnels: 3, target: "sg"));
        Check("Over the cap with no target: hk-2 and vn-1 (55 ms) - sg goes home",
            PathOf(plain, "sg") == RegionPath.HomePath && PathOf(plain, "hk") == RegionPath.Via("hk-2") && PathOf(plain, "jp") == RegionPath.Via("vn-1"),
            string.Join(", ", plain.Select(d => $"{d.RegionId}={d.Path}")));
        Check("Over the cap with sg the player's region (counts double): sg-1 with hk-2 for hk and jp (60 against 55)",
            PathOf(target, "sg") == RegionPath.Via("sg-1") && PathOf(target, "hk") == RegionPath.Via("hk-2") && PathOf(target, "jp") == RegionPath.Via("hk-2"),
            string.Join(", ", target.Select(d => $"{d.RegionId}={d.Path}")));
    }

    private static void TheTargetRegionDoesNotEvictABigSaving()
    {
        // Found by PlansDoNotFlapOnNoise against a hard pin: one tunnel besides home, the target 5.6 ms better on sg-1,
        // another region 46 ms better on vn-1. Double 5.6 is still far below 46.
        var plan = RegionPlanner.Plan(HomeRelay,
        [
            Region("sg", 40.9, via: [("sg-1", 35.3)]),
            Region("hk", 75.6, via: [("vn-1", 29.7), ("sg-1", 68.3)]),
        ], Options(maxTunnels: 2, target: "sg"));
        Check("The player's region 5.6 ms better does not evict 46 ms on another region: hk on vn-1, sg home",
            PathOf(plan, "hk") == RegionPath.Via("vn-1") && PathOf(plan, "sg") == RegionPath.HomePath,
            string.Join(", ", plan.Select(d => $"{d.RegionId}={d.Path}")));
    }

    /// <summary>
    /// The plan that showed the old rule's lock-in, from prod (apex, 2026-09-28 17:21 UTC, player on Singapore): home vn-2,
    /// the previous plan on vn-1 for hk and vn-3 for jp and sg. Relay by relay, vn-1 and vn-3 stayed and sg took vn-3 at
    /// 47 ms with sg-1 at 38. By set the open pair saves 46.9 and {vn-3, sg-1} 50.0 - 3 ms more for moving two regions,
    /// so the open pair would still stay; with sg the player's region counting double, sg gains 18.6 against hk's 6.2.
    /// </summary>
    private static void ApexFromDaNang()
    {
        var previous = new Dictionary<string, RegionPath>
        {
            ["asia-hk"] = RegionPath.Via("vn-1"), ["asia-jp"] = RegionPath.Via("vn-3"), ["asia-sg"] = RegionPath.Via("vn-3"),
        };
        var regions = new List<RegionMeasurement>
        {
            Region("asia-hk", 51.1, via: [("hk", 54.4), ("hk-2", 47.9), ("sg-1", 69), ("sg-2", 78.3), ("sg-4", 73.5), ("vn-1", 44.9), ("vn-3", 54)]),
            Region("asia-jp", 125.3, via: [("hk", 108.2), ("hk-2", 94.5), ("sg-1", 111.5), ("sg-2", 119), ("sg-4", 118.3), ("vn-1", 104.5), ("vn-3", 97.9)]),
            Region("asia-sg", 60.5, via: [("hk", 96.9), ("hk-2", 126.9), ("sg-1", 37.9), ("sg-2", 42.6), ("sg-4", 38.1), ("vn-1", 50.9), ("vn-3", 47.2)]),
        };
        var without = RegionPlanner.Plan("vn-2", regions, Options(maxTunnels: 3), previous);
        var plan = RegionPlanner.Plan("vn-2", regions, Options(maxTunnels: 3, target: "asia-sg"), previous);
        Check("Apex from Da Nang, no target: the open pair (vn-1, vn-3) stays - {vn-3, sg-1} saves only 3 ms more",
            PathOf(without, "asia-sg") == RegionPath.Via("vn-3") && PathOf(without, "asia-hk") == RegionPath.Via("vn-1"),
            string.Join(", ", without.Select(d => $"{d.RegionId}={d.Path}")));
        Check("Apex from Da Nang, player on Singapore: sg on sg-1 (38), jp stays on vn-3, hk goes home",
            PathOf(plan, "asia-sg") == RegionPath.Via("sg-1") && PathOf(plan, "asia-jp") == RegionPath.Via("vn-3") &&
            PathOf(plan, "asia-hk") == RegionPath.HomePath,
            string.Join(", ", plan.Select(d => $"{d.RegionId}={d.Path} ({d.Reason})")));
    }

    private static void APathInUseStaysUnlessClearlyBeaten()
    {
        var previous = new Dictionary<string, RegionPath> { ["hk"] = RegionPath.Via("hk-2") };
        var plan = RegionPlanner.Plan(HomeRelay, [Region("hk", 40, via: [("hk-2", 30), ("sg-1", 27)])], Options(), previous);
        Check("A path in use stays when the new best beats it by less than the margin (30 vs 27)",
            PathOf(plan, "hk") == RegionPath.Via("hk-2"), $"got {PathOf(plan, "hk")}");
    }

    private static void APathSlowerThanHomeIsLeftWhateverTheMargin()
    {
        var previous = new Dictionary<string, RegionPath> { ["hk"] = RegionPath.Via("hk-2") };
        var plan = RegionPlanner.Plan(HomeRelay, [Region("hk", 40, via: [("hk-2", 42)])], Options(), previous);
        Check("G2 through hysteresis: a path now slower than home is left even inside the margin (42 vs 40)",
            PathOf(plan, "hk") == RegionPath.HomePath, $"got {PathOf(plan, "hk")}");
    }

    /// <summary>
    /// The case the design exists for. vn-2, hk-2 and sg-1 to the servers as measured on 2026-09-25, plus the
    /// capture PC's legs to each relay (vn-2 10 ms, hk-2 30, sg-1 43); vn-1's numbers are illustrative - it was
    /// not measured against Delta Force. Home is vn-2. A line that detours to HCM through Hong Kong is not
    /// modelled here; that is what direct against vn-x settles in the field.
    /// </summary>
    private static void DeltaForceFromHanoi()
    {
        var plan = RegionPlanner.Plan(HomeRelay,
        [
            Region("hk", 10 + 24.2, direct: 29, true, ("vn-1", 20 + 23), ("hk-2", 30 + 2.7), ("sg-1", 43 + 33.4)),
            Region("sg", 10 + 40.1, direct: 55, true, ("vn-1", 20 + 38), ("hk-2", 30 + 40.4), ("sg-1", 43 + 1.7)),
            Region("hcm", 10 + 22.1, direct: 33, true, ("vn-1", 20 + 20), ("hk-2", 30 + 58.8), ("sg-1", 43 + 37.7)),
            Region("jkt", 10 + 55.3, landmark: false),
        ], Options(allowDirect: true));
        Check("Delta Force from Hanoi: sg leaves vn-2 for sg-1 (44.7 vs 50.1), hk and hcm stay on vn-2, jkt has no landmark",
            PathOf(plan, "sg") == RegionPath.Via("sg-1") && PathOf(plan, "hk") == RegionPath.HomePath &&
            PathOf(plan, "hcm") == RegionPath.HomePath && PathOf(plan, "jkt") == RegionPath.HomePath,
            string.Join(", ", plan.Select(d => $"{d.RegionId}={d.Path} ({d.Reason})")));
    }

    // ------------------------------------------------------------ packet loss (RelayLoss)

    private static RegionMeasurement Lossy(RegionMeasurement region, bool home = false, params string[] via) =>
        region with { HomeLossy = home, LossyVia = via.ToHashSet(StringComparer.Ordinal) };

    /// <summary>
    /// The evening that made loss count, 2026-09-29: the owner's line lost 26% into Da Nang (home) and nothing into
    /// Ho Chi Minh (vn-3), and the plan kept hcm home because 34 ms is not 5 ms faster than 38.
    /// </summary>
    private static void AHomeLosingPacketsIsLeftForACleanRelay()
    {
        var plan = RegionPlanner.Plan(HomeRelay, [Lossy(Region("hcm", 38, via: [("vn-3", 34), ("sg-4", 88)]), home: true)], Options());
        Check("2026-09-29: hcm leaves a home losing packets (38 ms) for vn-3 (34 ms), inside the margin on speed alone",
            PathOf(plan, "hcm") == RegionPath.Via("vn-3"), string.Join(", ", plan.Select(d => $"{d.Path} ({d.Reason})")));
    }

    private static void AHomeLosingPacketsIsLeftForACleanRelayMuchSlower()
    {
        var plan = RegionPlanner.Plan(HomeRelay, [Lossy(Region("sg", 40, via: ("sg-1", 110)), home: true)], Options());
        Check($"A home losing packets (40 ms) is left for a clean relay 70 ms slower - under the {RelayLoss.PenaltyMs:F0} ms penalty",
            PathOf(plan, "sg") == RegionPath.Via("sg-1"), $"got {PathOf(plan, "sg")} ({plan[0].Reason})");
        Check("  and the decision reports what the path measured, not its score",
            plan[0].ChosenMs == 110 && plan[0].HomeMs == 40 && plan[0].ChosenScore == 110 && plan[0].HomeScore == 140,
            $"ms {plan[0].ChosenMs}/{plan[0].HomeMs}, score {plan[0].ChosenScore}/{plan[0].HomeScore}");
    }

    private static void ALossyRelayNeverBeatsACleanHomeOnSpeed()
    {
        var plan = RegionPlanner.Plan(HomeRelay, [Lossy(Region("hk", 60, via: [("hk-2", 20), ("sg-1", 55)]), false, "hk-2")], Options());
        Check("A relay losing packets at 20 ms does not take a region from a clean home at 60 ms",
            PathOf(plan, "hk") == RegionPath.HomePath, $"got {PathOf(plan, "hk")} ({plan[0].Reason})");
    }

    /// <summary>
    /// Both scores carry the penalty, so speed decides - but the margin is the score's, max(5 ms, 10%) of 140, so
    /// two lossy paths are held apart by more than two clean ones. The side to err on: neither is worth a move.
    /// </summary>
    private static void WhenEveryPathLosesTheFastestStillWins()
    {
        var plan = RegionPlanner.Plan(HomeRelay, [Lossy(Region("hk", 40, via: ("hk-2", 25)), true, "hk-2")], Options());
        Check("Every path losing packets: speed decides - hk-2 25 ms against home 40",
            PathOf(plan, "hk") == RegionPath.Via("hk-2"), $"got {PathOf(plan, "hk")} ({plan[0].Reason})");
        plan = RegionPlanner.Plan(HomeRelay, [Lossy(Region("hk", 40, via: ("hk-2", 30)), true, "hk-2")], Options());
        Check("  but by the margin of the score: hk-2 30 ms against home 40 stays home (14 ms needed)",
            PathOf(plan, "hk") == RegionPath.HomePath, $"got {PathOf(plan, "hk")} ({plan[0].Reason})");
    }

    private static void APathInUseThatStartsLosingIsLeft()
    {
        var previous = new Dictionary<string, RegionPath> { ["hk"] = RegionPath.Via("hk-2") };
        var plan = RegionPlanner.Plan(HomeRelay, [Lossy(Region("hk", 40, via: ("hk-2", 30)), false, "hk-2")], Options(), previous);
        Check("Hysteresis does not keep a path that has started losing packets when home has not",
            PathOf(plan, "hk") == RegionPath.HomePath, $"got {PathOf(plan, "hk")} ({plan[0].Reason})");
    }

    private static void ALossyPathInUseIsLeftForAHomeThatIsNot()
    {
        var previous = new Dictionary<string, RegionPath> { ["hk"] = RegionPath.Via("sg-1") };
        var plan = RegionPlanner.Plan(HomeRelay, [Lossy(Region("hk", 50, via: [("sg-1", 45), ("hk-2", 52)]), true, "sg-1")], Options(), previous);
        Check("Home and the path in use both losing: the region goes to the one clean relay, hk-2, though it is the slowest",
            PathOf(plan, "hk") == RegionPath.Via("hk-2"), $"got {PathOf(plan, "hk")} ({plan[0].Reason})");
    }

    // ------------------------------------------------------------ properties

    private static List<RegionMeasurement> RandomRegions(Random rng)
    {
        var regions = new List<RegionMeasurement>();
        for (var r = rng.Next(1, 7); r > 0; r--)
        {
            var home = rng.Next(8) == 0 ? (double?)null : 5 + rng.NextDouble() * 150;
            var via = new Dictionary<string, double>();
            foreach (var relay in Relays)
            {
                if (rng.Next(3) == 0) continue;
                via[relay] = home is { } h && rng.Next(2) == 0 ? h + (rng.NextDouble() - 0.6) * 30 : 5 + rng.NextDouble() * 150;
            }
            var direct = rng.Next(3) == 0 ? (double?)null : 5 + rng.NextDouble() * 150;
            // A path in five losing packets, home included, so every property below also holds on the score.
            var lossyVia = via.Keys.Where(_ => rng.Next(5) == 0).ToHashSet(StringComparer.Ordinal);
            regions.Add(new RegionMeasurement($"r{regions.Count}", rng.Next(6) != 0, home, via, direct, rng.Next(5) == 0, lossyVia));
        }
        return regions;
    }

    private static RegionPath RandomPath(Random rng) => rng.Next(4) switch
    {
        0 => RegionPath.HomePath,
        1 => RegionPath.DirectPath,
        _ => RegionPath.Via(Relays[rng.Next(Relays.Length)]),
    };

    /// <summary>
    /// Twenty thousand random games, with and without a previous plan, and every plan checked against the rules
    /// that must hold whatever was measured.
    /// </summary>
    private static void PlansHoldForEveryRandomInput()
    {
        var rng = new Random(Seed + 30);
        var broken = new Dictionary<string, string>();
        void Broke(string rule, string detail) => broken.TryAdd(rule, detail);

        for (var i = 0; i < 20_000; i++)
        {
            var regions = RandomRegions(rng);
            var options = Options(allowDirect: rng.Next(2) == 0, maxTunnels: rng.Next(1, 5),
                target: rng.Next(2) == 0 ? null : regions[rng.Next(regions.Count)].RegionId);
            var previous = rng.Next(2) == 0 ? null : regions.ToDictionary(r => r.RegionId, _ => RandomPath(rng));
            var plan = RegionPlanner.Plan(HomeRelay, regions, options, previous);
            var again = RegionPlanner.Plan(HomeRelay, regions, options, previous);
            var at = $"seed {Seed}, game {i}";

            if (!plan.Select(d => (d.RegionId, d.Path)).SequenceEqual(again.Select(d => (d.RegionId, d.Path)))) Broke("deterministic", at);
            if (plan.Count != regions.Count) Broke("one decision per region", at);

            var relays = plan.Where(d => d.Path.Kind == PathKind.Relay).Select(d => d.Path.RelayId).Distinct().Count();
            if (relays > options.MaxTunnels - 1) Broke("cap", $"{at}: {relays} relays besides home with MaxTunnels {options.MaxTunnels}");

            foreach (var d in plan)
            {
                var m = regions.Single(r => r.RegionId == d.RegionId);
                var fresh = previous is null;

                if ((!m.HasLandmark || m.HomeMs is null) && d.Path != RegionPath.HomePath) Broke("G3 unmeasurable stays home", $"{at} {d.RegionId}");
                if (d.Path.Kind == PathKind.Direct && !options.AllowDirect) Broke("G8 direct only when allowed", $"{at} {d.RegionId}");
                if (d.Path.Kind == PathKind.Relay && d.Path.RelayId == HomeRelay) Broke("home is not a relay path", $"{at} {d.RegionId}");
                if (d.Path.Kind == PathKind.Relay && !m.ViaRelayMs.ContainsKey(d.Path.RelayId!)) Broke("a relay chosen has a number", $"{at} {d.RegionId}");

                if (d.Path == RegionPath.HomePath || m.HomeMs is not { } homeMs) continue;
                // Scores throughout: the round trip, plus the penalty for a path losing packets. Direct has none.
                var home = RelayLoss.Score(homeMs, m.HomeLossy);
                var chosen = d.Path.Kind == PathKind.Direct
                    ? m.DirectMs!.Value
                    : RelayLoss.Score(m.ViaRelayMs[d.Path.RelayId!], m.IsLossyVia(d.Path.RelayId!));

                // G2, always: never a path that scores worse than home.
                if (chosen > home) Broke("G2 never slower than home", $"{at} {d.RegionId}: {d.Path} {chosen:F1} vs home {home:F1}");

                // G2 and G8 in full, for a plan made from nothing: leaving home, and going direct, take the margin.
                if (fresh && !RescanScore.WorthMoving(home, chosen)) Broke("G2 leaves home only by the margin", $"{at} {d.RegionId}: {chosen:F1} vs home {home:F1}");
                if (fresh && d.Path.Kind == PathKind.Direct)
                {
                    // Against the best relay the plan could still use: when the cap is full, only the relays it kept.
                    var kept = plan.Where(x => x.Path.Kind == PathKind.Relay).Select(x => x.Path.RelayId!).ToHashSet();
                    var capFull = kept.Count >= options.MaxTunnels - 1;
                    var bestRelay = m.ViaRelayMs.Where(kv => kv.Key != HomeRelay && (!capFull || kept.Contains(kv.Key)))
                        .Select(kv => RelayLoss.Score(kv.Value, m.IsLossyVia(kv.Key))).DefaultIfEmpty(double.NaN).Min();
                    if (!double.IsNaN(bestRelay) && !RescanScore.WorthMoving(bestRelay, chosen))
                        Broke("G8 direct beats the best relay by the margin", $"{at} {d.RegionId}: direct {chosen:F1} vs relay {bestRelay:F1}");
                }
            }
        }

        var rules = new[]
        {
            "deterministic", "one decision per region", "cap", "G3 unmeasurable stays home", "G8 direct only when allowed",
            "home is not a relay path", "a relay chosen has a number", "G2 never slower than home",
            "G2 leaves home only by the margin", "G8 direct beats the best relay by the margin",
        };
        foreach (var rule in rules)
        {
            Check($"Property, 20,000 random plans: {rule}", !broken.ContainsKey(rule), broken.GetValueOrDefault(rule, ""));
        }
    }

    /// <summary>
    /// No ping-pong. Any threshold flips when a number sits on it, so jitter may move a region once; what it must
    /// never do is move it BACK. Plan the numbers, plan them again moved by up to a quarter of the margin (an
    /// ordinary evening), then plan the original numbers again: the third plan must keep what the second chose.
    /// The one exception is G2 itself - a path in use that measures slower than home is always left.
    /// </summary>
    private static void PlansDoNotFlapOnNoise()
    {
        var rng = new Random(Seed + 31);
        string? failure = null;
        var movedOnce = 0;
        for (var i = 0; i < 20_000 && failure is null; i++)
        {
            var regions = RandomRegions(rng);
            var options = Options(allowDirect: rng.Next(2) == 0, maxTunnels: rng.Next(1, 5),
                target: rng.Next(2) == 0 ? null : regions[rng.Next(regions.Count)].RegionId);

            double Jitter(double ms) => ms + (rng.NextDouble() * 2 - 1) * RelayPaths.HelpMargin(ms) / 4;
            var noisy = regions.Select(r => r with
            {
                HomeMs = r.HomeMs is { } h ? Jitter(h) : null,
                DirectMs = r.DirectMs is { } d ? Jitter(d) : null,
                ViaRelayMs = r.ViaRelayMs.ToDictionary(kv => kv.Key, kv => Jitter(kv.Value)),
            }).ToList();

            var first = RegionPlanner.Plan(HomeRelay, regions, options);
            var second = RegionPlanner.Plan(HomeRelay, noisy, options, first.ToDictionary(d => d.RegionId, d => d.Path));
            var third = RegionPlanner.Plan(HomeRelay, regions, options, second.ToDictionary(d => d.RegionId, d => d.Path));

            for (var r = 0; r < regions.Count; r++)
            {
                if (second[r].Path != first[r].Path) movedOnce++;
                if (third[r].Path == second[r].Path) continue;

                var m = regions[r];
                var held = second[r].Path;
                var heldScore = held.Kind switch
                {
                    PathKind.Direct => m.DirectMs,
                    PathKind.Relay => m.ViaRelayMs.TryGetValue(held.RelayId!, out var ms)
                        ? RelayLoss.Score(ms, m.IsLossyVia(held.RelayId!))
                        : (double?)null,
                    _ => m.HomeMs,
                };
                if (held.Kind != PathKind.Home && heldScore is { } w && m.HomeMs is { } h && w > RelayLoss.Score(h, m.HomeLossy)) continue;   // G2

                failure = $"seed {Seed}, game {i}: {m.RegionId} went {first[r].Path} -> {held} -> {third[r].Path} on jitter ({third[r].Reason})";
                break;
            }
        }
        Check($"Property, 20,000 random plans: jitter never sends a region back where it came from ({movedOnce:N0} single moves seen)",
            failure is null, failure ?? "");
    }
}
