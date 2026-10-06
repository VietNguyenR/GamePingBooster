using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using GamePingBooster.Service.Dns;
using GamePingBooster.Service.Native;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// SplitProxy's watch over connections it carries over the line: a path that stops mid-way is ended and the name goes
/// to the relay - so the worst a line that turns bad can do is one retry, never a hang. A dead path cannot be made on
/// loopback, so the decision (StallWatch, LineDropped) is fed by hand, TcpInfo's offsets are checked on a real
/// connection, and the reset case runs end to end.
/// </summary>
internal static partial class Program
{
    private static void AStoppedPathIsToldFromAThinkingServer()
    {
        TcpInfoReadsTheRightCounters();

        var stallMs = (long)SplitProxy.StallFor.TotalMilliseconds;

        // A long poll: the request was ACKed, nothing in flight, the server answers in a minute.
        var poll = new SplitProxy.StallWatch(SplitProxy.StallFor);
        string? said = null;
        for (var t = 0L; t <= 60_000; t += 1000) said ??= poll.Stalled(new TcpInfo.Sample(0, 500, 4000, 0), t);
        Check("a server thinking for a minute is left alone", said is null, said ?? "");

        // A big upload on a working path: always something in flight, no retransmission timeout.
        var upload = new SplitProxy.StallWatch(SplitProxy.StallFor);
        said = null;
        for (var t = 0L; t <= 30_000; t += 1000) said ??= upload.Stalled(new TcpInfo.Sample(64_000, (ulong)(t * 100), 4000, 0), t);
        Check("an upload that keeps something in flight is left alone", said is null, said ?? "");

        // A slow edge: in flight, timeouts, but bytes keep coming back.
        var slow = new SplitProxy.StallWatch(SplitProxy.StallFor);
        said = null;
        for (var t = 0L; t <= 30_000; t += 1000) said ??= slow.Stalled(new TcpInfo.Sample(300, 500, 4000 + (ulong)t, (uint)(t / 3000)), t);
        Check("a slow edge that still answers is left alone", said is null, said ?? "");

        // The path stops: the request sits unacknowledged, Windows retransmits, nothing comes back.
        var dead = new SplitProxy.StallWatch(SplitProxy.StallFor);
        long? at = null;
        for (var t = 0L; t <= 30_000 && at is null; t += 1000)
        {
            if (dead.Stalled(new TcpInfo.Sample(300, 800, 4000, t >= 2000 ? 1u : 0u), t) is not null) at = t;
        }
        Check($"a stopped path is ended within {SplitProxy.StallFor.TotalSeconds + 1:0} s ({at} ms)", at is { } ms && ms >= stallMs && ms <= stallMs + 1000,
            "never ended, or too early");

        // Read failures from the edge.
        Check("TCP giving up on the path ends it", SplitProxy.LineDropped(SocketError.TimedOut, waiting: false) is not null
            && SplitProxy.LineDropped(SocketError.NetworkReset, waiting: false) is not null, "not taken for the path");
        Check("a reset while the game waits ends it", SplitProxy.LineDropped(SocketError.ConnectionReset, waiting: true) is not null, "ignored");
        Check("a reset of an idle keep-alive connection does not", SplitProxy.LineDropped(SocketError.ConnectionReset, waiting: false) is null,
            "an edge closing an idle connection would send the name to the relay");
    }

    /// <summary>TcpInfo's offsets: BytesOut and BytesIn against what a loopback connection really sent and got.</summary>
    private static void TcpInfoReadsTheRightCounters()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var server = listener.AcceptSocket();
            client.Send(new byte[1234]);
            var got = 0;
            var buffer = new byte[4096];
            while (got < 1234) got += server.Receive(buffer);
            server.Send(new byte[567]);
            got = 0;
            while (got < 567) got += client.Receive(buffer);

            var sample = TcpInfo.Read(client);
            Check($"TCP_INFO reads this connection's counters ({sample})",
                sample is { BytesOut: 1234, BytesIn: 567, BytesInFlight: 0 }, "wrong offsets, or not on this Windows");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task AResetWhileTheGameWaitsSendsTheNameToTheRelay()
    {
        const string name = "prod-live-xenuine.playbattlegrounds.com";
        var hello = CaptureHello(name);

        // idle: the edge answers, then resets an idle connection. waiting: it resets while a request is unanswered.
        foreach (var waiting in new[] { false, true })
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var verdicts = new ConcurrentDictionary<string, SplitProxy.Verdict>(StringComparer.OrdinalIgnoreCase)
            {
                [name] = new(true, [EdgeAddress], DateTimeOffset.UtcNow, "judged whole a moment ago", Whole: true),
            };
            var proxy = new SplitProxy(new DohUpstream(_ => { }), new SplitRoutes(), s => Log.Enqueue(s), verdicts,
                new IPEndPoint(IPAddress.Loopback, 0), ((IPEndPoint)listener.LocalEndpoint).Port, _ => LineAddress);
            try
            {
                proxy.Start();
                var edgeSide = Task.Run(async () =>
                {
                    using var edge = await listener.AcceptSocketAsync();
                    var buffer = new byte[16384];
                    await edge.ReceiveAsync(buffer, SocketFlags.None);           // the hello
                    await edge.SendAsync(new byte[] { 0x16, 3, 3, 0, 1 }, SocketFlags.None);
                    if (waiting) await edge.ReceiveAsync(buffer, SocketFlags.None);   // the game's request, never answered
                    else await Task.Delay(300);
                    edge.LingerState = new LingerOption(true, 0);
                    edge.Close();                                                 // a reset
                });

                using var game = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await game.ConnectAsync(proxy.Endpoint);
                await game.SendAsync(hello, SocketFlags.None);
                var first = new byte[5];
                await game.ReceiveAsync(first, SocketFlags.None);
                if (waiting) await game.SendAsync(new byte[] { 0x17, 3, 3, 0, 1, 0 }, SocketFlags.None);
                await edgeSide;

                string ending;
                using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    try { ending = await game.ReceiveAsync(first, SocketFlags.None, limit.Token) == 0 ? "closed" : "data"; }
                    catch (SocketException ex) { ending = ex.SocketErrorCode.ToString(); }
                    catch (OperationCanceledException) { ending = "hung"; }
                }
                await Task.Delay(100);

                if (waiting)
                {
                    Check($"reset while the game waited: the game's connection fails at once ({ending})",
                        ending is "ConnectionReset" or "ConnectionAborted", "the game would wait");
                    Check("and the name goes to the relay", proxy.Stopped == 1 && !proxy.Answers(name) && verdicts[name].Held > DateTimeOffset.UtcNow,
                        $"stopped {proxy.Stopped}, {verdicts[name].Why}");
                }
                else
                {
                    Check($"an idle connection reset by the edge just ends ({ending})", ending is not "hung", "the game would wait");
                    Check("and the name stays over the line", proxy.Stopped == 0 && proxy.Answers(name), verdicts[name].Why);
                }
            }
            finally
            {
                await proxy.DisposeAsync();
                listener.Stop();
            }
        }
    }
}
