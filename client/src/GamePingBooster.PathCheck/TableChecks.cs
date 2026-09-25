using GamePingBooster.Core.Paths;
using static GamePingBooster.PathCheck.Packets;

namespace GamePingBooster.PathCheck;

internal static partial class Program
{
    private static void TableChecks()
    {
        TableAgreesWithAScanOfEveryRange();
        HostBitsAreIgnoredAsWindowsIgnoresThem();
        NonRangesAreSkippedAndNamed();
        OneRangeWrittenTwiceInARegionIsFine();
        TwoRegionsSharingAnAddressAreRefused();
        DeltaForceAsProductionHasIt();
    }

    /// <summary>
    /// Random games against a brute-force scan: for every address at and around every range edge, and random ones
    /// besides, the table must name exactly the region a scan finds - or refuse the game when a scan finds two.
    /// </summary>
    private static void TableAgreesWithAScanOfEveryRange()
    {
        var rng = new Random(Seed + 20);
        string? failure = null;
        int usable = 0, refused = 0;
        for (var game = 0; game < 3_000 && failure is null; game++)
        {
            var regions = new List<(string, IReadOnlyList<string>)>();
            var ranges = new List<(uint Start, uint End, int Region)>();
            var regionCount = rng.Next(1, 7);
            // A small corner of the address space, so ranges collide often enough to exercise the refusal.
            var baseAddress = Addr(rng.Next(11, 200), rng.Next(256), 0, 0);
            for (var r = 0; r < regionCount; r++)
            {
                var cidrs = new List<string>();
                for (var c = rng.Next(1, 9); c > 0; c--)
                {
                    var bits = rng.Next(3) == 0 ? 32 : rng.Next(16, 33);
                    var address = baseAddress + (uint)rng.Next(0, 1 << 16);
                    var cidr = $"{Show(address)}/{bits}";
                    cidrs.Add(cidr);
                    RegionTable.TryParseCidr(cidr, out var start, out var end);
                    ranges.Add((start, end, r));
                }
                regions.Add(($"r{r}", cidrs));
            }

            var table = RegionTable.Build(regions);
            var crossOverlap = ranges.Any(a => ranges.Any(b => a.Region != b.Region && a.Start <= b.End && b.Start <= a.End));
            if (crossOverlap)
            {
                refused++;
                if (table.Usable) failure = $"game {game}: two regions overlap and the table did not refuse it";
                continue;
            }
            usable++;
            if (!table.Usable) failure = $"game {game}: refused with no overlap ({string.Join("; ", table.Problems)})";

            var probes = ranges.SelectMany(x => new[] { x.Start - 1, x.Start, x.End, x.End + 1 })
                .Concat(Enumerable.Range(0, 50).Select(_ => baseAddress + (uint)rng.Next(0, 1 << 16)));
            foreach (var address in probes)
            {
                var expected = ranges.Where(x => x.Start <= address && address <= x.End).Select(x => x.Region).Distinct().ToList();
                var want = expected.Count == 0 ? -1 : expected[0];
                if (table.Find(address) != want)
                {
                    failure = $"game {game}: {Show(address)} found in {table.Find(address)}, a scan says {want}";
                    break;
                }
            }
        }
        Check($"Lookups agree with a scan of every range ({usable:N0} games), overlaps refused ({refused:N0})",
            failure is null && usable > 0 && refused > 0, failure ?? "the random games did not cover both cases");
    }

    private static void HostBitsAreIgnoredAsWindowsIgnoresThem()
    {
        var table = RegionTable.Build([("hk", ["43.132.208.47/20"])]);
        Check("A range written with host bits covers its whole block",
            table.Find(Addr(43, 132, 208, 0)) == 0 && table.Find(Addr(43, 132, 223, 255)) == 0 &&
            table.Find(Addr(43, 132, 224, 0)) == -1 && table.Find(Addr(43, 132, 207, 255)) == -1,
            "the block edges came out wrong");
    }

    private static void NonRangesAreSkippedAndNamed()
    {
        var table = RegionTable.Build([("sg", ["bad", "1.2.3.4", "::1/64", "1.2.3.0/0", "1.2.3.0/33", "43.134.102.87/32"])]);
        Check("Anything that is not an IPv4 range is skipped and named - a default route is never a region",
            table.Usable && table.Problems.Count == 5 && table.Find(Addr(43, 134, 102, 87)) == 0 && table.Find(Addr(1, 2, 3, 4)) == -1,
            $"{table.Problems.Count} problems: {string.Join("; ", table.Problems)}");
    }

