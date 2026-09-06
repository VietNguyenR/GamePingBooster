using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace GamePingBooster.App.Services;

/// <summary>
/// Signs in through the person's own browser, over a loopback redirect (RFC 8252).
///
/// The app listens on 127.0.0.1, opens the default browser at the licence server's
/// <c>/app/authorize</c> page, and waits for the browser to come back to that port with a
/// one-time code. The code is then exchanged for the same refresh token the password form
/// produces, so everything downstream - licence token, set-token, profile sync - is untouched.
///
/// Two things make it worth doing. The app never sees the password, which matters for software
/// that installs a network driver and asks for administrator rights. And the device-limit
/// refusal moves onto a web page that can explain it, instead of a native dialog shown after
/// somebody has already typed their password.
///
/// <b>TcpListener, not HttpListener.</b> HttpListener goes through http.sys, which requires a URL
/// reservation (<c>netsh http add urlacl</c>) or an elevated process even for a loopback prefix -
/// and this UI is deliberately unprivileged, so it would fail with access denied on every
/// machine. One GET request parsed by hand is a fraction of the code and has no such requirement.
/// It also avoids the http.sys interop under Native AOT entirely.
/// </summary>
public sealed class LoopbackAuth
{
    /// <summary>
    /// How long to wait for the browser to come back before giving up.
    ///
    /// Generous, because the person may have to sign in, and may have to find the browser window
    /// behind the app. The server's own code expires in five minutes, so this is the shorter of
    /// the two by design - the app should give up before the code does, or it would present one
    /// that cannot work and report the server's refusal instead of "you did not finish".
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(4);

    public sealed record Request(string AuthorizeUrl, string RedirectUri, string Verifier, string State);

    private readonly TcpListener _listener;
    private readonly Request _request;

    private LoopbackAuth(TcpListener listener, Request request)
    {
        _listener = listener;
        _request = request;
    }

    public string AuthorizeUrl => _request.AuthorizeUrl;

    /// <summary>
    /// Claims a loopback port and builds the URL to send the browser to.
    ///
    /// Port 0 asks the OS for a free one, which is the only way to avoid both colliding with
    /// something else and needing a fixed port nobody can guarantee is free.
    /// </summary>
    public static LoopbackAuth Start(string licenceUrl, string deviceLabel)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirectUri = $"http://127.0.0.1:{port}/callback";

            // PKCE. The verifier never leaves this process until the exchange; only its hash goes
            // through the browser. Without it, anything that can see the redirect - a browser
            // extension, a proxy, the history file - could spend the code before we do.
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

            // Separate from PKCE and doing a different job: it proves the callback we accept
            // belongs to the request we started, rather than to another sign-in on this machine.
            var state = Base64Url(RandomNumberGenerator.GetBytes(16));

            var url = $"{licenceUrl.TrimEnd('/')}/app/authorize" +
                      $"?redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                      $"&challenge={Uri.EscapeDataString(challenge)}" +
                      $"&state={Uri.EscapeDataString(state)}" +
                      $"&label={Uri.EscapeDataString(deviceLabel)}";

