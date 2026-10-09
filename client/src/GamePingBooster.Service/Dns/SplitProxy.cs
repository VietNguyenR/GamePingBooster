using GamePingBooster.Core.Profiles;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GamePingBooster.Core.Net;
using GamePingBooster.Service.Native;
using GamePingBooster.Service.Network;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// The profile's tunnel names over the player's own line, with the ClientHello split - instead of through the relay -
/// where the line is seen cutting them by name and a split hello is seen passing.
///
/// Why: the relay carries those names at 256 KB/s per session, shared with the match. PUBG's lobby is 8.5 MB from
/// prod-live-front when its cache is cold (first start of the day, after a patch): live on FPT Ha Noi, 2026-10-06,
/// launch to the Start button took 1:46 through the relay and 1:16 over the line split, the 30 s being 8.5 MB at the
/// relay's cap. FPT resets every handshake naming these names on every foreign edge; a hello split in two records
/// passes - see <see cref="SplitHello"/>.
///
/// How: the resolver answers such a name with <see cref="ListenAddress"/>, where this listens on 443. Each connection's
/// first record is read, its name looked up, and the connection opened to one of the name's own edges FROM THE
/// PHYSICAL ADAPTER'S ADDRESS - so it leaves over the line even where the profile routes that edge into the tunnel
/// (PUBG's LobbyAddress /32s do) - with the hello split. Then bytes are passed both ways, unread. TLS stays end to
/// end: the game checks the edge's own certificate, and nothing here can read what it sends.
///
/// The order is the one agreed with the owner: the line, then the line split, then the relay. A listed name is never
/// answered with the edge's own address - that was the 2026-10-03 black screen (front passed a test handshake on FPT
/// and the real lobby hung direct, with nothing to fall back on) - it is the proxy or the relay.
///
/// Over the line WHOLE (the hello unchanged) where no edge resets the name and the line takes a connection. That is
/// VNPT, live 2026-10-06: it blocks by DNS only, every edge completed whole, and every listed name rode the relay - the
/// lobby crawled at the relay's cap. No round-trip comparison with the tunnel: it kept PUBG's mainland .cn mirror, the
/// cold lobby's 8.2 MB, on the relay (2026-10-07) - see WholeOrRelayAsync.
///
/// Either way the relay stays the fallback per connection: a hello that is reset or not answered (the ISP may start
/// filtering, or reassembling) is sent again whole through the tunnel before the game sees anything, and the name
/// goes back to the relay for <see cref="FailedFor"/>.
///
/// Port 443 only, which is why only the profile's tunnel list is served: those are HTTPS fronts and APIs. A name a
/// game also uses on another port (Steam's connection managers) must never be answered with this address.
/// </summary>
internal sealed class SplitProxy : IAsyncDisposable
{
    /// <summary>Not 127.0.0.1, the address every ISP sinkhole uses - an answer of this one is ours and means "works".</summary>
    public static readonly IPAddress ListenAddress = IPAddress.Parse("127.77.0.1");

    /// <summary>Short: an answer of this address is only good while this runs, and it stops with unblocking.</summary>
    public const uint AnswerTtl = 30;

    /// <summary>True for the address this answers with - which the sinkhole checks must not take for the ISP's lie.</summary>
    public static bool IsOurs(IPAddress address) => address.Equals(ListenAddress);

    /// <summary>A verdict is probed again after this, in the background, while it is still used.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

    /// <summary>A verdict older than this is not used: the name is answered as if none existed, and probed.</summary>
    private static readonly TimeSpan UsableFor = TimeSpan.FromMinutes(30);

    /// <summary>After a split connection was cut live, the name stays on the relay this long.</summary>
    private static readonly TimeSpan FailedFor = TimeSpan.FromMinutes(3);

    /// <summary>Edges probed per name: whole, control and split on each, all at once.</summary>
    private const int ProbedEdges = 3;

    /// <summary>
    /// Edges that may stay silent after the hello before the name goes to the relay. One slow edge is not the line: PUBG's
    /// mainland .cn mirror answers in 0.5-1 s and now and then takes 6 s, and on 2026-10-07 one such edge sent the whole
    /// lobby through the relay's cap. Two silent edges are the line.
    /// </summary>
    private const int MaxSilentEdges = 2;

    private static readonly TimeSpan ProbeConnect = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Long enough for us-east: acrt-pcprod's split handshake took 1030 ms on FPT, xenuine's 761. A reset by the line
    /// lands within 30 ms, so this only bounds a stall.
    /// </summary>
    private static readonly TimeSpan ProbeHandshake = TimeSpan.FromMilliseconds(3000);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long the split hello waits for the edge's first byte before the connection goes through the tunnel instead.
    /// An honest edge answers in one round trip (us-east ~500 ms); a filter resets in 30 ms or hangs for ever.
    /// </summary>
    private static readonly TimeSpan FirstReplyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// What was decided for one name. Split: the proxy answers it and takes it over the line; Whole: with the hello
    /// unchanged rather than split. Edges are kept after a failure: the tunnel fallback needs them. Held: until when a
    /// connection cut live keeps the name on the relay, whatever a probe finds in the meantime.
    /// </summary>
    internal sealed record Verdict(bool Split, IPAddress[] Edges, DateTimeOffset At, string Why, DateTimeOffset Held = default,
        bool Whole = false);

