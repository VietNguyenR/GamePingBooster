namespace GamePingBooster.Core.Quality;

/// <summary>
/// The panel's "Mạng trong nhà" item, live, whenever the tunnel is up - before the game is even started, in the lobby, and
/// in a match. The owner asked on 2026-10-21 for the check to run from the moment "Bắt đầu tăng tốc" is pressed: until
/// then the item only learnt anything from a spike in a match (<see cref="SpikeEvent.Household"/> / Congested), so the
/// card said "measured while you play" through the whole lobby.
///
/// It judges on the router's own ping alone - nothing goes over the internet - with the floor rule the match rule uses
/// (<see cref="SpikeDetector.CongestedFloorMs"/>): once a second, the lower quarter of the last three seconds of answered
/// gateway echoes against normal. A queue of someone's data in the router delays every echo, so the floor rises; a weak
/// signal or interference retries some echoes and leaves the floor where it was (measured 2026-10-20 on Viettel, VNPT at
/// 29 % signal, FPT). It needs no verdict about the route to the relay, so a relay that ignores ICMP - sg-6 on
/// 2026-10-20 18:39, which left every spike of a phone speed test "unclear" - changes nothing here.
///
/// A second only counts with this PC quiet (under <see cref="SpikeDetector.QuietOwnMbps"/> each way): before a match
/// the PC itself is often what is downloading (a game update), and on a router with no counters that would read as
/// another device. Normal is the median of the last two minutes of calm echoes, not taken while the floor is up or this
/// PC is busy; a house already busy when the boost starts sets a high normal and is not seen until it calms - missed,
/// never a false alarm. An episode is reported once the floor stayed up two judged seconds; it ends after three calm ones.
///
/// One per recorder, fed every settled quarter second while connected, on the recorder's thread only.
/// </summary>
public sealed class HomeLineWatch
{
    /// <summary>Two minutes of answered calm echoes make normal.</summary>
    internal const int BaselineSamples = 120 * SpikeDetector.TicksPerSecond;

    /// <summary>Ten seconds of answered echoes before anything is judged - the detector's own warm-up.</summary>
    internal const int MinBaselineSamples = SpikeDetector.WarmupTicks;

    /// <summary>Judged seconds with the floor up before an episode is reported: one is three seconds of echoes already,
    /// two keeps a single odd window from ever reaching the panel.</summary>
    internal const int MinRisenSeconds = 2;

    /// <summary>Calm judged seconds that end an episode.</summary>
    internal const int CalmSecondsToClose = 3;

    /// <summary>Echoes sent with none answered after which the router is taken not to answer pings.</summary>
    internal const int SilentAfterSent = 30 * SpikeDetector.TicksPerSecond;

    private readonly QualityTick?[] _window = new QualityTick?[SpikeDetector.FloorWindowTicks];
    private readonly Queue<double> _calm = new();
    private readonly List<double> _sorted = new(BaselineSamples);
    private long _lastIndex = long.MinValue;
    private int _sent;
    private int _answered;
    private Open? _open;

    private sealed class Open
    {
        public required DateTimeOffset Start { get; init; }
        public DateTimeOffset LastRisenEnd { get; set; }
        public int RisenSeconds { get; set; }
        public int CalmSeconds { get; set; }
        public double PeakExcessMs { get; set; }
        public double FloorExcessMs { get; set; }
        public List<double> Other { get; } = [];
    }

    /// <summary>What normal is for the router's ping, or null while there is not enough of it.</summary>
    public double? NormalMs { get; private set; }

    /// <summary>
    /// Whether the router answers pings: null until enough were sent to say, false when none of the last
    /// <see cref="SilentAfterSent"/> were answered. A router that does not answer gives this item nothing to judge on.
    /// </summary>
    public bool? Answers => _answered > 0 ? true : _sent >= SilentAfterSent ? false : null;

    /// <summary>The episode going on now, once it counts; null otherwise.</summary>
    public HomeLineEpisode? Busy => _open is { RisenSeconds: >= MinRisenSeconds } open ? Describe(open, ongoing: true) : null;

    /// <summary>Takes one settled quarter second. Returns an episode when one just ended.</summary>
    public HomeLineEpisode? Feed(QualityTick tick)
    {
        HomeLineEpisode? ended = null;

        // A hole (a reconnect, a sleep) is not calm and not part of a queue: what was open ends where the data does.
        if (_lastIndex != long.MinValue && tick.Index != _lastIndex + 1)
        {
            ended = Close();
            Array.Clear(_window);
        }
        _lastIndex = tick.Index;
        _window[tick.Index % _window.Length] = tick;

        if (tick.GatewaySent)
        {
            _sent = Math.Min(_sent + 1, SilentAfterSent);
            if (tick.GatewayMs is not null) _answered = SilentAfterSent;
            else if (_answered > 0) _answered--;
        }

        if (_open is { } open && NormalMs is { } normal && tick.GatewaySent && tick.GatewayMs is { } ms)
        {
            open.PeakExcessMs = Math.Max(open.PeakExcessMs, ms - normal);
        }
        if (_open is { } o && (tick.OtherDownMbps is not null || tick.OtherUpMbps is not null))
        {
            o.Other.Add(Math.Max(tick.OtherDownMbps ?? 0, tick.OtherUpMbps ?? 0));
        }

        if (tick.Index % SpikeDetector.TicksPerSecond != 0) return ended;
        return Judge(tick) ?? ended;
    }

