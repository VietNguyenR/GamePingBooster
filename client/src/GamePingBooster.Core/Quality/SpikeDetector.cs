namespace GamePingBooster.Core.Quality;

/// <summary>
/// One quarter of a second of the path, as the service's spike recorder saw it.
///
/// Every latency field is a round trip that LEFT during this quarter second, and each has a
/// matching <c>Sent</c> flag, because two different things look like a missing number and only
/// one of them is a fault: a probe that was never sent says nothing, a probe that was sent and
/// never answered is loss. Collapsing the two is how a relay that was not being measured would
/// read as a relay dropping everything.
///
/// The gap fields are passive - no probe at all, only the timing of the game's own packets. A gap
/// is recorded in the quarter second the silence ENDED, when the next packet arrived. A silence
/// that never ends is a match that finished, not a spike, and recording gaps only on arrival is
/// what keeps every match end from being reported as a frozen server.
/// </summary>
public sealed class QualityTick(long index, DateTimeOffset startUtc)
{
    public long Index { get; } = index;
    public DateTimeOffset StartUtc { get; } = startUtc;

    /// <summary>The game was running and its packets were flowing, so the probes below were sent.</summary>
    public bool Active { get; set; }

    /// <summary>
    /// The game was running with no match under way - the lobby, the agent select, a queue. Only relayd's pong
    /// (B) and the Probes down the other ways in were sent, for <see cref="DoorSwitchPolicy"/>, which may move a
    /// player in the lobby as well as in a match. Not <see cref="Active"/>: nothing else reads these ticks, so
    /// the lobby is never a match, a spike or a baseline.
    /// </summary>
    public bool Lobby { get; set; }

    /// <summary>
    /// A0: ICMP to the home router. The one probe that separates the player's own network - Wi-Fi,
    /// cable, router - from everything past it. Many routers answer pings to themselves late or not
    /// at all, which is why it only ever CONFIRMS a verdict and never raises one.
    /// </summary>
    public bool GatewaySent { get; set; }
    public double? GatewayMs { get; set; }

    /// <summary>A: ICMP to the relay's (or entry's) public address over the physical path. Answered by its kernel.</summary>
    public bool RelayWireSent { get; set; }
    public double? RelayWireMs { get; set; }

    /// <summary>B: a protocol ping answered by relayd itself - A plus the relay process and its VPS.</summary>
    public bool RelayProcessSent { get; set; }
    public double? RelayProcessMs { get; set; }

    /// <summary>C: an echo through the tunnel to the region's landmark - B plus the relay's route to the datacentre.</summary>
    public bool DatacentreSent { get; set; }
    public double? DatacentreMs { get; set; }

    /// <summary>
    /// C': an echo through the tunnel towards the match server itself, or the last hop before it
    /// that answers. Recorded for evaluation; it does not decide verdicts. On 2026-09-15 the router
    /// it reached rate-limited its answers to about one a second and jumped to 120 ms on its own
    /// while the datacentre stayed flat - control-plane noise, not path.
    /// </summary>
    public bool ServerPathSent { get; set; }
    public double? ServerPathMs { get; set; }

    /// <summary>D: the longest silence between game packets arriving from the server, ending in this quarter second.</summary>
    public double DownGapMs { get; set; }
    public int DownPackets { get; set; }

    /// <summary>E: the longest silence between game packets leaving this PC, ending in this quarter second.</summary>
    public double UpGapMs { get; set; }
    public int UpPackets { get; set; }

    /// <summary>F: what the physical adapter carried, in Mbps, as of the latest once-a-second reading.</summary>
    public double? LineDownMbps { get; set; }
    public double? LineUpMbps { get; set; }

    /// <summary>
    /// How late the recorder itself ran this quarter second, in ms - a measure of how busy this PC was.
    ///
    /// relayd's pong and the datacentre echo are both timed inside the service, while the ICMP probes
    /// are timed by Windows. A PC too busy to schedule the service delays the first two and not the
    /// others, which reads exactly like a slow relay. This is the evidence that tells the two apart.
    /// </summary>
    public double LocalLagMs { get; set; }

    /// <summary>
    /// The way into the relay the tunnel was using this quarter second: the relay's own id, or the id
    /// of the entry it came in through. Null when nothing was being compared.
    /// </summary>
    public string? CurrentDoor { get; set; }

    /// <summary>
    /// The OTHER ways into the same relay, each timed by a Probe that left in this quarter second - see
    /// <see cref="DoorSwitchPolicy"/>. <see cref="DoorIds"/>[i] names the way <see cref="DoorSent"/>[i]
    /// and <see cref="DoorMs"/>[i] measured; the array is shared by every tick of one set of ways.
    /// Null when there are none - a relay with no entries - or entry switching is off.
    ///
    /// Comparable with <see cref="RelayProcessMs"/> and nothing else: both are a round trip to relayd
    /// itself, one down the current way and one down another.
    /// </summary>
    public string[]? DoorIds { get; set; }
    public bool[]? DoorSent { get; set; }
    public double?[]? DoorMs { get; set; }
}

public enum QualitySignal
{
    Gateway,
    RelayWire,
    RelayProcess,
    Datacentre,
    ServerPath,
    DownGap,
    UpGap,
}

/// <summary>What is normal for this session: the median of each round trip over the calm minute before.</summary>
public sealed record QualityBaseline(
    double? GatewayMs,
    double? RelayWireMs,
    double? RelayProcessMs,
    double? DatacentreMs,
    double? ServerPathMs,
    double? DownGapMs,
    double? UpGapMs)
{
    public double? Of(QualitySignal signal) => signal switch
    {
        QualitySignal.Gateway => GatewayMs,
        QualitySignal.RelayWire => RelayWireMs,
        QualitySignal.RelayProcess => RelayProcessMs,
        QualitySignal.Datacentre => DatacentreMs,
        QualitySignal.ServerPath => ServerPathMs,
        QualitySignal.DownGap => DownGapMs,
        QualitySignal.UpGap => UpGapMs,
        _ => null,
    };
}

