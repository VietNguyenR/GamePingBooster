using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Core.Protocol;
using GamePingBooster.Core.Quality;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Times every way into the relays the list shows - each relay's own address and every entry in front of it - with
/// measurement tickets (docs/PROTOCOL-v3.md), so the list shows the ping a connect to that relay will actually get.
///
/// Until 2026-10-09 the list was one ICMP echo to each relay's own address. At peak ICMP is the first thing an ISP
/// deprioritises and the entries were never measured at all: the owner's list said 60 ms for sg-1, connecting gave 49,
/// and 42-43 through an entry - and a customer judges by the list and leaves. Only a UDP round trip through relayd,
/// down the very way the tunnel would take, is that number; a ticket buys it without a session, so a thousand players
/// looking at the list take no slot on any relay (a handshake per relay would - see list-ping-handoff.md).
///
/// HOW. Per relay, a ticket asked for down every way in at once (the first answer wins; it is good for every way, ten
/// minutes at most), then one probe per way every <see cref="ProbeIntervalMs"/>, each way on a socket of its own made
/// as the tunnel makes its own. A way's number is the MEDIAN of the last <see cref="Window"/> - honest rather than
/// flattering: a best-of would show a number the player sees once in a while. A probe unanswered after
/// <see cref="LostAfter"/> counts as lost, and loss is scored as connect scores it (RelayLoss). Which way's number the
/// list shows is <see cref="DoorChoice"/>'s, the rule a connect to that relay uses too.
///
/// THE SOCKET IS THE NUMBER. A source port is a lane of the ISP's (LanePick): a fresh socket may land 5-17 ms away from
/// what this one measured. So a connect to a relay the list measured a moment ago handshakes on the winning way's own
/// socket (<see cref="Take"/>), and the player gets the very number they picked from.
///
/// Measuring runs only while somebody asks: every <see cref="Measure"/> keeps a relay measured for <see cref="IdleAfter"/>
/// more, and a relay nobody asked about since has its sockets closed. Tickets are kept across that, until they expire.
///
/// A relay that gives no ticket - older than the message, a PSK relay, a refused licence: all silence - is left to the
/// caller's ICMP echo, and asked again after <see cref="NoTicketBackoff"/>.
/// </summary>
internal sealed class RelayMeter : IDisposable
{
    /// <summary>What a ticket request is signed with. Null from the provider when this installation holds no usable licence.</summary>
    internal sealed record Credentials(byte[] Token, ECDsa DeviceKey, ulong ClientId);

    /// <summary>One probe per way this often. relayd answers 40 a second per device across every way in.</summary>
    internal const int ProbeIntervalMs = 250;

    /// <summary>Probes a second to one relay, all its ways together - under relayd's 40, with room to spare.</summary>
    private const int MaxPerRelayPerSecond = 30;

    /// <summary>
    /// The window a way's median and loss are taken over: 16-20 probes at one per way every 250 ms, as many as a
    /// connect's loss burst (RelayLoss.BurstPings). 10 s until 2026-10-09 - the list then took 5 s or more to follow
    /// a change of the line, and the owner read it as a list that did not update.
    /// </summary>
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    /// <summary>A probe still unanswered after this is lost. Nothing worth showing is this slow.</summary>
    internal static readonly TimeSpan LostAfter = TimeSpan.FromSeconds(1);

    /// <summary>How long a relay stays measured after the last <see cref="Measure"/> for it. The list asks every 2.5 s.</summary>
    internal static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(8);

    /// <summary>How long after the last answer a reading may still decide a connect: the lane must still be the one measured.</summary>
    internal static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan TicketAskEvery = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a list or a connect waits for a relay's first ticket. One round trip and a signature check.</summary>
    private static readonly TimeSpan TicketPatience = TimeSpan.FromSeconds(1);
    private const int TicketAsks = 3;
    private static readonly TimeSpan NoTicketBackoff = TimeSpan.FromSeconds(60);

    /// <summary>A ticket this close to its expiry is renewed - kept in use meanwhile.</summary>
    private static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan TakeWait = TimeSpan.FromMilliseconds(500);
    private const int TickMs = 50;

