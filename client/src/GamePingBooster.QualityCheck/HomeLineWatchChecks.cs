using GamePingBooster.Core.Quality;

namespace GamePingBooster.QualityCheck;

/// <summary>
/// The home line live, from Connect on (HomeLineWatch, 2026-10-21), and the match rule when the relay ignores ICMP.
/// The recorded series are the ones the match checks use: the router's ping, quarter second by quarter second.
/// </summary>
internal static partial class Program
{
    // The owner's Viettel match on sg-6, 2026-10-20 11:40:17 UTC (18:40 local), a phone speed test on the same Wi-Fi, no
    // router counters, and sg-6 answering no ICMP the whole match. As recorded (null = lost): the router, relayd, the
    // datacentre echo. Every spike of that match came out "unclear", so the card never spoke.
    private static readonly double?[] Sg6Gateway =
    [
        271, 23, 23, 2, 3, 2, 2, 1, 1, 2, 2, 3, 3, 6, 15, 2, 4, 4, 1, 8, 11, 1, 1, 2, 1, 4, 2, 1, 1, 1, 2, 2, 6, 9, 3, 5, 2, 1, 1,
        15, 52, 38, 62, 33, 28, 67, 4, 72, 11, 3, 30, 21, 27, 5, 128, 16, 14, 27, 17, 40, 22, 85, 110, 33, 64, 22, 46, 16, 13,
        42, 27, 5, 33, 69, 496, 234, 39, 13, 3, 10, 17, 23, 18, 17, 34, 15, 37, 16, 36, 36, 78, 12, 11, 13, 32, 52, 18, 40, 15,
        3, 2, 2, 2, 1, null, 554, 305, 57, 382, 269, 21, 20, 161, 42, 92, 262, 6, 262, 14, 456, 209, 74, 13, 122, 11, 2, 2, 1,
        1, 1, 3, 1,
    ];

    private static readonly double[] Sg6Process =
    [
        310.7, 62.4, 65.7, 46.1, 44, 44.3, 44.1, 43.6, 46.4, 45.2, 43.8, 46.3, 43.5, 41.9, 42.7, 42, 42.1, 45.3, 46.4, 41.8,
        41.6, 42.8, 41.1, 44.9, 41.5, 41.7, 43.6, 43.4, 44.1, 44.2, 44.6, 45.8, 41.3, 42.1, 46.1, 46.3, 41.3, 45.5, 46.6, 58.6,
        106.7, 64.4, 79.8, 73.4, 69.6, 106.4, 54.3, 114.7, 49.9, 51.9, 69.5, 76.7, 64.7, 48, 175.3, 64.8, 52, 74.1, 51.9,
        131.2, 59.4, 130.5, 154.6, 73.4, 113.3, 65.9, 84.5, 76.2, 59.1, 85.5, 77.4, 62.3, 80.4, 112.1, 539.7, 330.1, 77.2,
        45.9, 50.3, 53.3, 66.6, 68, 62, 61.1, 71.8, 55.5, 87.8, 60.2, 66.5, 75.9, 123.9, 56.9, 60.1, 56.7, 82.6, 100.2, 63.6,
        81.6, 64.4, 44, 47.8, 42.4, 42.3, 43.3, 833.7, 602.4, 352.3, 150.7, 421.8, 312.2, 64.7, 56.9, 199.4, 77.3, 153.4,
        308.2, 45.2, 306.4, 57.5, 504.2, 257.1, 137.9, 64, 153.6, 41.9, 43, 42.2, 41.9, 42, 42.3, 45.7, 42,
    ];