    private static void OneRangeWrittenTwiceInARegionIsFine()
    {
        var table = RegionTable.Build([("hk", ["150.109.64.0/20", "150.109.68.141/32", "150.109.64.0/21"])]);
        Check("Ranges overlapping inside one region are merged, not refused",
            table.Usable && table.Find(Addr(150, 109, 68, 141)) == 0 && table.Find(Addr(150, 109, 79, 255)) == 0,
            string.Join("; ", table.Problems));
    }

    private static void TwoRegionsSharingAnAddressAreRefused()
    {
        var table = RegionTable.Build([("hk", ["43.132.208.0/20"]), ("sg", ["43.132.208.47/32"])]);
        Check("A /32 in one region inside a /20 of another: refused, and both named",
            !table.Usable && table.Problems.Any(p => p.Contains("'hk'") && p.Contains("'sg'")),
            string.Join("; ", table.Problems));
    }

    /// <summary>Delta Force's five regions as imported on 2026-09-25, a sample of each.</summary>
    private static void DeltaForceAsProductionHasIt()
    {
        var table = RegionTable.Build([
            ("hk", ["43.132.208.0/20", "150.109.64.0/20", "43.154.80.0/20", "43.163.146.127/32", "119.28.3.57/32"]),
            ("sg", ["43.134.102.87/32", "129.226.203.119/32", "101.32.109.159/32"]),
            ("jkt", ["43.129.55.245/32", "43.133.137.180/32", "43.173.11.167/32"]),
            ("bkk", ["43.152.231.134/32"]),
            ("hcm", ["162.128.82.106/32", "216.27.173.3/32", "216.27.173.227/32"]),
        ]);
        Check("Delta Force as production has it: every sample in its own region, the HK landmark in none",
            table.Usable &&
            table.RegionIdAt(table.Find(Addr(43, 132, 208, 47))) == "hk" &&
            table.RegionIdAt(table.Find(Addr(129, 226, 203, 119))) == "sg" &&
            table.RegionIdAt(table.Find(Addr(43, 129, 55, 245))) == "jkt" &&
            table.RegionIdAt(table.Find(Addr(43, 152, 231, 134))) == "bkk" &&
            table.RegionIdAt(table.Find(Addr(216, 27, 173, 227))) == "hcm" &&
            table.Find(Addr(43, 132, 224, 20)) == -1,
            string.Join("; ", table.Problems));
    }

    // ------------------------------------------------------------ sticky destinations

    private sealed class FakeTunnel(string name)
    {
        public override string ToString() => name;
    }

