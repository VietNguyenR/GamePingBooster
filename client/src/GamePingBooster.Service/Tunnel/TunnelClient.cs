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
    private readonly byte[] _psk;
    private readonly ulong _clientId;
    private readonly Action<string> _log;

    private Socket? _socket;
    private WintunAdapter? _adapter;
    private Thread? _uplinkThread;
    private Thread? _downlinkThread;
    private CancellationTokenSource? _cts;

    private ulong _sessionId;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Counters read from other threads, hence Interlocked.
    private long _packetsSent;
    private long _packetsReceived;
    private long _pingsSent;
    private long _pongsReceived;
    private double _lastRttMs = -1;
    private long _lastPongTicks;

    /// <summary>Round-trip time of the handshake itself, measured before any traffic flows.</summary>
    public double HandshakeRttMs { get; private set; } = -1;

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
    public double? LastRttMs => _lastRttMs < 0 ? null : _lastRttMs;

    /// <summary>Fraction of pings that went unanswered - a rough packet loss estimate.</summary>
    public double? LossRatio
    {
        get
        {
            var sent = Interlocked.Read(ref _pingsSent);
            if (sent < 5) return null;
            var lost = sent - Interlocked.Read(ref _pongsReceived);
            return Math.Clamp((double)lost / sent, 0, 1);
        }
    }

    public GpbProtocol.HandshakeResult Session { get; private set; }

    public TunnelClient(IPEndPoint relayEndpoint, byte[] psk, ulong clientId, Action<string> log)
    {
        _relayEndpoint = relayEndpoint;
        _psk = psk;
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

            var req = GpbProtocol.BuildHandshakeReq(_psk, _clientId, DateTimeOffset.UtcNow);
            var sentAt = _clock.ElapsedTicks;
            await _socket.SendAsync(req, SocketFlags.None, ct).ConfigureAwait(false);
            _log($"Sent handshake to {_relayEndpoint} (attempt {attempt}/{attempts})");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var n = await _socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
                if (!GpbProtocol.TryParseHandshakeResp(_psk, buffer.AsSpan(0, n), out var result))
                {
                    _log("Got a reply with a bad signature - ignoring (probably stray internet noise).");
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
                Interlocked.Exchange(ref _lastPongTicks, _clock.ElapsedTicks);
                _log($"Handshake succeeded in {HandshakeRttMs:F0} ms. Tunnel IP: {result.ClientIp}, MTU {result.Mtu}");
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log("No reply within 2 seconds, retrying...");
            }
        }
        throw new TimeoutException(
            $"The relay at {_relayEndpoint} did not answer after {attempts} attempts. " +
            "Check that the relay is running, that the VPS firewall allows the UDP port, and that both sides share the same PSK.");
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
            try
            {
                var len = _adapter!.ReceivePacket(packet);
                if (len < 0)
                {
                    // Ring is empty - sleep until the driver signals a packet, do not spin.
                    _adapter.WaitForPacket(250);
                    continue;
                }
                if (len == 0) continue;

                var wireLen = GpbProtocol.WriteData(wire, _sessionId, packet.AsSpan(0, len));
                _socket!.Send(wire.AsSpan(0, wireLen), SocketFlags.None);
                Interlocked.Increment(ref _packetsSent);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                // ICMP port-unreachable from the relay, or a packet over the MTU: drop it, keep the tunnel alive.
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
                if (version != GpbProtocol.Version) continue;

                switch (type)
                {
                    case GpbProtocol.TypeData:
                        if (GpbProtocol.TryReadData(buffer.AsSpan(0, n), out var sid, out var ip) && sid == _sessionId)
                        {
                            _adapter!.SendPacket(ip);
                            Interlocked.Increment(ref _packetsReceived);
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
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Temporary network outage; try again next tick.
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (_socket is { Connected: true } && _sessionId != 0)
            {
                _socket.Send(GpbProtocol.BuildDisconnect(_sessionId));
            }
        }
        catch (Exception) { /* best effort; the relay's idle timeout handles the rest */ }

        _cts?.Cancel();
        _socket?.Dispose();
        _uplinkThread?.Join(TimeSpan.FromSeconds(2));
        _downlinkThread?.Join(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
    }
}
