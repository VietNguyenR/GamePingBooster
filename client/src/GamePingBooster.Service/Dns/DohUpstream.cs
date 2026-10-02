using System.Diagnostics;
using System.Net.Http.Headers;
using GamePingBooster.Core.Net;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// Asks an encrypted resolver, over HTTPS, addressed BY IP.
///
/// By IP and never by name. Resolving the resolver's own hostname would hand the censor the first
/// move - the bootstrap query goes to the ISP, in plaintext, and is exactly as forgeable as the
/// query it exists to protect. Cloudflare and Google both carry their addresses in the certificate
/// SAN, so an https URL with an IP literal validates normally and needs no DNS at all.
///
/// The query is relayed as the client sent it, byte for byte, and the reply is relayed back the
/// same way. RFC 8484 asks for a transaction id of zero so that caches can be shared; that is done
/// on a copy, and <see cref="LocalResolver"/> puts the client's id back on the reply. Nothing else
/// in the message is touched, so record types this code has never heard of pass through it intact.
/// </summary>
internal sealed class DohUpstream : IDisposable
{
    /// <summary>
    /// Two operators, asked together. Two rather than one because the whole feature dies with its
    /// upstream, and two who are unlikely to be blocked on the same day are cheap insurance.
    /// </summary>
    private static readonly string[] Resolvers = ["https://1.1.1.1/dns-query", "https://8.8.8.8/dns-query"];

    /// <summary>
    /// How long one resolver gets for one query before it is given up on.
    ///
    /// Below the HttpClient's own five seconds on purpose, and the reason is a measurement. On an FPT line on
    /// 2026-10-02 the service's own self-test - one query, ten seconds to answer - failed every minute for half an
    /// hour, on 0.3.7 and on the build after it alike, and so the policy never went on and the player kept the
    /// ISP's 127.0.0.1. The query that timed out was one this class RELAYS (every edge of the name was reset by
    /// the line, so there was nothing to synthesise from), and the relay then asked the two resolvers one after
    /// the other with five seconds each: one connection that hangs instead of failing, and the answer arrives at
    /// ten seconds - the moment the self-test stops listening. Asked together, with three seconds each, one
    /// hanging resolver costs nothing while the other answers.
    /// </summary>
    private static readonly TimeSpan PerQuery = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly Action<string> _log;

    private long _served;
    private long _failed;

    public DohUpstream(Action<string> log)
    {
        _log = log;
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),

            // One warm connection per resolver, kept open. A DoH query on a cold TCP+TLS
            // connection costs the handshake as well - about 200 ms on the line this was measured
            // on, against 33 ms warm - and that difference is the whole user-visible cost of the
            // feature.
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 4,
        })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    public long Served => Interlocked.Read(ref _served);
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>
    /// Relays one query. Returns the reply exactly as the resolver produced it, or null when no
    /// resolver could be reached - which the caller turns into SERVFAIL rather than silence.
    ///
    /// Both resolvers at once, and the first usable reply wins; the other is cancelled. See
    /// <see cref="PerQuery"/> for why not one after the other.
    /// </summary>
    public async Task<byte[]?> ResolveAsync(byte[] query, int length, CancellationToken ct)
    {
        var wire = new byte[length];
        Array.Copy(query, wire, length);
        DnsWire.WriteId(wire, 0);

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = Resolvers.Select(r => AskAsync(r, wire, race.Token)).ToList();

        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);

            if (await done.ConfigureAwait(false) is { } body)
            {
                race.Cancel();
                Interlocked.Increment(ref _served);
                return body;
            }
        }

        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _failed);
        return null;
    }

    /// <summary>
    /// After the first resolver has answered, how long the others still get. Both normally answer within
    /// a few dozen milliseconds of each other; one that has not after this is hanging, and waiting it out
    /// cost every first lookup on that line the whole of <see cref="PerQuery"/> (measured 3055 ms).
    /// </summary>
    private static readonly TimeSpan OthersAfterFirst = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The same question to every resolver, every answer kept. Used where the answers are compared rather than
    /// relayed - the edge check, which wants all the addresses either operator names. A resolver still silent
    /// <see cref="OthersAfterFirst"/> after another has answered is left out rather than waited for.
    /// </summary>
    public async Task<IReadOnlyList<byte[]>> ResolveEverywhereAsync(byte[] query, CancellationToken ct)
    {
        var wire = (byte[])query.Clone();
        DnsWire.WriteId(wire, 0);

        using var round = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var asks = Resolvers.Select(r => AskAsync(r, wire, round.Token)).ToList();

        // Until one has a reply - or all have given up - then a short grace for the rest.
        var pending = asks.ToList();
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            if (await done.ConfigureAwait(false) is not null)
            {
                if (pending.Count > 0)
                {
                    await Task.WhenAny(Task.WhenAll(pending), Task.Delay(OthersAfterFirst, ct)).ConfigureAwait(false);
                }
                break;
            }
        }

        var replies = asks.Where(a => a.IsCompletedSuccessfully).Select(a => a.Result).ToList();
        for (var i = 0; i < asks.Count; i++)
        {
            if (!asks[i].IsCompleted && !ct.IsCancellationRequested)
            {
                _log($"Unblock: DoH {Resolvers[i]} was still silent {OthersAfterFirst.TotalMilliseconds:0} ms after another " +
                     "resolver answered - left out of this lookup.");
            }
        }
        round.Cancel();
        await Task.WhenAll(asks).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        return [.. replies.Where(r => r is not null).Select(r => r!)];
    }

    /// <summary>
    /// One resolver, one query, at most <see cref="PerQuery"/>. Null when it failed, said so in the log with the
    /// time it took - prefixed "Unblock:" like everything else the feature says, so a filtered copy of the log
    /// (tools\Check-Unblock.ps1) still carries it. It did not on 2026-10-02, and the line that would have said
    /// where ten seconds went was the one left out.
    /// </summary>
    private async Task<byte[]?> AskAsync(string resolver, byte[] wire, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(PerQuery);

        try
        {
            using var content = new ByteArrayContent(wire);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");

            using var request = new HttpRequestMessage(HttpMethod.Post, resolver) { Content = content };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));

            using var response = await _http.SendAsync(request, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log($"Unblock: DoH {resolver} answered HTTP {(int)response.StatusCode} after {clock.ElapsedMilliseconds} ms.");
                return null;
            }

            var body = await response.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false);
            if (body.Length < DnsWire.HeaderLength)
            {
                _log($"Unblock: DoH {resolver} returned {body.Length} bytes, shorter than a DNS header.");
                return null;
            }
            return body;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up, or another resolver already answered - not this one's failure, nothing to say.
            return null;
        }
        catch (OperationCanceledException)
        {
            _log($"Unblock: DoH {resolver} did not answer within {PerQuery.TotalSeconds:0} s.");
            return null;
        }
        catch (Exception ex)
        {
            _log($"Unblock: DoH {resolver} failed after {clock.ElapsedMilliseconds} ms: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