    private readonly Func<Credentials?> _credentials;
    private readonly Action<string> _log;
    private readonly Dictionary<string, Target> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Relays already named in the log as giving no ticket: once per process, not once a minute.</summary>
    private readonly HashSet<string> _toldNoTicket = new(StringComparer.OrdinalIgnoreCase);
    private Task? _loop;
    private bool _disposed;

    public RelayMeter(Func<Credentials?> credentials, Action<string> log)
    {
        _credentials = credentials;
        _log = log;
    }

    // ------------------------------------------------------- asking

    /// <summary>
    /// Keeps <paramref name="relays"/> measured for <see cref="IdleAfter"/> from now, starting any that are not. Only relays
    /// with a public key: a ticket is a licensed relay's. Returns the ids it is measuring (or trying to).
    /// </summary>
    public List<string> Measure(IEnumerable<RelayEntry> relays)
    {
        var measuring = new List<string>();
        if (_credentials() is null) return measuring;

        var now = Stopwatch.GetTimestamp();
        var until = now + Ticks(IdleAfter);
        lock (_gate)
        {
            if (_disposed) return measuring;
            foreach (var relay in relays)
            {
                if (relay.ViaRelayId is not null || string.IsNullOrWhiteSpace(relay.PublicKey)) continue;

                if (!_targets.TryGetValue(relay.Id, out var target) || !target.SameWays(relay))
                {
                    target?.Close();
                    target = new Target(relay);
                    _targets[relay.Id] = target;
                }
                if (now < target.NoTicketUntil) continue;

                target.ActiveUntil = Math.Max(target.ActiveUntil, until);

                // Open the sockets now, not on the loop's next tick: a way with no socket counts as settled, so a list
                // reopened after the relay went idle used to stop waiting at once and show either ICMP or a window
                // with nothing in it yet ("the relay's own address did not answer", 2026-10-09 11:29:43). And what was
                // measured before going idle is no longer this socket's lane - nothing is fresh until a new answer.
                if (target.Open(this)) target.LastAnswerAt = long.MinValue / 2;
                measuring.Add(relay.Id);
            }
            _loop ??= Task.Run(LoopAsync);
        }
        return measuring;
    }

    /// <summary>
    /// Waits until every relay in <paramref name="relayIds"/> has a reading worth showing - each way answered
    /// <paramref name="answers"/> probes, or plainly did not - or gave no ticket, or <paramref name="max"/> passes.
    /// </summary>
    public async Task WaitAsync(IReadOnlyCollection<string> relayIds, int answers, TimeSpan max, CancellationToken ct)
    {
        var deadline = Stopwatch.GetTimestamp() + Ticks(max);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            bool done;
            lock (_gate)
            {
                done = relayIds.All(id => !_targets.TryGetValue(id, out var t) || t.Settled(answers));
            }
            if (done) return;
            await Task.Delay(TickMs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The way <see cref="DoorChoice"/> picks into <paramref name="relayId"/> from what was measured, with every way's
    /// reading - or null when the relay gave no ticket, is not being measured, or nothing answered within
    /// <see cref="FreshFor"/>.
    /// </summary>
    public (DoorPick Pick, IReadOnlyList<DoorReading> Readings)? Pick(string relayId)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (!_targets.TryGetValue(relayId, out var target) || target.Ticket is null) return null;
            if (now - target.LastAnswerAt > Ticks(FreshFor)) return null;

            var readings = target.Ways.Select(w => w.Reading(now)).ToList();
            var pick = DoorChoice.Pick(readings);
            return pick.Door is null ? null : (pick, readings);
        }
    }

    /// <summary>Whether <paramref name="relayId"/> answered a ticket request since it was last measured. False for an older relay.</summary>
    public bool HasTicket(string relayId)
    {
        lock (_gate) return _targets.TryGetValue(relayId, out var t) && t.Ticket is not null;
    }

