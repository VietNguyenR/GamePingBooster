using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using GamePingBooster.Core.Native;
using GamePingBooster.Core.Quality;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Watches the path the whole time a game is being played, and writes down every spike with where
/// on the path it happened.
///
/// It exists because "Report lag" could not answer the question it was built for. It measured the
/// twenty seconds after somebody pressed a button, and a spike is over in two to five, so by the
/// time a player had alt-tabbed out and ticked the consent box there was nothing left to find -
/// most reports came back "nothing found". This measures continuously and judges each spike from
/// the quarter seconds it happened in. <see cref="SpikeDetector"/> holds the method; this class is
/// the part with clocks and sockets.
///
/// Four times a second, while the game runs and its packets flow:
///
///   A0 ICMP to the home router                                        (Ping)
///   A  ICMP to the relay's public address over the physical path    (Ping, the kernel answers)
///   B  a protocol ping to relayd through the same socket as the game (TunnelClient.SendQualityPing)
///   C  an echo through the tunnel to the region's landmark           (TunnelClient.SendQualityEcho)
///
/// once a second, C': an echo towards the match server at a TTL found by walking the path once per
/// server. PUBG's match servers do not answer echoes, so the nearest router that does stands in.
/// Recorded, never used for a verdict: on 2026-09-15 that router answered about once a second
/// whatever it was sent and jumped to 120 ms by itself while the datacentre stayed flat.
///
/// And passively, from the pump threads, the longest silence in the game's own packets each way,
/// plus once a second what the physical adapter is carrying.
///
/// What it costs: about sixteen small packets a second, a timestamp and a subtraction per game
/// packet, and an uncontended lock per answer. Nothing here can slow or drop a game packet: every
/// failure is swallowed, and the pump threads only ever call the two <see cref="IQualitySink"/>
/// methods, which take one short lock.
///
/// What it keeps: nothing that names anybody. No addresses reach the file or the log - not the
/// router's, not the relay's, not the game server's. See <see cref="QualityFile"/>.
/// </summary>
internal sealed class SpikeRecorder : IQualitySink
{
    /// <summary>What the engine is doing right now, read once per tick.</summary>
    internal sealed record Context(
        TunnelClient? Tunnel,
        string? RelayId,
        string? EntryId,
        string? RelayName,
        IPAddress? RelayAddress,
        IPAddress? Landmark,
        string? RegionName,
        string? GameId,
        bool GameRunning,
        IReadOnlyList<DoorProbes.Door> Doors,
        bool MovesEnabled);

    private static readonly long TickLength = Stopwatch.Frequency / SpikeDetector.TicksPerSecond;

    /// <summary>How many ticks stay addressable for answers that arrive late. Sixteen seconds.</summary>
    private const int RingSize = 64;

    /// <summary>
    /// Ticks held back before the detector sees them, so every probe in them has answered or timed
    /// out. One and a half seconds, past the one-second probe timeout: a tick judged earlier would
    /// count a slow answer as a lost one.
    /// </summary>
    private const int SettleTicks = 6;

    private const int ProbeTimeoutMs = 1000;

    /// <summary>
    /// How long after the last game packet the probes keep going. Covers any silence the detector
    /// would still call a spike, so a stall is measured all the way through rather than switching
    /// the probes off in the middle of it.
    /// </summary>
    private static readonly long ActiveFor = Stopwatch.Frequency * 5;

    /// <summary>Game packets in one quarter second, both ways together, that count as a game being played.</summary>
    private const int MinActivePackets = 3;

    /// <summary>
    /// Thirty seconds with no game packets ends the match and writes its summary. The first real
    /// session went seventy seconds from one PUBG match's last packet to the next one's first, so
    /// this separates matches without splitting one on a loading screen.
    /// </summary>
    private static readonly long MatchEndsAfter = Stopwatch.Frequency * 30;

    /// <summary>
    /// Anything shorter is not a match worth a summary: connecting with a launcher open produced a
    /// 3.8-second "match" on 2026-09-15, before the game was even started.
    /// </summary>
    private const double MinMatchSeconds = 30;

    // The TTL walk towards the match server.
    private const byte WalkMaxTtl = 24;
    private const int WalkPerTick = 6;
    private const int WalkSettleTicks = 3 * SpikeDetector.TicksPerSecond;
    private static readonly long WalkAgainAfter = Stopwatch.Frequency * 30;

    // A bad evening on a bad line can produce a spike a minute. The file gets every one; the log
    // gets the first few per window and a count, so it stays readable for everything else in it.
    private const int LoggedPerWindow = 10;
    private static readonly TimeSpan LogWindow = TimeSpan.FromMinutes(5);

    private static readonly string? AppVersion = typeof(SpikeRecorder).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private enum ProbeKind : byte
    {
        None,
        Datacentre,
        ServerPath,
        Walk,
    }

    private struct Probe
    {
        public ushort Sequence;
        public ProbeKind Kind;
        public long TickIndex;
        public long SentAt;
        public byte Ttl;
    }

