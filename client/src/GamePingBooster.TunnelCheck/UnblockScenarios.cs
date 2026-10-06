using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GamePingBooster.Core.Net;
using GamePingBooster.Service.Dns;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The unblock resolver's edge checks:
///
///     dotnet run --project client/src/GamePingBooster.TunnelCheck -- unblock
///
/// Mostly live, so not part of the default run: it depends on what Akamai serves and on what the line in front of
/// this machine cuts today. A check whose precondition the line does not meet says SKIP, not PASS.
///
///   0. Offline: "cut by name" is told from "not TLS at all" by the control handshake, against two servers on
///      loopback - one that resets a handshake naming the blocked name (FPT's shape), one that speaks no TLS
///      (Steam's p2p discovery names). Only the first may ever be sent through the tunnel unasked.
///   1. A borrowed edge must serve the name, not only hold its certificate. prod-live-front's Akamai edge holds
///      *.playbattlegrounds.com and answers prod-live-cfentry with 400 - what PUBG got in Ho Chi Minh City on
///      2026-10-02 and reported as "cannot connect".
///   2. Asked with EDNS Client Subnet as a Viettel network in Ho Chi Minh City, CloudFront names prod-live-images'
///      edges in Vietnam - what WorkingEdges tries when every edge the line was given is cut (FPT HCM was given
///      Hong Kong and Singapore, and cut them all; HAN51 answered it in 0.25 s).
///   3. The tunnel only where the line cuts the name, listed in the profile's tunnel list or not. A listed name the
///      line lets through stays off it (prod-live-images rode the relay for every player before 0.3.8); a name the
///      line cuts on every address goes through it (FPT did for prod-live-cfentry; Viettel cuts per address, so it
///      rarely does). Needs 127.0.0.53:53 free - the service must not be running.
/// </summary>
internal static partial class Program
{
    private static int UnblockMain(string[] args)
    {
        Console.WriteLine("Cut by name, told from not TLS at all (offline):");
        CutByNameIsToldFromNoTls().GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine("Borrowed edges are asked for the name:");
        BorrowedEdgeMustServeTheName().GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine("Asked as another Vietnamese network, a CDN names its edges in the country:");
        AskedAsAnotherNetworkTheCdnNamesItsLocalEdges().GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine("A listed name goes through the tunnel on every line:");
        ACutNameGoesThroughTheTunnelUnasked("prod-live-front.playbattlegrounds.com").GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine("An unlisted name, only where the line cuts it:");
        foreach (var name in args.Length > 0 ? args : ["prod-live-cfentry.playbattlegrounds.com", "store.steampowered.com"])
        {
            if (ACutNameGoesThroughTheTunnelUnasked(name).GetAwaiter().GetResult()) break;
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "All unblock checks passed." : $"{_failures} unblock check(s) FAILED.");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task CutByNameIsToldFromNoTls()
    {
        const string blocked = "store.steampowered.com";
        var timeout = TimeSpan.FromSeconds(2);
        using var certificate = SelfSigned();

        // FPT's shape: the hello naming the blocked name is answered with a reset; any other name completes, with a
        // certificate that is not valid for it - as a real edge's is not for the control name.
        using var filterStop = new CancellationTokenSource();
        var filter = new TcpListener(IPAddress.Loopback, 0);
        filter.Start();
        var filterLoop = ServeAsync(filter, async client =>
        {
            using var tls = new SslStream(new NetworkStream(client, ownsSocket: false));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateSelectionCallback = (_, sni) =>
                {
                    if (sni == blocked)
                    {
                        client.LingerState = new LingerOption(true, 0);
                        client.Close();
                    }
                    return certificate;
                },
            });
        });

