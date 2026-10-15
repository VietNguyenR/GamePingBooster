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
/// What counts as a probe. A packet on the game's declared probe port (<see cref="ProbePlan"/>, PUBG UDP 8081), or
/// to or from one of the profile's landmarks - for a game without a plan the landmarks ARE its probe endpoints, and
/// the port is learned from them as before. Since PUBG's 2026-10-14 patch the endpoints rotate per session, so a
/// landmark is no longer how a probe is recognised, only one way of naming it.
///
/// Where an endpoint is. Resolved once a round is taken, first match wins: an exact landmark (its region, and its
/// place in that region's list as the index); a region of the plan, by the Azure prefixes it publishes; a region of
/// the profile, by its ranges. A resolved endpoint that is not a landmark is indexed after that region's landmarks,
/// so an index names one endpoint in a round. Anything else on the port is kept as unmapped (null region) - the game
/// probing somewhere none of these know is a finding of its own. Resolving names only: no probe endpoint is routed.
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
    private readonly Dictionary<string, int> _landmarkCounts = [];
    private readonly ProbePlan? _plan;
    private readonly (uint Start, uint End, string Region)[] _profileRanges;
    private readonly HashSet<ushort> _ports = [];
    private readonly Queue<Event> _events = new();
    private volatile bool _hasPorts;

    /// <param name="regions">The profile's regions: their landmarks, and their ranges for naming an endpoint.</param>
    /// <param name="plan">The game's declared probing, or null to learn the port from the landmarks alone.</param>
    public ProbeTap(IEnumerable<(string Region, IReadOnlyList<string> Landmarks, IReadOnlyList<string> Cidrs)> regions,
        ProbePlan? plan = null)
    {
        _landmarks = [];
        var ranges = new List<(uint, uint, string)>();
        foreach (var (region, landmarks, cidrs) in regions)
        {
            for (var i = 0; i < landmarks.Count; i++)
            {
                if (IPAddress.TryParse(landmarks[i], out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    _landmarks.TryAdd(ToUInt(address), (region, i));
                }
            }
            _landmarkCounts[region] = landmarks.Count;
            foreach (var cidr in cidrs)
            {
                if (ProbePlan.Range(cidr) is { } range) ranges.Add((range.Start, range.End, region));
            }
        }
        _profileRanges = ProbePlan.Disjoint(ranges);
        _plan = plan;
    }

    /// <summary>Whether anything can ever match: a declared probe port, or an IPv4 landmark to learn one from.</summary>
    public bool CanMatch => _plan is not null || _landmarks.Count > 0;

    /// <summary>One of the game's IPv4 UDP packets, already decoded: the far end, its port, and the QPC time.</summary>
    public void Observe(bool sent, uint remote, ushort remotePort, long timestamp)
    {
        var landmark = _landmarks.ContainsKey(remote);
        var declared = _plan is { } plan && plan.Port == remotePort;
        if (!landmark && !declared && !_hasPorts) return;
        lock (_gate)
        {
            if (landmark)
            {
                if (_ports.Add(remotePort)) _hasPorts = true;
            }
            else if (!declared && !_ports.Contains(remotePort))
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
    /// when nothing in <see cref="Window"/> before it reached an endpoint of a known region - unmapped traffic alone
    /// is not a round.
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
        return Summarise(events, matchAt, matchUtc, receivesVerified);
    }

    /// <summary>The region <paramref name="address"/> is named by, landmark first - see the class summary.</summary>
    private string? RegionOf(uint address) =>
        _landmarks.TryGetValue(address, out var landmark) ? landmark.Region
        : _plan?.RegionOf(address) ?? ProbePlan.Find(_profileRanges, address);

    private ProbeRound? Summarise(Event[] all, long matchAt, DateTimeOffset matchUtc, bool receivesVerified)
    {
        var from = matchAt - (long)(Window.TotalSeconds * Stopwatch.Frequency);
        var events = all.Where(e => e.Timestamp >= from && e.Timestamp <= matchAt).OrderBy(e => e.Timestamp).ToArray();
        var regions = events.Select(e => e.Address).Distinct().ToDictionary(a => a, RegionOf);
        if (!regions.Values.Any(r => r is not null)) return null;

        var maxTicks = (long)(MaxRttMs / 1000 * Stopwatch.Frequency);
        var endpoints = new List<ProbeEndpoint>();
        var unmapped = 0;
        var nextIndex = new Dictionary<string, int>(_landmarkCounts);
        foreach (var group in events.GroupBy(e => e.Address)
                     .OrderBy(g => regions[g.Key] is null ? 1 : 0).ThenBy(g => g.First().Timestamp))
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
            var region = regions[group.Key];
            int index;
            if (_landmarks.TryGetValue(group.Key, out var landmark)) index = landmark.Index;
            else if (region is null) index = unmapped++;
            else
            {
                index = nextIndex.GetValueOrDefault(region);
                nextIndex[region] = index + 1;
            }
            endpoints.Add(new ProbeEndpoint(
                region,
                index,
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
