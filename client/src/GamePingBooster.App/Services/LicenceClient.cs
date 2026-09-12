using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GamePingBooster.Core.Protocol;

namespace GamePingBooster.App.Services;

/// <summary>
/// The UI's side of the licence server: sign in, and exchange a refresh token for a licence
/// token. Two calls, and neither is on the latency path.
///
/// It lives in the UI rather than the service on purpose. Signing in is the user's act, with the
/// user's credentials, and the service runs as LocalSystem - putting a password prompt behind a
/// LocalSystem process would mean either a second UI or a wider pipe, and the pipe is a privilege
/// boundary this project keeps narrow. What crosses to the service is the finished token and
/// nothing else, write-only, via set-token.
///
/// Nothing here decides whether a token is any good. The relay does, offline, against the licence
/// server's public key. That is the whole design: a cracked UI gets somebody to a Connect button
/// that cannot connect.
/// </summary>
public sealed class LicenceClient : IDisposable
{
    /// <summary>
    /// How long a request may take, for every call except <see cref="ExchangeAsync"/>.
    ///
    /// Short: most of these have somebody looking at them, and a request that has not answered in
    /// ten seconds should say so rather than hang. The ones that run in the background retry on
    /// their own, so a short deadline costs them nothing.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long <see cref="ExchangeAsync"/> may take. Longer than the rest, because giving up on
    /// this one is far more expensive than on any other.
    ///
    /// The code it spends is single-use, and the server marks it spent while handling the request -
    /// whether or not anybody is still waiting for the answer. So a client that gives up at ten
    /// seconds on a server that finishes at twelve has thrown the code away, and there is no retry:
    /// the same request now gets "no longer valid". The person has to go through the browser
    /// again, and the refresh token the server issued is orphaned. Every other call can simply be
    /// made again.
    ///
    /// Thirty seconds because the production server was measured (2026-09-11) at up to 4.4 s for a
    /// single-query request rejected with a bogus code, and the real exchange runs several
    /// queries. It stays far inside the code's own five-minute life.
    /// </summary>
    public static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;

