using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;

namespace GamePingBooster.Service.Discovery;

/// <summary>
/// How to read a <see cref="RawUdpEvent"/>: which address is the far end, and whether the ports
/// and IPv4 addresses need their bytes swapped. Settled by <see cref="SelfTest"/> from packets
/// this tool sent itself, never from documentation - a wrong guess would not fail, it would
/// report plausible-looking addresses that nobody talked to.
/// </summary>
internal sealed class DecodingRules
{
    public required bool SwapIpv4 { get; init; }
    public required bool SwapPorts { get; init; }
    public required bool SendRemoteIsDestination { get; init; }

    /// <summary>Null when no receive event could be verified. Receives are then not counted at
    /// all, which costs nothing that matters: capture counts outbound packets only, and so does
    /// the threshold.</summary>
    public required bool? ReceiveRemoteIsDestination { get; init; }

    /// <summary>False when the IPv6 test got no events (no IPv6 on this machine, typically).
    /// IPv6 destinations are still listed, marked unverified.</summary>
    public required bool Ipv6Verified { get; init; }

    public required string Evidence { get; init; }
}

internal sealed class FlowRow
{
    public required IPAddress Address { get; init; }
    public long Sent { get; set; }
    public long Received { get; set; }
    public long SentBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public long FirstTimestamp { get; set; }
    public long LastTimestamp { get; set; }
    public SortedSet<int> Ports { get; } = [];

    public double Seconds => LastTimestamp > FirstTimestamp
        ? (LastTimestamp - FirstTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency
        : 0;
}

/// <summary>
/// Every remote address each process exchanged UDP with, folded up as events arrive.
///
/// Every process, not only the game's: a game's first packets can leave before the one-second
/// process poll has seen it start, and a table keyed by process id keeps those rather than losing
/// them to the race. Other processes' entries are thrown away when a session begins.
///
/// Written by the ETW thread, read by the main thread, under one lock. At a busy machine's event
/// rate an uncontended lock is well below the cost of the event itself, and the callback timing
/// in the status line would show it if not.
/// </summary>
internal sealed class FlowTable : IUdpEventSink
{
    /// <summary>Distinct (process, address) entries kept before new ones are only counted. A
    /// machine running a torrent client could otherwise grow this without limit.</summary>
    private const int MaxFlows = 50_000;

    private const int MaxPortsPerFlow = 16;

    private readonly record struct Key(uint Pid, bool IsV6, ulong Hi, ulong Lo);

    private sealed class Flow
    {
        public long Sent, Received, SentBytes, ReceivedBytes, First, Last;
        public readonly List<ushort> Ports = new(2);
    }

    private readonly uint _ownPid;
    private readonly object _gate = new();
    private readonly Dictionary<Key, Flow> _flows = [];
    private volatile DecodingRules? _rules;
    private volatile ConcurrentQueue<RawUdpEvent>? _ownEvents;
    private volatile HashSet<uint> _gamePids = [];

    private long _untracked;
    private long _gameEvents;
    private long _ignoredReceives;

    public FlowTable(uint ownPid) => _ownPid = ownPid;

    public DecodingRules? Rules
    {
        get => _rules;
        set => _rules = value;
    }

    /// <summary>Events from the game's processes since the tool started.</summary>
    public long GameEvents => Interlocked.Read(ref _gameEvents);

    /// <summary>Events dropped because the table was full.</summary>
    public long Untracked => Interlocked.Read(ref _untracked);

    /// <summary>Receives not counted because their decoding could not be verified.</summary>
    public long IgnoredReceives => Interlocked.Read(ref _ignoredReceives);

    public int Count
    {
        get { lock (_gate) return _flows.Count; }
    }

    /// <summary>Starts handing this tool's own events to the self-test instead of dropping them.</summary>
    public ConcurrentQueue<RawUdpEvent> CaptureOwnEvents() => _ownEvents = new ConcurrentQueue<RawUdpEvent>();

    public void StopCapturingOwnEvents() => _ownEvents = null;

    /// <summary>Which process ids belong to the game right now - only used to count their events.</summary>
    public void SetGamePids(IEnumerable<uint> pids) => _gamePids = [.. pids];

