using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GamePingBooster.Core.Protocol;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// The client end of the tunnel: one UDP socket talking to the relay, and two threads pumping
/// packets between that socket and the Wintun virtual adapter.
///
/// The two pump loops run on <b>dedicated threads, not the thread pool</b>. Game packets are
/// latency sensitive, and the thread pool can add milliseconds of delay when the machine is
/// under load - which is exactly when someone is playing a game.
/// </summary>
internal sealed class TunnelClient : IDisposable
{
    /// <summary>Where the tunnel sends: the relay, or an entry in front of it. Changes only in <see cref="MoveTo"/>.</summary>
    private IPEndPoint _relayEndpoint;
    private readonly TunnelAuth _auth;
    private readonly ulong _clientId;
    private readonly Action<string> _log;

    /// <summary>
    /// Volatile because <see cref="MoveTo"/> replaces it under the pump threads, which read it for every
    /// packet. See there for why they carry on over the swap rather than stopping.
    /// </summary>
    private volatile Socket? _socket;

    /// <summary>Set first thing in <see cref="Dispose"/>: the one socket failure the pump threads must end on.</summary>
    private volatile bool _disposing;
    private WintunAdapter? _adapter;
    private Thread? _uplinkThread;
    private Thread? _downlinkThread;
    private CancellationTokenSource? _cts;

    private ulong _sessionId;

    /// <summary>Cached from the session so the uplink filter does not recompute it per packet.</summary>
    private uint _subnetBroadcast;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Counters read from other threads, hence Interlocked.
    private long _packetsSent;
    private long _packetsReceived;
    private long _pingsSent;
    private long _pongsReceived;
    private double _lastRttMs = -1;
    private long _lastPongTicks;

    // Every place a packet can be lost on this machine rather than out on the internet. Without
    // these, a player reporting "it still feels laggy" leaves nothing to look at, and the honest
    // answer to "is the booster itself dropping packets?" is a shrug.
    private long _dropUplinkOversize;   // Wintun handed us a packet bigger than our buffer
    private long _dropUplinkPathMtu;    // the socket refused it: too big for the path, DF set
    private long _dropUplinkSendFailed; // ICMP port-unreachable or a transient socket error
    private long _dropUplinkLocalNoise; // multicast, broadcast and IPv6 that cannot cross a tunnel
    private long _dropDownlinkForeign;  // not ours: wrong version, wrong session, unparseable
    private long _dropDownlinkRingFull; // Windows drains the adapter slower than we fill it

    private long _lastReportedDrops;
    private int _keepaliveTicks;

    /// <summary>
    /// The spike recorder listening to this tunnel, or null. Read by the downlink thread for every
    /// ICMP packet and every pong, hence volatile rather than locked.
    /// </summary>
    private volatile IQualitySink? _qualitySink;

    /// <summary>The ICMP id every spike-recorder echo on this tunnel carries. Random so two tunnels never share one.</summary>
    private readonly ushort _qualityEchoId = (ushort)Random.Shared.Next(1, ushort.MaxValue);

    // The game's own packet timing, for the recorder. Each pair is written by exactly one pump thread
    // and read-and-reset by the recorder, so a read can occasionally lose one update to a race. That is
    // a quarter second's longest gap under-reported by one packet on a diagnostic, and not worth a lock
    // on the packet path.
    private long _lastUpUdpAt, _maxUpGap, _upUdpPackets;
    private long _lastDownUdpAt, _maxDownGap, _downUdpPackets;

    /// <summary>
    /// The echo currently in flight through the live tunnel, or null when none is.
    ///
    /// One slot, not a dictionary: probes are sent one at a time and time out well inside their
    /// own interval, so there is never a second one outstanding. The downlink thread reads this
    /// field for every packet it carries, and a hashtable lookup on the packet path to hold at
    /// most one entry would be cost for nothing.
    /// </summary>
    private volatile PendingProbe? _pendingProbe;
    private ushort _probeSequence;

    private sealed class PendingProbe
    {
        public required IPAddress Target { get; init; }
        public required ushort Id { get; init; }
        public required ushort Sequence { get; init; }
        public required long SentTicks { get; init; }

        /// <summary>
        /// Completed by the downlink thread. Continuations run asynchronously on purpose: the
        /// downlink thread is the one carrying game packets into the adapter, and letting an
        /// awaiting probe resume inline would put its work on the latency path.
        /// </summary>
        public TaskCompletionSource<double> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Which game addresses this tunnel is carrying traffic to. Diagnostic only - see
    /// <see cref="GameServerTally"/> for the question it exists to answer.
    /// </summary>
    public GameServerTally Destinations { get; } = new();

    /// <summary>Round-trip time of the handshake itself, measured before any traffic flows.</summary>
    public double HandshakeRttMs { get; private set; } = -1;

