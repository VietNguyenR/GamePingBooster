using System.Diagnostics;
using System.Net;

namespace GamePingBooster.Service.Discovery;

/// <summary>
/// What the game itself measured before a match: every UDP exchange it had with one of its datacentre probe
/// endpoints, as ETW saw them, turned into a round trip per endpoint.
///
/// Why it exists. PUBG chooses the datacentre for a match from its own probes - one endpoint per Azure region,
/// UDP 8081, over the player's own line (docs: probes-use-real-isp-path). Players connected to a Singapore relay
/// were landing in Korea a few times in a hundred (2026-10), while the app's own ICMP to the same endpoints, taken
/// at connect, said Singapore was 40 ms nearer every time. ICMP at connect is not what the game measures: it uses
/// UDP, minutes later. This records what the game's own packets got, at the moment it chose, so a Korea match can
/// be told apart: Singapore slow or silent on the line (a network cause - route the probes), or Singapore fast
/// and the game chose Korea anyway (matchmaking, nothing a route changes).
///
/// What counts as a probe. The profile's landmarks for the game ARE its probe endpoints (that is what a landmark
/// is for PUBG), so a packet to or from one of them is a probe, and the remote port it used is learned from it. A
/// packet on a learned port to an address that is not a landmark is kept too, as unmapped: the game probing an
/// endpoint the profile does not know - a new region, or a moved one - is a finding of its own. Nothing about a
/// port is written down here; a game whose landmarks are not probe endpoints simply never matches.
///
/// The round trip. ETW gives each send and receive with a QPC timestamp, not a pairing. A receive is paired with
/// the latest send to the same endpoint before it that no earlier receive has taken, within <see cref="MaxRttMs"/>.
/// With back-to-back sends that reads a little low, never high, and a lost probe cannot stretch the next one's
/// time, which a first-in-first-out pairing would. Sends nothing answered are counted as lost.
///
/// Fed from the ETW thread (<see cref="Observe"/>), read from the recorder's poll (<see cref="Take"/>), under
/// one lock. Probes run at a few packets a second; the lookup that rejects everything else is one set lookup.
/// </summary>
internal sealed class ProbeTap
{
    /// <summary>A reply later than this is not paired with that send - it is counted lost and the reply ignored.</summary>
    internal const double MaxRttMs = 2000;

    /// <summary>How much probing before the match a round keeps. 600 s, not the first 120: PUBG probes in bursts
    /// minutes apart (a 2.9 h capture holds ~190 packets per Singapore endpoint), so two minutes before the match
    /// usually held no burst at all. Each endpoint's LastSecondsBeforeMatch says how fresh its numbers are.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(600);

    /// <summary>Events kept, oldest dropped first. Probes are tens per endpoint per round; this is many rounds.</summary>
    private const int MaxEvents = 8192;

    /// <summary>Endpoints in a round at most: unmapped ones on a learned port are bounded by what the game sends.</summary>
    private const int MaxEndpoints = 32;

    private readonly record struct Event(uint Address, bool Sent, long Timestamp);

    private readonly object _gate = new();
    private readonly Dictionary<uint, (string Region, int Index)> _landmarks;
    private readonly HashSet<ushort> _ports = [];
    private readonly Queue<Event> _events = new();
    private volatile bool _hasPorts;