    private static void StickyChecks()
    {
        var a = new FakeTunnel("a");
        var b = new FakeTunnel("b");
        var server = Addr(43, 163, 146, 127);
        var window = (long)StickyDestinations<FakeTunnel>.StickyFor.TotalMilliseconds;

        {
            var sticky = new StickyDestinations<FakeTunnel>();
            var first = sticky.Resolve(server, 0, b, static (t, _) => t);
            var later = sticky.Resolve(server, 1_000, a, static (t, _) => t);
            Check("The closure-free resolve sticks the same way", first == b && later == b, $"went to {later}");
            var planned = sticky.Resolve(Addr(1, 2, 3, 4), 1_000, a, static (t, d) => d == Addr(1, 2, 3, 4) ? t : null);
            Check("and passes the destination to the plan", planned == a);
        }
        {
            var sticky = new StickyDestinations<FakeTunnel>();
            var c = new FakeTunnel("c");
            sticky.Resolve(server, 0, () => a);
            sticky.Resolve(Addr(1, 2, 3, 4), 0, () => b);
            var moved = sticky.Retarget(a, c);
            Check("Retarget moves what was stuck to the old home onto the new one, and nothing else",
                moved == 1 && sticky.StuckTo(server, 10) == c && sticky.StuckTo(Addr(1, 2, 3, 4), 10) == b);
            Check("and keeps when it was last used", sticky.StuckTo(server, window) == c && sticky.StuckTo(server, window + 1) is null);
        }

        {
            var sticky = new StickyDestinations<FakeTunnel>();
            var first = sticky.Resolve(server, 0, () => a);
            var later = sticky.Resolve(server, 1_000, () => b);
            Check("A destination in use keeps its tunnel when the plan changes", first == a && later == a,
                $"went to {later}");
        }
        {
            var sticky = new StickyDestinations<FakeTunnel>();
            sticky.Resolve(server, 0, () => a);
            var fresh = sticky.Resolve(Addr(43, 163, 146, 128), 1_000, () => b);
            Check("A new destination takes the plan as it is now", fresh == b, $"went to {fresh}");
        }
        {
            var sticky = new StickyDestinations<FakeTunnel>();
            sticky.Resolve(server, 0, () => a);
            var afterSilence = sticky.Resolve(server, window + 1, () => b);
            Check($"Silent both ways for over {window / 1000} s: the destination takes the plan again", afterSilence == b,
                $"still on {afterSilence}");
        }
        {
            var sticky = new StickyDestinations<FakeTunnel>();
            sticky.Resolve(server, 0, () => a);
            for (var t = 60_000L; t <= 600_000; t += 60_000) sticky.Touch(server, a, t);
            var still = sticky.Resolve(server, 600_000 + window - 1, () => b);
            Check("Replies alone keep a destination stuck - a flow quiet one way is still a flow", still == a, $"went to {still}");
        }
        {
            var sticky = new StickyDestinations<FakeTunnel>();
            sticky.Touch(server, a, 0);
            sticky.Resolve(server, 0, () => b);
            sticky.Touch(server, a, 1_000);
            Check("A reply never sticks a destination, and one through another tunnel refreshes nothing",
                sticky.StuckTo(server, 1_000) == b && sticky.Resolve(server, window + 1, () => a) == a,
                "a reply decided where a destination goes");
        }
        {
            var sticky = new StickyDestinations<FakeTunnel>();
            sticky.Resolve(server, 0, () => a);
            sticky.Resolve(Addr(1, 1, 1, 1), 0, () => a);
            sticky.Resolve(Addr(2, 2, 2, 2), 0, () => b);
            var released = sticky.Release(a);
            Check("A tunnel that is gone releases its destinations, and only its own",
                released == 2 && sticky.CountStuckTo(a, 0) == 0 && sticky.CountStuckTo(b, 0) == 1 &&
                sticky.Resolve(server, 1, () => b) == b,
                $"released {released}");
        }
        {
            var sticky = new StickyDestinations<FakeTunnel>();
            var none = sticky.Resolve(server, 0, () => null);
            Check("Not through a tunnel: nothing is stuck", none is null && sticky.StuckTo(server, 0) is null, "something stuck");
        }
        {
            var sticky = new StickyDestinations<FakeTunnel>();
            sticky.Resolve(server, 0, () => a);
            sticky.Resolve(Addr(1, 1, 1, 1), window, () => a);
            var swept = sticky.Sweep(window + 1);
            Check("Sweep drops only what has expired", swept == 1 && sticky.StuckTo(Addr(1, 1, 1, 1), window + 1) == a, $"swept {swept}");
        }
        {
            // The uplink, three downlinks and a supervisor at once. What must never happen is a destination that
            // was stuck to one tunnel answering with another while it is still inside its window. The clock is a
            // counter bumped per packet, so the window is made long enough that nothing legitimately expires.
            var sticky = new StickyDestinations<FakeTunnel>(TimeSpan.FromDays(365));
            var tunnels = new[] { a, b, new FakeTunnel("c") };
            var moved = 0;
            var clock = 0L;
            var stop = DateTime.UtcNow.AddMilliseconds(400);
            var uplink = Task.Run(() =>
            {
                var rng = new Random(Seed + 21);
                var first = new Dictionary<uint, FakeTunnel>();
                while (DateTime.UtcNow < stop)
                {
                    var destination = (uint)rng.Next(0, 64);
                    var now = Interlocked.Increment(ref clock);
                    var got = sticky.Resolve(destination, now, () => tunnels[rng.Next(tunnels.Length)])!;
                    if (first.TryGetValue(destination, out var had) && !ReferenceEquals(had, got)) Interlocked.Increment(ref moved);
                    first[destination] = got;
                }
            });
            var downlinks = tunnels.Select((t, i) => Task.Run(() =>
            {
                var rng = new Random(Seed + 22 + i);
                while (DateTime.UtcNow < stop) sticky.Touch((uint)rng.Next(0, 64), t, Interlocked.Read(ref clock));
            })).ToArray();
            var sweeper = Task.Run(() =>
            {
                while (DateTime.UtcNow < stop) sticky.Sweep(Interlocked.Read(ref clock));
            });
            Task.WaitAll([uplink, sweeper, .. downlinks]);
            Check("Under an uplink, three downlinks and a sweeper at once, no destination changes tunnel", moved == 0,
                $"{moved} destinations changed tunnel inside their window");
        }
    }
}