    /// <summary>
    /// Whether <see cref="Dispose"/> should tell the relay this client is leaving for good.
    ///
    /// A Disconnect makes the relay release the session AND drop the address reservation held for
    /// this client id. That is right when the user switches the booster off, and actively harmful
    /// during a reconnect: the reservation is the only reason a returning client keeps its inner
    /// IP, and therefore the only reason its routing table survives. A tunnel that is being
    /// replaced rather than shut down must be disposed with this set to false.
    /// </summary>
    public bool AnnounceDisconnect { get; set; } = true;

    /// <summary>
    /// How long since the relay last answered. The reconnect supervisor watches this: it is the
    /// only evidence available that a tunnel carrying no game traffic is still alive.
    /// </summary>
    public TimeSpan SinceLastPong
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastPongTicks);
            if (ticks == 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds((double)(_clock.ElapsedTicks - ticks) / Stopwatch.Frequency);
        }
    }

    public long PacketsSent => Interlocked.Read(ref _packetsSent);
    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);

    /// <summary>Packets lost inside this client, by any cause. Nothing to do with the network.</summary>
    public long PacketsDropped =>
        Interlocked.Read(ref _dropUplinkLocalNoise) +
        Interlocked.Read(ref _dropUplinkOversize) +
        Interlocked.Read(ref _dropUplinkPathMtu) +
        Interlocked.Read(ref _dropUplinkSendFailed) +
        Interlocked.Read(ref _dropDownlinkForeign) +
        Interlocked.Read(ref _dropDownlinkRingFull);
    /// <summary>
    /// The same total WITHOUT local noise - i.e. only the drops that mean something is wrong.
    ///
    /// Local noise is mDNS, SSDP, NetBIOS, IGMP and IPv6 that Windows pushes into every adapter
    /// whatever the routing table says. Filtering it is correct and constant: a healthy idle
    /// tunnel produced 155 of them in ten seconds. Counting that as a fault is how a diagnostic
    /// ends up reporting a problem on every machine it is ever pointed at - which it did, on the
    /// first report taken with the counter wired up.
    ///
    /// What is left is the set worth waking somebody for: a packet too big for the tunnel, one
    /// over the path MTU, a send that failed, a reply for a session we do not have, an adapter
    /// ring that overflowed.
    /// </summary>
    public long PacketsDroppedFaults =>
        Interlocked.Read(ref _dropUplinkOversize) +
        Interlocked.Read(ref _dropUplinkPathMtu) +
        Interlocked.Read(ref _dropUplinkSendFailed) +
        Interlocked.Read(ref _dropDownlinkForeign) +
        Interlocked.Read(ref _dropDownlinkRingFull);

    public double? LastRttMs => _lastRttMs < 0 ? null : _lastRttMs;

    /// <summary>Fraction of pings that went unanswered - a rough packet loss estimate.</summary>
    public double? LossRatio
    {
        get
        {
            var sent = Interlocked.Read(ref _pingsSent);
            if (sent < 5) return null;
            // Discount the most recent ping: it is normally still in flight, and counting it as
            // lost puts a permanent floor under this number - a perfectly healthy tunnel would
            // report several percent loss and send someone hunting a problem that is not there.
            var answered = Interlocked.Read(ref _pongsReceived);
            var outstanding = sent - 1;
            return Math.Clamp((double)(outstanding - answered) / outstanding, 0, 1);
        }
    }

    public GpbProtocol.HandshakeResult Session { get; private set; }

    public TunnelClient(IPEndPoint relayEndpoint, TunnelAuth auth, ulong clientId, Action<string> log)
    {
        _relayEndpoint = relayEndpoint;
        _auth = auth;
        _clientId = clientId;
        _log = log;
    }

    /// <summary>
    /// Handshakes with the relay to obtain a session id and inner IP. Retries up to
    /// <paramref name="attempts"/> times, since UDP gives no delivery guarantee.
    /// </summary>
    public async Task<GpbProtocol.HandshakeResult> HandshakeAsync(int attempts, CancellationToken ct)
    {
        _socket = NewSocket();
        _socket.Connect(_relayEndpoint);

        var buffer = new byte[GpbProtocol.MaxPacketLen];
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // A fresh nonce per attempt, deliberately. A late answer to attempt 1 arriving
            // during attempt 2 is then rejected on the echo instead of being adopted - which
            // is the retry race that used to leave the client talking into a session the
            // relay had already retired.
            var req = _auth.BuildRequest(_clientId, DateTimeOffset.UtcNow, out var nonce);
            var sentAt = _clock.ElapsedTicks;
            await _socket.SendAsync(req, SocketFlags.None, ct).ConfigureAwait(false);
            _log($"Sent handshake to {_auth.Describe} (attempt {attempt}/{attempts})");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var n = await _socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
                if (!_auth.TryParseResponse(buffer.AsSpan(0, n), nonce, out var result))
                {
                    _log("Got a reply with a bad signature or a nonce we did not send - ignoring " +
                         "(stray internet noise, or a late answer to an earlier attempt).");
                    continue;
                }
                if (result.Status != GpbProtocol.StatusOk)
                {
                    throw new InvalidOperationException(result.Status switch
                    {
                        GpbProtocol.StatusPoolFull => "The relay is full, try another one.",
                        GpbProtocol.StatusShutdown => "The relay is shutting down for maintenance.",
                        GpbProtocol.StatusVersionMismatch =>
                            $"The relay speaks a different protocol version than this client (we are v{GpbProtocol.Version}). Update whichever is older.",
                        GpbProtocol.StatusCredentialExpired =>
                            "Your subscription has expired. Sign in again to renew it.",
                        GpbProtocol.StatusCredentialRevoked =>
                            "This device is no longer authorised. Check your devices in the app.",
                        GpbProtocol.StatusTierTooLow =>
                            "This relay is reserved for a higher plan. Your subscription is fine - " +
                            "pick another relay, or upgrade to reach this one.",
                        _ => $"The relay refused the connection (status {result.Status})."
                    });
                }

                // The handshake is one clean round trip over the physical path, which makes it
                // the cheapest honest measurement of client-to-relay latency available - no
                // session needed, nothing to tear down, and it is exactly the number that
                // decides which relay to use.
                HandshakeRttMs = (_clock.ElapsedTicks - sentAt) * 1000.0 / Stopwatch.Frequency;

                _sessionId = result.SessionId;
                Session = result;
                _subnetBroadcast = UplinkFilter.SubnetBroadcastFor(result.ClientIp);
                Interlocked.Exchange(ref _lastPongTicks, _clock.ElapsedTicks);
                _log($"Handshake succeeded in {HandshakeRttMs:F0} ms. Tunnel IP: {result.ClientIp}, MTU {result.Mtu}");
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log("No reply within 2 seconds, retrying...");
            }
        }
        // Silence has several causes and this side cannot tell them apart, so name them all.
        // An earlier version named only one - "a relay in PSK mode will not answer" - and said
        // it about a relay that was in licensed mode and had simply refused the token, which
        // sends the reader to the wrong machine. The same mistake was made in licence-gen probe
        // on the same day.
        throw new TimeoutException(
            $"The relay at {_relayEndpoint} did not answer after {attempts} attempts. " +
            (_auth.IsToken
                ? "Any of these produces silence: the relay refused the licence token (its log " +
                  "says why), the relay is running in PSK mode and never answers a token " +
                  "handshake, the relay is not running, or the UDP port is not reachable."
                : "Any of these produces silence: the two sides do not share the same PSK, the " +
                  "relay is running in licensed mode and never answers a PSK handshake, the " +
                  "relay is not running, or the UDP port is not reachable."));
    }

    /// <summary>
    /// Round trip from here to <paramref name="landmark"/> and back, through this relay, in
    /// milliseconds - or null if nothing came back.
    ///
    /// This is the second leg the relay comparison used to be blind to. The handshake measures
    /// the player to the relay; a game server is measured from the relay onwards, and nothing on
    /// this machine can see that distance. So we send something the relay's kernel will forward
    /// like any other tunnelled packet - an ICMP echo to a landmark inside the game's datacentre
    /// - and time the whole path. What comes back is not leg one plus leg two estimated
    /// separately; it is the real number, including whatever the relay's own forwarding costs.
    ///
    /// Must be called after <see cref="HandshakeAsync"/> and before <see cref="StartPumping"/>:
    /// it reads the socket directly, and once the pump threads own it there is nobody to hand a
    /// reply back to.
    /// </summary>
    public async Task<double?> MeasureThroughTunnelAsync(
        IPAddress landmark, int attempts, CancellationToken ct)
    {
        if (_socket is null || _sessionId == 0) return null;

        var source = Session.ClientIp;
        var inner = new byte[GpbProtocol.MaxPacketLen];
        var wire = new byte[GpbProtocol.MaxPacketLen];
        var buffer = new byte[GpbProtocol.MaxPacketLen];
        double? best = null;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // A fresh id per attempt, so a late reply to attempt 1 cannot be timed against
            // attempt 2's clock and report a path that is faster than it is.
            var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            var sequence = (ushort)(attempt + 1);
            var innerLen = IcmpEcho.Build(inner, source, landmark, id, sequence);
            var wireLen = GpbProtocol.WriteData(wire, _sessionId, inner.AsSpan(0, innerLen));

            var sentAt = _clock.ElapsedTicks;
            try
            {
                await _socket.SendAsync(wire.AsMemory(0, wireLen), SocketFlags.None, ct).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return best;
            }

            // One deadline for the attempt as a whole, not per receive: the tunnel carries no
            // other traffic yet, but the relay may still answer a keepalive or a stray packet
            // from a previous session, and each of those would otherwise buy another full wait.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(LandmarkTimeoutMs));
            try
            {
                while (true)
                {
                    var n = await _socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
                    if (!GpbProtocol.TryReadData(buffer.AsSpan(0, n), out var sid, out var ip)) continue;
                    if (sid != _sessionId) continue;
                    if (!IcmpEcho.IsReplyTo(ip, landmark, id, sequence)) continue;

                    var rtt = (_clock.ElapsedTicks - sentAt) * 1000.0 / Stopwatch.Frequency;
                    if (best is null || rtt < best) best = rtt;
                    break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Silence. A landmark that never answers through any relay is a landmark
                // problem; one that answers through some and not others is a relay problem.
                // Neither is decided here - the caller sees which relays produced a number.
            }
            catch (SocketException)
            {
                return best;
            }
        }

        return best;
    }

    /// <summary>
    /// How long a landmark echo may take before the attempt is abandoned. Generous on purpose:
    /// this runs once per relay at connect time, and the whole point is to catch the relay whose
    /// second leg is long. Cutting it short would score exactly that relay as "no answer" and
    /// hand it the fallback, which is the first leg alone - the number we are trying to stop
    /// deciding things.
    /// </summary>
    private const int LandmarkTimeoutMs = 2000;

    /// <summary>
    /// Best of <paramref name="attempts"/> round trips to the relay, measured with pings rather
    /// than handshakes.
    ///
    /// This exists to make the first leg comparable with the second. The offset that turns the
    /// relay ping into an in-game estimate is <c>endToEnd - legOne</c>, and until now legOne was
    /// <see cref="HandshakeRttMs"/> - a SINGLE sample - while endToEnd was the best of three
    /// echoes. Subtracting a best-of-three from a single sample does not measure a distance, it
    /// measures which of the two got luckier, and on 2026-09-10 it produced 45 - 45 = 0 on a path
    /// whose second leg is really about 2.5 ms. The app then showed the relay ping with a label
    /// saying in-game ping.
    ///
    /// A ping is the right instrument for the repeat: it costs one small packet, and unlike a
    /// handshake it does not consume a session or an address out of the relay's pool - which is
    /// the reason best-of-three handshakes were rejected when this was first written.
    ///
    /// Same constraint as <see cref="MeasureThroughTunnelAsync"/>: after the handshake, before
    /// the pump threads take the socket.
    /// </summary>
    public async Task<double?> MeasureRelayRttAsync(int attempts, CancellationToken ct)
    {
        if (_socket is null || _sessionId == 0) return null;

        var buffer = new byte[GpbProtocol.MaxPacketLen];
        double? best = null;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var sentAt = _clock.ElapsedTicks;
            try
            {
                var ping = GpbProtocol.BuildPing(_sessionId, (ulong)sentAt);
                await _socket.SendAsync(ping, SocketFlags.None, ct).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return best;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(RelayPingTimeoutMs));
            try
            {
                while (true)
                {
                    var n = await _socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
                    if (!GpbProtocol.TryReadPong(buffer.AsSpan(0, n), out var sid, out var stamp)) continue;
                    if (sid != _sessionId) continue;

                    // Time against the stamp the relay echoed back, not against sentAt: a pong
                    // for an earlier attempt still in flight would otherwise be timed on this
                    // attempt's clock and report a relay that is nearer than it is.
                    if (stamp != (ulong)sentAt) continue;

                    var rtt = (_clock.ElapsedTicks - sentAt) * 1000.0 / Stopwatch.Frequency;
                    if (best is null || rtt < best) best = rtt;
                    break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // This attempt went unanswered. The others still count; a relay that answers none
                // of them returns null and the caller falls back to the handshake sample.
            }
            catch (SocketException)
            {
                return best;
            }
        }

        return best;
    }

    /// <summary>
    /// A relay ping is a round trip to a machine that is already talking to us, so it either
    /// comes back quickly or it is lost. Nothing like the landmark timeout is needed.
    /// </summary>
    private const int RelayPingTimeoutMs = 500;

    /// <summary>
    /// One ICMP echo to <paramref name="target"/> through the tunnel while it is carrying
    /// traffic, answered by the downlink thread.
    ///
    /// This is the measurement everything else was standing in for. <see cref="GameServerTally"/>
    /// knows the address the game actually chose, and an echo to that address travels the exact
    /// path the game's packets take - the player's connection, the relay, the relay's onward
    /// route, the server itself - so what comes back needs no offset, no subtraction and no
    /// clamping. The landmark model estimated all of that from a different host measured once at
    /// connect time.
    ///
    /// Unlike <see cref="MeasureThroughTunnelAsync"/> this CANNOT read the socket: by now the
    /// downlink thread owns it. So the reply is picked out of the downlink path instead - see the
    /// probe check in <see cref="DownlinkLoop"/> - and handed back through a completion source.
    ///
    /// Whether a live game server answers ICMP at all is not known yet. Testing it against
    /// servers from a finished match cannot say: a match server is torn down when the match ends,
    /// so silence there means the machine is gone, not that echo is filtered. The caller treats a
    /// null as "no answer this time" and keeps the estimate running.
    /// </summary>
    public async Task<double?> ProbeGameServerAsync(IPAddress target, int timeoutMs, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null || _sessionId == 0) return null;
        if (_pendingProbe is not null) return null;   // one at a time; the caller is a timer

        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var sequence = unchecked(++_probeSequence);

        var inner = new byte[GpbProtocol.MaxPacketLen];
        var wire = new byte[GpbProtocol.MaxPacketLen];
        var innerLen = IcmpEcho.Build(inner, Session.ClientIp, target, id, sequence);
        var wireLen = GpbProtocol.WriteData(wire, _sessionId, inner.AsSpan(0, innerLen));

        var probe = new PendingProbe
        {
            Target = target,
            Id = id,
            Sequence = sequence,
            SentTicks = _clock.ElapsedTicks,
        };
        _pendingProbe = probe;

        try
        {
            await socket.SendAsync(wire.AsMemory(0, wireLen), SocketFlags.None, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            await using var registration = timeout.Token.Register(() => probe.Completion.TrySetCanceled());

            return await probe.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;   // no answer inside the window
        }
        catch (SocketException)
        {
            return null;
        }
        finally
        {
            // Always clear the slot, including on the timeout path. Leaving a dead probe in place
            // would make the downlink thread keep testing every packet against it and would block
            // every later probe, which fails closed to "the game server never answers".
            _pendingProbe = null;
        }
    }

    /// <summary>Starts both pump threads plus the keepalive loop.</summary>
    public void StartPumping(WintunAdapter adapter, CancellationToken ct)
    {
        _adapter = adapter;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        _uplinkThread = new Thread(() => UplinkLoop(token))
        {
            Name = "gpb-uplink",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _downlinkThread = new Thread(() => DownlinkLoop(token))
        {
            Name = "gpb-downlink",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _uplinkThread.Start();
        _downlinkThread.Start();

        _ = Task.Run(() => KeepaliveLoopAsync(token), token);
    }

    /// <summary>Wintun to relay: read what Windows pushed into the adapter, wrap it, send it.</summary>
    private void UplinkLoop(CancellationToken ct)
    {
        var packet = new byte[GpbProtocol.MaxPacketLen];
        var wire = new byte[GpbProtocol.MaxPacketLen];

        while (!ct.IsCancellationRequested)
        {
            Socket? current = null;
            try
            {
                var len = _adapter!.ReceivePacket(packet);
                if (len < 0)
                {
                    // Ring is empty - sleep until the driver signals a packet, do not spin.
                    _adapter.WaitForPacket(250);
                    continue;
                }
                if (len == 0)
                {
                    // ReceivePacket returns 0 when the packet did not fit the buffer, which can
                    // only happen if the adapter MTU has been raised past MaxPacketLen.
                    Interlocked.Increment(ref _dropUplinkOversize);
                    continue;
                }

                if (UplinkFilter.IsLocalNoise(packet.AsSpan(0, len), _subnetBroadcast))
                {
                    Interlocked.Increment(ref _dropUplinkLocalNoise);
                    continue;
                }

                // Before wrapping, while the inner IP header is still in front of us. Reading it
                // here costs one header parse and saves ever having to reproduce this from a
                // packet capture on a tester's machine. Sits after the filter so the summary is
                // game traffic rather than the discovery chatter that was burying it.
                Destinations.Note(packet.AsSpan(0, len));

                // UDP only: the game's own stream. The lobby's TCP connection shares the tunnel and
                // goes quiet for seconds at a time, which would read as a frozen game.
                if (len >= IcmpEcho.Ipv4HeaderLen && packet[9] == 17)
                {
                    NoteCadence(ref _lastUpUdpAt, ref _maxUpGap, ref _upUdpPackets);
                }

                var wireLen = GpbProtocol.WriteData(wire, _sessionId, packet.AsSpan(0, len));
                current = _socket;
                current!.Send(wire.AsSpan(0, wireLen), SocketFlags.None);
                Interlocked.Increment(ref _packetsSent);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
            {
                // Too big for the path with DF set. Counted on its own because it is the signature
                // of an MTU that is wrong for this player's connection: a steady trickle here,
                // affecting only large packets, is the classic path-MTU black hole.
                Interlocked.Increment(ref _dropUplinkPathMtu);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // ICMP port-unreachable from the relay: drop it, keep the tunnel alive.
                Interlocked.Increment(ref _dropUplinkSendFailed);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.Interrupted or SocketError.OperationAborted)
            {
                // Normal shutdown - see the matching case in DownlinkLoop - or a MoveTo swapping the
                // socket under this send, in which case the next packet goes out on the new one. A socket
                // closed with nothing to replace it is neither, and ends the loop - and so does a failure
                // before any socket was touched (current still null): that is the adapter, not a move, and
                // carrying on would spin on it.
                if (_disposing || ct.IsCancellationRequested || current is null || ReferenceEquals(current, _socket)) return;
            }
            catch (ObjectDisposedException)
            {
                if (_disposing || ct.IsCancellationRequested || current is null || ReferenceEquals(current, _socket)) return;
            }
            catch (Exception ex)
            {
                _log($"Uplink thread error: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>Relay to Wintun: receive from the relay, unwrap, inject into the Windows stack.</summary>
    private void DownlinkLoop(CancellationToken ct)
    {
        var buffer = new byte[GpbProtocol.MaxPacketLen];

        while (!ct.IsCancellationRequested)
        {
            Socket? current = null;
            try
            {
                current = _socket;
                var n = current!.Receive(buffer, SocketFlags.None);
                if (n < 1) continue;

                var (version, type) = GpbProtocol.ParseHeader(buffer[0]);
                if (version != GpbProtocol.Version)
                {
                    Interlocked.Increment(ref _dropDownlinkForeign);
                    continue;
                }

                switch (type)
                {
                    case GpbProtocol.TypeData:
                        if (GpbProtocol.TryReadData(buffer.AsSpan(0, n), out var sid, out var ip) && sid == _sessionId)
                        {
                            // An answer to our own game-server probe, if one is outstanding. It is
                            // consumed here rather than injected: the request was assembled by
                            // hand instead of being sent through a socket, so Windows has no
                            // matching request and would drop the reply on the floor - and doing
                            // that would also count it as a delivered packet.
                            //
                            // The check costs a few length tests and a protocol byte compare, and
                            // only while a probe is in flight, which is a fraction of a second
                            // once a second. Everything else falls through untouched.
                            var pending = _pendingProbe;
                            if (pending is not null &&
                                IcmpEcho.IsReplyTo(ip, pending.Target, pending.Id, pending.Sequence))
                            {
                                var rtt = (_clock.ElapsedTicks - pending.SentTicks) * 1000.0 / Stopwatch.Frequency;
                                pending.Completion.TrySetResult(rtt);
                                break;
                            }

                            // The spike recorder's echoes, consumed for the same reason as the probe
                            // above. Tested on the protocol byte first, so the game's UDP never pays
                            // for more than one comparison.
                            if (ip.Length >= IcmpEcho.Ipv4HeaderLen && ip[9] == 1 &&
                                _qualitySink is { } sink &&
                                IcmpEcho.TryReadQualityReply(ip, _qualityEchoId, out var qualitySequence, out var expired))
                            {
                                sink.OnEcho(qualitySequence, expired, Stopwatch.GetTimestamp());
                                break;
                            }

                            if (ip.Length >= IcmpEcho.Ipv4HeaderLen && ip[9] == 17)
                            {
                                NoteCadence(ref _lastDownUdpAt, ref _maxDownGap, ref _downUdpPackets);
                            }

                            if (_adapter!.SendPacket(ip))
                            {
                                Interlocked.Increment(ref _packetsReceived);
                            }
                            else
                            {
                                Interlocked.Increment(ref _dropDownlinkRingFull);
                            }
                        }
                        else
                        {
                            Interlocked.Increment(ref _dropDownlinkForeign);
                        }
                        break;

                    case GpbProtocol.TypePong:
                        if (GpbProtocol.TryReadPong(buffer.AsSpan(0, n), out var psid, out var stamp) && psid == _sessionId)
                        {
                            var now = (ulong)_clock.ElapsedTicks;
                            _lastRttMs = (now - stamp) * 1000.0 / Stopwatch.Frequency;
                            Interlocked.Exchange(ref _lastPongTicks, (long)now);
                            Interlocked.Increment(ref _pongsReceived);
                            _qualitySink?.OnPong(_lastRttMs, Stopwatch.GetTimestamp());
                        }
                        break;
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // The relay is not up yet or just restarted - keep reading.
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.Interrupted or SocketError.OperationAborted)
            {
                // Dispose() closed the socket while Receive was blocking on it. That is how this
                // loop is meant to end, so it is not an error: logging it as one ("a blocking
                // operation was interrupted by a call to WSACancelBlockingCall") puts a scary
                // line in the middle of every reconnect and sends whoever reads the log next
                // hunting a fault that is not there.
                //
                // MoveTo ends a Receive the same way when it retires the old socket, and then this loop
                // must not end: the new socket is already in _socket, with the relay answering on it. The
                // same socket still in _socket means nothing replaced it, and reading it again would spin.
                if (_disposing || ct.IsCancellationRequested || ReferenceEquals(current, _socket)) return;
            }
            catch (ObjectDisposedException)
            {
                if (_disposing || ct.IsCancellationRequested || ReferenceEquals(current, _socket)) return;
            }
            catch (Exception ex)
            {
                _log($"Downlink thread error: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>
    /// Pings every second: it measures the RTT shown in the UI and keeps the ISP's NAT mapping
    /// alive while the player sits in a lobby with no game traffic flowing.
    ///
    /// It was every three seconds, which made the number on screen a three-second-old sample
    /// repeated three times - the status is pushed to the UI once a second, so two of every three
    /// updates carried nothing new. A game redraws its own ping about once a second, and a
    /// booster whose number lags it by up to three seconds looks wrong even when it is right.
    ///
    /// The extra traffic is nothing: one ping is a few dozen bytes, so this is well under a
    /// kilobyte a minute against a game sending over a hundred packets a second.
    /// </summary>
    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var ping = GpbProtocol.BuildPing(_sessionId, (ulong)_clock.ElapsedTicks);
                await _socket!.SendAsync(ping, SocketFlags.None, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _pingsSent);

                // Every thirtieth tick, so still once every 30 seconds now that the tick is a
                // second - the same cadence as the relay's own stats line, which makes the two
                // logs easy to read side by side. This divisor and the timer above have to move
                // together; missing that would have turned a half-minute report into a ten-second
                // one and tripled the noise in the log.
                if (++_keepaliveTicks % 30 == 0) ReportDrops();
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Temporary network outage; try again next tick.
            }
        }
    }

    // ------------------------------------------------------------ spike recorder

    /// <summary>The recorder receiving pongs and echo answers, or null. See <see cref="IQualitySink"/>.</summary>
    internal IQualitySink? QualitySink
    {
        get => _qualitySink;
        set => _qualitySink = value;
    }

    private static void NoteCadence(ref long lastAt, ref long maxGap, ref long packets)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = lastAt;
        lastAt = now;
        Interlocked.Increment(ref packets);
        if (previous != 0 && now - previous > Volatile.Read(ref maxGap)) Volatile.Write(ref maxGap, now - previous);
    }

    /// <summary>Reads and resets the game-packet timing. See <see cref="Cadence"/>.</summary>
    internal Cadence TakeCadence()
    {
        var upPackets = Interlocked.Exchange(ref _upUdpPackets, 0);
        var upGap = Interlocked.Exchange(ref _maxUpGap, 0);
        var downPackets = Interlocked.Exchange(ref _downUdpPackets, 0);
        var downGap = Interlocked.Exchange(ref _maxDownGap, 0);
        return new Cadence(
            upPackets, upGap * 1000.0 / Stopwatch.Frequency, Interlocked.Read(ref _lastUpUdpAt),
            downPackets, downGap * 1000.0 / Stopwatch.Frequency, Interlocked.Read(ref _lastDownUdpAt));
    }

    /// <summary>
    /// One extra keepalive, for the recorder. Counted as a sent ping so <see cref="LossRatio"/> keeps
    /// comparing like with like. Its pong reaches the recorder through <see cref="QualitySink"/>.
    /// </summary>
    internal void SendQualityPing()
    {
        var socket = _socket;
        if (socket is null || _sessionId == 0) return;
        try
        {
            socket.Send(GpbProtocol.BuildPing(_sessionId, (ulong)_clock.ElapsedTicks), SocketFlags.None);
            Interlocked.Increment(ref _pingsSent);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// One ICMP echo through the tunnel for the recorder, carrying this tunnel's quality id. The
    /// answer - reply or time-exceeded - arrives through <see cref="QualitySink"/>.
    ///
    /// Safe beside the pump threads: a UDP send is one datagram, and the socket takes concurrent ones.
    /// </summary>
    internal void SendQualityEcho(IPAddress target, ushort sequence, byte ttl)
    {
        var socket = _socket;
        if (socket is null || _sessionId == 0) return;

        Span<byte> inner = stackalloc byte[IcmpEcho.Ipv4HeaderLen + IcmpEcho.IcmpHeaderLen + 32];
        Span<byte> wire = stackalloc byte[GpbProtocol.DataHeaderLen + inner.Length];
        var innerLen = IcmpEcho.Build(inner, Session.ClientIp, target, _qualityEchoId, sequence, 32, ttl);
        var wireLen = GpbProtocol.WriteData(wire, _sessionId, inner[..innerLen]);
        try
        {
            socket.Send(wire[..wireLen], SocketFlags.None);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ------------------------------------------------------------ ways into the relay

    /// <summary>Where this tunnel is sending right now: the relay, or the entry in front of it.</summary>
    internal IPEndPoint Endpoint => _relayEndpoint;

    /// <summary>The relay's id for this session, for probes down the other ways in. Zero before the handshake.</summary>
    internal ulong SessionId => _sessionId;

    /// <summary>
    /// Moves this tunnel to another way into the SAME relay - an entry in front of it, or the relay itself
    /// when it came in through an entry - with no handshake, while the pumps keep running.
    ///
    /// Nothing about the session changes, which is why the game notices nothing. relayd keys a session by
    /// its id, not by where it comes from, and moves its return address to wherever the latest Ping or
    /// Data arrived from. The inner address, and with it the relay's NAT mapping and the address the game
    /// server sees, stay exactly as they were. What moves is only the stretch between this PC and relayd.
    /// Pointed at another relay this would be a session id that relay has never issued, and silence.
    ///
    /// The order is what keeps it clean. The new socket goes into <see cref="_socket"/> first, so the very
    /// next game packet leaves by the new way and turns the relay's return path round with it; the old
    /// socket is then closed - WITHOUT a Disconnect, which would end the session it shares - and the
    /// downlink thread, woken out of its Receive, carries on reading the new one. The ping after it turns
    /// the return path round even in a quarter second the game sends nothing. Whatever was already on its
    /// way back down the old way is lost: a round trip's worth of packets, against minutes on a bad road.
    ///
    /// The caller pins the new address to the physical adapter first. See RouteManager.PinDoorRoutes.
    /// </summary>
    internal void MoveTo(IPEndPoint endpoint)
    {
        if (_sessionId == 0) throw new InvalidOperationException("The tunnel has no session to move.");

        var fresh = NewSocket();
        try
        {
            fresh.Connect(endpoint);
        }
        catch
        {
            fresh.Dispose();
            throw;
        }

        var old = _socket;
        _socket = fresh;
        _relayEndpoint = endpoint;
        old?.Dispose();

        // Disposed while this ran - a disconnect racing a move. The pumps are already gone; leave no socket behind them.
        if (_disposing)
        {
            fresh.Dispose();
            return;
        }

        try
        {
            fresh.Send(GpbProtocol.BuildPing(_sessionId, (ulong)_clock.ElapsedTicks), SocketFlags.None);
            Interlocked.Increment(ref _pingsSent);
        }
        catch (SocketException)
        {
            // The next game packet or keepalive turns the return path round instead.
        }
    }

    private static Socket NewSocket() => new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
    {
        // Do not fragment: if the MTU is wrong we want to know immediately, not silently crawl.
        DontFragment = true,
        ReceiveBufferSize = 4 * 1024 * 1024,
        SendBufferSize = 4 * 1024 * 1024,
    };

    /// <summary>
    /// Logs the drop breakdown, but only when it has changed. A healthy tunnel stays silent, so
    /// the mere appearance of this line in a log is itself the signal.
    /// </summary>
    private void ReportDrops()
    {
        var total = PacketsDropped;
        if (total == Interlocked.Read(ref _lastReportedDrops)) return;
        Interlocked.Exchange(ref _lastReportedDrops, total);

        _log($"Packets dropped inside the client: {total} total - " +
             $"local noise {Interlocked.Read(ref _dropUplinkLocalNoise)}, " +
             $"uplink oversize {Interlocked.Read(ref _dropUplinkOversize)}, " +
             $"over path MTU {Interlocked.Read(ref _dropUplinkPathMtu)}, " +
             $"send failed {Interlocked.Read(ref _dropUplinkSendFailed)}, " +
             $"downlink not ours {Interlocked.Read(ref _dropDownlinkForeign)}, " +
             $"adapter ring full {Interlocked.Read(ref _dropDownlinkRingFull)}");
    }

    public void Dispose()
    {
        _disposing = true;

        // One last account before the counters go with the object.
        ReportDrops();

        try
        {
            if (AnnounceDisconnect && _socket is { Connected: true } && _sessionId != 0)
            {
                _socket.Send(GpbProtocol.BuildDisconnect(_sessionId));
            }
        }
        catch (Exception) { /* best effort; the relay's idle timeout handles the rest */ }

        _cts?.Cancel();
        _socket?.Dispose();

        // Both pumps must be gone before the caller disposes the Wintun adapter: they call into
        // the adapter's session on every packet, and WintunEndSession while one is still running
        // is a use-after-free that takes the whole LocalSystem service down. Neither loop can
        // block for long - the uplink waits at most 250 ms on the ring, and disposing the socket
        // above unblocks the downlink - so a timeout here means something is genuinely stuck, and
        // it must not pass silently.
        var uplinkStopped = _uplinkThread?.Join(TimeSpan.FromSeconds(2)) ?? true;
        var downlinkStopped = _downlinkThread?.Join(TimeSpan.FromSeconds(2)) ?? true;
        if (!uplinkStopped || !downlinkStopped)
        {
            _log($"WARNING: a pump thread did not stop within 2s (uplink stopped: {uplinkStopped}, " +
                 $"downlink stopped: {downlinkStopped}). The adapter is about to be released while " +
                 "it may still be in use - if the service dies right after this line, that is why.");
        }

        _cts?.Dispose();
    }
}
