using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using GamePingBooster.Core.Net;
using GamePingBooster.Service.Dns;
using GamePingBooster.Service.Native;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The split (SplitProxy, SplitHello) and closing what rode a relay (TcpConnections), offline: a server on loopback
/// that behaves as FPT's filter did on 2026-10-06 - it reads the first TLS record of a connection from "the line"
/// (source 127.0.0.3) and resets it when the blocked name is in it - and serves TLS otherwise. Connections from any
/// other address are "the tunnel", which the filter never sees.
/// </summary>
internal static partial class Program
{
    private const string SplitBlocked = "prod-live-front.playbattlegrounds.com";
    private static readonly IPAddress LineAddress = IPAddress.Parse("127.0.0.3");
    private static readonly IPAddress EdgeAddress = IPAddress.Loopback;

    private static async Task TheSplitHelloPassesAFilterThatReadsTheFirstRecord()
    {
        using var certificate = SelfSigned();
        await using var fpt = new LineFilter(SplitBlocked, reassembles: false, certificate);
        var timeout = TimeSpan.FromSeconds(3);

        var (whole, _) = await EdgeProber.HandshakeAsync(EdgeAddress, SplitBlocked, timeout, timeout, CancellationToken.None, fpt.Port, LineAddress);
        Check($"over the line, the hello whole is cut ({whole})", whole == "reset", "the fake filter does not filter");

        var (split, _) = await EdgeProber.HandshakeAsync(EdgeAddress, SplitBlocked, timeout, timeout, CancellationToken.None, fpt.Port, LineAddress, split: true);
        Check($"over the line, the hello split in two records completes ({split})", split is "ok" or "cert-mismatch",
            "the split hello did not pass, or the server could not read it");

        await using var proxy = new SplitProxy(new DohUpstream(_ => { }), new SplitRoutes(), s => Log.Enqueue(s),
            listen: new IPEndPoint(IPAddress.Loopback, 0), edgePort: fpt.Port, source: _ => LineAddress)
        {
            SplitPasses = outcome => outcome is "ok" or "cert-mismatch",
        };
        var verdict = await proxy.JudgeAsync(SplitBlocked, [EdgeAddress], CancellationToken.None);
        Check("a name the line cuts whole and lets through split is judged for the split", verdict.Split, verdict.Why);
        SplitProxy.Verdict free;

        // VNPT: the line filters nothing, so a listed name goes over it whole - whatever the tunnel's round trip, and
        // without routing its edges into the tunnel to measure one (2026-10-07: that kept the cold lobby on the relay).
        var routes = new SplitRoutes();
        await using (var vnpt = new SplitProxy(new DohUpstream(_ => { }), routes, s => Log.Enqueue(s),
            listen: new IPEndPoint(IPAddress.Loopback, 0), edgePort: fpt.Port, source: _ => LineAddress)
        {
            SplitPasses = outcome => outcome is "ok" or "cert-mismatch",
        })
        {
            free = await vnpt.JudgeAsync("prod-live-images.playbattlegrounds.com", [EdgeAddress], CancellationToken.None);
            Check("a name the line lets through whole goes over the line whole", free.Split && free.Whole, free.Why);
            Check("and judging it routes nothing into the tunnel", routes.Names.IsEmpty, string.Join(", ", routes.Names));
        }

        // The game's side over the line whole: the filter never sees the blocked name, so 200.
        await using (var overLine = new SplitProxy(new DohUpstream(_ => { }), new SplitRoutes(), s => Log.Enqueue(s),
            new ConcurrentDictionary<string, SplitProxy.Verdict>(StringComparer.OrdinalIgnoreCase)
            {
                ["prod-live-images.playbattlegrounds.com"] = free,
            },
            new IPEndPoint(IPAddress.Loopback, 0), fpt.Port, _ => LineAddress))
        {
            overLine.Start();
            var wholeStatus = await GetThroughAsync(overLine.Endpoint, "prod-live-images.playbattlegrounds.com");
            Check($"a connection for it reaches the edge over the line whole ({wholeStatus})",
                wholeStatus.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && overLine.SplitConnections == 1 && overLine.FellBack == 0,
                $"line {overLine.SplitConnections}, fell back {overLine.FellBack}");
        }

        // The game's side: a TLS client to the proxy, the name in its hello, a request answered by the edge.
        proxy.Start();
        var status = await GetThroughAsync(proxy.Endpoint, SplitBlocked);
        Check($"a connection to the proxy reaches the edge over the line, split ({status})",
            status.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && proxy.SplitConnections == 1 && proxy.FellBack == 0,
            $"split {proxy.SplitConnections}, fell back {proxy.FellBack}");
        Check("the edge saw it from the line", fpt.FromLine > 0, "the proxy did not send from the adapter's address");
    }