    private static readonly double[] Sg6Datacentre =
    [
        310.7, 62.4, 65.6, 46, 43.9, 44.3, 44, 43.6, 46.4, 45.1, 43.7, 46.3, 43.4, 43.5, 43.5, 42.8, 42.9, 45.3, 46.4, 42.7,
        43.4, 43.3, 43.3, 45.9, 42.8, 44.6, 44.3, 44.5, 44.1, 44.1, 44.5, 44.9, 43.1, 43.6, 46.1, 46, 43.1, 45.5, 46.6, 58.5,
        106.6, 69.8, 79.7, 77, 74.1, 106.3, 54.3, 114.7, 52, 51.9, 69.4, 56.1, 68.3, 54.2, 177.9, 57.4, 55.2, 74, 52, 83.6,
        74.3, 130.5, 154.6, 73.4, 113.2, 65.7, 87.6, 55.5, 59, 88.6, 77.3, 46.5, 80.3, 113.2, 539.6, 282.4, 77.5, 45.9, 53.5,
        53.2, 70.6, 71.7, 62.3, 70.5, 71.7, 55.5, 87.7, 62.9, 66.4, 76.7, 127, 52.7, 60.5, 56.7, 82.5, 100.1, 63.6, 84.9, 64.3,
        43.9, 47.7, 43.4, 43.1, 43.2, 833.7, 602.3, 352.2, 103.9, 421.7, 312.1, 64.6, 56.9, 199.4, 79, 153.7, 308.2, 55.4,
        306.5, 57.4, 504.1, 257, 137.9, 64, 157.3, 42.7, 43.6, 44.9, 43.6, 43.1, 43, 45.6, 42.8,
    ];

    // The phone speed tests recorded 2026-10-20 (the router's ping only), the same series as the match checks above.
    private static readonly double?[] VtSpeedTest =
    [
        2.2, 1.6, 1.5, 3.4, 2.4, 2.5, 1.9, 2.4, 2.8, 2.6, 1.9, 1.5, 1.9, 2.7, 2.6, 2.2, 2.4, 570.5, 7.6, 18.6, 283.3, 712.8, 9.6,
        18.5, 161.4, 53, 14.2, 14.2, 15, 118.8, 162.1, 41.2, null, null, 92.6, 57.5, 18.5, 124, 27.9, 569.1, 32.8, 27.7, null,
        193.6, 3.1, 2.2, 19.2, 19.1, 3.9, 1.5, 1.7, 1.9, 2.1, 1.7, 2.9, 2.9, 3.8, 63.3, 14.3, 43.9, 33.5, null, 487.4, 2, 25.5,
        53.8, 199.5, 9.8, 17.2, 55.8, 21.9, 36.6, 259.4, 71.7, 65.8, 2.3, 3.6, 1.8, 2.1, 5.2, 2.4, 1.3, 1.7, 7.4, 2.3, 2, 2, 2.4,
    ];

    private static readonly double?[] WeakSpeedTest =
    [
        1.9, 1.5, 2.6, 2.9, 2.1, 2.3, 2.1, 2, 1.9, 9.1, 14.9, 9.6, null, 710.5, 29, 10.3, 5.8, null, 340.2, 98.7, 198.6, 2, 2,
        38.2, 49.3, 304.9, 9.7, 355.8, 31.6, 34.8, 124.1, 22.4, 11, 39.6, 155.3, 31.7, 15.8, 532.3, 31.4, 60.7, 3.5, 4.8, 115.2,
        3.8, 6.2, 1.6, 2, 1.9, 2, 1.7, 4.2, 6.3, 4.2, 275.7, null, null, 112.9, 13.9, 6, null, 10.2, 2.7, 13.5, 56, 2.6, 42.2,
        2.4, 2.3, 2.4, 2, 1.6, 2.2, 2.2, 1.7, 1.6,
    ];

