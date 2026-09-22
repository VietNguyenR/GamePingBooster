using System.Net;
using System.Net.Sockets;
using GamePingBooster.Core.Net;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// A DNS server on loopback that answers the handful of names the ISP lies about, and forwards
/// everything else to the resolver the machine was already using.
///
/// Which names those are is not this type's business - it asks the policy, which comes from the
/// profile the licence server delivers.
///
/// It listens on 127.0.0.53 rather than 127.0.0.1 so it cannot collide with whatever else a
/// developer or a player has bound to the obvious loopback address, and so the address in the
/// adapter's DNS list is recognisably ours when somebody reads it back later.
///
/// Two rules decide everything:
///
///   claimed name   asked over DoH, because the ISP's answer for it is a lie (127.0.0.1)
///   anything else  relayed to the ISP's own resolver, unread and unchanged
///
/// The second rule is not laziness, it is the design. The ISP's answers are BETTER for a content
/// CDN - they name caches inside the country that connect in 10 ms - and they are the only correct
/// answers for a captive portal, a corporate split-horizon name, or a router's own hostname. A
/// resolver that "helpfully" sent everything abroad would break all three while fixing nothing.
/// See <see cref="UnblockPolicy"/> for what is claimed and why.
///
/// Both transports are served. Windows falls back to TCP whenever a UDP answer comes back
/// truncated, and a resolver that only spoke UDP would look like an intermittent failure on
/// exactly the large answers a CDN produces.
/// </summary>
internal sealed class LocalResolver : IAsyncDisposable
{
    /// <summary>The address the policy points Windows at. Not 127.0.0.1: see the type's summary.</summary>
    public static readonly IPAddress ListenAddress = IPAddress.Parse("127.0.0.53");

    private const int Port = 53;

    private readonly IReadOnlyList<IPAddress> _upstream;
    private readonly UnblockPolicy _policy;
    private readonly DohUpstream _doh;
    private readonly WorkingEdges _edges;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stopping = new();

    private Socket? _udp;
    private Socket? _tcp;
    private Task? _udpLoop;
    private Task? _tcpLoop;

    private long _scoped;
    private long _forwarded;
    private long _failed;

    public LocalResolver(IReadOnlyList<IPAddress> upstream, UnblockPolicy policy, Action<string> log)
    {
        _upstream = upstream;
        _policy = policy;
        _log = log;
        _doh = new DohUpstream(log);
        _edges = new WorkingEdges(_doh, log);
    }

    public long ScopedQueries => Interlocked.Read(ref _scoped);

    /// <summary>Addresses dropped for failing a TLS handshake - see <see cref="WorkingEdges"/>.</summary>
    public long RejectedEdges => _edges.Rejected;
    public long ForwardedQueries => Interlocked.Read(ref _forwarded);
    public long FailedQueries => Interlocked.Read(ref _failed);

    /// <summary>
    /// Binds and starts serving. Throws if the port is taken, which the caller must treat as a
    /// refusal to enable the feature: pointing Windows at an address nothing answers on would take
    /// the machine's DNS down with it.
    /// </summary>
    public void Start()
    {
        if (_upstream.Count == 0)
        {
            throw new InvalidOperationException(
                "No upstream resolver was captured, so unclaimed names could not be answered.");
        }

        var endpoint = new IPEndPoint(ListenAddress, Port);

        _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            // A UDP socket on Windows reports an ICMP port-unreachable from a previous send as an
            // error on the NEXT receive, which kills the loop for a reason that has nothing to do
            // with the packet it was about to read. Off.
            const int SIO_UDP_CONNRESET = -1744830452;
            _udp.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);

