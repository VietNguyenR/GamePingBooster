using System.Net;
using System.Net.Sockets;
using GamePingBooster.Service.Native;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

internal static partial class Program
{
    /// <summary>
    /// The game's TCP connections per name in the log - so a slow lobby or a blank store icon says which host it was,
    /// and whether its connections were cut, waited on, or closed normally (FPT, 2026-10-15).
    /// </summary>
    private static async Task LobbyTcpIsLoggedPerName()
    {
        // Windows' counters on a real connection: the path record's size is the one Windows expects.
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var accepted = await listener.AcceptTcpClientAsync();
            var local = (IPEndPoint)client.Client.LocalEndPoint!;
            var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
            var data = TcpEStats.Enable(local, remote);
            var path = TcpEStats.EnablePath(local, remote);
            await client.GetStream().WriteAsync(new byte[3000]);
            await accepted.GetStream().ReadExactlyAsync(new byte[3000]);
            var read = TcpEStats.ReadPath(local, remote);
            var counters = TcpEStats.Read(local, remote);
            Check($"Windows counts a connection's path (enable {data}/{path}, mss {read?.CurMss}, out {counters?.DataBytesOut})",
                data == 0 && path == 0 && read is { CurMss: > 0 } && counters is { DataBytesOut: >= 3000 },
                $"enable {data}/{path}, path {read}, data {counters}");
        }

        var images = new IPEndPoint(IPAddress.Parse("13.227.227.106"), 443);
        var front = new IPEndPoint(IPAddress.Parse("43.175.1.2"), 443);
        var proxy = new IPEndPoint(IPAddress.Parse("127.77.0.1"), 443);
        var proxyEnded = new Dictionary<int, string?>();
        var gameRuns = true;
        var rows = new List<TcpConnections.Owned>();
        var timeWait = new HashSet<(int, IPEndPoint)>();
        var bytes = new Dictionary<int, (ulong In, ulong Out)>();
        var lines = new List<string>();
        var watch = new LobbyTcpWatch(
            (remote, port) => remote.Equals(images) ? new LobbyTcpWatch.Named("prod-live-images", "the line", null, null)
                : remote.Equals(proxy) ? new LobbyTcpWatch.Named("prod-live-front", "the line whole", 2000, 500, proxyEnded.GetValueOrDefault(port))
                : null,
            () => null, lines.Add)
        {
            GameRuns = _ => gameRuns,
            Table = _ => (rows.ToList(), timeWait),
            Enable = (_, _) => 0,
            Read = (l, _) => bytes.TryGetValue(l.Port, out var b) ? new TcpEStats.Counters(1, 1, b.Out, b.In) : null,
            ReadPath = (_, _) => new TcpEStats.PathCounters(0, 0, 30, 1400),
        };
        TcpConnections.Owned Row(uint state, int port, IPEndPoint remote) =>
            new(state, port, remote, 1) { Local = IPAddress.Parse("192.168.1.5") };
        var pids = new HashSet<uint> { 1 };

        // 50001: closed by the game; 50002: gone unseen with the game waiting; 50003: waits 6 s; 50004: server closes;
        // 50005/50006: the split proxy's, one closed by its edge, one reset - the table shows neither.
        rows.AddRange([Row(5, 50001, images), Row(5, 50002, images), Row(5, 50003, images), Row(5, 50004, front),
            Row(5, 50005, proxy), Row(5, 50006, proxy)]);
        bytes[50001] = (0, 0); bytes[50002] = (0, 0); bytes[50003] = (0, 0); bytes[50004] = (0, 0);
        watch.Poll(pids, 0);
        bytes[50001] = (200_000, 800); bytes[50002] = (3000, 1200); bytes[50003] = (50_000, 900); bytes[50004] = (10_000, 500);
        watch.Poll(pids, 500);
        bytes[50002] = (3000, 1500); bytes[50003] = (50_000, 1400);
        watch.Poll(pids, 1000);
        rows.RemoveAll(r => r.LocalPort is 50001 or 50005 or 50006);
        proxyEnded[50005] = "the server";
        proxyEnded[50006] = "reset";
        timeWait.Add((50001, images));
        rows[rows.FindIndex(r => r.LocalPort == 50004)] = Row(8, 50004, front);
        watch.Poll(pids, 1500);
        rows.RemoveAll(r => r.LocalPort is 50002 or 50004);
        watch.Poll(pids, 7500);

        Check("a connection gone unseen while the game waited is said at once, its bytes not judged",
            lines.Any(l => l.Contains("prod-live-images (13.227.227.106) via the line - gone, the close unseen") &&
                           l.Contains("2.9 KB down, the game waiting")) && !lines.Any(l => l.Contains("no content")),
            string.Join(" / ", lines));
        Check("the split proxy says how its connections ended: a reset is said, an edge's close is not",
            lines.Any(l => l.Contains("prod-live-front (127.77.0.1) via the line whole - reset")) &&
            !lines.Any(l => l.Contains("prod-live-front (127.77.0.1) via the line whole - closed")),
            string.Join(" / ", lines));
        Check("a long wait for an answer is said at once",
            lines.Any(l => l.Contains("has waited 6.5 s for an answer")), string.Join(" / ", lines));

        watch.Poll(pids, 30_000);
        var summary = lines.FirstOrDefault(l => l.StartsWith("Lobby TCP, last 30 s, prod-live-images via the line:", StringComparison.Ordinal));
        Check($"the window says how each ended, per name ({summary})",
            summary is not null && summary.Contains("3 new, 1 open") && summary.Contains("1 by the game, 0 by the server, 0 reset, 1 unseen") &&
            summary.Contains("(1 while the game waited) (reset/unseen on 13.227.227.106 x1)") && summary.Contains("edges 13.227.227.106 x3"),
            string.Join(" / ", lines));
        var proxied = lines.FirstOrDefault(l => l.StartsWith("Lobby TCP, last 30 s, prod-live-front via the line whole:", StringComparison.Ordinal));
        Check($"the proxy's ends are counted from the proxy ({proxied})",
            proxied is not null && proxied.Contains("0 by the game, 1 by the server, 1 reset, 0 unseen;") && !proxied.Contains("reset/unseen on"),
            string.Join(" / ", lines));
        Check("a server's close is told apart, and an unnamed host is named by address",
            lines.Any(l => l.StartsWith("Lobby TCP, last 30 s, 43.175.1.2:443 via ?:", StringComparison.Ordinal) && l.Contains("1 by the server")),
            string.Join(" / ", lines));

        // The game exits: its open connections vanish at once, and none is an end.
        lines.Clear();
        gameRuns = false;
        rows.Clear();
        watch.Poll(pids, 31_000);
        watch.Poll(pids, 61_000);
        Check("connections the game takes with it when it exits are not counted as ended",
            lines.Any(l => l.Contains("the game closed with 1 connection(s) open")) &&
            !lines.Any(l => l.Contains("unseen") && !l.Contains("0 unseen")) && !lines.Any(l => l.Contains(" - gone")),
            string.Join(" / ", lines));
    }
}
