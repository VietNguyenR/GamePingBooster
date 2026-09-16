using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GamePingBooster.Core.Protocol;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Times the ways into the tunnel's relay that the tunnel is NOT using - the entries in front of it, or
/// the relay itself when the tunnel came in through an entry - with a Probe each, on a socket of their own.
///
/// A socket per way, not the tunnel's: a Probe must leave from a different address than the game's
/// traffic, or it would measure the way already in use. And a Probe rather than a Ping, because relayd
/// moves a session's return address after every Ping - one down a second way would drag the game's
/// traffic after it. See docs/PROTOCOL-v3.md.
///
/// Answers are read on the thread pool, not on a dedicated thread like the tunnel's downlink, so under
/// load they can be stamped a little late. That errs the right way: it makes the other way look slower
/// than it is, never faster, and a move is only ever made towards the faster one.
///
/// Nothing here is part of the tunnel. A failure of any kind leaves the way unmeasured, which the switch
/// policy reads as "not a candidate".
/// </summary>
internal sealed class DoorProbes : IDisposable
{
    /// <summary>A way into the relay: the id the profile gives it, and where to send.</summary>
    internal sealed record Door(string Id, IPEndPoint Endpoint);

    private readonly ulong _sessionId;
    private readonly Socket?[] _sockets;
    private readonly Action<DoorProbes, int, long, long> _onReply;
    private readonly CancellationTokenSource _cts = new();
    private long _answered;

    /// <summary>The ways measured, in slot order. Shared into every tick these probes fill.</summary>
    public string[] Ids { get; }

    /// <summary>How many answers have come back on any way, ever - zero for a relay too old to know the message.</summary>
    public long Answered => Interlocked.Read(ref _answered);

    /// <param name="onReply">Called with (this, slot, sent-at, received-at), both Stopwatch timestamps.</param>
    public DoorProbes(ulong sessionId, IReadOnlyList<Door> doors, Action<DoorProbes, int, long, long> onReply)
    {
        _sessionId = sessionId;
        _onReply = onReply;
        Ids = doors.Select(d => d.Id).ToArray();
        _sockets = new Socket?[doors.Count];

        for (var slot = 0; slot < doors.Count; slot++)
        {
            try
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(doors[slot].Endpoint);
                _sockets[slot] = socket;
                var s = slot;
                _ = Task.Run(() => ReceiveAsync(socket, s, _cts.Token));
            }
            catch (SocketException)
            {
                _sockets[slot] = null;
            }
        }
    }

    /// <summary>One Probe down one way. The stamp is the send time, echoed back unchanged by relayd.</summary>
    public void Send(int slot)
    {
        if ((uint)slot >= (uint)_sockets.Length || _sockets[slot] is not { } socket) return;
        try
        {
            socket.Send(GpbProtocol.BuildProbe(_sessionId, (ulong)Stopwatch.GetTimestamp()), SocketFlags.None);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ReceiveAsync(Socket socket, int slot, CancellationToken ct)
    {
        var buffer = new byte[GpbProtocol.MaxPacketLen];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var n = await socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
                var receivedAt = Stopwatch.GetTimestamp();
                if (!GpbProtocol.TryReadProbeReply(buffer.AsSpan(0, n), out var sid, out var stamp)) continue;
                if (sid != _sessionId) continue;

                // Only a stamp this process could have written: in the past, and within the last minute.
                var sentAt = (long)stamp;
                if (sentAt <= 0 || sentAt > receivedAt || receivedAt - sentAt > Stopwatch.Frequency * 60) continue;

                Interlocked.Increment(ref _answered);
                _onReply(this, slot, sentAt, receivedAt);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // ICMP port unreachable from a way that is down. It stays unmeasured; keep listening.
            }
            catch (SocketException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var socket in _sockets) socket?.Dispose();
        _cts.Dispose();
    }
}
