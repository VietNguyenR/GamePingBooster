using System.Net;
using System.Net.Security;
using System.Net.Sockets;

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

    public static async Task<bool> WorksAsync(IPAddress address, string sni, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Budget);

            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(address, Port), timeout.Token).ConfigureAwait(false);

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
            return errors == SslPolicyErrors.None;
        }
        catch (Exception)
        {
            // Every failure means the same thing here - do not hand this address out - and the
            // reasons are already understood: a reset from a filter, a timeout, a refused port.
            // The caller logs the count; the individual causes would be noise in a service log.
            return false;
        }
    }
}
