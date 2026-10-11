using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GamePingBooster.Service.Dns;
using GamePingBooster.Service.Native;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

internal static partial class Program
{
    /// <summary>
    /// After a match the service writes what the game is connected to and what the unblock proxy carries - so a player
    /// stuck on the lobby's loading screen only has to send the log (FPT, 2026-10-11).
    /// </summary>
    private static async Task WhatTheGameWaitsForAfterAMatchIsLogged()
    {
        // The table is read per process: a connection this process opens shows, with its state.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var accepted = await listener.AcceptTcpClientAsync();
        var self = new HashSet<uint> { (uint)Environment.ProcessId };
        var all = TcpConnections.OfProcesses(self);
        Check("a loopback connection is not the game's business and is left out",
            !all.Any(r => r.Remote.Port == port), string.Join(", ", all.Select(r => $"{r.Remote} {r.StateName}")));

        // The proxy's own address (127.77.x) is the one loopback that counts: the game's lobby goes through it.
        var proxyLike = new TcpConnections.Owned(5, 50000, new IPEndPoint(IPAddress.Parse("127.77.0.1"), 443), 1);
        var rows = new List<TcpConnections.Owned>
        {
            proxyLike,
            proxyLike with { LocalPort = 50001 },
            new(3, 50002, new IPEndPoint(IPAddress.Parse("52.1.2.3"), 443), 1),
        };
        var seen = new Dictionary<(int Port, IPEndPoint Remote), long>
        {
            [(50000, proxyLike.Remote)] = Stopwatch.GetTimestamp() - 40 * Stopwatch.Frequency,
            [(49999, proxyLike.Remote)] = Stopwatch.GetTimestamp(),   // closed since the last look
        };
        var line = TunnelEngine.DescribeGameTcp(rows, seen);
        Check($"the game's connections are told by server and state, the longest-open first ({line})",
            line.StartsWith("game TCP 3: 127.77.0.1:443 Established x2 (up to 40 s), 52.1.2.3:443 SynSent (0 s)", StringComparison.Ordinal),
            line);
        Check("a connection gone since the last look is forgotten", !seen.ContainsKey((49999, proxyLike.Remote)),
            string.Join(", ", seen.Keys));
        Check("no connection at all is said plainly", TunnelEngine.DescribeGameTcp([], []) == "game TCP 0 - no connection open",
            TunnelEngine.DescribeGameTcp([], []));

        // The proxy's view: the game sent a request after the last byte came back - it is waiting.
        var open = new SplitProxy.OpenConnection("prod-live-front.playbattlegrounds.com.cn", "the lobby proxy 130.94.6.215", 0);
        open.Down(4096, 1000);
        open.Up(600, 2000);
        var (down, waiting, quiet, age) = open.Read(17_000);
        Check($"a request with no answer since is waiting ({waiting} ms)", down == 4096 && waiting == 15_000 && quiet == 16_000 && age == 17_000,
            $"down {down}, waiting {waiting}, quiet {quiet}, age {age}");
        open.Down(100, 18_000);
        Check("an answer ends the wait", open.Read(19_000).WaitingMs is null, "still waiting");

        // And through a real proxy: an edge that takes the request and says nothing is shown waiting.
        using var certificate = SelfSigned();
        await using var fpt = new LineFilter(SplitBlocked, reassembles: false, certificate);
        await using var proxy = new SplitProxy(new DohUpstream(_ => { }), new SplitRoutes(), s => Log.Enqueue(s),
            new System.Collections.Concurrent.ConcurrentDictionary<string, SplitProxy.Verdict>(StringComparer.OrdinalIgnoreCase)
            {
                ["prod-live-images.playbattlegrounds.com"] = new(true, [EdgeAddress], DateTimeOffset.UtcNow, "judged a moment ago", Whole: true),
            },
            new IPEndPoint(IPAddress.Loopback, 0), fpt.Port, _ => LineAddress);
        proxy.Start();
        Check("nothing open, nothing said", proxy.DescribeOpen() is null, proxy.DescribeOpen() ?? "");
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(proxy.Endpoint);
        await using var tls = new System.Net.Security.SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync("prod-live-images.playbattlegrounds.com");
        string? said = null;
        for (var i = 0; i < 50 && said?.Contains("1 open", StringComparison.Ordinal) != true; i++)
        {
            await Task.Delay(20);
            said = proxy.DescribeOpen();
        }
        Check($"an open connection is told with its name and path ({said})",
            said?.StartsWith("prod-live-images.playbattlegrounds.com via the line whole: 1 open", StringComparison.Ordinal) == true, said ?? "null");
    }

    /// <summary>
    /// Closing the game (or Disconnect) before the next match and opening it again is how both stuck players got out on
    /// 2026-10-11 - so that is what sends an after-match-stuck report, carrying the "After the match" lines with the key
    /// that opens them.
    /// </summary>
    private static Task AStuckLobbyIsReportedWithItsLines()
    {
        Check("the game running again as new processes is a relaunch",
            TunnelEngine.Relaunched(new HashSet<uint> { 10, 11 }, new HashSet<uint> { 20 }), "not a relaunch");
        Check("the same game still running is not",
            !TunnelEngine.Relaunched(new HashSet<uint> { 10, 11 }, new HashSet<uint> { 11, 20 }), "a relaunch");
        Check("the game still closed is not",
            !TunnelEngine.Relaunched(new HashSet<uint> { 10 }, new HashSet<uint>()), "a relaunch");
        Check("nothing running when the watch began is not",
            !TunnelEngine.Relaunched(new HashSet<uint>(), new HashSet<uint> { 20 }), "a relaunch");

        var detail = TunnelEngine.StuckDetail("PUBG", "the game closed", TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(41));
        Check($"what happened is said in a line ({detail})",
            detail == "PUBG: the game closed 50 s after the match ended and the game was opened again 41 s later - stuck on the way back to the lobby?",
            detail);

        const string key = "2026-10-11 20:40:00.000  --- log key k=1a2b3c4d [key:QUJD] ---";
        const string other = "2026-10-11 19:00:00.000  --- log key k=99999999 [key:REVG] ---";
        var after = "2026-10-11 20:50:05.000  After the match, +5 s: game TCP 2: [enc:1a2b3c4d:AAAA]";
        var lines = new List<string> { other, key, "2026-10-11 20:49:00.000  Relay hk-3: 41 ms", after,
            "2026-10-11 20:50:40.000  After the match, +40 s: the game closed." };
        var log = UnblockDiagnosis.PickLogLines(lines);
        Check("the after-match lines are sent, led by the key their sealed parts need, and nothing else",
            log == string.Join((char)10, key, after, "2026-10-11 20:50:40.000  After the match, +40 s: the game closed."), log);

        // Long lines: the tail stops short of what the server takes (96 KB kept, 256 KB a body).
        var many = Enumerable.Range(0, 400).Select(i => $"After the match, +{i} s: {new string('x', 1500)}").ToList();
        var capped = UnblockDiagnosis.PickLogLines(many);
        Check($"the tail fits the server ({capped.Length} chars) and ends with the newest line",
            capped.Length <= 90 * 1024 && capped.EndsWith(many[^1], StringComparison.Ordinal), $"{capped.Length} chars");
        return Task.CompletedTask;
    }
}
