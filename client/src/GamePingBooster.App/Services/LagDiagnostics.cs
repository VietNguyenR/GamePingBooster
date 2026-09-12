using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Native;

namespace GamePingBooster.App.Services;

/// <summary>
/// Finds WHERE a lag spike is, while it is happening, and produces a report that can be sent.
///
/// A C# port of tools\Diagnose-Lag.ps1, kept faithful to its method because that method is the
/// part that took the work: every segment of the path is sampled at the SAME moment and scored
/// against the segment next to it, rather than against an absolute number. 8 ms of jitter to
/// your own router is a catastrophe; 8 ms of jitter to Singapore is a Tuesday.
///
/// IT HAS SINCE MOVED AHEAD OF THE SCRIPT, in three places and deliberately: rungs are compared
/// tick by tick rather than window against window (see Inherits), the spike threshold no longer
/// scales without limit (see BadReason), and rung 4 exists at all (see InternationalRung). All
/// three came out of one report on 2026-09-12 that blamed a player Wi-Fi network and a player ISP
/// for five seconds of trouble on the way to Singapore. The PowerShell still has the old method
/// and will still say isp-access to the same numbers.
///
/// The rungs:
///
///   1  home router        the default gateway - Wi-Fi, cable, the box in the hallway
///   2  ISP access         first hop past the router, or the CGNAT address if there is one
///   3  ISP domestic core  the last hop before the RTT jumps, i.e. still inside the country
///   4  international      rung 5 minus rung 3, tick by tick: the leg out of the country
///   5  relay              the relay's public address, over the physical path
///   6  relay process      the service's own live keepalive RTT, read from its status
///   7  in game            the service's measured game ping, read from its status
///
/// RUNG 4 IS DERIVED, NOT PROBED. The international leg is the one stretch of this path with no
/// address to ping - it is precisely the gap between the last domestic hop and the far end - so
/// it is measured by subtracting rung 3 from rung 5 in each tick, which is only legitimate
/// because those two are sampled inside the same one-second sweep. Until it existed the whole
/// international path lived inside rung 5's tolerance, and that tolerance scales with rung 5's
/// own distance: a relay 59 ms away had to spike by another 59 ms before anything was said. The
/// report of 2026-09-12 spiked by 67 ms there and the verdict landed on the player's ISP.
///
/// Rungs 6 and 7 are READ from the running service, never measured by opening a session of our
/// own. A second handshake during a match can land inside the relay's session-resume window and
/// knock the live client off its own session - diagnosing a lag spike by causing a worse one.
/// For the same reason nothing here disconnects, pauses or reconfigures the tunnel: the whole
/// method needs the tunnel carrying real traffic at the moment of measurement, and a report
/// taken with the tunnel down would have four empty rungs and nothing to compare.
///
/// TWO THINGS THE POWERSHELL VERSION HAS AND THIS DOES NOT, both deliberate:
///
///   - No local baseline. The script keeps a history under %LOCALAPPDATA% and, after three runs
///     taken while things felt fine, scores p50 against this connection's own normal. A button
///     labelled "Report lag" is pressed during lag and almost never while healthy, so that
///     history would stay empty exactly when it is needed. The server has the better version of
///     the same idea - many reports from many connections - which is why the raw samples are
///     uploaded and not only the verdict.
///   - No landmark. The lateral control needs a second address in the game's region, and those
///     live in the profile, which is sealed to the service's device key and cannot be read here.
///     It costs the "is the whole international path bad, or just this provider" nuance. The
///     verdict itself is unaffected: the script excludes the landmark from the ladder too.
/// </summary>
public static class LagDiagnostics
{
    /// <summary>Default sampling window. Long enough to see jitter, short enough to sit through.</summary>
    public const int DefaultSeconds = 20;

    private const int TraceMaxHops = 20;
    private const int TraceTimeoutMs = 1000;
    private const int PingTimeoutMs = 1000;

    // ------------------------------------------------------------------ shapes

    public sealed record Stats(
        [property: JsonPropertyName("sent")] int Sent,
        [property: JsonPropertyName("received")] int Received,
        [property: JsonPropertyName("lossPct")] double LossPct,
        [property: JsonPropertyName("p50")] double? P50,
        [property: JsonPropertyName("p95")] double? P95,
        [property: JsonPropertyName("max")] double? Max,
        [property: JsonPropertyName("jitter")] double? Jitter);

    public sealed record Rung(
        [property: JsonPropertyName("rung")] int Number,
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("address")] string? Address,

        /// <summary>
        /// The adapter this rung's packets actually leave by, as Windows decides it.
        ///
        /// Worth a column of its own because a rung leaving by the WRONG adapter is a whole class
        /// of fault that no amount of latency measurement can see. The relay leaving by the tunnel
        /// rather than the physical card is the loudest example - see RelayTunnelled.
        /// </summary>
        [property: JsonPropertyName("via")] string? Via,

        [property: JsonPropertyName("stats")] Stats? Stats,
        [property: JsonPropertyName("bad")] string? BadReason,
        [property: JsonPropertyName("samples")] IReadOnlyList<double> Samples,

