using System.Net;
using System.Net.Sockets;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Service.Discovery;
using GamePingBooster.Service.Native;
using Owned = GamePingBooster.Service.Native.TcpConnections.Owned;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// Discovery for a TCP game (roadmap step 4): the game's connections counted per address by Windows' own per-connection
/// counters, judged by the same rate rule as UDP. The addresses are Aniimo's from the 2026-10-13 live test.
/// </summary>
internal static partial class Program
{
    private static Task TcpDiscoveryJudgesTheMatchConnectionByItsSegments()
    {
        var aniimo = new GameEntry { Id = "aniimo", TcpGameplay = true, LobbyAddresses = ["8.219.197.133"] };
        Check("A TCP game without a declared rate needs 90 data segments in 30 s (3 a second)",
            GameDestinationRecorder.PacketsFor(aniimo) == 90, $"{GameDestinationRecorder.PacketsFor(aniimo)}");
        Check("  a UDP game keeps the 500-packet default",
            GameDestinationRecorder.PacketsFor(new GameEntry { Id = "pubg" }) == 500, "");
        Check("  and a declared rate wins for a TCP game too",
            GameDestinationRecorder.PacketsFor(new GameEntry { Id = "x", TcpGameplay = true, DiscoveryPacketsPerSecond = 10 }) == 300, "");

        var line = IPAddress.Parse("192.168.1.5");
        var inner = IPAddress.Parse("10.77.0.112");
        Owned Conn(IPAddress local, int localPort, string remote, int port) =>
            new(5, localPort, new IPEndPoint(IPAddress.Parse(remote), port), 42) { Local = local };

        var match = Conn(line, 50001, "47.84.201.35", 20000);
        var gateway = Conn(line, 50002, "47.245.180.147", 10000);
        var lobby = Conn(line, 50003, "8.219.197.133", 10000);
        var web = Conn(line, 50004, "47.253.108.56", 443);
        var tunnelled = Conn(inner, 50005, "43.106.8.250", 20000);

        var open = new List<Owned> { match, gateway, lobby, web, tunnelled };
        var counters = new Dictionary<int, ulong> { [50001] = 4000, [50002] = 10, [50003] = 0, [50004] = 0, [50005] = 0 };
        var enabled = new HashSet<int>();
        var sampler = new TcpFlowSampler(new HashSet<IPAddress> { IPAddress.Parse("8.219.197.133") }, () => inner, _ => { })
        {
            Connections = _ => [.. open],
            EnableCounters = (local, _) => { enabled.Add(local.Port); return 0; },
            ReadCounters = (local, _) => counters.TryGetValue(local.Port, out var n) ? new TcpEStats.Counters(n, n, 0, 0) : null,
        };
        var pids = new HashSet<uint> { 42 };

        var first = sampler.Sample(pids);
        Check("Web ports, the lobby /32 and connections riding the tunnel are never counted",
            enabled.SetEquals([50001, 50002]) && first.Count == 2, string.Join(",", enabled));
        Check("  and the first read is a baseline: 4,000 segments from before listening are not a burst",
            first.All(r => r.Sent == 0), string.Join(", ", first.Select(r => $"{r.Address}={r.Sent}")));

        counters[50001] += 120;
        counters[50002] += 5;
        counters[50005] += 900;
        var second = sampler.Sample(pids).ToDictionary(r => r.Address.ToString());
        Check("What each address sent since is counted: the match 120, its gateway 5",
            second["47.84.201.35"].Sent == 120 && second["47.245.180.147"].Sent == 5 && second.Count == 2,
            string.Join(", ", second.Select(p => $"{p.Key}={p.Value.Sent}")));
        Check("  with the port it was sent to", second["47.84.201.35"].Ports.SetEquals([20000]), "");

        // The game reconnects to the same server: the old connection goes, a new one starts from its own counters.
        open.Remove(match);
        open.Add(Conn(line, 50006, "47.84.201.35", 20000));
        counters[50006] = 3;
        sampler.Sample(pids);
        counters[50006] = 50;
        var third = sampler.Sample(pids).ToDictionary(r => r.Address.ToString());
        Check("A reconnect to the same server adds to its total instead of starting again",
            third["47.84.201.35"].Sent == 167, $"{third["47.84.201.35"].Sent}");

        sampler.Clear();
        var cleared = sampler.Sample(pids);
        Check("After listening stops, the next listen starts from a fresh baseline",
            cleared.All(r => r.Sent == 0), string.Join(", ", cleared.Select(r => $"{r.Address}={r.Sent}")));
        return Task.CompletedTask;
    }

    /// <summary>The real counters on a real connection. Switching them on needs Administrator; without it this says so
    /// and checks only that the refusal is the documented one.</summary>
    private static async Task TcpCountersCountARealConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        // IPv4 only: a default TcpClient is a dual-mode IPv6 socket, its connection sits in the IPv6 table, and the
        // IPv4 row finds nothing (error 1214). The game's connections are read from the IPv4 table, so they are found.
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();

        var local = (IPEndPoint)client.Client.LocalEndPoint!;
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        var error = TcpEStats.Enable(local, remote);
        if (error == 5)
        {
            Check("Per-connection counters need Administrator (error 5 here) - run elevated to check them", true, "");
            return;
        }
        Check("Windows switches the counters on for one connection", error == 0, $"error {error}");

        var stream = client.GetStream();
        var sink = server.GetStream();
        var buffer = new byte[64];
        for (var i = 0; i < 20; i++)
        {
            await stream.WriteAsync(new byte[] { (byte)i });
            await sink.ReadExactlyAsync(buffer.AsMemory(0, 1));
        }
        var counted = TcpEStats.Read(local, remote);
        Check("  and counts the data segments sent on it: 20 one-byte writes",
            counted is { DataSegsOut: >= 20 and < 40 }, $"{counted}");
    }
}