/// <summary>
/// The verdict vocabulary. Deliberately the same keys the lag report already uses on the server,
/// plus the two only this recorder can produce, so both land in one tally.
/// </summary>
public static class SpikeVerdicts
{
    /// <summary>In the home network: Wi-Fi, the cable or the router. The router's own ping rose with everything past it.</summary>
    public const string Router = "router";

    /// <summary>On the way to the relay, past the home network: the ISP, or the route out of the country.</summary>
    public const string Relay = "relay";

    /// <summary>At the relay: relayd or its VPS - or, on an entry, the entry-to-relay hop.</summary>
    public const string RelayProcess = "relay-udp";

    /// <summary>Past the relay, between it and the game's datacentre.</summary>
    public const string Game = "game";

    /// <summary>The server stopped sending while every path measured clean.</summary>
    public const string GameServer = "game-server";

    /// <summary>The game on this PC stopped sending.</summary>
    public const string Pc = "pc";

    /// <summary>Something moved, but not in a way the measurements can place.</summary>
    public const string Unclear = "unclear";

    public static string Describe(string verdict) => verdict switch
    {
        Router => "in the home network - Wi-Fi, the cable or the router; the router's own ping rose with everything past it",
        Relay => "on the way to the relay - the player's ISP or the route out of the country (the home router was clean, or does not answer pings)",
        RelayProcess => "at the relay - the relay process or its VPS, or the entry in front of it; the wire to it was clean",
        Game => "past the relay, between it and the game's datacentre",
        GameServer => "at the game server - it went quiet while every path measured clean",
        Pc => "on this PC - the game itself stopped sending packets",
        _ => "not attributable - a measurement moved, but not in a way that places it",
    };
}

/// <summary>
/// Why a verdict is marked low confidence. Each names a way the measurements can point at a segment
/// that was not the cause - which is the one outcome worse than no verdict, because it sends the
/// analysis to the wrong machine.
/// </summary>
public static class LowConfidenceReasons
{
    /// <summary>A server silence under two probe rounds: a burst of loss between probes looks the same.</summary>
    public const string ShortSilence = "short-silence";

    /// <summary>The server went quiet while a probe on the path was lost or late: the network, not the server, may be why.</summary>
    public const string NetworkDuringSilence = "network-during-silence";

    /// <summary>Only probes were lost; the game's own packets kept arriving on time. ICMP is dropped first under load.</summary>
    public const string LossNotFelt = "loss-not-felt";

    /// <summary>This PC was busy, which delays the measurements the service times itself and blames the relay.</summary>
    public const string PcBusy = "pc-busy";

    /// <summary>
    /// A server silence in a game with no datacentre probe - VALORANT has no landmark - so nothing measured
    /// the stretch from the relay to the game, and a route problem there looks exactly like a stalled server.
    /// </summary>
    public const string NoDatacentreProbe = "no-datacentre-probe";

    public static string Describe(string? reason) => reason switch
    {
        ShortSilence => "a silence this short could also be a burst of loss that fell between two probes",
        NetworkDuringSilence => "the server went quiet while probes on the path were lost or late, so the network may be the cause",
        LossNotFelt => "only probes were lost - the game's own packets kept arriving on time",
        PcBusy => "this PC was busy at the time, which delays the measurements the service makes itself",
        NoDatacentreProbe => "nothing measures the route from the relay to this game, so a problem there looks the same as a stalled server",
        _ => "the evidence is ambiguous",
    };
}

public sealed class SpikeEvent
{
    public required DateTimeOffset StartUtc { get; init; }
    public required double DurationMs { get; init; }
    public required string Verdict { get; init; }

    /// <summary>
    /// The verdict rests on evidence that could also mean something else. <see cref="LowConfidenceReason"/>
    /// says what - see <see cref="LowConfidenceReasons"/>. Kept rather than dropped: the spike still
    /// happened, and a pattern of low-confidence spikes is itself worth seeing.
    /// </summary>
    public bool LowConfidence { get; init; }

    /// <summary>One of <see cref="LowConfidenceReasons"/>, or null when the verdict is not in doubt.</summary>
    public string? LowConfidenceReason { get; init; }

    /// <summary>The worst <see cref="QualityTick.LocalLagMs"/> around the spike.</summary>
    public double MaxLocalLagMs { get; init; }

    /// <summary>
    /// The spike was still going when it reached the sixty-second cap and was cut there. A path that
    /// stays slow for ten minutes arrives as ten of these back to back - one sustained degradation,
    /// not ten separate spikes, and an analysis has to be able to tell the two apart.
    /// </summary>
    public bool Sustained { get; init; }

    /// <summary>Which latency signal the spike was measured on: the datacentre echo when there was one, else relayd's pong.</summary>
    public required QualitySignal Measured { get; init; }

    /// <summary>The worst excess over normal on <see cref="Measured"/>. Zero for a spike that was only a silence.</summary>
    public double PeakExcessMs { get; init; }

    public double GatewayExcessMs { get; init; }
    public double RelayWireExcessMs { get; init; }
    public double RelayProcessExcessMs { get; init; }
    public double DatacentreExcessMs { get; init; }
    public double ServerPathExcessMs { get; init; }

    public int GatewayLost { get; init; }
    public int RelayWireLost { get; init; }
    public int RelayProcessLost { get; init; }
    public int DatacentreLost { get; init; }
    public int ServerPathLost { get; init; }

    /// <summary>The longest silence in the game's packets during the spike, each way.</summary>
    public double MaxDownGapMs { get; init; }
    public double MaxUpGapMs { get; init; }

    /// <summary>The silence that made this a spike, when one did: the stall itself, not just the longest gap seen.</summary>
    public double SilenceMs { get; init; }

    /// <summary>How much of the datacentre excess was already visible at relayd, 0 to 1. Null when not comparable.</summary>
    public double? CarriedToRelay { get; init; }