    public ProbeTap(IEnumerable<(string Region, IReadOnlyList<string> Landmarks)> regions)
    {
        _landmarks = [];
        foreach (var (region, landmarks) in regions)
        {
            for (var i = 0; i < landmarks.Count; i++)
            {
                if (IPAddress.TryParse(landmarks[i], out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    _landmarks.TryAdd(ToUInt(address), (region, i));
                }
            }
        }
    }

    /// <summary>Whether the game has any IPv4 landmark - without one nothing can ever match.</summary>
    public bool HasLandmarks => _landmarks.Count > 0;

    /// <summary>One of the game's IPv4 UDP packets, already decoded: the far end, its port, and the QPC time.</summary>
    public void Observe(bool sent, uint remote, ushort remotePort, long timestamp)
    {
        var landmark = _landmarks.ContainsKey(remote);
        if (!landmark && !_hasPorts) return;
        lock (_gate)
        {
            if (landmark)
            {
                if (_ports.Add(remotePort)) _hasPorts = true;
            }
            else if (!_ports.Contains(remotePort))
            {
                return;
            }
            _events.Enqueue(new Event(remote, sent, timestamp));
            while (_events.Count > MaxEvents) _events.Dequeue();
        }
    }

    /// <summary>The QPC time of the latest probe seen, or null when none is held.</summary>
    public long? LastAt
    {
        get
        {
            lock (_gate) return _events.Count == 0 ? null : _events.Last().Timestamp;
        }
    }

    /// <summary>
    /// The probing held so far, as one round ending at <paramref name="matchAt"/> (QPC), and empties the tap. Null
    /// when nothing in <see cref="Window"/> before it reached a landmark - unmapped traffic alone is not a round.
    /// </summary>
    /// <param name="clock">This moment as (QPC, UTC), to date the match; the real clock when null. For tests.</param>
    public ProbeRound? Take(long matchAt, bool receivesVerified, (long Qpc, DateTimeOffset Utc)? clock = null)
    {
        var (nowQpc, nowUtc) = clock ?? (Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        var matchUtc = nowUtc.AddSeconds(-(nowQpc - matchAt) / (double)Stopwatch.Frequency);
        Event[] events;
        lock (_gate)
        {
            events = [.. _events];
            _events.Clear();
        }
        return Summarise(events, matchAt, matchUtc, receivesVerified, _landmarks);
    }

    private static ProbeRound? Summarise(Event[] all, long matchAt, DateTimeOffset matchUtc, bool receivesVerified,
        IReadOnlyDictionary<uint, (string Region, int Index)> landmarks)
    {
        var from = matchAt - (long)(Window.TotalSeconds * Stopwatch.Frequency);
        var events = all.Where(e => e.Timestamp >= from && e.Timestamp <= matchAt).OrderBy(e => e.Timestamp).ToArray();
        if (!events.Any(e => landmarks.ContainsKey(e.Address))) return null;

        var maxTicks = (long)(MaxRttMs / 1000 * Stopwatch.Frequency);
        var endpoints = new List<ProbeEndpoint>();
        var unmapped = 0;
        foreach (var group in events.GroupBy(e => e.Address)
                     .OrderBy(g => landmarks.ContainsKey(g.Key) ? 0 : 1).ThenBy(g => g.First().Timestamp))
        {
            if (endpoints.Count >= MaxEndpoints) break;

            var sends = new List<long>();
            var taken = new List<bool>();
            var rtts = new List<double>();
            var received = 0;
            foreach (var e in group)
            {
                if (e.Sent)
                {
                    sends.Add(e.Timestamp);
                    taken.Add(false);
                    continue;
                }
                received++;
                for (var i = sends.Count - 1; i >= 0; i--)
                {
                    if (e.Timestamp - sends[i] > maxTicks) break;
                    if (taken[i]) continue;
                    taken[i] = true;
                    rtts.Add((e.Timestamp - sends[i]) * 1000.0 / Stopwatch.Frequency);
                    break;
                }
            }

            rtts.Sort();
            var known = landmarks.TryGetValue(group.Key, out var where);
            endpoints.Add(new ProbeEndpoint(
                known ? where.Region : null,
                known ? where.Index : unmapped++,
                Address: ToAddress(group.Key),
                Sent: sends.Count,
                Received: received,
                Answered: rtts.Count,
                MinMs: rtts.Count > 0 ? rtts[0] : null,
                MedianMs: rtts.Count > 0 ? rtts[rtts.Count / 2] : null,
                MaxMs: rtts.Count > 0 ? rtts[^1] : null,
                LastSecondsBeforeMatch: (matchAt - group.Last().Timestamp) / (double)Stopwatch.Frequency));
        }

        return new ProbeRound(
            MatchUtc: matchUtc,
            FirstSecondsBeforeMatch: (matchAt - events[0].Timestamp) / (double)Stopwatch.Frequency,
            ReceivesVerified: receivesVerified,
            Endpoints: endpoints);
    }

    internal static uint ToUInt(IPAddress address) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    private static IPAddress ToAddress(uint value)
    {
        var bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }

    /// <summary>For tests: an event as the tap would have been handed it.</summary>
    internal void ObserveForTest(bool sent, IPAddress remote, ushort port, long timestamp) =>
        Observe(sent, ToUInt(remote), port, timestamp);
}

/// <summary>
/// The game's probing before one match, which began at <paramref name="MatchUtc"/> - as near as the recorder saw
/// it: the first packet to an off-tunnel server, or up to ten seconds late for one on the tunnel. <paramref name="ReceivesVerified"/> false means the ETW self-test could
/// not verify how receives decode on this Windows, so none were counted: sends only, no round trips.
/// </summary>
internal sealed record ProbeRound(
    DateTimeOffset MatchUtc,
    double FirstSecondsBeforeMatch,
    bool ReceivesVerified,
    IReadOnlyList<ProbeEndpoint> Endpoints);

/// <summary>
/// One endpoint's probes in a round. <paramref name="Region"/> and <paramref name="Index"/> name a landmark by its
/// region and place in that region's list; an unmapped endpoint has a null region and its own running index.
/// <paramref name="Address"/> is for this machine's log only (masked there) and never written into a record.
/// <paramref name="Answered"/> is the receives paired with a send; Received counts them all.
/// </summary>
internal sealed record ProbeEndpoint(
    string? Region,
    int Index,
    IPAddress Address,
    int Sent,
    int Received,
    int Answered,
    double? MinMs,
    double? MedianMs,
    double? MaxMs,
    double LastSecondsBeforeMatch);