        /// <summary>
        /// The same run with one slot per second of the window and null where nothing came back,
        /// so two rungs can be laid against each other TICK BY TICK.
        ///
        /// Samples alone cannot do that: a lost packet is simply absent from the list, so a single
        /// timeout slides every later sample against its neighbour's and the comparison silently
        /// starts reading the wrong second. Not serialised - the wire format carries what was
        /// received, and the alignment is only needed while the verdict is being decided.
        /// </summary>
        [property: JsonIgnore] IReadOnlyList<double?> Ticks,

        /// <summary>
        /// True for the rungs read from the service's status rather than pinged in the sweep.
        /// Those are a snapshot up to one push old, so they are allowed a tick of slack whenever
        /// their timing is compared with anything else.
        /// </summary>
        [property: JsonIgnore] bool StatusDerived = false,

        /// <summary>
        /// True for a rung computed from two others rather than measured. Such a rung can BE the
        /// verdict, but it can never be used to contradict one: rung 4 has rung 3 subtracted out
        /// of it by construction, so a genuine fault at rung 3 leaves it perfectly flat.
        /// </summary>
        [property: JsonIgnore] bool Derived = false);

    public sealed record Hop(
        [property: JsonPropertyName("ttl")] int Ttl,
        [property: JsonPropertyName("address")] string Address,
        [property: JsonPropertyName("rttMs")] double? RttMs,
        [property: JsonPropertyName("class")] string Class);

    public sealed record Report(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("takenUtc")] DateTimeOffset TakenUtc,
        [property: JsonPropertyName("seconds")] int Seconds,
        [property: JsonPropertyName("verdict")] string Verdict,
        [property: JsonPropertyName("verdictText")] string VerdictText,
        [property: JsonPropertyName("relayName")] string? RelayName,
        [property: JsonPropertyName("relayEndpoint")] string? RelayEndpoint,
        [property: JsonPropertyName("gameRegion")] string? GameRegion,
        [property: JsonPropertyName("tunnelState")] string? TunnelState,
        [property: JsonPropertyName("linkType")] string? LinkType,
        [property: JsonPropertyName("gameRunning")] bool GameRunning,

        /// <summary>
        /// How much slower the relay's own answers were than ICMP to the same box, or null when
        /// either was not measured. Sent whether or not it crossed the threshold: the threshold is
        /// a guess made with no data, and the only way to replace it with a real one is to collect
        /// the number from connections that are working as well as from ones that are not.
        /// </summary>
        [property: JsonPropertyName("relayProcessGapMs")] double? RelayProcessGapMs,

        /// <summary>
        /// What this PC pushed through its own network card while measuring, in Mbps.
        ///
        /// The single most common cause of lag that the player does not know about: a download, a
        /// game patch, a cloud sync, someone else in the house. Every rung can be perfectly clean
        /// and the game still stutter, because the line is full. Measured on the PHYSICAL adapter,
        /// so tunnel traffic is counted once where it really goes.
        /// </summary>
        [property: JsonPropertyName("pcDownMbps")] double? PcDownMbps,
        [property: JsonPropertyName("pcUpMbps")] double? PcUpMbps,

        /// <summary>
        /// Packets the SERVICE threw away inside this PC during the window, from its own counters.
        ///
        /// Not the network by definition - these never reached a wire. A number here means the
        /// per-cause breakdown in the service log is the next thing to read, and it separates
        /// "the path is bad" from "this machine is dropping its own traffic" without guessing.
        /// </summary>
        [property: JsonPropertyName("serviceDropped")] long? ServiceDropped,

        /// <summary>
        /// True when the relay's own address is leaving by the TUNNEL instead of the physical card.
        ///
        /// That is the pinned /32 having failed, and it is not a latency problem - it is a routing
        /// loop waiting to happen, and it makes rungs 5 and 6 measure the same thing so the report
        /// silently stops being able to separate the wire from the box. Exactly the fault that
        /// took a day on 2026-09-12 before anyone looked at a routing table.
        /// </summary>
        [property: JsonPropertyName("relayTunnelled")] bool RelayTunnelled,
        [property: JsonPropertyName("rungs")] IReadOnlyList<Rung> Rungs,
        [property: JsonPropertyName("trace")] IReadOnlyList<Hop> Trace,
        [property: JsonPropertyName("notes")] IReadOnlyList<string> Notes);

    // ------------------------------------------------------------------ statistics

    internal static double? Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToArray();
        var index = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    /// <summary>
    /// Mean absolute difference between consecutive samples, not the standard deviation.
    ///
    /// Standard deviation measures spread around an average, which a slow steady drift produces
    /// just as well as a packet that arrives 90 ms late. A game only ever feels the second one:
    /// what breaks interpolation is the change from one packet to the next. This is the same
    /// quantity RTP calls interarrival jitter, for the same reason.
    /// </summary>
    internal static double? Jitter(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return null;
        var sum = 0.0;
        for (var i = 1; i < values.Count; i++) sum += Math.Abs(values[i] - values[i - 1]);
        return sum / (values.Count - 1);
    }

    internal static Stats Summarise(IReadOnlyList<double> samples, int sent)
    {
        var received = samples.Count;
        var loss = sent > 0 ? 100.0 * (sent - received) / sent : 0.0;
        if (received == 0) return new Stats(sent, 0, loss, null, null, null, null);

        return new Stats(
            sent, received, loss,
            Percentile(samples, 50),
            Percentile(samples, 95),
            samples.Max(),
            Jitter(samples) ?? 0.0);
    }

    /// <summary>
    /// Why this rung looks unhealthy, or null when it does not.
    ///
    /// Thresholds scale with distance, because the same millisecond means different things at
    /// different distances. Loss does not scale: a lost packet is a lost packet at any range, and
    /// past a point neither does a spike - see the cap below.
    /// </summary>
    internal static string? BadReason(Stats? s)
    {
        if (s is null || s.Received == 0 || s.P50 is not { } p50 || s.P95 is not { } p95) return null;

        var jitterLimit = Math.Max(4.0, 0.25 * p50);

        // Scales with distance, but only so far. What breaks interpolation is a packet arriving
        // 50 ms late, and the game does not care whether the rung it happened on sits 5 ms away
        // or 60. Uncapped this read "max(15, p50)", which meant a 59 ms rung had to spike by
        // another 59 ms before it was worth mentioning: on 2026-09-12 the relay rung reached
        // 126 ms, missed that bar by six milliseconds, and was reported as healthy.
        var spikeLimit = Math.Clamp(0.5 * p50, 15.0, 30.0);

        var reasons = new List<string>();
        if (s.LossPct > 2.0) reasons.Add($"{s.LossPct:N0}% loss");
        if (s.Jitter is { } j && j > jitterLimit) reasons.Add($"jitter {j:N1} ms");
        if (p95 - p50 > spikeLimit) reasons.Add($"spikes to {p95:N0} ms");

        return reasons.Count == 0 ? null : string.Join(", ", reasons);
    }

    /// <summary>
    /// Does the symptom seen at <paramref name="inner"/> survive out to <paramref name="outer"/>?
    ///
    /// Physically it has to. Every packet that reaches the outer target has already crossed the
    /// inner one, so delay introduced at the inner rung is still in the outer rung's numbers -
    /// nothing further along the path can undo it. When the outer rung sits at its own normal
    /// during the very seconds the inner one was disturbed, the inner reading was never about the
    /// path at all: it is a router answering pings to its own address slowly while forwarding
    /// everything else perfectly. That is normal, it is what most routers do, and it is the single
    /// most common way a tool like this blames the wrong box.
    ///
    /// TICK BY TICK, not window against window. The previous version compared the two rungs'
    /// jitter across the whole twenty seconds, so an inner rung passed as long as the outer one
    /// was unsettled SOMEWHERE - by anything at all, including a fault with no connection to it.
    /// That is exactly how the report of 2026-09-12 blamed a player's ISP access network: its
    /// 4.4 ms of jitter came from two ticks that moved the domestic core by nothing whatsoever,
    /// and it was waved through on jitter the core had picked up one tick earlier from a single
    /// unrelated 99 ms answer of its own.
    ///
    /// What is compared is how much of the inner rung's excess over its OWN median is visible in
    /// the same tick further out. Half of it is enough to pass: a stretch of path cannot carry a
    /// disturbance away, but a probe can land just after one, and the rungs in a sweep are pinged
    /// in sequence rather than all at once.
    ///
    /// Deliberately NOT "is the outer rung also bad by its own threshold". Those thresholds scale
    /// with distance, so by the time a fault at the home router reaches the relay rung it is well
    /// inside the relay's tolerance.
    /// </summary>
    internal static bool Inherits(Rung inner, Rung outer)
    {
        if (inner.Stats is not { } innerStats || innerStats.P50 is not { } innerP50) return true;

        // A mute outer rung contradicts nothing; silence is not evidence either way.
        if (outer.Stats is not { Received: > 0 } outerStats || outerStats.P50 is not { } outerP50) return true;

        // Loss propagates strictly. One point of slack for rounding on a short window.
        if (outerStats.LossPct + 1.0 < innerStats.LossPct) return false;

        // A rung read from the service's status is a snapshot up to one push old, so it is allowed
        // to show the disturbance a tick either side. Rungs pinged in the same sweep are aligned to
        // a few hundred milliseconds and get no such licence.
        var slack = inner.StatusDerived || outer.StatusDerived ? 1 : 0;

        var ticks = Math.Min(inner.Ticks.Count, outer.Ticks.Count);
        var total = 0.0;
        var carried = 0.0;

        for (var t = 0; t < ticks; t++)
        {
            if (inner.Ticks[t] is not { } innerValue) continue;

            var excess = innerValue - innerP50;
            if (excess <= 0) continue;
            total += excess;

            var seen = 0.0;
            for (var u = Math.Max(0, t - slack); u <= Math.Min(ticks - 1, t + slack); u++)
            {
                if (outer.Ticks[u] is { } outerValue) seen = Math.Max(seen, outerValue - outerP50);
            }

            carried += Math.Min(excess, Math.Max(0.0, seen));
        }

        // Nothing to disprove. A rung flagged on jitter that never actually rose above its own
        // median has made no claim the outer rung is in a position to contradict.
        if (total < 10.0) return true;

        return carried >= 0.5 * total;
    }

    /// <summary>
    /// Rung 4: what is left of the round trip once the domestic part is subtracted out, tick by
    /// tick. Null when there is no domestic rung to subtract, or too little that survives it.
    ///
    /// The international leg is the one stretch of this path with no address to ping - it IS the
    /// gap between the last domestic hop and the far end - so it is the one rung that has to be
    /// arrived at rather than measured. The subtraction is only honest because both inputs come
    /// from the same one-second sweep, a few hundred milliseconds apart.
    ///
    /// It exists because nothing else on the ladder can see this segment. Rung 5's thresholds are
    /// sized for rung 5's distance, so an international leg swinging by 50 ms disappears inside a
    /// 59 ms relay rung, and the verdict then falls to whichever domestic rung happened to look
    /// worst - which is how a player's ISP took the blame for a bad night on a submarine cable in
    /// the report of 2026-09-12.
    /// </summary>
    internal static Rung? InternationalRung(Rung? core, Rung? relay)
    {
        if (core is null || relay is null) return null;

        var ticks = new List<double?>();
        var samples = new List<double>();
        var paired = Math.Min(core.Ticks.Count, relay.Ticks.Count);

        for (var t = 0; t < paired; t++)
        {
            // A tick where the NEAR hop answered slower than the far one says nothing about what
            // lies between them and everything about the near hop's own ICMP handling. Dropped
            // rather than floored at zero: a floor would invent an excursion where there was only
            // a router being slow about its own address.
            double? value = core.Ticks[t] is { } near && relay.Ticks[t] is { } far && far >= near
                ? far - near
                : null;

            ticks.Add(value);
            if (value is { } usable) samples.Add(usable);
        }

        // Too few usable pairs and the percentiles are a guess dressed up as a measurement.
        if (samples.Count < 5) return null;

        // Sent equals received deliberately. Loss belongs to whichever real rung lost the packet
        // and is reported there; counting it again here would invent a second fault with no owner.
        var stats = Summarise(samples, samples.Count);

        return new Rung(4, "international", "international leg", null, null,
            stats, BadReason(stats), samples, ticks, StatusDerived: false, Derived: true);
    }

    /// <summary>
    /// The innermost rung that is bad AND stays bad all the way out, or "clean".
    ///
    /// A fault propagates outward, so anything bad with calm rungs beyond it is a router
    /// deprioritising pings to itself rather than a fault on the path - see Inherits, which is
    /// where that judgement is actually made.
    /// </summary>
    internal static string Culprit(IReadOnlyList<Rung> ladder)
    {
        for (var i = 0; i < ladder.Count; i++)
        {
            if (ladder[i].BadReason is null) continue;

            var survives = true;
            for (var j = i + 1; j < ladder.Count; j++)
            {
                // A derived rung is not evidence about the rungs inside it. Rung 4 has rung 3
                // subtracted out of it by construction, so a real fault at rung 3 leaves rung 4
                // flat - and letting it vote would make every domestic verdict impossible.
                if (ladder[j].Derived) continue;
                if (!Inherits(ladder[i], ladder[j])) { survives = false; break; }
            }

            if (survives) return ladder[i].Key;
        }

        return "clean";
    }

    // ------------------------------------------------------------------ addresses

    /// <summary>
    /// Which part of the world an address belongs to, for picking the ISP rungs.
    ///
    /// 100.64/10 is carrier-grade NAT. It is not the public internet and it is not the house
    /// either - it is the ISP's access network, and in Vietnam it is extremely common. Calling it
    /// public would put the ISP's own equipment in the wrong rung.
    /// </summary>
    internal static string AddressClass(string text)
    {
        if (!IPAddress.TryParse(text, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            return "other";
        }

        var b = address.GetAddressBytes();
        if (b[0] is 10 or 127) return "private";
        if (b[0] == 192 && b[1] == 168) return "private";
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return "private";
        if (b[0] == 169 && b[1] == 254) return "private";
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return "cgnat";
        return "public";
    }

    /// <summary>
    /// Traceroute done with raw TTLs rather than by parsing tracert.exe, whose output is
    /// localised - on a Vietnamese Windows the words change and a regex written against the
    /// English text finds nothing.
    ///
    /// The hop's RTT is timed with a Stopwatch and NOT taken from PingReply.RoundtripTime. That
    /// property is only populated when Status is Success; every TtlExpired reply - which is every
    /// intermediate hop - reports 0. Trusting it made the entire trace look like a flat 0 ms, so
    /// the international step was never found and the domestic rung silently vanished.
    /// </summary>
    internal static async Task<List<Hop>> TraceAsync(string target, CancellationToken ct)
    {
        var hops = new List<Hop>();
        var payload = new byte[32];
        using var ping = new Ping();

        for (var ttl = 1; ttl <= TraceMaxHops; ttl++)
        {
            ct.ThrowIfCancellationRequested();

            string? address = null;
            double? best = null;
            var arrived = false;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var clock = Stopwatch.StartNew();
                PingReply? reply = null;
                try
                {
                    reply = await ping.SendPingAsync(target, TraceTimeoutMs, payload,
                        new PingOptions(ttl, false)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A hop that cannot be reached tells us nothing; the trace carries on.
                }
                clock.Stop();

                if (reply is null) continue;
                if (reply.Status is not (IPStatus.TtlExpired or IPStatus.Success)) continue;

                address = reply.Address.ToString();
                var rtt = clock.Elapsed.TotalMilliseconds;
                if (best is null || rtt < best) best = rtt;
                if (reply.Status == IPStatus.Success) arrived = true;
            }

            if (address is not null) hops.Add(new Hop(ttl, address, best, AddressClass(address)));
            if (arrived) break;
        }

        return hops;
    }

    /// <summary>
    /// The domestic/international boundary, derived from the trace rather than from a hardcoded
    /// list of Vietnamese addresses that would rot within a year.
    ///
    /// A submarine cable is the only thing on this path that adds tens of milliseconds in a
    /// single hop, so the largest RTT step in the trace is where the country ends. Below 15 ms
    /// there is no step worth calling one, and this returns nothing - the domestic rung is
    /// skipped rather than invented.
    ///
    /// PRIVATE AND CGNAT HOPS COUNT. They are the ISP's own routers, and on a Vietnamese consumer
    /// line the entire domestic path can be numbered out of 10/8, 172.16/12 and 100.64/10 -
    /// measured on Viettel, hops 2-5 are private at 3-10 ms and the first PUBLIC hop is already
    /// 44 ms, i.e. already across the cable.
    ///
    /// The ceiling is a fraction of the RELAY's own RTT, not of the next hop: a trace that steps
    /// from Singapore to somewhere further still has a perfectly good-looking step in it, and the
    /// near side of that one is 44 ms away in another country. Better no domestic rung than a
    /// fictional one.
    /// </summary>
    internal static Hop? DomesticHop(IReadOnlyList<Hop> hops, double? relayRtt)
    {
        var seen = hops.Where(h => h.RttMs is not null).ToArray();
        if (seen.Length < 2) return null;

        var bestGap = 0.0;
        var bestIndex = -1;
        for (var i = 1; i < seen.Length; i++)
        {
            var gap = seen[i].RttMs!.Value - seen[i - 1].RttMs!.Value;
            if (gap > bestGap) { bestGap = gap; bestIndex = i; }
        }
        if (bestIndex < 1 || bestGap < 15.0) return null;

        var near = seen[bestIndex - 1];
        var far = seen[bestIndex];

        var ceiling = 0.7 * far.RttMs!.Value;
        if (relayRtt is { } r && r > 0) ceiling = Math.Min(ceiling, 0.6 * r);

        return near.RttMs!.Value > ceiling ? null : near;
    }

    /// <summary>
    /// Best of a handful of ordinary echoes. Unlike the TTL walk this asks for Status=Success, so
    /// PingReply.RoundtripTime is populated and there is nothing to time by hand.
    /// </summary>
    internal static async Task<double?> BestRttAsync(string address, int count, CancellationToken ct)
    {
        double? best = null;
        using var ping = new Ping();
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(address, PingTimeoutMs).ConfigureAwait(false);
                if (reply.Status != IPStatus.Success) continue;
                if (best is null || reply.RoundtripTime < best) best = reply.RoundtripTime;
            }
            catch (Exception)
            {
                // Silence here is not a failure of the run - the ceiling simply falls back.
            }
        }
        return best;
    }

    /// <summary>The name of an adapter by its IPv4 interface index, or null when it is gone.</summary>
    private static string? AdapterName(uint index)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.GetIPProperties().GetIPv4Properties()?.Index == (int)index) return nic.Name;
            }
            catch (NetworkInformationException)
            {
                // No IPv4 on this adapter; it cannot be the answer.
            }
        }
        return null;
    }

    /// <summary>True when this interface index is one of our own tunnels.</summary>
    private static bool IsTunnel(uint index)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (nic.GetIPProperties().GetIPv4Properties()?.Index == (int)index) return true;
            }
            catch (NetworkInformationException)
            {
            }
        }
        return false;
    }

    /// <summary>Which adapter Windows would send this address by, named rather than numbered.</summary>
    private static string? ViaFor(string address) =>
        IPAddress.TryParse(address, out var parsed) && IpHelperInterop.BestInterfaceFor(parsed) is { } index
            ? AdapterName(index) ?? $"interface {index}"
            : null;

    /// <summary>
    /// Bytes in and out of the physical adapter, for working out what this PC was doing to its own
    /// line while the measurement ran.
    /// </summary>
    private static (long Received, long Sent)? PhysicalTraffic()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)) continue;
            if (nic.GetIPProperties().GatewayAddresses.Count == 0) continue;

            try
            {
                var stats = nic.GetIPv4Statistics();
                return (stats.BytesReceived, stats.BytesSent);
            }
            catch (NetworkInformationException)
            {
                return null;
            }
        }
        return null;
    }

    internal static IPAddress? DefaultGateway()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)) continue;

            var gateway = nic.GetIPProperties().GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            if (gateway is not null) return gateway;
        }
        return null;
    }

    private static string? LinkTypeOf()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)) continue;
            if (nic.GetIPProperties().GatewayAddresses.Count == 0) continue;

            return nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "wired";
        }
        return null;
    }

    // ------------------------------------------------------------------ the run

    private sealed class Target
    {
        public required int Number;
        public required string Key;
        public required string Label;
        public required string Address;
        public readonly List<double> Samples = [];

        /// <summary>One slot per tick, null where the echo did not come back. See Rung.Ticks.</summary>
        public readonly List<double?> Ticks = [];

        public int Sent;
    }

    /// <summary>
    /// Measures everything and returns the report.
    /// </summary>
    /// <param name="latestStatus">
    /// The most recent status the service pushed, read once a second for rungs 5 and 6. A
    /// function rather than a value because the run lasts twenty seconds and the point is to see
    /// how those numbers MOVE.
    /// </param>
    public static async Task<Report> RunAsync(
        Func<StatusMessage?> latestStatus,
        int seconds,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var notes = new List<string>();
        var started = DateTimeOffset.UtcNow;

        var status = latestStatus();

        // RelayAddress is the relay this session is ON. RelayEndpoints is what somebody typed into
        // Settings, which a licensed installation never fills in - reading that one first is what
        // made the first real report come back with an empty trace and three missing rungs.
        var relayEndpoint = status?.RelayAddress ?? status?.RelayEndpoints?.FirstOrDefault();
        var relayIp = relayEndpoint?.Split(':')[0];

        if (relayIp is null)
        {
            notes.Add("The service named no relay, so the path to it could not be traced and " +
                      "rungs 2 to 5 are missing - which is most of the point. Connect first, then " +
                      "report while the problem is happening.");
        }

        // The trace runs once, before sampling, and only picks WHICH addresses the ISP rungs are.
        // It is not part of the measurement: a TTL walk takes seconds and would skew a window
        // that is meant to catch every rung at the same moment.
        // The relay's own RTT, measured directly and BEFORE the trace, because DomesticHop needs
        // it to decide how near a hop must be to count as in-country.
        //
        // Not read off the trace, which is where it used to come from: a TTL walk that runs out of
        // hops before it arrives - five silent hops and a 20-hop cap did exactly that on the first
        // real run - leaves it null, and the check falls back to a looser ceiling derived from the
        // far side of the step. Three echoes cost nothing and cannot run out.
        progress?.Report("Measuring the relay...");
        var relayRtt = relayIp is not null ? await BestRttAsync(relayIp, 3, ct).ConfigureAwait(false) : null;

        progress?.Report("Tracing the path to the relay...");
        var trace = relayIp is not null ? await TraceAsync(relayIp, ct).ConfigureAwait(false) : [];
        var gateway = DefaultGateway();
        var domestic = DomesticHop(trace, relayRtt);

        var targets = new List<Target>();
        if (gateway is not null)
        {
            targets.Add(new Target { Number = 1, Key = "router", Label = "home router", Address = gateway.ToString() });
        }

        // The first hop past the router. On a CGNAT line that is the carrier's own address, which
        // is exactly the rung we want and is why cgnat is classed apart from private.
        var access = trace.FirstOrDefault(h => gateway is null || h.Address != gateway.ToString());
        if (access is not null && access.Address != relayIp)
        {
            targets.Add(new Target { Number = 2, Key = "isp-access", Label = "ISP access", Address = access.Address });
        }

        if (domestic is not null && domestic.Address != access?.Address)
        {
            targets.Add(new Target { Number = 3, Key = "isp-core", Label = "ISP domestic core", Address = domestic.Address });
        }
        else
        {
            notes.Add("No domestic rung: the trace showed no single hop where the RTT jumps by " +
                      "15 ms or more, so there is nothing to call the edge of the country.");
        }

        if (relayIp is not null)
        {
            targets.Add(new Target { Number = 5, Key = "relay", Label = "relay", Address = relayIp });
        }

        // ------------------------------------------------------------- sampling

        var trafficAtStart = PhysicalTraffic();
        long? droppedAtStart = null;
        long? droppedAtEnd = null;

        var tunnelSamples = new List<double>();
        var gameSamples = new List<double>();
        var tunnelTicks = new List<double?>();
        var gameTicks = new List<double?>();
        var statusSeen = 0;
        var gameRunning = false;
        string? gameRegion = null;
        string? tunnelState = null;

        using var ping = new Ping();
        for (var tick = 0; tick < seconds; tick++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Measuring... {tick + 1}/{seconds}s");

            var sweep = Stopwatch.StartNew();

            foreach (var target in targets)
            {
                target.Sent++;
                double? sample = null;
                try
                {
                    var reply = await ping.SendPingAsync(target.Address, PingTimeoutMs).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success) sample = reply.RoundtripTime;
                }
                catch (Exception)
                {
                    // Counted as loss by Sent outrunning Samples, which is what it is.
                }

                // Both lists, always. Samples is what was received and is what gets uploaded;
                // Ticks keeps the empty second in place so the rungs stay side by side.
                if (sample is { } rtt) target.Samples.Add(rtt);
                target.Ticks.Add(sample);
            }

            double? tunnelTick = null;
            double? gameTick = null;

            if (latestStatus() is { } live)
            {
                statusSeen++;
                tunnelState = live.State.ToString();
                gameRegion ??= live.GameRegionName;
                if (live.TunnelPingMs is { } t) { tunnelSamples.Add(t); tunnelTick = t; }
                if (live.GamePingMs is { } g) { gameSamples.Add(g); gameRunning = true; gameTick = g; }

                // Faults only, never the grand total: that one is mostly link-local chatter the
                // uplink filter is SUPPOSED to drop, and alarming on it fires on every healthy
                // machine - which is exactly what the first report with this wired up did.
                //
                // First and last, not a running total: the counter is cumulative since the
                // service started, and what matters is how much of it happened just now.
                droppedAtStart ??= live.PacketsDroppedFaults;
                droppedAtEnd = live.PacketsDroppedFaults;
            }

            tunnelTicks.Add(tunnelTick);
            gameTicks.Add(gameTick);

            var remaining = TimeSpan.FromSeconds(1) - sweep.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct).ConfigureAwait(false);
        }

        // ------------------------------------------------------------- scoring

        var rungs = new List<Rung>();
        foreach (var target in targets)
        {
            var stats = Summarise(target.Samples, target.Sent);
            rungs.Add(new Rung(target.Number, target.Key, target.Label, target.Address,
                ViaFor(target.Address), stats, BadReason(stats), target.Samples, target.Ticks));
        }

        if (InternationalRung(rungs.FirstOrDefault(r => r.Key == "isp-core"),
                              rungs.FirstOrDefault(r => r.Key == "relay")) is { } international)
        {
            rungs.Add(international);
        }

        if (statusSeen == 0)
        {
            notes.Add("The service pushed no status during the window, so rungs 6 and 7 are " +
                      "missing. Is the tunnel connected?");
        }

        var tunnelStats = Summarise(tunnelSamples, statusSeen);
        var wireStats = rungs.FirstOrDefault(r => r.Key == "relay")?.Stats;
        var gap = RelayProcessGap(wireStats, tunnelStats);

        if (tunnelSamples.Count > 0)
        {
            // The gap is merged into this rung's reasons rather than being a verdict of its own,
            // so the ladder keeps deciding who the culprit is by the one rule it has. A rung 6
            // that is slow while rung 5 is clean has nothing inside it to blame, so the ladder
            // lands on relay-udp - which is the answer.
            var reasons = new[] { BadReason(tunnelStats), gap?.Reason }
                .Where(r => r is not null);
            var merged = string.Join(", ", reasons);

            rungs.Add(new Rung(6, "relay-udp", "relay process", relayEndpoint,
                relayIp is null ? null : ViaFor(relayIp),
                tunnelStats, merged.Length == 0 ? null : merged, tunnelSamples, tunnelTicks,
                StatusDerived: true));
        }

        var gameStats = Summarise(gameSamples, statusSeen);
        if (gameSamples.Count > 0)
        {
            rungs.Add(new Rung(7, "game", "in game", null, null, gameStats, BadReason(gameStats),
                gameSamples, gameTicks, StatusDerived: true));
        }

        // Mute rungs are skipped entirely: a host that never answered has told us nothing, and
        // guessing from silence is how a tool like this earns its reputation.
        var ladder = rungs.Where(r => r.Stats is { Received: > 0 }).OrderBy(r => r.Number).ToList();
        var verdict = Culprit(ladder);

        // ------------------------------------------------------------- this machine

        var trafficAtEnd = PhysicalTraffic();
        double? downMbps = null, upMbps = null;
        if (trafficAtStart is { } a && trafficAtEnd is { } b && seconds > 0)
        {
            downMbps = (b.Received - a.Received) * 8.0 / seconds / 1e6;
            upMbps = (b.Sent - a.Sent) * 8.0 / seconds / 1e6;
        }

        // Loose thresholds on purpose. A line with something genuinely heavy on it is obvious at
        // these numbers, and a report that scolded every player with a browser open would be
        // ignored by the ones it is meant to help.
        if (downMbps > 20 || upMbps > 5)
        {
            notes.Add($"This PC moved {downMbps:N1} Mbps down and {upMbps:N1} Mbps up while " +
                      "measuring. That is not idle - a download, an update, a cloud sync or a " +
                      "stream is using the line, and it can cause all of this on its own. Stop it " +
                      "and report again before reading anything above.");
        }

        var serviceDropped = droppedAtEnd - droppedAtStart;
        if (serviceDropped > 0)
        {
            notes.Add($"The service dropped {serviceDropped} packets inside this PC during the " +
                      "window, for reasons that are not normal filtering. That is not the network " +
                      "- the per-cause breakdown is in the service log.");
        }

        var relayTunnelled = relayIp is not null
                             && IPAddress.TryParse(relayIp, out var relayAddress)
                             && IpHelperInterop.BestInterfaceFor(relayAddress) is { } relayVia
                             && IsTunnel(relayVia);
        if (relayTunnelled)
        {
            notes.Add("The relay's own address is leaving by the tunnel instead of the physical " +
                      "adapter. The pinned route that keeps relay traffic out of the tunnel is " +
                      "missing - that is a routing loop waiting to happen, and it makes rungs 5 " +
                      "and 6 measure the same thing, so they can no longer tell the wire from the " +
                      "relay.");
        }

        if (!gameRunning)
        {
            notes.Add("The game was not running, so no routes were installed and rung 6 is idle. " +
                      "A report taken during a match says more.");
        }

        return new Report(
            SchemaVersion: 2,
            TakenUtc: started,
            Seconds: seconds,
            Verdict: verdict,
            VerdictText: VerdictTextFor(verdict, ladder),
            RelayName: status?.RelayName,
            RelayEndpoint: relayEndpoint,
            GameRegion: gameRegion,
            TunnelState: tunnelState,
            LinkType: LinkTypeOf(),
            GameRunning: gameRunning,
            RelayProcessGapMs: gap?.Ms ?? RawGap(wireStats, tunnelStats),
            PcDownMbps: downMbps,
            PcUpMbps: upMbps,
            ServiceDropped: serviceDropped,
            RelayTunnelled: relayTunnelled,
            Rungs: rungs.OrderBy(r => r.Number).ToList(),
            Trace: trace,
            Notes: notes);
    }

    /// <summary>
    /// The latency the RELAY PROCESS adds on top of the wire, when there is enough of it to say so.
    ///
    /// This is the comparison rungs 5 and 6 exist for and the one the method description has
    /// always promised: both numbers are a round trip to the same box over the same wire, so what
    /// separates them is not the network. Rung 5 is answered by the far kernel, rung 6 by relayd
    /// in userspace, and on an idle relay that difference is well under a millisecond.
    ///
    /// It was not implemented until now - in either this or the PowerShell it came from - because
    /// every rung was scored only against its own distance and nothing ever compared two of them
    /// in absolute terms. A relay adding 13 ms therefore produced the verdict "clean", which is
    /// exactly the shape of failure this tool exists to catch.
    ///
    /// THE THRESHOLD IS DELIBERATELY LOOSE, and the wording deliberately names two causes. A
    /// userspace round trip also carries the CLIENT's own scheduling, and Wi-Fi power saving
    /// delays precisely that kind of wakeup - so a tight threshold would fire on every laptop on
    /// Wi-Fi and the warning would be worth nothing on the day it is real. What this can honestly
    /// say is that the two numbers disagree by more than userspace overhead explains; which end
    /// is responsible is settled by running once on a cable, and the text says so.
    /// </summary>
    /// <summary>The same subtraction with no threshold, for the record. Null when either is mute.</summary>
    private static double? RawGap(Stats? wire, Stats? process) =>
        wire?.P50 is { } w && process?.P50 is { } p && wire.Received > 0 && process.Received > 0
            ? p - w
            : null;

    internal static (double Ms, string Reason)? RelayProcessGap(Stats? wire, Stats? process)
    {
        if (wire?.P50 is not { } wireP50 || process?.P50 is not { } processP50) return null;
        if (wire.Received == 0 || process.Received == 0) return null;

        var gap = processP50 - wireP50;

        // Scales with distance for the same reason every other threshold here does, with a floor
        // so a nearby relay is not judged on a couple of milliseconds of noise.
        if (gap <= Math.Max(10.0, 0.25 * wireP50)) return null;

        return (gap, $"{gap:N0} ms slower than ICMP to the same box");
    }

    /// <summary>
    /// One sentence a person can act on. The key alone is for grouping on the server; this is
    /// what the person who pressed the button reads.
    /// </summary>
    private static string VerdictTextFor(string verdict, IReadOnlyList<Rung> ladder)
    {
        var reason = ladder.FirstOrDefault(r => r.Key == verdict)?.BadReason;
        var detail = reason is null ? "" : $" ({reason})";

        return verdict switch
        {
            "clean" => "Nothing on the path is misbehaving right now. If the game still felt bad, " +
                       "the problem was not on this path at the moment this ran - report again " +
                       "while it is happening.",
            "router" => $"The home network is the first thing that looks wrong{detail} - Wi-Fi, " +
                        "the cable, or the router itself. Everything past it inherits this.",
            "isp-access" => $"The ISP's access network is the first bad rung{detail}. The home " +
                            "network is clean, so this is the line into the building, not the house.",
            "isp-core" => $"The ISP's domestic network is the first bad rung{detail} - inside the " +
                          "country, before any international link.",
            "international" => $"Everything inside the country is clean, and the leg out of it is " +
                                $"not{detail}. That is the transit between your ISP and the relay's " +
                                "region, or the relay's own uplink - neither is on your line and " +
                                "neither is fixed from this end. If it keeps happening, a relay " +
                                "reached by a different route is the thing to try.",
            "relay" => $"The path to the relay is the first bad rung{detail}, while everything " +
                       "inside the country is clean. The fault is past the border - on the way " +
                       "out of the country, or at the relay's own front door.",
            "relay-udp" => $"The physical path to the relay is clean but the relay's own answers " +
                           $"are not{detail}. Both numbers are a round trip to the same machine " +
                           "over the same wire, so the difference is not the network - it is the " +
                           "relay process, or this PC's own scheduling. Running this once on a " +
                           "cable instead of Wi-Fi tells the two apart.",
            "game" => $"Everything up to the relay is clean and the game ping is not{detail}, so " +
                      "the fault is past the relay - between it and the game server.",
            _ => "No verdict.",
        };
    }
}