    public LicenceClient(string baseUrl)
    {
        // Connections race the host's addresses instead of trying them in turn. Without this, a line
        // whose IPv6 silently drops packets spends 22 s per IPv6 address before trying IPv4 - 44 s
        // against this server - while the browser on the same machine works. See HappyEyeballs.
        var handler = new SocketsHttpHandler { ConnectCallback = HappyEyeballs.ConnectCallback };

        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            // Off. Each call sets its own deadline in WithDeadline, because one number for every
            // call is wrong for the exchange - see ExchangeTimeout.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// How far this machine's clock ran ahead of the licence server's on the last call that got
    /// an answer - negative when it runs behind, null when it could not be measured.
    ///
    /// Worth measuring because a wrong clock breaks the tunnel in the one way nothing points at.
    /// The client signs the moment into every handshake and the relay refuses one further than
    /// <see cref="GpbProtocol.HandshakeSkew"/> from its own clock, silently, on every relay at
    /// once - which reads as "no relay answered, check the network" and sends whoever is helping
    /// to tcpdump. A customer on 2026-09-12 ran 537 s fast and cost a day of it.
    ///
    /// Measured here rather than anywhere else because this is the only conversation the app has
    /// with a machine whose clock is known to be right, and it already happens on every start.
    /// </summary>
    public TimeSpan? ClockSkew { get; private set; }

    /// <summary>
    /// The clock difference a response proves, as a LOWER BOUND, or null when the server sent no
    /// usable Date.
    ///
    /// A bound rather than a number, because the response leg is inside any naive subtraction and
    /// would show a slow link as a skewed clock. The server stamped Date at some instant between
    /// this machine sending and receiving, so a Date inside that window proves nothing is wrong
    /// however wide the window is - and a Date outside it is off by AT LEAST the distance to the
    /// nearer edge, whatever the latency was. That makes a warning built on this incapable of
    /// crying wolf over a slow connection, which is the property worth having: one false alarm
    /// about the clock and nobody reads the next one.
    ///
    /// Date has one-second resolution, so this is never accurate to better than a second. It does
    /// not need to be - what it guards is measured in minutes.
    /// </summary>
    internal static TimeSpan? SkewFrom(DateTimeOffset sentAt, DateTimeOffset receivedAt,
        DateTimeOffset? serverDate)
    {
        if (serverDate is not { } server) return null;
        if (server < sentAt) return sentAt - server;
        if (server > receivedAt) return receivedAt - server;
        return TimeSpan.Zero;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Runs one call under a deadline, and reports running out of time as a timeout.
    ///
    /// <b>That last part is the reason this exists.</b> HttpClient reports its own timeout as a
    /// <see cref="TaskCanceledException"/>, a subclass of <see cref="OperationCanceledException"/>,
    /// although nobody cancelled anything - and every caller reads OperationCanceledException as
    /// "the window closed". That shipped three times at once: the sign-in form said "Cancelled."
    /// after a sign-in that had worked in the browser, the account window stopped loading without
    /// a word, and one slow renewal ended TokenRefresher's loop for the life of the process.
    ///
    /// So the translation happens here, once, where it is known which token fired: the caller's
    /// means they gave up and is left alone; ours means the server was too slow and becomes
    /// <see cref="LicenceTimeoutException"/>. Callers keep their `when (ct.IsCancellationRequested)`
    /// filters as a second line, not the only one.
    /// </summary>
    private static async Task<T> WithDeadline<T>(TimeSpan timeout, CancellationToken ct,
        Func<CancellationToken, Task<T>> call)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            return await call(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new LicenceTimeoutException(timeout);
        }
    }

    /// <summary>
    /// Trades a browser sign-in's one-time code for this account's refresh token.
    ///
    /// The second half of the loopback flow - see LoopbackAuth. This is the ONLY way the app
    /// signs in: there is no password call here any more, and the app therefore has no code path
    /// that could handle a password even if something asked it to.
    ///
    /// The password endpoint this replaced, <c>/auth/login</c>, was deleted from the server on
    /// 2026-09-10. It had been kept for binaries already installed, and an audit found there were
    /// none: the form came out of this file four days before the only release ever tagged. What
    /// remained was the one route on the service that took a password over JSON with nothing in
    /// front of it. The name <see cref="LoginResult"/> is left alone - it is the shape this
    /// endpoint answers with, and renaming it now would be churn for its own sake.
    ///
    /// The verifier is the PKCE secret this process kept while only its hash travelled through
    /// the browser. The redirect URI is sent again so the server can check the code is being
    /// spent by whoever asked for it, and not by something that merely saw it go past.
    ///
    /// Runs under <see cref="ExchangeTimeout"/>, not the shorter default - see there for why.
    /// </summary>
    public Task<LoginResult> ExchangeAsync(string code, string verifier, string redirectUri,
        CancellationToken ct) =>
        WithDeadline(ExchangeTimeout, ct, async t =>
        {
            using var response = await _http.PostAsJsonAsync("auth/exchange",
                new ExchangeRequest { Code = code, Verifier = verifier, RedirectUri = redirectUri },
                LicenceJsonContext.Default.ExchangeRequest, t).ConfigureAwait(false);

            return await ReadAsync(response, LicenceJsonContext.Default.LoginResult, t).ConfigureAwait(false);
        });

    /// <summary>
    /// Exchanges the refresh token for a licence token bound to this machine's device key.
    ///
    /// The device public key has to go up: the token names it, and that is what makes a stolen
    /// token useless to anybody who does not also hold the private half.
    /// </summary>
    public Task<TokenResult> FetchTokenAsync(string refreshToken, string devicePublicKey,
        string deviceLabel, CancellationToken ct) =>
        WithDeadline(RequestTimeout, ct, async t =>
        {
            // Bracketed so the response leg cannot be mistaken for a skewed clock - see SkewFrom.
            var sentAt = DateTimeOffset.UtcNow;
            using var response = await _http.PostAsJsonAsync("auth/token",
                new TokenRequest
                {
                    RefreshToken = refreshToken,
                    DevicePublicKey = devicePublicKey,
                    DeviceLabel = deviceLabel,
                },
                LicenceJsonContext.Default.TokenRequest, t).ConfigureAwait(false);
            ClockSkew = SkewFrom(sentAt, DateTimeOffset.UtcNow, response.Headers.Date);

            return await ReadAsync(response, LicenceJsonContext.Default.TokenResult, t).ConfigureAwait(false);
        });

    /// <summary>
    /// Fetches the profile, SEALED to this machine's device key.
    ///
    /// What comes back is not readable here and is not meant to be: it is encrypted to the
    /// device key, which lives in the background service. This process passes it straight
    /// through. The ranges therefore never exist in the UI's memory, never cross the IPC pipe in
    /// readable form, and never reach the disk unsealed.
    /// </summary>
    public Task<string> FetchProfileAsync(string refreshToken, string devicePublicKey,
        string gameId, CancellationToken ct) =>
        WithDeadline(RequestTimeout, ct, async t =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"profile?game={Uri.EscapeDataString(gameId)}&device={Uri.EscapeDataString(devicePublicKey)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshToken);

            using var response = await _http.SendAsync(request, t).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content
                    .ReadFromJsonAsync(LicenceJsonContext.Default.SealedProfileResult, t)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body?.Envelope))
                {
                    throw new LicenceException("The licence server sent an empty game list.");
                }
                return body.Envelope;
            }

            throw await ErrorAsync(response, t, new()
            {
                [System.Net.HttpStatusCode.Unauthorized] = "Sign in again to update the game list.",
                [System.Net.HttpStatusCode.PaymentRequired] = "This account has no active subscription.",
                [System.Net.HttpStatusCode.TooManyRequests] = "Asked for the game list too often. It will update later.",
            }).ConfigureAwait(false);
        });

    /// <summary>The account, for the Account screen. Nothing here is a credential.</summary>
    public Task<AccountResult> FetchAccountAsync(string refreshToken, CancellationToken ct) =>
        WithDeadline(RequestTimeout, ct, async t =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "account");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshToken);

            using var response = await _http.SendAsync(request, t).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content
                    .ReadFromJsonAsync(LicenceJsonContext.Default.AccountResult, t)
                    .ConfigureAwait(false)
                    ?? throw new LicenceException("The licence server sent an empty answer.");
            }

            throw await ErrorAsync(response, t, new()
            {
                [System.Net.HttpStatusCode.Unauthorized] = "This sign-in has expired. Sign in again.",
            }).ConfigureAwait(false);
        });

    /// <summary>
    /// Ends the sign-in on the server.
    ///
    /// Best effort: signing out locally must succeed whether or not this does, because a person
    /// who wants their credentials off a machine should not be blocked by a network that is
    /// down. The credential is short-lived and revoking it is hygiene, not the mechanism.
    /// </summary>
    public Task LogoutAsync(string refreshToken, CancellationToken ct) =>
        WithDeadline(RequestTimeout, ct, async t =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "auth/logout");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshToken);
            using var response = await _http.SendAsync(request, t).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        });

    private static async Task<LicenceException> ErrorAsync(HttpResponseMessage response,
        CancellationToken ct, Dictionary<System.Net.HttpStatusCode, string> known)
    {
        string? serverMessage = null;
        try
        {
            var error = await response.Content
                .ReadFromJsonAsync(LicenceJsonContext.Default.ErrorResponse, ct).ConfigureAwait(false);
            serverMessage = error?.Error;
        }
        catch (Exception)
        {
            // Not JSON. Fall through to the status code.
        }

        if (serverMessage is not null) return new LicenceException(serverMessage, response.StatusCode);
        if (known.TryGetValue(response.StatusCode, out var message))
        {
            return new LicenceException(message, response.StatusCode);
        }
        return new LicenceException($"The licence server answered {(int)response.StatusCode}.",
            response.StatusCode);
    }

    /// <summary>
    /// Turns a response into either a result or an exception carrying a message worth showing.
    ///
    /// The server's own message is preferred over a status code, because the two failures that
    /// matter - an expired sign-in, and the device limit - are things the person can act on and a
    /// status code is not. Anything unrecognised falls back to something honest rather than
    /// inventing a cause.
    /// </summary>
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response,
        JsonTypeInfo<T> type, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var value = await response.Content.ReadFromJsonAsync(type, ct).ConfigureAwait(false);
            if (value is null) throw new LicenceException("The licence server sent an empty answer.");
            return value;
        }

        string? serverMessage = null;
        try
        {
            var error = await response.Content
                .ReadFromJsonAsync(LicenceJsonContext.Default.ErrorResponse, ct).ConfigureAwait(false);
            serverMessage = error?.Error;
        }
        catch (Exception)
        {
            // Not JSON, or not the shape expected. Fall through to the status code.
        }

        throw new LicenceException(serverMessage ?? response.StatusCode switch
        {
            // No password is ever sent from here, so a 401 can only mean the sign-in itself is no
            // longer accepted - an expired or revoked refresh token, or a one-time code already spent.
            System.Net.HttpStatusCode.Unauthorized => "That sign-in is no longer valid. Sign in again.",
            System.Net.HttpStatusCode.Forbidden => "This account is not allowed to add another device.",
            System.Net.HttpStatusCode.NotFound => "The licence server does not recognise this request. Check the address in settings.",
            _ => $"The licence server answered {(int)response.StatusCode}.",
        }, response.StatusCode);
    }
}