    private readonly DohUpstream _doh;
    private readonly IUnblockRoutes? _routes;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, Verdict> _verdicts;
    private readonly IPEndPoint _listen;
    private readonly int _edgePort;
    private readonly Func<IPAddress, IPAddress?> _source;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, Task> _probing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names whose first connection through the split was already said in the log.</summary>
    private readonly ConcurrentDictionary<string, byte> _said = new(StringComparer.OrdinalIgnoreCase);

    private Socket? _listener;
    private Task? _acceptLoop;
    private long _split;
    private long _fellBack;
    private long _stopped;

    /// <param name="verdicts">Kept by the caller per network (EdgeMemory.Split), so a reconnect does not probe again.</param>
    /// <param name="listen">TunnelCheck only: anything but <see cref="ListenAddress"/>:443.</param>
    /// <param name="edgePort">TunnelCheck only: the edges' port, 443 everywhere else.</param>
    /// <param name="source">TunnelCheck only: the address to send from. <see cref="LineSource"/> everywhere else.</param>
    public SplitProxy(DohUpstream doh, IUnblockRoutes? routes, Action<string> log,
        ConcurrentDictionary<string, Verdict>? verdicts = null, IPEndPoint? listen = null, int edgePort = 443,
        Func<IPAddress, IPAddress?>? source = null)
    {
        _doh = doh;
        _routes = routes;
        _log = log;
        _verdicts = verdicts ?? new ConcurrentDictionary<string, Verdict>(StringComparer.OrdinalIgnoreCase);
        _listen = listen ?? new IPEndPoint(ListenAddress, 443);
        _edgePort = edgePort;
        _source = source ?? LineSource;
    }

    public bool Running => _listener is not null;

    /// <summary>
    /// The profile's lobby proxies, in order (relay/cmd/lobbyproxy). A name <see cref="IsProxied"/> says yes to goes
    /// to them first - the game's hello unchanged - and only when none answers does it take the line or the relay as
    /// before. Empty means no proxy: the behaviour of every client before the field existed.
    /// </summary>
    internal IReadOnlyList<LobbyProxyEntry> LobbyProxies { get; init; } = [];

    /// <summary>Whether the name is on the profile's proxied list. See <see cref="LobbyProxies"/>.</summary>
    internal Func<string, bool> IsProxied { get; init; } = _ => false;

    /// <summary>
    /// Names whose connections never go through the tunnel, even when the split fails: Steam's download hosts. A relay
    /// caps a session at 256 KB/s shared with the game, so a failed split is a reset - Steam then asks another content
    /// server - not a detour. Decided 2026-10-09 after the owner ruled the fallback out for downloads.
    /// </summary>
    internal Func<string, bool> NeverRelay { get; init; } = _ => false;

    /// <summary>How long the proxies go unused after none of them answered a connection.</summary>
    private static readonly TimeSpan ProxyDownFor = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How long a proxy gets to answer the hello. Longer than FirstReplyTimeout: the proxy itself gives each mainland
    /// edge up to four seconds, and one edge in a few hangs.
    /// </summary>
    private static readonly TimeSpan ProxyFirstReply = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ProxyConnect = TimeSpan.FromSeconds(3);

    private long _proxyDownUntilTicks;
    private long _viaProxy;
    private long _proxyBytes;
    private readonly ConcurrentDictionary<Socket, byte> _proxied = new();

