using System.Diagnostics;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// Measuring relays side by side (TunnelEngine.MeasureSideBySideAsync, 2026-10-02): what connect and the move between
/// matches now do instead of one way after another. The ways are the profile of that day - seven relays, twenty-one ways -
/// and each fake measurement takes as long as a real one did, about 0.8 s. What must hold is the rule that made it
/// sequential in the first place: two ways into ONE relay are never open at once (G5).
/// </summary>
internal static partial class Program
{
    private static List<RelayEntry> TwentyOneWays()
    {
        static RelayEntry Relay(string id) => new() { Id = id, Name = id };
        static RelayEntry Entry(string id, string via) => new() { Id = id, Name = id, ViaRelayId = via };

        return
        [
            Relay("hk"), Entry("vn-1-hk", "hk"), Entry("vn-2-hk", "hk"), Entry("vn-4-hk", "hk"),
            Relay("hk-2"), Entry("vn-1-hk2", "hk-2"), Entry("vn-2-hk2", "hk-2"),
            Relay("hk-3"), Entry("vn-1-hk-3", "hk-3"), Entry("vn-2-hk-3", "hk-3"), Entry("vn-4-hk-3", "hk-3"),
            Relay("sg"), Entry("vn-1-sg", "sg"), Entry("vn-2-sg", "sg"), Entry("vn-3-sg", "sg"),
            Relay("sg-2"), Entry("vn-1-sg2", "sg-2"),
            Relay("sg-3"), Entry("vn-1-sg-3", "sg-3"),
            Relay("sg-4"), Entry("vn-1-sg4", "sg-4"),
        ];
    }

    /// <summary>A measurement that takes <paramref name="ms"/> and counts how many ways are open per relay at once.</summary>
    private sealed class FakeMeasure(int ms)
    {
        private readonly Dictionary<string, int> _open = new(StringComparer.OrdinalIgnoreCase);
        public int MostIntoOneRelay { get; private set; }
        public int MostAtOnce { get; private set; }
        public int Measured;
        private int _now;

        public async Task<TunnelEngine.RelayProbe?> Run(RelayEntry way, Action<string> say, CancellationToken ct)
        {
            var relay = RelayPaths.RelayIdOf(way);
            lock (_open)
            {
                _open[relay] = _open.GetValueOrDefault(relay) + 1;
                MostIntoOneRelay = Math.Max(MostIntoOneRelay, _open[relay]);
                MostAtOnce = Math.Max(MostAtOnce, ++_now);
            }
            try
            {
                await Task.Delay(ms, ct);
                Interlocked.Increment(ref Measured);
                say($"  {way.Name} [{way.Id}]: measured");
                return new TunnelEngine.RelayProbe(way, 40, 50);
            }
            finally
            {
                lock (_open)
                {
                    _open[relay]--;
                    _now--;
                }
            }
        }
    }

    private static async Task RelaysAreMeasuredSideBySide()
    {
        var ways = TwentyOneWays();
        var fake = new FakeMeasure(800);
        var probes = new List<TunnelEngine.RelayProbe>();
        var lines = new List<string>();
        var clock = Stopwatch.StartNew();

        var stopped = await TunnelEngine.MeasureSideBySideAsync(ways, probes, fake.Run, skip: null, stop: null, lines.Add,
            CancellationToken.None);
        var took = clock.Elapsed.TotalSeconds;

        Check("Every one of the twenty-one ways is measured", fake.Measured == 21 && probes.Count == 21, $"{fake.Measured} measured, {probes.Count} probes");
        Check("Never two ways into one relay open at once - each resumes the same relayd session (G5)",
            fake.MostIntoOneRelay == 1, $"{fake.MostIntoOneRelay} at once");
        Check("The seven relays measured side by side", fake.MostAtOnce == 7, $"{fake.MostAtOnce} at once");
        Check($"Four ways deep at 0.8 s each: about 3.2 s, against 17 one after another - took {took:F1} s", took is > 3.0 and < 5.0, $"{took:F1} s");
        Check("Not stopped", !stopped, "");
        Check("The log reads in the profile's order, relay by relay",
            lines.SequenceEqual(ways.Select(w => $"  {w.Name} [{w.Id}]: measured")), string.Join(" | ", lines.Take(6)));
        Check("The probes end in the profile's order, so a tie is broken as it was",
            probes.Select(p => p.Relay.Id).SequenceEqual(ways.Select(w => w.Id)), string.Join(", ", probes.Select(p => p.Relay.Id)));
    }

    private static async Task AStopReasonStopsEveryRelay()
    {
        var ways = TwentyOneWays();
        var fake = new FakeMeasure(300);
        var probes = new List<TunnelEngine.RelayProbe>();
        var clock = Stopwatch.StartNew();

        // The game starts sending after the first round: the next way any relay is about to measure stops them all.
        bool Stop() => clock.ElapsedMilliseconds > 400;

        var stopped = await TunnelEngine.MeasureSideBySideAsync(ways, probes, fake.Run, skip: null, Stop, _ => { },
            CancellationToken.None);

        Check("A match starting stops the measuring and says so", stopped, "");
        Check($"  ...before the rest are handshaken - {fake.Measured} of 21 measured", fake.Measured < 21, $"{fake.Measured}");
        Check($"  ...and promptly, {clock.ElapsedMilliseconds} ms", clock.ElapsedMilliseconds < 1500, $"{clock.ElapsedMilliseconds} ms");
    }

    private static async Task ASkippedWayIsNotMeasured()
    {
        var ways = TwentyOneWays();
        var fake = new FakeMeasure(10);
        var probes = new List<TunnelEngine.RelayProbe>();
        var lines = new List<string>();

        await TunnelEngine.MeasureSideBySideAsync(ways, probes, fake.Run,
            skip: w => w.Id == "vn-2-hk" ? $"  {w.Name} [{w.Id}]: skipped" : null, stop: null, lines.Add, CancellationToken.None);

        Check("A way inside a routed range is skipped, the rest measured", fake.Measured == 20 && !probes.Any(p => p.Relay.Id == "vn-2-hk"),
            $"{fake.Measured}");
        Check("  ...and the skip is said in its place", lines.IndexOf("  vn-2-hk [vn-2-hk]: skipped") == 2, string.Join(" | ", lines.Take(4)));
    }

    private static async Task TheBudgetStillEndsIt()
    {
        var ways = TwentyOneWays();
        var fake = new FakeMeasure(800);
        var probes = new List<TunnelEngine.RelayProbe>();
        var lines = new List<string>();
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));

        var threw = false;
        try
        {
            await TunnelEngine.MeasureSideBySideAsync(ways, probes, fake.Run, skip: null, stop: null, lines.Add, budget.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        Check("The rescan's budget running out still ends it as a cancellation, as the caller expects", threw, "");
        Check("  ...with what was measured kept for the caller to close", probes.Count == fake.Measured && probes.Count > 0,
            $"{probes.Count} probes, {fake.Measured} measured");
        Check("  ...and what was measured still logged", lines.Count == fake.Measured, $"{lines.Count} lines");
    }
}
