using GamePingBooster.Core.Paths;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Core.Quality;

namespace GamePingBooster.PathCheck;

internal static partial class Program
{
    private static readonly string[] Relays = ["vn-1", "vn-2", "hk-2", "sg-1", "sg-4"];
    private const string HomeRelay = "vn-2";

    private static PlannerOptions Options(bool allowDirect = false, int maxTunnels = 3) => new(allowDirect, maxTunnels, Relays);

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
        APathInUseStaysUnlessClearlyBeaten();
        APathSlowerThanHomeIsLeftWhateverTheMargin();
        DeltaForceFromHanoi();
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
        Check("Over the cap: hk-2 (30) and sg-4 (20) kept, sg (only sg-1, 15) goes home, jkt stays on sg-4",
            PathOf(plan, "hk") == RegionPath.Via("hk-2") && PathOf(plan, "jkt") == RegionPath.Via("sg-4") && PathOf(plan, "sg") == RegionPath.HomePath,
            string.Join(", ", plan.Select(d => $"{d.RegionId}={d.Path}")));
    }

    private static void OverTheCapTheRelaysAlreadyOpenStay()
    {
        var previous = new Dictionary<string, RegionPath> { ["sg"] = RegionPath.Via("sg-1") };
        var plan = RegionPlanner.Plan(HomeRelay,
        [
            Region("hk", 60, via: [("hk-2", 30)]),
            Region("sg", 60, via: [("sg-1", 45)]),
            Region("jkt", 60, via: [("sg-4", 40)]),
        ], Options(maxTunnels: 3), previous);
        Check("Over the cap: the relay the last plan already used stays before a bigger saving elsewhere",
            PathOf(plan, "sg") == RegionPath.Via("sg-1") && PathOf(plan, "hk") == RegionPath.Via("hk-2") && PathOf(plan, "jkt") == RegionPath.HomePath,
            string.Join(", ", plan.Select(d => $"{d.RegionId}={d.Path}")));
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
            regions.Add(new RegionMeasurement($"r{regions.Count}", rng.Next(6) != 0, home, via, direct));
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
            var options = Options(allowDirect: rng.Next(2) == 0, maxTunnels: rng.Next(1, 5));
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

                if (d.Path == RegionPath.HomePath || m.HomeMs is not { } home) continue;
                var chosen = d.Path.Kind == PathKind.Direct ? m.DirectMs!.Value : m.ViaRelayMs[d.Path.RelayId!];

                // G2, always: never a path measured slower than home.
                if (chosen > home) Broke("G2 never slower than home", $"{at} {d.RegionId}: {d.Path} {chosen:F1} vs home {home:F1}");

                // G2 and G8 in full, for a plan made from nothing: leaving home, and going direct, take the margin.
                if (fresh && !RescanScore.WorthMoving(home, chosen)) Broke("G2 leaves home only by the margin", $"{at} {d.RegionId}: {chosen:F1} vs home {home:F1}");
                if (fresh && d.Path.Kind == PathKind.Direct)
                {
                    // Against the best relay the plan could still use: when the cap is full, only the relays it kept.
                    var kept = plan.Where(x => x.Path.Kind == PathKind.Relay).Select(x => x.Path.RelayId!).ToHashSet();
                    var capFull = kept.Count >= options.MaxTunnels - 1;
                    var bestRelay = m.ViaRelayMs.Where(kv => kv.Key != HomeRelay && (!capFull || kept.Contains(kv.Key)))
                        .Select(kv => kv.Value).DefaultIfEmpty(double.NaN).Min();
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
            var options = Options(allowDirect: rng.Next(2) == 0, maxTunnels: rng.Next(1, 5));

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
                var heldMs = held.Kind switch
                {
                    PathKind.Direct => m.DirectMs,
                    PathKind.Relay => m.ViaRelayMs.TryGetValue(held.RelayId!, out var ms) ? ms : (double?)null,
                    _ => m.HomeMs,
                };
                if (held.Kind != PathKind.Home && heldMs is { } w && m.HomeMs is { } h && w > h) continue;   // G2

                failure = $"seed {Seed}, game {i}: {m.RegionId} went {first[r].Path} -> {held} -> {third[r].Path} on jitter ({third[r].Reason})";
                break;
            }
        }
        Check($"Property, 20,000 random plans: jitter never sends a region back where it came from ({movedOnce:N0} single moves seen)",
            failure is null, failure ?? "");
    }
}