    private static async Task AFilterThatReassemblesSendsTheConnectionThroughTheTunnel()
    {
        using var certificate = SelfSigned();
        await using var reassembling = new LineFilter(SplitBlocked, reassembles: true, certificate);
        var routes = new SplitRoutes();
        var verdicts = new ConcurrentDictionary<string, SplitProxy.Verdict>(StringComparer.OrdinalIgnoreCase)
        {
            [SplitBlocked] = new(true, [EdgeAddress], DateTimeOffset.UtcNow, "judged for the split a moment ago"),
        };
        await using var proxy = new SplitProxy(new DohUpstream(_ => { }), routes, s => Log.Enqueue(s), verdicts,
            new IPEndPoint(IPAddress.Loopback, 0), reassembling.Port, _ => LineAddress);
        proxy.Start();

        var status = await GetThroughAsync(proxy.Endpoint, SplitBlocked);
        Check($"the split is cut, and the game's connection still gets through - by the tunnel ({status})",
            status.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && proxy.FellBack == 1 && routes.Names.Contains(SplitBlocked),
            $"split {proxy.SplitConnections}, fell back {proxy.FellBack}, routed {string.Join(", ", routes.Names)}");
        Check("and the name goes back to the relay", !proxy.Answers(SplitBlocked) && !verdicts[SplitBlocked].Split,
            verdicts[SplitBlocked].Why);
    }

    private static async Task TheResolverAnswersAListedSplitNameWithTheProxy()
    {
        var memory = new EdgeMemory();
        memory.Split[SplitBlocked] = new SplitProxy.Verdict(true, [IPAddress.Parse("184.84.205.206")], DateTimeOffset.UtcNow, "test");
        memory.Split["store.steampowered.com"] = new SplitProxy.Verdict(true, [IPAddress.Parse("23.197.226.96")], DateTimeOffset.UtcNow, "test");
        var pubg = new UnblockApp("pubg", "PUBG", ["playbattlegrounds.com", "pubg.com"], [], SplitBlocked, [SplitBlocked]);
        var policy = new UnblockPolicy([pubg], "tunnelcheck");

        var resolver = new LocalResolver([IPAddress.Parse("8.8.8.8")], policy, s => Log.Enqueue(s), new SplitRoutes(), memory: memory);
        try
        {
            if (resolver.Split is not { } split || !split.Start())
            {
                Console.WriteLine($"  SKIP  {SplitProxy.ListenAddress}:443 is taken - a service with the split is running");
                return;
            }

            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var a = await resolver.AnswerAsync(DnsWire.BuildQuery(7, SplitBlocked), limit.Token);
            var parsed = DnsWire.Parse(a, a.Length);
            Check($"A for a listed name judged for the split is the proxy ({string.Join(", ", parsed.Addresses)})",
                parsed.Addresses.Count == 1 && SplitProxy.IsOurs(parsed.Addresses[0]), "answered with something else");

            var aaaa = await resolver.AnswerAsync(DnsWire.BuildQuery(8, SplitBlocked, DnsWire.TypeAaaa), limit.Token);
            var parsedAaaa = DnsWire.Parse(aaaa, aaaa.Length);
            Check("AAAA for it is empty, so IPv6 cannot go round the proxy", parsedAaaa.RCode == 0 && parsedAaaa.Addresses.Count == 0,
                $"rcode {parsedAaaa.RCode}, {parsedAaaa.Addresses.Count} address(es)");

        }
        finally
        {
            await resolver.DisposeAsync();
        }
    }

