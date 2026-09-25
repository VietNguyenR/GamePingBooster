using GamePingBooster.Core.Paths;

namespace GamePingBooster.PathCheck;

internal static partial class Program
{
    /// <summary>
    /// <see cref="MatchCarrier{TTunnel}"/> - which tunnel the in-game ping, the spike recorder and the status follow
    /// (docs/MULTI-TUNNEL.md 5.8). Driven one second at a time, as the in-game ping loop drives it.
    /// </summary>
    private static void CarrierChecks()
    {
        var home = new FakeTunnel("home");
        var hk = new FakeTunnel("hk-2");

        {
            var run = new CarrierRun(home);
            var answer = run.Step(0, (hk, 0));
            Check("Home carries until something else carries a match", ReferenceEquals(answer, home), $"got {answer}");
        }
        {
            var run = new CarrierRun(home);
            run.Seconds(1, 0, (hk, 0));
            var early = run.Seconds(2, 0, (hk, 30));
            var held = run.Seconds(1, 0, (hk, 30));
            Check("A match on another tunnel takes it over after 3 s, not before",
                ReferenceEquals(early, home) && ReferenceEquals(held, hk), $"after 2 s {early}, after 3 s {held}");
        }
        {
            var run = new CarrierRun(home);
            run.Seconds(1, 0, (hk, 0));
            run.Seconds(5, 0, (hk, 30));
            var stalled = run.Seconds(10, 0, (hk, 0));
            Check("A stall on the carrier does not move it", ReferenceEquals(stalled, hk), $"went to {stalled}");
            var before = run.Seconds(49, 0, (hk, 0));
            var after = run.Seconds(1, 0, (hk, 0));
            Check("It goes back to home after 60 s of nothing - past the recorder's end of a match",
                ReferenceEquals(before, hk) && ReferenceEquals(after, home), $"at 59 s {before}, at 60 s {after}");
        }
        {
            var run = new CarrierRun(home);
            run.Seconds(1, 0, (hk, 0));
            run.Seconds(5, 0, (hk, 30));
            var gone = run.Step(1, []);
            Check("A tunnel that closes hands over to home at once", ReferenceEquals(gone, home), $"stayed on {gone}");
        }
        {
            var run = new CarrierRun(home);
            run.Seconds(1, 0, (hk, 0));
            var trickle = run.Seconds(120, 0, (hk, 1));
            Check("A lobby trickle never takes it over", ReferenceEquals(trickle, home), $"went to {trickle}");
        }
        {
            var run = new CarrierRun(home);
            run.Seconds(1, 0, (hk, 0));
            for (var i = 0; i < 20; i++)
            {
                run.Seconds(2, 0, (hk, 40));
                run.Seconds(1, 0, (hk, 0));
            }
            Check("Bursts shorter than 3 s never take it over", ReferenceEquals(run.Last, home), $"went to {run.Last}");
        }
        {
            var run = new CarrierRun(home);
            run.Seconds(1, 0, (hk, 0));
            run.Seconds(300, 0, (hk, 30));
            run.Seconds(20, 0, (hk, 0));
            var next = run.Seconds(3, 30, (hk, 0));
            Check("The next match on home takes it back before the 60 s are up", ReferenceEquals(next, home), $"stayed on {next}");
        }
        {
            var run = new CarrierRun(home);
            run.Seconds(1, 0, (hk, 0));
            run.Seconds(300, 60, (hk, 8));
            Check("A tunnel carrying less than the carrier does not take it", ReferenceEquals(run.Last, home), $"went to {run.Last}");
        }
        {
            var carrier = new MatchCarrier<FakeTunnel>();
            var replaced = new FakeTunnel("home after a reconnect");
            carrier.Update(0, home, 0, []);
            var answer = carrier.Update(1_000, replaced, 0, []);
            Check("A reconnect's new home is the carrier, not the old one", ReferenceEquals(answer, replaced), $"got {answer}");
        }

        CarrierFollowsItsRules();
    }

