using System.Net;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// The game's own TCP connections while it runs - the lobby, the store, the images - followed one by one and written to
/// the log per name: how many opened, how each ended (closed by the game, by the server, or cut), what came down, how
/// long the game waited for an answer, and how the path treated it (retransmissions, retransmission timeouts, RTT).
///
/// Why: on FPT, 2026-10-15, the lobby was slow to click through and the store's icons stayed blank. The log could not
/// say whether the time went to the mainland .cn mirror, to the CloudFront image hosts the line keeps cutting, or to the
/// game itself - <see cref="TunnelEngine"/>'s after-match lines look at the connection table every 5 s, which misses
/// a connection that lives one second and cannot tell a reset from a close. This reads the table every
/// <see cref="Every"/> and Windows' per-connection counters (<see cref="TcpEStats"/>), so a connection cut with
/// nothing down, or one the game waits on for seconds, is seen and named.
///
/// How a connection ended, from what the table showed: one the game closed passes through TIME_WAIT; one the server
/// closed was seen in CLOSE_WAIT or LAST_ACK first. One that vanished with neither is "unseen", not cut: a server's close
/// the game answers within one look never shows CLOSE_WAIT (2026-10-16, FPT and VNPT: every short API request "without
/// a close"). The split proxy's connections (127.77.0.1) rarely show TIME_WAIT at all, so for them the proxy says who
/// ended first (<see cref="Named.EndedBy"/>): the game, the edge ("the server"), or a failure ("reset").
/// What came down is said in KB, never judged: a small API answer is as small as a bare handshake.
/// A game that exits takes all its connections with it; those are not counted as ended.
///
/// Logs only: nothing here changes a route, an answer or a connection. Nothing touches the game: the table and the
/// counters belong to Windows' TCP stack.
///
/// Not thread-safe: <see cref="Poll"/> is called from one loop.
/// </summary>
internal sealed class LobbyTcpWatch
{
    /// <summary>What a connection goes to: the name the resolver gave its address out for, and the way it takes.
    /// Down and Up are the split proxy's own counts, for a connection to it; EndedBy who ended it first, once one side
    /// has.</summary>
    internal readonly record struct Named(string Name, string Path, long? Down, long? Up, string? EndedBy = null);

    public static readonly TimeSpan Every = TimeSpan.FromMilliseconds(500);

    /// <summary>A summary per name this often, when the name had anything to say.</summary>
    internal const long WindowMs = 30_000;

    /// <summary>The game waiting this long for an answer on one connection is said at once.</summary>
    internal const long WaitSaidMs = 5_000;

    /// <summary>Still in SYN_SENT after this is said at once: the line is not letting the connection through.</summary>
    internal const long ConnectSaidMs = 3_000;

    /// <summary>
    /// A connection that ends with the game waiting at least this long is said at once. Shorter is the game's own
    /// goodbye: TLS sends close_notify just before the close.
    /// </summary>
    internal const long WaitedAtEndMs = 2_000;

    /// <summary>Lines said at once per window; the rest are counted in the summary.</summary>
    private const int EventsPerWindow = 20;

    /// <summary>Connections followed at once; more (2026-10-15: 240 to one image host) are counted, not followed.</summary>
    private const int MaxFollowed = 512;

    private const uint SynSent = 3, CloseWait = 8, LastAck = 10;

    private sealed class Conn(long firstMs)
    {
        public readonly long FirstMs = firstMs;
        public Named? Named;
        public uint State;
        public bool ServerClosed;
        public bool Counted;   // ESTATS switched on
        public long In, Out;   // since counting began
        public long TalliedIn;   // of In, already added to a window
        public long LastInMs = -1, LastOutMs = -1;
        public bool WaitSaid, ConnectSaid;
        public long WaitMaxMs;
        public uint? FirstRetrans, FirstTimeouts;
        public uint Retrans, Timeouts, RttMs;
    }

