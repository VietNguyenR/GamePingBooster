using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

// The multi-tunnel rig's stand-in game. Sends numbered UDP to the rig's two game servers from connected sockets, as a
// game does, reads the echoes back, and writes one line a second: what was sent, what came back, and the longest
// silence - so the driver can say whether a move lost anything.
//
//     RigGame.exe <control-file> <stats-file>
//
// The control file holds one word, re-read every 100 ms: quiet (nothing - the lobby), kr (a match in kr at 40/s, and
// sg at 5/s beside it), sg (a match in sg at 40/s), exit.

var control = args.Length > 0 ? args[0] : "riggame-control.txt";
var statsPath = args.Length > 1 ? args[1] : "riggame-stats.csv";

var servers = new Dictionary<string, IPEndPoint>
{
    ["sg"] = new(IPAddress.Parse("198.51.100.10"), 27015),
    ["kr"] = new(IPAddress.Parse("198.51.100.20"), 27015),
};
// Opened when a match starts and closed when it ends, as a game does: a UDP socket connected before the booster's
// routes went in keeps the way out it was connected on, and nothing it sends ever reaches the tunnel.
var sockets = new Dictionary<string, (Socket Socket, CancellationTokenSource Stop)>();
var sentAt = servers.Keys.ToDictionary(k => k, _ => new ConcurrentDictionary<int, long>());
var back = servers.Keys.ToDictionary(k => k, _ => new ConcurrentDictionary<int, long>());
var next = servers.Keys.ToDictionary(k => k, _ => 0);
var lastBack = servers.Keys.ToDictionary(k => k, _ => 0L);
var maxGap = servers.Keys.ToDictionary(k => k, _ => 0.0);
var gate = new object();
using var stop = new CancellationTokenSource();

Socket Open(string name)
{
    if (sockets.TryGetValue(name, out var open)) return open.Socket;
    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    socket.Connect(servers[name]);
    var cts = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
    sockets[name] = (socket, cts);
    lock (gate) lastBack[name] = 0;
    _ = Task.Run(async () =>
    {
        var buffer = new byte[2048];
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var n = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
                if (n < 4) continue;
                var now = Stopwatch.GetTimestamp();
                back[name].TryAdd(BinaryPrimitives.ReadInt32BigEndian(buffer), now);
                lock (gate)
                {
                    if (lastBack[name] != 0) maxGap[name] = Math.Max(maxGap[name], (now - lastBack[name]) * 1000.0 / Stopwatch.Frequency);
                    lastBack[name] = now;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { await Task.Delay(50); }
        }
    });
    return socket;
}

void CloseAll()
{
    foreach (var (socket, cts) in sockets.Values)
    {
        cts.Cancel();
        socket.Dispose();
    }
    sockets.Clear();
}

using var stats = new StreamWriter(statsPath, append: false) { AutoFlush = true };
stats.WriteLine("utc,mode,server,sent,back,lost_older_than_2s,max_gap_ms");

var mode = "quiet";
var lastRead = 0L;
var lastStats = Stopwatch.GetTimestamp();
var tick = 0;
while (true)
{
    if (Stopwatch.GetElapsedTime(lastRead).TotalMilliseconds >= 100)
    {
        lastRead = Stopwatch.GetTimestamp();
        try { mode = File.ReadAllText(control).Trim().ToLowerInvariant(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (mode == "exit") break;
    }

    void Send(string name)
    {
        var seq = next[name]++;
        var payload = new byte[60];
        BinaryPrimitives.WriteInt32BigEndian(payload, seq);
        sentAt[name][seq] = Stopwatch.GetTimestamp();
        try { Open(name).Send(payload); } catch (SocketException) { }
    }

    // 25 ms a tick: 40 packets a second for the match's server.
    if (mode == "kr")
    {
        Send("kr");
        if (tick % 8 == 0) Send("sg");
    }
    else if (mode == "sg")
    {
        Send("sg");
    }
    else if (sockets.Count > 0)
    {
        // Back in the lobby: the match's sockets close, and the next match opens new ones.
        await Task.Delay(1500);
        CloseAll();
    }
    tick++;

    if (Stopwatch.GetElapsedTime(lastStats).TotalSeconds >= 1)
    {
        lastStats = Stopwatch.GetTimestamp();
        var old = Stopwatch.GetTimestamp() - 2 * Stopwatch.Frequency;
        foreach (var name in servers.Keys)
        {
            var lost = sentAt[name].Count(kv => kv.Value < old && !back[name].ContainsKey(kv.Key));
            double gap;
            lock (gate)
            {
                // A silence still going on counts too, while this server is being sent to.
                var sending = mode == name || (mode == "kr" && name == "sg");
                if (sending && lastBack[name] != 0)
                {
                    maxGap[name] = Math.Max(maxGap[name], (Stopwatch.GetTimestamp() - lastBack[name]) * 1000.0 / Stopwatch.Frequency);
                }
                gap = maxGap[name];
                maxGap[name] = 0;
            }
            stats.WriteLine($"{DateTime.UtcNow:O},{mode},{name},{next[name]},{back[name].Count},{lost},{gap:F0}");
        }
    }
    await Task.Delay(25);
}

stop.Cancel();
CloseAll();
