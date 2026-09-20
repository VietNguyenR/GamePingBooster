using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace GamePingBooster.BlockCheck;

/// <summary>How a handshake ended. The kind is what the verdict reads; the message is for a human.</summary>
internal enum TlsOutcome
{
    Ok,
    /// <summary>No SYN-ACK. Either the address is a black hole or it was never a real address.</summary>
    ConnectTimedOut,
    ConnectRefused,
    /// <summary>The connection was reset part way through the handshake - the signature of SNI filtering.</summary>
    ResetDuringHandshake,
    HandshakeTimedOut,
    /// <summary>A certificate arrived that does not belong to this name: something is terminating TLS in the middle.</summary>
    CertificateNotValid,
    Failed,
}

internal sealed record TlsResult(
    string Address,
    string Sni,
    TlsOutcome Outcome,
    int ConnectMs,
    int HandshakeMs,
    string? CertificateSubject,
    string? CertificateIssuer,
    string? PolicyErrors,
    string? Detail)
{
    /// <summary>
    /// The only definition of "this address really serves this site" that a censor cannot fake.
    ///
    /// It is the certificate, not the reply, that settles it. A hijacked answer can point at a web
    /// server that returns 200 and a page; it cannot produce a chain that a trusted CA signed for
    /// steamcommunity.com. So every verdict in this tool rests on this property and none of them
    /// rest on what the content looked like.
    /// </summary>
    public bool Authentic => Outcome == TlsOutcome.Ok && PolicyErrors == null;
}

internal static class TlsProbes
{
    private const int Port = 443;

    public static async Task<TlsResult> ProbeAsync(IPAddress address, string sni, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var connectMs = 0;

        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(new IPEndPoint(address, Port), timeout.Token);
            }

            connectMs = (int)clock.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(address, sni, TlsOutcome.ConnectTimedOut, (int)clock.ElapsedMilliseconds, "no SYN-ACK within 5 s");
        }
        catch (SocketException ex)
        {
            var outcome = ex.SocketErrorCode == SocketError.ConnectionRefused
                ? TlsOutcome.ConnectRefused
                : TlsOutcome.Failed;
            return Fail(address, sni, outcome, (int)clock.ElapsedMilliseconds, ex.SocketErrorCode.ToString());
        }

        string? subject = null;
        string? issuer = null;
        var policyErrors = SslPolicyErrors.None;

        using var network = new NetworkStream(socket, ownsSocket: false);
        using var tls = new SslStream(network, leaveInnerStreamOpen: true, (_, certificate, _, errors) =>
        {
            // Accepted whatever it is, then judged afterwards. Letting .NET reject the chain would
            // collapse "a forged certificate arrived" and "no certificate arrived" into one
            // exception, and those two say completely different things about the line.
            //
            // Read to strings here rather than kept as a certificate: the object handed to this
            // callback is only valid for the length of the call, and reading it afterwards throws
            // about an invalid handle - which looks exactly like a network failure in the report.
            if (certificate is not null)
            {
                subject = certificate.Subject;
                issuer = certificate.Issuer;
            }

            policyErrors = errors;
            return true;
        });

        var handshakeStart = clock.ElapsedMilliseconds;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                // Empty means no SNI extension on the wire, which is how the no-SNI control works.
                TargetHost = sni,
            }, timeout.Token);
        }
        catch (Exception ex)
        {
            var handshakeMs = (int)(clock.ElapsedMilliseconds - handshakeStart);
            var outcome = Classify(ex);
            return new TlsResult(address.ToString(), SniLabel(sni), outcome, connectMs, handshakeMs,
                subject, issuer, Describe(policyErrors), Describe(ex));
        }

        var elapsed = (int)(clock.ElapsedMilliseconds - handshakeStart);
        var errorsText = Describe(policyErrors);

        return new TlsResult(
            address.ToString(),
            SniLabel(sni),
            errorsText == null ? TlsOutcome.Ok : TlsOutcome.CertificateNotValid,
            connectMs,
            elapsed,
            subject,
            issuer,
            errorsText,
            null);
    }

    private static TlsOutcome Classify(Exception ex) => ex switch
    {
        OperationCanceledException => TlsOutcome.HandshakeTimedOut,
        IOException { InnerException: SocketException { SocketErrorCode: SocketError.ConnectionReset } }
            => TlsOutcome.ResetDuringHandshake,
        SocketException { SocketErrorCode: SocketError.ConnectionReset } => TlsOutcome.ResetDuringHandshake,
        AuthenticationException => TlsOutcome.CertificateNotValid,
        _ => TlsOutcome.Failed,
    };

    private static TlsResult Fail(IPAddress address, string sni, TlsOutcome outcome, int connectMs, string detail) =>
        new(address.ToString(), SniLabel(sni), outcome, connectMs, 0, null, null, null, detail);

    private static string SniLabel(string sni) => sni.Length == 0 ? "(none)" : sni;

    private static string? Describe(SslPolicyErrors errors) =>
        errors == SslPolicyErrors.None ? null : errors.ToString();

    private static string Describe(Exception ex) =>
        ex.InnerException is null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})";

    public static string Describe(TlsOutcome outcome) => outcome switch
    {
        TlsOutcome.Ok => "TLS ok",
        TlsOutcome.ConnectTimedOut => "no SYN-ACK",
        TlsOutcome.ConnectRefused => "refused",
        TlsOutcome.ResetDuringHandshake => "reset mid-handshake",
        TlsOutcome.HandshakeTimedOut => "handshake stalled",
        TlsOutcome.CertificateNotValid => "wrong certificate",
        _ => "failed",
    };
}
