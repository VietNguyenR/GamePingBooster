using System.Diagnostics;
using System.Net;
using GamePingBooster.Core.Quality;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The region planner's echoes, sent to every region at once (TunnelClient.MeasureManyThroughTunnelAsync, 2026-09-29),
/// against a relay whose internet answers each landmark after a known delay. Faster is only worth having if every number
/// is the number one-at-a-time would have measured: the plan moves a region on a 5 ms difference.
/// </summary>
internal static partial class Program
{
    /// <summary>Five landmarks at Delta Force's spread from Vietnam: HCM, HK, SG, JKT, BKK-like distances.</summary>
    private static readonly (IPAddress Address, double DelayMs)[] PlanLandmarks =
    [
        (IPAddress.Parse("198.51.100.1"), 8),
        (IPAddress.Parse("198.51.100.2"), 33),
        (IPAddress.Parse("198.51.100.3"), 45),
        (IPAddress.Parse("198.51.100.4"), 71),
        (IPAddress.Parse("198.51.100.5"), 150),
    ];

    /// <summary>Loopback, two sockets and a scheduler: what the fake path adds on top of the configured delay, at most.</summary>
    private const double EchoToleranceMs = 2.0;

    private static FakeRelay RelayWithLandmarks(Action<uint, FakeRelay.EchoRule>? tweak = null)
    {
        var relay = new FakeRelay(Psk);
        foreach (var (address, delay) in PlanLandmarks)
        {
            var rule = new FakeRelay.EchoRule { DelayMs = delay };
            tweak?.Invoke(U32(address), rule);
            relay.EchoRules[U32(address)] = rule;
        }
        return relay;
    }

    private static async Task<List<double?>[]> Rounds(TunnelClient tunnel, IReadOnlyList<IPAddress> landmarks, int timeoutMs, int rounds = RescanScore.Samples)
    {
        var samples = landmarks.Select(_ => new List<double?>()).ToArray();
        for (var r = 0; r < rounds; r++)
        {
            var got = await tunnel.MeasureManyThroughTunnelAsync(landmarks, timeoutMs, CancellationToken.None);
            for (var i = 0; i < got.Length; i++) samples[i].Add(got[i]);
        }
        return samples;
    }

    private static string Show(IEnumerable<double?> values) => string.Join(" ", values.Select(v => v is { } x ? x.ToString("F1") : "-"));