    private readonly Func<Context> _context;
    private readonly Action<string> _log;
    private readonly QualityFile _file;
    private readonly object _gate = new();
    private readonly QualityTick?[] _ring = new QualityTick?[RingSize];
    private readonly Probe[] _probes = new Probe[1024];
    /// <summary>
    /// Where tick 0 starts: half a tick before the first step, so every step lands mid-tick. Set at
    /// construction instead, the steps landed 3 to 34 ms after a boundary (measured over 480 steps on
    /// 2026-09-15) - one timer firing 4 ms early then reads the previous index, and the index after it is
    /// skipped and goes in as a tick that measured nothing, which closes any open spike and breaks the
    /// five-second steady window every stall needs. Mid-tick leaves about 120 ms either way. Zero until
    /// the first step; nothing that reads it runs before then (pongs arrive only once attached).
    /// </summary>
    private long _epoch;

    private SpikeDetector _detector = new();
    private long _lastCreated = -1;
    private long _lastFed = -1;
    private ushort _sequence;
    private TunnelClient? _tunnel;
    private long _lastActivityAt;
    private long _lastStepAt;
    private string? _pathKey;
    private volatile bool _inMatch;

    /// <summary>
    /// A match is being recorded right now. The upload waits for this to be false: sending between
    /// matches is the promise, and a match is the one time the line must carry nothing extra.
    /// </summary>
    public bool InMatch => _inMatch;
    private Context? _matchContext;
    private bool _failedOnce;

    private IPAddress? _primary;
    private IPAddress? _walkTarget;
    private long _walkStartedTick = -1;
    private byte _walkNextTtl;
    private long _lastWalkAt;
    private readonly List<(byte Ttl, double RttMs, bool Reached)> _walkAnswers = [];
    private (IPAddress Target, byte Ttl)? _serverPath;

    private NetworkInterface? _nic;
    private IPAddress? _nicFor;
    private IPAddress? _gateway;
    private long _nicFoundAt;
    private long _lineAt;
    private long _lineReceived;
    private long _lineSent;
    private double? _lineDown;
    private double? _lineUp;

    private DateTimeOffset _windowStart;
    private int _windowLogged;
    private int _windowSuppressed;

    // ------------------------------------------------------------------ the other ways into the relay

    private readonly Func<DoorDecision, bool> _requestMove;
    private readonly DoorSwitchPolicy _policy = new();

    /// <summary>Probes down the ways the tunnel is not using, for the list and session they were made for.</summary>
    private DoorProbes? _doors;
    private IReadOnlyList<DoorProbes.Door>? _doorsList;
    private ulong _doorsSession;
    private int _doorProbeTicks;

    /// <summary>
    /// A decision being followed for the minute after it, so that its record says what happened next. Touched
    /// only by the recorder's own loop - Step, and EndMatch after it - so it takes no lock.
    /// </summary>
    private PendingMove? _pendingMove;

    private const int FollowTicks = 60 * SpikeDetector.TicksPerSecond;

    private sealed class PendingMove
    {
        public required DoorDecision Decision { get; init; }
        public required bool MovesEnabled { get; init; }
        public required bool Requested { get; init; }
        public required QualityMeta Meta { get; init; }

        public readonly List<double> From = [];
        public readonly List<double> To = [];
        public int FromSent, FromLost, ToSent, ToLost, Ticks;
        public bool Moved;
    }