            return new LoopbackAuth(listener, new Request(url, redirectUri, verifier, state));
        }
        catch
        {
            listener.Stop();
            throw;
        }
    }

    /// <summary>
    /// Opens the browser at the authorize page.
    ///
    /// <c>UseShellExecute</c> is what hands the URL to whatever the person has set as their
    /// default browser. Without it .NET tries to execute the string as a program and throws.
    /// </summary>
    public void OpenBrowser()
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _request.AuthorizeUrl,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Waits for the browser's callback and returns the one-time code.
    ///
    /// Anything arriving that is not the callback we are waiting for is answered and ignored,
    /// and the wait continues. A browser preconnecting, a stray probe, or a favicon request must
    /// not be able to end the sign-in - on this port, at this moment, that would be a denial of
    /// service anything on the machine could trigger by accident.
    /// </summary>
    public async Task<string> WaitForCodeAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);

        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
                var target = await ReadRequestTargetAsync(client, deadline.Token).ConfigureAwait(false);
                if (target is null)
                {
                    continue;
                }

                var query = ParseQuery(target);
                query.TryGetValue("error", out var error);
                query.TryGetValue("code", out var code);
                query.TryGetValue("state", out var state);

                if (error is not null)
                {
                    await RespondAsync(client, "Sign-in was refused", "You can close this tab and try again from the app.", deadline.Token).ConfigureAwait(false);
                    throw new InvalidOperationException($"The sign-in page reported: {error}");
                }

                if (code is null || state is null)
                {
                    // Not the callback. Answer politely so the browser is not left hanging, and
                    // keep waiting for the one we want.
                    await RespondAsync(client, "Waiting for sign-in", "Nothing to do here. Finish signing in on the other tab.", deadline.Token).ConfigureAwait(false);
                    continue;
                }

                // Fixed-time compare on a value an attacker would have to guess. The window is
                // tiny and the consequence small, but the cost of doing it properly is one call.
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(_request.State)))
                {
                    await RespondAsync(client, "That sign-in did not match", "Close this tab and start again from the app.", deadline.Token).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        "The sign-in that came back is not the one this app started. Nothing was changed. " +
                        "Try again, and if it keeps happening close any other copy of the app first.");
                }

                await RespondAsync(client, "Signed in", "You can close this tab and go back to Game Ping Booster.", deadline.Token).ConfigureAwait(false);
                return code;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No answer from the browser within {Timeout.TotalMinutes:F0} minutes. " +
                "If the browser did not open, use the email and password form instead.");
        }
    }

    /// <summary>
    /// The query string of a request target, decoded.
    ///
    /// Written out rather than calling HttpUtility.ParseQueryString, which lives in an assembly
    /// outside the shared framework. This app publishes with Native AOT, and a missing assembly
    /// there is a PUBLISH failure, not a compile failure - it would have built cleanly here and
    /// broken the installer build. Three parameters off a loopback callback do not justify that
    /// risk, and the parsing is eight lines.
    ///
    /// Last value wins on a duplicate key, which matters: a callback carrying `state` twice must
    /// not let an attacker append a value that a first-wins parser would ignore while the server
    /// read the other one.
    /// </summary>
    private static Dictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var mark = target.IndexOf('?');
        if (mark < 0) return result;

        foreach (var pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals < 0) continue;
            var name = Uri.UnescapeDataString(pair[..equals].Replace('+', ' '));
            result[name] = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }
        return result;
    }

    /// <summary>
    /// Reads the request line and returns its target, or null if this is not a GET we can use.
    ///
    /// Only the first line is read. The headers and body are of no interest, and reading to the
    /// end of a request that may have neither Content-Length nor a closed connection is a way to
    /// hang forever.
    /// </summary>
    private static async Task<string?> ReadRequestTargetAsync(TcpClient client, CancellationToken ct)
    {
        // A request line longer than this is not a browser callback. Capped so a socket that
        // sends bytes without a newline cannot grow this buffer without limit.
        const int MaxLine = 8 * 1024;

        var stream = client.GetStream();
        var buffer = new byte[MaxLine];
        var filled = 0;

        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (read == 0) break;
            filled += read;

            var end = buffer.AsSpan(0, filled).IndexOf((byte)'\n');
            if (end < 0) continue;

            var line = Encoding.ASCII.GetString(buffer, 0, end).TrimEnd('\r');
            var parts = line.Split(' ');
            return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
        }
        return null;
    }

    private static async Task RespondAsync(TcpClient client, string title, string detail, CancellationToken ct)
    {
        // Plain, self-contained HTML: this page renders on a machine that may have no network,
        // and a stylesheet or font it cannot fetch would leave it looking broken at the exact
        // moment the person is deciding whether the sign-in worked.
        var html =
            "<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            $"<title>{WebUtility.HtmlEncode(title)}</title></head>" +
            "<body style=\"font-family:system-ui,sans-serif;background:#0f172a;color:#e2e8f0;" +
            "display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">" +
            "<div style=\"text-align:center;max-width:32rem;padding:2rem\">" +
            $"<h1 style=\"font-size:1.5rem;margin:0 0 .5rem\">{WebUtility.HtmlEncode(title)}</h1>" +
            $"<p style=\"color:#94a3b8;margin:0\">{WebUtility.HtmlEncode(detail)}</p>" +
            "</div></body></html>";

        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n" +
            "Cache-Control: no-store\r\n\r\n");

        var stream = client.GetStream();
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public string Verifier => _request.Verifier;
    public string RedirectUri => _request.RedirectUri;

    public void Stop() => _listener.Stop();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