    /// <summary>
    /// VNPT 2026-10-07: accounts.pubg.com's warm-up /32 caught 13.227.185.127, a CloudFront HAN edge it shares with
    /// prod-live-cfentry, and the game's 12 MB from cfentry rode the relay. A name answered for the line drops such an
    /// address while it has others; with none left it is answered as is.
    /// </summary>
    private static void ALineAnswerLeavesOutTunnelledAddresses()
    {
        const string cfentry = "prod-live-cfentry.playbattlegrounds.com";
        var routed = IPAddress.Parse("13.227.185.127");
        IPAddress[] edges = [IPAddress.Parse("13.227.185.100"), routed, IPAddress.Parse("13.227.185.7")];
        var routes = new SplitRoutes { InTunnel = { routed } };
        var pubg = new UnblockApp("pubg", "PUBG", ["playbattlegrounds.com", "pubg.com"], [], SplitBlocked, [SplitBlocked]);
        var lines = new ConcurrentQueue<string>();
        var resolver = new LocalResolver([IPAddress.Parse("8.8.8.8")], new UnblockPolicy([pubg], "tunnelcheck"),
            lines.Enqueue, routes, split: false);

        var answered = resolver.KeepOffTheTunnel(cfentry, edges, routes);
        Check($"the routed edge is left out ({string.Join(", ", (IEnumerable<IPAddress>)answered)})",
            answered.Length == 2 && !answered.Contains(routed), "answered with the routed edge");
        Check("and that is said once", lines.Count(l => l.Contains("left out 13.227.185.127", StringComparison.Ordinal)) == 1 &&
            resolver.KeepOffTheTunnel(cfentry, edges, routes).Length == 2 &&
            lines.Count(l => l.Contains("left out", StringComparison.Ordinal)) == 1, string.Join(" | ", lines));

        IPAddress[] onlyRouted = [routed];
        var asIs = resolver.KeepOffTheTunnel(cfentry, onlyRouted, routes);
        Check("with every address routed, the answer is unchanged", asIs.SequenceEqual(onlyRouted),
            string.Join(", ", (IEnumerable<IPAddress>)asIs));
        Check("and that is said", lines.Any(l => l.Contains("every address for", StringComparison.Ordinal)), string.Join(" | ", lines));

        var none = new SplitRoutes();
        Check("with nothing routed, the answer is unchanged",
            resolver.KeepOffTheTunnel(cfentry, edges, none).SequenceEqual(edges), "something was left out");
        resolver.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// The other order, VNPT 2026-10-07 02:35: cfentry answered for the line with 13.227.185.100/.109/.127, then
    /// accounts.pubg.com's tunnel lookup asked to route .127 among its own. The guard routes accounts' other edges only.
    /// </summary>
    private static async Task ARouteAskedLaterLeavesTheLinesAddressesAlone()
    {
        const string cfentry = "prod-live-cfentry.playbattlegrounds.com";
        const string accounts = "accounts.pubg.com";
        IPAddress Ip(string last) => IPAddress.Parse($"13.227.185.{last}");
        var now = DateTimeOffset.UtcNow;
        var open = new HashSet<IPAddress>();
        var inner = new SplitRoutes();
        var lines = new ConcurrentQueue<string>();
        var guard = new LineGuard(inner, lines.Enqueue, () => open, () => now);
        string Routed() => inner.Routed.IsEmpty ? "" : string.Join(", ", (IEnumerable<IPAddress>)inner.Routed.Last());

        guard.AnsweredForLine(cfentry, [Ip("100"), Ip("109"), Ip("127")]);
        guard.Route(accounts, [Ip("40"), Ip("127"), Ip("54")]);
        Check($"another name's route leaves out the line's address ({Routed()})",
            inner.Routed.Last().SequenceEqual([Ip("40"), Ip("54")]), Routed());
        Check("and says so", lines.Any(l => l.Contains("13.227.185.127 for accounts.pubg.com is kept off the tunnel", StringComparison.Ordinal)),
            string.Join(" | ", lines));

        guard.Route(cfentry, [Ip("127")]);
        Check($"the same name, cut later, is still routed ({Routed()})", inner.Routed.Last().SequenceEqual([Ip("127")]), Routed());

        guard.Route("country-code.playbattlegrounds.com", [Ip("100"), Ip("109")]);
        Check($"with every address in use on the line, all are routed as before ({Routed()})",
            inner.Routed.Last().Length == 2 && lines.Any(l => l.Contains("every address for country-code", StringComparison.Ordinal)), Routed());

        now = now.Add(LineGuard.LineAnswerHeld).AddSeconds(1);
        guard.Route(accounts, [Ip("40"), Ip("127")]);
        Check($"after {LineGuard.LineAnswerHeld.TotalMinutes:0} min the address is free again ({Routed()})",
            inner.Routed.Last().Length == 2, Routed());

        open.Add(Ip("54"));
        guard.Route("acrt-pcprod.acs.pubg.com", [Ip("54"), Ip("60")]);
        Check($"an address with a connection open over the line is left out ({Routed()})",
            inner.Routed.Last().SequenceEqual([Ip("60")]), Routed());
        inner.InTunnel.Add(Ip("54"));
        guard.Route("acrt-pcprod.acs.pubg.com", [Ip("54"), Ip("60")]);
        Check($"but not when that connection already rides the tunnel ({Routed()})", inner.Routed.Last().Length == 2, Routed());

        // The table the guard reads: a connection on loopback is in it.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server = await listener.AcceptSocketAsync();
        Check("the open-connection table lists a live connection's remote address",
            TcpConnections.OpenRemotes().Contains(IPAddress.Loopback), "127.0.0.1 not found");
        listener.Stop();
    }

    private static async Task ConnectionsFromTheOldAddressAreFoundAndClosed()
    {
        var local = IPAddress.Parse("127.0.0.4");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Bind(new IPEndPoint(local, 0));
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port));
        using var server = await listener.AcceptSocketAsync();