            _udp.Bind(endpoint);
            _tcp.Bind(endpoint);
            _tcp.Listen(16);
        }
        catch (SocketException ex)
        {
            _udp.Dispose();
            _tcp.Dispose();
            _udp = null;
            _tcp = null;

            throw new InvalidOperationException(
                $"Could not listen on {endpoint} ({ex.SocketErrorCode}). Another DNS server is " +
                "probably bound to port 53 on this machine.", ex);
        }

        _udpLoop = Task.Run(() => ServeUdpAsync(_stopping.Token));
        _tcpLoop = Task.Run(() => ServeTcpAsync(_stopping.Token));

        _log($"Unblock resolver listening on {endpoint}, forwarding everything else to " +
             string.Join(", ", _upstream.Select(u => u.ToString())) + ".");
    }

    private async Task ServeUdpAsync(CancellationToken ct)
    {
        var socket = _udp!;
        var buffer = new byte[DnsWire.MaxUdpMessage];

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                _log($"Unblock resolver UDP receive failed: {ex.SocketErrorCode}");
                continue;
            }

            // Copied out before the next receive overwrites it, then handled off the accept path
            // so one slow upstream cannot stall every other query behind it.
            var query = buffer[..received.ReceivedBytes];
            var from = received.RemoteEndPoint;

            _ = Task.Run(async () =>
            {
                try
                {
                    var reply = await AnswerAsync(query, ct).ConfigureAwait(false);
                    await socket.SendToAsync(reply, SocketFlags.None, from, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (Exception ex)
                {
                    _log($"Unblock resolver failed to answer a UDP query: {ex.Message}");
                }
            }, ct);
        }
    }

    private async Task ServeTcpAsync(CancellationToken ct)
    {
        var listener = _tcp!;

        while (!ct.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                _log($"Unblock resolver TCP accept failed: {ex.SocketErrorCode}");
                continue;
            }

            _ = Task.Run(async () =>
            {
                using (connection)
                {
                    try
                    {
                        using var stream = new NetworkStream(connection, ownsSocket: false);

                        // DNS over TCP is length-prefixed, and a client may send more than one
                        // message down the same connection.
                        var header = new byte[2];
                        while (!ct.IsCancellationRequested)
                        {
                            if (!await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false)) break;

                            var length = (header[0] << 8) | header[1];
                            if (length is 0 or > 65535) break;

                            var query = new byte[length];
                            if (!await ReadExactlyAsync(stream, query, ct).ConfigureAwait(false)) break;

                            var reply = await AnswerAsync(query, ct).ConfigureAwait(false);

                            var framed = new byte[2 + reply.Length];
                            framed[0] = (byte)(reply.Length >> 8);
                            framed[1] = (byte)reply.Length;
                            reply.CopyTo(framed, 2);

                            await stream.WriteAsync(framed, ct).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _log($"Unblock resolver failed to answer a TCP query: {ex.Message}");
                    }
                }
            }, ct);
        }
    }

    /// <summary>
    /// The routing decision, and the only place it is made.
    ///
    /// A message whose question cannot be read is forwarded rather than refused. Being unable to
    /// classify something is not a reason to break it, and the ISP's resolver understands forms
    /// this code does not.
    /// </summary>
    private async Task<byte[]> AnswerAsync(byte[] query, CancellationToken ct)
    {
        var id = DnsWire.ReadId(query);

        if (DnsWire.TryReadQuestion(query, out var name, out var type) && _policy.ClaimedBy(name) is { } app)
        {
            // A records only go through the edge check. Everything else about these names - AAAA,
            // the HTTPS records browsers now ask for, anything invented later - is relayed as it
            // arrives, because the check has nothing to say about them and synthesising an answer
            // would mean dropping whatever the upstream knew that this code does not.
            if (type == DnsWire.TypeA)
            {
                // The service's canary travels with the question: it is the name proven to work on
                // this line, and the only thing worth asking when every address for THIS name is
                // filtered.
                var edges = await _edges.ForAsync(name, app.Canary, ct).ConfigureAwait(false);
                if (edges is not null)
                {
                    Interlocked.Increment(ref _scoped);
                    var built = DnsWire.BuildAnswer(query, edges, WorkingEdges.AnswerTtlSeconds);
                    DnsWire.WriteId(built, id);
                    return built;
                }

                // No address survived, or no upstream answered. Falls through to relaying the
                // upstream's own reply, which is what this did before the check existed.
            }

            var answer = await _doh.ResolveAsync(query, query.Length, ct).ConfigureAwait(false);
            if (answer is not null)
            {
                Interlocked.Increment(ref _scoped);
                DnsWire.WriteId(answer, id);
                return answer;
            }

            // Not forwarded to the ISP as a fallback, which would be the obvious thing to do and
            // would be wrong: the ISP's answer for these names is 127.0.0.1. A wrong answer with a
            // TTL is worse than a failure the client will retry, because it poisons the Windows
            // cache for as long as the lie says to keep it.
            Interlocked.Increment(ref _failed);
            _log($"No encrypted resolver could answer '{name}' - returning SERVFAIL rather than the ISP's answer.");
            return DnsWire.BuildFailure(query, 2);
        }

        var forwarded = await ForwardAsync(query, ct).ConfigureAwait(false);
        if (forwarded is not null)
        {
            Interlocked.Increment(ref _forwarded);
            return forwarded;
        }

        Interlocked.Increment(ref _failed);
        return DnsWire.BuildFailure(query, 2);
    }

    private async Task<byte[]?> ForwardAsync(byte[] query, CancellationToken ct)
    {
        foreach (var server in _upstream)
        {
            try
            {
                // The upstream's family, not IPv4: a machine handed only IPv6 resolvers by its
                // router is not a machine this feature may take DNS away from.
                using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                await socket.SendToAsync(query, SocketFlags.None, new IPEndPoint(server, Port), ct)
                    .ConfigureAwait(false);

                var buffer = new byte[DnsWire.MaxUdpMessage];

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));

                var received = await socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token)
                    .ConfigureAwait(false);

                if (received.ReceivedBytes >= DnsWire.HeaderLength)
                {
                    return buffer[..received.ReceivedBytes];
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _log($"Upstream resolver {server} did not answer within 2 s.");
            }
            catch (SocketException ex)
            {
                _log($"Upstream resolver {server} failed: {ex.SocketErrorCode}");
            }
        }

        return null;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        _udp?.Dispose();
        _tcp?.Dispose();

        foreach (var task in new[] { _udpLoop, _tcpLoop })
        {
            if (task is null) continue;
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception) { }
        }

        _doh.Dispose();
        _stopping.Dispose();
    }
}
