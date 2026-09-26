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
                    PingLike(socket, pkt, received.RemoteEndPoint, GpbProtocol.TypePong, roam: true);
                    Interlocked.Increment(ref Pings);
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

        // The relay's kernel answering an echo request to anybody.
        if (copy[9] == 1 && copy.Length >= 28 && copy[(copy[0] & 0x0F) * 4] == 8) SendToClient(session, EchoReply(copy));
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
        socket.Send(reply, reply.Length, from);
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
        _cts.Cancel();
        foreach (var socket in _sockets) socket.Dispose();
        try { Task.WaitAll([.. _loops], TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