        var riding = TcpConnections.From(local);
        Check($"the connection from {local} is found ({string.Join("; ", riding)})", riding.Count == 1,
            $"{riding.Count} found");

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.WriteLine("  SKIP  closing it needs Administrator - run TunnelCheck elevated to check that part");
            listener.Stop();
            return;
        }

        var (closed, _) = TcpConnections.Close(riding);
        var failed = false;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            failed = await client.ReceiveAsync(new byte[16], SocketFlags.None, limit.Token) == 0;
        }
        catch (SocketException) { failed = true; }
        catch (OperationCanceledException) { }
        Check("closed, the program's socket fails at once", closed == 1 && failed, $"closed {closed}, failed {failed}");
        listener.Stop();
    }

    private static void TheSplitHelloIsTwoRecordsOfTheSameHello()
    {
        // A real hello, as SslStream writes it, caught by a stream that only records.
        var hello = CaptureHello(SplitBlocked);
        var split = SplitHello.Split(hello);
        var first = (split[3] << 8) | split[4];
        var second = (split[5 + first + 3] << 8) | split[5 + first + 4];
        var joined = split.AsSpan(5, first).ToArray().Concat(split.AsSpan(10 + first, second).ToArray()).ToArray();
        var name = Encoding.ASCII.GetBytes(SplitBlocked);
        Check("two handshake records whose bytes are the hello's, and the name in neither whole",
            split[0] == 0x16 && split[5 + first] == 0x16 && 10 + first + second == split.Length &&
            joined.AsSpan().SequenceEqual(hello.AsSpan(5)) &&
            split.AsSpan(0, 5 + first).IndexOf(name) < 0 && split.AsSpan(5 + first).IndexOf(name) < 0,
            $"hello {hello.Length} B -> records of {first} and {second} B");
        Check("the name is read back from the hello", SplitHello.NameOf(hello) == SplitBlocked, SplitHello.NameOf(hello) ?? "null");
    }

    private static byte[] CaptureHello(string name)
    {
        var capture = new CaptureStream();
        using var tls = new SslStream(capture, leaveInnerStreamOpen: true, (_, _, _, _) => true);
        try { tls.AuthenticateAsClient(name); } catch (Exception) { }
        return capture.First ?? [];
    }

    /// <summary>Keeps the first write and ends the read side - all a handshake needs to write its hello.</summary>
    private sealed class CaptureStream : Stream
    {
        public byte[]? First { get; private set; }
        public override void Write(byte[] buffer, int offset, int count) => First ??= buffer.AsSpan(offset, count).ToArray();
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>A TLS client for <paramref name="name"/> to <paramref name="at"/>: the status line of HEAD /, or what went wrong.</summary>
    private static async Task<string> GetThroughAsync(IPEndPoint at, string name)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(at, limit.Token);
            using var tls = new SslStream(new NetworkStream(socket, ownsSocket: false), false, (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = name }, limit.Token);
            await tls.WriteAsync(Encoding.ASCII.GetBytes($"HEAD / HTTP/1.1\r\nHost: {name}\r\n\r\n"), limit.Token);
            var buffer = new byte[256];
            var read = await tls.ReadAsync(buffer, limit.Token);
            var text = Encoding.ASCII.GetString(buffer, 0, read);
            return text.Split("\r\n")[0];
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private sealed class SplitRoutes : IUnblockRoutes
    {
        public ConcurrentBag<string> Names { get; } = [];
        public HashSet<IPAddress> InTunnel { get; } = [];
        public bool Ready => true;
        public ConcurrentQueue<IPAddress[]> Routed { get; } = [];
        public bool Route(string name, IReadOnlyList<IPAddress> addresses)
        {
            Names.Add(name);
            Routed.Enqueue([.. addresses]);
            return true;
        }
        public bool Tunnelled(IPAddress address) => InTunnel.Contains(address);
    }

    /// <summary>
    /// FPT's filter, as measured: it reads the first TLS record of a connection from the line and resets it when the
    /// blocked name is in it. <c>reassembles</c>: the filter an ISP may build next - a hello in more than one record is
    /// reset too. Everything else is served as a TLS site answering 200.
    /// </summary>
    private sealed class LineFilter : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _loop;
        private readonly byte[] _blocked;
        private readonly bool _reassembles;
        private readonly X509Certificate2 _certificate;
        private int _fromLine;

        public LineFilter(string blocked, bool reassembles, X509Certificate2 certificate)
        {
            _blocked = Encoding.ASCII.GetBytes(blocked);
            _reassembles = reassembles;
            _certificate = certificate;
            _listener.Start();
            _loop = ServeAsync(_listener, HandleAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int FromLine => Volatile.Read(ref _fromLine);

        private async Task HandleAsync(Socket client)
        {
            var header = new byte[5];
            if (!await ReadAsync(client, header)) return;
            var record = new byte[5 + ((header[3] << 8) | header[4])];
            header.CopyTo(record, 0);
            if (!await ReadAsync(client, record.AsMemory(5))) return;

            if (((IPEndPoint)client.RemoteEndPoint!).Address.Equals(LineAddress))
            {
                Interlocked.Increment(ref _fromLine);
                var named = record.AsSpan().IndexOf(_blocked) >= 0;
                var handshakeLength = (record[6] << 16) | (record[7] << 8) | record[8];
                var partial = handshakeLength + 4 > record.Length - 5;
                if (named || (_reassembles && partial))
                {
                    client.LingerState = new LingerOption(true, 0);
                    client.Close();
                    return;
                }
            }

            using var tls = new SslStream(new Replay(record, new NetworkStream(client, ownsSocket: false)));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate });
            var buffer = new byte[1024];
            await tls.ReadAsync(buffer);
            await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
            await tls.FlushAsync();
        }

        private static async Task<bool> ReadAsync(Socket socket, Memory<byte> buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await socket.ReceiveAsync(buffer[read..], SocketFlags.None);
                if (n == 0) return false;
                read += n;
            }
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _loop;
        }
    }

    /// <summary>Reads <c>prefix</c> first, then the stream - what the filter already took off the wire.</summary>
    private sealed class Replay(byte[] prefix, Stream inner) : Stream
    {
        private int _at;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_at >= prefix.Length) return inner.Read(buffer);
            var n = Math.Min(buffer.Length, prefix.Length - _at);
            prefix.AsSpan(_at, n).CopyTo(buffer);
            _at += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_at >= prefix.Length) return await inner.ReadAsync(buffer, ct);
            return Read(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
