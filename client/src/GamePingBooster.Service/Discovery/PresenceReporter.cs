using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using GamePingBooster.Core.Net;

namespace GamePingBooster.Service.Discovery;

/// <summary>
/// Tells the licence server which game is running on this machine while it is connected, so /admin/relays
/// can show what each connected player is playing - the relay reports who is connected, and only this
/// machine knows which game the connection is for.
///
/// One small signed POST to /presence: when the game changes (including to none, at disconnect), and every
/// <see cref="Every"/> while one runs, so the page can tell a player still playing from a machine that went
/// quiet. It carries a game id and the time, nothing else - no address, no relay, no measurement.
///
/// Authenticated as discovery reports are, by the licence token and the device key's signature (see
/// DiscoveryUploader), under a domain of its own so neither report can be replayed as the other. Under the
/// same switch too, "Send connection quality", whose match records already carry the game.
///
/// Best effort and quiet: only the latest state matters, so nothing queues. A failed send is tried again a
/// minute later with whatever is current then, and the log gets one line per failure streak.
/// </summary>
internal sealed class PresenceReporter : IDisposable
{
    public const string Domain = "gpb-presence-v1\n";

    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private static readonly string? AppVersion = typeof(PresenceReporter).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private readonly Func<string?> _licenceUrl;
    private readonly Func<byte[]?> _token;
    private readonly ECDsa _deviceKey;
    private readonly Func<bool> _allowed;
    private readonly Action<string> _log;

    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http;
    private readonly Task _loop;

    private readonly object _gate = new();
    private string? _game;
    private bool _changed;
    private bool _failing;

    public PresenceReporter(Func<string?> licenceUrl, Func<byte[]?> token, ECDsa deviceKey, Func<bool> allowed, Action<string> log)
    {
        _licenceUrl = licenceUrl;
        _token = token;
        _deviceKey = deviceKey;
        _allowed = allowed;
        _log = log;
        _http = new HttpClient(new SocketsHttpHandler { ConnectCallback = HappyEyeballs.ConnectCallback }, disposeHandler: true)
        {
            Timeout = RequestTimeout,
        };
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>The game now running, by id, or null for none. Sent at once when it differs from the last.</summary>
    public void Set(string? gameId)
    {
        lock (_gate)
        {
            if (string.Equals(_game, gameId, StringComparison.OrdinalIgnoreCase)) return;
            _game = gameId;
            _changed = true;
        }
        _wake.Release();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var wait = Timeout.InfiniteTimeSpan;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            string? game;
            lock (_gate)
            {
                game = _game;
                _changed = false;
            }

            var sent = await SendAsync(game, ct).ConfigureAwait(false);
            // A game still running is said again later; none needs saying once.
            wait = !sent ? RetryAfter : game is not null ? Every : Timeout.InfiniteTimeSpan;
            lock (_gate)
            {
                if (_changed) wait = TimeSpan.Zero;
            }
        }
    }

    private async Task<bool> SendAsync(string? game, CancellationToken ct)
    {
        var url = _licenceUrl();
        var token = _token();
        if (!_allowed() || string.IsNullOrWhiteSpace(url) || token is null) return true;

        var body = Body(game, DateTimeOffset.UtcNow);
        using var request = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/presence")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Gpb-Licence", Convert.ToHexStringLower(token));
        request.Headers.Add("X-Gpb-Signature", DiscoveryReport.Sign(_deviceKey, body, Domain));

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                if (_failing) _log("Presence: the licence server takes the game in play again.");
                _failing = false;
                return true;
            }
            Fail($"the licence server answered {(int)response.StatusCode}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Fail(ex is TaskCanceledException ? $"no answer within {RequestTimeout.TotalSeconds:F0} s" : ex.Message);
        }
        return false;
    }

    private void Fail(string why)
    {
        if (!_failing) _log($"Presence: could not tell the licence server which game is running ({why}) - trying again every minute.");
        _failing = true;
    }

    /// <summary>{"game": "pubg" | null, "sentAt": "...", "appVersion": "..."}: what /presence reads.</summary>
    internal static byte[] Body(string? game, DateTimeOffset sentAt)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            if (game is null) json.WriteNull("game");
            else json.WriteString("game", game);
            json.WriteString("sentAt", sentAt.UtcDateTime.ToString("O"));
            if (AppVersion is not null) json.WriteString("appVersion", AppVersion);
            json.WriteEndObject();
        }
        return stream.ToArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _http.Dispose();
        _cts.Dispose();
        _wake.Dispose();
    }
}
