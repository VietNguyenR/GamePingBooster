using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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
    private readonly HttpClient _http;

    public LicenceClient(string baseUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            // Short: this is a sign-in box somebody is looking at, not a background job. A
            // request that has not answered in ten seconds should say so rather than hang.
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Signs in. Throws <see cref="LicenceException"/> with a message fit to show.</summary>
    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync("auth/login",
            new LoginRequest { Email = email, Password = password },
            LicenceJsonContext.Default.LoginRequest, ct).ConfigureAwait(false);

        return await ReadAsync(response, LicenceJsonContext.Default.LoginResult, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Trades a browser sign-in's one-time code for the same credential LoginAsync returns.
    ///
    /// The second half of the loopback flow - see LoopbackAuth. The answer is deliberately the
    /// same <see cref="LoginResult"/>, so everything after sign-in is one code path regardless of
    /// which way the person signed in.
    ///
    /// The verifier is the PKCE secret this process kept while only its hash travelled through
    /// the browser. The redirect URI is sent again so the server can check the code is being
    /// spent by whoever asked for it, and not by something that merely saw it go past.
    /// </summary>
    public async Task<LoginResult> ExchangeAsync(string code, string verifier, string redirectUri,
        CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync("auth/exchange",
            new ExchangeRequest { Code = code, Verifier = verifier, RedirectUri = redirectUri },
            LicenceJsonContext.Default.ExchangeRequest, ct).ConfigureAwait(false);

        return await ReadAsync(response, LicenceJsonContext.Default.LoginResult, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Exchanges the refresh token for a licence token bound to this machine's device key.
    ///
    /// The device public key has to go up: the token names it, and that is what makes a stolen
    /// token useless to anybody who does not also hold the private half.
    /// </summary>
    public async Task<TokenResult> FetchTokenAsync(string refreshToken, string devicePublicKey,
        string deviceLabel, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync("auth/token",
            new TokenRequest
            {
                RefreshToken = refreshToken,
                DevicePublicKey = devicePublicKey,
                DeviceLabel = deviceLabel,
            },
            LicenceJsonContext.Default.TokenRequest, ct).ConfigureAwait(false);

        return await ReadAsync(response, LicenceJsonContext.Default.TokenResult, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches the profile, SEALED to this machine's device key.
    ///
    /// What comes back is not readable here and is not meant to be: it is encrypted to the
    /// device key, which lives in the background service. This process passes it straight
    /// through. The ranges therefore never exist in the UI's memory, never cross the IPC pipe in
    /// readable form, and never reach the disk unsealed.
    /// </summary>
    public async Task<string> FetchProfileAsync(string refreshToken, string devicePublicKey,
        string gameId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"profile?game={Uri.EscapeDataString(gameId)}&device={Uri.EscapeDataString(devicePublicKey)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshToken);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content
                .ReadFromJsonAsync(LicenceJsonContext.Default.SealedProfileResult, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.Envelope))
            {
                throw new LicenceException("The licence server sent an empty game list.");
            }
            return body.Envelope;
        }

        throw await ErrorAsync(response, ct, new()
        {
            [System.Net.HttpStatusCode.Unauthorized] = "Sign in again to update the game list.",
            [System.Net.HttpStatusCode.PaymentRequired] = "This account has no active subscription.",
            [System.Net.HttpStatusCode.TooManyRequests] = "Asked for the game list too often. It will update later.",
        }).ConfigureAwait(false);
    }

    /// <summary>The account, for the Account screen. Nothing here is a credential.</summary>
    public async Task<AccountResult> FetchAccountAsync(string refreshToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "account");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshToken);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content
                .ReadFromJsonAsync(LicenceJsonContext.Default.AccountResult, ct)
                .ConfigureAwait(false)
                ?? throw new LicenceException("The licence server sent an empty answer.");
        }

        throw await ErrorAsync(response, ct, new()
        {
            [System.Net.HttpStatusCode.Unauthorized] = "This sign-in has expired. Sign in again.",
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the sign-in on the server.
    ///
    /// Best effort: signing out locally must succeed whether or not this does, because a person
    /// who wants their credentials off a machine should not be blocked by a network that is
    /// down. The credential is short-lived and revoking it is hygiene, not the mechanism.
    /// </summary>
    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshToken);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        _ = response.IsSuccessStatusCode;
    }

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
    /// matter - wrong password, and the device limit - are things the person can act on and a
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
            System.Net.HttpStatusCode.Unauthorized => "That email and password do not match an account.",
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

public sealed class LoginRequest
{
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
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
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(ExchangeRequest))]
[JsonSerializable(typeof(LoginResult))]
[JsonSerializable(typeof(TokenRequest))]
[JsonSerializable(typeof(TokenResult))]
[JsonSerializable(typeof(SealedProfileResult))]
[JsonSerializable(typeof(AccountResult))]
[JsonSerializable(typeof(ErrorResponse))]
public partial class LicenceJsonContext : JsonSerializerContext;