    /// <summary>
    /// Random traffic on three tunnels, checked every second against the rules restated independently: the answer is
    /// always an open tunnel; a tunnel other than home becomes the carrier only after 3 s at a match's rate; and the
    /// carrier, while open, is left only for a tunnel carrying more, or for home after 60 s of carrying nothing.
    /// </summary>
    private static void CarrierFollowsItsRules()
    {
        var random = new Random(Seed);
        var home = new FakeTunnel("home");
        var tunnels = new[] { new FakeTunnel("a"), new FakeTunnel("b") };
        var carrier = new MatchCarrier<FakeTunnel>();
        var counts = new Dictionary<FakeTunnel, long>(ReferenceEqualityComparer.Instance) { [home] = 0, [tunnels[0]] = 0, [tunnels[1]] = 0 };
        var busyFor = new Dictionary<FakeTunnel, int>(ReferenceEqualityComparer.Instance) { [home] = 0, [tunnels[0]] = 0, [tunnels[1]] = 0 };
        var rates = new Dictionary<FakeTunnel, int>(ReferenceEqualityComparer.Instance);
        var open = new HashSet<FakeTunnel>(ReferenceEqualityComparer.Instance) { tunnels[0], tunnels[1] };
        var quietFor = 0;
        FakeTunnel? previous = null;
        string? failure = null;

        // Long stretches of one state, the way matches and lobbies are, with the odd burst and tunnel closing.
        var plan = new Dictionary<FakeTunnel, (int Rate, int Left)>(ReferenceEqualityComparer.Instance);
        for (var second = 1; second <= 20_000 && failure is null; second++)
        {
            foreach (var t in counts.Keys)
            {
                if (!plan.TryGetValue(t, out var p) || p.Left == 0)
                {
                    var roll = random.Next(10);
                    p = (roll < 5 ? 0 : roll < 7 ? random.Next(1, 5) : random.Next(5, 150), random.Next(1, 90));
                }
                plan[t] = (p.Rate, p.Left - 1);
                rates[t] = p.Rate;
            }
            if (random.Next(400) == 0)
            {
                var t = tunnels[random.Next(2)];
                if (!open.Remove(t)) open.Add(t);
            }

            foreach (var t in counts.Keys) counts[t] += rates[t];
            foreach (var t in counts.Keys) busyFor[t] = rates[t] >= MatchCarrier<FakeTunnel>.MatchPacketsPerSecond ? busyFor[t] + 1 : 0;

            var others = open.Select(t => (t, counts[t])).ToList();
            var answer = carrier.Update(second * 1_000L, home, counts[home], others);

            if (!ReferenceEquals(answer, home) && !open.Contains(answer)) failure = $"second {second}: {answer} is not open";
            else if (previous is not null && !ReferenceEquals(answer, previous))
            {
                var stillOpen = ReferenceEquals(previous, home) || open.Contains(previous);
                var tookOver = busyFor[answer] >= 3 && rates[answer] > rates[previous];
                var wentHome = ReferenceEquals(answer, home) && (!stillOpen || quietFor >= 59);
                if (!ReferenceEquals(answer, home) && busyFor[answer] < 3)
                {
                    failure = $"second {second}: {answer} took over after {busyFor[answer]} s at a match's rate";
                }
                else if (stillOpen && !tookOver && !wentHome)
                {
                    failure = $"second {second}: left {previous} (quiet {quietFor} s, {rates[previous]}/s) for {answer} ({rates[answer]}/s)";
                }
            }

            if (!ReferenceEquals(answer, previous) || rates[answer] >= MatchCarrier<FakeTunnel>.MatchPacketsPerSecond) quietFor = 0;
            else quietFor++;
            previous = answer;
        }
        Check("20 000 random seconds on three tunnels follow the rules", failure is null, $"{failure} (seed {Seed})");
    }

    /// <summary>A carrier driven one second at a time, with each tunnel's packets a second.</summary>
    private sealed class CarrierRun(FakeTunnel home)
    {
        private readonly MatchCarrier<FakeTunnel> _carrier = new();
        private readonly Dictionary<FakeTunnel, long> _counts = new(ReferenceEqualityComparer.Instance);
        private long _second;

        public FakeTunnel? Last { get; private set; }

        public FakeTunnel Seconds(int seconds, int homeRate, params (FakeTunnel Tunnel, int Rate)[] others)
        {
            FakeTunnel answer = home;
            for (var i = 0; i < seconds; i++) answer = Step(homeRate, others);
            return answer;
        }

        public FakeTunnel Step(int homeRate, params (FakeTunnel Tunnel, int Rate)[] others)
        {
            _second++;
            _counts[home] = _counts.GetValueOrDefault(home) + homeRate;
            foreach (var (t, rate) in others) _counts[t] = _counts.GetValueOrDefault(t) + rate;
            var answer = _carrier.Update(_second * 1_000, home, _counts[home], [.. others.Select(o => (o.Tunnel, _counts[o.Tunnel]))]);
            Last = answer;
            return answer;
        }
    }
}
