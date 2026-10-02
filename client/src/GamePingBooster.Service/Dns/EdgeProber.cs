using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// Asks one question about one address: does a TLS handshake for THIS name complete on it?
///
/// Measured on a VNPT line on 2026-09-22, three names against one Akamai edge:
///
///     23.15.142.182  SNI store.steampowered.com  -> reset after 19.2 s
///     23.15.142.182  SNI steamcommunity.com      -> reset after 19.2 s
///     23.15.142.182  SNI www.microsoft.com       -> handshake in 0.33 s
///     23.15.140.216  SNI store / community       -> handshake in 0.09 s
///
/// So the filtering is neither the name alone nor the address alone - it is the pair. Some edges
/// sit behind a middlebox that reads the server name out of the TLS hello and kills Steam's; others
/// do not. Which one a player gets is pure luck of what the upstream resolver returned, and that is
/// the whole difference between "Steam is instant" and "the store takes twenty seconds".
///
/// Hence this: the resolver hands out addresses it has just watched complete a handshake, rather
/// than whichever one an upstream happened to name first.
/// </summary>
internal static class EdgeProber
{
    private const int Port = 443;

    /// <summary>
    /// Two and a half seconds, and the number is the point.
    ///
    /// A working edge answers in about 90 ms. A filtered one takes 19 seconds to send the reset -
    /// the middlebox lets the connection hang first - so anything generous enough to "be fair" to
    /// it would cost every player twenty seconds on a cache miss. Waiting for that verdict is
    /// pointless: an edge that has not finished a handshake in 2.5 s is not one to hand out.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(2500);

    /// <summary>The name a handshake is compared against when asking whether the line cuts by name. Nobody filters it.</summary>
    public const string ControlName = "www.microsoft.com";

    /// <param name="connected">
    /// Called once the TCP connection is taken, before the handshake. WorkingEdges tells "this address
    /// is a web front that failed the handshake" (filtering) from "nothing here answers on 443" (not a
    /// web front at all) by it.
    /// </param>
    /// <param name="servesName">
    /// Also ask the edge for the name over HTTP, and refuse it when it says it does not serve it - see
    /// <see cref="ServesNameAsync"/>. For edges borrowed from another name, where a valid certificate proves
    /// much less than it seems to.
    /// </param>
    public static async Task<bool> WorksAsync(IPAddress address, string sni, CancellationToken ct, Action? connected = null,
        bool servesName = false)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Budget);

            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(address, Port), timeout.Token).ConfigureAwait(false);
            connected?.Invoke();

            var errors = SslPolicyErrors.None;

            using var network = new NetworkStream(socket, ownsSocket: false);
            using var tls = new SslStream(network, leaveInnerStreamOpen: true, (_, _, _, policyErrors) =>
            {
                // Recorded, then judged below. Rejecting here would collapse "a forged certificate
                // arrived" into the same exception as "nothing arrived", and those say different
                // things about the line.
                errors = policyErrors;
                return true;
            });

            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = sni,
            }, timeout.Token).ConfigureAwait(false);

            // A completed handshake is not enough. The certificate has to be valid for the name
            // asked about, or this address is not serving that site and handing it out would swap
            // a slow failure for a browser full of certificate warnings.
            if (errors != SslPolicyErrors.None) return false;

            return !servesName || await ServesNameAsync(tls, sni, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Every failure means the same thing here - do not hand this address out - and the
            // reasons are already understood: a reset from a filter, a timeout, a refused port.
            // The caller logs the count; the individual causes would be noise in a service log.
            return false;
        }
    }
    /// <summary>
    /// Whether the edge, asked for the name over the handshake just made, answers as a host of it.
    ///
    /// A certificate valid for the name is not that. On 2026-10-02 an FPT line in Ho Chi Minh City reset every
    /// CloudFront address of prod-live-cfentry and prod-live-images, so the resolver borrowed prod-live-front's
    /// Akamai edge: its *.playbattlegrounds.com certificate passed for both, and Akamai answered every request for
    /// them with 400 Bad Request - it holds the certificate, not the site. PUBG said it could not connect.
    ///
    /// So: HEAD / for the name, and refuse 400 and 421, the two ways an edge says "not a host of mine" (Akamai's
    /// Invalid URL, the standard Misdirected Request). Nothing else is judged. Every claimed name measured that day
    /// answered its own edge with 200, 301, 302 or 404 - an API with nothing at / is still the right server - and
    /// steamcommunity.com's Akamai edge answered 200 for the store, which is the borrow WorkingEdges exists for.
    /// No status line at all counts as a refusal: an edge that will not speak HTTP for the name is not proven.
    /// </summary>
    private static async Task<bool> ServesNameAsync(SslStream tls, string name, CancellationToken ct)
    {
        var request = Encoding.ASCII.GetBytes(
            $"HEAD / HTTP/1.1\r\nHost: {name}\r\nUser-Agent: GamePingBooster\r\nAccept: */*\r\nConnection: close\r\n\r\n");
        await tls.WriteAsync(request, ct).ConfigureAwait(false);
        await tls.FlushAsync(ct).ConfigureAwait(false);

        // "HTTP/1.1 400" is all that is needed, and it arrives in the first record.
        var buffer = new byte[64];
        var read = 0;
        while (read < 12)
        {
            var n = await tls.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }

        var line = Encoding.ASCII.GetString(buffer, 0, read);
        if (read < 12 || !line.StartsWith("HTTP/1.", StringComparison.Ordinal) ||
            !int.TryParse(line.AsSpan(9, 3), out var status))
        {
            return false;
        }

        return status is not (400 or 421);
    }

    /// <summary>
    /// One TLS handshake, said as what happened: ok, cert-mismatch (it completed, for another name), reset,
    /// stall, no-tcp, refused or tls-error - with how long it took, because a reset before the server could have
    /// answered is a filter, not the server.
    /// </summary>
    /// <param name="port">443 always, except for TunnelCheck's servers on loopback.</param>
    public static async Task<(string Outcome, long Ms)> HandshakeAsync(IPAddress address, string sni,
        TimeSpan connectTimeout, TimeSpan handshakeTimeout, CancellationToken ct, int port = Port)
    {
        var clock = Stopwatch.StartNew();
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(connectTimeout);
                await socket.ConnectAsync(new IPEndPoint(address, port), connect.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ("no-tcp", clock.ElapsedMilliseconds);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return ("refused", clock.ElapsedMilliseconds);
        }
        catch (SocketException)
        {
            return ("no-tcp", clock.ElapsedMilliseconds);
        }

        var errors = SslPolicyErrors.None;
        try
        {
            using var network = new NetworkStream(socket, ownsSocket: false);
            using var tls = new SslStream(network, leaveInnerStreamOpen: true, (_, _, _, e) =>
            {
                errors = e;
                return true;
            });
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(handshakeTimeout);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = sni }, handshake.Token)
                .ConfigureAwait(false);
            return (errors == SslPolicyErrors.None ? "ok" : "cert-mismatch", clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ("stall", clock.ElapsedMilliseconds);
        }
        catch (AuthenticationException) when (errors == SslPolicyErrors.None)
        {
            return ("tls-error", clock.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException)
        {
            return ("reset", clock.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// A handshake for the real name cut, while one for another name on the same address gets an answer from the
    /// server - completed, completed for the wrong name, or refused by the server with a TLS alert (Akamai answers
    /// a name it does not serve that way, measured 2026-10-02). Any of those means the path is open and the line is
    /// cutting this name.
    /// </summary>
    public static bool CutByName(string real, string control) =>
        real is "reset" or "stall" && control is "ok" or "cert-mismatch" or "tls-error";
}
