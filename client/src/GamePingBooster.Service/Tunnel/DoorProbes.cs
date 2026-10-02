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
///
/// Until a move, that is. A socket's source port decides which of the ISP's parallel links it rides (LanePick), so
/// the number a way was judged by belongs to its socket, not to the way: on 0.3.2-0.3.6 the tunnel moved onto a
/// fresh socket, and 35-42% of the moves landed more than 5 ms slower than the way had measured - a fifth to a third
/// slower than the way they left. A move now takes the measured socket itself (<see cref="Take"/>), sockets made as
/// the tunnel makes its own.
/// </summary>
internal sealed class DoorProbes : IDisposable
{
    /// <summary>A way into the relay: the id the profile gives it, and where to send.</summary>
    internal sealed record Door(string Id, IPEndPoint Endpoint);

    /// <summary>How long <see cref="Take"/> waits for a way's pending receive to end before giving the socket up.</summary>
    private static readonly TimeSpan TakeWait = TimeSpan.FromMilliseconds(500);

    private readonly ulong _sessionId;
    private readonly Door[] _doors;
    private readonly Socket?[] _sockets;
    private readonly Task?[] _receivers;
    private readonly CancellationTokenSource[] _slotCts;
    private readonly Action<DoorProbes, int, long, long> _onReply;
    private readonly object _gate = new();
    private bool _disposed;
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
        _doors = doors.ToArray();
        Ids = doors.Select(d => d.Id).ToArray();
        _sockets = new Socket?[doors.Count];
        _receivers = new Task?[doors.Count];
        _slotCts = new CancellationTokenSource[doors.Count];

        for (var slot = 0; slot < doors.Count; slot++)
        {
            _slotCts[slot] = new CancellationTokenSource();
            Socket? socket = null;
            try
            {
                socket = TunnelClient.NewSocket();
                socket.Connect(doors[slot].Endpoint);
                _sockets[slot] = socket;
                var s = slot;
                var token = _slotCts[slot].Token;
                _receivers[slot] = Task.Run(() => ReceiveAsync(socket, s, token));
            }
            catch (SocketException)
            {
                socket?.Dispose();
                _sockets[slot] = null;
            }
        }
    }

    /// <summary>
    /// Hands over the socket that has been measuring <paramref name="doorId"/> at <paramref name="endpoint"/> for the
    /// session <paramref name="sessionId"/>, for the tunnel to move onto: its lane is the one the switch policy judged.
    /// The way is no longer probed here. Null when these probes are not for that way and session, are disposed, or the
    /// socket's receive did not stop in time - the caller then moves on a fresh socket, as before.
    ///
    /// The receive pending on the socket is ended first: left running, it would race the tunnel's downlink for the
    /// game's packets. Nothing but Probe answers can be in flight to it until the tunnel sends from it.
    /// </summary>
    public Socket? Take(string doorId, IPEndPoint endpoint, ulong sessionId)
    {
        if (sessionId != _sessionId) return null;
        var slot = Array.FindIndex(_doors, d => d.Id.Equals(doorId, StringComparison.OrdinalIgnoreCase) && d.Endpoint.Equals(endpoint));
        if (slot < 0) return null;

        Socket socket;
        Task? receiver;
        lock (_gate)
        {
            if (_disposed || _sockets[slot] is not { } open) return null;
            socket = open;
            _sockets[slot] = null;
            receiver = _receivers[slot];
            _slotCts[slot].Cancel();
        }

        try
        {
            if (receiver is null || receiver.Wait(TakeWait)) return socket;
        }
        catch (AggregateException)
        {
        }
        socket.Dispose();
        return null;
    }

    /// <summary>One Probe down one way. The stamp is the send time, echoed back unchanged by relayd.</summary>
    public void Send(int slot)
    {
        if ((uint)slot >= (uint)_sockets.Length || Volatile.Read(ref _sockets[slot]) is not { } socket) return;
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
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            for (var slot = 0; slot < _sockets.Length; slot++)
            {
                _slotCts[slot].Cancel();
                _sockets[slot]?.Dispose();
                _sockets[slot] = null;
            }
        }
    }
}