    /// <summary>
    /// Hands over the socket that has been measuring <paramref name="doorId"/> into <paramref name="relayId"/>, connected
    /// to <paramref name="endpoint"/>, for a tunnel to handshake on - its lane is the one measured. The relay is no longer
    /// measured on it; its other ways go on. Null when there is no such open way, or its receive did not stop in time:
    /// the caller then handshakes on a fresh socket, as before.
    /// </summary>
    public Socket? Take(string relayId, string doorId, IPEndPoint endpoint)
    {
        Way? way;
        lock (_gate)
        {
            if (!_targets.TryGetValue(relayId, out var target)) return null;
            way = target.Ways.FirstOrDefault(w => w.DoorId.Equals(doorId, StringComparison.OrdinalIgnoreCase) && w.Endpoint.Equals(endpoint));
        }
        return way?.Take(TakeWait);
    }

    // ------------------------------------------------------- the loop

    private async Task LoopAsync()
    {
        while (true)
        {
            try
            {
                if (!Tick()) return;
                await Task.Delay(TickMs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never the reason a list or a connect fails: the relays fall back to ICMP until the next Measure.
                _log($"Relay list measuring stopped: {ex.Message}");
                lock (_gate)
                {
                    foreach (var target in _targets.Values) target.Close();
                    _loop = null;
                }
                return;
            }
        }
    }

    /// <summary>One pass over every relay: ticket requests and probes that are due. False once nothing is measured.</summary>
    private bool Tick()
    {
        var credentials = _credentials();
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_disposed)
            {
                _loop = null;
                return false;
            }

            var any = false;
            foreach (var target in _targets.Values)
            {
                if (credentials is null || now >= target.ActiveUntil)
                {
                    target.Close();
                    continue;
                }
                any = true;
                target.Open(this);

                if (target.Ticket is not null && now >= target.TicketGoodUntil) target.Ticket = null;
                if (target.NeedsTicket(now) && now - target.AskedAt >= Ticks(TicketAskEvery))
                {
                    if (target.Ticket is null && target.Asks >= TicketAsks)
                    {
                        // Silence three times over: an older relayd, or the licence refused. ICMP for a minute.
                        if (_toldNoTicket.Add(target.RelayId))
                        {
                            _log($"{target.RelayId} gives no measurement ticket (an older relay, or the licence refused) - " +
                                 "the relay list shows an ICMP echo to it instead.");
                        }
                        target.NoTicketUntil = now + Ticks(NoTicketBackoff);
                        target.Asks = 0;
                        target.ActiveUntil = 0;
                        target.Close();
                        continue;
                    }
                    target.AskForTicket(credentials, now);
                }

                if (target.Ticket is { } ticket && now < target.TicketGoodUntil)
                {
                    var interval = Math.Max(ProbeIntervalMs, target.Ways.Count * 1000 / MaxPerRelayPerSecond);
                    foreach (var way in target.Ways) way.ProbeIfDue(ticket, now, Ticks(TimeSpan.FromMilliseconds(interval)));
                }
            }

            if (!any)
            {
                _loop = null;
                return false;
            }
            return true;
        }
    }

    private void OnTicket(Target target, byte[] ticket)
    {
        lock (_gate)
        {
            if (target.Ticket is not null && ticket.AsSpan().SequenceEqual(target.Ticket)) return;
            var expiry = DateTimeOffset.FromUnixTimeSeconds((long)BinaryPrimitives.ReadUInt64BigEndian(ticket.AsSpan(0, 8)));

            // The relay's clock against ours: a ticket good for ten minutes by its clock is good for that long from now.
            var left = expiry - DateTimeOffset.UtcNow;
            if (left > TimeSpan.FromMinutes(10)) left = TimeSpan.FromMinutes(10);
            if (left <= TimeSpan.Zero) return;

            if (target.Ticket is null && _toldNoTicket.Remove(target.RelayId))
            {
                _log($"{target.RelayId} gives measurement tickets again - the relay list measures every way into it.");
            }
            target.Ticket = ticket;
            target.TicketGoodUntil = Stopwatch.GetTimestamp() + Ticks(left);
            target.Asks = 0;
        }
    }

    private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var target in _targets.Values) target.Close();
        }
    }

    // ------------------------------------------------------- one relay

    private sealed class Target(RelayEntry relay)
    {
        public string RelayId { get; } = relay.Id;
        public List<Way> Ways { get; } = RelayPaths.DoorsOf([relay], relay.Id)
            .Select(d => IPEndPoint.TryParse(d.Endpoint, out var ep) ? new Way(d.Id, d.ViaRelayId is null, ep) : null)
            .OfType<Way>()
            .ToList();

        public long ActiveUntil;
        public byte[]? Ticket;
        public long TicketGoodUntil;
        public byte[]? PendingNonce;
        public long AskedAt = long.MinValue / 2;
        public long FirstAskedAt;
        public int Asks;
        public long NoTicketUntil;
        public long LastAnswerAt = long.MinValue / 2;

        /// <summary>The same ways at the same addresses: a profile change rebuilds the relay, the next ask keeps it.</summary>
        public bool SameWays(RelayEntry relay)
        {
            var doors = RelayPaths.DoorsOf([relay], relay.Id);
            return doors.Count == Ways.Count &&
                   doors.Zip(Ways).All(p => p.First.Id.Equals(p.Second.DoorId, StringComparison.OrdinalIgnoreCase) &&
                                            IPEndPoint.TryParse(p.First.Endpoint, out var ep) && ep.Equals(p.Second.Endpoint));
        }

        public bool NeedsTicket(long now) => Ticket is null || TicketGoodUntil - now < Ticks(RenewBefore);

        /// <summary>Opens every way not open yet. True when one was: its window starts empty.</summary>
        public bool Open(RelayMeter meter)
        {
            var opened = false;
            foreach (var way in Ways) opened |= way.Open(meter, this);
            return opened;
        }

        public void Close()
        {
            foreach (var way in Ways) way.Close();
        }

        /// <summary>One request, down every open way at once: whichever way works first brings the ticket.</summary>
        public void AskForTicket(Credentials credentials, long now)
        {
            var request = GpbProtocol.BuildMeasureReq(credentials.DeviceKey, credentials.Token, credentials.ClientId,
                DateTimeOffset.UtcNow, out var nonce);
            PendingNonce = nonce;
            AskedAt = now;
            if (Asks++ == 0) FirstAskedAt = now;
            foreach (var way in Ways) way.Send(request);
        }

        /// <summary>
        /// Every way has answered <paramref name="answers"/> probes, or has had that many go unanswered - or the relay
        /// gave no ticket within <see cref="TicketPatience"/> of being asked, which is an older relayd or a refusal: not
        /// worth holding a list or a connect up for while it is asked twice more.
        /// </summary>
        public bool Settled(int answers)
        {
            if (ActiveUntil == 0) return true;
            var now = Stopwatch.GetTimestamp();
            if (Ticket is null) return Asks > 0 && now - FirstAskedAt >= Ticks(TicketPatience);
            return Ways.All(w => w.Settled(now, answers));
        }
    }

    // ------------------------------------------------------- one way in

    private sealed class Way(string doorId, bool direct, IPEndPoint endpoint)
    {
        public string DoorId { get; } = doorId;
        public bool Direct { get; } = direct;
        public IPEndPoint Endpoint { get; } = endpoint;

        private readonly object _gate = new();
        private Socket? _socket;
        private CancellationTokenSource? _cts;
        private Task? _receiver;
        private long _lastSentAt = long.MinValue / 2;

        /// <summary>Probes sent in the window, by send stamp, with their round trip once answered.</summary>
        private readonly List<(long SentAt, double? Ms)> _sent = [];

        public bool Open(RelayMeter meter, Target target)
        {
            lock (_gate)
            {
                if (_socket is not null) return false;
                Socket? socket = null;
                try
                {
                    socket = TunnelClient.NewSocket();
                    socket.Connect(Endpoint);
                }
                catch (SocketException)
                {
                    socket?.Dispose();
                    return false;
                }
                _socket = socket;
                _sent.Clear();
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _receiver = Task.Run(() => ReceiveAsync(meter, target, socket, token));
                return true;
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _cts?.Cancel();
                _socket?.Dispose();
                _socket = null;
                _cts = null;
                _receiver = null;
                _sent.Clear();
            }
        }

        public Socket? Take(TimeSpan wait)
        {
            Socket socket;
            Task? receiver;
            lock (_gate)
            {
                if (_socket is not { } open) return null;
                socket = open;
                receiver = _receiver;
                _cts?.Cancel();
                _socket = null;
                _cts = null;
                _receiver = null;
                _sent.Clear();
            }
            try
            {
                if (receiver is null || receiver.Wait(wait)) return socket;
            }
            catch (AggregateException)
            {
            }
            socket.Dispose();
            return null;
        }

        public void Send(byte[] packet)
        {
            lock (_gate)
            {
                if (_socket is null) return;
                try
                {
                    _socket.Send(packet, SocketFlags.None);
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void ProbeIfDue(byte[] ticket, long now, long interval)
        {
            lock (_gate)
            {
                if (_socket is null || now - _lastSentAt < interval) return;
                _lastSentAt = now;
                _sent.RemoveAll(s => now - s.SentAt > Ticks(Window) + Ticks(LostAfter));
                _sent.Add((now, null));
                try
                {
                    _socket.Send(GpbProtocol.BuildMeasureProbe(ticket, (ulong)now), SocketFlags.None);
                }
                catch (SocketException)
                {
                    // Counted as sent and unanswered: a way the PC cannot send down is no way in.
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private void OnReply(long sentAt, long receivedAt)
        {
            lock (_gate)
            {
                var i = _sent.FindIndex(s => s.SentAt == sentAt);
                if (i < 0 || _sent[i].Ms is not null) return;
                _sent[i] = (sentAt, (receivedAt - sentAt) * 1000.0 / Stopwatch.Frequency);
            }
        }

        /// <summary>The median of the window's answers, and its loss over the probes old enough to have been answered.</summary>
        public DoorReading Reading(long now)
        {
            lock (_gate)
            {
                var window = _sent.Where(s => now - s.SentAt <= Ticks(Window)).ToList();
                var answered = window.Where(s => s.Ms is not null).Select(s => s.Ms!.Value).Order().ToList();
                var aged = window.Where(s => now - s.SentAt >= Ticks(LostAfter)).ToList();
                var loss = new PingLoss(aged.Count, aged.Count(s => s.Ms is not null));
                double? median = answered.Count == 0
                    ? null
                    : answered.Count % 2 == 1
                        ? answered[answered.Count / 2]
                        : (answered[answered.Count / 2 - 1] + answered[answered.Count / 2]) / 2;
                return new DoorReading(DoorId, Direct, median, loss);
            }
        }

        /// <summary><paramref name="answers"/> answers in the window, or as many probes gone unanswered; a closed way is settled.</summary>
        public bool Settled(long now, int answers)
        {
            lock (_gate)
            {
                if (_socket is null) return true;
                var answered = _sent.Count(s => s.Ms is not null);
                var lost = _sent.Count(s => s.Ms is null && now - s.SentAt >= Ticks(LostAfter));
                return answered >= answers || lost >= answers;
            }
        }

        private async Task ReceiveAsync(RelayMeter meter, Target target, Socket socket, CancellationToken ct)
        {
            var buffer = new byte[GpbProtocol.MaxPacketLen];
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var n = await socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
                    var receivedAt = Stopwatch.GetTimestamp();
                    var packet = buffer.AsSpan(0, n);

                    if (GpbProtocol.TryReadMeasureReply(packet, out var stamp))
                    {
                        // Only a stamp this process could have written: in the past, within the window.
                        var sentAt = (long)stamp;
                        if (sentAt <= 0 || sentAt > receivedAt || receivedAt - sentAt > Ticks(LostAfter)) continue;
                        OnReply(sentAt, receivedAt);
                        lock (meter._gate) target.LastAnswerAt = receivedAt;
                        continue;
                    }

                    byte[]? nonce;
                    lock (meter._gate) nonce = target.PendingNonce;
                    if (nonce is not null && GpbProtocol.TryParseMeasureTicket(packet, nonce, out var ticket))
                    {
                        meter.OnTicket(target, ticket);
                    }
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
                    // ICMP port unreachable from a way that is down. It goes unanswered; keep listening.
                }
                catch (SocketException)
                {
                    return;
                }
            }
        }
    }
}
