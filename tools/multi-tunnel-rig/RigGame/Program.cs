using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

// The multi-tunnel rig's stand-in game. Sends numbered UDP to the rig's two game servers from connected sockets, as a
// game does, reads the echoes back, and writes one line a second: what was sent, what came back, and the longest
// silence - so the driver can say whether a move lost anything.
//
//     RigGame.exe <control-file> <stats-file> [tcp-server]
//
// The control file holds one word, re-read every 100 ms: quiet (nothing - the lobby), kr (a match in kr at 40/s, and
// sg at 5/s beside it), sg (a match in sg at 40/s), tcp (a match over ONE TCP connection to sg port 20000 at 40/s, as
// Aniimo plays), tcp-idle (that connection kept open, nothing sent - a player standing still), exit. The TCP connection
// is opened the first time it is needed and kept through tcp and tcp-idle; any other word closes it. When it breaks -
// reset, closed, a send refused - the break is counted and the next send opens a new one, as a game reconnects.
// tcp-server is where that connection goes, 198.51.100.10 when not given: the rig's line server L for a game whose
// connection is opened over the player's own line before the booster's routes go in.

var control = args.Length > 0 ? args[0] : "riggame-control.txt";
var statsPath = args.Length > 1 ? args[1] : "riggame-stats.csv";

var servers = new Dictionary<string, IPEndPoint>
{
    ["sg"] = new(IPAddress.Parse("198.51.100.10"), 27015),
    ["kr"] = new(IPAddress.Parse("198.51.100.20"), 27015),
};
var tcpServer = new IPEndPoint(IPAddress.Parse(args.Length > 2 ? args[2] : "198.51.100.10"), 20000);
string[] names = [.. servers.Keys, "tcp"];
// Opened when a match starts and closed when it ends, as a game does: a UDP socket connected before the booster's
// routes went in keeps the way out it was connected on, and nothing it sends ever reaches the tunnel.
var sockets = new Dictionary<string, (Socket Socket, CancellationTokenSource Stop)>();
var sentAt = names.ToDictionary(k => k, _ => new ConcurrentDictionary<int, long>());
var back = names.ToDictionary(k => k, _ => new ConcurrentDictionary<int, long>());
var next = names.ToDictionary(k => k, _ => 0);
var lastBack = names.ToDictionary(k => k, _ => 0L);
var maxGap = names.ToDictionary(k => k, _ => 0.0);
// The TCP connection and how many times one has broken; both under gate.
Socket? tcp = null;
var tcpBreaks = 0;
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
                Echoed(name, BinaryPrimitives.ReadInt32BigEndian(buffer));
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { await Task.Delay(50); }
        }
    });
    return socket;
}

void Echoed(string name, int seq)
{
    var now = Stopwatch.GetTimestamp();
    back[name].TryAdd(seq, now);
    lock (gate)
    {
        if (lastBack[name] != 0) maxGap[name] = Math.Max(maxGap[name], (now - lastBack[name]) * 1000.0 / Stopwatch.Frequency);
        lastBack[name] = now;
    }
}

// Ends the connection; counted as a break unless it is being closed on purpose.
void DropTcp(Socket socket, bool broke)
{
    lock (gate)
    {
        if (!ReferenceEquals(tcp, socket)) return;
        tcp = null;
        if (broke) tcpBreaks++;
    }
    socket.Dispose();
}

Socket? OpenTcp()
{
    lock (gate) if (tcp is not null) return tcp;
    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
    try
    {
        socket.Connect(tcpServer);
    }
    catch (SocketException)
    {
        socket.Dispose();
        lock (gate) tcpBreaks++;
        return null;
    }
    lock (gate)
    {
        tcp = socket;
        lastBack["tcp"] = 0;
    }
    _ = Task.Run(async () =>
    {
        // The echo comes back as a byte stream: whole 60-byte frames, read one at a time.
        var frame = new byte[60];
        try
        {
            while (true)
            {
                var have = 0;
                while (have < frame.Length)
                {
                    var n = await socket.ReceiveAsync(frame.AsMemory(have), SocketFlags.None, stop.Token);
                    if (n == 0) throw new SocketException((int)SocketError.ConnectionReset);
                    have += n;
                }
                Echoed("tcp", BinaryPrimitives.ReadInt32BigEndian(frame));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { DropTcp(socket, broke: true); }
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
stats.WriteLine("utc,mode,server,sent,back,lost_older_than_2s,max_gap_ms,tcp_breaks");

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

    void SendTcp()
    {
        if (OpenTcp() is not { } socket) return;
        var seq = next["tcp"]++;
        var payload = new byte[60];
        BinaryPrimitives.WriteInt32BigEndian(payload, seq);
        sentAt["tcp"][seq] = Stopwatch.GetTimestamp();
        try { socket.Send(payload); }
        catch (SocketException) { DropTcp(socket, broke: true); }
        catch (ObjectDisposedException) { }
    }

    if (mode is not ("tcp" or "tcp-idle"))
    {
        Socket? open;
        lock (gate) open = tcp;
        if (open is not null) DropTcp(open, broke: false);
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
    else if (mode == "tcp")
    {
        SendTcp();
    }
    else if (mode == "tcp-idle")
    {
        // The connection stays open and silent, as a logged-in player standing in town.
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
        foreach (var name in names)
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
            int breaks;
            lock (gate) breaks = tcpBreaks;
            stats.WriteLine($"{DateTime.UtcNow:O},{mode},{name},{next[name]},{back[name].Count},{lost},{gap:F0},{(name == "tcp" ? breaks : 0)}");
        }
    }
    await Task.Delay(25);
}

stop.Cancel();
CloseAll();
Socket? last;
lock (gate) last = tcp;
if (last is not null) DropTcp(last, broke: false);