    /// <summary>How much of relayd's excess was already visible on the wire to the relay, 0 to 1. Null when not comparable.</summary>
    public double? CarriedToWire { get; init; }

    /// <summary>How much of the wire's excess was already visible at the home router, 0 to 1. Null when not comparable.</summary>
    public double? CarriedToGateway { get; init; }

    public double? LineDownMbps { get; init; }
    public double? LineUpMbps { get; init; }

    /// <summary>The physical line was carrying far more than a game during the spike - a download, a stream, someone else.</summary>
    public bool BusyLine { get; init; }

    public required QualityBaseline Baseline { get; init; }

    /// <summary>The spike with context either side. <see cref="EventFrom"/> and <see cref="EventTo"/> index the spike itself.</summary>
    public required IReadOnlyList<QualityTick> Ticks { get; init; }
    public required int EventFrom { get; init; }
    public required int EventTo { get; init; }
}

public sealed class MatchSummary
{
    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }
    public required double ActiveSeconds { get; init; }
    public required int Spikes { get; init; }
    public required IReadOnlyDictionary<string, int> SpikesByVerdict { get; init; }

    /// <summary>How many of <see cref="Spikes"/> were low confidence - so "this match had a spike" can mean a trusted one.</summary>
    public int LowConfidenceSpikes { get; init; }

    public double? DatacentreP50 { get; init; }
    public double? DatacentreP95 { get; init; }
    public int DatacentreSent { get; init; }
    public int DatacentreLost { get; init; }

    public double? RelayProcessP50 { get; init; }
    public double? RelayProcessP95 { get; init; }
    public int RelayProcessSent { get; init; }
    public int RelayProcessLost { get; init; }

    public double MaxDownGapMs { get; init; }
}

/// <summary>
/// Finds spikes in a stream of quarter-second ticks and says where on the path each one was.
///
/// WHY THIS EXISTS. The lag report measured twenty seconds starting when somebody pressed a button,
/// and a spike is over in two to five. By the time a player had alt-tabbed and ticked the consent
/// box the thing being reported had gone, so most reports came back "nothing found". This runs the
/// whole time a game is sending, and judges each spike from the quarter seconds it happened in.
///
/// THE METHOD is the lag report's, kept on purpose: every signal is scored against its OWN normal,
/// and a spike belongs to the innermost segment whose disturbance is still visible further out in
/// the same moment. The latency signals nest - the datacentre echo crosses relayd, relayd's pong
/// crosses the wire to the relay, the wire crosses the home router - so a delay introduced early is
/// still inside everything further out, and nothing further along can remove it. What is compared is
/// how much of the outer signal's excess the inner one already carried, tick by tick with a quarter
/// second of slack, because the probes in one tick are sent a few milliseconds apart.
///
/// WHY THE GAME'S OWN PACKETS ARE WATCHED TOO. A match server that stalls answers ICMP perfectly
/// throughout, because its kernel answers ICMP, not the game. No probe can see it. What does change
/// is that the stream of game packets stops, so a silence from the server while every probe stays
/// at normal is the one signature of the server itself. The same silence in the OTHER direction,
/// packets leaving this PC, is the game on this machine.
///
/// A SILENCE ONLY COUNTS INSIDE A STEADY STREAM, and the first real session is why. A match does not
/// stream steadily from its first second: while the map loads the client sends in bursts seconds
/// apart and the server drops to five packets a second, and the first match recorded on 2026-09-15
/// reported a 3.2 s "PC stall" that was a loading screen. At its end the last packets straggle out
/// and stop, and that was reported as a 236 ms stall. So a silence is a stall only when the stream
/// had been steady for the five seconds before it, is longer than twice anything in those five
/// seconds, and the stream comes back at its old pace afterwards.
///
/// Pure logic: no clock, no sockets, no files. The recorder in the service feeds it ticks once
/// every probe in them has had time to answer, and a console check drives it with synthetic ones.
/// </summary>
public sealed class SpikeDetector
{
    public const int TicksPerSecond = 4;
    public const double TickMs = 1000.0 / TicksPerSecond;

    /// <summary>The calm window normal is computed from.</summary>
    internal const int BaselineTicks = 60 * TicksPerSecond;

    /// <summary>No verdicts until this many calm ticks exist: a median of three samples is a guess.</summary>
    internal const int WarmupTicks = 10 * TicksPerSecond;

    /// <summary>
    /// Quiet ticks after the last triggering one before an event is closed. Triggers inside this
    /// window join the same event, so a path that bounces for four seconds is one spike, not six.
    /// </summary>
    internal const int AfterTicks = 2 * TicksPerSecond;

    internal const int BeforeTicks = 10 * TicksPerSecond;
    internal const int MaxEventTicks = 60 * TicksPerSecond;

    /// <summary>How long a stream must have been steady before a silence in it can be a stall.</summary>
    internal const int SteadyTicks = 5 * TicksPerSecond;

    /// <summary>Quarter seconds in <see cref="SteadyTicks"/> allowed to carry no packets at all.</summary>
    internal const int SteadyAllowedEmpty = 2;

    /// <summary>Ticks kept for gap context: the longest countable silence plus the steady run before it, plus the event context.</summary>
    private const int HistoryTicks = BeforeTicks + SteadyTicks + 20;

    /// <summary>
    /// What a lost probe counts as, in excess milliseconds. A loss has to take part in the same
    /// comparison as a delay - the datacentre echo lost while relayd answered on time places the
    /// fault past the relay exactly as a late one would - and 300 is larger than any delay that
    /// is not itself a loss, while small enough not to drown a real excess beside it.
    /// </summary>
    internal const double LossExcessMs = 300;

    /// <summary>Anything longer is the stream stopping - a match ending, a menu - not a spike.</summary>
    internal const double MaxGapMs = 5000;

    /// <summary>Below this a silence could have fallen between two probe rounds. See <see cref="SpikeEvent.LowConfidence"/>.</summary>
    internal const double ShortSilenceMs = 2 * TickMs;

