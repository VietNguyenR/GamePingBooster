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
    /// Two operators, tried in order. Two rather than one because the whole feature dies with its
    /// upstream, and two who are unlikely to be blocked on the same day are cheap insurance.
    /// </summary>
    private static readonly string[] Resolvers = ["https://1.1.1.1/dns-query", "https://8.8.8.8/dns-query"];

    private readonly HttpClient _http;
    private readonly Action<string> _log;

    /// <summary>
    /// Which resolver answered last, so the second one is not re-tried from the top every query
    /// once the first has gone quiet. Reset by a failure, not by a timer: a resolver that starts
    /// working again will be found the next time the current one fails.
    /// </summary>
    private int _preferred;

    private long _served;
    private long _failed;

    public DohUpstream(Action<string> log)
    {
        _log = log;
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),

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
    /// </summary>
    public async Task<byte[]?> ResolveAsync(byte[] query, int length, CancellationToken ct)
    {
        var wire = new byte[length];
        Array.Copy(query, wire, length);
        DnsWire.WriteId(wire, 0);

        var start = _preferred;

        for (var attempt = 0; attempt < Resolvers.Length; attempt++)
        {
            var index = (start + attempt) % Resolvers.Length;
            var resolver = Resolvers[index];
            var clock = Stopwatch.StartNew();

            try
            {
                using var content = new ByteArrayContent(wire);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");

                using var request = new HttpRequestMessage(HttpMethod.Post, resolver) { Content = content };
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));

                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _log($"DoH {resolver} answered HTTP {(int)response.StatusCode}.");
                    continue;
                }

                var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (body.Length < DnsWire.HeaderLength)
                {
                    _log($"DoH {resolver} returned {body.Length} bytes, shorter than a DNS header.");
                    continue;
                }

                if (index != _preferred)
                {
                    _log($"DoH now using {resolver} ({clock.ElapsedMilliseconds} ms).");
                    _preferred = index;
                }

                Interlocked.Increment(ref _served);
                return body;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"DoH {resolver} failed after {clock.ElapsedMilliseconds} ms: {ex.Message}");
            }
        }

        Interlocked.Increment(ref _failed);
        return null;
    }

    public void Dispose() => _http.Dispose();
}