/// <summary>
/// A failure worth putting in front of the user, already worded for them.
///
/// It carries the status code as well as the sentence, because one caller has to tell apart two
/// refusals that read the same to a person: "the server said no this time", which is worth
/// retrying, and "this account has no subscription", which is not and which should drop the
/// licence rather than keep presenting it. Every other caller still reads only Message.
/// </summary>
public sealed class LicenceException(string message, System.Net.HttpStatusCode? status = null)
    : Exception(message)
{
    /// <summary>The HTTP status behind it, or null when the request never got an answer.</summary>
    public System.Net.HttpStatusCode? StatusCode { get; } = status;
}

/// <summary>
/// The licence server did not answer within the call's deadline.
///
/// A <see cref="TimeoutException"/>, deliberately NOT a <see cref="LicenceException"/>. A
/// LicenceException means the server answered and said no, and callers act on that: TokenRefresher
/// backs off for longer, and drops the licence outright on a 402. A timeout says nothing about the
/// licence - it is a network failure, and every caller already has a catch for those.
/// </summary>
public sealed class LicenceTimeoutException(TimeSpan waited)
    : TimeoutException($"The licence server did not answer within {waited.TotalSeconds:F0} seconds.")
{
    /// <summary>The deadline that ran out - 10 or 30 seconds depending on the call.</summary>
    public TimeSpan Waited { get; } = waited;
}