    /// <summary>
    /// How late the recorder may run before its own measurements are suspect. Windows' timer resolution
    /// puts ordinary lateness at up to about 16 ms; three times that is a PC that was genuinely busy.
    /// </summary>
    internal const double LocalLagSuspectMs = 50;

    // The lag report's own "this line is not idle" figures, so the two agree on what busy means.
    internal const double BusyDownMbps = 20;
    internal const double BusyUpMbps = 5;

    /// <summary>
    /// How far over normal a round trip must go. 15 ms is the smallest jump a player reports; the
    /// quarter of normal keeps a long path from being judged on the jitter a short one never has.
    /// </summary>
    internal static double LatencyThreshold(double normalMs) => Math.Max(15.0, 0.25 * normalMs);

    /// <summary>
    /// How long a silence must be. A game sends tens of packets a second, so its normal gap is
    /// tens of milliseconds; 150 ms is where a stall becomes something a player sees.
    /// </summary>
    internal static double GapThreshold(double normalMs) => Math.Max(150.0, 4.0 * normalMs);

    [Flags]
    private enum Trigger
    {
        None = 0,
        Latency = 1,
        Loss = 2,
        ServerGap = 4,
        PcGap = 8,
    }

    private sealed class OpenEvent
    {
        public required QualityBaseline Baseline { get; init; }
        public required List<QualityTick> Before { get; init; }
        public List<QualityTick> Body { get; } = [];
        public long LastTriggerIndex { get; set; }
    }

    private readonly Queue<QualityTick> _calm = new();
    private readonly List<QualityTick> _history = [];
    private OpenEvent? _open;
    private long _lastIndex = long.MinValue;

    private readonly MatchAccumulator _match = new();

    private static readonly IReadOnlyList<SpikeEvent> NoEvents = [];

    /// <summary>Feeds one settled tick. Returns every event this tick closed, which is usually none.</summary>
    public IReadOnlyList<SpikeEvent> Feed(QualityTick tick)
    {
        List<SpikeEvent>? closed = null;

        // A hole in the sequence - a reconnect, a machine that slept - is not a calm stretch and not
        // part of any spike either. Whatever was open ends where the data does.
        var continuous = _lastIndex == long.MinValue || tick.Index == _lastIndex + 1;
        _lastIndex = tick.Index;
        if (!tick.Active || !continuous) Close(ref closed);

        Remember(tick);
        if (!tick.Active) return closed ?? NoEvents;

        _match.Note(tick);
        var position = _history.Count - 1;

        if (_open is null)
        {
            var baseline = CurrentBaseline();
            var trigger = baseline is null ? Trigger.None : Evaluate(_history, position, baseline);
            if (trigger == Trigger.None)
            {
                AddCalm(tick);
            }
            else
            {
                var from = Math.Max(0, position - BeforeTicks);
                _open = new OpenEvent
                {
                    Baseline = baseline!,
                    Before = _history.GetRange(from, position - from),
                    LastTriggerIndex = tick.Index,
                };
                _open.Body.Add(tick);
            }
        }
        else
        {
            _open.Body.Add(tick);
            if (Evaluate(_history, position, _open.Baseline) != Trigger.None)
            {
                _open.LastTriggerIndex = tick.Index;
            }

            if (tick.Index - _open.LastTriggerIndex >= AfterTicks || _open.Body.Count >= MaxEventTicks)
            {
                Close(ref closed);
            }
        }

        return closed ?? NoEvents;
    }

    /// <summary>Closes whatever spike is open, for a disconnect or the end of a match.</summary>
    public IReadOnlyList<SpikeEvent> Flush()
    {
        List<SpikeEvent>? closed = null;
        Close(ref closed);
        return closed ?? NoEvents;
    }

    /// <summary>
    /// Forgets what normal was, for a path that changed under the recorder - a failover to another relay,
    /// an entry, a landmark that went away - while keeping the match it happened in.
    ///
    /// Without this, normal stays the OLD path's for up to a minute. A failover from a 43 ms relay to a
    /// 70 ms one then puts every quarter second 27 ms over normal, and the log fills with spikes "on the
    /// way to the relay" that are nothing but a different relay. Closes any open spike first, on the
    /// normal it was opened with, and starts the ten-second warm-up again.
    /// </summary>
    public IReadOnlyList<SpikeEvent> Restart()
    {
        var closed = Flush();
        _calm.Clear();
        _history.Clear();
        _lastIndex = long.MinValue;
        return closed;
    }

    /// <summary>
    /// Ends the current match and returns its summary, or null when no game traffic was seen.
    /// Flush first: an open spike belongs to the match it happened in.
    /// </summary>
    public MatchSummary? EndMatch() => _match.End();

    private void Remember(QualityTick tick)
    {
        _history.Add(tick);
        if (_history.Count > 2 * HistoryTicks) _history.RemoveRange(0, _history.Count - HistoryTicks);
    }

    private void AddCalm(QualityTick tick)
    {
        _calm.Enqueue(tick);
        while (_calm.Count > BaselineTicks) _calm.Dequeue();
    }

    private void Close(ref List<SpikeEvent>? closed)
    {
        var open = _open;
        _open = null;
        if (open is null) return;

        var built = Build(open);
        if (built is null)
        {
            // Did not qualify - a lone blip, or a silence that turned out to be the stream ending.
            // Its ticks were normal enough to be part of normal.
            foreach (var tick in open.Body) AddCalm(tick);
            return;
        }

        _match.NoteSpike(built.Verdict, built.LowConfidence);
        (closed ??= []).Add(built);
    }

    // ------------------------------------------------------------------ baseline