    /// <summary>How often the lobby proxies' throughput is looked at.</summary>
    internal TimeSpan ProxyTick { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A proxy that answers but moves a download at 10-150 KB/s is worse than none: on 2026-10-07 the Hong Kong box
    /// carried the lobby at 41 KB/s for two minutes because the player's evening route to it was throttled, while
    /// the line alone gave 100-200 KB/s. Slower than this for <see cref="ProxySlowTicks"/> ticks and the proxies are left
    /// alone for <see cref="ProxyDownFor"/>.
    /// </summary>
    internal ProxySpeedWatch SpeedWatch { get; init; } = new();

    /// <summary>
    /// Told the bytes the proxied connections received in one tick; says when the proxies are too slow. A tick with
    /// under <see cref="MinTransfer"/> of data is chatter (keep-alives, a log post), not a download, and neither
    /// counts as slow nor clears the count.
    /// </summary>
    internal sealed class ProxySpeedWatch(long minBytesPerSecond = 150_000, int slowTicks = 3, long minTransfer = 20_000)
    {
        private int _slow;

        /// <returns>Why the proxies are too slow, or null.</returns>
        public string? Observe(long bytes, double seconds, int open)
        {
            if (open == 0) { _slow = 0; return null; }
            if (bytes < minTransfer * seconds / 2.0) return null;
            var rate = bytes / seconds;
            if (rate >= minBytesPerSecond) { _slow = 0; return null; }
            if (++_slow < slowTicks) return null;
            _slow = 0;
            return $"{rate / 1000:0} KB/s for {slowTicks} ticks, under {minBytesPerSecond / 1000} KB/s";
        }
    }

    private async Task ProxySpeedLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(ProxyTick);
            long last = 0;
            var clock = Stopwatch.StartNew();
            var lastMs = 0L;
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = Interlocked.Read(ref _proxyBytes);
                var ms = clock.ElapsedMilliseconds;
                var why = SpeedWatch.Observe(now - last, Math.Max(0.001, (ms - lastMs) / 1000.0), _proxied.Count);
                last = now;
                lastMs = ms;
                if (why is null) continue;

                Interlocked.Exchange(ref _proxyDownUntilTicks, DateTime.UtcNow.Add(ProxyDownFor).Ticks);
                var reset = 0;
                foreach (var client in _proxied.Keys)
                {
                    // Reset, not closed: the game must see a failure at once and open the connection again - by the
                    // line or the relay, the proxies being left alone now.
                    try { client.LingerState = new LingerOption(true, 0); client.Dispose(); reset++; } catch (Exception) { }
                }
                _said.Clear();
                _log($"Unblock: the lobby proxies moved a download too slowly ({why}); {reset} connection(s) reset so the game " +
                     $"opens them again over the line or the relay, and the proxies are left alone for {ProxyDownFor.TotalMinutes:0} min.");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Connections carried by a lobby proxy.</summary>
    public long ProxiedConnections => Interlocked.Read(ref _viaProxy);

    private bool ProxyUsable(string name) =>
        LobbyProxies.Count > 0 && IsProxied(name) && DateTime.UtcNow.Ticks >= Interlocked.Read(ref _proxyDownUntilTicks);

    /// <summary>The address and port this listens on - for TunnelCheck, which listens on a free port.</summary>
    public IPEndPoint Endpoint => (IPEndPoint?)_listener?.LocalEndPoint ?? _listen;

    /// <summary>
    /// Which outcome of a probe (split, or whole for the line) counts as passing: a valid certificate, "ok". TunnelCheck's servers on loopback
    /// cannot present one for a real name, and accept "cert-mismatch" too.
    /// </summary>
    internal Func<string, bool> SplitPasses { get; init; } = outcome => outcome == "ok";

    public long SplitConnections => Interlocked.Read(ref _split);
    public long FellBack => Interlocked.Read(ref _fellBack);

    /// <summary>Connections over the line ended by the watch because their path stopped.</summary>
    public long Stopped => Interlocked.Read(ref _stopped);

    /// <summary>
    /// Listens, or says why not and stays off - then every listed name rides the relay, as before this existed. Never
    /// throws: the split is an improvement, and failing to start it is no reason to refuse unblocking.
    /// </summary>
    public bool Start()
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.Bind(_listen);
            listener.Listen(128);
        }
        catch (SocketException ex)
        {
            listener.Dispose();
            _log($"Unblock: could not listen on {_listen} for the split ({ex.SocketErrorCode}) - the profile's tunnel " +
                 "names go through the relay only.");
            return false;
        }

