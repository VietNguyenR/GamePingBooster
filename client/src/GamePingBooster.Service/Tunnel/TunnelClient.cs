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
    private readonly IPEndPoint _relayEndpoint;
    private readonly TunnelAuth _auth;
    private readonly ulong _clientId;
    private readonly Action<string> _log;

    private Socket? _socket;
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
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            // Do not fragment: if the MTU is wrong we want to know immediately, not silently crawl.
            DontFragment = true,
            ReceiveBufferSize = 4 * 1024 * 1024,
            SendBufferSize = 4 * 1024 * 1024,
        };
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

                var wireLen = GpbProtocol.WriteData(wire, _sessionId, packet.AsSpan(0, len));
                _socket!.Send(wire.AsSpan(0, wireLen), SocketFlags.None);
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
                // Normal shutdown - see the matching case in DownlinkLoop.
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
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
            try
            {
                var n = _socket!.Receive(buffer, SocketFlags.None);
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
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log($"Downlink thread error: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>
    /// Pings every 3 seconds: it measures the RTT shown in the UI and keeps the ISP's NAT
    /// mapping alive while the player sits in a lobby with no game traffic flowing.
    /// </summary>
    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var ping = GpbProtocol.BuildPing(_sessionId, (ulong)_clock.ElapsedTicks);
                await _socket!.SendAsync(ping, SocketFlags.None, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _pingsSent);

                // Every tenth tick, so once every 30 seconds - the same cadence as the relay's own
                // stats line, which makes the two logs easy to read side by side.
                if (++_keepaliveTicks % 10 == 0) ReportDrops();
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Temporary network outage; try again next tick.
            }
        }
    }

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