    private QualityBaseline? CurrentBaseline()
    {
        if (_calm.Count < WarmupTicks) return null;

        var baseline = new QualityBaseline(
            Median(_calm, t => t.GatewayMs),
            Median(_calm, t => t.RelayWireMs),
            Median(_calm, t => t.RelayProcessMs),
            Median(_calm, t => t.DatacentreMs),
            Median(_calm, t => t.ServerPathMs),
            Median(_calm, t => t.DownPackets > 0 ? t.DownGapMs : null),
            Median(_calm, t => t.UpPackets > 0 ? t.UpGapMs : null));

        // Nothing to judge against without at least one round trip to the relay.
        return baseline.RelayProcessMs is null && baseline.DatacentreMs is null ? null : baseline;
    }

    private static double? Median(IEnumerable<QualityTick> ticks, Func<QualityTick, double?> select)
    {
        var values = ticks.Select(select).Where(v => v is not null).Select(v => v!.Value).ToList();
        // A signal answered in fewer than a sixth of the calm ticks has no normal worth the name.
        if (values.Count < WarmupTicks / 4) return null;
        return Percentile(values, 50);
    }

    internal static double Percentile(List<double> values, double p)
    {
        values.Sort();
        var index = (int)Math.Ceiling(p / 100.0 * values.Count) - 1;
        return values[Math.Clamp(index, 0, values.Count - 1)];
    }

    // ------------------------------------------------------------------ triggers

    /// <summary>
    /// The latency signal a tick is judged on: the datacentre echo whenever one was sent, because
    /// it covers the most path; relayd's pong otherwise. Chosen per tick, not per session, so a
    /// landmark that goes away after a failover drops the judgement to relayd straight away instead
    /// of leaving a minute of ticks that nothing measures.
    /// </summary>
    private static QualitySignal? Outer(QualityTick tick, QualityBaseline baseline)
    {
        if (tick.DatacentreSent && baseline.DatacentreMs is not null) return QualitySignal.Datacentre;
        if (tick.RelayProcessSent && baseline.RelayProcessMs is not null) return QualitySignal.RelayProcess;
        return null;
    }

    /// <summary>What <paramref name="window"/>[<paramref name="position"/>] triggers. The window supplies the stream's recent past.</summary>
    private static Trigger Evaluate(List<QualityTick> window, int position, QualityBaseline baseline)
    {
        var tick = window[position];
        var trigger = Trigger.None;

        if (Outer(tick, baseline) is { } outer && baseline.Of(outer) is { } normal)
        {
            var value = Value(tick, outer);
            if (value is { } v && v - normal >= LatencyThreshold(normal)) trigger |= Trigger.Latency;
            else if (Lost(tick, outer)) trigger |= Trigger.Loss;
        }

        if (Stall(window, position, down: true) is not null) trigger |= Trigger.ServerGap;
        if (Stall(window, position, down: false) is not null) trigger |= Trigger.PcGap;
        return trigger;
    }

    /// <summary>
    /// The silence that ended in this tick, when it is a stall in a steady stream; null otherwise.
    ///
    /// Judged against the five seconds BEFORE the silence began, not before the tick it ended in -
    /// a two-second stall leaves eight empty ticks in front of it, and those are the stall, not
    /// evidence that the stream was never steady. Normal is that window's own median rather than
    /// the session's, because a game's cadence changes with what it is doing: the server sent every
    /// 200 ms before the 2026-09-15 match started and every 25 ms during it.
    /// </summary>
    private static (double Gap, double Normal)? Stall(List<QualityTick> window, int position, bool down)
    {
        var tick = window[position];
        var packets = down ? tick.DownPackets : tick.UpPackets;
        var gap = down ? tick.DownGapMs : tick.UpGapMs;
        if (packets == 0 || gap < 150.0 || gap >= MaxGapMs) return null;

        var covered = (int)Math.Ceiling(gap / TickMs);
        var end = position - covered;            // exclusive: the silence began after this
        var start = end - SteadyTicks;
        if (start < 0) return null;

        var empty = 0;
        var largest = 0.0;
        var gaps = new List<double>(SteadyTicks);
        for (var i = start; i < end; i++)
        {
            var before = window[i];
            if (!before.Active) return null;

            var count = down ? before.DownPackets : before.UpPackets;
            if (count == 0)
            {
                if (++empty > SteadyAllowedEmpty) return null;
                continue;
            }

            var g = down ? before.DownGapMs : before.UpGapMs;
            gaps.Add(g);
            largest = Math.Max(largest, g);
        }
        if (gaps.Count == 0) return null;

        var normal = Percentile(gaps, 50);
        var threshold = Math.Max(GapThreshold(normal), 2 * largest);
        return gap >= threshold ? (gap, normal) : null;
    }

    /// <summary>
    /// Whether the stream came back at its old pace after a silence.
    ///
    /// Two ways a long gap is not a stall, and both have been seen. The stream may not come back at
    /// all - a match over, a disconnect - and the silence before its last straggling packet is just
    /// the end. Or it may come back at a different pace: a server dropping from forty packets a
    /// second to five as a match hands over to a loading screen shows one long gap and then more of
    /// them, which is a change of gear, not a pause. A stall ends and the old cadence resumes.
    ///
    /// Needs at least two ticks to look at; an event closed before it has them does not count one.
    /// </summary>
    private static bool Continues(List<QualityTick> window, int position, bool down, double normal)
    {
        var looked = 0;
        var gaps = new List<double>(AfterTicks);
        for (var i = position + 1; i < window.Count && i <= position + AfterTicks; i++)
        {
            looked++;
            var after = window[i];
            if (after.Active && (down ? after.DownPackets : after.UpPackets) > 0)
            {
                gaps.Add(down ? after.DownGapMs : after.UpGapMs);
            }
        }
        if (looked < 2 || gaps.Count * 2 < looked) return false;
        return Percentile(gaps, 50) <= Math.Max(75.0, 2 * normal);
    }

    internal static double? Value(QualityTick tick, QualitySignal signal) => signal switch
    {
        QualitySignal.Gateway => tick.GatewayMs,
        QualitySignal.RelayWire => tick.RelayWireMs,
        QualitySignal.RelayProcess => tick.RelayProcessMs,
        QualitySignal.Datacentre => tick.DatacentreMs,
        QualitySignal.ServerPath => tick.ServerPathMs,
        QualitySignal.DownGap => tick.DownPackets > 0 ? tick.DownGapMs : null,
        QualitySignal.UpGap => tick.UpPackets > 0 ? tick.UpGapMs : null,
        _ => null,
    };