        // Steam's p2p discovery shape: takes the connection on 443 and answers in a protocol that is not TLS.
        var plain = new TcpListener(IPAddress.Loopback, 0);
        plain.Start();
        var plainLoop = ServeAsync(plain, async client =>
        {
            var buffer = new byte[512];
            await client.ReceiveAsync(buffer, SocketFlags.None);
            await client.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n"), SocketFlags.None);
        });

        var filterPort = ((IPEndPoint)filter.LocalEndpoint).Port;
        var plainPort = ((IPEndPoint)plain.LocalEndpoint).Port;

        var (real, _) = await EdgeProber.HandshakeAsync(IPAddress.Loopback, blocked, timeout, timeout, CancellationToken.None, filterPort);
        var (control, _) = await EdgeProber.HandshakeAsync(IPAddress.Loopback, EdgeProber.ControlName, timeout, timeout, CancellationToken.None, filterPort);
        Check($"a reset for the name and an answer for the control is a cut (real {real}, control {control})",
            EdgeProber.CutByName(real, control), "the filter was not recognised");

        (real, _) = await EdgeProber.HandshakeAsync(IPAddress.Loopback, blocked, timeout, timeout, CancellationToken.None, plainPort);
        (control, _) = await EdgeProber.HandshakeAsync(IPAddress.Loopback, EdgeProber.ControlName, timeout, timeout, CancellationToken.None, plainPort);
        Check($"a server that speaks no TLS is not a cut (real {real}, control {control})",
            !EdgeProber.CutByName(real, control), "it would be sent through the tunnel for nothing");