    /// <summary>A different network or router: normal starts again, and an open episode ends unreported.</summary>
    public void Reset()
    {
        _open = null;
        _calm.Clear();
        Array.Clear(_window);
        NormalMs = null;
        _lastIndex = long.MinValue;
        _sent = 0;
        _answered = 0;
    }

    private HomeLineEpisode? Judge(QualityTick now)
    {
        var answered = new List<double>(_window.Length);
        var ownQuiet = true;
        var full = true;
        foreach (var t in _window)
        {
            if (t is null)
            {
                full = false;
                continue;
            }
            if (t.GatewaySent && t.GatewayMs is { } ms) answered.Add(ms);
            if ((t.LineDownMbps ?? 0) >= SpikeDetector.QuietOwnMbps || (t.LineUpMbps ?? 0) >= SpikeDetector.QuietOwnMbps) ownQuiet = false;
        }

        double? floor = null;
        if (full && answered.Count >= SpikeDetector.FloorMinAnswers)
        {
            answered.Sort();
            floor = answered[answered.Count / 4];
        }

        var risen = ownQuiet && floor is { } f && NormalMs is { } normal && f - normal >= SpikeDetector.CongestedFloorMs;
        if (risen)
        {
            var open = _open ??= new Open { Start = WindowStart() };
            open.RisenSeconds++;
            open.CalmSeconds = 0;
            open.LastRisenEnd = now.StartUtc.AddMilliseconds(SpikeDetector.TickMs);
            open.FloorExcessMs = Math.Max(open.FloorExcessMs, floor!.Value - NormalMs!.Value);
            return null;
        }

        if (_open is { } was)
        {
            was.CalmSeconds++;
            if (was.CalmSeconds >= CalmSecondsToClose) return Close();
            return null;
        }

        // Calm and quiet: the last second's echoes go into normal. Never while an episode is open or this PC is busy -
        // either would teach normal the very queue it is there to show.
        if (ownQuiet)
        {
            for (var i = 0; i < SpikeDetector.TicksPerSecond; i++)
            {
                if (_window[(now.Index - i + _window.Length) % _window.Length] is { GatewaySent: true, GatewayMs: { } ms })
                {
                    _calm.Enqueue(ms);
                    if (_calm.Count > BaselineSamples) _calm.Dequeue();
                }
            }
            if (_calm.Count >= MinBaselineSamples)
            {
                _sorted.Clear();
                _sorted.AddRange(_calm);
                _sorted.Sort();
                var mid = _sorted.Count / 2;
                NormalMs = _sorted.Count % 2 == 1 ? _sorted[mid] : (_sorted[mid - 1] + _sorted[mid]) / 2;
            }
        }
        return null;
    }

    private DateTimeOffset WindowStart()
    {
        var first = _window.Where(t => t is not null).MinBy(t => t!.Index);
        return first?.StartUtc ?? DateTimeOffset.UtcNow;
    }

    private HomeLineEpisode? Close()
    {
        var open = _open;
        _open = null;
        return open is { RisenSeconds: >= MinRisenSeconds } ? Describe(open, ongoing: false) : null;
    }

    private static HomeLineEpisode Describe(Open open, bool ongoing)
    {
        double? other = null;
        if (open.Other.Count > 0)
        {
            var sorted = open.Other.OrderBy(v => v).ToList();
            var mid = sorted.Count / 2;
            other = sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }
        return new HomeLineEpisode(
            open.Start,
            Math.Max(0, (open.LastRisenEnd - open.Start).TotalSeconds),
            open.PeakExcessMs,
            open.FloorExcessMs,
            other,
            ongoing);
    }
}

/// <summary>
/// A stretch in which the router held a queue while this PC was quiet (<see cref="HomeLineWatch"/>). Seconds runs from the
/// first echo of the window that first rose to the end of the last risen second. OtherMbps is the median of what the
/// other devices moved meanwhile, when the router gives counters; at <see cref="SpikeDetector.HouseholdOtherMbps"/> or
/// more it names the cause (<see cref="Household"/>).
/// </summary>
public sealed record HomeLineEpisode(
    DateTimeOffset StartUtc,
    double Seconds,
    double PeakGatewayExcessMs,
    double FloorExcessMs,
    double? OtherMbps,
    bool Ongoing)
{
    public bool Household => OtherMbps >= SpikeDetector.HouseholdOtherMbps;
}