    internal static bool Lost(QualityTick tick, QualitySignal signal) => signal switch
    {
        QualitySignal.Gateway => tick.GatewaySent && tick.GatewayMs is null,
        QualitySignal.RelayWire => tick.RelayWireSent && tick.RelayWireMs is null,
        QualitySignal.RelayProcess => tick.RelayProcessSent && tick.RelayProcessMs is null,
        QualitySignal.Datacentre => tick.DatacentreSent && tick.DatacentreMs is null,
        QualitySignal.ServerPath => tick.ServerPathSent && tick.ServerPathMs is null,
        _ => false,
    };

    /// <summary>Excess over normal on one signal in one tick. A loss counts as <see cref="LossExcessMs"/>.</summary>
    private static double Excess(QualityTick tick, QualitySignal signal, double normal)
    {
        if (Value(tick, signal) is { } v) return Math.Max(0, v - normal);
        return Lost(tick, signal) ? LossExcessMs : 0;
    }

    // ------------------------------------------------------------------ the verdict

    private static SpikeEvent? Build(OpenEvent open)
    {
        var baseline = open.Baseline;

        var window = new List<QualityTick>(open.Before.Count + open.Body.Count);
        window.AddRange(open.Before);
        window.AddRange(open.Body);

        // The spike runs to the last triggering tick; the quiet tail after it stays as context.
        var first = open.Before.Count;
        var lastInBody = open.Body.FindLastIndex(t => t.Index == open.LastTriggerIndex);
        var last = first + (lastInBody < 0 ? open.Body.Count - 1 : lastInBody);
        var spike = window.GetRange(first, last - first + 1);

        var latencyTicks = 0;
        var lossTicks = 0;
        var serverStall = 0.0;
        var pcStall = 0.0;
        var peak = 0.0;
        QualitySignal? measured = null;

        for (var p = first; p <= last; p++)
        {
            var tick = window[p];
            var trigger = Evaluate(window, p, baseline);
            if (trigger.HasFlag(Trigger.Latency)) latencyTicks++;
            if (trigger.HasFlag(Trigger.Loss)) lossTicks++;

            // A silence only stands if the stream came back.
            if (trigger.HasFlag(Trigger.ServerGap) && Stall(window, p, down: true) is { } server &&
                Continues(window, p, down: true, server.Normal))
            {
                serverStall = Math.Max(serverStall, server.Gap);
            }
            if (trigger.HasFlag(Trigger.PcGap) && Stall(window, p, down: false) is { } pc &&
                Continues(window, p, down: false, pc.Normal))
            {
                pcStall = Math.Max(pcStall, pc.Gap);
            }

            if (Outer(tick, baseline) is { } outer && baseline.Of(outer) is { } normal && Value(tick, outer) is { } v)
            {
                measured ??= outer;
                if (outer == measured) peak = Math.Max(peak, v - normal);
            }
        }

        measured ??= baseline.DatacentreMs is not null ? QualitySignal.Datacentre : QualitySignal.RelayProcess;
        var threshold = baseline.Of(measured.Value) is { } m ? LatencyThreshold(m) : 15.0;

        // What makes a spike worth a verdict. A single quarter second a little over the line is what
        // ICMP does now and then on a healthy path - a router answering late about itself - and
        // reporting every one would bury the spikes a player actually felt. So one tick only counts
        // when it is at least twice the threshold; otherwise it takes two. Loss likewise takes two.
        // A stall needs no second tick: 150 ms of nothing in a steady stream is already a duration.
        var delayed = latencyTicks >= 2 || (latencyTicks == 1 && peak >= 2 * threshold);
        var latency = delayed || lossTicks >= 2;
        var serverGap = serverStall > 0;
        var pcGap = pcStall > 0;
        if (!latency && !serverGap && !pcGap) return null;

        double? toRelay = null;
        double? toWire = null;
        double? toGateway = null;
        string verdict;

        if (pcGap)
        {
            // First, because it poisons every other signal: a PC that stopped the game also stops
            // answering relayd's pongs on time, and that would read as a relay fault.
            verdict = SpikeVerdicts.Pc;
        }
        else if (latency)
        {
            verdict = SpikeVerdicts.Unclear;
            var reachedRelay = true;

            if (measured == QualitySignal.Datacentre)
            {
                toRelay = Carried(spike, QualitySignal.Datacentre, QualitySignal.RelayProcess, baseline);
                if (toRelay is null) reachedRelay = false;
                else if (toRelay < 0.5)
                {
                    verdict = SpikeVerdicts.Game;
                    reachedRelay = false;
                }
            }

            if (reachedRelay)
            {
                toWire = Carried(spike, QualitySignal.RelayProcess, QualitySignal.RelayWire, baseline);
                if (toWire is { } w) verdict = w >= 0.5 ? SpikeVerdicts.Relay : SpikeVerdicts.RelayProcess;
            }

            if (verdict == SpikeVerdicts.Relay)
            {
                // The home router only ever narrows a verdict it did not raise. One that does not
                // answer pings - plenty do not - leaves the verdict where the wire put it.
                toGateway = Carried(spike, QualitySignal.RelayWire, QualitySignal.Gateway, baseline);
                if (toGateway >= 0.5) verdict = SpikeVerdicts.Router;
            }
        }
        else
        {
            // Only a silence from the server, with every round trip at normal. The one thing no
            // probe can see, and the reason these gaps are watched at all.
            verdict = SpikeVerdicts.GameServer;
        }

        var silence = pcGap ? pcStall : serverGap ? serverStall : 0;
        var maxDownGap = spike.Where(t => t.DownPackets > 0).Select(t => t.DownGapMs).DefaultIfEmpty(0).Max();
        var maxUpGap = spike.Where(t => t.UpPackets > 0).Select(t => t.UpGapMs).DefaultIfEmpty(0).Max();
        var lineDown = spike.Max(t => t.LineDownMbps);
        var lineUp = spike.Max(t => t.LineUpMbps);

        // ---------------------------------------------------------------- how sure

        string? reason = null;

        if (verdict == SpikeVerdicts.GameServer)
        {
            // The one verdict no probe can confirm, so it is the one that must have nothing against it.
            // A loss burst on the way back from the relay silences the game's packets just as a stalled
            // server does, and if a probe crossed the path during the silence and was lost or late, the
            // path is at least as likely a cause. Placing it on the server then would send the analysis
            // to the one party that cannot be asked.
            var covered = (int)Math.Ceiling(silence / TickMs);
            if (ProbesTroubled(window, Math.Max(0, first - covered), last, baseline))
            {
                verdict = SpikeVerdicts.Unclear;
                reason = LowConfidenceReasons.NetworkDuringSilence;
            }
            else if (baseline.DatacentreMs is null)
            {
                reason = LowConfidenceReasons.NoDatacentreProbe;
            }
            else if (silence < ShortSilenceMs)
            {
                reason = LowConfidenceReasons.ShortSilence;
            }
        }

        var localLag = 0.0;
        for (var p = Math.Max(0, first - 1); p <= Math.Min(window.Count - 1, last + 1); p++)
        {
            localLag = Math.Max(localLag, window[p].LocalLagMs);
        }
        if (reason is null && localLag >= LocalLagSuspectMs && verdict is SpikeVerdicts.RelayProcess or SpikeVerdicts.Game)
        {
            // Both verdicts rest on relayd's pong or the datacentre echo rising while Windows' own ICMP
            // did not - which is also exactly what a PC too busy to run the service looks like.
            reason = LowConfidenceReasons.PcBusy;
        }

        if (reason is null && !delayed && lossTicks >= 2 && !serverGap && !pcGap && !GameFeltIt(window, first, last, baseline))
        {
            // Probes lost, nothing late, and the game's own stream never faltered. ICMP is what a busy
            // router drops first, so this may be a measurement that failed rather than a path that did.
            reason = LowConfidenceReasons.LossNotFelt;
        }

        // A silence is recorded when it ends, so a spike that is only a silence began that long before.
        var start = spike[0].StartUtc;
        if (!latency) start -= TimeSpan.FromMilliseconds(silence);

        return new SpikeEvent
        {
            StartUtc = start,
            DurationMs = Math.Max(latency ? spike.Count * TickMs : 0, silence),
            Verdict = verdict,
            LowConfidence = reason is not null,
            LowConfidenceReason = reason,
            MaxLocalLagMs = localLag,
            Sustained = open.Body.Count >= MaxEventTicks && lastInBody == open.Body.Count - 1,
            Measured = measured.Value,
            PeakExcessMs = latency ? PeakExcess(spike, measured.Value, baseline) : 0,
            GatewayExcessMs = PeakExcess(spike, QualitySignal.Gateway, baseline),
            RelayWireExcessMs = PeakExcess(spike, QualitySignal.RelayWire, baseline),
            RelayProcessExcessMs = PeakExcess(spike, QualitySignal.RelayProcess, baseline),
            DatacentreExcessMs = PeakExcess(spike, QualitySignal.Datacentre, baseline),
            ServerPathExcessMs = PeakExcess(spike, QualitySignal.ServerPath, baseline),
            GatewayLost = spike.Count(t => Lost(t, QualitySignal.Gateway)),
            RelayWireLost = spike.Count(t => Lost(t, QualitySignal.RelayWire)),
            RelayProcessLost = spike.Count(t => Lost(t, QualitySignal.RelayProcess)),
            DatacentreLost = spike.Count(t => Lost(t, QualitySignal.Datacentre)),
            ServerPathLost = spike.Count(t => Lost(t, QualitySignal.ServerPath)),
            MaxDownGapMs = maxDownGap,
            MaxUpGapMs = maxUpGap,
            SilenceMs = silence,
            CarriedToRelay = toRelay,
            CarriedToWire = toWire,
            CarriedToGateway = toGateway,
            LineDownMbps = lineDown,
            LineUpMbps = lineUp,
            BusyLine = lineDown > BusyDownMbps || lineUp > BusyUpMbps,
            Baseline = baseline,
            Ticks = window,
            EventFrom = first,
            EventTo = last,
        };
    }