public sealed class ExchangeRequest
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("verifier")] public string Verifier { get; set; } = "";
    [JsonPropertyName("redirectUri")] public string RedirectUri { get; set; } = "";
}

public sealed class LoginResult
{
    [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = "";

    /// <summary>
    /// The account id, as a STRING.
    ///
    /// Not a number, and not only because the licence server uses cuids. A JSON number is a
    /// double, exact only to 2^53, so a 64-bit id cannot survive the trip - the development stub
    /// got away with declaring uint64 purely because its ids were 1, 2 and 3. The first real
    /// server returned a cuid and the client failed with "The JSON value could not be converted
    /// to System.UInt64".
    ///
    /// Nothing here reads it. It is carried so a support conversation can name an account.
    /// </summary>
    [JsonPropertyName("userId")] public string UserId { get; set; } = "";

    [JsonPropertyName("deviceLimit")] public int DeviceLimit { get; set; }
}

public sealed class TokenRequest
{
    [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("devicePublicKey")] public string DevicePublicKey { get; set; } = "";
    [JsonPropertyName("deviceLabel")] public string DeviceLabel { get; set; } = "";
}

public sealed class TokenResult
{
    /// <summary>The licence token as hex, 300 characters. Opaque here; the relay verifies it.</summary>
    [JsonPropertyName("token")] public string Token { get; set; } = "";

    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }

    /// <summary>A string, for the same reason as on LoginResult.</summary>
    [JsonPropertyName("userId")] public string UserId { get; set; } = "";
}

public sealed class SealedProfileResult
{
    [JsonPropertyName("profileVersion")] public int ProfileVersion { get; set; }

    /// <summary>The sealed envelope as hex. Opaque here - only the service can open it.</summary>
    [JsonPropertyName("envelope")] public string Envelope { get; set; } = "";
}

public sealed class AccountResult
{
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("plan")] public string? Plan { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }

    /// <summary>Unix seconds, or null when there is no subscription.</summary>
    [JsonPropertyName("expiresAt")] public long? ExpiresAt { get; set; }

    [JsonPropertyName("deviceCount")] public int DeviceCount { get; set; }
    [JsonPropertyName("deviceLimit")] public int DeviceLimit { get; set; }
}

public sealed class ErrorResponse
{
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>Source-generated JSON: the App is published with Native AOT, like the service.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ExchangeRequest))]
[JsonSerializable(typeof(LoginResult))]
[JsonSerializable(typeof(TokenRequest))]
[JsonSerializable(typeof(TokenResult))]
[JsonSerializable(typeof(SealedProfileResult))]
[JsonSerializable(typeof(AccountResult))]
[JsonSerializable(typeof(ErrorResponse))]
public partial class LicenceJsonContext : JsonSerializerContext;
