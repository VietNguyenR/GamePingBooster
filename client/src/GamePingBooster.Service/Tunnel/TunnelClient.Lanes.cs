using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GamePingBooster.Core.Protocol;
using GamePingBooster.Core.Quality;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// What a lane hunt measured, and the sockets it measured with - still open, so the one chosen can be handed to
/// <see cref="TunnelClient.MoveToLane"/> as it is. Disposing closes every socket not taken.
/// </summary>
internal sealed class LaneHunt : IDisposable
{
    private readonly Socket?[] _sockets;

    public LaneHunt(LaneSample current, IReadOnlyList<LaneSample> candidates, Socket?[] sockets, int probesPerSecond, double seconds)
    {
        Current = current;
        Candidates = candidates;
        _sockets = sockets;
        ProbesPerSecond = probesPerSecond;
        Seconds = seconds;
    }

    /// <summary>The tunnel's own socket, slot 0.</summary>
    public LaneSample Current { get; }

    /// <summary>The sockets tried beside it, slots 1.. in order. A socket that could not be opened is a sample with nothing sent.</summary>
    public IReadOnlyList<LaneSample> Candidates { get; }

    public int ProbesPerSecond { get; }
    public double Seconds { get; }

    /// <summary>The socket of candidate <paramref name="slot"/> (1-based, as in <see cref="LaneSample.Slot"/>), now the caller's to keep or close.</summary>
    public Socket? Take(int slot)
    {
        if (slot < 1 || slot > _sockets.Length) return null;
        var socket = _sockets[slot - 1];
        _sockets[slot - 1] = null;
        return socket;
    }

    public void Dispose()
    {
        for (var i = 0; i < _sockets.Length; i++)
        {
            _sockets[i]?.Dispose();
            _sockets[i] = null;
        }
    }
}

internal sealed partial class TunnelClient
{
    /// <summary>
    /// Set only while something measures the tunnel's own socket with Probes - a lane hunt, or WayCheck timing the way in
    /// use: the downlink thread drops each ProbeReply that reaches the socket in here, with the moment it arrived. Null the
    /// rest of the time, which costs the downlink one field read per reply - and a reply arrives only while one is sending.
    /// One holder at a time (<see cref="ClaimProbeReplies"/>): two would take each other's answers.
    /// </summary>
    private ConcurrentQueue<(long Stamp, long At)>? _laneReplies;

    /// <summary>
    /// The queue the downlink files the tunnel's own Probe answers in, now the caller's until <see cref="ReleaseProbeReplies"/>;
    /// null while another measurement holds it.
    /// </summary>
    internal ConcurrentQueue<(long Stamp, long At)>? ClaimProbeReplies()
    {
        var replies = new ConcurrentQueue<(long Stamp, long At)>();
        return Interlocked.CompareExchange(ref _laneReplies, replies, null) is null ? replies : null;
    }

    internal void ReleaseProbeReplies(ConcurrentQueue<(long Stamp, long At)> replies) =>
        Interlocked.CompareExchange(ref _laneReplies, null, replies);