    /// <summary>Whether any probe on the path to the datacentre was lost or over threshold between two ticks.</summary>
    private static bool ProbesTroubled(List<QualityTick> window, int from, int to, QualityBaseline baseline)
    {
        foreach (var signal in new[] { QualitySignal.RelayWire, QualitySignal.RelayProcess, QualitySignal.Datacentre })
        {
            if (baseline.Of(signal) is not { } normal) continue;
            for (var p = from; p <= to && p < window.Count; p++)
            {
                var tick = window[p];
                if (!tick.Active) continue;
                if (Lost(tick, signal)) return true;
                if (Value(tick, signal) is { } v && v - normal >= LatencyThreshold(normal)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether the game's own packets from the server faltered during a spike or just after it: a gap
    /// well past normal, or a quarter second with none at all.
    /// </summary>
    private static bool GameFeltIt(List<QualityTick> window, int first, int last, QualityBaseline baseline)
    {
        var normal = baseline.DownGapMs ?? 25;
        var limit = Math.Max(100.0, 3 * normal);
        for (var p = first; p <= Math.Min(window.Count - 1, last + 2); p++)
        {
            var tick = window[p];
            if (!tick.Active) continue;
            if (tick.DownPackets == 0 || tick.DownGapMs >= limit) return true;
        }
        return false;
    }

    /// <summary>Largest excess over normal, not counting losses - those are reported as counts.</summary>
    private static double PeakExcess(List<QualityTick> ticks, QualitySignal signal, QualityBaseline baseline)
    {
        if (baseline.Of(signal) is not { } normal) return 0;
        var peak = 0.0;
        foreach (var tick in ticks)
        {
            if (Value(tick, signal) is { } v) peak = Math.Max(peak, v - normal);
        }
        return peak;
    }

    /// <summary>
    /// The share of <paramref name="outer"/>'s excess that <paramref name="inner"/> already showed in
    /// the same quarter second, give or take one. Null when either has no normal to compare against,
    /// or the outer signal never rose - neither of which is evidence about where a spike was.
    ///
    /// Half is enough to call it carried. The inner probe cannot remove delay from a path it is part
    /// of, but it can land a moment before or after a short burst that the outer one caught.
    /// </summary>
    internal static double? Carried(List<QualityTick> ticks, QualitySignal outer, QualitySignal inner, QualityBaseline baseline)
    {
        if (baseline.Of(outer) is not { } outerNormal || baseline.Of(inner) is not { } innerNormal) return null;

        var total = 0.0;
        var carried = 0.0;
        for (var i = 0; i < ticks.Count; i++)
        {
            var excess = Excess(ticks[i], outer, outerNormal);
            if (excess <= 0) continue;
            total += excess;

            var outerLost = Lost(ticks[i], outer);
            var seen = 0.0;
            for (var j = Math.Max(0, i - 1); j <= Math.Min(ticks.Count - 1, i + 1); j++)
            {
                seen = Math.Max(seen, InnerExcess(ticks[j], inner, innerNormal, outerLost));
            }
            carried += Math.Min(excess, seen);
        }

        return total <= 0 ? null : carried / total;
    }

    /// <summary>
    /// The inner signal's excess, for <see cref="Carried"/>. A delay always counts. A LOSS counts only
    /// when it is evidence about the path the game uses.
    ///
    /// relayd's pong is UDP on the game's own path, so its loss is. An ICMP probe's loss - to the home
    /// router, or to the relay's kernel - is not on its own: home routers commonly answer only a ping or
    /// two a second, and ISPs police ICMP first when a link is busy. Counting that loss as the inner
    /// segment "carrying" an outer delay would place a congested ISP route on the player's router, or a
    /// slow relay on the route to it. So an ICMP loss counts only in a quarter second where the outer
    /// probe was lost as well - everything gone at once, which is a real outage.
    /// </summary>
    private static double InnerExcess(QualityTick tick, QualitySignal inner, double normal, bool outerLost)
    {
        if (Value(tick, inner) is { } v) return Math.Max(0, v - normal);
        if (!Lost(tick, inner)) return 0;
        return inner == QualitySignal.RelayProcess || outerLost ? LossExcessMs : 0;
    }

    // ------------------------------------------------------------------ the match

    private sealed class MatchAccumulator
    {
        private DateTimeOffset? _start;
        private DateTimeOffset _end;
        private int _activeTicks;
        private readonly List<double> _datacentre = [];
        private readonly List<double> _relayProcess = [];
        private int _datacentreSent, _datacentreLost, _relayProcessSent, _relayProcessLost;
        private double _maxDownGap;
        private readonly Dictionary<string, int> _spikes = [];
        private int _lowConfidence;

        /// <summary>Four hours of quarter seconds. Past it the percentiles stop growing, the counts do not.</summary>
        private const int MaxSamples = 4 * 3600 * TicksPerSecond;

        public void Note(QualityTick tick)
        {
            _start ??= tick.StartUtc;
            _end = tick.StartUtc.AddMilliseconds(TickMs);
            _activeTicks++;

            if (tick.DatacentreSent)
            {
                _datacentreSent++;
                if (tick.DatacentreMs is { } c) { if (_datacentre.Count < MaxSamples) _datacentre.Add(c); }
                else _datacentreLost++;
            }
            if (tick.RelayProcessSent)
            {
                _relayProcessSent++;
                if (tick.RelayProcessMs is { } b) { if (_relayProcess.Count < MaxSamples) _relayProcess.Add(b); }
                else _relayProcessLost++;
            }
            if (tick.DownPackets > 0 && tick.DownGapMs < MaxGapMs) _maxDownGap = Math.Max(_maxDownGap, tick.DownGapMs);
        }

        public void NoteSpike(string verdict, bool lowConfidence)
        {
            _spikes[verdict] = _spikes.GetValueOrDefault(verdict) + 1;
            if (lowConfidence) _lowConfidence++;
        }

        public MatchSummary? End()
        {
            if (_start is not { } start || _activeTicks == 0)
            {
                Reset();
                return null;
            }

            var summary = new MatchSummary
            {
                StartUtc = start,
                EndUtc = _end,
                ActiveSeconds = _activeTicks * TickMs / 1000.0,
                Spikes = _spikes.Values.Sum(),
                SpikesByVerdict = new Dictionary<string, int>(_spikes),
                LowConfidenceSpikes = _lowConfidence,
                DatacentreP50 = _datacentre.Count == 0 ? null : Percentile(_datacentre, 50),
                DatacentreP95 = _datacentre.Count == 0 ? null : Percentile(_datacentre, 95),
                DatacentreSent = _datacentreSent,
                DatacentreLost = _datacentreLost,
                RelayProcessP50 = _relayProcess.Count == 0 ? null : Percentile(_relayProcess, 50),
                RelayProcessP95 = _relayProcess.Count == 0 ? null : Percentile(_relayProcess, 95),
                RelayProcessSent = _relayProcessSent,
                RelayProcessLost = _relayProcessLost,
                MaxDownGapMs = _maxDownGap,
            };
            Reset();
            return summary;
        }

        private void Reset()
        {
            _start = null;
            _activeTicks = 0;
            _datacentre.Clear();
            _relayProcess.Clear();
            _datacentreSent = _datacentreLost = _relayProcessSent = _relayProcessLost = 0;
            _maxDownGap = 0;
            _spikes.Clear();
            _lowConfidence = 0;
        }
    }
}
