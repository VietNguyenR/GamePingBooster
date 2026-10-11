namespace GamePingBooster.Core.Quality;

/// <summary>
/// When a path's round trips swing too much to be compared on their median alone, and what that costs it.
///
/// Until 2026-10-11 every choice of a path - the relay list and connect (DoorChoice), the rescan between matches,
/// another tunnel's ways (WayCheck), entry switching's steady rule - ranked on the median, plus RelayLoss's penalty.
/// A median does not see a road that swings: through vn-2 the round trip sat at 38 ms half the time and at 50-60 the
/// rest, so its median beat every steady road beside it and the app chose it, again and again. On prod, 2026-10-02 to
/// 10-11, the ways in through vn-2 had 2.2-3.2 spikes a minute against about 0.5 on every other way, every day and on
/// every ISP, and still carried ~180 FPT matches a day, because each one won on its median. A customer on FPT played
/// 2026-10-10 midday on vn-2-sg5 at p50 38 / p95 48-62 with 20-43 spikes a match, while vn-5-sg4 answered 41-42 flat
/// (fpt-jitter-handoff.md). A game hides a steady 45 ms far better than one that jumps between 38 and 60.
///
/// So a path that swings carries a penalty, as one that loses packets does: <see cref="Weight"/> ms for every ms its
/// spread - the 90th percentile less the median - exceeds <see cref="AllowanceMs"/>. The 90th, not the 95th: most
/// choices have eight to twenty samples, and the 95th of eight is the slowest one.
///
/// THE SPREAD IS COUNTED OVER THE CALMEST PATH MEASURED BESIDE IT (<see cref="OverCalmest"/>), never on its own. A busy
/// Wi-Fi or a PC that stalls shakes every path measured at that moment, and moving would fix none of it; only a swing
/// one path has and the others do not is the road's. Measured on the 2026-10-10 customer's records, eight samples at a
/// time: the vn-2 ways spread 5.5-8 ms at the median and over 4 ms in 70-90% of windows, every other way 0.2-1.3 ms -
/// so even a short measurement sees it most of the time, and <see cref="JitterMemory"/> covers the rest.
///
/// A penalty rather than a ban, as for loss: when every path swings the fastest still wins. Pure: numbers in, numbers out.
/// </summary>
public static class Jitter
{
    /// <summary>Fewer answered samples than this give no spread: one slow answer in five would be its 90th percentile.</summary>
    public const int MinSamples = 6;

    /// <summary>
    /// Spread over the calmest path that costs nothing. A steady way spreads 0.2-1.3 ms over eight samples and up to
    /// 3-4 ms in an unlucky window; vn-2 spread 5.5-9.
    /// </summary>
    public const double AllowanceMs = 3;

    /// <summary>Each ms of spread past the allowance counts as this many ms of round trip.</summary>
    public const double Weight = 2;

    /// <summary>The 90th percentile less the median of <paramref name="answered"/>, or null with fewer than <see cref="MinSamples"/>.</summary>
    public static double? Spread(IEnumerable<double> answered)
    {
        var values = answered.ToList();
        if (values.Count < MinSamples) return null;
        return SpikeDetector.Percentile(values, 90) - SpikeDetector.Percentile(values, 50);
    }

    /// <summary>
    /// Each spread less the smallest of them: what each path swings that the calmest one measured with it does not.
    /// A null spread stays null.
    /// </summary>
    public static double?[] OverCalmest(IReadOnlyList<double?> spreads)
    {
        var known = spreads.OfType<double>().ToList();
        var calmest = known.Count == 0 ? 0 : known.Min();
        return spreads.Select(s => s is { } v ? Math.Max(0, v - calmest) : (double?)null).ToArray();
    }

    /// <summary>What a path that swings <paramref name="excessMs"/> more than the calmest adds to its round trip when compared.</summary>
    public static double Penalty(double? excessMs) => excessMs is { } e ? Weight * Math.Max(0, e - AllowanceMs) : 0;

    /// <summary>The larger of two excesses, either of which may be unknown.</summary>
    public static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    /// <summary>", swinging 8 ms (counted 10 ms slower)" when the excess costs something, "" otherwise.</summary>
    public static string Note(double? excessMs) =>
        Penalty(excessMs) is var penalty and > 0 ? $", swinging {excessMs:F0} ms (counted {penalty:F0} ms slower)" : "";
}

/// <summary>
/// The ways into a relay that have been seen to swing on this network, for the choices that only measure for a second
/// or two. Fed by entry switching, which times every way into the relay in use four times a second for as long as the
/// game runs (<see cref="DoorSwitchPolicy.WayJitter"/>): two minutes of that is far surer than the eight echoes a rescan
/// takes, which caught the vn-2 swing in 70-90% of tries - and one miss puts a player on it for a match.
///
/// A way is remembered for <see cref="KeepFor"/> after it was last seen swinging, and forgotten as soon as it is seen
/// steady: the memory only ever stands in for a measurement nobody has made since. Kept for one network at a time -
/// the caller names it (<see cref="OnNetwork"/>), and another network starts empty: a swing is about a line.
/// Thread-safe; the clock is passed in.
/// </summary>
public sealed class JitterMemory
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromHours(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, (double ExcessMs, DateTimeOffset Until)> _ways = new(StringComparer.OrdinalIgnoreCase);
    private string? _network;

    /// <summary>The network the tunnel now leaves by. A different one from before forgets everything.</summary>
    public void OnNetwork(string? network)
    {
        if (network is null) return;
        lock (_gate)
        {
            if (string.Equals(network, _network, StringComparison.Ordinal)) return;
            _network = network;
            _ways.Clear();
        }
    }

    /// <summary>
    /// What a long window showed, way by way: a way that swings is remembered (or kept) at that excess, a way that
    /// does not is forgotten. Returns the ways newly remembered or forgotten, for the log.
    /// </summary>
    public (List<string> Remembered, List<string> Forgotten) Observe(IReadOnlyDictionary<string, double> excessByWay, DateTimeOffset now)
    {
        var remembered = new List<string>();
        var forgotten = new List<string>();
        lock (_gate)
        {
            foreach (var (way, excess) in excessByWay)
            {
                if (Jitter.Penalty(excess) > 0)
                {
                    if (!_ways.TryGetValue(way, out var held) || held.Until <= now) remembered.Add(way);
                    _ways[way] = (excess, now + KeepFor);
                }
                else if (_ways.Remove(way, out var held) && held.Until > now)
                {
                    forgotten.Add(way);
                }
            }
        }
        return (remembered, forgotten);
    }

    /// <summary>The excess <paramref name="wayId"/> was last seen swinging by, while remembered; null otherwise.</summary>
    public double? ExcessOf(string wayId, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _ways.TryGetValue(wayId, out var held) && held.Until > now ? held.ExcessMs : null;
        }
    }

    /// <summary>What a short measurement of <paramref name="wayId"/> is judged on: the larger of what it measured and what is remembered.</summary>
    public double? Floor(string wayId, double? measured, DateTimeOffset now) => Jitter.Max(measured, ExcessOf(wayId, now));
}