    /// <summary>
    /// One Probe down the tunnel's own socket, stamped <paramref name="stamp"/> (a Stopwatch timestamp); relayd answers it
    /// to that socket without moving anything. False when there is no session or the send failed. Its answer reaches the
    /// queue of <see cref="ClaimProbeReplies"/>, when one is held.
    /// </summary>
    internal bool SendOwnProbe(long stamp)
    {
        if (_socket is not { } socket || _sessionId == 0) return false;
        try
        {
            socket.Send(GpbProtocol.BuildProbe(_sessionId, (ulong)stamp), SocketFlags.None);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens <paramref name="candidates"/> sockets to the address the tunnel sends to - the same relay or entry, each
    /// from a port of its own - and times <paramref name="rounds"/> Probes on each and on the tunnel's own socket, at
    /// <paramref name="probesPerSecond"/> all together. See <see cref="LanePick"/> for why ports differ at all.
    ///
    /// A Probe is safe on any of them while the game plays: relayd answers it where it came from and moves nothing
    /// (docs/PROTOCOL-v3.md). The tunnel's own Probes are answered to its own socket, read by the downlink thread; the
    /// others are read here, on a thread of our own at the downlink's priority - a pool thread under a game's load
    /// stamps late, and it would be the candidates, never the lane in use, that looked slow for it.
    ///
    /// No async receive is ever left pending on a candidate: they are read with Select, so the one chosen can go to the
    /// tunnel's downlink thread the moment this returns. With <paramref name="candidates"/> 0 it measures the lane in use
    /// alone. Null when the tunnel has no session.
    /// </summary>
    internal Task<LaneHunt?> HuntLanesAsync(int candidates, int rounds, int probesPerSecond, CancellationToken ct)
    {
        if (_socket is null || _sessionId == 0) return Task.FromResult<LaneHunt?>(null);

        var done = new TaskCompletionSource<LaneHunt?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.TrySetResult(HuntLanes(candidates, rounds, probesPerSecond, ct));
            }
            catch (OperationCanceledException)
            {
                done.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        })
        {
            Name = "gpb-lanes",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        thread.Start();
        return done.Task;
    }

    /// <summary>How long after the last Probe answers are still waited for. A lane slower than this is no lane to take.</summary>
    private const int LaneWaitMs = 800;

    private LaneHunt? HuntLanes(int candidateCount, int rounds, int probesPerSecond, CancellationToken ct)
    {
        var tunnelSocket = _socket;
        var sessionId = _sessionId;
        var endpoint = _relayEndpoint;
        if (tunnelSocket is null || sessionId == 0) return null;

        var sockets = new Socket?[candidateCount];
        for (var i = 0; i < candidateCount; i++)
        {
            try
            {
                var socket = NewSocket();
                try
                {
                    socket.Connect(endpoint);
                    sockets[i] = socket;
                }
                catch
                {
                    socket.Dispose();
                }
            }
            catch (SocketException)
            {
                // Out of ports or buffers: fewer lanes tried, nothing worse.
            }
        }

        var slots = candidateCount + 1;
        var sent = new int[slots];
        var rtts = new List<double>[slots];
        var pending = new HashSet<long>[slots];
        for (var s = 0; s < slots; s++)
        {
            rtts[s] = [];
            pending[s] = [];
        }

        // Another measurement is reading the tunnel's own answers: no hunt this time rather than two halves.
        if (ClaimProbeReplies() is not { } replies)
        {
            foreach (var socket in sockets) socket?.Dispose();
            return null;
        }
        var started = Stopwatch.GetTimestamp();
        try
        {
            var spacing = Stopwatch.Frequency / Math.Max(1, probesPerSecond);
            var total = slots * rounds;
            var next = Stopwatch.GetTimestamp();
            var lastStamp = 0L;
            var index = 0;
            var buffer = new byte[GpbProtocol.MaxPacketLen];
            var readable = new List<Socket>(candidateCount);
            long? waitUntil = null;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var now = Stopwatch.GetTimestamp();

                // Round-robin, slot 0 first in every round: each lane is sampled across the whole hunt, so a moment of
                // queueing anywhere lands on all of them alike.
                if (index < total && now >= next)
                {
                    var slot = index % slots;
                    var stamp = Math.Max(now, lastStamp + 1);
                    lastStamp = stamp;
                    var socket = slot == 0 ? _socket : sockets[slot - 1];
                    // The tunnel moved under the hunt - it cannot, the supervisor runs both - or a candidate failed:
                    // that slot simply goes unmeasured.
                    if (socket is not null && (slot != 0 || ReferenceEquals(socket, tunnelSocket)))
                    {
                        try
                        {
                            pending[slot].Add(stamp);
                            socket.Send(GpbProtocol.BuildProbe(sessionId, (ulong)stamp), SocketFlags.None);
                            sent[slot]++;
                        }
                        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                        {
                            pending[slot].Remove(stamp);
                        }
                    }
                    index++;
                    next += spacing;
                    if (index == total) waitUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * LaneWaitMs / 1000;
                }

                while (replies.TryDequeue(out var reply)) Answer(0, reply.Stamp, reply.At);

                if (waitUntil is { } until && (now >= until || pending.All(p => p.Count == 0))) break;

                // Wait for an answer or the next send, whichever is first, never more than 5 ms: the tunnel's own
                // answers arrive in the queue above, which Select cannot wake for.
                var waitTicks = index < total ? Math.Max(0, next - Stopwatch.GetTimestamp()) : Stopwatch.Frequency / 200;
                var waitMicros = (int)Math.Clamp(waitTicks * 1_000_000 / Stopwatch.Frequency, 0, 5_000);

                readable.Clear();
                foreach (var socket in sockets) if (socket is not null) readable.Add(socket);
                if (readable.Count == 0)
                {
                    if (waitMicros > 0) Thread.Sleep(Math.Max(1, waitMicros / 1000));
                    continue;
                }
                try
                {
                    Socket.Select(readable, null, null, Math.Max(1, waitMicros));
                }
                catch (SocketException)
                {
                    continue;
                }

                foreach (var socket in readable)
                {
                    var slot = Array.IndexOf(sockets, socket) + 1;
                    try
                    {
                        while (socket.Available > 0)
                        {
                            var n = socket.Receive(buffer, SocketFlags.None);
                            var at = Stopwatch.GetTimestamp();
                            if (GpbProtocol.TryReadProbeReply(buffer.AsSpan(0, n), out var sid, out var stamp) && sid == sessionId)
                            {
                                Answer(slot, (long)stamp, at);
                            }
                        }
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        // ICMP port unreachable on this port: its Probes go unanswered, which is what they will count as.
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                    }
                }
            }
        }
        catch
        {
            foreach (var socket in sockets) socket?.Dispose();
            throw;
        }
        finally
        {
            ReleaseProbeReplies(replies);
        }

        var current = Sample(0);
        var tried = Enumerable.Range(1, candidateCount).Select(Sample).ToList();
        return new LaneHunt(current, tried, sockets, probesPerSecond, Stopwatch.GetElapsedTime(started).TotalSeconds);

        void Answer(int slot, long stamp, long at)
        {
            if (slot < 0 || slot >= slots || !pending[slot].Remove(stamp)) return;
            rtts[slot].Add((at - stamp) * 1000.0 / Stopwatch.Frequency);
        }

        LaneSample Sample(int slot)
        {
            var times = rtts[slot];
            times.Sort();
            double? median = times.Count == 0
                ? null
                : times.Count % 2 == 1 ? times[times.Count / 2] : (times[times.Count / 2 - 1] + times[times.Count / 2]) / 2;
            return new LaneSample(slot, median, times.Count == 0 ? null : times[^1], new PingLoss(sent[slot], times.Count));
        }
    }
}