    /// <param name="requestMove">
    /// Asked when the switch policy decides the tunnel should move to another way into its relay; true when
    /// the engine will make the move. Only called when moves are enabled at all.
    /// </param>
    public SpikeRecorder(Func<Context> context, Action<string> log, Func<DoorDecision, bool>? requestMove = null)
    {
        _context = context;
        _log = log;
        _requestMove = requestMove ?? (_ => false);
        _file = new QualityFile(log);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(SpikeDetector.TickMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    Step();
                }
                catch (Exception ex)
                {
                    // A diagnostic must never be the reason a tunnel misbehaves, and a loop that logs
                    // the same exception four times a second would be exactly that for the log.
                    if (!_failedOnce)
                    {
                        _failedOnce = true;
                        _log($"The spike recorder hit an error and carries on: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            Attach(null);
            try
            {
                EndMatch();
            }
            catch (Exception ex)
            {
                _log($"The spike recorder could not write its last match summary: {ex.Message}");
            }
            _doors?.Dispose();
            _doors = null;
        }
    }

    private void Step()
    {
        var now = Stopwatch.GetTimestamp();
        if (_epoch == 0) _epoch = now - TickLength / 2;
        var index = (now - _epoch) / TickLength;

        // How late this step ran. The timer asks for one every quarter second; anything well past that
        // is this PC not getting round to the service - see QualityTick.LocalLagMs.
        var localLag = _lastStepAt == 0
            ? 0
            : Math.Max(0, (now - _lastStepAt) * 1000.0 / Stopwatch.Frequency - SpikeDetector.TickMs);
        _lastStepAt = now;

        // The timer fired twice inside one quarter second. Checked before anything is read: this used
        // to come after TakeCadence, which resets the game-packet counters, so the packets and the
        // longest silence of that stretch were taken and thrown away. Left alone, they count into the
        // next tick instead. _lastCreated is only ever written on this thread.
        if (_lastCreated >= 0 && index <= _lastCreated) return;

        var context = _context();
        Attach(context.Tunnel);

        // A different path under the recorder - another relay after a failover, an entry, a landmark
        // that went away - makes the old normal wrong. See SpikeDetector.Restart.
        var pathKey = $"{context.RelayId}|{context.EntryId}|{context.Landmark}";
        if (_pathKey is not null && pathKey != _pathKey)
        {
            IReadOnlyList<SpikeEvent> restarted;
            lock (_gate) restarted = _detector.Restart();
            foreach (var spike in restarted) Report(spike, _matchContext ?? context);
            // Ids only. The key holds the landmark's address, and nothing this recorder writes names one.
            _log($"Spike recorder: the path changed (now relay {context.RelayId ?? "none"}" +
                 $"{(context.EntryId is null ? "" : $" via {context.EntryId}")}" +
                 $"{(context.Landmark is null ? ", no datacentre probe" : "")}); measuring what normal is again.");
        }
        _pathKey = pathKey;

        var tunnel = _tunnel;

        // Probes down the other ways into the relay follow the list the engine hands over - the same list
        // object until the relay, the way in use or the profile changes - and the session they speak for.
        var doorList = tunnel is { SessionId: not 0 } ? context.Doors : [];
        var session = tunnel?.SessionId ?? 0;
        if (!ReferenceEquals(doorList, _doorsList) || session != _doorsSession)
        {
            _doors?.Dispose();
            _doors = doorList.Count == 0 ? null : new DoorProbes(session, doorList, OnDoorReply);
            _doorsList = doorList;
            _doorsSession = session;
            _doorProbeTicks = 0;
        }

        var cadence = tunnel?.TakeCadence();
        // A few packets, not one. Counter-Strike 2 keeps a standby Steam relay alive with a single small
        // packet every ~46 s even in the menus, and counting that as play flipped the recorder into a
        // "match" for five seconds each time - holding back the upload for nothing. A match sends tens
        // of packets every quarter second.
        if (cadence is { } seen && seen.UpPackets + seen.DownPackets >= MinActivePackets) _lastActivityAt = now;

        // The game must be RUNNING, not merely something moving through the tunnel. The lobby routes
        // go in at connect, and a launcher talking to them looked like a match on 2026-09-15.
        var active = tunnel is not null && context.GameRunning &&
                     _lastActivityAt != 0 && now - _lastActivityAt < ActiveFor;

        QualityTick tick;
        lock (_gate)
        {
            if (_lastCreated < 0)
            {
                _lastCreated = index - 1;
                _lastFed = index - 1;
            }
            // A timer that fell behind - a busy machine, a sleep - leaves holes. They go in as ticks
            // that measured nothing, which the detector reads as a break rather than as calm.
            var utcNow = DateTimeOffset.UtcNow;
            for (var missed = _lastCreated + 1; missed < index; missed++)
            {
                Put(new QualityTick(missed, utcNow - TimeSpan.FromMilliseconds((index - missed) * SpikeDetector.TickMs)));
            }

            tick = new QualityTick(index, utcNow) { Active = active, LocalLagMs = localLag };
            if (active && _doors is { } doors)
            {
                tick.CurrentDoor = context.EntryId ?? context.RelayId;
                tick.DoorIds = doors.Ids;
                tick.DoorSent = new bool[doors.Ids.Length];
                tick.DoorMs = new double?[doors.Ids.Length];
            }
            if (cadence is { } c)
            {
                tick.UpPackets = (int)Math.Min(c.UpPackets, int.MaxValue);
                tick.UpGapMs = c.UpGapMs;
                tick.DownPackets = (int)Math.Min(c.DownPackets, int.MaxValue);
                tick.DownGapMs = c.DownGapMs;
            }
            Put(tick);
            _lastCreated = index;
        }

        if (active && tunnel is not null)
        {
            _inMatch = true;
            _matchContext = context;

            if (index % SpikeDetector.TicksPerSecond == 0) ReadAdapter(now, context.RelayAddress);
            // Every tick carries the latest reading, so a spike a quarter second long still says
            // whether the line was busy around it.
            tick.LineDownMbps = _lineDown;
            tick.LineUpMbps = _lineUp;

            SendProbes(tunnel, tick, context, now);
        }

        List<SpikeEvent>? closed = null;
        List<QualityTick>? settledTicks = null;
        lock (_gate)
        {
            while (_lastFed < index - SettleTicks)
            {
                _lastFed++;
                if (Slot(_lastFed) is not { } settled) continue;
                var events = _detector.Feed(settled);
                if (events.Count > 0) (closed ??= []).AddRange(events);
                (settledTicks ??= []).Add(settled);
            }
        }

        // The same settled quarter seconds go to the switch policy and to the move being followed - OUTSIDE the
        // lock, which the downlink thread takes for every pong. Once _lastFed has passed a tick nothing writes to
        // it again, and the policy and the move belong to this loop alone, so there is nothing to guard.
        List<DoorDecision>? decisions = null;
        List<PendingMove>? followed = null;
        if (settledTicks is not null)
        {
            foreach (var settled in settledTicks)
            {
                if (_policy.Feed(settled) is { } decision) (decisions ??= []).Add(decision);
                if (Follow(settled) is { } done) (followed ??= []).Add(done);
            }
        }
        if (closed is not null)
        {
            foreach (var spike in closed) Report(spike, context);
        }
        if (followed is not null)
        {
            foreach (var move in followed) WriteMove(move);
        }
        if (decisions is not null)
        {
            foreach (var decision in decisions) Decide(decision, context);
        }

        if (_inMatch && !active && now - _lastActivityAt >= MatchEndsAfter) EndMatch();
    }

    /// <summary>
    /// Follows the engine's tunnel. A reconnect replaces the TunnelClient, and the recorder has to
    /// stop listening to the old one and start on the new one without either missing or doubling.
    /// </summary>
    private void Attach(TunnelClient? tunnel)
    {
        if (ReferenceEquals(tunnel, _tunnel)) return;

        if (_tunnel is not null) _tunnel.QualitySink = null;
        _tunnel = tunnel;
        if (tunnel is not null) tunnel.QualitySink = this;

        // The server path belonged to the old tunnel's session and possibly a different relay.
        _serverPath = null;
        _walkTarget = null;
        _walkStartedTick = -1;
        _primary = null;
    }

    private QualityTick? Slot(long index)
    {
        if (index < 0) return null;
        var tick = _ring[index % RingSize];
        return tick is not null && tick.Index == index ? tick : null;
    }

    private void Put(QualityTick tick) => _ring[tick.Index % RingSize] = tick;

    // ------------------------------------------------------------------ probes

    private void SendProbes(TunnelClient tunnel, QualityTick tick, Context context, long now)
    {
        if (_gateway is { } gateway)
        {
            tick.GatewaySent = true;
            _ = PingAsync(gateway, tick.Index, static (t, ms) => t.GatewayMs = ms);
        }

        if (context.RelayAddress is { } relay)
        {
            tick.RelayWireSent = true;
            _ = PingAsync(relay, tick.Index, static (t, ms) => t.RelayWireMs = ms);
        }

        tick.RelayProcessSent = true;
        tunnel.SendQualityPing();

        if (context.Landmark is { } landmark)
        {
            tick.DatacentreSent = true;
            tunnel.SendQualityEcho(landmark, Register(ProbeKind.Datacentre, tick.Index, 64), 64);
        }

        // Once a second. The router it reaches answers about that often regardless, so faster only
        // turned three probes in four into losses.
        if (_serverPath is { } path && tick.Index % SpikeDetector.TicksPerSecond == 0)
        {
            tick.ServerPathSent = true;
            tunnel.SendQualityEcho(path.Target, Register(ProbeKind.ServerPath, tick.Index, path.Ttl), path.Ttl);
        }

        // The other ways into the relay, one Probe each, the same quarter second as the pong they are compared with.
        if (_doors is { } doors && tick.DoorSent is { } doorSent)
        {
            for (var slot = 0; slot < doorSent.Length; slot++)
            {
                doorSent[slot] = true;
                doors.Send(slot);
            }

            // Ten seconds of probes and not one answer on any way: a relayd older than the message, which
            // drops it. Said once per list, so a way that never answers is not mistaken for a broken entry.
            if (++_doorProbeTicks == 10 * SpikeDetector.TicksPerSecond && doors.Answered == 0)
            {
                _log($"Entry switching: relay {context.RelayId} answers no probes on the other way(s) in " +
                     $"({string.Join(", ", doors.Ids)}) - its relayd is older than the Probe message. Nothing " +
                     "is measured or moved on this connection until the relay is updated.");
            }
        }

        WalkStep(tunnel, tick.Index, now);
    }

    private ushort Register(ProbeKind kind, long tickIndex, byte ttl)
    {
        lock (_gate)
        {
            var sequence = unchecked(++_sequence);
            _probes[sequence % _probes.Length] = new Probe
            {
                Sequence = sequence,
                Kind = kind,
                TickIndex = tickIndex,
                SentAt = Stopwatch.GetTimestamp(),
                Ttl = ttl,
            };
            return sequence;
        }
    }

    /// <summary>
    /// A0 and A: an ordinary ping over the physical path - the relay's address is pinned to it, the
    /// router is on it. A fresh Ping per probe, because one Ping cannot send while its last request is
    /// still out, and a slow answer is exactly the moment the next quarter second's probe must still go.
    /// </summary>
    private async Task PingAsync(IPAddress address, long index, Action<QualityTick, double> record)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, ProbeTimeoutMs).ConfigureAwait(false);
            if (reply.Status != IPStatus.Success) return;
            lock (_gate)
            {
                if (index > _lastFed && Slot(index) is { } tick) record(tick, reply.RoundtripTime);
            }
        }
        catch (Exception)
        {
            // Unanswered is recorded by the tick's Sent flag with no value, which is what it is.
        }
    }

    public void OnPong(double rttMs, long receivedAt)
    {
        // Filed under the quarter second the ping LEFT in, which is what every other probe is filed
        // under. The pong's own round trip says when that was.
        var sentAt = receivedAt - (long)(rttMs * Stopwatch.Frequency / 1000.0);
        if (sentAt < _epoch) return;
        var index = (sentAt - _epoch) / TickLength;

        lock (_gate)
        {
            if (index <= _lastFed || Slot(index) is not { } tick) return;
            // Keepalives land here too. The worse of two answers in one quarter second is the one a
            // game packet sent in that quarter second could have had.
            tick.RelayProcessMs = tick.RelayProcessMs is { } existing ? Math.Max(existing, rttMs) : rttMs;
        }
    }

    public void OnEcho(ushort sequence, bool timeExceeded, long receivedAt)
    {
        lock (_gate)
        {
            ref var probe = ref _probes[sequence % _probes.Length];
            if (probe.Kind == ProbeKind.None || probe.Sequence != sequence) return;

            var rtt = (receivedAt - probe.SentAt) * 1000.0 / Stopwatch.Frequency;
            var kind = probe.Kind;
            var index = probe.TickIndex;
            var ttl = probe.Ttl;
            probe.Kind = ProbeKind.None;   // one answer per probe; a duplicate is not a second sample

            switch (kind)
            {
                case ProbeKind.Walk:
                    _walkAnswers.Add((ttl, rtt, !timeExceeded));
                    break;

                case ProbeKind.Datacentre:
                    // Sent with a full TTL, so a time-exceeded means a routing loop past the relay -
                    // not the landmark answering, and not a number to put on the chart.
                    if (!timeExceeded && index > _lastFed && Slot(index) is { } tick) tick.DatacentreMs = rtt;
                    break;

                case ProbeKind.ServerPath:
                    if (index > _lastFed && Slot(index) is { } server) server.ServerPathMs = rtt;
                    break;
            }
        }
    }

    /// <summary>
    /// A Probe answered down another way into the relay. Filed under the quarter second it LEFT in, like
    /// every other probe, and only into ticks that were filled by these same probes.
    /// </summary>
    private void OnDoorReply(DoorProbes probes, int slot, long sentAt, long receivedAt)
    {
        var epoch = _epoch;
        if (epoch == 0 || sentAt < epoch) return;
        var index = (sentAt - epoch) / TickLength;
        var rtt = (receivedAt - sentAt) * 1000.0 / Stopwatch.Frequency;

        lock (_gate)
        {
            if (index <= _lastFed || Slot(index) is not { } tick) return;
            if (!ReferenceEquals(tick.DoorIds, probes.Ids) || tick.DoorMs is not { } ms || (uint)slot >= (uint)ms.Length) return;
            ms[slot] = ms[slot] is { } existing ? Math.Min(existing, rtt) : rtt;
        }
    }

    // ------------------------------------------------------------------ the walk towards the server

    /// <summary>
    /// Once per match server, sends echoes towards it at TTL 1 to <see cref="WalkMaxTtl"/> and keeps
    /// the furthest router that answers, or the server itself if it does.
    ///
    /// This is the experiment the landmark cannot replace. The landmark is a different host in the
    /// same datacentre: it sees the relay's route into that datacentre, and nothing of the last
    /// stretch to the particular machine running this match. The first session found five or six
    /// routers past the relay answering, the furthest at the same distance as the landmark - so far
    /// it adds nothing the landmark does not already show, and is recorded rather than used.
    /// </summary>
    private void WalkStep(TunnelClient tunnel, long index, long now)
    {
        if (index % SpikeDetector.TicksPerSecond == 0 && tunnel.Destinations.PrimaryDestination is { } primary)
        {
            _primary = primary;
        }

        if (_walkStartedTick < 0)
        {
            if (_primary is null || _primary.Equals(_walkTarget)) return;
            if (_lastWalkAt != 0 && now - _lastWalkAt < WalkAgainAfter) return;

            _walkTarget = _primary;
            _walkStartedTick = index;
            _walkNextTtl = 1;
            _lastWalkAt = now;
            _serverPath = null;
            lock (_gate) _walkAnswers.Clear();
        }

        var target = _walkTarget!;
        for (var sent = 0; sent < WalkPerTick && _walkNextTtl <= WalkMaxTtl; sent++, _walkNextTtl++)
        {
            tunnel.SendQualityEcho(target, Register(ProbeKind.Walk, index, _walkNextTtl), _walkNextTtl);
        }

        if (_walkNextTtl > WalkMaxTtl && index - _walkStartedTick >= WalkSettleTicks)
        {
            _walkStartedTick = -1;
            FinishWalk(target);
        }
    }

    private void FinishWalk(IPAddress target)
    {
        List<(byte Ttl, double RttMs, bool Reached)> answers;
        lock (_gate)
        {
            answers = [.. _walkAnswers];
            _walkAnswers.Clear();
        }

        if (answers.Any(a => a.Reached))
        {
            var best = answers.Where(a => a.Reached).Min(a => a.RttMs);
            _serverPath = (target, 64);
            _log($"Spike recorder: the game server answers echoes through the tunnel ({best:F0} ms), so it is watched directly.");
            return;
        }

        // TTL 1 expires at the relay itself. Past it are the routers this walk is for.
        var relayAnswered = answers.Any(a => a.Ttl == 1);
        var hops = answers.Where(a => a.Ttl > 1).ToList();
        if (hops.Count == 0)
        {
            _log("Spike recorder: no router past the relay answered on the way to the game server, and the server does " +
                 "not answer echoes. " +
                 (relayAnswered
                     ? "The relay itself did, so expired echoes do come back through the tunnel - the routers are silent. "
                     : "Not even the relay answered an expired echo, so its firewall may be dropping them. ") +
                 "Only the region's landmark is watched: the relay's route into the datacentre, not this server.");
            return;
        }

        var furthest = hops.OrderByDescending(a => a.Ttl).First();
        _serverPath = (target, furthest.Ttl);
        _log($"Spike recorder: on the way to the game server, {hops.Count} of {WalkMaxTtl - 1} routers past the relay " +
             $"answered; the furthest, {furthest.Ttl - 1} hops past it, in {furthest.RttMs:F0} ms. The server itself " +
             "does not answer echoes, so that router is watched as the nearest point to it.");
    }

    // ------------------------------------------------------------------ this PC's own network

    /// <summary>
    /// Finds the physical adapter and its router once a minute, and reads what the adapter carried
    /// since the last second. The traffic figure counts the tunnel too, which is tens of kilobytes a
    /// second against thresholds in megabits - the point is a download or a stream on the same line,
    /// and those are not subtle.
    /// </summary>
    private void ReadAdapter(long now, IPAddress? relay)
    {
        try
        {
            if (_nic is null || now - _nicFoundAt > Stopwatch.Frequency * 60 || !Equals(relay, _nicFor))
            {
                (_nic, _gateway) = FindPhysicalAdapter(relay);
                _nicFor = relay;
                _nicFoundAt = now;
                _lineAt = 0;
                _lineDown = null;
                _lineUp = null;
            }
            if (_nic is null) return;

            var stats = _nic.GetIPv4Statistics();
            if (_lineAt != 0)
            {
                var seconds = (now - _lineAt) / (double)Stopwatch.Frequency;
                if (seconds > 0)
                {
                    _lineDown = Math.Max(0, stats.BytesReceived - _lineReceived) * 8.0 / seconds / 1e6;
                    _lineUp = Math.Max(0, stats.BytesSent - _lineSent) * 8.0 / seconds / 1e6;
                }
            }
            _lineAt = now;
            _lineReceived = stats.BytesReceived;
            _lineSent = stats.BytesSent;
        }
        catch (NetworkInformationException)
        {
            _nic = null;
            _gateway = null;
        }
    }

    /// <summary>
    /// The adapter the relay's traffic actually leaves by, and its router.
    ///
    /// Asked of the routing table, not guessed. "The first adapter with a gateway" is a VPN, a Hyper-V
    /// switch or another booster's adapter on plenty of gaming PCs, and then the router probe pings a
    /// virtual gateway, the link is reported wired when it is Wi-Fi, and the line figure is some other
    /// adapter's - three wrong inputs to the one verdict that blames the player's own network. The
    /// first-with-a-gateway search stays only as the fallback for a table that cannot be read.
    /// </summary>
    private static (NetworkInterface? Nic, IPAddress? Gateway) FindPhysicalAdapter(IPAddress? relay)
    {
        if (relay is not null && IpHelperInterop.BestInterfaceFor(relay) is { } index)
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (nic.GetIPProperties().GetIPv4Properties()?.Index != (int)index) continue;
                    var gateway = nic.GetIPProperties().GatewayAddresses
                        .Select(g => g.Address)
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                    return (nic, gateway);
                }
                catch (NetworkInformationException)
                {
                }
            }
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var gateway = nic.GetIPProperties().GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                if (gateway is null) continue;
                return (nic, gateway);
            }
            catch (NetworkInformationException)
            {
            }
        }
        return (null, null);
    }

    // ------------------------------------------------------------------ reporting

    private QualityMeta Meta(Context context) => new(
        AppVersion,
        context.GameId,
        context.RelayId,
        context.EntryId,
        context.RegionName,
        _nic is null ? null : _nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "wired");

    private void Report(SpikeEvent spike, Context context)
    {
        _file.WriteSpike(spike, Meta(context));

        var now = DateTimeOffset.UtcNow;
        if (now - _windowStart >= LogWindow)
        {
            if (_windowSuppressed > 0)
            {
                _log($"Spike recorder: {_windowSuppressed} more spike(s) in the last {LogWindow.TotalMinutes:F0} minutes, " +
                     $"written to {QualityFile.DirectoryPath} but not to this log.");
            }
            _windowStart = now;
            _windowLogged = 0;
            _windowSuppressed = 0;
        }

        if (_windowLogged >= LoggedPerWindow)
        {
            _windowSuppressed++;
            return;
        }
        _windowLogged++;
        _log(Describe(spike));
    }

    private void EndMatch()
    {
        IReadOnlyList<SpikeEvent> closed;
        MatchSummary? summary;
        lock (_gate)
        {
            closed = _detector.Flush();
            summary = _detector.EndMatch();

            // A fresh normal for the next match: it may be on another relay, and a minute of the last
            // one's numbers would judge the first minute of this one.
            _detector = new SpikeDetector();
        }

        var context = _matchContext ?? _context();
        foreach (var spike in closed) Report(spike, context);

        // A move still being followed is written as far as it got: the match, and with it the comparison, is over.
        var pending = _pendingMove;
        _pendingMove = null;
        if (pending is not null) WriteMove(pending);

        if (_inMatch && summary is not null && summary.ActiveSeconds >= MinMatchSeconds)
        {
            _file.WriteMatch(summary, Meta(context));
            _log(DescribeMatch(summary));
        }
        _inMatch = false;
    }

    // ------------------------------------------------------------------ moves between ways in

    /// <summary>
    /// The policy decided the current way into the relay is worse than another. Asks the engine to move when
    /// moves are on, says so in the log - ids only, never an address - and starts following the next minute.
    /// </summary>
    private void Decide(DoorDecision decision, Context context)
    {
        var requested = context.MovesEnabled && _requestMove(decision);
        var move = new PendingMove
        {
            Decision = decision,
            MovesEnabled = context.MovesEnabled,
            Requested = requested,
            Meta = Meta(context),
        };

        var previous = _pendingMove;
        _pendingMove = move;
        // Five minutes between decisions make this rare; the earlier one is written as far as it got.
        if (previous is not null) WriteMove(previous);

        var what = requested
            ? "moving to it"
            : context.MovesEnabled
                ? "not moving - the tunnel is not connected right now"
                : "recorded, not moved (entrySwitching is \"record\")";
        var seconds = decision.WindowTicks / SpikeDetector.TicksPerSecond;
        _log(decision.Return
            ? $"Entry switching: {decision.To}, left earlier, has recovered - faster than {decision.From} in " +
              $"{decision.WorseShare:P0} of the last {seconds} s, {Figures(decision.ToStats)} against " +
              $"{Figures(decision.FromStats)}; {(requested ? "going back to it" : what)}."
            : $"Entry switching: {decision.From} was worse than {decision.To} in {decision.WorseShare:P0} of the last " +
              $"{seconds} s - {Figures(decision.FromStats)} against {Figures(decision.ToStats)}; {what}.");
    }

    /// <summary>Adds one settled quarter second to the move being followed. Returns it once the minute is complete.</summary>
    private PendingMove? Follow(QualityTick tick)
    {
        if (_pendingMove is not { } move || tick.Index <= move.Decision.TickIndex) return null;

        move.Ticks++;
        Measure(tick, move.Decision.From, ref move.FromSent, ref move.FromLost, move.From);
        Measure(tick, move.Decision.To, ref move.ToSent, ref move.ToLost, move.To);
        if (string.Equals(tick.CurrentDoor, move.Decision.To, StringComparison.OrdinalIgnoreCase)) move.Moved = true;

        if (move.Ticks < FollowTicks) return null;
        _pendingMove = null;
        return move;
    }

    /// <summary>
    /// One way's round trip to relayd in one quarter second: the pong when the tunnel was on that way, its Probe
    /// when it was not. Which is which changes the moment a move is made, and this is what follows it across.
    /// </summary>
    private static void Measure(QualityTick tick, string door, ref int sent, ref int lost, List<double> values)
    {
        if (string.Equals(tick.CurrentDoor, door, StringComparison.OrdinalIgnoreCase))
        {
            if (!tick.RelayProcessSent) return;
            sent++;
            if (tick.RelayProcessMs is { } pong) values.Add(pong);
            else lost++;
            return;
        }

        if (tick.DoorIds is not { } ids || tick.DoorSent is not { } doorSent || tick.DoorMs is not { } doorMs) return;
        var slot = Array.FindIndex(ids, id => string.Equals(id, door, StringComparison.OrdinalIgnoreCase));
        if (slot < 0 || !doorSent[slot]) return;
        sent++;
        if (doorMs[slot] is { } probe) values.Add(probe);
        else lost++;
    }

    private void WriteMove(PendingMove move)
    {
        var afterFrom = DoorSwitchPolicy.Stats(move.From, move.FromSent, move.FromLost);
        var afterTo = DoorSwitchPolicy.Stats(move.To, move.ToSent, move.ToLost);
        var seconds = move.Ticks * SpikeDetector.TickMs / 1000.0;
        _file.WriteMove(move.Decision, move.MovesEnabled, move.Requested, move.Moved, seconds, afterFrom, afterTo, move.Meta);

        _log($"Entry switching, {seconds:F0} s after the decision to leave {move.Decision.From}: " +
             $"{move.Decision.From} {Figures(afterFrom)}, {move.Decision.To} {Figures(afterTo)}; " +
             (move.Moved ? $"the tunnel is on {move.Decision.To}." : $"the tunnel stayed on {move.Decision.From}."));
    }

    private static string Figures(DoorStats stats) =>
        stats.P50 is { } p50
            ? $"median {p50:F0} ms, 95th {stats.P95:F0} ms, {stats.LossPct ?? 0:F1}% lost"
            : stats.Sent == 0 ? "not measured" : "nothing answered";

    internal static string Describe(SpikeEvent spike)
    {
        var when = spike.StartUtc.ToLocalTime().ToString("HH:mm:ss");
        // Three shapes, not two. A spike made only of lost probes has no excess and no silence, and
        // used to be logged as "0 ms of silence from the server" under a verdict about the relay.
        var measuredLost = spike.Measured == QualitySignal.Datacentre ? spike.DatacentreLost : spike.RelayProcessLost;
        var size = spike.SilenceMs > 0 && spike.PeakExcessMs < 15
            ? $"{spike.SilenceMs:F0} ms of silence {(spike.Verdict == SpikeVerdicts.Pc ? "from this PC" : "from the server")}"
            : spike.PeakExcessMs >= 15 || measuredLost == 0
                ? $"+{spike.PeakExcessMs:F0} ms for {spike.DurationMs / 1000:F1} s"
                : $"{measuredLost} probe(s) lost over {spike.DurationMs / 1000:F1} s";

        var where = SpikeVerdicts.Describe(spike.Verdict);
        if (spike.LowConfidence)
        {
            where = $"low confidence - {where} ({LowConfidenceReasons.Describe(spike.LowConfidenceReason)})";
        }

        var text = $"Spike at {when}, {size}: {where}. " +
                   $"Over normal: router {Part(spike.GatewayExcessMs, spike.GatewayLost, spike.Baseline.GatewayMs)}, " +
                   $"to the relay {Part(spike.RelayWireExcessMs, spike.RelayWireLost, spike.Baseline.RelayWireMs)}, " +
                   $"relayd {Part(spike.RelayProcessExcessMs, spike.RelayProcessLost, spike.Baseline.RelayProcessMs)}, " +
                   $"datacentre {Part(spike.DatacentreExcessMs, spike.DatacentreLost, spike.Baseline.DatacentreMs)}; " +
                   $"longest silence from the server {spike.MaxDownGapMs:F0} ms, from this PC {spike.MaxUpGapMs:F0} ms.";

        if (spike.BusyLine)
        {
            text += $" The line was busy at the time ({spike.LineDownMbps ?? 0:F1} Mbps down, {spike.LineUpMbps ?? 0:F1} up) - " +
                    "something else on this connection was using it.";
        }
        return text;
    }

    private static string Part(double excess, int lost, double? normal)
    {
        if (normal is null) return "not measured";
        var text = $"+{excess:F0} ms";
        return lost > 0 ? $"{text} and {lost} lost" : text;
    }

    internal static string DescribeMatch(MatchSummary match)
    {
        var minutes = match.ActiveSeconds / 60;
        var spikes = match.Spikes == 0
            ? "no spikes"
            : $"{match.Spikes} spike(s) ({string.Join(", ", match.SpikesByVerdict.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}"))})";

        var text = $"Match quality over {minutes:F0} min: {spikes}.";
        if (match.DatacentreP50 is { } p50)
        {
            var loss = match.DatacentreSent == 0 ? 0 : 100.0 * match.DatacentreLost / match.DatacentreSent;
            text += $" Datacentre echo median {p50:F0} ms, 95th percentile {match.DatacentreP95:F0} ms, {loss:F1}% lost.";
        }
        if (match.RelayProcessP50 is { } relay)
        {
            text += $" relayd median {relay:F0} ms, 95th percentile {match.RelayProcessP95:F0} ms.";
        }
        return text + $" Details in {QualityFile.DirectoryPath}.";
    }
}