        _listener = listener;
        _acceptLoop = Task.Run(() => AcceptAsync(listener, _stopping.Token));
        if (LobbyProxies.Count > 0) _ = Task.Run(() => ProxySpeedLoopAsync(_stopping.Token));
        return true;
    }

    /// <summary>
    /// True when <paramref name="name"/> is to be answered with <see cref="ListenAddress"/> now. Starts a probe when
    /// there is no verdict, or a fresh one in the background when it is getting old; never waits for one - until a
    /// verdict exists the name is answered as before (the relay), so this can only ever make a lookup faster.
    /// </summary>
    public bool Answers(string name)
    {
        if (!Running) return false;
        var now = DateTimeOffset.UtcNow;
        if (ProxyUsable(name))
        {
            // A proxied name is answered at once: the connection tries the proxy first, and the line's verdict is only
            // the fallback, judged meanwhile.
            if (!_verdicts.TryGetValue(name, out var known) || now - known.At > Fresh) ProbeInBackground(name);
            return true;
        }
        if (!_verdicts.TryGetValue(name, out var verdict) || now - verdict.At > UsableFor)
        {
            ProbeInBackground(name);
            return false;
        }
        if (now - verdict.At > Fresh) ProbeInBackground(name);
        return verdict.Split && verdict.Held <= now;
    }

    /// <summary>
    /// <see cref="Answers"/>, but when there is no usable verdict it waits for the probe (at most <paramref name="wait"/>)
    /// instead of answering "no". For a name about to be sent through the tunnel: the answer to "can it go over the line
    /// instead" has to come BEFORE a /32 is routed, because a route cannot be taken back without cutting a live flow.
    /// </summary>
    public async Task<bool> AnswersAfterJudgingAsync(string name, TimeSpan wait, CancellationToken ct)
    {
        if (Answers(name)) return true;
        if (_verdicts.TryGetValue(name, out var verdict) && DateTimeOffset.UtcNow - verdict.At <= UsableFor) return false;
        if (_probing.TryGetValue(name, out var probe))
        {
            try { await Task.WhenAny(probe, Task.Delay(wait, ct)).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        return Answers(name);
    }

    private void ProbeInBackground(string name)
    {
        if (_stopping.IsCancellationRequested) return;
        var ct = _stopping.Token;
        var started = false;
        var task = _probing.GetOrAdd(name, key =>
        {
            started = true;
            return Task.Run(async () =>
            {
                try { await ProbeAsync(key, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log($"Unblock: probing {key} for the split failed ({ex.Message})."); }
            }, CancellationToken.None);
        });
        if (started) _ = task.ContinueWith(_ => _probing.TryRemove(new KeyValuePair<string, Task>(name, task)), TaskScheduler.Default);
    }

    /// <summary>The name's own edges, from every resolver in turn, then <see cref="JudgeAsync"/>.</summary>
    private async Task ProbeAsync(string name, CancellationToken ct)
    {
        var perUpstream = new List<IReadOnlyList<IPAddress>>();
        foreach (var (_, reply) in await _doh.ResolveEachAsync(DnsWire.BuildQuery(0, name), ct).ConfigureAwait(false))
        {
            try
            {
                perUpstream.Add([.. DnsWire.Parse(reply, reply.Length).Addresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)]);
            }
            catch (FormatException) { }
        }
        await JudgeAsync(name, EdgeRanking.Interleave(perUpstream, ProbedEdges), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// On each edge, from the line: the real name whole, the control name, and the real name split. The name goes over
    /// the line split only when the line cuts it whole (a reset or stall while the control is answered - the same test
    /// as WorkingEdges.IsCutAsync) and the split completes with a valid certificate. Internal for TunnelCheck.
    /// </summary>
    internal async Task<Verdict> JudgeAsync(string name, IReadOnlyList<IPAddress> candidates, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        Verdict verdict;
        if (candidates.Count == 0)
        {
            verdict = new Verdict(false, [], DateTimeOffset.UtcNow, "no resolver returned an address");
        }
        else if (_source(candidates[0]) is not { } source)
        {
            verdict = new Verdict(false, [], DateTimeOffset.UtcNow, "no way out over the line was found to send from");
        }
        else
        {
            var results = await Task.WhenAll(candidates.Take(ProbedEdges).Select(async address =>
            {
                var whole = EdgeProber.HandshakeAsync(address, name, ProbeConnect, ProbeHandshake, ct, _edgePort, source);
                var control = EdgeProber.HandshakeAsync(address, EdgeProber.ControlName, ProbeConnect, ProbeHandshake, ct, _edgePort, source);
                var split = EdgeProber.HandshakeAsync(address, name, ProbeConnect, ProbeHandshake, ct, _edgePort, source, split: true);
                return (Address: address, Whole: await whole.ConfigureAwait(false), Control: await control.ConfigureAwait(false),
                    Split: await split.ConfigureAwait(false));
            })).ConfigureAwait(false);

            // Cut on EVERY edge the control reached, not on one. FPT cuts these names on every foreign edge; an edge
            // that stalls while another completes whole is a slow server, not a filter - PUBG's mainland .cn mirror
            // looked "cut" that way on 2026-10-06 (122.193.250.66 stalled, 218.61.165.129 completed) and was sent over
            // the line, the one path it is known to hang on from Vietnam.
            var reached = results.Where(r => r.Control.Outcome is "ok" or "cert-mismatch" or "tls-error").ToArray();
            var cut = reached.Length > 0 && reached.All(r => EdgeProber.CutByName(r.Whole.Outcome, r.Control.Outcome));
            IPAddress[] passing = [.. results.Where(r => SplitPasses(r.Split.Outcome)).OrderBy(r => r.Split.Ms).Select(r => r.Address)];
            var seen = string.Join(", ", results.Select(r =>
                $"{r.Address} whole {r.Whole.Outcome}, control {r.Control.Outcome}, split {r.Split.Outcome} {r.Split.Ms} ms"));

            // Whole over the line only where NO edge resets it: one reset means the line filters the name, and a
            // filter that lets some edges through today is not one to send the lobby past. A stall is a slow edge.
            var resetSomewhere = reached.Any(r => r.Whole.Outcome == "reset");
            IPAddress[] wholePassing = [.. results.Where(r => SplitPasses(r.Whole.Outcome)).OrderBy(r => r.Whole.Ms).Select(r => r.Address)];

            verdict = cut
                ? passing.Length == 0
                    ? new Verdict(false, [], DateTimeOffset.UtcNow, $"the line cuts it split as well ({seen})")
                    : new Verdict(true, passing, DateTimeOffset.UtcNow, $"the line cuts it whole and lets it through split ({seen})")
                : resetSomewhere || wholePassing.Length == 0
                    ? new Verdict(false, passing, DateTimeOffset.UtcNow, $"the line does not cut it on every edge ({seen})")
                    : await WholeOrRelayAsync(wholePassing, source, seen, ct).ConfigureAwait(false);
        }

        // A cut seen live is believed over a probe: the probe's hello is SChannel's, the game's is its own.
        var before = _verdicts.TryGetValue(name, out var old) ? old : null;
        if (before is not null && before.Held > DateTimeOffset.UtcNow)
        {
            verdict = verdict with { Split = false, Whole = false, Held = before.Held, Why = before.Why };
        }

        // Said when the decision changes, not every ten minutes.
        _verdicts[name] = verdict;
        if (before is null || before.Split != verdict.Split || before.Whole != verdict.Whole)
        {
            _log(!verdict.Split
                ? $"Unblock: {name} stays on the relay - {verdict.Why}."
                : $"Unblock: {name} goes over the line with the hello {(verdict.Whole ? "whole" : "split")}, not through " +
                  $"the relay - {verdict.Why}, {clock.ElapsedMilliseconds} ms.");
        }
        return verdict;
    }

    /// <summary>
    /// The line does not filter the name: it goes over the line whole, whatever the tunnel's round trip. Until
    /// 2026-10-07 it stayed on the relay when the tunnel connected faster (by max(10 ms, 10%)), and that sent the cold
    /// lobby through the relay's cap: on VNPT the lobby's 8.2 MB comes from the .cn mirror (49 connections), which
    /// connected in 207 ms over the line against 162 ms through the tunnel - and then downloaded at 0.9-1.4 MB/s per
    /// connection over the line against 0.25-0.35 MB/s for all of them through the relay. The rule also flipped with
    /// tens of ms (the night before .cn went whole, 267 against 384). A round trip says nothing about a bulk name;
    /// whatever the line does wrong after the first byte is the watch's (<see cref="StallWatch"/>). Throttling - slow
    /// but moving - is not caught.
    /// Relay only when no edge takes a connection from the line now.
    /// </summary>
    private async Task<Verdict> WholeOrRelayAsync(IPAddress[] edges, IPAddress source, string seen, CancellationToken ct)
    {
        var line = await Task.WhenAll(edges.Select(a => ConnectMsAsync(a, source, ct))).ConfigureAwait(false);
        if (line.Where(ms => ms is not null).Min() is not { } lineBest)
        {
            return new Verdict(false, edges, DateTimeOffset.UtcNow, $"the line does not cut it but took no connection now ({seen})");
        }

        // Fastest first over the line: the proxy tries them in this order.
        IPAddress[] ordered = [.. edges.Zip(line).OrderBy(p => p.Second ?? long.MaxValue).Select(p => p.First)];
        return new Verdict(true, ordered, DateTimeOffset.UtcNow, $"the line does not cut it on any edge (connect over the line {lineBest} ms; {seen})", Whole: true);
    }

    /// <summary>A TCP connect's time in ms from <paramref name="source"/>; null when none.</summary>
    private async Task<long?> ConnectMsAsync(IPAddress address, IPAddress source, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var clock = Stopwatch.StartNew();
        try
        {
            socket.Bind(new IPEndPoint(source, 0));
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(ProbeConnect);
            await socket.ConnectAsync(new IPEndPoint(address, _edgePort), connect.Token).ConfigureAwait(false);
            return clock.ElapsedMilliseconds;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// The physical adapter's address towards <paramref name="destination"/>: the interface RouteManager pins relays
    /// through, read as the address Windows picks for its gateway. Bound to it, a connection leaves by that adapter
    /// whatever /32 the tunnel holds for the destination - Windows sends by the strong host model, and the live test
    /// on 2026-10-06 saw it: bound, a whole hello to a lobby edge routed into the tunnel was reset by FPT.
    /// </summary>
    public static IPAddress? LineSource(IPAddress destination)
    {
        try
        {
            if (RouteManager.GetRouteTo(destination) is not { } route) return null;
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(route.Gateway, 9));   // a UDP connect sends nothing; it only picks the address
            return ((IPEndPoint)socket.LocalEndPoint!).Address;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task AcceptAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try { client = await listener.AcceptAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            _ = Task.Run(() => HandleAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(Socket client, CancellationToken ct)
    {
        Socket? edge = null;
        try
        {
            client.NoDelay = true;
            var record = await ReadHelloAsync(client, ct).ConfigureAwait(false);
            if (record is null || SplitHello.NameOf(record) is not { } name) return;
            byte[]? reply = null;
            var viaProxy = false;
            if (ProxyUsable(name))
            {
                (edge, reply) = await ViaProxyAsync(name, record, ct).ConfigureAwait(false);
                viaProxy = edge is not null;
            }

            Verdict? verdict = null;
            if (!viaProxy)
            {
                if ((!_verdicts.TryGetValue(name, out verdict) || verdict.Edges.Length == 0) && IsProxied(name))
                {
                    // Answered for the proxy before any verdict existed, and the proxy did not answer: judge it now.
                    await ProbeAsync(name, ct).ConfigureAwait(false);
                    _verdicts.TryGetValue(name, out verdict);
                }
                if (verdict is null || verdict.Edges.Length == 0)
                {
                    if (_said.TryAdd("?" + name, 0)) _log($"Unblock: a connection for {name} reached the split with no edge known for it - closed.");
                    return;
                }
            }

            if (!viaProxy && verdict!.Split)
            {
                (edge, reply) = await OverTheLineAsync(name, verdict.Edges, record, verdict.Whole, ct).ConfigureAwait(false);
            }
            if (edge is null)
            {
                if (NeverRelay(name))
                {
                    // Reset, so the client sees a failure at once and tries another content server.
                    if (_said.TryAdd("r" + name, 0))
                        _log($"Unblock: {name} is a download host and the split did not carry it - reset, not sent through the relay.");
                    try { client.LingerState = new LingerOption(true, 0); } catch (Exception) { }
                    return;
                }

                // Through the tunnel with the hello as the game sent it - nothing has reached the game yet, so it
                // never knows. Also where a connection lands that was answered just before the name went back.
                edge = await ThroughTunnelAsync(name, verdict!.Edges, record, ct).ConfigureAwait(false);
                if (edge is null) return;
            }

            // Only a connection over the line is watched: one through the tunnel has nowhere better to go.
            var overLine = reply is not null && !viaProxy;
            if (reply is not null) await client.SendAsync(reply, SocketFlags.None, ct).ConfigureAwait(false);
            if (viaProxy) _proxied[client] = 0;
            if (await PumpAsync(client, edge, overLine, ct, viaProxy ? n => Interlocked.Add(ref _proxyBytes, n) : null).ConfigureAwait(false) is { } dead)
            {
                // Reset, not closed: the game must see a failure at once and open the connection again - and the
                // name is on the relay by then, so the new one goes through the tunnel.
                HoldOnRelay(name, $"a connection over the line stopped mid-way: {dead}");
                Interlocked.Increment(ref _stopped);
                try { client.LingerState = new LingerOption(true, 0); } catch (Exception) { }
                _log($"Unblock: {name} - a connection over the line stopped mid-way ({dead}); reset it so the game opens " +
                     $"it again, through the relay, and the name goes through the relay for {FailedFor.TotalMinutes:0} min.");
            }
        }
        catch (Exception)
        {
            // A connection that ends badly is the game's business, and it already knows: its socket closes.
        }
        finally
        {
            _proxied.TryRemove(client, out _);
            edge?.Dispose();
            client.Dispose();
        }
    }

    /// <summary>
    /// The hello, unchanged, to the first lobby proxy that answers it; its reply with the connection. Null - and the
    /// proxies left alone for <see cref="ProxyDownFor"/> - when none does.
    /// </summary>
    private async Task<(Socket? Edge, byte[]? Reply)> ViaProxyAsync(string name, byte[] record, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var why = "no proxy to try";
        foreach (var proxy in LobbyProxies)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var owned = new SocketOwner(socket);
            try
            {
                var address = IPAddress.TryParse(proxy.Host, out var literal)
                    ? literal
                    : (await System.Net.Dns.GetHostAddressesAsync(proxy.Host, AddressFamily.InterNetwork, ct).ConfigureAwait(false)).FirstOrDefault();
                if (address is null) { why = $"{proxy.Host} has no address"; continue; }

                if (_source(address) is { } source) socket.Bind(new IPEndPoint(source, 0));
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connect.CancelAfter(ProxyConnect);
                    await socket.ConnectAsync(new IPEndPoint(address, proxy.Port), connect.Token).ConfigureAwait(false);
                }

                await socket.SendAsync(record, SocketFlags.None, ct).ConfigureAwait(false);
                var buffer = new byte[16384];
                int read;
                using (var first = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    first.CancelAfter(ProxyFirstReply);
                    read = await socket.ReceiveAsync(buffer, SocketFlags.None, first.Token).ConfigureAwait(false);
                }
                if (read > 0)
                {
                    Interlocked.Increment(ref _viaProxy);
                    if (_said.TryAdd(name, 0))
                    {
                        _log($"Unblock: {name} - first connection through the lobby proxy {proxy.Host}:{proxy.Port}, answered in " +
                             $"{clock.ElapsedMilliseconds} ms.");
                    }
                    return (owned.Release(), buffer[..read]);
                }
                why = $"{proxy.Host} closed the connection after the hello";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                why = $"{proxy.Host} did not answer in time";
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                why = $"{proxy.Host}: {(ex as SocketException)?.SocketErrorCode.ToString() ?? ex.Message}";
            }
        }

        Interlocked.Exchange(ref _proxyDownUntilTicks, DateTime.UtcNow.Add(ProxyDownFor).Ticks);
        _said.TryRemove(name, out _);
        _log($"Unblock: {name} - no lobby proxy answered ({why}); this connection takes the line or the relay, and the " +
             $"proxies are left alone for {ProxyDownFor.TotalMinutes:0} min.");
        return (null, null);
    }

    /// <summary>The first record from the game - the ClientHello - or null when it is not one.</summary>
    private static async Task<byte[]?> ReadHelloAsync(Socket client, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        var header = new byte[5];
        if (!await ReadExactlyAsync(client, header, limit.Token).ConfigureAwait(false)) return null;
        var length = (header[3] << 8) | header[4];
        if (header[0] != 0x16 || length == 0 || length + 5 > SplitHello.MaxRecord) return null;

        var record = new byte[5 + length];
        header.CopyTo(record, 0);
        return await ReadExactlyAsync(client, record.AsMemory(5), limit.Token).ConfigureAwait(false) ? record : null;
    }

    /// <summary>
    /// Connects to the first edge that answers, from the line, sends the hello - split, or whole when the line does not
    /// filter the name - and waits for the first reply. Null when the line cut it - then the name goes back to the
    /// relay and the caller uses the tunnel.
    /// </summary>
    private async Task<(Socket? Edge, byte[]? Reply)> OverTheLineAsync(string name, IPAddress[] edges, byte[] record,
        bool whole, CancellationToken ct)
    {
        var how = whole ? "whole" : "split";
        var clock = Stopwatch.StartNew();
        string? why = null;
        var silent = 0;
        foreach (var address in edges.Take(ProbedEdges))
        {
            if (_source(address) is not { } source) { why = "no way out over the line"; break; }
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var owned = new SocketOwner(socket);
            try
            {
                socket.Bind(new IPEndPoint(source, 0));
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connect.CancelAfter(ConnectTimeout);
                    await socket.ConnectAsync(new IPEndPoint(address, _edgePort), connect.Token).ConfigureAwait(false);
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // This edge does not take connections now - another may, before blaming the split.
                why = $"{address} took no connection";
                continue;
            }

            try
            {
                await socket.SendAsync(whole ? record : SplitHello.Split(record), SocketFlags.None, ct).ConfigureAwait(false);
                var buffer = new byte[16384];
                int read;
                using (var first = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    first.CancelAfter(FirstReplyTimeout);
                    read = await socket.ReceiveAsync(buffer, SocketFlags.None, first.Token).ConfigureAwait(false);
                }
                if (read > 0)
                {
                    Interlocked.Increment(ref _split);
                    if (_said.TryAdd(name, 0))
                    {
                        _log($"Unblock: {name} - first connection over the line {how}, {address} answered in " +
                             $"{clock.ElapsedMilliseconds} ms.");
                    }
                    return (owned.Release(), buffer[..read]);
                }
                why = $"{address} closed the connection after the {how} hello";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                why = $"{address} did not answer the {how} hello in {FirstReplyTimeout.TotalSeconds:0} s";
                // Silence is an edge that is slow or down as often as a filter (a filter resets): try the next one.
                if (++silent < MaxSilentEdges) continue;
            }
            catch (SocketException ex)
            {
                why = $"{address} {(ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted ? "reset" : ex.SocketErrorCode.ToString())} the {how} hello";
            }
            break;   // a reset or close after the hello is the line's, not the edge's: no point trying the next
        }

        HoldOnRelay(name, $"the line cut it live ({how}): {why}");
        _log($"Unblock: {name} - the {how} hello did not get through over the line ({why}); this connection goes through the relay, and so " +
             $"does the name for {FailedFor.TotalMinutes:0} min.");
        return (null, null);
    }

    /// <summary>Back to the relay for <see cref="FailedFor"/>: the lookups that follow get the tunnel's answer, not this address.</summary>
    private void HoldOnRelay(string name, string why)
    {
        if (_verdicts.TryGetValue(name, out var verdict))
        {
            _verdicts[name] = verdict with
            {
                Split = false,
                Whole = false,
                Held = DateTimeOffset.UtcNow.Add(FailedFor),
                Why = why,
            };
        }
        _said.TryRemove(name, out _);
    }

    /// <summary>The same hello, unchanged, to an edge routed into the tunnel. Null without a tunnel.</summary>
    private async Task<Socket?> ThroughTunnelAsync(string name, IPAddress[] edges, byte[] record, CancellationToken ct)
    {
        if (_routes is not { Ready: true } routes || !routes.Route(name, edges)) return null;

        // Only the routed ones while there are any: an edge kept on the line (LineGuard - another connection uses it
        // there) would take this hello straight back to the line that just failed it.
        var routed = edges.Where(routes.Tunnelled).ToArray();
        foreach (var address in routed.Length > 0 ? routed : edges)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var owned = new SocketOwner(socket);
            try
            {
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connect.CancelAfter(ConnectTimeout);
                    await socket.ConnectAsync(new IPEndPoint(address, _edgePort), connect.Token).ConfigureAwait(false);
                }
                await socket.SendAsync(record, SocketFlags.None, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _fellBack);
                return owned.Release();
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // The next edge.
            }
        }
        return null;
    }

    /// <summary>
    /// Both ways until both are done; an end on one side is passed on as an end, a failure closes both. Over the line
    /// it is also watched, and ends when the path stops carrying it - returning why, null otherwise. See
    /// <see cref="StallWatch"/> and <see cref="LineDropped"/>.
    /// </summary>
    private static async Task<string?> PumpAsync(Socket client, Socket edge, bool overLine, CancellationToken ct,
        Action<int>? downBytes = null)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var flow = new Flow();
        if (overLine) KeepAliveFast(edge);
        var up = CopyAsync(client, edge, flow.Up, null, done.Token);
        var down = CopyAsync(edge, client, flow.Down, overLine ? code => flow.Dead(LineDropped(code, flow.Waiting)) : null, done.Token,
            downBytes);
        var watch = overLine ? WatchAsync(edge, flow, done) : Task.CompletedTask;

        var first = await Task.WhenAny(up, down).ConfigureAwait(false);
        if (!await first.ConfigureAwait(false) || flow.Why is not null)
        {
            await done.CancelAsync().ConfigureAwait(false);
        }
        await Task.WhenAll(up, down).ConfigureAwait(false);
        await done.CancelAsync().ConfigureAwait(false);
        await watch.ConfigureAwait(false);
        return flow.Why;
    }

    /// <summary>
    /// Keepalives every second after 5 s of quiet, 4 unanswered and the connection fails: a path that died while the
    /// edge was sending (the game has nothing in flight then, so <see cref="StallWatch"/> sees nothing) ends in about
    /// 9 s instead of waiting out the game's own timeout. A live edge answers keepalives even while it thinks.
    /// </summary>
    private static void KeepAliveFast(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 4);
        }
        catch (Exception)
        {
            // An older Windows: the stall watch alone.
        }
    }

    /// <summary>How often a connection over the line is looked at.</summary>
    private static readonly TimeSpan WatchTick = TimeSpan.FromSeconds(1);

    /// <summary>How long bytes may sit unacknowledged, with nothing coming back, before the path counts as stopped.</summary>
    internal static readonly TimeSpan StallFor = TimeSpan.FromSeconds(6);

    private static async Task WatchAsync(Socket edge, Flow flow, CancellationTokenSource done)
    {
        var stall = new StallWatch(StallFor);
        var clock = Stopwatch.StartNew();
        try
        {
            using var timer = new PeriodicTimer(WatchTick);
            while (await timer.WaitForNextTickAsync(done.Token).ConfigureAwait(false))
            {
                if (TcpInfo.Read(edge) is not { } sample) return;   // not on this Windows: the keepalives alone
                if (stall.Stalled(sample, clock.ElapsedMilliseconds) is { } why)
                {
                    flow.Dead(why);
                    await done.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Told what TCP knows of a connection every tick; says when its path has stopped. Stopped: bytes the game sent
    /// have been in flight - unacknowledged - for <see cref="StallFor"/> with no byte from the edge, and Windows ran a
    /// retransmission timeout in that time. A server that is only thinking has ACKed what it got, so nothing is in
    /// flight; one that is sending moves BytesIn; a slow but working path ACKs within a few round trips. Internal for
    /// TunnelCheck.
    /// </summary>
    internal sealed class StallWatch(TimeSpan stallFor)
    {
        private long? _since;
        private ulong _inAt;
        private uint _timeoutsBefore;
        private uint _lastTimeouts;

        public string? Stalled(TcpInfo.Sample sample, long nowMs)
        {
            var previous = _lastTimeouts;
            _lastTimeouts = sample.TimeoutEpisodes;
            if (sample.BytesInFlight == 0)
            {
                _since = null;
                return null;
            }
            if (_since is null || sample.BytesIn != _inAt)
            {
                _since = nowMs;
                _inAt = sample.BytesIn;
                _timeoutsBefore = previous;
                return null;
            }
            return nowMs - _since.Value >= (long)stallFor.TotalMilliseconds && sample.TimeoutEpisodes > _timeoutsBefore
                ? $"{sample.BytesInFlight} B unacknowledged for {(nowMs - _since.Value) / 1000.0:0.#} s, " +
                  $"{sample.TimeoutEpisodes - _timeoutsBefore} retransmission timeout(s), nothing back"
                : null;
        }
    }

    /// <summary>
    /// A read from the edge over the line that failed: how the line dropped it, or null for an ordinary end. Timed
    /// out / network reset is TCP itself giving up (keepalives or retransmissions unanswered): always the path. A reset
    /// is the path only while the game waits for an answer - an edge closing an idle keep-alive connection with a
    /// reset is ordinary, and must not send a name to the relay. Internal for TunnelCheck.
    /// </summary>
    internal static string? LineDropped(SocketError code, bool waiting) => code switch
    {
        SocketError.TimedOut or SocketError.NetworkReset or SocketError.HostUnreachable or SocketError.NetworkUnreachable
            or SocketError.NetworkDown => $"TCP gave up on the path ({code})",
        SocketError.ConnectionReset or SocketError.ConnectionAborted when waiting => "reset while the game waited for an answer",
        _ => null,
    };

    /// <summary>One connection's traffic: whether the game waits for the edge, and why the connection was ended, if it was.</summary>
    private sealed class Flow
    {
        private long _sent;
        private long _answered;
        private string? _why;

        public void Up() => Interlocked.Increment(ref _sent);

        public void Down() => Interlocked.Exchange(ref _answered, Interlocked.Read(ref _sent));

        /// <summary>The game sent something after the edge last sent anything.</summary>
        public bool Waiting => Interlocked.Read(ref _sent) > Interlocked.Read(ref _answered);

        public string? Why => Volatile.Read(ref _why);

        public void Dead(string? why)
        {
            if (why is not null) Interlocked.CompareExchange(ref _why, why, null);
        }
    }

    /// <returns>True when <paramref name="from"/> ended cleanly; false on a failure.</returns>
    /// <param name="seen">Called for every read that carried bytes.</param>
    /// <param name="readFailed">Told why a read from <paramref name="from"/> failed - the edge's side only.</param>
    private static async Task<bool> CopyAsync(Socket from, Socket to, Action seen, Action<SocketError>? readFailed,
        CancellationToken ct, Action<int>? counted = null)
    {
        var buffer = new byte[65536];
        while (true)
        {
            int n;
            try
            {
                n = await from.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                readFailed?.Invoke(ex.SocketErrorCode);
                return false;
            }
            catch (Exception)
            {
                return false;
            }
            if (n == 0)
            {
                try { to.Shutdown(SocketShutdown.Send); } catch (Exception) { }
                return true;
            }
            seen();
            counted?.Invoke(n);
            try
            {
                var sent = 0;
                while (sent < n) sent += await to.SendAsync(buffer.AsMemory(sent, n - sent), SocketFlags.None, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static async Task<bool> ReadExactlyAsync(Socket socket, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await socket.ReceiveAsync(buffer[read..], SocketFlags.None, ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    /// <summary>Disposes the socket unless it was handed on - so no path out of a method leaks one.</summary>
    private sealed class SocketOwner(Socket socket) : IDisposable
    {
        private Socket? _socket = socket;

        public Socket Release()
        {
            var s = _socket!;
            _socket = null;
            return s;
        }

        public void Dispose() => _socket?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener?.Dispose();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception) { }
        }
        if (_split + _fellBack > 0)
        {
            _log($"Unblock: the proxy carried {_split} connection(s) over the line; {_fellBack} went through the relay instead{(_stopped > 0 ? $"; {_stopped} stopped mid-way and were reset" : "")}.");
        }
        _stopping.Dispose();
    }
}