    private static async Task PlanEchoesAtOnce()
    {
        var landmarks = PlanLandmarks.Select(l => l.Address).ToList();

        // 1. Every region at once measures what one at a time measures, and what the path really is.
        using (var relay = RelayWithLandmarks())
        using (var rig = new Rig())
        {
            var tunnel = await rig.OpenAsync(relay, 101);
            await Rounds(tunnel, landmarks, TunnelEngine.PlanEchoTimeoutMs, rounds: 1);   // warm-up: JIT, first sends

            var clock = Stopwatch.StartNew();
            var many = await Rounds(tunnel, landmarks, TunnelEngine.PlanEchoTimeoutMs);
            var manyMs = clock.Elapsed.TotalMilliseconds;

            clock.Restart();
            var single = landmarks.Select(_ => new List<double?>()).ToArray();
            for (var i = 0; i < landmarks.Count; i++)
            {
                for (var r = 0; r < RescanScore.Samples; r++)
                {
                    single[i].Add(await tunnel.MeasureThroughTunnelAsync(landmarks[i], attempts: 1, CancellationToken.None));
                }
            }
            var singleMs = clock.Elapsed.TotalMilliseconds;

            for (var i = 0; i < landmarks.Count; i++)
            {
                var truth = PlanLandmarks[i].DelayMs;
                var m = RescanScore.Median(many[i]);
                var s = RescanScore.Median(single[i]);
                var minMany = many[i].OfType<double>().DefaultIfEmpty(double.NaN).Min();
                Check($"At once, {truth} ms landmark: median {m:F1} ms, within {EchoToleranceMs} of the path and of one at a time ({s:F1})",
                    m is { } mv && s is { } sv && Math.Abs(mv - truth) <= EchoToleranceMs && Math.Abs(mv - sv) <= EchoToleranceMs,
                    $"at once {Show(many[i])} | one at a time {Show(single[i])}");
                Check($"At once, {truth} ms landmark: no sample faster than the path (none timed on another echo's clock)",
                    minMany >= truth - 0.2, $"fastest {minMany:F2}");
            }

            var sum = PlanLandmarks.Sum(l => l.DelayMs) * RescanScore.Samples;
            var slowest = PlanLandmarks.Max(l => l.DelayMs) * RescanScore.Samples;
            Check($"At once costs the slowest landmark per round: {manyMs:F0} ms for 8 rounds (floor {slowest:F0}), one at a time {singleMs:F0} ms (floor {sum:F0})",
                manyMs < slowest + 150 && singleMs > sum - 50, $"at once {manyMs:F0}, one at a time {singleMs:F0}");
        }

        // 2. A silent landmark costs one timeout per round and nothing else.
        using (var relay = RelayWithLandmarks((a, r) => { if (a == U32(PlanLandmarks[2].Address)) r.Drop = _ => true; }))
        using (var rig = new Rig())
        {
            var tunnel = await rig.OpenAsync(relay, 102);
            var clock = Stopwatch.StartNew();
            var got = await Rounds(tunnel, landmarks, timeoutMs: 300, rounds: 4);
            var perRound = clock.Elapsed.TotalMilliseconds / 4;
            Check("A silent landmark: null in every round",
                got[2].All(v => v is null), Show(got[2]));
            Check("A silent landmark: the others still answer, on time",
                Enumerable.Range(0, 5).Where(i => i != 2).All(i => got[i].All(v => v is { } x && Math.Abs(x - PlanLandmarks[i].DelayMs) <= EchoToleranceMs)),
                string.Join(" | ", got.Select(Show)));
            Check($"A silent landmark: each round ends at the timeout, not later ({perRound:F0} ms against 300)",
                perRound is >= 295 and < 360, $"{perRound:F0} ms a round");
        }

        // 3. An answer after the timeout is lost - and never lands in the next round as a fast one.
        using (var relay = RelayWithLandmarks((a, r) => { if (a == U32(PlanLandmarks[1].Address)) r.DelayMs = 420; }))
        using (var rig = new Rig())
        {
            var tunnel = await rig.OpenAsync(relay, 103);
            var got = await Rounds(tunnel, landmarks, timeoutMs: 300, rounds: 6);
            Check("A landmark answering after the timeout: null in every round",
                got[1].All(v => v is null), Show(got[1]));
            Check("Its late answers, arriving in the next round, are matched to nothing - the others read true",
                Enumerable.Range(0, 5).Where(i => i != 1).All(i => got[i].All(v => v is { } x && Math.Abs(x - PlanLandmarks[i].DelayMs) <= EchoToleranceMs)),
                string.Join(" | ", got.Select(Show)));
        }

        // 4. Duplicates, and strays that look like answers, change nothing.
        using (var relay = RelayWithLandmarks((_, r) => r.Duplicate = true))
        using (var rig = new Rig())
        {
            var tunnel = await rig.OpenAsync(relay, 104);
            var session = relay.SessionOf(tunnel.SessionId)!;
            var inner = tunnel.Session.ClientIp;
            var samples = landmarks.Select(_ => new List<double?>()).ToArray();
            for (var round = 0; round < RescanScore.Samples; round++)
            {
                // A reply from every landmark with an id and sequence nobody asked for, just before the round.
                foreach (var l in landmarks) relay.SendToClient(session, StrayEchoReply(l, inner));
                var got = await tunnel.MeasureManyThroughTunnelAsync(landmarks, TunnelEngine.PlanEchoTimeoutMs, CancellationToken.None);
                for (var i = 0; i < got.Length; i++) samples[i].Add(got[i]);
            }
            Check("Duplicate replies and stray replies from the landmarks: every sample still the path's own",
                Enumerable.Range(0, 5).All(i => samples[i].All(v => v is { } x && Math.Abs(x - PlanLandmarks[i].DelayMs) <= EchoToleranceMs)),
                string.Join(" | ", samples.Select(Show)));
        }

        // 5. Two regions sharing a landmark each get their own answer; partial loss leaves a median that still counts.
        using (var relay = RelayWithLandmarks((a, r) => { if (a == U32(PlanLandmarks[3].Address)) r.Drop = n => n % 4 == 0; }))
        using (var rig = new Rig())
        {
            var tunnel = await rig.OpenAsync(relay, 105);
            var twice = new List<IPAddress> { landmarks[0], landmarks[0], landmarks[3] };
            var got = await Rounds(tunnel, twice, TunnelEngine.PlanEchoTimeoutMs);
            Check("One landmark listed for two regions: both answered in every round",
                got[0].All(v => v is not null) && got[1].All(v => v is not null), $"{Show(got[0])} | {Show(got[1])}");
            var answered = got[2].Count(v => v is not null);
            var median = RescanScore.Median(got[2]);
            Check($"Every 4th echo lost: {answered} of 8 answered, median {median:F1} still the path's ({PlanLandmarks[3].DelayMs})",
                answered == 6 && median is { } m && Math.Abs(m - PlanLandmarks[3].DelayMs) <= EchoToleranceMs, Show(got[2]));
        }

        // 6. Through a live tunnel - home, or a secondary already open - every region at once goes through the downlink
        //    thread's probe table. Seven in flight is Delta Force's five plus the in-game ping and a match-region probe.
        using (var relay = RelayWithLandmarks())
        using (var rig = new Rig())
        {
            var tunnel = await rig.StartAsync(relay, 106);
            var targets = landmarks.Concat([landmarks[0], landmarks[4]]).ToList();
            var truths = PlanLandmarks.Select(l => l.DelayMs).Concat([PlanLandmarks[0].DelayMs, PlanLandmarks[4].DelayMs]).ToList();
            var rounds = targets.Select(_ => new List<double?>()).ToArray();
            for (var round = 0; round < RescanScore.Samples; round++)
            {
                var got = await Task.WhenAll(targets.Select(t => tunnel.ProbeGameServerAsync(t, 800, CancellationToken.None)));
                for (var i = 0; i < got.Length; i++) rounds[i].Add(got[i]);
            }
            Check("Live tunnel, seven echoes at once: every one answered and timed to its own path",
                Enumerable.Range(0, targets.Count).All(i => rounds[i].All(v => v is { } x && Math.Abs(x - truths[i]) <= EchoToleranceMs)),
                string.Join(" | ", rounds.Select(Show)));

            var over = await Task.WhenAll(Enumerable.Range(0, 17).Select(_ => tunnel.ProbeGameServerAsync(landmarks[4], 800, CancellationToken.None)));
            Check("Control: seventeen at once overflow the sixteen slots - one comes back null (so the check above could fail)",
                over.Count(v => v is null) == 1, $"{over.Count(v => v is null)} null of 17");
        }
    }

    /// <summary>An echo reply from <paramref name="from"/> to the client with an id and sequence the client never used.</summary>
    private static byte[] StrayEchoReply(IPAddress from, IPAddress to)
    {
        var packet = new byte[28];
        packet[0] = 0x45;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 28);
        packet[8] = 64;
        packet[9] = 1;
        from.TryWriteBytes(packet.AsSpan(12), out _);
        to.TryWriteBytes(packet.AsSpan(16), out _);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), Checksum(packet.AsSpan(0, 20)));
        packet[20] = 0;   // echo reply
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24), (ushort)Random.Shared.Next(1, ushort.MaxValue));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), Checksum(packet.AsSpan(20)));
        return packet;
    }
}
