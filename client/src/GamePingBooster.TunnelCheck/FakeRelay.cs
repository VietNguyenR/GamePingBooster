using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using GamePingBooster.Core.Protocol;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// A relay on loopback that speaks the v3 PSK wire format and keeps relayd's rules - the ones the client's
/// packet path depends on - so that path can be driven in-process with no VPS, driver or Administrator.
///
/// Kept from relay/internal/server/server.go, on purpose and nothing more:
///   - a handshake from a client id that already has a session gets THAT session back (allocSession's
///     resume path), with the same inner address;
///   - Data whose inner source is not the session's inner address is dropped (anti-spoofing);
///   - Data and Ping move the session's return address to wherever they came from (roaming); Probe does not;
///   - a Disconnect ends the session only from the session's current address.
/// And, standing in for the relay's kernel: an ICMP echo request to any address is answered.
///
/// Several ports can front one relay - "doors", as an entry in front of a relay is to the client.
/// </summary>
internal sealed class FakeRelay : IDisposable
{
    private readonly byte[] _psk;
    private readonly List<UdpClient> _sockets = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];
    private readonly ConcurrentDictionary<ulong, Session> _bySessionId = new();
    private readonly ConcurrentDictionary<ulong, Session> _byClientId = new();
    private int _nextInner;

    public sealed class Session
    {
        public required ulong Id { get; init; }
        public required ulong ClientId { get; init; }
        public required uint InnerIp { get; init; }
        public volatile IPEndPoint? Address;
        public volatile UdpClient? Socket;
    }

    /// <summary>A Data packet that passed every check: its inner IP packet, the whole datagram, when, and which port it came in on.</summary>
    public readonly record struct Arrival(byte[] Inner, byte[] Wire, long At, int Port, ulong SessionId);

    public ConcurrentQueue<Arrival> Arrivals { get; } = new();

    public long Spoofed;
    public long Pings;

    /// <summary>Pings this returns true for, by their 1-based number, are dropped without a pong. Null drops none.</summary>
    public volatile Func<long, bool>? DropPing;

    /// <summary>
    /// Extra time before a Pong or a Probe reply leaves each door, by door index: a road into the relay that has become
    /// slow, as Viettel's to sg-2 was on 2026-09-15. Zero sends at once. Set it before, or while, a scenario runs.
    /// </summary>
    public double[] DoorDelayMs { get; }

    /// <summary>
    /// Extra time before a Pong or a Probe reply, by the port the client sent FROM: an ISP spreading flows over
    /// parallel links by a hash of the ports, each link its own round trip (LanePick, measured 2026-09-30). Null adds none.
    /// </summary>
    public volatile Func<int, double>? LaneDelayMs;

    public long Probes;
    public long Disconnects;
    public long Handshakes;

    public IReadOnlyList<int> Ports => _sockets.Select(s => ((IPEndPoint)s.Client.LocalEndPoint!).Port).ToList();

    public IPEndPoint Door(int index = 0) => new(IPAddress.Loopback, Ports[index]);

    /// <param name="firstInner">The last octet of the first inner address handed out, 10.77.0.x.</param>
    public FakeRelay(byte[] psk, int doors = 1, int firstInner = 2)
    {
        _psk = psk;
        _nextInner = firstInner;
        DoorDelayMs = new double[doors];
        for (var i = 0; i < doors; i++)
        {
            var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

            // A scenario hands the client thousands of packets at once, and loopback delivers them faster than
            // this loop drains them: with Windows' default buffer the kernel dropped a few percent HERE, which
            // read as the client losing them (it had sent every one - measured before this line was added).
            socket.Client.ReceiveBufferSize = 16 * 1024 * 1024;
            // A client that has gone away makes Windows report ICMP port unreachable as a receive error on the
            // next ReceiveAsync; a relay must not care.
            const int SioUdpConnReset = -1744830452;
            socket.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            _sockets.Add(socket);
            _loops.Add(Task.Run(() => LoopAsync(socket, _cts.Token)));
        }
    }

    public Session? SessionOf(ulong sessionId) => _bySessionId.GetValueOrDefault(sessionId);

    /// <summary>Sends an inner IP packet to the client, as the relay's TUN would hand it back.</summary>
    public void SendToClient(Session session, ReadOnlySpan<byte> inner)
    {
        var wire = new byte[GpbProtocol.DataHeaderLen + inner.Length];
        GpbProtocol.WriteData(wire, session.Id, inner);
        if (session.Address is { } to && session.Socket is { } socket) socket.Send(wire, wire.Length, to);
    }

    private async Task LoopAsync(UdpClient socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            var at = Stopwatch.GetTimestamp();
            var pkt = received.Buffer;
            if (pkt.Length < 1) continue;
            var (version, type) = GpbProtocol.ParseHeader(pkt[0]);
            if (version != GpbProtocol.Version) continue;

            switch (type)
            {
                case GpbProtocol.TypeHandshakeReq:
                    Handshake(socket, pkt, received.RemoteEndPoint);
                    break;
                case GpbProtocol.TypeData:
                    Data(socket, pkt, received.RemoteEndPoint, at);
                    break;
                case GpbProtocol.TypePing:
                    // Counted first, so DropPing sees the ping's 1-based number - a line losing packets, as on 2026-09-29.
                    var nth = Interlocked.Increment(ref Pings);
                    if (DropPing?.Invoke(nth) == true) break;
                    PingLike(socket, pkt, received.RemoteEndPoint, GpbProtocol.TypePong, roam: true);
                    break;
                case GpbProtocol.TypeProbe:
                    PingLike(socket, pkt, received.RemoteEndPoint, GpbProtocol.TypeProbeReply, roam: false);
                    Interlocked.Increment(ref Probes);
                    break;
                case GpbProtocol.TypeDisconnect:
                    Disconnect(pkt, received.RemoteEndPoint);
                    break;
            }
        }
    }

    private void Handshake(UdpClient socket, byte[] req, IPEndPoint from)
    {
        if (req.Length != GpbProtocol.HandshakeReqPskLen || req[1] != GpbProtocol.AuthModePsk) return;
        var mac = HMACSHA256.HashData(_psk, req.AsSpan(0, 26));
        if (!CryptographicOperations.FixedTimeEquals(mac, req.AsSpan(26, 32))) return;

        var clientId = BinaryPrimitives.ReadUInt64BigEndian(req.AsSpan(18, 8));
        var session = _byClientId.GetOrAdd(clientId, id =>
        {
            var created = new Session
            {
                Id = BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8)) | 1,
                ClientId = id,
                InnerIp = 0x0A4D0000u | (uint)Interlocked.Increment(ref _nextInner) - 1,
            };
            _bySessionId[created.Id] = created;
            return created;
        });
        session.Address = from;
        session.Socket = socket;
        Interlocked.Increment(ref Handshakes);

        var resp = new byte[GpbProtocol.HandshakeRespPskLen];
        resp[0] = (byte)((GpbProtocol.Version << 4) | GpbProtocol.TypeHandshakeResp);
        resp[1] = GpbProtocol.StatusOk;
        BinaryPrimitives.WriteUInt64BigEndian(resp.AsSpan(2), session.Id);
        BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(10), session.InnerIp);
        BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(14), 0x0A4D0001);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(18), 1400);
        req.AsSpan(2, GpbProtocol.NonceLen).CopyTo(resp.AsSpan(20));
        HMACSHA256.HashData(_psk, resp.AsSpan(0, 28)).CopyTo(resp.AsSpan(28));
        socket.Send(resp, resp.Length, from);
    }

    private void Data(UdpClient socket, byte[] pkt, IPEndPoint from, long at)
    {
        if (!GpbProtocol.TryReadData(pkt, out var sid, out var inner)) return;
        if (!_bySessionId.TryGetValue(sid, out var session)) return;
        if (inner.Length < 20 || BinaryPrimitives.ReadUInt32BigEndian(inner[12..]) != session.InnerIp)
        {
            Interlocked.Increment(ref Spoofed);
            return;
        }

        session.Address = from;
        session.Socket = socket;
        var copy = inner.ToArray();
        Arrivals.Enqueue(new Arrival(copy, pkt, at, ((IPEndPoint)socket.Client.LocalEndPoint!).Port, sid));

        // The relay's kernel answering an echo request to anybody - at once, or as EchoRules says the internet would.
        if (copy[9] == 1 && copy.Length >= 28 && copy[(copy[0] & 0x0F) * 4] == 8)
        {
            var target = BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(16));
            if (!EchoRules.TryGetValue(target, out var rule))
            {
                SendToClient(session, EchoReply(copy));
                return;
            }
            var nth = Interlocked.Increment(ref rule.Requests);
            if (rule.Drop?.Invoke(nth) == true) return;
            var reply = EchoReply(copy);
            _echoes.Schedule(session, reply, at, rule.DelayMs, this);
            if (rule.Duplicate) _echoes.Schedule(session, reply, at, rule.DelayMs + 5, this);
        }
    }

    /// <summary>
    /// How the internet beyond this relay answers echoes to one address: after <see cref="DelayMs"/>, measured from
    /// the request's arrival here, on a spinning thread rather than a timer (Windows timers tick every 15.6 ms, which
    /// would be the error this is meant to find). <see cref="Drop"/> sees the request's 1-based count.
    /// </summary>
    public sealed class EchoRule
    {
        public double DelayMs;
        public Func<long, bool>? Drop;
        public bool Duplicate;
        public long Requests;
    }

    /// <summary>By target address (big-endian uint): echoes to anything not here are answered at once.</summary>
    public ConcurrentDictionary<uint, EchoRule> EchoRules { get; } = new();

    private readonly EchoScheduler _echoes = new();

    /// <summary>Sends replies at their due time to within a few microseconds - one thread, spinning while anything is due.</summary>
    private sealed class EchoScheduler : IDisposable
    {
        private readonly List<(long Due, Session Session, byte[] Reply, FakeRelay Relay)> _due = [];
        private readonly ManualResetEventSlim _wake = new();
        private readonly Thread _thread;
        private volatile bool _stop;

        public EchoScheduler()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "fake-relay-echoes", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public void Schedule(Session session, byte[] reply, long arrivedAt, double delayMs, FakeRelay relay)
        {
            var due = arrivedAt + (long)(delayMs * Stopwatch.Frequency / 1000.0);
            lock (_due) _due.Add((due, session, reply, relay));
            _wake.Set();
        }

        private void Run()
        {
            while (!_stop)
            {
                (long Due, Session Session, byte[] Reply, FakeRelay Relay)? next = null;
                lock (_due)
                {
                    if (_due.Count > 0)
                    {
                        var i = 0;
                        for (var j = 1; j < _due.Count; j++) if (_due[j].Due < _due[i].Due) i = j;
                        if (Stopwatch.GetTimestamp() >= _due[i].Due)
                        {
                            next = _due[i];
                            _due.RemoveAt(i);
                        }
                    }
                }
                if (next is { } send)
                {
                    send.Relay.SendToClient(send.Session, send.Reply);
                    continue;
                }
                bool empty;
                lock (_due) empty = _due.Count == 0;
                if (empty)
                {
                    _wake.Wait(50);
                    _wake.Reset();
                }
                else
                {
                    Thread.SpinWait(20);
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            _thread.Join(1000);
            _wake.Dispose();
        }
    }

    private void PingLike(UdpClient socket, byte[] pkt, IPEndPoint from, byte replyType, bool roam)
    {
        if (pkt.Length != GpbProtocol.PingLen) return;
        var sid = BinaryPrimitives.ReadUInt64BigEndian(pkt.AsSpan(1, 8));
        if (!_bySessionId.TryGetValue(sid, out var session)) return;
        if (roam)
        {
            session.Address = from;
            session.Socket = socket;
        }
        var reply = (byte[])pkt.Clone();
        reply[0] = (byte)((GpbProtocol.Version << 4) | replyType);
        var door = _sockets.IndexOf(socket);
        var delay = (door >= 0 ? Volatile.Read(ref DoorDelayMs[door]) : 0) + (LaneDelayMs?.Invoke(from.Port) ?? 0);
        if (delay <= 0)
        {
            socket.Send(reply, reply.Length, from);
            return;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delay));
            try { socket.Send(reply, reply.Length, from); } catch (ObjectDisposedException) { } catch (SocketException) { }
        });
    }

    private void Disconnect(byte[] pkt, IPEndPoint from)
    {
        if (pkt.Length != GpbProtocol.DisconnectLen) return;
        var sid = BinaryPrimitives.ReadUInt64BigEndian(pkt.AsSpan(1, 8));
        if (!_bySessionId.TryGetValue(sid, out var session)) return;
        if (!from.Equals(session.Address)) return;
        _bySessionId.TryRemove(sid, out _);
        _byClientId.TryRemove(session.ClientId, out _);
        Interlocked.Increment(ref Disconnects);
    }

    /// <summary>The reply an IPv4 host sends to an echo request: addresses swapped, type 0, checksums redone.</summary>
    private static byte[] EchoReply(byte[] request)
    {
        var reply = (byte[])request.Clone();
        var ihl = (reply[0] & 0x0F) * 4;
        request.AsSpan(12, 4).CopyTo(reply.AsSpan(16));
        request.AsSpan(16, 4).CopyTo(reply.AsSpan(12));
        reply[8] = 64;
        reply[10] = reply[11] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(10), Checksum(reply.AsSpan(0, ihl)));
        reply[ihl] = 0;
        reply[ihl + 2] = reply[ihl + 3] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(ihl + 2), Checksum(reply.AsSpan(ihl)));
        return reply;
    }

    private static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var i = 0;
        for (; i + 1 < data.Length; i += 2) sum += (uint)(data[i] << 8 | data[i + 1]);
        if (i < data.Length) sum += (uint)(data[i] << 8);
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    public void Dispose()
    {
        _echoes.Dispose();
        _cts.Cancel();
        foreach (var socket in _sockets) socket.Dispose();
        try { Task.WaitAll([.. _loops], TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