        filter.Stop();
        plain.Stop();
        await Task.WhenAll(filterLoop, plainLoop);
    }

    private static async Task ServeAsync(TcpListener listener, Func<Socket, Task> handle)
    {
        while (true)
        {
            Socket client;
            try { client = await listener.AcceptSocketAsync(); }
            catch (Exception) { return; }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try { await handle(client); }
                    catch (Exception) { }
                }
            });
        }
    }

    /// <summary>A certificate for a name nobody asks for, with its key usable by SslStream on Windows.</summary>
    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=tunnelcheck.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    private static async Task BorrowedEdgeMustServeTheName()
    {
        const string front = "prod-live-front.playbattlegrounds.com";
        const string cfentry = "prod-live-cfentry.playbattlegrounds.com";

        // front's edges differ by resolver (Tencent from one, Akamai from another), and only Akamai's hold the
        // wildcard. Every resolver's answer, then the two Akamai edges the HCM player was handed.
        var doh = new DohUpstream(_ => { });
        var candidates = new List<IPAddress>();
        foreach (var reply in await doh.ResolveEverywhereAsync(DnsWire.BuildQuery(1, front), CancellationToken.None))
        {
            candidates.AddRange(DnsWire.Parse(reply, reply.Length).Addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork));
        }
        candidates.AddRange([IPAddress.Parse("23.66.150.216"), IPAddress.Parse("104.68.37.139")]);

        IPAddress? akamai = null;
        foreach (var candidate in candidates.Distinct())
        {
            if (await EdgeProber.WorksAsync(candidate, front, CancellationToken.None, servesName: true) &&
                await EdgeProber.WorksAsync(candidate, cfentry, CancellationToken.None))
            {
                akamai = candidate;
                break;
            }
        }
        if (akamai is null)
        {
            Console.WriteLine($"  SKIP  no edge of {front} holds a certificate for {cfentry} from this line - nothing to borrow");
            return;
        }
        Check($"{front}'s edge {akamai} serves it, and holds a certificate for {cfentry}", true);

        var served = await EdgeProber.WorksAsync(akamai, cfentry, CancellationToken.None, servesName: true);
        Check($"{akamai} is refused for {cfentry} all the same (it answers 400)", !served,
            "the edge was accepted for a name it does not serve");
    }

    private static async Task AskedAsAnotherNetworkTheCdnNamesItsLocalEdges()
    {
        const string images = "prod-live-images.playbattlegrounds.com";
        var doh = new DohUpstream(_ => { });

        var plain = new HashSet<IPAddress>();
        foreach (var reply in await doh.ResolveEverywhereAsync(DnsWire.BuildQuery(1, images), CancellationToken.None))
        {
            plain.UnionWith(DnsWire.Parse(reply, reply.Length).Addresses);
        }

        var asViettelHcm = new List<IPAddress>();
        foreach (var reply in await doh.ResolveAsSubnetsAsync(images, [(IPAddress.Parse("27.64.0.0"), 24)], CancellationToken.None))
        {
            asViettelHcm.AddRange(DnsWire.Parse(reply, reply.Length).Addresses);
        }
        if (asViettelHcm.Count == 0)
        {
            Console.WriteLine("  SKIP  Google did not answer the subnet query");
            return;
        }

        Check($"asked as 27.64.0.0/24, {images} is {string.Join(", ", asViettelHcm)} - not only what this line is told " +
              $"({string.Join(", ", plain)})", asViettelHcm.Any(a => !plain.Contains(a)),
            "the Client Subnet option made no difference - it is not reaching the CDN");

        var works = false;
        foreach (var address in asViettelHcm.Take(2))
        {
            works |= await EdgeProber.WorksAsync(address, images, CancellationToken.None);
        }
        Check("and those edges complete a handshake for it", works, "none of them did from this line");
    }

    /// <returns>True when the line did cut the name, so the check ran.</returns>
    private static async Task<bool> ACutNameGoesThroughTheTunnelUnasked(string name)
    {
        var lines = new ConcurrentQueue<string>();
        var routes = new RecordingRoutes();
        // The tunnel list prod carries on 2026-10-03.
        var pubg = new UnblockApp("pubg", "PUBG", ["playbattlegrounds.com", "pubg.com"], [], "prod-live-front.playbattlegrounds.com",
            ["prod-live-front.playbattlegrounds.com", "prod-live-xenuine.playbattlegrounds.com", "acrt-pcprod.acs.pubg.com", "accounts.pubg.com"]);
        var steam = new UnblockApp("steam", "Steam", ["steampowered.com", "steamcommunity.com"], [], "steamcommunity.com");
        var policy = new UnblockPolicy([pubg, steam], "tunnelcheck");

        // Asked straight, not over 127.0.0.53:53 - a running service holds that port, and the answer is the same code.
        var resolver = new LocalResolver([IPAddress.Parse("8.8.8.8")], policy, lines.Enqueue, routes);
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await resolver.AnswerAsync(DnsWire.BuildQuery(7, name), limit.Token);
        }
        finally
        {
            await resolver.DisposeAsync();
        }

        foreach (var line in lines) Console.WriteLine($"        {line}");

        if (pubg.RoutesThroughTunnel(name))
        {
            Check($"{name} is listed, so it was handed to the tunnel without asking the line", routes.Names.Contains(name),
                "a listed name was answered from the line");
            return true;
        }

        if (!lines.Any(l => l.Contains($"the line cuts {name} by name", StringComparison.Ordinal)))
        {
            Check($"{name} is not listed and this line lets it through, so it stays off the tunnel",
                routes.Names.IsEmpty, $"routed for {string.Join(", ", routes.Names.Distinct())}");
            return false;
        }

        // The recording routes change nothing, so the probe through "the tunnel" fails like the line's did: what is
        // checked is that the resolver went there once the cut was confirmed.
        Check($"{name} was handed to the tunnel after the line was seen cutting it", routes.Names.Contains(name),
            "the resolver relayed the cut answer without trying the tunnel");
        return true;
    }

    private sealed class RecordingRoutes : IUnblockRoutes
    {
        public ConcurrentBag<string> Names { get; } = [];

        public bool Ready => true;

        public bool Route(string name, IReadOnlyList<IPAddress> addresses)
        {
            Names.Add(name);
            return true;
        }

        public bool Tunnelled(IPAddress address) => false;
    }
}