    /// <summary>One name and path, over one window.</summary>
    private sealed class Tally
    {
        public int Opened, ByGame, ByServer, Reset, Unseen, EndedWaiting, SlowConnects, LongWaits;
        public long Down;
        public long WaitMaxMs;
        public long Retrans, Timeouts;
        public readonly List<uint> Rtts = [];
        public readonly Dictionary<IPAddress, int> Edges = [];
        public readonly Dictionary<IPAddress, int> CutEdges = [];
    }

    private readonly Dictionary<(int LocalPort, IPEndPoint Remote), Conn> _conns = [];
    private readonly Dictionary<(string Name, string Path), Tally> _window = [];
    private long _windowStartMs = -1;
    private int _eventsThisWindow, _eventsDropped, _unfollowed;
    private readonly Action<string> _log;
    private readonly Func<IPAddress?> _tunnelAddress;
    private readonly Func<IPEndPoint, int, Named?> _nameOf;

    public LobbyTcpWatch(Func<IPEndPoint, int, Named?> nameOf, Func<IPAddress?> tunnelAddress, Action<string> log)
    {
        _nameOf = nameOf;
        _tunnelAddress = tunnelAddress;
        _log = log;
    }

    // Windows, unless TunnelCheck stands in for it.
    internal Func<IReadOnlySet<uint>, (List<TcpConnections.Owned> Game, HashSet<(int LocalPort, IPEndPoint Remote)> TimeWait)> Table
    { get; init; } = TcpConnections.OfProcessesAndTimeWait;
    internal Func<IPEndPoint, IPEndPoint, uint> Enable { get; init; } = (l, r) =>
    {
        // The data counters decide; the path counters are a bonus.
        var error = TcpEStats.Enable(l, r);
        if (error == 0) TcpEStats.EnablePath(l, r);
        return error;
    };
    internal Func<IPEndPoint, IPEndPoint, TcpEStats.Counters?> Read { get; init; } = TcpEStats.Read;
    internal Func<IPEndPoint, IPEndPoint, TcpEStats.PathCounters?> ReadPath { get; init; } = TcpEStats.ReadPath;