    private static void HomeLineWatchChecks()
    {
        ARelayIgnoringPingsStillNamesTheRouter();
        TheSg6MatchSpikeIsCongested();

        // Live, from Connect on.
        var calm = Watch(2, Repeat(null, 5 * 60 * 4));
        Check("Watch: five calm minutes - no episode, the router answers", calm.Episodes.Count == 0 && !calm.EverBusy && calm.Answers == true,
            calm.Describe());

        var phone = Watch(1.8, VtSpeedTest);
        Check("Watch: Viettel phone speed test before a match - router busy, not named (no counters)",
            phone.Episodes.Count >= 1 && phone.EverBusy && phone.Episodes.All(e => !e.Household && e.OtherMbps is null), phone.Describe());

        var weak = Watch(1.9, WeakSpeedTest);
        Check("Watch: VNPT at 29 % signal, phone speed test - router busy", weak.Episodes.Count >= 1, weak.Describe());

        var sg6 = Watch(2, Sg6Gateway);
        Check("Watch: the sg-6 match's router series - router busy, the relay's ICMP does not matter", sg6.Episodes.Count >= 1, sg6.Describe());

        var named = Watch(3, Repeat(45, 40), other: 500);
        Check("Watch: floor +42 ms for 10 s while other devices move 500 Mbps - household, with the figure",
            named.Episodes.Count == 1 && named.Episodes[0].Household && named.Episodes[0].OtherMbps is > 450 and < 550, named.Describe());

        var stream = Watch(2, Repeat(null, 4 * 60), other: 25);
        Check("Watch: other devices at 25 Mbps on a calm router (a 4K stream) - nothing", stream.Episodes.Count == 0 && !stream.EverBusy,
            stream.Describe());

        var late = Watch(3, Enumerable.Range(0, 160).Select(i => (double?)(i % 2 == 0 ? 153 : 3)));
        Check("Watch: every other echo 150 ms late on a calm floor (weak signal, interference) - nothing",
            late.Episodes.Count == 0 && !late.EverBusy, late.Describe());

        var own = Watch(3, Repeat(45, 40), lineDown: 300);
        Check("Watch: floor up while this PC itself downloads 300 Mbps - nothing (a game update before playing)",
            own.Episodes.Count == 0 && !own.EverBusy, own.Describe());

        var single = Watch(3, Repeat(45, 8));
        Check("Watch: the router slow for 2 s, then calm - not reported", single.Episodes.Count == 0 && !single.EverBusy, single.Describe());

        var silent = Watch(null, Repeat(null, 60 * 4));
        Check("Watch: a router that answers no pings - never busy, and says it does not answer",
            silent.Episodes.Count == 0 && !silent.EverBusy && silent.Answers == false, silent.Describe());

        var busyAtStart = WatchFromConnect(Repeat(45, 60 * 4));
        Check("Watch: a house already busy at Connect sets a high normal - missed, never a false alarm",
            busyAtStart.Episodes.Count == 0 && !busyAtStart.EverBusy, busyAtStart.Describe());

        var hole = new HomeLineWatch();
        var index = 0L;
        foreach (var ms in Repeat(null, 240)) hole.Feed(WatchTick(index++, 3 + Wobble(index), null, 0.4));
        foreach (var ms in Repeat(45, 40)) hole.Feed(WatchTick(index++, 48, null, 0.4));
        var wasBusy = hole.Busy is not null;
        var closed = hole.Feed(WatchTick(index + 100, 3, null, 0.4));
        Check("Watch: a hole (reconnect, sleep) ends the episode it interrupts", wasBusy && closed is { Ongoing: false } && hole.Busy is null,
            $"busy before {wasBusy}, closed {closed}");
    }

    // A relay that answers no ICMP at all leaves the wire with no normal. The router is then held against relayd's pong.
    private static void ARelayIgnoringPingsStillNamesTheRouter()
    {
        var s = new Session();
        s.Repeat(240, () => DeadWire(s.Tick()));
        s.Repeat(16, () => DeadWire(s.Tick(gateway: 60, process: 60, datacentre: 60)));
        s.Repeat(12, () => DeadWire(s.Tick()));
        s.Events.AddRange(s.Detector.Flush());
        Check("Relay ignoring ICMP: the router slow with relayd - router, and congested",
            s.Events.Count == 1 && s.Events[0].Verdict == SpikeVerdicts.Router && s.Events[0].Congested, Got(s));

        var route = new Session();
        route.Repeat(240, () => DeadWire(route.Tick()));
        route.Repeat(16, () => DeadWire(route.Tick(process: 60, datacentre: 60)));
        route.Repeat(12, () => DeadWire(route.Tick()));
        route.Events.AddRange(route.Detector.Flush());
        Check("  ...the route slow past a calm router stays unclear, never router",
            route.Events.Count == 1 && route.Events[0].Verdict == SpikeVerdicts.Unclear && !route.Events[0].Congested, Got(route));
    }