    public void OnUdpEvent(in RawUdpEvent e)
    {
        if (e.Pid == _ownPid)
        {
            // The self-test's packets, and nothing this tool sends may ever count as traffic.
            _ownEvents?.Enqueue(e);
            return;
        }

        var rules = _rules;
        if (rules is null) return;

        if (_gamePids.Contains(e.Pid)) Interlocked.Increment(ref _gameEvents);

        bool remoteIsDestination;
        if (e.Direction == NetDirection.Send)
        {
            remoteIsDestination = rules.SendRemoteIsDestination;
        }
        else if (rules.ReceiveRemoteIsDestination is { } receive)
        {
            remoteIsDestination = receive;
        }
        else
        {
            Interlocked.Increment(ref _ignoredReceives);
            return;
        }

        var hi = remoteIsDestination ? e.DaddrHi : e.SaddrHi;
        var lo = remoteIsDestination ? e.DaddrLo : e.SaddrLo;
        var port = remoteIsDestination ? e.DportRaw : e.SportRaw;
        if (!e.IsV6 && rules.SwapIpv4) lo = BinaryPrimitives.ReverseEndianness((uint)lo);
        if (rules.SwapPorts) port = BinaryPrimitives.ReverseEndianness(port);

        var key = new Key(e.Pid, e.IsV6, hi, lo);
        lock (_gate)
        {
            if (!_flows.TryGetValue(key, out var flow))
            {
                if (_flows.Count >= MaxFlows)
                {
                    _untracked++;
                    return;
                }
                flow = new Flow { First = e.Timestamp };
                _flows[key] = flow;
            }

            if (e.Direction == NetDirection.Send)
            {
                flow.Sent++;
                flow.SentBytes += e.PayloadBytes;
                // Ports are the destination ports the process sent to, as capture lists them.
                if (flow.Ports.Count < MaxPortsPerFlow && !flow.Ports.Contains(port)) flow.Ports.Add(port);
            }
            else
            {
                flow.Received++;
                flow.ReceivedBytes += e.PayloadBytes;
            }

            if (e.Timestamp < flow.First) flow.First = e.Timestamp;
            if (e.Timestamp > flow.Last) flow.Last = e.Timestamp;
        }
    }

    /// <summary>Drops every entry that does not belong to one of these processes.</summary>
    public void RemoveAllExcept(IReadOnlySet<uint> pids)
    {
        lock (_gate)
        {
            foreach (var key in _flows.Keys.Where(k => !pids.Contains(k.Pid)).ToList()) _flows.Remove(key);
        }
    }

    public void Clear()
    {
        lock (_gate) _flows.Clear();
    }

    /// <summary>These processes' flows, merged by remote address.</summary>
    public List<FlowRow> Snapshot(IReadOnlySet<uint> pids)
    {
        var rows = new Dictionary<(bool, ulong, ulong), FlowRow>();
        lock (_gate)
        {
            foreach (var (key, flow) in _flows)
            {
                if (!pids.Contains(key.Pid)) continue;
                if (!rows.TryGetValue((key.IsV6, key.Hi, key.Lo), out var row))
                {
                    row = new FlowRow
                    {
                        Address = ToAddress(key.IsV6, key.Hi, key.Lo),
                        FirstTimestamp = flow.First,
                        LastTimestamp = flow.Last,
                    };
                    rows[(key.IsV6, key.Hi, key.Lo)] = row;
                }
                row.Sent += flow.Sent;
                row.Received += flow.Received;
                row.SentBytes += flow.SentBytes;
                row.ReceivedBytes += flow.ReceivedBytes;
                row.FirstTimestamp = Math.Min(row.FirstTimestamp, flow.First);
                row.LastTimestamp = Math.Max(row.LastTimestamp, flow.Last);
                foreach (var port in flow.Ports) row.Ports.Add(port);
            }
        }
        return [.. rows.Values];
    }

    private static IPAddress ToAddress(bool isV6, ulong hi, ulong lo)
    {
        if (!isV6)
        {
            Span<byte> v4 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(v4, (uint)lo);
            return new IPAddress(v4);
        }
        Span<byte> v6 = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(v6, hi);
        BinaryPrimitives.WriteUInt64BigEndian(v6[8..], lo);
        return new IPAddress(v6);
    }
}
