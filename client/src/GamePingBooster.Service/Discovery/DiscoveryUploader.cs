using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using GamePingBooster.Core.Net;

namespace GamePingBooster.Service.Discovery;

/// <summary>
/// Sends the servers <see cref="GameDestinationRecorder"/> finds to the licence server, as soon as they
/// are found, and tries again until the server has them.
///
/// Held in memory only, by the owner's decision on 2026-09-17: a found server is written to no file on
/// the player's machine, and the log shows only its first octet ("20.*.*.*", see Destinations.Mask).
/// Every attempt and its answer is logged that way, because a report that vanished without a line was
/// impossible to diagnose. The cost is plain - a server still waiting when the
/// service stops is lost - and it is a small one, because the same server is found again in the next
/// match played on it, on this machine or another.
///
/// Sent by this LocalSystem service directly, not handed to the app like connection-quality records.
/// The app's route exists because those uploads authenticate with the person's refresh token, which
/// this process cannot read. This one authenticates with what the service does hold: the licence token
/// (X-Gpb-Licence), which names the device, and the device key's signature over the body
/// (X-Gpb-Signature), which proves the device sent it. See web-service/app/lib/discovery.server.ts.
///
/// Retries, in order of what the answer means:
///
///   200            accepted and rejected ids are forgotten; the rest is sent again.
///   429            the account's daily allowance is spent - what was named is forgotten, the rest
///                  waits an hour.
///   401            token or signature refused. The app renews the token, so this waits and tries again.
///   400, 413       the server will never take this batch; it is dropped rather than resent forever.
///   anything else  network failure or a 5xx: back off, 30 s doubling to 30 min.
///
/// Anything older than <see cref="MaxAge"/> is dropped unsent, and the queue holds at most
/// <see cref="MaxQueued"/>: a machine that cannot reach the server for a day has nothing worth keeping.
/// </summary>
internal sealed class DiscoveryUploader : IDisposable
{
    private const int MaxQueued = 50;
    private const int BatchSize = 20;
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly string? AppVersion = typeof(DiscoveryUploader).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private readonly Func<string?> _licenceUrl;
    private readonly Func<byte[]?> _token;
    private readonly ECDsa _deviceKey;
    private readonly Func<bool> _allowed;
    private readonly Action<string> _log;

    private readonly object _gate = new();
    private readonly List<(DiscoveredServer Server, DateTime QueuedUtc)> _queue = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http;
    private readonly Task _loop;
    private TimeSpan _backoff = TimeSpan.Zero;