    private static void TheSg6MatchSpikeIsCongested()
    {
        var s = new Session();
        s.Repeat(240, () =>
        {
            var t = DeadWire(s.Tick());
            t.GatewayMs = 2 + Math.Abs(Wobble(t.Index)) / 3;
            t.RelayProcessMs = 42.3 + Wobble(t.Index) / 3;
            t.DatacentreMs = 43.5 + Wobble(t.Index) / 3;
            return t;
        });
        for (var i = 0; i < Sg6Gateway.Length; i++)
        {
            var t = DeadWire(s.Tick());
            t.GatewayMs = Sg6Gateway[i];
            t.RelayProcessMs = Sg6Process[i];
            t.DatacentreMs = Sg6Datacentre[i];
            s.Feed(t);
        }
        s.Repeat(12, () => DeadWire(s.Tick()));
        s.Events.AddRange(s.Detector.Flush());
        Check("The sg-6 match (relay ICMP dead, phone speed test): a congested router spike",
            s.Events.Any(e => e.Verdict == SpikeVerdicts.Router && e.Congested) && s.Events.All(e => !e.Household), Got(s));
    }

    private static QualityTick DeadWire(QualityTick tick)
    {
        tick.RelayWireMs = null;
        return tick;
    }

    // ------------------------------------------------------------------ the watch harness

    private sealed record WatchResult(List<HomeLineEpisode> Episodes, bool EverBusy, bool? Answers)
    {
        public string Describe() =>
            $"{Episodes.Count} episode(s) [{string.Join("; ", Episodes.Select(e => $"{e.Seconds:F1} s floor +{e.FloorExcessMs:F0} other {e.OtherMbps}"))}]" +
            $", busy seen {EverBusy}, answers {Answers}";
    }

    /// <summary>
    /// A minute of calm at <paramref name="normal"/> (null: no answer at all), then the series (null in it = the calm
    /// value; Repeat makes it), then half a minute calm. Ticks are not Active: before the game, as the watch mostly sees.
    /// </summary>
    private static WatchResult Watch(double? normal, IEnumerable<double?> series, double? other = null, double lineDown = 0.4)
    {
        var watch = new HomeLineWatch();
        var episodes = new List<HomeLineEpisode>();
        var busy = false;
        var index = 0L;
        void Feed(double? ms, double? others, double down)
        {
            if (watch.Feed(WatchTick(index, ms, others, down)) is { } e) episodes.Add(e);
            busy |= watch.Busy is not null;
            index++;
        }
        double? Calm() => normal is { } n ? n + Math.Abs(Wobble(index)) / 2 : null;

        for (var i = 0; i < 240; i++) Feed(Calm(), other is null ? null : 2, 0.4);
        foreach (var ms in series) Feed(ms ?? Calm(), other, lineDown);
        for (var i = 0; i < 120; i++) Feed(Calm(), other is null ? null : 2, 0.4);
        return new WatchResult(episodes, busy, watch.Answers);
    }

    /// <summary>The series from the very first quarter second - no calm before it.</summary>
    private static WatchResult WatchFromConnect(IEnumerable<double?> series)
    {
        var watch = new HomeLineWatch();
        var episodes = new List<HomeLineEpisode>();
        var busy = false;
        var index = 0L;
        foreach (var ms in series)
        {
            if (watch.Feed(WatchTick(index, ms + Wobble(index), null, 0.4)) is { } e) episodes.Add(e);
            busy |= watch.Busy is not null;
            index++;
        }
        return new WatchResult(episodes, busy, watch.Answers);
    }

    private static QualityTick WatchTick(long index, double? gatewayMs, double? other, double lineDown) =>
        new(index, new DateTimeOffset(2026, 10, 21, 1, 0, 0, TimeSpan.Zero).AddMilliseconds(index * SpikeDetector.TickMs))
        {
            GatewaySent = true,
            GatewayMs = gatewayMs,
            LineDownMbps = lineDown,
            LineUpMbps = 0.3,
            OtherDownMbps = other,
            OtherUpMbps = other is null ? null : 0.5,
        };

    private static IEnumerable<double?> Repeat(double? value, int count) => Enumerable.Repeat(value, count);

    /// <summary>A deterministic ±1.5 ms of jitter.</summary>
    private static double Wobble(long index) => ((index * 7919 % 31) / 31.0 - 0.5) * 3;
}
