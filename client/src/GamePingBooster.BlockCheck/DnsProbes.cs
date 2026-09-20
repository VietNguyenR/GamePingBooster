using GamePingBooster.Core.Net;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace GamePingBooster.BlockCheck;

/// <summary>One reply to one plaintext query - kept separate because a poisoned query often draws two.</summary>
internal sealed record PlainReply(int ElapsedMs, string From, int RCode, string[] Addresses, string[] CNames);

internal sealed record PlainResult(string Server, string? Error, PlainReply[] Replies)
{
    /// <summary>
    /// Two replies to one query, disagreeing, is injection caught in the act: an on-path device
    /// answered before the real resolver could and the real answer arrived behind it. No CDN and
    /// no anycast explains it - there was one question and one transaction id.
    /// </summary>
    public bool Contradicted =>
        Replies.Length > 1 &&
        Replies.Skip(1).Any(r => !r.Addresses.OrderBy(a => a).SequenceEqual(Replies[0].Addresses.OrderBy(a => a)));

    public string[] Addresses => Replies.Length == 0 ? [] : Replies[0].Addresses;
}

internal sealed record DohResult(string Resolver, int ElapsedMs, int RCode, string? Error, string[] Addresses, string[] CNames);

internal static class DnsProbes
{
    /// <summary>
    /// A plaintext query straight to one resolver, then a short wait for a SECOND reply.
    ///
    /// That wait is the point of the whole function. A normal exchange is one question and one
    /// answer, and every resolver library stops reading the moment the first one lands - which is
    /// exactly what an injecting middlebox relies on, because it only has to be faster than the
    /// real server, not to stop it. Keeping the socket open for another second turns the most
    /// common form of DNS blocking from an inference into a recording.
    /// </summary>
    public static async Task<PlainResult> QueryAsync(IPAddress server, string host, CancellationToken ct)
    {
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = DnsWire.BuildQuery(id, host);

        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        var replies = new List<PlainReply>();
        var buffer = new byte[4096];
        var clock = Stopwatch.StartNew();

        try
        {
            await socket.SendToAsync(query, SocketFlags.None, new IPEndPoint(server, 53), ct);

            // Two deadlines: the first answer gets two seconds, and once one arrives the socket
            // stays open a further 1.2 s for a contradicting one. 1.2 s covers a real answer
            // crossing a sea cable behind an injected answer made two hops away.
            var firstDeadline = TimeSpan.FromSeconds(2);
            var afterFirst = TimeSpan.FromMilliseconds(1200);

            while (true)
            {
                var budget = replies.Count == 0
                    ? firstDeadline - clock.Elapsed
                    : afterFirst - (clock.Elapsed - TimeSpan.FromMilliseconds(replies[0].ElapsedMs));
                if (budget <= TimeSpan.Zero) break;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(budget);

                SocketReceiveFromResult received;
                try
                {
                    received = await socket.ReceiveFromAsync(
                        buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break;
                }

                DnsMessage parsed;
                try { parsed = DnsWire.Parse(buffer, received.ReceivedBytes); }
                catch (FormatException) { continue; }

                if (parsed.Id != id) continue;   // not ours

                replies.Add(new PlainReply(
                    (int)clock.ElapsedMilliseconds,
                    received.RemoteEndPoint.ToString() ?? "?",
                    parsed.RCode,
                    [.. parsed.Addresses.Select(a => a.ToString())],
                    [.. parsed.CNames]));
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return new PlainResult(server.ToString(), Describe(ex), [.. replies]);
        }

        return replies.Count == 0
            ? new PlainResult(server.ToString(), "no reply within 2 s", [])
            : new PlainResult(server.ToString(), null, [.. replies]);
    }

    /// <summary>
    /// The same question over DoH, addressed to the resolver BY IP.
    ///
    /// By IP because the alternative bootstraps a censorship test on the thing being tested: asking
    /// the system resolver where cloudflare-dns.com lives hands the censor a second chance to lie.
    /// Cloudflare and Google both carry their own addresses in the certificate SAN, so an https URL
    /// with an IP literal validates normally and needs no DNS at all.
    /// </summary>
    public static async Task<DohResult> QueryDohAsync(HttpClient http, string resolverIp, string host, CancellationToken ct)
    {
        var query = DnsWire.BuildQuery(0, host);   // RFC 8484: id 0, so caches can be shared
        var clock = Stopwatch.StartNew();

        try
        {
            using var content = new ByteArrayContent(query);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{resolverIp}/dns-query")
            {
                Content = content,
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new DohResult(resolverIp, (int)clock.ElapsedMilliseconds, -1,
                    $"HTTP {(int)response.StatusCode}", [], []);
            }

            var body = await response.Content.ReadAsByteArrayAsync(ct);
            var parsed = DnsWire.Parse(body, body.Length);

            return parsed.RCode != 0
                ? new DohResult(resolverIp, (int)clock.ElapsedMilliseconds, parsed.RCode,
                    DnsWire.RCodeName(parsed.RCode), [], [])
                : new DohResult(resolverIp, (int)clock.ElapsedMilliseconds, 0, null,
                    [.. parsed.Addresses.Select(a => a.ToString())], [.. parsed.CNames]);
        }
        catch (Exception ex)
        {
            // DoH being unreachable is not a failed probe, it is a finding: the fix this tool
            // exists to justify would not work on this line either.
            return new DohResult(resolverIp, (int)clock.ElapsedMilliseconds, -1, Describe(ex), [], []);
        }
    }

    public static async Task<(string[] Addresses, string? Error)> SystemResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            return ([.. addresses.Select(a => a.ToString())], null);
        }
        catch (Exception ex)
        {
            return ([], Describe(ex));
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "timed out",
        SocketException socket => socket.SocketErrorCode.ToString(),
        HttpRequestException { InnerException: SocketException inner } => inner.SocketErrorCode.ToString(),
        _ => ex.GetType().Name + ": " + ex.Message,
    };
}