    /// <param name="licenceUrl">The licence server's base URL, or null on a self-hosted installation.</param>
    /// <param name="token">The stored licence token, or null when not signed in.</param>
    /// <param name="allowed">Whether sending is switched on - "Send connection quality" in Settings.
    /// Read before every attempt; switched off, the queue is emptied.</param>
    public DiscoveryUploader(Func<string?> licenceUrl, Func<byte[]?> token, ECDsa deviceKey, Func<bool> allowed, Action<string> log)
    {
        _licenceUrl = licenceUrl;
        _token = token;
        _deviceKey = deviceKey;
        _allowed = allowed;
        _log = log;

        // Addresses raced, as the app's licence calls are. See HappyEyeballs for the line whose
        // IPv6 silently dropped packets and made every request wait 44 s.
        _http = new HttpClient(new SocketsHttpHandler { ConnectCallback = HappyEyeballs.ConnectCallback }, disposeHandler: true)
        {
            Timeout = RequestTimeout,
        };
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Whether a report could be sent at all right now: sending switched on, a licence server, a token.</summary>
    public bool CanSend => WhyNotSend() is null;

    /// <summary>Null when a report could be sent; otherwise why not, in words for the log.</summary>
    public string? WhyNotSend() =>
        !_allowed() ? "sending connection quality is switched off in Settings"
        : string.IsNullOrWhiteSpace(_licenceUrl()) ? "this installation has no licence server"
        : _token() is null ? "not signed in - there is no licence token to send with"
        : null;

    public void Report(DiscoveredServer server)
    {
        lock (_gate)
        {
            // The same address twice while the first still waits is the same finding.
            if (_queue.Any(q => q.Server.Game == server.Game && q.Server.Address == server.Address)) return;
            if (_queue.Count >= MaxQueued)
            {
                _log($"Discovery: the queue is full - dropped the oldest report ({Destinations.Mask(_queue[0].Server.Address)}).");
                _queue.RemoveAt(0);
            }
            _queue.Add((server, DateTime.UtcNow));
            _log($"Discovery: queued {Destinations.Mask(server.Address)} for {server.Game} ({_queue.Count} waiting).");
        }
        _backoff = TimeSpan.Zero;
        _wake.Release();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_backoff > TimeSpan.Zero)
                {
                    await _wake.WaitAsync(_backoff, ct).ConfigureAwait(false);
                }
                else
                {
                    await _wake.WaitAsync(ct).ConfigureAwait(false);
                }

                while (!ct.IsCancellationRequested && await SendOnceAsync(ct).ConfigureAwait(false))
                {
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Never let the loop die; the next wake or backoff tries again.
                Grow();
            }
        }
    }

    /// <summary>Sends one batch. True when there may be more to send straight away.</summary>
    private async Task<bool> SendOnceAsync(CancellationToken ct)
    {
        List<DiscoveredServer> batch;
        lock (_gate)
        {
            if (!_allowed())
            {
                if (_queue.Count > 0) _log($"Discovery: sharing was switched off - discarded {_queue.Count} unsent report(s).");
                _queue.Clear();
                _backoff = TimeSpan.Zero;
                return false;
            }
            var expired = _queue.RemoveAll(q => DateTime.UtcNow - q.QueuedUtc > MaxAge);
            if (expired > 0) _log($"Discovery: dropped {expired} report(s) that could not be sent within {MaxAge.TotalHours:F0} h.");
            if (_queue.Count == 0)
            {
                _backoff = TimeSpan.Zero;
                return false;
            }
            batch = _queue.Take(BatchSize).Select(q => q.Server).ToList();
        }

        var url = _licenceUrl();
        var token = _token();
        if (string.IsNullOrWhiteSpace(url) || token is null)
        {
            // Not signed in yet, or signed out: the app pushes a token when it can.
            Grow();
            _log($"Discovery: {batch.Count} report(s) waiting - {WhyNotSend()}; next try in {Describe(_backoff)}.");
            return false;
        }

        var names = string.Join(", ", batch.Select(b => Destinations.Mask(b.Address)));
        _log($"Discovery: sending {batch.Count} report(s) ({names}) to {url.TrimEnd('/')}/discovery.");

        var body = DiscoveryReport.Body(batch, AppVersion);
        using var request = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/discovery")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Gpb-Licence", Convert.ToHexStringLower(token));
        request.Headers.Add("X-Gpb-Signature", DiscoveryReport.Sign(_deviceKey, body));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Fail(ex is TaskCanceledException
                ? $"no answer within {RequestTimeout.TotalSeconds:F0} s"
                : $"the licence server could not be reached: {ex.Message}");
            return false;
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var answer = await ReadAnswerAsync(response, ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.TooManyRequests)
            {
                var answered = answer.Accepted.Concat(answer.Rejected).ToHashSet(StringComparer.Ordinal);
                lock (_gate) _queue.RemoveAll(q => answered.Contains(q.Server.Id));
                _log($"Discovery: HTTP {status} - {answer.Accepted.Count} accepted, {answer.Rejected.Count} refused, " +
                     $"{batch.Count(b => !answered.Contains(b.Id))} kept for later" +
                     (answer.Error is { } note ? $" ({note})" : "") + ".");

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _backoff = TimeSpan.FromHours(1);
                    return false;
                }
                if (answered.Count == 0)
                {
                    // An answer that names nothing leaves everything queued; without a backoff it would
                    // wait for the next server found to be tried again.
                    Grow();
                    return false;
                }
                Succeeded();
                return true;
            }

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)
            {
                var ids = batch.Select(b => b.Id).ToHashSet();
                lock (_gate) _queue.RemoveAll(q => ids.Contains(q.Server.Id));
                _log($"Discovery: HTTP {status} - the server will never take this batch; dropped {batch.Count} report(s)" +
                     (answer.Error is { } reason ? $": {reason}" : "."));
                Succeeded();
                return true;
            }

            Fail($"HTTP {status}" + (answer.Error is { } error ? $": {error}" : ""));
            return false;
        }
    }

    private sealed record Answer(List<string> Accepted, List<string> Rejected, string? Error);

    /// <summary>The server's JSON answer: which ids it accepted and refused, and its error text if any.</summary>
    private static async Task<Answer> ReadAnswerAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var accepted = new List<string>();
        var rejected = new List<string>();
        string? error = null;
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                Collect(root, "accepted", accepted);
                Collect(root, "rejected", rejected);
                if (root.TryGetProperty("error", out var text) && text.ValueKind == JsonValueKind.String) error = text.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON - a proxy's error page, typically. Nothing named: everything stays queued.
            error = "the answer was not JSON";
        }
        return new Answer(accepted, rejected, error);

        static void Collect(JsonElement root, string name, List<string> into)
        {
            if (!root.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } id) into.Add(id);
            }
        }
    }

    private void Grow() =>
        _backoff = _backoff == TimeSpan.Zero ? FirstBackoff : TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaxBackoff.Ticks));

    /// <summary>A line per failed attempt; the backoff keeps that to a handful an hour.</summary>
    private void Fail(string why)
    {
        Grow();
        _log($"Discovery: the report could not be sent ({why}) - next try in {Describe(_backoff)}.");
    }

    private void Succeeded() => _backoff = TimeSpan.Zero;

    private static string Describe(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{span.TotalMinutes:F0} min" : $"{span.TotalSeconds:F0} s";

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _http.Dispose();
        _cts.Dispose();
    }
}