    /// <summary>Whether one of the game's processes still runs: connections gone with the game are not ends.</summary>
    internal Func<IReadOnlySet<uint>, bool> GameRuns { get; init; } = pids => pids.Any(pid =>
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }   // not running
        catch (Exception) { return true; }   // running, but not ours to look into
    });

    /// <summary>The game closed or the watch stops: what is left is written, then forgotten.</summary>
    public void Flush(long nowMs)
    {
        foreach (var (key, conn) in _conns) End(key, conn, WatchStopped, nowMs);
        _conns.Clear();
        WriteWindow(nowMs);
        _windowStartMs = -1;
    }

    /// <summary>One look at the game's connections, at <paramref name="nowMs"/> on any steady clock.</summary>
    public void Poll(IReadOnlySet<uint> pids, long nowMs)
    {
        if (_windowStartMs < 0) _windowStartMs = nowMs;
        var (rows, timeWait) = Table(pids);
        var inner = _tunnelAddress();
        var live = new HashSet<(int, IPEndPoint)>();

        foreach (var row in rows)
        {
            var key = (row.LocalPort, row.Remote);
            live.Add(key);
            if (!_conns.TryGetValue(key, out var conn))
            {
                if (_conns.Count >= MaxFollowed) { _unfollowed++; continue; }
                conn = new Conn(nowMs);
                _conns[key] = conn;
                var local = new IPEndPoint(row.Local, row.LocalPort);
                conn.Counted = row.State != SynSent && Enable(local, row.Remote) == 0;
            }
            var localEnd = new IPEndPoint(row.Local, row.LocalPort);
            if (!conn.Counted && row.State != SynSent && conn.State == SynSent)
                conn.Counted = Enable(localEnd, row.Remote) == 0;
            conn.State = row.State;
            if (row.State is CloseWait or LastAck) conn.ServerClosed = true;

            // Named on first sight when it can be, else on a later look: the proxy learns the name from the hello.
            if (conn.Named is null || conn.Named.Value.Down is not null)
            {
                var named = _nameOf(row.Remote, row.LocalPort);
                if (named is { } n && inner is not null && row.Local.Equals(inner)) named = n with { Path = "the relay" };
                if (named is not null) conn.Named = named;
            }

            if (row.State == SynSent)
            {
                if (!conn.ConnectSaid && nowMs - conn.FirstMs >= ConnectSaidMs)
                {
                    conn.ConnectSaid = true;
                    TallyOf(conn, row.Remote).SlowConnects++;
                    Event($"Lobby TCP: {Label(conn, row.Remote)} - still connecting after {(nowMs - conn.FirstMs) / 1000.0:0.0} s.");
                }
                continue;
            }

            Update(conn, localEnd, row.Remote, nowMs);
        }

        var gone = _conns.Where(c => !live.Contains(c.Key)).ToArray();
        if (gone.Length > 0 && !GameRuns(pids))
        {
            // 2026-10-16 VNPT: closing the game wrote every open connection as "gone without a close".
            foreach (var (key, conn) in gone) { _conns.Remove(key); End(key, conn, WatchStopped, nowMs); }
            _log($"Lobby TCP: the game closed with {gone.Length} connection(s) open - not counted as ended.");
            gone = [];
        }
        foreach (var (key, conn) in gone)
        {
            _conns.Remove(key);
            // The proxy has the last word on its own connections; asked once more, now the game's side has gone.
            if (conn.Named is { Down: not null } && _nameOf(key.Remote, key.LocalPort) is { Down: not null } last)
                conn.Named = last;
            var how = timeWait.Contains(key) ? "the game"
                : conn.ServerClosed ? "the server"
                : conn.Named?.EndedBy;
            End(key, conn, how, nowMs);
        }

        if (nowMs - _windowStartMs >= WindowMs) WriteWindow(nowMs);
    }

    private void Update(Conn conn, IPEndPoint local, IPEndPoint remote, long nowMs)
    {
        long inNow, outNow;
        if (conn.Counted && Read(local, remote) is { } counters)
        {
            inNow = (long)counters.DataBytesIn;
            outNow = (long)counters.DataBytesOut;
        }
        else if (conn.Named is { Down: { } down, Up: { } up })
        {
            inNow = down;
            outNow = up;
        }
        else
        {
            return;
        }

        // The split proxy names a connection once it has read the hello: until then (2026-10-15 FPT test: "127.77.0.1:443
        // via ?"), its bytes wait rather than land under the bare address.
        var named = conn.Named is not null || !IPAddress.IsLoopback(remote.Address) || nowMs - conn.FirstMs >= NameWaitMs;
        if (inNow > conn.In)
        {
            conn.LastInMs = nowMs;
            if (named) { TallyOf(conn, remote).Down += inNow - conn.TalliedIn; conn.TalliedIn = inNow; }
            conn.In = inNow;
        }
        if (outNow > conn.Out) { conn.LastOutMs = nowMs; conn.Out = outNow; }

        if (conn.Counted && named && ReadPath(local, remote) is { } path &&
            path.PktsRetrans >= (conn.FirstRetrans ?? 0) && path.Timeouts >= (conn.FirstTimeouts ?? 0))
        {
            conn.FirstRetrans ??= path.PktsRetrans;
            conn.FirstTimeouts ??= path.Timeouts;
            var tally = TallyOf(conn, remote);
            var retrans = path.PktsRetrans - conn.FirstRetrans.Value;
            var timeouts = path.Timeouts - conn.FirstTimeouts.Value;
            tally.Retrans += retrans - conn.Retrans;
            tally.Timeouts += timeouts - conn.Timeouts;
            conn.Retrans = retrans;
            conn.Timeouts = timeouts;
            if (path.SmoothedRttMs > 0) conn.RttMs = path.SmoothedRttMs;
        }

        // Waiting: the game sent something after the last byte came back. An idle keep-alive is quiet both ways.
        if (conn.LastOutMs > conn.LastInMs)
        {
            var waited = nowMs - conn.LastOutMs;
            conn.WaitMaxMs = Math.Max(conn.WaitMaxMs, waited);
            if (!conn.WaitSaid && waited >= WaitSaidMs)
            {
                conn.WaitSaid = true;
                TallyOf(conn, remote).LongWaits++;
                Event($"Lobby TCP: {Label(conn, remote)} - the game has waited {waited / 1000.0:0.0} s for an answer " +
                      $"({conn.In / 1024} KB down so far, open {(nowMs - conn.FirstMs) / 1000} s{PathText(conn)}).");
            }
        }
        else
        {
            conn.WaitSaid = false;
        }
    }

    private const string WatchStopped = "the watch stopped";

    /// <param name="by">"the game", "the server", "reset" (the split proxy saw a failure), or null when nothing showed
    /// who ended it.</param>
    private void End((int LocalPort, IPEndPoint Remote) key, Conn conn, string? by, long nowMs)
    {
        var tally = TallyOf(conn, key.Remote);
        // The proxy's own count is the last word for its connections: bytes since the last look land here.
        if (conn.Named is { Down: { } down } && down > conn.TalliedIn)
        {
            tally.Down += down - conn.TalliedIn;
            conn.TalliedIn = conn.In = down;
        }
        // A wait that ran until the end is longer than the last look saw.
        var waitedMs = conn.LastOutMs > conn.LastInMs ? nowMs - conn.LastOutMs : 0;
        if (by != WatchStopped) conn.WaitMaxMs = Math.Max(conn.WaitMaxMs, waitedMs);
        if (conn.WaitMaxMs > tally.WaitMaxMs) tally.WaitMaxMs = conn.WaitMaxMs;
        if (conn.RttMs > 0) tally.Rtts.Add(conn.RttMs);
        if (by == WatchStopped) return;
        if (by == "the game") { tally.ByGame++; return; }

        if (by == "the server") tally.ByServer++;
        else if (by == "reset") tally.Reset++;
        else tally.Unseen++;
        if (by != "the server") tally.CutEdges[key.Remote.Address] = tally.CutEdges.GetValueOrDefault(key.Remote.Address) + 1;

        var waiting = waitedMs >= WaitedAtEndMs;
        if (waiting) tally.EndedWaiting++;
        // Said at once: one that never connected, one the proxy saw fail, one that ended with the game owed an answer.
        if (!waiting && by != "reset" && conn.State != SynSent) return;
        var what = conn.State == SynSent ? "never connected"
            : by == "reset" ? "reset"
            : by == "the server" ? "closed by the server"
            : "gone, the close unseen";
        Event($"Lobby TCP: {Label(conn, key.Remote)} - {what} after {(nowMs - conn.FirstMs) / 1000.0:0.0} s " +
              $"({conn.In / 1024.0:0.0} KB down{(waiting ? $", the game waiting {waitedMs / 1000.0:0.0} s" : "")}{PathText(conn)}).");
    }

    /// <summary>A connection to the split proxy waits this long to be named before it is tallied by address.</summary>
    private const long NameWaitMs = 2_000;

    private Tally TallyOf(Conn conn, IPEndPoint remote)
    {
        var named = conn.Named;
        var key = (named?.Name ?? $"{remote.Address}:{remote.Port}", named?.Path ?? "?");
        if (!_window.TryGetValue(key, out var tally))
        {
            tally = new Tally();
            _window[key] = tally;
        }
        // The first time a connection lands in a window it counts as opened there.
        if (_counting.Add(conn))
        {
            tally.Opened++;
            tally.Edges[remote.Address] = tally.Edges.GetValueOrDefault(remote.Address) + 1;
        }
        return tally;
    }

    private readonly HashSet<Conn> _counting = [];

    private void WriteWindow(long nowMs)
    {
        var seconds = (nowMs - _windowStartMs) / 1000.0;
        foreach (var ((name, path), t) in _window.OrderByDescending(w => w.Value.Down))
        {
            var open = _conns.Values.Count(c => c.Named is { } n ? n.Name == name && n.Path == path : name.Contains(':'));
            var parts = new List<string>
            {
                $"{t.Opened} new, {open} open",
                $"ended: {t.ByGame} by the game, {t.ByServer} by the server, {t.Reset} reset, {t.Unseen} unseen" +
                (t.EndedWaiting > 0 ? $" ({t.EndedWaiting} while the game waited)" : "") + CutText(t),
                $"{t.Down / 1024} KB down",
            };
            if (t.WaitMaxMs > 0) parts.Add($"longest wait {t.WaitMaxMs / 1000.0:0.0} s{(t.LongWaits > 0 ? $" ({t.LongWaits} over {WaitSaidMs / 1000} s)" : "")}");
            if (t.SlowConnects > 0) parts.Add($"{t.SlowConnects} slow to connect");
            if (t.Retrans > 0 || t.Timeouts > 0) parts.Add($"{t.Retrans} segments resent, {t.Timeouts} retransmission timeouts");
            if (t.Rtts.Count > 0)
            {
                var sorted = t.Rtts.Order().ToArray();
                parts.Add($"rtt {sorted[sorted.Length / 2]} ms (max {sorted[^1]})");
            }
            var edges = t.Edges.Where(e => !IPAddress.IsLoopback(e.Key)).OrderByDescending(e => e.Value).ToArray();
            if (edges.Length > 0)
                parts.Add($"edges {string.Join(", ", edges.Take(4).Select(e => $"{e.Key} x{e.Value}"))}" +
                          (edges.Length > 4 ? $" and {edges.Length - 4} more" : ""));
            _log($"Lobby TCP, last {seconds:0} s, {name} via {path}: {string.Join("; ", parts)}.");
        }
        if (_eventsDropped > 0) _log($"Lobby TCP: {_eventsDropped} more line(s) like the above were not written this window.");
        if (_unfollowed > 0) _log($"Lobby TCP: {_unfollowed} connection(s) not followed - over {MaxFollowed} open at once.");

        _window.Clear();
        // A connection still open counts as new in the next window only if it was not counted yet.
        _counting.IntersectWith(_conns.Values);
        _windowStartMs = nowMs;
        _eventsThisWindow = _eventsDropped = _unfollowed = 0;
    }

    /// <summary>Where the resets and unseen ends were, by edge; the proxy's own address says nothing.</summary>
    private static string CutText(Tally t)
    {
        var edges = t.CutEdges.Where(e => !IPAddress.IsLoopback(e.Key)).OrderByDescending(e => e.Value).Take(4).ToArray();
        return edges.Length == 0 ? "" : $" (reset/unseen on {string.Join(", ", edges.Select(e => $"{e.Key} x{e.Value}"))})";
    }

    private void Event(string line)
    {
        if (_eventsThisWindow++ < EventsPerWindow) _log(line);
        else _eventsDropped++;
    }

    private static string Label(Conn conn, IPEndPoint remote) =>
        conn.Named is { } n ? $"{n.Name} ({remote.Address}{(remote.Port == 443 ? "" : $":{remote.Port}")}) via {n.Path}"
            : $"{remote} (name unknown)";

    private static string PathText(Conn conn) =>
        (conn.Retrans > 0 || conn.Timeouts > 0 ? $", {conn.Retrans} resent, {conn.Timeouts} timeouts" : "") +
        (conn.RttMs > 0 ? $", rtt {conn.RttMs} ms" : "");
}
