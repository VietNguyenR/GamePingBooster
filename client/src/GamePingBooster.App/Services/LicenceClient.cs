using System.Net.Http;
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
        });
    }
}

/// <summary>A failure worth putting in front of the user, already worded for them.</summary>
public sealed class LicenceException(string message) : Exception(message);

public sealed class LoginRequest
{
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
}

public sealed class LoginResult
{
    [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("userId")] public ulong UserId { get; set; }
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
    [JsonPropertyName("userId")] public ulong UserId { get; set; }
}

public sealed class ErrorResponse
{
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>Source-generated JSON: the App is published with Native AOT, like the service.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResult))]
[JsonSerializable(typeof(TokenRequest))]
[JsonSerializable(typeof(TokenResult))]
[JsonSerializable(typeof(ErrorResponse))]
public partial class LicenceJsonContext : JsonSerializerContext;
